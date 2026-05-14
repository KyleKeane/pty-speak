# ADR 0004 — IOCell is the unit of shell interaction; ContentHistory is the sole substrate; OutputDispatcher is the sole non-emergency channel

- **Status**: Proposed (2026-05-14)
- **Date**: 2026-05-14
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 51 PR-V, in response to the
  maintainer's 2026-05-14 dogfood at preview.134 (commit
  `ae33bc9`) where running `test-04-yes-no.cmd` four times
  in sequence produced "things go completely haywire... not
  reading the beginning half... not reading the end...
  reading a bunch of stuff well after I've moved on." The
  trail of PRs that led there (Cycle 49's PR-K through
  PR-U) all share a structural flaw: they bolt screen-row-
  based extraction logic on top of a substrate
  (`ContentHistory`) that is already Seq-based and
  notebook-shaped. Each PR fixed one symptom of "row index
  becomes meaningless under scroll / multi-run
  accumulation" and ratcheted the cleverness without
  fixing the root cause. The maintainer's verbatim
  framing — "the core infrastructure is not robust enough
  and extensible and modular enough to handle other shells";
  "we need to be just handling The Computational in and
  out in a similar way that a computational notebook such
  as Wolfram does"; "I want to be able to have an
  abstraction layer on top of all of this that will allow
  us to route each one of these different events that is
  presently going into NVDA through different channels,
  such as spatial audio and custom speech synthesis" —
  motivates the pivot captured here.
