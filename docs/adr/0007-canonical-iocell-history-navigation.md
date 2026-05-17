# ADR 0007 — SpeechCursor as the canonical navigable IOCell history: typed cells, per-cell operations, and live-trickle review

- **Status**: Accepted (2026-05-16; maintainer directed
  implementation to proceed after extensive co-authoring —
  "this is as much conceptualization as I can do … progress
  on implementation"). D1–D9 + D5a adopted; D5a is the
  adopted resolution of the former-open D5; D8's `Tree`-vs-
  `List` control type is ratified by the Phase 6a dogfood;
  the D9 principle is elevated to
  [ADR 0008](0008-maximal-semantic-surfacing.md).
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

**D7 — Clean demarcation: the cell pipeline sources only
from the typed IOCell layer; it does not depend on or extend
the legacy review-cursor text materialisation.** (Maintainer
strategic concern 2026-05-16.) The new pipeline (typed
transcript D1 → operations D2 → list control D5a) reads its
data **only** from the clean typed IOCell model
(`SessionModel`), never *through* the ADR 0002 ContentHistory
text-tail materialisation (the 256 KB-capped
`ContentHistoryTextProvider` / keystroke-suppression
patchwork that backs the live review-cursor document). That
materialisation stays exactly as-is, serving the **live
current-output** surface only; the cell pipeline neither
reads from it nor adds to it. This is the operative
expression of the ADR 0004/0006 thesis ("do not build clever
things on a fragile substrate"): the demarcation is an
*invariant*, not an aspiration. Concretely:

- **Phase 0** removes the dead legacy Seq engine — the
  redesign then has exactly one model in `SpeechCursor`.
- **Phase 1**'s typed transcript is sourced from the
  finalized `IOCell` at the seal boundary (the existing
  `appendCell` site), *not* re-derived from a
  `ContentHistory` text slice. The portability-lint /
  namespace boundary (ADR 0006 R4) is the enforcement
  surface; a Phase-1 check asserts no new dependency edge
  from the cell pipeline onto the text-materialisation
  module.
- **D6's oracle** doubles as the coupling police: if a
  later phase accidentally routes cell content through the
  legacy text path, the structural drift the patchwork
  introduces (cap truncation, keystroke-suppression gaps)
  surfaces as a corpus-script structure mismatch.
- The legacy materialisation is **not refactored by this
  ADR**. Whether the review-cursor document should later
  become a faithful verbatim record of shell output *or* a
  nicer post-seal variant is a **Phase 6 fork** (recorded
  in Open Decisions), explicitly *not* a near-term task —
  the near-term value is the boundary, not the rework.

**D8 — Control type and update mechanism (expert
recommendation; maintainer deferred this).** The history is
rendered with a **standard WPF control that maps to a
standard UIA control type — never a bespoke
`AutomationPeer`.** This is the single most reliability-
relevant choice: NVDA's most robust, best-tested code paths
are the standard control types; a custom peer is precisely
the fragile path the ADR 0004/0006 thesis warns against.

- **Primary recommendation: `TreeView` → UIA `Tree`.** The
  model is hierarchical (cells → sub-prompt / progress
  segments per D3/D5; the far-field per-cell line-by-line
  drill-in). `Tree` gives NVDA native level / expand-collapse
  / "item N of M, level K" semantics for free, and
  expand-collapse is the natural "drill into a large cell's
  output" affordance.
- **Fallback: `ListBox`/`ListView` → UIA `List`** if the
  first cut is deliberately flat (no segment hierarchy yet).
  Also extremely well-supported; the simpler 6a starting
  point. The model can start as a `List` and grow to a
  `Tree` when segments land (Phase 4/5) without changing the
  typed model.
- Either way the control is a **projection** of the typed
  model (D5a/D1): each node carries the IOCell `Id` behind
  it so D2 operations bind to identity, and the `CellKind`
  drives node role/labelling. The `CommandOutputTuple`
  semantics are conveyed via the standard control's
  name/description/structure, *not* a bespoke peer.
- **Honest caveat**: not locally verifiable here (no NVDA in
  the sandbox). This is an expertise-grounded recommendation;
  the **Phase 6a dogfood is its ratification gate** — the
  phased plan exists precisely so this is validated on a
  minimal control before anything is layered on it. If the
  6a dogfood finds `Tree` mis-behaves under NVDA, fall back
  to `List`; the typed model is unaffected either way.

- **Update mechanism**: append is **event-driven off the
  canonical seal event** (the existing `appendCell` site,
  D7-clean: sourced from the finalized `IOCell`, never a
  `ContentHistory` text slice), marshalled to the UI
  dispatcher thread, into an observable append-only typed
  collection the control is bound to. The standard control
  raises its **standard UIA structure / item-added event**
  so AT discovers the new node **without the focus / review
  position being moved** — auto-follow to the live edge only
  when the user is already at the edge (the D5a Q1/Q2
  contract). Never a poll; the list is a subscriber to the
  canonical pipeline (D9), not a re-derivation.

**D9 — Cell lifecycle / navigation / operation events are
first-class, modality-agnostic events on the canonical
pipeline.** (Maintainer principle 2026-05-16.) Every cell
event — appended, navigated-to, operated-on, segment-arrived
— is emitted as a typed semantic event through the existing
canonical channel surface (`OutputDispatcher`, ADR 0004
Decision 4) with its **own unambiguous `ActivityId`** (new
ids under a `pty-speak.cell.*` family in
`Terminal.Core/Types.fs`), exactly as today's output / mode /
shell-switch events are. Speech (`NvdaChannel`), earcon
(`EarconChannel` / WASAPI `Terminal.Audio`), and **future
modalities — multi-line braille especially — compose from the
same event**; speech is not primary with earcon bolted on.
The cell-history pipeline therefore *is* the application of
ADR 0004 Decision 4 + ADR 0001's substrate/channel dichotomy
to the history surface: one canonical, unambiguous,
typed event stream; many composable sinks. Because the events
are unambiguous and typed, richer composite modalities (a
braille line that mirrors the focused cell while speech reads
it; an earcon that marks cell-kind on navigation) can be
built **without re-deriving meaning** from rendered text.

This makes the computational cell history **the most complete
semantic representation of the interaction that can be
synthesized from the information available** — which is the
point: a shell encodes minimal metadata and leans on a visual
rendering for the user to interpret meaning; pty-speak's role
is to recover the maximal unambiguous semantic structure from
that stream and expose it as composable events and
representations, rather than relaying computationally
ambiguous content.

> **Note — this generalizes beyond ADR 0007; now elevated.**
> The maximal-semantic-surfacing principle in D9's last
> paragraph is a project-wide guiding principle, not specific
> to the cell history. The maintainer directed its elevation
> (2026-05-16); it is now
> **[ADR 0008](0008-maximal-semantic-surfacing.md) (Accepted)**,
> with pointers from ADR 0001 and CORE-ABSTRACTION-BOUNDARY.
> D9 is the cell-history-layer application of that principle;
> ADR 0008 is the canonical statement.

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
  change; ~~sequenced last because it depends on the typed
  model (D1), the per-cell ops (D2 — items host them), and
  the segment model (Phases 4–5) being settled~~ **— amended
  2026-05-17: 6a is decoupled from the segment model and
  pulled forward; it depends only on D1 + D2 (both shipped).
  See § Re-sequencing amendment 2026-05-17.** Splits into
  sub-phases: **6a** basic control (D8: `TreeView`/UIA `Tree`
  primary, `ListBox`/UIA `List` fallback) + `Ctrl+Shift+
  Left/Right` pane switch + the D9 `pty-speak.cell.*` event
  family on the canonical pipeline — *its dogfood is the D8
  control-type ratification gate* → **6b** focus memory +
  kind-filtered jumps → **6c** bookmarks/sections → **6d**
  operation-discovery menu. Nothing past 6a is built until
  6a's NVDA dogfood confirms the control type.

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
  R3c/R3e watermark composition. **DEFERRED (amendment
  2026-05-17): not a now-decision; the one-per-idle-flush
  default stands to be revisited at Phase 4 implementation,
  informed by the boundary-diagnostic capture.**
- **Claude `IdleFlushMs = None` interplay** (Phase 4):
  Claude emits no idle-flush boundary today. Either derive
  `ProgressSegment` boundaries from Claude's own
  newline/turn structure, or revisit `IdleFlushMs` for
  Claude. ~~Decide in Phase 4 with a Claude dogfood.~~
  **SUBSUMED (amendment 2026-05-17) into the
  boundary-diagnostic-capture track; Claude explicitly
  deferred until cmd/PowerShell interface + methodology
  settled.**
- **rerun-input safety** (Phase 3): **RESOLVED 2026-05-17
  (maintainer, post-`52-ADR7-P3` dogfood).** The shipped
  two-step arm/confirm was the conservative reading; the
  maintainer (product owner) resolved it in favour of the
  simple flow: rerun **clears the current prompt line and
  inserts the command at the prompt, NOT auto-run** — the
  user's own Enter is the safety affordance (still "no
  auto-run on a gesture", just satisfied by the human Enter
  rather than a second menu invocation). Echo-before-run =
  the command visible on the prompt line. Provenance =
  announce + counts-only Information log (no new IOCell
  schema field; ADR 0004 v2 unchanged — ADR 0009's typed
  outcome/metadata is the place richer provenance lands if
  ever wanted). Same `insertAtPromptClearingLine` path as
  the diagnostic test-script insertion.
- **Review-cursor document evolution** (Phase 6 fork; *not*
  near-term): the legacy live review-cursor document
  (ADR 0002) is a patchwork. Once the cell pipeline is the
  primary history surface, the live document could become
  (a) a faithful **verbatim** record of shell output exactly
  as the shell presented it, or (b) a **nicer post-seal
  variant** rendered after the cell finalizes. Recorded so
  the fork is not re-discovered; deliberately deferred —
  D7's demarcation means this can be decided independently,
  later, without blocking the cell pipeline.
- **Per-cell content exploration & display options**
  (far-field aspiration; explicitly NOT next-steps,
  maintainer 2026-05-16): line-by-line review *within* a
  large cell's output, and user-requested alternate
  renderings of a raw cell (as a list, as a table). The
  typed cell + D5a list make this *possible* later (the
  item can host a sub-navigation / a render-mode switch),
  but it is far field and must not pull focus from the
  near-term phases. Recorded only so the cell/list design
  does not foreclose it (keep the item's content
  addressable, not pre-flattened).
- **Final control-type pick: `Tree` vs `List`** (Phase 6a
  research + dogfood). D8 recommends `Tree`; the 6a dogfood
  ratifies or falls back to `List`. Decided on real NVDA
  behaviour, not in the abstract.
- **`pty-speak.cell.*` ActivityId taxonomy** (Phase 6a /
  D9): the exact event set (cell-appended, cell-focused,
  cell-operated, segment-arrived, pane-switched) and their
  ids in `Terminal.Core/Types.fs`, so earcon/braille sinks
  have a stable contract. Settle when 6a wires the first
  event; keep ids unambiguous and single-purpose.

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

## Known issue surfaced (not introduced)

The cell history is a faithful projection of upstream
`SessionModel.extractIOCell`. A **pre-existing** substrate
residue therefore shows through it: editing the command line
before Enter (backspace / reflow) desyncs command/output
pairing by one (a phantom input cell). Root cause + the
maintainer's 2026-05-17 `52-ADR7-P2a` dogfood finding +
the "continue phases, track it, no speculative patch"
decision are recorded in **ADR 0006 §"Deferred to R6+"
item 1 ("Tracked variant — command-line-edit input/output
desync")**. This *validates* the ADR's premise: the
navigable history turned a "sounds haywire" defect into a
deterministic off-by-one. **Phase 7's oracle is designed to
pin exactly this** once its harness exists; the clean fix is
ADR 0006 item 1's marker/`Overwrite`-aware reconstruction,
not anything in the ADR 0007 navigation/ops layer.

### Esc line-clear ineffective in `insertAtPromptClearingLine` (Phase 3; tracked, deferred)

Maintainer dogfood 2026-05-17 (post the Phase 3
simplification): both rerun-focused-input and the diagnostic
test-script insertion **insert their text correctly at the
prompt cursor but do NOT clear the user's existing
partially-typed input first** — contrary to the code's
intent (`insertAtPromptClearingLine` prepends a `0x1B`/Esc
specifically to clear the line). This is therefore a real
bug, **pre-existing since Cycle 47** (2026-05-13, when the
Esc-prefix was introduced for the diagnostic-test
insertion — the assumption was never separately verified;
the Phase 3 simplification merely surfaced it by exercising
the clear explicitly). Likely cause (locally unverifiable —
no ConPTY/dotnet in the dev sandbox): a lone `0x1B`
immediately followed by bytes is not delivered to the
shell's line editor as an "Escape key / clear-line" action
(ConPTY escape-sequence disambiguation drops it or metafies
it as `Alt+<char>`); also shell-line-editor-specific (the
assumption is documented as *cmd cooked-mode*; PSReadLine
differs regardless — the shell-at-test-time was not captured
and must be confirmed on the eventual fix dogfood). **Decision
(maintainer 2026-05-17): track + defer; do not block the ADR
0007 phase sequence.** The insert (core capability) works;
the missing clear is a polish gap. A correct fix needs a
ConPTY-input dogfood-iteration loop and is *not* a safe blind
patch. Recorded here, in the `insertAtPromptClearingLine`
code comment, the `52-ADR7-P3` matrix row, and CHANGELOG.

## Status / next

**Accepted (2026-05-16).** The maintainer co-authored
D1–D9 + D5a and directed implementation to proceed ("this is
as much conceptualization as I can do … progress on
implementation"). R6c is replaced by **"ADR 0007 Phase 0…7"**,
each its own PR + dogfood under the walking-skeleton
discipline; the cmd announce-heuristic FREEZE is unaffected
(this is the navigation / operations / channel layer, not the
announce-reconstruction layer). No scope narrowing was
requested — the full Phase 0…7 is in force.

Adopted specifics: **D5a** is the adopted resolution of the
former-open D5 (history = focusable standard control + pane
switch); **D8** recommends `TreeView`/UIA `Tree`
(`List` fallback) with the **Phase 6a NVDA dogfood as the
control-type ratification gate** — `Tree` is not locked until
6a confirms it under NVDA; **D9**'s project-wide principle is
elevated to [ADR 0008](0008-maximal-semantic-surfacing.md)
(Accepted). **D6/Phase 7 changes the economics of the whole
plan**: once the cell history is an assertable on-send
mirror, every later phase's dogfood is mostly a structural
self-check the agent runs, with only a thin audible-delivery
confirmation left to the maintainer — so the walking-skeleton
loop gets materially cheaper as the phases progress.

**Implementation order**: Phase 0 → 1 → 2 → 3 shipped
CI-green. **Re-sequenced 2026-05-17** (see
[§ Re-sequencing amendment](#re-sequencing-amendment-2026-05-17-maintainer-ratified)):
**Phase 6a now → boundary-diagnostic track → Phase 4/4b/5 →
Phase 6b+ / Phase 7**, each gated by its own CI + dogfood
(6a's NVDA dogfood = the hard D8 control-type ratification
gate). CI failures and essential blocking questions are
raised via `AskUserQuestion` (phone notification), per the
maintainer's standing instruction, not buried in chat text.

**Ship log (per-phase, updated as phases land):**

- **Phase 0** — Implemented & CI-green (dead Seq engine
  deleted; net-subtractive).
- **Phase 1** — Implemented & CI-green (typed
  `CellTranscript`/`CellView`; narration byte-identical).
- **Phase 2a** — Implemented, CI-green, **dogfood
  feature-PASSED 2026-05-17** (`Ctrl+Shift+C` + menu copy
  whole cell). Surfaced the pre-existing command-line-edit
  desync → ADR 0006 deferred item 1 "Tracked variant 1".
- **Phase 2b** — Implemented, CI-green, **dogfood
  feature-PASSED 2026-05-17** (menu-only copy-command /
  copy-output; output-copy + Manual-nav focus-hold
  confirmed). Surfaced the clean-command argument
  truncation → ADR 0006 deferred item 1 "Tracked
  variant 2".
- **Phase 2c** — Implemented, CI-green, **dogfood
  feature-PASSED 2026-05-17** under PowerShell
  (`cmd /c exit 7` → Jump to Last Error jumps to it).
  Scope clarified: exit-code failure detection sees
  **external-process non-zero exits under PowerShell**
  only — cmd transports no exit code (documented
  limitation) and PowerShell *cmdlet* errors don't set
  `$LASTEXITCODE`. Follow-up shipped: cmd now speaks an
  honest shell-gated capability message instead of the
  misleading "No failed command in history." The
  test-surfaced boundary confusion = the already-tracked
  ADR 0006 item-1 variants 1 & 2, not a Phase 2c bug.
- **Phase 3** — Implemented & CI-green. Mechanics
  dogfood-PASSED 2026-05-17; **behaviour then simplified
  per maintainer UX direction** (the "rerun-input safety"
  open decision, resolved above): the two-step arm/confirm
  + `TerminalView.InjectCommand` (auto-run) were removed;
  rerun now **clears the prompt line + inserts the command,
  no auto-run** via the shared `insertAtPromptClearingLine`
  primitive (the same Esc-clear path the diagnostic
  test-script insertion uses; `InjectCommand` deleted as
  dead code). Re-dogfood row `52-ADR7-P3` (new behaviour)
  pending.
- **Phase 4** — **deferred by the 2026-05-17 re-sequencing
  amendment** (was the autonomous-sprint stop point). Open
  decision A's default stands to be revisited here; B is
  subsumed into the new boundary-diagnostic-capture track;
  **C is RESOLVED — C1** + the differentiable-kind / filtering
  requirement. Phase 4/4b/5 now sequence *after* Phase 6a +
  the boundary-diagnostic track. See
  [§ Re-sequencing amendment](#re-sequencing-amendment-2026-05-17-maintainer-ratified)
  (it supersedes the "Phase 4 readiness brief" below, which is
  retained as the decision trail).
- **Phase 6a** — **pulled forward; STARTS NOW** per the
  re-sequencing amendment. Decoupled from the segment model;
  depends only on D1 + D2 (both shipped). Kind-generic
  focusable history list (D8) + `Ctrl+Shift+Left/Right` pane
  switch + the D9 `pty-speak.cell.*` event family, on existing
  cmd/PowerShell cells. Its NVDA dogfood remains the **hard
  D8 control-type ratification gate**.
- **Phases 4/4b/5 → 7** — sequence after Phase 6a + the
  boundary-diagnostic track (5 = intra-cell segments on the
  settled model; 7's oracle asserts it). Each lands as its
  own CI-green PR + `52-ADR7-P*` dogfood row.
- **Cell metadata / typed outcome** (maintainer ask
  2026-05-17) —
  [ADR 0009](0009-canonical-cell-metadata-and-typed-outcome.md)
  (**Accepted** 2026-05-17; not yet implemented). Its D5
  generalises **Phase 6b** kind-filtered jumps to
  outcome/tag-filtered search; its P-B backs **Phase 6c**
  bookmarks; D3 ratifies the D9/Phase-6a
  `CellId`-as-focus-key contract. Independent of Phase 4;
  not in the autonomous sprint; P-A landable when the
  maintainer schedules it.

## Phase 4 readiness brief (autonomous-sprint stop point, 2026-05-17)

> **⚠ SUPERSEDED 2026-05-17 by
> [§ Re-sequencing amendment](#re-sequencing-amendment-2026-05-17-maintainer-ratified).**
> Retained as the decision trail (the delta between this
> proposal and the ratified re-sequencing is itself a useful
> artifact, per the project's mark-historical-don't-delete
> discipline). Things this brief says that are now untrue:
> "Phase 4 is the deliberate stop / one word ('C1, proceed')
> unblocks 4a" — the maintainer instead re-sequenced (Phase 6a
> pulled forward; A deferred; B folded into the
> boundary-diagnostic track; C = C1 + a differentiable-kind /
> filtering requirement). Read the amendment for the live
> plan; the A/B/C analysis below remains accurate as the
> *input* to that decision.

Phases 0–3 shipped CI-green this sprint (2a/2b dogfood
feature-PASSED; 2c/3 dogfood rows pending). Phase 4 is the
deliberate stop. This brief is what the maintainer needs to
unblock it.

**Open decision A — ProgressSegment granularity — RESOLVED
(agent, conservative default).** Recommendation: **one
ProgressSegment per idle-flush chunk** (cmd/PowerShell
`IdleFlushMs = Some 350`), i.e. reuse the *existing* idle-flush
boundary as the segment boundary. Rationale: it introduces no
new coalescing layer, so it **cannot regress the R3c/R3e
watermark composition** (the explicit constraint in the open
decision); coalesced-cadence is a later refinement once real
navigation feel is known. This needs no maintainer input —
adopt unless overridden.

**Open decision B — Claude `IdleFlushMs = None` interplay —
BLOCKED on a maintainer Claude dogfood.** Claude emits no
idle-flush boundary, so option-A's segment boundary does not
exist for the Claude shell. The ADR's two candidates:
(B1) derive ProgressSegment boundaries from Claude's own
newline/turn structure, or (B2) revisit `IdleFlushMs` for
Claude. **The dogfood needed:** run a multi-line / streaming
Claude response under the current build and report whether
the *announce cadence* already chunks at usable points
(favours B1 — segment on the same boundaries the announce
already uses) or arrives as one undifferentiated blob
(favours B2 — give Claude a non-`None` `IdleFlushMs`). This
is recorded as **Phase 4b**.

**Open decision C — ProgressSegment ↔ sealed-cell model
relationship — needs maintainer design ratification (it
cascades into Phases 5/7).** When the cell seals, `appendCell`
already adds the authoritative `Output` item. If live
ProgressSegments were also appended during Executing, the
transcript holds *both* the per-chunk segments and the final
consolidated Output for one cell. Candidates: (C1) keep both
— segments are the immutable live-trickle record, the sealed
`Output` is the authoritative consolidation (lowest-risk:
`appendCell` untouched, zero regression to Phases 0–3 + the
dogfood-validated narration; the agent's recommendation);
(C2) on seal, replace the segments with the consolidated
`Output`; (C3) on seal, suppress the `Output` item when
segments cover it. **Only a dogfood answers which feels
right** (does hearing segments-then-a-final-Output read as
redundant or as a useful recap?). Phases 5 (intra-cell
segments) and 7 (the oracle asserts the segment structure)
are built on whichever model is chosen — hence this is the
maintainer's call, not the agent's.

**Proposed split for maintainer review:**

- **Phase 4a** = ProgressSegment data primitive
  (`SpeechCursor.appendProgressSegment`, pure + unit-tested)
  + the idle-flush→segment feed for cmd/PowerShell, under
  the **C1** model (recommended) — *contingent on the
  maintainer ratifying C1*. Kind-agnostic navigation
  (`cellPrevious/cellNext/cellToLatest`) already makes
  appended segments navigable with no accessor change.
- **Phase 4b** = Claude segment derivation, decided by the
  decision-B dogfood.

The agent did **not** pre-build 4a because (a) C1 is a
ratification, not an agent default — wrong choice cascades
into 5/7; (b) shipping an `appendProgressSegment` with a
guessed live-feed integration and no compiler/dogfood
feedback is exactly the half-finished speculative scaffolding
the guidelines forbid. One word from the maintainer ("C1,
proceed") unblocks 4a immediately.

## Re-sequencing amendment 2026-05-17 (maintainer-ratified)

The maintainer reviewed the Phase 4 readiness brief and,
rather than simply unblocking 4a, **re-sequenced the plan**.
Verbatim intent (2026-05-17): keep every piece of the
interaction and tag each with a clearly-differentiable kind so
the user can *skip or filter* segments in the interface; take a
comprehensive, timestamped diagnostic record of every event
from each shell (the Claude Code CLI especially — spinner +
time-interval chunks into an ongoing thread) so boundary
markers are derived from real signal analysis, not guesswork;
**focus cmd + PowerShell first and get the cell-history
interface + navigation scaffolding in place before engaging
other-shell (Claude) boundary complexity.** The maintainer
explicitly asked whether the interface can be scaffolded now
even with imperfect cell boundaries — it can (rationale
below) — and confirmed "proceed with autonomy" (2026-05-17).
This amendment is the ratified record; it does **not** change
the IOCell data model (ADR 0004 stands) or D1–D9/D5a.

### Open decisions A / B / C — resolved by this amendment

- **A — ProgressSegment granularity — DEFERRED, not a
  now-decision.** The maintainer's question ("do you really
  need this right now?") is correct: granularity only bites
  when segment *generation* is implemented (Phase 4), which
  this amendment defers behind the interface scaffolding and
  the boundary-diagnostic track. The brief's conservative
  default (one segment per idle-flush chunk; no new coalescing
  layer; cannot regress the R3c/R3e watermark) **stands as the
  default to revisit at Phase 4 implementation**, informed by
  the boundary-diagnostic capture — not decided now.

- **B — Claude `IdleFlushMs = None` interplay — remains
  blocked, now subsumed into the boundary-diagnostic track.**
  Claude's complexity (spinner + interval chunks into an
  ongoing thread) is **explicitly deferred** until the
  cmd/PowerShell interface and the boundary-determination
  methodology are settled. The B1-vs-B2 dogfood the brief
  asked for is replaced by — and folded into — the
  comprehensive event-capture track defined below: the
  capture *is* the analysis B needs, done from recorded
  signal rather than a single listen-and-report pass.

- **C — ProgressSegment ↔ sealed-cell model — RESOLVED:
  C1, with an added differentiable-kind / filtering
  requirement.** The maintainer ratified **C1** (keep both:
  the immutable live-trickle `ProgressSegment`s *and* the
  sealed consolidated `Output` — `appendCell` untouched, zero
  regression to Phases 0–3 and the dogfood-validated
  narration). Added requirement: `ProgressSegment` **must be
  a clearly-differentiable `CellKind`** so the interface can
  let the user skip it, or later choose to show / hide it, or
  show only the final result. This is satisfiable **by
  construction** — `CellKind.ProgressSegment` is already a
  distinct case in D1's discriminator, separate from
  `Input` / `Output` / `SubPromptExchange`. Consequently:

  > **Cell-history filtering is elevated to a first-class
  > requirement.** The pipeline keeps and distinctly tags
  > everything; the *interface* decides what to show. Filter
  > axes: by `CellKind` (e.g. hide `ProgressSegment`, show
  > only `Input`/`Output`, "final result only"), and — once
  > [ADR 0009](0009-canonical-cell-metadata-and-typed-outcome.md)
  > lands — by `CellOutcome` / tag. This is the same surface
  > as **Phase 6b** kind-filtered jumps and **ADR 0009 D5**
  > outcome/tag-filtered search; they are now drawn together
  > under one explicit requirement rather than treated as
  > independent niceties. No model change is needed for it —
  > only that the list (Phase 6a) is built kind-generically
  > (it renders *any* `CellView` by `Kind`), which the
  > re-sequencing requires anyway.

### The interface scaffolding is decoupled from boundary correctness

The original Phase 6 description sequenced 6a "last because it
depends on the typed model (D1), the per-cell ops (D2), **and
the segment model (Phases 4–5) being settled**." The middle
clause was conservative over-coupling. Phase 6a — the
focusable cell-history list control (D8), the
`Ctrl+Shift+Left/Right` pane switch, and the D9
`pty-speak.cell.*` event family — depends only on **D1
(Phase 1, shipped)** and **D2 (Phase 2, shipped)**. It renders
*any* `CellView` by its `Kind`; `ProgressSegment` is simply
another kind it will render once segments exist. **Imperfect
cell boundaries do not block it**: a mis-split cell still
renders, is still navigable, still hosts per-cell ops — the
boundary bugs are an orthogonal, already-tracked track (ADR
0006 §"Deferred to R6+" item 1 variants 1 & 2), and improving
them later only improves the *content inside* an
already-working list, with no interface rework. **No extra
exemplars are needed for the interface component itself** —
it is structural (a standard UIA control bound to
`CellTranscript` + the pane-switch gesture + cell events).
The hard **D8 control-type NVDA-dogfood ratification gate is
unchanged**: nothing past 6a is built until 6a's dogfood
confirms `Tree` (or falls back to `List`).

### New work item — boundary-diagnostic-capture track

The maintainer's diagnostic-record idea is adopted as a
**distinct track**, parallel to / after the interface
scaffolding and feeding Phase 4 + Phase 7:

> Capture a comprehensive, high-resolution-**timestamped**
> record of every event observed from each shell transport
> (and the Claude Code CLI) — bytes/chunks in, idle-flush
> ticks, OSC-133 boundaries, state-machine transitions,
> announce sends, seal events — with whatever metadata is
> available, so reliable **begin/end-of-evaluation** signals
> are determined by *analysing real recorded signal*, not
> inferred by ear. **cmd / PowerShell first** (the clean
> OSC-133 reference); **Claude deferred** until the interface
> and the methodology on the simpler shells are settled.

This is the ADR-0008-aligned, D6-oracle-feeding successor to
"the maintainer eventually notices it sounds wrong": it turns
boundary determination into a data exercise. Its **detailed
design** (exact event set, capture format, the offline /
in-app analysis surface, retention) is itself a scoped work
item to be specified when the track is taken up — **recorded
here, deliberately not built speculatively now** (per the
half-finished-scaffolding guideline). It graduates to its own
ADR if it grows past a tracked work item.

### Net implementation order (supersedes the brief's "C1, proceed → 4a")

1. **Phase 6a — interface scaffolding, NOW.** Kind-generic
   focusable history list (D8 `Tree`-primary / `List`-fallback)
   + `Ctrl+Shift+Left/Right` pane switch + the D9
   `pty-speak.cell.*` event family on the canonical pipeline,
   on existing cmd/PowerShell `Input`/`Output` cells. Its NVDA
   dogfood **is** the D8 control-type ratification gate.
2. **Boundary-diagnostic-capture track** — parallel/after;
   cmd/PowerShell first, Claude deferred.
3. **Phase 4 / 4b / 5 — segment model**, layered into the
   now-proven list, informed by the capture (A's default
   revisited here; B decided from recorded signal).
4. **Phase 6b+ filtering** (kind / outcome / tag) layered on
   6a per the elevated first-class requirement; **Phase 7
   oracle** asserts the settled segment structure.

Phases 0–3 status in the Ship log is unchanged. The cmd
announce-heuristic FREEZE is unaffected (this is the
navigation / interface / channel layer). Phase 6a starts
immediately as its own CI-gated PR + NVDA dogfood under the
walking-skeleton discipline.
