# SessionModel substrate

> **Snapshot**: 2026-05-06
> **Status**: design / forward-looking — not yet implemented
> **Authoring item**: backlog item 28 (research stage)
> **Companion docs**:
> - [`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md) — canonical vocabulary for stage / pathway / event-type names; this doc uses that vocabulary
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas; parameters specific to SessionModel land here
> - [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) — canonical spec; this doc proposes substrate changes the spec will need to absorb
> - [`spec/overview.md`](../spec/overview.md) — references OSC 133 as recommended authoritative source; this doc operationalises that recommendation
> - [`SECURITY.md`](../SECURITY.md) — threat model; SessionModel persistence has security implications
> - [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) — channel-based-communication principle. SessionModel mutations happen on the `pumpChannel` consumer thread; future Tier 2 persistence will use a dedicated flush-to-disk channel.

## What this document is

A forward-looking design for the **SessionModel substrate** —
a new pty-speak component that maintains a semantically-tagged
history of (prompt, command, output, exit-code) tuples. Tuples
are sourced from **OSC 133** escape sequences emitted by
shells, with a **heuristic fallback** for shells that don't
emit OSC 133 (notably `claude.exe` per
[`spec/overview.md:71`](../spec/overview.md), and `cmd.exe` /
PowerShell by default).

The SessionModel sits in the pipeline between **Stage 2
(Parser application)** and **Stage 4 (Canonical-state
synthesis)**, consuming a new `ScreenNotification.PromptBoundary`
event type. It exposes a query API that history-aware pathways
(future ReplPathway, FormPathway, ClaudeCodePathway,
AiInterpretedPathway, SessionConsumer base) consume.

This is a **design document**, not a code change. The
deliverable specifies the contract; implementation cycles
build against it.

## Why this exists

Through PR #166 (sub-row suffix-diff) and PR #169 (mode-barrier
flush policy), pty-speak gained progressively richer screen-
state diffing. Release-build validation 2026-05-06 surfaced
two related architectural limits:

1. **Scroll misalignment**: when shell output causes scroll,
   the per-row `LastEmittedRowText` cache becomes misaligned
   with the screen (cache index N still holds what USED TO BE
   at row N; screen now has shifted content). Diff sees
   "many rows changed" → bulk-change fallback engages →
   re-announces stale content.
2. **No structured history**: pty-speak's runtime state is
   the live 30×120 cell grid + the FileLogger log. There is
   no in-memory representation of "the last command the user
   ran", "its output", "what they typed before that", etc.

The maintainer's framing 2026-05-06:

> "After each evaluation of a piece of code, can you let me
> know if you can get a really clear semantic indication of
> the input line and the resulting corresponding output before
> the next input line? I think that would be really important
> data to start storing for later computation, even if we
> leave the screen rendering as it is now. I don't think we
> should fix it on DIFF optimization for the current
> interface."

The SessionModel substrate is the architectural answer. It
captures the (prompt, command, output, exit-code) structure
explicitly. Once the substrate exists:

- **History navigation** becomes a SessionModel query, not a
  scrollback buffer extension (item 27 reframes around this).
- **Echo correlation** can ground against the active "input
  line" tuple in the SessionModel rather than per-keystroke
  inference.
- **Scroll-misalignment** stops being a screen-rendering bug;
  the SessionModel knows what's "the same content shifted"
  vs. "genuinely new content" because its data model is
  semantic, not visual.
- **AI summarisation** (Phase 3) consumes per-tuple output
  blocks, not raw screen text.
- **Replay / postmortem** opens up — the SessionModel can be
  serialised to disk and loaded by an offline tool to replay
  a session for analysis.

## Audience

Three intended readers:

1. **The maintainer**, when reasoning about future
   features ("does this need SessionModel integration?
   does it need new tuple metadata?").
2. **Future Claude sessions**, when implementing the
   substrate or building pathways on top.
3. **Future contributors**, when reading code that
   references SessionModel.

The doc is **NOT** a user-facing guide. End-user
documentation for history navigation hotkeys, etc.
will live in user-facing docs once implementation
ships.

## Reading order

1. **Why this exists** (above) — context.
2. **OSC 133 protocol** — the source of truth.
3. **Heuristic fallback** — for shells without OSC 133.
4. **Data model** — `SessionModel`, `SessionTuple`,
   `BoundaryKind`, transient vs persistent state.
5. **Pipeline integration** — where SessionModel sits in
   the 12-stage pipeline; new notification types; new
   pathway protocol method.
6. **Pathway integration** — how each pathway type
   consumes SessionModel.
7. **Persistence** — in-memory + disk format +
   cross-session continuity.
8. **Query API** — what pathways can ask of SessionModel.
9. **Implementation precedence** — what ships first vs
   later.
10. **Open questions** — design decisions still in flux.
11. **Out of scope** — what this doc explicitly doesn't
    cover.

## OSC 133 protocol

OSC 133 (also called "FinalTerm" or "iTerm semantic
prompts") is a set of OSC escape sequences that shells
emit to mark the boundaries between prompt, command
input, command output, and command-finished events. It
is the **authoritative source** for SessionModel data
when the shell supports it.

### Sequence taxonomy

| Sequence | Bytes | Meaning |
|---|---|---|
| **OSC 133 A** | `ESC ] 133 ; A BEL` (or `ESC ] 133 ; A ESC \`) | **Prompt start.** A new shell prompt is being drawn. The user has not yet started typing. |
| **OSC 133 B** | `ESC ] 133 ; B BEL` | **Command start.** Everything from this point until OSC 133 C is the user's typed command line (input). The prompt has finished rendering; the cursor is at the start of the input field. |
| **OSC 133 C** | `ESC ] 133 ; C BEL` | **Output start.** The user has pressed Enter. The shell is now processing the command. Everything from this point until OSC 133 D is command output (stdout + stderr; pty-speak doesn't distinguish them). |
| **OSC 133 D** | `ESC ] 133 ; D BEL` or `ESC ] 133 ; D ; <exit_code> BEL` | **Command done.** The command has finished. The optional `<exit_code>` parameter is the integer exit status (0 = success). |

### Optional parameters

The OSC 133 spec allows additional `key=value` parameters
appended after the boundary letter (and exit code, for D).
Examples seen in the wild:

- `OSC 133 ; A ; aid=<command-id>` — pairs the prompt with a
  later D event by command ID.
- `OSC 133 ; B` (rare; usually no parameters)
- `OSC 133 ; C` (rare; usually no parameters)
- `OSC 133 ; D ; 0` — exit code, no other params
- `OSC 133 ; D ; 0 ; aid=<command-id>` — exit code + command ID
- `OSC 133 ; D ; 0 ; cl=<command-line>` — exit code + literal command line (rare; only some shells emit this)

The pty-speak SessionModel parses `aid=<id>` (command ID)
and `<exit_code>` for D. Other key=value pairs are
accepted but stored verbatim in a `Map<string, string>`
field for forward compatibility — future shells may emit
new metadata; SessionModel doesn't drop it.

### Shell support today

| Shell | OSC 133 emit? | Notes |
|---|---|---|
| **zsh** with `zsh-history-substring-search` or `oh-my-posh` integration | Yes (opt-in) | Requires user setup. Recommended for power users. |
| **fish** (default) | Yes | Default behaviour 3.5+. |
| **bash** with `__vsc_prompt_cmd_*` (VSCode shell integration) | Yes | Requires `bash --rcfile` setup or sourcing the integration script. |
| **PowerShell** with custom `prompt` function | Yes (opt-in) | Default `prompt` doesn't emit; user must override. |
| **cmd.exe** | No | Cannot emit OSC 133 directly; would require shell-script wrapper. |
| **claude.exe** | No | Per [`spec/overview.md:71`](../spec/overview.md), Claude Code does NOT emit OSC 133. Tracked upstream as #22528, #26235, #32635 (all open + unresolved as of 2026-05-06). |

For `cmd.exe` and `claude.exe`, the heuristic fallback is
the primary detection mechanism.

### Termination

OSC sequences terminate with either BEL (`0x07`) or ST
(`ESC \`, two bytes `0x1B 0x5C`). Pty-speak's parser already
supports both terminators (per
`StateMachine.fs:348-395`). SessionModel doesn't need to
care which terminator was used — both produce an
`OscDispatch` event with the same `parms` content.

### What pty-speak does today with OSC 133

Currently nothing. The parser emits `OscDispatch` events for
every OSC sequence; `Screen.Apply` silently drops all
OSC dispatches with a security comment (`Screen.fs:583-612`)
about OSC 52 clipboard and other hostile-input vectors.
OSC 133 falls through this drop today.

### What pty-speak will do with OSC 133

`Screen.Apply`'s OSC arm gains a new branch:

```fsharp
| OscDispatch(parms, _) ->
    if parms.Length > 0 then
        let oscType = System.Text.Encoding.ASCII.GetString(parms.[0])
        match oscType with
        | "133" -> handleOsc133 parms
        | _ -> ()  // other OSCs continue to be dropped
