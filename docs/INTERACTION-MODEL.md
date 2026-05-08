# Interaction Model

> **Snapshot**: 2026-05-07
> **Status**: design / forward-looking — names a conceptual abstraction and a target architecture; some named pieces ship today, most are reserved for future work
> **Authoring item**: backlog item 29 (research stage)
> **Companion docs**:
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational vocabulary: 12-stage byte-to-announcement flow, event taxonomy, substrate inventory. This doc references it for stage / pathway / event-type names.
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — substrate design for structured (prompt, command, output, exit-code) history. This doc places SessionModel inside the higher-level architectural framing.
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas. Knobs cited in this doc cross-reference there.
> - [`ACCESSIBILITY-INTERACTION-MODEL.md`](ACCESSIBILITY-INTERACTION-MODEL.md) — the screen-reader-interaction layer (caret tension, modal dialogs, NVDA round-trips). Sister doc; INTERACTION-MODEL is one layer above (architectural framing); A-I-M is one layer below (per-screen-reader behaviour).
> - [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition layer (multi-pane workspace framework). The Shell Interaction Manager owns the shell pane; PANE-MODEL extends into multi-pane composition (file tree / cherry-picked I/O / language docs / AI assistance).
> - [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) — canonical spec. This doc proposes a higher-level framing the spec will absorb.
> - [`spec/overview.md`](../spec/overview.md) — references OSC 133 + the five-stage architecture this doc anchors against.
> - [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) — channel-based-communication principle. The Shell Interaction Manager's inter-thread boundaries map to the channels documented there.

## What this document is

A description of pty-speak's **higher-level architectural
framing** — the mental model that ties together the
operational pipeline, the structured-history substrate, the
input pathway (future), and the screen-reader-interface
contract.

Concretely, this doc:

1. Names the **Shell Interaction Manager** (SIM) — the
   conceptual abstraction that owns the bidirectional
   conversation between pty-speak and the spawned shell
   program.
2. Defines a **three-component model** — Input Composition
   Surface, Active Output, Historical Document — for how
   the SIM organises shell interaction internally.
3. Frames pty-speak's data shape as a **structured
   computational document** — the navigable artefact a user
   reasons over.
4. Specifies an **interactive element taxonomy** — what
   each placeholder in `SemanticCategory` is for, what
   triggers it, and what consumers do with it.
5. Cross-references the existing operational substrates
   (PIPELINE-NARRATIVE) and history substrates
   (SESSION-MODEL) so a reader knows which lens to pick
   for a given question.

The doc is **descriptive** for the parts of the model that
ship today (StreamPathway emit chain; mode-barrier handling;
empty-payload semantic events). It is **forward-looking** for
the parts the model RESERVES (Input Composition Surface as a
distinct module; SessionModel-driven Active-Output / History
boundary; FormPathway for interactive elements). The
forward-looking pieces are tagged explicitly so a reader can
distinguish "this is real" from "this is design intent".

## Why this exists

Through PRs #161 → #172, pty-speak has accumulated
substantive operational machinery (the 12-stage pipeline,
display pathways, colour-detection, suffix-diff, parameter
atlas) and substantive design substrate (PIPELINE-NARRATIVE,
SESSION-MODEL, USER-SETTINGS, AUDIT-CODE-CONSISTENCY). What
was missing — surfaced by the maintainer's reflection
2026-05-06 — is the **higher-level architectural framing**
that lets a contributor answer "what IS pty-speak, in one
sentence?" without descending into per-pipeline-stage
mechanics.

The maintainer's framing, in their own words:

> "Trying to understand the correct architecture of the
> interaction that we are managing here … I think we need
> to call this component of the system something specific
> so that I can reference it, how about the **shell
> interaction manager** for the component that takes care of
> sending user input for the result of user interactions to
> the shell program and handles the interpretation of the
> response from the shell program that is then semantically
> marked up before being added into the **structured
> computational document** that will allow for quick and
> effective navigation through the history as well as the
> presentation of canonical interaction models for different
> types of user input."

Everything PIPELINE-NARRATIVE and SESSION-MODEL describe is
in service of that higher-level framing. Without writing the
framing down, future contributors (and Claude sessions) have
to reconstruct it from per-stage mechanics every time they
add a feature. Surface it once; reference it forever.

## How this differs from the companion docs

| Lens | Doc | Question answered |
|---|---|---|
| Architectural framing — what IS pty-speak? | **INTERACTION-MODEL.md** (this doc) | "Where does this feature go in the higher-level model?" |
| Operational mechanics — how does data flow? | PIPELINE-NARRATIVE.md | "Which stage produces / consumes this data?" |
| History substrate — how is interaction recorded? | SESSION-MODEL.md | "How are commands + outputs stored + queried?" |
| Parameter atlas — which knobs exist? | USER-SETTINGS.md | "What's tunable in stage / pathway X?" |
| Screen-reader interaction — caret + UIA | ACCESSIBILITY-INTERACTION-MODEL.md | "How does NVDA navigate this surface?" |
| UI composition — multi-pane workspace | PANE-MODEL.md | "Where does a new pane (file tree / AI assistance / etc.) go?" |
| Canonical spec | spec/event-and-output-framework.md | "What did the design commit to?" |

Reader picks lens by question. Per-PR referencing names a
specific section, e.g.
"per INTERACTION-MODEL §5.b (Active Output)" or
"per PIPELINE-NARRATIVE Stage 7 (row-level diff)".

## Audience

Three intended readers, in order of likely first use:

1. **The maintainer**, when reasoning about whether a
   proposed feature fits within the SIM's responsibilities,
   needs new substrate, or belongs to a sister system (UI,
   logging, packaging). The doc is a "where does this live?"
   sieve.
2. **Future Claude sessions**, when adding behaviour. The
   standard question chain becomes: "is this Input
   Composition Surface, Active Output, or Historical
   Document? which interactive element category? which
   pipeline stages does it touch?"
3. **Future contributors**, when navigating the codebase.
   Goes alongside `ARCHITECTURE.md` (module map) and
   `PIPELINE-NARRATIVE.md` (flow map) as a higher-level
   framing.

The interaction model is **NOT** a user-facing guide.
End-user documentation lives in `README.md`, `INSTALL.md`,
and future settings guides.

## Reading order

For a complete pass:

1. **The shell interaction reality** — what's actually
   exchanged between pty-speak and a shell program at the
   byte level. Honest about how messy this is.
2. **The Shell Interaction Manager** — names the
   abstraction, lists its responsibilities, maps to
   existing + future modules.
3. **The three-component model** — Input Composition
   Surface / Active Output / Historical Document.
   Includes the canonical bidirectional-flow diagram.
4. **Structured computational document framing** — the
   Jupyter / Wolfram notebook analogy and where it does +
   doesn't transfer.
5. **Interactive element taxonomy** — every
   `SemanticCategory` placeholder gets a row with trigger,
   producer, consumer, status.
6. **How this composes with existing substrates** — the
   cross-reference matrix.
7. **Substrate gaps** — what the model needs that doesn't
   exist yet (cross-references to backlog items).
8. **Versioning + maintenance** — snapshot model; how this
   doc stays honest as code evolves.
9. **Open questions** — design forks awaiting maintainer
   input.

## Status of this document

The interaction model is a **snapshot** — it describes the
architecture as-of the snapshot date. The codebase will
evolve; this doc gets re-snapshot-ed periodically (probably
once per major substrate phase). The snapshot model is
explicit: drift is acceptable; aim for correctness AT the
snapshot date; refresh when drift becomes load-bearing.

When the codebase substantively shifts (new pathway lands,
SessionModel implementation begins, input framework cycle
ships), this doc gets a new snapshot date + a "What
changed since last snapshot" section at the top.

## The shell interaction reality

Before naming abstractions, this section grounds the model
in what's actually exchanged between pty-speak and a shell
program. The byte stream is the substrate; everything
higher is interpretation.

### What flows in each direction

```
                ┌─────────────┐
                │  pty-speak  │
                │  (host)     │
                └─────┬───────┘
                      │
        stdin bytes   │   stdout + stderr bytes
        (input)       │   (output)
                      ↓
                ┌─────────────┐
                │  ConPTY     │
                │  driver     │
                └─────┬───────┘
                      │
                      ↓
                ┌─────────────┐
                │  shell      │
                │  process    │
                │  (cmd /     │
                │   pwsh /    │
                │   claude)   │
                └─────────────┘
```

Two separate byte streams flow simultaneously:

- **Input bytes (host → shell, via stdin)**: keystrokes
  encoded by [`KeyEncoding`](../src/Terminal.Core/KeyEncoding.fs)
  into VT-encoded sequences (e.g. Up arrow → `ESC [ A`),
  written via
  [`ConPtyHost.WriteBytes`](../src/Terminal.Pty/ConPtyHost.fs).
  Paste content also flows here, optionally wrapped in
  bracketed-paste markers.
- **Output bytes (shell → host, via stdout)**: shell
  output, including BOTH the user's typed input echoed
  back by the shell's own line editor AND the shell's
  responses (command output, prompts, etc.). Read by
  [`ConPtyHost`](../src/Terminal.Pty/ConPtyHost.fs)'s
  reader thread; parsed by
  [`VtParser`](../src/Terminal.Parser/Parser.fs).

The two streams are **asynchronous and uncorrelated at the
byte level**. pty-speak sends a byte; the shell may echo it
back immediately, or after processing, or never (e.g. if the
shell has line-editor interception of certain keystrokes
like `Ctrl+C`).

### What the bytes carry

Output bytes are not just printable text. They carry:

- **Printable text** (UTF-8 bytes ≥ 0x20, < 0x7F or > 0x7F).
- **Control characters** (BEL 0x07, BS 0x08, LF 0x0A,
  CR 0x0D, ESC 0x1B, etc.). Each is a command to the
  terminal.
- **CSI sequences** (`ESC [ ...`): cursor positioning,
  erasure, scroll regions, mode toggles. The shell uses
  these to repaint the screen.
- **SGR sequences** (a CSI subgroup, `ESC [ 0m`,
  `ESC [ 31m`): foreground / background colours, bold,
  underline, etc.
- **OSC sequences** (`ESC ] ...`): advanced commands.
  Notable for pty-speak: OSC 8 (hyperlinks), OSC 52
  (clipboard), and OSC 133 (semantic prompt boundaries —
  central to the future SessionModel substrate).
- **DCS / SOS / PM / APC**: rarely-used control strings.
  pty-speak parses but mostly ignores.

[`VtParser`](../src/Terminal.Parser/Parser.fs) decodes
these byte categories into structured `ParserEvent` values;
[`Screen`](../src/Terminal.Core/Screen.fs) consumes them and
mutates the cell grid; downstream stages observe the cell
grid via `ScreenNotification`.

Input bytes are simpler — pty-speak's
[`KeyEncoding`](../src/Terminal.Core/KeyEncoding.fs) emits
VT-encoded byte sequences derived from the user's keystrokes
(navigation keys → CSI; printable keys → UTF-8 of the
character; control keys → control bytes). The shell's own
line-editor decides what to do with them.

### What gets lost in translation

The maintainer's framing of "send command, get result" is
**not literally true at the byte level**. What's literally
true:

1. **No "command" abstraction**: pty-speak sees individual
   keystrokes streaming out, not a command. The shell's
   line editor accumulates them and decides when to "submit
   the command" (typically on `Enter`).
2. **No "result" abstraction**: pty-speak sees screen
   mutations, not "the command's output". The shell paints
   output on top of the cell grid; pty-speak sees only the
   grid changes.
3. **Echo is implicit**: when the user types `e`, the shell
   may echo `e` back through stdout. From pty-speak's
   perspective, this looks identical to the shell printing
   `e` of its own accord. Without OSC 133 (or another
   protocol layer), pty-speak cannot distinguish "the user
   typed e" from "the shell wrote e as part of output".

This is the gap the SessionModel substrate (item 28) and
the Phase 2 input framework cycle close. Today, pty-speak
operates in the "byte stream + screen grid" reality; future
work elevates that to "structured input + structured
output + correlated history".

### Why this matters for the higher-level framing

Naming a "Shell Interaction Manager" without grounding it
in the byte-stream reality produces an abstraction that
papers over the actual complexity. The SIM's responsibilities
include MANAGING the gap between byte-stream reality and
structured-interaction reality — recovering structure where
the substrate doesn't provide it (heuristics for
prompt-boundary detection in cmd/PowerShell), surfacing
structure where the substrate does provide it (OSC 133 in
configured shells), and degrading gracefully when neither
applies (raw pass-through to display pathway).

