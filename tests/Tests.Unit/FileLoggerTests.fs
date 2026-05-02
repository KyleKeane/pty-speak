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

/// Read the current day's log file as a single string.
let private readTodayLog (dir: string) : string =
    let path =
        Path.Combine(
            dir,
            sprintf "pty-speak-%s.log"
                (DateTime.UtcNow.Date.ToString("yyyy-MM-dd")))
    if File.Exists(path) then File.ReadAllText(path) else ""

[<Fact>]
let ``logger writes Information entries to today's log file`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test.Category")
    logger.LogInformation("hello {Who}", "world")
    // Disposing the sink awaits the background drain so the file
    // is flushed deterministically before we read it.
    (provider :> IDisposable).Dispose()
    let content = readTodayLog dir
    Assert.Contains("[INF]", content)
    Assert.Contains("[Test.Category]", content)
    Assert.Contains("hello world", content)

[<Fact>]
let ``logger respects minimum level filtering`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Warning)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    logger.LogDebug("debug-noise")
    logger.LogInformation("info-noise")
    logger.LogWarning("warn-signal")
    logger.LogError("error-signal")
    (provider :> IDisposable).Dispose()
    let content = readTodayLog dir
    Assert.DoesNotContain("debug-noise", content)
    Assert.DoesNotContain("info-noise", content)
    Assert.Contains("warn-signal", content)
    Assert.Contains("error-signal", content)

[<Fact>]
let ``logger writes exception details when an exception is supplied`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    let ex = InvalidOperationException("boom")
    logger.LogError(ex, "operation failed at step={Step}", 3)
    (provider :> IDisposable).Dispose()
    let content = readTodayLog dir
    Assert.Contains("operation failed at step=3", content)
    Assert.Contains("InvalidOperationException", content)
    Assert.Contains("boom", content)

[<Fact>]
let ``retention sweep deletes log files older than RetentionDays`` () =
    let dir = freshTempDir ()
    // Plant a stale log file: 30 days old.
    let stalePath =
        Path.Combine(dir, "pty-speak-2020-01-01.log")
    File.WriteAllText(stalePath, "old content\n")
    File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-30.0))
    Assert.True(File.Exists(stalePath))
    // Plant a fresh log file: 1 day old. Should survive.
    let freshPath =
        Path.Combine(dir, "pty-speak-keep.log")
    File.WriteAllText(freshPath, "fresh content\n")
    File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow.AddDays(-1.0))
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    // Force the drain task to actually run by writing one entry.
    let logger = provider.CreateLogger("Test")
    logger.LogInformation("kick the writer")
    (provider :> IDisposable).Dispose()
    Assert.False(File.Exists(stalePath),
        "Expected the 30-day-old file to be deleted by retention sweep")
    Assert.True(File.Exists(freshPath),
        "Expected the 1-day-old file to survive retention sweep")

[<Fact>]
let ``logger creates the log directory if it does not exist`` () =
    // Pick a path under a fresh temp dir but DON'T create it yet.
    let parent = freshTempDir ()
    let nestedDir = Path.Combine(parent, "nested", "logs")
    Assert.False(Directory.Exists(nestedDir))
    use sink = new FileLoggerSink(optionsAt nestedDir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let logger = provider.CreateLogger("Test")
    logger.LogInformation("should create the parent dir")
    (provider :> IDisposable).Dispose()
    Assert.True(Directory.Exists(nestedDir),
        "Expected sink to create the log directory tree on first write")

[<Fact>]
let ``LogDirectory member exposes the configured path`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    Assert.Equal(dir, sink.LogDirectory)

[<Fact>]
let ``Logger module returns NullLogger before configure is called`` () =
    // Static module — set a fresh state before checking.
    // (Technically global mutable state across tests; this test
    // runs early and configure() in production wouldn't have
    // been called yet for a fresh test process.)
    let logger = Logger.get "Test.Category"
    // NullLogger.IsEnabled returns false for every level.
    Assert.False(logger.IsEnabled(LogLevel.Information))

[<Fact>]
let ``concurrent writes from multiple threads all land in the file`` () =
    // Stress test: spawn N threads each writing M entries.
    // Channel + single-reader drain should serialise them
    // without dropping any.
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
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
    (provider :> IDisposable).Dispose()
    let content = readTodayLog dir
    // Verify a sample entry from each thread is present. (We
    // don't check the count exactly because the channel is
    // bounded; under extreme contention TryWrite may drop
    // overflow entries by design. The acceptance criterion is
    // that the writer task didn't crash and entries from every
    // thread reached the file.)
    for t in 0 .. threadCount - 1 do
        Assert.Contains(sprintf "thread=%d" t, content)

[<Fact>]
let ``formatter throwing does not crash the sink`` () =
    let dir = freshTempDir ()
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
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
    let content = readTodayLog dir
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
    use sink = new FileLoggerSink(optionsAt dir LogLevel.Information)
    let provider = new FileLoggerProvider(sink) :> ILoggerProvider
    let factory = Logger.createFactory provider
    Logger.configure factory
    let logger = Logger.get "Configured.Category"
    Assert.True(logger.IsEnabled(LogLevel.Information))
    logger.LogInformation("via configured factory")
    (provider :> IDisposable).Dispose()
    let content = readTodayLog dir
    Assert.Contains("[Configured.Category]", content)
    Assert.Contains("via configured factory", content)
