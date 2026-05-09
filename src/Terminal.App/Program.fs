namespace PtySpeak.App

open System
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Controls
open System.Windows.Input
open System.Windows.Threading
open Microsoft.Extensions.Logging
open Velopack
open Velopack.Sources
open Terminal.Core
open Terminal.Core.Channels
open Terminal.Audio
open Terminal.Parser
open Terminal.Pty
open PtySpeak.Views

module Program =

    let private ScreenRows = 30
    let private ScreenCols = 120

    /// GitHub Releases endpoint Velopack pulls update metadata
    /// from. `prerelease=true` because Stage 11 ships on the
    /// `0.0.x-preview.N` channel; once the line graduates to
    /// `v0.1.0+` we'll flip this to honour the channel marker
    /// from `Directory.Build.props` rather than blanket-true.
    let private UpdateRepoUrl = "https://github.com/KyleKeane/pty-speak"

    /// Single in-flight guard so the user pressing Ctrl+Shift+U
    /// repeatedly while a download is mid-flight doesn't kick
    /// off concurrent UpdateManager tasks. Dispatcher-thread
    /// only — no Interlocked needed because all writes happen
    /// from the keybinding handler which runs on the UI thread.
    let mutable private updateInProgress = false

    /// SessionModel Tier 1.D-fix (Cycle 17) — channel-driven
    /// actor model. The notification consumer task becomes the
    /// SOLE owner of composition-root mutable state
    /// (`currentSession`, `promptDetector`, `activePathway`).
    /// Producers (parser reader thread, screen-event subscribers,
    /// tick-pump) enqueue `PumpInput` values into a single
    /// `BoundedChannel<PumpInput>`; the consumer reads serially.
    /// Eliminates the race introduced by Tier 1.F's diagnostic-
    /// snapshot capture + the new tick-driven detector
    /// invocation that closes Cycle 14's idle-gap hole.
    ///
    /// **Architectural alignment** (maintainer principle
    /// 2026-05-08): channels are the canonical inter-thread
    /// communication primitive in pty-speak. Code-level limits
    /// (where pure functions / Event<T> / direct mutables stay
    /// idiomatic) are documented in the Cycle 32 research-stage
    /// doc `docs/CHANNEL-ARCHITECTURE.md` (deferred backlog
    /// item; not yet shipped).
    type PumpInput =
        /// A `ScreenNotification` produced by the parser reader
        /// thread or a `Screen.fs` event subscriber.
        | Notification of ScreenNotification
        /// A 50ms tick produced by the existing PathwayTickPump
        /// (now simplified to a pure channel producer). Drives
        /// the heuristic detector + `activePathway.Tick` +
        /// `OutputDispatcher.dispatchTick` from the consumer
        /// thread.
        | Tick of DateTimeOffset

    /// Wire a freshly-spawned ConPtyHost into the screen + view. Spawns
    /// a single background task that pulls byte chunks off the host's
    /// stdout channel, feeds them through the parser, applies the
    /// resulting VtEvents to the screen (on the WPF dispatcher thread
    /// for Stage 3b), and publishes a `RowsChanged` to the
    /// notification channel so the UIA peer can `RaiseNotificationEvent`
    /// for NVDA. Per the audit-cycle PR-B plan, this is the seam
    /// Stage 5's coalescer plugs into: today every applied batch
    /// produces one notification, and Stage 5 will insert a
    /// debounce/dedup layer between this publish and the consumer
    /// without changing the contract.
    ///
    /// Unexpected exceptions in the loop publish a `ParserError`
    /// rather than being swallowed (closes the cross-cutting
    /// "parser exceptions are silently swallowed" gap from the
    /// audit). `OperationCanceledException` is still treated as
    /// the normal shutdown path and not surfaced to the user.
    let private startReaderLoop
            (dispatcher: Dispatcher)
            (host: ConPtyHost)
            (parser: Parser)
            (screen: Screen)
            (view: TerminalView)
            (notifications: System.Threading.Channels.ChannelWriter<PumpInput>)
            (onChunkRead: int -> unit)
            (ct: CancellationToken) : Task =
        Task.Run(fun () ->
            task {
                let logger = Logger.get "Terminal.App.startReaderLoop"
                try
                    while not ct.IsCancellationRequested do
                        let! chunk = host.Stdout.ReadAsync(ct).AsTask()
                        if chunk.Length > 0 then
                            // Stage 7-followup PR-F — record the
                            // moment of last live PTY activity. The
                            // heartbeat timer + Ctrl+Shift+H health
                            // check both read this to detect reader
                            // staleness (a wedged reader vs a quiet
                            // shell).
                            onChunkRead chunk.Length
                            let events = Parser.feedArray parser chunk
                            if events.Length > 0 then
                                let action () =
                                    for e in events do screen.Apply(e)
                                    view.InvalidateScreen()
                                let! _ =
                                    dispatcher
                                        .InvokeAsync(Action(action))
                                        .Task
                                // Stage 5 will refine "which rows
                                // changed" once the coalescer lands;
                                // for the seam we publish an empty
                                // list (the consumer treats it as
                                // "something changed, you decide what
                                // to read"). A future revision can
                                // compute the row-set from screen
                                // sequence number deltas.
                                let written =
                                    notifications.TryWrite(
                                        Notification (RowsChanged []))
                                // Streaming-path instrumentation at
                                // Debug — production default
                                // (Information) is silent; flip the
                                // min-level via env override to
                                // capture the parser → channel seam
                                // when diagnosing streaming-silence
                                // bugs. Metadata only (chunk byte
                                // count + event count); never the
                                // bytes themselves.
                                logger.LogDebug(
                                    "Reader published RowsChanged. ChunkBytes={ChunkBytes} Events={Events} ChannelAccepted={ChannelAccepted}",
                                    chunk.Length, events.Length, written)
                                ()
                with
                | :? OperationCanceledException -> ()
                // Stage 7-followup PR-I — clean-shutdown exceptions
                // from the underlying ConPTY pipe / channel shouldn't
                // be reported as parser errors. The empirical NVDA
                // pass on 2026-05-03 surfaced a spurious "Terminal
                // parser error: ...the channel has been closed."
                // announcement firing every time the user pressed
                // Ctrl+Shift+1 / Ctrl+Shift+2 (shell hot-switch),
                // because `switchToShell` disposes the old
                // ConPtyHost — completing its internal stdout
                // channel — and `host.Stdout.ReadAsync(ct)` then
                // throws ChannelClosedException, which the
                // catch-all arm below mis-classifies as a real
                // parser/reader fault.
                //
                // The three exception types caught here all indicate
                // the pipe / channel was intentionally shut down:
                //   * ChannelClosedException — channel completed
                //     (host.Dispose calls chan.Writer.TryComplete()).
                //   * IOException — underlying pipe handle was
                //     closed while a read was in flight.
                //   * ObjectDisposedException — the FileStream /
                //     SafeFileHandle was disposed mid-read.
                // Treat all three as silent shutdown.
                | :? System.Threading.Channels.ChannelClosedException -> ()
                | :? System.IO.IOException -> ()
                | :? ObjectDisposedException -> ()
                | ex ->
                    // Audit-cycle SR-2: sanitise ex.Message before
                    // it reaches NVDA via the notification channel.
                    // Reader-loop exceptions can wrap arbitrary
                    // parser internals; control characters in the
                    // message would confuse NVDA's notification
                    // handler. See SECURITY.md TC-5.
                    let safe = AnnounceSanitiser.sanitise ex.Message
                    let _ =
                        notifications.TryWrite(
                            Notification
                                (ParserError(
                                    sprintf "Parser/reader loop: %s" safe)))
                    ()
            } :> Task)

    /// Run the Velopack auto-update flow on a background task,
    /// announcing each phase via NVDA Notification events.
    ///
    /// Stage 11 (this code path) is the in-app replacement for
    /// the standalone `scripts/install-latest-preview.ps1`
    /// bridge: pressing Ctrl+Shift+U from inside the running
    /// app fetches the next preview's delta-nupkg from GitHub
    /// Releases, downloads ~KB-sized binary diff (rather than
    /// the ~66 MB full setup), and restarts in-place via
    /// Velopack's `ApplyUpdatesAndRestart`. No SmartScreen
    /// prompt, no UAC, no installer dialog.
    ///
    /// All UpdateManager calls happen on a background task;
    /// announcements marshal back to the WPF dispatcher so
    /// NVDA's UIA event raise is on the right thread.
    let private runUpdateFlow (window: MainWindow) : unit =
        if updateInProgress then
            window.TerminalSurface.Announce(
                "Update already in progress; ignoring repeat keypress.")
        else
            updateInProgress <- true

            let announce (msg: string) =
                window.Dispatcher.Invoke(fun () ->
                    window.TerminalSurface.Announce(msg))

            let task =
                task {
                    try
                        let source = GithubSource(UpdateRepoUrl, null, true)
                        let mgr = UpdateManager(source)

                        if not mgr.IsInstalled then
                            announce
                                "Auto-update is only available in installed builds. Use scripts/install-latest-preview.ps1 for development copies."
                        else
                            announce "Checking for updates..."
                            let! info = mgr.CheckForUpdatesAsync()
                            match Option.ofObj info with
                            | None ->
                                announce "No update available; you are on the latest version."
                            | Some updateInfo ->
                                let version = updateInfo.TargetFullRelease.Version
                                announce (sprintf "Downloading update %A" version)
                                // Progress callback fires on the
                                // download thread; coalesce to
                                // 25% buckets so NVDA isn't
                                // spammed. Velopack's API takes
                                // a raw Action<int>, NOT an
                                // IProgress<int> — using
                                // Progress<int> trips FS0001.
                                let lastBucket = ref -1
                                let progress =
                                    Action<int>(fun pct ->
                                        let bucket = pct / 25
                                        if bucket > lastBucket.Value then
                                            lastBucket.Value <- bucket
                                            announce (sprintf "%d percent downloaded" pct))
                                do! mgr.DownloadUpdatesAsync(updateInfo, progress)
                                announce "Restarting to apply update..."
                                mgr.ApplyUpdatesAndRestart(updateInfo)
                    with ex ->
                        // Audit-cycle PR-D extracted the
                        // exception → user-message mapping into
                        // the pure `UpdateMessages.announcementForException`
                        // function in Terminal.Core so the
                        // four cases (HttpRequestException →
                        // network message, TaskCanceledException →
                        // timeout message, IOException → disk
                        // message, catch-all → generic message)
                        // are unit-testable without mocking
                        // Velopack's UpdateManager. New Velopack
                        // exception types (e.g. SignatureMismatch
                        // when signing returns) get added as
                        // discrete branches in that module; this
                        // call site doesn't change.
                        announce (UpdateMessages.announcementForException ex)

                    updateInProgress <- false
                }
            task |> ignore

    /// Pre-framework-cycle PR-O — translate
    /// `HotkeyRegistry.HotkeyKey` to WPF `Key`. Lives at the
    /// app-level boundary because Terminal.Core deliberately
    /// keeps WPF dependencies out per `KeyEncoding`'s pattern.
    /// Failure modes throw rather than degrade — a HotkeyKey
    /// that doesn't translate is a registry / translator
    /// inconsistency that should be caught at startup, not
    /// silently dropped.
    let private translateHotkeyKey
            (k: HotkeyRegistry.HotkeyKey)
            : Key =
        match k with
        | HotkeyRegistry.Letter c ->
            match System.Char.ToUpperInvariant(c) with
            | 'A' -> Key.A | 'B' -> Key.B | 'C' -> Key.C | 'D' -> Key.D
            | 'E' -> Key.E | 'F' -> Key.F | 'G' -> Key.G | 'H' -> Key.H
            | 'I' -> Key.I | 'J' -> Key.J | 'K' -> Key.K | 'L' -> Key.L
            | 'M' -> Key.M | 'N' -> Key.N | 'O' -> Key.O | 'P' -> Key.P
            | 'Q' -> Key.Q | 'R' -> Key.R | 'S' -> Key.S | 'T' -> Key.T
            | 'U' -> Key.U | 'V' -> Key.V | 'W' -> Key.W | 'X' -> Key.X
            | 'Y' -> Key.Y | 'Z' -> Key.Z
            | other ->
                failwithf
                    "HotkeyRegistry.Letter %c not mapped to WPF Key — \
                     update Program.fs translateHotkeyKey"
                    other
        | HotkeyRegistry.Digit 1 -> Key.D1
        | HotkeyRegistry.Digit 2 -> Key.D2
        | HotkeyRegistry.Digit 3 -> Key.D3
        | HotkeyRegistry.Digit 4 -> Key.D4
        | HotkeyRegistry.Digit 5 -> Key.D5
        | HotkeyRegistry.Digit 6 -> Key.D6
        | HotkeyRegistry.Digit 7 -> Key.D7
        | HotkeyRegistry.Digit 8 -> Key.D8
        | HotkeyRegistry.Digit 9 -> Key.D9
        | HotkeyRegistry.Digit n ->
            failwithf
                "HotkeyRegistry.Digit %d out of supported range 1-9 \
                 (number-row digits only; numpad reserved for NVDA \
                 review-cursor commands)"
                n
        | HotkeyRegistry.Semicolon -> Key.OemSemicolon

    /// Pre-framework-cycle PR-O — translate
    /// `HotkeyRegistry.Modifier` set to WPF `ModifierKeys` flags.
    let private translateHotkeyModifiers
            (mods: Set<HotkeyRegistry.Modifier>)
            : ModifierKeys =
        let mutable result = ModifierKeys.None
        if mods.Contains HotkeyRegistry.Ctrl then
            result <- result ||| ModifierKeys.Control
        if mods.Contains HotkeyRegistry.Shift then
            result <- result ||| ModifierKeys.Shift
        if mods.Contains HotkeyRegistry.Alt then
            result <- result ||| ModifierKeys.Alt
        result

    /// Pre-framework-cycle PR-O — wire a registered
    /// `AppCommand` through WPF's RoutedCommand + KeyBinding +
    /// CommandBinding triple. Replaces the 8 individual
    /// `setupXyzKeybinding` functions and the local `bind`
    /// helper that PR-J extracted in `setupShellSwitchKeybindings`.
    /// The `KeyBinding` lives in the Window's `InputBindings`
    /// collection so the gesture is captured BEFORE Stage 6's
    /// PTY-input keyboard handler routes the keys to the child
    /// shell (`OnPreviewKeyDown` filter ordering, pinned by
    /// xUnit + behavioural tests; see
    /// `src/Views/TerminalView.cs AppReservedHotkeys` and the
    /// load-bearing filter chain there).
    ///
    /// Phase 2 user-settings will inject overridden Hotkey
    /// records (different Key / Modifiers per command) before
    /// this call; the dispatch path stays unchanged.
    /// Cycle 26b — return type changed from `unit` to
    /// `RoutedCommand` so compose() can capture each created
    /// command into a dictionary indexed by `AppCommand` and
    /// then wire `MenuItem.Command` from the corresponding
    /// XAML-named `MenuItem_<nameOf cmd>` element. Pressing
    /// the keyboard gesture and selecting the menu item
    /// invoke the same `RoutedCommand` and therefore the same
    /// handler — single source of truth, zero behaviour
    /// duplication.
    ///
    /// Cycle 26b — `Hotkey.Key` and `Hotkey.Modifiers` are now
    /// `option`-typed. Menu-only commands (`Key = None,
    /// Modifiers = None`) skip the `KeyBinding` registration
    /// (no keyboard gesture exists) but still register the
    /// `CommandBinding` so the menu can dispatch via the
    /// returned `RoutedCommand`.
    let private bindHotkey
            (window: MainWindow)
            (cmd: HotkeyRegistry.AppCommand)
            (handler: unit -> unit)
            : RoutedCommand =
        let hk = HotkeyRegistry.hotkeyOf cmd
        let routed = RoutedCommand(HotkeyRegistry.nameOf cmd, typeof<MainWindow>)
        match hk.Key, hk.Modifiers with
        | Some k, Some m ->
            let gesture =
                KeyGesture(translateHotkeyKey k, translateHotkeyModifiers m)
            window.InputBindings.Add(KeyBinding(routed, gesture)) |> ignore
        | _ ->
            // Menu-only command: no KeyBinding installed.
            // The CommandBinding below still registers so the
            // RoutedCommand can be invoked from MenuItem.Command.
            ()
        window.CommandBindings.Add(
            CommandBinding(
                routed,
                ExecutedRoutedEventHandler(fun _ _ -> handler ())))
        |> ignore
        routed

    // Ctrl+Shift+D's body lives in
    // `Terminal.App.Diagnostics.runFullBattery` (PR #165 —
    // extended diagnostic battery). The closure that wires the
    // hotkey to the new module is defined next to
    // `runHealthCheck` further down because it captures compose-
    // local state (`hostHandle`, `currentShell`,
    // `screen.SequenceNumber`); the bind itself sits in the
    // closure-bind group near line 1537.

    /// Open the GitHub "draft a new release" form for this
    /// repository in the user's default web browser. Triggered
    /// by `Ctrl+Shift+R` (mnemonic: **R**elease).
    ///
    /// The maintainer's normal release flow per
    /// `docs/RELEASE-PROCESS.md` is to publish a release in the
    /// GitHub Releases UI (which creates the tag), and the
    /// `release: published` event then triggers the Velopack
    /// build/upload workflow. This hotkey shortcuts directly to
    /// that form so the release-cut step is one keypress
    /// from inside pty-speak — saving a tab-and-click trip
    /// every cadence.
    ///
    /// `Ctrl+Shift+R` and `Alt+Shift+R` (Stage 10 review-mode
    /// toggle, reserved) are different gestures — different
    /// modifier sets — so WPF treats them as distinct
    /// `KeyGesture`s. The mnemonic overlap (both R) is the only
    /// cost.
    ///
    /// The URL is derived from `UpdateRepoUrl` (the same
    /// constant the Velopack auto-update flow uses) so a fork
    /// or a self-hosted variant only needs to update one
    /// constant. Phase 2's TOML config will make `UpdateRepoUrl`
    /// user-configurable per `SECURITY.md` row C-1; this hotkey
    /// inherits whatever the user configures.
    let private runOpenNewRelease (window: MainWindow) : unit =
        let url = UpdateRepoUrl + "/releases/new"
        // Announce-before-focus-grab pattern: the launched browser
        // will steal focus and NVDA's
        // interrupt-on-focus-change will truncate the queued speech
        // unless we give it ~700ms head start).
        // TODO Phase 2: TOML-configurable delay.
        window.TerminalSurface.Announce(
            "Opening new release form.",
            ActivityIds.newRelease)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        let psi = System.Diagnostics.ProcessStartInfo()
                        psi.FileName <- url
                        psi.UseShellExecute <- true
                        System.Diagnostics.Process.Start(psi) |> ignore
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not open new release form: %s" safe,
                            ActivityIds.error)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                ()
            }
        ()

    /// Mutable handle to the active `FileLoggerSink`. Set by
    /// `compose ()` at startup; consulted by `runOpenLogs` to
    /// find the active log directory and disposed in
    /// `app.Exit.Add` so the writer task flushes pending entries.
    let mutable private loggerSink : FileLoggerSink option = None

    /// Cycle 25a — open the pty-speak data folder
    /// (`%LOCALAPPDATA%\PtySpeak\`, the parent of `\logs`,
    /// `\sessions`, and `config.toml`) in File Explorer.
    /// Triggered by `Ctrl+Shift+P`. Replaces the prior
    /// "open logs folder" hotkey because the parent gives
    /// one-keystroke access to all three first-tier
    /// directories the maintainer cares about; navigating into
    /// `\logs` or `\sessions` from there is a single arrow-key
    /// step in Explorer.
    let private runOpenDataFolder (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runOpenDataFolder"
        log.LogInformation("Ctrl+Shift+P pressed — opening pty-speak data folder.")
        let baseDir =
            System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData)
        let dir = System.IO.Path.Combine(baseDir, "PtySpeak")
        // Same announce-before-focus-grab pattern as the other
        // hotkeys that launch a separate window.
        window.TerminalSurface.Announce(
            "Opening data folder.",
            ActivityIds.openDataFolder)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        System.IO.Directory.CreateDirectory(dir) |> ignore
                        let psi = System.Diagnostics.ProcessStartInfo()
                        psi.FileName <- "explorer.exe"
                        psi.Arguments <- sprintf "\"%s\"" dir
                        psi.UseShellExecute <- true
                        System.Diagnostics.Process.Start(psi) |> ignore
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not open data folder: %s" safe,
                            ActivityIds.error)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                ()
            }
        ()

    /// Cycle 25a — auto-create `config.toml` with sensible
    /// defaults if no file exists, then open in the default app
    /// (Notepad on a stock Windows install). Triggered by
    /// `Ctrl+Shift+E`. Designed for screen-reader workflow:
    /// instead of the maintainer hand-typing TOML headers
    /// (which during the Cycle 24 walkthrough produced a typo
    /// — `[sessionmodel._persistence]` instead of
    /// `[session_model.persistence]` — that took multiple debug
    /// rounds to pin), the app writes a known-correct boilerplate
    /// they can edit by changing values rather than authoring
    /// from scratch.
    let private runOpenConfig (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runOpenConfig"
        log.LogInformation("Ctrl+Shift+E pressed — opening config file.")
        let filePath = Config.defaultConfigFilePath ()
        // Same announce-before-focus-grab pattern as
        // runOpenDataFolder. The announcement text differs based
        // on whether we created a fresh file so the maintainer
        // knows what to expect when their editor opens.
        let createdFresh =
            try
                Config.writeDefaults filePath
            with ex ->
                let safe = AnnounceSanitiser.sanitise ex.Message
                log.LogError(
                    ex,
                    "Failed to write default config file at {Path}.",
                    filePath)
                window.TerminalSurface.Announce(
                    sprintf "Could not create config file: %s" safe,
                    ActivityIds.error)
                false
        let announceText =
            if createdFresh then
                "Created config file with defaults; opening."
            else
                "Opening config file."
        window.TerminalSurface.Announce(
            announceText,
            ActivityIds.openConfig)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        let psi = System.Diagnostics.ProcessStartInfo()
                        psi.FileName <- filePath
                        psi.UseShellExecute <- true
                        System.Diagnostics.Process.Start(psi) |> ignore
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not open config file: %s" safe,
                            ActivityIds.error)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                ()
            }
        ()

    /// Launch the interactive process-cleanup test
    /// (`scripts/test-process-cleanup.ps1`) in a separate
    /// PowerShell window. Triggered by Diagnostics → Test Process
    /// Cleanup menu item (Cycle 26c — first menu-only command;
    /// no keyboard accelerator).
    ///
    /// The script needs the user to physically close pty-speak
    /// (Alt+F4) so it can verify Job Object cascade-kill cleanup
    /// — autonomous in-process diagnostic batteries can't drive
    /// that. PowerShell launches with `-NoExit` so the script's
    /// PASS/FAIL output stays visible after the script exits.
    /// Script path resolves via `AppContext.BaseDirectory` —
    /// `Terminal.App.fsproj`'s `<Content Include>` copies the
    /// script next to `Terminal.App.exe` at build time, and
    /// Velopack packs it for distribution.
    ///
    /// Mirrors `runOpenNewRelease` / `runOpenDataFolder` /
    /// `runOpenConfig`'s announce-before-focus-grab pattern:
    /// announce first, 700ms delay so NVDA finishes speaking,
    /// then launch the new process.
    let private runTestProcessCleanup (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runTestProcessCleanup"
        let scriptPath =
            System.IO.Path.Combine(
                System.AppContext.BaseDirectory,
                "test-process-cleanup.ps1")
        log.LogInformation(
            "Process-cleanup script launch requested. ScriptPath={Path}",
            scriptPath)
        window.TerminalSurface.Announce(
            "Launching process-cleanup test in a separate PowerShell window.",
            ActivityIds.processCleanup)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        if not (System.IO.File.Exists scriptPath) then
                            window.TerminalSurface.Announce(
                                "Process-cleanup script not found in install directory.",
                                ActivityIds.error)
                        else
                            let psi = System.Diagnostics.ProcessStartInfo()
                            psi.FileName <- "powershell.exe"
                            psi.Arguments <-
                                sprintf
                                    "-NoExit -ExecutionPolicy Bypass -File \"%s\""
                                    scriptPath
                            psi.UseShellExecute <- true
                            System.Diagnostics.Process.Start(psi) |> ignore
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not launch process-cleanup test: %s" safe,
                            ActivityIds.error)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                ()
            }
        ()

    /// Cycle 28 — Window menu: close the main window. Mirrors
    /// the OS-level `Alt+F4` gesture, which WPF Window already
    /// handles natively via `SystemCommands.CloseWindow`. The
    /// menu path calls `Window.Close()` directly so the close
    /// event chain (and the existing `app.Exit` cleanup
    /// pipeline that disposes hostHandle / cts / heartbeat /
    /// loggerSink in order) fires on either path.
    let private runCloseWindow (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runCloseWindow"
        log.LogInformation("Window menu Close Window invoked.")
        window.Close()

    /// Cycle 28 — Window menu: explicit application shutdown via
    /// `Application.Current.Shutdown()`. In a single-window app
    /// the visible behaviour is identical to `runCloseWindow`
    /// (both trigger the `app.Exit` chain), but the separate
    /// menu slot keeps the option open for multi-pane Phase 2
    /// plans where Close-Window-vs-Exit-App becomes meaningful.
    /// `_window` is unused (Shutdown is a static call) but the
    /// signature matches `runCloseWindow` for handler-shape
    /// consistency at the bind call site.
    let private runExitApp (_window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runExitApp"
        log.LogInformation("Window menu Exit invoked.")
        Application.Current.Shutdown()

    // Cycle 25b-1a — `runCopyLatestLog` (Ctrl+Shift+L) removed.
    // The Ctrl+Shift+D diagnostic battery now bundles the active
    // FileLogger log into its dump-and-clipboard payload (alongside
    // the battery log, config.toml, session-log summary, and
    // redacted env), so a separate "copy just the log" hotkey is
    // redundant and contributed to working-memory hotkey-count
    // pressure. The defense-in-depth `OemSemicolon` direct handler
    // in `TerminalView.OnPreviewKeyDown` and the
    // `SetCopyLogToClipboardHandler` plumbing are deleted in the
    // same change.

    // Cycle 27 — `runToggleDebugLog` and `runMuteEarcons` were
    // removed alongside the migration of `ToggleDebugLog` and
    // `MuteEarcons` from `AppCommand` to `MultiStateCommand`.
    // Their behaviour now lives inline in the closures passed
    // to `bindMultiState` for `LoggingLevel` and `EarconsMode`
    // (further down in `compose ()`), and dispatch is per-option
    // (`SetByName "debug"`, `SetByName "muted"`, etc.) rather
    // than flip-toggle. Announcement still uses
    // `ActivityIds.logToggle` so NVDA users' notification-
    // processing configuration is unchanged.

    /// Cycle 27 — capture for a multi-state command's compose-
    /// time wiring. `GetCurrent` reports the active option's
    /// `OptionId` (used by the parent's `SubmenuOpened` handler
    /// to refresh `IsChecked` on every open). `SetByName`
    /// activates the named option; per-option `RoutedCommand`
    /// instances wire each option's MenuItem to a closure that
    /// calls `SetByName <OptionId>`.
    type private MultiStateBinding =
        { GetCurrent: unit -> string
          SetByName: string -> unit
          PerOptionCommand:
              System.Collections.Generic.Dictionary<string, RoutedCommand> }

    /// Cycle 27 — register `RoutedCommand` + `CommandBinding`
    /// for each option of a multi-state command. Mirrors
    /// `bindHotkey`'s shape minus the `KeyBinding` step
    /// (multi-state is menu-only by canon as of Cycle 27).
    /// Returns a `MultiStateBinding` capturing `GetCurrent` /
    /// `SetByName` (for the menu-wiring loop's
    /// `SubmenuOpened` handler) and the per-option
    /// `RoutedCommand` dictionary (for assigning each option's
    /// `MenuItem.Command`).
    let private bindMultiState
            (window: MainWindow)
            (def: HotkeyRegistry.MultiStateDef)
            (getCurrent: unit -> string)
            (setByName: string -> unit)
            : MultiStateBinding =
        let perOption =
            System.Collections.Generic.Dictionary<string, RoutedCommand>()
        let cmdName = HotkeyRegistry.multiStateNameOf def.Command
        for opt in def.Options do
            let routedName = sprintf "%s_%s" cmdName opt.OptionId
            let routed = RoutedCommand(routedName, typeof<MainWindow>)
            let optionId = opt.OptionId
            window.CommandBindings.Add(
                CommandBinding(
                    routed,
                    ExecutedRoutedEventHandler(fun _ _ -> setByName optionId)))
            |> ignore
            perOption.[optionId] <- routed
        { GetCurrent = getCurrent
          SetByName = setByName
          PerOptionCommand = perOption }

    /// Composition seam — Stage 4+ plugs Elmish.WPF and the UIA peer
    /// in here. For Stage 3b we just hold references to the long-lived
    /// pieces and ensure they're disposed on Application.Exit.
    let compose (app: Application) (window: MainWindow) : unit =
        // Wire up file-based logging FIRST, before anything else
        // can produce log calls. Default-on (Information level);
        // Cycle 27's View → Logging Level → Information / Debug
        // multi-state menu items flip the level at runtime. Logs
        // go to %LOCALAPPDATA%\PtySpeak\logs\pty-speak-{date}.log
        // with daily rolling and 7-day retention; the
        // `Ctrl+Shift+P` hotkey (Data → Open Data Folder) opens
        // the parent of the logs folder.
        let logOptions = FileLoggerOptions.createDefault ()
        let sink = new FileLoggerSink(logOptions)
        loggerSink <- Some sink
        let provider = new FileLoggerProvider(sink)
        let logFactory = Logger.createFactory (provider :> ILoggerProvider)
        Logger.configure logFactory
        let log = Logger.get "Terminal.App.Program"
        log.LogInformation(
            "pty-speak starting. version={Version} os={Os} logs={LogDir}",
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            sink.LogDirectory)

        let cts = new CancellationTokenSource()
        let screen = Screen(rows = ScreenRows, cols = ScreenCols)
        let parser = Parser.create ()
        window.TerminalSurface.SetScreen(screen)

        // Cycle 32b — first consumer of the IDisplayBuffer boundary
        // interface (Cycle 31b declaration). Inline F# object
        // expression wrapping the `screen` instance just constructed
        // above; YAGNI principle over a separate named adapter
        // class until a second implementation surfaces. Future
        // renderers (Avalonia, GTK, AppKit) inject their own
        // IDisplayBuffer instead of going through Screen directly.
        let displayBuffer =
            { new IDisplayBuffer with
                member _.Snapshot(startRow, count) =
                    screen.SnapshotRows(startRow, count) }
        window.TerminalSurface.SetDisplayBuffer(displayBuffer)

        // Pre-framework-cycle PR-O — wire each app-reserved
        // hotkey through the unified `bindHotkey` helper which
        // looks up the default Hotkey for the AppCommand from
        // `HotkeyRegistry.builtIns` and installs the WPF
        // RoutedCommand + KeyBinding + CommandBinding triple.
        // Replaces the 8 stand-alone `setupXyzKeybinding`
        // functions (setupAutoUpdate, setupDiagnostic,
        // setupNewRelease, setupOpenLogs, setupCopyLatestLog,
        // setupToggleDebugLog, setupHealthCheck,
        // setupIncidentMarker) plus the local `bind` helper
        // inside `setupShellSwitchKeybindings` that
        // duplicated the same boilerplate.
        // Order doesn't matter relative to the ConPTY spawn
        // below; we install before the window is loaded so the
        // keybindings are live for the user's first keypress.
        //
        // Cycle 26b — capture each `bindHotkey` return value
        // (the `RoutedCommand` it created) into a dictionary
        // indexed by `AppCommand` so the menu-wiring step
        // below this block can assign the same command to the
        // matching XAML-named `MenuItem_<nameOf cmd>` element.
        // Pressing the keyboard gesture and selecting the
        // menu item invoke the same `RoutedCommand` and
        // therefore the same handler — single source of truth.
        // The local `bind` wrapper centralises the dictionary
        // population so individual call sites stay one-liners.
        let menuCommands =
            System.Collections.Generic.Dictionary<HotkeyRegistry.AppCommand, RoutedCommand>()
        let bind cmd handler =
            let routed = bindHotkey window cmd handler
            menuCommands.[cmd] <- routed
        bind HotkeyRegistry.CheckForUpdates (fun () -> runUpdateFlow window)
        // Ctrl+Shift+D's bind is in the closure-bind group below
        // (around line 1537) — its handler captures local
        // mutables (`hostHandle`, `currentShell`, `screen`) that
        // aren't in scope here yet.
        bind HotkeyRegistry.DraftNewRelease (fun () -> runOpenNewRelease window)
        bind HotkeyRegistry.OpenDataFolder (fun () -> runOpenDataFolder window)
        bind HotkeyRegistry.OpenConfig (fun () -> runOpenConfig window)
        // Cycle 27 — `ToggleDebugLog` and `MuteEarcons` migrated
        // to `MultiStateCommand` as `LoggingLevel` and
        // `EarconsMode`. Their wiring lives in the
        // `multiStateBindings` block further down (after the
        // shell-switch binds).
        // Cycle 26c — first menu-only command. `bindHotkey`
        // skips the KeyBinding installation (no gesture) but
        // still creates the CommandBinding so the menu can
        // dispatch via the captured RoutedCommand.
        bind HotkeyRegistry.RunProcessCleanupScript (fun () -> runTestProcessCleanup window)
        // Cycle 28 — Window menu commands. Both menu-only.
        // CloseWindow's MenuItem in MainWindow.xaml carries
        // `InputGestureText="Alt+F4"` directly (the
        // reflection-driven wiring loop only assigns
        // InputGestureText for `Some`-key entries, leaving
        // hardcoded XAML values for None-key commands alone).
        bind HotkeyRegistry.CloseWindow (fun () -> runCloseWindow window)
        bind HotkeyRegistry.ExitApp (fun () -> runExitApp window)

        // Cycle 25b-1a — `SetCopyLogToClipboardHandler` defense-in-
        // depth wiring removed alongside the Ctrl+Shift+L hotkey
        // itself. The Ctrl+Shift+D bundle is now the single
        // paste-the-log path.

        // Phase A — display-pathway pipeline:
        //
        //   parser thread → pumpChannel (256, DropOldest)
        //                       ↓
        //                 PathwayPump (one Task)
        //                       ↓
        //              ┌────────┴────────────────────────┐
        //              ↓                                  ↓
        //   RowsChanged: build Canonical →     Bell / ParserError:
        //   activePathway.Consume →            OutputEventBuilder.fromScreenNotification →
        //   OutputDispatcher.dispatch          OutputDispatcher.dispatch
        //              ↓                                  ↓
        //   ModeChanged: activePathway.OnModeBarrier (flushed events) →
        //   OutputDispatcher.dispatch (each), then build the barrier
        //   OutputEvent + dispatch.
        //              ↓
        //              EarconProfile.Apply (claims BellRang) +
        //              FileLoggerChannel + NvdaChannel render.
        //
        // + concurrent PathwayTickPump (one Task):
        //   PeriodicTimer(50ms) → activePathway.Tick(now) →
        //   OutputDispatcher.dispatch (trailing-edge flush of
        //   any pending diff in the StreamPathway debounce
        //   accumulator).
        //
        // **Why the pathway sits BEFORE the dispatcher** rather
        // than as a profile inside it. Profiles map OutputEvent
        // → ChannelDecision[]; pathways map canonical screen
        // state → OutputEvent[]. Different abstraction. The
        // pathway's role is to decide *what to say*; the
        // profile's role is to decide *which channel says it*.
        // Phase A introduces the pathway layer above the
        // existing profile layer; Layer 4 (profiles, channels,
        // dispatcher) is unchanged.
        //
        // **Per-shell pathway selection** is hardcoded in
        // `selectPathwayForShell` below — Phase B will replace
        // the hardcoded mapping with TOML config per-shell.
        // v1: cmd / powershell / claude → StreamPathway. vim /
        // less can be tested by setting `PTYSPEAK_SHELL` to a
        // shell wired to TuiPathway (no built-in mapping ships
        // for vim/less yet — TuiPathway is wired but only
        // selectable when a future shell entry uses it).
        // SessionModel Tier 1.D-fix (Cycle 17) — channel-driven
        // actor model. Single `BoundedChannel<PumpInput>` carries
        // BOTH ScreenNotification events (RowsChanged / Bell /
        // ModeChanged / ParserError / PromptBoundary) AND
        // synthetic Tick events from the simplified
        // PathwayTickPump. Notification consumer (sole owner of
        // composition-root state) reads serially.
        let pumpChannel =
            let opts =
                System.Threading.Channels.BoundedChannelOptions(256,
                    FullMode =
                        System.Threading.Channels.BoundedChannelFullMode.DropOldest)
            System.Threading.Channels.Channel.CreateBounded<PumpInput>(opts)

        // Bridge Screen.ModeChanged events into the parser-side
        // channel so the Stream profile can use them as flush
        // barriers (alt-screen swap, etc.). The screen fires
        // this AFTER releasing its internal lock, so pushing to
        // a Channel here is non-blocking and deadlock-free.
        screen.ModeChanged.Add(fun (flag, value) ->
            pumpChannel.Writer.TryWrite(
                Notification (ModeChanged (flag, value))) |> ignore)
        // Stage 8d.1 — bridge screen.Bell events into the
        // notification channel so the PathwayPump produces
        // OutputEvent.BellRang for the Earcon profile.
        screen.Bell.Add(fun () ->
            pumpChannel.Writer.TryWrite(
                Notification ScreenNotification.Bell) |> ignore)
        // SessionModel Tier 1.B — bridge screen.PromptBoundary
        // events (parsed OSC 133 sequences) into the
        // notification channel. The PathwayPump's no-op
        // PromptBoundary arm (added in Tier 1.A) currently
        // discards them; Tier 1.C wires the SessionModel state
        // machine + active-pathway OnPromptBoundary dispatch.
        // The Screen fires these AFTER releasing its internal
        // lock (mirrors the Bell + ModeChanged pattern), so
        // TryWrite is non-blocking and deadlock-free.
        screen.PromptBoundary.Add(fun boundary ->
            pumpChannel.Writer.TryWrite(
                Notification (ScreenNotification.PromptBoundary boundary))
            |> ignore)

        // Stage 8b — output framework substrate composition.
        //
        // The marshal callback uses synchronous `Dispatcher.Invoke`
        // so the drain blocks on each Announce until the WPF
        // dispatch completes — same effective backpressure as
        // Stage 7's `let! _ = InvokeAsync(...).Task` await. The
        // pumps run on thread-pool workers (not the UI thread),
        // so blocking is safe; UI thread never waits on a pump
        // worker. Behaviour-identical to Stage 7 / Stage 8a in
        // throughput + ordering.
        let nvdaChannel =
            NvdaChannel.create (fun (msg, activityId) ->
                let action () =
                    window.TerminalSurface.Announce(msg, activityId)
                window.Dispatcher.Invoke(Action(action)))
        OutputDispatcher.ChannelRegistry.register nvdaChannel
        // Stage 8c — FileLogger as a first-class channel. Every
        // event the Stream profile emits now lands in the rolling
        // log structurally, alongside the live NVDA reading. The
        // `Ctrl+Shift+;` clipboard-copy flow (PR-F) carries the
        // full event trail for post-hoc diagnosis.
        let fileLoggerChannel =
            FileLoggerChannel.create
                (Logger.get "Terminal.Core.FileLoggerChannel")
        OutputDispatcher.ChannelRegistry.register fileLoggerChannel
        // Stage 8d.1 — WASAPI Earcons channel. Plays a sine-tone
        // ping when the Earcon profile claims a Semantic
        // category (today: BellRang only). The marshal callback
        // binds to `Terminal.Audio.EarconPlayer.play` which feeds
        // NAudio's WasapiOut. The Ctrl+Shift+M hotkey toggles
        // mute via `EarconChannel.toggle ()` (the channel's Send
        // checks `isMuted` before invoking play).
        let earconChannel =
            EarconChannel.create
                (EarconPlayer.play EarconPalette.defaultPalette)
        OutputDispatcher.ChannelRegistry.register earconChannel
        // Phase A — split the StreamProfile work into Layer 3
        // (StreamPathway) for SEMANTIC decisions + Layer 4
        // (PassThroughProfile) for RENDERING decisions:
        //   - Layer 2 (CanonicalState) — pure substrate, no
        //     profile registration needed.
        //   - Layer 3 (StreamPathway) — emits OutputEvents the
        //     dispatcher routes through Layer 4 profiles.
        //   - Layer 4 (PassThroughProfile + EarconProfile) — the
        //     PassThroughProfile fans every event to NVDA +
        //     FileLogger as RenderText decisions; EarconProfile
        //     additionally claims BellRang and emits a RenderEarcon
        //     decision for the EarconChannel. Behaviour-identical
        //     to the old StreamProfile catch-all + EarconProfile
        //     pair (the old StreamProfile's catch-all branch
        //     became PassThroughProfile verbatim).
        let passThroughProfile = PassThroughProfile.create ()
        OutputDispatcher.ProfileRegistry.register passThroughProfile
        let earconProfile = EarconProfile.create ()
        OutputDispatcher.ProfileRegistry.register earconProfile
        // Cycle 29b — SelectionProfile registers alongside the
        // pass-through + earcon profiles. SelectionDetector
        // emits `SelectionShown` / `SelectionItem` /
        // `SelectionDismissed` events with empty Payload (the
        // empty-payload trick — `NvdaChannel` skips
        // `RenderText ""` per `NvdaChannel.fs:87`, so
        // PassThroughProfile's catch-all doesn't double-emit
        // an NVDA announcement). SelectionProfile then reads
        // `Extensions[AllItems]` / `[ItemText]` / `[ItemIndex]`
        // / `[SelectedIndex]` / `[ItemCount]` to construct the
        // user-facing text and emit a NVDA + FileLogger pair.
        let selectionProfile = SelectionProfile.create ()
        OutputDispatcher.ProfileRegistry.register selectionProfile
        // Active set: [ passThroughProfile; earconProfile;
        // selectionProfile ]. PassThrough fans non-empty
        // payloads to NVDA + FileLogger; Earcon claims
        // BellRang / ErrorLine / WarningLine; Selection claims
        // SelectionShown / SelectionItem / SelectionDismissed
        // and emits the user-facing text rendering for them.
        OutputDispatcher.ProfileRegistry.setActiveProfileSet
            [ passThroughProfile; earconProfile; selectionProfile ]

        // Phase B (subset, "C2") — load the user's TOML config
        // once at startup. `Config.tryLoad` never throws; on
        // any failure mode (file absent, malformed TOML, schema
        // mismatch, unknown keys) it logs via `log` and falls
        // back to defaults, so absence-of-config is byte-
        // equivalent to pre-C2 behaviour. The loaded `config`
        // is captured by `selectPathwayForShell`'s closure +
        // consulted on every pathway construction (startup,
        // Ctrl+Shift+1/2/3 hot-switch, alt-screen swap).
        let config =
            Config.tryLoad log (Config.defaultConfigFilePath ())

        // Cycle 25a — apply `[logging] min_level` from TOML to
        // the FileLogger sink. The sink was constructed with
        // env-var-or-Information at line ~706 (BEFORE Config
        // loads, because Config loading itself emits log lines
        // that need a sink). Now that Config is loaded, apply
        // the TOML-resolved level IF the env var was NOT set
        // (env wins over TOML, mirroring the established
        // `[startup] default_shell` / `PTYSPEAK_SHELL` pattern).
        match config.LoggingOverrides.MinLevel, loggerSink with
        | Some tomlLevel, Some s ->
            let envLevel =
                Environment.GetEnvironmentVariable("PTYSPEAK_LOG_LEVEL")
            let envSet =
                match envLevel with
                | null -> false
                | "" -> false
                | _ -> true
            if envSet then
                log.LogInformation(
                    "Config: [logging] min_level = {Toml} ignored because PTYSPEAK_LOG_LEVEL env var is set (env-var-overrides-TOML precedence).",
                    tomlLevel)
            else
                log.LogInformation(
                    "Config: applying [logging] min_level = {Level} from TOML.",
                    tomlLevel)
                s.SetMinLevel(tomlLevel)
        | _ -> ()

        // Cycle 24a — log the resolved SessionModel persistence
        // mode once at startup. Pure observability; no I/O was
        // wired in 24a. Cycle 24c (this PR) actually persists
        // tuples for `session_log` mode using the
        // `applyAndCapture` / `finalizeIncompleteAndCapture`
        // seams below.
        // Cycle 25a — `mutable` so the shell-switch handler can
        // reload after a TOML edit without a full restart. All
        // downstream references go through this binding (the
        // closures `sessionLogWriterFactory`, `dispatchTupleToWriter`,
        // and the runAnnounceSessionLogPath handler all read the
        // live value).
        let mutable persistenceConfig = config.SessionPersistence
        let persistenceOutputDir =
            match persistenceConfig.OutputDir with
            | Some dir -> dir
            | None -> "<default>"
        log.LogInformation(
            "SessionModel persistence mode: {Mode} (output_dir={OutputDir}, format={Format}, max_session_size_mb={MaxSessionSizeMb})",
            SessionPersistence.modeToString persistenceConfig.Mode,
            persistenceOutputDir,
            SessionPersistence.formatToString persistenceConfig.Format,
            persistenceConfig.MaxSessionSizeMb)

        // Cycle 24d-2 — register env-var-value redaction
        // patterns. Reads the process env at startup, captures
        // values for any name matching the Stage 7 deny-list
        // pattern (`*_TOKEN`, `*_SECRET`, `*_KEY`,
        // `*_PASSWORD`; `ANTHROPIC_API_KEY` exempted), and
        // registers values ≥ 16 chars. Run unconditionally
        // (even for `MemoryOnly` mode — cheap; the registered
        // values are only consulted by the sink's `writeOne`,
        // which never runs when no sink exists). Per
        // LOGGING.md "log counts, never names or values", the
        // count is logged at Information level inside
        // `registerFromEnvironment`.
        SessionSanitiser.registerFromEnvironment log
        |> ignore

        // Cycle 24c — construct the SessionLogWriter sink for
        // the initial shell session when persistence is
        // requested. `MemoryOnly` skips construction entirely
        // (no file is ever opened). `SessionLog` and `Always`
        // both construct the sink today (Always degrades to
        // SessionLog semantics per the warning above).
        //
        // Sink lifetime: one-per-shell-session. Shell-switch
        // disposes the old sink + constructs a fresh one for
        // the new SessionId; app shutdown disposes whatever
        // sink is current. The mutable handle below is the
        // single source of truth.
        let sessionLogWriterFactory
                (sessionId: System.Guid)
                : SessionLogWriterSink option
                =
            match persistenceConfig.Mode with
            | SessionPersistence.MemoryOnly -> None
            | SessionPersistence.SessionLog
            | SessionPersistence.Always ->
                let opts =
                    SessionLogWriterOptions.createDefault
                        sessionId persistenceConfig.OutputDir
                let sink = new SessionLogWriterSink(opts, log)
                log.LogInformation(
                    "SessionLogWriter started: path={Path}",
                    sink.ActiveLogPath)
                Some sink
        let mutable sessionLogWriter
                : SessionLogWriterSink option = None

        // Cycle 24d-1 — dispatch helper for finalised tuples.
        // `MemoryOnly` (no sink) → no-op. `SessionLog` →
        // `Enqueue` (non-blocking after queue). `Always` →
        // `EnqueueSync` (audit-grade durability; blocks the
        // SessionModel state-machine call path until the tuple
        // is on disk; 10-second timeout with graceful
        // degradation). Both `handlePromptBoundary` and the
        // shell-switch path call this helper for consistency.
        let dispatchTupleToWriter
                (tuple: SessionModel.SessionTuple) : unit =
            match sessionLogWriter, persistenceConfig.Mode with
            | None, _ -> ()
            | Some sink, SessionPersistence.Always ->
                sink.EnqueueSync tuple
            | Some sink, _ ->
                sink.Enqueue tuple

        // Phase B (subset, "C2") — per-shell pathway selection.
        // Reads the loaded `config` for both pathway choice and
        // pathway parameters. Three v1 built-in shells (cmd /
        // powershell / claude) all default to "stream"; users
        // override per-shell via `[shell.<id>] pathway = "..."`
        // and per-pathway parameters via `[pathway.stream]`.
        // Unknown pathway names log a Warning and fall back to
        // StreamPathway with resolved parameters (so the user
        // still gets per-pathway parameter benefits even when
        // the name is misspelled).
        let shellIdToConfigKey (shellId: ShellRegistry.ShellId) : string =
            match shellId with
            | ShellRegistry.Cmd -> "cmd"
            | ShellRegistry.PowerShell -> "powershell"
            | ShellRegistry.Claude -> "claude"
        let selectPathwayForShell
                (shellId: ShellRegistry.ShellId)
                : DisplayPathway.T =
            let shellKey = shellIdToConfigKey shellId
            let pathwayId = Config.resolveShellPathway config shellKey
            let streamParams = Config.resolveStreamParameters config
            match pathwayId with
            | "stream" -> StreamPathway.create streamParams
            | "tui" -> TuiPathway.create ()
            | unknown ->
                log.LogWarning(
                    "Config: unknown pathway '{Pathway}' for shell {Shell}; falling back to stream.",
                    unknown, shellKey)
                StreamPathway.create streamParams

        // Initial pathway — StreamPathway with the resolved
        // parameters from `config` (or defaults if no config
        // is present). The mutable is reassigned by
        // `switchToShell` below when the user hot-switches
        // and by the startup-shell alignment block once
        // `chosenShell` resolves; for the brief window
        // between this initialisation and the alignment
        // swap, no ScreenNotifications can arrive (the
        // ConPty isn't spawned yet) so the StreamPathway
        // placeholder is safe regardless of `config`'s
        // shell-pathway override.
        let mutable activePathway : DisplayPathway.T =
            StreamPathway.create (Config.resolveStreamParameters config)

        // Phase B (subset) — alt-screen → TuiPathway auto-detect
        // bookkeeping. The PathwayPump's `handleModeChanged`
        // consults `PathwaySelector.decideAltScreenAction` on
        // every `ModeChanged`; when an alt-screen toggle requires
        // a pathway swap, the pump needs to know which shell to
        // resolve the "default" back to (i.e., on alt-screen
        // exit, swap back to the active shell's default
        // streaming pathway, not always to cmd's). Tracked as a
        // `ShellId` rather than the full `Shell` record because
        // `selectPathwayForShell` only needs the id; this avoids
        // a forward reference to `chosenShell` (which is resolved
        // later in `compose`). Defaults to `Cmd` so a swap that
        // somehow fires before `chosenShell` resolves still
        // produces a valid pathway. Updated in two places:
        //   - startup-shell alignment (after `chosenShell`
        //     resolves)
        //   - `switchToShell`'s `Ok newHost` branch
        let mutable currentShellId : ShellRegistry.ShellId =
            ShellRegistry.Cmd

        // SessionModel Tier 1.C — structured-history substrate.
        // One instance per shell session; replaced on
        // Ctrl+Shift+1/2/3 hot-switch (per Q5 resolution
        // 2026-05-07: per-shell-session model). Initialised
        // with a `cmd` placeholder; the startup-shell alignment
        // block (~30 lines below) re-creates with the resolved
        // shell key alongside `currentShellId`. Mutated on the
        // PathwayPump worker thread (single-threaded for
        // notification consumption); composition root reads
        // it only via the pump.
        let mutable currentSession : SessionModel.T =
            SessionModel.createDefault (shellIdToConfigKey ShellRegistry.Cmd)

        // Cycle 24c — initial sink for the first shell's
        // SessionId. None when persistence is MemoryOnly.
        sessionLogWriter <- sessionLogWriterFactory currentSession.SessionId

        // SessionModel Tier 1.D — heuristic prompt-boundary
        // detector. Per-shell regex + stability window
        // produces synthetic PromptBoundary events for shells
        // that don't emit OSC 133 (cmd / PowerShell / Claude
        // — all three by default). Reset on shell-switch +
        // alt-screen entry (stale per-row matches would
        // produce phantom boundaries on alt-screen exit).
        // Mutated on the PathwayPump worker thread alongside
        // `currentSession`.
        let mutable promptDetector : HeuristicPromptDetector.T =
            HeuristicPromptDetector.create ()

        // Cycle 29b — selection-prompt detector. Same actor-
        // model contract as `promptDetector`: mutated on the
        // PathwayPump worker thread, captured by closures that
        // stay in this scope. Cycle 32a wires
        // `[profile.selection]` TOML overrides via
        // `Config.resolveSelectionParameters`; absent or
        // empty section → `SelectionDetector.defaultParameters`
        // verbatim. Reset on shell-switch + alt-screen entry
        // alongside `promptDetector`.
        let mutable selectionDetector : SelectionDetector.T =
            SelectionDetector.create
                (Config.resolveSelectionParameters config)

        // PathwayPump — Phase A replacement for TranslatorPump.
        // Reads raw ScreenNotifications and routes by case:
        //   - RowsChanged: snapshot the screen → build a
        //     CanonicalState.Canonical → call
        //     `activePathway.Consume canonical` → dispatch each
        //     emitted OutputEvent through the dispatcher (which
        //     runs the EarconProfile + sends to NvdaChannel +
        //     FileLoggerChannel for non-Earcon-claimed events).
        //   - Bell / ParserError: build an OutputEvent via the
        //     existing OutputEventBuilder.fromScreenNotification
        //     (the pathway doesn't see these — they're parser-
        //     side signals, not screen-state changes).
        //   - ModeChanged: invoke `activePathway.OnModeBarrier
        //     now` to flush any pending pathway state, dispatch
        //     each flushed event, then build the barrier
        //     OutputEvent via OutputEventBuilder and dispatch.
        //
        // The pathway's `LastEmittedRowHashes` baseline + debounce
        // accumulator + spinner-history all live inside the
        // pathway closure; the pump just hands canonical state
        // in and dispatches the OutputEvents that come out.
        let dispatchPathwayEvents (events: OutputEvent[]) : unit =
            for ev in events do
                OutputDispatcher.dispatch ev

        // Per-case handlers extracted from the match block. F# 9
        // under TreatWarningsAsErrors=true can be brittle about
        // sequence-in-match-arm shapes (see CONTRIBUTING.md
        // 'F# 9 gotchas' + bit PR #132); pulling each arm to a
        // named helper keeps the match body single-expression.
        let pumpLog = Logger.get "Terminal.App.Program.pathwayPump"

        // SessionModel Tier 1.C — PromptBoundary handler. Advances
        // the SessionModel state machine with the boundary, then
        // dispatches to the active pathway's `OnPromptBoundary`.
        // Stream / Tui pathways currently return `[||]` (no-op
        // overrides from Tier 1.A); Phase 2 pathways
        // (ReplPathway, ClaudeCodePathway, FormPathway) will
        // override with non-trivial logic.
        //
        // Defined BEFORE `handleRowsChanged` so the latter can
        // dispatch heuristic-detected boundaries (Tier 1.D) via
        // this helper. F# `let` bindings are sequential.
        let handlePromptBoundary
                (boundary: PromptBoundaryData)
                (snapshot: Cell[][])
                : unit
                =
            // SessionModel Tier 1.E — PromptText augmentation.
            // Heuristic boundaries arrive with `MatchedRowText`
            // already populated (the detector renders the row
            // it matched). OSC 133 boundaries arrive from the
            // notification channel without snapshot context; we
            // capture a fresh snapshot here and render the
            // cursor's row to populate `MatchedRowText` for
            // SessionModel.apply's PromptText write.
            //
            // Tier 1.E2.B (Cycle 20b): caller-supplied
            // `snapshot` parameter is forwarded to
            // `SessionModel.apply` for finalize-time
            // CommandText + OutputText extraction. The
            // heuristic path passes the snapshot it captured
            // for `tryDetect`; the OSC 133 path captures a
            // fresh snapshot for augmentation + reuses it
            // for the apply call.
            let augmented, snapshotForApply =
                match boundary.MatchedRowText, boundary.Kind with
                | Some _, _ -> boundary, snapshot
                | None, BoundaryKind.PromptStart ->
                    let _, (cursorRow, _), snap =
                        screen.SnapshotRows(0, screen.Rows)
                    let text =
                        CanonicalState.renderRow snap cursorRow
                    // Tier 1.E2.A: also populate
                    // MatchedRowIndex from the cursor row, so
                    // OSC 133-emitting shells can drive the
                    // row-index-aware emission gate identically
                    // to heuristic-detector emissions.
                    let aug =
                        { boundary with
                            MatchedRowText = Some text
                            MatchedRowIndex = Some cursorRow }
                    aug, snap
                | None, _ -> boundary, snapshot
            // Cycle 24c — `applyAndCapture` returns the new
            // state plus an Option carrying the freshly-finalised
            // tuple (when this boundary triggered an
            // Active→History transition). Dispatch the tuple to
            // the writer; `MemoryOnly` mode leaves
            // `sessionLogWriter` as `None` and the dispatch
            // becomes a no-op. Cycle 24d-1: `Always` mode routes
            // through `EnqueueSync` (blocking) via
            // `dispatchTupleToWriter`.
            let nextSession, finalisedOpt =
                SessionModel.applyAndCapture
                    currentSession augmented snapshotForApply
            currentSession <- nextSession
            match finalisedOpt with
            | Some tuple -> dispatchTupleToWriter tuple
            | None -> ()
            let emitted = activePathway.OnPromptBoundary augmented
            pumpLog.LogDebug(
                "PathwayPump PromptBoundary {Kind} → {Pathway}.OnPromptBoundary → {Count} events.",
                augmented.Kind,
                activePathway.Id,
                emitted.Length)
            dispatchPathwayEvents emitted

        // SessionModel Tier 1.D-cleanup (Cycle 19) — shared
        // detector invocation. Was duplicated across
        // `handleRowsChanged` (frame-driven path) and
        // `handleTick` (50ms tick-driven path; introduced in
        // Cycle 17 to close the idle-gap hole). Both call
        // sites had identical 4-arg shape:
        //   shellKey + detectionTime + tryDetect + state
        //   update + dispatch on Some.
        // Helper closes over `currentShellId` + `promptDetector`
        // (composition-root mutables; safe because all callers
        // run on the single notification-consumer thread per
        // the channel-driven actor model from Cycle 17).
        let runDetector
                (snapshot: Cell[][])
                (cursorPos: int * int)
                (now: DateTime)
                : unit
                =
            let shellKey = shellIdToConfigKey currentShellId
            let boundary, nextDetector =
                HeuristicPromptDetector.tryDetect
                    snapshot cursorPos shellKey now promptDetector
            promptDetector <- nextDetector
            // Tier 1.E2.B: forward the snapshot so
            // handlePromptBoundary can pass it through to
            // SessionModel.apply for finalize-time content
            // extraction.
            match boundary with
            | Some data -> handlePromptBoundary data snapshot
            | None -> ()
            // Cycle 29b — also drive the SelectionDetector on
            // the same snapshot+cursor+now triple. The detector
            // short-circuits internally when shellKey ≠ "claude"
            // (per its `shouldDetect` gate), so cmd / PowerShell
            // sessions pay only the function-call cost. Each
            // emitted OutputEvent flows directly to the
            // dispatcher, which fans it through the active
            // profile set (PassThrough's catch-all → empty NVDA
            // payload skipped + FileLogger logs the structured
            // event; SelectionProfile renders the user-facing
            // text and emits NVDA + FileLogger pair).
            let selectionEvents, nextSelectionDetector =
                SelectionDetector.tryDetect
                    snapshot cursorPos now shellKey selectionDetector
            selectionDetector <- nextSelectionDetector
            for event in selectionEvents do
                OutputDispatcher.dispatch event

        let handleRowsChanged () : unit =
            let seq, cursorPos, snapshot = screen.SnapshotRows(0, screen.Rows)
            // SessionModel Tier 1.D — heuristic prompt-
            // boundary detection. Runs BEFORE pathway
            // consumption so the SessionModel state machine
            // advances ahead of any pathway query against
            // currentSession. See `runDetector` for the
            // shared invocation logic; Cycle 19 factored it
            // out of `handleRowsChanged` + `handleTick` to
            // remove duplication.
            runDetector snapshot cursorPos (DateTime.UtcNow)
            let canonical = CanonicalState.create snapshot cursorPos seq
            let emitted = activePathway.Consume canonical
            pumpLog.LogDebug(
                "PathwayPump RowsChanged → {Pathway}.Consume → {Count} events.",
                activePathway.Id,
                emitted.Length)
            dispatchPathwayEvents emitted
        // Phase B (subset) — alt-screen swap helper. Mirrors
        // `switchToShell`'s pathway-swap sequence (flush via
        // `OnModeBarrier`, `Reset` outgoing, reassign, seed
        // `SetBaseline` against the current screen) so the
        // mid-session pathway-swap state semantics match the
        // hot-switch path. Idempotent under no-op decisions
        // (`PathwaySelector.Keep` short-circuits to the existing
        // mode-barrier flush).
        let swapPathwayForAltScreen (next: DisplayPathway.T) : unit =
            try activePathway.Reset () with _ -> ()
            activePathway <- next
            try
                let seq, cursorPos, snapshot =
                    screen.SnapshotRows(0, screen.Rows)
                let canonical =
                    CanonicalState.create snapshot cursorPos seq
                activePathway.SetBaseline canonical
            with _ -> ()
            pumpLog.LogInformation(
                "PathwayPump auto-swapped pathway. NewPathway={Pathway}",
                activePathway.Id)

        let handleModeChanged (notification: ScreenNotification) : unit =
            let now = DateTimeOffset.UtcNow
            // Step 1 — flush the OUTGOING pathway's pending
            // state via OnModeBarrier so any pre-mode-change
            // diff lands at NVDA before the pathway swap.
            let flushed = activePathway.OnModeBarrier now
            pumpLog.LogDebug(
                "PathwayPump ModeChanged → {Pathway}.OnModeBarrier → {Count} flushed events.",
                activePathway.Id,
                flushed.Length)
            dispatchPathwayEvents flushed
            // Step 2 — consult PathwaySelector to decide whether
            // an alt-screen toggle warrants a pathway swap. Most
            // ModeChanged events return Keep (no swap) and the
            // existing barrier-OutputEvent dispatch finishes
            // the flow. Alt-screen toggles between the user's
            // streaming shell and a TUI app (vim / less / top)
            // swap pathways here so the user gets streaming
            // diffs in cmd / powershell / claude and review-
            // cursor-only navigation in vim / less.
            match PathwaySelector.decideAltScreenAction
                    activePathway.Id notification with
            | PathwaySelector.Keep ->
                ()
            | PathwaySelector.SwapToTui ->
                // SessionModel Tier 1.D — Q3 wiring
                // closure. Mark the session as alt-screen-
                // active so subsequent boundaries are
                // ignored by `SessionModel.apply`. Reset
                // the heuristic detector so stale per-row
                // matches don't produce phantom boundaries
                // on alt-screen exit.
                currentSession <-
                    SessionModel.enterAltScreen currentSession
                promptDetector <-
                    HeuristicPromptDetector.reset promptDetector
                // Cycle 29b — also reset the selection detector
                // on alt-screen entry. Selection prompts can't
                // appear inside alt-screen TUIs (vim / less /
                // top); a stale candidate would emit a phantom
                // SelectionDismissed on alt-screen exit if not
                // reset.
                selectionDetector <-
                    SelectionDetector.reset selectionDetector
                swapPathwayForAltScreen (TuiPathway.create ())
            | PathwaySelector.SwapToShellDefault ->
                // SessionModel Tier 1.D — Q3 wiring
                // closure. Clear the alt-screen flag so
                // subsequent boundaries advance the state
                // machine again. Reset the detector for a
                // fresh stability window post-exit.
                currentSession <-
                    SessionModel.exitAltScreen currentSession
                promptDetector <-
                    HeuristicPromptDetector.reset promptDetector
                // Cycle 29b — companion reset on alt-screen exit.
                selectionDetector <-
                    SelectionDetector.reset selectionDetector
                swapPathwayForAltScreen (selectPathwayForShell currentShellId)
            // Step 3 — dispatch the barrier OutputEvent so the
            // FileLogger captures the mode transition in the
            // audit trail and NvdaChannel routes via
            // ActivityIds.mode (empty Payload skips the actual
            // announce per NvdaChannel's contract).
            let barrier =
                OutputEventBuilder.fromScreenNotification notification
            OutputDispatcher.dispatch barrier
        let handleSimpleNotification (notification: ScreenNotification) : unit =
            let event = OutputEventBuilder.fromScreenNotification notification
            pumpLog.LogDebug(
                "PathwayPump → Dispatch. Semantic={Semantic} Priority={Priority}",
                event.Semantic, event.Priority)
            OutputDispatcher.dispatch event

        // SessionModel Tier 1.D-fix (Cycle 17) — tick handler.
        // Runs every 50ms inside the notification-consumer task
        // (driven by the simplified PathwayTickPump producer).
        // Three responsibilities:
        //   1. Heuristic detector invocation. Closes Cycle 14's
        //      idle-gap hole — at idle no RowsChanged events
        //      arrive, so the Cycle 14-shape frame-driven-only
        //      detector never got the second tryDetect call to
        //      check stability and emit. Tick-driven invocation
        //      runs every 50ms regardless of frame activity.
        //   2. activePathway.Tick. Moved here from the standalone
        //      PathwayTickPump task body so all composition-root
        //      state mutations happen on this single consumer
        //      thread (per the channel-driven actor model).
        //   3. OutputDispatcher.dispatchTick. Same rationale.
        //
        // Single-threaded with handleRowsChanged /
        // handlePromptBoundary / handleModeChanged because all
        // PumpInput cases are processed serially by this same
        // consumer task. No race on currentSession /
        // promptDetector / activePathway mutations.
        let handleTick (now: DateTimeOffset) : unit =
            let _, cursorPos, snapshot = screen.SnapshotRows(0, screen.Rows)
            // Cycle 19 — shared `runDetector` helper. Cycle 17
            // introduced this tick-driven invocation to close
            // the idle-gap hole; the inline body has been
            // factored out for parity with `handleRowsChanged`.
            runDetector snapshot cursorPos now.UtcDateTime
            let emitted = activePathway.Tick now
            if emitted.Length > 0 then
                dispatchPathwayEvents emitted
            OutputDispatcher.dispatchTick now

        let _ =
            Task.Run(fun () ->
                task {
                    try
                        let reader = pumpChannel.Reader
                        let mutable keepGoing = true
                        while keepGoing && not cts.Token.IsCancellationRequested do
                            let! got = reader.WaitToReadAsync(cts.Token).AsTask()
                            if not got then
                                keepGoing <- false
                            else
                                let mutable peek =
                                    Unchecked.defaultof<PumpInput>
                                while reader.TryRead(&peek) do
                                    match peek with
                                    | Notification (RowsChanged _) ->
                                        handleRowsChanged ()
                                    | Notification ((ModeChanged _) as n) ->
                                        handleModeChanged n
                                    | Notification ((ParserError _) as n) ->
                                        handleSimpleNotification n
                                    | Notification (Bell as n) ->
                                        handleSimpleNotification n
                                    | Notification (PromptBoundary boundary) ->
                                        // SessionModel Tier 1.C —
                                        // advance the state
                                        // machine + dispatch to
                                        // active pathway's
                                        // OnPromptBoundary. Tier
                                        // 1.B's OSC 133 producer
                                        // emits these from
                                        // shells that opt in; Tier
                                        // 1.D added the heuristic-
                                        // fallback module so cmd /
                                        // PowerShell / Claude also
                                        // produce boundaries
                                        // without OSC 133 support.
                                        // Tier 1.E2.B: capture a
                                        // fresh snapshot so apply
                                        // can extract CommandText
                                        // + OutputText from the
                                        // current screen state.
                                        let _, _, oscSnap =
                                            screen.SnapshotRows(
                                                0, screen.Rows)
                                        handlePromptBoundary
                                            boundary oscSnap
                                    | Tick now ->
                                        // SessionModel Tier 1.D-fix
                                        // (Cycle 17) — tick-driven
                                        // detector invocation.
                                        // Cycle 14's frame-driven-
                                        // only approach left a hole:
                                        // at idle (no RowsChanged
                                        // events), the detector
                                        // recorded a per-row match
                                        // but never got called again
                                        // to check the stability
                                        // window. Maintainer's
                                        // manual NVDA validation
                                        // 2026-05-08 demonstrated
                                        // 0 tuples + LastEmitted
                                        // PromptText=none after
                                        // typing + idle. Driving
                                        // tryDetect from the existing
                                        // 50ms tick (now arriving
                                        // here as a Tick PumpInput
                                        // case) closes the gap. The
                                        // tick also drives
                                        // activePathway.Tick +
                                        // OutputDispatcher.dispatchTick
                                        // (moved here from the
                                        // separate tick-pump task,
                                        // which is now a pure
                                        // channel producer).
                                        handleTick now
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        // Post-Stage-6 diagnostic safety net,
                        // preserved across the Phase A reorganisation.
                        // Surface the exception via one last
                        // direct `Announce(..., pty-speak.error)`
                        // — direct call (not through the
                        // dispatcher) because the framework
                        // substrate may itself be the exception
                        // source. Sanitise through SR-2's
                        // chokepoint so PTY-originated control
                        // bytes can't reach NVDA verbatim.
                        try
                            log.LogError(
                                ex,
                                "PathwayPump crashed; streaming announcements halted.")
                            let safe =
                                AnnounceSanitiser.sanitise ex.Message
                            let action () =
                                window.TerminalSurface.Announce(
                                    sprintf
                                        "PathwayPump exception: %s"
                                        safe,
                                    ActivityIds.error)
                            window.Dispatcher.Invoke(Action(action))
                        with _ -> ()
                } :> Task)

        // PathwayTickPump — Cycle 17 simplification. Was a
        // standalone task that called `activePathway.Tick now` +
        // `OutputDispatcher.dispatchTick now` directly; now a
        // pure channel producer enqueueing `Tick now` into the
        // shared `pumpChannel`. The notification-consumer task
        // owns the actual tick handling (`handleTick` above),
        // which also runs the heuristic detector to close
        // Cycle 14's idle-gap hole. Single-threaded mutation
        // contract restored.
        //
        // The 50ms cadence is the timer rate, NOT the debounce
        // window (200ms inside StreamPathway). Faster ticks =
        // lower worst-case latency on trailing-edge flush; the
        // cost is negligible (each Tick that handleTick
        // processes is a pure-function call when no work is
        // pending).
        //
        // If the channel is full (256 capacity; very unlikely
        // for 50ms ticks + sparse notifications), `TryWrite`
        // returns false silently — the next tick fires 50ms
        // later. Acceptable backpressure.
        let _ =
            Task.Run(fun () ->
                task {
                    try
                        use tickTimer =
                            new System.Threading.PeriodicTimer(
                                TimeSpan.FromMilliseconds 50.0)
                        let mutable keepGoing = true
                        while keepGoing && not cts.Token.IsCancellationRequested do
                            let! tickFired =
                                tickTimer.WaitForNextTickAsync(cts.Token).AsTask()
                            if not tickFired then
                                keepGoing <- false
                            else
                                let now = DateTimeOffset.UtcNow
                                pumpChannel.Writer.TryWrite(Tick now)
                                |> ignore
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        try
                            log.LogError(
                                ex,
                                "PathwayTickPump crashed; trailing-edge flushes halted.")
                            // No user-facing announce here —
                            // PathwayTickPump failure is silent
                            // degradation (Consume still fires
                            // for every event via handleRowsChanged;
                            // only the trailing-edge flush +
                            // tick-driven detector stop). The
                            // error is logged for `Ctrl+Shift+;`
                            // post-hoc diagnosis.
                        with _ -> ()
                } :> Task)

        // Stage 7 PR-B — resolve which shell to spawn. cmd.exe stays
        // the default per maintainer instruction; `PTYSPEAK_SHELL=claude`
        // / `=powershell` / `=pwsh` (or any future menu UI) flips it.
        // Unrecognised env-var values fall back to cmd with a warning
        // log so the user isn't locked out of a working terminal by a
        // typo. PR-C added Ctrl+Shift+1 (cmd) / Ctrl+Shift+2 hotkeys;
        // PR-J reordered: Ctrl+Shift+1 = cmd, +2 = PowerShell, +3 =
        // claude.
        //
        // Cycle 19 — `[startup] default_shell` TOML override.
        // Precedence (highest → lowest): TOML config → env var
        // → cmd built-in default. Use case: maintainer has
        // `PTYSPEAK_SHELL=claude` set from prior testing + wants
        // cmd as durable default without manipulating env vars.
        // Setting `[startup] default_shell = "cmd"` in the TOML
        // wins over the env var.
        let resolveStartupShell () : ShellRegistry.Shell * string =
            let envVar = Environment.GetEnvironmentVariable("PTYSPEAK_SHELL")
            // Distinguish "unset" from "set to garbage" so the
            // log line is actionable. `null` / empty / whitespace
            // is the common case (no env var set); a non-empty
            // unrecognised value is a typo or stale config and
            // earns a warning. Extracted to a helper so the
            // arm body of `parseEnvVar`'s `None` case stays a
            // single expression — F# 9 + `TreatWarningsAsErrors`
            // can be brittle about sequence-in-match-arm
            // shapes, and the helper sidesteps that risk.
            let logIfUnrecognised () : unit =
                match envVar with
                | null -> ()
                | v when System.String.IsNullOrWhiteSpace(v) -> ()
                | v ->
                    log.LogWarning(
                        "PTYSPEAK_SHELL=\"{Value}\" not recognised; falling back to cmd.exe. Recognised values: cmd, claude, powershell, pwsh.",
                        v)
            // Cycle 19 — TOML config takes precedence. When set,
            // we use it directly without consulting the env
            // var. The Config-side parser already validated the
            // value against `knownShellKeys`; here we just map
            // `string → ShellId` via `parseEnvVar`.
            let configShell =
                match Config.resolveDefaultShell config with
                | Some shellKey ->
                    match ShellRegistry.parseEnvVar shellKey with
                    | Some id ->
                        log.LogInformation(
                            "Startup shell resolved from [startup] default_shell = \"{Shell}\" (overriding PTYSPEAK_SHELL).",
                            shellKey)
                        Some id
                    | None ->
                        // Defensive — Config-side parser already
                        // filtered against `knownShellKeys`, so this
                        // arm shouldn't fire. Log + fall through.
                        log.LogWarning(
                            "Config: [startup] default_shell = \"{Shell}\" passed schema validation but parseEnvVar rejected it; falling through to PTYSPEAK_SHELL.",
                            shellKey)
                        None
                | None -> None
            let requested =
                match configShell with
                | Some id -> id
                | None ->
                    match ShellRegistry.parseEnvVar envVar with
                    | Some id -> id
                    | None ->
                        logIfUnrecognised ()
                        ShellRegistry.Cmd
            // `tryFind` only returns None for ids not registered in
            // `builtIns`; both Cmd and Claude are registered, so this
            // is unreachable for the requested id, but the cmd-fallback
            // is shared with the resolution-failure branch below.
            let cmdShell =
                match ShellRegistry.tryFind ShellRegistry.Cmd with
                | Some s -> s
                | None -> failwith "Cmd not registered in ShellRegistry.builtIns"
            match ShellRegistry.tryFind requested with
            | None ->
                cmdShell, "cmd.exe"
            | Some shell ->
                match shell.Resolve() with
                | Ok cmdLine ->
                    shell, cmdLine
                | Error reason ->
                    log.LogWarning(
                        "Shell {Shell} unavailable: {Reason}. Falling back to {Fallback}.",
                        shell.DisplayName,
                        reason,
                        cmdShell.DisplayName)
                    let fallbackCmd =
                        match cmdShell.Resolve() with
                        | Ok c -> c
                        | Error _ -> "cmd.exe"
                    cmdShell, fallbackCmd

        let chosenShell, commandLine = resolveStartupShell ()
        log.LogInformation(
            "Startup shell: {Shell} (command line: {CommandLine}).",
            chosenShell.DisplayName,
            commandLine)

        // Phase A — align the active pathway with the resolved
        // startup shell. v1 maps cmd / powershell / claude all
        // to StreamPathway, so this swap is a no-op today (the
        // mutable was already initialised to StreamPathway).
        // Phase 2 will map Claude to a ClaudeCodePathway; this
        // swap ensures the right pathway is active when the
        // PathwayPump processes the first ScreenNotification.
        try activePathway.Reset () with _ -> ()
        activePathway <- selectPathwayForShell chosenShell.Id
        // Phase B (subset) — sync the alt-screen-detect
        // bookkeeping with the resolved startup shell so the
        // PathwayPump's `SwapToShellDefault` (alt-screen exit)
        // resolves to the correct pathway for the running
        // shell.
        currentShellId <- chosenShell.Id

        // SessionModel Tier 1.C — re-create with the resolved
        // startup shell key. The placeholder declared above
        // (with `cmd` key) is replaced here once `chosenShell`
        // is known; no boundaries can have arrived yet (the
        // ConPty isn't spawned until `wirePostSpawn`), so
        // recreating discards no observable state.
        currentSession <-
            SessionModel.createDefault (shellIdToConfigKey chosenShell.Id)

        // Stage 7-followup PR-I — `chosenShell` is the value chosen
        // at startup and never changes. Hot-switching shells
        // (`Ctrl+Shift+1` / `Ctrl+Shift+2`) updates `currentShell`
        // below; the heartbeat + health-check both read this for
        // their state snapshots. The empirical NVDA pass on
        // 2026-05-03 surfaced this as a follow-on finding: the
        // post-switch heartbeat continued reporting
        // "Shell=Command Prompt" after the user had switched to
        // claude.exe, because `chosenShell.DisplayName` was the
        // captured-at-startup value. Cosmetic for the user but
        // confusing for log analysis.
        let mutable currentShell : ShellRegistry.Shell = chosenShell

        let cfg : PtyConfig =
            { Cols = int16 ScreenCols
              Rows = int16 ScreenRows
              CommandLine = commandLine }

        let mutable hostHandle : ConPtyHost option = None

        // Stage 7-followup PR-F — last-byte timestamp shared across
        // every reader (initial spawn + each shell hot-switch). The
        // heartbeat timer + Ctrl+Shift+H health check both read
        // this to derive reader-loop staleness ("how long since the
        // PTY produced output"), which is the single most useful
        // signal for distinguishing "pty-speak is wedged" from
        // "the shell is just idle waiting for input". Updates are
        // unsynchronised — DateTimeOffset is 16 bytes (torn read
        // possible) but the heartbeat is a diagnostic; one
        // weird-looking entry occasionally is acceptable.
        let mutable lastReadUtc = DateTimeOffset.UtcNow

        // Stage 7-followup PR-F — incident-marker handler. Single
        // press writes a clear "=== INCIDENT MARKER {timestamp} ==="
        // boundary line to the active log via the standard
        // ILogger/FileLogger path, then announces via NVDA. The
        // user reproduces the issue, then copies the log via
        // Ctrl+Shift+;; server-side grep for the marker extracts
        // the relevant slice from the full log. Replaces the
        // env-var-and-relaunch debug-capture workflow with three
        // keystrokes (G, B, ;) entirely inside pty-speak.
        let runIncidentMarker () : unit =
            try
                let timestamp =
                    DateTimeOffset.UtcNow.ToString(
                        "yyyy-MM-ddTHH:mm:ss.fffZ")
                let markerLog = Logger.get "Terminal.App.IncidentMarker"
                markerLog.LogInformation(
                    "=== INCIDENT MARKER {Timestamp} === (Ctrl+Shift+B)",
                    timestamp)
                window.TerminalSurface.Announce(
                    "Incident marker logged. Reproduce your issue, then press Ctrl+Shift+; to copy the log.",
                    ActivityIds.incidentMarker)
            with ex ->
                let safe = AnnounceSanitiser.sanitise ex.Message
                log.LogError(ex, "Incident marker raised exception.")
                window.TerminalSurface.Announce(
                    sprintf "Incident marker failed: %s" safe,
                    ActivityIds.error)

        // Stage 7-followup PR-F — health-check handler. Reads
        // current runtime state (shell + PID, log level, last-byte
        // staleness, channel queue depths) and announces a one-line
        // summary. The summary opens with a status verdict
        // (healthy / queue-near-capacity / reader-wedged) so a
        // screen-reader user can determine in one keystroke whether
        // pty-speak is functioning, instead of inferring from "is
        // NVDA reading anything?".
        let runHealthCheck () : unit =
            try
                let now = DateTimeOffset.UtcNow
                let staleness = (now - lastReadUtc).TotalMilliseconds
                let pid =
                    match hostHandle with
                    | Some h -> int h.ProcessId
                    | None -> 0
                // Stage 7-followup PR-J — liveness probe. The
                // ConPtyHost handle's recorded PID is the value we
                // captured at spawn; the OS may have already reaped
                // the child (e.g. claude.exe exited silently after
                // a terminal-capability handshake stalled, or
                // PowerShell's startup banner threw). Check whether
                // the kernel still knows about that PID via
                // Process.GetProcessById, which throws
                // ArgumentException for a reaped/non-existent PID.
                // The result feeds both the verdict and the
                // user-facing announce so a screen-reader user can
                // distinguish "child exited" from "child running
                // but quiet" — a distinction that previously
                // required reading the log file.
                let alive =
                    if pid <= 0 then
                        false
                    else
                        try
                            use _ = System.Diagnostics.Process.GetProcessById(pid)
                            true
                        with _ -> false
                let levelStr =
                    match loggerSink with
                    | Some s -> s.MinLevel.ToString()
                    | None -> "Unknown"
                let notifDepth = pumpChannel.Reader.Count
                // Verdict heuristic, in priority order:
                //   1. Child PID dead → "Child shell process has
                //      exited." (highest signal — explains every
                //      downstream symptom).
                //   2. Reader wedge: no bytes in 5+ seconds AND
                //      pending work in the notification queue.
                //   3. Notification queue near capacity (95%) —
                //      DropOldest means past-capacity is silent
                //      data loss; warn before that.
                //   4. Healthy.
                //
                // Stage 8b: the coalescedChannel was removed when
                // the Coalescer absorbed into the Stream profile;
                // queue-depth reporting now covers only the
                // notification channel.
                let verdict =
                    if pid > 0 && not alive then
                        sprintf
                            "Child shell process %d has exited."
                            pid
                    elif staleness > 5000.0 && notifDepth > 0 then
                        sprintf
                            "Reader appears wedged. Last byte %.0f seconds ago."
                            (staleness / 1000.0)
                    elif notifDepth >= 244 then
                        sprintf
                            "Notification queue near capacity (%d of 256)."
                            notifDepth
                    else
                        "Pty-speak healthy."
                let aliveStr = if alive then "alive" else "dead"
                let summary =
                    sprintf
                        "%s %s shell, PID %d (%s), log level %s. Reader last byte %.0f ms ago. Notification queue %d of 256."
                        verdict
                        currentShell.DisplayName
                        pid
                        aliveStr
                        levelStr
                        staleness
                        notifDepth
                log.LogInformation("Health check requested. {Summary}", summary)
                window.TerminalSurface.Announce(
                    summary,
                    ActivityIds.healthCheck)
            with ex ->
                let safe = AnnounceSanitiser.sanitise ex.Message
                log.LogError(ex, "Health check raised exception.")
                window.TerminalSurface.Announce(
                    sprintf "Health check failed: %s" safe,
                    ActivityIds.error)

        // Cycle 25b-1a — single source of truth for the
        // session-log mode/path triage line. Both Ctrl+Shift+S
        // (verbal announce, via runAnnounceSessionLogPath below)
        // and Ctrl+Shift+D's bundle (via the resolveSessionLogSummary
        // closure passed into runFullBattery) read this. Defined
        // before runDiagnostic so the closure can capture it.
        let buildSessionLogSummary () : string =
            let modeStr =
                SessionPersistence.modeToString
                    persistenceConfig.Mode
            match sessionLogWriter with
            | None ->
                sprintf
                    "Session log mode %s; no file."
                    modeStr
            | Some sink ->
                sprintf
                    "Session log mode %s; path %s."
                    modeStr
                    sink.ActiveLogPath

        // PR #165 — Ctrl+Shift+D (extended diagnostic battery).
        // The closure is defined here (not at module top) because
        // it reads `hostHandle`, `currentShell`, and the live
        // `screen.SequenceNumber` for quiescence detection — all
        // compose-local. Each resolver runs at hotkey-press time
        // so a hot-switch between Ctrl+Shift+D presses is picked
        // up correctly.
        let runDiagnostic () : unit =
            Diagnostics.runFullBattery
                window
                (fun () -> hostHandle)
                (fun () -> currentShell)
                (fun () -> screen.SequenceNumber)
                // Tier 1.F — SessionModel substrate snapshot.
                // Closure captures `currentSession`,
                // `promptDetector`, and `activePathway` from the
                // `compose ()` local scope. Resolves at
                // hotkey-press time so a hot-switch (and the
                // associated `currentSession` recreation) is
                // picked up correctly. Diagnostic log + announce
                // surfaces substrate state per Tier 1.F's
                // observability deliverable.
                (fun () ->
                    Diagnostics.captureSessionModel
                        currentSession
                        promptDetector
                        activePathway.Id)
                // Cycle 25b — snapshot-bundle path resolver. The
                // diagnostic battery now includes the FileLogger
                // active log slice in its dump-and-clipboard
                // bundle so a paste-back to triage chat carries
                // Cycle 24f / 24g `Config:` parse messages, the
                // heartbeat trail, and any error-path log lines
                // alongside the battery's own output. Resolves at
                // press-time because `loggerSink` is a module-
                // level mutable that compose () sets after this
                // closure is captured.
                (fun () ->
                    loggerSink |> Option.map (fun s -> s.ActiveLogPath))
                // Cycle 25b-1a — session-log summary line for the
                // bundle's `--- SESSION LOG ---` section. Same
                // string Ctrl+Shift+S announces. Closure captures
                // `buildSessionLogSummary` (defined above) so the
                // mode/path are resolved at press-time and reflect
                // any reload-on-shell-switch that happened since.
                buildSessionLogSummary

        // Cycle 22b — Ctrl+Shift+Y. Closure captures
        // `currentSession` from compose-local scope. Resolves
        // at hotkey-press time so a hot-switch (and the
        // associated `currentSession` recreation) is picked
        // up correctly. Mirrors the `runCopyLatestLog`
        // pattern (Ctrl+Shift+;): the heavy lifting runs in
        // a Task off the dispatcher, with a dedicated STA
        // thread for `Clipboard.SetText` (which requires
        // STA apartment + can hang on contention with NVDA's
        // clipboard hooks). The hotkey handler returns
        // immediately; the dispatcher never blocks.
        let runCopyHistoryToClipboard () : unit =
            let log =
                Logger.get
                    "Terminal.App.Program.runCopyHistoryToClipboard"
            log.LogInformation(
                "Ctrl+Shift+Y pressed — copying SessionModel history to clipboard.")
            // Snapshot currentSession at hotkey-press time on
            // the dispatcher thread. F# records are immutable
            // so field-level reads are tear-free; the worst
            // case is ~50ms staleness if a tick fires
            // concurrently. Same pattern Cycle 16's
            // diagnostic-snapshot capture uses (Ctrl+Shift+D).
            let snapshot = currentSession
            let now = DateTime.UtcNow
            let content =
                SessionModel.formatHistoryForClipboard now snapshot
            let entryCount = snapshot.History.Count
            let activeSuffix =
                if Option.isSome snapshot.Active then
                    " plus active tuple"
                else
                    ""
            let _ =
                task {
                    try
                        // Same STA-thread + 3s-timeout pattern
                        // as `runCopyLatestLog`. STA required
                        // by Clipboard.SetText; bounded timeout
                        // prevents hang on clipboard contention.
                        let setOk = TaskCompletionSource<bool>()
                        let staBody = ThreadStart(fun () ->
                            try
                                System.Windows.Clipboard.SetText(content)
                                setOk.TrySetResult(true) |> ignore
                            with ex ->
                                log.LogWarning(
                                    ex,
                                    "Clipboard.SetText threw: {Message}",
                                    ex.Message)
                                setOk.TrySetResult(false) |> ignore)
                        let staThread = new Thread(staBody)
                        staThread.SetApartmentState(ApartmentState.STA)
                        staThread.IsBackground <- true
                        staThread.Start()
                        let! winner =
                            Task.WhenAny(
                                setOk.Task :> Task,
                                Task.Delay(3000))
                        let succeeded =
                            obj.ReferenceEquals(winner, setOk.Task)
                            && setOk.Task.Result
                        let bytes =
                            System.Text.Encoding.UTF8.GetByteCount(content)
                        let msg, activityId =
                            if succeeded then
                                log.LogInformation(
                                    "Copied SessionModel history to clipboard. Entries={Count} Bytes={Bytes}",
                                    entryCount, bytes)
                                sprintf
                                    "History copied to clipboard. %d of %d entries%s, %d bytes."
                                    entryCount
                                    snapshot.MaxHistorySize
                                    activeSuffix
                                    bytes,
                                ActivityIds.diagnostic
                            else
                                log.LogWarning(
                                    "Clipboard.SetText timed out or failed after 3s; clipboard may not contain history content.")
                                "Clipboard copy timed out. Try again.",
                                ActivityIds.error
                        let action () =
                            window.TerminalSurface.Announce(msg, activityId)
                        do! window.Dispatcher.InvokeAsync(Action(action)).Task
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        log.LogError(
                            ex, "Failed to copy SessionModel history.")
                        let action () =
                            window.TerminalSurface.Announce(
                                sprintf "Could not copy history: %s" safe,
                                ActivityIds.error)
                        try
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                        with _ -> ()
                }
            ()

        // Cycle 24e — `Ctrl+Shift+S`: announce the active
        // session-log file path. Reads `sessionLogWriter` (post
        // -Cycle-24c) + `persistenceConfig.Mode` (post-Cycle-24a)
        // from compose-local state. The closure is defined here
        // (rather than as a module-level handler) for the same
        // reason as `runHealthCheck` — it captures mutable
        // compose-local references that don't exist at
        // module-load time.
        let runAnnounceSessionLogPath () : unit =
            try
                let summary = buildSessionLogSummary ()
                log.LogInformation(
                    "Session log path requested. {Summary}",
                    summary)
                window.TerminalSurface.Announce(
                    summary,
                    ActivityIds.sessionLogPath)
            with ex ->
                let safe = AnnounceSanitiser.sanitise ex.Message
                log.LogError(
                    ex,
                    "Session log path announce raised exception.")
                window.TerminalSurface.Announce(
                    sprintf
                        "Session log path announce failed: %s"
                        safe,
                    ActivityIds.error)

        // Stage 7-followup PR-F — wire Ctrl+Shift+H and Ctrl+Shift+B
        // through the unified `bindHotkey` helper (PR-O). Both
        // closures capture compose-local state (lastReadUtc,
        // hostHandle, channels, currentShell) so they're built
        // here rather than as module-level handlers like the
        // Ctrl+Shift+G toggle (which only needs loggerSink).
        bind HotkeyRegistry.HealthCheck runHealthCheck
        bind HotkeyRegistry.IncidentMarker runIncidentMarker
        bind HotkeyRegistry.RunDiagnostic runDiagnostic
        bind HotkeyRegistry.CopyHistoryToClipboard runCopyHistoryToClipboard
        bind HotkeyRegistry.AnnounceSessionLogPath runAnnounceSessionLogPath

        // Stage 7-followup PR-F — background heartbeat. Every 5
        // seconds, log a single Information-level "Heartbeat" line
        // capturing the same state the health check announces.
        // Heartbeats stopping appear as a clean wedge timestamp in
        // the log when troubleshooting later. Runs on the
        // System.Threading.Timer thread pool — does NOT touch the
        // WPF dispatcher, so the heartbeat keeps emitting even if
        // the dispatcher is wedged.
        let runHeartbeat () : unit =
            try
                let now = DateTimeOffset.UtcNow
                let staleness = (now - lastReadUtc).TotalMilliseconds
                let pid =
                    match hostHandle with
                    | Some h -> int h.ProcessId
                    | None -> 0
                // PR-J — same liveness probe as runHealthCheck.
                // Logging the alive flag every 5s gives post-hoc
                // log analysis a clean "child died at HH:MM:SS"
                // breadcrumb without needing the user to press
                // Ctrl+Shift+H at the right moment.
                let alive =
                    if pid <= 0 then
                        false
                    else
                        try
                            use _ = System.Diagnostics.Process.GetProcessById(pid)
                            true
                        with _ -> false
                let level =
                    match loggerSink with
                    | Some s -> s.MinLevel
                    | None -> LogLevel.Information
                let notifDepth = pumpChannel.Reader.Count
                log.LogInformation(
                    "Heartbeat. Shell={Shell} Pid={Pid} Alive={Alive} Level={Level} LastReadAgoMs={Staleness:F0} NotifQueue={Notif}/{NotifCap}",
                    currentShell.DisplayName,
                    pid,
                    alive,
                    level,
                    staleness,
                    notifDepth,
                    256)
            with ex ->
                // Don't let heartbeat crashes propagate to the
                // timer thread — log and continue.
                try
                    log.LogWarning(
                        ex,
                        "Heartbeat raised exception: {Message}",
                        ex.Message)
                with _ -> ()
        let heartbeatTimer =
            new System.Threading.Timer(
                callback = TimerCallback(fun _ -> runHeartbeat ()),
                state = null,
                dueTime = TimeSpan.FromSeconds(5.0),
                period = TimeSpan.FromSeconds(5.0))

        // Stage 7 PR-C — extracted from the initial-spawn branch
        // so the shell-switch coordinator below can reuse the
        // exact same wiring without duplication. Wires the
        // env-scrub log + reader-loop start + SetPtyHost
        // keyboard/paste/focus/resize callbacks. Caller is
        // responsible for setting `hostHandle <- Some host`
        // before invoking; this function only does the
        // post-host setup.
        //
        // Stage 6 PR-B keyboard-pipeline rationale (preserved
        // verbatim from the pre-PR-C inline form): SetPtyHost
        // takes two callbacks because Views/ intentionally
        // doesn't reference Terminal.Pty (would break the
        // F#-first / WPF-only-at-the-edge boundary). The view
        // invokes them on the WPF dispatcher thread, which is
        // also the only thread that touches the ConPTY stdin
        // pipe (single-writer discipline).
        let wirePostSpawn (host: ConPtyHost) : unit =
            log.LogInformation(
                "ConPTY child spawned. Pid={Pid}",
                host.ProcessId)
            // Stage 7 PR-A — env-scrub PO-5. PR-K expanded the
            // log line: previous "stripped {Count}" only reported
            // deny-list strikes (`*_TOKEN`/`*_SECRET`/`*_KEY`/
            // `*_PASSWORD`) and obscured the much larger drop count
            // from the allow-list filter. The 2026-05-03 NVDA pass
            // surfaced this when "stripped 0" appeared in the log
            // while PowerShell + claude.exe were dying because ~50
            // parent vars (SystemRoot, WINDIR, TEMP, …) were being
            // silently stripped. New format reports the full
            // kept/parent picture so future regressions of the same
            // shape are visible at a glance.
            //
            // Counts only — never names or values (per `SECURITY.md`
            // logging discipline: env-var names like `BANK_API_KEY`
            // are themselves sensitive).
            log.LogInformation(
                "Env-scrub: kept {Kept} of {Parent} parent vars; dropped {Denied} as sensitive (deny-list).",
                host.EnvScrubKeptCount,
                host.EnvScrubParentCount,
                host.EnvScrubStrippedCount)
            let _ =
                startReaderLoop
                    window.Dispatcher
                    host
                    parser
                    screen
                    window.TerminalSurface
                    pumpChannel.Writer
                    (fun _ -> lastReadUtc <- DateTimeOffset.UtcNow)
                    cts.Token
            window.TerminalSurface.SetPtyHost(
                Action<byte[]>(fun bytes -> host.WriteBytes(bytes)),
                Action<int, int>(fun cols rows ->
                    // Resize is best-effort: a transient failure
                    // (e.g. the child has just exited) shouldn't
                    // crash the app. The next SizeChanged tick
                    // retries naturally.
                    match host.Resize(int16 cols, int16 rows) with
                    | Ok () -> ()
                    | Error _ -> ()))

        // Defer ConPTY spawn until the window has loaded so any startup
        // failure can surface in the UI rather than crashing during
        // Application.OnStartup.
        window.Loaded.Add(fun _ ->
            log.LogInformation(
                "Window loaded; spawning ConPTY child. Cols={Cols} Rows={Rows} CommandLine={CommandLine}",
                cfg.Cols,
                cfg.Rows,
                cfg.CommandLine)
            match ConPtyHost.start cfg with
            | Error e ->
                log.LogError(
                    "ConPTY spawn failed: {Error}",
                    sprintf "%A" e)
                // Publish a ParserError so the user hears about
                // ConPTY spawn failures via NVDA rather than
                // staring at a silent empty terminal.
                let _ =
                    pumpChannel.Writer.TryWrite(
                        Notification
                            (ParserError "ConPTY child process failed to start."))
                ()
            | Ok host ->
                hostHandle <- Some host
                wirePostSpawn host)

        // Stage 7 PR-C — shell hot-switch coordinator.
        // `Ctrl+Shift+1` (cmd) / `Ctrl+Shift+2` (claude) call
        // this with the target ShellId. It announces, waits for
        // NVDA's speech queue, tears down the current host,
        // spawns the new one with the new command line, and
        // re-wires the post-spawn callbacks via wirePostSpawn.
        //
        // Known limitations (defer to follow-up PRs if NVDA
        // validation flags them):
        //   * Screen state is NOT reset — the new shell's first
        //     paint overlays the previous screen. cmd → claude
        //     is clean (claude enters alt-screen via `?1049h`,
        //     clearing it). claude → cmd may briefly show alt-
        //     screen residue until cmd's prompt overwrites the
        //     primary buffer.
        //   * Parser state is NOT reset — if the previous shell
        //     terminated mid-CSI/OSC sequence, a few garbage
        //     bytes may parse oddly until the new shell sends
        //     a complete sequence and the parser re-syncs.
        //   * UIA peer ranges are NOT invalidated — NVDA's
        //     review cursor may briefly point at stale text
        //     until the new shell's first announce-bound
        //     output triggers a fresh Notification event.
        //
        // Each is acceptable for v1; the validation matrix in
        // PR-D documents any that surface as user-visible
        // problems and the framework cycles will own the
        // architectural fixes.
        //
        // **Framework-territory tag (PR-N).** Do not patch the
        // three caveats above incrementally. They're owned by
        // the Output framework cycle (Part 3 of
        // `docs/PROJECT-PLAN-2026-05.md`): the framework will
        // introduce an `OnShellSwitched` lifecycle signal that
        // each profile uses to reset its detection + semantic-
        // annotation state, which is the right seam for
        // screen-buffer reset, parser-state reset, and UIA peer
        // invalidation. Pre-framework drive-by fixes here would
        // create a precedent the framework either has to adopt
        // or break — both of which are worse than waiting.
        let switchToShell (target: ShellRegistry.ShellId) : unit =
            match ShellRegistry.tryFind target with
            | None ->
                log.LogWarning(
                    "Shell-switch target {Target} not registered in ShellRegistry.builtIns.",
                    sprintf "%A" target)
            | Some shell ->
                match shell.Resolve() with
                | Error reason ->
                    // Resolution failure (e.g. claude.exe not on
                    // PATH) — keep the existing host running so
                    // the user isn't dropped into a dead window;
                    // announce the failure so they know the
                    // hotkey didn't silently fail.
                    let safe = AnnounceSanitiser.sanitise reason
                    log.LogWarning(
                        "Shell-switch resolve failed for {Shell}: {Reason}",
                        shell.DisplayName,
                        reason)
                    window.TerminalSurface.Announce(
                        sprintf "Cannot switch to %s: %s" shell.DisplayName safe,
                        ActivityIds.error)
                | Ok newCmdLine ->
                    // Announce-before-launch pattern from
                    // Stage 4b's diagnostic-launcher: NVDA
                    // gets the cue into its speech queue
                    // BEFORE the new shell's first paint
                    // takes focus and triggers
                    // interrupt-on-focus-change. ~700ms is
                    // the empirically-validated wait window.
                    window.TerminalSurface.Announce(
                        sprintf "Switching to %s." shell.DisplayName,
                        ActivityIds.shellSwitch)
                    let _ =
                        task {
                            do! Task.Delay(700)
                            let action () =
                                try
                                    match hostHandle with
                                    | Some h ->
                                        (h :> IDisposable).Dispose()
                                        hostHandle <- None
                                    | None -> ()
                                    let newCfg : PtyConfig =
                                        { Cols = int16 ScreenCols
                                          Rows = int16 ScreenRows
                                          CommandLine = newCmdLine }
                                    log.LogInformation(
                                        "Shell-switch: spawning {Shell}. CommandLine={CommandLine}",
                                        shell.DisplayName,
                                        newCmdLine)
                                    match ConPtyHost.start newCfg with
                                    | Error e ->
                                        log.LogError(
                                            "Shell-switch ConPTY spawn failed: {Error}",
                                            sprintf "%A" e)
                                        window.TerminalSurface.Announce(
                                            sprintf "Could not launch %s." shell.DisplayName,
                                            ActivityIds.error)
                                    | Ok newHost ->
                                        hostHandle <- Some newHost
                                        // Stage 7-followup PR-I — update
                                        // currentShell so subsequent
                                        // heartbeats + health checks
                                        // report the post-switch shell
                                        // identity. Captured-at-startup
                                        // `chosenShell` was never
                                        // updated previously; the
                                        // 2026-05-03 NVDA pass log
                                        // showed
                                        // "Heartbeat. Shell=Command Prompt Pid=2596"
                                        // after switching to claude
                                        // (cosmetic but confusing for
                                        // log analysis).
                                        currentShell <- shell
                                        // Phase B (subset) —
                                        // keep alt-screen-
                                        // detect bookkeeping
                                        // aligned with the
                                        // hot-switched shell so
                                        // a subsequent alt-
                                        // screen-exit swap
                                        // resolves to the new
                                        // shell's default
                                        // pathway, not the
                                        // pre-switch shell's.
                                        currentShellId <- shell.Id
                                        // SessionModel Tier 1.C —
                                        // per-shell-session
                                        // recreation (per Q5
                                        // resolution 2026-05-07).
                                        // Finalise the prior
                                        // shell's in-flight active
                                        // tuple as incomplete
                                        // (CommandFinishedAt =
                                        // shell-switch-time;
                                        // ExitCode = None) before
                                        // recreating. Tier 1.C has
                                        // no persistence so the
                                        // finalised tuple is
                                        // structurally
                                        // discarded with the prior
                                        // SessionModel; Tier 2's
                                        // persistence will use
                                        // this seam to flush
                                        // History before
                                        // recreation. The
                                        // finalize-then-recreate
                                        // ordering preserves the
                                        // invariant that no tuple
                                        // ever leaves the
                                        // substrate without a
                                        // CommandFinishedAt.
                                        // Cycle 24c — capture the
                                        // shell-switch finalised
                                        // tuple (if any) and flush
                                        // it through the OLD
                                        // sink before rotating to
                                        // the new shell's sink.
                                        // Cycle 24d-1: routed via
                                        // `dispatchTupleToWriter`
                                        // so `Always` mode blocks
                                        // until durable.
                                        let nextSession, finalisedOpt =
                                            SessionModel.finalizeIncompleteAndCapture
                                                currentSession
                                                DateTime.UtcNow
                                        currentSession <- nextSession
                                        match finalisedOpt with
                                        | Some tuple ->
                                            dispatchTupleToWriter tuple
                                        | None -> ()
                                        // Cycle 24c — dispose the
                                        // outgoing shell's sink
                                        // BEFORE creating the
                                        // fresh SessionModel; the
                                        // dispose flushes pending
                                        // entries to the old file.
                                        // A new sink is then
                                        // constructed for the new
                                        // shell's SessionId below.
                                        match sessionLogWriter with
                                        | Some sink ->
                                            (sink :> System.IDisposable).Dispose()
                                        | None -> ()
                                        sessionLogWriter <- None
                                        // Cycle 25a — reload TOML
                                        // before constructing the
                                        // new shell's sink so any
                                        // mid-session edit takes
                                        // effect on the next switch.
                                        // Persistence + sanitiser are
                                        // the only knobs that
                                        // meaningfully change between
                                        // shell sessions; default_shell
                                        // is moot (target shell is
                                        // already chosen), [logging]
                                        // honors its established
                                        // startup-only ephemerality
                                        // (Ctrl+Shift+G is the runtime
                                        // path), and [pathway.stream]
                                        // stays startup-only this
                                        // cycle.
                                        try
                                            let freshConfig =
                                                Config.tryLoad
                                                    log
                                                    (Config.defaultConfigFilePath ())
                                            persistenceConfig <-
                                                freshConfig.SessionPersistence
                                            SessionSanitiser.registerFromEnvironment log
                                            |> ignore
                                        with ex ->
                                            log.LogWarning(
                                                ex,
                                                "Cycle 25a: config reload on shell-switch failed; keeping prior persistenceConfig.")
                                        currentSession <-
                                            SessionModel.createDefault
                                                (shellIdToConfigKey shell.Id)
                                        sessionLogWriter <-
                                            sessionLogWriterFactory
                                                currentSession.SessionId
                                        // SessionModel Tier 1.D —
                                        // reset heuristic
                                        // detector for the new
                                        // shell. Stale per-row
                                        // matches from the
                                        // outgoing shell would
                                        // otherwise produce
                                        // phantom boundaries on
                                        // the new shell's first
                                        // frame.
                                        promptDetector <-
                                            HeuristicPromptDetector.reset
                                                promptDetector
                                        // Cycle 29b — companion
                                        // reset on shell-switch
                                        // so a stale candidate
                                        // from the prior shell
                                        // doesn't leak into the
                                        // new one.
                                        selectionDetector <-
                                            SelectionDetector.reset
                                                selectionDetector
                                        // Phase A — swap the active
                                        // pathway for the new shell.
                                        // `Reset` clears any pending
                                        // diff + last-emitted row
                                        // hash baseline on the
                                        // outgoing pathway so its
                                        // state can't leak into the
                                        // new shell's session;
                                        // `selectPathwayForShell`
                                        // picks the right pathway
                                        // shape for the new shell
                                        // (today all three
                                        // built-in shells map to
                                        // StreamPathway, but Phase 2
                                        // will swap Claude to a
                                        // ClaudeCodePathway and the
                                        // hot-switch path will
                                        // start to differentiate).
                                        try activePathway.Reset () with _ -> ()
                                        activePathway <-
                                            selectPathwayForShell shell.Id
                                        // Phase A.1 — seed the new
                                        // pathway's diff baseline
                                        // with the screen's current
                                        // snapshot so the next
                                        // Consume emits only the
                                        // new shell's paint, not the
                                        // residual content the old
                                        // shell left on the screen.
                                        // The screen buffer is NOT
                                        // cleared on shell-switch
                                        // (separate framework-cycle
                                        // concern, see the comment
                                        // block above), so without
                                        // this seed the user hears
                                        // the previous shell's
                                        // content re-announced when
                                        // the new shell's first
                                        // RowsChanged arrives. Must
                                        // happen BEFORE wirePostSpawn
                                        // starts the reader loop so
                                        // there's no race with the
                                        // new shell's first paint.
                                        try
                                            let seq, cursorPos, snapshot =
                                                screen.SnapshotRows(0, screen.Rows)
                                            let canonical =
                                                CanonicalState.create snapshot cursorPos seq
                                            activePathway.SetBaseline canonical
                                        with _ -> ()
                                        wirePostSpawn newHost
                                        window.TerminalSurface.Announce(
                                            sprintf "Switched to %s." shell.DisplayName,
                                            ActivityIds.shellSwitch)
                                with ex ->
                                    let safe = AnnounceSanitiser.sanitise ex.Message
                                    log.LogError(
                                        ex,
                                        "Shell-switch crashed: {Message}",
                                        ex.Message)
                                    window.TerminalSurface.Announce(
                                        sprintf "Shell switch failed: %s" safe,
                                        ActivityIds.error)
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                            ()
                        }
                    ()

        // Wire `Ctrl+Shift+1` → cmd, `Ctrl+Shift+2` → PowerShell,
        // `Ctrl+Shift+3` → claude. PR-J reordered the slots and
        // added PowerShell as the diagnostic control shell (always
        // installed, no auth, fast prompt) so isolating shell-switch
        // infrastructure bugs from claude-specific issues is one
        // keypress away. PR-O (this PR) refactored the wiring
        // through the unified `bindHotkey` helper; the previous
        // local `bind` helper inside `setupShellSwitchKeybindings`
        // is now superseded by the module-level helper.
        //
        // The keys are listed in `TerminalView.AppReservedHotkeys`
        // so `OnPreviewKeyDown` doesn't mark them Handled before
        // `InputBindings` can fire.
        bind HotkeyRegistry.SwitchToCmd (fun () -> switchToShell ShellRegistry.Cmd)
        bind HotkeyRegistry.SwitchToPowerShell (fun () -> switchToShell ShellRegistry.PowerShell)
        bind HotkeyRegistry.SwitchToClaude (fun () -> switchToShell ShellRegistry.Claude)

        // Cycle 27 — wire the multi-state commands. Each
        // `bindMultiState` call registers per-option
        // `RoutedCommand` + `CommandBinding` (so each option's
        // MenuItem can dispatch via `MenuItem.Command`) and
        // captures `getCurrent` / `setByName` closures over the
        // backing state. The set-by-name closure is idempotent
        // (no-op when current ≡ target) so a no-change menu
        // selection logs once and announces once without flapping
        // the underlying state.
        //
        // Both migrated handlers reuse `ActivityIds.logToggle`
        // for announcements so NVDA users' notification-
        // processing configuration is unchanged from the prior
        // Ctrl+Shift+G / Ctrl+Shift+M era.
        let multiStateBindings =
            System.Collections.Generic.Dictionary<HotkeyRegistry.MultiStateCommand, MultiStateBinding>()

        let earconsLog = Logger.get "Terminal.App.Program.bindMultiState.EarconsMode"
        let earconsDef = HotkeyRegistry.multiStateOf HotkeyRegistry.EarconsMode
        let earconsBinding =
            bindMultiState
                window
                earconsDef
                (fun () ->
                    if EarconChannel.isMuted () then "muted" else "enabled")
                (fun target ->
                    let wantMuted = target = "muted"
                    let isMuted = EarconChannel.isMuted ()
                    if isMuted <> wantMuted then
                        EarconChannel.toggle () |> ignore
                        earconsLog.LogInformation(
                            "EarconsMode set to {Target}. NowMuted={NowMuted}",
                            target,
                            wantMuted)
                        let cue =
                            if wantMuted then "Earcons muted."
                            else "Earcons unmuted."
                        window.TerminalSurface.Announce(cue, ActivityIds.logToggle))
        multiStateBindings.[HotkeyRegistry.EarconsMode] <- earconsBinding

        let loggingLog = Logger.get "Terminal.App.Program.bindMultiState.LoggingLevel"
        let loggingDef = HotkeyRegistry.multiStateOf HotkeyRegistry.LoggingLevel
        let loggingBinding =
            bindMultiState
                window
                loggingDef
                (fun () ->
                    match loggerSink with
                    | Some sink when sink.MinLevel = LogLevel.Debug -> "debug"
                    | _ -> "information")
                (fun target ->
                    match loggerSink with
                    | None ->
                        loggingLog.LogWarning(
                            "LoggingLevel set requested but loggerSink is None; skipped. Target={Target}",
                            target)
                        window.TerminalSurface.Announce(
                            "Logger not initialised; level change skipped.",
                            ActivityIds.error)
                    | Some sink ->
                        let next =
                            match target with
                            | "debug" -> LogLevel.Debug
                            | _ -> LogLevel.Information
                        if sink.MinLevel <> next then
                            sink.SetMinLevel(next)
                            loggingLog.LogInformation(
                                "LoggingLevel set to {Target}. NewLevel={NewLevel}",
                                target,
                                next)
                            let cue =
                                match next with
                                | LogLevel.Debug -> "Debug logging on."
                                | _ -> "Debug logging off."
                            window.TerminalSurface.Announce(cue, ActivityIds.logToggle))
        multiStateBindings.[HotkeyRegistry.LoggingLevel] <- loggingBinding

        // Cycle 26b — wire the menu items. Walk the
        // `menuCommands` dictionary populated by the `bind`
        // wrapper above. For each entry, find the XAML-named
        // `MenuItem_<nameOf cmd>` element via reflection and
        // assign its `Command` (so menu click invokes the same
        // RoutedCommand as the keyboard gesture) and
        // `InputGestureText` (so NVDA reads the shortcut when
        // the menu item is focused). Reflection is the cheapest
        // way to keep the wiring data-driven — adding a new
        // menu item is just (a) AppCommand DU case, (b) builtIns
        // row, (c) named MenuItem in XAML; the wiring loop
        // below picks it up automatically.
        let windowType = window.GetType()
        for kv in menuCommands do
            let fieldName = sprintf "MenuItem_%s" (HotkeyRegistry.nameOf kv.Key)
            match windowType.GetField(fieldName) with
            | null ->
                // No XAML MenuItem with this name. Either the
                // command isn't surfaced in the menu yet (some
                // future cycle adds it) or the XAML name diverged
                // from the registry's nameOf — neither is fatal at
                // runtime but the missing surface is worth a log
                // line at a higher level if it ever bites.
                ()
            | field ->
                match field.GetValue(window) with
                | :? MenuItem as menuItem ->
                    menuItem.Command <- kv.Value
                    HotkeyRegistry.gestureText (HotkeyRegistry.hotkeyOf kv.Key)
                    |> Option.iter (fun text ->
                        menuItem.InputGestureText <- text)
                | _ -> ()

        // Cycle 27 — wire the multi-state menu items. For each
        // `MultiStateCommand` in `multiStateBindings`, find:
        //   - The parent MenuItem `MenuItem_<cmdName>`. Hook
        //     its `SubmenuOpened` event to refresh `IsChecked`
        //     on every child whenever the user opens the
        //     submenu (state is queried via `GetCurrent` then;
        //     no observer-style subscription needed).
        //   - Each child `MenuItem_<cmdName>_<optionId>`. Set
        //     `IsCheckable=true` (so WPF surfaces UIA
        //     TogglePattern that NVDA reads as
        //     "checked"/"not checked") and assign its `Command`
        //     to the per-option `RoutedCommand` from
        //     `bindMultiState`.
        //
        // Reflection-driven for the same reason as the
        // single-action wiring loop above — adding a new
        // multi-state command is just (a) MultiStateCommand DU
        // case + multiStateNameOf arm + multiStateBuiltIns row,
        // (b) `bindMultiState` call in compose(), (c) parent +
        // per-option MenuItems in XAML; the loop picks them up
        // automatically.
        for kv in multiStateBindings do
            let cmd = kv.Key
            let binding = kv.Value
            let def = HotkeyRegistry.multiStateOf cmd
            let cmdName = HotkeyRegistry.multiStateNameOf cmd
            for opt in def.Options do
                let childFieldName = sprintf "MenuItem_%s_%s" cmdName opt.OptionId
                match windowType.GetField(childFieldName) with
                | null ->
                    // No XAML MenuItem with this name. Skip
                    // silently — same forgiving behaviour as
                    // the single-action loop above.
                    ()
                | childField ->
                    match childField.GetValue(window) with
                    | :? MenuItem as childItem ->
                        childItem.IsCheckable <- true
                        match binding.PerOptionCommand.TryGetValue(opt.OptionId) with
                        | true, routed -> childItem.Command <- routed
                        | false, _ -> ()
                    | _ -> ()
            let parentFieldName = sprintf "MenuItem_%s" cmdName
            match windowType.GetField(parentFieldName) with
            | null -> ()
            | parentField ->
                match parentField.GetValue(window) with
                | :? MenuItem as parentItem ->
                    parentItem.SubmenuOpened.Add(fun _ ->
                        let currentId = binding.GetCurrent()
                        for opt in def.Options do
                            let childFieldName =
                                sprintf "MenuItem_%s_%s" cmdName opt.OptionId
                            match windowType.GetField(childFieldName) with
                            | null -> ()
                            | childField ->
                                match childField.GetValue(window) with
                                | :? MenuItem as childItem ->
                                    childItem.IsChecked <- (opt.OptionId = currentId)
                                | _ -> ())
                | _ -> ()

        app.Exit.Add(fun _ ->
            try log.LogInformation("pty-speak exiting.") with _ -> ()
            // Cycle 24c — flush + close the SessionLogWriter
            // BEFORE disposing the FileLogger provider so its
            // shutdown errors (if any) get captured in the log.
            // A 2-second timeout-bounded drain is built into the
            // sink's `Dispose`; on timeout the OS reclaims the
            // file handle on process exit.
            try
                match sessionLogWriter with
                | Some sink -> (sink :> System.IDisposable).Dispose()
                | None -> ()
            with _ -> ()
            try cts.Cancel() with _ -> ()
            // Stage 7-followup PR-F — stop the heartbeat timer
            // before the logger is disposed so the timer doesn't
            // fire one last entry into a closed channel and log a
            // confusing exception.
            try heartbeatTimer.Dispose() with _ -> ()
            // Complete the notification-channel writer so the
            // PathwayPump task exits cleanly when the channel
            // runs dry. Phase A replaced the Stage 8b
            // TranslatorPump+TickPump pair with the
            // PathwayPump+PathwayTickPump pair; both observe
            // `cts.Token.IsCancellationRequested` for shutdown
            // and the channel completion is the additional
            // backstop for the read-loop.
            try pumpChannel.Writer.TryComplete() |> ignore with _ -> ()
            match hostHandle with
            | Some h -> (h :> IDisposable).Dispose()
            | None -> ()
            cts.Dispose()
            // Dispose the logger LAST so any cleanup logging
            // above lands in the file. The provider's Dispose
            // cancels the channel writer, drains pending entries,
            // flushes, and closes the file.
            try (provider :> IDisposable).Dispose() with _ -> ())

    [<EntryPoint>]
    [<STAThread>]
    let main _argv =
        // VelopackApp.Build().Run() must execute before any WPF type loads
        // (Velopack issue #195). It returns immediately for normal launches
        // and short-circuits the process during install/update events.
        VelopackApp.Build().Run()

        let app = App()
        let window = MainWindow()
        compose app window
        app.Run(window)
