# Architecture

This document is a working developer reference. The full rationale,
prior-art survey, and tradeoff analysis live in
[`spec/overview.md`](../spec/overview.md); this file is the operational
map: how data flows, which module owns which concern, and where the
thread boundaries are.

> **Currency note.** The diagrams and module table below are the
> *target* architecture from `spec/overview.md`. As of `main` at
> Stage 3b, only the rows annotated **(implemented)** are in code; the
> rest land in the stages noted in parentheses. See
> [`docs/ROADMAP.md`](ROADMAP.md) and
> [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) for what has actually shipped.

## High-level data flow

### Current pipeline (Stage 3b on `main`)

```
cmd.exe → ConPTY → Terminal.Pty (ConPtyHost) → Terminal.Parser
                                               → Terminal.Core.Screen
                                               → Views.TerminalView (WPF)
```

Mutation happens on the WPF Dispatcher thread for now (Stage 3b);
Stage 5 carves the parser/screen ownership onto a dedicated thread.

### Target pipeline (post-Stage 5)

```
+-----------------------------+
|        cmd.exe / claude.exe |   child process
+--------------+--------------+
               |
               | UTF-8 VT bytes
               v
+--------------+--------------+
|   ConPTY (kernel)           |   Windows pseudo-console
+--------------+--------------+
               |
               | anonymous pipes (separate threads in/out)
               v
+--------------+--------------+
|   Terminal.Pty / .Native    |   P/Invoke + Job Object lifecycle
+--------------+--------------+
               |
               | ReadOnlySequence<byte>
               v
+--------------+--------------+
|   Terminal.Parser           |   Williams VT500 state machine
+--------------+--------------+
               |
               | VtEvent stream
               v
+--------------+--------------+
|   Terminal.Semantics        |   Screen model + SemanticEvent derivation
+--------------+--------------+
               |
               | SemanticEvent
               v
+--------------+--------------+
|   Terminal.EventBus         |   BroadcastBlock + bounded Channels
+----+----------+--------+----+
     |          |        |
     |          |        v
     |          |  +-----+----------+
     |          |  | Terminal.Audio |   IAudioSink → WasapiOut (Earcons)
     |          |  +----------------+
     |          v
     |   +------+---------+
     |   | Terminal.Ui.Wpf|   Elmish.WPF host + TerminalView
     |   +------+---------+
     |          |
     |          v
     |   +------+---------------+
     |   | Terminal.Accessibility|  WPF AutomationPeer + UIA providers
     |   +-----------------------+
     v
+----+----+
| Logger  |   Serilog (planned)
+---------+
```

## Modules

`(implemented)` rows have code on `main` today; other rows land in the
stage shown in parentheses.