## The Shell Interaction Manager (SIM)

### Definition

**Shell Interaction Manager (SIM)** is the conceptual
abstraction that owns the bidirectional shell-program
conversation. It is **not** (today, and probably not ever)
a single F# module — it is a **coordinated set of modules**
working together, each responsible for one slice of the
conversation.

The SIM is the layer-name for "everything between the user's
keyboard / paste / hotkey input and the screen-reader / log /
earcon output that ISN'T just the byte plumbing or the
display layer". When a contributor asks "where does this
behaviour go?", the SIM is the third option after "UI layer"
and "Display layer".

### Responsibilities

The SIM owns five responsibilities:

1. **Input transmission**: convert user input intentions
   (keystrokes, paste, hotkeys, future selection-prompt
   responses, future autocomplete confirmations) into byte
   streams sent to PTY stdin.
2. **Output interpretation**: consume PTY stdout bytes;
   parse them via VtParser; route screen mutations to the
   pathway pump; ALSO route semantic markup (OSC 133
   boundaries, future OSC 8 hyperlinks, future selection
   detection) to the structured-document layer.
3. **Bidirectional correlation**: track which output bytes
   are echoes of input vs. genuine shell-side responses.
   Critical for echo-suppression at the announce layer
   (Phase 2 input framework cycle).
4. **Semantic event production**: emit
   `SelectionShown` / `PromptDetected` / `CommandSubmitted` /
   `CommandFinished` / `ErrorLine` / etc. when the
   appropriate triggers fire. Each triggered event is a
   piece of architectural framing the user can interact
   with via screen reader / earcon / log.
5. **Document model maintenance**: feed structured units
   (input lines, output blocks, interactive elements) to
   the structured-computational-document layer
   (SessionModel + future).

### Mapping to existing + future modules

The SIM's responsibilities map to a coordinated set of
modules. Today's modules:

| SIM responsibility | Today's module(s) | File:line |
|---|---|---|
| Input transmission — encoding | [`KeyEncoding`](../src/Terminal.Core/KeyEncoding.fs) | per-key encode functions |
| Input transmission — byte write | [`ConPtyHost.WriteBytes`](../src/Terminal.Pty/ConPtyHost.fs) | byte plumbing |
| Output interpretation — byte read | [`ConPtyHost`](../src/Terminal.Pty/ConPtyHost.fs) reader thread | byte plumbing |
| Output interpretation — VT parse | [`VtParser`](../src/Terminal.Parser/Parser.fs) | parser state machine |
| Output interpretation — grid mutation | [`Screen`](../src/Terminal.Core/Screen.fs) | cell-grid maintenance |
| Output interpretation — notification fan-out | [`Screen.notify`](../src/Terminal.Core/Screen.fs) | `ScreenNotification` events |
| Document model — canonical state | [`CanonicalState`](../src/Terminal.Core/CanonicalState.fs) | snapshot + diff |
| Document model — display pathway | [`StreamPathway`](../src/Terminal.Core/StreamPathway.fs) / [`TuiPathway`](../src/Terminal.Core/TuiPathway.fs) | per-shell pathways |
| Semantic event production — color earcon | StreamPathway colour-detection | red/yellow row check |
| Semantic event production — bell | [`OutputEventBuilder`](../src/Terminal.Core/OutputEventBuilder.fs) | BEL handling |