- **Companion docs**:
  - [`0001-substrate-channel-dichotomy.md`](0001-substrate-channel-dichotomy.md)
    — substrate vs channel framing. ADR 0004 doubles down:
    `ContentHistory` is THE substrate post-pivot;
    `OutputDispatcher` is THE channel surface.
  - [`0002-uia-textedit-caret-output.md`](0002-uia-textedit-caret-output.md)
    — the UIA channel decision. ADR 0004 keeps that
    channel; it changes how non-NVDA channels (spatial
    audio, custom TTS) compose alongside it.
  - [`0003-shell-interaction-state-machine.md`](0003-shell-interaction-state-machine.md)
    — `ShellInteraction` two-state machine (Composing /
    Executing). ADR 0004 keeps that machine. It also keeps
    `EntrySource` provenance tagging (the PR-C
    contribution) — the IOCell extractor reads
    `EntrySource` per-byte instead of walking screen rows.
  - [`../CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md)
    — names the three sub-panes (input / current-output /
    history) and three reserved peers (notifications,
    contextual keyword, input assistant). ADR 0004 renames
    the unit of interaction (tuple → IOCell) and locks the
    per-cell extraction contract.
  - [`../SESSION-MODEL.md`](../SESSION-MODEL.md) — locked
    on-disk wire format from Cycle 24b. ADR 0004 extends
    it: `schemaVersion` bumps 1 → 2; the type renames to
    `IOCell`; two new fields (`Phase`, `CellSequence`) join
    the existing shape. The hand-rolled byte-stable
    serializer discipline survives.

## Context

### Where the existing model stops working

Pty-speak's pipeline post-Cycle-49 is:

```
PTY stdout bytes
  → VtParser (typed VtEvents)
  → Screen (2-D grid, ~30×120, scrolls on overflow)
    + ContentHistory (linear append-only log, Seq-keyed,
      EntrySource-tagged per Cycle 48 PR-C)
  → SessionModel.applyAndCaptureWithContentHistory
      (tuple boundaries via HeuristicPromptDetector regex
       on screen rows + OSC 133 markers if available)
    ├─ extractContentFromContentHistory (Seq-based +
    │   newline-split heuristic for marker-less shells)
    └─ extractContent (row-walk fallback when CH path
        returns None — silent garbage when Screen scrolls)
  → Announce paths
    ├─ Tuple-final (subPromptScreenReader + cursor-row
    │  capture + computePromptCommandWrapRows)
    ├─ Sub-prompt narration (set/p, choice, pause)
    ├─ History-recall announce (Up/Down arrow draft
    │  restoration, per Cycle 49 PR-I)
    └─ Direct `window.TerminalSurface.Announce` calls
       across ~60 sites, each its own bespoke wiring
  → NVDA channel (RaiseNotificationEvent + UIA Text-pattern
    materialiser caret) + Earcon channel (WASAPI palette)
```

The pipeline runs against TWO sources of truth:

1. **ContentHistory** — a Seq-keyed typed-event log.
   Append-only, EntrySource-tagged per Cycle 48 PR-C
   (`UserInputEcho`, `CmdOutput`, `CmdSubPrompt`,
   `ShellPrompt`, `BoundaryMarker`, `Unknown`). Each entry
   has a monotonic Seq integer; "already spoken?" is an
   integer comparison.

2. **Screen** — a 2-D 30×120 cell grid. Scrolls on
   overflow. Row indices are MEANINGLESS once the active
   tuple's prompt has scrolled off the visible area.

The bugs Cycle 49 patched (PR-K → PR-U) all reduce to:
**screen-row coordinates are unreliable on a scrolling
buffer, but the extraction / narration paths depend on
them anyway**. Specifically:

- `SessionModel.extractContent` falls back to a row-walk
  on the screen snapshot when ContentHistory's
  newline-split heuristic returns `None`. After 4
  consecutive command runs, the prior prompt's `rowIdx`
  and the new prompt's `rowIdx` collide on the same row
  (both scrolled to the top), producing empty `CommandText`
  / `OutputText` and silent NVDA — or worse, leaking
  scrollback bytes into the announce body.
- `subPromptScreenReader` walks `screen.SnapshotRows`
  every sub-prompt fire. After scroll, the "rows since
  PromptStart" computation can return rows from an UNRELATED
  prior tuple, narrating stale text.
- `computePromptCommandWrapRows` computes the number of
  display rows a typed command occupies for the
  preamble-line-count calculation. Wrap math is row-grid
  arithmetic; once the prompt scrolls off, the wrap count
  for the original line is unrecoverable.
- The cursor-row capture in the byte-write wrapper's CR
  branch (Cycle 50 PR-R) records `Screen.Cursor.Row` at
  Enter time to seed `subPromptPreambleLineCount`. On a
  scrolled buffer, that row references a different visual
  location than it did at byte time.

Each PR patched one symptom. The 2026-05-14 four-run
test-04 dogfood demonstrated that the symptoms COMPOSE
catastrophically — the user described it as "things go
completely haywire". The diagnosis is structural, not
incremental: **the bytes the user cares about all flow
through ContentHistory with EntrySource provenance
attached; the screen grid is a finite VISUAL projection of
the same bytes, and using it as the source-of-truth for
"what cell is this byte part of?" is a category error.**

The maintainer's framing reframes the pivot in terms
already familiar from the project's strategic docs:

> *"we need to be just handling The Computational in and
> out in a similar way that a computational notebook such
> as Wolfram does"*

In Wolfram (and Jupyter, and the original Lisp REPL): an
interaction is a SEQUENCE of **cells**. Each cell is an
(input, output) pair — the user types an expression into
the input cell, evaluates it, and the output cell is
populated with the result. Cells are persistent, ordered,
and addressable; the notebook's "current state" is the
trailing list of cells plus the in-flight active cell.

The screen, in this framing, is a **viewport** onto the
trailing portion of the cell list. It's not the
substrate; it's a rendering of the substrate. A screen
reader for a notebook needs to navigate the **cell list**,
not the viewport.

### Why this is the right moment for the pivot

Three factors make this a small, scoped change despite
sounding architectural:

1. **The substrate is already in place.** ContentHistory
   is Seq-based, EntrySource-tagged, and battle-tested
   (Cycles 45 + 47 + 48). It already represents what a
   "cell list" needs to represent.

2. **The channel surface is already in place.**
   `OutputDispatcher` + `ProfileRegistry` +
   `ChannelRegistry` (Cycles 36 / 38 / 42) is a
   production-pluggable routing layer. `NvdaChannel`,
   `FileLoggerChannel`, and `EarconChannel` all conform to
   `IOutputSink`. A spatial-audio channel or custom-TTS
   channel adds one file each; no other code needs to
   change. The infrastructure the maintainer asked for
   ("an abstraction layer ... to route each one of these
   different events ... through different channels") is
   already there — it's just under-used because Cycle 49's
   PR-K → PR-U bypassed it with direct
   `window.TerminalSurface.Announce` calls.

3. **The pre-existing data structure is already
   IOCell-shaped.** `SessionModel.SessionTuple` is
   (`PromptText`, `CommandText`, `OutputText`,
   `ExitCode`, timestamps, provenance). Rename the type;
   add two fields (`Phase`, `CellSequence`). The
   migration is mechanical, not structural.

The cost of Cycle 51 is therefore: **delete the
screen-row-based extraction code that's fighting the
substrate, route the bypass announces back through the
dispatcher, and rename the unit of interaction so its
purpose is no longer obscured by legacy "tuple" wording.**
That's the work this ADR proposes.

## Decision

This ADR locks four decisions. They are NON-NEGOTIABLE in
the migration PRs (PR-V through PR-Z) without an explicit
ADR amendment.

### Decision 1 — IOCell is the unit of interaction

**Rename**: `SessionModel.SessionTuple` → `SessionModel.IOCell`.

An **IOCell** is the sealed `(prompt, command, output,
exit-code)` triple plus its provenance metadata. It is
THE unit of shell interaction. The "active IOCell" is the
in-flight cell (the one the user is typing into, or whose
output is currently streaming). The "IOCell history" is
the bounded queue of sealed past cells (today's
`SessionModel.T.History`).

The conceptual change is the name. The data shape stays
~95% the same — two fields are added (`Phase` and
`CellSequence`) per the canonical data structure section
below. Every existing test stays valid with
constructor-default values for the two new fields.

### Decision 2 — Sub-prompts are state inside the parent IOCell (v1)

A **sub-prompt** is an interactive prompt emitted MID-cell
(`set /p`, `choice`, `pause`). v1 treats sub-prompts as
**state within the parent IOCell**, not as a child cell.
This matches how today's substrate already represents
them (`EntrySource.CmdSubPrompt` on entries inside the
active cell).

The IOCell's lifecycle phases (per the `IOCellPhase` DU
below) include `AwaitingSubPromptResponse of string` —
the parent cell pauses in this state until the user types
the response. When the response arrives, the phase
returns to `Executing` and the sub-prompt's
(question, response) pair stays inline in the cell's
output region.

**Future cycle may promote sub-prompts to nested IOCells**
if dogfood surfaces a need for per-sub-prompt review-
cursor navigation. The substrate already supports it via
`EntrySource`. v1's choice is "inline" because:

- The screen-reader user's mental model is "one command,
  one cell" — interactive prompts are a sub-detail of the
  command, not a separate command.
- Implementation is simpler — no nested cell tree, no
  cell-stack push/pop on sub-prompt entry/exit.
- Migration is smaller — today's behavior is already
  inline; we're explicitly NOT changing it in v1.

### Decision 3 — ContentHistory is the sole extraction substrate

For all IOCell extraction (boundary detection, command /
output region splits, sub-prompt response slicing), the
substrate is `ContentHistory`. **`Screen.SnapshotRows` is
display-only post-pivot.** It parses bytes for rendering
(WPF terminal view + UIA `ITextProvider` reads for
review-cursor navigation), but NEVER as a source-of-truth
for "what cell is this byte part of?" or "where does this
cell begin or end?".

Anything that needs "where did the cell begin" gets a Seq
ID via `ContentHistory.tryLatestMarker`. Anything that
needs "what is the text since point X" gets it via
`ContentHistory.sliceText` (or the new
`ContentHistory.entriesAfter` helper for cases needing
per-entry provenance).

**Drop-on-None contract**: when
`ContentHistory.tryLatestMarker(PromptStart)` returns
`None`, extraction returns `None` and the IOCell does
NOT finalize. This is strict; per the maintainer
2026-05-14, drop-the-cell beats announce-stale-garbage.
Consequences:

- Shells whose `HeuristicPromptDetector` regex doesn't
  emit a PromptStart marker in time get NO IOCell
  announce.
- We are forced to make heuristic detection robust per
  supported shell (cmd is supported; PowerShell deferred
  pending its own heuristic spike).
- The failure mode is **loud silence** — a clear missing
  announce instead of a misleading garbage announce.
  Dogfood will surface this immediately if it happens,
  rather than 4 runs deep into a session.

### Decision 4 — OutputDispatcher is the sole non-emergency channel

All substrate-driven announces flow through
`OutputDispatcher.dispatch(event)` →
`ProfileRegistry` → `ChannelRegistry` → `IOutputSink`
implementations. The `SemanticCategory` DU is the closed
taxonomy of events (`PromptShown`, `CommandFinished`,
`OutputStreaming`, `BellRang`, `AltScreenEnter`,
`SelectionShown`, etc.). Profiles pattern-match on
category + extensions and emit `ChannelDecision`s.
Channels implement `IOutputSink` and route to their
platform binding (NVDA UIA, WASAPI earcon, file logger,
future spatial audio, future custom TTS).

This is called **Tier 1** of the channel contract.

**Tier 2** is a narrow named bypass for announces that
describe **what the app did** (not what the shell did)
AND must work even when the substrate or dispatcher is
broken. Tier 2 is invoked via a new method:

```fsharp
member this.AnnounceEmergency
    (msg: string, ?activityId: string) : unit
```

which lives on `TerminalSurface` and routes directly to
`RaiseNotificationEvent` on a dedicated
`pty-speak.app-affordance` activity ID. No
ProfileRegistry / ChannelRegistry lookup; no
SemanticCategory mapping. This is the I/O of last resort.

**The Tier 2 closed list** (per the 2026-05-14 announce-
site inventory):

- `Ctrl+Shift+U` Velopack update flow announces
- `Ctrl+Shift+R` release-form launcher
- `Ctrl+Shift+P` data-folder opener
- `Ctrl+Shift+E` config-editor opener (success + error
  fallbacks)
- `Ctrl+Shift+1/2/3` shell-switch success + failure
- `Ctrl+Shift+H` health-check announce
- `Ctrl+Shift+B` incident-marker logged announce
- `Ctrl+Shift+;` copy-log announces (success + error)
- `Ctrl+Shift+O` open-last-output announces (empty-history
  case, file-open success)
- `Ctrl+Shift+A` re-narrate-last-output announces
- `Ctrl+Shift+S` session-log-path announces
- `Ctrl+Shift+Y` copy-session-history announces
- `Ctrl+Shift+D` diagnostic-snapshot completion announce
- All `Diagnostics → ...` menu items (Test Process
  Cleanup, Test commands, Grep Diagnostics, Extract sub-
  menu items, Open Manual Tests)
- `View → Logging Level / Earcons / Streaming` mode-
  toggle announces
- `Window → Close Window / Exit` confirmations (if any)
- Exception fallbacks: substrate errors, channel failures,
  clipboard timeouts. These announce "X failed; <reason>"
  directly because the substrate or channel they would
  normally use IS the thing that failed.

**Tier 1 sites** (per the same inventory; ~3-5 total
post-spike):

- `Program.fs:1777` — PR-K sub-prompt announce body.
  Refactor to dispatch `SemanticCategory.PromptShown` (or
  a new `SemanticCategory.SubPromptShown` if the existing
  category doesn't cover the case cleanly).
- `Program.fs:2645` — tuple-final announce. Refactor to
  dispatch `SemanticCategory.CommandFinished` carrying
  the OutputText.
- The Cycle 49 PR-I history-recall announce on
  `ActivityIds.inputAssistant`. Add a new
  `SemanticCategory.InputAssistant` variant; profiles
  decide how to render (NVDA channel marshals; spatial-
  audio channel could play a recall earcon + mute speech;
  future TTS channel could read at a different prosody).
- Mode-change announces (alt-screen enter/exit, selection
  shown/dismissed, shell-policy change). Currently a
  mix; the inventory will reconcile during PR-Y.

**Rule**: new code defaults to Tier 1. Tier 2 additions
require an explicit ADR amendment naming the new site +
the reason it cannot be substrate-driven. The
`AnnounceEmergency` method's name is intentionally
discomforting — every call site is admitting the
substrate / dispatcher path is unavailable.

## IOCell canonical data structure

This section locks the in-memory shape, the on-disk wire
format, and the round-trip discipline for the IOCell type.
It is non-negotiable for PR-W and PR-W2.

### What already exists (don't reinvent)

The codebase already has a **decades-stable, schema-
versioned, JSONL on-disk wire format** for `SessionTuple`
shipped in Cycle 24b. Locked decisions per
`SessionModel.fs:1137-1180`:

1. **`"schemaVersion": 1`** as the first JSON key. Future
   schema changes increment the value; old files always
   remain readable; replay tools branch on it.
2. **Hand-rolled serializer** (`formatTupleAsJsonl` at
   `SessionModel.fs:1357`). Zero JSON library dependencies.
   Byte-stable across .NET versions. Matches the Tomlyn-
   uses-only-what-we-need pattern from Cycle 24a.
3. **DU cases serialize as tagged objects** `{"kind":
   "<case>", ...payload}` — uniform shape, future cases
   land cleanly.
4. **Maps serialize as JSON arrays** of records (not
   objects keyed by F# DU values) — avoids latent
   payload-variant collision bugs.
5. **String-escape policy**: RFC 8259 minimum + `` for
   DEL (deliberate superset for parser robustness). Lone
   UTF-16 surrogates throw (loud failure beats silent
   corruption).
6. Canonical doc: `docs/SESSION-MODEL.md` §"On-disk wire
   format (Cycle 24b)". This ADR's PR-W replaces it with
   `docs/IOCELL-SCHEMA.md`.
7. Persistence pipeline: `SessionLogWriter.fs` writes
   `session-<SessionId>.jsonl` per shell session (config:
   `format=jsonl`). Already in production.

**The pivot extends this format; it does not replace it.**
The hand-rolled byte-stable discipline survives the
schemaVersion bump.

### In-memory representation

**F# records + discriminated unions.** Idiomatic F#;
already in use for `SessionTuple`. Properties:

- **Immutable by default** — pattern-matching safe; no
  defensive copies needed for cell-history navigation
  or replay state-machine snapshots.
- **Structural equality** — IOCells compare by value;
  free for cache / dedup / round-trip property tests.
- **Algebraic** — DUs encode variant data (cell phase,
  boundary source, exit-code presence) at the type
  level; the compiler enforces exhaustive matches.
- **First-party** — no extra dependency.
- **Closer to Wolfram expressions than JSON is** — F# DUs
  ARE `head args...` algebraic terms in the same shape
  Wolfram uses. The on-disk JSON is the portable surface;
  in-memory the structure is Wolfram-shaped — which is
  the maintainer's stated computational-notebook target.

The v1 IOCell type (locked):

```fsharp
/// Lifecycle phase of an IOCell. The active cell carries
/// one of {Composing, Executing, AwaitingSubPromptResponse};
/// every cell in `History` carries Sealed.
[<RequireQualifiedAccess>]
type IOCellPhase =
    | Composing
    | Executing
    | AwaitingSubPromptResponse of subPromptText: string
    | Sealed

/// Canonical unit of shell interaction. Renamed from
/// SessionTuple per ADR 0004 Decision 1.
///
/// Identity contract: `Id` and `CellSequence` are assigned
/// at CELL CREATION (the moment the PromptStart boundary
/// fires), NOT at seal. The active cell IS the same
/// identity it'll have once sealed.
type IOCell =
    { Id: Guid
      CellSequence: int64                  // NEW in v1
      ShellId: string
      Phase: IOCellPhase                   // NEW in v1
      PromptStartedAt: DateTime
      CommandStartedAt: DateTime option
      OutputStartedAt: DateTime option
      CommandFinishedAt: DateTime option
      PromptText: string
      CommandText: string
      OutputText: string
      ExitCode: int option
      Sources: Map<BoundaryKind, BoundarySource>
      ExtraParams: Map<string, string> }
```

`SessionTuple` + two fields (`Phase`, `CellSequence`) +
a renamed type. The data shape is ~95% the same.

### On-disk wire format (v1)

**JSONL with hand-rolled `formatIOCellAsJsonl`**. Extends
`formatTupleAsJsonl`. Bumps `"schemaVersion"` 1 → 2:

```jsonl
{"schemaVersion":2,"id":"f2c8b1a4-...","cellSequence":42,"shellId":"cmd","phase":{"kind":"sealed"},"promptStartedAt":"2026-05-14T13:18:00.000Z","commandStartedAt":"2026-05-14T13:18:02.123Z","outputStartedAt":"2026-05-14T13:18:02.124Z","commandFinishedAt":"2026-05-14T13:18:14.500Z","promptText":"C:\\Users\\Kyle\\...current>","commandText":"\"...test-04-yes-no.cmd\"","outputText":"=== ...START === ... You chose Yes. === ...END ===","exitCode":null,"sources":[{"boundary":"PromptStart","source":{"kind":"HeuristicPromptRegex","stabilityMs":100}},{"boundary":"CommandFinished","source":{"kind":"InferredFromNextPromptStart"}}],"extraParams":[]}
```

Key locks:

- **`phase` serializes as a tagged DU object**:
  `{"kind":"composing"}`, `{"kind":"executing"}`,
  `{"kind":"awaitingSubPromptResponse","subPromptText":"<>"}`,
  `{"kind":"sealed"}`. Matches the Cycle 24b DU rule.
- **`cellSequence` is a new int64 field**. JSON number.
- **`schemaVersion` = 2** for every IOCell record.
- **Old `schemaVersion=1` files remain readable** by
  future replay tools (they branch on the version).

### Round-trip discipline

v1 ships **`IOCell.parseFromJsonl : string -> Result<IOCell, ParseError>`**
in PR-W2. Locked decisions:

- **Hand-rolled parser** (no JSON library; matches Cycle
  24b discipline).
- **Branches on `"schemaVersion"`** — only `2` supported
  in v1; `Result.Error` for `1` with a clear message.
- **No user-facing surface** — exercised only by FsCheck
  round-trip property tests.
- **Confidence canary**: any future schema change that
  isn't round-trip-faithful fails CI immediately.

### Why JSON over XML over Wolfram expressions

- **In-memory ops**: F# records + DUs. Zero serialization
  cost; pattern-match-fast.
- **On-disk**: JSONL. Universal, portable, line-delimited
  (streaming-friendly), versioned.
- **Cross-tool** (future): same JSONL — Python / JS / Rust
  replay tools parse with any stock JSON library.

XML adds verbosity without expressive gain. Wolfram
expressions are isomorphic to F# DUs in shape but harder
to consume cross-tool. JSON is the best balance for
"F#-native in memory, universal on disk".

## Consequences

### Positive

- **Row-index bugs eliminated by construction.** No code
  on the extraction or announce hot path reads screen-row
  coordinates post-pivot. The classes of bugs Cycle 49
  PR-K → PR-U patched cannot recur.
- **Channel extensibility surfaces immediately.** A
  spatial-audio sink or custom-TTS sink is one file
  implementing `IOutputSink` + a registration call —
  zero changes to substrate, dispatcher, or profiles.
- **Smaller code surface.** `extractContent` (row-walk)
  + `subPromptScreenReader` (row-walk) +
  `computePromptCommandWrapRows` + the byte-write CR
  cursor-row capture all delete in PR-W and PR-X.
  Net LOC goes DOWN despite the ADR's verbosity.
- **The maintainer's mental model snaps back into
  alignment.** "Cells" is what the spec already said the
  intended model was (CANONICAL-DISPLAY-CATALOG §1.6,
  CORE-ABSTRACTION-BOUNDARY §6). The rename makes the
  code say what the spec says.
- **Schema-versioned persistence stays portable.** The
  hand-rolled JSONL serializer's decade-stable contract
  survives the version bump; future replay tools (and
  cross-tool integrations) read the format with any
  stock JSON library.

### Negative

- **Shells without OSC 133 AND without working heuristic
  detection get NO IOCell announce.** The drop-on-None
  contract makes this a feature, but it's a regression
  for any shell whose `HeuristicPromptDetector` regex
  doesn't fire reliably. PowerShell is the immediate
  case: it has been "deferred" repeatedly; ADR 0004
  formalises that deferral. PowerShell support needs its
  own heuristic spike before that shell can ship.
- **Cycle 49 PR-I's history-recall announce becomes a
  new `SemanticCategory`.** The PR-I dogfood validated
  the audible behavior; the channel-routing refactor in
  PR-Y must preserve it bit-for-bit, which means a new
  variant `SemanticCategory.InputAssistant` (or
  `HistoryRecall`; bikeshed during PR-Y) joins the closed
  taxonomy. Adding a variant means every profile
  pattern-match needs an arm; the compiler flags missing
  arms but they're real edits.
- **The `AnnounceEmergency` rename touches ~50 call
  sites.** Mechanical, but it's a visible churn in the
  PR-Y diff. The PR-Y closure audit (`grep -rn 'Announce\b'`)
  must verify the bypass list matches the closed list in
  this ADR.
- **Schema-version migration is one-way.** v1 readers
  do NOT load `schemaVersion=2` files; v2 readers
  explicitly reject `schemaVersion=1` files (with a
  clear error). A downgrade would require either a
  separate migration tool (out of scope for v1) or
  keeping both formatters alive (rejected per the
  Cycle-25 SubstrateMode-collapse precedent — dual-path
  is technical debt).

### Migration cost

~5 sequenced PRs after this ADR (V) lands. Detailed
sequencing in the "Migration plan" section below. Total
scope: ~5-7 days of focused work + maintainer dogfood
sessions between each PR.

## Migration plan

Each PR is independently CI-gated. Each PR ships through
the maintainer's release-build dogfood loop (per
`docs/ACCESSIBILITY-TESTING.md`). The cycle closure audit
(PR-Z) ships last per CLAUDE.md §"Cycle closure audit".

### PR-V (this ADR; docs-only fast-merge)

- New file: `docs/adr/0004-iocell-model-for-shell-interaction.md`.
- Updates to `CLAUDE.md` §"Reading order at session
  start" (add ADR 0004 to the list, between ADR 0003 and
  the RFC 0001 archive entry).
- Updates to `docs/SESSION-HANDOFF.md` §"Current state"
  + §"Next stage".
- Append-bottom CHANGELOG.md entry under `[Unreleased]`.
- Markdown link check is the only CI gate (per CLAUDE.md
  §"Docs-only PRs may merge after only the markdown link
  check passes").

### PR-W (IOCell type + extraction + schema-version-2 formatter)

- **Type rename + extension**: `SessionTuple` → `IOCell`
  in `src/Terminal.Core/SessionModel.fs` (~150 sites;
  mechanical via `Edit replace_all=true`). Add `Phase`
  and `CellSequence` fields. Add the `IOCellPhase` DU.
  Apply the assign-at-creation rule per Decision 1.
- **Extraction path swap**: add the spike's
  `extractIOCell` function. Wire as PRIMARY in
  `applyAndCaptureWithContentHistory`. **Delete
  `extractContent`** (row-walk path). When the new
  extractor returns `None`, the cell drops silently
  (logged at Information).
- **On-disk formatter**: rename `formatTupleAsJsonl`
  → `formatIOCellAsJsonl`. Bump `schemaVersion` 1 → 2.
  Serialize the two new fields per the on-disk shape
  above. Update the locked-format docstring block in
  `SessionModel.fs:1137-1180`.
- **SessionLogWriter wiring**: `SessionLogWriter.fs:231`
  call site swaps to the new formatter. Pipeline shape
  unchanged.
- **Tests**: rewrite `SessionModelTests.fs` cases that
  exercise the row-walk path to instead assert the
  drop-on-None contract. Add unit tests for
  `IOCellPhase` serialization. Round-trip property
  tests deferred to PR-W2.
- **Docs**: replace `docs/SESSION-MODEL.md` with
  `docs/IOCELL-SCHEMA.md` (same wire-format-lock
  discipline, bumped to schemaVersion 2). Old
  SESSION-MODEL.md kept as a short pointer doc.

### PR-W2 (`IOCell.parseFromJsonl` round-trip reader)

- **New function**: `IOCell.parseFromJsonl : string -> Result<IOCell, ParseError>`
  in `SessionModel.fs`. Hand-rolled parser. Branches on
  `schemaVersion`.
- **No user-facing surface**.
- **FsCheck property tests**: new
  `tests/Tests.Unit/IOCellRoundTripTests.fs`:
  `forall ioCell, parseFromJsonl (formatIOCellAsJsonl ioCell) = Ok ioCell`.
  Generators cover the DU cases, optional fields,
  edge-case strings (lone surrogates throw on format
  per existing policy; reader rejects the same payload
  as malformed). `<Compile Include="...">` entry per
  CONTRIBUTING test-fixture rule.

### PR-X (SubPromptIdle / SubPromptResponse Seq-based)

- Replace `subPromptScreenReader` body with a Seq-based
  reader: slice ContentHistory from latest PromptStart
  Seq through current `latestSeq`, classify per
  EntrySource, return concatenated text.
- Replace `capturePreambleForSubPromptResponse` body
  with `subPromptPreambleSeq <- ContentHistory.latestSeq`.
- Replace `subPromptPreambleLineCount` consumer with
  `ContentHistory.sliceText (subPromptPreambleSeq,
  Int64.MaxValue)`.
- **Delete** `computePromptCommandWrapRows`,
  `lastSubmittedCommandLength`,
  `lastSubmittedCommandEndCursorRow`,
  `lastSubmittedCommandPromptRow`, the byte-write
  wrapper's CR-branch cursor-row capture.
- Tests: add unit tests for the new Seq-based handlers
  (pure F# functions; substrate fakes are easy per
  PR-S's CI-TESTING-FUTURE.md Win 1).

### PR-Y (Tier 1 channel routing)

- Audit every `window.TerminalSurface.Announce` call
  site in `Program.fs` against the Tier 1 / Tier 2
  closed lists in this ADR.
- For each Tier 1 site (~3-5 total): refactor to
  `OutputDispatcher.dispatch(OutputEvent.create ...)`.
  Add new `SemanticCategory` variants where needed.
  Update profiles + channels.
- For each Tier 2 site (~50): rename direct
  `window.TerminalSurface.Announce` to
  `window.TerminalSurface.AnnounceEmergency` on a
  dedicated `pty-speak.app-affordance` activity ID.
- **`AnnounceEmergency` method**: new addition to
  `TerminalSurface` in `MainWindow.xaml.cs` (or the
  F# wrapper if one exists). Routes directly to
  `RaiseNotificationEvent` — no dispatcher lookup.
- Channel-extensibility smoke test: write a no-op
  `LoggingProfile` that records every dispatched event
  → verifies every Tier 1 announce flows through the
  dispatcher post-refactor.

### PR-Z (Closure audit)

- Per CLAUDE.md §"Cycle closure audit".
- Update `docs/CORE-ABSTRACTION-BOUNDARY.md` to point at
  ADR 0004 as the cell-model lock.
- Update `docs/INTERACTION-MODEL.md` snapshot.
- Update `docs/CANONICAL-DISPLAY-CATALOG.md` (cells use
  the catalog's three exemplars for rendering).
- Update `docs/ACCESSIBILITY-TESTING.md` matrix with
  Cycle 51 walk row.
- Mark this ADR's Status → Accepted / Implemented.
- Mark `docs/spike/cycle51-iocell-learnings.md` HISTORICAL
  (or delete; spike branch material).
- Tag (maintainer push): `cycle51-iocell-shipped`.

## Out of scope (deferred, NOT Cycle 51)

- **Sub-prompts as nested IOCells.** v1 keeps inline.
  ADR 0006 (future) if dogfood demands. Requires v2
  schema bump (3) and the `IOCellOutputChunk` DU.
- **Structured `Output: IOCellOutputChunk list` field.**
  Reserved for v2 schema. Reserved shape:
  ```fsharp
  type IOCellOutputChunk =
      | RawText of text: string
      | SubPromptInteraction of question: string * response: string
      | InteractiveList of items: string list * selection: string option
      | ErrorLine of text: string
      | Marker of kind: string * payload: string option
  ```
- **Cell-level navigation hotkeys** (`h`, `o`,
  `Alt+Up/Down`) per CANONICAL-DISPLAY-CATALOG §1.6.
  Follow-on cycle.
- **User-facing replay UI**. The `parseFromJsonl`
  reader shipped in PR-W2 is the foundation; a future
  cycle adds `Ctrl+Shift+J` "Replay session log" or a
  menu item.
- **PowerShell support.** Depends on per-shell Seq-based
  heuristic emission. Cycle 51 closes the gate (`cmd`
  is fully solid); the PS-specific work is a separate
  cycle.
- **Alt-screen TUI cell semantics** (vim, less). Don't
  fit the cell model cleanly. Keep current alt-screen
  pause-boundary behavior unchanged.
- **History persistence + replay across sessions**
  (Phase 3 from PROJECT-PLAN). Cell-list serialization
  format is the on-disk JSONL described here; cross-
  session reload is its own cycle.
- **Spatial audio channel implementation.** ADR 0004
  proves the extensibility surface; the actual
  spatial-audio sink + its config is its own project.
- **Custom speech synthesis channel.** Same as above —
  extensibility proved, implementation deferred.

## Status notes

### 2026-05-14 — Proposed

ADR 0004 drafted on PR-V branch.
Cycle 51 spike (Phase 0) running in parallel on branch
`spike/cycle51-iocell-substrate-exploration` (commit
`de8bf81`) to validate the
classification-by-`EntrySource` extractor against the
maintainer's 4-run test-04 reproducer. ADR contents
locked from the plan; spike dogfood will inform any
edits before PR-V opens for review.

## References

- ADR 0001 — Substrate / channel dichotomy. The framing
  this ADR doubles down on.
- ADR 0002 — UIA TextEdit caret output. The channel
  decision that survives the pivot.
- ADR 0003 — Shell interaction state machine. The
  semantic layer above the substrate; locked.
- `docs/CORE-ABSTRACTION-BOUNDARY.md` — three sub-panes
  + three reserved peers.
- `docs/CANONICAL-DISPLAY-CATALOG.md` — three exemplar
  display kinds (raw text, list, form with text input).
- `docs/SESSION-MODEL.md` — Cycle 24b on-disk wire
  format. Extended (not replaced) by this ADR.
- `docs/PROJECT-PLAN-2026-05-12.md` — strategic plan;
  Cycle 51 lands as the next entry.
- Cycle 49 retro (the post-PR-K-through-PR-U dogfood
  pile-up that motivated the pivot).
- Maintainer's 2026-05-14 framing: "we need to be just
  handling The Computational in and out in a similar
  way that a computational notebook such as Wolfram
  does."
