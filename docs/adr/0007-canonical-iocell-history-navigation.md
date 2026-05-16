# ADR 0007 — SpeechCursor as the canonical navigable IOCell history: typed cells, per-cell operations, and live-trickle review

- **Status**: Proposed (2026-05-16)
- **Date**: 2026-05-16
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 52. The maintainer redirected
  R6c away from the planned dead-code "quick patch" toward a
  comprehensive design review of the SpeechCursor /
  IOCell-history component. Verbatim framing (2026-05-16):
  *"The speech cursor should be the canonical history of all
  IOCells including input, output, and sub-prompt output …
  we will want to give the user many different mechanisms to
  explore this history and interact with it, such as copying
  the content to the clipboard or turning the input cell into
  a new input cell to rerun the command. I think this whole
  component of the app probably needs a comprehensive design
  review rather than a quick patch … This is a very important
  component of the system since it gives the hierarchy of the
  computational history that we want to allow the user to
  explore. In the case of something like the progress loop or
  Claude code CLI this becomes the core mechanic that will
  allow me to review the trickle of messages that occurred in
  the past, as well as the current trickle that is coming in
  from an ongoing response."*
- **Companion docs**: ADR 0001 (substrate/channel dichotomy),
  ADR 0002 (UIA TextEdit caret channel), ADR 0004 (IOCell
  data model), ADR 0006 §"Deferred to R6+" items 2–5 (this
  ADR supersedes and sequences that cluster),
  `docs/CORE-ABSTRACTION-BOUNDARY.md` §6 (three-sub-pane
  paradigm + history sub-pane), `docs/CANONICAL-DISPLAY-CATALOG.md`
  (the `CommandOutputTuple` primitive).

## Context

### What this component is for