```

`handleOsc133` parses the boundary letter (A/B/C/D), the
optional exit code (D), and any key=value parameters into a
`PromptBoundaryData` record. Then publishes a new
`ScreenNotification.PromptBoundary` event onto the
notification channel.

PathwayPump consumes the `PromptBoundary` notification and
calls `activePathway.OnPromptBoundary boundaryData` (a new
method on the `DisplayPathway.T` protocol). Pathways that
maintain a SessionModel (initially: `SessionConsumer` base
+ ReplPathway / ClaudeCodePathway / FormPathway) update
their model accordingly.

### Security: hostile OSC 133 emissions

OSC sequences arriving from the PTY are not authenticated.
A malicious or buggy program in the shell could emit
spurious OSC 133 sequences to mislead the SessionModel.
Mitigations:

- **Bounded payload size**: parser already enforces
  `MAX_OSC_RAW = 1024` bytes per sequence; over-budget
  emissions are silently dropped (per `StateMachine.fs:377-378`).
- **Tuple-bounded model**: SessionModel maintains a bounded
  ring buffer of recent tuples (default 100; configurable).
  A program emitting many spurious OSC 133 D events can
  pollute history but cannot crash the substrate.
- **Exit-code validation**: parser accepts any integer; if
  the value is not parseable, the SessionModel records
  `None` for exit code rather than crashing.
- **No execution of OSC 133 content**: the boundary
  semantics are pure metadata. Even malicious sequences
  only mis-tag history; they don't trigger any side
  effects.
- **No clipboard / file system access**: SessionModel
  persistence (when enabled) writes to the FileLogger's
  parent directory only. No path traversal vectors via
  OSC 133 fields.
- **Pty-speak does NOT echo OSC 133**: shells emit them on
  output; pty-speak consumes them but does not propagate
  them to NVDA. There's no "OSC 133 announcement attack".

## Heuristic fallback

For shells that don't emit OSC 133, the SessionModel
falls back to **heuristic prompt detection**. This is
inherently less reliable than OSC 133 (false positives,
false negatives possible), but provides degraded-but-
useful service for shells that haven't been configured.

### Detection layers

The fallback uses a layered detection approach:

1. **Per-shell prompt regex** — for known shells (cmd,
   PowerShell, claude), a regex matches the prompt
   pattern (e.g. cmd's `^[A-Z]:\\.*>\s*$`, PowerShell's
   `^PS\s.*>\s*$`, etc.). Matches are tentative;
   stability checks (below) confirm.
2. **Stability check** — a row that matches the prompt
   regex must remain stable for `prompt_stability_ms`
   milliseconds (default 100ms) before SessionModel
   considers it a real prompt boundary. Filters out
   transient screen updates that happen to match.
3. **Cursor-position check** — when cursor-aware diff
   ships (Phase 2), an additional check: the cursor
   must be ON the candidate prompt row's right edge for
   the row to be a "user is now editing" prompt.
4. **Output-block delimiters** — between two detected
   prompt rows, everything is treated as the previous
   command's output. Without OSC 133 D, exit code is
   unknown (recorded as `None`).
5. **Claude Code Ink-box detection** — a special-case
   heuristic for `claude.exe`, which uses Unicode box-
   drawing characters (`╭─╮│╰─╯`) and a `│ >` prompt
   indicator. Per [`spec/overview.md:71`](../spec/overview.md),
   the recommended detection looks for a row matching
   `^.*│\s>.*$` in conjunction with the box characters.

### Per-shell configuration

The SessionModel's heuristic-fallback parameters are
per-shell, configured via TOML:

```toml
[session_model.fallback.cmd]
prompt_regex = '^[A-Z]:\\.*>\s*$'
prompt_stability_ms = 100

[session_model.fallback.powershell]
prompt_regex = '^PS\s.*>\s*$'
prompt_stability_ms = 100

[session_model.fallback.claude]
detection_mode = "claude_ink_box"
prompt_indicator_regex = '^.*│\s>.*$'
prompt_stability_ms = 200
```

These are atlas parameters (item 28 → USER-SETTINGS
update). Default values ship in code; user can override.

### Limitations of the fallback

The heuristic fallback is **best-effort**. Known failure
modes:

- **Multi-line prompts** — Powerline, Starship,
  oh-my-posh prompts can span multiple rows. Prompt-
  regex won't match; SessionModel may not detect the
  boundary.
- **Async prompt segments** — git-status, kube-context,
  command-duration segments can resolve mid-typing,
  rewriting the prompt. SessionModel may detect a "new
  prompt" mid-command and split a command across two
  tuples.
- **Custom prompts** — users with non-default prompts
  won't be detected unless they update
  `prompt_regex`.
- **Shells with no fallback configured** — produce no
  SessionModel data; SessionModel-aware pathways
  degrade to non-history-aware behaviour.

These limitations are explicitly documented; they justify
why **OSC 133 is the recommended source** and the
fallback is "for shells that haven't been configured for
OSC 133". The maintainer (and other power users) should
be encouraged to configure their shells for OSC 133 emission.

### Shell-side configuration recipes

The SessionModel design doc also recommends shell
configuration recipes for emitting OSC 133, since the
substrate is most reliable when shells are configured
correctly. Recipes will live in
[`docs/SHELL-INTEGRATION.md`](SHELL-INTEGRATION.md)
(future doc, separate item) — not in this design doc, to
keep this focused on the substrate itself.

## Data model

### Core types

```fsharp
namespace Terminal.Core

/// Boundary kind sourced from OSC 133 (or heuristic
/// fallback). Drives SessionModel state transitions.
type BoundaryKind =
    /// OSC 133 A — a new prompt is being drawn.
    | PromptStart
    /// OSC 133 B — the user has begun typing the command.
    | CommandStart
    /// OSC 133 C — the user has pressed Enter; output
    /// begins.
    | OutputStart
    /// OSC 133 D — the command has finished. Optional
    /// exit code.
    | CommandFinished of exitCode: int option

/// Source of a boundary detection. Recorded for
/// observability and post-hoc debugging — was this
/// boundary trustworthy?
type BoundarySource =
    /// OSC 133 sequence parsed from the byte stream.
    /// Most trustworthy.
    | Osc133
    /// Heuristic prompt-regex match. Less trustworthy;
    /// may have false positives.
    | HeuristicPromptRegex of stabilityMs: int
    /// Claude Code Ink-box pattern match. Trustworthy
    /// for Claude only.
    | HeuristicClaudeInkBox

/// Data carried by a PromptBoundary notification.
type PromptBoundaryData =
    { Kind: BoundaryKind
      Source: BoundarySource
      /// When the boundary was detected (parser-side wall
      /// clock).
      Timestamp: DateTimeOffset
      /// OSC 133 command-ID parameter (`aid=...`). Empty
      /// for fallback-detected boundaries.
      CommandId: string option
      /// Forward-compatibility: any OSC 133 key=value
      /// parameters that aren't explicitly modelled.
      ExtraParams: Map<string, string> }

/// One semantic unit in the session history. A SessionTuple
/// represents one prompt → command → output → finished
/// cycle. Built incrementally as boundaries arrive.
type SessionTuple =
    { /// Stable identifier for this tuple. UUID-based for
      /// uniqueness; useful for debug logging + future
      /// query API.
      Id: System.Guid
      /// OSC 133 command ID if provided (`aid=...`).
      CommandId: string option
      /// The shell that produced this tuple. Snapshotted
      /// from the active ShellRegistry.Shell at boundary
      /// time.
      ShellId: string
      /// Wall-clock timestamps for the four boundaries.
      /// Some may be `None` if the cycle was incomplete
      /// (e.g. shell crashed before D fired).
      PromptStartedAt: DateTimeOffset option
      CommandStartedAt: DateTimeOffset option
      OutputStartedAt: DateTimeOffset option
      CommandFinishedAt: DateTimeOffset option
      /// Text of the prompt that preceded the command.
      /// Captured between PromptStart and CommandStart
      /// (or fallback's prompt-row content).
      PromptText: string
      /// Text the user typed. Captured between
      /// CommandStart and OutputStart. May span
      /// multiple lines (here-docs, multi-line strings).
      CommandText: string
      /// Output text. Captured between OutputStart and
      /// CommandFinished. May be very long; persistence
      /// layer can truncate or stream-to-disk per policy.
      OutputText: string
      /// Exit code from OSC 133 D parameter. `None` if
      /// the cycle was incomplete or the shell didn't
      /// emit a code.
      ExitCode: int option
      /// Source attribution per boundary. Helps debug
      /// "why was this command tagged as it was?".
      Sources: Map<BoundaryKind, BoundarySource>
      /// Forward-compatibility: any OSC 133 key=value
      /// parameters across all four boundaries.
      ExtraParams: Map<string, string> }

/// The current incomplete tuple — the one being built
/// as boundaries arrive. Becomes a `SessionTuple` and
/// joins the history when the next PromptStart fires
/// (or shell exit, or explicit reset).
type ActiveSessionTuple =
    { Id: System.Guid
      State: ActiveTupleState
      /// All fields as in SessionTuple; some may be
      /// `None` until their respective boundary fires.
      // ... (mirrors SessionTuple fields)
    }

and ActiveTupleState =
    /// Between PromptStart and CommandStart — user is
    /// looking at the prompt; hasn't typed yet.
    | AwaitingCommandStart
    /// Between CommandStart and OutputStart — user is
    /// editing the command line.
    | EditingCommand
    /// Between OutputStart and CommandFinished — command
    /// is running; output streaming.
    | OutputStreaming
    /// All four boundaries have fired; tuple is complete
    /// and ready to be moved into history.
    | Complete

/// The SessionModel itself. One instance per shell
/// session (recreated on shell-switch with a fresh
/// state).
type SessionModel =
    { /// Shell this model belongs to. Captured at
      /// construction.
      ShellId: string
      /// Completed tuples, in chronological order.
      /// Bounded ring buffer (default 100 tuples; see
      /// parameters).
      History: System.Collections.Generic.Queue<SessionTuple>
      /// Maximum history size (in tuples). Beyond this,
      /// oldest tuples are dropped (or persisted to disk
      /// per the persistence policy, then dropped from
      /// memory).
      MaxHistorySize: int
      /// The currently-being-built tuple, if any. `None`
      /// if no boundary has been seen yet (or after a
      /// reset).
      Active: ActiveSessionTuple option
      /// Aggregate metadata for the whole session.
      SessionStartedAt: DateTimeOffset
      SessionId: System.Guid }
