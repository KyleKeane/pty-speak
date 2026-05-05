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
    let private bindHotkey
            (window: MainWindow)
            (cmd: HotkeyRegistry.AppCommand)
            (handler: unit -> unit)
            : unit =
        let hk = HotkeyRegistry.hotkeyOf cmd
        let routed = RoutedCommand(HotkeyRegistry.nameOf cmd, typeof<MainWindow>)
        let gesture =
            KeyGesture(
                translateHotkeyKey hk.Key,
                translateHotkeyModifiers hk.Modifiers)
        window.InputBindings.Add(KeyBinding(routed, gesture)) |> ignore
        window.CommandBindings.Add(
            CommandBinding(
                routed,
                ExecutedRoutedEventHandler(fun _ _ -> handler ())))
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
        let snapshot = enumerateShellProcesses ()
        let log = Logger.get "Terminal.App.Program.runDiagnostic"
        log.LogInformation(
            "Diagnostic snapshot. ProcessCounts={Snapshot}",
            snapshot)
        // Stage 8d-followup (2026-05-04) — earcon audio diagnostic.
        //
        // The original Ctrl+Shift+D ritual launched a PowerShell
        // script (`test-process-cleanup.ps1`) in a separate window
        // that ran a process-tree close-and-recheck. The script
        // had been consistently passing in NVDA validation, but
        // required closing pty-speak to complete — adding friction
        // to the diagnostic ritual. The PS1 launch is commented
        // out below per maintainer 2026-05-04. Restore by
        // uncommenting if orphan-detection diagnosis ever needs
        // it again; the PS1 file still ships in the installer.
        //
        // The new ritual plays each earcon in sequence with an
        // announce between, so the maintainer can verify the
        // earcon audio path by pressing Ctrl+Shift+D — no need to
        // trigger BEL or coloured shell output. Earcons play via
        // a direct `EarconPlayer.play` call (bypassing the
        // EarconChannel mute state); this lets the test verify
        // the audio output path even when earcons are muted via
        // Ctrl+Shift+M for normal use.
        window.TerminalSurface.Announce(
            sprintf
                "Diagnostic snapshot: %s. Testing earcons."
                snapshot,
            ActivityIds.diagnostic)
        // Helper: announce a label, wait for NVDA to read it,
        // play the earcon, wait for the tone to finish. Defined
        // outside the outer `task { ... }` block to keep the
        // outer state machine statically compilable (the F# 9
        // task CE flags `for tuple in list do { do! ... }`
        // patterns as `FS3511`; an explicit unroll with three
        // `do!` calls compiles cleanly).
        let announceAndPlay (label: string) (earconId: string) : Task =
            task {
                let action () =
                    window.TerminalSurface.Announce(
                        label,
                        ActivityIds.diagnostic)
                do! window.Dispatcher.InvokeAsync(Action(action)).Task
                // Wait for NVDA to finish reading the label
                // before the tone plays — otherwise the tone
                // overlaps the speech.
                do! Task.Delay(400)
                EarconPlayer.play
                    EarconPalette.defaultPalette
                    earconId
                // Wait for the tone to finish (max 150ms) so
                // the next label announce doesn't fire while
                // the tone is still audible.
                do! Task.Delay(500)
            }
        let _ =
            task {
                // Brief settle so the snapshot announce reaches
                // NVDA's speech queue before the first per-earcon
                // announce queues up behind it.
                do! Task.Delay(800)
                do! announceAndPlay "Bell ping." "bell-ping"
                do! announceAndPlay "Error tone." "error-tone"
                do! announceAndPlay "Warning tone." "warning-tone"
                let final () =
                    window.TerminalSurface.Announce(
                        "Earcon test complete.",
                        ActivityIds.diagnostic)
                do! window.Dispatcher.InvokeAsync(Action(final)).Task
                ()
            }
        // PowerShell script launch — commented out per maintainer
        // 2026-05-04. Original code preserved below for future
        // reactivation if needed.
        //
        // let scriptPath =
        //     System.IO.Path.Combine(
        //         System.AppContext.BaseDirectory,
        //         "test-process-cleanup.ps1")
        // if not (System.IO.File.Exists scriptPath) then
        //     window.TerminalSurface.Announce(
        //         sprintf
        //             "Cleanup script not found at %s."
        //             scriptPath,
        //         ActivityIds.diagnostic)
        // else
        //     let _ =
        //         task {
        //             do! Task.Delay(700)
        //             let action () =
        //                 try
        //                     let psi = System.Diagnostics.ProcessStartInfo()
        //                     psi.FileName <- "powershell.exe"
        //                     psi.Arguments <-
        //                         sprintf
        //                             "-ExecutionPolicy Bypass -NoExit -File \"%s\""
        //                             scriptPath
        //                     psi.UseShellExecute <- true
        //                     System.Diagnostics.Process.Start(psi) |> ignore
        //                 with ex ->
        //                     let safe = AnnounceSanitiser.sanitise ex.Message
        //                     window.TerminalSurface.Announce(
        //                         sprintf "Could not launch diagnostic: %s" safe,
        //                         ActivityIds.error)
        //             do! window.Dispatcher.InvokeAsync(Action(action)).Task
        //             ()
        //         }
        //     ()
        ()

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

    /// Stage 8d.1 — Ctrl+Shift+M handler. Toggles the
    /// process-wide earcon mute state via `EarconChannel.toggle`
    /// and announces the new state via NVDA. Reuses
    /// `ActivityIds.logToggle` for the announcement (same
    /// semantic family — toggling a diagnostic-config setting,
    /// like Ctrl+Shift+G's debug-logging toggle).
    let private runMuteEarcons (window: MainWindow) : unit =
        let log = Logger.get "Terminal.App.Program.runMuteEarcons"
        let nowMuted = EarconChannel.toggle ()
        log.LogInformation(
            "Ctrl+Shift+M pressed; earcons toggled. NowMuted={NowMuted}",
            nowMuted)
        let cue =
            if nowMuted then "Earcons muted."
            else "Earcons unmuted."
        window.TerminalSurface.Announce(cue, ActivityIds.logToggle)

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
        bindHotkey window HotkeyRegistry.CheckForUpdates (fun () -> runUpdateFlow window)
        bindHotkey window HotkeyRegistry.RunDiagnostic (fun () -> runDiagnostic window)
        bindHotkey window HotkeyRegistry.DraftNewRelease (fun () -> runOpenNewRelease window)
        bindHotkey window HotkeyRegistry.OpenLogsFolder (fun () -> runOpenLogs window)
        bindHotkey window HotkeyRegistry.CopyLatestLog (fun () -> runCopyLatestLog window)
        bindHotkey window HotkeyRegistry.ToggleDebugLog (fun () -> runToggleDebugLog window)
        bindHotkey window HotkeyRegistry.MuteEarcons (fun () -> runMuteEarcons window)

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

        // Phase A — display-pathway pipeline:
        //
        //   parser thread → notificationChannel (256, DropOldest)
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
        let notificationChannel =
            let opts =
                System.Threading.Channels.BoundedChannelOptions(256,
                    FullMode =
                        System.Threading.Channels.BoundedChannelFullMode.DropOldest)
            System.Threading.Channels.Channel.CreateBounded<ScreenNotification>(opts)

        // Bridge Screen.ModeChanged events into the parser-side
        // channel so the Stream profile can use them as flush
        // barriers (alt-screen swap, etc.). The screen fires
        // this AFTER releasing its internal lock, so pushing to
        // a Channel here is non-blocking and deadlock-free.
        screen.ModeChanged.Add(fun (flag, value) ->
            notificationChannel.Writer.TryWrite(ModeChanged (flag, value)) |> ignore)
        // Stage 8d.1 — bridge screen.Bell events into the
        // notification channel so the PathwayPump produces
        // OutputEvent.BellRang for the Earcon profile.
        screen.Bell.Add(fun () ->
            notificationChannel.Writer.TryWrite(ScreenNotification.Bell) |> ignore)

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
        // Phase A — active set: [ passThroughProfile;
        // earconProfile ]. The pass-through fans every event
        // to NVDA + FileLogger; EarconProfile additionally
        // claims BellRang for the bell-ping earcon. NvdaChannel
        // skips empty payloads (so BellRang's empty Payload
        // doesn't double-announce); FileLogger records every
        // event regardless (audit trail).
        OutputDispatcher.ProfileRegistry.setActiveProfileSet
            [ passThroughProfile; earconProfile ]

        // Phase A — per-shell pathway selection. v1 hardcoded
        // mapping; Phase B replaces with TOML config. The
        // pathway is recreated on every shell switch so its
        // internal state is fresh — even StreamPathway → cmd
        // → StreamPathway → PowerShell discards the cmd-session
        // baseline so PowerShell's first paint emits in full.
        let selectPathwayForShell
                (shellId: ShellRegistry.ShellId)
                : DisplayPathway.T =
            match shellId with
            | ShellRegistry.Cmd ->
                StreamPathway.create StreamPathway.defaultParameters
            | ShellRegistry.PowerShell ->
                StreamPathway.create StreamPathway.defaultParameters
            | ShellRegistry.Claude ->
                // Phase 2 will swap Claude to a ClaudeCodePathway
                // that interprets the alt-screen UI. Phase A
                // ships the streaming pathway so the verbose-
                // readback regression is fixed for all three
                // built-in shells.
                StreamPathway.create StreamPathway.defaultParameters

        // Initial pathway — StreamPathway covers all three v1
        // built-in shells (cmd / powershell / claude). The
        // mutable is reassigned by `switchToShell` below when
        // the user hot-switches; for the startup shell, the
        // initial value matches whatever shell `chosenShell`
        // resolves to (all three map to StreamPathway today).
        // When a future Phase 2 maps a shell to a different
        // pathway (e.g. ClaudeCodePathway), the startup-resolve
        // path will need to reselect after `chosenShell` is
        // determined.
        let mutable activePathway : DisplayPathway.T =
            StreamPathway.create StreamPathway.defaultParameters

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
        let handleRowsChanged () : unit =
            let seq, snapshot = screen.SnapshotRows(0, screen.Rows)
            let canonical = CanonicalState.create snapshot seq
            let emitted = activePathway.Consume canonical
            pumpLog.LogDebug(
                "PathwayPump RowsChanged → {Pathway}.Consume → {Count} events.",
                activePathway.Id,
                emitted.Length)
            dispatchPathwayEvents emitted
        let handleModeChanged (notification: ScreenNotification) : unit =
            let now = DateTimeOffset.UtcNow
            let flushed = activePathway.OnModeBarrier now
            pumpLog.LogDebug(
                "PathwayPump ModeChanged → {Pathway}.OnModeBarrier → {Count} flushed events.",
                activePathway.Id,
                flushed.Length)
            dispatchPathwayEvents flushed
            let barrier =
                OutputEventBuilder.fromScreenNotification notification
            OutputDispatcher.dispatch barrier
        let handleSimpleNotification (notification: ScreenNotification) : unit =
            let event = OutputEventBuilder.fromScreenNotification notification
            pumpLog.LogDebug(
                "PathwayPump → Dispatch. Semantic={Semantic} Priority={Priority}",
                event.Semantic, event.Priority)
            OutputDispatcher.dispatch event

        let _ =
            Task.Run(fun () ->
                task {
                    try
                        let reader = notificationChannel.Reader
                        let mutable keepGoing = true
                        while keepGoing && not cts.Token.IsCancellationRequested do
                            let! got = reader.WaitToReadAsync(cts.Token).AsTask()
                            if not got then
                                keepGoing <- false
                            else
                                let mutable peek =
                                    Unchecked.defaultof<ScreenNotification>
                                while reader.TryRead(&peek) do
                                    match peek with
                                    | RowsChanged _ -> handleRowsChanged ()
                                    | ModeChanged _ -> handleModeChanged peek
                                    | ParserError _
                                    | Bell -> handleSimpleNotification peek
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

        // PathwayTickPump — periodic timer for the active
        // pathway's trailing-edge flush. Replaces Stage 8b's
        // TickPump (which drove `OutputDispatcher.dispatchTick`
        // for the StreamProfile.Tick trailing-edge flush). The
        // 50ms cadence is the timer rate, NOT the debounce
        // window (200ms inside StreamPathway). Faster ticks =
        // lower worst-case latency on trailing-edge flush; the
        // cost is negligible (each Tick that returns [||] is
        // a pure-function call).
        let _ =
            Task.Run(fun () ->
                task {
                    try
                        use tickTimer =
                            new System.Threading.PeriodicTimer(
                                TimeSpan.FromMilliseconds 50.0)
                        let mutable keepGoing = true
                        while keepGoing && not cts.Token.IsCancellationRequested do
                            let! tickFired = tickTimer.WaitForNextTickAsync(cts.Token).AsTask()
                            if not tickFired then
                                keepGoing <- false
                            else
                                let now = DateTimeOffset.UtcNow
                                let emitted = activePathway.Tick now
                                if emitted.Length > 0 then
                                    dispatchPathwayEvents emitted
                                // Layer 4 also still receives Tick
                                // for any profile that uses it (no
                                // v1 profile does today, but the
                                // contract is preserved for forward-
                                // compat).
                                OutputDispatcher.dispatchTick now
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
                            // for every event; only the trailing-
                            // edge flush stops). The error is
                            // logged for `Ctrl+Shift+;` post-hoc
                            // diagnosis.
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

        // Phase A — align the active pathway with the resolved
        // startup shell. v1 maps cmd / powershell / claude all
        // to StreamPathway, so this swap is a no-op today (the
        // mutable was already initialised to StreamPathway).
        // Phase 2 will map Claude to a ClaudeCodePathway; this
        // swap ensures the right pathway is active when the
        // PathwayPump processes the first ScreenNotification.
        try activePathway.Reset () with _ -> ()
        activePathway <- selectPathwayForShell chosenShell.Id

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

        // Stage 7-followup PR-F — wire Ctrl+Shift+H and Ctrl+Shift+B
        // through the unified `bindHotkey` helper (PR-O). Both
        // closures capture compose-local state (lastReadUtc,
        // hostHandle, channels, currentShell) so they're built
        // here rather than as module-level handlers like the
        // Ctrl+Shift+G toggle (which only needs loggerSink).
        bindHotkey window HotkeyRegistry.HealthCheck runHealthCheck
        bindHotkey window HotkeyRegistry.IncidentMarker runIncidentMarker

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
                                            let seq, snapshot =
                                                screen.SnapshotRows(0, screen.Rows)
                                            let canonical =
                                                CanonicalState.create snapshot seq
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
        bindHotkey window HotkeyRegistry.SwitchToCmd (fun () -> switchToShell ShellRegistry.Cmd)
        bindHotkey window HotkeyRegistry.SwitchToPowerShell (fun () -> switchToShell ShellRegistry.PowerShell)
        bindHotkey window HotkeyRegistry.SwitchToClaude (fun () -> switchToShell ShellRegistry.Claude)

        app.Exit.Add(fun _ ->
            try log.LogInformation("pty-speak exiting.") with _ -> ()
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
            try notificationChannel.Writer.TryComplete() |> ignore with _ -> ()
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
