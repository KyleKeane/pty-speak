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
type FileLoggerSink (options: FileLoggerOptions) =

    let channel =
        let opts =
            BoundedChannelOptions(options.ChannelCapacity,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false)
        Channel.CreateBounded<LogEntry>(opts)

    let cts = new CancellationTokenSource()

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

    /// Compute the path for a given UTC date.
    let pathForDate (utcDate: DateTime) : string =
        Path.Combine(
            options.LogDirectory,
            sprintf "pty-speak-%s.log" (utcDate.ToString("yyyy-MM-dd")))

    /// Delete log files older than `RetentionDays` days. Best-
    /// effort — exceptions are swallowed (a stray file in the
    /// logs directory shouldn't crash the app).
    let runRetention () =
        try
            if Directory.Exists(options.LogDirectory) then
                let cutoff = DateTime.UtcNow.AddDays(-(float options.RetentionDays))
                for path in Directory.EnumerateFiles(options.LogDirectory, "pty-speak-*.log") do
                    try
                        let info = FileInfo(path)
                        if info.LastWriteTimeUtc < cutoff then
                            info.Delete()
                    with _ -> ()
        with _ -> ()

    /// Background drain. Runs until the channel is completed or
    /// the cancellation token fires.
    let drainTask =
        Task.Run(fun () ->
            task {
                Directory.CreateDirectory(options.LogDirectory) |> ignore
                runRetention ()

                let mutable currentDate = DateTime.UtcNow.Date
                let mutable writer : StreamWriter | null = null

                let openWriter (date: DateTime) =
                    let stream =
                        new FileStream(
                            pathForDate date,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite)
                    new StreamWriter(stream, Encoding.UTF8)

                let ensureWriter () =
                    let today = DateTime.UtcNow.Date
                    if writer = null then
                        writer <- openWriter today
                        currentDate <- today
                    elif today <> currentDate then
                        // Daily roll: close yesterday's writer,
                        // open today's, run retention sweep again.
                        // Pattern-match `writer` to satisfy F# 9
                        // strict-null: even though we know writer
                        // is non-null in this elif branch, the
                        // compiler's flow analysis doesn't carry
                        // that across the `today <> currentDate`
                        // condition.
                        (match writer with
                         | null -> ()
                         | w ->
                             try (w :> IDisposable).Dispose() with _ -> ())
                        writer <- openWriter today
                        currentDate <- today
                        runRetention ()

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

    /// Path to the currently active log file. Useful for the
    /// Ctrl+Shift+L "open logs folder" hotkey.
    member _.LogDirectory : string = options.LogDirectory

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
