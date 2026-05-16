# ADR 0001 — Substrate / channel dichotomy as a non-negotiable architectural constraint

- **Status**: Accepted
  - **Status note (2026-05-16)**: the *purpose* this
    dichotomy serves is now stated as a project-wide
    guiding principle in
    [`0008-maximal-semantic-surfacing.md`](0008-maximal-semantic-surfacing.md)
    (Accepted) — recover maximal unambiguous semantics and
    emit typed canonical events; never relay computationally
    ambiguous content. ADR 0001 is the *mechanism* (substrate
    vs. channel); ADR 0008 is the *why*. Read 0008 alongside
    this.
- **Date**: 2026-05-09
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: PROJECT-PLAN-2026-05-09 §A (architectural
  assertion); Cycle 30 doc-only PR.
- **Companion docs**:
  - [`../CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md)
    — full architectural framing.
  - [`../PROJECT-PLAN-2026-05-09.md`](../PROJECT-PLAN-2026-05-09.md)
    — strategic plan that depends on this ADR.

## Context

Through Stages 1-7, pty-speak's substrate has been the
**screen grid** — a 2D `(row, col)` cell array maintained by
`Terminal.Core.Screen` and updated by VT events emitted from
`VtParser`. Channels (NvdaChannel, FileLoggerChannel,
EarconChannel) consume `OutputEvent`s emitted by
`StreamPathway`, which derives those events by **diffing the
screen grid row-by-row between flushes**.

This works for TUI workloads (vim, htop, less, anything that
draws an alt-screen interface) because alt-screen apps are
inherently 2D. But for **linear / streaming text** workloads —
plain-cmd / plain-PowerShell output, `claude --dangerously-skip-
permissions` text and tool-use output, build / compile output —
the screen grid is the **wrong** primary substrate. The byte
stream existed first; the screen grid was derived from it; but
under today's architecture the channels read the derived form
back as if it were primary.

Concrete failure modes confirmed in production (Cycle 29b NVDA
validation 2026-05-09):

- **Spinner storms** — Claude's thinking-state spinners produce
  ~80 NVDA announces per Claude turn. Identical-hash dedup
  catches repeats but the incrementing token-counter on each
  spinner frame defeats it.
- **Red-tone misfires** — Claude's spinner glyphs are red-
  coloured, so the row-diff path fires `ErrorLine` events for
  every spinner frame; ~30 `error-tone` earcons per Claude
  turn.
- **`extractCommandAndOutput` fragility** — `SessionModel.fs:316-
  427` reconstructs (command, output) tuples by walking screen-
  grid rows, duplicating the parsing work and reintroducing
  edge cases.

The fix requires **inverting the substrate**: linear text
becomes the source of truth for linear workloads; the screen
grid becomes one of several derived projections (used only for
alt-screen content). The inversion is a multi-cycle effort
(planned across Cycles 33-35); it requires a stable
architectural commitment to be sound.

## Decision

The maintainer-blessed canonical assertion (lifted verbatim
from PROJECT-PLAN-2026-05-09 §1) becomes a **non-negotiable
architectural constraint**:

> 1. **The natural language structure of the way that we think
>    of text in the terminal is to model the way the bytes
>    flow.** A terminal is a stream of bytes. Linear text is the
>    natural shape of those bytes. The screen grid is one
>    derived projection of the byte stream, suitable for alt-
>    screen TUIs; it is not the canonical substrate.
> 2. **As the bytes are flowing through the terminal, we want
>    to apply NLP-style methodologies to dissect aspects from
>    that stream of bytes.** The byte → semantic-event pipeline
>    is structurally analogous to a tokenize → parse → classify
>    → enrich NLP pipeline. Each stage adds a derived layer of
>    structure without erasing the prior stages.
> 3. **Various canonical formats of display will be deployed in
>    a way that we currently call channels.** A canonical
>    display is a presentation primitive with a fixed UIA control
>    type, an ARIA role analog, a reading pattern under each
>    screen reader, and an interaction contract. Channels deliver
>    canonical-display payloads; the substrate produces them.
> 4. **It's important that we structure the application in a way
>    that the methods of dissection can be ported to other
>    operating systems.** Substrate is OS-portable; channels are
>    OS-specific (UIA on Windows, AT-SPI on Linux,
>    NSAccessibility on macOS). The boundary between them is the
>    portability seam.

The four parts together yield: **a portable, layered substrate
that produces canonical-display payloads, consumed by OS-
specific channel adapters.**

## Consequences

### Positive

- **Linear workloads (Claude, plain cmd / pwsh, build output)
  get a substrate that fits their natural shape.** Spinner
  storms, red-tone misfires, and `extractCommandAndOutput`
  fragility all become tractable once the linear-text producer
  ships (Cycle 34) and the Stream profile rebuilds against it
  (Cycle 35).
- **The channel layer becomes purely presentational.** Profiles
  pattern-match on semantic events; channels render
  `ChannelDecision` payloads. No channel-side reclassification
  logic; no substrate-state-leakage into UIA peers.
- **Cross-platform is a structural property, not a future
  retrofit.** The substrate / channel boundary doubles as the
  Windows / Linux / macOS portability seam. A future
  `Terminal.Channels.AT-SPI` Linux adapter consumes the same
  `ChannelDecision` records as `Terminal.Accessibility`'s
  Windows UIA channel.
- **Multi-pane consumes the same substrate.** The three-sub-
  pane decomposition (command-input / current-output / history)
  + three reserved peer panes (notification queue, contextual
  keyword info, input assistant) are channel-layer consumers.
  Adding a new pane does not require new substrate plumbing.

### Negative / costs

- **A multi-cycle inversion (Cycles 33-35).** The shipping
  Stream profile must rebuild against the linear substrate
  without regressing the existing NVDA matrix. Cycle 36 is a
  dedicated validation gate for this.
- **Two parallel substrates exist during the transition.** The
  screen grid stays the primary substrate until Cycle 35; the
  linear-text producer runs in parallel from Cycle 34. The
  two-substrate window is a complexity tax accepted as the
  price of a safe inversion.
- **Existing tests keyed to screen-row-diff semantics need
  updating.** Per CONTRIBUTING.md "Tests with semantic-laden
  assertions need updating when semantics change", every test
  that asserts on `OutputEvent.Payload` content under the
  current row-diff semantics needs to be reviewed against the
  new suffix-since-last-commit semantics in Cycle 35.
- **`extractCommandAndOutput` is preserved but demoted.** The
  fallback path remains for session-restore-from-disk
  scenarios (where the linear stream wasn't captured at runtime);
  the runtime path uses the linear-text producer's high-water-
  mark. Two code paths instead of one — accepted for backwards
  compatibility with on-disk session files.

### Boundary enforcement

The boundary is enforced **structurally**, not just by
convention:

- **Substrate code (in `Terminal.Core`) may NOT import
  `Terminal.Accessibility`** or any UIA / WPF / OS-specific
  module. Code review blocks a substrate → channel reference.
- **Channel code may NOT import substrate-internal modules.**
  Channels consume the published `OutputEvent` /
  `SemanticEvent` / `ChannelDecision` types but do not reach
  into `LinearTextStream` internals, `VtParser` state, or the
  screen grid's cell array.
- **Channels may not produce new semantic events.** If a
  channel needs richer information than the substrate provides,
  the right fix is a new detector in the substrate, not a
  channel-side reclassification.

These rules are surfaced in CORE-ABSTRACTION-BOUNDARY.md §2-§3
and become standard review checks once the boundary doc lands.

## Alternatives considered

### Alternative A: Keep the screen grid as the primary substrate; fix bugs incrementally

**Why rejected**: Each fix would be a one-off heuristic patch
(spinner-glyph denylist, red-character-count threshold, prompt-
boundary heuristic refinement). The architectural mismatch
between linear bytes and 2D grid would persist; future
workloads (REPL inputs, form prompts, LLM tool-use) would
each hit fresh variants of the same class of bug. The
maintenance cost compounds.

### Alternative B: Maintain only the linear substrate; drop the screen grid entirely

**Why rejected**: Alt-screen TUIs (vim, htop, less) are real
workloads that need a 2D substrate. Dropping the screen grid
breaks them. The boundary doc's framing — "screen grid demoted
to alt-screen-only" — preserves the screen grid for the
workloads it was correct for, while letting linear workloads
use a substrate that fits their shape.

### Alternative C: Build the canonical-display catalog from a research deliverable before committing to the boundary

**Why rejected**: The maintainer's 2026-05-09 redirect was to
defer research and ship three exemplar canonical displays
(raw text / interactive list / form with text input) that
establish the abstraction. The boundary itself does not
require the full taxonomy — it requires a stable assertion of
"substrate produces canonical-display payloads, channels render
them". The three exemplars are sufficient to lock the
boundary; extension can wait for concrete demand.

## Status notes

- **2026-05-09**: Accepted. CORE-ABSTRACTION-BOUNDARY.md ships
  in the same Cycle 30 PR as this ADR.
- **Future supersession**: a new ADR may supersede this one if
  the four-part assertion's wording changes substantively. Re-
  snapshot conditions for CORE-ABSTRACTION-BOUNDARY.md (e.g.
  Cycle 34 ships LinearTextStream) are NOT supersession events;
  the assertion remains stable across them.
