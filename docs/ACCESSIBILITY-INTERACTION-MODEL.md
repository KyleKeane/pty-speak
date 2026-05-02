# Accessibility interaction model

## Genesis

This document was requested by the maintainer mid-session
during post-Stage-6 verification, after a manual NVDA test
surfaced a deep design tension that doesn't have a single
clean fix. The maintainer's framing, in their own words:

> *"This is very complicated because we need interactive
> keyboard focus to jump to some of the output buffers in
> order to issue responses to Claude Code via PTY ... I
> think this deserves a thoughtful outline so it is
> captured and identifies why this is such a challenging
> problem to face and solve."*

> *"the complexity of these designed decisions about where
> the system caret is focused, the tension of getting
> keyboard focus to interact [with] PTY interface options,
> which we might need to surface as separate modal
> dialogues, [and] any method of getting notifications
> back to NVDA."*

The triggering observation was that NVDA's "current line"
command read directory output above the input line, while
the actual system caret was in the input line below — the
two were desynchronised because pty-speak does not yet
surface caret/selection information to UIA. That specific
gap has a known fix (see Pattern B below). But the broader
conversation it surfaced is bigger than one fix: it's the
shape of the design space we have to navigate to give blind
developers a first-class terminal experience.

This document captures the conceptual map. It is
intentionally a **skeleton** — the maintainer will flesh out
the specifics over time. The goal here is to name the
paradigms, surface the tensions, and give every future
contributor the right vocabulary to reason about this space.

## Why this is hard

A terminal emulator is a screen reader's worst case. Three
distinct positional concerns must stay in sync, and the
default screen-reader heuristics are tuned for application
shapes (form fields, web documents, file trees) that
terminals fundamentally aren't.

The three positional concerns:

1. **The system caret** — the OS-level text-input cursor.
   Where the operating system thinks bytes go when the user
   types.
2. **The screen reader's reading position** — what NVDA /
   JAWS / Narrator considers "the current focus of
   attention". Drives "current line", "current word", auto-
   announce on change.
3. **The PTY child's cursor** — where the shell thinks the
   cursor is in its terminal grid. Moves when the shell
   emits `\x1b[H` / `\x1b[2;5H` / similar.

In a typical text editor, all three line up automatically.
In a terminal, they constantly diverge:

- Output streams in **above** the input line; the system
  caret stays at the input.
- The screen reader's default tracking misses the streamed
  output entirely.
- Sighted users glance at the new content; blind users
  hear nothing without explicit intervention.

## Glossary

Naming the paradigms so future PRs and discussions can
reference them precisely.

### Operating-system / WPF concepts

- **System caret** — Win32 `CARETPOSITION` / WPF
  `Keyboard.FocusedElement`. One per process. Bytes typed
  on the keyboard go to this position.
- **Focus** — WPF concept; per-window / per-element. Different
  from system caret in WPF (a control can be focused without
  a caret, and vice versa).
- **Keyboard focus** — the focused element receiving
  `KeyDown` / `TextInput` events. In pty-speak, this is the
  `TerminalView` after launch.

### Screen-reader concepts (NVDA-centric, applies to JAWS / Narrator with rephrasing)

- **Focus mode** — NVDA's default for application content.
  Tracks system caret and announces what's near it.
  Real-time interaction; user's keystrokes go to the focused
  control.
- **Browse mode** — NVDA's mode for web pages and complex
  documents. Different "browse cursor"; arrow keys navigate
  semantically (next heading, next link, etc.); user
  explicitly switches modes via NVDA+Space.
- **Review cursor** — always available regardless of mode.
  Driven by the user via NVDA review commands (Numpad in
  laptop layout, custom in others). Does NOT move the
  system caret. Used for exploring content without
  disturbing input state.
- **Object navigation** — NVDA's tree-walk over UIA
  elements. Up / down moves between siblings / parent.
  Distinct from the review cursor's character-level
  navigation.
- **NVDA-modifier key** — Insert (default) or CapsLock
  (laptop layout). User configures which. Many NVDA
  shortcuts use this prefix.

### UIA (Microsoft UI Automation) primitives

- **`IRawElementProviderSimple`** — every UIA element.
- **Document control type** — what NVDA expects for terminal-
  shaped content. Stage 4 set this for `TerminalView`.
- **`ITextProvider`** — exposes element content as a
  document-shaped surface with ranges, selections, etc.
- **`ITextProvider.DocumentRange`** — the full content as
  one range.