```

### Why this shape

- **`SessionTuple` is immutable** once added to history.
  Mutations during construction happen on
  `ActiveSessionTuple`; once complete, it transitions
  to a `SessionTuple` record. F# records' default
  immutability gives this for free.
- **`Active` is an option** — between sessions and on
  reset, the model has no active tuple. Pathways query
  `model.Active` for "is the user currently editing?"
  with a clear "no" answer.
- **`History` is a `Queue`** rather than a `List` — the
  bounded-ring-buffer semantics need O(1) enqueue +
  dequeue. F#'s `System.Collections.Generic.Queue<T>`
  is suitable; alternatively, a custom `RingBuffer`
  type if the queue's API doesn't fit.
- **`Sources: Map<BoundaryKind, BoundarySource>`** —
  per-boundary attribution. Critical for debugging
  fallback behaviour. When a tuple looks wrong, the
  Sources field tells you "the PromptStart was OSC 133
  but the CommandFinished was a heuristic timeout" or
  similar.
- **`ExtraParams: Map<string, string>`** — accept
  unknown OSC 133 parameters; preserve them. Future
  shells may emit fields we don't model yet; we don't
  drop them.
- **`SessionId: Guid`** — disambiguates between sessions
  for persistence + replay. One pty-speak launch
  produces multiple SessionIds (one per shell-switch).

### What this shape deliberately omits

- **Stdout vs stderr separation** — pty-speak's pipeline
  doesn't preserve the distinction (ConPTY merges them
  before pty-speak sees the bytes). SessionTuple's
  `OutputText` is the merged stream. Adding stderr-only
  capture is out of scope; would require ConPTY
  changes.
- **Per-character timestamps within OutputText** —
  preserving "this character arrived at time T" for
  every byte is space-prohibitive and not needed for
  current use cases. Tuple-level
  `OutputStartedAt` + `CommandFinishedAt` is sufficient.
- **Streaming output index** — for very long output (a
  long DIR listing, a tail -f), the SessionTuple
  accumulates `OutputText` until CommandFinished. We
  don't index intermediate "this line was output at T"
  positions. If a future feature needs this (e.g.
  sub-tuple navigation), it gets added then.
- **Cell-attribute preservation** — `OutputText` is
  rendered text (post-`AnnounceSanitiser`,
  trailing-blank-trimmed); SGR colour and other
  attributes are dropped. If "the user wants to know
  command output was red" matters, future work can
  preserve attributes per-line; not in this scope.

### State machine

The `ActiveSessionTuple.State` transitions follow OSC
133's semantics:

```
None
 └─ on PromptStart →  AwaitingCommandStart
                      │
                      ├─ on CommandStart → EditingCommand
                      │                    │
                      │                    ├─ on OutputStart → OutputStreaming
                      │                    │                   │
                      │                    │                   ├─ on CommandFinished → Complete (transitions to SessionTuple in History)
                      │                    │                   │
                      │                    │                   ├─ on PromptStart → reset (close incomplete; new active)
                      │                    │                   │
                      │                    │                   └─ on shell exit → reset (close incomplete; flush)
                      │                    │
                      │                    ├─ on PromptStart → reset (close incomplete; new active)
                      │                    │
                      │                    └─ on shell exit → reset
                      │
                      ├─ on PromptStart → replace active (treated as new prompt)
                      │
                      └─ on shell exit → reset
```

Recovery from out-of-order boundaries (e.g. OutputStart
without preceding CommandStart): the SessionModel
synthesises the missing CommandStart with `CommandText =
""` (empty command) and proceeds. Source attribution
records the synthesis. This is rare in practice; OSC 133
sequences from real shells are well-ordered.

## Pipeline integration

This section maps SessionModel into the 12-stage pipeline
from [`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md). New
notification types, new pathway protocol method, new
substrate component.

### Stage 2 (Parser application) — gains OSC 133 handler

`Screen.Apply`'s OSC dispatcher arm gains an explicit
branch:

```fsharp
| OscDispatch(parms, _) when parms.Length > 0 ->
    let oscType = System.Text.Encoding.ASCII.GetString(parms.[0])
    match oscType with
    | "133" ->
        // Parse boundary kind + parameters; build
        // PromptBoundaryData; publish to notification
        // channel.
        let boundaryData = parseOsc133Boundary parms
        notifyChannel.Writer.TryWrite(
            ScreenNotification.PromptBoundary boundaryData)
        |> ignore
    | _ ->
        // Other OSCs (52 / 0 / 2 / 7 / 8 / etc.) continue
        // to be silently dropped per security comment.
        ()
| OscDispatch _ ->
    // Empty params: drop.
    ()
```

`parseOsc133Boundary` is a pure function in
`Terminal.Core` (placement TBD; possibly a new
`Osc133.fs` module). Tests live in
`tests/Tests.Unit/Osc133Tests.fs`.

### Stage 3 (Notification emission) — gains PromptBoundary variant

`ScreenNotification` discriminated union grows:

```fsharp
type ScreenNotification =
    | RowsChanged of int list
    | ParserError of string
    | ModeChanged of TerminalModeFlag * bool
    | Bell
    /// PR ## (item 28 implementation) — emitted on OSC
    /// 133 A/B/C/D parsing or on heuristic-fallback
    /// boundary detection.
    | PromptBoundary of PromptBoundaryData
```

The notification channel's bounded capacity (256) and
drop-oldest-on-full policy apply uniformly; PromptBoundary
events are not specially privileged.

### Stage 3.5 (NEW) — SessionModel update

A new conceptual stage between notification emission and
canonical-state synthesis. PathwayPump consumes
`PromptBoundary` notifications and updates the active
SessionModel BEFORE handing canonical-state to the
display pathway.

```
PathwayPump reader loop:
    match notification with
    | RowsChanged _ -> handleRowsChanged ()
    | ModeChanged _ -> handleModeChanged ...
    | PromptBoundary data ->
        sessionModel.Apply data       // NEW — update model
        activePathway.OnPromptBoundary data  // NEW — let pathway respond
    | ParserError _ | Bell -> handleSimpleNotification ...
```

`sessionModel.Apply` updates the SessionModel state as
described in the state machine above. `OnPromptBoundary`
is a new method on the `DisplayPathway.T` protocol;
default implementation for non-history-aware pathways
(StreamPathway, TuiPathway) is a no-op.

The order matters: SessionModel updates BEFORE
pathway.OnPromptBoundary so the pathway can query the
updated model.

### Stage 4 (Canonical-state synthesis) — gains optional cursor

