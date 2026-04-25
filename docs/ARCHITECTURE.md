# Architecture

This document is a working developer reference. The full rationale,
prior-art survey, and tradeoff analysis live in
[`spec/overview.md`](../spec/overview.md); this file is the operational
map: how data flows, which module owns which concern, and where the
thread boundaries are.

## High-level data flow

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

| Project                         | Layer        | Owns                                                                              |
|---------------------------------|--------------|-----------------------------------------------------------------------------------|
| `Terminal.Core`                 | Domain       | Pure types: `Cell`, `SgrAttrs`, `VtEvent`, `SemanticEvent`, `Earcon`, `BusMessage` |
| `Terminal.Pty.Native`           | Interop      | `[<DllImport>]` signatures, `SafeHandle` subclasses, struct layouts                |
| `Terminal.Pty`                  | Host         | `CreatePseudoConsole` lifecycle, Job Object, child env block                       |
| `Terminal.Parser`               | Stateful     | Williams VT500 state machine; emits `VtEvent`                                      |
| `Terminal.Semantics`            | Stateful     | `ScreenBuffer` (primary + alt + scrollback ring), `VtEvent → SemanticEvent`        |
| `Terminal.EventBus`             | Plumbing     | `BroadcastBlock<SemanticEvent>` + per-consumer `Channel<T>`                        |
| `Terminal.Ui.Wpf`               | UI           | `TerminalView : FrameworkElement`, `OnRender` with `FormattedText`/`GlyphRun`      |
| `Views` (C# WPF)                | UI           | XAML — Elmish.WPF binding layer                                                    |
| `Terminal.Accessibility`        | UIA          | `TerminalAutomationPeer`, `ITextProvider`, `ITextRangeProvider`, list peers        |
| `Terminal.Audio`                | Audio        | `IAudioSink`, `WasapiSink`, `EnvelopedSampleProvider`                              |
| `Terminal.Tts` *(future)*       | Audio        | Piper subprocess sink, SAPI5 sink                                                  |
| `Terminal.Osc` *(future)*       | Audio        | Rug.Osc → SuperCollider sink                                                       |
| `Terminal.Update`               | Distribution | Velopack `UpdateManager` wrapper; Ed25519 manifest verification                    |
| `Terminal.App`                  | Composition  | `Main`, keyboard router, config (Tomlyn), DI graph                                 |

## Threading model

The threading rules below are non-negotiable. Violations cause hangs
(ConPTY) or silent no-ops (UIA).

| Thread                    | Owns                                                                                         |
|---------------------------|----------------------------------------------------------------------------------------------|
| ConPTY read thread        | `ReadFile` from `outputReadSide` into a `Channel<byte[]>`. Synchronous I/O only — ConPTY forbids `OVERLAPPED`. |
| ConPTY write thread       | Drains a write channel into `WriteFile` on `inputWriteSide`.                                 |
| Parser / semantics thread | Single thread that owns the screen buffer and parser state; mutates the buffer; emits events. |
| Earcon thread (NAudio)    | NAudio's own playback thread; receives `AudioEvent` over a bounded channel.                  |
| TPL Dataflow consumers    | One `ActionBlock` per consumer with `MaxDegreeOfParallelism = 1` for FIFO order.             |
| WPF Dispatcher (UI)       | Renders the `TerminalView`; **all** `RaiseNotificationEvent` and `RaiseAutomationEvent` calls. |
| UIA RPC thread            | Microsoft-owned; calls into our `ITextRangeProvider` from outside the Dispatcher. **Snapshots only**, no mutation. |

Marshalling rules:

- Parser thread to UI: `Dispatcher.BeginInvoke` or `Async.SwitchToContext`.
  Heed Windows Terminal's warning that `RunAsync(...).get()` deadlocks
  NVDA's `SignalTextChanged`.
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
- `Terminal.Accessibility/TerminalAutomationPeer.fs` — the UIA peer.
  Mimic `TermControlAutomationPeer.cpp` from microsoft/terminal.
- `Terminal.Semantics/SpinnerDetector.fs` — the row-hash / 5-Hz / 1-s
  rule that prevents Claude Code's spinner from freezing NVDA. This is
  the single biggest accessibility win in Phase 1.

## See also

- Full rationale: [`spec/overview.md`](../spec/overview.md)
- Stage plan: [`spec/tech-plan.md`](../spec/tech-plan.md)
- Roadmap: [`ROADMAP.md`](ROADMAP.md)
- Build: [`BUILD.md`](BUILD.md)
- Release process: [`RELEASE-PROCESS.md`](RELEASE-PROCESS.md)