- **`ITextProvider.GetSelection()`** — the currently
  selected range(s). For terminals **without** mouse
  selection, this is conventionally a zero-width range at
  the caret position. **pty-speak does not yet expose
  this; that's the gap behind the maintainer's "current
  line is desynchronised" observation.** Phase 2.
- **`ITextProvider2`** — newer interface; adds
  `RangeFromAnnotations`, `GetCaretRange`. NVDA prefers it
  when present.
- **`RaiseAutomationEvent` / `RaiseNotificationEvent`** —
  pushes an event to UIA listeners (NVDA, etc.). The
  Notification variant is the per-event one-shot
  announcement; **Stage 5 uses this for streaming output**.
  See `TerminalView.Announce`.
- **`AutomationNotificationKind`** — `Other`, `ActionAborted`,
  `ActionCompleted`, `ConfirmationOfAction`,
  `ItemAdded`, `ItemRemoved`. We use `Other`.
- **`AutomationNotificationProcessing`** — `MostRecent` (new
  notification supersedes pending), `ImportantAll` (queue
  all in order), `CurrentThenMostRecent`, `All`. Stage 6
  post-fix uses `ImportantAll` for streaming, `MostRecent`
  for hotkeys.
- **`activityId`** — string tag attached to a notification.
  NVDA can be configured per-tag. We define a stable
  vocabulary in `Terminal.Core.ActivityIds`
  (`pty-speak.output`, `pty-speak.update`, etc.).
- **`Live region` pattern** — UIA-level "this content
  changes; announce updates". **NVDA explicitly disables
  this for terminals** (per `spec/tech-plan.md` §5.6) to
  avoid double-announce; pty-speak does NOT use it.
- **`TextChangedEvent`** — UIA event for text content
  changes. **Also disabled by NVDA for terminals**, same
  reason. Not used.

### PTY (pseudo-console) concepts

- **ConPTY** — Windows pseudo-console API. One pipe pair
  (stdin, stdout) plus a control handle. The host (us)
  reads stdout, writes stdin; the child (cmd.exe,
  PowerShell, claude-code) reads stdin, writes stdout.
- **VT escape sequences** — bytes the child emits to move
  the cursor, change colours, switch modes, etc. We parse
  them in `Terminal.Parser` and apply to the in-memory
  `Screen`.
- **Alt screen** — secondary back-buffer some apps use for
  full-screen TUI (`vim`, `less`, `claude-code`). Different
  expectations from primary buffer about scrolling, cursor,
  history.
- **DECCKM** — application vs normal cursor mode. Different
  arrow-key encodings. Stage 6 PR-A wired this.
- **Bracketed paste** — DECSET ?2004; pastes get wrapped in
  `\x1b[200~`...`\x1b[201~` markers so the shell knows it's
  a paste rather than typed input.
- **Focus reporting** — DECSET ?1004; emits `\x1b[I` /
  `\x1b[O` on focus-in / focus-out. Editors use this.

### Terminal interaction paradigms (cross-shell)

- **Line discipline** — the shell's edit-while-typing model.
  cmd.exe: minimal. PowerShell + PSReadLine: rich
  (history search, multi-line, syntax highlighting). bash /
  zsh + readline: vi or emacs mode.
- **Multi-line input** — block-paste a script, multi-line
  string, here-doc. The shell handles continuation; from
  the user's perspective the cursor moves between lines
  without submitting.
- **TUI applications** — full-screen apps that take over
  the terminal (htop, vim, less, fzf, claude-code's Ink-
  rendered UI). Different paradigm from line-discipline
  shells; key bindings differ; "what is selected" /
  "what is focused" is application-defined.
- **Prompt** — the shell's "I'm ready for input" indicator.
  In cmd.exe: `C:\Users\...>`. In PowerShell: `PS C:\>`. In
  bash: `$`. Visually a static line; semantically a state
  marker the user needs to detect.

## The core tensions

This is the heart of the document. Each tension below is a
real conflict where a choice must be made; some have a
clear answer, others are open.

### Tension 1 — System caret vs. content of interest

The system caret is on the input line. New content
(command output, stream from a long-running process)
appears **above** the caret. NVDA's default caret-tracking
heuristic gives the user nothing for content above the
caret.

**Why this matters:** the bulk of useful information in a
terminal session is output, not input. A blind user who
only hears their own typing is functionally worse off than
a sighted user with a non-screen-reader-aware terminal.