This stage is unchanged in shape but the canonical record
gains a `CursorPosition: (int * int)` field. The cursor
lives on `Screen.Cursor` today (per
[`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md)
"Substrate inventory"); CanonicalState gains a snapshot
of it. Required for:

- SessionModel's "what character is the cursor on?"
  queries (used by EditingCommand state to capture the
  command-line text without OSC 133 B/C boundaries — the
  cursor delimits it implicitly).
- Future cursor-aware diff (Phase 2).

This change is small but cross-cuts canonical-state-
related code. Bundling with the SessionModel
implementation reduces churn.

### Stage 7 (Row-level diff) — gains content-set query

When SessionModel ships, the bulk-change-fallback
heuristic (stage 8b) gains an additional check:

> Before emitting `diff.ChangedText`, filter out rows
> whose content already appears in `SessionModel`'s
> recent output text. Scrolled rows have content
> matching previously-emitted tuples; emit only
> genuinely new content.

This is the proper fix for the scroll-misalignment issue
discovered 2026-05-06 (the one PR #170 was originally
intended to address heuristically). Once SessionModel
exists, the filter is a clean lookup against the
session-history; no need for a heuristic
content-set-from-cache.

### Stage 8 (Sub-row suffix detection) — gains active-command awareness

Suffix-diff during `EditingCommand` state can use the
SessionModel's active tuple to ground its suffix
computation. Specifically: when `model.Active.State =
EditingCommand`, the "previous text" baseline is
`model.Active.CommandText` (what we know the user has
already typed) rather than the per-row cache. This
sidesteps scroll-misalignment for the editing case.

### DisplayPathway.T protocol gains OnPromptBoundary

```fsharp
type T =
    { Id: string
      Consume: CanonicalState.Canonical -> OutputEvent[]
      Tick: DateTimeOffset -> OutputEvent[]
      OnModeBarrier: DateTimeOffset -> OutputEvent[]
      Reset: unit -> unit
      SetBaseline: CanonicalState.Canonical -> unit
      /// NEW (item 28 implementation) — called when a
      /// PromptBoundary notification fires. Default
      /// implementation for non-history-aware pathways
      /// (StreamPathway, TuiPathway) is a no-op
      /// returning [||]. SessionConsumer-base pathways
      /// (ReplPathway, ClaudeCodePathway, FormPathway,
      /// AiInterpretedPathway) implement it
      /// substantively.
      OnPromptBoundary: PromptBoundaryData -> OutputEvent[] }
```

Pathways that don't care about prompt boundaries return
`[||]` and rely on the SessionModel's own state-
maintenance (which happens upstream of OnPromptBoundary
per Stage 3.5 above). Pathways that DO care emit
`PromptDetected` / `CommandSubmitted` /
`CommandFinished` semantic events (the placeholder
SemanticCategory cases in `OutputEventTypes.fs:77-81`),
or pathway-specific outputs (e.g. ReplPathway emits a
"command started" announce at OSC 133 C).

### Where SessionModel lives in the composition root

`Program.fs:compose` constructs and owns the SessionModel.
Wiring:

```fsharp
let sessionModel = SessionModel.create initialShell
// On shell-switch:
let switchToShell (target: ShellRegistry.ShellId) =
    sessionModel.PersistAndReset()  // flush + start fresh
    // ... existing shell-switch logic ...
```

The `sessionModel` reference is passed to:
- `PathwayPump` for boundary notification handling.
- Active pathway's construction (so pathway can query
  the model on `Consume`).
- Diagnostic battery (Ctrl+Shift+D) for inspection.
- Future: hotkey handlers for history navigation.

### Threading

- **Updates** to SessionModel happen on the PathwayPump
  thread (same thread that handles RowsChanged etc.).
  No locks needed for write-from-pump-only.
- **Reads** from SessionModel happen on:
  - PathwayPump (for OnPromptBoundary callback to
    pathway).
  - Active pathway's `Consume` (which runs on the same
    pump thread).
  - Diagnostic battery (which runs on a Task.Run thread;
    needs lock).
  - Future: hotkey handlers (WPF dispatcher thread;
    needs lock).
- **Recommendation**: SessionModel internal state is
  guarded by a lightweight lock (similar to Screen's
  gate-lock pattern). Writes from pump take the lock
  briefly; reads from other threads take it for the
  duration of the query.

### Capture mechanics — how text gets into SessionTuple fields

The PromptBoundary notification carries the boundary kind
+ timestamp + extra params. It does NOT carry the actual
prompt / command / output text. SessionModel must capture
text by observing screen state at boundary time.

**PromptText capture**: at PromptStart, SessionModel
notes the cursor's current row (via Screen.Cursor) and
records "the prompt is being drawn here". When
CommandStart fires, SessionModel reads the row content
from the screen — that row's text is the prompt.

**CommandText capture**: between CommandStart and
OutputStart, SessionModel observes the row mutations
that span the cursor row. At OutputStart, it reads the
row content (or rows, for multi-line commands) starting
from the prompt's right edge — that's the command text.
The cursor position at OutputStart is the end of the
command.

**OutputText capture**: between OutputStart and
CommandFinished, SessionModel accumulates ALL screen
mutations as the output text. This is the most expensive
field — long-running commands can produce many KB of
output. Persistence layer can stream this to disk
incrementally (see Persistence section).

**Capture during heuristic fallback**: similar logic but
the boundaries are inferred from prompt-row stability
rather than OSC 133 sequences. Less reliable but
functional.

## Pathway integration

How each pathway type consumes (or chooses not to consume)
the SessionModel.

### StreamPathway — no SessionModel awareness (default)

The default pathway, used for cmd / PowerShell / Claude
Code / unknown shells. Does NOT consume SessionModel by
default; behaviour is unchanged from current
implementation.

`OnPromptBoundary` returns `[||]`. The SessionModel still
tracks tuples (the substrate is shared) but the pathway
doesn't use them for output decisions.

**Future enhancement**: when scroll-misalignment is
addressed, StreamPathway's stage 7 / 8b lookup against
SessionModel content-set replaces the cache-based
heuristic. This is enabled-by-default once shipping.

### TuiPathway — no SessionModel awareness

Alt-screen sessions are not "command/output cycles"; vim
/ less / htop don't fit the SessionModel mental model.
TuiPathway returns `[||]` from `OnPromptBoundary`.

The SessionModel still tracks alt-screen entry/exit (via
ModeBarrier) but doesn't try to capture vim's content as
tuples.

### ReplPathway — primary SessionModel consumer (Phase 2)

For Python REPL, Node REPL, ipython, IRB, and similar
explicit-prompt shells. Consumes SessionModel as the
primary data structure.

Behaviour:

- **On PromptStart**: emit a brief "ready for input"
  cue (configurable; default: silent — the prompt's
  characters announce naturally).
- **On CommandStart**: pathway transitions to
  "user-is-editing" mode. Future: integrate with input
  framework's echo correlation; suppress per-keystroke
  echoes if NVDA is announcing them.
- **On OutputStart**: emit "running command" cue
  (configurable). Switch from input-aware to
  output-streaming mode.
- **On CommandFinished**:
  - If exit code is `Some 0`: emit success cue (default:
    silent; the output already announced).
  - If exit code is `Some non-zero`: emit error cue
    (configurable; default: tone alert).
  - If exit code is `None` (incomplete tuple): emit
    "command finished without exit signal" cue
    (rare; debug indicator).
- **On request (hotkey)**: emit a previous tuple's
  content. E.g. Alt+Up → "command N: <CommandText> →
  <OutputText> (exit 0)".

Atlas parameters:
- `[pathway.repl] prompt_announce` — cue policy for
  PromptStart.
- `[pathway.repl] command_start_announce` — cue policy
  for CommandStart.
- `[pathway.repl] output_start_announce` — cue policy
  for OutputStart.
- `[pathway.repl] success_announce` — cue policy for
  exit code 0.
- `[pathway.repl] error_announce` — cue policy for
  non-zero exit code.

### FormPathway — selection-aware via SessionModel (Phase 2)

For gum / fzf / claude-code's selection prompts. The
SessionModel may not be the primary data source (forms
have a richer state model — currently-selected option,
total options, etc.) but FormPathway uses
SessionModel boundaries to know when a form is opening
or closing.

Behaviour:

- **On PromptStart**: pathway resets its form state.
- **On CommandStart**: pathway switches into form-active
  mode (selection events become the dominant output).
- **On OutputStart**: pathway exits form-active mode;
  treats subsequent screen mutations as command output.
- **On CommandFinished**: pathway emits "selection
  complete" cue with the resolved selection.

The form-specific state (currently-selected option,
etc.) lives on the FormPathway instance, not in
SessionModel. SessionModel's role is just the
semantic-boundary scaffolding.

### ClaudeCodePathway — Claude-specific via fallback (Phase 2)

Claude Code does NOT emit OSC 133. ClaudeCodePathway
relies on the heuristic fallback's
`HeuristicClaudeInkBox` mode to detect prompt boundaries.

Behaviour:

- **On PromptStart (heuristic)**: pathway suppresses
  banner red text emit (resolves backlog item 22).
- **On CommandStart**: pathway captures the user's
  message.
- **On OutputStart**: pathway captures Claude's
  response.
- **On CommandFinished (heuristic — detected by next
  PromptStart)**: pathway emits "Claude responded" cue.

The heuristic's reliability is shell-version-dependent;
Claude Code's UI changes may break detection. Atlas
parameters allow override:
- `[pathway.claude_code] prompt_indicator_regex`
- `[pathway.claude_code] response_separator_pattern`

### AiInterpretedPathway — output summarisation (Phase 3)

For users who opt in to AI summarisation of command
output. Substrate-prerequisite: SessionModel + AI
inference pipeline (separate concern).

Behaviour:

- **On CommandFinished**: pathway sends
  `tuple.OutputText` to an AI endpoint. Receives a
  summary. Replaces the original output emit with the
  summary as a single StreamChunk (or supplements it,
  depending on user preference).

Privacy: per `SECURITY.md`, opt-in only; never default.
The user's command + output is sent to the AI endpoint;
they must consent.

### SessionConsumer base — shared mixin (Phase 2)

A pathway "concern" providing common SessionModel-
integration behaviour. ReplPathway, FormPathway,
ClaudeCodePathway, AiInterpretedPathway all extend it.

Provides:

- `OnPromptBoundary` plumbing — converts the boundary
  to the right pathway-state-machine transition.
- Tuple-level event emission — `PromptDetected`,
  `CommandSubmitted`, `CommandFinished` semantic events
  with proper Source field set.
- History query helpers — `latestTuple()`,
  `tupleByCommandId(id)`, etc.

Implementation approach: F# composition rather than
inheritance. A `SessionAwarePathway` record wraps a
`DisplayPathway.T` with additional state and overrides
its protocol methods. Concrete pathways (ReplPathway
etc.) construct themselves by composing their
specifics with `SessionAwarePathway`.

## Persistence

The SessionModel can be persisted to disk for cross-
session continuity, replay, and offline analysis. This
section specifies the persistence story.

### Persistence policy options

```toml
[session_model.persistence]
mode = "memory_only"  # "memory_only" | "session_log" | "always"

# When mode = "session_log" or "always":
output_dir = "<%LOCALAPPDATA%>/PtySpeak/sessions"
format = "jsonl"  # "jsonl" | "msgpack" (jsonl is human-readable; msgpack is compact)
max_session_size_mb = 50  # rotate / truncate beyond this
```

### Mode: memory_only (default)

SessionModel state lives entirely in RAM. On pty-speak
exit, history is lost. Cross-session continuity is none.

Use case: privacy-sensitive sessions, ephemeral usage,
power users who don't need history beyond the current
launch.

### Mode: session_log

SessionModel writes each completed tuple to disk as it
moves from `Active` to `History`. File location:
`<output_dir>/session-<SessionId>.jsonl`. One JSON
object per tuple, line-delimited.

The on-disk wire format is **decades-stable** — see the
"On-disk wire format (Cycle 24b)" sub-section below for
the canonical reference. Cycle 24b ships the pure
serializer (`SessionModel.formatTupleAsJsonl`); Cycle 24c
adds the bounded-channel async writer that calls it on
the Active → History transition.

Each pty-speak launch produces one file (one SessionId).
Within a launch, shell-switches START NEW FILES (one
file per shell session).

Use case: power users who want history; audit trail;
debugging.

### Mode: always

Same as session_log but tuple persistence happens
synchronously on Active → History transition (no
buffering). Tighter durability but slightly slower
write cadence.

Use case: high-stakes audit scenarios where data loss
on crash is unacceptable.

### On-disk wire format (Cycle 24b)

The serializer is `SessionModel.formatTupleAsJsonl :
SessionTuple -> string` in
`src/Terminal.Core/SessionModel.fs`. It returns one JSON
object followed by a literal `\n` terminator. **This format
is decades-stable**: locked design decisions enforced by
unit tests in `tests/Tests.Unit/SessionModelJsonlTests.fs`.

The writer that calls this serializer ships in **Cycle 24c**
as `Terminal.Core.SessionLogWriterSink`
(`src/Terminal.Core/SessionLogWriter.fs`). The composition
root at `src/Terminal.App/Program.fs` constructs a sink
per shell session (when persistence mode is `SessionLog` or
`Always`), pattern-matches the
`SessionModel.applyAndCapture` / `finalizeIncompleteAndCapture`
return tuples, and dispatches each finalised tuple to the
sink. The sink owns one `BoundedChannel<SessionTuple>` +
one background drain task + one open `StreamWriter`; pinned
by `tests/Tests.Unit/SessionLogWriterTests.fs`.

Canonical example (line breaks added for readability — the
on-disk form is one line per tuple, no whitespace):

```jsonl
{
  "schemaVersion": 1,
  "id": "11111111-2222-3333-4444-555555555555",
  "commandId": null,
  "shellId": "powershell",
  "promptStartedAt": "2026-05-09T14:23:45.1234567Z",
  "commandStartedAt": "2026-05-09T14:23:46.4560000Z",
  "outputStartedAt": "2026-05-09T14:23:46.7890000Z",
  "commandFinishedAt": "2026-05-09T14:23:48.0120000Z",
  "promptText": "PS C:\\Users\\test>",
  "commandText": "dir",
  "outputText": " Volume in drive C is OS\n...",
  "exitCode": 0,
  "sources": [
    {"boundary": "PromptStart",     "source": {"kind": "Osc133"}},
    {"boundary": "CommandStart",    "source": {"kind": "Osc133"}},
    {"boundary": "OutputStart",     "source": {"kind": "Osc133"}},
    {"boundary": "CommandFinished", "source": {"kind": "Osc133"}}
  ],
  "extraParams": {}
}
```

**Locked design decisions:**

1. **Per-record `"schemaVersion"`** — first key on every
   record. Today's value is `1`. Future schema changes
   increment the value; replay tools branch on it; old
   files always remain readable. Reserved values: `0` is
   forbidden (a missing or zero value should be detectable
   as corruption).
2. **Field order is fixed**: schemaVersion → identity (id,
   commandId, shellId) → timestamps (promptStartedAt,
   commandStartedAt, outputStartedAt, commandFinishedAt) →
   text (promptText, commandText, outputText) → exitCode
   → maps (sources, extraParams). Pinned by a snapshot
   test.
3. **camelCase field names** throughout.
4. **DateTime serialisation**: ISO-8601 UTC with 100ns
   ticks (`yyyy-MM-ddTHH:mm:ss.fffffffZ`). Lossless from
   the Windows clock — sub-millisecond precision survives
   round-trip. Always UTC; the formatter calls
   `dt.ToUniversalTime()` defensively.
5. **`option<T>` handling**: `None` → JSON `null`; `Some v`
   → the value's normal encoding. Never an empty string,
   never a missing key.
6. **`BoundarySource` is always a tagged object**: uniform
   `{"kind":"<case>"}` shape across all DU cases, with
   payload fields siblings of `kind`. Examples:
   - `Osc133` → `{"kind":"Osc133"}`
   - `HeuristicPromptRegex 500` → `{"kind":"HeuristicPromptRegex","stabilityMs":500}`
   - `HeuristicClaudeInkBox` → `{"kind":"HeuristicClaudeInkBox"}`
7. **`sources` is an array of records, not an object**:
   each entry is `{"boundary":"<kind-name>","source":<tagged-object>}`.
   Sorted by an explicit ordinal: `PromptStart=0,
   CommandStart=1, OutputStart=2, CommandFinished=3`.
   Diverges from earlier illustrative examples in this
   doc — the array shape avoids a latent collision bug
   where `BoundaryKind.CommandFinished _` payload variants
   would alias to the same JSON key. The boundary name in
   `"boundary"` is the bare DU case name (no payload — the
   exit code lives on `tuple.exitCode` and is not
   duplicated here).
8. **`extraParams`** is a JSON object keyed by the OSC 133
   parameter name. Keys iterate in ordinal-string-compare
   order (F# `Map<string, _>` natural sort) for byte-stable
   output across .NET versions.
9. **String escapes** follow RFC 8259 minimum (named
   escapes for `"`, `\`, `\b`, `\f`, `\n`, `\r`, `\t`;
   `\u00XX` for any other byte in `0x00-0x1F`) PLUS DEL
   (`0x7F` → ``, deliberate superset; many parsers
   and viewers mis-handle bare DEL). C1 controls
   (`0x80-0x9F`), forward slash, U+2028, U+2029 pass
   through unescaped.
10. **Lone UTF-16 surrogates throw at emit time**. Silent
    corruption of a decades-old log is the worst possible
    failure mode; a thrown exception forces upstream code
    to fix the surrogate-producing bug.
11. **Trailing terminator is the literal `"\n"`**, never
    `Environment.NewLine`. The latter would emit `\r\n` on
    Windows and silently break byte stability across
    platforms.
12. **No UTF-8 BOM ever**. The Cycle 24c writer must use
    `UTF8Encoding(false)`.
13. **No Unicode normalisation**. Bytes pass through
    verbatim — normalisation is a semantic transform; the
    substrate preserves shell output exactly as received.

Schema migration runtime ships in Cycle 25+ (the
deserializer). Cycle 24b only emits version 1; the
versioning hook is the future-proofing handle.

### Env-var-value sanitisation (Cycle 24d-2)

Before each tuple is written to disk, the
`SessionLogWriterSink` runs it through
`SessionSanitiser.sanitiseTuple`
(`src/Terminal.Core/SessionSanitiser.fs`). The sanitiser
replaces any occurrence of a registered env-var value with
the marker `<REDACTED:UPPERCASE_NAME>` in the tuple's
`commandText`, `outputText`, `promptText`, and every
`extraParams` value. The substrate's in-memory History keeps
unsanitised text (the user can recover their own commands
via `Ctrl+Shift+Y`); only the persistence layer redacts.

**Registration**: at startup, the composition root calls
`SessionSanitiser.registerFromEnvironment` which enumerates
the process environment and registers values for every var
whose name matches the Stage 7 deny-list pattern (suffix
match on uppercase: `*_TOKEN`, `*_SECRET`, `*_KEY`,
`*_PASSWORD`; `ANTHROPIC_API_KEY` exempted as Claude Code's
primary credential — same precedent as
`Terminal.Pty.Native.isDenied`). Registration is silent for
values shorter than 16 chars (the `MinValueLength`
threshold) to avoid false-positive redactions on common
short values like `BANK_API_KEY=admin`.

**Threat model**: a shell expands an env var into output
(e.g. `echo $GITHUB_TOKEN` substitutes the literal token
value into stdout). Stage 7's NAME-only env-scrub at the
spawn boundary prevents the CHILD shell from seeing the
deny-listed env vars; this sanitiser is the complement —
when pty-speak's parent process inherits a deny-listed env
var, and the user types a command that echoes its value,
the on-disk artefact won't contain the secret. NAME-only
matching can't catch this case because once the shell has
expanded the variable, the literal name is gone.

**Marker format** is decades-stable. Future replay tools
parse `<REDACTED:([A-Z_]+)>` to identify which credential
was redacted. The format is documented here so it can't
change without a coordinated migration; if it ever does,
the change increments `JsonlSchemaVersion`.

**Limitations**: the sanitiser captures values at startup;
env vars set after launch are NOT retroactively scrubbed.
Restart-to-update is the documented cost. Future cycles
can add per-tuple env re-scan if demand surfaces. Pattern-
based detection (e.g. AWS-key regex `^AKIA[0-9A-Z]{16}$`)
is also out of scope today.

### Config reload on shell switch (Cycle 25a)

The composition root re-reads `%LOCALAPPDATA%\PtySpeak\config.toml`
on every `Ctrl+Shift+1` / `+2` / `+3` shell switch and
applies any change to `[session_model.persistence]` to the
new shell session's writer. This lets a maintainer edit the
TOML mid-session and validate the change with a single
keystroke instead of relaunching pty-speak.

**What reloads:**

- `[session_model.persistence] mode` / `output_dir` /
  `max_session_size_mb` — the new sink is constructed with
  the fresh values; the per-shell-session file path may
  change (`session_log` → `always` mid-session is
  supported).
- `SessionSanitiser.registerFromEnvironment` is re-run; any
  new deny-listed env vars set since startup register their
  values for redaction. Existing registrations stay
  (idempotent).

**What does NOT reload (documented; revisit in a future cycle):**

- `[startup] default_shell` — the target shell is already
  chosen on switch.
- `[logging] min_level` — the FileLogger sink starts with
  the env-var-or-Information level + the TOML override
  (Cycle 25a's startup-time apply); `Ctrl+Shift+G` is the
  runtime path for adjusting verbosity.
- `[pathway.stream]` — pathway parameters bake into
  pathway state at construction; safe-reload is a
  follow-up.

A reload failure (TOML parse error mid-session, file
deleted, etc.) logs a Warning and keeps the prior
`persistenceConfig` so the new shell session still works.

### Cross-session loading

Future read API: pty-speak exposes a CLI command (or
hotkey-triggered loader) that reads previous session
log files and presents them for navigation:

```
pty-speak-replay --session-id <id>
pty-speak-replay --shell powershell --since "2026-05-06"
```

This is item 4 in the strategic backlog (REPL CLI replay
tool); SessionModel persistence is the substrate it
consumes.

### Persistence security

- **No secrets in tuples by default**: command lines
  often contain secrets (e.g. `export
  AWS_SECRET=...`). The maintainer's `SECURITY.md`
  PO-5 row covers this; SessionModel persistence
  layer applies the same env-var sanitiser to
  `commandText` before writing.
- **File permissions**: session log files inherit the
  FileLogger directory's permissions (user-only by
  default on Windows). NTFS ACLs scoped to the user's
  SID.
- **Opt-in only for non-default modes**: default is
  `memory_only`. User must explicitly enable
  `session_log` or `always`. Surface in
  `config.toml.sample` with prominent comment.
- **No PII collection**: pty-speak does not phone home
  with session data. Persistence is local-only.

### Persistence: bounded ring buffer

Even in `memory_only` mode, the in-memory `History`
queue is bounded:

```toml
[session_model]
max_history_size = 100  # tuples
```

Beyond 100 tuples, oldest are dropped (or written to
disk if persistence is enabled, then dropped from
memory). 100 is a sensible default; configurable via
TOML.

The `SessionModel.Apply` method handles eviction
silently — pathways don't need to know about it. Future
queries against evicted tuples return `None`.

## Query API

Pathways and hotkey handlers query the SessionModel
through a clean API. This section specifies the surface.

### Read queries

```fsharp
type SessionModel with
    /// The active tuple, if any. None during
    /// non-active states (between sessions, after
    /// reset, before any boundary fired).
    member Active : ActiveSessionTuple option

    /// All complete tuples in chronological order.
    /// Bounded ring buffer; size <= MaxHistorySize.
    member History : SessionTuple seq

    /// The most-recent complete tuple, if any.
    member LatestTuple : SessionTuple option

    /// The Nth-from-last tuple (0 = latest, 1 =
    /// previous, etc.). None if N >= history size.
    member NthFromLast : int -> SessionTuple option

    /// Look up a tuple by OSC 133 command-ID
    /// (`aid=<id>`). None if no tuple has that ID
    /// (incl. heuristic-detected tuples without ID).
    member TupleByCommandId : string -> SessionTuple option

    /// Tuples matching a predicate. E.g. "all tuples
    /// with non-zero exit codes" (review-failures
    /// hotkey).
    member TuplesWhere : (SessionTuple -> bool) -> SessionTuple seq

    /// Tuples within a time window (since-last-
    /// session-start, or since a wall-clock cutoff).
    member TuplesSince : DateTimeOffset -> SessionTuple seq
```

### Write events (substrate-internal only)

```fsharp
type SessionModel with
    /// Apply a PromptBoundary notification. Updates
    /// state machine + commits / promotes / replaces
    /// active tuple as appropriate. Pump-thread only.
    member internal Apply : PromptBoundaryData -> unit

    /// Append output text to the active tuple's
    /// OutputText. Called by PathwayPump after each
    /// RowsChanged when ActiveSessionTuple.State =
    /// OutputStreaming. Pump-thread only.
    member internal AppendActiveOutput : string -> unit

    /// Append command text to the active tuple's
    /// CommandText. Called by PathwayPump after each
    /// RowsChanged when ActiveSessionTuple.State =
    /// EditingCommand. Pump-thread only.
    member internal AppendActiveCommand : string -> unit

    /// Reset on shell-switch / app-exit. Persists
    /// active tuple as incomplete; clears state.
    member internal PersistAndReset : unit -> unit
```

The write API is `internal` — only PathwayPump and
Program.fs's compose call it. External pathways query
via the read API only.

### Threading guarantees

- All read API methods are safe to call from any
  thread.
- Write API methods MUST be called from the
  PathwayPump thread (single-writer guarantee).
- Internal locking uses a single mutex; reads block
  while writes happen, but writes are short.

### Diagnostic API

```fsharp
type SessionModel with
    /// Snapshot the entire model for inspection.
    /// Used by Ctrl+Shift+D diagnostic battery.
    member SnapshotForDiagnostic : unit -> string

    /// Report the SessionModel's health: tuple count,
    /// active state, persistence mode, recent
    /// boundary sources. Used by health-check
    /// (Ctrl+Shift+H) when SessionModel is enabled.
    member HealthReport : unit -> string
```

## Implementation precedence

The SessionModel substrate is large enough to warrant
incremental shipping. Tiers below sort by
self-containedness and value-per-LOC.

### Tier 1 — substrate skeleton (single PR)

Ship the bare substrate without any pathway integration.
After this tier:

- `SessionModel.fs` exists with the data model + state
  machine + apply method.
- OSC 133 parser added to `Screen.Apply` + new
  `ScreenNotification.PromptBoundary` variant.
- PathwayPump consumes `PromptBoundary` and updates
  SessionModel.
- `DisplayPathway.T` gains `OnPromptBoundary` method
  with default-no-op for StreamPathway / TuiPathway.
- Diagnostic battery's snapshot includes SessionModel
  state for inspection.
- All existing tests pass; no behaviour change for
  current pathways.
- New tests: OSC 133 parser, SessionModel state
  machine, PromptBoundary notification routing.

Estimated scope: ~600-800 LOC + ~300 LOC tests + the
new module. ~2-3 sessions of focused implementation.

This tier alone provides:
- Visibility into shell support (which shells emit
  OSC 133 vs. need fallback).
- Per-tuple persistence in memory (queryable from
  diagnostic battery for debugging).
- The substrate ReplPathway / ClaudeCodePathway etc.
  will build on.

### Tier 2 — heuristic fallback (single PR)

Ship the per-shell fallback for cmd / PowerShell /
Claude Code. Without this, shells that don't emit OSC
133 produce empty SessionModel.

After this tier:

- Heuristic prompt-regex matching per shell.
- Stability check.
- Claude Code Ink-box detection.
- Per-shell parameters in TOML for regex tuning.
- Tests for each fallback mode.

Estimated scope: ~300-500 LOC + ~200 LOC tests.

### Tier 3 — bulk-change-fallback content-set query (single PR)

Ship the proper fix for scroll-misalignment. Stage 8b
(StreamPathway's bulk-change branch) consults
SessionModel's recent-output-text content-set; emits
only rows whose content isn't already in the model.

After this tier:

- Backlog UX issue from 2026-05-06 release validation
  is resolved at the substrate level.
- StreamPathway now benefits from SessionModel even
  though it's not "history-aware" in the full sense.

Estimated scope: ~100-200 LOC + ~100 LOC tests.

### Tier 4 — ReplPathway implementation (multi-PR cycle)

Ship the first SessionModel-aware pathway. Likely
multiple PRs:

- ReplPathway scaffolding + protocol implementation
- ReplPathway tuple-aware emit logic
- ReplPathway atlas parameters
- Per-shell selection: Python REPL / Node REPL /
  ipython auto-detect ReplPathway
- Tests, docs, integration

Estimated scope: ~1500-2500 LOC.

### Tier 5 — FormPathway, ClaudeCodePathway, history navigation (multi-PR cycle)

Each of these is a distinct feature track. They share
the SessionConsumer base.

Estimated scope: ~3000-5000 LOC across all three.

### Tier 6 — persistence + cross-session loading (multi-PR cycle)

> **Vocabulary note (added 2026-05-09):** persistence is
> now tracked under **cycle-number naming** as **Cycle 24**
> (sub-cycles `24a–24e`) per the convention chosen in PR
> #202. The "Tier 6" label below was the design-time
> grouping; the user-facing canonical labels are
> `Cycle 24a` (TOML schema, PR #203 — shipped),
> `Cycle 24b` (JSONL serializer, PR #206 — shipped),
> `Cycle 24c` (file writer — in flight), `Cycle 24d`
> (`Always` sync flush + secrets sanitisation), `Cycle 24e`
> (NVDA matrix + diagnostic helper). See
> `docs/PROJECT-PLAN-2026-05-09.md` (the current 2026-05-09
> successor; supersedes `PROJECT-PLAN-2026-05-revision.md`)
> and `docs/SESSION-HANDOFF.md` for the active sequencing. The
> "Tier 6" framing is preserved here for decision-history
> continuity.

JSONL session-log writer, pty-speak-replay CLI tool
(item 4), config plumbing for persistence modes.

Estimated scope: ~1000-1500 LOC.

### Tier 7 — AI summarisation (Phase 3)

AiInterpretedPathway. Separate research stage; depends
on AI inference pipeline being chosen + privacy /
opt-in flow being designed.

Estimated scope: TBD; large.

### Recommended ordering

Tier 1 → Tier 2 → Tier 3 (resolves UX issue) → Tier 4
(ReplPathway as proof of value) → Tier 5 (broader
pathway taxonomy) → Tier 6 (persistence) → Tier 7 (AI).

Each tier is a multi-PR effort; the substrate work is
~1-2 years of sustained development at the current
session-pace, fully committed. Shorter horizons are
possible if scope is reduced (e.g. ship Tiers 1-3 then
defer the rest).

## Open questions — design decisions still in flux

These are genuine design questions; the maintainer's
input is welcome before implementation cycles start.

### Q1: Cursor in CanonicalState — additive or breaking? — ✅ Resolved 2026-05-07 (shipped Tier 1.B)

**Resolution**: **ADDITIVE; shipped in Tier 1.B**.
`Canonical` record gained a required field
`CursorPosition: (int * int)` per the SESSION-MODEL.md
sketch. `Screen.SnapshotRows` was extended to return
`(seq, cursor, snapshot)` — atomic capture under the
gate lock. 4 production call sites updated; 32 test call
sites updated mechanically. No record-destructuring
patterns existed in production code so the migration
was purely mechanical.

**Rationale**: keeping Tier 1.A scoped to "data types
only" means the cursor field is unnecessary in the first
PR. Bundling it with Tier 1.B (where the OSC 133
producer + heuristic fallback need cursor position to
attribute boundaries to specific row positions) groups
related changes.

**Original question**: Adding `CursorPosition: (int * int)`
to the Canonical record is breaking for any consumer
that destructures it. We could add it as a `voption`
field for backward compat, or bump the record version
with a migration path. Recommend: add as required
field; update all consumers (limited surface —
StreamPathway, TuiPathway, test code).

### Q2: Heuristic fallback default — on or off? — ✅ Resolved 2026-05-08 (Tier 1.D shipped)

**Resolution**: **ON by default for known shells (cmd,
PowerShell, claude); OFF for unknown shells**. Shipped in
Tier 1.D (narrowed-scope split during Cycle 13 planning):
Tier 1.C shipped the SessionModel state machine + composition
wiring; Tier 1.D shipped the heuristic-fallback module that
attaches to RowsChanged notifications.

**Tier 1.D scope (locked via maintainer AskUserQuestion
2026-05-08)**:
- **PromptStart-only emission**. Detector emits
  `BoundaryKind.PromptStart` events only. The Cycle 13 state
  machine's "PromptStart while Active=Some" transition
  auto-finalises the prior tuple as incomplete; tuples carry
  `PromptStartedAt` + `CommandFinishedAt-from-next-prompt`.
  Defers heuristic synthesis of CommandStart / OutputStart /
  CommandFinished (need Phase 2 input framework / cursor-aware
  diff / OSC 133 D respectively).
- **Regex-only Claude detection**. Single regex `^.*│\s>.*$`
  per row, same shape as cmd / PowerShell. Source =
  `BoundarySource.HeuristicPromptRegex 200` (200ms stability
  for Claude's slower TUI cadence; 100ms for cmd / PowerShell).
  Defers full Ink-box context check (multi-row `╭─╮│╰─╯`
  scanning per §275-280) — regex catches the prompt indicator
  without verifying the surrounding box-character context.
- **Hardcoded defaults**. All per-shell parameters baked into
  `HeuristicPromptDetector.fs`. No `Config.fs` schema changes;
  no `[session_model.fallback.*]` TOML sections this cycle.
  TOML wiring per §282-300 + USER-SETTINGS atlas entries
  defer until a pathway makes the params user-actionable.

**Rationale** (per maintainer agreement, audit walk-
through): without OSC 133, heuristic is the only signal
for cmd / PowerShell / Claude (none emit OSC 133 by
default); OFF means SessionModel produces nothing for
the everyday case. Future shells gain heuristics on
opt-in.

**Original question**: For shells that don't emit OSC
133, should heuristic fallback be on by default or off?
Trade-off: on provides degraded service even when
unconfigured; off requires explicit opt-in but never
produces false-positive tuples. Recommend: ON by
default for known shells (cmd, PowerShell, claude);
OFF for unknown shells.

### Q3: SessionModel reset on alt-screen entry? — ✅ Resolved 2026-05-08 (Tier 1.D fully shipped)

**Resolution**: **PAUSE on alt-screen entry; resume on
exit** without losing prior tuples. Tier 1.C shipped the
`IsAltScreenActive` field on `SessionModel.T` plus
`enterAltScreen` / `exitAltScreen` helpers + an
`apply` guard that returns state unchanged when the
flag is set. Tier 1.D closed the wiring: composition root
in `Program.fs:handleModeChanged` calls
`SessionModel.enterAltScreen` on `PathwaySelector.SwapToTui`
and `SessionModel.exitAltScreen` on `SwapToShellDefault`,
alongside the existing pathway swap. The
`HeuristicPromptDetector` is also reset on both transitions
(stale per-row matches would otherwise produce phantom
boundaries on alt-screen exit).

**Rationale** (per maintainer agreement, audit walk-
through): vim / less / TUI sessions aren't
command-output cycles; pausing avoids false-positive
boundary detection during alt-screen activity.

**Original question**: When user enters vim (alt-screen
toggle), should SessionModel pause / reset? The vim
"session" isn't a command/output cycle. Recommend:
pause — SessionModel notes "alt-screen active; no
boundary detection until exit". On alt-screen exit,
resume from where we left off. Don't lose tuples that
were complete before vim opened.

### Q4: Multi-line commands — capture per line or as one? — ✅ Resolved 2026-05-07 (Tier 1.A)

**Resolution**: **ONE string with embedded newlines**.
Captured in `SessionTuple.CommandText: string` per the
Tier 1.A type definition (PR #185 — first post-audit
implementation cycle).

**Rationale**: easier serialisation; matches the
original byte stream's structure; pathways that want
per-line iteration can split. Matches existing F# +
.NET string conventions (`Split('\n')` is
single-allocation).

**Original question**: For commands that span multiple
lines (here-docs, multi-line strings, line-
continuations), should SessionTuple's `CommandText` be
one string with embedded newlines, or an array of
lines? Recommend: one string with newlines. Easier to
serialise; matches the original byte stream's
structure. Pathways that want per-line iteration can
split.

### Q5: How does shell-switch interact with active tuple? — ✅ Resolved 2026-05-07 (Tier 1.C shipped)

**Resolution**: **per-shell-session SessionModel by
default**. Each Ctrl+Shift+1/2/3 hot-switch starts a
fresh SessionModel for the new shell; the active tuple
in the prior shell finalises (option B from the
original question — persist as incomplete with
`ExitCode = None`,
`CommandFinishedAt = shell-switch-time`). **Opt-in
unified history as TOML setting** for power users who
want cross-shell tuple aggregation.

**Tier 1.C implementation**: `SessionModel.finalizeIncomplete:
T -> DateTime -> T` helper moves any active tuple to
History with `ExitCode = None`. Composition root in
`switchToShell` calls `finalizeIncomplete` BEFORE
recreating `currentSession` with the new shell's key.
Tier 1.C has no persistence so the finalised tuple is
structurally discarded with the prior SessionModel; Tier
2 (persistence) will use the same finalize seam to flush
History to disk before recreation.

**Cross-reference**: INTERACTION-MODEL.md Q6 captures
the same decision at the architectural-framing layer
(per-shell Historical Document by default).

**Rationale** (per maintainer agreement, audit walk-
through Cluster 3): simpler default; explicit opt-in
for power users; matches the substrate-first principle
of not pre-committing; preserves user history without
silently losing data on shell switch.

**Original question**: If user runs `dir`, then
Ctrl+Shift+1 to switch shells mid-output (rare but
possible), the active tuple is incomplete. Should
SessionModel:
A. Discard it.
B. Persist it as incomplete (ExitCode = None,
   CommandFinishedAt = shell-switch-time).
C. Error out.
Recommend: B. Preserves user history; never silently
loses data.

### Q6: Echo correlation interaction — ✅ Resolved 2026-05-07 (Tier 1)

**Resolution**: **separate concerns**. Echo correlation
(Phase 2 input framework) tracks per-keystroke timing /
input-to-output matching; SessionModel
(this substrate) tracks boundary-to-boundary text
accumulation. They share the active tuple via the
SessionModel's exposed `Active` field but use it
differently.

**Rationale**: tighter coupling would prematurely
constrain the input framework's design. Phase 2's echo-
correlation work is its own substrate cycle; sharing
the active tuple is sufficient integration.

**Original question**: The Phase 2 input framework's
echo correlation needs to know "what is the user
editing right now". The active tuple's `EditingCommand`
state provides this. But the echo correlation also
tracks per-keystroke timing. Open: should the
echo-correlation tracker BE the SessionModel's
command-text-capture mechanism, or should they be
separate? Recommend: separate. Echo correlation is
about input-to-output matching; the SessionModel's
command capture is about boundary-to-boundary text
accumulation. They share the active tuple but use it
differently.

### Q7: Per-output-block AI summarisation — at C or at D? — ✅ Resolved 2026-05-07 (Tier 3+)

**Resolution**: **at CommandFinished (D)**; streaming
summarisation deferred to a future Phase 3 cycle. Tier 1
captures `OutputText` continuously between
`OutputStart` and `CommandFinished`; the
AiInterpretedPathway (when it ships) consumes the full
`OutputText` post-finalisation.

**Rationale** (per maintainer agreement, audit walk-
through): streaming summarisation is much harder
(requires partial-output AI prompts; risks
hallucination at low context); the simpler "summarise
on finish" path ships first. Streaming-summary
revisited if the simpler version proves insufficient.

**Original question**: For AiInterpretedPathway: when
does the AI summary fire — at OutputStart (so it can
summarise as output streams in)? At CommandFinished
(so it has the full output)? Recommend: at
CommandFinished. Streaming summarisation is much
harder; defer.

### Q8: Should SessionModel know about exit codes' meaning? — ✅ Resolved 2026-05-07 (Tier 1.A)

**Resolution**: **NO**. SessionModel records the value
verbatim as `int option`; pathways / consumers
interpret semantically (e.g. "exit 0 = success",
"exit 1 = grep no-match", per-shell idioms).

**Implementation in Tier 1.A** (PR #185): `SessionTuple.ExitCode: int option`
captures the value or `None` when not reported by the
shell. Future per-shell or per-context rules could be
added in CUSTOMIZATION-MODEL substrate (item 31).

**Original question**: A non-zero exit code typically
indicates failure but not always (some commands use
exit codes as semantic return values, e.g. `grep`
returns 1 for "no match"). Should SessionModel
categorise exit codes? Recommend: no. SessionModel
records the value; pathways / consumers interpret.
Future per-shell rules could be added if needed.

## Out of scope

This document explicitly does NOT cover:

- **Implementation details** — the doc specifies the
  contract; implementation cycles fill it in. Specific
  F# code patterns, struct vs record choices, exact
  collection types beyond what's here.
- **Phase 3 AI inference pipeline** — choosing an AI
  provider, prompt design for summarisation, opt-in
  consent flow. Separate document(s) when Phase 3
  starts.
- **Persistence format-version migrations** — when
  schema changes between releases, the format has to
  cope. Out of scope for v1; address when first
  schema migration happens.
- **Network sync / multi-device history** — not in
  pty-speak's scope. SessionModel is local-only.
- **Privacy-preserving analytics** — pty-speak doesn't
  emit telemetry; SessionModel's data stays on the
  user's machine.
- **VtParser-side testing of OSC 133** — the parser's
  responsibility ends with `OscDispatch` event
  emission. SessionModel's responsibility starts with
  receiving that event. Tests at the boundary live
  in their respective projects.
- **End-user documentation** — how to configure shells
  for OSC 133, how to use history navigation hotkeys,
  etc. Lives in user-facing docs once features ship.

## Cross-references

This doc references / is referenced by:

- [`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md) —
  uses the 12-stage glossary; SessionModel insertion
  point is between Stages 3 and 4 (called "Stage 3.5"
  here for now; future re-numbering possible).
- [`USER-SETTINGS.md`](USER-SETTINGS.md) — SessionModel
  parameters land here as a new section once
  implementation starts.
- [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
  — references `PromptDetected` and `CommandSubmitted`
  as placeholder semantic categories awaiting OSC 133.
  Spec absorbs this doc's data model when
  implementation lands.
- [`spec/overview.md`](../spec/overview.md) —
  references OSC 133 + Claude Code's lack of support;
  this doc operationalises both.
- [`SECURITY.md`](../SECURITY.md) — env-var sanitiser
  applies to `commandText` persistence; no PII
  collection.
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — F#
  conventions for the new module.
- Future [`SHELL-INTEGRATION.md`](SHELL-INTEGRATION.md)
  — recipes for configuring shells to emit OSC 133.
- Future [`CHANGELOG.md`](../CHANGELOG.md) — entries
  per implementation tier.

## Change log

| Date | Change |
|---|---|
| 2026-05-06 | Initial design. OSC 133 protocol scope, heuristic fallback for cmd / PowerShell / Claude Code, data model (SessionModel + SessionTuple + ActiveSessionTuple + BoundaryKind), pipeline integration (Stage 3.5 insertion + new ScreenNotification variant + new DisplayPathway protocol method), pathway-by-pathway integration (StreamPathway, TuiPathway, ReplPathway, FormPathway, ClaudeCodePathway, AiInterpretedPathway, SessionConsumer), persistence story (memory-only / session-log / always modes; JSONL or msgpack format), query API, implementation precedence (7 tiers), 8 open questions. ~1500 lines. |
| 2026-05-07 | Cluster 1-4 open questions resolved per audit walk-through (Q1/Q3/Q4/Q5/Q6/Q7/Q8 resolved; Q2 deferred to Tier 1.D). |
| 2026-05-08 | Tier 1.C state machine + composition wiring shipped. `SessionModel.apply` no-op replaced with full state machine (13+ transitions covering happy path + defensive paths). `IsAltScreenActive` field + `enterAltScreen`/`exitAltScreen` helpers added (Q3 partial — full wiring deferred to Tier 1.D). `finalizeIncomplete` helper added (Q5 — finalises in-flight active tuple as incomplete on shell-switch). Composition root in `Program.fs` declares + recreates `currentSession`; replaces no-op PromptBoundary arm with `handlePromptBoundary` that advances state machine + dispatches active-pathway `OnPromptBoundary`. Q2 (heuristic fallback) deferred to Tier 1.D per narrowed-scope split. |
| 2026-05-08 | Tier 1.D heuristic prompt-boundary fallback + alt-screen wiring shipped. New `src/Terminal.Core/HeuristicPromptDetector.fs` module: per-shell prompt regex (cmd / PowerShell / Claude) + stability window (100ms / 100ms / 200ms) + duplicate suppression. Q2 closed: ON for known shells, OFF for unknown. Q3 closed: composition root in `handleModeChanged` calls `SessionModel.enterAltScreen` / `exitAltScreen` alongside existing pathway swap; detector reset on both transitions. Maintainer-locked scope: PromptStart-only emission, regex-only Claude detection, hardcoded defaults (no TOML this cycle). ~880 LOC + ~33 tests. Behaviour change: SessionModel now populates with tuples for every shipped shell during normal use. Zero user-visible NVDA change. |
| 2026-05-08 | Tier 1.E PromptText capture (text accumulation, part 1) shipped. Maintainer-locked scope: PromptText only; CommandText / OutputText named follow-up Tier 1.E2. Extends `PromptBoundaryData` with optional `MatchedRowText: string option`. HeuristicPromptDetector populates inline (renders the matching row before emit). `Program.fs.handlePromptBoundary` augments OSC 133 boundaries via fresh `screen.SnapshotRows` capture + `CanonicalState.renderRow` over the cursor's row. `SessionModel.apply`'s `newTuple` writes `MatchedRowText` into `Active.Tuple.PromptText` on PromptStart transitions. ~270 LOC + ~7 tests. Behaviour change: SessionModel tuples now carry the prompt-row text for each session boundary. Zero user-visible NVDA change. |
| 2026-05-08 | Tier 1.F diagnostic battery extension shipped — **closes Tier 1 substrate cycle**. Extends `Ctrl+Shift+D` (`Diagnostics.runFullBattery`) with SessionModel + HeuristicPromptDetector state inspection. New `Diagnostics.SessionModelSnapshot` + `RecentTupleView` types + `captureSessionModel` + `formatSessionModelSnapshot` pure helpers in `Diagnostics.fs`. Composition root (`Program.fs.runDiagnostic`) constructs a fifth resolver closure capturing `currentSession` + `promptDetector` + `activePathway.Id`. Substrate state lands in the diagnostic log as a multi-line `[Diagnostic.SessionModel]` block (SessionId / ShellId / IsAltScreenActive / History counts / Active state + PromptText / detector last-emitted prompt + per-row matches size / active pathway / 3 most-recent tuples with prompts + timestamps + exit codes + Tier-1.E2-readiness booleans). NVDA announce gains brief substrate fragment ("SessionModel: K tuples, active state X."). Bundled doc: CONTRIBUTING.md F# `internal` cross-assembly visibility gotcha banked from Cycle 15 fixup. ~340 LOC across 4 files. Behaviour change: Ctrl+Shift+D announce gains ~5-10 spoken words; full state available via clipboard-copied log. |
| 2026-05-08 | Tier 1.D-fix tick-driven detector + channel-driven actor model shipped (Cycle 17). Maintainer's manual NVDA validation 2026-05-08 exposed Cycle 14's frame-driven-only design hole: at idle (no `RowsChanged` events for 6+ seconds after typing), the detector recorded a per-row regex match but never got the second `tryDetect` call to check stability — `Active=none` and `LastEmittedPromptText=none` despite normal use. Cycle 17 corrects via the channel-driven actor model the maintainer locked in via AskUserQuestion: introduces `PumpInput = Notification of ScreenNotification \| Tick of DateTimeOffset` DU; renames `notificationChannel` → `pumpChannel` carrying both case kinds; notification-consumer task becomes the SOLE owner of composition-root mutable state (`currentSession`, `promptDetector`, `activePathway`); `handleTick` helper drives the heuristic detector + `activePathway.Tick` + `OutputDispatcher.dispatchTick` from the consumer thread. Tick-pump simplifies to a pure channel producer. Eliminates the race introduced by Tier 1.F's diagnostic snapshot capture; restores Cycle 13's single-threaded-mutation contract. ~140 LOC net across `src/Terminal.App/Program.fs`. Behaviour change: SessionModel `Active` populates within 100-200ms of any prompt becoming stable, regardless of subsequent idle. |
| 2026-05-08 | Tier 1.E2.A row-index-aware detector emission shipped (Cycle 20a; first half of Tier 1.E2 split per maintainer AskUserQuestion 2026-05-08). Closes the "stable-prompt cmd usage produces zero tuples" gap surfaced by 2026-05-08 manual NVDA validation. Detector emits when `(text, rowIdx)` differs from the last-emitted pair (was: text alone). Catches the cmd case where prompt text is identical across commands but row index changes after a command cycle. New `MatchedRowIndex: int option` field on `PromptBoundaryData` (forward-compat plumbing for Tier 1.E2.B's CommandText / OutputText extraction at finalize time); `LastEmittedPromptRowIndex: int option` on detector state. `Program.fs.handlePromptBoundary` OSC 133 augmentation populates `MatchedRowIndex = Some cursorRow` alongside `MatchedRowText`. ~270 LOC + ~10 tests. Behaviour change: SessionModel History grows on every command in cmd / PowerShell, not just on prompt-text changes. CommandText / OutputText fields stay empty until Tier 1.E2.B (named follow-up). |
| 2026-05-08 | Tier 1.E2.B CommandText + OutputText extraction shipped (Cycle 20b; second half of the Tier 1.E2 split). **Closes Tier 1 substrate cycle end-to-end.** New `ActiveSessionTuple.PromptRowIndex: int option` field (recorded from boundary's `MatchedRowIndex` at PromptStart). `SessionModel.apply` signature extended with `snapshot: Cell[][]` parameter — threaded through `Program.fs.handlePromptBoundary` from the detector's snapshot context (heuristic) or freshly captured (OSC 133). New private `extractContent` helper computes CommandText (old-prompt-row content minus PromptText prefix, with TrimStart) + OutputText (rows between old + new prompts, joined with newlines, empty rows filtered). Defensive checks: empty PromptText, missing row indices, out-of-bounds row indices, scroll-mid-cycle (rendered row doesn't start with captured PromptText) — all skip extraction gracefully. Tier 1.F diagnostic log lines extended with truncated CmdText / OutText (cap 80 chars). ~500 LOC + ~10 new tests. Behaviour change: SessionModel tuples carry full content for typical cmd / PowerShell command cycles. Tier 1 substrate is structurally complete + content-complete; ready for Tier 2 persistence + Phase 2 input framework. |






