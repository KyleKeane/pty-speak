namespace PtySpeak.App

open System
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Automation.Peers
open System.Windows.Controls
open System.Windows.Input
open System.Windows.Threading
open Microsoft.Extensions.Logging
open Velopack
open Velopack.Sources
open Terminal.Core
open Terminal.Core.Channels
open Terminal.Audio
open Terminal.Pty
open Terminal.Shell
open Terminal.Accessibility
open PtySpeak.Views

module Program =

    let private ScreenRows = 30
    let private ScreenCols = 120

    /// Cycle 46 post-audit (2026-05-13) — cap for the tuple-
    /// final `Announce(text, ActivityIds.output)` call's payload
    /// length. The pre-cap behaviour shipped a single 19 KB
    /// notification for a default-shell `dir`, which NVDA hands
    /// to SAPI as one ~5–10 minute utterance that cannot be
    /// interrupted by another notification or by the
    /// Edit-control typed-character setting (NVDA's interrupt
    /// path doesn't displace an in-flight SAPI render).
    /// 800 characters caps the worst-case utterance to roughly
    /// 30–60 seconds at normal SAPI rate; the user hears the
    /// tail of the output, which usually carries the
    /// most-relevant bytes (final summary / next prompt / exit
    /// status). Full output remains in `ContentHistory` and is
    /// reachable via the `Ctrl+Shift+O` open-last-output hotkey
    /// (writes the tuple's full `OutputText` to a temp file
    /// under `%LOCALAPPDATA%\PtySpeak\extracts\` and opens it
    /// in the default text editor) or via `Ctrl+Shift+Y`
    /// (copies the SessionModel history to the clipboard).
    let private OutputAnnounceCapChars = 800

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
    /// (`currentSession`, `promptDetector`, `selectionDetector`,
    /// `contentHistory`, `speechCursor`). Cycle 45c retired the
    /// `activePathway` mutable that used to live in this list.
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
        /// the heuristic detector + `OutputDispatcher.dispatchTick`
        /// from the consumer thread. (Pre-Cycle-45c also drove
        /// `activePathway.Tick`; that call was deleted in PR-3b.)
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
            (adapter: Terminal.Shell.CmdAdapter)
            (screen: Screen)
            (view: TerminalView)
            (contentHistory: ContentHistory.T)
            (onSpeechCursorWake: unit -> unit)
            (notifications: System.Threading.Channels.ChannelWriter<PumpInput>)
            (onChunkRead: int -> unit)
            // Cycle 48 PR-B (ADR 0003) — observe each PTY-stdout
            // byte for the ShellInteraction sub-prompt detector.
            // Called once per byte, on the reader thread, before
            // VtParser sees it.
            (onByteFromPty: DateTime -> byte -> unit)
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
                            // Cycle 48 PR-B — observe bytes for
                            // ShellInteraction sub-prompt
                            // detection. Use a single timestamp
                            // per chunk to keep observations
                            // consistent within a chunk; finer
                            // resolution would not change
                            // sub-prompt outcomes (the threshold
                            // is in the hundreds of ms).
                            let chunkTs = DateTime.UtcNow
                            for i in 0 .. chunk.Length - 1 do
                                onByteFromPty chunkTs chunk.[i]
                            let events = adapter.Translate chunk
                            // Cycle 45c — feed ContentHistory (the
                            // sole aural substrate post-PR-3c).
                            // `appendFromEvent` is Gate-locked so
                            // the reader-thread feed is safe
                            // alongside pump-thread `appendMarker`
                            // calls from the boundary handler.
                            let now = DateTime.UtcNow
                            let mutable contentDirty = false
                            for ev in events do
                                let appended =
                                    ContentHistory.appendFromEvent
                                        contentHistory
                                        now
                                        ev
                                if not (List.isEmpty appended) then
                                    contentDirty <- true
                            if events.Length > 0 then
                                let action () =
                                    for e in events do screen.Apply(e)
                                    view.InvalidateScreen()
                                    // Cycle 45 Commit 2 —
                                    // SpeechCursor.onAppend reads
                                    // from ContentHistory directly
                                    // (idempotent w.r.t. multiple
                                    // dispatchers). We call it iff
                                    // this chunk produced new
                                    // entries; otherwise the
                                    // history is unchanged.
                                    if contentDirty then
                                        onSpeechCursorWake ()
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
        | HotkeyRegistry.Up -> Key.Up
        | HotkeyRegistry.Down -> Key.Down
        | HotkeyRegistry.End -> Key.End
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

    /// Cycle 38a-followup — Diagnostics → Open Manual Tests.
    /// Reads `ACCESSIBILITY-TESTING.md` (deployed alongside
    /// Terminal.App.exe via Content + CopyToOutput in
    /// `Terminal.App.fsproj`), filters to sections marked
    /// `<!-- DOGFOOD -->`, renders Markdig HTML wrapped in an
    /// HTML5 document with a `<main>` landmark, writes to
    /// `%LOCALAPPDATA%\PtySpeak\manual-tests.html`, and opens
    /// in the default browser via `ShellExecute`. NVDA browse-
    /// mode H key jumps section headings; D key jumps the
    /// `<main>` landmark.
    let private openManualTests (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.openManualTests"
        // Forward-looking announce BEFORE the browser launch so
        // NVDA has time to speak before focus changes. Mirrors
        // `runTestProcessCleanup`'s announce-then-Task.Delay-then-
        // launch pattern.
        window.TerminalSurface.Announce(
            "Opening manual tests in default browser.",
            ActivityIds.diagnostic)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        let mdPath =
                            System.IO.Path.Combine(
                                System.AppContext.BaseDirectory,
                                "ACCESSIBILITY-TESTING.md")
                        if not (System.IO.File.Exists mdPath) then
                            log.LogWarning(
                                "Manual-tests source markdown not found at {Path}",
                                mdPath)
                            window.TerminalSurface.Announce(
                                "Manual tests source file not found in install directory.",
                                ActivityIds.error)
                        else
                            let mdContent =
                                System.IO.File.ReadAllText(mdPath)
                            let html =
                                ManualTestsHtml.filterAndConvert mdContent
                            let outDir =
                                System.IO.Path.Combine(
                                    System.Environment.GetFolderPath(
                                        System.Environment.SpecialFolder.LocalApplicationData),
                                    "PtySpeak")
                            System.IO.Directory.CreateDirectory(outDir)
                            |> ignore
                            let htmlPath =
                                System.IO.Path.Combine(
                                    outDir,
                                    "manual-tests.html")
                            System.IO.File.WriteAllText(
                                htmlPath, html, System.Text.Encoding.UTF8)
                            let psi = System.Diagnostics.ProcessStartInfo()
                            psi.FileName <- htmlPath
                            psi.UseShellExecute <- true
                            System.Diagnostics.Process.Start(psi) |> ignore
                            log.LogInformation(
                                "Manual-tests HTML written and opened. Path={Path}",
                                htmlPath)
                    with ex ->
                        log.LogWarning(
                            ex,
                            "openManualTests failed: {Message}",
                            ex.Message)
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not open manual tests: %s" safe,
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
        // Cycle 45 Commit 2 — version reporting via the
        // `AssemblyInformationalVersionAttribute`. The build
        // pipeline injects the GitHub release tag (e.g.
        // "0.0.1-preview.90") at publish time; `Assembly.GetName()
        // .Version` (the old call) only carries the System.Version
        // 4-part shape (e.g. "0.0.1.0") which can't represent
        // prerelease suffixes. Mirror of MainWindow.xaml.cs:29-42.
        let resolveInformationalVersion () : string =
            try
                let asm = System.Reflection.Assembly.GetExecutingAssembly()
                // F# can't use the C#-extension-method form of
                // `Assembly.GetCustomAttribute<T>()`; route through
                // the static `System.Attribute.GetCustomAttribute`
                // instead and pattern-match on the boxed result.
                let attr =
                    System.Attribute.GetCustomAttribute(
                        asm,
                        typeof<System.Reflection.AssemblyInformationalVersionAttribute>)
                match attr with
                | :? System.Reflection.AssemblyInformationalVersionAttribute as a
                    when not (System.String.IsNullOrWhiteSpace a.InformationalVersion) ->
                    // R4-followup (build-identity, 2026-05-16) —
                    // KEEP the "+<short-sha>" trailer (was stripped
                    // here). A local build always reports the
                    // Directory.Build.props default Version
                    // (0.0.1-preview.1); the SHA appended by the
                    // `SetGitShortShaSourceRevision` target is the
                    // ONLY thing that distinguishes which commit is
                    // running. Surfaced via Ctrl+Shift+H and the
                    // startup log so a dogfood can confirm the
                    // build matches `git rev-parse --short HEAD`.
                    // Releases carry tag+sha — both unambiguous.
                    a.InformationalVersion
                | _ -> "unknown"
            with _ -> "unknown"
        log.LogInformation(
            "pty-speak starting. version={Version} os={Os} logs={LogDir}",
            resolveInformationalVersion (),
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            sink.LogDirectory)

        let cts = new CancellationTokenSource()
        let screen = Screen(rows = ScreenRows, cols = ScreenCols)
        // R1.4 (ADR 0006): the cmd transport adapter owns the VT
        // parser. Behaviour-identical seam — `Translate` is a
        // verbatim `Parser.feedArray` wrapper; created once and
        // not reset across shell switches, exactly as the bare
        // parser was.
        let cmdAdapter = Terminal.Shell.CmdAdapter()

        // Cycle 45 — ContentHistory + SpeechCursor are the aural
        // substrate (parallel to the screen-grid + UIA surface).
        // ContentHistory holds the shell session's append-only
        // typed log; SpeechCursor announces from it (AutoDrive)
        // and lets the user navigate it (Manual). Scope is the
        // shell session (NOT individual tuples) so the user can
        // Speech-Cursor backwards through completed commands —
        // `ContentHistory.reset` fires only when the shell
        // hot-switches.
        let mutable contentHistory =
            ContentHistory.create ContentHistory.defaultParameters
        let mutable speechCursor =
            SpeechCursor.create SpeechCursor.defaultParameters

        // Cycle 47 follow-up (2026-05-13) — idle-flush watermark.
        // Declared at compose-top alongside `contentHistory` so
        // both the boundary handler (further down in compose) and
        // the idle-flush `DispatcherTimer` (further down still)
        // can close over it. Tracks the highest ContentHistory
        // `seq` whose content has already been narrated via
        // Announce; both paths slice from this watermark, cap at
        // `OutputAnnounceCapChars`, fire Announce, advance the
        // watermark. The unified watermark means tuple-finalise
        // doesn't re-announce content idle-flush already covered,
        // and idle-flush fills the intra-script gaps
        // (`set /p` pauses, `pause` builtin, slow output between
        // sections) that PromptStart-driven tuple-finalise would
        // otherwise leave silent. Initial value -1L matches
        // `ContentHistory.latestSeq` on a fresh empty history;
        // resets to -1L on shell hot-switch (see
        // `ContentHistory.reset` call site in `switchToShell`).
        let mutable lastAnnouncedSeq : int64 = -1L

        // Cycle 48 PR-B (ADR 0003) — ShellInteraction state
        // machine, observe-only mode. Receives the same signals
        // as the existing announce paths (Enter pressed via
        // `writePtyBytes` wrapper, PromptStart via the boundary
        // handler, idle ticks via the idle-flush timer, byte
        // arrivals via the reader loop) and logs transitions
        // at Information. Does NOT alter announce routing or
        // ContentHistory state in this PR — that switch is
        // staged for PR-E. PR-C wires the resulting Source tag
        // into ContentHistory.Entry; PR-D adds the
        // UserInputBuffer.
        let shellInteraction = ShellInteraction.State()
        let shellInteractionLog =
            Logger.get "Terminal.Core.ShellInteraction"
        // Cycle 48 PR-E (ADR 0003) — recordTransition drives the
        // log AND, for SubPromptIdle transitions, the announce
        // path. Forward-declared here as a mutable ref so it can
        // be invoked from the boundary handler / writePtyBytes
        // wrapper / idle-flush timer (all defined before the
        // dependencies `currentShellPolicy` + `lastAnnouncedText`
        // get declared). The real implementation is assigned
        // below the dependencies; pre-assignment calls (none in
        // practice — all callers are event handlers that fire
        // post-compose) no-op.
        let mutable recordTransitionImpl
                : ShellInteraction.Transition -> unit =
            fun _ -> ()
        let recordTransition trigger = recordTransitionImpl trigger

        // Cycle 48 PR-C — wire ContentHistory's source-resolver
        // delegate to read the current ShellInteraction state.
        // appendFromEvent + Marker constructions consult this
        // closure on every entry so the resulting Entry.Source
        // reflects whether bytes arrived during Composing
        // (UserInputEcho) or Executing (CmdOutput).
        ContentHistory.setSourceResolver
            contentHistory
            (fun () ->
                ShellInteraction.entrySourceFor shellInteraction.Current)

        // Cycle 47 follow-up (2026-05-13) post-preview.114 —
        // companion text watermark: the most recent string this
        // process asked NVDA to read (capped at
        // `OutputAnnounceCapChars`; preview only). Used at
        // tuple-finalise time to trim the prefix of
        // `tuple.OutputText` that the idle-flush watermark
        // already announced — the test-02 `set/p` failure mode
        // where the user heard "Enter your text:" via idle-flush
        // and then heard "Enter your text: foo" again at
        // tuple-final after pressing Enter. Seq-based slicing
        // (the earlier approach) couldn't solve this because
        // `tuple.OutputText` is curated from screen rows, not
        // ContentHistory seqs. Resets to "" on shell hot-switch.
        let mutable lastAnnouncedText : string = ""

        // Cycle 51 PR-X — Seq-watermark narration (ADR 0004).
        // Replaces the entire screen-row / line-count sub-prompt
        // machinery (the screen-reader callback, the wrap-rows
        // helper, the preamble-capture callback, the preamble
        // line-count, and the submitted-command screen captures)
        // with two monotonic ContentHistory Seq watermarks. That
        // machinery was the root of the 2026-05-14 haywire
        // (ADR 0004 §4b) AND the history-scroll garbage dogfood
        // (slicing PromptStart→tail accumulates every Up/Down
        // redraw).
        //
        // `awaitingSubPromptEnter` flips true when SubPromptIdle
        // transitions Executing → Composing (a sub-prompt has
        // surfaced); flips false on EnterPressed (the user
        // submitted) OR PromptDetected (a new top-level prompt
        // arrived without the user ever responding — script
        // bailed, shell crashed, etc.). It distinguishes a
        // command Enter from a sub-prompt-response Enter.
        //
        // `commandEnterSeq` = ContentHistory.latestSeq captured
        // ONLY at the top-level command Enter (not a sub-prompt
        // response — the command's whole output region begins at
        // the original command Enter). Threaded into
        // SessionModel.extractIOCell so the persisted
        // command/output split is immune to history-scroll
        // accumulation. Resets to -1L on shell hot-switch (see
        // `ContentHistory.reset` call site in `switchToShell`).
        //
        // R3b (ADR 0005/0006) retired the `lastEnterSeq` announce
        // watermark and the PR-Y / PR-AB sub-prompt-question /
        // command-echo strips: the tuple-final announce now speaks
        // the R2-sealed IOCell's OutputText directly (already
        // cleanly ;B-anchored by extractIOCell), so no
        // announce-side watermark or strip state remains.
        // `awaitingSubPromptEnter` stays — it gates
        // `commandEnterSeq` so a sub-prompt-response Enter does
        // not move the top-level command watermark.
        let mutable awaitingSubPromptEnter : bool = false
        let mutable commandEnterSeq : int64 = -1L
        // Cycle 51 PR-AC / R3c — speak the shell's pre-prompt
        // banner on the first `PromptStart`. **Default `true`**
        // (R3c, dogfood #1): the prior comment claimed fresh
        // launch was "already covered" because NVDA reads the
        // document on first focus — the 2026-05-16 foundation
        // dogfood disproved that (fresh launch only said
        // "Terminal, edit, blank"; R3b's deletion of the old
        // PR-AA `lastEnterSeq<0` banner path removed the only
        // fresh-launch banner mechanism). With the watermark
        // model the banner is just un-spoken pre-prompt output
        // (Seq > the initial `lastAnnouncedSeq = -1`), so the
        // same first-PromptStart path covers BOTH fresh launch
        // (default true) and shell-switch (`switchToShell`
        // re-arms it + resets the watermark). Consumed on the
        // first PromptStart.
        let mutable announceBannerOnNextPrompt : bool = true

        // Cycle 45 Commit 2 — SpeechCursor announce callback +
        // "wake up" trigger. Defined here (very early in compose)
        // so the prompt-boundary handler and the selection-
        // detector emit path (both lower in the file) can close
        // over them. The callback runs on the WPF dispatcher
        // thread — every caller dispatches its
        // `SpeechCursor.onAppend` through `window.Dispatcher.InvokeAsync`,
        // so the peer lookup below is safe without another
        // marshalling hop.
        //
        // Cycle 46 PR-C / PR-D — caret-move helper. Looks up
        // `TerminalView`'s UIA peer and raises the
        // text-selection-changed event so NVDA's native "read
        // from caret" path picks up the new ContentHistory
        // tail. Shared between the PR-C boundary handler (tuple
        // finalise) and the PR-D `speechCursorAnnounce`
        // delegation (auto-drive + manual review-cursor nav).
        // Must be called on the WPF dispatcher thread.
        let raiseCaretMovedToTail () =
            let peer =
                UIElementAutomationPeer.FromElement(window.TerminalSurface)
            match peer with
            | null ->
                // No UIA client connected; silent no-op.
                ()
            | :? TerminalAutomationPeer as tp ->
                tp.RaiseCaretMovedToTail()
            | _ ->
                // Defensive non-throwing fallback; the only
                // peer type TerminalView constructs is
                // `TerminalAutomationPeer` so this branch is
                // unreachable in practice.
                ()

        // Cycle 49 PR-C — invalidate NVDA's UIA Text-pattern
        // cache so the review cursor picks up new content
        // without the user having to run an extra command to
        // nudge the cache. `TextPatternOnTextChanged` is the
        // canonical "the document's text has been replaced"
        // signal (whereas `TextPatternOnTextSelectionChanged`,
        // raised by `raiseCaretMovedToTail` above, is dropped by
        // NVDA when `GetSelection()` returns empty — see ADR
        // 0002 §"Status notes" 2026-05-13 post-PR-D entry).
        //
        // Called alongside each `Announce(...)` at every point
        // where ContentHistory has just grown: the SpeechCursor
        // announce callback (auto-drive narration), the
        // tuple-finalise Announce in `boundaryAction`, and the
        // sub-prompt Announce in `recordTransitionImpl`. The
        // peer-NULL case (no UIA client connected yet) silent
        // no-ops, mirroring `raiseCaretMovedToTail`'s pattern.
        let raiseTextChanged () =
            let peer =
                UIElementAutomationPeer.FromElement(window.TerminalSurface)
            match peer with
            | null -> ()
            | :? TerminalAutomationPeer as tp ->
                tp.RaiseTextChanged()
            | _ -> ()

        // Cycle 46 PR-D + post-PR-D audit (2026-05-13) — emit
        // BOTH the announce AND the caret-move event.
        //
        // The PR-D plan was "delegate to caret only; drop
        // Announce" (ADR §4 Option ★ Replace). Maintainer
        // testing on the preview build surfaced the failure
        // mode flagged in CYCLE-46-NEXT-STEPS.md §2's risk
        // register: NVDA doesn't react to bare
        // `TextPatternOnTextSelectionChanged` on a read-only
        // Edit when `ITextProvider.GetSelection()` returns an
        // empty array (which ours does — PR-B's
        // ContentHistoryTextProvider has no selection model).
        // The caret-move event fires, NVDA queries
        // `GetSelection`, gets nothing back, reads nothing.
        // Spoken output stopped entirely while menus +
        // diagnostic announces still worked, confirming the
        // failure was caret-side, not Announce-side.
        //
        // The fix pivots to ADR §4 Option ★★ Augment:
        //   * Restore the `Announce(text, activityId)` call so
        //     NVDA receives the content via the channel it
        //     already knows how to read.
        //   * Keep `raiseCaretMovedToTail ()` as a defensive
        //     signal — NVDA may consume the event for review-
        //     cursor positioning or future client integrations,
        //     and dropping it would invalidate the PR-C/PR-D
        //     diff against the rest of the audit.
        //   * Rely on PR-B's `ControlType=Edit` flip — kept
        //     in place — for NVDA's "Speech interrupt for
        //     typed character" setting, which fires on ANY
        //     key press inside an Edit regardless of how
        //     speech was initiated. That preserves the
        //     typing-interrupts-speech win that motivated
        //     Cycle 46 in the first place.
        //
        // Net effect: spoken output behaves as it did pre-
        // Cycle-46 (via Announce), AND typing interrupts
        // speech (via the Edit-control type), AND the
        // architectural cleanup of PR-D (legacy screen-grid
        // types deleted, new ContentHistory-backed substrate)
        // stays in place. See ADR 0002 Status notes for the
        // post-merge audit entry.
        // Cycle 47 follow-up (2026-05-13, post-preview.113) —
        // dropped the `raiseCaretMovedToTail ()` call. The
        // helper is preserved (defined above) for future
        // re-introduction with a selection-aware ITextProvider,
        // but auto-driven Announce paths no longer fire the
        // caret event because NVDA reads DocumentRange (which
        // carries semantic-boundary markers via
        // `tailTextWithMarkers`) when the event fires, and the
        // maintainer reported hearing "--- prompt ---" mid-
        // narration during the preview.113 dogfood. Announce is
        // sufficient for the auto-drive path; manual Speech
        // Cursor navigation surfaces markers explicitly when
        // the user wants to inspect boundary structure.
        let speechCursorAnnounce ((text: string), (activityId: string)) =
            window.TerminalSurface.Announce(text, activityId)
            // Cycle 49 PR-C — pair every auto-drive announce
            // with a UIA TextChanged raise so NVDA's review
            // cursor picks up the appended ContentHistory tail
            // without the user needing to run another command
            // to nudge the cache.
            raiseTextChanged ()

        // "Wake up" trigger: ContentHistory has appended.
        // SpeechCursor.onAppend reads from history directly
        // (idempotent), so no entries-list parameter.
        let onSpeechCursorWake () =
            SpeechCursor.onAppend
                speechCursor
                contentHistory
                speechCursorAnnounce

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

        // Cycle 46 PR-B — wire ContentHistory as the backing
        // store for TerminalView's UIA Text pattern. The view's
        // ITextProvider (constructed in TerminalView()'s
        // constructor as ContentHistoryTextProvider over a
        // closure on _contentHistory) materialises the
        // substrate's tail (last 256 KB via
        // ContentHistory.tailText) on each DocumentRange call.
        // Until this line runs, the closure resolves to null
        // and UIA queries return an empty range. See
        // docs/adr/0002-uia-textedit-caret-output.md PR-B
        // section.
        window.TerminalSurface.SetContentHistory(contentHistory)

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
        // Cycle 38a-followup — menu-only; Diagnostics → Open Manual Tests.
        bind HotkeyRegistry.OpenManualTests (fun () -> openManualTests window)

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
            NvdaChannel.create
                (fun (msg, activityId) ->
                    let action () =
                        window.TerminalSurface.Announce(msg, activityId)
                    window.Dispatcher.Invoke(Action(action)))
                (fun (payload, activityId) ->
                    // Cycle 37a — RenderRaw routing. View stub logs only;
                    // Cycle 37b promotes to peer-state update on the
                    // active TerminalAutomationPeer.
                    let action () =
                        window.TerminalSurface.AnnounceRawPayload(payload, activityId)
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
        // Cycle 38b — startup active set uses the canonical
        // [passthrough; earcon; selection] triple as a placeholder
        // until `chosenShell` is resolved later in composition. The
        // dispatcher doesn't see events until after the PTY
        // starts producing, so the placeholder window is
        // dispatch-event-free. `setActiveProfileSet` is
        // re-called once `chosenShell` is known (line ~1962)
        // and again on every shell-switch.
        //
        // Cycle 39 (2026-05-11) — Cycle 38c's EchoSuppressorProfile
        // was reverted: it solved a problem the maintainer never
        // reported. The actual reported issue (next-prompt bleeding
        // into the output announce) is addressed by Cycle 40's
        // three-panel channel routing.
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
                (tuple: SessionModel.IOCell) : unit =
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
        // Cycle 45c PR-3b — `selectPathwayForShell` deleted along
        // with the pathway layer. Per-shell behaviour now flows
        // through `ShellPolicy` (verbosity / prompt-path /
        // selection-detector toggles); the pathway-type concept
        // (`stream` / `tui`) is no longer represented because
        // ContentHistory is the universal substrate.
        //
        // The startup-time `activePathway` mutable + the
        // shell-switch reassignment block in `switchToShell`
        // (`activePathway.Reset () / activePathway <- ...`) are
        // also gone — there's no pathway state to reset.

        // Cycle 45c PR-3b — `currentShellId` retained. The
        // pathway-swap bookkeeping that used to be the primary
        // consumer (`selectPathwayForShell currentShellId` in
        // the SwapToShellDefault arm) is gone, but the mutable
        // still drives `shellIdToConfigKey` lookups for ShellPolicy
        // / profile resolution in `handlePromptBoundary` +
        // `switchToShell`.
        let mutable currentShellId : ShellRegistry.ShellId =
            ShellRegistry.Cmd

        // Cycle 45f — per-shell verbosity policy. Resolved at
        // shell-switch time through a three-layer model:
        //   1. ShellPolicy.defaults (compiled baseline)
        //   2. Config.resolveShellPolicy (TOML overlay)
        //   3. runtimeShellPolicy (menu-driven runtime override;
        //      lost on app restart)
        //
        // `currentShellPolicy` always carries the effective
        // policy for the active shell — handlePromptBoundary
        // reads its `Streaming` to gate the tuple-finalise
        // announce; switchToShell + the View → Output Verbosity
        // menu mutate it via `applyShellPolicy`.
        let mutable currentShellPolicy : ShellPolicy.T =
            ShellPolicy.forShell (shellIdToConfigKey ShellRegistry.Cmd)
        let mutable runtimeShellPolicy : Map<string, ShellPolicy.T> =
            Map.empty

        // Cycle 48 PR-E — install the real recordTransition body
        // now that `currentShellPolicy` + `lastAnnouncedText` are
        // in scope. Closes over both; later mutations to
        // `currentShellPolicy` (shell switch, verbosity menu)
        // are picked up on each call because they're mutable
        // cells.
        recordTransitionImpl <-
            fun (trigger : ShellInteraction.Transition) ->
                let now = DateTime.UtcNow
                match ShellInteraction.applyTransition shellInteraction trigger now with
                | Some outcome ->
                    shellInteractionLog.LogInformation(
                        "ShellInteraction transition: {Prior} --[{Trigger}]--> {New} AccumulatedOutputLen={Len}",
                        ShellInteraction.describeState outcome.PriorState,
                        ShellInteraction.describeTrigger outcome.Trigger,
                        ShellInteraction.describeState outcome.NewState,
                        outcome.AccumulatedOutput.Length)
                    let isSubPromptTransition =
                        match outcome.Trigger, outcome.PriorState, outcome.NewState with
                        | ShellInteraction.SubPromptIdle _,
                            ShellInteraction.Executing _,
                            ShellInteraction.Composing _ -> true
                        | _ -> false
                    let shouldAnnounce =
                        isSubPromptTransition
                        && (match currentShellPolicy.Streaming with
                            | ShellPolicy.TupleFinalOnly -> true
                            | ShellPolicy.LineByLine -> false
                            | ShellPolicy.Off -> false)
                    if shouldAnnounce then
                        // PR-K (2026-05-14, post-Cycle-49) —
                        // sub-prompt announce reads the SCREEN
                        // (rows [Active.PromptRowIndex + 1,
                        // cursorRow]) rather than the raw
                        // accumulator's last line.
                        //
                        // PR-F's accumulator-last-line approach
                        // failed on the first run of a `.cmd`
                        // script because cmd emits an OSC title-
                        // set sequence (`\x1B]0;...\x07`) AFTER
                        // the script's `Enter your name:`
                        // prompt; that escape sequence is on a
                        // line of its own in the byte stream and
                        // the reverse-scan picked it as "last
                        // non-empty line", so NVDA narrated
                        // "]0;C:\WINDOWS\SYSTEM32\cmd.exe — ..."
                        // (the maintainer's 2026-05-14 dogfood
                        // "show CMD"-style mishearing). On a
                        // second run the OSC is suppressed by
                        // cmd (title already set) and PR-F
                        // worked.
                        //
                        // The screen approach is robust: Screen
                        // already absorbs OSC sequences as state
                        // changes (window title, palette, etc.)
                        // and `renderRow` returns only printable
                        // cell content. The rendered rows from
                        // the prompt onward give the user the
                        // full sub-prompt preamble — start
                        // marker, intro text, prompt line — in
                        // the same order they appeared on
                        // screen.
                        //
                        // Falls back to the PR-F accumulator
                        // last-line approach when
                        // `currentSession.Active.PromptRowIndex`
                        // is unavailable (e.g. PowerShell which
                        // the heuristic prompt detector doesn't
                        // recognise today, so PromptRowIndex
                        // stays ValueNone there).
                        let raw = outcome.AccumulatedOutput
                        // Cycle 51 PR-AA — speak the FULL preamble
                        // + question, not just the question line.
                        // PR-X took only the accumulator's last
                        // non-empty line because the raw byte
                        // accumulator carries an OSC window-title
                        // sequence whose printable body
                        // (`]0;…\foo.cmd"`) survives the lone-ESC
                        // `sanitise` as spoken garbage. Strip whole
                        // ANSI/OSC *sequences* first, then split
                        // into visible lines and speak them all —
                        // the maintainer asked for the intro
                        // context ("This test asks…", "Enter a
                        // number:") that last-line-only dropped.
                        let lines =
                            (AnnounceSanitiser.stripSequences raw)
                                .Replace("\r", "\n")
                                .Split(
                                    [| '\n' |],
                                    System.StringSplitOptions.None)
                            |> Array.map (fun l ->
                                (AnnounceSanitiser.sanitise l).Trim())
                            |> Array.filter (fun l -> l.Length > 0)
                        // The question/prompt is the last non-empty
                        // line (PR-X's proven extraction). Kept
                        // separately for PR-Y's post-response
                        // strip, which matches the tuple-final
                        // delta — that slice begins at the
                        // question, never at the preamble.
                        let questionLine =
                            if lines.Length > 0 then
                                lines.[lines.Length - 1]
                            else
                                ""
                        // Single-line utterance (space-joined):
                        // RaiseNotificationEvent's displayString is
                        // single-line by contract and the embedded
                        // controls are already gone.
                        let trimmed = (String.concat " " lines).Trim()
                        if not (System.String.IsNullOrWhiteSpace trimmed) then
                            let toSay =
                                if trimmed.Length <= OutputAnnounceCapChars then trimmed
                                else trimmed.Substring(trimmed.Length - OutputAnnounceCapChars)
                            if toSay.Length < trimmed.Length then
                                log.LogInformation(
                                    "PR-AA sub-prompt announce truncated. OriginalLen={Orig} CappedLen={Capped} Cap={Cap}",
                                    trimmed.Length, toSay.Length, OutputAnnounceCapChars)
                            log.LogInformation(
                                "PR-AA sub-prompt announce. Length={Len} Lines={Lines} AccumulatorLen={Acc}",
                                toSay.Length, lines.Length, raw.Length)
                            log.LogDebug(
                                "PR-AA sub-prompt announce body. Question={Question} Announce={Announce}",
                                questionLine, toSay)
                            window.TerminalSurface.Announce(toSay, ActivityIds.output)
                            // Cycle 49 PR-C — invalidate UIA
                            // Text-pattern cache so the review
                            // cursor picks up the sub-prompt
                            // bytes immediately rather than
                            // waiting for the next command.
                            raiseTextChanged ()
                            lastAnnouncedText <- toSay
                            lastAnnouncedSeq <- ContentHistory.latestSeq contentHistory
                        let readyEvent =
                            OutputEvent.create
                                SemanticCategory.ReadyForInput
                                Priority.Background
                                "shell-interaction-sub-prompt"
                                ""
                        OutputDispatcher.dispatch readyEvent
                    // R3b (ADR 0005/0006) — awaitingSubPromptEnter
                    // bookkeeping. Gates `commandEnterSeq` so a
                    // sub-prompt-response Enter doesn't move the
                    // top-level command watermark; PromptDetected
                    // clears it if the user never responded
                    // (script bailed mid sub-prompt). The PR-X
                    // `lastEnterSeq` announce-watermark capture
                    // here was retired with the delta announce.
                    match outcome.Trigger with
                    | ShellInteraction.SubPromptIdle _ when isSubPromptTransition ->
                        awaitingSubPromptEnter <- true
                    | ShellInteraction.PromptDetected _ ->
                        awaitingSubPromptEnter <- false
                    | ShellInteraction.EnterPressed _ when awaitingSubPromptEnter ->
                        awaitingSubPromptEnter <- false
                    | _ -> ()
                | None ->
                    shellInteractionLog.LogDebug(
                        "ShellInteraction trigger no-op for current state: {State} --[{Trigger}]--",
                        ShellInteraction.describeState shellInteraction.Current,
                        ShellInteraction.describeTrigger trigger)

        // Resolve and apply the effective policy for `shellKey`
        // through the three-layer overlay (runtime → TOML →
        // compiled defaults). Updates `currentShellPolicy` AND
        // pushes the verbosity dials into
        // `speechCursor.Parameters` so subsequent `onAppend`
        // invocations observe the new policy.
        //
        // Call sites:
        //   - Startup-shell alignment (after `chosenShell` resolves)
        //   - `switchToShell`'s `Ok newHost` branch
        //   - `View → Output Verbosity` / `Prompt Path` menu picks
        //     (which write to `runtimeShellPolicy` first, then
        //     call this to push the change into SpeechCursor)
        let applyShellPolicy (shellKey: string) : unit =
            let resolved =
                match Map.tryFind shellKey runtimeShellPolicy with
                | Some p -> p
                | None -> Config.resolveShellPolicy config shellKey
            currentShellPolicy <- resolved
            let skipText =
                match resolved.Streaming with
                | ShellPolicy.TupleFinalOnly -> true
                | ShellPolicy.LineByLine -> false
                | ShellPolicy.Off -> true
            let current = SpeechCursor.getParameters speechCursor
            let parameters =
                { current with
                    SkipTextSpansInAutoDrive = skipText
                    PromptPath = resolved.PromptPath }
            SpeechCursor.setParameters speechCursor parameters

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

        // R3a (ADR 0005/0006) — precedence: once an OSC-133
        // boundary has been observed in the current shell
        // session, the heuristic detector is muted (its
        // synthetic boundaries are no longer dispatched). This
        // stops the regex detector from fighting the
        // authoritative shell-emitted markers — the root cause
        // of the announce-path compensation pile. Per-shell-
        // session: set by `handlePromptBoundary` on the first
        // `BoundarySource.Osc133`, reset by `switchToShell`
        // (NOT on alt-screen — the shell keeps emitting OSC
        // across alt-screen toggles). Mutated on the same
        // notification-consumer thread as `promptDetector`.
        let mutable oscSeenThisSession : bool = false

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
        // Cycle 45c PR-3b — `dispatchPathwayEvents` deleted along
        // with the pathway-emit call sites. Direct
        // `OutputDispatcher.dispatch` is still used elsewhere
        // (boundary barriers, earcons, ready-for-input).

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
        //
        // Cycle 45c PR-3b — `resolvedStreamParams` removed.
        // The Cycle 35b SubstrateMode dispatch collapsed to
        // `useContentHistory = true` (always-on ContentHistory
        // substrate). PR-3c deletes
        // `Config.resolveStreamParameters` and the
        // `StreamPathway` enum.
        let handlePromptBoundary
                (boundary: PromptBoundaryData)
                (snapshot: Cell[][])
                : unit
                =
            // R3a (ADR 0005/0006) — precedence latch. Any
            // OSC-133-sourced boundary proves the shell emits
            // shell-integration markers; from here on the
            // heuristic detector is muted for this shell
            // session (see `runDetector`). One-time
            // transition log so a bundle shows exactly when
            // the session went OSC-authoritative.
            match boundary.Source with
            | BoundarySource.Osc133 ->
                if not oscSeenThisSession then
                    oscSeenThisSession <- true
                    log.LogInformation(
                        "R3a precedence: first OSC-133 boundary observed; heuristic detector now muted for this shell session. Shell={Shell} Kind={Kind}",
                        shellIdToConfigKey currentShellId,
                        sprintf "%A" boundary.Kind)
            | _ -> ()
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
                // R4c (ADR 0005/0006) — `CommandFinished` joins
                // `PromptStart` here. cmd's deferred `;D` (R4c)
                // is what finalises a cell (the `Some active,
                // CommandFinished` SessionModel arm) instead of
                // the next prompt's `;A` interrupt. By the time
                // this handler drains, `screen.Apply` has already
                // processed the whole read chunk (the reader loop
                // appends + applies the full chunk before the
                // boundary notification drains — Program.fs
                // `startReaderLoop`), so the cursor row is the
                // NEXT prompt's path. Augmenting `;D` with that
                // row text gives `extractIOCell.stripNextPrompt`
                // (and the tuple-final announce's trailing trim)
                // the exact suffix to strip — keeping OutputText
                // clean, equivalent to the pre-R4c PromptStart-
                // interrupt finalise. Without this the `;D`
                // boundary's `MatchedRowText` stays `None`
                // (`Osc133.tryParse` produces records pre-
                // augmentation) and the trailing next-prompt
                // path would leak into OutputText.
                | None, (BoundaryKind.PromptStart | BoundaryKind.CommandFinished _) ->
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
            // Cycle 45c PR-3b — SubstrateMode dispatch collapsed.
            // Cycle 51 PR-W — ContentHistory is the SOLE
            // substrate; the boolean is always `true`. There is
            // no row-walk fallback: when no PromptStart Seq is
            // present `extractIOCell` returns None and the cell
            // is dropped (ADR 0004 drop-on-None).
            // SessionModel.IsAltScreenActive separately gates
            // boundary processing during alt-screen.
            let nextSession, finalisedOpt =
                SessionModel.applyAndCaptureWithContentHistory
                    currentSession augmented snapshotForApply
                    contentHistory true commandEnterSeq
            currentSession <- nextSession
            match finalisedOpt with
            | Some tuple ->
                dispatchTupleToWriter tuple
                // Cycle 51 PR-AD (ADR 0004) — feed the sealed
                // cell's authoritative command + output into the
                // SpeechCursor transcript so Manual navigation
                // (Ctrl+Shift+Up/Down/End) can review the command
                // line itself and post-single-key-response output
                // (both filtered from the raw ContentHistory-Seq
                // path as `UserInputEcho`; ADR 0004 §4a).
                SpeechCursor.appendCell
                    speechCursor
                    tuple.Id
                    tuple.CellSequence
                    tuple.CommandText
                    tuple.OutputText
                    tuple.ExitCode
            | None -> ()

            // Cycle 45 Commit 2 — ContentHistory + SpeechCursor
            // boundary handling. Emit a `MarkerKind` matching the
            // SessionModel state-transition kind so SpeechCursor
            // can navigate the prompt-boundary structure of the
            // shell session.
            //
            // Cycle 45 Commit 2 follow-up (2026-05-11): the
            // original wiring ALSO reset ContentHistory +
            // SpeechCursor on tuple seal, on the assumption that
            // a tuple boundary closed out the addressable history.
            // User dogfood surfaced that this leaves the cursor
            // with nothing to navigate — every Next/Previous
            // gesture after a completed command returns "already
            // at first/latest entry" because the history is
            // empty post-reset. The corrected scope is "the
            // shell session": entries accumulate across tuple
            // boundaries; reset only on shell-switch (handled in
            // `switchToShell` further below).
            let markerKind =
                match augmented.Kind with
                | BoundaryKind.PromptStart ->
                    Some ContentHistory.MarkerKind.PromptStart
                | BoundaryKind.CommandStart ->
                    Some ContentHistory.MarkerKind.CommandStart
                | BoundaryKind.OutputStart ->
                    Some ContentHistory.MarkerKind.OutputStart
                | BoundaryKind.CommandFinished _ ->
                    Some ContentHistory.MarkerKind.CommandFinished
            // Cycle 45 Commit 2 follow-up — do NOT reset
            // ContentHistory + SpeechCursor on tuple seal.
            // Original Commit 2 reset here on the assumption that
            // ContentHistory's scope is "the active tuple"; user
            // dogfood (2026-05-11) surfaced that this leaves the
            // SpeechCursor with nothing to navigate the moment a
            // command completes — every Next / Previous gesture
            // returns "Already at the first/latest entry" because
            // the history is empty post-reset. The right scope is
            // "the shell session" — entries accumulate across
            // tuple boundaries; the user can Speech-Cursor back
            // through completed commands. Reset on shell-switch
            // (in `switchToShell` below) still fires; that's a
            // legitimate fresh-slate boundary.
            // Cycle 45 fixup (2026-05-12) — tuple-finalise output
            // announce. cmd's command-line editing reprints suffix
            // bytes whose Print events accumulate into the active
            // TextSpan, so auto-announcing TextSpans on seal produces
            // inflated narrations that don't match what the user
            // actually ran. SessionModel's `IOCell` captures
            // CommandText + OutputText from the screen grid at
            // finalise time (authoritative) — announce OutputText
            // here on tuple seal as the user-facing "what did the
            // shell print" cue.
            //
            // Cycle 45f (2026-05-12) — gate the tuple-finalise
            // announce on `currentShellPolicy.Streaming`. Under
            // `TupleFinalOnly` (cmd / PowerShell default) the
            // announce fires as before. Under `LineByLine` the
            // per-TextSpan announces already covered the output;
            // suppressing here avoids double-narration. Under `Off`
            // both streaming AND tuple-finalise are silent —
            // SpeechCursor manual navigation is the only way to
            // hear output.
            // R3d (ADR 0005/0006) — the tuple-final announce
            // speaks the un-spoken portion of THIS cell's clean
            // output. Lower bound = `max commandEnterSeq
            // lastAnnouncedSeq`:
            //   * `commandEnterSeq` is `extractIOCell`'s CmdOscAB
            //     output-start (the top-level command's Enter
            //     watermark; NOT moved by a sub-prompt response).
            //     It sits AFTER the typed-command echo, so plain
            //     commands announce OUTPUT ONLY. R3c sliced from
            //     `lastAnnouncedSeq` alone — but the idle-flush
            //     (`runHeartbeat`) silently bumps that to the
            //     NEXT cell's CommandStart marker, BEFORE its
            //     echo, so R3c re-spoke the typed command
            //     ("echo X⏎X" instead of "X"; bundle-proven
            //     2026-05-16, build 09321e7).
            //   * `lastAnnouncedSeq` is the settled post-announce
            //     watermark; for a sub-prompt it has advanced
            //     PAST the already-spoken question (which is
            //     printed after `commandEnterSeq`), so it wins
            //     and only the post-response output is announced
            //     (dogfood #2 stays fixed — maintainer-validated).
            // `max` picks whichever correctly excludes
            // already-heard bytes. NOT KI-R2-1's racy byte-stream
            // `lastEnterSeq`. Trailing next-prompt trimmed by the
            // same bounded `MatchedRowText` strip `extractIOCell`
            // uses. `IOCell.OutputText` (persisted) unchanged —
            // announce vs persistence intentionally diverge.
            // Gated on a finalised cell + `TupleFinalOnly`. The
            // pre-prompt banner is the separate `bannerAnnounce`
            // path below. (Synthetic diagnostic-battery writes
            // don't set `commandEnterSeq`; that harness-only
            // staleness is a known non-UX limitation.)
            let tupleFinaliseAnnounce =
                match finalisedOpt, currentShellPolicy.Streaming with
                | Some _, ShellPolicy.TupleFinalOnly ->
                    let fromSeq = max commandEnterSeq lastAnnouncedSeq
                    let raw =
                        ContentHistory.sliceText
                            contentHistory
                            fromSeq
                            System.Int64.MaxValue
                    let withoutNextPrompt =
                        let r = raw.TrimEnd('\r', '\n')
                        match augmented.MatchedRowText with
                        | Some p when not (System.String.IsNullOrEmpty p)
                                      && r.EndsWith(p) ->
                            r.Substring(0, r.Length - p.Length)
                        | _ -> raw
                    let body =
                        (withoutNextPrompt.TrimStart('\r', '\n'))
                            .TrimEnd('\r', '\n')
                    if System.String.IsNullOrWhiteSpace body then None
                    else Some body
                | _ -> None
            // Cycle 45f — pass `MatchedRowText` as the PromptStart
            // marker's payload so SpeechCursor.renderEntryWithPolicy
            // can apply the per-shell `PromptPathMode` (Suppress /
            // FinalDirOnly / Full). Other marker kinds carry no
            // payload (renderEntry returns None for them anyway).
            let markerPayload =
                match markerKind with
                | Some ContentHistory.MarkerKind.PromptStart ->
                    augmented.MatchedRowText
                | _ -> None
            // Cycle 51 PR-AC / R3c — the shell's pre-prompt
            // banner, spoken once on the first PromptStart (fresh
            // launch via the `announceBannerOnNextPrompt` default;
            // post-switch via `switchToShell` re-arming it). R3c:
            // slice from `lastAnnouncedSeq` (the SAME un-spoken
            // Seq-gap rule as the tuple-final), not a hard `0L`.
            // On fresh launch / post-switch the watermark is -1
            // (initial / reset by switchToShell) so this still
            // captures the full pre-prompt banner; using the
            // watermark keeps banner + tuple-final on one
            // consistent "speak what hasn't been spoken" model.
            // Trailing prompt path + escape sequences trimmed as
            // before.
            let bannerAnnounce =
                if announceBannerOnNextPrompt
                   && markerKind
                      = Some ContentHistory.MarkerKind.PromptStart then
                    let raw =
                        ContentHistory.sliceText
                            contentHistory
                            lastAnnouncedSeq
                            System.Int64.MaxValue
                    let withoutPrompt =
                        match augmented.MatchedRowText with
                        | Some p when not (System.String.IsNullOrEmpty p)
                                      && raw.EndsWith(p) ->
                            raw.Substring(0, raw.Length - p.Length)
                        | _ -> raw
                    let lines =
                        (AnnounceSanitiser.stripSequences withoutPrompt)
                            .Replace("\r", "\n")
                            .Split(
                                [| '\n' |],
                                System.StringSplitOptions.None)
                        |> Array.map (fun l ->
                            (AnnounceSanitiser.sanitise l).Trim())
                        |> Array.filter (fun l -> l.Length > 0)
                    let joined = (String.concat " " lines).Trim()
                    if System.String.IsNullOrWhiteSpace joined then None
                    else Some joined
                else
                    None
            let boundaryAction () =
                // Cycle 48 PR-B (ADR 0003) — observe PromptStart
                // for the ShellInteraction state machine
                // (transition [b]: Executing → Composing). The
                // boundary handler fires for PromptStart /
                // CommandStart / OutputStart / CommandFinished;
                // only PromptStart drives transition [b]. Other
                // boundaries log but don't transition. Observe-
                // only in PR-B; announce routing unchanged.
                match markerKind with
                | Some ContentHistory.MarkerKind.PromptStart ->
                    let promptText =
                        defaultArg augmented.MatchedRowText ""
                    recordTransition
                        (ShellInteraction.PromptDetected promptText)
                | _ -> ()
                match markerKind with
                | Some k ->
                    // Cycle 47 follow-up (2026-05-13) — synthesize
                    // CommandFinished ("--- end output ---") marker
                    // BEFORE the PromptStart when SessionModel
                    // finalised a prior tuple as incomplete (the
                    // heuristic "PromptStart while
                    // Active=AwaitingCommandStart" transition).
                    // **R4c note (2026-05-16):** post-R4c cmd
                    // emits a real deferred `;D`, so cmd finalises
                    // via the `Some active, CommandFinished` arm
                    // and its `;A` opens with `finalisedOpt = None`
                    // — this synthetic guard no longer trips for
                    // cmd (the natural `;D` CommandFinished marker
                    // replaces it, correctly placed). It now fires
                    // only for genuinely-heuristic shells (claude),
                    // which still emit no OSC 133. Result in the
                    // review-cursor document:
                    //
                    //   ...prior command's output bytes...
                    //   --- end output ---
                    //   next prompt's path bytes
                    //   --- begin prompt ---
                    //   ...new command...
                    //
                    // The CommandFinished marker lands AFTER the
                    // prior tuple's output TextSpan and Newline
                    // entries that already sealed (via cmd's CRLF),
                    // so navigation by line gives the user a
                    // detectable "end of evaluation" boundary
                    // before the new prompt's path TextSpan. Note
                    // that this is heuristic — `appendMarker` seals
                    // the active span (which may contain the new
                    // prompt's path bytes already accumulated
                    // between cmd writing them and the heuristic
                    // detector firing). The marker therefore appears
                    // AFTER the new prompt's path in the rendered
                    // tail, not before it. The cleaner model — a
                    // real shell-emitted CommandFinished — is what
                    // R4c delivers for cmd; this heuristic path is
                    // the residual fallback for shells that emit no
                    // OSC 133 (claude).
                    // **P2 (2026-05-16) — explicit heuristic-only
                    // guard.** Post-R4c/R5b this was already
                    // correct *by construction* (cmd/PowerShell
                    // finalise on the real `;D` call, so the `;A`
                    // PromptStart call has `finalisedOpt = None`
                    // → this never tripped for them). P2 adds
                    // `not oscSeenThisSession` to make the
                    // heuristic-only intent explicit and to harden
                    // the defensive edge: if an OSC shell ever
                    // reaches `;A` with an Active cell (a missed/
                    // garbled `;D`), an OSC-authoritative session
                    // must NOT fall back to the cmd-heuristic-era
                    // synthetic marker — that is exactly the R3a
                    // precedence principle ("once OSC seen, mute
                    // heuristic"). Golden path: identical. claude
                    // (`oscSeenThisSession` always false — no
                    // OSC 133): unchanged, still fires.
                    if (not oscSeenThisSession)
                        && finalisedOpt.IsSome
                        && k = ContentHistory.MarkerKind.PromptStart then
                        ContentHistory.appendMarker
                            contentHistory
                            ContentHistory.MarkerKind.CommandFinished
                            DateTime.UtcNow
                            None
                        |> ignore
                    // R4c (ADR 0005/0006) — cmd's deferred `;D`
                    // is emitted at the head of EVERY prompt,
                    // including the very first (no prior command
                    // → SessionModel's `None, CommandFinished`
                    // arm logs + ignores it) and any `;D` whose
                    // cell dropped (drop-on-None, ADR 0004). Those
                    // did NOT finalise a cell; appending a
                    // `CommandFinished` marker for them injects a
                    // stray "end output" boundary into
                    // ContentHistory — a silent-but-navigable
                    // SpeechCursor stop at session start. Gate the
                    // natural CommandFinished marker on a real
                    // finalise (`finalisedOpt.IsSome`).
                    // PromptStart / CommandStart / OutputStart
                    // always append — they bracket regions
                    // regardless of seal. The synthetic
                    // heuristic-shell compensation above is a
                    // separate path (direct append, not via `k`)
                    // and is unaffected.
                    let strayCommandFinished =
                        k = ContentHistory.MarkerKind.CommandFinished
                        && finalisedOpt.IsNone
                    if strayCommandFinished then
                        log.LogInformation(
                            "R4c cmd CommandFinished suppressed (no cell finalised; leading or drop-on-None ;D). Source={Source} Shell={Shell}",
                            sprintf "%A" augmented.Source,
                            shellIdToConfigKey currentShellId)
                    else
                        ContentHistory.appendMarker
                            contentHistory k DateTime.UtcNow markerPayload
                        |> ignore
                        SpeechCursor.onAppend
                            speechCursor
                            contentHistory
                            speechCursorAnnounce
                | None -> ()
                // Cycle 46 PR-C — caret-move replaces the
                // RaiseNotificationEvent path for terminal output.
                // ContentHistory already holds the new content
                // (appended by the reader loop ahead of this
                // boundary handler); raising
                // `AutomationEvents.TextSelectionChanged` on the
                // peer signals NVDA to re-read DocumentRange and
                // pick up the tail. NVDA's native "read from
                // caret" path handles the pacing + the
                // typing-interrupts-speech behaviour the previous
                // `RaiseNotificationEvent + MostRecent` path
                // couldn't deliver (the failure trail recorded in
                // ADR 0002 Context). The `tupleFinaliseAnnounce`
                // gate (`ShellPolicy.Streaming` matching
                // `TupleFinalOnly` with non-blank output) stays
                // — under `LineByLine` per-TextSpan announces
                // already covered the output and we shouldn't
                // double-fire; under `Off` we want silence.
                // Cycle 46 PR-C + post-PR-D audit — Option ★★
                // Augment. The Announce restores spoken output
                // (NVDA reads the tuple-final text); the
                // caret-move event signals the Edit-control
                // change; the ControlType=Edit (PR-B) enables
                // NVDA's typed-character interrupt. See the
                // matching comment on `speechCursorAnnounce`
                // above for the full rationale.
                //
                // Cycle 46 post-audit (2026-05-13) — cap the
                // tuple-final announce at the last
                // OutputAnnounceCapChars characters. The
                // pre-cap behaviour shipped one giant Announce
                // (observed: MsgLen=18953 for a default-shell
                // `dir`) which becomes a 5–10 minute single
                // SAPI utterance NVDA cannot interrupt — neither
                // `MostRecent` on a different `activityId` nor
                // the Edit-control typed-character setting
                // displaces an in-flight SAPI render. Capping at
                // ~800 chars keeps the worst-case utterance to
                // ~30–60 seconds; the user hears the tail of the
                // command's output (typically the most relevant
                // bytes — final summary line / next prompt /
                // exit status). Full output is preserved in
                // ContentHistory and reachable via
                // `Ctrl+Shift+O` (open last output in the default
                // text editor) or `Ctrl+Shift+Y` (copy session
                // history to clipboard).
                //
                // No prefix or NVDA instruction is added to the
                // audible text — keep the cap silent at the
                // channel layer and trust the hotkey + log to
                // surface the cap when needed.
                //
                // Cycle 51 PR-X — post-Enter delta announce. The
                // Seq-watermark slice + next-prompt trim was done
                // above when `tupleFinaliseAnnounce` was computed
                // (immune to history-scroll accumulation; never
                // re-reads the command echo / preamble / sub-prompt
                // question the user already heard). This block only
                // caps + fires it. No `raiseCaretMovedToTail ()`
                // call — NVDA reads the DocumentRange when the
                // caret event fires, and the maintainer reported
                // hearing "--- prompt ---" mid-narration when it
                // did; the Announce below is the sole audible path,
                // the review cursor stays reachable via Speech
                // Cursor navigation.
                // Cycle 51 PR-X — the delta was already sliced
                // from the Seq watermark + next-prompt-trimmed
                // when `tupleFinaliseAnnounce` was computed. No
                // line-count arithmetic, no prefix-trim. Just cap
                // the audible length (full output stays in
                // ContentHistory + the persisted IOCell, reachable
                // via Ctrl+Shift+O / Ctrl+Shift+Y).
                match tupleFinaliseAnnounce with
                | Some text ->
                    let toSay =
                        if text.Length <= OutputAnnounceCapChars then text
                        else text.Substring(text.Length - OutputAnnounceCapChars)
                    if toSay.Length < text.Length then
                        log.LogInformation(
                            "R3d tuple-final announce truncated. OriginalLen={Orig} CappedLen={Capped} Cap={Cap}",
                            text.Length, toSay.Length, OutputAnnounceCapChars)
                    log.LogInformation(
                        "R3d tuple-final announce (clean output, watermark-intersected). CommandEnterSeq={CommandEnterSeq} LastAnnouncedSeq={LastAnnouncedSeq} FromSeq={FromSeq} Len={Len}",
                        commandEnterSeq, lastAnnouncedSeq, (max commandEnterSeq lastAnnouncedSeq), toSay.Length)
                    log.LogDebug(
                        "R3d tuple-final announce body. Announce={Announce}",
                        toSay)
                    window.TerminalSurface.Announce(toSay, ActivityIds.output)
                    // Cycle 49 PR-C — invalidate UIA Text-pattern
                    // cache so the review cursor picks up the
                    // freshly-finalised tuple content immediately.
                    raiseTextChanged ()
                    lastAnnouncedText <- toSay
                    lastAnnouncedSeq <- ContentHistory.latestSeq contentHistory
                | None -> ()
                // Cycle 51 PR-AC — first post-switch PromptStart:
                // speak the new shell's banner, and consume the
                // flag even if the slice was empty (so a
                // banner-less shell doesn't retry on the next
                // prompt and narrate accumulated output as a
                // "banner").
                if announceBannerOnNextPrompt
                   && markerKind
                      = Some ContentHistory.MarkerKind.PromptStart then
                    announceBannerOnNextPrompt <- false
                    match bannerAnnounce with
                    | Some banner ->
                        let toSay =
                            if banner.Length <= OutputAnnounceCapChars then
                                banner
                            else
                                banner.Substring(0, OutputAnnounceCapChars)
                        log.LogInformation(
                            "R3c banner announce (un-spoken pre-prompt gap). FromSeq={FromSeq} Length={Len}",
                            lastAnnouncedSeq, toSay.Length)
                        log.LogDebug(
                            "R3c banner announce body. Announce={Announce}",
                            toSay)
                        window.TerminalSurface.Announce(
                            toSay, ActivityIds.output)
                        raiseTextChanged ()
                        lastAnnouncedText <- toSay
                        lastAnnouncedSeq <-
                            ContentHistory.latestSeq contentHistory
                    | None -> ()
            window.Dispatcher.InvokeAsync(Action(boundaryAction))
            |> ignore

            if finalisedOpt.IsSome then
                let readyEvent =
                    OutputEvent.create
                        SemanticCategory.ReadyForInput
                        Priority.Background
                        "session-model"
                        ""
                OutputDispatcher.dispatch readyEvent

            // Cycle 45c PR-3b — activePathway.OnPromptBoundary call
            // deleted. The pathway emitted OutputEvents whose
            // Payloads were empty post-Cycle-45 (ContentHistory +
            // SpeechCursor took over the announce side); the
            // dispatch was logged but produced no NVDA effect.
            pumpLog.LogDebug(
                "PathwayPump PromptBoundary {Kind} → ContentHistory append (no pathway emit).",
                augmented.Kind)

        // SessionModel Tier 1.D-cleanup (Cycle 19) — shared
        // detector invocation. Was duplicated across
        // `handleRowsChanged` (frame-driven path) and
        // `handleTick` (50ms tick-driven path; introduced in
        // Cycle 17 to close the idle-gap hole). Both call
        // sites had identical 4-arg shape:
        //   shellKey + detectionTime + tryDetect + state
        //   update + dispatch on Some.
        // P1 (ADR 0006 R5/R6-prep — heuristic-detector
        // detection-time gate). The heuristic prompt detector's
        // boundary is ALWAYS muted + discarded once OSC-133 is
        // authoritative for the shell session
        // (`oscSeenThisSession`, latched by R3a). Running its
        // per-chunk regex/stability scan post-OSC is pure waste
        // + log spam (the 2026-05-16 bundle showed hundreds of
        // muted/SUPPRESSED lines). The prompt-detector logic is
        // extracted here verbatim so `runDetector` can SKIP it
        // entirely once OSC is authoritative — observationally
        // identical (a muted boundary and no boundary are the
        // same; nothing reads `promptDetector` except the
        // Ctrl+Shift+D snapshot, which then shows its as-of-OSC
        // state). Claude never sets `oscSeenThisSession` (no
        // OSC-133) so its detector stays fully live. Single
        // notification-consumer thread ⇒ `oscSeenThisSession`
        // cannot flip mid-call, so the gate is exactly
        // equivalent to the prior emit-time mute. `shellKey` is
        // passed in (also used by the SelectionDetector below).
        let runHeuristicPromptDetector
                (snapshot: Cell[][])
                (cursorPos: int * int)
                (shellKey: string)
                (now: DateTime)
                : unit
                =
            let boundary, nextDetector =
                HeuristicPromptDetector.tryDetect
                    snapshot cursorPos shellKey now promptDetector
            promptDetector <- nextDetector
            // Tier 1.E2.B: forward the snapshot so
            // handlePromptBoundary can pass it through to
            // SessionModel.apply for finalize-time content
            // extraction.
            match boundary with
            | Some data when oscSeenThisSession ->
                // Defensive only — `runDetector` gates this
                // helper behind `not oscSeenThisSession` and the
                // single-thread invariant means the flag cannot
                // flip mid-call, so this arm is now unreachable
                // in practice; kept verbatim (prior behaviour,
                // minimal diff).
                log.LogDebug(
                    "R3a: muted heuristic boundary (OSC-133 authoritative this session). Shell={Shell} Kind={Kind} Source={Source}",
                    shellKey,
                    sprintf "%A" data.Kind,
                    sprintf "%A" data.Source)
            | Some data -> handlePromptBoundary data snapshot
            | None -> ()

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
            // P1 — skip the heuristic scan entirely once OSC-133
            // is authoritative (see `runHeuristicPromptDetector`).
            // Behaviour-identical to the prior emit-time mute;
            // eliminates the per-chunk waste + log spam.
            if not oscSeenThisSession then
                runHeuristicPromptDetector snapshot cursorPos shellKey now
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

            // Cycle 45 Commit 2 — also mirror selection events
            // into ContentHistory as `MarkerKind.SelectionShown`
            // / `SelectionDismissed` markers. SelectionItem
            // events do NOT emit markers (they're item-by-item
            // additions to the already-active selection; the
            // SpeechCursor's list-navigation mode handles
            // per-item announcement separately when the user
            // arrows through). Payload for `SelectionShown`
            // carries the item-list text so `renderEntry` can
            // announce "Selection prompt: Edit, Yes, Always,
            // No." All ContentHistory + SpeechCursor mutations
            // dispatch through the WPF dispatcher for serialised
            // ordering with reader-thread appends and hotkey-
            // driven cursor moves.
            let selectionMarkerWork =
                selectionEvents
                |> Array.choose (fun ev ->
                    match ev.Semantic with
                    | SemanticCategory.SelectionShown ->
                        // Extensions is `Map<string, obj>`; the
                        // selection.allItems value SHOULD be a
                        // string per `SelectionExtensions`'
                        // schema, but extract defensively.
                        let payload =
                            match Map.tryFind SelectionExtensions.AllItems ev.Extensions with
                            | Some (:? string as s) when not (System.String.IsNullOrEmpty s) ->
                                Some s
                            | _ -> None
                        Some (ContentHistory.MarkerKind.SelectionShown, payload)
                    | SemanticCategory.SelectionDismissed ->
                        Some (ContentHistory.MarkerKind.SelectionDismissed, None)
                    | _ -> None)
            if selectionMarkerWork.Length > 0 then
                let selectionMarkerAction () =
                    for kind, payload in selectionMarkerWork do
                        ContentHistory.appendMarker
                            contentHistory kind DateTime.UtcNow payload
                        |> ignore
                    SpeechCursor.onAppend
                        speechCursor
                        contentHistory
                        speechCursorAnnounce
                window.Dispatcher.InvokeAsync(Action(selectionMarkerAction))
                |> ignore

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
            // Cycle 35a — pass alt-screen state for SubstrateMode=Auto
            // dispatch in StreamPathway.processCanonicalState.
            // Cycle 45c PR-3b — activePathway.Consume call deleted.
            // CanonicalState construction is retained because
            // future framework cycles (Phase 2 ReplPathway /
            // ClaudeCodePathway) will reintroduce a Consume-shaped
            // pathway primitive against a different substrate.
            let canonical =
                CanonicalState.create snapshot cursorPos seq screen.Modes.AltScreen
            ignore canonical
            pumpLog.LogDebug(
                "PathwayPump RowsChanged → ContentHistory append (no pathway emit).")
        // Cycle 45c PR-3b — `swapPathwayForAltScreen` deleted.
        // Alt-screen entry/exit no longer swaps pathway
        // implementations; the SessionModel state-machine call
        // (`enterAltScreen` / `exitAltScreen`) was the only
        // user-facing semantic of the swap and that's now invoked
        // directly in `handleModeChanged`.
        let handleModeChanged (notification: ScreenNotification) : unit =
            ignore notification
            // Cycle 45c PR-3b — alt-screen transitions toggle
            // SessionModel + reset the prompt + selection
            // detectors. The pathway swap logic that used to wrap
            // these calls (PathwaySelector.decideAltScreenAction
            // + swapPathwayForAltScreen) is gone — ContentHistory
            // is the sole substrate post-PR-3b. SessionModel's
            // alt-screen flag continues to govern boundary
            // suppression during TUI apps.
            if screen.Modes.AltScreen then
                currentSession <-
                    SessionModel.enterAltScreen currentSession
            else
                currentSession <-
                    SessionModel.exitAltScreen currentSession
            promptDetector <-
                HeuristicPromptDetector.reset promptDetector
            selectionDetector <-
                SelectionDetector.reset selectionDetector
            // Barrier OutputEvent dispatch retained so the
            // FileLogger captures the mode transition in the
            // audit trail (NvdaChannel routes via ActivityIds.mode;
            // empty Payload skips the actual announce per
            // NvdaChannel's contract).
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
            // Cycle 45c PR-3b — activePathway.Tick call deleted.
            // ContentHistory has its own `tick` (idle-window seal)
            // which is invoked by SpeechCursor's announce path.
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
            // R1.3 (ADR 0006): the shell-resolution decision now
            // lives in the single orchestration point. This is a
            // behaviour-identical delegation — same precedence
            // (TOML → env → cmd), same log templates (emitted via
            // the same `log` instance, so category + structured
            // args are byte-identical), same fallback semantics.
            // See src/Terminal.Shell/SessionHost.fs.
            Terminal.Shell.SessionHost.ResolveStartupShell(config, log)

        let chosenShell, commandLine = resolveStartupShell ()
        log.LogInformation(
            "Startup shell: {Shell} (command line: {CommandLine}).",
            chosenShell.DisplayName,
            commandLine)

        // Cycle 45c PR-3b — pathway alignment removed (no pathway
        // layer post-PR-3b). `currentShellId` still gets synced
        // so ShellPolicy / profile resolution sees the right
        // shell on the first boundary.
        currentShellId <- chosenShell.Id

        // Cycle 45f — apply the startup shell's verbosity policy
        // before any reader output flows. Layer 1 + Layer 2
        // (compiled defaults + TOML overlay; Layer 3 is empty at
        // startup). This pushes `Streaming` / `PromptPath` into
        // `speechCursor.Parameters` so the first PromptStart
        // marker + first tuple seal respect the policy.
        applyShellPolicy (shellIdToConfigKey chosenShell.Id)

        // Cycle 38b — resolve the active profile set for the
        // chosen shell. Defined here (after `chosenShell` is
        // known) so the resolver can read the shell key. Used
        // both at startup (this call) and on every shell-switch
        // (later in `switchToShell`). All shells default to the
        // canonical `[passthrough; earcon; selection]` triple;
        // TOML `[shell.<key>] profiles = [...]` overrides per
        // shell. (Cycle 39 reverted Cycle 38c's per-shell
        // EchoSuppressor default since it didn't address the
        // maintainer's reported issue. The infrastructure stays
        // in place so future shell-specific profiles can plug
        // in via TOML without re-introducing 38b.)
        let shellIdToProfileKey (id: ShellRegistry.ShellId) : string =
            match id with
            | ShellRegistry.Cmd -> "cmd"
            | ShellRegistry.PowerShell -> "powershell"
            | ShellRegistry.Claude -> "claude"
        let resolveProfilesForShell (shellId: ShellRegistry.ShellId) : Profile list =
            let shellKey = shellIdToProfileKey shellId
            let defaultSet =
                [ "passthrough"; "earcon"; "selection" ]
            let configured =
                Config.resolveShellProfiles config shellKey
                |> Option.map Array.toList
                |> Option.defaultValue defaultSet
            configured
            |> List.choose (fun pid ->
                match OutputDispatcher.ProfileRegistry.lookup pid with
                | Some p -> Some p
                | None ->
                    log.LogWarning(
                        "Config: profile id '{Id}' for shell '{Shell}' is not registered; dropped from active set.",
                        pid, shellKey)
                    None)
        OutputDispatcher.ProfileRegistry.setActiveProfileSet
            (resolveProfilesForShell chosenShell.Id)

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
                // R4-followup (2026-05-16, maintainer request) —
                // surface the AssemblyInformationalVersion in the
                // Ctrl+Shift+H announce so a local `git pull` +
                // build can be sanity-checked against the intended
                // version without a GUI walk (the window title
                // doesn't carry the release tag on a dev build the
                // way an installed preview does). Reuses the
                // startup-log resolver; also captured in the
                // bundle via the LogInformation below.
                //
                // R4-followup-2 (2026-05-16, dogfood) — the
                // `+<sha>` trailer is a 7-char hex commit id;
                // tokens like `09321e7` match the
                // `<digits>e<digits>` shape so NVDA's number
                // reader speaks them as scientific notation
                // ("9321 times ten to the 7"). Spell the SHA out
                // character-by-character (space-separated) in the
                // SPOKEN summary only — NVDA then reads each
                // character. The startup log + the
                // `LogInformation` below keep the raw `+sha`
                // (unchanged `resolveInformationalVersion`) for
                // grep/triage; this only reshapes what is voiced.
                let spokenVersion =
                    let v = resolveInformationalVersion ()
                    let plus = v.IndexOf('+')
                    if plus >= 0 && plus < v.Length - 1 then
                        let basePart = v.Substring(0, plus)
                        let sha = v.Substring(plus + 1)
                        let spelled =
                            System.String.Join(
                                " ",
                                sha.ToCharArray() |> Array.map string)
                        sprintf "%s build %s" basePart spelled
                    else
                        v
                let summary =
                    sprintf
                        "%s Version %s. %s shell, PID %d (%s), log level %s. Reader last byte %.0f ms ago. Notification queue %d of 256."
                        verdict
                        spokenVersion
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
                    // Cycle 45c PR-3b — activePathway.Id replaced
                    // with a constant since the pathway layer is
                    // gone. PR-3c renames the
                    // captureSessionModel parameter (it's now
                    // historically named).
                    Diagnostics.captureSessionModel
                        currentSession
                        promptDetector
                        "content-history")
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
                // Cycle 45c — ContentHistory resolver for the
                // bundle's `--- CONTENT HISTORY (last 64KB) ---`
                // section. Replaces Cycle 34b's LinearTextStream
                // resolver. The class reference is stable across
                // the session (`reset` is called on shell-switch
                // but the same cell is reused), so the closure
                // captures the same `contentHistory` cell on
                // every press.
                (fun () -> contentHistory)

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

        // ADR 0007 Phase 2a (2026-05-16) — `Ctrl+Shift+C`:
        // copy the focused SpeechCursor cell (command + output)
        // to the clipboard. The per-cell analogue of
        // `Ctrl+Shift+Y` (all history) / `Ctrl+Shift+O` (last
        // output): acts on whichever cell the Manual cursor
        // (Ctrl+Shift+Up/Down/End) is parked on. Reuses the
        // same bounded STA-thread Clipboard.SetText pattern as
        // `runCopyHistoryToClipboard` (STA required by
        // Clipboard.SetText; 3s timeout prevents a contention
        // hang). Counts only — never the command/output text —
        // are logged (logging discipline).
        let runCopyFocusedCell () : unit =
            let log =
                Logger.get
                    "Terminal.App.Program.runCopyFocusedCell"
            log.LogInformation(
                "Ctrl+Shift+C pressed — copying the focused cell to clipboard.")
            match SpeechCursor.focusedCell speechCursor with
            | None ->
                window.TerminalSurface.Announce(
                    "No focused cell. Press Ctrl+Shift+Up or Down to focus a cell first.",
                    ActivityIds.diagnostic)
            | Some cell ->
                let parts =
                    [ cell.Command |> Option.map (fun c -> "Command: " + c)
                      cell.Output |> Option.map (fun o -> "Output: " + o) ]
                    |> List.choose id
                let content = String.Join("\n\n", parts)
                let cmdLen =
                    cell.Command
                    |> Option.map String.length
                    |> Option.defaultValue 0
                let outLen =
                    cell.Output
                    |> Option.map String.length
                    |> Option.defaultValue 0
                let _ =
                    task {
                        try
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
                                        "ADR 0007 Phase 2a focused-cell copy. CellSeq={CellSeq} CmdLen={CmdLen} OutLen={OutLen} Bytes={Bytes}",
                                        cell.CellSequence,
                                        cmdLen,
                                        outLen,
                                        bytes)
                                    sprintf
                                        "Cell %d copied to clipboard, %d bytes."
                                        cell.CellSequence
                                        bytes,
                                    ActivityIds.diagnostic
                                else
                                    log.LogWarning(
                                        "Clipboard.SetText timed out or failed after 3s (focused-cell copy).")
                                    "Clipboard copy timed out. Try again.",
                                    ActivityIds.error
                            let action () =
                                window.TerminalSurface.Announce(msg, activityId)
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                        with ex ->
                            let safe = AnnounceSanitiser.sanitise ex.Message
                            log.LogError(
                                ex, "Failed to copy focused cell.")
                            let action () =
                                window.TerminalSurface.Announce(
                                    sprintf "Could not copy cell: %s" safe,
                                    ActivityIds.error)
                            try
                                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                            with _ -> ()
                    }
                ()

        // ADR 0007 Phase 2b (2026-05-17) — copy ONE side of
        // the focused cell. Menu-only (Output History → Copy
        // Focused Cell Command / Output); the keyboarded
        // whole-cell case is Ctrl+Shift+C (Phase 2a). Same
        // bounded STA-thread Clipboard.SetText pattern as
        // `runCopyFocusedCell`; counts only are logged (never
        // the command/output text — logging discipline). The
        // STA block is duplicated rather than abstracted,
        // matching the established local style
        // (`runCopyHistoryToClipboard` / `runCopyFocusedCell`)
        // and to avoid touching the merged, dogfood-passed
        // Phase 2a handler.
        let runCopyFocusedCellSide
                (sideName: string)
                (pick: SpeechCursor.FocusedCell -> string option)
                : unit =
            let log =
                Logger.get
                    "Terminal.App.Program.runCopyFocusedCellSide"
            log.LogInformation(
                "Phase 2b — copying the focused cell's {Side} to clipboard.",
                sideName)
            match SpeechCursor.focusedCell speechCursor with
            | None ->
                window.TerminalSurface.Announce(
                    "No focused cell. Press Ctrl+Shift+Up or Down to focus a cell first.",
                    ActivityIds.diagnostic)
            | Some cell ->
                match pick cell with
                | None ->
                    window.TerminalSurface.Announce(
                        sprintf "The focused cell has no %s." sideName,
                        ActivityIds.diagnostic)
                | Some content ->
                    let contentLen = content.Length
                    let _ =
                        task {
                            try
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
                                            "ADR 0007 Phase 2b focused-cell side copy. Side={Side} CellSeq={CellSeq} Len={Len} Bytes={Bytes}",
                                            sideName,
                                            cell.CellSequence,
                                            contentLen,
                                            bytes)
                                        sprintf
                                            "Cell %d %s copied to clipboard, %d bytes."
                                            cell.CellSequence
                                            sideName
                                            bytes,
                                        ActivityIds.diagnostic
                                    else
                                        log.LogWarning(
                                            "Clipboard.SetText timed out or failed after 3s (focused-cell {Side} copy).",
                                            sideName)
                                        "Clipboard copy timed out. Try again.",
                                        ActivityIds.error
                                let action () =
                                    window.TerminalSurface.Announce(msg, activityId)
                                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                            with ex ->
                                let safe = AnnounceSanitiser.sanitise ex.Message
                                log.LogError(
                                    ex,
                                    "Failed to copy focused cell {Side}.",
                                    sideName)
                                let action () =
                                    window.TerminalSurface.Announce(
                                        sprintf "Could not copy %s: %s" sideName safe,
                                        ActivityIds.error)
                                try
                                    do! window.Dispatcher.InvokeAsync(Action(action)).Task
                                with _ -> ()
                        }
                    ()

        let runCopyFocusedCellCommand () : unit =
            runCopyFocusedCellSide "command" (fun c -> c.Command)

        let runCopyFocusedCellOutput () : unit =
            runCopyFocusedCellSide "output" (fun c -> c.Output)

        // Cycle 24e — `Ctrl+Shift+S`: announce the active
        // Cycle 46 post-audit (2026-05-13) — open the latest
        // tuple's full `OutputText` in the default text editor.
        // Companion to the 800-char tuple-final Announce cap:
        // the cap keeps the audible read interruptible; this
        // hotkey is the escape hatch for hearing / reading the
        // full output when the cap matters.
        //
        // File path:
        //   %LOCALAPPDATA%\PtySpeak\extracts\last-output-<ts>.txt
        // A fresh timestamped file per invocation (matches the
        // Diagnostics → Grep diagnostics extract pattern). The
        // accumulation in `extracts\` is bounded by the user's
        // explicit invocation count; no auto-pruning.
        //
        // Defaults to the most recently-finalised tuple
        // (`History |> Seq.tryLast`). Mid-command Active tuples
        // aren't surfaced — the user can wait for the prompt to
        // return. If the history is empty, the hotkey announces
        // "No prior output" rather than opening an empty file.
        //
        // The 700ms announce-then-launch delay mirrors
        // `runOpenConfig`'s pattern: NVDA finishes speaking the
        // confirmation before the editor takes focus and starts
        // its own UIA traffic.
        let runOpenLastOutput () : unit =
            let log =
                Logger.get "Terminal.App.Program.runOpenLastOutput"
            log.LogInformation(
                "Ctrl+Shift+O pressed — opening last output in default editor.")
            let snapshot = currentSession
            match snapshot.History |> Seq.tryLast with
            | None ->
                log.LogInformation(
                    "No prior output to open (History is empty).")
                window.TerminalSurface.Announce(
                    "No prior output.",
                    ActivityIds.diagnostic)
            | Some tuple ->
                let timestamp =
                    DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fff")
                let extractsDir =
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "PtySpeak",
                        "extracts")
                let filePath =
                    System.IO.Path.Combine(
                        extractsDir,
                        sprintf "last-output-%s.txt" timestamp)
                try
                    System.IO.Directory.CreateDirectory(extractsDir)
                    |> ignore
                    System.IO.File.WriteAllText(
                        filePath,
                        tuple.OutputText,
                        System.Text.Encoding.UTF8)
                    log.LogInformation(
                        "Wrote last output to file. Path={Path} OutputLen={Len}",
                        filePath, tuple.OutputText.Length)
                    window.TerminalSurface.Announce(
                        "Opening last output.",
                        ActivityIds.diagnostic)
                    let _ =
                        task {
                            do! Task.Delay(700)
                            let action () =
                                try
                                    let psi =
                                        System.Diagnostics.ProcessStartInfo()
                                    psi.FileName <- filePath
                                    psi.UseShellExecute <- true
                                    System.Diagnostics.Process.Start(psi)
                                    |> ignore
                                with ex ->
                                    let safe =
                                        AnnounceSanitiser.sanitise ex.Message
                                    log.LogError(
                                        ex,
                                        "Could not launch text editor: {Path}",
                                        filePath)
                                    window.TerminalSurface.Announce(
                                        sprintf
                                            "Could not open output file: %s"
                                            safe,
                                        ActivityIds.error)
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                            ()
                        }
                    ()
                with ex ->
                    let safe = AnnounceSanitiser.sanitise ex.Message
                    log.LogError(
                        ex,
                        "Could not write last-output file at {Path}.",
                        filePath)
                    window.TerminalSurface.Announce(
                        sprintf "Could not save output: %s" safe,
                        ActivityIds.error)

        // Cycle 46 post-audit (2026-05-13) — re-narrate the
        // latest finalised tuple's `OutputText`, capped at the
        // same `OutputAnnounceCapChars` the auto-narrate uses.
        // For the user who missed the auto-Announce (was
        // speaking, typing, switched window, etc.). Goes
        // through the same `TerminalView.Announce` channel +
        // `ActivityIds.output` activity ID, so it supersedes
        // any other in-flight `pty-speak.output` notification
        // via NVDA's `MostRecent` processing — the cap keeps
        // the utterance bounded to ~30–60s either way.
        //
        // `Ctrl+Shift+O` is the companion when the user wants
        // the FULL output (writes to a file + opens the default
        // text editor); this hotkey is the spoken counterpart.
        let runAnnounceLastOutput () : unit =
            let log =
                Logger.get "Terminal.App.Program.runAnnounceLastOutput"
            log.LogInformation(
                "Ctrl+Shift+A pressed — re-narrating last output.")
            let snapshot = currentSession
            match snapshot.History |> Seq.tryLast with
            | None ->
                log.LogInformation(
                    "No prior output to re-narrate (History is empty).")
                window.TerminalSurface.Announce(
                    "No prior output.",
                    ActivityIds.diagnostic)
            | Some tuple when System.String.IsNullOrWhiteSpace tuple.OutputText ->
                log.LogInformation(
                    "Latest tuple has empty OutputText.")
                window.TerminalSurface.Announce(
                    "Last command produced no output.",
                    ActivityIds.diagnostic)
            | Some tuple ->
                let toSay =
                    if tuple.OutputText.Length <= OutputAnnounceCapChars then
                        tuple.OutputText
                    else
                        tuple.OutputText.Substring(
                            tuple.OutputText.Length - OutputAnnounceCapChars)
                if toSay.Length < tuple.OutputText.Length then
                    log.LogInformation(
                        "Re-narrate truncated. OriginalLen={Orig} CappedLen={Capped} Cap={Cap}",
                        tuple.OutputText.Length,
                        toSay.Length,
                        OutputAnnounceCapChars)
                window.TerminalSurface.Announce(
                    toSay,
                    ActivityIds.output)

        // Cycle 47 — CMD interaction test runner. The user
        // selects one of eight `Diagnostics → CMD Interaction
        // Tests → ...` menu items; this writes the script
        // invocation to the PTY input cursor so the user can
        // review + press Enter to run. Doesn't auto-Enter so
        // the maintainer keeps control (avoid surprising the
        // user with an automatic execution; also lets them
        // edit the path if they want to redirect output to a
        // file or pipe to another command first).
        //
        // The PTY write goes through `TerminalView.WritePtyBytes`
        // which is the same path keystrokes use, so cmd sees
        // it as typed input. cmd's default ECHO behaviour
        // echoes the path back so it shows up in
        // ContentHistory; NVDA will read the announce we fire
        // ("Test command inserted: <id>") rather than the path
        // bytes themselves.
        //
        // Path resolution: scripts live under
        // `<install-dir>/scripts/cmd-tests/<id>.cmd` per the
        // `<Content Include>` glob in `Terminal.App.fsproj`.
        // `AppContext.BaseDirectory` is the install dir at
        // runtime.
        // ADR 0007 Phase 3 (2026-05-17, maintainer UX
        // direction) — the shared "insert this text at the
        // prompt, do NOT auto-run" primitive. Prepends `Esc`
        // (0x1B) with the *intent* of clearing the current
        // input line first (so the line is fully replaced, the
        // maintainer's "delete whatever is in the current input
        // to completely replace that line" requirement). NO
        // trailing `\r` — the user reviews the inserted text
        // and presses Enter themselves; that explicit human
        // submit is the safety affordance (no auto-run on a
        // gesture). Used by BOTH the diagnostic test-script
        // insertion and rerun-focused-input so the two are
        // provably the same path.
        //
        // KNOWN ISSUE (tracked, deferred — maintainer dogfood
        // 2026-05-17; pre-existing since Cycle 47 when the
        // Esc-prefix was introduced for the diagnostic-test
        // insertion). The `Esc` prefix does NOT actually clear
        // the line in practice: the text inserts correctly at
        // the cursor but prior partially-typed input is left
        // intact. Likely cause (locally unverifiable — no
        // ConPTY/dotnet here): a lone `0x1B` immediately
        // followed by bytes is not delivered to the shell's
        // line editor as an "Escape key / clear-line" action —
        // ConPTY's escape-sequence disambiguation drops it (or
        // metafies it as `Alt+<char>`), so cmd's cooked-mode
        // editor never sees the Escape keypress. The behaviour
        // is also shell-line-editor-specific (the assumption is
        // documented as cmd cooked-mode; PowerShell/PSReadLine
        // would differ regardless). A correct fix needs a
        // ConPTY-input dogfood-iteration loop + confirming the
        // shell-at-test-time; deferred so it does not block the
        // ADR 0007 phase sequence. Insertion (the core
        // capability) works; the missing clear is a polish gap.
        // See ADR 0007 §"Known issue surfaced" + the
        // `52-ADR7-P3` matrix row.
        let insertAtPromptClearingLine (textToInsert: string) : unit =
            let bytes =
                Array.append
                    [| 0x1Buy |]
                    (System.Text.Encoding.UTF8.GetBytes(textToInsert))
            window.TerminalSurface.WritePtyBytes(bytes)

        let runCmdTest (testId: string) : unit =
            let log =
                Logger.get "Terminal.App.Program.runCmdTest"
            log.LogInformation(
                "CmdTest invoked. TestId={TestId}", testId)
            let scriptPath =
                System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "scripts",
                    "cmd-tests",
                    sprintf "%s.cmd" testId)
            if not (System.IO.File.Exists scriptPath) then
                log.LogError(
                    "CmdTest script missing. TestId={TestId} Path={Path}",
                    testId, scriptPath)
                window.TerminalSurface.Announce(
                    sprintf "Could not find test script %s." testId,
                    ActivityIds.error)
            else
                // Quote the path (handles spaces in install
                // dir like "Program Files (x86)") and append a
                // trailing space so the user can type more
                // arguments / redirects before Enter if they
                // want. The line-clear (Esc) + no-auto-run is
                // now the shared `insertAtPromptClearingLine`
                // primitive (Cycle 47 follow-up behaviour,
                // unchanged — just factored so rerun-input uses
                // the identical path).
                insertAtPromptClearingLine (sprintf "\"%s\" " scriptPath)
                window.TerminalSurface.Announce(
                    sprintf
                        "Test command inserted: %s. Press Enter to run."
                        testId,
                    ActivityIds.diagnostic)

        let runCmdTestEcho () = runCmdTest "test-01-echo"
        let runCmdTestTextInput () = runCmdTest "test-02-text-input"
        let runCmdTestNumericInput () = runCmdTest "test-03-numeric-input"
        let runCmdTestYesNo () = runCmdTest "test-04-yes-no"
        let runCmdTestMultiChoice () = runCmdTest "test-05-multi-choice"
        let runCmdTestPause () = runCmdTest "test-06-pause"
        let runCmdTestProgress () = runCmdTest "test-07-progress"
        let runCmdTestStderr () = runCmdTest "test-08-stderr"
        let runCmdTestMultiInterrupt () = runCmdTest "test-09-multi-interrupt"

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

        // ---------------------------------------------------------------
        // Cycle 43a — Diagnostics → Extract submenu + Grep dialog
        // ---------------------------------------------------------------
        //
        // Six new menu-only commands (no keyboard accelerators):
        //   * `CopyLatestBundle`     — Diagnostics → Copy latest diagnostic bundle
        //   * `GrepDiagnostics`      — Diagnostics → Grep diagnostics...
        //   * `ExtractLast50LogLines` — Diagnostics → Extract → By Recency
        //   * `ExtractErrorsAndWarnings` — Diagnostics → Extract → By Event Type
        //   * `ExtractActiveConfig`     — Diagnostics → Extract → By Bundle Section
        //   * `ExtractVersionHeader`    — Diagnostics → Extract → Snapshot
        //
        // All six follow the same shape: compute a body string,
        // wrap it with a header, cap clipboard payload at 60 KB
        // (Cycle 29b iOS-paste-crash ceiling), write the full
        // untruncated text to `%LOCALAPPDATA%\PtySpeak\extracts\`
        // as a paste-fallback, copy the (possibly-truncated)
        // clipboard view via the STA-thread + 3s-timeout pattern
        // shared by `runCopyHistoryToClipboard`, and announce the
        // size + file path via NVDA. The shared shape lives in
        // `runExtractorClipboard` below; the per-command logic
        // is a single closure passed as `computeBody`.

        // Shared building block for the lightweight diagnostic
        // bundle that `CopyLatestBundle` copies and that
        // `GrepDiagnostics` searches over. Resolvers run at
        // press-time so a hot-switch between presses is picked up
        // correctly. Skips the diagnostic-battery + canonical-
        // corpus sections that `Ctrl+Shift+D` includes (those
        // require running test commands against the live shell,
        // adding ~10 seconds of wall time); the lightweight
        // assembly is current-state-only and completes in ~100 ms.
        let buildLightweightBundle () : string =
            let now = DateTime.UtcNow
            let configPath = Config.defaultConfigFilePath ()
            let sessionLogSummary = buildSessionLogSummary ()
            let fileLoggerPath =
                loggerSink |> Option.map (fun s -> s.ActiveLogPath)
            let tailText =
                try
                    ContentHistory.tailText contentHistory (64 * 1024)
                    |> AnnounceSanitiser.sanitiseForBundle
                with _ -> "(ContentHistory tail unavailable)"
            // Cycle 45c follow-up — prepend the ContentHistory
            // stats header so the lightweight bundle carries the
            // same "did the substrate see anything?" answer as the
            // full bundle.
            let statsHeader =
                try Diagnostics.formatContentHistoryStats contentHistory
                with _ -> "(ContentHistory stats unavailable)"
            let contentHistorySection =
                sprintf "%s\n%s" statsHeader tailText
            Diagnostics.formatLightweightBundle
                now
                fileLoggerPath
                configPath
                sessionLogSummary
                contentHistorySection

        // Cycle 43a — the canonical "extractor → clipboard + file
        // + announce" pipeline. Captures `window` from compose-
        // local scope; otherwise pure (delegates body production
        // to the caller-supplied closure). Mirrors the STA + 3s
        // pattern of `runCopyHistoryToClipboard`; the difference
        // is the file-write step ahead of the clipboard hand-off
        // so paste-back consumers who can't paste from clipboard
        // (e.g. iOS chat clients on huge content) have the
        // untruncated text on disk.
        let runExtractorClipboard
                (extractorName: string)
                (sourceDescription: string)
                (computeBody: unit -> string)
                : unit =
            let log =
                Logger.get
                    (sprintf
                        "Terminal.App.Program.runExtractor.%s"
                        extractorName)
            log.LogInformation(
                "Extractor pressed: {Name}", extractorName)
            let _ =
                task {
                    try
                        let now = DateTimeOffset.UtcNow
                        let body = computeBody ()
                        let fullContent =
                            DiagnosticExtracts.formatExtractHeader
                                now
                                extractorName
                                sourceDescription
                                body
                        let (clipboardContent, truncated) =
                            DiagnosticExtracts.truncateForClipboard
                                DiagnosticExtracts.clipboardSafetyCeilingBytes
                                fullContent
                        let extractPath =
                            DiagnosticExtracts.extractFilePath
                                now
                                extractorName
                        let mutable extractWritten = false
                        try
                            DiagnosticExtracts.writeExtractFile
                                extractPath
                                fullContent
                            extractWritten <- true
                        with ex ->
                            log.LogWarning(
                                ex,
                                "Extract file write failed at {Path}",
                                extractPath)
                        let setOk = TaskCompletionSource<bool>()
                        let staBody = ThreadStart(fun () ->
                            try
                                System.Windows.Clipboard.SetText(clipboardContent)
                                setOk.TrySetResult(true) |> ignore
                            with ex ->
                                log.LogWarning(
                                    ex,
                                    "Extractor clipboard SetText threw: {Message}",
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
                        let copied =
                            obj.ReferenceEquals(winner, setOk.Task)
                            && setOk.Task.Result
                        let bytes =
                            System.Text.Encoding.UTF8.GetByteCount(clipboardContent)
                        let sizeStr =
                            DiagnosticExtracts.formatBytesForAnnounce bytes
                        let truncSuffix =
                            if truncated then " (clipboard truncated)"
                            else ""
                        let fileSuffix =
                            if extractWritten then
                                sprintf
                                    " Extract file at %s."
                                    extractPath
                            else
                                " Extract file write failed."
                        let msg, activityId =
                            if copied then
                                log.LogInformation(
                                    "Extractor {Name} copied. Bytes={Bytes} Truncated={Truncated} ExtractPath={Path}",
                                    extractorName,
                                    bytes,
                                    truncated,
                                    extractPath)
                                sprintf
                                    "%s copied to clipboard: %s%s.%s"
                                    extractorName
                                    sizeStr
                                    truncSuffix
                                    fileSuffix,
                                ActivityIds.diagnostic
                            else
                                log.LogWarning(
                                    "Extractor {Name} clipboard timed out.",
                                    extractorName)
                                sprintf
                                    "%s clipboard copy timed out.%s"
                                    extractorName
                                    fileSuffix,
                                ActivityIds.error
                        let action () =
                            window.TerminalSurface.Announce(msg, activityId)
                        do! window.Dispatcher.InvokeAsync(Action(action)).Task
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        log.LogError(
                            ex,
                            "Extractor {Name} failed.",
                            extractorName)
                        let action () =
                            window.TerminalSurface.Announce(
                                sprintf "%s failed: %s" extractorName safe,
                                ActivityIds.error)
                        try
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                        with _ -> ()
                }
            ()

        // --- Top-level: Copy latest diagnostic bundle (no battery) ---
        let runCopyLatestBundle () : unit =
            runExtractorClipboard
                "CopyLatestBundle"
                "lightweight diagnostic bundle (FileLogger active log + config + session log summary + linear stream tail + redacted environment)"
                buildLightweightBundle

        // --- Top-level: Grep diagnostics... (modal dialog) ---
        //
        // Runs on the dispatcher (hotkey handler is dispatcher-bound
        // by `bindHotkey`'s contract). Dialog is modal-with-owner
        // so NVDA's focus model reads the dialog as a child of the
        // main window and Alt+F4 / Escape close it cleanly. After
        // the dialog returns, the grep + clipboard + announce
        // pipeline runs through `runExtractorClipboard` so the
        // size-cap / file-write / NVDA-announce behaviour is
        // identical to the other extractors.
        let runGrepDiagnostics () : unit =
            let log =
                Logger.get "Terminal.App.Program.runGrepDiagnostics"
            try
                let dialog = GrepDialog()
                dialog.Owner <- window
                let result = dialog.ShowDialog()
                if result.HasValue && result.Value then
                    let opts : DiagnosticGrep.GrepOptions =
                        { Pattern = dialog.Pattern
                          CaseSensitive = dialog.CaseSensitive
                          TreatAsRegex = dialog.TreatAsRegex
                          ContextLines = dialog.ContextLines }
                    let extractorName =
                        sprintf "grep-%s" dialog.Pattern
                    let source =
                        sprintf
                            "lightweight diagnostic bundle; grep pattern=%s regex=%b case=%b context=%d"
                            dialog.Pattern
                            dialog.TreatAsRegex
                            dialog.CaseSensitive
                            dialog.ContextLines
                    let computeBody () =
                        let bundle = buildLightweightBundle ()
                        DiagnosticGrep.formatGrep opts bundle
                    runExtractorClipboard
                        extractorName
                        source
                        computeBody
                else
                    log.LogInformation(
                        "Grep dialog cancelled.")
            with ex ->
                let safe = AnnounceSanitiser.sanitise ex.Message
                log.LogError(ex, "Grep dialog failed to open.")
                window.TerminalSurface.Announce(
                    sprintf "Grep dialog failed: %s" safe,
                    ActivityIds.error)

        // --- Extract → By Recency → Last 50 log lines ---
        let runExtractLast50LogLines () : unit =
            runExtractorClipboard
                "ExtractLast50LogLines"
                "FileLogger active log; last 50 lines"
                (fun () ->
                    match loggerSink with
                    | None -> "(FileLogger not configured)"
                    | Some sink ->
                        DiagnosticExtracts.tailLogLines
                            sink.ActiveLogPath
                            50)

        // --- Extract → By Event Type → Errors and warnings ---
        let runExtractErrorsAndWarnings () : unit =
            runExtractorClipboard
                "ExtractErrorsAndWarnings"
                "FileLogger active log; entries with Semantic=ErrorLine, WarningLine, or ParserError"
                (fun () ->
                    match loggerSink with
                    | None -> "(FileLogger not configured)"
                    | Some sink ->
                        DiagnosticExtracts.filterLogBySemantic
                            sink.ActiveLogPath
                            [ "ErrorLine"; "WarningLine"; "ParserError" ])

        // --- Extract → By Bundle Section → Active config.toml ---
        let runExtractActiveConfig () : unit =
            runExtractorClipboard
                "ExtractActiveConfig"
                (sprintf
                    "config.toml at %s"
                    (Config.defaultConfigFilePath ()))
                (fun () ->
                    let path = Config.defaultConfigFilePath ()
                    Diagnostics.readFileSafe path)

        // --- Extract → Test Run → <testId> ---
        //
        // Cycle 47 follow-up (2026-05-13) — eight extractors, one
        // per CMD test in `scripts/cmd-tests/`. Body source is
        // `ContentHistory.tailText` (256 KB cap — same window
        // PR-B's UIA substrate uses); `DiagnosticExtracts.extractByTest`
        // does the bracket-marker scan. Output is the bracketed
        // body of every run in the tail, with run dividers when
        // there are multiple. Falls back to a one-line "no runs
        // found" placeholder when the bracket markers haven't
        // been emitted yet (e.g. the user hasn't run the
        // companion `CmdTest*` invocation in this session).
        let runExtractTestRun
                (extractorName: string)
                (testId: string)
                : unit =
            runExtractorClipboard
                extractorName
                (sprintf
                    "ContentHistory tail; bracketed slice for %s"
                    testId)
                (fun () ->
                    let tail =
                        try
                            ContentHistory.tailText
                                contentHistory
                                (256 * 1024)
                            |> AnnounceSanitiser.sanitiseForBundle
                        with _ ->
                            "(ContentHistory tail unavailable)"
                    DiagnosticExtracts.extractByTest testId tail)

        let runExtractTestEcho () : unit =
            runExtractTestRun "ExtractTestEcho" "test-01-echo"
        let runExtractTestTextInput () : unit =
            runExtractTestRun "ExtractTestTextInput" "test-02-text-input"
        let runExtractTestNumericInput () : unit =
            runExtractTestRun "ExtractTestNumericInput" "test-03-numeric-input"
        let runExtractTestYesNo () : unit =
            runExtractTestRun "ExtractTestYesNo" "test-04-yes-no"
        let runExtractTestMultiChoice () : unit =
            runExtractTestRun "ExtractTestMultiChoice" "test-05-multi-choice"
        let runExtractTestPause () : unit =
            runExtractTestRun "ExtractTestPause" "test-06-pause"
        let runExtractTestProgress () : unit =
            runExtractTestRun "ExtractTestProgress" "test-07-progress"
        let runExtractTestStderr () : unit =
            runExtractTestRun "ExtractTestStderr" "test-08-stderr"

        // --- Extract → Snapshot → Version + environment header ---
        let runExtractVersionHeader () : unit =
            runExtractorClipboard
                "ExtractVersionHeader"
                "process version + OS + .NET + PID (small status snapshot)"
                (fun () ->
                    let version = resolveInformationalVersion ()
                    let pid =
                        System.Diagnostics.Process.GetCurrentProcess().Id
                    let sb = System.Text.StringBuilder()
                    sb.AppendLine(sprintf "Version: %s" version) |> ignore
                    sb.AppendLine(
                        sprintf
                            "OS: %s"
                            (Environment.OSVersion.VersionString)) |> ignore
                    sb.AppendLine(
                        sprintf
                            ".NET: %s"
                            (Environment.Version.ToString())) |> ignore
                    sb.AppendLine(sprintf "Process ID: %d" pid) |> ignore
                    sb.AppendLine(
                        sprintf
                            "Active shell: %s"
                            currentShell.DisplayName) |> ignore
                    sb.ToString())

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
        bind HotkeyRegistry.OpenLastOutput runOpenLastOutput
        bind HotkeyRegistry.AnnounceLastOutput runAnnounceLastOutput
        bind HotkeyRegistry.CopyFocusedCell runCopyFocusedCell
        bind HotkeyRegistry.CopyFocusedCellCommand runCopyFocusedCellCommand
        bind HotkeyRegistry.CopyFocusedCellOutput runCopyFocusedCellOutput
        bind HotkeyRegistry.CmdTestEcho runCmdTestEcho
        bind HotkeyRegistry.CmdTestTextInput runCmdTestTextInput
        bind HotkeyRegistry.CmdTestNumericInput runCmdTestNumericInput
        bind HotkeyRegistry.CmdTestYesNo runCmdTestYesNo
        bind HotkeyRegistry.CmdTestMultiChoice runCmdTestMultiChoice
        bind HotkeyRegistry.CmdTestPause runCmdTestPause
        bind HotkeyRegistry.CmdTestProgress runCmdTestProgress
        bind HotkeyRegistry.CmdTestStderr runCmdTestStderr
        bind HotkeyRegistry.CmdTestMultiInterrupt runCmdTestMultiInterrupt
        // Cycle 43a — diagnostic chunk extractors + grep dialog.
        // All menu-only (no keyboard accelerator); `bindHotkey`
        // skips the KeyBinding installation but still registers
        // the CommandBinding so MenuItem.Command dispatch works.
        bind HotkeyRegistry.CopyLatestBundle runCopyLatestBundle
        bind HotkeyRegistry.GrepDiagnostics runGrepDiagnostics
        bind HotkeyRegistry.ExtractLast50LogLines runExtractLast50LogLines
        bind HotkeyRegistry.ExtractErrorsAndWarnings runExtractErrorsAndWarnings
        bind HotkeyRegistry.ExtractActiveConfig runExtractActiveConfig
        bind HotkeyRegistry.ExtractVersionHeader runExtractVersionHeader
        // Cycle 47 follow-up — test-bracketed extractors.
        bind HotkeyRegistry.ExtractTestEcho runExtractTestEcho
        bind HotkeyRegistry.ExtractTestTextInput runExtractTestTextInput
        bind HotkeyRegistry.ExtractTestNumericInput runExtractTestNumericInput
        bind HotkeyRegistry.ExtractTestYesNo runExtractTestYesNo
        bind HotkeyRegistry.ExtractTestMultiChoice runExtractTestMultiChoice
        bind HotkeyRegistry.ExtractTestPause runExtractTestPause
        bind HotkeyRegistry.ExtractTestProgress runExtractTestProgress
        bind HotkeyRegistry.ExtractTestStderr runExtractTestStderr

        // Cycle 45 Commit 2 — SpeechCursor navigation commands.
        // Menu-only (no keyboard accelerators in this cycle —
        // adding them risks NVDA collisions; revisit once
        // SpeechCursor's UX has settled in the maintainer's
        // dogfood). Each handler runs on the WPF dispatcher
        // (where the binding fires), the same thread the
        // reader-loop dispatcher actions use; serialisation
        // is preserved.
        //
        // Cycle 51 PR-AD (ADR 0004) — Manual navigation walks the
        // sealed-IOCell transcript (command + output per cell),
        // not raw ContentHistory entries. This is what lets the
        // user review the command line itself and post-single-key
        // -response output, both of which the legacy Seq path
        // filtered as `UserInputEcho`. The manual-step announce
        // keeps the `diagnostic` activity id so users can
        // per-tag-mute explicit navigation separately from live
        // output.
        // ADR 0007 D9 / Phase 6a-1 — emit the typed `Focused`
        // cell event on the canonical cell pipeline
        // (`CellEventBus`) after a successful user navigation.
        // PURELY ADDITIVE: the dogfood-validated direct announce
        // in each handler below is untouched and byte-identical;
        // no sink renders this event yet (6a-2's history list is
        // the first subscriber), so 6a-1 is CI-only with no
        // audible change. `cellCurrentView` resolves the typed
        // cell the cursor just landed on — every nav accessor
        // sets `CellPos` to it on a `Some`, so this is `Some`
        // exactly when navigation moved (no event on the
        // edge/empty `None`, where focus did not move).
        let publishCellFocused () : unit =
            match SpeechCursor.cellCurrentView speechCursor with
            | Some cv -> CellEventBus.publish (CellEventBus.Focused cv)
            | None -> ()

        let runSpeechCursorNext () : unit =
            match SpeechCursor.cellNext speechCursor with
            | Some (text, _) ->
                window.TerminalSurface.Announce(
                    text, ActivityIds.diagnostic)
                publishCellFocused ()
            | None ->
                window.TerminalSurface.Announce(
                    "Already at the latest entry.",
                    ActivityIds.diagnostic)

        let runSpeechCursorPrevious () : unit =
            match SpeechCursor.cellPrevious speechCursor with
            | Some (text, _) ->
                window.TerminalSurface.Announce(
                    text, ActivityIds.diagnostic)
                publishCellFocused ()
            | None ->
                window.TerminalSurface.Announce(
                    "Already at the first entry.",
                    ActivityIds.diagnostic)

        let runSpeechCursorJumpToLatest () : unit =
            match SpeechCursor.cellToLatest speechCursor with
            | Some (text, _) ->
                window.TerminalSurface.Announce(
                    text, ActivityIds.diagnostic)
                publishCellFocused ()
            | None ->
                window.TerminalSurface.Announce(
                    "History is empty.", ActivityIds.diagnostic)

        // ADR 0007 Phase 2c — menu-only: jump the Manual cursor
        // to the most recent failed (non-zero exit) cell.
        // Counts-only Information log (no command/output text;
        // logging discipline) so a bundle confirms the path
        // fired + which cell it landed on.
        let runJumpToLastError () : unit =
            let log =
                Logger.get "Terminal.App.Program.runJumpToLastError"
            match SpeechCursor.jumpToLastError speechCursor with
            | Some (text, _) ->
                let landedSeq =
                    match SpeechCursor.cellCurrentView speechCursor with
                    | Some v -> v.CellSequence
                    | None -> -1L
                log.LogInformation(
                    "ADR 0007 Phase 2c jump-to-last-error. Found=true CellSeq={CellSeq}",
                    landedSeq)
                window.TerminalSurface.Announce(
                    text, ActivityIds.diagnostic)
                publishCellFocused ()
            | None ->
                // ADR 0007 Phase 2c follow-up (2026-05-17,
                // maintainer dogfood). Exit-code failure
                // detection only sees an exit code when one is
                // actually transported: PowerShell emits
                // `;D;$LASTEXITCODE` (external-process non-zero
                // exits only — cmdlet errors do NOT set
                // `$LASTEXITCODE`); cmd emits a bare `;D` with
                // NO exit code at all (documented transport
                // limitation, `CmdAdapter.fs:52-65`, maintainer
                // decision 2026-05-16 — clink/doskey out of
                // scope). Under cmd the generic "no failed
                // command" line actively misled the maintainer
                // (3 genuinely-failing commands → "none"); a
                // shell-type-gated honest capability message is
                // NOT an announce-reconstruction heuristic (it
                // parses nothing — it states a static transport
                // fact), so it is outside the cmd-heuristic
                // FREEZE.
                let msg =
                    match currentShellId with
                    | ShellRegistry.Cmd ->
                        "cmd does not report command exit codes; Jump to Last Error needs PowerShell."
                    | _ ->
                        "No failed command in history."
                log.LogInformation(
                    "ADR 0007 Phase 2c jump-to-last-error. Found=false Shell={Shell}",
                    (sprintf "%A" currentShellId))
                window.TerminalSurface.Announce(
                    msg,
                    ActivityIds.diagnostic)

        // ADR 0007 Phase 3 — rerun-input. Maintainer UX
        // direction (2026-05-17, after the 52-ADR7-P3 dogfood):
        // drop the two-step arm/confirm — it was the
        // conservative reading of the ADR's "confirm gesture"
        // open decision, which the maintainer (product owner)
        // has now resolved in favour of the simpler flow.
        // Behaviour: take the focused input cell's command,
        // CLEAR the current prompt line, and INSERT that command
        // at the prompt — the same `insertAtPromptClearingLine`
        // path the diagnostic test-script insertion uses. NO
        // auto-run: the user reviews the inserted line and
        // presses Enter themselves. That explicit human submit
        // is the safety affordance (still "no auto-run on a
        // gesture", per the ADR's risk-control intent — just
        // satisfied by the user's own Enter rather than a second
        // menu invocation). Counts-only Information log
        // (SourceCellSeq + CmdLen, never the command text — it
        // appears on the prompt line for the user, not in the
        // bundle; logging discipline).
        let runRerunInput () : unit =
            let log = Logger.get "Terminal.App.Program.runRerunInput"
            match SpeechCursor.focusedCell speechCursor with
            | None ->
                window.TerminalSurface.Announce(
                    "No focused cell. Navigate to a command cell first.",
                    ActivityIds.diagnostic)
            | Some fc ->
                match fc.Command with
                | None ->
                    window.TerminalSurface.Announce(
                        "The focused cell has no command to rerun.",
                        ActivityIds.diagnostic)
                | Some cmd ->
                    log.LogInformation(
                        "ADR 0007 Phase 3 rerun-input inserted. SourceCellSeq={CellSeq} CmdLen={CmdLen}",
                        fc.CellSequence,
                        cmd.Length)
                    insertAtPromptClearingLine cmd
                    window.TerminalSurface.Announce(
                        sprintf
                            "Command from cell %d inserted at the prompt. Press Enter to run."
                            fc.CellSequence,
                        ActivityIds.diagnostic)

        let runSpeechCursorToggleMode () : unit =
            let newMode = SpeechCursor.toggleMode speechCursor
            let label =
                match newMode with
                | SpeechCursor.AutoDrive -> "Speech cursor mode: AutoDrive."
                | SpeechCursor.Manual -> "Speech cursor mode: Manual."
            window.TerminalSurface.Announce(label, ActivityIds.diagnostic)

        bind HotkeyRegistry.SpeechCursorNext runSpeechCursorNext
        bind HotkeyRegistry.SpeechCursorPrevious runSpeechCursorPrevious
        bind HotkeyRegistry.SpeechCursorJumpToLatest runSpeechCursorJumpToLatest
        bind HotkeyRegistry.SpeechCursorToggleMode runSpeechCursorToggleMode
        bind HotkeyRegistry.JumpToLastError runJumpToLastError
        bind HotkeyRegistry.RerunFocusedInput runRerunInput

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

        // Cycle 47 follow-up (2026-05-13) — idle-flush
        // dispatcher. Ticks every 100 ms on the WPF dispatcher;
        // if `currentShellPolicy.IdleFlushMs` is `Some N` AND
        // the parser has been idle for ≥ N ms (i.e.
        // `lastReadUtc` is at least N ms in the past) AND
        // `ContentHistory.latestSeq` is past
        // `lastAnnouncedSeq`, slice the gap, cap at
        // `OutputAnnounceCapChars`, fire
        // `Announce(text, ActivityIds.output)`, advance the
        // watermark.
        //
        // Solves the intra-script `set /p` / `pause` /
        // `choice` problem: cmd just stops emitting bytes
        // when it's waiting for keystroke input; no
        // `PromptStart` fires; the tuple-finalise auto-narrate
        // is silent until the script completes. With idle-flush,
        // the parser-quiet period IS the trigger — after 350 ms
        // of stillness, pty-speak announces whatever has
        // accumulated since the last announce.
        //
        // 350 ms threshold (configurable per-shell via
        // `ShellPolicy.IdleFlushMs`) is short enough to feel
        // responsive during a `set /p` pause, long enough to
        // avoid firing between rapid output chunks of a normal
        // streaming command (`dir`'s line-emission rate is well
        // under 350 ms per line, but each chunk produces
        // multiple lines so 350 ms idle = the whole batch is
        // done streaming).
        //
        // Uses `DispatcherTimer` (WPF UI thread) rather than
        // `System.Threading.Timer` because `Announce` calls
        // through `peer.RaiseNotificationEvent` which must run
        // on the WPF dispatcher per the UIA peer contract. The
        // tuple-finalise path achieves this via
        // `window.Dispatcher.InvokeAsync`; here we just run
        // natively on the dispatcher thread to begin with.
        let idleFlushTimer = DispatcherTimer()
        idleFlushTimer.Interval <- TimeSpan.FromMilliseconds(100.0)
        idleFlushTimer.Tick.Add(fun _ ->
            // Cycle 48 PR-B (ADR 0003) — observe-mode sub-prompt
            // detection. Run before the existing idle-flush body
            // so the state-machine log captures transitions
            // independently of the legacy announce path. PR-E
            // will replace the legacy idle-flush body with
            // routing driven from this transition.
            try
                match ShellInteraction.trySubPromptDetect
                          shellInteraction DateTime.UtcNow with
                | Some trigger -> recordTransition trigger
                | None -> ()
            with ex ->
                shellInteractionLog.LogWarning(
                    ex,
                    "ShellInteraction sub-prompt detector raised: {Message}",
                    ex.Message)
            try
                match currentShellPolicy.IdleFlushMs with
                | Some thresholdMs ->
                    let idleMs =
                        (DateTimeOffset.UtcNow - lastReadUtc).TotalMilliseconds
                    // Cycle 47 follow-up (2026-05-13) post-preview.116
                    // — typing-window gate. preview.116 dogfood
                    // surfaced the per-character idle-flush failure
                    // mode: user types `e`, cmd echoes `e`, 350 ms
                    // pause, idle-flush slices the single byte and
                    // announces "e". Then `c`, then `h` ... NVDA
                    // reads each char (alongside its own keyboard-
                    // hook char-speech). Pre-fix this fired the
                    // `ReadyForInput` earcon too — the user heard a
                    // beep AND a per-char read on every keystroke.
                    //
                    // Cycle 48 PR-E (ADR 0003) — typing-window
                    // gate + idle-flush announce body retired.
                    // Sub-prompt detection happens via
                    // ShellInteraction's trySubPromptDetect (run
                    // earlier in this same tick) which fires
                    // recordTransition → sub-prompt announce.
                    // The legacy ContentHistory.tick + sliceText
                    // + Announce path is gone; the tick stays
                    // because it seals stale active spans (so
                    // the rendered tail in the diagnostic
                    // bundle stays current). The watermark
                    // bumping below preserves the no-double-
                    // talk invariant if any future legacy-path
                    // query re-reads.
                    if idleMs >= float thresholdMs then
                        ContentHistory.tick contentHistory DateTime.UtcNow
                        |> ignore
                        let latest = ContentHistory.latestSeq contentHistory
                        if latest > lastAnnouncedSeq then
                            // R6a (ADR 0006 R6 — hybrid progress
                            // streaming). PR-E retired the legacy
                            // idle-flush *body*; R6a re-wires this
                            // quiescence point to fire the SAME clean
                            // watermark slice the tuple-final uses,
                            // but DURING the Executing window, so a
                            // long-running command isn't silent until
                            // it seals. Watermark-composed with the
                            // seal announce (the R3c/R3e primitive
                            // `52-R3c-multi` validated: each flush
                            // advances `lastAnnouncedSeq`, so the
                            // tuple-final at `;D` speaks only the
                            // remainder — no double-talk). Gated:
                            //   * Executing only — never while
                            //     Composing at the prompt; the
                            //     sub-prompt + banner paths own those
                            //     windows (mutually exclusive — this
                            //     fires mid-Executing, they fire at
                            //     the Executing→Composing boundary /
                            //     first PromptStart).
                            //   * Streaming = TupleFinalOnly — the
                            //     only policy with a final read to
                            //     compose against; LineByLine / Off
                            //     keep their semantics untouched.
                            //   * fromSeq = max commandEnterSeq
                            //     lastAnnouncedSeq — the R3d
                            //     watermark; excludes the typed-
                            //     command echo (slicing from
                            //     lastAnnouncedSeq alone would
                            //     re-introduce the "echo hi⏎hi"
                            //     regression R3d fixed). NO
                            //     next-prompt strip: the next prompt
                            //     does not exist yet mid-Executing
                            //     (that is the seal's job).
                            // claude unaffected (its IdleFlushMs =
                            // None ⇒ this whole arm never runs).
                            // ActivityIds.output + MostRecent: a
                            // later progress chunk correctly
                            // supersedes an unfinished earlier one
                            // (identical to the tuple-final).
                            let isExecuting =
                                match shellInteraction.Current with
                                | ShellInteraction.Executing _ -> true
                                | _ -> false
                            let progressOptIn =
                                match currentShellPolicy.Streaming with
                                | ShellPolicy.TupleFinalOnly -> true
                                | _ -> false
                            if isExecuting && progressOptIn then
                                let fromSeq =
                                    max commandEnterSeq lastAnnouncedSeq
                                let raw =
                                    ContentHistory.sliceText
                                        contentHistory
                                        fromSeq
                                        System.Int64.MaxValue
                                let body =
                                    (raw.TrimStart('\r', '\n'))
                                        .TrimEnd('\r', '\n')
                                if not
                                    (System.String.IsNullOrWhiteSpace
                                        body) then
                                    let toSay =
                                        if body.Length
                                           <= OutputAnnounceCapChars
                                        then body
                                        else
                                            body.Substring(
                                                body.Length
                                                - OutputAnnounceCapChars)
                                    log.LogInformation(
                                        "R6a progress announce (Executing idle-flush). CommandEnterSeq={CommandEnterSeq} LastAnnouncedSeq={LastAnnouncedSeq} FromSeq={FromSeq} Len={Len}",
                                        commandEnterSeq,
                                        lastAnnouncedSeq,
                                        fromSeq,
                                        toSay.Length)
                                    window.TerminalSurface.Announce(
                                        toSay, ActivityIds.output)
                            // Advance the watermark in EVERY case
                            // (announced or not / not-Executing /
                            // not-opt-in) — preserves the prior
                            // silent no-double-talk invariant
                            // exactly; the seal then speaks only the
                            // remainder.
                            lastAnnouncedSeq <- latest
                | None -> ()
            with ex ->
                // Idle-flush exceptions must not wedge the
                // timer. Log + continue; the next tick fires
                // the same path and would surface a persistent
                // bug rather than silently nothing.
                log.LogWarning(
                    ex,
                    "Idle-flush tick raised exception: {Message}",
                    ex.Message))
        idleFlushTimer.Start()

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
            // Cycle 45 Commit 2 — `speechCursorAnnounce` and
            // `onSpeechCursorWake` are defined earlier in
            // `compose` so the prompt-boundary handler and
            // selection-detector marker emission can close over
            // them. See lines ~880-905.

            let _ =
                startReaderLoop
                    window.Dispatcher
                    host
                    cmdAdapter
                    screen
                    window.TerminalSurface
                    contentHistory
                    onSpeechCursorWake
                    pumpChannel.Writer
                    (fun _ -> lastReadUtc <- DateTimeOffset.UtcNow)
                    (fun ts b -> ShellInteraction.observeByte shellInteraction ts b)
                    cts.Token

            // Cycle 49 PR-I — history-recall draft announce.
            //
            // When the user presses Up / Down arrow during Composing,
            // the shell (cmd's doskey, PowerShell's PSReadLine, bash
            // readline) rewrites the on-screen input line to display
            // the previous / next command from its history. The PTY
            // bytes the shell sends to perform the rewrite (clear-
            // line CSI sequences, cursor-position resets, the
            // recalled-command bytes) flow through the reader thread
            // into Screen but bypass `UserInputBuffer`'s byte-stream
            // tracking — `UserInputBuffer` watches OUTGOING bytes
            // (user keystrokes to PTY) and so doesn't see the shell-
            // side rewrite.
            //
            // The fix here doesn't try to reverse-engineer the shell's
            // rewrite protocol (CSI 2K, CSI nG, bare `\r`, etc. vary
            // per shell). Instead: when the byte-write wrapper sees
            // an Up / Down arrow keystroke going OUT to the PTY,
            // schedule a debounced read of the current prompt row
            // (100 ms after the last Up / Down — the typical PTY
            // round-trip + render budget) and announce whatever the
            // input line now reads as. Rapid Up / Down spam coalesces
            // to a single announce of the final state.
            let historyRecallTimer = DispatcherTimer()
            historyRecallTimer.Interval <- TimeSpan.FromMilliseconds(100.0)
            let tryAnnounceRecalledDraft () =
                try
                    let _, (cursorRow, _), snapshot =
                        screen.SnapshotRows(0, screen.Rows)
                    let promptText =
                        match shellInteraction.Current with
                        | ShellInteraction.Composing data ->
                            match data.PromptText with
                            | ValueSome t -> Some t
                            | ValueNone -> None
                        | _ -> None
                    let promptRowOpt =
                        match currentSession.Active with
                        | Some active -> active.PromptRowIndex
                        | None -> None
                    // Cycle 51 PR-Z — a recalled command that
                    // soft-wraps past `screen.Cols` (long script
                    // paths) spans prompt-row..cursor-row, and cmd
                    // scrolls the viewport, so a *fixed*
                    // `PromptRowIndex` ends up pointing at the
                    // wrapped tail (`…test-01-e` → `cho.cmd"`).
                    // Resolve the prompt row by scanning UP from
                    // the cursor for the row that currently starts
                    // with the prompt text (robust to scroll);
                    // fall back to `Active.PromptRowIndex`.
                    let actualPromptRow =
                        match promptText with
                        | Some pt ->
                            let mutable found = -1
                            let mutable r =
                                min cursorRow (snapshot.Length - 1)
                            while found < 0 && r >= 0 do
                                if (CanonicalState.renderRow snapshot r)
                                    .StartsWith(
                                        pt, StringComparison.Ordinal) then
                                    found <- r
                                r <- r - 1
                            if found >= 0 then Some found else promptRowOpt
                        | None -> promptRowOpt
                    match actualPromptRow with
                    | Some promptRow when
                        promptRow >= 0 && promptRow < snapshot.Length ->
                        // Join prompt-row..cursor-row so a wrapped
                        // recall is read whole. cmd soft-wraps only
                        // at full width, so non-final rows in the
                        // span carry no trailing padding; the final
                        // `.Trim()` handles the cursor row's. When
                        // the command doesn't wrap, cursorRow ==
                        // promptRow → single row (unchanged
                        // behaviour). With no prompt text (e.g.
                        // PowerShell) we can't bound the span
                        // safely, so stay on the single prompt row.
                        let endRow =
                            match promptText with
                            | Some _ ->
                                max promptRow
                                    (min cursorRow (snapshot.Length - 1))
                            | None -> promptRow
                        let row =
                            seq {
                                for r in promptRow .. endRow ->
                                    CanonicalState.renderRow snapshot r }
                            |> String.concat ""
                        // Strip the prompt-path prefix if the state
                        // machine has one. Without the strip the
                        // announce reads back the full
                        // `C:\Users\Kyle\…\current>recalledCmd`
                        // every time, which the user has already
                        // navigated past visually. Inspired by the
                        // sub-prompt last-line announce (PR-F).
                        let stripped =
                            match promptText with
                            | Some pt
                                when row.StartsWith(
                                    pt, StringComparison.Ordinal) ->
                                true
                            | _ -> false
                        let draft =
                            if stripped then
                                match promptText with
                                | Some pt -> row.Substring(pt.Length)
                                | None -> row
                            else
                                row
                        let trimmed = draft.Trim()
                        // PR-J (2026-05-14) — diagnostic
                        // logging. Maintainer reported a non-
                        // deterministic "show CMD" mis-announce
                        // after a few evaluations in cmd. Most
                        // likely the prompt-prefix strip
                        // mis-matched and the full row (path +
                        // command) got narrated. The Debug-
                        // level Row / PromptText / Stripped /
                        // Announce arguments let the next
                        // diagnostic bundle confirm which case
                        // hit and what text NVDA spoke.
                        //
                        // Debug-level (not Information) so
                        // these don't flood the bundle when
                        // FileLogger is at Information.
                        // Announce text is logged for parity
                        // with `UserInputBuffer captured` —
                        // both go to NVDA out loud so no new
                        // sensitivity boundary is crossed.
                        let promptTextDesc =
                            match promptText with
                            | Some pt -> pt
                            | None -> "<none>"
                        if not (System.String.IsNullOrWhiteSpace trimmed) then
                            log.LogInformation(
                                "PR-I history-recall announce. RowLen={RowLen} DraftLen={DraftLen} Stripped={Stripped}",
                                row.Length, trimmed.Length, stripped)
                            log.LogDebug(
                                "PR-I history-recall details. PromptRow={PromptRow} Row={Row} PromptText={PromptText} Stripped={Stripped} Announce={Announce}",
                                promptRow, row, promptTextDesc,
                                stripped, trimmed)
                            window.TerminalSurface.Announce(
                                trimmed, ActivityIds.inputAssistant)
                        else
                            // Empty recall (shell offered no history /
                            // history exhausted). Stay silent —
                            // narrating a blank line is noise.
                            log.LogDebug(
                                "PR-I history-recall: empty draft, no announce. PromptRow={PromptRow} Row={Row} PromptText={PromptText} Stripped={Stripped}",
                                promptRow, row, promptTextDesc, stripped)
                    | _ -> ()
                with ex ->
                    log.LogWarning(
                        ex,
                        "history-recall announce failed: {Message}",
                        ex.Message)
            historyRecallTimer.Tick.Add(fun _ ->
                // PR-L (2026-05-14) — settle gate. The 100 ms
                // timer is reset by every Up/Down keypress
                // (debounce); on tick, also check whether PTY
                // bytes are still flowing in. If the reader has
                // emitted bytes within the last 100 ms, cmd's
                // response to the recall is mid-flight; defer
                // by restarting the timer. Otherwise the
                // screen-read happens against a settled
                // display.
                //
                // Maintainer 2026-05-14 dogfood: rapid Up/Down
                // tapping reproduced two symptoms — (1) spoken
                // text different from visually-displayed
                // command, (2) visually-displayed command
                // different from what cmd executes on Enter.
                // Symptom (1) was my screen-read firing
                // mid-response (cmd's line-rewrite bytes still
                // arriving). Symptom (2) is fundamentally a
                // user-perception race against ConPTY round-
                // trip and isn't pty-speak's to fix — but
                // resolving (1) means the spoken text matches
                // what cmd will actually run if Enter is
                // pressed at the settled moment.
                let now = DateTimeOffset.UtcNow
                let elapsedSinceLastRead =
                    (now - lastReadUtc).TotalMilliseconds
                if elapsedSinceLastRead < 100.0 then
                    log.LogDebug(
                        "PR-L history-recall settle-gate: deferring (LastReadAgoMs={Ms}).",
                        elapsedSinceLastRead)
                    historyRecallTimer.Stop()
                    historyRecallTimer.Start()
                else
                    historyRecallTimer.Stop()
                    tryAnnounceRecalledDraft ())
            let triggerHistoryRecallDebounce () =
                // Stop + restart so rapid Up / Down keypresses
                // coalesce to a single announce of the final state.
                historyRecallTimer.Stop()
                historyRecallTimer.Start()

            window.TerminalSurface.SetPtyHost(
                Action<byte[]>(fun bytes ->
                    // Cycle 48 PR-D (ADR 0003) — byte-stream-
                    // driven UserInputBuffer maintenance + Enter
                    // detection. Each printable ASCII byte
                    // (0x20..0x7E) appends to the buffer; BS
                    // (0x08) backspaces; CR (0x0D) captures the
                    // current buffer text and fires
                    // EnterPressed with that text. Multi-byte
                    // sequences (arrow keys, Unicode chars
                    // outside ASCII range) fall through and are
                    // not tracked; refining to true key-level
                    // tracking is deferred (would route the
                    // KeyCode through to the buffer alongside
                    // the encoded bytes — see ADR §5.5).
                    //
                    // R3e (2026-05-16) — a single-key sub-prompt
                    // (cmd `choice`) is answered by a keystroke
                    // with NO `\r`, so the `\r`→EnterPressed
                    // branch below never fires; without this the
                    // state stays stuck
                    // `Composing(SinglekeySubmit=true)`, which
                    // mis-tags cmd's resumed output as
                    // `UserInputEcho` AND blocks re-detection of
                    // a 2nd sub-prompt in the same command (the
                    // test-09 / KI defect, bundle-proven
                    // 2026-05-16). Emit `SingleKeySubmitted` on
                    // the first keystroke while in that state →
                    // `Executing`. The `SinglekeySubmit` guard
                    // leaves normal command typing untouched, and
                    // post-transition the state is `Executing` so
                    // later writes no-op.
                    match shellInteraction.Current with
                    | ShellInteraction.Composing d when d.SinglekeySubmit && bytes.Length > 0 ->
                        recordTransition ShellInteraction.SingleKeySubmitted
                    | _ -> ()
                    for i in 0 .. bytes.Length - 1 do
                        let b = bytes.[i]
                        if b >= 0x20uy && b <= 0x7Euy then
                            shellInteraction.UserInputBuffer.AppendChar(char b)
                        elif b = 0x08uy then
                            shellInteraction.UserInputBuffer.Backspace()
                        elif b = 0x0Duy then
                            let captured =
                                shellInteraction.UserInputBuffer.Capture()
                            shellInteractionLog.LogDebug(
                                "UserInputBuffer captured on Enter: Length={Length} Text={Text}",
                                captured.Length, captured)
                            // Cycle 51 PR-X — capture the
                            // command-Enter Seq watermark.
                            // `commandEnterSeq` advances ONLY on the
                            // top-level command Enter — a
                            // sub-prompt-response Enter must not move
                            // it, since the command's whole output
                            // region (question + interaction +
                            // post-response) still begins at the
                            // original command Enter. It is threaded
                            // into SessionModel.extractIOCell so the
                            // persisted command/output split is
                            // immune to history-scroll accumulation.
                            // R3b (ADR 0005/0006) — the PR-X
                            // `lastEnterSeq` announce watermark and
                            // the PR-Y / PR-AB strip state were
                            // retired; the tuple-final announce now
                            // speaks the sealed IOCell OutputText.
                            if not awaitingSubPromptEnter then
                                commandEnterSeq <-
                                    ContentHistory.latestSeq contentHistory
                            shellInteractionLog.LogInformation(
                                "R3b command-Enter watermark. CommandEnterSeq={CommandEnterSeq} SubPromptResponse={SubPromptResponse}",
                                commandEnterSeq,
                                awaitingSubPromptEnter)
                            recordTransition
                                (ShellInteraction.EnterPressed captured)
                    // Cycle 49 PR-I — history-recall announce.
                    // Detect Up/Down arrow keystrokes the user
                    // is sending to the shell; cmd's doskey /
                    // PSReadLine / bash readline will replace
                    // the on-screen input line with the
                    // recalled command. Schedule a debounced
                    // read of the prompt row + announce so the
                    // user hears what was just inserted.
                    //
                    // Cursor-key encoding (`KeyEncoding.fs`):
                    //   Normal mode      `\x1B [ A/B` (3 bytes)
                    //   App / DECCKM     `\x1B O A/B` (3 bytes)
                    // Modifier-augmented forms (Shift+Up etc.)
                    // are 6+ bytes and not history-recall; we
                    // ignore them.
                    if bytes.Length = 3
                       && bytes.[0] = 0x1Buy
                       && (bytes.[1] = 0x5Buy || bytes.[1] = 0x4Fuy)
                       && (bytes.[2] = 0x41uy || bytes.[2] = 0x42uy) then
                        triggerHistoryRecallDebounce ()
                    host.WriteBytes(bytes)),
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
                    // R2 (ADR 0005/0006, Option B) — cmd
                    // OSC-133 prompt injection on
                    // switch-to-cmd. Gated on the switch
                    // target so claude / PowerShell switches
                    // are byte-identical. The cmd transport
                    // adapter owns the injection (ADR 0006).
                    // R5a (ADR 0006) — route the per-shell
                    // OSC-133 injection through the single
                    // selection seam (`SessionHost.Osc133-
                    // IntegratorFor`) instead of an inline
                    // `if = Cmd` gate. Byte-identical: cmd
                    // wraps + logs exactly as before; non-cmd
                    // is identity + no log (the pre-R5a `else`).
                    // R5b adds the PowerShell arm in the
                    // selector, not here.
                    let newCmdLine =
                        let integrated =
                            (Terminal.Shell.SessionHost.Osc133IntegratorFor
                                target) newCmdLine
                        if target = ShellRegistry.Cmd then
                            log.LogInformation(
                                "R2 cmd OSC-133 prompt injection applied (shell-switch). Base={Base} Integrated={Integrated}",
                                newCmdLine,
                                integrated)
                        elif target = ShellRegistry.PowerShell then
                            log.LogInformation(
                                "R5b PowerShell OSC-133 prompt injection applied (shell-switch). Base={Base} Integrated={Integrated}",
                                newCmdLine,
                                integrated)
                        integrated
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
                                        // R3a (ADR 0005/0006)
                                        // — new shell session:
                                        // re-arm the heuristic
                                        // until the new shell's
                                        // first OSC-133 (a
                                        // cmd→claude switch has
                                        // no OSC and MUST keep
                                        // the heuristic; a
                                        // cmd→cmd switch re-mutes
                                        // on the first new-session
                                        // OSC boundary).
                                        oscSeenThisSession <- false
                                        // Cycle 29b — companion
                                        // reset on shell-switch
                                        // so a stale candidate
                                        // from the prior shell
                                        // doesn't leak into the
                                        // new one.
                                        selectionDetector <-
                                            SelectionDetector.reset
                                                selectionDetector
                                        // Cycle 45 Commit 2 —
                                        // ContentHistory +
                                        // SpeechCursor companion
                                        // resets. The previous
                                        // shell's tuple history
                                        // is no longer relevant;
                                        // start the new shell
                                        // with empty substrate.
                                        // Runs directly here (no
                                        // dispatcher hop) because
                                        // switchToShell already
                                        // executes on the
                                        // dispatcher thread.
                                        ContentHistory.reset contentHistory
                                        SpeechCursor.reset speechCursor
                                        // Cycle 51 PR-AD — the
                                        // previous shell's cell
                                        // transcript is no longer
                                        // relevant after a switch.
                                        SpeechCursor.cellReset speechCursor
                                        // Cycle 47 follow-up —
                                        // reset the idle-flush
                                        // watermark when
                                        // ContentHistory's
                                        // sequence numbering
                                        // restarts. Without
                                        // this, the comparison
                                        // `latest > watermark`
                                        // would stay false
                                        // until the new shell
                                        // accumulated more
                                        // seqs than the old
                                        // one had — meaning
                                        // initial new-shell
                                        // output would silently
                                        // skip narration.
                                        lastAnnouncedSeq <- -1L
                                        lastAnnouncedText <- ""
                                        // R3b (ADR 0005/0006) —
                                        // commandEnterSeq resets
                                        // with ContentHistory (a
                                        // stale Seq would slice
                                        // garbage from the fresh
                                        // shell's history). The
                                        // PR-X lastEnterSeq +
                                        // PR-Y/PR-AB strip state
                                        // were retired.
                                        commandEnterSeq <- -1L
                                        // Cycle 51 PR-AC — speak
                                        // the new shell's banner on
                                        // its first prompt (the
                                        // control already has focus
                                        // so NVDA won't re-read it).
                                        announceBannerOnNextPrompt <- true
                                        // Cycle 48 PR-B —
                                        // ShellInteraction state
                                        // resets on shell switch
                                        // alongside the watermarks.
                                        // The new shell starts in
                                        // a fresh Composing state.
                                        shellInteraction.Reset()
                                        // Cycle 45f — apply the
                                        // new shell's verbosity
                                        // policy through the
                                        // three-layer overlay
                                        // (runtime → TOML →
                                        // compiled defaults).
                                        // Runs AFTER
                                        // SpeechCursor.reset (so
                                        // Position / LastSpokenSeq
                                        // are clean for the new
                                        // shell's history) and
                                        // BEFORE wirePostSpawn
                                        // below, so the first
                                        // paint of the new shell
                                        // narrates under the
                                        // right policy.
                                        applyShellPolicy
                                            (shellIdToConfigKey shell.Id)
                                        // Cycle 38b — re-resolve
                                        // the active profile set
                                        // for the new shell so its
                                        // TOML `[shell.<key>] profiles`
                                        // takes effect. All shells
                                        // default to the canonical
                                        // [passthrough; earcon;
                                        // selection] triple post-
                                        // Cycle 39 revert.
                                        OutputDispatcher.ProfileRegistry.setActiveProfileSet
                                            (resolveProfilesForShell shell.Id)
                                        // Cycle 45c PR-3b — pathway
                                        // Reset + reassignment +
                                        // SetBaseline removed. The
                                        // ContentHistory reset
                                        // earlier in `switchToShell`
                                        // already cleared the
                                        // substrate; no pathway
                                        // state to clear. Phase 2
                                        // ClaudeCodePathway will
                                        // reintroduce a substrate-
                                        // swap mechanism if needed,
                                        // but its shape is
                                        // ContentHistory-aware.
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

        // Cycle 45f — Output Verbosity menu binding. Menu pick
        // writes to `runtimeShellPolicy.[currentShellKey]`
        // (Layer 3 of the three-layer settings model) then
        // `applyShellPolicy` pushes the new policy into
        // `speechCursor.Parameters`. Subsequent
        // `SpeechCursor.onAppend` invocations observe the new
        // mode; entries already announced under the previous
        // policy stay announced (no rewind).
        let outputVerbosityLog =
            Logger.get "Terminal.App.Program.bindMultiState.OutputVerbosity"
        let outputVerbosityDef =
            HotkeyRegistry.multiStateOf HotkeyRegistry.OutputVerbosity
        let outputVerbosityBinding =
            bindMultiState
                window
                outputVerbosityDef
                (fun () ->
                    match currentShellPolicy.Streaming with
                    | ShellPolicy.TupleFinalOnly -> "tuple_final"
                    | ShellPolicy.LineByLine -> "line_by_line"
                    | ShellPolicy.Off -> "off")
                (fun target ->
                    let next =
                        match target with
                        | "line_by_line" -> Some ShellPolicy.LineByLine
                        | "off" -> Some ShellPolicy.Off
                        | "tuple_final" -> Some ShellPolicy.TupleFinalOnly
                        | _ -> None
                    match next with
                    | None ->
                        outputVerbosityLog.LogWarning(
                            "OutputVerbosity set unknown target '{Target}'; ignored.",
                            target)
                    | Some streaming ->
                        let shellKey = shellIdToConfigKey currentShellId
                        let updated =
                            { currentShellPolicy with
                                Streaming = streaming }
                        runtimeShellPolicy <-
                            Map.add shellKey updated runtimeShellPolicy
                        applyShellPolicy shellKey
                        outputVerbosityLog.LogInformation(
                            "OutputVerbosity set to {Target} for shell {Shell}.",
                            target, shellKey)
                        let cue =
                            match streaming with
                            | ShellPolicy.TupleFinalOnly ->
                                "Output verbosity tuple final."
                            | ShellPolicy.LineByLine ->
                                "Output verbosity line by line."
                            | ShellPolicy.Off ->
                                "Output verbosity off."
                        window.TerminalSurface.Announce(
                            cue, ActivityIds.logToggle))
        multiStateBindings.[HotkeyRegistry.OutputVerbosity] <-
            outputVerbosityBinding

        // Cycle 45f — Prompt Path menu binding. Same shape +
        // scoping as OutputVerbosity.
        let promptPathLog =
            Logger.get "Terminal.App.Program.bindMultiState.PromptPathVerbosity"
        let promptPathDef =
            HotkeyRegistry.multiStateOf HotkeyRegistry.PromptPathVerbosity
        let promptPathBinding =
            bindMultiState
                window
                promptPathDef
                (fun () ->
                    match currentShellPolicy.PromptPath with
                    | ShellPolicy.Suppress -> "suppress"
                    | ShellPolicy.FinalDirOnly -> "final_dir_only"
                    | ShellPolicy.Full -> "full"
                    | ShellPolicy.FullOnChangeElseFinal -> "full_on_change"
                    | ShellPolicy.FinalOnChangeElseFull -> "final_on_change"
                    | ShellPolicy.SilentOnUnchangedFullOnChange ->
                        "full_on_change_silent"
                    | ShellPolicy.SilentOnUnchangedFinalOnChange ->
                        "final_on_change_silent")
                (fun target ->
                    let next =
                        match target with
                        | "final_dir_only" -> Some ShellPolicy.FinalDirOnly
                        | "full" -> Some ShellPolicy.Full
                        | "full_on_change" ->
                            Some ShellPolicy.FullOnChangeElseFinal
                        | "final_on_change" ->
                            Some ShellPolicy.FinalOnChangeElseFull
                        | "full_on_change_silent" ->
                            Some ShellPolicy.SilentOnUnchangedFullOnChange
                        | "final_on_change_silent" ->
                            Some ShellPolicy.SilentOnUnchangedFinalOnChange
                        | "suppress" -> Some ShellPolicy.Suppress
                        | _ -> None
                    match next with
                    | None ->
                        promptPathLog.LogWarning(
                            "PromptPathVerbosity set unknown target '{Target}'; ignored.",
                            target)
                    | Some promptPath ->
                        let shellKey = shellIdToConfigKey currentShellId
                        let updated =
                            { currentShellPolicy with
                                PromptPath = promptPath }
                        runtimeShellPolicy <-
                            Map.add shellKey updated runtimeShellPolicy
                        applyShellPolicy shellKey
                        promptPathLog.LogInformation(
                            "PromptPathVerbosity set to {Target} for shell {Shell}.",
                            target, shellKey)
                        let cue =
                            match promptPath with
                            | ShellPolicy.Suppress -> "Prompt path suppressed."
                            | ShellPolicy.FinalDirOnly ->
                                "Prompt path final directory only."
                            | ShellPolicy.Full -> "Prompt path full."
                            | ShellPolicy.FullOnChangeElseFinal ->
                                "Prompt path full on directory change."
                            | ShellPolicy.FinalOnChangeElseFull ->
                                "Prompt path final directory on change, full when unchanged."
                            | ShellPolicy.SilentOnUnchangedFullOnChange ->
                                "Prompt path full on change, silent when unchanged."
                            | ShellPolicy.SilentOnUnchangedFinalOnChange ->
                                "Prompt path final directory on change, silent when unchanged."
                        window.TerminalSurface.Announce(
                            cue, ActivityIds.logToggle))
        multiStateBindings.[HotkeyRegistry.PromptPathVerbosity] <-
            promptPathBinding

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
