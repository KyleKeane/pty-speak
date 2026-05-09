# Core abstraction boundary

> **Snapshot**: 2026-05-09
> **Status**: architectural framing — locks the substrate /
> channel dichotomy that all downstream stages build against.
> **Authoring item**: PROJECT-PLAN-2026-05-09 §A (architectural
> assertion).
> **ADR**: [`adr/0001-substrate-channel-dichotomy.md`](adr/0001-substrate-channel-dichotomy.md)
> records the dichotomy as a non-negotiable design constraint.
> **Companion docs**:
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational
>   mechanics of today's 12-stage pipeline; this doc names which
>   of those stages live below the boundary (substrate) vs. above
>   (channels).
> - [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) — channel-
>   based-communication principle; this doc renames the boundary
>   side as "substrate" and clarifies the inversion required to
>   support trickle-feed inputs.
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — Shell
>   Interaction Manager + three-component model; this doc
>   re-frames the components as sub-panes (§6).
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — history substrate;
>   the linear-text producer (§5) is the canonical replacement
>   for `extractCommandAndOutput`'s screen-row reconstruction.
> - [`PANE-MODEL.md`](PANE-MODEL.md) — workspace + multi-pane
>   framework; consumes §6's three-sub-pane decomposition + three
>   reserved peer panes.
> - [`PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-09.md) —
>   Section 1 (architectural assertion) is the source text this
>   doc lifts verbatim.

## What this document is

The **architectural boundary** that separates pty-speak's
**substrate** (the byte → semantic-event substrate produced by
the parser pipeline) from its **channels** (the canonical-display
primitives that consume substrate output and render it for the
user via NVDA / earcons / FileLogger / future surfaces).

The boundary is a **non-negotiable design constraint** going
forward. Every stage from the linear-text substrate inversion
(2026-05-09 plan Cycle 34) onward is designed against this
boundary: substrate code may not import channel concerns;
channel code may not depend on substrate-internal modules.

This doc is the canonical statement of what the boundary is, why
it exists, what each side may do, and how the two sides
communicate. It supersedes prior framings in
[`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) and
[`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) where those
framings are silent on the boundary.

## Why this exists

Through Stages 1-7, pty-speak grew a screen-grid substrate
(`Terminal.Core.Screen`) that accumulates parsed VT events into
a visual cell grid. Channels (NvdaChannel, FileLoggerChannel,
EarconChannel) consume `OutputEvent`s emitted by `StreamPathway`,
which derives those events by **diffing the screen grid row-by-
row between flushes** (`StreamPathway.fs:45-200`,
`Coalescer.fs:35-150`).

The screen-grid substrate is the right answer for **TUI**
workloads that draw alt-screen interfaces (vim, htop, less),
because alt-screen apps are inherently 2D — they paint cells at
arbitrary `(row, col)` positions and rely on the screen grid as
truth. But the screen-grid substrate is the **wrong** answer for
**linear / streaming text** workloads, which include:

- Plain-cmd / plain-PowerShell output (`dir`, `git status`).
- `claude --dangerously-skip-permissions` text and tool-use
  output (the dominant maintainer workload).
- Build / compile output (`dotnet build`, `npm install`).
- Any output that doesn't enter alt-screen.

For linear workloads, the screen grid forces the substrate to
**reconstruct** the natural byte sequence by walking cells and
joining rows — an inversion of cause and effect. The byte
sequence existed first; the screen grid was derived from it; but
the channels read the derived form back as if it were primary.
This shows up as bugs:

- **Spinner storms** (Cycle 29b confirmed: ~80 announces per
  Claude turn). Each spinner glyph rotation is a row-diff event
  fired through StreamPathway; identical-hash dedup catches
  repeats but Claude's incrementing token-counter defeats it.
- **Red-tone misfires** (Cycle 29b confirmed: ~30 error-tones
  per Claude turn). Spinner glyphs are red-coloured; the row-
  diff path can't tell "spinner frame" from "actual error line"
  because both arrive as `RowChanged` events.
- **`extractCommandAndOutput` fragility** (`SessionModel.fs:316-
  427`). The session-history substrate reconstructs (command,
  output) tuples by walking rows of the screen grid and
  splitting at prompt boundaries — duplicating the parsing work
  the screen grid already did and reintroducing all the row-
  wrap / line-continuation edge cases.

The fix requires **inverting the substrate**: the linear text
stream becomes the source of truth; the screen grid becomes one
of several derived projections (used only for alt-screen
content). This doc names the boundary that makes the inversion
sound.

## §1 — The four-part architectural assertion

This is the maintainer-blessed canonical assertion (PROJECT-PLAN-
2026-05-09 §1, lifted verbatim and recorded in ADR 0001):

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
>    OS-specific (UIA on Windows, AT-SPI on Linux, NSAccessibility
>    on macOS). The boundary between them is the portability
>    seam.

The four parts together yield: a portable, layered substrate
that produces canonical-display payloads, consumed by OS-specific
channel adapters.

## §2 — The substrate side

The substrate is everything **below the boundary** — the byte →
semantic-event flow.

### Substrate composition

- **Layer 0 (raw bytes)** — `ConPtyHost` reads PTY stdout into
  the Coalescer's input queue. No interpretation.
- **Layer 1 (parser)** — `Terminal.Core.VtParser` tokenizes
  bytes into VT events (Print, CSI, OSC, DCS, ESC, control).
  This is the **lexer** in NLP terms.
- **Layer 2 (linear-text producer + screen producer)** —
  Two parallel substrate projections:
  - `LinearTextStream` (Cycle 34, planned) — appends every
    Coalescer-batched byte (less alt-screen content) to a
    linear in-memory buffer with a live-region pointer for
    overwrite-in-place sequences. **This is the canonical
    substrate for linear workloads.**
  - `Terminal.Core.Screen` (shipping today) — the cell grid.
    **Demoted** to "alt-screen substrate only" once §6 inversion
    completes; remains the canonical substrate for vim / htop /
    less / any alt-screen TUI.
- **Layer 3 (detectors)** — derived semantic-event producers
  consume substrate output:
  - `HeuristicPromptDetector` (shipping) — prompt-boundary
    finalize.
  - `SelectionDetector` (Cycle 29a-c, shipping) — interactive-
    list candidate detection.
  - Future: `ErrorLineDetector`, `KeywordDetector`,
    `FormPromptDetector`, `ProgressDetector`.
- **Layer 4 (semantic-event store)** — `OutputEvent` /
  `SemanticEvent` records flowing through the pipeline. These
  are the **tokens with attached semantic categories** in NLP
  terms. **Profiles** (StreamProfile, SelectionProfile,
  EarconProfile, future FormProfile) consume these to make
  channel decisions.

### What substrate code may NOT do

- **No imports from the `Terminal.Accessibility` assembly.**
  Substrate code may not reference `TerminalAutomationPeer`,
  any UIA pattern provider, or the `System.Windows.Automation`
  / `System.Windows.Automation.Peers` namespaces.
- **No P/Invoke into Win32 UIA.**
  `UiaRaiseNotificationEvent` calls live in the channel layer.
- **No imports from `Terminal.App` (composition root).**
  Substrate is consumed by the app, never the reverse.
- **No WPF dependencies in the linear-text producer or
  detectors.** They operate on byte / event streams; rendering
  is the channel layer's job.

### What substrate code MAY do

- Produce `OutputEvent` / `SemanticEvent` records with
  structured payloads.
- Maintain in-memory data structures (linear-text buffer,
  screen grid, session history).
- Run on background threads (`Coalescer` already does;
  `LinearTextStream` will).
- Emit diagnostics through the `IDiagnosticSink` boundary.

### Today's substrate gaps

- The linear-text producer doesn't exist yet (Cycle 34 ships
  it).
- `extractCommandAndOutput` is screen-grid-derived and
  duplicates parser work; replaced by the linear-text producer's
  high-water-mark commit semantics.
- The screen-grid substrate is currently the canonical substrate
  for both alt-screen AND linear workloads; the inversion
  reduces it to alt-screen-only.

## §3 — The channel side

The channel layer is everything **above the boundary** — the
semantic-event → user-perceivable output flow.

### Channel composition

- **Profiles** (StreamProfile, SelectionProfile, EarconProfile,
  future FormProfile, future ErrorProfile) — pattern-match on
  `SemanticEvent.Category` + `Extensions`; emit `ChannelDecision`
  records targeted at specific channels.
- **Channels** (NvdaChannel, FileLoggerChannel, EarconChannel,
  future SapiChannel for cross-platform) — execute
  `ChannelDecision` payloads. NvdaChannel raises UIA
  notifications; FileLoggerChannel writes structured log lines;
  EarconChannel plays WASAPI audio.
- **OS adapters** — channels are OS-specific. The Windows
  build's NvdaChannel uses UIA; a future Linux build's
  AtspiChannel would use AT-SPI (the same profile output drives
  both).

### Canonical displays (the channel-layer vocabulary)

A **canonical display** is a presentation primitive with:

- A **UIA control type** (`Document`, `List`, `Group`, etc.).
- An **ARIA role analog** (`role="log"`, `role="listbox"`,
  `role="form"`).
- A **reading pattern** under each major screen reader (NVDA,
  JAWS, Narrator).
- A fixed **interaction contract** (read-only, arrow-navigable,
  Tab-between-fields, etc.).

Three exemplar canonical displays (§5 below) seed the catalog;
extension points (severity alert, indeterminate progress, status
indicator, tabular output, hierarchical tree) are named but not
specified in this cycle.

### What channel code may NOT do

- **No imports from substrate-internal modules.** Channel code
  consumes `OutputEvent` / `SemanticEvent` / `ChannelDecision` —
  the published types — but does not reach into
  `LinearTextStream` internals, `VtParser`'s state machine, or
  the screen grid's cell array.
- **No producing new semantic events.** Channels render; they
  do not classify. If a channel needs richer information than
  the substrate provides, the right fix is a new detector in
  the substrate, not a channel-side reclassification.
- **No mutable cross-channel state.** Two channels rendering
  the same `ChannelDecision` must produce the same output
  regardless of order.

### What channel code MAY do

- Call OS-specific accessibility APIs (UIA, AT-SPI,
  NSAccessibility).
- Render to WPF / Avalonia / GTK surfaces.
- Maintain channel-local state (NvdaChannel's per-ActivityId
  notification dedup is a current example).
- Coordinate with WPF dispatcher / UI thread.

## §4 — The NLP-style parser pipeline

The substrate is structured as an NLP-style layered analysis:

| NLP stage | Substrate stage | Today |
|---|---|---|
| Lexer (bytes → tokens) | `VtParser` (bytes → VT events) | ✅ shipping |
| Parser (tokens → trees) | `LinearTextStream` (events → linear text + live region) | 📋 Cycle 34 |
| | `Screen` (events → cell grid) | ✅ shipping (demoted to alt-screen only post-Cycle 35) |
| Tagger / classifier (trees → semantic categories) | `HeuristicPromptDetector`, `SelectionDetector`, future `ErrorLineDetector` / `FormPromptDetector` / `KeywordDetector` | partially shipping |
| Enricher (categories → structured records) | Profiles (StreamProfile, SelectionProfile, EarconProfile, future FormProfile) | partially shipping |

Each layer adds a derived view; **no layer erases prior layers**.
The bytes remain available; the linear text remains available;
the events remain available. Detectors that disagree (e.g.
`SelectionDetector` says "this is an interactive list" but a
hypothetical `FormPromptDetector` says "this is a form field")
both emit, and profile precedence (TOML-configurable per
[USER-SETTINGS.md](USER-SETTINGS.md) when that lands) decides
which channel decision wins.

This layering is what makes the substrate **portable**: a
Linux build re-uses the parser, the linear-text producer, the
detectors, and the profile decisions; only the channel layer
swaps OS adapters.

## §5 — Three exemplar canonical displays

The 2026-05-09 plan ships **three simple representative
canonical-display exemplars** that establish the abstraction
without over-specifying. The catalog can be extended later
(severity alert, indeterminate progress, status indicator,
tabular output, hierarchical tree) without rework once these
three are in place. Full interaction contracts + UIA mappings
are scoped in `CANONICAL-DISPLAY-CATALOG.md` (Cycle 33,
forthcoming).

### Exemplar 1 — Raw text

The bulk of cmd / PowerShell / Claude text output.

- **Substrate**: linear-text (the Stream pathway, post-Cycle
  35).
- **UIA**: `ControlType.Document` + `TextPattern` (already
  shipped at the screen substrate; rewires to linear substrate
  in Cycle 35).
- **ARIA analog**: `role="log"` aria-live=polite for streaming
  output; `role="document"` for sealed history tuples.
- **NVDA**: review-cursor navigable; live announces via
  `UiaRaiseNotificationEvent`; quick-nav `o`/`O` for next/prev
  output block (future history-sub-pane work).
- **Interaction**: read-only.
- **Ships in**: Cycle 35 (Stream profile rebuild).

### Exemplar 2 — Interactive list

Selection prompts: Claude tool-use, cmd `choice`, fzf-non-alt-
screen.

- **Substrate**: derived semantic-event store
  (SelectionDetector output → SelectionProfile decisions).
- **UIA**: `ControlType.List` + `ControlType.ListItem` with
  `ISelectionProvider` + `ISelectionItemProvider`. Already
  partially shipped via Cycles 29a/29b as text-only; promoted
  to full UIA listbox semantics in Cycle 37.
- **ARIA analog**: `role="listbox"` with `aria-activedescendant`.
- **NVDA**: announces "list with N items, item M of N"; arrow-
  key navigable; Enter invokes.
- **Interaction**: arrow keys move selection; Enter invokes;
  Esc dismisses.
- **Ships in**: Cycle 37 (Stage 8e-B UIA listbox peer).

### Exemplar 3 — Form with text input

Multi-field prompts: `Read-Host` chains, credentials prompt,
multi-step interactive command builders.

- **Substrate**: linear-text (Stream pathway) +
  `InputPathway` for keystroke routing.
- **UIA**: `ControlType.Group` containing one or more
  `ControlType.Edit` with `IValueProvider`.
- **ARIA analog**: `role="form"` with descendant
  `role="textbox"` fields and `aria-label`.
- **NVDA**: focus-mode automatically (forms-mode); arrow / Tab
  between fields; Enter submits.
- **Interaction**: Tab between fields; type to fill; Enter
  submits.
- **Ships in**: Cycle 38 (input framework foundation +
  FormProfile; Form is the most input-heavy canonical display
  and naturally pairs with the InputPathway substrate).

### Catalog extension points (named, not specified)

Future canonical displays the catalog may grow:

- **Severity alert** — error / warning lines. Likely UIA
  `Notification` event + earcon channel, ARIA `role="alert"`
  (assertive).
- **Status indicator** — health, mode, shell. Likely UIA
  `StatusBar`, ARIA `role="status"`.
- **Indeterminate progress** — spinners, "Thinking…". Live-
  region overwrite at substrate layer; suppressed at channel
  layer (no per-frame announces). ARIA `role="progressbar"`
  aria-valuenow=undefined.
- **Tabular output** — `dir /columns`, `Get-Process`
  output. UIA `Table` + `TableItem`; ARIA `role="table"`.
- **Hierarchical tree** — `tree /F` output. UIA `Tree` +
  `TreeItem`; ARIA `role="tree"`.

These extension points are mentioned to establish that the
catalog is not closed; the three exemplars are the working
seed, not the full vocabulary.

## §6 — Three-sub-pane interaction paradigm

The shell pane (today's `TerminalView`, owned by the Shell
Interaction Manager per [INTERACTION-MODEL §4](INTERACTION-MODEL.md))
is internally three **sub-panes**, each consuming a different
substrate slice:

### The three sub-panes

1. **Command-input sub-pane** — where the user types commands
   that get sent to the PTY child. Today: typing into the
   single `TerminalView` surface; future: a dedicated text-
   input region. Substrate: future `InputPathway` (Cycle 38)
   + `EchoCorrelator`. Visible representation of "what I am
   about to send".

2. **Current-output sub-pane** — where the active in-flight
   output is rendered using whatever canonical display the
   parser determines (raw text for plain output, interactive
   list for selection prompts, form for input fields, future
   primitives for additional cases). Substrate: linear-text
   producer (raw text) + detector output (interactive list /
   form). The current-output sub-pane is the place where the
   parser's classification decisions become user-perceivable
   form.

3. **History sub-pane** — sealed past `SessionTuple`s exposed
   as **CommandOutputTuple** canonical-display primitives
   (the unit of history navigation per
   [`docs/research/Output-paradigms.md` §1.6](research/Output-paradigms.md)).
   Each tuple wraps the submitted command, the output stream,
   and the exit code as a single semantically-navigable region.
   Concrete quick-nav contract:
   - **`h` / `Shift+h`** — next / previous command boundary
     (each command line is exposed as a level-2 heading via
     UIA `Document` + TextPattern attributes).
   - **`o` / `Shift+o`** — next / previous output block (the
     output region of the current tuple; embedded-object
     navigation in NVDA browse mode).
   - **`Alt+Up` / `Alt+Down`** — previous / next tuple boundary
     (parallel to VS Code's `editor.action.marker.next`).
   - **Jump-to-most-recent-output** — reserved hotkey TBD;
     resets focus to the most recent CommandOutputTuple's
     output region.

   Substrate: SessionModel + the linear-text producer's high-
   water-mark commits (post-Cycle 34, replaces
   `extractCommandAndOutput`'s screen-row reconstruction).
   The CommandOutputTuple primitive (UIA `ControlType.Group`
   wrapping a read-only `ControlType.Edit` for the command,
   a `ControlType.Document` for the output, and a
   `ControlType.Text` for the exit code with `ItemStatus`)
   is one of the canonical-display extension points named in
   §5; the catalog's full interaction contract for this
   primitive lands in Cycle 33 RFC.

### Why this decomposition

The three sub-panes crystallize a fixed interaction paradigm:
the user is always in one of three modes — **inputting** a
command, **interacting with** the current output, or
**exploring** history. The framework names that paradigm so
implementation cycles can build against it.

Today's `TerminalView` plays all three roles in a single
surface. The decomposition is **conceptual first** — the three
sub-panes have distinct substrates and accessibility surfaces
even though they share rendering today. A future Phase 2 layout
PR may surface them as distinct WPF regions; the conceptual
decomposition does not require it.

### Three additional reserved peer panes

Beyond the three sub-panes that decompose the shell pane, the
2026-05-09 redirect added **three new reserved peer panes** to
the workspace catalog. These are siblings to the shell pane,
captured in [`PANE-MODEL.md`](PANE-MODEL.md) as 📋 reserved
(documentation only; no design / spec / implementation in this
plan):

- **Notification queue pane** — ephemeral toast-style
  notifications surfaced from stream input (e.g., spinner-burst
  summary, build-completed, test-failure-detected). Clears on
  read or timeout. UIA `role="log"` aria-live=assertive analog.
- **Contextual keyword info pane** — displays contextual
  information about particular keywords surfaced in output
  (e.g., when output mentions a file path, error code, package
  name, or symbol, this pane offers documentation / definition /
  link). Driven by a future detector layer (`KeywordDetector`).
- **Input assistant pane** — gives contextual information
  about commands being typed in the command-input sub-pane
  (e.g., man-page snippet for `git push`, autocomplete
  suggestions, parameter help). Coordinated with the command-
  input sub-pane via the input framework substrate (Cycle 38).

These additions do not change the architectural boundary; they
extend the workspace with new channel-layer consumers of the
same substrate.

## §7 — Streaming-incomplete protocol

The linear-text substrate is **incomplete at every tick**. A
byte that arrived 5ms ago might be the start of a longer
sequence whose final shape is still in flight. The protocol
that lets profiles emit usefully against an incomplete stream:

- **Idle quanta as commit points.** Coalescer's debounce-window
  fire = high-water-mark commit; profiles emit suffix-since-
  last-commit.
- **Prompt boundary as finalize.** `HeuristicPromptDetector`
  fire = `SessionTuple.OutputText` sealed; immutable in History.
- **Mid-stream events carry `Sealed: bool` extension.** Unsealed
  events are advisory (profiles may show provisional state but
  must not commit irreversibly); sealed events are authoritative.
- **Live region.** A `(start, length)` pointer into the
  substrate tail. Bytes overwriting the live region (`\r`-
  without-`\n`, cursor-up-then-back, ESC[K) replace tail; do
  not append. Spinners + progress bars + `--More--` paginator
  prompts live here. Detection mechanism finalized in Cycle 33
  RFC.

This protocol is the contract that makes the linear-text
substrate usable by channels without erasing the streaming
nature of the underlying byte flow. Channels see a sequence of
sealed + unsealed events; they decide what to do with each.

## §8 — The portability invariant

The boundary doubles as the **portability seam**. A future
non-Windows build of pty-speak (Linux + AT-SPI; macOS +
NSAccessibility) re-uses everything below the boundary
unchanged:

- VtParser
- LinearTextStream (post-Cycle 34)
- Screen (for alt-screen)
- All detectors (PromptDetector, SelectionDetector, future
  ErrorLineDetector / FormPromptDetector / KeywordDetector)
- All profiles (StreamProfile, SelectionProfile, EarconProfile,
  future FormProfile)
- SessionModel + the linear-text high-water-mark replacement
  for `extractCommandAndOutput`

Only the channel-side OS adapters change:

- NvdaChannel → AtspiChannel / NSAccessibilityChannel
- WPF rendering → GTK / AppKit rendering
- WASAPI EarconChannel → ALSA / CoreAudio EarconChannel

The portability invariant is enforced **structurally**: substrate
projects (`Terminal.Core`) do not reference channel projects
(`Terminal.Accessibility`, the future `Terminal.Channels.AT-SPI`).
A code review that surfaces a substrate → channel reference is
a boundary violation; reviewers block.

## §9 — What this doc is NOT

- **Not a spec.** Concrete F# / C# types, module shapes,
  pattern provider implementations live in
  [spec/tech-plan.md](../spec/tech-plan.md) and the forthcoming
  [`CANONICAL-DISPLAY-CATALOG.md`](CANONICAL-DISPLAY-CATALOG.md)
  (Cycle 33).
- **Not an implementation plan.** Cycle-by-cycle rollout lives
  in [`PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-09.md).