Future modules (as research-stage backlog items):

| SIM responsibility | Future module(s) | Backlog item |
|---|---|---|
| Bidirectional correlation — echo tracking | EchoCorrelation substrate | Phase 2 input framework |
| Input transmission — structured input pathway | InputPathway protocol | Phase 2 input framework |
| Document model — session structure | [SessionModel](SESSION-MODEL.md) | item 28 |
| Document model — replay / postmortem | snapshot serialisation | item 2 / item 4 |
| Semantic event production — prompt boundary | OSC 133 producer | SessionModel substrate |
| Semantic event production — selection | FormPathway | Phase 2 / 3 |
| Semantic event production — Claude Code structure | ClaudeCodePathway | Phase 2 / 3 |

### Why "manager" and not "handler" / "controller"

The maintainer's word choice was deliberate. The SIM
**manages** the conversation in the sense of:

- **Reconciling competing concerns** — input bytes need to
  reach the shell quickly; output bytes need to be
  interpreted carefully. The SIM brokers between
  immediacy (input) and structure (output).
- **Maintaining state across turns** — the conversation has
  a history; the SIM owns that history's structure.
- **Mediating between participants** — the user, the
  shell, the screen reader, the log, the earcon channel
  are all separate participants with different needs. The
  SIM mediates.
- **Owning the conversation contract** — what does
  "submit a command" mean? what does "switch shells" mean?
  what does "interrupt" mean? The SIM defines + enforces
  these contracts.

"Handler" implies single-event reactive logic; "controller"
implies UI-pattern MVC. "Manager" captures the conversational,
multi-turn, multi-participant character of what the
abstraction owns.

### Why a coordinated set, not a single F# module

The SIM is conceptual because making it a single F# module
would conflict with three architectural realities:

1. **Different threading models** per slice. ConPtyHost's
   reader thread is a dedicated I/O thread; VtParser runs
   on it inline. The pathway pump runs on a different
   thread per architectural decision. UIA notification
   dispatch must marshal to the WPF dispatcher thread.
   A single SIM module would either force everything onto
   one thread (a bottleneck) or thread-scatter (defeating
   the abstraction).
2. **Different lifetime models** per slice. ConPtyHost is
   per-shell-spawn; pathway state is per-pathway-instance;
   SessionModel will be per-session (across shell switches);
   echo correlation will be per-active-input. A single
   module can't own multiple lifetimes cleanly.
3. **Different testability concerns** per slice. KeyEncoding
   is unit-testable in isolation (input → bytes function);
   pathway logic is testable via canonical-state snapshots;
   echo correlation will require time-traversal tests. A
   single SIM module would mix unrelated test concerns.

The coordinated-set framing lets each slice pick its own
threading, lifetime, and test approach while still being
referenceable as "part of the SIM" architecturally.

### When to introduce a literal `SimCoordinator` module

Plausible scenarios where a literal module emerges:

- **Cross-slice state machine**: if state transitions
  ("active prompt" → "command running" → "command finished"
  → "next prompt") need to be visible to multiple slices,
  a coordinator emits the transitions and slices subscribe.
  Likely after SessionModel implementation lands.
- **Cross-slice cancellation / kill-switch**: a user-press
  interrupt that needs to STOP both input transmission
  AND output interpretation AND the announce queue
  simultaneously. Phase B's kill-switch substrate is the
  first candidate.
- **Cross-slice diagnostics**: the Ctrl+Shift+D battery
  could grow to want a "snapshot the SIM's full state"
  capability. A coordinator would centralise that.

For now: keep SIM conceptual. Re-evaluate when the
cross-slice pressure surfaces.

## The three-component model

The SIM organises shell interaction internally into three
named components. Each component answers a different
question about "what is the user interacting with?", and
each component has different temporal characteristics
(when it lives; when it transitions; when it is read
vs. written).

### 5.a Input Composition Surface

**The "persistent text field" where the user composes
commands.**

#### What it is

The conceptual surface where the user's typed bytes
accumulate before being submitted to the shell as a
command. Today this is **handled implicitly** by the
shell's own line editor (cmd's REPL, PowerShell's
PSReadLine, Claude's prompt). pty-speak today has
**no explicit Input Composition Surface module**; it
sees the user's keystrokes as opaque bytes destined for
PTY stdin and the shell's echo of those bytes as opaque
output.

#### What it tracks (future, after Phase 2 input framework)

A future `InputBuffer` module owns this surface, tracking:

- **Bytes typed since prompt-start**: the current command
  text being composed.
- **Cursor position within that text**: where the next
  byte will be inserted (matches the shell's line-editor
  cursor position).
- **Editing history within the active line**: undo /
  redo stack within the line, before submission.
