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

## Technical constraints we work within

This section names the platform-, library-, and protocol-level
limits that shape every decision below. Future PRs are
encouraged to add to this list when they hit a new one.

### WPF + Win32

- **WPF dispatcher is single-threaded and STA.** UI work, UIA
  peer events, and clipboard access all serialise on the
  dispatcher thread. Long operations on the dispatcher freeze
  the window. Background work has to marshal back via
  `Dispatcher.InvokeAsync` for any UI-touching call.
- **Routed-event ordering:** `PreviewKeyDown` (tunneling) →
  `KeyDown` (bubbling) → `InputBindings` → `CommandBindings`.
  Marking `e.Handled = true` in any handler stops the rest. We
  exploit this for the `AppReservedHotkeys` short-circuit.
- **`InputBindings` on a custom `FrameworkElement` are NOT
  auto-routed by `CommandManager`.** `KeyBinding` →
  `CommandBinding` only fires reliably on built-in `Control`
  subclasses. We learned this in Stage 6 (Ctrl+V paste fix);
  the fix was direct gesture handling at the top of
  `OnPreviewKeyDown`.
- **`Clipboard.SetText` requires STA + can throw
  `COMException`** when the OS clipboard is contended (e.g. the
  user just pasted from another app). Single-attempt is
  acceptable; the user retries.
- **`MeasureOverride` controls layout.** Returning a fixed size
  means the element doesn't track parent resize — we hit this
  as the Stage 6 resize-cuts-off-text bug; fix was to honour
  `availableSize`.
- **WPF's `OnRender` runs on the dispatcher.** Long renders
  block input. We use `Screen.SnapshotRows` to take ONE
  locked snapshot per render frame instead of repeated
  `GetCell` calls (Acc/9 fix in Stage 5).
