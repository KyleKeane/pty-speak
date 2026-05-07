# Pane Model

> **Snapshot**: 2026-05-07
> **Status**: design / forward-looking — sketch only; not a full spec; not yet implemented
> **Authoring item**: backlog item 30 (research stage)
> **Companion docs**:
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing (Shell Interaction Manager + three-component model). The shell pane is owned by the SIM; this doc extends into multi-pane composition.
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational mechanics (12-stage byte-to-announcement flow). Each pane runs its own pipeline.
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — history substrate. The cherry-picked I/O pairs pane consumes SessionModel queries.
> - [`ACCESSIBILITY-INTERACTION-MODEL.md`](ACCESSIBILITY-INTERACTION-MODEL.md) — caret / focus / UIA tension at the screen-reader-interface layer. Multi-pane multiplies that tension.
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas. Pane-state TOML schema + pane-switch hotkeys land here.

## What this document is

A **forward-looking sketch** for the multi-pane workspace
framework that pty-speak should support. It names the
abstractions, drafts the contract every pane needs to
satisfy, catalogs five pane types (one shipping, four
reserved), flags the accessibility hard problems, and
surfaces open questions for the maintainer.

It is deliberately a **sketch**, not a full spec. Per the
maintainer's directive 2026-05-07: "These are not urgent to
spec out in detail now, but we want to ensure there's a good
framework for adding more interactive panes in the future."
Specific implementation details — concrete F# / C# types,
XAML composition, IPC shapes, TOML schemas — are deferred to
the eventual implementation cycle.

The doc is **descriptive** for the part that ships today
(the shell pane, owned by the Shell Interaction Manager) and
**forward-looking** for everything else (the Pane Coordinator
abstraction, per-pane accessibility surfaces, four reserved
pane types). Each piece is tagged so readers can distinguish
"this is real" from "this is design intent".

## Why this exists

Through PRs #170-#173, pty-speak's research-stage docs grew
to cover four lenses on the substrate: operational
mechanics ([PIPELINE-NARRATIVE](PIPELINE-NARRATIVE.md)),
history substrate ([SESSION-MODEL](SESSION-MODEL.md)),
architectural framing
([INTERACTION-MODEL](INTERACTION-MODEL.md)), and audit drift
inventory
([AUDIT-CODE-CONSISTENCY](AUDIT-CODE-CONSISTENCY.md)).

**None address UI composition.** Today's pty-speak treats
the shell view as the entire application: `MainWindow.xaml`
holds a single `<TerminalView>` filling the window; there's
no `TabControl` / `DockPanel` / `GridSplitter` / child-window
infrastructure; `AppReservedHotkeys` has no pane-navigation
gestures; the `TerminalAutomationPeer` is constructed 1:1
with the single `TerminalView`. Adding a second surface —
file tree, AI assistance, anything — requires architectural
work that isn't scaffolded.

The maintainer's request 2026-05-07, in their own words:

> "Please also include a brief sketch of how we might allow
> for additional panes to be added in the future such as a
> file tree or custom cherry picked input output pairs or
> language documentation or AI assistance. These are not
> urgent to spec out in detail now, but we want to ensure
> there's a good framework for adding more interactive panes
> in the future."

This doc is the answer. By naming the abstractions now,
future implementation cycles have a vocabulary + checklist
to build against, rather than reinventing each piece per
pane.

## Audience

Three intended readers:

1. **The maintainer**, when reasoning about whether a
   proposed feature is a NEW PANE or an EXTENSION of an
   existing pane. The doc helps decide.
2. **Future Claude sessions**, when implementation cycles
   approach. The contract + catalog + open questions are
   the design starting points.
3. **Future contributors**, when reading code that
   references multi-pane infrastructure (when it lands).

The doc is **NOT** a user-facing guide.

## Reading order

1. **The single-pane today** — current shape; what's
   already wired; what isn't.
2. **The multi-pane vision** — workspace with panes; shell
   pane is one of many.
3. **Naming** — Pane, Pane Coordinator, Workspace.
4. **The pane contract** — six concerns every pane must
   address.
5. **Pane catalog** — five entries (shell ✅; file tree +
   cherry-picked I/O + language docs + AI assistance 📋).
6. **Coordination protocols** — three patterns sketched.
7. **Accessibility — the hard problems** — six named
   challenges.
8. **Substrate gaps** — six items missing today.
9. **Composition with existing substrate** — cross-references.
10. **Versioning + maintenance** — snapshot model.
11. **Open questions** — five for maintainer.

## The single-pane today

pty-speak's current shape is **single-surface, monolithic**.
This section grounds the multi-pane vision in what's
actually here.

### MainWindow.xaml

[`src/Views/MainWindow.xaml`](../src/Views/MainWindow.xaml)
lines 1-14:

```xml
<Window …>
  <Grid>
    <views:TerminalView x:Name="TerminalSurface"
                        x:FieldModifier="public" />
  </Grid>
</Window>
```

A bare `<Grid>` with no `RowDefinitions` / `ColumnDefinitions`
holds exactly one child. The `TerminalView` fills the entire
window. **Adding a sibling pane requires replacing this with
a multi-row / multi-column / multi-tab layout.**

### TerminalView.cs

