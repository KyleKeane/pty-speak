namespace Terminal.Core

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// File-logger infrastructure — writes structured log entries to
/// `%LOCALAPPDATA%\PtySpeak\logs\pty-speak-{yyyy-MM-dd}.log`.
///
/// Design:
///
///   * **No third-party dependencies.** Implements
///     `Microsoft.Extensions.Logging.ILogger` /
///     `ILoggerProvider` directly. The abstraction package ships
///     with the .NET 9 SDK; only our custom file-sink code is
///     project-local.
///   * **Single background task drains a bounded channel.** Every
///     `logger.LogInformation(...)` call enqueues a `LogEntry`
///     and returns synchronously; the writer thread serialises
///     formatting + file I/O on a separate task so the WPF
///     dispatcher / parser thread / coalescer never block on
///     disk.
///   * **Daily rolling.** The writer notices when
///     `DateTimeOffset.UtcNow.Date` changes and reopens the file
///     under the new date stamp.
///   * **7-day retention.** On startup the writer scans the log
///     directory and deletes files older than the configured
///     retention window. Tunable; Phase 2 user-settings will
///     surface this as a configurable value.
///   * **Crash-safe-enough.** `FileShare.ReadWrite` allows
///     external readers (NVDA, Notepad) to open the active log;
///     each batch is flushed via `StreamWriter.Flush` so a hard
///     crash loses at most the last partial line.
///
/// Threading: every public surface is thread-safe by virtue of
/// the channel — call from any thread, including the WPF
/// dispatcher and the parser/reader threads.
///
/// SECURITY (call-site discipline):
///   * **Never log** typed user input, paste content, full
///     screen contents, environment variables, or anything that
///     could plausibly contain secrets.
///   * The sink does NOT auto-sanitise — that's the caller's
///     responsibility. Logs are opaque text; whatever the
///     caller writes lands in the file as-is.

type FileLoggerOptions =
    { LogDirectory: string
      RetentionDays: int
      MinLevel: LogLevel
      ChannelCapacity: int }

module FileLoggerOptions =

    /// Read the `PTYSPEAK_LOG_LEVEL` env var as a runtime
    /// override for the default minimum log level. Useful for
    /// the maintainer / contributors to flip to Debug or Trace
    /// without code changes — the Phase 2 user-settings UI will
    /// later expose this as a real setting. Recognised values
    /// match the `Microsoft.Extensions.Logging.LogLevel` enum
    /// names (case-insensitive): `Trace`, `Debug`, `Information`,
    /// `Warning`, `Error`, `Critical`, `None`. Anything else
    /// silently falls back to `Information`.
    let private envOverrideLogLevel (fallback: LogLevel) : LogLevel =
        match Environment.GetEnvironmentVariable("PTYSPEAK_LOG_LEVEL") with
        | null -> fallback
        | "" -> fallback
        | s ->
            let mutable parsed = fallback
            if Enum.TryParse<LogLevel>(s, true, &parsed) then parsed
            else fallback

    /// Defaults for development: log everything Information and
    /// above to `%LOCALAPPDATA%\PtySpeak\logs\`, keep 7 days,
    /// 8192-entry bounded channel.
    /// The minimum level is overridden by `PTYSPEAK_LOG_LEVEL`
    /// if set (see `envOverrideLogLevel`).
    let createDefault () : FileLoggerOptions =
        let baseDir =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        { LogDirectory = Path.Combine(baseDir, "PtySpeak", "logs")
          RetentionDays = 7
          MinLevel = envOverrideLogLevel LogLevel.Information
          ChannelCapacity = 8192 }

/// One queued log line — the writer task formats this into bytes
/// and appends to the active log file.
[<Struct>]
type internal LogEntry =
    { Timestamp: DateTimeOffset
      Level: LogLevel
      Category: string
      Message: string
      Exception: exn voption }