- **Shell-line-editor state**: continuation indicator
  (heredoc / multi-line `\` continuation), search-mode
  active (Ctrl+R history search), tab-completion popup
  visible.
- **Echo-correlation state**: which screen bytes are
  echoes of input (to be suppressed at the announce
  layer) vs. genuine shell output (to be announced).

#### When it transitions

- **Prompt appears (OSC 133 A)**: surface becomes active;
  buffer cleared.
- **User types**: bytes accumulate; cursor advances.
- **User edits** (Backspace, Delete, arrow keys, Home /
  End, Ctrl+W word-delete, etc.): bytes / cursor mutate
  per the shell's line-editor rules.
- **User submits (Enter)**: surface transitions to
  inactive; the SessionModel's Active Output begins.
  The command text is captured in the SessionModel
  tuple's `CommandText` field.
- **User cancels (Ctrl+C)**: buffer cleared without
  submission; new prompt appears.

#### Today's reality

Today, none of this is captured at the pty-speak layer.
The shell's line editor handles editing; pty-speak just
relays keystrokes and watches the screen change. NVDA's
own keyboard echo is what announces typed characters to
the user. The Input Composition Surface as a
**named pty-speak component** is RESERVED for the Phase 2
input framework cycle.

#### Why naming it now matters

Surfacing the gap explicitly informs every UX decision
the maintainer faces today:

- **"Why does my arrow-key produce no announcement?"** —
  because arrow keys don't change row content, only the
  shell-line-editor's cursor position, and pty-speak has
  no module that observes that position.
- **"Why does typing a character announce twice?"** —
  because both NVDA's keyboard echo (input-side) and
  pty-speak's StreamPathway (output-side, via shell echo)
  produce announcements; with no Input Composition
  Surface in pty-speak, there's nothing to coordinate
  echo-suppression.
- **"Why does backspace need a parameter?"** — because
  pty-speak observes only screen-text shrinkage, not the
  shell-line-editor's cursor-back behaviour; the parameter
  is a substitute for the structural awareness an Input
  Composition Surface would provide.

When the Phase 2 input framework cycle ships, several
parameters in [`USER-SETTINGS.md`](USER-SETTINGS.md) become
unnecessary because the structural answer subsumes the
parameterised heuristic.

### 5.b Active Output

**The "current output" zone — what the most recent command
is producing right now.**

#### What it is

The conceptual zone where the shell's response to the most
recent command appears. Today this is the **30×120 cell
grid** below the most recent prompt; pty-speak observes its
mutations via `ScreenNotification`, diffs them via
`CanonicalState`, and emits them via `StreamPathway`.

The Active Output is **distinct from the Historical
Document** in three ways:

1. **Mutability**: the Active Output changes as the
   shell streams bytes (output appearing line-by-line);
   the Historical Document is read-only.
2. **Interactivity**: the user may need to respond to
   interactive elements within the Active Output (a
   selection prompt, a confirm dialog, a multi-line edit);
   the Historical Document is purely navigable.
3. **Lifetime**: the Active Output begins at
   command-submission and ends at command-completion,
   typically signalled by OSC 133 D (or the heuristic
   "next prompt appears" fallback). Once it ends, its
   content moves into the Historical Document and the
   next command's Active Output begins.

#### What it tracks (today + future)

Today (StreamPathway): the live cell grid, per-row
content hashes, last-emitted row text (for suffix-diff),
cumulative pending diff (during debounce window), color
detection state.

Future (post-SessionModel):

- The **active SessionTuple** (post-CommandStart,
  pre-CommandFinished) — the structured representation
  of "what command the user submitted; what bytes have
  arrived in response so far; whether it has finished".
- **Interactive elements within the output**: detected
  via heuristics (FormPathway will cover gum / fzf /
  similar) or via shell-emitted markup (future). Each
  detected element exposes a contract: "what choice is
  highlighted; what choices exist; how does the user
  respond".
- **Streaming semantic events as they arrive**: ErrorLine,
  WarningLine, BellRang, future PromptDetected and
  HyperlinkOpened. Each event annotates the Active
  Output with structure.
- **Echo-correlated bytes**: which output bytes are
  echoes of what the user typed (to be suppressed at the
  announce layer); this is the Active Output's view on
  the bidirectional correlation responsibility of the
  SIM.

#### When it transitions

- **Command-submitted (OSC 133 B / heuristic prompt-line
  changes)**: Active Output begins. New empty zone.
- **Output bytes arrive**: Active Output accumulates.
  StreamPathway emits as content streams in.
- **Interactive element appears (FormPathway detect)**:
  Active Output's interactivity model changes; the user's
  next input may resolve the element rather than be
  passed through to the shell.
- **Command-finished (OSC 133 D / heuristic next-prompt
  detection)**: Active Output ends; its content (now
  immutable) moves to the Historical Document; next
  command's Active Output begins.

#### Today's reality vs. future ideal

Today: the cell grid IS the Active Output; pty-speak
emits as content streams in but has no concept of
"command-finished". The Historical Document doesn't
exist as a structured artefact (only as scrollback in
the cell grid + the FileLogger log file).

Future (post-SessionModel + post-Phase-2): the Active
Output is a structured zone with explicit start /
streaming / end transitions, interactive-element
detection, and a clean handoff to the Historical
Document.

### 5.c Historical Document

**The "navigable past" — completed commands + their
outputs, queryable.**

#### What it is

The structured, navigable representation of completed
shell interactions. Today this is **NOT a structured
artefact** — it's the FileLogger log file (post-hoc;
not navigable mid-session) plus whatever cell-grid
scrollback the user can reach. Future
[SessionModel](SESSION-MODEL.md) is the substrate that
makes it structured.

#### What it tracks (future)

Per [SESSION-MODEL.md §4](SESSION-MODEL.md):

- **`SessionTuple` array** — bounded ring buffer of
  completed (prompt, command, output, exit-code,
  timestamps) tuples.
- **Active tuple reference** — a pointer to the tuple
  currently being filled (which is conceptually the
  Active Output's structured form).
- **Per-tuple metadata**: shell ID at command time,
  start / end timestamps, exit code, semantic events
  that fired during the command (e.g. "this tuple had
  3 ErrorLine events"), interactive-element resolutions
  (e.g. "this tuple's selection prompt was answered
  with option B").
- **Cross-tuple aggregation** (queries): "the last
  command", "the last failed command", "all commands
  matching pattern X", etc.

#### When it grows

- **Command-finished**: Active Output's content moves
  into the Historical Document as a new immutable
  tuple. Active Output zone is reused for the next
  command.
- **Shell switch (Ctrl+Shift+1/2/3)**: depending on
  config, the Historical Document either persists
  per-shell (each shell has its own history) or
  unifies across shells. SESSION-MODEL.md §X frames
  this question.
- **Eviction**: when the ring buffer is full, oldest
  tuples drop. SESSION-MODEL.md frames the size
  parameter.

#### How it's queried (future)

Per [SESSION-MODEL.md §6](SESSION-MODEL.md):

- "Get the last N tuples" — for review-mode hotkey
  workflows.
- "Get the active tuple" — for echo correlation, for
  current-command earcon decisions.
- "Filter by exit-code" — for "read me only the failed
  commands".
- "Filter by semantic-event presence" — for "find the
  command that produced an ErrorLine".
- "Get tuple at position N" — for navigation hotkeys.

#### Today's reality

Today, "look at what just happened" is one of:

- The cell grid (volatile; subject to scroll, redraw,
  alt-screen toggle).
- The FileLogger log file (post-hoc; opens in Notepad
  via Ctrl+Shift+L).
- Manual scrollback within the spawned shell (PageUp /
  PageDown, but pty-speak's WPF view doesn't yet
  surface a scrollback model).

None of these are structured for navigation. The
Historical Document is the gap that
[SESSION-MODEL.md](SESSION-MODEL.md) closes.

### 5.d The bidirectional flow diagram

The canonical diagram for the three-component model. Each
arrow is a real or planned flow; bracketed seams ([ ]) are
correlation / transition points where structure is enforced.

```
       ┌──────────────────────────────────────────────────┐
       │           Shell Interaction Manager (SIM)        │
       │                                                  │
       │  ┌────────────────────────────────────────────┐  │
keyboard│  │   5.a Input Composition Surface           │  │
paste ──┼─▶│   (InputBuffer; Phase 2 future)           │──┼─▶ stdin
hotkeys │  │                                            │  │   bytes
       │  │   • current command text                    │  │
       │  │   • cursor position                         │  │
       │  │   • line-editor state                       │  │
       │  └────────────────┬───────────────────────────┘  │
       │                   │                              │
       │            [ echo correlation seam ]             │
       │            (Phase 2 future)                      │
       │                   │                              │
       │  ┌────────────────▼───────────────────────────┐  │
stdout ┼─▶│   5.b Active Output                        │  │
bytes  │  │   (cell grid today; SessionTuple future)   │  │
       │  │                                            │  │
       │  │   • streaming content                       │  │
       │  │   • interactive elements                    │  │
       │  │   • semantic events as they fire            │  │
       │  └────────────────┬───────────────────────────┘  │
       │                   │                              │
       │            [ command-finished seam ]             │
       │            (OSC 133 D / heuristic;               │
       │             SessionModel future)                 │
       │                   │                              │
       │  ┌────────────────▼───────────────────────────┐  │
       │  │   5.c Historical Document                  │  │
       │  │   (SessionModel.History; future)           │  │
       │  │                                            │  │
       │  │   • bounded ring buffer of tuples           │  │
       │  │   • queryable + navigable                   │  │
       │  └─────────────────────────────────────────────┘  │
       │                                                  │
       └──────────────┬───────────────────────────────────┘
                      │
                      ▼
              to display layer
              (DisplayPathway → Profile → Channel →
               NVDA / Earcons / Log per
               PIPELINE-NARRATIVE Stages 7-12)
```

### Reading the diagram

- **Top-to-bottom**: lifecycle of one command. User input
  composes (5.a); user submits; output streams (5.b);
  command finishes; output moves to history (5.c).
- **Left arrows**: input direction (keyboard / paste /
  hotkeys → stdin bytes).
- **Right arrows**: output direction (stdout bytes →
  Active Output → display layer).
- **Bracketed seams**: correlation points. Where the SIM
  enforces structure that the byte stream doesn't
  inherently provide. Both seams are PHASE 2 / FUTURE
  work.
- **Display layer**: not part of the SIM; the SIM passes
  structured events to the display layer per
  PIPELINE-NARRATIVE Stages 7-12.

### Where today's code sits in the diagram

- **5.a Input Composition Surface**: NOT IMPLEMENTED.
  Keyboard / paste / hotkeys flow directly through
  `KeyEncoding.encode` → `ConPtyHost.WriteBytes` with no
  intermediate buffering or structure.
- **5.b Active Output**: PARTIALLY IMPLEMENTED. The
  cell grid + StreamPathway emit chain is the present-
  day Active Output. Missing: SessionTuple structure,
  interactive-element detection, command-finished
  awareness.
- **5.c Historical Document**: NOT IMPLEMENTED as a
  structured artefact. The FileLogger log file is the
  closest thing. SessionModel substrate
  ([SESSION-MODEL.md](SESSION-MODEL.md)) is the gap.
- **Echo correlation seam**: NOT IMPLEMENTED. Phase 2
  input framework cycle.
- **Command-finished seam**: PARTIALLY IMPLEMENTED via
  the mode-barrier (alt-screen toggle, shell switch);
  the SessionModel-aware version requires OSC 133 + the
  heuristic fallback per SESSION-MODEL.md.

### What this model unifies

Naming the three components + two seams pulls together
several previously-loose concepts:

- **Per-row suffix-diff** (StreamPathway today) is
  partial echo-correlation; it heuristically suppresses
  re-announcing the prompt prefix because it correlates
  the current row text with the last-emitted row text.
  Echo correlation makes this explicit (input
  bytes → suppressed echo bytes) rather than heuristic.
- **Mode-barrier flush policy** (PR #169) is a partial
  command-finished seam; alt-screen toggle and shell
  switch both behave like "the current output context
  has ended; emit anything pending and reset".
  SessionModel's command-finished seam generalises this.
- **Backspace policy parameter** (PR #168) is an
  artefact of NOT having an Input Composition Surface;
  with one, the parameter would be answered structurally
  ("the surface knows the user pressed backspace; the
  Active Output knows the screen content shrank because
  of that backspace").

The three-component model isn't a NEW design; it's the
EXISTING design articulated explicitly so future work
can target the right component.

## Structured computational document framing

The maintainer's framing puts the Historical Document at
the centre: each completed shell interaction is a "cell" in
a navigable, queryable document — analogous to Jupyter or
Wolfram notebook cells. This section unpacks the analogy,
explores where it transfers, and names where it doesn't.

### The Jupyter / Wolfram analogy

A computational notebook (Jupyter, Wolfram, Observable)
organises a session into:

- **Input cells** (code the user wrote).
- **Output cells** (the kernel's response).
- **A linear ordering** of cells (the document is a
  sequence).
- **Per-cell state** (Active / completed / errored).
- **Navigation** (jump to cell N; previous / next cell;
  collapse / expand).
- **Cell-internal interactivity** (Jupyter widgets;
  Wolfram dynamic objects).

The mapping to pty-speak's three-component model:

| Notebook concept | pty-speak equivalent |
|---|---|
| Input cell | A `SessionTuple`'s prompt + command-text fields. |
| Output cell | The same `SessionTuple`'s output-bytes / output-text fields. |
| Linear ordering of cells | The SessionModel's bounded ring buffer; tuples ordered by completion timestamp. |
| Per-cell state (Active / completed) | Active Output (5.b) is the "running cell"; Historical Document (5.c) is the completed cells. |
| Navigation | "Read the last command's output", "jump to N commands ago", "show me only failed commands" — the SessionModel query API. |
| Cell-internal interactivity | Active Output's interactive elements (selections, forms). When the user is responding to a `gum-choose` prompt, the Active Output cell has an interactive widget. |

### Where the analogy transfers

1. **Linear ordering**: shell interactions are inherently
   linear. Each command runs after the previous one
   completes (or interrupts it). This matches notebook
   ordering well.
2. **Cell state machine**: command-not-yet-submitted →
   command-running → command-finished maps cleanly to
   editing → executing → completed.
3. **Navigation primitives**: "previous cell", "next cell",
   "jump to N" all make sense for shell interactions.
   Future review-mode (Stage 10) likely uses these
   primitives.
4. **Per-cell interactivity**: when a `gum-choose` prompt
   is on screen, the user is "responding to the active
   cell's widget". Same paradigm as a Jupyter widget.
5. **Document-level operations**: search, filter, export
   — all valid for shell history.
6. **Cell metadata**: timestamps, exit code, semantic
   events fired (ErrorLine count, etc.) all map cleanly.

### Where the analogy doesn't transfer

1. **Streaming output**: notebook cells typically execute
   atomically (input → result); shell commands stream
   bytes. The Active Output's "I'm receiving bytes right
   now" character has no clean Jupyter analogue (closest:
   long-running cell with interim output, but it's a
   second-class concept in Jupyter).
2. **No re-execution**: notebook cells can be re-run by
   placing the cursor and pressing Shift+Enter. Shell
   tuples are immutable once completed. Re-running a
   shell command means typing it again.
3. **Side effects everywhere**: notebook cells can be
   pure or side-effecting; shell commands are
   ALWAYS side-effecting (filesystem changes, process
   spawns, etc.). This affects what "navigation" can
   meaningfully do — you can READ history but you can't
   EDIT a previous tuple's command and re-run.
4. **Rich media output**: Jupyter cells output images,
   plots, HTML widgets. Shell commands output character
   text + ANSI escape sequences. The output medium is
   fundamentally narrower.
5. **Inter-cell dependency**: Jupyter cells share a
   kernel namespace; later cells can use earlier cells'
   variables. Shell commands share the shell's process
   state (env vars, cwd) but each command is its own
   process tree.
6. **Multi-shell sessions**: pty-speak supports
   Ctrl+Shift+1/2/3 hot-switch across shells. Jupyter
   sessions are single-kernel. The Historical Document
   has to decide: per-shell history or unified history?
   Open question (see SESSION-MODEL.md).

### The right level of inspiration

The notebook analogy is **inspirational at the
navigation + structure level** — it tells us that linear,
per-cell, queryable interaction history is a known-good
UX pattern that screen-reader users particularly benefit
from (each cell is a self-contained navigable unit).

The analogy is **NOT prescriptive at the implementation
level** — pty-speak doesn't need a kernel-execution model,
doesn't need rich-media output, doesn't need re-execution
semantics. It needs the structural shape (cells; states;
navigation) without the implementation overhead.

### Implications for future design

Naming this framing pins several future-design decisions
in advance:

1. **History navigation hotkeys** should follow the
   notebook idiom: previous / next / jump-to-N. Not the
   shell scrollback idiom (PageUp / PageDown) — that's
   a less structured navigation primitive.
2. **History export** should produce a structured
   artefact (JSON with one tuple per array entry,
   matching the SessionModel's persistence schema), not
   a raw transcript. Future replay tools (item 4) consume
   this.
3. **Cell-internal interactive elements** (selections,
   forms) should be addressable as "the active cell's
   element"; the user can navigate to it specifically,
   not just to the cell as a whole.
4. **Per-cell summarisation** (Phase 3 AI work) should
   produce per-tuple summaries, not whole-document
   summaries. Each summary attaches to its tuple.

## Interactive element taxonomy

Every `SemanticCategory` placeholder in
[`OutputEventTypes.fs`](../src/Terminal.Core/OutputEventTypes.fs)
represents a piece of interaction structure. This section
specifies what each placeholder is FOR — what triggers it,
which SIM module produces it, what consumers do with it,
and what status the producer is in today.

### The full taxonomy table

| Element type | Triggers | Producer (today) | Producer (future) | Consumer interaction | Status |
|---|---|---|---|---|---|
| `StreamChunk` | Coalesced text from streaming output (the everyday case) | StreamPathway suffix-diff path | unchanged | NVDA: announce; FileLogger: record | ✅ shipping |
| `BellRang` | BEL byte (0x07) in stdout | OutputEventBuilder | unchanged | EarconChannel: bell-ping tone | ✅ shipping |
| `ErrorLine` | Red-dominant changed row in the diff | StreamPathway colour-detection | (Phase 3 may refine via AI) | EarconChannel: error-tone (400 Hz) | ✅ shipping |
| `WarningLine` | Yellow-dominant changed row | StreamPathway colour-detection | unchanged | EarconChannel: warning-tone (600 Hz) | ✅ shipping |
| `SpinnerTick` | Spinner-pattern detected in the Coalescer | StreamPathway suppress branch | unchanged | (suppressed; no announce; future: optional earcon) | ✅ shipping (suppression only) |
| `AltScreenEntered` | DECSET 1049 (alt-screen on) | OutputEventBuilder via ModeChanged | unchanged | TuiPathway: stop streaming; ModeBarrier flush | ✅ shipping |
| `ModeBarrier` | Other mode flips (alt-screen exit, bracketed-paste, focus-reporting, DECCKM) | OutputEventBuilder via ModeChanged | unchanged | StreamPathway: flush per policy | ✅ shipping |
| `ParserError` | VtParser exception or malformed sequence | OutputEventBuilder | unchanged | NVDA: announce on `pty-speak.error`; FileLogger: record | ✅ shipping |
| `Custom` | User-extension category (string id) | (open) | Phase 2/3 third-party extensions | (route via Extensions metadata) | ✅ shipping (no producers yet) |
| `PromptDetected` | OSC 133 A from shell, OR heuristic prompt-row appearance | (none) | SessionModel substrate | NVDA: optional "ready for input" cue; SessionModel: tuple-start | 📋 reserved |
| `CommandSubmitted` | OSC 133 B, OR heuristic Enter-at-prompt | (none) | SIM input pathway + SessionModel | NVDA: optional "command starting" cue; SessionModel: capture command text | 📋 reserved |
| (NEW) `CommandFinished` | OSC 133 D + exit code, OR heuristic next-prompt detection | (none) | SessionModel substrate | NVDA: optional "succeeded" / "failed" cue; SessionModel: tuple-finish + move to history | 📋 reserved (NEW name; not yet in `SemanticCategory` enum) |
| `SelectionShown` | Detection of `gum-choose` / `fzf` / similar interactive prompts (heuristic) | (none) | FormPathway | NVDA: enumerate options; FormPathway: open interaction loop | 📋 reserved |
| `SelectionItem` | Within a selection, cursor-highlight moves to a different item | (none) | FormPathway | NVDA: announce highlighted option | 📋 reserved |
| `SelectionDismissed` | User confirms (Enter) or cancels (Esc) a selection | (none) | FormPathway | NVDA: announce result; SessionModel: record resolution | 📋 reserved |
| `HyperlinkOpened` | OSC 8 sequence | (none) | OSC 8 producer (Phase 2) | NVDA: link announce | 📋 reserved |
| (NEW) `InputCompletionMenu` | Tab-completion menu detected (PowerShell PSReadLine, Claude Code completions) | (none) | ClaudeCodePathway / ReplPathway | NVDA: enumerate completions | 📋 reserved (NEW name) |
| (NEW) `MultiLineCommand` | Heredoc / `\` continuation / unclosed quotes | (none) | InputPathway / SessionModel | NVDA: "multi-line input mode" cue; SessionModel: defer command-submitted until terminator | 📋 reserved (NEW name) |

### What "reserved" means

A 📋 reserved element is a placeholder name without a
producer. The category exists in the enum (or is proposed
to be added) so:

- Future work has a name to reference.
- The interactive-element taxonomy is structurally
  complete in the doc even when implementations lag.
- Cross-references between docs (e.g. SESSION-MODEL ↔
  PIPELINE-NARRATIVE ↔ INTERACTION-MODEL) are stable.

When a producer ships, the status flips from 📋 reserved
to ✅ shipping. The doc gets a new snapshot date.

### How to read each row

For each element type, the consumer interaction column
specifies BOTH a NVDA-side action (or "suppressed" /
"none") AND a SIM-side action (or "none"). Most rows have
both because semantic events are the SIM's vocabulary for
"this is interesting; here's what just happened" — both
the screen reader (NVDA) and the structured-document
(SessionModel) need to know about each event, but each
consumes it differently:

- NVDA cares about announcing → which activity ID,
  which earcon, which priority.
- SessionModel cares about structuring → which tuple
  field this fills, which transition this triggers.
- Other consumers (FileLogger, EarconChannel, future
  ReplPathway-internal logic) sit alongside.

The interactive-element taxonomy is the **SIM's
vocabulary of "things worth noticing"**. Adding a new
category means: "we want to be able to react
differently when this happens; here are its triggers,
producer, consumers".

### When to add a new category

Three criteria all need to hold:

1. **Distinct trigger**: the new category is reliably
   detectable from a substrate signal (byte sequence,
   row pattern, mode flag). If detection requires
   heuristics that overlap with existing categories,
   prefer extending an existing category's metadata
   (`Extensions` map) rather than adding a new one.
2. **Distinct consumer behaviour**: at least one consumer
   reacts differently to this category than to existing
   ones. If every consumer would do "same as
   StreamChunk", the category isn't earning its keep.
3. **Future-proofing for substrate gaps**: even if no
   producer ships immediately, having the name reserved
   prevents ad-hoc handling later. New rows added to
   this table are valid even with `(none)` producers.

### Naming the three NEW reservations

This doc proposes three new `SemanticCategory` cases
(`CommandFinished`, `InputCompletionMenu`,
`MultiLineCommand`). They're NOT added to the enum
today (per Open Question 4 in §10). The proposal is:

- **`CommandFinished`** is added when SessionModel
  substrate ships (item 28 implementation).
- **`InputCompletionMenu`** is added when the first
  consumer (likely ClaudeCodePathway) ships.
- **`MultiLineCommand`** is added when the InputPathway
  protocol ships (Phase 2 input framework cycle).

Until then, they're reserved-by-name in this doc so
future PRs have a stable identifier to reference.

### Cross-reference to PIPELINE-NARRATIVE event taxonomy

PIPELINE-NARRATIVE.md §3 (event taxonomy) lists
**system-level events** (keypress, screen mutation, mode
change, etc.) — the things that flow through the
pipeline. INTERACTION-MODEL.md (this section) lists
**interactive-element categories** — the
semantically-tagged outputs the SIM produces from those
system-level events.

Roughly:

- PIPELINE-NARRATIVE event = "something happened in the
  substrate"
- INTERACTION-MODEL element = "the SIM's interpretation
  of what that something means"

Same underlying activity; different lens. PIPELINE-
NARRATIVE is the operational lens; INTERACTION-MODEL is
the semantic lens.

## How this composes with existing substrates

The Interaction Model doesn't replace PIPELINE-NARRATIVE
or SESSION-MODEL — it sits one layer above them as the
architectural framing. This section maps the
relationships explicitly, so a reader knows where to
look for what.

### Cross-reference matrix

| Concept (this doc) | PIPELINE-NARRATIVE counterpart | SESSION-MODEL counterpart | Notes |
|---|---|---|---|
| Input Composition Surface (5.a) | Future Stage 0 (input substrate) | Active tuple's `CommandText` capture | Phase 2 input framework cycle owns the implementation |
| Active Output (5.b) | Stages 5-12 (live emit pipeline) | Active `SessionTuple` (post-CommandStart, pre-CommandFinished) | StreamPathway is the today-state; SessionModel adds structure |
| Historical Document (5.c) | (post-pipeline; not described in PIPELINE-NARRATIVE) | History ring buffer of completed `SessionTuple` | SESSION-MODEL.md is canonical for design |
| Echo correlation seam | Future Stage 0 ↔ Stage 8 link | Active tuple grounding | Phase 2 owns; today's suffix-diff is heuristic stand-in |
| Command-finished seam | Mode-barrier handling (today, partial) | OSC 133 D + heuristic fallback (future) | SESSION-MODEL.md §3 details the heuristics |
| Interactive elements (5b sub-content) | (event taxonomy line items) | Per-tuple metadata (selection resolutions, etc.) | `SemanticCategory` extensions |
| SIM (overall) | Cross-cuts all stages | Cross-cuts substrate | Conceptual; not a literal F# module today |

### Which doc to read for which question

The maintainer's workflow when reasoning about a feature:

1. **"What IS this feature in pty-speak's architecture?"**
   → INTERACTION-MODEL (this doc). Decide: input,
   active-output, or history? Decide: which interactive
   element category?
2. **"Where does the data flow for this?"** →
   PIPELINE-NARRATIVE. Find the stage(s) the feature
   touches; understand inputs / outputs / parameters.
3. **"How does this fit into history?"** → SESSION-MODEL.
   If the feature produces or consumes structured
   tuples, this is where the contract lives.
4. **"What knobs does this expose?"** → USER-SETTINGS.
   Add the parameter to the atlas; cross-reference here
   if the architecture demands it.
5. **"How does NVDA navigate this?"** →
   ACCESSIBILITY-INTERACTION-MODEL. Caret / focus / UIA
   considerations.
6. **"What does the spec say?"** →
   spec/event-and-output-framework.md (canonical) +
   spec/tech-plan.md (sequencing).

### Worked example: "I want to add prompt-boundary detection"

Walking through which doc owns which decision:

| Question | Owner doc | Section |
|---|---|---|
| Is this input, output, or history? | INTERACTION-MODEL | §5.a/b/c — it's a transition between Active Output and Historical Document; affects all three |
| Which substrate component implements it? | INTERACTION-MODEL | §4 SIM responsibility 4 (semantic event production) |
| What's the trigger source? | SESSION-MODEL | §3 OSC 133 + heuristic fallback |
| Which interactive element category fires? | INTERACTION-MODEL | §7 PromptDetected (📋 reserved) + CommandFinished (📋 reserved, NEW) |
| Which pipeline stage is affected? | PIPELINE-NARRATIVE | Stage 3.5 (new ScreenNotification.PromptBoundary) |
| What's the data shape? | SESSION-MODEL | §4 ActiveSessionTuple + transitions |
| What's announced? | INTERACTION-MODEL | §7 consumer interaction column |
| What's the spec deviation? | spec/event-and-output-framework.md | (requires ADR authorisation) |
| What knobs does it expose? | USER-SETTINGS.md | optional `prompt_announce_cue = on/off` etc. |

Each doc carries one piece; together they specify the
feature without overlap.

### Worked example: "Why does cmd typing announce twice?"

The double-announce issue surfaced 2026-05-06. Walking
through which doc explains which piece:

| Question | Owner doc | Section |
|---|---|---|
| What's the architectural cause? | INTERACTION-MODEL | §5.a — no Input Composition Surface, so no echo correlation |
| Where does the second announce come from? | PIPELINE-NARRATIVE | Stage 8 (suffix-diff emit) |
| What parameter would suppress it heuristically? | USER-SETTINGS.md | `echo_correlation` (📋 reserved) |
| What's the structural fix? | INTERACTION-MODEL | §5.a echo-correlation seam (Phase 2) |
| What's the spec implication? | spec/event-and-output-framework.md | implicit — Phase 2 input framework cycle |

The answer: today, the heuristic is "type a row; suffix-
diff finds the new char". The architectural fix is "the
Input Composition Surface knows the user typed a char;
the Active Output expects that char to echo; the
correlation suppresses the announce-side double-emit".
Two layers of fix; both are valid; the architectural one
is on the Phase 2 roadmap.

## Substrate gaps

The Interaction Model surfaces several pieces that don't
exist yet. Each is a research-stage backlog item; this
section catalogs them so the reader has a single map of
"what's missing" tied to the architectural framing.

### Catalog

| Gap | Backlog item | Current workaround | Architectural fix |
|---|---|---|---|
| Input Composition Surface as a named module | Phase 2 input framework cycle | Shell line editor handles editing; pty-speak just relays bytes | InputPathway protocol + InputBuffer module |
| SessionModel substrate (Historical Document) | item 28 | FileLogger log file (post-hoc); cell grid (volatile) | OSC 133 + heuristic fallback per [SESSION-MODEL.md](SESSION-MODEL.md) |
| Echo correlation seam | Phase 2 input framework cycle | Per-row suffix-diff + `echo_correlation` parameter (📋) | Track (input bytes → expected echo bytes → suppress at announce layer) |
| Command-finished seam | item 28 | Mode-barrier flush (partial; alt-screen / shell-switch only) | OSC 133 D + heuristic next-prompt detection |
| Cursor-aware diff | Phase 2 prerequisite | Row-level diff with suffix-diff per row | Cursor position threaded through CanonicalState; diff considers both content + cursor changes |
| Per-input-vs-output ActivityId routing | Phase 2 input framework cycle | All stream output uses `pty-speak.output` ActivityId | Separate `pty-speak.input-echo` with different NotificationProcessing |
| FormPathway / ReplPathway / ClaudeCodePathway | Phase 2 / 3 | StreamPathway only | Pathway-per-shell-class with semantic awareness |
| Interactive-element triggers (Selection / InputCompletionMenu / MultiLineCommand) | Phase 2 / 3 | (none) | FormPathway + ClaudeCodePathway + InputPathway implementations |
| Profile.Priority awareness | Phase 2 output framework cycle | Stream profile is pass-through; doesn't honour `Priority.Background` | Profile-layer suppression of Background events; spec D.2 mapping |
| Per-shell parameter overrides in TOML | Phase B | Single `[pathway.stream]` block applies uniformly | `[shell.<id>.pathway.stream]` overrides per shell |
| Snapshot / replay substrate | items 2 + 4 | (none) | Canonical-state serialisation + offline replay binary |

### Reading the catalog

For each gap:

- **Backlog item**: where the work is tracked (in the plan
  file's strategic backlog, or as a Phase-cycle reservation).
- **Current workaround**: what pty-speak does today INSTEAD,
  often with a heuristic that approximates the structural
  fix.
- **Architectural fix**: what the SIM-aligned solution
  looks like (referenced from the relevant section of
  PIPELINE-NARRATIVE / SESSION-MODEL / this doc).

The catalog is **descriptive**, not prescriptive about
sequencing. Some gaps (SessionModel) are higher priority
than others (per-shell TOML overrides) per the
substrate-first sequence the maintainer set 2026-05-06.

### Why explicit gap naming matters

Three reasons:

1. **Future contributors** see the architectural shape
   AND its current limits in one doc. They don't have to
   reverse-engineer "why does this work this way?".
2. **The maintainer** has a checklist when prioritising:
   each gap is a candidate for a research-stage or
   implementation cycle.
3. **The spec** absorbs gap naming via ADR authorisation
   when implementations land. The doc → spec → code flow
   is preserved.

## Versioning + maintenance

This doc follows the **snapshot model** established by
PIPELINE-NARRATIVE and SESSION-MODEL. Specific conventions:

### Snapshot dating

Top-of-doc front-matter carries `Snapshot: YYYY-MM-DD`.
This is the date the doc was last verified to match the
codebase + companion docs. Future contributors should
NOT update this without doing the verification.

### When to re-snapshot

Trigger conditions:

- A named module in the SIM responsibilities table moves
  files or significantly changes shape.
- An interactive-element category transitions from 📋
  reserved to ✅ shipping.
- A substrate gap closes (new module ships).
- Cross-references to companion docs (PIPELINE-NARRATIVE,
  SESSION-MODEL, USER-SETTINGS) drift.
- The maintainer's framing of the architecture shifts
  (rare; the framing has been stable since 2026-05-06).

When re-snapshotting, add a "What changed since last
snapshot" section near the top. After 2-3 snapshots
that section gets archived to a per-doc HISTORICAL log.

### What stability means

The interaction-model framing (SIM, three components,
notebook analogy) is intended to be **stable across
phases**. The substrate gaps catalog will shrink as
implementation lands; the categories list will grow as
producers ship; the cross-references will firm up. The
**framing itself** doesn't change just because a piece
was implemented — implementation success means the
framing got an entry in the catalog filled in, not that
the framing was wrong.

If the framing changes (e.g. the maintainer redirects
from "SIM" to "Shell Conversation Manager", or splits
the Active Output into "command output" + "interactive
zone"), that's a substantive doc rewrite, not a
re-snapshot. Substantive rewrites need ADR-style
authorisation per CLAUDE.md's spec-immutability rule
extended to architectural docs.

### Cross-doc consistency

When this doc says "X" and PIPELINE-NARRATIVE says
"Y" for the same thing, that's drift. Triage:

1. **Is one of them wrong about code?** — the doc that
   matches code wins; update the other.
2. **Are both consistent with code but inconsistent with
   each other?** — usually means a vocabulary collision
   (same code, two names). Pick one name, update both
   docs.
3. **Are they describing different things?** — clarify
   in both docs which lens each is using.

Drift is OK in small amounts; surfaced drift is an
opportunity to refine the vocabulary; drift that lingers
across multiple sessions is a signal that the vocabulary
needs explicit reconciliation.

## Open questions / Resolutions

Design forks surfaced for maintainer review. As of
2026-05-07, all 6 questions resolved per the audit-phase
walk-through (PR #181 plan + Cycle 8 fixup PR).
Resolutions captured below; original framing preserved
for historical context.

### Q1. Is "Shell Interaction Manager" the right name? — ✅ Resolved 2026-05-07

**Resolution**: KEEP **"Shell Interaction Manager" / SIM**
as proposed.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 1): clear; maintainer chose it; maps
cleanly to the responsibilities list; changing now would
create vocabulary churn across docs.

**Original question**: The maintainer proposed "Shell
Interaction Manager" 2026-05-06. Alternatives that
surfaced:

- **"Shell Conversation Manager"** — emphasises the
  bidirectional, multi-turn character. Slightly more
  human-friendly; "interaction" feels more
  technical / clinical.
- **"Interactive Document Coordinator"** — emphasises
  the structured-computational-document framing. Risks
  conflation with future GUI document objects.
- **"Pty Interaction Substrate"** — more architectural;
  pairs with "Display Pathway substrate" + "Output
  Channel substrate" naming patterns. But "substrate"
  is overloaded in this codebase already.

### Q2. Should the SIM become a literal F# module? — ✅ Resolved 2026-05-07

**Resolution**: KEEP CONCEPTUAL for now. Re-evaluate
when SessionModel implementation begins + the cross-slice
state machine is concrete. Almost certainly happens
during Phase 2.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 2): avoids premature abstraction;
threading + lifetime + testability concerns differ per
slice; "coordinated set of modules" framing serves
current substrate well.

**Original question**: Today's framing is "coordinated
set of modules; not a single F# module". Under what
conditions would a literal `SimCoordinator` (or similar)
module appear?

- Cross-slice state machine emerges (active prompt →
  command running → command finished → next prompt
  transitions visible to multiple slices).
- Cross-slice cancellation / kill-switch needed (Phase B).
- Cross-slice diagnostics needed (an extension of the
  Ctrl+Shift+D battery).

### Q3. Should INTERACTION-MODEL supersede PIPELINE-NARRATIVE / SESSION-MODEL? — ✅ Resolved 2026-05-07

**Resolution**: NO. The docs are complementary lenses,
not redundant.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 2): each doc answers a different
question. INTERACTION-MODEL is the "what is pty-speak
architecturally" lens; PIPELINE-NARRATIVE is the "how
does data flow" lens; SESSION-MODEL is the "how is
history structured" lens. A reader picks the lens for
the question they're asking. Each doc is shorter + more
focused for staying single-lens.

**Original question**: Reading INTERACTION-MODEL gives
a contributor a high-level picture. Could it replace
the others?

### Q4. Add the three NEW reservations to `SemanticCategory` immediately? — ✅ Resolved 2026-05-07

**Resolution**: DEFER adding to the enum. Reserve names
in this doc. Add to enum when concrete producer logic
exists.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 3): adding enum cases without producers
means every consumer's pattern-match needs a
`| _ -> ()` arm immediately, which is noise.
Reserved-by-doc gives future PRs a name to use without
forcing the immediate enum churn. The enum is small
enough that adding three cases when three implementations
land is a clean PR.

**Original question**: The taxonomy proposes
`CommandFinished`, `InputCompletionMenu`,
`MultiLineCommand` as reserved names. Should they be
added to the F# enum now (with `(none)` producers) or
deferred until producers ship?

### Q5. Is the notebook analogy load-bearing or just inspirational? — ✅ Resolved 2026-05-07

**Resolution**: **INSPIRATIONAL**. Pty-speak isn't a
notebook tool — it's a screen-reader-first terminal.
UX should optimise for terminal + screen-reader
workflows; borrow notebook idioms where they fit;
don't promise feature parity.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 3): cell numbers might or might not be
visible; jump-to-N might be a hotkey or a query; the
doc doesn't pre-commit.

**Original question**: If load-bearing (e.g. we promise
users "navigate like a notebook"), then implementations
need to deliver notebook-like UX (cell numbers visible,
jump-to-N, etc.). If just inspirational, implementations
have latitude.

### Q6. Per-shell history vs. unified history? — ✅ Resolved 2026-05-07

**Resolution**: **per-shell-session SessionModel by
default; opt-in unified history as TOML setting**.
Each Ctrl+Shift+1/2/3 hot-switch starts a fresh
SessionModel for the new shell. Unified history is a
power-user opt-in.

**Cross-reference**: SESSION-MODEL.md Q5 resolution
captures the same decision at the substrate-design
layer; this resolution applies the architectural-framing
implication.

**Rationale** (per maintainer agreement, audit walk-
through Cluster 3): simpler default; explicit opt-in
for power users; matches the substrate-first principle
of not pre-committing.

**Original question**: When the user uses
Ctrl+Shift+1/2/3 to hot-switch shells, does the
Historical Document carry over or split? E.g. does the
"previous command" hotkey from cmd find PowerShell
commands or only cmd commands?

## Companion-doc cross-reference index

For convenience, here's a single-line reference to the
canonical sections in companion docs that this doc
points to repeatedly:

- **PIPELINE-NARRATIVE.md §2 (Pipeline glossary — current
  state)** — the 12-stage operational pipeline.
- **PIPELINE-NARRATIVE.md §3 (Event taxonomy)** —
  system-level event types (different from this doc's
  semantic-element categories).
- **PIPELINE-NARRATIVE.md §4 (Substrate inventory)** —
  what exists vs. what's reserved at the operational
  layer.
- **SESSION-MODEL.md §3 (OSC 133 protocol + heuristic
  fallback)** — how the command-finished seam is
  detected.
- **SESSION-MODEL.md §4 (Data model)** — the
  SessionTuple shape.
- **SESSION-MODEL.md §6 (Query API)** — how the
  Historical Document is consumed.
- **USER-SETTINGS.md** — parameter atlas; future
  parameters cited here cross-reference there.
- **ACCESSIBILITY-INTERACTION-MODEL.md** —
  screen-reader-side interaction; caret / focus / UIA.
- **spec/event-and-output-framework.md §B (Output)** —
  canonical OutputEvent + Profile + Channel substrate.
- **spec/tech-plan.md §10** — Stage 10 (review mode +
  quick-nav), the first non-built-in consumer of the
  SessionModel substrate.

