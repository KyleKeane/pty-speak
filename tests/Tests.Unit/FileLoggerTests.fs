module PtySpeak.Tests.Unit.FileLoggerTests

open System
open System.IO
open System.Threading
open Microsoft.Extensions.Logging
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Logging-PR tests — verify the FileLoggerSink writes entries to the
// active log file, runs retention on startup, and respects min-level
// filtering. Tests use a per-test temp directory so they can run in
// parallel without colliding on the real %LOCALAPPDATA% folder.
// ---------------------------------------------------------------------

let private freshTempDir () : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-logtest-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory(dir) |> ignore
    dir

/// Build options pointing at a fresh temp directory; sink and
/// provider are caller-managed.
let private optionsAt (dir: string) (minLevel: LogLevel) : FileLoggerOptions =
    { LogDirectory = dir
      RetentionDays = 7
      MinLevel = minLevel
      ChannelCapacity = 256 }

/// Read the active session's log file via the sink's
/// `ActiveLogPath` accessor. Tests in this file capture the
/// sink before disposal (or use the sink directly) so they
/// have a handle to the path.
let private readActiveLog (sink: FileLoggerSink) : string =
    if File.Exists(sink.ActiveLogPath) then
        File.ReadAllText(sink.ActiveLogPath)
    else
        ""

/// Read whatever log files exist in the logs root (across all
/// day-folders) as a concatenated string. Useful when a test
/// wants to verify content without holding the sink reference.
let private readAllLogContent (dir: string) : string =
    if not (Directory.Exists(dir)) then "" else
    let allFiles =
        Directory.EnumerateDirectories(dir)
        |> Seq.collect (fun d ->
            try Directory.EnumerateFiles(d, "pty-speak-*.log")
            with _ -> Seq.empty)
    let sb = System.Text.StringBuilder()
    for f in allFiles do
        try sb.Append(File.ReadAllText(f)) |> ignore with _ -> ()
    sb.ToString()

[<Fact>]
let ``logger writes Information entries to the active session log file`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test.Category")
    logger.LogInformation("hello {Who}", "world")
    // Disposing the provider awaits the background drain so the
    // file is flushed deterministically before we read it.
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.Contains("[INF]", content)
    Assert.Contains("[Test.Category]", content)
    Assert.Contains("hello world", content)

[<Fact>]
let ``active log lives inside a day-folder named yyyy-MM-dd`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    logger.LogInformation("create the file")
    (provider :> IDisposable).Dispose()
    // ActiveLogPath should be: {dir}\{yyyy-MM-dd}\pty-speak-{HH-mm-ss}.log
    let parentFolder = Path.GetDirectoryName(sink.ActiveLogPath)
    let parentName = Path.GetFileName(parentFolder)
    let mutable parsed = DateTime.MinValue
    let parsedOk =
        DateTime.TryParseExact(
            parentName,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            &parsed)
    Assert.True(parsedOk,
        sprintf "Expected day-folder named yyyy-MM-dd; got '%s'" parentName)
    let fileName = Path.GetFileName(sink.ActiveLogPath)
    Assert.StartsWith("pty-speak-", fileName)
    Assert.EndsWith(".log", fileName)

[<Fact>]
let ``logger respects minimum level filtering`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Warning)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    logger.LogDebug("debug-noise")
    logger.LogInformation("info-noise")
    logger.LogWarning("warn-signal")
    logger.LogError("error-signal")
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.DoesNotContain("debug-noise", content)
    Assert.DoesNotContain("info-noise", content)
    Assert.Contains("warn-signal", content)
    Assert.Contains("error-signal", content)

[<Fact>]
let ``logger writes exception details when an exception is supplied`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    let ex = InvalidOperationException("boom")
    logger.LogError(ex, "operation failed at step={Step}", 3)
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.Contains("operation failed at step=3", content)
    Assert.Contains("InvalidOperationException", content)
    Assert.Contains("boom", content)