- **Not a research deliverable.** The two Claude-research
  handoffs originally drafted (canonical-display taxonomy
  survey + streaming partial-emit prior-art survey) are
  deferred; the three exemplar canonical displays in §5
  establish the abstraction without requiring full taxonomy
  scoping. Research can be commissioned later if extension
  demand arises (per PROJECT-PLAN-2026-05-09 §6).

## §10 — Cross-references

- **ADR**: [`adr/0001-substrate-channel-dichotomy.md`](adr/0001-substrate-channel-dichotomy.md)
- **Strategic plan**: [`PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-09.md)
- **Channel principle (older framing)**:
  [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md)
- **Pipeline mechanics**: [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md)
- **Interaction model**: [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md)
- **Session history**: [`SESSION-MODEL.md`](SESSION-MODEL.md)
- **Pane catalog + sub-pane decomposition**:
  [`PANE-MODEL.md`](PANE-MODEL.md)
- **Spec authority**: [`spec/tech-plan.md`](../spec/tech-plan.md)

## Versioning + maintenance

Follows the snapshot model established by the other research-
stage docs. Top-of-doc front matter carries
`Snapshot: YYYY-MM-DD`. Trigger conditions for re-snapshot:

- Cycle 33 RFC ships the `CANONICAL-DISPLAY-CATALOG.md` —
  cross-reference here.
- Cycle 34 ships `LinearTextStream` — substrate description
  in §2 transitions from "📋" to "✅".
- Cycle 35 demotes the screen-grid substrate to alt-screen-
  only — §2 wording shifts.
- Cycle 38 ships FormProfile + InputPathway — §5 Exemplar 3
  status transitions.
- A new exemplar is promoted from "extension point" to
  "shipping" — §5 grows.

The boundary itself (§1's four-part assertion) is intended to be
**stable across implementation cycles**. If the boundary changes,
that's an ADR-level event (a new ADR superseding 0001), not a
re-snapshot.
