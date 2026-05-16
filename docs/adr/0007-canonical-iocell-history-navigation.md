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

**D5a — Recommended D5 resolution: render the history as a
real focusable list control, with a pane-switch gesture.**
(Maintainer proposal 2026-05-16.) Rather than hand-
materialising the typed history into the existing flat
review-cursor *text* document, render it as an actual
on-screen **list control** — each cell an item (the
`CommandOutputTuple` shape from CANONICAL-DISPLAY-CATALOG as
the item template / automation peer). `Ctrl+Shift+Left` /
`Ctrl+Shift+Right` switch the *pane / mode* between the
live command-interaction surface and this history list; the
user then moves through the list with the **standard list
keys assistive tech already maps natively** (item up/down,
Home/End, type-ahead, "list with N items, item M of N"),
not bespoke app hotkeys. This is the recommended way to
satisfy D5's "one mental model" goal and is preferred over
text-document materialisation because:

- A standard list control with correct automation peers is
  AT-compatible *by construction* — NVDA gets native list
  browse semantics without a bespoke text projection whose
  review-cursor behaviour we'd have to tune and keep aligned.
- It shrinks the custom-hotkey surface (familiar list keys
  vs. learn-and-document app gestures).
- It cleanly divides the three-sub-pane model
  (CORE-ABSTRACTION-BOUNDARY §6): ADR 0002's TextEdit caret
  stays the **live current-output** surface (the streaming
  trickle); the **history** sub-pane becomes this focusable
  list. The two-models tension D5 worried about dissolves —
  current-output and history are deliberately distinct
  panes, each with the control type that fits it, joined by
  the `Ctrl+Shift+Left/Right` switch.
- Each list item is the natural host for the D2 per-cell
  operations (invoke / context-menu patterns → copy command
  / copy output / rerun input), so D2 and the channel
  rendering converge instead of being layered separately.

Open questions this raises (to resolve in Phase 6, recorded
not glossed):

1. **Focus vs. the live shell.** While focus is in the
   history list, keystrokes go to the list, not the shell.
   The pane-switch gesture is the control for this; Phase 6
   must define what returns focus to the terminal, and
   whether/how the live trickle keeps announcing (D3) while
   the user is browsing history (this maps onto the existing
   AutoDrive-suspend-during-Manual model).
2. **A live-updating item under AT.** When the executing
   cell is an item in the list and its `ProgressSegment`s
   are still arriving (D3 / Phase 4), the item is mutating
   while the user may be parked on or near it. Phase 6 must
   define the UIA update contract (item-added / live-region
   semantics) so new segments are discoverable without
   yanking the user's position.
3. **Retention vs. virtualisation.** The list must
   virtualise for long sessions yet stay bound to the
   retained typed transcript (the retention-vs-ring-buffer
   open decision below), so scrolling far back does not
   fall off the announce ring buffer.

D5a is the **recommended** resolution; the plain-text-
materialisation form of D5 and the parallel-only form are
retained as the recorded alternatives if the list-control
form proves infeasible under the WPF/UIA stack. Maintainer
to ratify D5/D5a together.

**D5a considerations to carry into the list design**
(maintainer-flagged 2026-05-16; explicitly *not* decided
here — the basic list lands first and these are felt out
against real navigation, then resolved in the phase that
owns each). Recorded so they are not re-litigated when we
arrive:

- **Per-pane focus memory.** Each pane (live interaction /
  history list) remembers its last-focused position; the
  `Ctrl+Shift+Left/Right` switch restores focus to where the
  user last was in that pane, not to a reset position.
  (Phase 6; interacts with D5a open question 1.)
- **Kind-filtered structured jumps.** Beyond item-by-item:
  jump-to-first-cell, jump-to-last-cell, previous/next
  **input** cell, previous/next **output** cell. These fall
  out of D1's `CellKind` almost for free (the analogue of
  assistive tech's jump-by-element-type quick-nav within the
  list). Hotkey slots per the AppReservedHotkey contract.
  (Phase 6, building on D1/D2.)