/// Shared sink behind every `FileLogger` instance. One per
/// process. Owns the background drain task + the active file
/// stream.
///
/// File layout (per-session files in per-day folders):
///
/// ```text
/// %LOCALAPPDATA%\PtySpeak\logs\
/// ├── 2026-05-02\
/// │   ├── pty-speak-2026-05-02-13-45-23-189.log    ← session that launched at 13:45:23.189 UTC
/// │   └── pty-speak-2026-05-02-15-30-44-027.log
/// └── 2026-05-01\
///     └── pty-speak-2026-05-01-09-15-22-318.log
/// ```
///
/// One file per launch (per-session) inside a day-folder.
/// Lets the maintainer / user navigate to a specific session
/// when reporting a bug without scrolling a giant aggregated
/// file. Retention deletes whole day-folders older than the
/// configured window.
type FileLoggerSink (options: FileLoggerOptions) =

    /// Capture launch instant ONCE at construction. Each session
    /// writes to a single file named after this timestamp; if
    /// the session runs past midnight the file stays in its
    /// launch-day folder rather than splitting across two
    /// folders.
    let launchUtc = DateTimeOffset.UtcNow

    let channel =
        let opts =
            BoundedChannelOptions(options.ChannelCapacity,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false)
        Channel.CreateBounded<LogEntry>(opts)

    let cts = new CancellationTokenSource()

    // Flush-barrier TCS used by `FlushPending`. The drain loop
    // atomically swaps in a fresh TCS after every successful
    // `StreamWriter.Flush`, then completes the previous one — so a
    // caller that captures the current TCS and awaits it gets
    // signalled the next time the loop completes a flush. Used by
    // `Ctrl+Shift+;` log-copy so the clipboard captures
    // up-to-the-moment state instead of the file contents minus
    // however many entries are still in flight in the channel.
    let flushTcsLock = obj ()
    let mutable flushTcs = TaskCompletionSource<unit>()

    /// Atomically swap the current `flushTcs` for a fresh one and
    /// complete the previous one. Called from the drain loop after
    /// every successful flush. Idempotent — `TrySetResult` on an
    /// already-completed TCS is a no-op.
    let signalFlushComplete () =
        let prev =
            lock flushTcsLock (fun () ->
                let p = flushTcs
                flushTcs <- TaskCompletionSource<unit>()
                p)
        prev.TrySetResult(()) |> ignore

    /// Format one log entry as a single line. Multi-line text
    /// (exception stack traces) is preserved as-is so readers
    /// can navigate it; downstream tooling that expects
    /// one-entry-per-line should split on the timestamp prefix.
    let formatEntry (e: LogEntry) : string =
        let levelLabel =
            match e.Level with
            | LogLevel.Trace -> "TRC"
            | LogLevel.Debug -> "DBG"
            | LogLevel.Information -> "INF"
            | LogLevel.Warning -> "WRN"
            | LogLevel.Error -> "ERR"
            | LogLevel.Critical -> "CRT"
            | _ -> "???"
        let sb = StringBuilder()
        sb.Append(e.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
            .Append(" [")
            .Append(levelLabel)
            .Append("] [")
            .Append(e.Category)
            .Append("] ")
            .Append(e.Message)
            |> ignore
        match e.Exception with
        | ValueNone -> ()
        | ValueSome ex ->
            sb.Append('\n').Append(ex.ToString()) |> ignore
        sb.ToString()

    /// Compute the day-folder path + session-file path for the
    /// launch instant. Day folder named `yyyy-MM-dd`; file
    /// named `pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log` (no colons;
    /// Windows file paths reject `:`). The full date+time in the
    /// filename keeps the file self-describing when extracted
    /// from its day-folder context (e.g. when emailed as a bug
    /// report attachment) and means alphabetical sort equals
    /// chronological sort. The `fff` (millisecond) suffix is the
    /// uniqueness tie-breaker per Issue #107: two launches in
    /// the same UTC second collide on the seconds field but
    /// differ on milliseconds, so `FileMode.CreateNew`-style
    /// retry isn't required. Time fields are UTC for
    /// timezone-independent sorting.
    let pathsForLaunch () : string * string =
        let dayFolder =
            Path.Combine(
                options.LogDirectory,
                launchUtc.UtcDateTime.ToString("yyyy-MM-dd"))
        let fileName =
            sprintf "pty-speak-%s.log"
                (launchUtc.UtcDateTime.ToString("yyyy-MM-dd-HH-mm-ss-fff"))
        let filePath = Path.Combine(dayFolder, fileName)
        dayFolder, filePath

    /// Delete day-folders older than `RetentionDays`. Best-
    /// effort — folder names that don't parse as `yyyy-MM-dd`
    /// are ignored (someone's manually-created subfolder
    /// shouldn't cause a crash). Exceptions on individual
    /// folder deletes are swallowed (a stray file holding a
    /// folder open shouldn't block startup).
    let runRetention () =
        try
            if Directory.Exists(options.LogDirectory) then
                let cutoff =
                    DateTime.UtcNow.Date.AddDays(-(float options.RetentionDays))
                for dirPath in Directory.EnumerateDirectories(options.LogDirectory) do
                    try
                        let dirName = Path.GetFileName(dirPath)
                        let mutable parsed = DateTime.MinValue
                        let ok =
                            DateTime.TryParseExact(
                                dirName,
                                "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None,
                                &parsed)
                        if ok && parsed.Date < cutoff then
                            Directory.Delete(dirPath, true)
                    with _ -> ()
        with _ -> ()

    /// Background drain. Runs until the channel is completed or
    /// the cancellation token fires.
    let drainTask =
        Task.Run(fun () ->
            task {
                let dayFolder, filePath = pathsForLaunch ()
                Directory.CreateDirectory(dayFolder) |> ignore
                runRetention ()

                // Per-session log: open ONCE at startup, write
                // until the sink is disposed. No daily-roll
                // logic — a session is one file by design. If a
                // session runs past midnight it stays in its
                // launch-day folder; the next launch creates a
                // file in the new day's folder.
                let mutable writer : StreamWriter | null = null

                let openWriter () =
                    let stream =
                        new FileStream(
                            filePath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite)
                    new StreamWriter(stream, Encoding.UTF8)

                let ensureWriter () =
                    if writer = null then
                        writer <- openWriter ()

                let writeOne (entry: LogEntry) =
                    try
                        ensureWriter ()
                        let line = formatEntry entry
                        match writer with
                        | null -> ()
                        | w ->
                            w.WriteLine(line)
                    with _ ->
                        // Logging must NOT crash the app. Swallow
                        // anything the file system throws; the next
                        // write attempt will try again.
                        ()

                let mutable keepGoing = true
                let reader = channel.Reader
                try
                    while keepGoing && not cts.Token.IsCancellationRequested do
                        let! got = reader.WaitToReadAsync(cts.Token).AsTask()
                        if not got then
                            keepGoing <- false
                        else
                            let mutable peek = Unchecked.defaultof<LogEntry>
                            while reader.TryRead(&peek) do
                                writeOne peek
                            try
                                match writer with
                                | null -> ()
                                | w -> w.Flush()
                            with _ -> ()
                            // Signal any FlushPending callers that
                            // a flush just completed; entries
                            // enqueued before this iteration are
                            // now on disk.
                            signalFlushComplete ()
                with
                | :? OperationCanceledException -> ()
                | _ -> ()

                // Final drain after cancellation: pick up any
                // entries that were enqueued before shutdown.
                let mutable final = Unchecked.defaultof<LogEntry>
                while channel.Reader.TryRead(&final) do
                    writeOne final

                try
                    match writer with
                    | null -> ()
                    | w ->
                        w.Flush()
                        (w :> IDisposable).Dispose()
                with _ -> ()
                // Final flush signal after cleanup. Unblocks any
                // FlushPending caller still awaiting the TCS at
                // dispose time so they hit completion rather than
                // timeout.
                signalFlushComplete ()
            } :> Task)

    member _.IsEnabled (level: LogLevel) : bool =
        level >= options.MinLevel

    /// Enqueue an entry. Returns immediately. Drops silently if
    /// the channel is closed (post-shutdown).
    member _.TryWrite
            (timestamp: DateTimeOffset,
             level: LogLevel,
             category: string,
             message: string,
             ex: exn voption) : unit =
        if level >= options.MinLevel then
            let entry =
                { Timestamp = timestamp
                  Level = level
                  Category = category
                  Message = message
                  Exception = ex }
            channel.Writer.TryWrite(entry) |> ignore

    /// Path to the configured logs root directory (the parent
    /// of the per-day folders). Used by the `Ctrl+Shift+L`
    /// hotkey to open the root in File Explorer; the user
    /// then navigates to today's day-folder and picks the
    /// session of interest.
    member _.LogDirectory : string = options.LogDirectory

    /// Path to THIS session's log file (inside today's day-
    /// folder, named with the launch time). Useful for tools
    /// that want to grab the active session's log directly,
    /// e.g. a future "copy latest log to clipboard" hotkey or
    /// a Claude-Code-on-the-machine integration that wants the
    /// most recent log without scanning the directory.
    member _.ActiveLogPath : string =
        let _, filePath = pathsForLaunch ()
        filePath

    /// Wait for any in-flight log entries to reach disk, OR for
    /// `timeoutMs` to elapse — whichever comes first. Returns
    /// `true` if a flush completed within the window, `false` on
    /// timeout. Pass `0` (or negative) to wait indefinitely.
    ///
    /// Use case: `Ctrl+Shift+;` log-copy needs the file to reflect
    /// the most recent log entries before reading. Without this,
    /// the bounded channel may still hold ~milliseconds of
    /// entries that haven't been written yet, and the clipboard
    /// captures a stale snapshot. Caller invokes this on the
    /// dispatcher thread before reading the file; the brief block
    /// is acceptable for an explicit-user-gesture hotkey but
    /// unsuitable for hot paths (use `TryWrite` + best-effort
    /// reads there).
    ///
    /// Caveat: if the channel is idle (no pending entries) the
    /// drain loop is parked in `WaitToReadAsync` and won't fire
    /// a flush until something arrives. In that case `FlushPending`
    /// hits timeout and returns `false`. The caller's read still
    /// produces correct output because there's nothing in the
    /// pipeline to be missing — the timeout is benign in this
    /// scenario.
    member _.FlushPending (timeoutMs: int) : Task<bool> =
        task {
            // Capture the current TCS atomically. The drain loop
            // will swap it for a fresh one after the next flush
            // and complete the captured one — at which point our
            // await returns.
            let tcs =
                lock flushTcsLock (fun () -> flushTcs)
            // `Task.Delay(-1)` waits forever per Microsoft docs
            // (Timeout.Infinite). Lets the same `WhenAny` line
            // handle both bounded and unbounded waits without an
            // extra `if`-branch.
            let delayMs = if timeoutMs <= 0 then -1 else timeoutMs
            let! winner =
                Task.WhenAny(tcs.Task, Task.Delay(delayMs))
            return winner = (tcs.Task :> Task)
        }

    interface IDisposable with
        member _.Dispose() =
            try channel.Writer.TryComplete() |> ignore with _ -> ()
            try cts.Cancel() with _ -> ()
            try drainTask.Wait(2000) |> ignore with _ -> ()
            try cts.Dispose() with _ -> ()

/// No-op `IDisposable` returned from `BeginScope`. Singleton —
/// scopes are unused by this logger but the contract demands a
/// non-null return value (F# 9 reads the C# `IDisposable?` as
/// non-null in the abstractions package), so a do-nothing
/// instance keeps the type checker happy without allocating
/// per call.
type private NoopDisposable() =
    static let instance = new NoopDisposable() :> IDisposable
    static member Instance : IDisposable = instance
    interface IDisposable with
        member _.Dispose() = ()

/// `ILogger` implementation scoped to one category. Pushes to
/// the shared sink.
type internal FileLogger (sink: FileLoggerSink, category: string) =

    interface ILogger with
        member _.BeginScope<'TState when 'TState : not null> (_state: 'TState) : IDisposable =
            // Scopes aren't used by this logger — structured
            // properties land in the message text directly.
            NoopDisposable.Instance

        member _.IsEnabled (level: LogLevel) : bool =
            sink.IsEnabled level

        member _.Log<'TState>
                (level: LogLevel,
                 _eventId: EventId,
                 state: 'TState,
                 ex: exn | null,
                 formatter: System.Func<'TState, exn | null, string>) : unit =
            if sink.IsEnabled level then
                let message =
                    try formatter.Invoke(state, ex)
                    with formatErr ->
                        sprintf "<formatter threw %s: %s>"
                            (formatErr.GetType().Name)
                            formatErr.Message
                let exVOpt =
                    match ex with
                    | null -> ValueNone
                    | e -> ValueSome e
                sink.TryWrite(
                    DateTimeOffset.UtcNow,
                    level,
                    category,
                    message,
                    exVOpt)

/// `ILoggerProvider` registered with the standard
/// `LoggerFactory.Create`. One provider per process; one sink
/// shared across all loggers from this provider.
type FileLoggerProvider (sink: FileLoggerSink) =
    interface ILoggerProvider with
        member _.CreateLogger (category: string) : ILogger =
            FileLogger(sink, category) :> ILogger
        member _.Dispose() =
            (sink :> IDisposable).Dispose()

/// Static accessor so F# code anywhere in Terminal.Core (and
/// downstream projects) can grab a categorised logger without
/// threading an `ILoggerFactory` through every constructor.
///
/// `Program.fs compose ()` calls `Logger.configure factory` at
/// startup; before that, `Logger.get` returns a no-op logger so
/// any logging call in early-init code is silently skipped
/// rather than throwing.
///
/// Usage from F#:
///
/// ```fsharp
/// let private logger = Logger.get "Terminal.Core.Coalescer"
/// logger.LogInformation("Started runLoop, debounce={Window}", debounceWindow)
/// logger.LogError(ex, "Coalescer crashed")
/// ```
///
/// The `LogXxx` extension methods come from
/// `Microsoft.Extensions.Logging` and accept structured
/// templates (`{Name}` placeholders) the same way ASP.NET Core
/// loggers do.
module Logger =

    let private noopFactory : ILoggerFactory =
        { new ILoggerFactory with
            member _.CreateLogger (_categoryName: string) : ILogger =
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
                :> ILogger
            member _.AddProvider (_provider: ILoggerProvider) : unit = ()
            member _.Dispose() : unit = () }

    let mutable private factory : ILoggerFactory = noopFactory

    /// Build a minimal `ILoggerFactory` backed by a single
    /// `ILoggerProvider`. Avoids pulling in the
    /// `Microsoft.Extensions.Logging` package (non-Abstractions)
    /// just to call `LoggerFactory.Create`.
    let createFactory (provider: ILoggerProvider) : ILoggerFactory =
        { new ILoggerFactory with
            member _.CreateLogger (cat: string) : ILogger =
                provider.CreateLogger(cat)
            member _.AddProvider (_p: ILoggerProvider) : unit = ()
            member _.Dispose() : unit = provider.Dispose() }

    /// Wire a configured `ILoggerFactory` (typically built via
    /// `createFactory` against a `FileLoggerProvider` in
    /// `Program.fs compose ()`). Idempotent — calling it again
    /// replaces the factory; the previous one's sink is NOT
    /// disposed automatically (caller manages lifetimes).
    let configure (f: ILoggerFactory) : unit = factory <- f

    /// Get a categorised logger. Always returns a non-null
    /// `ILogger`; if `configure` hasn't run yet, returns a
    /// no-op logger so callers don't need to null-check.
    let get (category: string) : ILogger = factory.CreateLogger(category)
