namespace PtySpeak.App

open System
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Input
open System.Windows.Threading
open Microsoft.Extensions.Logging
open Velopack
open Velopack.Sources
open Terminal.Core
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
            (notifications: System.Threading.Channels.ChannelWriter<ScreenNotification>)
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
                                let written = notifications.TryWrite(RowsChanged [])
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
                            ParserError(sprintf "Parser/reader loop: %s" safe))
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

    /// Wire `Ctrl+Shift+U` to trigger `runUpdateFlow`. The
    /// `KeyBinding` lives in the Window's `InputBindings`
    /// collection so the gesture is captured BEFORE any future
    /// PTY-input keyboard handler routes the keys to the child
    /// shell — Stage 6 (keyboard input to PTY) will need to
    /// honour the same priority order so app-level shortcuts
    /// keep working.
    let private setupAutoUpdateKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("CheckForUpdates", typeof<MainWindow>)
        let gesture = KeyGesture(Key.U, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> runUpdateFlow window)))
        |> ignore

    /// Launch the bundled process-cleanup diagnostic script in a
    /// separate PowerShell window. Triggered by `Ctrl+Shift+D` —
    /// added because Task Manager's Processes-tab chevron-expand
    /// affordance is not screen-reader-accessible, so a maintainer
    /// running the deferred Stage 4 process-cleanup test on
    /// NVDA cannot navigate the Task Manager UI to verify the
    /// process tree by hand. The diagnostic script lives next to
    /// `Terminal.App.exe` (bundled via `Terminal.App.fsproj`'s
    /// `Content` include) and emits one-fact-per-line stdout that
    /// NVDA reads aloud naturally.
    ///
    /// The PowerShell process is launched with `-NoExit` so the
    /// spawned window stays open after the test completes; the
    /// user reads the result, then closes that window manually.
    /// This is intentional — auto-closing would lose the output.
    ///
    /// Future: more diagnostics (UIA peer health, ConPTY child
    /// status, version dump) can be added as additional scripts
    /// next to this one and routed through the same hotkey via
    /// a sub-menu, OR added as their own hotkeys following the
    /// app-reserved-hotkey contract in `spec/tech-plan.md` §6.
    /// PR-J — enumerate the live shell-related processes and
    /// return both a per-name count list and a one-line announce-
    /// safe summary. Used by the Ctrl+Shift+D handler to report
    /// state inline BEFORE launching the cleanup script — gives
    /// the user (or a Claude session triaging on their behalf)
    /// an immediate "what's currently running" snapshot without
    /// the close-and-recheck round trip.
    ///
    /// Names checked: `cmd`, `powershell`, `pwsh`, `claude`,
    /// `Terminal.App`. `Process.GetProcessesByName` is the supported
    /// .NET API — case-insensitive, no `.exe` suffix in the name
    /// argument. The returned `Process` objects are disposed
    /// immediately; we only need the count.
    ///
    /// Logged at Information level too so post-hoc log analysis
    /// captures the snapshot even if the user pressed Ctrl+Shift+D
    /// without writing down what NVDA said.
    let private enumerateShellProcesses () : string =
        let names = [| "cmd"; "powershell"; "pwsh"; "claude"; "Terminal.App" |]
        let counts =
            names
            |> Array.map (fun n ->
                let count =
                    try
                        let procs = System.Diagnostics.Process.GetProcessesByName(n)
                        for p in procs do
                            try p.Dispose() with _ -> ()
                        procs.Length
                    with _ -> -1
                n, count)
        let parts =
            counts
            |> Array.map (fun (n, c) ->
                if c < 0 then sprintf "%s ?" n
                else sprintf "%d %s" c n)
        String.concat ", " parts

    let private runDiagnostic (window: MainWindow) : unit =
        let scriptPath =
            System.IO.Path.Combine(
                System.AppContext.BaseDirectory,
                "test-process-cleanup.ps1")
        // PR-J — inline process enumeration. The user's previous
        // diagnostic ritual was "press Ctrl+Shift+D, close the
        // window, watch what gets reaped" — fine for a deliberate
        // close-and-recheck sweep but heavyweight for a quick "is
        // anything weird running right now?" check. Enumerating
        // up-front and announcing the counts means a screen-reader
        // user gets the snapshot in one keystroke without losing
        // their pty-speak session. Maintainer feedback 2026-05-03:
        // "you should automatically check child processes when you
        // launch diagnostics".
        //
        // The full close-and-recheck flow (PowerShell script in a
        // separate window) STILL launches afterwards so the
        // existing orphan-detection workflow continues working;
        // the inline enumeration is purely additive.
        let snapshot = enumerateShellProcesses ()
        let log = Logger.get "Terminal.App.Program.runDiagnostic"
        log.LogInformation(
            "Diagnostic snapshot. ProcessCounts={Snapshot}",
            snapshot)
        if not (System.IO.File.Exists scriptPath) then
            window.TerminalSurface.Announce(
                sprintf
                    "Diagnostic snapshot: %s. Cleanup script not found at %s."
                    snapshot
                    scriptPath,
                ActivityIds.diagnostic)
        else
            // Announce FIRST so NVDA's speech queue holds the message
            // before focus shifts. The new PowerShell window's
            // activation triggers NVDA's interrupt-on-focus-change,
            // which truncates whatever is currently being spoken — so
            // the previous "Process.Start then Announce" order made the
            // announce effectively silent (the queued speech was
            // immediately overwritten by NVDA reading the new PowerShell
            // window's title). Pre-Stage-5 NVDA verification on
            // `v0.0.1-preview.NN` confirmed the regression.
            //
            // PR-J prefixes the snapshot to the launch cue so a
            // screen-reader user hears "1 cmd, 0 powershell, 0 pwsh,
            // 1 claude, 1 Terminal.App. Launching diagnostic." in
            // one announcement — strictly additive to the previous
            // "Launching diagnostic." cue. The 700ms-then-spawn
            // pattern below stays unchanged.
            // TODO Phase 2: the 700ms delay should come from a TOML
            // setting alongside the Stage 5 coalescer constants.
            window.TerminalSurface.Announce(
                sprintf
                    "Diagnostic snapshot: %s. Launching cleanup test."
                    snapshot,
                ActivityIds.diagnostic)
            let _ =
                task {
                    do! Task.Delay(700)
                    let action () =
                        try
                            let psi = System.Diagnostics.ProcessStartInfo()
                            psi.FileName <- "powershell.exe"
                            psi.Arguments <-
                                sprintf
                                    "-ExecutionPolicy Bypass -NoExit -File \"%s\""
                                    scriptPath
                            psi.UseShellExecute <- true
                            System.Diagnostics.Process.Start(psi) |> ignore
                        with ex ->
                            let safe = AnnounceSanitiser.sanitise ex.Message
                            window.TerminalSurface.Announce(
                                sprintf "Could not launch diagnostic: %s" safe,
                                ActivityIds.error)
                    do! window.Dispatcher.InvokeAsync(Action(action)).Task
                    ()
                }
            ()

    /// Wire `Ctrl+Shift+D` to trigger `runDiagnostic`. Same
    /// pattern as `setupAutoUpdateKeybinding` above. Per the
    /// app-reserved-hotkey contract, this gesture is in the
    /// reserved list (Ctrl+Shift+U for update, Ctrl+Shift+D
    /// for diagnostic, Ctrl+Shift+R for new-release form,
    /// Ctrl+Shift+M for Stage 9 mute, Alt+Shift+R for Stage 10
    /// review mode) and Stage 6's keyboard layer must continue
    /// to honour the priority order.
    let private setupDiagnosticKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("RunDiagnostic", typeof<MainWindow>)
        let gesture = KeyGesture(Key.D, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> runDiagnostic window)))
        |> ignore

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
        // Same announce-before-focus-grab pattern as `runDiagnostic`
        // above (the launched browser will steal focus and NVDA's
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

    /// Wire `Ctrl+Shift+R` to trigger `runOpenNewRelease`. Same
    /// pattern as the other reserved hotkeys above.
    let private setupNewReleaseKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("OpenNewRelease", typeof<MainWindow>)
        let gesture = KeyGesture(Key.R, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> runOpenNewRelease window)))
        |> ignore

    /// Mutable handle to the active `FileLoggerSink`. Set by
    /// `compose ()` at startup; consulted by `runOpenLogs` to
    /// find the active log directory and disposed in
    /// `app.Exit.Add` so the writer task flushes pending entries.
    let mutable private loggerSink : FileLoggerSink option = None

    /// Open the active logs folder in File Explorer. Triggered
    /// by `Ctrl+Shift+L`. Useful so the user can grab the latest
    /// log file (e.g. to send to a maintainer when reporting a
    /// bug) without leaving pty-speak.
    let private runOpenLogs (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runOpenLogs"
        log.LogInformation("Ctrl+Shift+L pressed — opening logs folder.")
        let dir =
            match loggerSink with
            | Some s -> s.LogDirectory
            | None ->
                // Logger hasn't initialised — fall back to the
                // default directory so the explorer at least
                // opens a sensible parent folder.
                let baseDir =
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData)
                System.IO.Path.Combine(baseDir, "PtySpeak", "logs")
        // Same announce-before-focus-grab pattern as the other
        // hotkeys that launch a separate window.
        window.TerminalSurface.Announce(
            "Opening logs folder.",
            ActivityIds.diagnostic)
        let _ =
            task {
                do! Task.Delay(700)
                let action () =
                    try
                        // Ensure the folder exists before we ask
                        // explorer to open it; on a fresh install
                        // before any logs have been written, the
                        // directory may not yet exist.
                        System.IO.Directory.CreateDirectory(dir) |> ignore
                        let psi = System.Diagnostics.ProcessStartInfo()
                        psi.FileName <- "explorer.exe"
                        psi.Arguments <- sprintf "\"%s\"" dir
                        psi.UseShellExecute <- true
                        System.Diagnostics.Process.Start(psi) |> ignore
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        window.TerminalSurface.Announce(
                            sprintf "Could not open logs folder: %s" safe,
                            ActivityIds.error)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                ()
            }
        ()

    /// Copy the active session's log file content to the
    /// system clipboard so the maintainer can paste it into a
    /// bug report without navigating File Explorer. Triggered
    /// by `Ctrl+Shift+;` (the semicolon / colon key, immediately
    /// to the right of `L` on a US-layout keyboard). Pairs by
    /// physical proximity with the `Ctrl+Shift+L` open-folder
    /// primary: same hand position, two adjacent keys, "open
    /// the folder | copy the active file". Reads the file
    /// pointed to by `FileLoggerSink.ActiveLogPath`, copies the
    /// entire content as a single string, and announces the
    /// byte count via NVDA on success.
    ///
    /// Hotkey-choice history. The original binding was
    /// `Ctrl+Alt+L` (paired with `Ctrl+Shift+L` open-folder).
    /// Two production issues forced the move:
    ///
    /// 1. `Ctrl+Alt+L` is the Windows Magnifier "zoom-in"
    ///    shortcut on some default Magnifier configurations,
    ///    so the OS swallowed the gesture before it reached
    ///    pty-speak.
    /// 2. The `Alt`-modifier path through WPF's input pipeline
    ///    delivers `e.Key = Key.System` + `e.SystemKey = Key.L`,
    ///    which required a SystemKey-aware filter throughout
    ///    `OnPreviewKeyDown` — and that filter then intercepted
    ///    `Alt+F4`, breaking the OS window-close gesture.
    ///
    /// `Ctrl+Shift+C` was considered but reserved for a future
    /// copy-latest-command-output feature (the cross-terminal
    /// convention for that gesture). Layout caveat: on non-US
    /// keyboards the `OemSemicolon` virtual-key sits in a
    /// different physical position; remap when configurable
    /// keybindings ship in Phase 2.
    let private runCopyLatestLog (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runCopyLatestLog"
        log.LogInformation("Ctrl+Shift+; pressed — copying active log to clipboard.")
        match loggerSink with
        | None ->
            window.TerminalSurface.Announce(
                "Logging is not initialised yet; nothing to copy.",
                ActivityIds.error)
        | Some sink ->
            // Stage 7-followup PR-G — fix dispatcher deadlock that
            // wedged the WPF window when this hotkey ran while NVDA
            // had a long readout queued.
            //
            // The previous implementation called
            // `sink.FlushPending(500).Result` on the dispatcher.
            // `FlushPending` is `task { ... let! winner =
            // Task.WhenAny(...) ... }`; the `let!` captures the
            // WPF dispatcher's `SynchronizationContext`. When the
            // dispatcher thread calls `.Result`, it blocks waiting
            // for the task. The task's continuation needs to
            // resume on the captured context — which is the
            // dispatcher itself. Permanent deadlock; the 500ms
            // timeout never fires because the timeout's
            // continuation can't run on the wedged dispatcher.
            // Empirical confirmation: 2026-05-03 NVDA pass log
            // showed `Ctrl+Shift+;` entry-log fired at 19:13:51.861
            // followed by zero subsequent dispatcher events for
            // 2.5+ minutes (heartbeats kept firing on the
            // background timer, confirming the runtime was alive
            // but the WPF dispatcher was stuck).
            //
            // Additional concern: `Clipboard.SetText` requires the
            // STA apartment AND can hang on contention with NVDA's
            // clipboard hooks / antivirus / clipboard managers.
            // Both issues are fixed by running the whole copy
            // operation off the dispatcher in a Task: FlushPending
            // is awaited normally (no `.Result`), the clipboard
            // SetText runs on a dedicated STA thread with a 3s
            // timeout, and the announcement dispatches back to the
            // WPF thread on completion. The hotkey handler returns
            // immediately; the dispatcher never blocks.
            let _ =
                task {
                    try
                        let! drained = sink.FlushPending(500)
                        if not drained then
                            log.LogInformation(
                                "FlushPending timed out after 500ms; clipboard may be missing entries enqueued in the last few hundred ms.")
                        let path = sink.ActiveLogPath
                        if not (System.IO.File.Exists path) then
                            let action () =
                                window.TerminalSurface.Announce(
                                    "Active log file does not exist yet; press a key or wait for an event first.",
                                    ActivityIds.error)
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                        else
                            // FileShare.ReadWrite matches the
                            // FileLogger writer's open mode (the
                            // writer holds the file with FileAccess.Write
                            // + FileShare.ReadWrite, so a reader
                            // with FileShare.ReadWrite is granted
                            // by the OS).
                            let content =
                                use stream =
                                    new System.IO.FileStream(
                                        path,
                                        System.IO.FileMode.Open,
                                        System.IO.FileAccess.Read,
                                        System.IO.FileShare.ReadWrite)
                                use reader =
                                    new System.IO.StreamReader(
                                        stream, System.Text.Encoding.UTF8)
                                reader.ReadToEnd()
                            // System.Windows.Clipboard.SetText
                            // requires STA apartment. Thread-pool
                            // threads are MTA, so we spin up a
                            // dedicated STA thread for this single
                            // operation. Bounded by a 3s timeout
                            // so clipboard contention can't hang
                            // the workflow indefinitely.
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
                                        "Copied active log to clipboard. Path={Path} Bytes={Bytes}",
                                        path, bytes)
                                    sprintf
                                        "Log copied to clipboard. %d bytes; ready to paste."
                                        bytes,
                                    ActivityIds.diagnostic
                                else
                                    log.LogWarning(
                                        "Clipboard.SetText timed out or failed after 3s; clipboard may not contain log content.")
                                    "Clipboard copy timed out. Try again, or open the logs folder via Ctrl+Shift+L.",
                                    ActivityIds.error
                            let action () =
                                window.TerminalSurface.Announce(msg, activityId)
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                    with ex ->
                        let safe = AnnounceSanitiser.sanitise ex.Message
                        log.LogError(ex, "Failed to copy active log to clipboard.")
                        let action () =
                            window.TerminalSurface.Announce(
                                sprintf "Could not copy log: %s" safe,
                                ActivityIds.error)
                        try
                            do! window.Dispatcher.InvokeAsync(Action(action)).Task
                        with _ -> ()
                }
            ()

    /// Wire `Ctrl+Shift+;` to trigger `runCopyLatestLog`.
    let private setupCopyLatestLogKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("CopyLatestLog", typeof<MainWindow>)
        let gesture = KeyGesture(Key.OemSemicolon, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> runCopyLatestLog window)))
        |> ignore

    /// Wire `Ctrl+Shift+L` to trigger `runOpenLogs`. Same
    /// pattern as the other reserved hotkeys above.
    let private setupOpenLogsKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("OpenLogs", typeof<MainWindow>)
        let gesture = KeyGesture(Key.L, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
                CommandBinding(
                    cmd,
                    ExecutedRoutedEventHandler(fun _ _ -> runOpenLogs window)))
        |> ignore

    /// Stage 7-followup PR-E — toggle the active `FileLoggerSink`'s
    /// min-level between Information (default) and Debug, and
    /// announce the new state via NVDA. Lets the maintainer enable
    /// verbose debug logging from inside pty-speak without an
    /// env-var dance + relaunch (the previous workflow). Each
    /// press flips the level; the level is persistent for the
    /// lifetime of the session, but does NOT survive across
    /// launches (a future Phase-2 user-settings TOML can persist
    /// it if desired).
    ///
    /// The toggle event itself logs at `Information` level so the
    /// audit trail captures every transition regardless of which
    /// state we just left (Information passes both filters).
    /// Announcement uses `ActivityIds.logToggle` so users can
    /// configure NVDA's notification processing for diagnostic-
    /// config announcements separately from streaming output.
    let private runToggleDebugLog (window: MainWindow) : unit =
        match loggerSink with
        | None ->
            let log = Logger.get "Terminal.App.Program.runToggleDebugLog"
            log.LogWarning(
                "Ctrl+Shift+G pressed but loggerSink is None; toggle skipped.")
            window.TerminalSurface.Announce(
                "Logger not initialised; toggle skipped.",
                ActivityIds.error)
        | Some sink ->
            let log = Logger.get "Terminal.App.Program.runToggleDebugLog"
            let next =
                if sink.MinLevel = LogLevel.Debug then LogLevel.Information
                else LogLevel.Debug
            sink.SetMinLevel(next)
            log.LogInformation(
                "Ctrl+Shift+G pressed; FileLogger min-level toggled to {NewLevel}.",
                next)
            let cue =
                match next with
                | LogLevel.Debug -> "Debug logging on."
                | _ -> "Debug logging off."
            window.TerminalSurface.Announce(cue, ActivityIds.logToggle)

    /// Wire `Ctrl+Shift+G` to trigger `runToggleDebugLog`. Same
    /// pattern as the other reserved hotkeys above. The gesture
    /// is reserved in `TerminalView.AppReservedHotkeys` so Stage 6's
    /// `OnPreviewKeyDown` filter doesn't mark it `Handled = true`
    /// before WPF's `InputBindings` machinery can fire.
    let private setupToggleDebugLogKeybinding (window: MainWindow) : unit =
        let cmd = RoutedCommand("ToggleDebugLog", typeof<MainWindow>)
        let gesture = KeyGesture(Key.G, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> runToggleDebugLog window)))
        |> ignore

    /// Stage 7-followup PR-F — wire `Ctrl+Shift+H` to a caller-
    /// supplied health-check callback. The callback is a closure
    /// constructed inside `compose ()` that reads compose-local
    /// state (host, channel queue depths, last-byte timestamp,
    /// log level) and announces a one-line summary via NVDA.
    /// Same closure-passing pattern as
    /// `setupShellSwitchKeybindings` (which takes
    /// `ShellId -> unit`); keeps this setup function pure
    /// boilerplate.
    let private setupHealthCheckKeybinding
            (window: MainWindow)
            (run: unit -> unit) : unit =
        let cmd = RoutedCommand("HealthCheck", typeof<MainWindow>)
        let gesture = KeyGesture(Key.H, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> run ())))
        |> ignore

    /// Stage 7-followup PR-F — wire `Ctrl+Shift+B` to a caller-
    /// supplied incident-marker callback. The callback writes a
    /// clear "=== INCIDENT MARKER {timestamp} ===" line into the
    /// active log + announces. Replaces the env-var-and-relaunch
    /// debug-capture workflow with three keystrokes
    /// (`Ctrl+Shift+G`, `Ctrl+Shift+B`, `Ctrl+Shift+;`) entirely
    /// inside pty-speak.
    let private setupIncidentMarkerKeybinding
            (window: MainWindow)
            (run: unit -> unit) : unit =
        let cmd = RoutedCommand("IncidentMarker", typeof<MainWindow>)
        let gesture = KeyGesture(Key.B, ModifierKeys.Control ||| ModifierKeys.Shift)
        window.InputBindings.Add(KeyBinding(cmd, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                cmd,
                ExecutedRoutedEventHandler(fun _ _ -> run ())))
        |> ignore

    /// Composition seam — Stage 4+ plugs Elmish.WPF and the UIA peer
    /// in here. For Stage 3b we just hold references to the long-lived
    /// pieces and ensure they're disposed on Application.Exit.
    let compose (app: Application) (window: MainWindow) : unit =
        // Wire up file-based logging FIRST, before anything else
        // can produce log calls. Default-on (Information level);
        // Phase 2 user-settings will surface the toggle. Logs go
        // to %LOCALAPPDATA%\PtySpeak\logs\pty-speak-{date}.log
        // with daily rolling and 7-day retention; the
        // `Ctrl+Shift+L` hotkey opens the folder.
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

        // Stage 11 — wire Ctrl+Shift+U to the Velopack auto-
        // update flow. Order doesn't matter relative to the
        // ConPTY spawn below, but we install it before the
        // window is loaded so the keybinding is live for the
        // user's first keypress.
        setupAutoUpdateKeybinding window

        // Wire Ctrl+Shift+D to launch the bundled process-cleanup
        // diagnostic script in a separate PowerShell window.
        // Same install-before-window-load reasoning as above.
        setupDiagnosticKeybinding window

        // Wire Ctrl+Shift+R to open the GitHub "draft a new
        // release" form in the user's default browser.
        setupNewReleaseKeybinding window

        // Wire Ctrl+Shift+L to open the logs folder in File Explorer.
        setupOpenLogsKeybinding window

        // Wire Ctrl+Shift+; to copy the active session's log file
        // contents to the clipboard. Useful for sending the log
        // to a maintainer without navigating Explorer.
        setupCopyLatestLogKeybinding window

        // Stage 7-followup PR-E — wire Ctrl+Shift+G to toggle the
        // FileLogger min-level (Information ↔ Debug) at runtime,
        // with an audible NVDA announcement of the new state. Lets
        // the maintainer enable verbose debug logging without the
        // env-var-and-relaunch dance.
        setupToggleDebugLogKeybinding window

        // Direct dispatch via TerminalView.OnPreviewKeyDown is
        // kept as a defence-in-depth path because Window-level
        // KeyBinding routing for custom FrameworkElements has
        // been observed to flake (the Ctrl+V family of bugs).
        // Both paths are wired; whichever fires first wins.
        // Ctrl+Shift+; is a plain Ctrl+Shift gesture so no
        // SystemKey unwrap is needed in the filter chain — the
        // PR #108 SystemKey filter was removed in this PR
        // because it intercepted Alt+F4.
        window.TerminalSurface.SetCopyLogToClipboardHandler(
            Action(fun () -> runCopyLatestLog window))

        // Stage 5 — two-channel pipeline:
        //
        //   parser thread → notificationChannel (256, DropOldest)
        //                       ↓
        //                 Coalescer.runLoop (one Task)
        //                       ↓
        //                 coalescedChannel (16, Wait)
        //                       ↓
        //                 drain task → window.TerminalSurface.Announce(msg, activityId)
        //
        // The DropOldest source channel means a flooding parser
        // can't grow the backlog without bound; the coalescer
        // applies debounce + hash dedup + spinner suppression
        // and emits at most one CoalescedNotification per
        // ~200ms window. The downstream channel is small +
        // Wait because the drain marshals to the WPF
        // dispatcher per item and we want backpressure on
        // pathological emit rates rather than dropping
        // already-coalesced notifications.
        let notificationChannel =
            let opts =
                System.Threading.Channels.BoundedChannelOptions(256,
                    FullMode =
                        System.Threading.Channels.BoundedChannelFullMode.DropOldest)
            System.Threading.Channels.Channel.CreateBounded<ScreenNotification>(opts)

        let coalescedChannel =
            let opts =
                System.Threading.Channels.BoundedChannelOptions(16,
                    FullMode =
                        System.Threading.Channels.BoundedChannelFullMode.Wait)
            System.Threading.Channels.Channel.CreateBounded<Coalescer.CoalescedNotification>(opts)

        // Bridge Screen.ModeChanged events into the parser-side
        // channel so the coalescer can use them as flush
        // barriers (alt-screen swap, etc.) and pass them
        // through as ModeBarrier announcements. The screen
        // fires this AFTER releasing its internal lock, so
        // pushing to a Channel here is non-blocking and
        // deadlock-free.
        screen.ModeChanged.Add(fun (flag, value) ->
            notificationChannel.Writer.TryWrite(ModeChanged (flag, value)) |> ignore)

        // Start the coalescer with the SHARED cts.Token so
        // shutdown cancels reader, coalescer, and drain in
        // unison. Production passes TimeProvider.System;
        // unit tests inject FakeTimeProvider directly into
        // Coalescer.processRowsChanged / onTimerTick.
        let _ =
            Coalescer.runLoop
                notificationChannel.Reader
                coalescedChannel.Writer
                screen
                TimeProvider.System
                cts.Token

        // Drain the coalesced channel onto the WPF dispatcher,
        // calling `TerminalSurface.Announce(message, activityId)`
        // per coalesced item. The activityId vocabulary is
        // defined in `Terminal.Core.ActivityIds`; NVDA users
        // can configure per-tag handling (e.g. quieter speech
        // for `pty-speak.update` install events vs.
        // `pty-speak.output` streaming text).
        let _ =
            Task.Run(fun () ->
                task {
                    let drainLog = Logger.get "Terminal.App.Program.drain"
                    try
                        let reader = coalescedChannel.Reader
                        let mutable keepGoing = true
                        while keepGoing && not cts.Token.IsCancellationRequested do
                            let! got = reader.WaitToReadAsync(cts.Token).AsTask()
                            if not got then
                                keepGoing <- false
                            else
                                let mutable peek =
                                    Unchecked.defaultof<Coalescer.CoalescedNotification>
                                while reader.TryRead(&peek) do
                                    let msg, activityId =
                                        match peek with
                                        | Coalescer.OutputBatch text ->
                                            // Stage 7-followup PR-H — announce-length
                                            // stopgap. The 500-character cap below is
                                            // ARBITRARY: empirical NVDA pass 2026-05-03
                                            // showed `dir`/`set`-class output produced
                                            // 1316/1347-character announces taking
                                            // 30-45 seconds of NVDA speech each, making
                                            // the terminal effectively unusable. 500
                                            // chars is approximately 15-20 seconds —
                                            // tolerable upper bound balanced against
                                            // information loss at the cut. Tracked in
                                            // GitHub issue #139 for revisiting the
                                            // threshold once the next NVDA pass yields
                                            // real-feel data, AND for surfacing this
                                            // as a user-configurable setting (catalog
                                            // entry in `docs/USER-SETTINGS.md`).
                                            //
                                            // The proper architectural fix is the
                                            // Output framework cycle's Stream profile
                                            // (`docs/PROJECT-PLAN-2026-05.md` Part 3.2)
                                            // — suffix-diff append-only emission
                                            // eliminates the verbose-readback at its
                                            // source rather than capping the output.
                                            // Delete this cap + the footer cue when
                                            // the Stream profile ships.
                                            let limit = 500
                                            let capped =
                                                if text.Length <= limit then text
                                                else
                                                    let head = text.Substring(0, limit)
                                                    let extra = text.Length - limit
                                                    sprintf
                                                        "%s ...announcement truncated; %d more characters available — press Ctrl+Shift+; to copy full log."
                                                        head
                                                        extra
                                            capped, ActivityIds.output
                                        | Coalescer.ErrorPassthrough s ->
                                            sprintf "Terminal parser error: %s" s,
                                            ActivityIds.error
                                        | Coalescer.ModeBarrier _ ->
                                            // Stage 5 ships an empty string for
                                            // mode barriers — Stage 6 may replace
                                            // this with a verbosity-aware
                                            // description ("entered alt-screen",
                                            // "DECCKM application mode", etc.).
                                            "", ActivityIds.mode
                                    if msg <> "" then
                                        // Streaming-path instrumentation
                                        // at Debug — same rationale as
                                        // the reader and coalescer
                                        // entries: production stays
                                        // silent, env override flips on
                                        // for diagnosis. Metadata only
                                        // (activityId + length); never
                                        // the message text.
                                        drainLog.LogDebug(
                                            "Drain → Announce. ActivityId={ActivityId} MsgLen={MsgLen}",
                                            activityId, msg.Length)
                                        let action () =
                                            window.TerminalSurface.Announce(msg, activityId)
                                        let! _ =
                                            window.Dispatcher
                                                .InvokeAsync(Action(action))
                                                .Task
                                        ()
                                    else
                                        drainLog.LogDebug(
                                            "Drain skipped empty msg. ActivityId={ActivityId}",
                                            activityId)
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        // Post-Stage-6 diagnostic safety net.
                        // Previously this was `| _ -> ()` — any
                        // exception killed the drain task silently
                        // and streaming announcements stopped
                        // forever with no clue why. Surfacing the
                        // exception via one final
                        // `Announce(..., pty-speak.error)` lets a
                        // user (or maintainer) hear that something
                        // went wrong before the task exits.
                        // Sanitise the message through SR-2's
                        // chokepoint so PTY-originated control
                        // bytes can't reach NVDA verbatim.
                        try
                            log.LogError(
                                ex,
                                "Drain task crashed; streaming announcements halted.")
                            let safe =
                                AnnounceSanitiser.sanitise ex.Message
                            let action () =
                                window.TerminalSurface.Announce(
                                    sprintf
                                        "Drain task exception: %s"
                                        safe,
                                    ActivityIds.error)
                            window.Dispatcher.Invoke(Action(action))
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
            let requested =
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
                let notifDepth = notificationChannel.Reader.Count
                let coalDepth = coalescedChannel.Reader.Count
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
                        "%s %s shell, PID %d (%s), log level %s. Reader last byte %.0f ms ago. Notification queue %d of 256. Coalesced queue %d of 16."
                        verdict
                        currentShell.DisplayName
                        pid
                        aliveStr
                        levelStr
                        staleness
                        notifDepth
                        coalDepth
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

        // Stage 7-followup PR-F — wire Ctrl+Shift+H and Ctrl+Shift+B
        // through the standard reserved-hotkey machinery. Both
        // closures capture compose-local state (lastReadUtc,
        // hostHandle, channels, chosenShell) so they're built
        // here rather than as module-level handlers like the
        // Ctrl+Shift+G toggle (which only needs loggerSink).
        setupHealthCheckKeybinding window runHealthCheck
        setupIncidentMarkerKeybinding window runIncidentMarker

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
                let notifDepth = notificationChannel.Reader.Count
                let coalDepth = coalescedChannel.Reader.Count
                log.LogInformation(
                    "Heartbeat. Shell={Shell} Pid={Pid} Alive={Alive} Level={Level} LastReadAgoMs={Staleness:F0} NotifQueue={Notif}/{NotifCap} CoalQueue={Coal}/{CoalCap}",
                    currentShell.DisplayName,
                    pid,
                    alive,
                    level,
                    staleness,
                    notifDepth,
                    256,
                    coalDepth,
                    16)
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
                    notificationChannel.Writer
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
                    notificationChannel.Writer.TryWrite(
                        ParserError "ConPTY child process failed to start.")
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
        // keypress away.
        //
        // Pattern matches `setupAutoUpdateKeybinding` etc.; same
        // window-level KeyBinding + RoutedCommand + CommandBinding
        // triple. The keys are listed in
        // `TerminalView.AppReservedHotkeys` so OnPreviewKeyDown
        // doesn't mark them Handled before InputBindings can fire.
        let setupShellSwitchKeybindings (window: MainWindow)
                                        (switchTo: ShellRegistry.ShellId -> unit) : unit =
            let bind (name: string) (key: Key) (target: ShellRegistry.ShellId) : unit =
                let routed = RoutedCommand(name, typeof<MainWindow>)
                let gesture = KeyGesture(key, ModifierKeys.Control ||| ModifierKeys.Shift)
                window.InputBindings.Add(KeyBinding(routed, gesture)) |> ignore
                window.CommandBindings.Add(
                    CommandBinding(
                        routed,
                        ExecutedRoutedEventHandler(fun _ _ -> switchTo target)))
                |> ignore
            bind "SwitchToCmdShell" Key.D1 ShellRegistry.Cmd
            bind "SwitchToPowerShellShell" Key.D2 ShellRegistry.PowerShell
            bind "SwitchToClaudeShell" Key.D3 ShellRegistry.Claude

        setupShellSwitchKeybindings window switchToShell

        app.Exit.Add(fun _ ->
            try log.LogInformation("pty-speak exiting.") with _ -> ()
            try cts.Cancel() with _ -> ()
            // Stage 7-followup PR-F — stop the heartbeat timer
            // before the logger is disposed so the timer doesn't
            // fire one last entry into a closed channel and log a
            // confusing exception.
            try heartbeatTimer.Dispose() with _ -> ()
            // Complete both writers so the coalescer and drain
            // tasks exit cleanly when their channels run dry.
            try notificationChannel.Writer.TryComplete() |> ignore with _ -> ()
            try coalescedChannel.Writer.TryComplete() |> ignore with _ -> ()
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
