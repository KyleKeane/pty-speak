namespace PtySpeak.App

open System
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Input
open System.Windows.Threading
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
            (ct: CancellationToken) : Task =
        Task.Run(fun () ->
            task {
                try
                    while not ct.IsCancellationRequested do
                        let! chunk = host.Stdout.ReadAsync(ct).AsTask()
                        if chunk.Length > 0 then
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
                                let _ = notifications.TryWrite(RowsChanged [])
                                ()
                with
                | :? OperationCanceledException -> ()
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
    let private runDiagnostic (window: MainWindow) : unit =
        let scriptPath =
            System.IO.Path.Combine(
                System.AppContext.BaseDirectory,
                "test-process-cleanup.ps1")
        if not (System.IO.File.Exists scriptPath) then
            window.TerminalSurface.Announce(
                sprintf
                    "Diagnostic script not found at %s. Re-install pty-speak or report this as a packaging regression."
                    scriptPath)
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
            // Fix: announce a SHORT cue ("Launching diagnostic.") that
            // NVDA can fully read in well under the focus-grab latency,
            // wait ~700ms, then start the process. The PowerShell
            // window's title takes over from there — which is the
            // natural way for screen-reader users to confirm the new
            // window arrived.
            // TODO Phase 2: the 700ms delay should come from a TOML
            // setting alongside the Stage 5 coalescer constants.
            window.TerminalSurface.Announce(
                "Launching diagnostic.",
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

    /// Composition seam — Stage 4+ plugs Elmish.WPF and the UIA peer
    /// in here. For Stage 3b we just hold references to the long-lived
    /// pieces and ensure they're disposed on Application.Exit.
    let compose (app: Application) (window: MainWindow) : unit =
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
                                            text, ActivityIds.output
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
                                        let action () =
                                            window.TerminalSurface.Announce(msg, activityId)
                                        let! _ =
                                            window.Dispatcher
                                                .InvokeAsync(Action(action))
                                                .Task
                                        ()
                    with
                    | :? OperationCanceledException -> ()
                    | _ -> ()
                } :> Task)

        let cfg : PtyConfig =
            { Cols = int16 ScreenCols
              Rows = int16 ScreenRows
              CommandLine = "cmd.exe" }

        let mutable hostHandle : ConPtyHost option = None

        // Defer ConPTY spawn until the window has loaded so any startup
        // failure can surface in the UI rather than crashing during
        // Application.OnStartup.
        window.Loaded.Add(fun _ ->
            match ConPtyHost.start cfg with
            | Error _ ->
                // Publish a ParserError so the user hears about
                // ConPTY spawn failures via NVDA rather than
                // staring at a silent empty terminal.
                let _ =
                    notificationChannel.Writer.TryWrite(
                        ParserError "ConPTY child process failed to start.")
                ()
            | Ok host ->
                hostHandle <- Some host
                let _ =
                    startReaderLoop
                        window.Dispatcher
                        host
                        parser
                        screen
                        window.TerminalSurface
                        notificationChannel.Writer
                        cts.Token
                // Stage 6 PR-B — wire keyboard input + paste + focus
                // events + window-resize through to the new host.
                // SetPtyHost takes two callbacks because Views/
                // intentionally doesn't reference Terminal.Pty (would
                // break the F#-first / WPF-only-at-the-edge boundary).
                // The view invokes them on the WPF dispatcher thread,
                // which is also the only thread that touches the
                // ConPTY stdin pipe (single-writer discipline).
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
                ())

        app.Exit.Add(fun _ ->
            try cts.Cancel() with _ -> ()
            // Complete both writers so the coalescer and drain
            // tasks exit cleanly when their channels run dry.
            try notificationChannel.Writer.TryComplete() |> ignore with _ -> ()
            try coalescedChannel.Writer.TryComplete() |> ignore with _ -> ()
            match hostHandle with
            | Some h -> (h :> IDisposable).Dispose()
            | None -> ()
            cts.Dispose())

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