- **User bookmarks and section markers.** Let the user drop
  lightweight landmarks into the history (a bookmark on a
  cell; a named section boundary) and jump between them.
  This is *user-authored* data, distinct from shell-derived
  cells — open question to resolve when built: session-only
  vs. persisted alongside the session log (ties to the
  retention decision). (Phase 6+; new lightweight concept.)
- **Operation discovery without breaking review.** The D2
  per-cell operations must be discoverable without
  interrupting coherent linear review. Two complementary
  surfaces, both acting on the **focused** cell: the
  assistive-tech-standard context menu (Applications key /
  Shift+F10) on the focused item, and a single
  "operate-on-focused-cell" menu (a command list the user
  opens deliberately). Decide the exact split when the list
  exists and the review feel is real — the constraint is
  that opening/closing the menu must not move or lose the
  review position.

These are subtle and partly un-imaginable before the basic
list exists; the list (Phase 6) is the place to start, and
each item above is layered and validated against real
navigation afterwards, not built speculatively up front.

**D6 — The cell history is the assertable record of what was
sent to the channel (a test oracle).** (Maintainer
contribution 2026-05-16.) **Invariant: every channel send
writes a correspondingly-bounded cell / segment on the *same
code path*, at the same moment, so that for every announce
`announced_text == the text of the cell/segment it produced`,
with the cell/segment boundaries matching the announce
boundaries.** Consequence: the cell history becomes a
machine-readable mirror of the announce stream. A scripted
scenario (e.g. the multi-interrupt cmd script — its
sub-prompt components come back as chunks appended to the
latest output cell) can be **validated by asserting the
resulting cell/segment structure against an expected
structure**, rather than the maintainer listening and
reporting what they heard. This collapses most of the
listen-and-report dogfood loop into automated structural
assertions the app (or a test) checks itself. The residual
that remains genuinely audible-only is "did NVDA actually
voice it" — and even that shrinks: because the history write
is *on the send path* (not a parallel reconstruction), a
correctly-bounded entry in the history is itself evidence the
send was issued; only the NVDA-side delivery (a TTS/SAPI
concern outside this process) is left for a thin human or
NVDA-hook confirmation. This is **why** D1's typed cells and
D3's explicit segment boundaries are load-bearing beyond
navigation: they are also the assertion surface.

Stated positively (maintainer, 2026-05-16): a *predetermined*
command (a corpus script) has a **known-correct expected cell
structure**. Asserting the actual written structure against
that expected is therefore a precise **drift detector for the
cell-boundary-determination methodology** — any mismatch is
an immediate, localised signal that boundary detection has
regressed. This is a standing guard against exactly the
failure mode that drove cycles 45–51 (boundary / row-index
heuristics silently drifting under scroll / multi-run
accumulation; the ADR 0004 origin story): instead of the
maintainer eventually noticing "things go haywire" by ear,
the corpus self-check fails the moment a boundary moves.

One subtlety this rests on: the expected structure must be an
**independent pin** — authored from a known-correct baseline
(or a frozen golden capture), *not* re-derived at assert time
from the same boundary code under test. If the expectation
were generated by the very logic being validated, drift would
move both in lockstep and the assert would pass regardless —
it would have no independent reference against which to detect
the regression. So Phase
7's expected-structure fixtures are hand-authored / golden-
frozen per scenario, reviewed when the scenario's correct
shape is itself (re)decided — never auto-generated from live
output. (The remaining residual is the narrow case where a
boundary is wrong *and* the independent pin happens to agree;
pinning structure — kinds / order / segment count / boundary
positions — rather than exact environment-dependent text
keeps that residual small.)

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