**Resolution direction:** Stage 5's
`RaiseNotificationEvent` pattern. Notifications fire
regardless of caret position. We coalesce + debounce + send
each chunk as a notification with `pty-speak.output`
activityId. NVDA reads them as they arrive.

**Open issues:** the streaming-silence bug we're currently
debugging is in this layer. See PR #103's diagnostic logs
for the in-flight investigation.

### Tension 2 — Focus mode vs. browse mode vs. review cursor

NVDA has three reading-position concepts. Terminals don't
fit cleanly into any of them.

- **Focus mode** assumes the system caret is meaningful
  and content arrives at the caret (e.g. typing into a
  text box). Wrong for terminal output (above caret).
- **Browse mode** assumes static document content
  navigated semantically. Wrong because terminals are
  real-time and the user needs to type back.
- **Review cursor** is fine for exploration but doesn't
  follow new content automatically.

The maintainer's observation: switching modes (NVDA+Space)
is awkward for fluid use. A user reading streaming output
who then needs to type a response shouldn't have to flip
modes mid-thought.

**Resolution direction:** stay in **focus mode**
permanently; use Notification events for streaming output
(Pattern A); add caret/selection support so the existing
focus-mode commands ("current line" etc.) work correctly
when used (Pattern B). The user only resorts to the review
cursor for offline exploration.

**Future stage candidate (Stage 10):** an explicit "review
mode" hotkey (`Alt+Shift+R`) that snapshots the screen and
hands the user a snapshot to navigate without disturbing
the input state. Different from NVDA's browse mode; lighter
weight; pty-speak-specific.

### Tension 3 — Notifications vs. caret-tracking

Two strategies for getting new content read aloud:

- **Notifications**: one-way, no caret motion, doesn't
  disturb other UI tasks. Stage 5's choice.
- **Caret-tracking**: move the system caret to the new
  content; NVDA reads automatically. Forces the input
  cursor away from the input line, breaking type-while-
  reading.

**Conflict:** caret-tracking would tell NVDA "current line
is the new output" but would also tell the OS "type bytes
go to the output line". The latter is wrong.

**Resolution:** notifications for streaming, never move
the system caret away from the input. Caret support via
`ITextProvider.GetSelection()` is for **introspection
only** (NVDA's "current line" command queries it without
moving anything).

### Tension 4 — Output streams vs. typed echo

When the user types `d`, cmd.exe echoes `d` back as part of
its stdout. From our parser's perspective, the echoed `d`
is identical to any other output character. From the
user's perspective, NVDA may already be announcing the
typed character via its own "speak typed characters"
setting — and our notification pipeline would announce it
again, causing double-speech.

**Conflict:** we can't distinguish "this character is the
user's typing being echoed" from "this character is real
output" without state-tracking the input pipeline.

**Mitigations being considered:**

- Suppress single-character notifications when the most
  recent typing event matches the next-arrived echo byte.
- Document that NVDA's "speak typed characters" should be
  off when using pty-speak (currently a workaround;
  surface it in user docs).
- Future: PSReadLine and similar shells emit different
  byte patterns for echo vs. fresh output; potentially
  detect and suppress per-shell.

**Open question:** are we OK with the current double-
announce reality, given the maintainer's NVDA setting?
Should we ship suppression heuristics? Phase 2 candidate.

### Tension 5 — Real-time interaction vs. modal interaction

Some interactions are inherently modal — confirming a
destructive action, multi-line input, paste-as-block,
filling out a form-like prompt. These don't fit the
"type a command and see output" rhythm.

**Maintainer's framing:** *"we might need to surface as
separate modal dialogues"*

For these cases, breaking out of the terminal flow into a
WPF modal dialog gives:

- Standard text-edit UIA semantics inside the dialog.
- Screen reader treats it as a regular form (focus mode
  works as expected).
- Returns control to the PTY when dismissed.

**Trade-off:** less faithful as a terminal emulator;
wraps shell-level interactions in a host-app shell.

**Open candidates** for modal treatment:

1. Multi-line input (the user wants to compose a block
   before sending).
2. Paste preview (review what's about to be pasted before
   committing — paste-injection defence already strips
   `\x1b[201~`, but a modal lets the user audibly confirm
   the content first).
3. Confirmation prompts that the host can detect (e.g.
   "Continue? (y/n)"). Detect heuristically + offer a
   modal yes/no dialog.
4. Search-in-output (find a string in the scrollback;
   present results in a list rather than ANSI-colored
   grep output).