[`src/Views/TerminalView.cs`](../src/Views/TerminalView.cs)
line 31:

```csharp
public class TerminalView : FrameworkElement
```

Inherits from `FrameworkElement` (a low-level WPF base; no
default styling, no Background property). Designed as a
self-contained rendering surface: instantiated at
`MainWindow.xaml.cs:9`; wired via `SetPtyHost` (line 184)
+ `SetScreen` (line 207) callbacks; renders cell-by-cell via
`OnRender` (line 995); creates its own UIA peer via
`OnCreateAutomationPeer` (line 960).

All callbacks are 1:1 with the single instance. **No
"register a surface" or "compose multiple surfaces"
pattern exists.**

### AppReservedHotkeys

[`src/Views/TerminalView.cs:379-496`](../src/Views/TerminalView.cs)
lists 12 reserved gestures: update / diagnostic / release /
log open + copy / shell-switch / debug-toggle / health-check /
incident-marker / earcon-mute. **No pane-navigation
gestures** ("switch focus", "next pane", "open file tree").
The reserved-but-unbound list (Ctrl+Shift+4/5/6 for additional
shells per CLAUDE.md) extends the shell-switch model, not a
pane model.

### TerminalAutomationPeer

[`src/Terminal.Accessibility/TerminalAutomationPeer.fs:59-94`](../src/Terminal.Accessibility/TerminalAutomationPeer.fs):

```fsharp
type internal TerminalAutomationPeer(owner: FrameworkElement,
                                      textProvider: ITextProvider)
```

Takes a `FrameworkElement` owner + an `ITextProvider`.
Implements `ITextRangeProvider` / `ITextProvider` for
terminal text reading. **Decoupled from `TerminalView`
specifically** (takes `FrameworkElement`), but instantiated
1:1 per `TerminalView` via `OnCreateAutomationPeer()`. Each
pane in a multi-pane app would construct its own peer the
same way; the pattern is reusable but not yet exercised.

ActivityIds are **app-level constants** (defined in
`Terminal.Core.OutputEventTypes`), e.g. `pty-speak.output`,
`pty-speak.update`. Multi-pane needs scoping
(`pty-speak.<pane-id>.output`) so concurrent emissions
don't collide.

### App.cs + App.xaml

[`src/Views/App.cs`](../src/Views/App.cs) is 7 lines:

```csharp
public class App : Application { }
```

**No `App.xaml`.** No application-level pane infrastructure.
No child windows, popup resources, or multi-window logic.
The app is a single-document interface by design.

### Summary

The current architecture is single-surface, end-to-end.
Every layer (Window → Grid → TerminalView → peer →
ActivityIds) hardcodes "one surface". Multi-pane is a clean
architectural break, not an incremental refactor. The doc
names what would need to change.

## The multi-pane vision

