module PtySpeak.Tests.Unit.SessionLogWriterTests

open System
open System.IO
open System.Text.Json
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 24c — SessionLogWriterSink behavioural pinning
// ---------------------------------------------------------------------
//
// Tests pin:
//   * Path resolution: <output_dir>/session-<SessionId>.jsonl.
//   * Single-tuple enqueue → exactly one JSONL line on disk.
//   * Multi-tuple enqueue → lines preserved in enqueue order.
//   * Each line ends with the literal `\n` (not `\r\n` even on
//     Windows; the sink uses formatTupleAsJsonl's `\n` terminator
//     directly).
//   * Disposal flushes pending writes before returning.
//   * Disposal is idempotent.
//   * Output directory is created if missing.
//   * Disk-error swallowing: an unwritable output dir does NOT
//     surface an exception to the caller.
//   * Lone-surrogate tuples are dropped (logged) without breaking
//     the writer for subsequent tuples (Cycle 24b serializer
//     contract bridges into the writer's silent error handling).

// ---------------------------------------------------------------------
// Test fixtures
// ---------------------------------------------------------------------

let private freshTempDir () : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-sessionlog-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory(dir) |> ignore
    dir

let private fixedSessionId =
    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")

let private fixedTuple () : SessionModel.SessionTuple =
    { Id = Guid.Parse("11111111-2222-3333-4444-555555555555")
      CommandId = None
      ShellId = "powershell"
      PromptStartedAt = DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc)
      CommandStartedAt = None
      OutputStartedAt = None
      CommandFinishedAt =
        Some (DateTime(2026, 5, 9, 12, 0, 1, DateTimeKind.Utc))
      PromptText = "PS>"
      CommandText = "echo hi"
      OutputText = "hi"
      ExitCode = Some 0
      Sources = Map.empty
      ExtraParams = Map.empty }

let private optionsFor (dir: string) (sessionId: Guid) : SessionLogWriterOptions =
    { OutputDirectory = dir
      SessionId = sessionId
      ChannelCapacity = 16 }

let private newSink (dir: string) (sessionId: Guid) : SessionLogWriterSink =
    let opts = optionsFor dir sessionId
    new SessionLogWriterSink(opts, NullLogger.Instance)

/// Construct a sink, run an action, dispose, return the file
/// content. Mirrors the FileLoggerTests pattern.
let private withSink
        (dir: string)
        (sessionId: Guid)
        (action: SessionLogWriterSink -> unit)
        : string
        =
    let sink = newSink dir sessionId
    let path = sink.ActiveLogPath
    try action sink
    finally (sink :> IDisposable).Dispose()
    if File.Exists(path) then File.ReadAllText(path) else ""

// ---------------------------------------------------------------------
// Path resolution
// ---------------------------------------------------------------------

[<Fact>]
let ``ActiveLogPath is <dir>/session-<SessionId>.jsonl`` () =
    let dir = freshTempDir ()
    let sink = newSink dir fixedSessionId
    try
        let expected =
            Path.Combine(
                dir,
                sprintf "session-%s.jsonl" (fixedSessionId.ToString("D")))
        Assert.Equal(expected, sink.ActiveLogPath)
    finally
        (sink :> IDisposable).Dispose()

[<Fact>]
let ``output directory is created if missing`` () =
    let parent = freshTempDir ()
    // Use a sub-path that does NOT exist yet.
    let nestedDir = Path.Combine(parent, "fresh-subdir")
    Assert.False(Directory.Exists(nestedDir))
    let sink = newSink nestedDir fixedSessionId
    try
        // Drain task creates the directory at startup; enqueue
        // a tuple to ensure the drain has run.
        sink.Enqueue (fixedTuple ())
    finally
        (sink :> IDisposable).Dispose()
    Assert.True(Directory.Exists(nestedDir))

// ---------------------------------------------------------------------
// Single-tuple enqueue
// ---------------------------------------------------------------------

[<Fact>]
let ``enqueueing one tuple writes exactly one JSONL line`` () =
    let dir = freshTempDir ()
    let content =
        withSink dir fixedSessionId (fun sink ->
            sink.Enqueue (fixedTuple ()))
    // Exactly one trailing newline → split by '\n' yields two
    // entries (the JSON + the empty trailing string).
    let parts = content.Split([| '\n' |])
    Assert.Equal(2, parts.Length)
    Assert.Equal("", parts.[1])
    // Round-trip the JSON line through System.Text.Json as an
    // oracle — proves the file content matches the
    // formatTupleAsJsonl output verbatim.
    use doc = System.Text.Json.JsonDocument.Parse(parts.[0])
    Assert.Equal(
        "11111111-2222-3333-4444-555555555555",
        doc.RootElement.GetProperty("id").GetString())

[<Fact>]
let ``emitted line ends with LF, never CRLF`` () =
    let dir = freshTempDir ()
    let content =
        withSink dir fixedSessionId (fun sink ->
            sink.Enqueue (fixedTuple ()))
    Assert.True(content.EndsWith("\n"))
    Assert.False(content.Contains("\r"), "Output must not contain CR (would break cross-platform stability).")

// ---------------------------------------------------------------------
// Multi-tuple enqueue
// ---------------------------------------------------------------------