**Decision pending:** which of these warrant a modal
break-out and which stay in-terminal? Each has UX
implications.

### Tension 6 — Multiple shells, multiple paradigms

cmd.exe, PowerShell + PSReadLine, bash / zsh, claude-code
(Ink TUI) each have different expectations:

- cmd.exe: bare line-discipline, no Ctrl+L, no Ctrl+R
  history search. Our Stage 6 PR-B special-cases Ctrl+L
  to send `cls\r` for cmd.exe; documented as cmd.exe-
  specific.
- PowerShell + PSReadLine: rich edit experience, Ctrl+L
  clears, Ctrl+R searches history, Tab completion with
  popup.
- bash / zsh + readline: vi mode, emacs mode, M-b /
  M-f word motion.
- claude-code (Ink): full-screen TUI; key bindings
  defined by Ink components; cursor navigation handled
  internally.

**Conflict:** the keyboard encoder must produce bytes that
each shell understands. Some keys (Ctrl+L) have different
"correct" semantics per shell. Some hotkeys we want for the
host (Ctrl+Shift+L for logs) might collide with shell
gestures.

**Resolution direction:** the **app-reserved hotkey list**
in `TerminalView.AppReservedHotkeys` documents what the
host claims, with the rest forwarded to the shell. Any
host hotkey choice must be a chord that no shell uses for
critical interaction.

**Open issues:** shell-aware behaviour (per-shell config
for things like Ctrl+L) is a Phase 2 candidate. Phase 2
also adds shell detection so the encoder can pick correct
behaviour automatically.

### Tension 7 — Two-way conversation latency vs. NVDA throttling

NVDA's notification queue has limited depth and processing
heuristics. Rapid bursts of output can overwhelm it,
causing dropped speech. Stage 5's coalescer handles this
on the producer side (debounce + dedup); NVDA's
`AutomationNotificationProcessing.ImportantAll` setting
handles it on the consumer side.

**Open issues:** a long-running command that prints lots of
fast output (compile, test runs) can produce more
notifications than NVDA can speak. The user wants:

- A way to "tune out" while the command runs and "catch
  up" at the end (Stage 8 candidate: a "command complete"
  signal).
- A way to interrupt the announcement (NVDA's `Ctrl` key
  silences the speech queue, but the user may want
  finer-grained control).
- Spinner suppression (already in the coalescer).

## Design patterns being considered

### Pattern A — Notification-based streaming (Stage 5; current)

Use UIA `RaiseNotificationEvent` for streaming output.
System caret stays at PTY input. Screen reader announces
output as Notifications regardless of caret. Coalesced /
debounced upstream.

**Status:** shipped in Stage 5, but the streaming-silence
bug surfaced in post-Stage-6 verification suggests a
defect in this path. Diagnosis ongoing (PR #103's logging
infrastructure exists for this).

### Pattern B — Caret/selection sync (Phase 2 candidate)

Implement `ITextProvider.GetSelection()` returning a zero-
width range at the current PTY cursor position.

NVDA's "current line" / "current word" / etc. read from
this position. Caret-relative commands ("say from caret to
end") work correctly. Selection-related commands (when we
add real selection) work correctly.

**Critically: this does NOT move the system caret** — it
just tells NVDA where the caret IS. The system caret stays
on the input line; type-while-reading remains intact.

**Status:** logged as a Phase 2 stage; the streaming-fix
investigation is gating it until that's resolved.

### Pattern C — Modal dialogs for structured interactions

Pop a WPF modal for inherently-modal interactions (multi-
line input, paste preview, confirmations). Standard UIA
semantics inside; returns to PTY on dismiss.

**Status:** maintainer-suggested; not yet specified.
Specific candidates listed under Tension 5.

### Pattern D — Custom review mode (Stage 10 reservation)

`Alt+Shift+R` (already reserved) toggles a host-managed
"review mode". Snapshot the screen; intercept arrow keys
for navigation; second `Alt+Shift+R` returns to PTY input.
Lighter-weight than NVDA's browse mode; pty-speak-specific.

**Status:** reserved but not built.

### Pattern E — Live regions (forbidden by spec)

NVDA disables live-region announcements for terminals.
Documented as not viable in `spec/tech-plan.md` §5.6.
Listed here for completeness so future contributors don't
re-propose it.

## Specific challenging cases (reference list)

These are the cases the design has to handle correctly. Each
is a future expansion section the maintainer will fill in:

- **Streaming output via Notifications** — Stage 5; debugging
  in flight.
