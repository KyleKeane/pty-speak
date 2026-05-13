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
    // SessionModel snapshot — Tier 1.F substrate observability
    // ---------------------------------------------------------------

    /// Truncate `text` to `maxLen` chars, appending `...` if
    /// truncation occurred. Used for prompt-text fields in the
    /// SessionModel snapshot — long PromptTexts (Powerline-style
    /// shells with embedded git status etc.) would dominate the
    /// log line otherwise.
    ///
    /// **F# 9 nullness**: parameter is `string` (non-nullable);
    /// no defensive `isNull` check needed (FS3261 would fire
    /// under TreatWarningsAsErrors). Callers pass values
    /// sourced from `SessionTuple.PromptText` (record field —
    /// always non-null) or `string option` unwrapped via
    /// pattern match — both paths guarantee non-null input.
    let internal truncate (maxLen: int) (text: string) : string =
        if text.Length <= maxLen then text
        else text.Substring(0, maxLen) + "..."

    /// Per-tuple view captured for the SessionModel diagnostic
    /// snapshot. Tier 1.F surfaces the fields most useful for
    /// substrate verification — PromptText / timestamps / exit
    /// code — plus the Tier 1.E2.B content fields (CommandText
    /// + OutputText) once they populate. The boolean
    /// `HasCommandText` / `HasOutputText` fields remain for
    /// quick "did extraction fire?" inspection at a glance.
    type RecentTupleView =
        { Index: int
          PromptText: string
          CommandStartedAt: DateTime option
          OutputStartedAt: DateTime option
          CommandFinishedAt: DateTime option
          ExitCode: int option
          HasCommandText: bool
          HasOutputText: bool
          /// Tier 1.E2.B (Cycle 20b) — the extracted command
          /// text from the old prompt's row at finalize time.
          /// Empty when extraction skipped (no
          /// MatchedRowIndex, scroll-mid-cycle defensive
          /// skip, etc.).
          CommandText: string
          /// Tier 1.E2.B (Cycle 20b) — the extracted output
          /// text from rows between old + new prompts at
          /// finalize time. Empty when no rows between (e.g.
          /// clear-screen, OSC 133 CommandFinished without
          /// next-prompt context, etc.).
          OutputText: string }

    /// Snapshot of SessionModel + HeuristicPromptDetector +
    /// active-pathway state, captured at diagnostic-battery
    /// start time. Tier 1.F's primary observability deliverable.
    /// Composed by the composition-root closure
    /// (`Program.fs.runDiagnostic`) so the data shape stays
    /// co-located with the formatter in `Diagnostics.fs`.
    type SessionModelSnapshot =
        { SessionId: Guid
          ShellId: string
          SessionStartedAt: DateTime
          IsAltScreenActive: bool
          HistoryCount: int
          MaxHistorySize: int
          ActiveState: string option
          ActivePromptText: string option
          LastEmittedPromptText: string option
          PerRowMatchesSize: int
          ActivePathwayId: string
          /// Most recent up to 3 tuples; ordered most-recent-first.
          RecentTuples: RecentTupleView[] }

    /// Pure helper: capture a `SessionModelSnapshot` from the
    /// composition-root state. Caller passes the live values;
    /// this function reads fields without mutation.
    ///
    /// **Most-recent-first ordering**: `History` is FIFO (oldest
    /// dequeued on cap; newest enqueued at tail). Reverse the
    /// `ToArray()` slice so the snapshot's index 0 is the most
    /// recent tuple.
    let captureSessionModel
            (session: SessionModel.T)
            (detector: HeuristicPromptDetector.T)
            (activePathwayId: string)
            : SessionModelSnapshot
            =
        let activeState, activePromptText =
            match session.Active with
            | Some active ->
                let stateName =
                    match active.State with
                    | SessionModel.ActiveTupleState.AwaitingPromptStart ->
                        "AwaitingPromptStart"
                    | SessionModel.ActiveTupleState.AwaitingCommandStart ->
                        "AwaitingCommandStart"
                    | SessionModel.ActiveTupleState.EditingCommand ->
                        "EditingCommand"
                    | SessionModel.ActiveTupleState.OutputStreaming ->
                        "OutputStreaming"
                Some stateName, Some active.Tuple.PromptText
            | None -> None, None
        let historyArr = session.History.ToArray()
        let recentCount = min 3 historyArr.Length
        let recentTuples =
            Array.init
                recentCount
                (fun i ->
                    let idx = historyArr.Length - 1 - i
                    let t = historyArr.[idx]
                    { Index = i
                      PromptText = t.PromptText
                      CommandStartedAt = t.CommandStartedAt
                      OutputStartedAt = t.OutputStartedAt
                      CommandFinishedAt = t.CommandFinishedAt
                      ExitCode = t.ExitCode
                      HasCommandText = not (String.IsNullOrEmpty t.CommandText)
                      HasOutputText = not (String.IsNullOrEmpty t.OutputText)
                      // Tier 1.E2.B: full content for log-line
                      // display. Truncation happens in
                      // `formatTuple` (cap 80 chars) so the
                      // record carries the full text for
                      // future query-API consumers.
                      CommandText = t.CommandText
                      OutputText = t.OutputText })
        { SessionId = session.SessionId
          ShellId = session.ShellId
          SessionStartedAt = session.SessionStartedAt
          IsAltScreenActive = session.IsAltScreenActive
          HistoryCount = session.History.Count
          MaxHistorySize = session.MaxHistorySize
          ActiveState = activeState
          ActivePromptText = activePromptText
          LastEmittedPromptText = detector.LastEmittedPromptText
          PerRowMatchesSize = detector.PerRowMatches.Count
          ActivePathwayId = activePathwayId
          RecentTuples = recentTuples }

    /// Render a `SessionModelSnapshot` as a list of log lines.
    /// Each line is logged separately (multi-line block in the
    /// diagnostic log) so a paste-into-chat preserves grep-
    /// friendly format.
    ///
    /// Long PromptTexts truncate at 40 chars (`truncate` helper)
    /// to keep log lines paste-friendly. The truncation
    /// boundary is unit-tested.
    let formatSessionModelSnapshot
            (snap: SessionModelSnapshot)
            : string list
            =
        let altLabel =
            if snap.IsAltScreenActive then "true" else "false"
        let activeLine =
            match snap.ActiveState, snap.ActivePromptText with
            | Some state, Some prompt ->
                sprintf
                    "History=%d/%d, Active=%s, ActivePromptText=\"%s\""
                    snap.HistoryCount
                    snap.MaxHistorySize
                    state
                    (truncate 40 prompt)
            | _ ->
                sprintf
                    "History=%d/%d, Active=none"
                    snap.HistoryCount
                    snap.MaxHistorySize
        let lastEmittedLine =
            match snap.LastEmittedPromptText with
            | Some text ->
                sprintf
                    "Detector LastEmittedPromptText=\"%s\" PerRowMatches=%d"
                    (truncate 40 text)
                    snap.PerRowMatchesSize
            | None ->
                sprintf
                    "Detector LastEmittedPromptText=none PerRowMatches=%d"
                    snap.PerRowMatchesSize
        let formatTuple (v: RecentTupleView) : string =
            let cs =
                match v.CommandStartedAt with
                | Some t -> t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                | None -> "none"
            let os =
                match v.OutputStartedAt with
                | Some t -> t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                | None -> "none"
            let cf =
                match v.CommandFinishedAt with
                | Some t -> t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                | None -> "none"
            let exit =
                match v.ExitCode with
                | Some c -> string c
                | None -> "none"
            // Tier 1.E2.B: surface truncated content fields
            // (cap 80 chars; longer than PromptText's 40
            // since command outputs typically run longer
            // than prompts). Empty content renders as `""`
            // which is paste-friendly + lets the maintainer
            // verify "extraction did not fire" at a glance.
            let cmdText =
                if System.String.IsNullOrEmpty v.CommandText then "\"\""
                else sprintf "\"%s\"" (truncate 80 v.CommandText)
            let outText =
                if System.String.IsNullOrEmpty v.OutputText then "\"\""
                else sprintf "\"%s\"" (truncate 80 v.OutputText)
            sprintf
                "RecentTuple[%d]: Prompt=\"%s\" CmdStarted=%s OutStarted=%s Finished=%s Exit=%s CmdText=%s OutText=%s"
                v.Index
                (truncate 40 v.PromptText)
                cs os cf exit
                cmdText
                outText
        let header =
            [ "BEGIN substrate inspection."
              sprintf
                  "SessionId=%s, Shell=%s, AltScreen=%s"
                  (string snap.SessionId)
                  snap.ShellId
                  altLabel
              sprintf
                  "StartedAt=%s"
                  (snap.SessionStartedAt.ToString(
                      "yyyy-MM-ddTHH:mm:ss.fffZ"))
              activeLine
              lastEmittedLine
              sprintf "ActivePathway=%s" snap.ActivePathwayId ]
        let tupleLines =
            snap.RecentTuples
            |> Array.map formatTuple
            |> Array.toList
        header @ tupleLines @ [ "END substrate inspection." ]

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

    /// PowerShell test set. Post-Cycle-45c (2026-05-12) the
    /// announce path runs through ContentHistory + SessionModel
    /// tuple-finalise, not the deleted StreamPathway / colour
    /// detector. The "did this command produce any user-visible
    /// effect?" signal is now `ReadyForInput` (the chime fired
    /// when the next prompt arrives + the tuple is sealed).
    /// `StreamChunk` is no longer emitted from `handleRowsChanged`
    /// (the dispatch was removed in PR-3b); `ErrorLine` /
    /// `WarningLine` are retired with the colour detector. T2–T5
    /// are kept around as documented placeholders so the rename
    /// shows up in `grep` once the colour-detection follow-up
    /// cycle re-introduces semantic colour analysis on top of
    /// ContentHistory.
    let private powerShellTests : DiagnosticTest[] =
        [|
            { Name = "T1.Plain"
              Description = "Plain Write-Host finalises a tuple → ReadyForInput chime."
              SetupCommand = None
              Command = "Write-Host PtySpeakDiagPlain"
              MustInclude = [| SemanticCategory.ReadyForInput |]
              MustNotInclude = [| SemanticCategory.ParserError |] }
        |]

    /// Cmd test set — same shape as PowerShell post-Cycle-45c.
    /// One plain-echo case probes "bytes go in, prompt comes back,
    /// tuple finalises". cmd has minimal colour support; the
    /// retired colour-detection tests don't reappear here.
    let private cmdTests : DiagnosticTest[] =
        [|
            { Name = "T1.Plain"
              Description = "Plain echo finalises a tuple → ReadyForInput chime."
              SetupCommand = None
              Command = "echo PtySpeakDiagPlain"
              MustInclude = [| SemanticCategory.ReadyForInput |]
              MustNotInclude = [| SemanticCategory.ParserError |] }
            { Name = "T2.SecondPlain"
              Description = "A second echo finalises another tuple — regression pin for the silent-third-echo bug (PR #280)."
              SetupCommand = Some "echo PtySpeakDiagFirst"
              Command = "echo PtySpeakDiagSecond"
              MustInclude = [| SemanticCategory.ReadyForInput |]
              MustNotInclude = [| SemanticCategory.ParserError |] }
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
    /// Cycle 25b: takes `now` so the diagnostic-log file and the
    /// snapshot bundle file share an identical stamp for
    /// cross-referencing.
    /// Cycle 45 Commit 2 — resolve the version string for log
    /// + diagnostic-bundle headers. Uses `AssemblyInformationalVersionAttribute`
    /// so prerelease suffixes like "0.0.1-preview.90" survive
    /// (System.Version's 4-part shape collapses them to
    /// "0.0.1.0"). Strips any "+commit-sha" trailer
    /// SourceLink / deterministic builds append. Mirror of
    /// `MainWindow.xaml.cs:29-42` and
    /// `Program.fs:resolveInformationalVersion`. Defensive try/with
    /// returns "unknown" rather than throwing — version reporting
    /// must never crash diagnostic capture.
    let private resolveInformationalVersion () : string =
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
                let raw = a.InformationalVersion
                let plusIdx = raw.IndexOf('+')
                if plusIdx > 0 then raw.Substring(0, plusIdx) else raw
            | _ -> "unknown"
        with _ -> "unknown"

    /// Cycle 45f-followup (2026-05-12) — full informational
    /// version *including* any `+sha` trailer that the build
    /// pipeline appends. The stripped variant above is intended
    /// for human-facing surfaces (startup log, "About" UI);
    /// the raw variant is for diagnostic-bundle headers so a
    /// paste-back unambiguously records WHICH commit produced
    /// the build (handy for verifying "is my fix actually in
    /// the build I'm dogfooding?"). Returns `"unknown"` only
    /// if the attribute is missing entirely.
    let private resolveInformationalVersionWithSha () : string =
        try
            let asm = System.Reflection.Assembly.GetExecutingAssembly()
            let attr =
                System.Attribute.GetCustomAttribute(
                    asm,
                    typeof<System.Reflection.AssemblyInformationalVersionAttribute>)
            match attr with
            | :? System.Reflection.AssemblyInformationalVersionAttribute as a
                when not (System.String.IsNullOrWhiteSpace a.InformationalVersion) ->
                a.InformationalVersion
            | _ -> "unknown"
        with _ -> "unknown"

    let private resolveDiagnosticLogPath (now: DateTime) : string =
        let root =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dayFolder = now.ToString("yyyy-MM-dd")
        let stamp = now.ToString("yyyy-MM-dd-HH-mm-ss-fff")
        let dir = Path.Combine(root, "PtySpeak", "logs", dayFolder)
        Path.Combine(dir, sprintf "diagnostic-%s.log" stamp)

    /// Cycle 25b — paired snapshot-bundle path. Lives under
    /// `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\` (a flat
    /// folder; one file per Ctrl+Shift+D press, named with the
    /// same stamp as the diagnostic-log file from that run so
    /// future cross-referencing is mechanical). Plain `.txt` so
    /// the maintainer can paste the contents back into a triage
    /// chat or open in any text viewer.
    let private resolveSnapshotPath (now: DateTime) : string =
        let root =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let stamp = now.ToString("yyyy-MM-dd-HH-mm-ss-fff")
        let dir = Path.Combine(root, "PtySpeak", "diagnostic-snapshots")
        Path.Combine(dir, sprintf "snapshot-%s.txt" stamp)

    /// Cycle 45c — sibling-file path for the ContentHistory FULL
    /// reconstruction (the bundle inlines only the last 64 KB;
    /// the full reconstruction goes here so a maintainer can
    /// reference it for forensics without bloating the clipboard
    /// payload). Mirrors `resolveSnapshotPath`'s pattern with the
    /// same timestamp so the two files are mechanically
    /// cross-referenceable.
    ///
    /// Replaces Cycle 34b's `linear-stream-*.txt` companion.
    let private resolveContentHistoryPath (now: DateTime) : string =
        let root =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let stamp = now.ToString("yyyy-MM-dd-HH-mm-ss-fff")
        let dir = Path.Combine(root, "PtySpeak", "diagnostic-snapshots")
        Path.Combine(dir, sprintf "content-history-%s.txt" stamp)

    /// Cycle 45c follow-up — format a one-line `Stats:` header for
    /// the `--- CONTENT HISTORY ---` bundle section. Surfaces:
    ///
    ///   * `entries` — total entries in the substrate (TextSpan +
    ///     Newline + Overwrite + Marker + Spinner). Zero implies
    ///     the substrate never saw bytes — a regression we want
    ///     loud rather than hidden behind a "did the tail look
    ///     populated?" eyeball check.
    ///   * `latestSeq` — the highest assigned Seq. `-1` matches the
    ///     "never saw bytes" case explicitly.
    ///   * Per-marker tallies — `PromptStart=N CommandStart=N
    ///     OutputStart=N CommandFinished=N`. The silent-third-echo
    ///     bug (PR #280) had a populated tail but missing
    ///     `OutputStart` markers; this tally would have shown that
    ///     without the maintainer having to scroll the inline tail.
    ///
    /// Caller wraps in `try ... with _ -> "(stats unavailable)"` so
    /// a substrate-internal failure can't break the bundle.
    let internal formatContentHistoryStats (history: ContentHistory.T) : string =
        let entries = ContentHistory.snapshot history
        let entryCount = entries.Length
        let latestSeq = ContentHistory.latestSeq history
        let markerCounts =
            entries
            |> Array.choose (fun e ->
                match e with
                | ContentHistory.Marker m -> Some m.Kind
                | _ -> None)
            |> Array.countBy id
            |> Map.ofArray
        let tally (kind: ContentHistory.MarkerKind) : int =
            Map.tryFind kind markerCounts |> Option.defaultValue 0
        // Cycle 48 PR-C — per-source counts. Lets paste-back
        // triage answer "did the substrate classify each byte
        // correctly?" without scrolling the 64 KB tail.
        let sourceCounts =
            entries
            |> Array.map ContentHistory.entrySource
            |> Array.countBy id
            |> Map.ofArray
        let sourceTally (src: ContentHistory.EntrySource) : int =
            Map.tryFind src sourceCounts |> Option.defaultValue 0
        sprintf
            "Stats: entries=%d latestSeq=%d PromptStart=%d CommandStart=%d OutputStart=%d CommandFinished=%d BellRang=%d SelectionShown=%d AltScreenEnter=%d | Sources: UserInputEcho=%d CmdOutput=%d CmdSubPrompt=%d ShellPrompt=%d BoundaryMarker=%d Unknown=%d"
            entryCount
            latestSeq
            (tally ContentHistory.MarkerKind.PromptStart)
            (tally ContentHistory.MarkerKind.CommandStart)
            (tally ContentHistory.MarkerKind.OutputStart)
            (tally ContentHistory.MarkerKind.CommandFinished)
            (tally ContentHistory.MarkerKind.BellRang)
            (tally ContentHistory.MarkerKind.SelectionShown)
            (tally ContentHistory.MarkerKind.AltScreenEnter)
            (sourceTally ContentHistory.EntrySource.UserInputEcho)
            (sourceTally ContentHistory.EntrySource.CmdOutput)
            (sourceTally ContentHistory.EntrySource.CmdSubPrompt)
            (sourceTally ContentHistory.EntrySource.ShellPrompt)
            (sourceTally ContentHistory.EntrySource.BoundaryMarker)
            (sourceTally ContentHistory.EntrySource.Unknown)

    // ---------------------------------------------------------------
    // Cycle 25b — diagnostic-dump bundle
    // ---------------------------------------------------------------

    /// Read a file's content with FileShare.ReadWrite. Used for
    /// the active FileLogger log (which the running app is still
    /// writing to) and for config.toml. Returns a parenthesised
    /// note when the file is missing or unreadable rather than
    /// throwing — the bundle should always include something for
    /// each section so the maintainer can see "config not
    /// present" vs "config read failed".
    ///
    /// Cycle 43a — bumped from `private` to `internal` so the new
    /// `formatLightweightBundle` (consumed by `CopyLatestBundle`
    /// and `GrepDiagnostics` from Program.fs) can reuse the same
    /// FileShare.ReadWrite + missing-file fallback semantics.
    let internal readFileSafe (path: string) : string =
        try
            if not (File.Exists path) then
                sprintf "(file not present: %s)" path
            else
                use stream =
                    new FileStream(
                        path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite)
                use reader = new StreamReader(stream, Encoding.UTF8)
                reader.ReadToEnd()
        with ex ->
            sprintf "(read failed: %s)" ex.Message

    /// Env-var deny-list mirroring `SessionSanitiser`'s pattern
    /// for value-redaction in session logs (`*_TOKEN`, `*_SECRET`,
    /// `*_KEY`, `*_PASSWORD`, `*_PASSWD`). `ANTHROPIC_API_KEY` is
    /// exempted: the maintainer's Claude-shell triage flow needs
    /// to confirm it's actually set in the spawned-app environment
    /// when debugging "claude.exe spawned but immediately
    /// errored", and the maintainer routinely shares the dump
    /// content back to triage conversations where the value is
    /// expected to be visible. Names always appear verbatim — the
    /// SET of configured variables is itself useful diagnostic
    /// info (e.g. spotting a missing `PATH` entry that broke a
    /// shell launch).
    let private isSensitiveEnvName (name: string) : bool =
        if String.IsNullOrEmpty name then false
        elif String.Equals(name, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase) then false
        else
            let upper = name.ToUpperInvariant()
            upper.EndsWith("_TOKEN")
            || upper.EndsWith("_SECRET")
            || upper.EndsWith("_KEY")
            || upper.EndsWith("_PASSWORD")
            || upper.EndsWith("_PASSWD")

    /// Snapshot the current process environment, sorted by name,
    /// with sensitive values redacted. Format: one `NAME=value`
    /// line per variable.
    ///
    /// Cycle 43a — bumped from `private` to `internal` so the
    /// `formatLightweightBundle` orchestrator + future
    /// extractors can reuse the redaction logic without
    /// duplicating the deny-list.
    let internal formatEnvironmentRedacted () : string =
        let table = Environment.GetEnvironmentVariables()
        let names = ResizeArray<string>()
        for entry in table do
            let de = entry :?> System.Collections.DictionaryEntry
            match de.Key with
            | :? string as k -> names.Add(k)
            | _ -> ()
        names.Sort()
        let sb = StringBuilder()
        for name in names do
            let value =
                match table.[name] with
                | :? string as s -> s
                | null -> ""
                | other -> string other
            let displayValue =
                if isSensitiveEnvName name then "<redacted by suite>"
                else value
            sb.AppendLine(sprintf "%s=%s" name displayValue) |> ignore
        sb.ToString()

    /// Assemble the combined diagnostic dump bundle. Plain text
    /// with `--- SECTION ---` markers between sections; format
    /// is intentionally stable so future replay tools can index
    /// content by section header. The diagnostic-battery log
    /// (already gathered by `runFullBattery`) plus the active
    /// FileLogger log slice (which carries Cycle 24f / 24g
    /// `Config:` parse messages and the runtime heartbeat trail)
    /// plus the literal config.toml plus a redacted env snapshot.
    let private formatDiagnosticBundle
            (now: DateTime)
            (batteryLogPath: string)
            (batteryLogContent: string)
            (fileLoggerLogPath: string option)
            (configPath: string)
            (sessionLogSummary: string)
            (contentHistorySection: string)
            (corpusResultsSection: string)
            : string =
        let sb = StringBuilder()
        let appendLine (s: string) = sb.AppendLine(s) |> ignore
        let separator = "========================================================="
        appendLine separator
        appendLine "pty-speak diagnostic snapshot (Cycle 25b)"
        appendLine (sprintf "Captured: %s UTC" (now.ToString("yyyy-MM-dd HH:mm:ss")))
        let version = resolveInformationalVersion ()
        let versionFull = resolveInformationalVersionWithSha ()
        appendLine (sprintf "Version: %s" version)
        // Cycle 45f-followup — emit the full +sha-suffixed
        // version (when the build pipeline stamped it) on a
        // separate line so a paste-back unambiguously identifies
        // which commit produced this build. Skip the line if
        // there's no SHA to differentiate.
        if versionFull <> version
           && versionFull <> "unknown" then
            appendLine (sprintf "Build: %s" versionFull)
        appendLine (sprintf "OS: %s" (Environment.OSVersion.VersionString))
        appendLine (sprintf ".NET: %s" (Environment.Version.ToString()))
        appendLine (sprintf "Process ID: %d" (System.Diagnostics.Process.GetCurrentProcess().Id))
        appendLine separator
        appendLine ""

        appendLine "--- DIAGNOSTIC BATTERY LOG ---"
        appendLine (sprintf "(source: %s)" batteryLogPath)
        appendLine batteryLogContent
        appendLine ""

        appendLine "--- FILELOGGER ACTIVE LOG ---"
        match fileLoggerLogPath with
        | Some path ->
            appendLine (sprintf "(source: %s)" path)
            appendLine (readFileSafe path)
        | None ->
            appendLine "(FileLogger not configured)"
        appendLine ""

        appendLine "--- CONFIG.TOML ---"
        appendLine (sprintf "(source: %s)" configPath)
        appendLine (readFileSafe configPath)
        appendLine ""

        appendLine "--- SESSION LOG ---"
        appendLine sessionLogSummary
        appendLine ""

        // Cycle 45c — ContentHistory tail (last 64 KB inline;
        // full reconstruction lives in a sibling
        // `content-history-<ts>.txt` file referenced by the
        // section header). Replaces Cycle 34b's
        // `--- LINEAR STREAM ---` section. The 64 KB cap is hard
        // per the Cycle 29b iOS-paste-crash incident: bundles
        // must stay paste-friendly. Caller pre-formats the
        // section text (ContentHistory.tailText + AnnounceSanitiser
        // strip controls) before passing here.
        appendLine "--- CONTENT HISTORY (last 64KB) ---"
        appendLine contentHistorySection
        appendLine ""

        // Cycle 38a — canonical interaction-pair corpus results.
        // The runner ran the active shell's scenarios from
        // `canonical-interactions.toml` (deployed next to the .exe);
        // this section reports per-scenario PASS / FAIL with the
        // observed `SemanticCategory` events. Empty body if the
        // corpus file is missing or no scenarios match the shell.
        appendLine "--- CANONICAL CORPUS RESULTS ---"
        appendLine corpusResultsSection
        appendLine ""

        appendLine "--- ENVIRONMENT (deny-listed values redacted) ---"
        appendLine (formatEnvironmentRedacted ())
        appendLine ""

        appendLine "--- END OF SNAPSHOT ---"
        sb.ToString()

    /// Cycle 43a — assemble a "lightweight" bundle: the same
    /// section structure as `formatDiagnosticBundle` minus the
    /// `--- DIAGNOSTIC BATTERY LOG ---` and
    /// `--- CANONICAL CORPUS RESULTS ---` sections that require
    /// running test commands against the live shell (~10 seconds
    /// of wall time + an externally-visible side effect on the
    /// shell). The lightweight variant assembles in ~100 ms from
    /// already-on-disk artifacts and is what the new top-level
    /// `Diagnostics → Copy latest diagnostic bundle to clipboard`
    /// item produces, plus the corpus the
    /// `Diagnostics → Grep diagnostics...` dialog searches over.
    ///
    /// The bundle banner reads "lightweight" so paste-back
    /// consumers know which variant they got — distinguishes
    /// "you got a full battery run" from "you got a fast
    /// current-state snapshot".
    ///
    /// `internal` rather than `public`: Program.fs (same
    /// assembly) is the only intended caller.
    let internal formatLightweightBundle
            (now: DateTime)
            (fileLoggerLogPath: string option)
            (configPath: string)
            (sessionLogSummary: string)
            (contentHistorySection: string)
            : string =
        let sb = StringBuilder()
        let appendLine (s: string) = sb.AppendLine(s) |> ignore
        let separator = "========================================================="
        appendLine separator
        appendLine "pty-speak diagnostic snapshot (Cycle 43a lightweight)"
        appendLine (sprintf "Captured: %s UTC" (now.ToString("yyyy-MM-dd HH:mm:ss")))
        let version = resolveInformationalVersion ()
        let versionFull = resolveInformationalVersionWithSha ()
        appendLine (sprintf "Version: %s" version)
        // Cycle 45f-followup — `Build:` line carries the full
        // SHA-suffixed informational version when the build
        // pipeline stamped one (mirrors the full-bundle header).
        if versionFull <> version
           && versionFull <> "unknown" then
            appendLine (sprintf "Build: %s" versionFull)
        appendLine (sprintf "OS: %s" (Environment.OSVersion.VersionString))
        appendLine (sprintf ".NET: %s" (Environment.Version.ToString()))
        appendLine (sprintf "Process ID: %d" (System.Diagnostics.Process.GetCurrentProcess().Id))
        appendLine "Variant: lightweight (no diagnostic battery; no canonical corpus)"
        appendLine separator
        appendLine ""

        appendLine "--- FILELOGGER ACTIVE LOG ---"
        match fileLoggerLogPath with
        | Some path ->
            appendLine (sprintf "(source: %s)" path)
            appendLine (readFileSafe path)
        | None ->
            appendLine "(FileLogger not configured)"
        appendLine ""

        appendLine "--- CONFIG.TOML ---"
        appendLine (sprintf "(source: %s)" configPath)
        appendLine (readFileSafe configPath)
        appendLine ""

        appendLine "--- SESSION LOG ---"
        appendLine sessionLogSummary
        appendLine ""

        appendLine "--- CONTENT HISTORY (last 64KB) ---"
        appendLine contentHistorySection
        appendLine ""

        appendLine "--- ENVIRONMENT (deny-listed values redacted) ---"
        appendLine (formatEnvironmentRedacted ())
        appendLine ""

        appendLine "--- END OF SNAPSHOT ---"
        sb.ToString()

    /// Write the bundle text to disk. Creates the parent dir if
    /// missing. Returns the path written, or `None` on failure
    /// (the caller still has the bundle in memory for clipboard
    /// fallback). Never throws.
    let private writeSnapshotFile
            (log: ILogger)
            (path: string)
            (content: string)
            : string option =
        try
            // F# 9 nullness: Path.GetDirectoryName returns
            // `string | null` (the F# 9 annotation marks every
            // System.IO API that can legitimately return null).
            // Narrow via pattern match so the non-null arm
            // type-checks against Directory.CreateDirectory's
            // non-nullable signature.
            match Path.GetDirectoryName(path) with
            | null -> ()
            | "" -> ()
            | dir -> Directory.CreateDirectory(dir) |> ignore
            File.WriteAllText(path, content, Encoding.UTF8)
            Some path
        with ex ->
            log.LogWarning(
                ex,
                "Diagnostic snapshot file write failed at {Path}: {Message}",
                path, ex.Message)
            None

    // ---------------------------------------------------------------
    // Cycle 38a — canonical interaction-pair corpus runner
    // ---------------------------------------------------------------

    /// Resolve the canonical-corpus TOML path. Lives next to the
    /// Terminal.App.exe at runtime (Content + CopyToOutputDirectory
    /// in `Terminal.App.fsproj`); the source-of-truth file lives at
    /// `tests/fixtures/canonical-interactions.toml` in the repo and
    /// is `<Link>`-flattened on copy. Returns `None` if the file
    /// isn't present (e.g. running an older build where the corpus
    /// hadn't shipped yet).
    let private resolveCorpusPath () : string option =
        let path =
            Path.Combine(
                AppContext.BaseDirectory,
                "canonical-interactions.toml")
        if File.Exists(path) then Some path else None

    /// Filter a Scenario array down to the entries matching the
    /// active shell's identifier. Maps `ShellRegistry.ShellId`
    /// values to the corpus's lower-case shell-key strings —
    /// mirrors the `shellIdToConfigKey` pattern in `Program.fs:1141`.
    let private filterScenariosForShell
            (shellId: ShellRegistry.ShellId)
            (scenarios: CanonicalCorpus.Scenario[])
            : CanonicalCorpus.Scenario[] =
        let shellKey =
            match shellId with
            | ShellRegistry.Cmd -> "cmd"
            | ShellRegistry.PowerShell -> "powershell"
            | ShellRegistry.Claude -> "claude"
        scenarios
        |> Array.filter (fun s -> s.Shell = shellKey)

    /// Run one scenario. Mirrors `runOneTest`'s shape but takes the
    /// per-scenario `QuiescenceMs` / `TimeoutMs` parameters from
    /// the Scenario record rather than the hard-coded 200/1500
    /// `runOneTest` uses. Returns a `ScenarioResult` (no Boolean —
    /// the caller assembles the bundle).
    let private runOneScenario
            (writer: DiagnosticLogWriter)
            (writeBytes: byte[] -> Task)
            (resolveSeq: unit -> int64)
            (scenario: CanonicalCorpus.Scenario)
            : Task<CanonicalCorpus.ScenarioResult> =
        task {
            let cat = sprintf "Diagnostic.Corpus.%s" scenario.Id
            writer.WriteLine LogLevel.Information cat
                (sprintf "BEGIN %s" scenario.Description)
            let tap = DiagnosticEventTap()
            // Setup phase (optional).
            match scenario.SetupCommand with
            | Some setupCmd ->
                let setupBytes = commandBytes setupCmd
                writer.WriteLine LogLevel.Information cat
                    (sprintf "SETUP %s" (hexDump setupBytes))
                tap.Enable()
                do! writeBytes setupBytes
                let! settled, elapsed =
                    waitForQuiescence
                        resolveSeq
                        scenario.QuiescenceMs
                        scenario.TimeoutMs
                let setupEvents = tap.DisableAndDrain()
                writer.WriteLine LogLevel.Information cat
                    (sprintf "SETUP_RESULT settled=%b elapsedMs=%d events=%d"
                         settled elapsed setupEvents.Length)
            | None -> ()
            // Main phase.
            let mainBytes = commandBytes scenario.Command
            writer.WriteLine LogLevel.Information cat
                (sprintf "WRITE %s" (hexDump mainBytes))
            tap.Enable()
            let started = DateTimeOffset.UtcNow
            do! writeBytes mainBytes
            let! settled, _ =
                waitForQuiescence
                    resolveSeq
                    scenario.QuiescenceMs
                    scenario.TimeoutMs
            let events = tap.DisableAndDrain()
            let elapsedMs =
                int (DateTimeOffset.UtcNow - started).TotalMilliseconds
            let semantics =
                events |> Array.map (fun ev -> ev.Semantic)
            let payloads =
                events |> Array.map (fun ev -> ev.Payload)
            writer.WriteLine LogLevel.Information cat
                (sprintf "WAIT settled=%b elapsedMs=%d" settled elapsedMs)
            writer.WriteLine LogLevel.Information cat
                (sprintf "EVENTS (%d): %s"
                     events.Length
                     (events
                      |> Array.map formatEvent
                      |> String.concat " | "))
            // Evaluate must_include / must_not_include.
            let missing =
                scenario.MustInclude
                |> Array.filter (fun s -> not (Array.contains s semantics))
            let unexpected =
                scenario.MustNotInclude
                |> Array.filter (fun s -> Array.contains s semantics)
            let outcome : CanonicalCorpus.ScenarioOutcome =
                if not settled then
                    CanonicalCorpus.Fail
                        (sprintf "no quiescence within %dms" scenario.TimeoutMs)
                elif not (Array.isEmpty missing) then
                    let names =
                        missing
                        |> Array.map (sprintf "%A")
                        |> String.concat ","
                    CanonicalCorpus.Fail (sprintf "missing=%s" names)
                elif not (Array.isEmpty unexpected) then
                    let names =
                        unexpected
                        |> Array.map (sprintf "%A")
                        |> String.concat ","
                    CanonicalCorpus.Fail (sprintf "unexpected=%s" names)
                else
                    CanonicalCorpus.Pass
            let outcomeMsg =
                match outcome with
                | CanonicalCorpus.Pass -> "PASS"
                | CanonicalCorpus.Fail r -> sprintf "FAIL — %s" r
            writer.WriteLine
                (match outcome with
                 | CanonicalCorpus.Pass -> LogLevel.Information
                 | _ -> LogLevel.Warning)
                cat outcomeMsg
            let result : CanonicalCorpus.ScenarioResult =
                { Scenario = scenario
                  Outcome = outcome
                  ObservedSemantics = semantics
                  ObservedPayloads = payloads
                  ElapsedMs = elapsedMs }
            return result
        }

    /// Run the canonical-corpus scenarios for the active shell.
    /// Returns `Ok results` on success or `Error message` if the
    /// corpus file is missing or malformed (the caller emits a
    /// short bundle section in that case).
    let private runCorpus
            (writer: DiagnosticLogWriter)
            (writeBytes: byte[] -> Task)
            (resolveSeq: unit -> int64)
            (shellId: ShellRegistry.ShellId)
            : Task<Result<CanonicalCorpus.ScenarioResult[], string>> =
        task {
            match resolveCorpusPath () with
            | None ->
                writer.WriteLine LogLevel.Information "Diagnostic.Corpus"
                    "Corpus file not found next to executable; section omitted."
                return Error "canonical-interactions.toml not found"
            | Some path ->
                writer.WriteLine LogLevel.Information "Diagnostic.Corpus"
                    (sprintf "Loading corpus from %s" path)
                match CanonicalCorpus.loadCanonicalCorpus path with
                | Error e ->
                    writer.WriteLine LogLevel.Warning "Diagnostic.Corpus"
                        (sprintf "Corpus parse error: %s" e)
                    return Error e
                | Ok scenarios ->
                    let filtered =
                        filterScenariosForShell shellId scenarios
                    writer.WriteLine LogLevel.Information "Diagnostic.Corpus"
                        (sprintf
                            "Loaded %d scenarios; %d match active shell %A."
                            scenarios.Length filtered.Length shellId)
                    let results = ResizeArray<CanonicalCorpus.ScenarioResult>()
                    for scenario in filtered do
                        let! r =
                            runOneScenario
                                writer writeBytes resolveSeq scenario
                        results.Add(r)
                    return Ok (results.ToArray())
        }

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
            (resolveSessionSnapshot: unit -> SessionModelSnapshot)
            (resolveFileLoggerLogPath: unit -> string option)
            (resolveSessionLogSummary: unit -> string)
            (resolveContentHistory: unit -> ContentHistory.T)
            : unit =
        let log = Logger.get "Terminal.App.Diagnostics.runFullBattery"
        let _ =
            task {
                try
                    // Cycle 25b — single timestamp drives the
                    // diagnostic-battery log filename AND the
                    // paired snapshot bundle filename so the two
                    // are mechanically cross-referenceable.
                    let now = DateTimeOffset.UtcNow.UtcDateTime
                    let logPath = resolveDiagnosticLogPath now
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
                    let startMsg =
                        sprintf
                            "Starting diagnostic on %s. About 10 seconds; commands will run in your shell."
                            shell.DisplayName
                    do! announce startMsg
                    // T0 — process snapshot.
                    let snapshot = enumerateShellProcesses ()
                    writer.WriteLine LogLevel.Information "Diagnostic.T0.Snapshot" snapshot
                    log.LogInformation(
                        "Diagnostic snapshot. ProcessCounts={Snapshot}", snapshot)
                    // Tier 1.F — SessionModel substrate inspection.
                    // Captured at battery-start (closure resolves
                    // composition-root mutables); rendered into the
                    // log as a multi-line block. Inserted BEFORE
                    // the per-shell command battery so substrate
                    // state contextualises any test failures that
                    // follow.
                    let sessionSnapshot = resolveSessionSnapshot ()
                    let sessionLines =
                        formatSessionModelSnapshot sessionSnapshot
                    for line in sessionLines do
                        writer.WriteLine
                            LogLevel.Information
                            "Diagnostic.SessionModel"
                            line
                    log.LogInformation(
                        "Diagnostic SessionModel inspection logged. History={Count} Active={Active}",
                        sessionSnapshot.HistoryCount,
                        match sessionSnapshot.ActiveState with
                        | Some s -> s
                        | None -> "none")
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

                    // Cycle 38a — canonical interaction-pair corpus
                    // run. Independent of the existing per-shell
                    // `DiagnosticTest` battery: the corpus targets
                    // a curated catalogue of (bytes-in,
                    // expected-NVDA-out) scenarios that grow over
                    // sub-cycles 38b-e. Failure to load the corpus
                    // (missing file, parse error) is non-fatal —
                    // the bundle just renders an explanatory
                    // section rather than aborting the battery.
                    let! corpusResultOuter =
                        runCorpus writer writeBytes resolveSeq shell.Id
                    let corpusResultsSection =
                        match corpusResultOuter with
                        | Ok results ->
                            CanonicalCorpus.formatCorpusResultsForBundle results
                        | Error msg ->
                            sprintf
                                "(corpus skipped: %s)"
                                msg
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
                    // Tier 1.F — brief substrate fragment in the
                    // NVDA announce. Adds ~5-10 spoken words so the
                    // maintainer hears substrate state alongside
                    // the battery summary; full multi-line state
                    // is in the (clipboard-copied) log.
                    //
                    // Cycle 20a-followup: announce "K of N command
                    // history entries" rather than the older "K
                    // tuples". Replaces jargon with a phrase that
                    // matches the substrate's user-facing meaning
                    // (history length) + reports the cap so the
                    // maintainer can hear "approaching cap"
                    // pressure without paste-into-chat.
                    let substrateFragment =
                        match sessionSnapshot.ActiveState with
                        | Some state ->
                            sprintf
                                " SessionModel: %d of %d command history entries, active state %s."
                                sessionSnapshot.HistoryCount
                                sessionSnapshot.MaxHistorySize
                                state
                        | None ->
                            sprintf
                                " SessionModel: %d of %d command history entries, no active state."
                                sessionSnapshot.HistoryCount
                                sessionSnapshot.MaxHistorySize
                    // Cycle 38a-followup — corpus pass/fail counts +
                    // names of failing scenarios in the spoken
                    // summary so the maintainer hears which
                    // canonical-interaction rows misbehaved without
                    // opening the bundle file. Empty string for
                    // `Error` (corpus missing/malformed; bundle
                    // section already explains) and for
                    // zero-scenarios (e.g. PowerShell/Claude with no
                    // 38a-baseline rows yet — avoids "Corpus: all 0
                    // passed.").
                    let corpusSpoken =
                        match corpusResultOuter with
                        | Ok results when results.Length > 0 ->
                            let passed =
                                results
                                |> Array.filter (fun r ->
                                    match r.Outcome with
                                    | CanonicalCorpus.Pass -> true
                                    | _ -> false)
                                |> Array.length
                            let scenarioTotal = results.Length
                            let scenarioFailed = scenarioTotal - passed
                            if scenarioFailed = 0 then
                                sprintf
                                    " Corpus: all %d passed."
                                    scenarioTotal
                            else
                                let failingIds =
                                    results
                                    |> Array.choose (fun r ->
                                        match r.Outcome with
                                        | CanonicalCorpus.Fail _ ->
                                            Some r.Scenario.Id
                                        | _ -> None)
                                    |> String.concat ", "
                                sprintf
                                    " Corpus: %d of %d passed; failing: %s."
                                    passed scenarioTotal failingIds
                        | _ -> ""
                    let summaryLine =
                        if total = 0 then
                            sprintf
                                "Diagnostic complete. Snapshot and earcons logged. Shell %s skipped command battery.%s%s"
                                shell.DisplayName
                                substrateFragment
                                corpusSpoken
                        else
                            sprintf
                                "Diagnostic complete. %d of %d passed.%s%s"
                                pass total
                                substrateFragment
                                corpusSpoken
                    // Cycle 25b — assemble the combined dump
                    // bundle: diagnostic-battery log + FileLogger
                    // active log + config.toml + redacted env.
                    // Write to the dated snapshot folder; clipboard
                    // payload is the BUNDLE (not just the
                    // diagnostic-battery log) so a paste-back to
                    // triage chat carries everything in one block.
                    let configPath = Config.defaultConfigFilePath ()
                    let fileLoggerLogPath = resolveFileLoggerLogPath ()
                    let sessionLogSummary = resolveSessionLogSummary ()
                    let snapshotPath = resolveSnapshotPath now

                    // Cycle 45c — capture the ContentHistory tail
                    // for the bundle's `--- CONTENT HISTORY
                    // (last 64KB) ---` section + write the FULL
                    // reconstruction to a sibling
                    // `content-history-<ts>.txt` file. Replaces
                    // Cycle 34b's LinearTextStream byte-tail. The
                    // 64KB inline cap protects the clipboard
                    // payload from the Cycle 29b iOS-paste-crash
                    // failure mode; the sibling file gives
                    // forensics access to the full content.
                    let history = resolveContentHistory ()
                    let historySiblingPath =
                        resolveContentHistoryPath now
                    let allHistoryText =
                        ContentHistory.tailText history Int32.MaxValue
                    try
                        match Path.GetDirectoryName(historySiblingPath) with
                        | null -> ()
                        | dir when String.IsNullOrEmpty dir -> ()
                        | dir ->
                            Directory.CreateDirectory(dir) |> ignore
                        File.WriteAllText(
                            historySiblingPath,
                            allHistoryText,
                            System.Text.Encoding.UTF8)
                    with ex ->
                        log.LogWarning(
                            ex,
                            "Diagnostic: failed to write content-history sibling file at {Path}",
                            historySiblingPath)
                    let tailText =
                        try
                            ContentHistory.tailText history (64 * 1024)
                            |> AnnounceSanitiser.sanitiseForBundle
                        with _ -> "(ContentHistory tail unavailable)"
                    // Cycle 45c follow-up — ContentHistory stats
                    // header. Surfaces total entry count, per-kind
                    // marker tallies, and latest seq so a paste-back
                    // immediately answers "did the substrate even
                    // see anything?" without scrolling the 64KB
                    // tail. The silent-third-echo bug (PR #280) had
                    // a populated tail but missing PromptStart
                    // markers; this header would have shown that.
                    let statsHeader =
                        try formatContentHistoryStats history
                        with _ -> "(ContentHistory stats unavailable)"
                    let contentHistorySection =
                        sprintf "(source: %s)\n%s\n%s" historySiblingPath statsHeader tailText

                    let bundle =
                        formatDiagnosticBundle
                            now
                            logPath
                            content
                            fileLoggerLogPath
                            configPath
                            sessionLogSummary
                            contentHistorySection
                            corpusResultsSection
                    let writtenPath = writeSnapshotFile log snapshotPath bundle
                    let savedFragment =
                        match writtenPath with
                        | Some p -> sprintf " Snapshot saved to %s." p
                        | None -> " Snapshot file write failed; clipboard still has the bundle."
                    let! copied = copyToClipboardSta log bundle
                    if copied then
                        let msg =
                            sprintf
                                "%s Diagnostic snapshot bundle copied to clipboard.%s"
                                summaryLine
                                savedFragment
                        do! announce msg
                    else
                        let msg =
                            match writtenPath with
                            | Some p ->
                                sprintf
                                    "%s Clipboard copy failed; snapshot saved to %s."
                                    summaryLine
                                    p
                            | None ->
                                sprintf
                                    "%s Clipboard copy and snapshot write both failed; diagnostic log at %s."
                                    summaryLine
                                    logPath
                        do! announce msg
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