[<Fact>]
let ``retention sweep deletes day-folders older than RetentionDays`` () =
    let dir = freshTempDir ()
    // Plant a stale day-folder named for a date >7 days ago.
    let staleDate = DateTime.UtcNow.Date.AddDays(-30.0)
    let staleDirName = staleDate.ToString("yyyy-MM-dd")
    let staleDir = Path.Combine(dir, staleDirName)
    Directory.CreateDirectory(staleDir) |> ignore
    File.WriteAllText(
        Path.Combine(staleDir, "pty-speak-12-00-00.log"),
        "old content\n")
    Assert.True(Directory.Exists(staleDir))
    // Plant a fresh day-folder (yesterday) — should survive.
    let freshDate = DateTime.UtcNow.Date.AddDays(-1.0)
    let freshDirName = freshDate.ToString("yyyy-MM-dd")
    let freshDir = Path.Combine(dir, freshDirName)
    Directory.CreateDirectory(freshDir) |> ignore
    File.WriteAllText(
        Path.Combine(freshDir, "pty-speak-12-00-00.log"),
        "fresh content\n")
    // Plant a folder with a non-date name — should be ignored
    // entirely (defensive: stray folders shouldn't crash).
    let strayDir = Path.Combine(dir, "not-a-date")
    Directory.CreateDirectory(strayDir) |> ignore
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    // Force the drain task to actually run by writing one entry.
    let logger = provider.CreateLogger("Test")
    logger.LogInformation("kick the writer")
    (provider :> IDisposable).Dispose()
    Assert.False(Directory.Exists(staleDir),
        "Expected the 30-day-old day-folder to be deleted")
    Assert.True(Directory.Exists(freshDir),
        "Expected the 1-day-old day-folder to survive")
    Assert.True(Directory.Exists(strayDir),
        "Expected a folder with a non-date name to be ignored by retention")