- **Line / character / word navigation** — Stage 4; shipped.
- **Typed-character echo** — Tension 4; double-announce risk.
- **Claude Code interactive flows** — Stage 7 territory;
  Ink TUI semantics.
- **Multi-line input** — Pattern C candidate.
- **Long-running output** — coalescer + Stage 8 prompt-redraw
  signal.
- **Paste with bracketed paste** — Stage 6 PR-B; security
  posture documented in SECURITY.md.
- **Selection / Copy / Find-in-output** — Phase 2; UIA
  selection support.
- **Multiple shells / per-shell behaviour** — Tension 6;
  shell detection a Phase 2 candidate.
- **Resize during long output** — Stage 6 debounce; tested.
- **Alt-screen toggle (`vim` / `less` / `claude-code`)** —
  Stage 4.5 PR-B; coalescer flush barrier shipped.

## Open architectural questions

The questions below are unresolved. They need maintainer
decisions before specific stages or PRs can land:

1. **Should Pattern C (modal dialogs) be added for any of
   the candidates listed?** Each has trade-offs between
   accessibility correctness and terminal-emulator
   faithfulness. Prioritise the highest-frequency
   interactions first.
2. **Should pty-speak document a "configure NVDA this way"
   guide** that addresses the typed-character double-
   announce, the speech rate, the notification activityId
   filters, etc.?
3. **Is Pattern D (Alt+Shift+R review mode) the right
   primary navigation gesture?** Or should we lean
   harder on NVDA's review cursor and not add a custom
   mode?
4. **What's the threshold for "shell-aware behaviour"?**
   Per-shell configuration (Ctrl+L → `cls\r` for cmd.exe,
   `0x0C` for bash, etc.) is correct but adds complexity.
5. **How do we handle TUI mode entry/exit
   announcements?** When `vim` opens, the screen reader
   should know "we're in vim now"; when it closes, "back
   to shell". Stage 4.5 PR-B's alt-screen flush barrier
   is the substrate; what announcement text?

## Inspiration / prior art

A starting list for the maintainer to fill in with detail
about each:

- **Windows Terminal** — handles some of this; NVDA uses
  the `TermControl2` className heuristic. We have a
  reserved follow-up to add this className signal to our
  peer (logged in the Stage 5 plan as out-of-scope).
- **iTerm2 (macOS)** — VoiceOver integration; different
  paradigm (Quartz-based).
- **Terminal.app (macOS)** — different model again.
- **Apple's Terminal accessibility documentation** —
  reference for VoiceOver expectations.
- **NVDA's Terminal add-on** — third-party extension that
  changes how NVDA handles terminal-shaped content; worth
  studying.
- **Emacs accessibility (emacspeak)** — independent of UIA
  but the original screen-reader-aware shell experience.
- **`speakup` (Linux console screen reader)** — kernel-
  level character-by-character speech; very different
  paradigm but informative for thinking about real-time
  reading.

## References

- [`spec/tech-plan.md`](../spec/tech-plan.md) §4
  (UIA peer + Document role), §5 (streaming output via
  Notifications), §6 (keyboard input + DECCKM + paste +
  focus reporting), §10 (review mode reservation).
- [`SECURITY.md`](../SECURITY.md) — security-relevant
  interaction constraints (paste-injection defence,
  logging chokepoint, NVDA-modifier-key filter).
- [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) — keybindings
  list, Phase 2 settings candidates.
- [`docs/LOGGING.md`](LOGGING.md) — diagnostic surface for
  bugs in this layer.
- NVDA's developer guide:
  <https://www.nvaccess.org/files/nvda/documentation/developerGuide.html>
- Microsoft UIA docs (Text pattern):
  <https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange>

## Status

This document is a **skeleton**. The maintainer will fill
in detail over time as decisions land or new tensions
surface. Future PRs touching this surface should:

1. Update the relevant section here with the rationale.
2. Cross-link to the implementation in
   `src/Terminal.Accessibility/`,
   `src/Terminal.Core/Coalescer.fs`, or
   `src/Views/TerminalView.cs`.
3. Add new entries to the "Specific challenging cases"
   list when introducing new interaction surfaces.
4. Surface any newly-discovered tensions in the "Tensions"
   section so future contributors aren't re-discovering them.

The contributor convention captured here parallels the
"Consider configurability when iterating" rule in
[`CONTRIBUTING.md`](../CONTRIBUTING.md): when a PR makes a
decision in this design space, log it.