| Project                         | Layer        | Owns                                                                                 | Status                |
|---------------------------------|--------------|--------------------------------------------------------------------------------------|-----------------------|
| `Terminal.Core`                 | Domain       | Pure types: `ColorSpec`, `SgrAttrs`, `Cell`, `Cursor`, `VtEvent`, plus `Screen` (mutable buffer) | implemented (3a)      |
| `Terminal.Pty.Native`           | Interop      | `[<DllImport>]` signatures, `SafeHandle` subclasses, struct layouts                  | implemented (1)       |
| `Terminal.Pty`                  | Host         | `CreatePseudoConsole` lifecycle, `ConPtyHost`, stdin `FileStream`, `Channel<byte[]>` reader | implemented (1); Job Object lifecycle deferred |
| `Terminal.Parser`               | Stateful     | Williams VT500 state machine; emits `VtEvent`                                        | implemented (2)       |
| `Terminal.Audio`                | Audio        | Placeholder; `IAudioSink`, `WasapiSink` arrive in Stage 9                            | placeholder           |
| `Terminal.Accessibility`        | UIA          | Placeholder; `TerminalAutomationPeer`, `ITextProvider`, `ITextRangeProvider` in Stage 4 | placeholder           |
| `Views` (C# WPF library)        | UI           | `MainWindow.xaml`, `App.cs : Application`, `TerminalView : FrameworkElement` (custom `OnRender` over `Screen`) | implemented (0, 3b) |
| `Terminal.App` (F# EXE)         | Composition  | `[<EntryPoint>]`, `VelopackApp.Build().Run()`, `ConPtyHost → Parser → Screen → TerminalView` wiring | implemented (0, 3b) |
| `Terminal.Semantics` *(future)* | Stateful     | `VtEvent → SemanticEvent` (spinner detection, list detection, OSC sanitisation)      | Stage 5+              |
| `Terminal.EventBus` *(future)*  | Plumbing     | `BroadcastBlock<SemanticEvent>` + per-consumer `Channel<T>`                          | Stage 5+              |
| `Terminal.Tts` *(future)*       | Audio        | Piper subprocess sink, SAPI5 sink                                                    | Phase 2               |
| `Terminal.Osc` *(future)*       | Audio        | Rug.Osc → SuperCollider sink                                                         | Phase 3               |
| `Terminal.Update` *(future)*    | Distribution | Velopack `UpdateManager` wrapper; Ed25519 manifest verification                      | Stage 11              |

## Threading model

The threading rules below are non-negotiable. Violations cause hangs
(ConPTY) or silent no-ops (UIA).

### Today (Stage 3b on `main`)

| Thread                    | Owns                                                                                         |
|---------------------------|----------------------------------------------------------------------------------------------|
| ConPTY read thread        | `ConPtyHost`'s dedicated reader `Task`: `ReadFile` from `outputReadSide` into a bounded `Channel<byte array>`. Synchronous I/O only — ConPTY forbids `OVERLAPPED`. |
| WPF Dispatcher (UI)       | Reads chunks from the channel via `Dispatcher.InvokeAsync`, feeds them through the `Parser`, applies `VtEvent`s to the `Screen`, and invalidates the `TerminalView` for repaint. Mutation and rendering both run here for now. |

### Target (post-Stage 5)

| Thread                    | Owns                                                                                         |
|---------------------------|----------------------------------------------------------------------------------------------|
| ConPTY read thread        | Unchanged — produces bytes into a channel.                                                   |
| ConPTY write thread       | Drains a write channel into `WriteFile` on `inputWriteSide` (Stage 6).                       |
| Parser / semantics thread | Single thread that owns the screen buffer and parser state; mutates the buffer; emits `SemanticEvent`s. |
| Earcon thread (NAudio)    | NAudio's own playback thread; receives `AudioEvent` over a bounded channel (Stage 9).         |
| TPL Dataflow consumers    | One `ActionBlock` per consumer with `MaxDegreeOfParallelism = 1` for FIFO order.             |
| WPF Dispatcher (UI)       | Renders the `TerminalView` from immutable snapshots; **all** `RaiseNotificationEvent` and `RaiseAutomationEvent` calls (Stage 4+). |
| UIA RPC thread            | Microsoft-owned; calls into our `ITextRangeProvider` from outside the Dispatcher. **Snapshots only**, no mutation (Stage 4+). |

Marshalling rules:

- Parser/semantics thread to UI: `Dispatcher.InvokeAsync` (preferred),
  `Dispatcher.BeginInvoke`, or `Async.SwitchToContext`. Heed Windows
  Terminal's warning that `RunAsync(...).get()` deadlocks NVDA's
  `SignalTextChanged`.
- UIA RPC to anywhere: don't. UIA hands you a snapshot range request;
  return a snapshot. The snapshot is an immutable array of rows — cheap
  to keep around, safe to read off-thread.

## Key architectural commitments

These are locked in from day one because reversing them later is a
rewrite. See the spec for justification.

1. **Typed event stream as the canonical layer.** Every consumer (UI,
   UIA, earcons, logging, future TTS, future OSC) reads from the same
   `SemanticEvent` stream. New consumers do not require parser changes.
2. **`IAudioSink` from day one.** v1 implements `WasapiSink`; the
   abstraction already accepts spatial coordinates and OSC bundles.
3. **UIA `Notification` events for streaming, not `TextChanged`.** Per
   NVDA PR #14047, `TextChanged` is the wrong tool for terminal output.
   We intentionally do not implement `ITextProvider2.TextChangedEvent`
   on the streaming path.
4. **Snapshot-based UIA ranges.** Mutation and read live on different
   threads. Each `ITextRangeProvider` holds an immutable snapshot of
   the rows it covers. We never lock the buffer to serve a UIA call.
5. **Heuristic list detection, not vendor-specific parsing.** We never
   import Ink internals. The list-detection heuristic (stable
   rectangular region + single-row SGR diff + arrow-key correlation)
   works for any TUI, not just Claude Code.
6. **No clipboard write from the child.** OSC 52 is not a feature we
   add later. See [`SECURITY.md`](../SECURITY.md).
7. **`bInheritHandles = FALSE`.** ConPTY duplicates handles via the
   attribute list. We never inherit pipe handles into the child.

## Where the magic lives

If you only have time to read three files when you start contributing:

- `Terminal.Parser/StateMachine.fs` — the Williams VT500 state machine.
  The cleanest reference is `alacritty/vte`; the F# DU port keeps the
  same caps (`MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`,
  `MAX_OSC_RAW = 1024`).
- `Terminal.Core/Screen.fs` — the screen model. Mutable buffer with
  `Apply(VtEvent)` covering Print + auto-wrap + scroll, BS/HT/LF/CR,
  CSI cursor moves and erases, and basic-16 SGR.
- `Views/TerminalView.cs` — the WPF custom `FrameworkElement` that
  renders the buffer. `OnRender` coalesces same-attr cell runs into
  single `FormattedText`s; backgrounds drawn first, text on top,
  manual underline at baseline.

Coming attractions worth knowing about in advance:

- `Terminal.Accessibility/TerminalAutomationPeer.fs` (Stage 4) — the
  UIA peer. Mimic `TermControlAutomationPeer.cpp` from
  microsoft/terminal.
- `Terminal.Semantics/SpinnerDetector.fs` (Stage 5+) — the row-hash /
  5-Hz / 1-s rule that prevents Claude Code's spinner from freezing
  NVDA. This is the single biggest accessibility win in Phase 1.

## See also

- Full rationale: [`spec/overview.md`](../spec/overview.md)
- Stage plan: [`spec/tech-plan.md`](../spec/tech-plan.md)
- Roadmap: [`ROADMAP.md`](ROADMAP.md)
- Build: [`BUILD.md`](BUILD.md)
- Release process: [`RELEASE-PROCESS.md`](RELEASE-PROCESS.md)
- ConPTY platform notes (observed quirks): [`CONPTY-NOTES.md`](CONPTY-NOTES.md)
- Stable checkpoints / rollback: [`CHECKPOINTS.md`](CHECKPOINTS.md)
