# Architecture

This document is a working developer reference. The full rationale,
prior-art survey, and tradeoff analysis live in
[`spec/overview.md`](../spec/overview.md); this file is the operational
map: how data flows, which module owns which concern, and where the
thread boundaries are.

> **Currency note** (snapshot 2026-05-18; module table refreshed
> for Cycle 52 ADR 0006 three-layer refoundation + ADR 0007
> CellEventBus). The pre-Cycle-45 12-stage screen-grid-diff
> pipeline (StreamPathway / LinearTextStream / DisplayPathway /
> TuiPathway / PathwaySelector) was retired by Cycle 45c (PRs
> #274–#278). The aural substrate is `ContentHistory` +
> `SpeechCursor`; `CellEventBus` (ADR 0007 D9, Cycle 52 #404)
> is the parallel typed cell-event bus. Cycle 52 ADR 0006
> landed the transport/core/channel three-layer boundary
> (`Terminal.Shell` adapters; portability-lint-enforced). The
> data-flow ASCII below reflects the substrate but predates
> the `Terminal.Shell` split + `CellEventBus`; the module
> table is current. Sections further down that still
> reference the retired pipeline (PathwayPump threading,
> pathway-specific file inventory) are **historical**. The
> live strategic gate is
> [`docs/adr/0010-interaction-strategy-structured-runner-vs-passthrough.md`](adr/0010-interaction-strategy-structured-runner-vs-passthrough.md)
> (Proposed 2026-05-18) — a structured command-runner would
> add a new transport adapter behind the same ADR 0006 seam.
>
> Pre-Cycle-45 stage definitions remain accessible in the
> archived
> [`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md)
> and the May-2026 audit tracks
> ([`AUDIT-CODE-CONSISTENCY.md`](archive/pre-cycle-45/AUDIT-CODE-CONSISTENCY.md)
> et al.), all under `docs/archive/pre-cycle-45/`. The substrate
> spec is [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md);
> the substrate/channel architectural framing is
> [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md)
> + [`docs/adr/0001-substrate-channel-dichotomy.md`](adr/0001-substrate-channel-dichotomy.md).

## High-level data flow

### Current pipeline (post-Stage-7 substrate; 2026-05-07)

The shipped pipeline runs through the 12 stages catalogued in
PIPELINE-NARRATIVE:

```
cmd.exe / pwsh.exe / claude.exe   ← Ctrl+Shift+1/2/3 hot-switch
       │
       │ UTF-8 VT bytes via ConPTY anonymous pipes
       ▼
Terminal.Pty.ConPtyHost            (Stage 1 byte ingestion;
       │                            P/Invoke wrap; Job-Object lifecycle)
       │ byte chunks via Channel<byte[]>
       ▼
Terminal.Parser.Parser             (Stage 2 parser application;
       │                            Williams VT500 state machine)
       │ VtEvent stream
       ▼
Terminal.Core.Screen.Apply         (cell-grid mutations under gate-lock)
       │ ScreenNotification (Stage 3 notification emission)
       ▼
Terminal.Core.CanonicalState       (Stage 4 canonical-state synthesis;
       │                            snapshot + row hashes + sequence)
       │ CanonicalState
       ▼
Terminal.Core.ContentHistory       (Cycle 45 substrate — append-only
       │                            typed-entry log per shell session;
       │                            TextSpan / Newline / Overwrite /
       │                            Marker / Spinner entries with
       │                            monotonic Seq identity. Replaces
       │                            the Cycle 5-9 StreamPathway
       │                            screen-grid-diff pipeline.)
       │ ContentHistory.Entry[]
       ▼
Terminal.Core.SpeechCursor         (Cycle 45 announce-and-navigate
       │                            primitive over ContentHistory.
       │                            AutoDrive emits new entries to
       │                            NVDA; Manual mode lets the user
       │                            navigate the shell-session
       │                            history.)
       │ OutputEvent[]
       ▼
Terminal.Core.OutputDispatcher     (Stage 10 profile claim;
       │                            Stage 11 channel rendering)
       │ ChannelDecision[]
       ├─→ NvdaChannel       → UIA Notification → NVDA (Stage 12)
       ├─→ EarconChannel     → NAudio WASAPI playback
       └─→ FileLoggerChannel → bounded channel + crash-safe writer
```

Auxiliary substrate per `docs/INTERACTION-MODEL.md` (the Shell
Interaction Manager): KeyEncoding for input transmission;
HotkeyRegistry for app-reserved gestures; Diagnostics for
Ctrl+Shift+D self-test battery; Config for TOML-based parameter
loading; AnnounceSanitiser as the per-row sanitisation
chokepoint.

### Forward-looking substrate

Future research-stage docs reserve names for substrate not yet
implemented:

- **SessionModel** (per `docs/SESSION-MODEL.md`, item 28) — OSC
  133-sourced (prompt, command, output, exit-code) tuple history;
  inserts at Stage 3.5 between notification emission and
  canonical-state synthesis.
- **InputPathway protocol** (Phase 2) — analogous to
  DisplayPathway for input-side; ships echo correlation +
  cursor-aware editing.
- **Pane abstraction** (per `docs/PANE-MODEL.md`, item 30) —
  multi-pane workspace; today's shell view is one pane in a
  future workspace.
- **Customization substrate** (per `docs/CUSTOMIZATION-MODEL.md`,
  item 31) — every pipeline stage gains an alternatives registry
  + per-output trace + override rules.

## Modules

All ✅ rows have code on `main` as of 2026-05-07. 📋 reserved
rows are research-stage substrates not yet implemented (see
`docs/PIPELINE-NARRATIVE.md` for the operational vocabulary +
the linked research-stage docs for design).

### Shipped projects

| Project | Layer | Owns | Status |
|---|---|---|---|
| `Terminal.Core` | Pure session core (ADR 0006) | Pure types (`ColorSpec`, `SgrAttrs`, `Cell`, `Cursor`, `VtEvent`); `Screen`; `Coalescer`; `CanonicalState`; `ContentHistory` (Cycle 45 aural substrate); `SpeechCursor`; `CellEventBus` (ADR 0007 D9 — parallel typed cell-event bus, Cycle 52 #404); `SessionModel`; `Osc133` (pure decoder); `SelectionDetector`; `OutputDispatcher`; `ProfileRegistry`; `PassThroughProfile`; `EarconProfile`; `SelectionProfile`; `NvdaChannel`; `EarconChannel`; `FileLoggerChannel`; `FileLogger`; `Config`; `OutputEventTypes` (incl. `SemanticCategory`, `Priority`, `ActivityIds`); `OutputEventBuilder`; `AnnounceSanitiser`; `KeyEncoding`; `HotkeyRegistry`; `ShellPolicy`; `Logger`. **No WPF / P/Invoke / shell strings / `Terminal.Shell` dependency — CI-enforced by `portability-lint` (Cycle 52 R4b).** | ✅ shipped (Stages 1-3 + 5 + 8a-8d + substrate cycle + Cycle 45; `HeuristicPromptDetector` relocated to `Terminal.Shell` in Cycle 52 R1.2/R4a) |
| `Terminal.Shell` | Transport (ADR 0006: one adapter per shell) | `SessionHost` (one-file orchestration — startup shell-resolution); `ShellEvent` / `ShellAdapter` (`IShellAdapter` — the R2+ boundary contract); `CmdAdapter` (VT-parser wrap + OSC-133 `prompt` injection, Cycle 52 R2 Option B); `PowerShellAdapter` (`-NoExit -EncodedCommand` `prompt`-function emitting `;A`/`;B`/`;D;$LASTEXITCODE` — real exit code; Cycle 52 R5 #374/#375); `HeuristicPromptDetector` (the no-OSC fallback, muted once OSC-133 seen — R3a). Dependency direction Shell → {Core, Pty, Parser}, never the reverse | ✅ shipped (Cycle 52 R1 #348–#352; R2–R4 #353–#357; R5 #374–#375) |
| `Terminal.Pty.Native` | Interop | `[<DllImport>]` signatures, `SafeHandle` subclasses, struct layouts | ✅ shipped (Stage 1) |
| `Terminal.Pty` | Host | `CreatePseudoConsole` lifecycle, `ConPtyHost`, `readerLoop` helper, stdin `FileStream`, `Channel<byte[]>`-based reader; `ShellRegistry` + `EnvBlock` (env-scrub PO-5) | ✅ shipped (Stage 1 + Stage 7 PR-A/PR-B/PR-K env-scrub) |
| `Terminal.Parser` | Stateful | Williams VT500 state machine (`StateMachine.fs`, `Parser.fs`); emits `VtEvent` | ✅ shipped (Stage 2) |
| `Terminal.Audio` | Audio | NAudio-backed `EarconPlayer` + `EarconPalette` (bell-ping 800Hz, error-tone 400Hz, warning-tone 600Hz); WASAPI playback (`WasapiOut` per-play instance) | ✅ shipped (Stage 8d.1) |
| `Terminal.Accessibility` | UIA | `TerminalAutomationPeer` (Edit control type since Cycle 46 PR-B; UIA Notifications API for non-output events; `GetPattern` override returning the Text pattern; `RaiseCaretMovedToTail` for tuple-finalise + SpeechCursor-auto-drive caret-move events); `ContentHistoryTextProvider` (`ITextProvider` over `Func<ContentHistory.T | null>`, materialises via `ContentHistory.tailText` capped at 256 KB); `ContentHistoryTextRange` (full `ITextRangeProvider` with Character / Word / Line / Document / Paragraph / Page / Format units — last three degrade to Line per UIA convention) | ✅ shipped (Stage 4 + Cycle 46 PR-A→PR-D substrate+channel swap) |
| `Views` (C# WPF library) | UI | `MainWindow.xaml`, `App.cs : Application`, `TerminalView : FrameworkElement` (custom `OnRender` over `Screen`); paste handling, focus management, app-reserved hotkey filter | ✅ shipped (Stage 0 + 3b + 6) |
| `Terminal.App` (F# EXE) | Composition | `[<EntryPoint>]`; `VelopackApp.Build().Run()`; full pipeline wiring (`ConPtyHost → Parser → Screen → CanonicalState → ContentHistory → SpeechCursor → OutputDispatcher → channels`); `Diagnostics.fs` Ctrl+Shift+D self-test battery; heartbeat; hotkey dispatch | ✅ shipped (Stages 0 + 3b + 5 + 6 + 7 + Cycle 45) |
| (Velopack `UpdateManager` lives in `Terminal.App`) | Distribution | `runUpdateFlow` + `setupAutoUpdateKeybinding` in `src/Terminal.App/Program.fs`; `Ctrl+Shift+U` triggers; Ed25519 manifest verification returns at v0.1.0+ per `docs/RELEASE-PROCESS.md` | ✅ shipped (Stage 11) — kept in `Terminal.App` (composition root) rather than a dedicated `Terminal.Update` project, per walking-skeleton discipline |

### Reserved (forward-looking; not in code today)

| Substrate | Owns | Reservation | Source doc |
|---|---|---|---|
| SessionModel | (prompt, command, output, exit-code) tuples sourced from OSC 133 + heuristic fallback; per-shell-session by default | item 28 (Phase 2 implementation = FIRST POST-AUDIT IMPLEMENTATION CYCLE) | `docs/SESSION-MODEL.md` |
| InputPathway protocol | Echo correlation; cursor-aware editing; structured input pathway | Phase 2 input framework cycle | `docs/INTERACTION-MODEL.md` §5.a (Input Composition Surface) |
| Pane abstraction + Pane Coordinator | Multi-pane workspace; today's shell view is one pane | item 30 (Phase 2/3) | `docs/PANE-MODEL.md` |
| Customization substrate | Alternatives registry per stage; per-output trace; override rules; Pipeline Inspector pane | item 31 (Phase 2/3) | `docs/CUSTOMIZATION-MODEL.md` |
| ClaudeCodePathway / ReplPathway / FormPathway / AiInterpretedPathway | Per-shell-class display pathways with semantic awareness | Phase 2/3 | `docs/PIPELINE-NARRATIVE.md` pathway taxonomy |
| `Terminal.Tts` (future) | Piper subprocess sink, SAPI5 sink (alongside NVDA's existing UIA path) | Phase 2/3 | `spec/event-and-output-framework.md` Part B.4 deferred channels |
| Spatial audio (`Terminal.Osc` originally; now `EarconAt of Earcon * Position3D`) | Rug.Osc → SuperCollider sink; ASIO output | Phase 3 | `spec/event-and-output-framework.md` Part B.4 |

Note: original draft project names `Terminal.Semantics` and
`Terminal.EventBus` (per the original tech-plan) never
materialised as separate projects. Per walking-skeleton
discipline, semantic-event derivation lives in
`Terminal.Core.Coalescer` + `Terminal.Core.CanonicalState` +
`Terminal.Core.StreamPathway`; event-bus plumbing lives in
`Terminal.Core.OutputDispatcher` (event-tap mechanism per
PR #165) + `Terminal.Core.ChannelRegistry`.

## Threading model

The threading rules below are non-negotiable. Violations cause
hangs (ConPTY) or silent no-ops (UIA).

### Today (post-Stage-7 substrate; 2026-05-07)

| Thread | Owns |
|---|---|
| ConPTY read thread | `Terminal.Pty.ConPtyHost`'s dedicated reader (composed in `Terminal.App.Program.startReaderLoop`): synchronous `ReadFile` from `outputReadSide` into a bounded `Channel<byte array>`. Synchronous I/O only — ConPTY forbids `OVERLAPPED`. |
| ConPTY write thread | `ConPtyHost.WriteBytes` — drains keystroke bytes from `KeyEncoding` into `WriteFile` on `inputWriteSide` (Stage 6). |
| Parser / Screen mutation thread | Single thread that owns the screen buffer + parser state; reads byte chunks from the read-thread channel; runs `Parser` → `Screen.Apply`; emits `ScreenNotification` events under gate-lock. |
| Notification-consumer thread (Cycle 17 actor model) | Drains `ScreenNotification` + `Tick` events from `pumpChannel`. Single owner of composition-root mutable state (`currentSession`, `promptDetector`, `selectionDetector`, `contentHistory`, `speechCursor`). Calls `CanonicalState.create` for snapshot context, runs the heuristic detectors, appends OSC 133 markers to `ContentHistory`, dispatches to profiles via `OutputDispatcher.dispatch`. Cycle 45c retired the per-shell pathway-swap path (alt-screen now just toggles `SessionModel.enterAltScreen` / `exitAltScreen`). Historically called "PathwayPump"; the name lingers in some log lines + comments. |
| FileLogger writer thread | `FileLoggerChannel` enqueues to a bounded channel; the writer dequeues + writes through (`AutoFlush = true` + `FileShare.ReadWrite`); crash-safe per-line semantics. |
| Earcon thread (NAudio) | NAudio's own playback thread (per-play `WasapiOut` instance per PR #158); receives earcon-id requests via `EarconChannel`. |
| WPF Dispatcher (UI) | Renders the `TerminalView` from immutable cell snapshots; **all** UIA `RaiseNotificationEvent` and `RaiseAutomationEvent` calls. Marshals from PathwayPump via `Dispatcher.InvokeAsync`. |
| UIA RPC thread | Microsoft-owned; calls into our `ITextRangeProvider` from outside the Dispatcher. **Snapshots only**, no mutation. |
| Diagnostic battery thread | `Terminal.App.Diagnostics` — when Ctrl+Shift+D fires, runs the self-test battery on a worker; uses `OutputDispatcher.installEventTap` (PR #165) to capture events; writes through to a per-run diagnostic log file (crash-safe like FileLogger). |
| Heartbeat thread | 5-second background heartbeat per `runHeartbeat` in `Program.fs`; logs `Pid={Pid} Alive={Alive}` for post-hoc liveness diagnosis. |

### Forward-looking thread additions

When **SessionModel substrate** ships (Tier 1 implementation),
expect:
- Per-tuple lifecycle managed on the PathwayPump thread
  (or its successor); no new dedicated thread.
- OSC 133 detection inline with `Screen.Apply` events.
- Persistence (if enabled) via a separate writer thread per
  the FileLogger pattern.

When **Phase 2 input framework** ships:
- Echo correlation lives at the keystroke / pathway-pump
  intersection.
- Likely no new thread — the existing PathwayPump becomes
  bidirectional (input + output cuts).

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

If you only have time to read five files when you start
contributing:

- `Terminal.Parser/StateMachine.fs` + `Parser.fs` — the
  Williams VT500 state machine. The cleanest reference is
  `alacritty/vte`; the F# DU port keeps the same caps
  (`MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`,
  `MAX_OSC_RAW = 1024`).
- `Terminal.Core/Screen.fs` — the screen model. Mutable buffer
  with `Apply(VtEvent)` covering Print + auto-wrap + scroll,
  BS/HT/LF/CR, CSI cursor moves and erases, SGR (basic-16 +
  256-cube + Truecolor), DECSC/DECRC, alt-screen 1049 back-
  buffer, and the OSC 52 SECURITY-CRITICAL silent drop.
- `Terminal.Core/ContentHistory.fs` — the post-Cycle-45 aural
  substrate: append-only typed-entry log per shell session.
  Entries (`TextSpan`, `Newline`, `Overwrite`, `Marker`, `Spinner`)
  carry a monotonic `Seq`. Query helpers: `tryLatestMarker`,
  `sliceText`, `tailText`. Replaced the Cycle 5-9 StreamPathway
  / LinearTextStream pipeline (PRs #263–#270 + #274–#278).
- `Terminal.Core/SpeechCursor.fs` — announce-and-navigate
  primitive over `ContentHistory`. AutoDrive emits new entries to
  NVDA; Manual mode (`Ctrl+Shift+Up/Down` / Home / End) lets the
  user navigate the session history.
- `Terminal.Core/OutputDispatcher.fs` — Profile/Channel routing
  + event-tap mechanism (`installEventTap` per PR #165, the
  diagnostic battery's introspection seam).
- `Terminal.Accessibility/TerminalAutomationPeer.fs` — the UIA
  peer. Document role + UIA Notifications API + `GetPattern`
  override returning Text pattern. Mimics
  `TermControlAutomationPeer.cpp` from microsoft/terminal.
  Snapshot-based ranges; never locks the buffer.

Substrate research-stage docs to read alongside the code:

- [`docs/PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md) — the
  12-stage pipeline glossary (operational vocabulary).
- [`docs/INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — the
  Shell Interaction Manager + three-component model.
- [`docs/SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking
  history substrate.
- [`docs/PANE-MODEL.md`](PANE-MODEL.md) — multi-pane workspace
  framework.
- [`docs/CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) —
  user-introspectable + customizable pipeline principle.

## See also

### Spec + strategic plans
- Full rationale: [`spec/overview.md`](../spec/overview.md)
- Original stage plan: [`spec/tech-plan.md`](../spec/tech-plan.md)
- Post-Stage-7 substrate spec: [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
- Roadmap: [`ROADMAP.md`](ROADMAP.md)
- Strategic plan (snapshot 2026-05-12): [`PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md). Predecessor revisions archived under [`archive/pre-cycle-45/`](archive/pre-cycle-45/).

### Substrate research-stage docs
- Pipeline vocabulary: [`PIPELINE-NARRATIVE.md`](archive/pre-cycle-45/PIPELINE-NARRATIVE.md)
- History substrate: [`SESSION-MODEL.md`](SESSION-MODEL.md)
- Architectural framing: [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md)
- UI composition: [`PANE-MODEL.md`](PANE-MODEL.md)
- User-customization: [`CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md)

### Audit-track docs
- Code consistency: [`AUDIT-CODE-CONSISTENCY.md`](archive/pre-cycle-45/AUDIT-CODE-CONSISTENCY.md)
- Test inventory: [`AUDIT-TEST-INVENTORY.md`](archive/pre-cycle-45/AUDIT-TEST-INVENTORY.md)
- Spec alignment: [`AUDIT-SPEC-ALIGNMENT.md`](archive/pre-cycle-45/AUDIT-SPEC-ALIGNMENT.md)
- Atlas alignment: [`AUDIT-ATLAS-ALIGNMENT.md`](archive/pre-cycle-45/AUDIT-ATLAS-ALIGNMENT.md)
- Doc currency: [`AUDIT-DOC-CURRENCY.md`](archive/pre-cycle-45/AUDIT-DOC-CURRENCY.md)
- Backlog validation: [`AUDIT-BACKLOG-VALIDATION.md`](archive/pre-cycle-45/AUDIT-BACKLOG-VALIDATION.md)

### Operational / release
- Build: [`BUILD.md`](BUILD.md)
- Release process: [`RELEASE-PROCESS.md`](RELEASE-PROCESS.md)
- ConPTY platform notes: [`CONPTY-NOTES.md`](CONPTY-NOTES.md)
- Stable checkpoints / rollback: [`CHECKPOINTS.md`](CHECKPOINTS.md)
- Stage 11 update-failure NVDA reference: [`UPDATE-FAILURES.md`](UPDATE-FAILURES.md)
- User settings catalog: [`USER-SETTINGS.md`](USER-SETTINGS.md)
- Manual smoke-test matrix: [`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
- Logging architecture: [`LOGGING.md`](LOGGING.md)
- Doc index: [`DOC-MAP.md`](DOC-MAP.md)