- **Phase 6 — history pane + current-line.** Per the
  recommended **D5a**: render the typed history as a
  focusable list control (`CommandOutputTuple` item peers),
  reachable via the `Ctrl+Shift+Left/Right` pane switch and
  navigated with standard list keys; resolve the three D5a
  open questions (focus vs. live shell, live-updating item
  under AT, virtualisation vs. retention). Propagate the
  live current line to NVDA "read current line" (ADR 0006
  item 4). If D5a is rejected at ratification, this phase
  instead materialises into the review-cursor text document
  (the plain D5 form). Then layer the **D5a considerations**
  (per-pane focus memory; kind-filtered jumps —
  first/last/prev-next input/output; user bookmarks +
  section markers; the operation-discovery menu surfaces) —
  each validated against real navigation feel once the basic
  list works, not built speculatively. Largest channel-side
  change; sequenced last because it depends on the typed
  model (D1), the per-cell ops (D2 — items host them), and
  the segment model (Phases 4–5) being settled. May itself
  split into sub-phases (6a basic list + pane switch → 6b
  focus memory + structured jumps → 6c bookmarks/sections →
  6d operation-discovery menu) once the basic list exists
  and the feel is real.

- **Phase 7 — automated cell-structure diagnostics (D6).**
  Extend the existing test corpus + `Diagnostics → Test …`
  menu so each scripted scenario carries an **expected
  cell/segment structure** (kinds, order, segment count,
  boundary positions — not exact text, which is environment-
  dependent), and the app self-checks the written history
  against it. The multi-interrupt cmd script is the first
  target (assert: one input cell, then N `SubPromptExchange`
  segments in order within the latest output cell, each
  non-empty, boundaries where the `;`-protocol /
  state-machine transitions say). Turns "maintainer listens
  and reports" into "agent runs the script and asserts the
  structure"; the remaining manual step is a thin
  audible-delivery confirmation, not a transcription. Beyond
  cheaper dogfood, this is a **standing regression guard on
  the boundary-determination methodology**: it stays in CI /
  the diagnostics menu and fails the instant a boundary
  drifts on a pinned scenario — the automated successor to
  "the maintainer eventually notices it sounds wrong."
  Expected-structure fixtures are hand-authored / golden-
  frozen (the independent-pin requirement in D6), not
  generated from live output. Sequenced after Phases 4–5
  because the oracle asserts the segment model those phases
  build. Each prior phase may *retro-add* its scenario's
  expected-structure assertion here once Phase 7's harness
  exists.

## Open decisions to resolve (within the phase that needs them)

- **D5 / D5a ratification** (Phase 6 gate). Recommended:
  **D5a** — render history as a focusable list control with a
  `Ctrl+Shift+Left/Right` pane switch and standard list-key
  navigation, parallel typed model authoritative. Fallbacks
  (recorded): plain D5 text-document materialisation, or
  parallel-only. Maintainer call; ratify D5/D5a together.
- **Retention vs the announce ring buffer.** `SessionModel`
  history is a ring buffer (`MaxHistorySize`); evicted cells
  vanish. Proposal: the *navigable* transcript is a separate
  retained structure independent of the announce ring buffer
  so navigation history isn't silently truncated. Bound +
  spill-to-session-log policy TBD (Phase 1).
- **Bookmark / section persistence** (Phase 6c): user-
  authored landmarks are session-only vs. persisted
  alongside the session log. Decide with the retention
  decision above (same lifetime question, user-data
  flavour).
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
this ADR (in particular D5/D5a — the recommended history
rendering is a focusable list control with a
`Ctrl+Shift+Left/Right` pane switch and standard list-key
navigation). On acceptance, R6c is replaced
by "ADR 0007 Phase 0…7", each its own PR + dogfood under the
walking-skeleton discipline; the cmd announce-heuristic
FREEZE is unaffected (this is the navigation / operations /
channel layer, not the announce-reconstruction layer). If
the maintainer wants the design narrowed (e.g. defer
Phases 4–7), that scoping is taken here, before Phase 0
ships. **D6/Phase 7 changes the economics of the whole
plan**: once the cell history is an assertable on-send
mirror, every later phase's dogfood is mostly a structural
self-check the agent runs, with only a thin audible-delivery
confirmation left to the maintainer — so the walking-skeleton
loop gets materially cheaper as the phases progress.