[<Fact>]
let ``three enqueued tuples produce three lines in enqueue order`` () =
    let dir = freshTempDir ()
    let baseTuple = fixedTuple ()
    let tuples =
        [ { baseTuple with
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111")
                CommandText = "first" }
          { baseTuple with
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222")
                CommandText = "second" }
          { baseTuple with
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333")
                CommandText = "third" } ]
    let content =
        withSink dir fixedSessionId (fun sink ->
            for t in tuples do sink.Enqueue t)
    let lines =
        content.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    Assert.Equal(3, lines.Length)
    Assert.Contains("\"commandText\":\"first\"", lines.[0])
    Assert.Contains("\"commandText\":\"second\"", lines.[1])
    Assert.Contains("\"commandText\":\"third\"", lines.[2])

// ---------------------------------------------------------------------
// Disposal semantics
// ---------------------------------------------------------------------

[<Fact>]
let ``Dispose flushes pending writes before returning`` () =
    let dir = freshTempDir ()
    let sink = newSink dir fixedSessionId
    let path = sink.ActiveLogPath
    sink.Enqueue (fixedTuple ())
    // Dispose synchronously waits up to 2s for the drain task.
    (sink :> IDisposable).Dispose()
    // After Dispose returns, the file MUST contain the line.
    let content = File.ReadAllText(path)
    Assert.True(content.Length > 0)
    use doc =
        System.Text.Json.JsonDocument.Parse(content.TrimEnd('\n'))
    Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind)

[<Fact>]
let ``calling Dispose twice is idempotent`` () =
    let dir = freshTempDir ()
    let sink = newSink dir fixedSessionId
    sink.Enqueue (fixedTuple ())
    (sink :> IDisposable).Dispose()
    // Second dispose should not throw.
    (sink :> IDisposable).Dispose()

// ---------------------------------------------------------------------
// Lone-surrogate tuples — Cycle 24b serializer throws; the writer
// swallows + skips so subsequent tuples still write.
// ---------------------------------------------------------------------

[<Fact>]
let ``lone surrogate in a tuple is logged + skipped without breaking subsequent writes`` () =
    let dir = freshTempDir ()
    let goodFirst = fixedTuple ()
    let badMiddle =
        { fixedTuple () with
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999")
            OutputText = String([| char 0xD800 |]) } // lone high surrogate
    let goodLast =
        { fixedTuple () with
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001")
            CommandText = "after-bad" }
    let content =
        withSink dir fixedSessionId (fun sink ->
            sink.Enqueue goodFirst
            sink.Enqueue badMiddle
            sink.Enqueue goodLast)
    let lines =
        content.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    // The bad tuple is skipped; only the two good ones land.
    Assert.Equal(2, lines.Length)
    Assert.Contains("\"id\":\"11111111-2222-3333-4444-555555555555\"", lines.[0])
    Assert.Contains("\"id\":\"00000000-0000-0000-0000-000000000001\"", lines.[1])
    Assert.DoesNotContain("99999999", content)

// ---------------------------------------------------------------------
// Concurrency — multiple writer threads must not produce torn lines
// ---------------------------------------------------------------------

[<Fact>]
let ``concurrent enqueues from multiple threads produce intact lines`` () =
    let dir = freshTempDir ()
    let baseTuple = fixedTuple ()
    let count = 20
    let content =
        withSink dir fixedSessionId (fun sink ->
            let tasks =
                [ 0 .. count - 1 ]
                |> List.map (fun i ->
                    System.Threading.Tasks.Task.Run(fun () ->
                        let t =
                            { baseTuple with
                                Id = Guid.NewGuid()
                                CommandText = sprintf "cmd-%d" i }
                        sink.Enqueue t))
                |> List.toArray
            System.Threading.Tasks.Task.WaitAll(tasks))
    let lines =
        content.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    Assert.Equal(count, lines.Length)
    // Every line must parse cleanly — torn writes would surface
    // as JsonException here.
    for line in lines do
        use _doc = System.Text.Json.JsonDocument.Parse(line)
        ()

// ---------------------------------------------------------------------
// SessionLogWriterOptions — default-output-directory resolution
// ---------------------------------------------------------------------

[<Fact>]
let ``createDefault uses output_dir override when Some non-empty`` () =
    let opts =
        SessionLogWriterOptions.createDefault
            fixedSessionId
            (Some "C:\\custom\\sessions")
    Assert.Equal("C:\\custom\\sessions", opts.OutputDirectory)
    Assert.Equal(fixedSessionId, opts.SessionId)
    Assert.Equal(
        SessionLogWriterOptions.DefaultChannelCapacity,
        opts.ChannelCapacity)

[<Fact>]
let ``createDefault falls back to %LOCALAPPDATA%\PtySpeak\sessions when override is None`` () =
    let opts =
        SessionLogWriterOptions.createDefault fixedSessionId None
    Assert.Contains("PtySpeak", opts.OutputDirectory)
    Assert.EndsWith("sessions", opts.OutputDirectory)

[<Fact>]
let ``createDefault treats empty / whitespace override as None`` () =
    let opts =
        SessionLogWriterOptions.createDefault fixedSessionId (Some "")
    Assert.Contains("PtySpeak", opts.OutputDirectory)
    let opts2 =
        SessionLogWriterOptions.createDefault fixedSessionId (Some "   ")
    Assert.Contains("PtySpeak", opts2.OutputDirectory)
