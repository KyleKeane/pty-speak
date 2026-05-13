# ADR 0003 — Shell interaction is modelled as a state machine over user-input vs cmd-output, not as a byte-stream announce pipeline

- **Status**: Accepted / Implemented (2026-05-13)
- **Date**: 2026-05-13 (Proposed)
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 48 PR-A, in response to the
  failed dogfood trail PR #299 → #300 → #301 → #302 → #303 →
  #304 → #305 that left the maintainer reporting "typed
  characters being announced by PTY" + "occasional multi-
  character cluster spoken as a word if I type quickly" +
  "the underlying document that I can navigate with the NVDA
  review cursor has drifted substantially from the actual
  content on the screen" in preview.117 dogfood. The
  resulting frustration ("I have lost an operational model
  of what you have built and can no longer conceptualize how
  to help you get out of this situation") prompted the
  re-orientation captured here.
- **Companion docs**:
  - [`0001-substrate-channel-dichotomy.md`](0001-substrate-channel-dichotomy.md)
    — substrate vs. channel framing. This ADR adds a third
    framing on top: **interaction** as a semantic layer
    distinct from substrate (history) and channel (NVDA
    speech / earcons).
  - [`0002-uia-textedit-caret-output.md`](0002-uia-textedit-caret-output.md)
    — the channel-side decision. Its conclusions about UIA
    Edit-control type, caret events, and the substrate-swap
    of the UIA Text-pattern view stay correct. ADR 0003
    keeps that channel; it changes **what feeds the channel**.
  - [`../CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md)
    — names ContentHistory + SpeechCursor as the substrate.
    ADR 0003 adds **ShellInteraction** as the semantic layer
    above the substrate and below the channel.

## Context

### Where the existing model stops working

`pty-speak`'s pipeline today is:

```
PTY stdout bytes
  → VtParser (typed VtEvents)
  → Screen (2-D grid, what a terminal emulator would render)
    + ContentHistory (linear append-only log: TextSpan / Newline / Overwrite / Marker)
  → SessionModel (tuple boundaries via HeuristicPromptDetector regex on screen rows)
  → Announce paths (tuple-final + idle-flush + SpeechCursor manual + UIA Text-pattern review)
  → NVDA channel (RaiseNotificationEvent + UIA caret) and Earcon channel (WASAPI)
```

The substrate (ContentHistory) is a **byte-level log of
everything that crossed the wire**. It does not distinguish:

- a character the user just typed that cmd echoed back to
  stdout (an *echo* byte),
- a character cmd produced as part of running a command (a
  *real-output* byte), or
- a line cmd printed because it is internally asking for
  more input via stdin (a *sub-prompt* byte — `set /p`,
  `pause`, interactive script asking yes/no).

All three look identical at the byte layer. cmd does not
emit OSC 133 boundary sequences, so the heuristic regex on
screen rows (`HeuristicPromptDetector`) is the only signal
that distinguishes "the shell is at a fresh PS1" from
everything else. That regex fires once per shell-prompt
transition; it does not fire on sub-prompts, mid-stream
output, or typing-into-the-prompt echo.

Every announce gate I have shipped in Cycle 47 has been an
attempt to recover this missing semantic information *from
the byte stream alone*, after the fact:

- **PR #299** — `sanitiseForBundle` preserves newlines so
  the diagnostic bundle reads as multi-line.
- **PR #300** — typing-window gate on the UIA Text-pattern
  materialiser excludes the active TextSpan when a keystroke
  is < 350 ms ago, on the theory that NVDA's polling would
  otherwise see mid-keystroke deltas as inserted text.
- **PR #301** — tuple-final announce trims any prefix
  matching `lastAnnouncedText` to suppress `set/p` replay.
- **PR #302** — `ReadyForInput` earcon dispatched from
  idle-flush so `set/p` produces an audible cue.
- **PR #303** — top-level menu mnemonic conflict fixes
  (unrelated; clean).
- **PR #305** — `ContentHistory.LogicalCursorRow` tracking
  + cursor-row-change synthetic Newline so cmd's CSI-
  positioned banner rows render as separate lines. Revoked
  PR #302's idle-flush ReadyForInput dispatch (it fired per
  keystroke). Threaded `LastKeystrokeAtUtc` from
  `TerminalView` into the idle-flush timer.

The 350 ms typing-window gate from #300 and #305 fails
under the maintainer's actual typing cadence (~600–800 ms
between keystrokes when using a screen reader). preview.117
log shows idle-flush firing `MsgHead=e`, `MsgHead=cho`,
`MsgHead=h`, `MsgHead=i` because each window expires
between keypresses. There is no smaller threshold that
fixes this without breaking `set/p` detection; there is no
larger threshold either, because idle-flush by construction
fires *after* an idle period and any reasonable "still
typing" window is shorter than its own idle threshold.

The maintainer's preview.117 review names the load-bearing
issue directly:

> I really feel like we are stuck in unnecessarily detailed
> fiddling with specific NVDA quirks rather than focusing on
> the core infrastructure which should be extracting a
> semantic and structured computational representation of
> the information you were getting back from the shell and
> making an informed and intelligent decision about how to
> present that information to the user.

ADR 0003 names that semantic representation and pins the
state machine that drives it.

### The architectural mismatch in one sentence

**We model the byte stream; we should model the
interaction.** Every gate, prefix-trim, sealed-only view,
and cursor-row newline synthesis is downstream of this
mismatch. None of them can succeed in isolation because
they are trying to recover information that the byte
stream does not carry.

### Test corpus this ADR must handle

`Diagnostics → CMD Interaction Tests` ships eight scripts
in `scripts/cmd-tests/`. Each represents an interaction
pattern that the announce layer must handle correctly:

| Test | Script behaviour                                        | Pattern                                                 |
|------|---------------------------------------------------------|---------------------------------------------------------|
| 01   | `echo Line 1 of 8 ... Line 8 of 8`                      | Simple shell command, multi-line output.                |
| 02   | `set /p name=Enter your name:` then `echo Hello %name%` | Sub-prompt text input.                                  |
| 03   | `set /p n=Number? :` then arithmetic on `%n%`           | Sub-prompt with computation.                            |
| 04   | `set /p ans=Y/N? :` with branching                      | Sub-prompt with constrained answer.                     |
| 05   | `choice /c YNAE` (4-way choice)                         | Sub-prompt via `choice` (single keystroke, no Enter).   |
| 06   | `pause`                                                 | "Press any key to continue . . ." sub-prompt.           |
| 07   | `for /L %i in (1,1,10) do (echo Step %i & ping -n 2 localhost > nul)` | Streaming output across 20 s. |
| 08   | `echo error >&2`                                        | Stderr-colored line (red).                              |

A full-featured interaction model must produce sensible
spoken output for every one of these without per-test
heuristics. Tests 02–06 are the cases that the current
idle-flush + tuple-final pipeline cannot handle without
patching, because each requires distinguishing
"sub-prompt waiting for input" from "shell command
finished" from "typed-char echo" — three things the byte
stream collapses together.

### Why incremental patches keep failing

Three symptoms recur across the dogfood trail:

1. **Per-character chatter.** Idle-flush slices single-byte
   chunks (`e`, `c`, `h`, `o`) and Announces them. Because
   the substrate doesn't tag echo bytes, every cmd-echoed
   keypress is a candidate for announcement and the gate
   that suppresses it must fire within a window shorter
   than the user's typing pace.
2. **Set/p replays the prompt text.** Idle-flush announces
   "Enter your text:" before the user types; the tuple-
   finalise announce then says "Enter your text: foo"
   afterwards. The text watermark (#301) is a prefix-match
   heuristic that fires only when the strings match
   verbatim; cmd reformats the line subtly in some flows
   and the trim doesn't fire.
3. **The review cursor drifts from the screen.** PR #300's
   typing-window suppression excludes the active TextSpan
   from the UIA Text-pattern view while typing, so the
   review cursor lags the visual cursor by ≥350 ms.
   Reverting it brings back NVDA polling reading mid-
   keystroke deltas (the bug #300 was supposed to fix).

Each fix introduces or reveals another. The root cause is
shared: pty-speak doesn't know the *meaning* of each byte
it receives, so it can only guess.

## Decision

Introduce **`ShellInteraction`** — a state machine that
sits above ContentHistory / Screen / SessionModel and below
the announce layer. It encodes the semantic state of the
shell session as one of a small number of well-defined
states, and drives announce, earcon, and review-cursor
decisions from state transitions instead of from byte-
stream deltas.

### Conceptual model

The state machine has **two states** and an explicit
transition table. A two-state design is deliberate: each
additional state adds combinatorial complexity in the
transition table and the announce-routing decisions, and
the two we keep are the only distinction the user actually
needs.

```
type InteractionState =
    | Composing   // pty-speak is waiting for the user.
                  // cmd output is treated as ECHO; not announced
                  // and not preserved as "real output" for
                  // tuple finalisation. The user's input goes
                  // into UserInputBuffer and (separately) to PTY
                  // stdin. NVDA's keyboard hook handles per-char
                  // typing announce per the user's NVDA settings.
                  //
    | Executing   // cmd is doing work for us.
                  // cmd output is REAL OUTPUT; it accumulates
                  // and gets announced (per ShellPolicy verbosity)
                  // and gets routed into ContentHistory as
                  // semantically-tagged content.
```

**Composing** subsumes three byte-level cases that look
identical today:

- The user is at a fresh shell prompt (`PS1`), typing a
  command.
- The user is at a sub-prompt (`set /p`, `pause`, `choice`),
  typing the answer or pressing any-key.
- The user is at a Python REPL `>>> ` or `... ` (future
  shells).

What unifies them: **cmd's output is the echo of what the
user is typing right now.** Announcing it would duplicate
what NVDA's keyboard hook already does. So we suppress.

**Executing** subsumes:

- A regular command is running and emitting output (e.g.
  `dir`, `echo hi`, `ping -n 5`).
- A command has been submitted and cmd is producing the
  next prompt's bytes before the prompt detector fires.
- An interactive command is between its sub-prompts (e.g.
  `set /p` has received its line but hasn't yet emitted
  the next sub-prompt).

What unifies them: **cmd's output is not echo of user
input.** Announce it.

### Transition table

```
                 Composing  Executing
Composing → ─────[a]─────→ ✓
Executing → ←─────[b]──── ←─────[c]──── ✓ (alt-screen: see §5.4)
```

#### [a] Composing → Executing

Trigger: **the user submitted input.** Concretely, a
keyboard event corresponding to "Enter" (or `choice`'s
single-key submission) was processed by `TerminalView` and
the line-encoder wrote a submit byte (typically `0x0D`) to
PTY stdin.

For Composing → Executing we DO NOT wait for cmd to
acknowledge. We transition on the keypress, before any
echo comes back. This is the only way to silence the
echo of Enter itself.

Carry-over data: the contents of `UserInputBuffer`
(everything the user typed in this Composing burst) is
captured as the submitted command text. The buffer
clears.

Special-case: `choice /c` and `pause` submit on a single
keypress without Enter. The encoder for those flows
(detected via the prior sub-prompt's content — `Press any
key to continue . . .` or `[Y,N,A,E]?`) treats any keypress
as a submit. This is a per-sub-prompt encoder decision; the
state machine sees a normal Composing → Executing
transition driven by "the encoder said so."

#### [b] Executing → Composing via shell-prompt detection

Trigger: **`HeuristicPromptDetector` emits a
`PromptStart` boundary.** A shell-style prompt row
materialised on the screen — cmd is back at PS1.

Carry-over data: the bytes that accumulated during
Executing are flushed as the command's output. The
announce layer fires `tuple-final` for that output (per
`ShellPolicy.Streaming`). The `ReadyForInput` earcon
plays.

Replaces today's `SessionModel.applyAndCapture` →
finalisedTuple → `Tuple-final announce` path.

#### [c] Executing → Composing via sub-prompt detection

Trigger: **cmd has been idle for ≥ `idleThresholdMs` AND
the last byte produced is not `\n` (LF) AND
HeuristicPromptDetector did not fire during this idle.**

The "last byte not `\n`" heuristic is the key. It
distinguishes:

- "cmd just printed a line of output and is about to
  print the next" — the last byte IS `\n`. Stay in
  Executing. Streaming-line announce (per
  `ShellPolicy.LineByLine`) may fire if the policy says so.
- "cmd just printed `Enter your name:` and is waiting" —
  the last byte is `:` (or `?` or `]` or space). The
  cursor is mid-line. The shell is waiting for stdin.
  Treat as a sub-prompt. Fire `tuple-final` announce of
  the accumulated output (which IS the sub-prompt text)
  and the `ReadyForInput` earcon.

`idleThresholdMs` becomes a per-shell setting (today's
`ShellPolicy.IdleFlushMs`). The default for cmd stays at
350 ms because shorter spans incorrectly fire mid-stream.

`pause` (test 06) and `choice` (test 05) work because cmd
in those modes ends its prompt without `\n` —
`Press any key to continue . . . ` ends with space-dot-dot-
dot-space, no newline. `[Y,N,A,E]?` likewise.

### The `UserInputBuffer`

`UserInputBuffer` is the **canonical record of what the
user has typed** in the current Composing burst. It is
maintained by `TerminalView`'s keyboard handler at the
moment of keypress, **not** reconstructed from PTY echo
bytes.

```
type UserInputBuffer = {
    mutable Chars : ResizeArray<char>     // the in-progress line
    mutable CursorIndex : int              // where the cursor is within Chars
}
```

Operations:

- **Regular char (letters, digits, symbols, space)**:
  insert at `CursorIndex`, advance `CursorIndex` by one.
- **Backspace**: remove char at `CursorIndex - 1`, decrement
  `CursorIndex`. If `CursorIndex` already 0, no-op.
- **Left/right arrow**: move `CursorIndex`. Buffer
  unchanged.
- **Home/End**: jump `CursorIndex` to 0 / `Chars.Count`.
- **Delete (forward)**: remove char at `CursorIndex`. No
  index change.
- **Enter**: capture full `Chars` as the submitted command;
  clear buffer; `CursorIndex := 0`. Triggers transition [a].
- **Ctrl+C / Ctrl+\\\\ / signal keys**: clear buffer; pty-
  speak writes the signal byte to PTY. State follows the
  signal: if cmd interrupts and returns to PS1, [b]
  eventually fires.
- **Up/Down (history navigation)**: complex. Today cmd
  handles command history server-side via its own buffer;
  arrow keys are encoded as `ESC[A` / `ESC[B` and cmd writes
  the recalled line over the prompt. Our `UserInputBuffer`
  has no way to know what cmd recalled. **MVP**: after the
  history-navigation byte sequence is sent, we read the
  cmd input row from `Screen` (between the prompt path and
  the cursor) and replace `UserInputBuffer.Chars` with that
  text. Detailed design in §5.5.
- **Tab (completion)**: send tab to PTY; cmd may emit a
  completion. Same `Screen`-row resync as for history.
- **Paste**: insert pasted text into `UserInputBuffer`; send
  the text bytes to PTY. If pasted text contains `\r` or
  `\n`, treat each as a submit and trigger transition [a]
  for that line.

### What the buffer is used for

1. **Knowing what command was submitted** when transition
   [a] fires. The captured `Chars` is the `CommandText` for
   the resulting tuple. Cleaner than today's screen-row
   extraction (which can include the user's edits / BS / re-
   types as garbage characters).
2. **Driving the UIA review cursor's "current input line"**
   so the maintainer can navigate to and read what they're
   currently typing in a clean form, not the active-span
   raw byte stream with backspace-encoded edit history.
3. **Future: autocomplete + interjections.** The buffer is
   the canonical text pty-speak proposes completions
   against. Completions are inserted into the buffer (and
   sent to PTY) the same way pasted text is.

### What the buffer is NOT used for

- **Byte-level echo matching against PTY stdout.** We
  considered a design where every PTY-stdout byte is
  matched against the head of `UserInputBuffer` to tag echo
  versus real output. We rejected it because cmd's echo of
  a typed character is not a clean 1-to-1: cmd may echo
  `\b \b` for a backspace, `\r\n` for an Enter, and
  multi-character escape sequences for history navigation.
  A byte-matching approach would need a state machine of
  its own (the "echo machine") just to track what cmd
  *should* echo for each input key, and that state machine
  is exactly the complexity ADR 0003 is trying to
  consolidate.
- **Determining `Composing` versus `Executing`.** The state
  is determined by transition triggers (Enter sent, prompt
  detected, idle-with-no-trailing-newline), not by buffer
  contents.

### Signal sources by shell

The InteractionState DU is universal. The signals that
drive transitions are shell-specific. Each shell adapter
provides a small set of detectors:

#### cmd

- `Enter sent` (transition [a]): `TerminalView` keyboard
  handler emits `0x0D` to PTY → fires transition.
- `HeuristicPromptDetector PromptStart` (transition [b]):
  unchanged.
- `Idle-with-no-trailing-LF` (transition [c]): new detector.
  Watches `ContentHistory.appendFromEvent` outputs. When
  cmd has been idle ≥ `idleThresholdMs` AND the most recent
  TextSpan / active-span tail does not end in `\n`, fire.

#### claude (OSC 133)

- `Enter sent` (transition [a]): as cmd, plus OSC 133 `B`
  (CommandStart) is an explicit confirmation; we transition
  on whichever fires first.
- `OSC 133 A` (PromptStart) (transition [b]): the shell
  emitted an explicit prompt-start marker.
- `OSC 133 D` (CommandFinished) (transition [b]): the
  shell emitted an explicit command-finished marker.
- Sub-prompt detection (transition [c]) is unnecessary for
  claude; the OSC 133 protocol covers boundaries.

#### PowerShell (Windows PowerShell / pwsh)

PSReadLine performs aggressive line editing via cursor
moves and partial-line redraws. Echo bytes mix with prompt
re-rendering. Today's `HeuristicPromptDetector` regex
matches the default PS1; the same regex drives transition
[b]. Sub-prompt detection [c] is more fragile because
PSReadLine may emit short bursts of bytes for inline
suggestions that look like "cmd is idle waiting for
input." Mitigations:

- Use a longer `idleThresholdMs` for PowerShell (~800 ms
  vs 350 ms for cmd).
- Treat any `CsiDispatch` for cursor-up / save-cursor
  during the idle as a "suggestion redraw, not a sub-
  prompt" hint.

PowerShell support is **not in scope for the initial
implementation**. cmd + claude first; PowerShell follows
in a separate cycle once the model is shaken out.

#### Python REPL / arbitrary REPLs

Deferred. The model accommodates them (any prompt that
ends without `\n` and is stable ≥ idle threshold is a
sub-prompt) but no explicit signal source is built. The
heuristic-only fallback is acceptable.

### Announce routing

Today's announce paths are unified by ADR 0003 as
follows:

#### Tuple-final announce (the only "automatic" speech path)

- **When**: on transition [b] OR [c].
- **What**: the bytes accumulated during the just-finished
  Executing window, sanitised + capped at
  `OutputAnnounceCapChars` (today's 800).
- **How**: `TerminalView.Announce(text, ActivityIds.output,
  MostRecent)`. Identical channel to today's tuple-final.
- **Why one path**: a sub-prompt and a finished command are
  the same UX event from the user's perspective — "cmd
  said something and now wants me to act." Treating them
  identically in the channel collapses the
  set/p-replay edge case to a non-issue (there is no
  separate idle-flush competing for the same content).

#### Streaming-line announce (optional, per `ShellPolicy.LineByLine`)

- **When**: during Executing, when a complete line lands
  (most-recent byte was `\n`) AND `ShellPolicy.Streaming =
  LineByLine`.
- **What**: the freshly-completed line text.
- **How**: `TerminalView.Announce` with `Polite` processing
  (don't displace the queued tuple-final).
- **Why**: tests 07 + 08 (long-running, streaming) need
  per-line readout. The bare tuple-final path holds the
  whole output until the next PS1, which can be 20+
  seconds for a ping loop.

The default `ShellPolicy.Streaming` for cmd remains
`TupleFinalOnly` (today's behaviour). LineByLine is opt-in
via the existing `Display → Output Verbosity → Line By
Line` menu item.

#### `ReadyForInput` earcon

- **When**: on transition [b] OR [c]. Same as tuple-final.
- **What**: the existing 3000 Hz × 15 ms click via
  EarconChannel.
- **Why**: an audible "you can type now" signal that does
  not interrupt the tuple-final speech (it plays on a
  separate WASAPI stream).
- **Replaces**: PR #302's `Producer=idle-flush`
  ReadyForInput dispatch (which fired per-keystroke and
  was reverted in #305). The new dispatch only fires on
  state transitions, never on byte-level idle.

#### `BellRang`, `ErrorLine`, `WarningLine` earcons

Unchanged. Driven by VtEvents (`Execute 0x07` for BEL) and
row-color detection (Cycle 8d.2 territory, deferred).

#### Idle-flush

**Removed.** Its two jobs (mid-stream announce + sub-prompt
detection) are subsumed by streaming-line announce and
transition [c] respectively. The `Tick`-based timer in
`Program.fs` no longer fires `Announce`; it remains as
plumbing for the sub-prompt detector + the
`ContentHistory.tick` staleness sweep.

### UIA review cursor view

The UIA `ITextProvider.DocumentRange` materialises a
view that the NVDA review cursor navigates. Today it's
`ContentHistoryMaterialiser.materialise` →
`tailTextWithMarkers` (or `tailTextWithMarkersSealedOnly`
during typing windows per PR #300).

Under ADR 0003 the materialised view is composed of:

1. **History** — `ContentHistory` entries up to the
   current state's start. Markers (`begin/end prompt`,
   `begin/end output`) remain navigable. PR #305's
   cursor-row synthetic newlines remain (separate visual
   rows render as separate lines).
2. **Current input line** — when state is `Composing`,
   substitute `UserInputBuffer.Chars` as the active line
   AFTER the most recent prompt path. The user navigating
   to the bottom of the document sees their cleanly-typed
   command, not the active TextSpan's raw byte stream.
3. **Current executing output (so far)** — when state is
   `Executing`, render the bytes accumulated so far. This
   may include incomplete lines (cursor mid-line); the
   review cursor sees them as they are.

The typing-window suppression from PR #300 is **removed**.
The active span is always included for review-cursor
purposes, but during Composing it's replaced by the
`UserInputBuffer`'s cleaner content. NVDA's polling sees a
stable view across typing because the buffer changes only
on keystrokes (which are user-initiated, not byte-driven),
so each NVDA poll observes the user's most-recent intent
rather than the cmd-echo-stream-at-this-millisecond.

### Where ContentHistory + Screen + SessionModel fit

`ContentHistory` **stays** as the substrate-level log of
the byte stream. It feeds:

- The diagnostic bundle's `--- CONTENT HISTORY ---`
  section.
- The UIA review-cursor's "history" portion of the
  materialised view (component 1 above).
- `SpeechCursor` manual navigation (`Ctrl+Shift+Up/Down/End`).

`Screen` **stays** as the visual grid. It feeds the
WPF drawing, the heuristic prompt detector, and the
sub-prompt-detector's "last byte was/wasn't \n" check.

`SessionModel` is **mostly retired** for cmd. Its tuple
extraction (CmdText + OutputText from screen rows) is
replaced by `UserInputBuffer` + the Executing-window
accumulator. Its state machine
(`AwaitingCommandStart` / `EditingCommand` / etc.) is
superseded by `InteractionState`. The `SessionTuple`
record stays as the persistence + history-replay vehicle —
each Executing → Composing transition emits a finalised
tuple — but the state-machine fields collapse to the
two-state `InteractionState` value.

For OSC 133 shells (claude), `SessionModel`'s richer
state machine remains useful because the shell provides
the detail. The cmd adapter is the simpler form.

## Consequences

### Positive

- **One announce path per audible event.** Tuple-final
  fires on Executing → Composing, regardless of trigger.
  No competing idle-flush. No prefix-trim watermark
  reconciliation. The `set/p` replay class of bug
  disappears by construction.
- **Per-character chatter goes away.** Bytes received
  during Composing are echo by definition (state-based
  classification). They are not announced. No threshold
  to tune; no race between idle-flush and keystroke
  timing.
- **Review cursor matches the screen.** The materialised
  view is built from the buffer and the accumulator, both
  updated synchronously with user keystrokes and cmd
  output. NVDA polling sees a coherent view at every
  observation.
- **The test corpus 01–08 maps cleanly to state
  transitions** (full mapping in §4 below). No
  per-test heuristic.
- **Backspace edit history is invisible to the announce
  path** because the buffer never contains BS — it
  represents the *current line* at any moment. The
  review-cursor "current input line" shows clean text.
- **Future autocomplete + interjections become first-class
  modulations of the buffer**, not byte-stream interventions.
- **The model generalises to other shells.** OSC 133
  shells use richer signals to drive the same transition
  table. PowerShell, bash, Python REPL — each provides its
  own signal sources behind a small adapter.
- **The model collapses several ad-hoc gates** (PR #300's
  typing-window materialiser gate, PR #301's prefix trim,
  PR #305's idle-flush typing-window gate) into one
  semantic state. Less code, less surface for regressions.

### Negative / costs

- **One-time refactor cost**: 5–7 PRs (see §6), spread
  across substrate, state machine, announce wiring, UIA
  materialiser, and a closure audit. Bigger than any
  single Cycle 47 patch was.
- **State-machine bugs replace gate bugs.** If the wrong
  transition fires, we get a new failure mode that no
  Cycle-47 patch could have produced. Most likely
  candidates: heuristic prompt detector misfires
  (transition [b] fires inside the user's typing burst);
  sub-prompt detector false-positives on a streaming
  pause (transition [c] fires mid-`ping`); the
  Enter-detection in `TerminalView` misses a key
  combination (Composing → Executing never fires, every
  cmd response treated as echo).
- **Async cmd output during Composing is silenced by
  default.** Rare in cmd (cmd doesn't have async jobs);
  more common in PowerShell. Acceptable for cmd; PowerShell
  needs a follow-up (background-job detector).
- **Sub-prompt detection depends on a heuristic** (last
  byte not `\n`). It's robust for set/p / pause /
  choice but degrades for shells that end sub-prompts
  with `\n` for stylistic reasons. Document as a known
  failure mode; revisit with OSC 133 adoption.
- **UserInputBuffer can drift from cmd's actual command-
  line content** during history navigation (up/down
  arrows), tab completion, paste with special chars, etc.
  The §5.5 Screen-resync mitigates but isn't perfect. If
  the buffer drifts, the captured `CommandText` at
  transition [a] is wrong; the actual command runs
  correctly (cmd uses its own copy), but the audible
  description of "you ran X" is wrong.

## Detailed design

### §4. Test corpus mapping

Each of the eight CMD interaction tests is walked through
the state machine to demonstrate that the announce output
matches what the maintainer expects to hear.

Notation:

- `[user types ...]` — keystrokes processed by `TerminalView`.
- `[user presses Enter]` — `0x0D` written; transition [a].
- `[heuristic fires]` — `HeuristicPromptDetector` emits
  PromptStart; transition [b].
- `[idle ≥ 350 ms, last byte not \n]` — transition [c].
- `→ Speak: "..."` — `Announce` is called with this text.
- `→ Earcon: ready-prompt` — `ReadyForInput` dispatched.

#### Test 01 — Simple Echo

```
[banner arrives]              State: Composing (initial)
[heuristic fires]             → Speak: "C:\Users\...\current>"
                              → Earcon: ready-prompt
                              (entered Composing fresh)
[user types "echo Line"...]   Buffer fills. Echo bytes from cmd
                              arrive in Composing → suppressed.
[user presses Enter]          State → Executing.
                              Buffer captured: "echo Line 1 of 8 ..."
                              Buffer cleared.
[cmd outputs 8 lines]         Bytes accumulate in Executing window.
                              LineByLine: each line announced as it
                              completes. TupleFinalOnly (default):
                              held for transition [b].
[heuristic fires]             → Speak: <8-line accumulated output>
                              (capped at 800 chars)
                              → Earcon: ready-prompt
                              State → Composing.
```

Audible: the 8-line block, then a click. Pre-fix the
maintainer also heard each typed character + per-line
idle-flush of cmd's echo of the input. Post-fix: silent
during typing; one announce at the end.

#### Test 02 — Text Input (set /p)

```
[at shell prompt]             State: Composing.
[user types "set /p name="
 "Enter your name:"]          Buffer fills. Echo suppressed.
[user presses Enter]          State → Executing. Buffer captured.
[cmd echoes Enter as \r\n]    \r\n accumulates in Executing
                              (treated as "real output" — no echo
                              suppression in Executing). Trailing
                              whitespace stripped at announce time.
[cmd writes "Enter your name:"
 then waits]                  No \n at end. Idle ≥ 350 ms passes.
[transition [c] fires]        → Speak: "Enter your name:"
                              → Earcon: ready-prompt
                              State → Composing.
[user types "Kyle"]           Buffer fills. Echo suppressed.
[user presses Enter]          State → Executing. Buffer captured: "Kyle".
[cmd echoes Enter + writes
 next line "Hello Kyle!"
 + writes next prompt]        Bytes accumulate.
[heuristic fires]             → Speak: "Hello Kyle!" (or similar)
                              → Earcon: ready-prompt
                              State → Composing.
```

Audible: prompt path, prompt query, then result + click.
Pre-fix the maintainer heard "Enter your text:" *and*
"Enter your text: Kyle" replayed at tuple-finalise.
Post-fix: each announce fires exactly once and they
correspond to discrete user-facing events.

#### Test 03 — Numeric Input + Calculation

Identical to test 02 with `Number?:` as the sub-prompt and
`set /a` for the computation. State sequence identical.

#### Test 04 — Yes / No Choice

```
[at shell prompt]             Composing.
[user submits "set /p ans=
 Y or N? "]                   → Executing.
[cmd outputs "Y or N? "
 (last byte: space, no \n)]   Idle ≥ 350 ms.
[transition [c] fires]        → Speak: "Y or N?"
                              → Earcon.
                              Composing.
[user types "Y" + Enter]      → Executing.
[cmd runs branch + next prompt] Heuristic → Composing.
                              → Speak: <branch output>.
                              → Earcon.
```

#### Test 05 — Multi-Option Choice (`choice /c YNAE`)

```
[at shell prompt]             Composing.
[user submits
 "choice /c YNAE /m Pick:"]   → Executing.
[cmd outputs "Pick: [Y,N,A,E]?"
 (last byte: ?, no \n)]       Idle ≥ 350 ms.
[transition [c] fires]        → Speak: "Pick: [Y,N,A,E]?"
                              → Earcon.
                              Composing.
[user presses single key]     `choice` consumes the keystroke;
                              the per-sub-prompt encoder treats it as
                              a submit without Enter (recognised by
                              the sub-prompt text containing the
                              `[X,Y,Z]?` pattern).
                              State → Executing.
[cmd writes ERRORLEVEL set +
 next prompt]                 Heuristic → Composing.
                              → Speak: <next prompt path or none>.
                              → Earcon.
```

The single-key submission requires the encoder to detect
`choice`-style prompts. Mechanism: when transition [c] fires,
inspect the sub-prompt text for the regex `\\[\\w(,\\w)*\\]\\?`
and flag the resulting Composing state as
`SinglekeySubmit`. The keyboard handler, when in
`SinglekeySubmit` Composing, treats any non-modifier key as
a submit.

#### Test 06 — Pause / Continue

```
[user submits "pause"]        → Executing.
[cmd outputs "Press any key
 to continue . . . " (no \n)] Idle ≥ 350 ms.
[transition [c] fires]        → Speak: "Press any key to continue . . ."
                              → Earcon.
                              Composing[SinglekeySubmit].
[user presses any key]        → Executing.
[cmd writes next prompt]      Heuristic → Composing.
                              → Speak: <prompt path>.
                              → Earcon.
```

`pause`'s prompt text is detected by the same regex (or
a specific string match) and triggers SinglekeySubmit.

#### Test 07 — Progress Loop

```
[user submits the for-loop]   → Executing.
[cmd outputs "Step 1\r\n"]    Last byte: \n. Transition [c] does NOT
                              fire (the \n means cmd is mid-stream).
                              LineByLine policy: → Speak: "Step 1"
                              (or held silent under TupleFinalOnly).
[cmd waits 2 s for ping]      Still Executing — no transition.
                              ContentHistory.tick may seal active
                              span (for streaming) but the state
                              machine is unaffected.
[cmd outputs "Step 2\r\n"]    Same. LineByLine fires per step.
...
[cmd outputs "Step 10\r\n"]   Last step.
[cmd writes next prompt]      Heuristic → Composing.
                              TupleFinalOnly: → Speak: <10-line
                              accumulated, capped at 800 chars>.
                              LineByLine: tuple-final announce is
                              redundant; suppress if the per-line
                              announces already covered it.
                              → Earcon.
```

For LineByLine the per-line policy is straightforward; for
TupleFinalOnly the user hears nothing during the 20 s loop
and a clipped summary at the end. The maintainer can switch
verbosity via the existing `Display → Output Verbosity`
menu.

#### Test 08 — Stderr Output

```
[user submits "echo error >&2"] → Executing.
[cmd outputs "error\r\n"
 in red]                        VtParser flags the SGR red attribute.
                                Color-detection (Cycle 8d.2, deferred)
                                tags the row as ErrorLine.
                                EarconProfile maps ErrorLine →
                                "error-tone" earcon.
                                → Earcon: error-tone.
                                Text portion accumulates.
[cmd writes next prompt]        Heuristic → Composing.
                                → Speak: "error"
                                → Earcon: ready-prompt.
```

ErrorLine routing is orthogonal to the state machine; the
state machine handles when, the row-color detection handles
what kind of earcon.

### §5. Implementation details

#### §5.1. Concrete F# types

```
namespace Terminal.Core

module ShellInteraction =

    type InteractionState =
        | Composing of ComposingData
        | Executing of ExecutingData

    and ComposingData = {
        EnteredAt : DateTime
        PromptText : string voption          // last prompt path, for redraw / review cursor
        SinglekeySubmit : bool               // true for choice / pause sub-prompts
        Buffer : UserInputBuffer
    }

    and ExecutingData = {
        EnteredAt : DateTime
        SubmittedCommand : string             // the captured buffer at transition [a]
        OutputAccumulator : StringBuilder     // bytes received during this Executing
        OutputLastByteIsLf : bool             // sub-prompt detection helper
        OutputLastByteAt : DateTime           // for the idle threshold
    }

    type UserInputBuffer = {
        mutable Chars : ResizeArray<char>
        mutable CursorIndex : int
    }

    type Transition =
        | EnterPressed                        // → [a]
        | PromptDetected of PromptText : string
                                              // → [b]
        | SubPromptIdle                       // → [c]
        | AltScreenEntered                    // future
        | AltScreenExited                     // future

    type State = {
        mutable Current : InteractionState
        mutable IdleThresholdMs : int
        mutable LastTransitionAt : DateTime
    }
```

Pure: a `tryTransition` function over `(State, Transition)
→ Option<{NewState, AnnounceText, EarconKind}>`. The
adapter layer (`Program.fs`) consumes the result and calls
`Announce` + `EarconChannel.Send` accordingly.

#### §5.2. Sub-prompt detector

A small per-tick observer:

```
let trySubPromptDetect (state : State) (now : DateTime) : Transition option =
    match state.Current with
    | Executing d when
        not d.OutputLastByteIsLf
        && (now - d.OutputLastByteAt).TotalMilliseconds >= float state.IdleThresholdMs
        && d.OutputAccumulator.Length > 0 ->
        Some SubPromptIdle
    | _ -> None
```

Runs from the same `DispatcherTimer` that today drives
`idleFlushTimer`. Tick interval stays 100 ms.

#### §5.3. `TerminalView` keyboard handler integration

The existing `OnPreviewKeyDown` already records the
keystroke timestamp (Cycle 47 post-preview.114). We add:

- An `IUserInputBufferAdapter` interface (or direct call)
  that pty-speak's keyboard pipeline invokes on every
  non-reserved key, BEFORE writing to PTY:
  - `OnChar(c)`: insert into buffer.
  - `OnBackspace()`: delete from buffer.
  - `OnArrowLeftRight(direction)`: move cursor index.
  - `OnEnter()`: capture + clear buffer; fire `EnterPressed`
    transition.
  - `OnPaste(text)`: bulk insert + multi-line submit
    handling.
  - `OnHistoryNav(direction)`: send arrow to PTY, then
    schedule a `Screen`-row resync 100 ms later.
- The buffer's content is read by the UIA materialiser
  (substituting the active TextSpan during Composing).

#### §5.4. Alt-screen handling

Today's alt-screen detection (`Screen.AltScreenActive` +
`MarkerKind.AltScreenEnter`) signals when cmd / a TUI
takes over the screen. Under ADR 0003 this maps to a
**third** state we don't expose at the InteractionState
level: when alt-screen is active, the announce pipeline is
suspended (today's behaviour). `InteractionState` is
recorded as the value it had at alt-screen entry; on exit,
that value is restored.

This is conceptually a layered "modal overlay" rather than
a third state. The state machine remains two-state.

#### §5.5. History navigation + tab completion resync

When the user presses Up / Down / Tab, the encoder writes
the escape sequence to PTY. cmd responds by overwriting
the prompt line. Our `UserInputBuffer` is now stale.

Resync strategy:

1. Send the byte sequence to PTY.
2. Schedule a `Screen`-row resync after the next idle
   tick (~100 ms).
3. The resync reads the current cmd input row from
   `Screen` — the cells between the prompt path's end and
   the cursor position. This row is cmd's view of the
   command line.
4. Replace `UserInputBuffer.Chars` with that text and set
   `CursorIndex` to the cursor's column within the row.

The resync is best-effort: if the screen has scrolled
during the idle, the read may be wrong. Acceptable for
MVP; refinements possible.

#### §5.6. Sanitiser + cap on the tuple-final announce

The Executing-window `OutputAccumulator` is sanitised at
announce time:

- Strip ANSI / CSI escape sequences (already handled by
  using the parsed VtEvents — Print only).
- Strip BEL (will not appear; routed to earcon).
- Cap at `OutputAnnounceCapChars` (800) from the tail.
- Strip leading and trailing whitespace runs of ≥ 2.

Same as today's `AnnounceSanitiser.sanitise` +
`OutputAnnounceCapChars` cap. No new sanitiser.

### §6. Migration plan

Implementation lands as **six PRs** (call them Cycle 48
PR-A → PR-F). The plan was five PRs in the initial draft;
the §9.5 resolution (add `Source : EntrySource` to
`ContentHistory.Entry` now rather than defer) split the
substrate-schema change out of what was PR-C.

Each PR is independently CI-gated and matrix-walked where
applicable. The state machine ships in **silent
observe-only mode first** so we can validate signal
correctness before changing announce routing.

#### PR-A — This document.

Land ADR 0003 in `docs/adr/0003-shell-interaction-state-
machine.md`. Update `CLAUDE.md` reading order to include
it. Add a Cycle 48 sub-section to
`docs/PROJECT-PLAN-2026-05-12.md` naming the six-PR
sequence. **Validation gate**: maintainer review of this
ADR. No code changes; eligible for the docs-only fast
merge.

#### PR-B — `ShellInteraction` types + observer.

Ship the `InteractionState` DU + `tryTransition` pure
function + the sub-prompt detector, all in
`Terminal.Core/ShellInteraction.fs`. Wire it as an
**observer** at the composition root: it receives the same
signals (Enter pressed, PromptStart fired, idle ticks) and
logs its state transitions at Info level, but does NOT
change announce routing, materialised view, or
ContentHistory schema. Existing idle-flush / tuple-final
paths still drive audible output.

**Validation**: matrix rows Cycle 48-B1 through 48-B8 walk
each test in the CMD corpus with Debug logging on and
verify the state-transition log matches the §4 mapping. If
a transition fires at the wrong time, the failure shows up
in the log without disturbing the maintainer's audible
session.

#### PR-C — `ContentHistory.Entry.Source : EntrySource`.

Add the `EntrySource` DU (per §9.5 resolution:
`UserInputEcho` / `CmdOutput` / `CmdSubPrompt` /
`ShellPrompt` / `Marker` / `Unknown`). Add the `Source`
field to every `Entry` variant. Update
`appendFromEvent` + `appendMarker` to set the field by
reading the current `InteractionState` (provided as a
delegate from the composition root). Pre-state-machine
entries (those that landed before PR-B) get `Unknown`.

**Validation**: matrix row Cycle 48-C1 walks test 01–08
with the diagnostic bundle and verifies the
`--- CONTENT HISTORY ---` section shows the right `Source`
distribution per test. Test 02 (set /p) should show
`UserInputEcho` for the user's name + `CmdSubPrompt` for
"Enter your name:" + `ShellPrompt` for the path rows +
`CmdOutput` for "Hello Kyle!". The diagnostic bundle's
content-history rendering gains an `[Source]` prefix on
each line for forensic readability.

#### PR-D — `UserInputBuffer` updated by keyboard handler.

`TerminalView.OnPreviewKeyDown` calls into the buffer for
each non-reserved key. Buffer content is logged at Debug
on every Enter (for diagnostic correlation). The buffer
participates in the InteractionState (transition [a] reads
it) but the announce routing still goes through the old
paths. The §5.5 history-navigation Screen-row resync
ships here.

**Validation**: matrix row Cycle 48-D1 verifies that for
test 01, the buffer log on Enter shows the exact
submitted text. Tests 02 / 03 / 04 walked to confirm
sub-prompt input lines also captured correctly. Cycle
48-D2 verifies that pressing Backspace mid-type removes
the char from the buffer. Cycle 48-D3 walks history
navigation (Up/Down) and confirms the post-resync buffer
matches the recalled line.

#### PR-E — Switch announce routing to InteractionState.

The Executing → Composing transition's announce fires as
the *sole* tuple-final path. Idle-flush is removed (its
sub-prompt detection moves to the state machine). The
UIA materialiser substitutes `UserInputBuffer` content
for the active TextSpan during Composing. SpeechCursor
filters out entries where `Source = UserInputEcho` (per
§9.6 resolution: applies to both AutoDrive AND Manual
navigation). Today's gates (PR #300 typing-window, PR #301
prefix trim, PR #305 idle-flush typing gate) are
removed — the state machine makes them moot.

**Validation**: full matrix walk Cycle 48-E1 through E8
on the CMD test corpus. The maintainer dogfoods
preview.118. Listening criteria for each test:

- Test 01: silent during typing; one announce of the full
  output; one earcon at the end.
- Test 02: silent during typing of `set /p`; announce of
  `Enter your name:` + earcon; silent during typing of
  the answer; announce of `Hello Kyle!` + earcon.
- Test 04 / 05 (yes-no, choice): sub-prompt announce +
  earcon; SinglekeySubmit consumes the user's single
  keypress; next prompt announce + earcon.
- Test 06 (pause): announce of `Press any key to
  continue . . .` + earcon; silent on any-key press
  (which is the user's input + transition [a]); announce
  of next prompt + earcon.
- Test 07 (progress loop): under default TupleFinalOnly,
  silent for 20 s + one capped announce at the end. Under
  LineByLine, one announce per `Step N`; tuple-final
  skipped (per §9.3).
- Test 08 (stderr): error-tone earcon on the red line;
  tuple-final announce of "error" + ready-prompt earcon.
- SpeechCursor manual nav (`Ctrl+Shift+Up/Down/End`)
  skips the Composing-window entries; user hears only
  output + markers.

If any test fails the listening criterion, fix in a fixup
commit on PR-E before merge.

#### PR-F — Cleanup + closure audit.

Remove now-dead code: `SessionModel.applyAndCapture` tuple-
extraction is preserved for the history-replay path but
the state-machine fields collapse to a thin shim; the
`HeuristicPromptDetector → SessionModel → tuple-final
announce` chain is replaced by `HeuristicPromptDetector →
ShellInteraction → tuple-final announce`. PR #300's
typing-window materialise gate, PR #301's prefix trim,
PR #305's idle-flush typing gate, and the per-character-
heuristic earcon dispatch all delete cleanly.

Update `SESSION-HANDOFF.md`, `CLAUDE.md` "Current
sequencing", `docs/PROJECT-PLAN-2026-05-12.md` change
log, and the `[Unreleased]` CHANGELOG section. Mark ADR
0003 status as `Accepted / Implemented`. Extend the
"Status notes" block (already present from §9 resolution
on 2026-05-13) with per-PR deviations from the proposed
design, if any.

### §7. Risks and mitigations

| Risk                                                            | Mitigation                                                                                              |
|-----------------------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| Heuristic prompt detector misfires (transition [b] during typing) | Detector regex is already conservative; matches `<drive>:\\<path>>` strictly. Logged at Debug. Observable in PR-B before switching announce routing. |
| Sub-prompt detector false-positive on streaming pause (transition [c] mid-`ping`) | Last-byte-not-`\n` check filters out streaming pauses (each `ping` line ends with `\n`). Edge case: a `ping` line truncated mid-write. Acceptable; rare. |
| Enter-detection in `TerminalView` misses a key combination       | Comprehensive key handling in PR-C with unit tests for every cmd-relevant key combo (Enter, Shift+Enter, Ctrl+J, etc.). |
| `UserInputBuffer` drift on history navigation                   | Screen-row resync in §5.5. Best-effort; doesn't affect cmd's actual execution, only the audible "you ran X" description. |
| `OutputAccumulator` grows unbounded for long-running commands   | Cap at 64 KB (same as `tailText`). After cap, drop oldest bytes (FIFO). Announce already capped at 800 chars from tail.  |
| Alt-screen handling (vim, less, full TUI)                       | Already a deferred area in §5.4. Carry-over from today's `AltScreenActive` flag.                       |
| Race between sub-prompt detector and prompt-detector            | Both fire on the same timer tick; mutually exclusive (sub-prompt requires no-`\n`; prompt detector requires the regex to match — implies cmd's `>`-terminated line). |
| PowerShell PSReadLine sends short bursts of bytes that look like sub-prompts | PowerShell support deferred until cycle after MVP. Mitigation: longer `idleThresholdMs` (~800 ms) when active shell is PowerShell. |
| Existing diagnostic battery (T1/T2) expects today's announce ordering | Battery tests fire `WRITE` events; each `\r` in the write is an Enter for the state machine. Battery walks transition [a] → [c]/[b] correctly. Logged at INFO; existing test assertions on `ReadyForInput` events still satisfied. |

### §8. Alternatives considered

#### Alternative A — Byte-level echo matching against `UserInputBuffer`

Maintain a queue of bytes pty-speak has sent to stdin.
For every byte received from stdout, match against the
head of the queue. If matches, tag as `echo`; if no, tag
as real output. Announce only real-output bytes.

**Rejected because**: cmd's echo is not 1-to-1 with sent
bytes. Backspace sends `0x08` but cmd echoes `\b \b`
(BS, space, BS) to overwrite. Enter sends `0x0D` but cmd
echoes `\r\n`. Arrow keys send 3-byte escape sequences
but cmd echoes nothing or a screen redraw. A correct
matcher needs its own state machine of "what cmd should
echo for each input", which is exactly the per-shell
complexity ADR 0003 consolidates into a single
state machine.

State-based suppression (Composing means "all cmd output
is echo") is coarser but robust. The few cases it
mishandles (async output during Composing) are acceptable
for cmd; PowerShell support adds them back behind the
adapter layer.

#### Alternative B — A three-state machine with explicit `SubPromptInput`

Make `SubPromptInput` a distinct state from `Composing`
(at a fresh shell prompt). The distinction would let
us, e.g., announce the prompt path differently for sub-
prompts vs shell prompts.

**Rejected because**: the user-facing semantics are
identical. In both states, cmd is waiting for the user;
cmd's output is echo. The distinction adds combinatorial
complexity to the transition table without changing the
announce decisions. If a future feature does need to
distinguish them, we can add a `Kind` discriminator to
`ComposingData` (`FreshShellPrompt | SubPrompt`) without
splitting the state.

#### Alternative C — Drive everything from OSC 133 even for cmd

Inject our own OSC 133 emitters into cmd via a wrapper
batch file or PROMPT customization. Then every cmd
session would emit explicit boundaries.

**Rejected because**: requires modifying the user's
shell environment (PROMPT variable) at startup, which
breaks "pty-speak runs cmd unchanged." Cmd's PROMPT
customization is also fragile under different locales
and quoting rules. We'd be adding a new failure mode
(prompt injection didn't take effect) to fix the
absence of an existing signal.

#### Alternative D — Defer announce decisions to NVDA via richer UIA

Make the UIA Text-pattern view rich enough (with
`TextAttribute` annotations like `IsReadOnly`,
`IsHidden`) that NVDA decides what to read. Don't fire
`Announce` automatically at all.

**Rejected because**: pty-speak has supported deeper
audible UX (earcons, prompt-path narration, mid-stream
streaming) for cycles. Deferring everything to NVDA's
reading mode would regress those. The state machine
preserves the announce path's expressiveness while
fixing the per-character-chatter and replay bugs.

### §9. Open questions

These were the design decisions I wanted explicit
maintainer input on before PR-B lands. **Resolved
2026-05-13** via in-chat AskUserQuestion. Each one is
recorded here with the original framing + the resolved
choice, so a future audit can trace why each call was made.

#### §9.1. Idle threshold for sub-prompt detection

Today's `idleFlushMs` is 350 ms for cmd. The same value
drives transition [c]. Is 350 ms right?

- **Too short**: a streaming command that pauses
  between bytes (e.g., `for /L %i ... ping`) may
  briefly look idle and fire a spurious transition [c].
  The "last byte is `\n`" filter catches the common
  case (ping lines end with `\n`) but not all cases.
- **Too long**: `set /p` users wait noticeably longer
  before hearing the prompt.

**Resolution: 350 ms cmd default; expose as a per-shell
setting in `ShellPolicy`.** PowerShell can use a longer
value (~800 ms) when its support lands.

#### §9.2. Tuple-final announce content when Executing produced no output

Sometimes cmd has nothing to say. Example: user types
Enter at an empty prompt. cmd echoes `\r\n` (stripped at
announce time) and shows the next prompt.

- **Option A**: skip the announce entirely (just earcon).
- **Option B**: announce the prompt path ("`C:\Users\...\current>`")
  on every Composing entry, regardless of output.
- **Option C**: speak path on session-start only.

**Resolution: Option A — earcon only.** The user knows
they're at a fresh prompt because they just pressed Enter;
the click confirms it without verbal repetition.

#### §9.3. LineByLine + transition [b] interaction

If `ShellPolicy.Streaming = LineByLine`, per-line
announces already covered the output. Should transition
[b] also fire a tuple-final announce of the full
accumulated text?

- **Option A**: skip tuple-final under LineByLine. Earcon
  still fires.
- **Option B**: fire tuple-final anyway; user hears the
  output twice.

**Resolution: Option A.** Skip the redundant announce;
earcon is the "all done" signal.

#### §9.4. SinglekeySubmit detection

What patterns mark a sub-prompt as accepting a single
keystroke without Enter?

- `pause`'s output is `Press any key to continue . . . `.
- `choice /c <opts>` outputs `<msg>[<opts>]? `.
- Future: pty-speak's own selection-list overlay (test
  04 alternative).

**Resolution: regex + literal patterns.** Detect via regex
`\\[\\w+(,\\w+)*\\]\\?\\s*$` (for `choice`) and substring
match `Press any key to continue` (for `pause`). Add new
patterns as we encounter them. Log when SinglekeySubmit is
activated so we can audit. Extension points: a TOML config
table `[shell_policy.sub_prompt_patterns]` lets users add
custom patterns without a code change.

#### §9.5. Should `ContentHistory` be aware of the InteractionState?

Today `ContentHistory` is byte-level. Should it gain a
per-entry `Provenance` tag (`Echo | Output | UserInput`)
so the materialised view can color-code or filter?

- **Option A**: defer; reconstruct from transition log.
- **Option B**: add `Source : EntrySource` to every Entry
  now.
- **Option C**: tag boundaries only (cheap middle ground).

**Resolution: Option B — add `Source` tag now.** This
deviates from my initial recommendation (defer). The
maintainer's call: doing it during Cycle 48 means the
InteractionState integration writes the tag at append
time, with no retrofit later. Aligns with the "Semantic
labels" medium-term cycle on the roadmap (which now
collapses into Cycle 48 rather than waiting).

**Implementation impact**: `ContentHistory.Entry` gains a
non-null `Source : EntrySource` field. `EntrySource` is a
DU:

```
type EntrySource =
    | UserInputEcho       // cmd echoed bytes the user typed
    | CmdOutput           // bytes cmd produced as command output
    | CmdSubPrompt        // bytes cmd produced as a sub-prompt
    | ShellPrompt         // bytes that are the shell's PS1
    | Marker              // boundary marker (PromptStart, etc.)
    | Unknown             // pre-state-machine fallback
```

The classification is set at append time by reading the
current `InteractionState`. PR-C (substrate change) and
PR-D (state-machine integration) become co-dependent;
sequencing in §6 updated accordingly.

#### §9.6. What about `SpeechCursor`?

`SpeechCursor` is the manual review-cursor entrypoint
(`Ctrl+Shift+Up/Down/End`). It navigates
ContentHistory's entries. Under ADR 0003, should it
also be aware of the InteractionState — e.g., skip
`Composing`-window entries (which are echo)?

- **Option A**: AutoDrive skips Composing entries; manual
  navigation includes them.
- **Option B**: navigate everything (no awareness).
- **Option C**: AutoDrive AND manual navigation skip
  Composing entries.

**Resolution: Option C — skip Composing entries in both
AutoDrive and Manual.** This deviates from my initial
recommendation (Option A). The maintainer's call: the
user shouldn't hear their own typed echo via SpeechCursor
even when navigating manually. Edit history (BS / re-types)
is reachable via the UIA review cursor (a separate path),
which IS byte-faithful.

**Implementation impact**: `SpeechCursor` filters entries
where `entry.Source = UserInputEcho`. The skip is
unconditional (no AutoDrive vs Manual branch).

#### §9.7. Multiple shells in one session (hot-switch)

Today `Ctrl+Shift+1/2/3` switches between cmd /
PowerShell / claude. The InteractionState resets on
switch (the new shell's state machine starts fresh).
Confirmed: `state.Current = Composing { fresh }`,
`UserInputBuffer.Chars = []`.

Resolution: handled in PR-B. No new question.

## Status notes

### 2026-05-13 — Open Questions §9.1 → §9.6 resolved

In-chat resolution via AskUserQuestion. Maintainer
accepted defaults on §9.1–§9.4; on §9.5 + §9.6 opted for
the more substantial-but-cleaner alternative over my
initial recommendation.

The §9.5 + §9.6 resolutions add scope to Cycle 48:
adding `Source : EntrySource` to `ContentHistory.Entry`
is a substrate-level schema change that affects
serialisation, the diagnostic-bundle reader, every
tail-walk consumer, and `SpeechCursor`'s filter. Net
effect: the five-PR plan in §6 expands to six PRs by
splitting the substrate change (Source tag) from the
keyboard-handler change (UserInputBuffer). Updated §6
sequencing reflects this; the validation gates and
overall rollout shape are unchanged.

### 2026-05-13 — Implementation shipped (PRs #306–#310 + closure)

Six PRs landed on `main`:

| PR    | Commit    | Per-PR deviation from §6 plan, if any                           |
|-------|-----------|-----------------------------------------------------------------|
| #306  | `f0fbbe8` | PR-A as planned (ADR + open-question resolution).               |
| #307  | `3c2a372` | PR-B as planned (observe-only state machine).                   |
| #308  | `5d34369` | PR-C as planned, with one rename: `EntrySource.Marker` → `EntrySource.BoundaryMarker` to avoid shadowing `ContentHistory.Entry.Marker` at qualified-access under `[<RequireQualifiedAccess>]`. Caught by F# compiler post-merge; fix landed in same PR via fixup. |
| #309  | `96b6e56` | PR-D as planned (UserInputBuffer + writePtyBytes wrapper). The §5.5 history-navigation Screen-row resync is **deferred** to a future cycle — byte-stream MVP only handles printable ASCII + BS + CR. |
| #310  | `459a0b2` | PR-E **narrowed in scope** from the §6 plan. Sub-prompt announce moves to the state machine (`SubPromptIdle` transition); SpeechCursor filter for `UserInputEcho` ships; idle-flush announce body retired. The §6 plan also called for routing `PromptDetected` (transition [b]) through the state machine and replacing `tuple.OutputText` with the state-machine accumulator; this deviation **keeps the SessionModel-driven tuple-final path** because the accumulator includes command-echo + next-prompt-path bytes (messy) while SessionModel extracts clean screen-row text. Defence-in-depth: the `tupleFinaliseAnnounce` + `lastAnnouncedText` prefix-trim machinery from PR #301 stays in source. PR-F (this) doesn't delete it; a future PR can after preview.118 dogfood confirms PR-E is robust. |
| this PR (PR-F) | (closure) | Docs-only sweep: SESSION-HANDOFF, CLAUDE.md "Current sequencing", project plan change log, ADR status + this note. The §6 plan mentioned PR-F "removes now-dead code"; the deviation in PR-E (keeping SessionModel-driven announce + prefix-trim) means PR-F does NOT delete that dead code yet. Cleanup is staged for a follow-up PR after dogfood validation. |

The Cycle 48 model is now live. Audible behaviour changes:

- **Per-character chatter from `idle-flush` is gone.** The
  body that produced "e", "ec", "ech", ... announces no
  longer runs.
- **Sub-prompts (`set/p`, `pause`, `choice`) announce via
  state-machine transitions.** The detector "idle for
  `IdleThresholdMs` AND last byte not LF" fires the
  announce + earcon together.
- **SpeechCursor skips `UserInputEcho` entries** in both
  AutoDrive and Manual navigation.

What did NOT change:

- Tuple-final announce for regular shell commands still
  comes through `SessionModel.applyAndCapture` → boundary
  handler. The Cycle 22b row-extraction code path is
  intact.
- ReadyForInput earcon on `SessionModel.finalisedOpt`
  still fires.
- The PR #300 UIA-materialiser typing-window gate is still
  in place (not made redundant by PR-E since it serves the
  UIA polling case, not the announce path).

## References

- [`docs/adr/0001-substrate-channel-dichotomy.md`](0001-substrate-channel-dichotomy.md)
- [`docs/adr/0002-uia-textedit-caret-output.md`](0002-uia-textedit-caret-output.md)
- [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md)
- [`docs/CANONICAL-DISPLAY-CATALOG.md`](../CANONICAL-DISPLAY-CATALOG.md)
- [`docs/ACCESSIBILITY-TESTING.md`](../ACCESSIBILITY-TESTING.md)
- [`spec/tech-plan.md`](../../spec/tech-plan.md)
- [`scripts/cmd-tests/`](../../scripts/cmd-tests/) — test corpus
- Cycle 47 follow-up PR trail (#299–#305) for the failure
  modes that motivated this ADR