pty-speak should support a **workspace** of multiple
**panes**. The shell pane (today's app) is one pane in the
workspace; future panes (file tree, cherry-picked I/O,
language docs, AI assistance) are siblings. Each pane is
self-contained but can coordinate with others.

### What a workspace looks like

A WPF window hosting (for example) a horizontal split:

```
┌──────────────────────────────────────────────────────┐
│ pty-speak                                            │
├─────────────┬────────────────────┬───────────────────┤
│  File tree  │   Shell pane       │   AI assistance   │
│             │   (today's app)    │                   │
│  src/       │                    │   suggested:      │
│  > docs/    │   PS> dir          │     git status    │
│    spec/    │   ...              │                   │
│  README.md  │                    │   explanation:    │
│             │                    │     dir lists ... │
└─────────────┴────────────────────┴───────────────────┘
```

Or vertical:

```
┌──────────────────────────────────────────────────────┐
│ Shell pane (60%)                                     │
│   PS> dir                                            │
│   ...                                                │
├──────────────────────────────────────────────────────┤
│ Cherry-picked I/O pairs (40%)                        │
│   [pinned 2026-05-06 14:32]                          │
│   $ git status                                       │
│   On branch main                                     │
│   ...                                                │
└──────────────────────────────────────────────────────┘
```

Or any other arrangement the maintainer wants. The framework
doesn't impose layout; it provides the substrate.

### Why this matters for accessibility

Multi-pane is a UX win even (especially) for screen-reader
users:

- **File tree pane** lets the maintainer navigate
  filesystem with NVDA's native tree-reading idioms
  instead of typing `ls` repeatedly + parsing output.
- **Cherry-picked I/O pane** preserves valuable command
  outputs that would otherwise scroll out of the terminal,
  pinned and addressable by NVDA's review cursor without
  fighting terminal scrollback.
- **Language documentation pane** gives instant access to
  reference material without leaving the terminal app
  (and without losing screen-reader focus context).
- **AI assistance pane** is a natural-language helper
  embedded in the workflow, addressable by hotkey + read
  by NVDA without context-switch.

These aren't ornament; they directly extend the screen-
reader-first design ethos.

### Why "ensure a good framework now"

If the first multi-pane PR has to invent the framework
from scratch, that PR becomes huge + risky. By naming the
framework now (this doc) + sketching the contract every
pane must satisfy, individual pane PRs in the future stay
small + focused: each PR adds one pane that conforms to
the contract. The framework PR — when it ships — implements
the abstractions; subsequent pane PRs are ~mechanical.

## Naming

Three terms used throughout this doc.

### Pane

A **pane** is a self-contained content surface with:

- A stable **identity** (name + display label).
- A **content source** (what produces the displayed
  content).
- A **rendering** output (how the content draws onto the
  screen).
- An **accessibility surface** (UIA peer, activity IDs,
  focus contract).
- An **interaction model** (how the user navigates +
  acts within the pane).
- A **lifecycle** (when it appears / disappears /
  persists).

The shell pane (today's `TerminalView`) is one pane.
Future panes are siblings.

### Pane Coordinator

The **Pane Coordinator** is the conceptual abstraction
that owns the workspace's pane set. It manages:

- **Pane lifecycle**: which panes are open, in what
  arrangement, sizes, focus order.
- **Focus routing**: which pane has keyboard focus; how
  the user switches; how NVDA's review cursor follows.
- **Accessibility surface coordination**: per-pane UIA
  peers wired into the workspace tree; activity-id
  scoping.
- **Inter-pane protocols**: messages flowing between
  panes (file-tree-click → shell `cd`; AI-pane "Run this"
  → shell input).
- **Persistence**: which panes / sizes / arrangements
  restore on relaunch (TOML config).

Like the **Shell Interaction Manager** (per
[INTERACTION-MODEL §4](INTERACTION-MODEL.md)), the Pane
Coordinator is a **conceptual abstraction**, not
necessarily a single F# module today. It maps to a
coordinated set of modules:

- A **layout host** (whichever WPF construct holds the
  panes — `Grid`, `DockPanel`, `Avalonia DockingHost`, or
  custom).
- A **focus manager** (handles pane-switch hotkeys,
  Tab / Shift+Tab, focus restoration).
- A **pane registry** (lookup table of registered pane
  instances by id).
- A **persistence layer** (TOML read / write of pane
  state).
- An **inter-pane message bus** (or simpler: direct
  references between specific pane pairs).

The same rationale that keeps the SIM conceptual applies
here: different threading models per slice (UI thread for
focus management; background threads for content
producers), different lifetime models (workspace-level
vs. per-pane), different testability concerns. A literal
`PaneCoordinator` F# / C# class may emerge during
implementation; for now, "coordinated set" is the framing.

### Workspace

The **workspace** is the user-visible composition: the
collection of panes currently open + their arrangement.
It's what lives in the WPF window. The Pane Coordinator
manages the workspace's state; the workspace itself is the
state.

The terminology mirrors IDE conventions (VS Code: panel
group containing panels; JetBrains: tool window strip
containing tool windows; tmux: window containing panes).
Conventions are loose; the doc adopts "workspace + pane +
Pane Coordinator" for consistency. See Open Question 1 for
maintainer naming preference.

## The pane contract

Every pane needs to address six concerns. The contract is
**descriptive of what concerns exist**, not prescriptive of
HOW to satisfy them. Implementation cycles pick concrete
shapes per pane.

### 1. Identity

- **Stable internal name** (`shell`, `file-tree`,
  `history-pinned`, `docs`, `ai-assistant`). String
  identifier referenced by code, TOML config, hotkeys.
- **User-facing label** (e.g. "Shell", "File tree",
  "Pinned commands", "Documentation", "AI assistance").
  Used in pane-switch announcements + window title
  decorations.
- **Stability**: the internal name is part of the
  TOML config schema; renaming is a breaking change.

### 2. Content source

- **Producer** that generates the pane's content. For
  the shell pane: the SIM (the PTY child + screen
  pipeline). For the file tree pane: a Filesystem
  Producer (directory enumeration + OS file events).
  For the cherry-picked I/O pairs pane: a Pinned-Tuple
  Producer (SessionModel queries + user-pin gestures).
- **Threading model**: producers may run on background
  threads; content arriving at the pane must marshal to
  the WPF dispatcher.
- **Update cadence**: streaming (shell pane), event-
  driven (file tree on file system change), on-demand
  (docs pane on user navigation), conversational (AI
  pane on user prompt).

### 3. Rendering

- **Surface type**: custom `FrameworkElement` with
  `OnRender` (today's `TerminalView` pattern), or hosted
  WPF `Control` (e.g. `TreeView` for file tree;
  `RichTextBox` for docs; standard controls compose
  faster but sacrifice fine-grained accessibility
  control).
- **Theming**: must respect app-wide theme parameters
  (background, foreground, font). Cross-references
  USER-SETTINGS.md theme schema (when that lands).
- **Resize behaviour**: how the pane reflows on
  workspace resize / pane-split adjustment.

### 4. Accessibility surface

- **UIA peer**: each pane provides its own
  `OnCreateAutomationPeer` returning a peer that
  implements the right UIA pattern set for the pane's
  content (text → `ITextProvider`; tree →
  `ISelectionProvider` + `IInvokeProvider` + tree-walk
  semantics; document with hyperlinks → text + invoke
  for links).
- **Activity IDs**: scoped per pane. Today's
  `pty-speak.output` becomes
  `pty-speak.shell.output`; new panes get
  `pty-speak.file-tree.focus-changed`,
  `pty-speak.ai-assistant.response`, etc.
- **Focus contract**: the pane's response to gaining /
  losing keyboard focus. First-focus announcement is
  load-bearing for screen-reader users (NVDA needs to
  hear "file tree pane focused; 12 entries; current is
  src/").
- **NVDA review cursor**: the pane's content must be
  walkable by NVDA's review cursor. For text content,
  `ITextRangeProvider` (today's pattern). For tree /
  list content, the tree / selection patterns.

### 5. Input handling

- **Keyboard routing**: when the pane has focus, what
  keys does it consume vs. let bubble up to app-level
  hotkeys? AppReservedHotkeys still take precedence
  (Ctrl+Shift+1/2/3 etc. work from any pane).
- **Mouse routing**: clicks land on the pane; clicks
  outside pane bounds either focus a different pane
  or are ignored.
- **Paste handling**: per-pane paste behaviour. Shell
  pane: bracketed-paste to PTY (today's behaviour).
  Other panes: pane-specific paste (e.g. AI assistance
  pane pastes into the prompt input area).
- **Hotkey extensions**: a pane MAY declare additional
  reserved hotkeys (e.g. file-tree pane reserves
  `F2` for rename). These extend, not replace,
  AppReservedHotkeys. Conflicts surface in TOML
  validation.

### 6. Lifecycle

- **Default visibility**: which panes are open at first
  launch. Default = shell pane only (preserves today's
  experience).
- **Add / remove**: hotkey / menu gesture to open /
  close a pane. Closing a pane preserves its state for
  re-open (e.g. cherry-picked I/O list survives close /
  re-open).
- **Restore on relaunch**: TOML config records which
  panes are open + their sizes + arrangement. On
  relaunch, the workspace restores. Per Open Question 4
  for schema scope.
- **Focus on add**: newly-added panes get keyboard
  focus, and NVDA announces their first-focus
  announcement.
- **Resource ownership**: pane-specific resources
  (background threads, file watchers, network connections)
  start when the pane opens, stop when it closes.

## Pane catalog

Five entries: one shipping today, four 📋 reserved for
future implementation cycles. Each gets a paragraph sketch
covering what it shows, what produces its content, the user-
interaction model, and how it might coordinate with other
panes. Concrete UI / data shapes deferred.

| Pane | Producer | Content source | Status |
|---|---|---|---|
| **Shell pane** | SIM | PTY child process; cell grid | ✅ shipping |
| **File tree pane** | Filesystem Producer (future) | Directory enumeration; OS file events | 📋 reserved |
| **Cherry-picked I/O pairs pane** | Pinned-Tuple Producer (future) | SessionModel query (selected tuples) | 📋 reserved |
| **Language documentation pane** | Doc Producer (future) | Local docset / web docs / LSP-sourced | 📋 reserved |
| **AI assistance pane** | LLM Producer (future) | Anthropic Claude API; SessionModel-grounded | 📋 reserved |

### Shell pane (✅ shipping)

The pane that ships today. Owned by the
[Shell Interaction Manager](INTERACTION-MODEL.md). Its
content is the PTY child process's screen output (the cell
grid in `Terminal.Core.Screen`); its rendering is the
custom `OnRender` in `TerminalView`; its UIA peer is
`TerminalAutomationPeer` with `ITextRangeProvider` /
`ITextProvider`; its input handling is keyboard / paste
routing through `KeyEncoding` to `ConPtyHost.WriteBytes`.

In the multi-pane future, the shell pane is one pane in the
workspace. Hot-switching shells (Ctrl+Shift+1/2/3) operates
within the shell pane; switching panes (Ctrl+Shift+P or
similar — see Open Question 5) moves focus between panes.

The shell pane's three-component model (Input Composition
Surface, Active Output, Historical Document — per
[INTERACTION-MODEL §5](INTERACTION-MODEL.md)) is internal
to the shell pane; the workspace doesn't see those
components, just the pane as a whole.

### File tree pane (📋 reserved)

A filesystem-tree view of the working directory + parents.
Produces:

- A tree structure rooted at the current shell pane's
  `pwd`.
- Updates on filesystem changes (OS file watcher events).
- Updates on shell pane's `cd` (the shell-to-pane
  coordination protocol).

User interaction:

- Arrow keys navigate the tree (UIA tree pattern; NVDA
  reads each entry).
- Enter on a directory: cd's the shell pane to that
  directory.
- Enter on a file: depends on the file type — code files
  open the docs pane to that file; text files might
  preview in a viewer.
- F2 / similar: rename (subject to per-pane hotkey
  registration; see Pane Contract §5).

Cross-pane coordination:

- File tree → shell: click sends `cd <path>\r` via shell
  pane's input.
- Shell → file tree: shell `pwd` change updates tree's
  highlighted directory.

Reserved decisions: visibility (always-on docked sidebar
vs. toggle-on-demand); breadcrumb integration (does the
tree show breadcrumbs of cwd ancestry above current dir).

### Cherry-picked I/O pairs pane (📋 reserved)

A pinned list of selected (command, output) pairs from the
SessionModel history. The maintainer pins valuable outputs
they want to refer back to without losing them to terminal
scrollback.

Produces:

- A list of pinned `SessionTuple` instances (per
  [SESSION-MODEL.md §4](SESSION-MODEL.md)).
- Updates on user pin / unpin gestures.
- Each pinned tuple shows: the command, the output, the
  exit code, the timestamp.

User interaction:

- A hotkey (suggested: `Ctrl+Shift+P` for "pin current
  output", subject to AppReservedHotkeys allocation)
  pins the most-recently-completed shell-pane tuple.
- Up / Down arrows navigate pinned entries.
- Enter on an entry: focus / read the entry's full
  content via NVDA.
- Delete on an entry: unpin.
- A "search pinned" subordinate input (subject to
  scope; might be deferred).

Cross-pane coordination:

- Shell command-finished → cherry-picked I/O: candidate
  for pinning (the user opts in via gesture).
- Cherry-picked I/O drag-drop → AI assistance: "explain
  this command's output" (depends on AI pane existing).

Reserved decisions: persistence across sessions (TOML
config or separate pinned-corpus file); maximum pinned
count; per-shell or unified pin list.

### Language documentation pane (📋 reserved)

A reference / documentation viewer. Produces:

- Documentation content from a configurable source
  (local docset like Dash / Zeal; LSP server's
  hover-document for the file in focus; manually-curated
  text snippets).
- Updates on the user's explicit "look up X"
  request (hotkey or selection-driven).
- Updates on shell pane context (e.g. when the user
  types `git status`, the docs pane shows `git status`
  reference).

User interaction:

- A hotkey (suggested: `Ctrl+Shift+?` or context menu)
  asks "look up the word at point in the shell pane".
- Arrow keys / Page Up / Down navigate the doc text.
- Hyperlinks within docs: Enter follows; UIA invoke
  pattern.

Cross-pane coordination:

- Shell selection / context → docs: contextual lookup.
- Docs → shell: copy a code example back into the shell
  prompt (paste-into-shell-from-pane gesture).

Reserved decisions: doc source registry (where docs come
from; how new sources are added); offline vs. web
sourcing; doc theme + accessibility (hyperlink density,
semantic-element exposure).

### AI assistance pane (📋 reserved)

An LLM-powered conversational helper embedded in the
workspace. Produces:

- LLM responses to user queries.
- Suggestions grounded in SessionModel history (recent
  commands, recent errors).
- Optionally: LLM-summarised digests of long shell
  outputs.

User interaction:

- A prompt input area (text-entry surface within the
  pane).
- Submit a query (Enter or hotkey).
- Read the response (NVDA reads it as it streams).
- "Run this" gesture: take a suggested shell command
  the LLM produced and send it to the shell pane's
  input.

Cross-pane coordination:

- Shell → AI: "explain this output" (selection in shell
  pane sent as context to LLM).
- Cherry-picked I/O → AI: drag a tuple in.
- AI → shell: send a suggested command as input.
- SessionModel → AI: history is grounding context for
  the LLM (per
  [SESSION-MODEL.md §6 query API](SESSION-MODEL.md)).

Reserved decisions: API client implementation (Anthropic
SDK); auth token storage (TOML / OS keyring); cost +
rate-limit handling; offline degradation (what happens
without network).

## Coordination protocols

Three patterns of pane-to-pane (or shell-to-pane)
communication. Sketched, not specified.

### Pattern 1: Pane → shell action

A non-shell pane initiates a shell action by sending bytes
to the shell pane's input.

Examples:

- File tree: click on directory → `cd <path>\r`
- AI assistance: "Run this command" → bytes of the
  suggested command + Enter
- Cherry-picked I/O: re-run a pinned command (drag back
  to shell pane)

Mechanism: each non-shell pane gets a reference to (or
gestures via the Pane Coordinator to) the shell pane's
**input transmission** capability — the same surface that
keyboard / paste hits today via
`ConPtyHost.WriteBytes`. Bytes travel through the
existing PTY stdin path; from the shell's perspective,
indistinguishable from user keystrokes.

Future Phase 2 echo correlation (per
[INTERACTION-MODEL §5.a](INTERACTION-MODEL.md)) treats
pane-originated bytes as input — they get correlated +
suppressed at the announce layer the same way user-typed
bytes do.

### Pattern 2: Shell → pane state

The shell pane's state changes propagate to interested
panes.

Examples:

- Shell `pwd` change (user `cd`'d somewhere) → file tree
  highlights the new directory
- Shell command-finished → cherry-picked I/O pane shows
  candidate-for-pin notification + AI pane updates its
  grounding history
- Shell error / non-zero exit → AI pane offers to
  suggest a fix

Mechanism: the SIM (or future SessionModel) emits
**semantic events** that the Pane Coordinator routes to
subscribed pane producers. The shell pane is unaware of
the routing; it just produces events at the SIM /
SessionModel layer per
[INTERACTION-MODEL §7 (interactive element taxonomy)](INTERACTION-MODEL.md).

### Pattern 3: Pane ↔ pane

Two non-shell panes communicate.

Examples:

- Drag a pinned tuple from cherry-picked I/O pane into
  AI assistance pane → "explain this command's output"
- Drag a documentation snippet from docs pane into AI
  assistance pane → "summarise this paragraph"
- Hotkey from any pane → "open the file at point in
  docs pane"

Mechanism: depends on Open Question 2 (single-window vs.
multi-window). Within one window, drag-drop +
keyboard-driven gestures are feasible. Cross-window
coordination requires explicit IPC.

Sketched only; the right concrete mechanism (event bus?
direct references? async channels?) emerges during
implementation.

## Accessibility — the hard problems

Multi-pane + screen reader is genuinely hard. This section
names the hard problems explicitly so future work doesn't
ambush itself. None are solved here; each is research-stage
material for the eventual implementation cycle.

### 1. Focus routing

Which pane has keyboard focus, and how does the user
switch?

Options:
- **Hotkey-driven cycle**: `Ctrl+Shift+P` cycles forward,
  `Ctrl+Shift+Shift+P` cycles back.
- **Direct hotkey per pane**: e.g. `Ctrl+Shift+S` for
  shell, `Ctrl+Shift+T` for tree, `Ctrl+Shift+I` for AI
  assistance. Simpler mental model; risks running out of
  letter slots.
- **Tab cycle**: Tab moves focus forward, Shift+Tab back.
  Conflicts with shell pane's Tab behaviour
  (autocomplete) — may not be viable.
- **Spatial navigation**: arrow-key chord (e.g.
  `Ctrl+Shift+Right`) moves focus to the pane in that
  direction.

Each option has accessibility trade-offs. Direct hotkeys
are most discoverable for screen-reader users; spatial
nav is least discoverable.

### 2. NVDA review cursor + focused element

NVDA's review cursor anchors to the focused element by
default. When the user switches panes, the review cursor
needs to track the new pane's content automatically — OR
the user needs explicit "move review cursor to focused
element" gestures.

Existing tension (per
[ACCESSIBILITY-INTERACTION-MODEL.md](ACCESSIBILITY-INTERACTION-MODEL.md)):
NVDA's review cursor + system caret + PTY child cursor
are three positions that constantly diverge. Multi-pane
adds: each pane has its own caret + UIA peer, so the
divergence multiplies per pane. The user has to mentally
track multiple "where am I?" positions simultaneously.

This is the single hardest problem multi-pane introduces.
Implementation cycles will likely need:
- An explicit "I'm in pane X" state announcement on
  every pane switch.
- A "current pane summary" hotkey (similar to
  Ctrl+Shift+H today's health-check) that re-orients the
  user.
- Per-pane discoverability: the workspace exposes "panes
  available: shell, file tree, ..." via UIA so NVDA can
  enumerate.

### 3. ActivityId scoping

Today's `pty-speak.output` is application-level. With
multiple panes emitting concurrent UIA notifications, the
reader can't disambiguate "which pane this announcement
came from".

Required scoping:
- `pty-speak.shell.output`
- `pty-speak.file-tree.focus-changed`
- `pty-speak.cherry-picked.pin-confirmed`
- `pty-speak.docs.navigated`
- `pty-speak.ai-assistant.response`

The activity-id scheme grows. Implementation must update
`Terminal.Core.OutputEventTypes.ActivityIds` to support
per-pane scoping; pane producers emit with the scoped id;
NvdaChannel's NotificationProcessing mapping extends to
recognise pane-scoped ids.

This is mostly mechanical, but extensive — every existing
ActivityId reference becomes pane-scoped.

### 4. Pane-switch announcement

When the user switches panes, NVDA must announce the new
pane in a load-bearing way:

- "Shell pane focused. PowerShell. Last command exited 0."
- "File tree pane focused. 12 entries. Current: src,
  directory."
- "Cherry-picked I/O pane focused. 3 pinned. Current:
  git status from 2026-05-06 14:32."
- "AI assistance pane focused. Response area. 4 messages
  in conversation."

Each pane defines its own first-focus announcement
template. Templates are TOML-configurable (per Open
Question 4 scope).

The announcement competes with the pane-switch
handshake (NVDA's own focus-changed announcement) — pty-
speak's announcement may need to come AFTER NVDA's so the
user gets both ("File tree pane" from NVDA + "12 entries.
Current: src." from pty-speak). Sequencing is delicate.

### 5. Per-pane UIA pattern sets

Each pane type needs its own UIA pattern set:

| Pane | Primary UIA patterns |
|---|---|
| Shell pane | `ITextProvider` + `ITextRangeProvider` (today's pattern) |
| File tree pane | `ISelectionProvider` + `IInvokeProvider` + tree-walk semantics; potentially `IExpandCollapseProvider` |
| Cherry-picked I/O pane | `ISelectionProvider` + `IInvokeProvider` + per-tuple `ITextRangeProvider` (each tuple is a navigable text region) |
| Language documentation pane | `ITextProvider` + `ITextRangeProvider` + hyperlink invoke |
| AI assistance pane | Two regions — input area (`IValueProvider`) + response area (`ITextRangeProvider`) — potentially as separate UIA peers within the pane |

Each pane PR ships its own peer; the framework PR
provides the registration + workspace-level wiring.

### 6. Compounded caret / UIA / NVDA review cursor tension

[ACCESSIBILITY-INTERACTION-MODEL.md](ACCESSIBILITY-INTERACTION-MODEL.md)
documents the tension between system caret, NVDA review
cursor, PTY cursor for the single shell pane. Multi-pane
multiplies this:

- Each pane has its own system-caret-equivalent (or no
  caret at all, for non-text panes).
- NVDA's review cursor follows focus across panes,
  sometimes resetting to top-of-content on switch.
- The shell pane's PTY cursor is unchanged but now
  competes with other panes' interactivity for the
  user's attention.

This is hard. Implementation cycles will need explicit
research + likely several NVDA-validation rounds to
land an experience that doesn't constantly disorient.

## Substrate gaps

Six pieces don't exist today and would need to be built
for multi-pane to work. This is the implementation
checklist.

### 1. Pane abstraction (no F# / C# type)

Today there's no `Pane` / `IPane` / similar. The shell
pane is implicit — `TerminalView` + its peer + its
producer (the SIM) — but not formalised as an instance
of an abstraction.

Implementation needs:
- A `Pane` interface or abstract class (probably C#
  for WPF FrameworkElement integration; F# can call
  through).
- Pane registration with the Pane Coordinator at app
  startup.
- A pane-id-to-instance lookup map.

### 2. Pane Coordinator (no orchestration layer)

No layer exists that owns "which panes are open". `App.cs`
+ `MainWindow.xaml.cs` directly hardcode the single shell
pane. Implementation needs:
- A `PaneCoordinator` (or coordinated set of modules)
  that:
  - Registers panes.
  - Manages workspace layout.
  - Routes focus.
  - Persists / restores state.

### 3. Multi-content MainWindow.xaml

Today's `<Grid>` with a single child must become a layout
host capable of holding multiple panes. Options:
- WPF `<Grid>` with `<RowDefinition>` /
  `<ColumnDefinition>` + `<GridSplitter>` for
  user-resizable splits.
- WPF `<DockPanel>` for docked panes (sidebar +
  content pattern).
- A third-party library like AvalonDock for richer
  docking semantics.
- Custom layout host (most flexible, most work).

### 4. Per-pane UIA peers

Today's 1:1 `TerminalView` ↔ `TerminalAutomationPeer` is
the pattern, but only one instance exists. Implementation
needs:
- Each pane's `OnCreateAutomationPeer` returning the
  appropriate peer for its UIA pattern set.
- Pane Coordinator level peer for the workspace itself
  (a container that exposes children — likely
  `WindowAutomationPeer`-equivalent).

### 5. Per-pane ActivityIds

Today: app-level. Implementation needs:
- `ActivityIds` module updated to support pane-scoped
  ids (e.g. `Activities.forPane "shell" "output" =
  "pty-speak.shell.output"`).
- All existing emit sites updated to emit pane-scoped.
- `NvdaChannel.notificationProcessingFor` updated to
  recognise pane-scoped ids.

### 6. Pane-state persistence in TOML

Today: not persisted. Implementation needs:
- A new `[workspace]` section in TOML config recording
  open panes, sizes, arrangement.
- Per-pane `[pane.<id>]` sections for pane-specific
  parameters.
- Validation + warnings for unknown pane ids / malformed
  layouts.
- Default-config behaviour: empty workspace = today's
  experience (shell pane only).

## Composition with existing substrate

This doc is a sister to the existing four research docs.
Cross-references:

| Concept (PANE-MODEL) | Cross-reference |
|---|---|
| Pane Coordinator | [INTERACTION-MODEL §4](INTERACTION-MODEL.md) — analogous role to Shell Interaction Manager, one architectural level up |
| Shell pane (today's) | [INTERACTION-MODEL §5](INTERACTION-MODEL.md) — internal three-component model |
| Cherry-picked I/O pane content | [SESSION-MODEL.md §4-§6](SESSION-MODEL.md) — SessionTuple shape + query API |
| Per-pane pipeline | [PIPELINE-NARRATIVE.md §2](PIPELINE-NARRATIVE.md) — each pane runs its own variant of the 12-stage pipeline |
| Pane accessibility | [ACCESSIBILITY-INTERACTION-MODEL.md](ACCESSIBILITY-INTERACTION-MODEL.md) — caret / UIA / NVDA review-cursor tension that compounds across panes |
| Pane-state TOML schema | [USER-SETTINGS.md](USER-SETTINGS.md) — parameter atlas; multi-pane parameters land here when designed |
| Spec authority | [spec/event-and-output-framework.md](../spec/event-and-output-framework.md) — canonical spec; multi-pane will need ADR-style authorisation when implementation begins |

## Versioning + maintenance

Follows the snapshot model established by
PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL.

### Snapshot dating

Top-of-doc front matter carries `Snapshot: YYYY-MM-DD`.
This is the date the doc was last verified against:
- The codebase (no scaffolding for multi-pane today;
  re-verify when scaffolding lands).
- Companion docs (cross-references current).
- Maintainer's framing (open questions reflect current
  state).

### When to re-snapshot

Trigger conditions:
- A pane type transitions from 📋 reserved to ✅
  shipping.
- The Pane Coordinator (or equivalent) lands as a
  literal F# / C# module.
- Cross-references to companion docs drift.
- An open question lands with maintainer input.
- The maintainer's naming preference shifts.

### What stability means

The framing — Pane abstraction, Pane Coordinator,
six-concern pane contract, three coordination protocols —
is intended to be **stable across implementation
cycles**. The catalog will grow as panes ship; the
substrate gaps catalog will shrink as gaps close. The
**framing itself** doesn't change just because a piece
was implemented.

If the framing changes (e.g. the maintainer redirects to
a different naming or split / merge of components),
that's a substantive doc rewrite, not a re-snapshot.

## Open questions

Five design forks awaiting maintainer input. Each is
paired with a recommended position; the maintainer's
decision selects + this doc gets updated.

### Q1. Naming — pane / workspace / Pane Coordinator?

The doc adopts "Pane" + "Workspace" + "Pane Coordinator".
Alternatives:
- **Surface / Layout / Layout Coordinator** — closer to
  WPF idiom; "surface" is generic.
- **Panel / Workspace / Panel Manager** — closer to VS
  Code idiom.
- **View / Viewport / View Manager** — closer to MVC
  idiom; risks confusing with WPF `View`.
- **Tile / Window / Window Manager** — closer to tmux /
  i3wm idiom.

**Recommended position**: keep **Pane / Workspace /
Pane Coordinator**. Aligns with screen-reader users'
familiar terminology (NVDA reads many tools as "panes"),
parallels existing INTERACTION-MODEL / SESSION-MODEL doc
naming, distinct from WPF's existing `Panel` /
`FrameworkElement` vocabulary so no collision.

### Q2. Single-window multi-pane vs. multi-window?

Two architectural choices:
- **Single-window multi-pane**: one WPF `Window`
  containing all panes (split / dock layout). Simpler
  accessibility (one focus tree); harder for
  monitor-spanning; harder to give panes truly
  independent positions.
- **Multi-window**: each pane is its own `Window` (or
  `Window`s grouped by tab). Better monitor-spanning;
  harder accessibility (multiple top-level windows
  multiply NVDA focus issues).

**Recommended position**: start with **single-window
multi-pane**. Matches today's experience (one window).
Single focus tree simplifies accessibility design. If
monitor-spanning becomes a strong requirement,
multi-window is additive (a power-user feature).

### Q3. Floating vs. docked vs. both?

Layout flexibility:
- **Fixed dock**: panes live in fixed positions
  (sidebar / main / inspector). Familiar from JetBrains
  IDEs. Predictable; less flexible.
- **Freeform floating**: panes can be moved / resized
  anywhere. Familiar from older Visual Studio. More
  flexible; harder to design accessibility (focus order
  becomes spatial).
- **Both**: dock by default, allow floating. Most
  flexible; most complex.

**Recommended position**: **dock with optional
GridSplitter resize**. Predictable; accessible; matches
modern IDE conventions; doesn't preclude floating in a
later phase. Skip floating for v1.

### Q4. Per-pane TOML schema scope?

How rich should the multi-pane TOML schema be?
- **Minimal**: just "which panes are open" +
  "arrangement". Pane-specific params stay in code.
- **Moderate**: which panes + arrangement + per-pane
  size / position + per-pane visibility flags.
- **Rich**: everything moderate + per-pane semantic
  parameters (e.g. file tree's "ignore patterns",
  AI assistance's "model name", docs' "doc source
  registry").

**Recommended position**: **moderate**. Layout / size /
visibility are user-configurable; per-pane semantic
parameters land in their own subsections per the
existing USER-SETTINGS.md atlas pattern. Avoids one mega-
section that's hard to maintain.

### Q5. WSL2 / remote SSH shells — parallel panes or hot-switch?

CLAUDE.md reserves `Ctrl+Shift+4/5/6` for additional
shells (WSL, Python REPL, etc.). Two interpretations:
- **Hot-switch within shell pane**: today's
  `Ctrl+Shift+1/2/3` model extends; the user has ONE
  shell pane, hot-switches between cmd / pwsh / claude /
  WSL / Python / etc.
- **Multiple parallel shell panes**: each shell type is
  its own pane; user sees them simultaneously.

**Recommended position**: **start with hot-switch model
(today's behaviour extended)**; **allow multiple-pane
power-user mode later**. Reasons:
- Hot-switch is well-understood by maintainer + ships
  today.
- Multiple parallel shell panes multiplies the
  accessibility hard problems (focus, review cursor,
  per-pane peer collisions).
- Power-user mode is additive (the user opts into a
  second shell pane explicitly).

## Companion-doc cross-reference index

Quick reference to canonical sections:

- **INTERACTION-MODEL.md §4 (Shell Interaction Manager)**
  — analogous abstraction one layer below.
- **INTERACTION-MODEL.md §5 (three-component model)** —
  the shell pane's internal structure.
- **INTERACTION-MODEL.md §7 (interactive element
  taxonomy)** — semantic events that flow into pane
  coordinations.
- **SESSION-MODEL.md §4 (data model)** — SessionTuple
  shape; consumed by cherry-picked I/O pane.
- **SESSION-MODEL.md §6 (query API)** — how the
  cherry-picked I/O pane fetches its content.
- **PIPELINE-NARRATIVE.md §2 (pipeline glossary)** —
  the 12-stage pipeline that the shell pane runs;
  other panes run their own variants.
- **ACCESSIBILITY-INTERACTION-MODEL.md** — single-pane
  caret/UIA tension that compounds across panes.
- **USER-SETTINGS.md** — parameter atlas; multi-pane
  parameters land here when designed.
- **CLAUDE.md** "App-reserved hotkey contract" — current
  AppReservedHotkeys table; multi-pane extensions land
  here.