The SpeechCursor history is the only surface through which a
screen-reader user explores the **computational history** of
a session: what was run, what came back, the sub-prompt
exchanges in between, and — critically — the *trickle* of a
long-running command or a streaming Claude / progress-loop
response, both *after the fact* and *while it is still
arriving*. It is the notebook the user navigates. ADR 0004
already locked the *data* model for this (IOCell, "the unit
of shell interaction", Wolfram-notebook framing); this ADR is
about the **navigation + operations + channel** layer on top
of that data — the part the user actually touches.

### Honest current-state assessment

A four-probe review of the live code (2026-05-16, on
`main` `cf2b766`) found a sharp split between a clean data
model and a lossy interaction layer:

1. **IOCell — clean (the data model).**
   `src/Terminal.Core/SessionModel.fs` (IOCell record
   ~`:101–165`) is an immutable, structurally-equal record
   with a **stable identity assigned at creation, not at
   seal** (`Id : Guid` + `CellSequence : int64`; ADR 0004
   Decision 1), typed content (`CommandText`, `OutputText`,
   `PromptText`, `ExitCode`, `Phase`, timestamps, `Sources`,
   `ExtraParams`), and a byte-stable JSONL `schemaVersion=2`
   serializer + round-trip reader. As a *sealed unit of
   data* this is sound and does not need redesign.

2. **CellTranscript — lossy (the navigation layer).** The
   user-facing Manual surface is
   `SpeechCursor.CellTranscript : ResizeArray<string *
   string>` (`SpeechCursor.fs` ~`:175–176`). At the
   IOCell→transcript boundary (`appendCell`,
   `Program.fs` ~`:2050–2051`) the rich typed IOCell is
   **reduced to `(text, activityId)` string pairs with a
   hardcoded `ActivityIds.output`**. Identity (`Id`,
   `CellSequence`), kind (command vs output vs sub-prompt),
   exit code, timestamps — **all discarded at append time.**
   From a transcript position there is no way back to the
   IOCell, and command-vs-output is only inferable by list
   parity. This is a string-list patch, not an extensible
   cell-navigation abstraction — precisely the "layering
   smell" the maintainer's instinct flagged: the user-facing
   surface is data-poor because the *internal* engine is
   Seq-only.

3. **Dual structure — a dead engine alongside the live one.**
   SpeechCursor carries a legacy ContentHistory-Seq engine
   (`next` / `previous` / `toLatest` / `toMarker` / `current`
   / `speakCurrent` / `speakSince`) with **zero production
   callers** (test-only). The *live* paths are: AutoDrive
   `onAppend` + `Position`/`LastSpokenSeq` bookkeeping (still
   a Seq-walk over `ContentHistory`, *not* the cell
   transcript), the Cell Manual surface
   (`cellNext`/`cellPrevious`/`cellToLatest`/`appendCell`/
   `cellReset`), and selection-suspend. Two parallel
   navigation models, one of them dead, is itself an
   obstacle to reasoning about the redesign.

4. **Channel — flat and largely aspirational.** Per ADR
   0002, output is a single flat UIA `TextEdit` document
   (the `ContentHistory` tail, 256 KB cap) with **no
   per-cell UIA structure** and no review-cursor "cell
   document". The `CORE-ABSTRACTION-BOUNDARY.md` §6
   three-sub-pane model and the `CommandOutputTuple` UIA
   primitive (a `Group` wrapping a read-only command `Edit`,
   an output `Document`, an exit-code `Text`) plus the
   `h`/`o`/`Alt+Up`/`Alt+Down` history quick-nav contract
   are **entirely unimplemented** — conceptual targets, not
   code.

5. **Live trickle — ephemeral, the core gap.** R6a progress
   chunks are **transient announces only**: they advance
   `lastAnnouncedSeq` but create **no distinct
   ContentHistory/IOCell boundary**. Only the *final sealed*
   cell is navigable. There is **no mechanism to scroll back
   through a still-arriving response while it streams**, and
   Claude (`IdleFlushMs = None`) emits no progress boundary
   at all. Sub-prompt output is fused into the parent cell's
   `OutputText`, not separately navigable. This is exactly
   the maintainer's stated core mechanic ("review the
   trickle … in the past, as well as the current trickle …
   from an ongoing response") — and it is the least-built
   part of the system.

### What is already catalogued (but scattered and partial)

ADR 0006 §"Deferred to R6+" items **2** (replace SpeechCursor
manual nav with canonical IOCell-history nav + per-cell ops:
copy-output, run-again), **3** (open decision: materialise
IOCell history into the review-cursor UIA document, or keep a
parallel structure?), **4** (live current-line → NVDA
read-current-line), **5** (multi-interrupt IOCell internal
navigable chunks); plus ADR 0004 Decision 2's future-direction
note (navigable chunks within a container, "gated on the
canonical IOCell being solid") and CORE-ABSTRACTION-BOUNDARY
§6. These anticipate most of the target but are (a) scattered
across three documents, (b) explicitly deferred/unsequenced,
(c) silent on the **live in-flight trickle** requirement,
which is new in the maintainer's 2026-05-16 message. This ADR
unifies them into one decision + one sequenced plan and
resolves the open question.

## Decision

Five decisions are locked (Proposed; maintainer to ratify).
None changes the IOCell *data* model (ADR 0004 stands) — this
is the navigation / operations / channel layer on top.

**D1 — The navigable unit is the typed IOCell, bound to
stable cell identity.** `CellTranscript` stops being
`ResizeArray<string * string>`. It becomes an ordered
collection of typed cell *views* that retain the source
IOCell's identity (`Id` / `CellSequence`) and a `CellKind`
discriminator (`Input` | `Output` | `SubPromptExchange` |
`ProgressSegment`). Navigation and every operation bind to
cell identity, never to a `ContentHistory` Seq-walk and never
to list-parity inference. The data already exists on the
IOCell; this decision stops *throwing it away* at the
`appendCell` boundary.

**D2 — A typed per-cell operations layer.** A single typed
dispatch — conceptually `operate(cellRef, op)` — backs every
interaction. v1 operations: `copy-command`, `copy-output`,
`copy-cell` (to clipboard, at the *navigated* cell, not just
"the last one"); `rerun-input` (turn an input cell into a new
input cell — re-submit its `CommandText` as a fresh command);
`jump-to-cell-N`; `jump-to-last-error` (nearest prior cell
with non-zero `ExitCode`). The existing whole-history hotkeys
(`Ctrl+Shift+Y` copy-all, `Ctrl+Shift+O` open-last,
`Ctrl+Shift+A` re-narrate-last) are re-expressed as the
"all" / "last" specialisation of this primitive, not parallel
one-offs.

**D3 — Live in-flight navigability is a first-class
requirement.** The currently-`Executing` cell is itself a
navigable cell whose trickle is reviewable **while it is
still streaming**. Each progress chunk (R6a idle-flush) and,
for Claude, each natural streaming segment becomes an
addressable intra-cell `ProgressSegment` delimited by an
explicit boundary (a new `ContentHistory.MarkerKind`, e.g.
`ProgressBoundary`, emitted where R6a today only advances
`lastAnnouncedSeq`). The user can move back through earlier
segments of an ongoing response and forward to the live edge
without waiting for the seal. This is the progress-loop /
Claude-CLI core mechanic and the part with no current
implementation.

**D4 — One navigation model.** Delete the dead legacy
ContentHistory-Seq engine (`next` / `previous` / `toLatest` /
`toMarker` / `current` / `speakCurrent` / `speakSince` — zero
production callers). Pure subtraction, no behaviour change.
This is the work that was scoped as the R6c "quick patch";
it is **reframed as Phase 0 of this ADR** so the redesign
begins from a single abstraction rather than a dual one. It
ships only as part of this accepted plan, not as a standalone
patch — per the maintainer's "comprehensive design review
rather than a quick patch".

**D5 — Channel: materialise, with a parallel typed source of
truth (resolves ADR 0006 item 3).** The canonical typed cell
history is the source of truth (D1). It is *also* materialised
into the review-cursor UIA document as a structured sequence
(per-cell `CommandOutputTuple`-shaped regions per
CANONICAL-DISPLAY-CATALOG) so that **review-cursor navigation
== cell-history navigation** — the screen-reader user gets
one mental model, not two. Rationale: a parallel-only
structure (the alternative) would leave NVDA's review cursor
walking the flat 256 KB text tail while the "real" history
lived in an inaccessible side model — re-introducing the
two-models problem at the channel layer. The typed model
stays authoritative for operations (copy/rerun bind to
`Id`); the UIA materialisation is a projection. Maintainer to
ratify D5 specifically — it is the one previously-open
architectural decision.

## Consequences — phased plan (walking-skeleton)

One PR + NVDA dogfood per phase, in order; each phase is
independently revertible and adds one capability. No phase
changes the ADR 0004 IOCell schema.

- **Phase 0 — single model.** Delete the 7 dead Seq
  functions + their unit tests; tighten the module docstring
  and the "legacy retained" note. Net-subtractive, **no
  audible behaviour change** (regression-only dogfood:
  `Ctrl+Shift+Up/Down/End` still navigate cells, AutoDrive
  unchanged). Unblocks reasoning about a single abstraction.

- **Phase 1 — typed transcript.** `CellTranscript` carries
  `{ CellId; CellSequence; Kind; Text; ActivityId }` (or a
  reference to the retained IOCell) instead of
  `(string,string)`. Pure refactor; AutoDrive + Manual
  narration output is byte-identical. Unblocks every
  subsequent phase. Dogfood: navigation audibly unchanged;
  the bundle shows the kind tag.

- **Phase 2 — per-cell read/copy operations.** `copy-command`
  / `copy-output` / `copy-cell` at the navigated cell;
  `jump-to-cell` / `jump-to-last-error`. New reserved
  hotkeys (slot allocation per the AppReservedHotkey
  contract). Existing `Ctrl+Shift+Y/O/A` reframed as the
  all/last specialisations.

- **Phase 3 — rerun input as new command.** `rerun-input`
  re-submits the navigated input cell's `CommandText` as a
  fresh command (a new IOCell), with an explicit confirm /
  echo affordance. Risk-controlled: no auto-run on
  navigation; explicit gesture only.

- **Phase 4 — live in-flight trickle.** `ProgressBoundary`
  marker; the Executing cell is navigable with addressable
  `ProgressSegment`s; review past segments + the live edge of
  an ongoing response (progress loop / Claude). Resolves the
  in-flight-materialisation question (D5 applies to the live
  cell too). The headline mechanic.

- **Phase 5 — multi-interrupt intra-cell segments.** Promote
  sub-prompt exchanges to navigable intra-cell segments
  (ADR 0006 item 5 / ADR 0004 Decision 2 future note) —
  *intra-cell segments, not nested cells*; the schema stays
  v2.

- **Phase 6 — channel materialisation + current-line.**
  Materialise the typed history into the review-cursor UIA
  document (D5) as `CommandOutputTuple` regions; propagate
  the live current line to NVDA "read current line"
  (ADR 0006 item 4). Largest channel-side change; sequenced
  last because it depends on the typed model (D1) and the
  segment model (Phases 4–5) being settled.

## Open decisions to resolve (within the phase that needs them)

- **D5 ratification** (Phase 6 gate): materialise-vs-parallel.
  Recommended: materialise + parallel authoritative model.
  Maintainer call.
- **Retention vs the announce ring buffer.** `SessionModel`
  history is a ring buffer (`MaxHistorySize`); evicted cells
  vanish. Proposal: the *navigable* transcript is a separate
  retained structure independent of the announce ring buffer
  so navigation history isn't silently truncated. Bound +
  spill-to-session-log policy TBD (Phase 1).
- **ProgressSegment granularity** (Phase 4): one segment per
  idle-flush chunk vs coalesced cadence. Tie to ADR 0006
  deferred item 9 (queue-and-coalesce); must not regress the
  R3c/R3e watermark composition.
- **Claude `IdleFlushMs = None` interplay** (Phase 4):
  Claude emits no idle-flush boundary today. Either derive
  `ProgressSegment` boundaries from Claude's own
  newline/turn structure, or revisit `IdleFlushMs` for
  Claude. Decide in Phase 4 with a Claude dogfood.
- **rerun-input safety** (Phase 3): confirm gesture,
  echo-before-run, and provenance marking on the new IOCell.

## Alternatives considered

- **Bolt operations onto the string-pair transcript via
  positional inference.** Rejected: the exact layering smell
  the maintainer flagged; command-vs-output parity is
  fragile (whitespace-only cells are skipped), and
  copy/rerun need identity the strings don't carry.
- **Parallel typed model only, no UIA materialisation.**
  Recorded as the D5 alternative. Rejected as the default:
  it strands NVDA's review cursor on the flat text tail and
  recreates a two-models problem at the channel layer.
  Retained as the fallback if materialisation proves
  infeasible under the UIA Text pattern.
- **Promote sub-prompts to nested IOCells (schema v3) now.**
  Rejected for this ADR's scope: ADR 0004 deliberately keeps
  sub-prompts inline in v1; intra-cell *segments* (Phase 5)
  deliver the navigation win without a schema migration.
  Nested-cell schema remains a future ADR if segments prove
  insufficient.
- **Do nothing / ship the R6c quick patch as-is.** Rejected
  by the maintainer's explicit redirect; the patch is folded
  in as Phase 0.

## Relationship to other ADRs

- **Supersedes / sequences** ADR 0006 §"Deferred to R6+"
  items 2, 3, 4, 5 — they become Phases 1–6 here; ADR 0006's
  deferred list points at this ADR for that cluster once
  accepted.
- **Operationalises** ADR 0004 Decision 2's future-direction
  note (navigable chunks within a container) and
  CORE-ABSTRACTION-BOUNDARY §6 (history sub-pane interaction
  contract) + CANONICAL-DISPLAY-CATALOG (`CommandOutputTuple`).
- **Honors unchanged**: ADR 0001 (linear substrate — cells
  are a projection, not a new substrate), ADR 0002 (UIA
  TextEdit channel — Phase 6 structures *within* it), ADR
  0004 (IOCell data model + `schemaVersion=2` — untouched).

## Status / next

**Proposed.** No code lands until the maintainer accepts
this ADR (in particular D5). On acceptance, R6c is replaced
by "ADR 0007 Phase 0…6", each its own PR + dogfood under the
walking-skeleton discipline; the cmd announce-heuristic
FREEZE is unaffected (this is the navigation / operations /
channel layer, not the announce-reconstruction layer). If
the maintainer wants the design narrowed (e.g. defer
Phases 4–6), that scoping is taken here, before Phase 0
ships.