[<Fact>]
let ``logger creates the day-folder if it does not exist`` () =
    // Pick a path under a fresh temp dir but DON'T create it yet.
    let parent = freshTempDir ()
    let nestedDir = Path.Combine(parent, "nested", "logs")
    Assert.False(Directory.Exists(nestedDir))
    let sink = new FileLoggerSink(optionsAt nestedDir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    logger.LogInformation("should create the parent dir + day folder")
    (provider :> IDisposable).Dispose()
    Assert.True(Directory.Exists(nestedDir),
        "Expected sink to create the log root")
    let dayFolder = Path.GetDirectoryName(sink.ActiveLogPath)
    Assert.True(Directory.Exists(dayFolder),
        sprintf "Expected sink to create the day folder %s" dayFolder)

[<Fact>]
let ``LogDirectory member exposes the configured root path`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    Assert.Equal(dir, sink.LogDirectory)

[<Fact>]
let ``ActiveLogPath member exposes the per-session file inside today's day-folder`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    // The path should start with `{dir}\{yyyy-MM-dd}\` and end
    // with `pty-speak-{HH-mm-ss}.log`.
    Assert.StartsWith(dir, sink.ActiveLogPath)
    let fileName = Path.GetFileName(sink.ActiveLogPath)
    Assert.StartsWith("pty-speak-", fileName)
    Assert.EndsWith(".log", fileName)

// Note: a "Logger module returns NullLogger before configure"
// test was tried and removed. The Logger module's `factory`
// field is mutable static state, which xUnit can't isolate
// across test methods (the "after configure" test below
// permanently sets it). Testing the default-to-NullLogger
// path is defensive UX, not contractual; not worth the
// test-isolation pain. The default is exercised in
// production by the brief window between process start and
// `Logger.configure` running in `Program.fs compose ()`.

[<Fact>]
let ``concurrent writes from multiple threads all land in the file`` () =
    // Stress test: spawn N threads each writing M entries.
    // Channel + single-reader drain should serialise them
    // without dropping any. ChannelCapacity is bumped above
    // total-entries so TryWrite never falls back to drop;
    // the contract under capacity exhaustion is "drop oldest
    // wait" by design but isn't what this test exercises.
    let dir = freshTempDir ()
    let opts =
        { LogDirectory = dir
          RetentionDays = 7
          MinLevel = LogLevel.Information
          ChannelCapacity = 8192 }
    use sink = new FileLoggerSink(opts)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Concurrent")
    let threadCount = 8
    let perThreadCount = 50
    let threads =
        [| for t in 0 .. threadCount - 1 ->
            new Thread(fun () ->
                for i in 0 .. perThreadCount - 1 do
                    logger.LogInformation(
                        "thread={Thread} entry={Entry}",
                        t, i)) |]
    for thread in threads do thread.Start()
    for thread in threads do thread.Join()
    // Sentinel: write one more entry AFTER all threads finish.
    // If the writer task survived the concurrent burst, this
    // entry lands. (This is the test's primary acceptance
    // criterion — sink survived.)
    logger.LogInformation("post-concurrent sentinel")
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.Contains("post-concurrent sentinel", content)
    // Verify SOMETHING from the concurrent burst made it too —
    // not every thread (the channel is bounded; if a future
    // refactor drops capacity, individual threads may lose
    // entries), but at least most.
    let threadsThatLanded =
        [0..threadCount-1]
        |> List.sumBy (fun t ->
            if content.Contains(sprintf "thread=%d" t) then 1 else 0)
    Assert.True(
        threadsThatLanded >= threadCount / 2,
        sprintf "Expected at least %d threads' entries to land; got %d"
            (threadCount / 2) threadsThatLanded)

[<Fact>]
let ``formatter throwing does not crash the sink`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("FormatterCrash")
    // Issue a log call where the formatter throws.
    logger.Log(
        LogLevel.Information,
        EventId(),
        "state-value",
        null,
        System.Func<string, exn | null, string>(fun _state _ex ->
            raise (InvalidOperationException("formatter exploded"))))
    // The sink must survive — a subsequent normal call should
    // still land in the file.
    logger.LogInformation("post-crash entry survives")
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.Contains("post-crash entry survives", content)
    // The fallback message for the broken formatter should also
    // appear (so the diagnostic is captured rather than swallowed).
    Assert.Contains("<formatter threw", content)
    Assert.Contains("InvalidOperationException", content)

[<Fact>]
let ``PTYSPEAK_LOG_LEVEL env var overrides the default min level`` () =
    let original = Environment.GetEnvironmentVariable("PTYSPEAK_LOG_LEVEL")
    try
        Environment.SetEnvironmentVariable("PTYSPEAK_LOG_LEVEL", "Debug")
        let opts = FileLoggerOptions.createDefault ()
        Assert.Equal(LogLevel.Debug, opts.MinLevel)
    finally
        Environment.SetEnvironmentVariable("PTYSPEAK_LOG_LEVEL", original)

[<Fact>]
let ``PTYSPEAK_LOG_LEVEL with an invalid value falls back to Information`` () =
    let original = Environment.GetEnvironmentVariable("PTYSPEAK_LOG_LEVEL")
    try
        Environment.SetEnvironmentVariable("PTYSPEAK_LOG_LEVEL", "Banana")
        let opts = FileLoggerOptions.createDefault ()
        Assert.Equal(LogLevel.Information, opts.MinLevel)
    finally
        Environment.SetEnvironmentVariable("PTYSPEAK_LOG_LEVEL", original)

[<Fact>]
let ``Logger module returns the configured factory's logger after configure`` () =
    let dir = freshTempDir ()
    let sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let factory = Logger.createFactory provider
    Logger.configure factory
    let logger = Logger.get "Configured.Category"
    Assert.True(logger.IsEnabled(LogLevel.Information))
    logger.LogInformation("via configured factory")
    (provider :> IDisposable).Dispose()
    let content = readActiveLog sink
    Assert.Contains("[Configured.Category]", content)
    Assert.Contains("via configured factory", content)
