namespace PtySpeak.App

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open PtySpeak.Views
open Terminal.Audio
open Terminal.Core
open Terminal.Pty

/// Ctrl+Shift+D diagnostic battery.
///
/// **What this is.** A self-test harness that runs on demand
/// from the Ctrl+Shift+D hotkey. Captures the current process
/// snapshot, replays the three earcons, then runs a per-shell
/// command battery that exercises the canonical pipeline end-
/// to-end (write bytes via `ConPtyHost.WriteBytes`, observe the
/// `OutputEvent`s the active pathway emits, compare against
/// expected). Writes a structured artefact log file with PASS /
/// FAIL per test + a summary, then copies the log to the
/// clipboard so the maintainer can paste it into chat without
/// any extra steps.
///
/// **Why.** Phase A.2 + the changed-rows hotfix shipped on the
/// back of one round of manual NVDA validation per behaviour.
/// Each round costs the maintainer minutes of "open shell, run
/// command, listen, describe what NVDA said". As pathway
/// behaviour grows (suffix-diff, ReplPathway, content-type
/// triggers), the validation cost grows linearly. This battery
/// reduces the loop to "press one hotkey, paste log".
///
/// **Architecture.** Three pieces:
///
///   * `DiagnosticLogWriter` — short-lived per-run file writer.
///     `AutoFlush = true` + `FileShare.ReadWrite` mirror the
///     FileLogger crash-safety paradigm: every line is on disk
///     before the next is queued, so a crash mid-battery still
///     leaves a tail file with everything up to the crash.
///   * `DiagnosticEventTap` — wraps `OutputDispatcher.installEventTap`
///     to capture `OutputEvent`s during a known time window.
///     Drained synchronously after each test; cross-thread-safe
///     via `ConcurrentQueue<T>`.
///   * `runFullBattery` — orchestrator. Opens the writer, runs
///     universal + per-shell tests, closes the writer, copies
///     to clipboard, announces the summary.
///
/// **Threading.** The hotkey handler runs on the WPF dispatcher
/// thread. `runFullBattery` posts a `Task.Run` body so the
/// dispatcher isn't blocked for the ~10s battery duration; each
/// `WriteBytes` and `Announce` marshals back to the dispatcher
/// via `window.Dispatcher.InvokeAsync` (matching the existing
/// keystroke-write contract per `ConPtyHost.fs:49-56`).
[<RequireQualifiedAccess>]
module Diagnostics =

    // ---------------------------------------------------------------
    // Format helpers
    // ---------------------------------------------------------------

    /// Map `LogLevel` to the three-letter label that FileLogger
    /// uses (`INF`, `DBG`, `ERR`, ...). Inlined here rather than
    /// extracted from FileLogger because `FileLogger.formatEntry`
    /// is class-private and exposing it would require a wider
    /// refactor — eight lines of duplication isn't worth that.
    let private levelLabel (level: LogLevel) : string =
        match level with
        | LogLevel.Trace -> "TRC"
        | LogLevel.Debug -> "DBG"
        | LogLevel.Information -> "INF"
        | LogLevel.Warning -> "WRN"
        | LogLevel.Error -> "ERR"
        | LogLevel.Critical -> "CRT"
        | _ -> "???"

    // ---------------------------------------------------------------
    // DiagnosticLogWriter — short-lived per-run file writer.
    // ---------------------------------------------------------------

    /// A line-oriented log writer whose crash-safety story matches
    /// `FileLogger`: every WriteLine is flushed to disk before
    /// returning, the FileStream is opened with
    /// `FileShare.ReadWrite` so external readers (NVDA, Notepad)
    /// can open the file mid-write, and `Dispose` flushes one
    /// final time before closing. One instance per diagnostic run.
    type DiagnosticLogWriter (path: string) =
        do
            // Make sure the parent directory exists. The day-folder
            // may be fresh on the first diagnostic of the day.
            // `Path.GetDirectoryName` is nullable per F# 9 nullness
            // rules (see CONTRIBUTING.md "F# 9 nullness annotations"
            // — same list as `Environment.GetEnvironmentVariable`,
            // `Path.GetFileName`, etc.); narrow before passing to
            // `Directory.CreateDirectory`.
            match Path.GetDirectoryName(path) with
            | null -> ()
            | dir when String.IsNullOrEmpty dir -> ()
            | dir ->
                Directory.CreateDirectory(dir) |> ignore
        let stream =
            new FileStream(
                path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite, bufferSize = 4096,
                options = FileOptions.None)
        let writer = new StreamWriter(stream, Encoding.UTF8)
        do writer.AutoFlush <- true

        /// Resolved file path. Useful for the announce or for
        /// later `File.ReadAllText` to load the log content for
        /// the clipboard copy.
        member _.Path = path

        /// Append one log line. Format mirrors FileLogger:
        /// `yyyy-MM-ddTHH:mm:ss.fffZ [LEVEL] [Category] message`.
        /// Multi-line `message` is preserved as-is; downstream
        /// tooling that expects one-entry-per-line should split
        /// on the timestamp prefix.
        member _.WriteLine (level: LogLevel) (category: string) (message: string) : unit =
            let ts =
                DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            let line = sprintf "%s [%s] [%s] %s" ts (levelLabel level) category message
            writer.WriteLine(line)

        /// Flush + close. Idempotent — `Dispose` may be called
        /// twice with no ill effect (StreamWriter handles that).
        interface IDisposable with
            member _.Dispose () =
                try writer.Flush() with _ -> ()
                try writer.Close() with _ -> ()

    // ---------------------------------------------------------------
    // DiagnosticEventTap — observes events flowing through the
    // dispatcher during a known time window.
    // ---------------------------------------------------------------

    /// Captures every `OutputEvent` dispatched between `Enable`
    /// and the matching `DisableAndDrain`. Backed by a
    /// `ConcurrentQueue<T>` because `OutputDispatcher.dispatch`
    /// runs on the PathwayPump worker thread while the diagnostic
    /// orchestrator drains from the test-loop thread.
    type DiagnosticEventTap () =
        let queue = ConcurrentQueue<OutputEvent>()
        let mutable subscription : IDisposable option = None

        /// Begin capturing. Must be paired with `DisableAndDrain`
        /// before the next test (otherwise events leak across the
        /// boundary). Idempotent — re-enabling without disabling
        /// disposes the previous subscription first.
        member this.Enable () : unit =
            this.DisableAndDrain () |> ignore
            subscription <-
                Some (OutputDispatcher.installEventTap (fun ev -> queue.Enqueue(ev)))

        /// Stop capturing and return everything captured since
        /// `Enable`. Calling this without an active subscription
        /// returns an empty array.
        member _.DisableAndDrain () : OutputEvent[] =
            (match subscription with
             | Some s ->
                 try s.Dispose() with _ -> ()
                 subscription <- None
             | None -> ())
            let buf = ResizeArray<OutputEvent>()
            let mutable item = Unchecked.defaultof<OutputEvent>
            while queue.TryDequeue(&item) do buf.Add(item)
            buf.ToArray()

    // ---------------------------------------------------------------
    // Process snapshot — moved from Program.fs so it lives next
    // to its only caller (the diagnostic).
    // ---------------------------------------------------------------

    /// Count `cmd.exe`, `powershell.exe`, `pwsh.exe`, `claude.exe`,
    /// and `Terminal.App.exe` in the live process list. Returns
    /// a screen-reader-friendly comma-separated string. Mirrors
    /// the in-app live counts the maintainer might cross-check
    /// against `tasklist | findstr` if they suspect a leak.
    let enumerateShellProcesses () : string =
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

    // ---------------------------------------------------------------
    // Test definitions
    // ---------------------------------------------------------------

    /// One battery test case. `SetupCommand` runs first if Some
    /// (no assertions; tap drained between setup and main); then
    /// `Command` runs and the captured events are checked against
    /// `MustInclude` / `MustNotInclude`. The setup field is what
    /// makes the "plain after red" regression-guard test
    /// expressible — the red command is setup, then the plain
    /// command is the assertion target.
    type DiagnosticTest =
        { Name: string
          Description: string
          SetupCommand: string option
          Command: string
          MustInclude: SemanticCategory[]
          MustNotInclude: SemanticCategory[] }

    /// Convert a UTF-8 command string + trailing CR into bytes
    /// suitable for `ConPtyHost.WriteBytes`. CR (`\r`, 0x0D) is
    /// the byte the WPF keyboard handler sends for the Enter key
    /// per `KeyEncoding.fs:204-208` — shells respond to it
    /// identically to a real Enter press.
    let private commandBytes (cmd: string) : byte[] =
        let bs = Encoding.UTF8.GetBytes(cmd)
        Array.append bs [| 0x0Duy |]

    /// PowerShell test set. Exercises plain output + the colour-
    /// detection paths added by Phase A.2 (PR #163) and refined
    /// by the changed-rows hotfix (PR #164).
    let private powerShellTests : DiagnosticTest[] =
        [|
            { Name = "T1.Plain"
              Description = "Plain Write-Host emits StreamChunk only."
              SetupCommand = None
              Command = "Write-Host PtySpeakDiagPlain"
              MustInclude = [| SemanticCategory.StreamChunk |]
              MustNotInclude = [| SemanticCategory.ErrorLine; SemanticCategory.WarningLine |] }
            { Name = "T2.Red"
              Description = "Red Write-Host emits StreamChunk + ErrorLine."
              SetupCommand = None
              Command = "Write-Host -ForegroundColor Red PtySpeakDiagRed"
              MustInclude = [| SemanticCategory.StreamChunk; SemanticCategory.ErrorLine |]
              MustNotInclude = [| SemanticCategory.WarningLine |] }
            { Name = "T3.Yellow"
              Description = "Yellow Write-Host emits StreamChunk + WarningLine."
              SetupCommand = None
              Command = "Write-Host -ForegroundColor Yellow PtySpeakDiagYellow"
              MustInclude = [| SemanticCategory.StreamChunk; SemanticCategory.WarningLine |]
              MustNotInclude = [| SemanticCategory.ErrorLine |] }
            { Name = "T4.PlainAfterRed"
              Description = "Plain after red emits StreamChunk only (PR #164 regression guard)."
              SetupCommand = Some "Write-Host -ForegroundColor Red PtySpeakDiagSetup"
              Command = "Write-Host PtySpeakDiagPlainAfter"
              MustInclude = [| SemanticCategory.StreamChunk |]
              MustNotInclude = [| SemanticCategory.ErrorLine; SemanticCategory.WarningLine |] }
            { Name = "T5.YellowAfterRed"
              Description = "Yellow after red emits WarningLine, not ErrorLine (PR #164 regression guard)."
              SetupCommand = Some "Write-Host -ForegroundColor Red PtySpeakDiagSetup2"
              Command = "Write-Host -ForegroundColor Yellow PtySpeakDiagYellowAfter"
              MustInclude = [| SemanticCategory.StreamChunk; SemanticCategory.WarningLine |]
              MustNotInclude = [| SemanticCategory.ErrorLine |] }
        |]

    /// Cmd test set — limited to plain echo. Cmd has minimal
    /// colour support; ANSI-escape colour testing is a separate
    /// follow-up.
    let private cmdTests : DiagnosticTest[] =
        [|
            { Name = "T1.Plain"
              Description = "Plain echo emits StreamChunk only."
              SetupCommand = None
              Command = "echo PtySpeakDiagPlain"
              MustInclude = [| SemanticCategory.StreamChunk |]
              MustNotInclude = [| SemanticCategory.ErrorLine; SemanticCategory.WarningLine |] }
        |]

    /// Pick the test set matching the active shell. Claude
    /// returns an empty array — non-deterministic interactive AI
    /// can't be exercised with fixed expectations.
    let private selectTestsForShell (shellId: ShellRegistry.ShellId) : DiagnosticTest[] =
        match shellId with
        | ShellRegistry.PowerShell -> powerShellTests
        | ShellRegistry.Cmd -> cmdTests
        | ShellRegistry.Claude -> [||]

    // ---------------------------------------------------------------
    // Quiescence detection
    // ---------------------------------------------------------------

    /// Wait until the screen sequence number stops advancing for
    /// `quiescenceMs` consecutive milliseconds, or until
    /// `timeoutMs` total milliseconds have elapsed (whichever
    /// comes first). Returns `(settled, elapsedMs)` —
    /// `settled = true` if quiescence was detected within the
    /// budget, `false` if the timeout fired.
    let private waitForQuiescence
            (resolveSeq: unit -> int64)
            (quiescenceMs: int)
            (timeoutMs: int)
            : Task<bool * int> =
        task {
            let pollMs = 50
            let started = DateTimeOffset.UtcNow
            let mutable lastSeq = resolveSeq ()
            let mutable lastChangeAt = started
            let mutable settled = false
            let mutable timedOut = false
            while not settled && not timedOut do
                do! Task.Delay(pollMs)
                let now = DateTimeOffset.UtcNow
                let curSeq = resolveSeq ()
                if curSeq <> lastSeq then
                    lastSeq <- curSeq
                    lastChangeAt <- now
                let sinceChange = (now - lastChangeAt).TotalMilliseconds
                let sinceStart = (now - started).TotalMilliseconds
                if sinceChange >= float quiescenceMs then
                    settled <- true
                elif sinceStart >= float timeoutMs then
                    timedOut <- true
            let elapsed =
                int (DateTimeOffset.UtcNow - started).TotalMilliseconds
            return settled, elapsed
        }

    // ---------------------------------------------------------------
    // Per-test runner
    // ---------------------------------------------------------------

    /// Hex dump of a byte array — `[57 72 69 74 65 ... 0D] (16 bytes)`.
    let private hexDump (bytes: byte[]) : string =
        let sb = StringBuilder()
        sb.Append('[') |> ignore
        let n = min 32 bytes.Length
        for i in 0 .. n - 1 do
            if i > 0 then sb.Append(' ') |> ignore
            sb.AppendFormat("{0:X2}", bytes.[i]) |> ignore
        if bytes.Length > n then sb.Append(" ...") |> ignore
        sb.AppendFormat("] ({0} bytes)", bytes.Length) |> ignore
        sb.ToString()

    /// Format an OutputEvent for the diagnostic log — keeps it to
    /// one line, payload truncated at 120 chars so a long stack
    /// trace doesn't blow up the log.
    let private formatEvent (ev: OutputEvent) : string =
        let payload = ev.Payload
        let truncated =
            if payload.Length <= 120 then payload
            else payload.Substring(0, 117) + "..."
        let safe = truncated.Replace('\n', ' ').Replace('\r', ' ')
        sprintf "%A(payload=\"%s\")" ev.Semantic safe

    /// Run one test. Issues setup (if any), drains tap, runs
    /// main command, waits for quiescence, drains tap, evaluates
    /// MustInclude / MustNotInclude, logs PASS / FAIL with full
    /// detail. Returns `true` for PASS.
    let private runOneTest
            (writer: DiagnosticLogWriter)
            (writeBytes: byte[] -> Task)
            (resolveSeq: unit -> int64)
            (test: DiagnosticTest)
            : Task<bool> =
        task {
            let cat = sprintf "Diagnostic.%s" test.Name
            writer.WriteLine LogLevel.Information cat
                (sprintf "BEGIN %s" test.Description)
            // Setup phase: write setup bytes, wait, drain tap
            // without checking. Fresh tap for the main phase.
            let tap = DiagnosticEventTap()
            match test.SetupCommand with
            | Some setupCmd ->
                let setupBytes = commandBytes setupCmd
                writer.WriteLine LogLevel.Information cat
                    (sprintf "SETUP %s" (hexDump setupBytes))
                tap.Enable()
                do! writeBytes setupBytes
                let! settled, elapsed =
                    waitForQuiescence resolveSeq 200 1500
                let setupEvents = tap.DisableAndDrain()
                writer.WriteLine LogLevel.Information cat
                    (sprintf "SETUP_RESULT settled=%b elapsedMs=%d events=%d"
                         settled elapsed setupEvents.Length)
            | None -> ()
            // Main phase
            let mainBytes = commandBytes test.Command
            writer.WriteLine LogLevel.Information cat
                (sprintf "WRITE %s" (hexDump mainBytes))
            tap.Enable()
            do! writeBytes mainBytes
            let! settled, elapsed =
                waitForQuiescence resolveSeq 200 1500
            let events = tap.DisableAndDrain()
            writer.WriteLine LogLevel.Information cat
                (sprintf "WAIT settled=%b elapsedMs=%d" settled elapsed)
            let semantics =
                events |> Array.map (fun ev -> ev.Semantic)
            writer.WriteLine LogLevel.Information cat
                (sprintf "EVENTS (%d): %s"
                     events.Length
                     (events
                      |> Array.map formatEvent
                      |> String.concat " | "))
            // Check inclusions
            let missing =
                test.MustInclude
                |> Array.filter (fun s -> not (Array.contains s semantics))
            // Check exclusions
            let unexpected =
                test.MustNotInclude
                |> Array.filter (fun s -> Array.contains s semantics)
            let pass =
                Array.isEmpty missing
                && Array.isEmpty unexpected
                && settled
            let resultMsg =
                if pass then "PASS"
                elif not settled then
                    sprintf
                        "FAIL — no quiescence within %dms (events captured: %d)"
                        1500 events.Length
                else
                    let parts = ResizeArray<string>()
                    if not (Array.isEmpty missing) then
                        parts.Add(
                            sprintf "missing=%s"
                                (missing
                                 |> Array.map (sprintf "%A")
                                 |> String.concat ","))
                    if not (Array.isEmpty unexpected) then
                        parts.Add(
                            sprintf "unexpected=%s"
                                (unexpected
                                 |> Array.map (sprintf "%A")
                                 |> String.concat ","))
                    sprintf "FAIL — %s" (String.concat "; " parts)
            writer.WriteLine
                (if pass then LogLevel.Information else LogLevel.Warning)
                cat resultMsg
            return pass
        }

    // ---------------------------------------------------------------
    // Earcon replay — extracted from the previous runDiagnostic
    // body so it's part of the battery rather than parallel to it.
    // ---------------------------------------------------------------

    /// Replay the three earcons (bell-ping, error-tone,
    /// warning-tone) with NVDA labels between each, so the
    /// maintainer can verify the audio path independently of the
    /// test-battery's `EarconChannel` route.
    let private runEarconReplay
            (window: MainWindow)
            (writer: DiagnosticLogWriter)
            : Task =
        task {
            let cat = "Diagnostic.Earcons"
            let announceAndPlay (label: string) (earconId: string) : Task =
                task {
                    let act () =
                        window.TerminalSurface.Announce(label, ActivityIds.diagnostic)
                    do! window.Dispatcher.InvokeAsync(Action(act)).Task
                    do! Task.Delay(400)
                    EarconPlayer.play EarconPalette.defaultPalette earconId
                    writer.WriteLine LogLevel.Information cat
                        (sprintf "PLAYED %s" earconId)
                    do! Task.Delay(500)
                }
            // Brief settle so the earlier announce reaches the
            // NVDA queue before this one queues behind it.
            do! Task.Delay(800)
            do! announceAndPlay "Bell ping." "bell-ping"
            do! announceAndPlay "Error tone." "error-tone"
            do! announceAndPlay "Warning tone." "warning-tone"
            ()
        }

    // ---------------------------------------------------------------
    // Clipboard helper — mirror Ctrl+Shift+; (Program.fs:674-754)
    // pattern: STA thread + 3s timeout.
    // ---------------------------------------------------------------

    /// Copy `content` to the Windows clipboard via a dedicated
    /// STA thread with a 3s timeout. Returns `true` on success.
    /// Never throws — clipboard contention or hook misbehaviour
    /// is logged but not propagated.
    let private copyToClipboardSta
            (log: ILogger)
            (content: string)
            : Task<bool> =
        task {
            let setOk = TaskCompletionSource<bool>()
            let staBody = ThreadStart(fun () ->
                try
                    System.Windows.Clipboard.SetText(content)
                    setOk.TrySetResult(true) |> ignore
                with ex ->
                    log.LogWarning(
                        ex,
                        "Diagnostic clipboard SetText threw: {Message}",
                        ex.Message)
                    setOk.TrySetResult(false) |> ignore)
            let staThread = new Thread(staBody)
            staThread.SetApartmentState(ApartmentState.STA)
            staThread.IsBackground <- true
            staThread.Start()
            let! winner =
                Task.WhenAny(setOk.Task :> Task, Task.Delay(3000))
            return
                obj.ReferenceEquals(winner, setOk.Task)
                && setOk.Task.Result
        }

    // ---------------------------------------------------------------
    // Path resolution
    // ---------------------------------------------------------------

    /// Resolve the diagnostic-log path for a fresh run.
    /// `%LOCALAPPDATA%\PtySpeak\logs\YYYY-MM-DD\diagnostic-YYYY-MM-DD-HH-mm-ss-fff.log`.
    /// Same parent folder as regular `pty-speak-*.log` files so
    /// `Ctrl+Shift+L` opens the right area; the
    /// `diagnostic-` prefix is what distinguishes them.
    let private resolveDiagnosticLogPath () : string =
        let now = DateTimeOffset.UtcNow.UtcDateTime
        let root =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dayFolder = now.ToString("yyyy-MM-dd")
        let stamp = now.ToString("yyyy-MM-dd-HH-mm-ss-fff")
        let dir = Path.Combine(root, "PtySpeak", "logs", dayFolder)
        Path.Combine(dir, sprintf "diagnostic-%s.log" stamp)

    // ---------------------------------------------------------------
    // Main entry — runFullBattery
    // ---------------------------------------------------------------

    /// Run the complete diagnostic battery. Called from the
    /// Ctrl+Shift+D hotkey handler; returns immediately while
    /// the battery runs on a background task.
    ///
    /// Parameters are resolvers (rather than direct values) so
    /// the diagnostic always reads the LIVE state — e.g. the
    /// active shell may have changed via `Ctrl+Shift+1/2/3`
    /// between the hotkey binding and the press.
    let runFullBattery
            (window: MainWindow)
            (resolveHost: unit -> ConPtyHost option)
            (resolveShell: unit -> ShellRegistry.Shell)
            (resolveSeq: unit -> int64)
            : unit =
        let log = Logger.get "Terminal.App.Diagnostics.runFullBattery"
        let _ =
            task {
                try
                    let logPath = resolveDiagnosticLogPath ()
                    use writer = new DiagnosticLogWriter(logPath)
                    let cat = "Diagnostic.Init"
                    let shell = resolveShell ()
                    let pid =
                        match resolveHost () with
                        | Some h -> int h.ProcessId
                        | None -> 0
                    writer.WriteLine LogLevel.Information cat
                        (sprintf "Battery starting. Shell=%s ShellId=%A PID=%d LogPath=%s"
                             shell.DisplayName shell.Id pid logPath)
                    log.LogInformation(
                        "Diagnostic battery starting. Shell={Shell} LogPath={LogPath}",
                        shell.DisplayName, logPath)
                    let announce (msg: string) : Task =
                        task {
                            let act () =
                                window.TerminalSurface.Announce(msg, ActivityIds.diagnostic)
                            do! window.Dispatcher.InvokeAsync(Action(act)).Task
                        }
                    do! announce
                        (sprintf
                            "Starting diagnostic on %s. About 10 seconds; commands will run in your shell."
                            shell.DisplayName)
                    // T0 — process snapshot.
                    let snapshot = enumerateShellProcesses ()
                    writer.WriteLine LogLevel.Information "Diagnostic.T0.Snapshot" snapshot
                    log.LogInformation(
                        "Diagnostic snapshot. ProcessCounts={Snapshot}", snapshot)
                    // T_Earcons — replay the three earcons.
                    do! runEarconReplay window writer
                    // Per-shell command battery.
                    let writeBytes (bytes: byte[]) : Task =
                        task {
                            let act () =
                                match resolveHost () with
                                | Some h -> h.WriteBytes(bytes)
                                | None -> ()
                            do! window.Dispatcher.InvokeAsync(Action(act)).Task
                        }
                    let tests = selectTestsForShell shell.Id
                    let mutable pass = 0
                    let mutable fail = 0
                    if Array.isEmpty tests then
                        writer.WriteLine LogLevel.Information "Diagnostic.Battery"
                            (sprintf
                                "Skipped command battery: no test set for shell %A."
                                shell.Id)
                    else
                        for test in tests do
                            let! ok = runOneTest writer writeBytes resolveSeq test
                            if ok then pass <- pass + 1 else fail <- fail + 1
                    let total = pass + fail
                    writer.WriteLine LogLevel.Information "Diagnostic.Summary"
                        (sprintf "PASS=%d FAIL=%d TOTAL=%d Shell=%A"
                             pass fail total shell.Id)
                    log.LogInformation(
                        "Diagnostic battery complete. Pass={Pass} Fail={Fail} Total={Total}",
                        pass, fail, total)
                    // Close the writer BEFORE reading the file for clipboard.
                    (writer :> IDisposable).Dispose()
                    // Read the diagnostic log content for clipboard. Same
                    // FileShare.ReadWrite open mode as Ctrl+Shift+; uses.
                    let content =
                        try
                            use stream =
                                new FileStream(
                                    logPath, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite)
                            use reader = new StreamReader(stream, Encoding.UTF8)
                            reader.ReadToEnd()
                        with ex ->
                            log.LogWarning(
                                ex,
                                "Failed to read diagnostic log for clipboard: {Message}",
                                ex.Message)
                            ""
                    let summaryLine =
                        if total = 0 then
                            sprintf
                                "Diagnostic complete. Snapshot and earcons logged. Shell %s skipped command battery."
                                shell.DisplayName
                        else
                            sprintf
                                "Diagnostic complete. %d of %d passed."
                                pass total
                    if String.IsNullOrEmpty content then
                        do! announce
                            (sprintf "%s Could not read diagnostic log." summaryLine)
                    else
                        let! copied = copyToClipboardSta log content
                        if copied then
                            do! announce
                                (sprintf "%s Diagnostic log copied to clipboard." summaryLine)
                        else
                            do! announce
                                (sprintf
                                    "%s Clipboard copy failed; diagnostic log at %s."
                                    summaryLine
                                    logPath)
                with ex ->
                    log.LogError(
                        ex,
                        "Diagnostic battery raised an unhandled exception: {Message}",
                        ex.Message)
                    try
                        let act () =
                            window.TerminalSurface.Announce(
                                sprintf "Diagnostic failed: %s" ex.Message,
                                ActivityIds.error)
                        do! window.Dispatcher.InvokeAsync(Action(act)).Task
                    with _ -> ()
            }
        ()