- **F# 9 strict-null + WPF interop friction.** F# 9's
  nullness analysis sometimes disagrees with C# nullable
  annotations on third-party assemblies (e.g.
  `ILogger.BeginScope` returning `IDisposable?` is read by
  F# as non-null). Workaround: return a no-op disposable
  rather than `null`.

### ConPTY (Windows pseudo-console)

- **No OVERLAPPED I/O on ConPTY pipes.** All reads and writes
  are synchronous. Our reader runs in its own background
  task; our writer (`ConPtyHost.WriteBytes`) blocks the
  caller until the kernel accepts the bytes.
- **Pipe handles must be released by the parent immediately
  after `CreatePseudoConsole`** or the child's pipes never
  signal EOF. Single missed close causes hangs on shutdown.
  Documented inline in `PseudoConsole.fs:create`.
- **The ConPTY init prologue includes `\x1b[?9001h\x1b[?1004h`**
  emitted by ConPTY itself before the child runs. Stage 4.5's
  parser must not choke on these; Stage 6 PR-A's
  FocusReporting arm now handles `?1004h` explicitly.
- **`ResizePseudoConsole` is documented thread-safe** but the
  child shell does layout work on every resize. WPF's
  per-pixel `SizeChanged` during a window drag would hammer
  the child without debouncing — Stage 6's 200ms
  `DispatcherTimer` debounce protects against this.
- **`Job Object` containment requires the child to be
  assigned BEFORE its first instruction** for strict
  guarantees. We don't pass `CREATE_SUSPENDED` (microsecond
  race window accepted; cmd.exe doesn't fork that fast).
  `KILL_ON_JOB_CLOSE` ensures the descendant tree dies on
  parent exit even on hard parent crash.
- **Environment variables inherit from parent by default.**
  `lpEnvironment = NULL` in `CreateProcess`. Sensitive vars
  (`GITHUB_TOKEN`, `OPENAI_API_KEY`) reach the child unless
  filtered. Stage 7 will add the filter (env-scrub PO-5).

### UIA (UI Automation) — what NVDA actually consumes

- **`UIElementAutomationPeer.FromElement` returns `null`
  until UIA queries the element.** Peer creation is lazy.
  Our `Announce` early-skips when peer is null; it's a
  defensive default, not a bug.
- **NVDA disables `LiveRegion` and `TextChanged` events for
  terminals.** Forbidden by `spec/tech-plan.md` §5.6 to
  prevent double-announce. Don't re-add them.
- **`AutomationNotificationProcessing.MostRecent` is
  per-`activityId`.** Rapid bursts of the same activityId
  supersede each other in NVDA's queue; chunks earlier in a
  burst can be dropped before NVDA reads them. We use
  `ImportantAll` for streaming output (Stage 6 post-fix) and
  `MostRecent` for hotkey-style one-shot announcements.
- **`ITextProvider` vs `ITextProvider2`.** Newer NVDA versions
  prefer the `2` variant when present; it adds caret-aware
  range methods (`GetCaretRange`). We currently implement
  `ITextProvider` only.
- **`ITextProvider.GetSelection()` is how NVDA learns the
  caret position.** Returning a zero-width range at the PTY
  cursor lets NVDA's "current line" command read the right
  line WITHOUT moving the system caret. We don't yet
  implement this; the disconnect that triggered this
  document is precisely this gap.
- **Document control type is the right shape for terminal
  content.** Stage 4 sets it; NVDA recognises it as
  document-shaped and exposes its content via the Text
  pattern.
- **NVDA notification queue depth is implementation-defined**
  and can drop entries under sustained load. The Coalescer
  (Stage 5) handles producer-side rate-limiting.
- **`activityId` is a free-form string the user / contributor
  can later filter on.** We define a stable vocabulary
  (`pty-speak.output`, `pty-speak.update`, `pty-speak.error`,
  `pty-speak.diagnostic`, `pty-speak.releases`,
  `pty-speak.mode`, `pty-speak.new-release`).

### NVDA-specific behaviour we have to coexist with

- **NVDA-modifier key is user-configurable** (Insert,
  CapsLock, both). Our screen-reader-modifier filter has to
  let bare presses of either through so NVDA receives them.
- **"Speak typed characters" setting** echoes typed letters
  via NVDA itself — independent of our streaming. Risks
  double-announce when the shell echoes back through our
  Notification path. Currently the maintainer ships with
  this on; future suppression heuristics are Phase 2.
- **NVDA Speech History viewer (NVDA+F4 then F4)** is what
  sighted contributors should ask blind users to consult
  for "what did NVDA actually say". Distinct from "what did
  the user hear" (audio output, speech rate, etc. matter).
- **NVDA's review cursor doesn't move automatically** when
  document content changes. If we want "the most recent
  output line is what NVDA reads on the next review
  command", we'd need to push the review cursor — which
  isn't an NVDA-supported operation through standard UIA.

### VT escape sequence parser

- **Only a fraction of xterm's sequence space is implemented.**
  Stage 4.5 covers DECTCEM, DECSC/DECRC, alt-screen 1049,
  256/truecolor SGR, OSC 52 (silent drop). Stage 6 adds
  DECCKM, bracketed paste, focus reporting. **Hundreds of
  other private modes** exist; they're silently dropped.
- **Response-generating sequences are forbidden** per
  `SECURITY.md` (CVE-2003-0063, CVE-2022-45872, etc.). DSR,
  DA1/2/3, DECRQM, DECRQSS, cursor-position reports, title
  reports — all parsed and dropped, never responded to.
- **Malformed input must not crash the parser.** Audit-cycle
  SR-1 + SR-3 hardened the state machine; the catch-all in
  the reader loop publishes a `ParserError` rather than
  letting the exception terminate the app.
- **OSC 52 (clipboard set from child) is silently dropped.**
  A child writing `\x1b]52;c;<base64>\x07` MUST NOT be one
  paste away from RCE.

### F# 9 / .NET 9

- **Strict-null + central package management** are both on.
  Reading C# `Nullable<T>` annotations through F#'s nullness
  view sometimes diverges (`IDisposable?` from
  `Microsoft.Extensions.Logging.Abstractions` 9.0.0 is read
  as non-null in F#). Workarounds documented at the call
  sites.
- **`type ... and ...`** for mutual recursion in F# requires
  `let rec` for value bindings (we hit this in
  Coalescer.fs's hash functions). Type definitions don't
  need `rec` because F# auto-recurses.
- **F# extension method visibility** requires `open` on the
  containing namespace. The `LogInformation` /
  `LogError` extension methods are in
  `Microsoft.Extensions.Logging`; missing the open is a
  compile error rather than a fallback to the underlying
  `Log` method.
- **F# generic interface methods on `ILogger`** have known
  syntax pitfalls; the `member _.Log<'TState>` form with
  nullness annotations is finicky and worth verifying via CI.

### Threading model

- **Three thread classes** in steady-state:
  1. **WPF dispatcher** — UI, UIA peer events, hotkey
     handlers, OnRender, OnPreviewKeyDown.
  2. **PTY reader** — Task.Run loop reading stdout chunks;
     marshals VtEvent application back to dispatcher via
     `Dispatcher.InvokeAsync`.
  3. **Coalescer drain** — background Task.Run loop reading
     the notification channel; marshals `Announce` calls back
     to dispatcher.
- **The PTY stdin pipe is single-writer.** All keyboard /
  paste / focus-event writes funnel through the dispatcher
  (single-threaded by definition); no lock needed.
- **`Channel<T>` is the cross-thread primitive of choice.**
  Bounded with `FullMode = DropOldest` (parser → coalescer)
  or `FullMode = Wait` (coalescer → drain). DropOldest at
  the high-volume seam protects against an out-of-control
  parser; Wait at the low-volume seam preserves
  notification ordering.

### File system + persistence

- **`%LOCALAPPDATA%`** is the conventional per-user data
  root for Windows desktop apps. Logs go under
  `PtySpeak/logs/`.
- **No registry use.** We deliberately avoid HKCU; logs +
  future TOML config live in well-known file paths so
  uninstall + reinstall is clean.
- **Windows file paths reject `:`** in filenames. Session
  log files use `HH-mm-ss` not `HH:mm:ss`.

## Desired blind-user workflows

Each workflow below is a **scenario** the design must
support, named with the maintainer's user-perspective in
mind. Most have specific implementation work pending; the
status notes which stage delivers them.

### W1 — First launch and orientation

**User goal:** Open pty-speak; immediately know what shell
is running, what version of pty-speak is loaded, and that
the cursor is ready for input.

**Current state:** Window title + Document role announce
on focus (Stage 4). Cmd.exe banner streams via Stage 5
coalescer. **Working** in steady-state.

**Open issues:** if the streaming-silence bug we're
diagnosing also affects launch announcements, the user
hears only the title. Currently being investigated via the
session-log infrastructure.

### W2 — Type a command, hear it run

**User goal:** Type `git status`; hear typed characters
(NVDA setting); press Enter; hear each output line stream
back at conversational pace; know when the command
completes and the prompt is ready again.

**Current state:** Stage 5 streams output as
Notifications. Stage 6 routes typed input. **Streaming-
silence bug currently breaks this for typed-command
output**; banner reads on launch but `dir` / `echo` output
goes silent. Diagnosis in flight.

**Stage 8 candidate:** "Command complete" cue when the
prompt redraws — an audible confirmation that the user
can take their next action. Strategic review §G assigned
this to Stage 8.

### W3 — Long-running output (compile, test, install)

**User goal:** Run `npm install` or `cargo build`. Hear
that something is happening (not silence); not be drowned
in spinner output; know when it finishes; be able to skim
the most recent output afterwards.

**Current state:** Coalescer's spinner suppression handles
the flood (Stage 5). "Something is happening" relies on
non-spinner output still arriving. "Finishes" cue is W2's
Stage 8 candidate.

**Open issues:** the user may want to "tune out" while the
command runs and "catch up" afterwards. Currently NVDA
keeps speaking until the queue empties; if they ALT+TAB
away, output keeps streaming into the screen but they
miss the audio. Some affordance for "summarise the last N
seconds when I'm ready" would help — Phase 2.

### W4 — Review previous command's output

**User goal:** Just ran `dir`; want to hear specific
lines from the output (e.g. "what was the size of
config.json?") without re-running the command.

**Current state:** NVDA review cursor walks the screen via
the UIA Text pattern (Stage 4). **Working** for
character/word/line navigation.

**Open issues:** the "current line" desync the maintainer
hit (NVDA reports a different line than where the system
caret is). Pattern B fixes this — implement
`ITextProvider.GetSelection()` returning a zero-width
range at the PTY cursor. Phase 2 stage.

### W5 — Find a specific string in output

**User goal:** Test runner printed 200 lines; want to find
"FAIL:" specifically. Don't want to arrow-key through 200
lines.

**Current state:** **Not supported.** Falls back to
NVDA's built-in find functionality (NVDA+Ctrl+F in some
configurations) or the user re-runs the command piped to
`findstr` / `grep`.

**Phase 2 candidate:** in-app search affordance —
`Ctrl+F` opens a modal search box; results presented as a
navigable list ("Failed: line 47", "Failed: line 89");
selecting a result moves the review cursor to that line.

### W6 — Claude Code roundtrip (the primary use case)

**User goal:** Type a question; hear Claude's streaming
response, line by line; know when Claude is "thinking"
vs "done"; be able to interrupt or follow up; navigate
back through prior responses for reference.

**Current state:** Substrate exists (Stage 4.5 alt-screen
+ DECCKM via Stage 6). End-to-end roundtrip is **Stage 7**
— not yet shipped.

**Open issues:** Claude Code uses Ink (React-style TUI).
Ink does full-screen redraws + cursor-positioning; the
"current line" concept blurs because the WHOLE screen
might be conceptually one logical region. Worth special-
casing: detect Ink-shaped output and switch to a different
announcement strategy (e.g. announce the whole new state
instead of per-line diffs).

**Lack of precedent:** no existing terminal emulator has
solved screen-reader-friendly Claude Code interaction.
We're inventing this.

### W7 — Multi-line input

**User goal:** Compose a multi-line message / script /
heredoc. Want to navigate within the in-progress block,
edit prior lines, then submit when ready.

**Current state:** Shell-dependent. cmd.exe: not really
supported. PowerShell + PSReadLine: rich multi-line edit.
bash + readline: vi/emacs mode multi-line.

**Phase 2 candidate (Pattern C):** modal "compose"
dialog with standard text-edit UIA semantics. User
presses a hotkey (e.g. `Ctrl+Shift+M`?), edits in a
familiar text-area-shaped surface, submits to the
shell as a paste. Bypasses shell-specific multi-line
quirks; consistent UX across all shells.

### W8 — Paste content safely

**User goal:** Copy a snippet from a doc; paste into
terminal; know what was pasted; be confident it didn't
include malicious escape sequences.

**Current state:** Stage 6 PR-B wires Ctrl+V → bracketed-
paste-aware encoder with `\x1b[201~` stripping for
injection defence. **Working.**

**Phase 2 candidate:** "review before paste" modal —
display the pasted content in a dialog; user presses
Enter to commit or Esc to cancel. Especially useful for
security-sensitive sessions (e.g. running an ssh command
where a paste could include malicious content).

### W9 — Switch between apps fluidly

**User goal:** Alt+Tab to a docs page, find a command
syntax, Alt+Tab back to pty-speak; continue typing
naturally.

**Current state:** WPF focus events fire normally;
pty-speak's input cursor is right where it was;
keystrokes resume. **Working visually.** Audibly:
NVDA will announce "pty-speak terminal" on app refocus
(default NVDA behaviour for window focus); doesn't tell
the user where the cursor is in the terminal context.

**Open issues:** a "you're back; cursor is on prompt"
cue could help. Tied to W4's caret-tracking fix —
once `GetSelection` returns the cursor position, NVDA's
on-focus content read may pick up the right place.

### W10 — Recover from confusion

**User goal:** "Wait, where am I? What was the last
thing that happened? Is this still my terminal?"

**Current state:** NVDA review cursor + W4 navigation
help. NVDA+T reads the window title.

**Phase 2 candidate:** "tell me current state" hotkey
that announces a structured status: "Pty-speak.
Current shell: cmd.exe. Last command: dir. Cursor at
prompt. Output buffer: 47 lines available for review."
One-shot, NVDA-friendly recovery cue.

### W11 — Discover features

**User goal:** "What hotkeys does pty-speak provide?
What can I do with it?"

**Current state:** README + USER-SETTINGS.md document
the hotkeys; no in-app discovery.

**Phase 2 candidate:** `Ctrl+Shift+?` (or similar)
announces a structured summary of available hotkeys.
Like a "command palette" but audible.

### W12 — Configure pty-speak to my preferences

**User goal:** Adjust speech rate hints, font size,
log verbosity, NVDA voice settings.

**Current state:** Hardcoded everything. Phase 2's TOML
config substrate (catalogued in
[`USER-SETTINGS.md`](USER-SETTINGS.md)) is the path.

### W13 — Report a bug, paste a log

**User goal:** Something went wrong; capture diagnostics
and send to a maintainer / contributor.

**Current state:** Logging shipped (PR #102; logs at
`%LOCALAPPDATA%\PtySpeak\logs\`). PR #103 in flight to
add per-session files + `Ctrl+Alt+L` clipboard copy for
one-keypress sharing.

### W14 — Interrupt a stuck process

**User goal:** Run a long command, realise it's hung,
press Ctrl+C, have it actually interrupt.

**Current state:** Stage 6 wires Ctrl+C through the
encoder as 0x03 (SIGINT). **Working** for cmd.exe.

**Open issues:** if pty-speak's UI is ALSO hung (e.g. a
WPF dispatcher freeze), Ctrl+C might not reach the
encoder. Independent problem; Phase 2 monitoring.

## Lack of existing precedent

This section is the maintainer's specific request: **honestly
acknowledge what no one has solved yet** and where pty-speak
is doing original work.

### What HAS been done

- **Sighted-developer terminal emulators**: solved for
  decades. xterm, Windows Terminal, iTerm2, Alacritty,
  WezTerm — visual rendering of VT escape sequences is
  well-understood.
- **Screen-reader-aware text editing**: standard.
  TextBoxes in WPF / Cocoa / GTK have decades of
  accessibility infrastructure.
- **Web browser accessibility**: well-developed. NVDA's
  browse mode, ARIA live regions, semantic HTML — mature
  ecosystem.

### What has been PARTIALLY done

- **Windows Terminal + NVDA**: NVDA detects the
  `TermControl2` class name and applies terminal-specific
  heuristics. Works for streaming output via UIA's
  `TextChanged` event (which NVDA only enables for
  recognised terminal classes — pty-speak doesn't have
  this signal yet, which is one of our follow-ups).
  Still imperfect for many of W1-W14 above.
- **iTerm2 + VoiceOver (macOS)**: bespoke accessibility
  layer with VoiceOver-specific routing. Different
  platform; can't directly port.
- **emacspeak (Emacs)**: predates UIA entirely.
  Character-level speech feedback inside Emacs's input
  loop. Single-app paradigm; not a terminal emulator
  but a screen-reader-aware editor that happens to
  include terminal modes.
- **`speakup` (Linux console screen reader)**: kernel-
  level character-level speech. Different paradigm
  (frame-buffer console, not pseudo-console).
- **NVDA's third-party "Terminal" addon**: changes how
  NVDA handles terminal-shaped content. Worth studying
  but addresses NVDA's side, not the terminal's.

### What has NOT been done (where we're inventing)

- **Streaming output via `RaiseNotificationEvent` with
  per-`activityId` policy choices.** Stage 5 was a
  deliberate design experiment; the literature on which
  `NotificationProcessing` mode to pick for high-frequency
  terminal output is essentially nonexistent.
- **Coalescer-with-frame-hash for spinner / redraw
  suppression in NVDA notifications.** The spec for
  Stage 5 cited it as a guess at a reasonable heuristic;
  no prior implementations exist for terminal output.
- **Caret-tracking via `ITextProvider.GetSelection` for
  PTY cursor position.** Implementations exist for text
  editors, but the terminal case (where the caret moves
  due to escape sequences, not direct user navigation) is
  an open design question.
- **Modal break-outs from terminal flow** (Pattern C).
  Not a thing in any existing terminal emulator. We're
  considering it specifically because the screen-reader-
  fluent path may diverge from the sighted-fluent path.
- **Screen-reader-friendly Claude Code interaction.**
  Ink's full-screen TUI is the test case for Stage 7;
  no terminal emulator has shipped a documented solution.
  This is also where pty-speak's user value is highest —
  blind developers using Claude Code is the primary use
  case the project exists to serve.
- **A "command complete" audio cue.** Some shell prompts
  emit `\a` (bell) as a completion hint; NVDA doesn't
  forward terminal bells distinctively. Stage 8 will
  invent the right signal.
- **Custom review mode (Pattern D)** — distinct from
  NVDA's browse mode but specific to terminal scrollback
  semantics. Stage 10 reservation.
- **Per-shell behaviour adaptation.** No terminal emulator
  meaningfully adapts to the spawned shell beyond a
  per-shell colour scheme. We've started this with Ctrl+L
  → cmd.exe-specific `cls\r`; a fuller framework is Phase
  2+.

### What's hard about inventing

- **No NVDA conformance test suite for terminals.** We
  rely on the maintainer's lived experience as a blind
  developer plus xUnit pinning of the producer-side
  contracts (Stage 5 coalescer tests, Stage 6 keyboard
  encoding tests, etc.). Audio-side correctness is
  manual.
- **Iteration cycle is slow.** "Did NVDA actually read
  this?" requires a Windows machine + NVDA install + a
  human listener. Not friendly to CI-driven development.
- **The right design depends on the user's NVDA settings.**
  Speech rate, "speak typed characters" on/off, modifier
  key choice, voice — all change what the right pty-speak
  behaviour looks like. We can document recommendations
  but can't standardise.
- **Sighted contributors can't easily verify changes.**
  The audio-output observation is qualitatively different
  from visual-output observation. A sighted reviewer can
  read the screen text but not assess whether the speech
  felt natural. Maintainer + community of blind-user
  testers is the long-term path.
- **The blind-developer-using-terminal user community
  is small but growing.** Each shipped preview feeds back
  into the next stage's design. Long feedback loop.



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
