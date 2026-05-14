module PtySpeak.Tests.Unit.DiagnosticExtractsTests

open System
open System.IO
open System.Text
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 43a — DiagnosticExtracts pure-helper tests.
// ---------------------------------------------------------------------
//
// Pins the public contract of every helper exposed by
// `Terminal.Core/DiagnosticExtracts.fs`. The orchestrator in
// `Terminal.App/Program.fs` depends on these contracts for the
// new `Diagnostics → Copy latest diagnostic bundle`,
// `Diagnostics → Grep diagnostics...`, and `Diagnostics → Extract`
// menu items; a regression here can silently break the chunking
// workflow without surfacing in any NVDA test until the
// maintainer tries to copy a chunk and gets the wrong content.

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

/// Build a deterministic UTF-8 log file in a unique temp directory
/// so concurrent tests don't trip on each other. Returns the path;
/// caller is responsible for deleting (xunit's per-fact isolation
/// + IDisposable would be cleaner but the test surface is small
/// enough that explicit File.Delete works).
let private writeTempLog (content: string) : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-extracts-test-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "active.log")
    File.WriteAllText(path, content, Encoding.UTF8)
    path

let private fixedNow : DateTimeOffset =
    DateTimeOffset(2026, 5, 11, 14, 32, 17, 456, TimeSpan.Zero)

// ---------------------------------------------------------------------
// slugifyForFilename
// ---------------------------------------------------------------------

[<Fact>]
let ``slugifyForFilename collapses non-alnum runs to single dashes`` () =
    let slug = DiagnosticExtracts.slugifyForFilename 40 "Hello, world!  foo"
    Assert.Equal("hello-world-foo", slug)

[<Fact>]
let ``slugifyForFilename trims leading and trailing dashes`` () =
    let slug = DiagnosticExtracts.slugifyForFilename 40 "   ?? bang ??   "
    Assert.Equal("bang", slug)

[<Fact>]
let ``slugifyForFilename caps length and produces untitled for empty input`` () =
    let long = String.replicate 200 "a"
    let slug = DiagnosticExtracts.slugifyForFilename 10 long
    Assert.Equal(10, slug.Length)
    Assert.Equal("untitled", DiagnosticExtracts.slugifyForFilename 40 "")
    Assert.Equal("untitled", DiagnosticExtracts.slugifyForFilename 40 "!!!!")

[<Fact>]
let ``slugifyForFilename lowercases letters`` () =
    let slug = DiagnosticExtracts.slugifyForFilename 40 "MixedCASE"
    Assert.Equal("mixedcase", slug)

// ---------------------------------------------------------------------
// extractFilePath
// ---------------------------------------------------------------------

[<Fact>]
let ``extractFilePath embeds slugged name and yyyy-MM-dd timestamp`` () =
    let path = DiagnosticExtracts.extractFilePath fixedNow "MyExtractor"
    let leaf = Path.GetFileName(path)
    Assert.Equal("myextractor-2026-05-11-14-32-17-456.txt", leaf)
    Assert.EndsWith(".txt", path)
    Assert.Contains("extracts", path)

// ---------------------------------------------------------------------
// parseLogTimestamp
// ---------------------------------------------------------------------

[<Fact>]
let ``parseLogTimestamp accepts the exact FileLogger ISO format`` () =
    let line = "2026-05-11T14:32:17.456Z [INF] [Category] message body"
    let parsed = DiagnosticExtracts.parseLogTimestamp line
    Assert.True(parsed.IsSome)
    let dt = parsed.Value
    Assert.Equal(2026, dt.Year)
    Assert.Equal(5, dt.Month)
    Assert.Equal(11, dt.Day)
    Assert.Equal(14, dt.Hour)
    Assert.Equal(456, dt.Millisecond)

[<Fact>]
let ``parseLogTimestamp returns None for short or malformed lines`` () =
    Assert.Equal(None, DiagnosticExtracts.parseLogTimestamp "")
    Assert.Equal(None, DiagnosticExtracts.parseLogTimestamp "short")
    Assert.Equal(None, DiagnosticExtracts.parseLogTimestamp "not-a-timestamp line continuation")
    Assert.Equal(None, DiagnosticExtracts.parseLogTimestamp "2026-99-99T99:99:99.999Z bad date")

// ---------------------------------------------------------------------
// tailLogLines
// ---------------------------------------------------------------------

[<Fact>]
let ``tailLogLines returns the last N lines in order`` () =
    let body =
        seq { for i in 1 .. 10 -> sprintf "line-%d" i }
        |> String.concat "\n"
    let path = writeTempLog (body + "\n")
    try
        let tail = DiagnosticExtracts.tailLogLines path 3
        Assert.Equal("line-8\nline-9\nline-10", tail)
    finally
        File.Delete(path)

[<Fact>]
let ``tailLogLines tolerates fewer lines than requested`` () =
    let path = writeTempLog "only-one\n"
    try
        let tail = DiagnosticExtracts.tailLogLines path 10
        Assert.Equal("only-one", tail)
    finally
        File.Delete(path)

[<Fact>]
let ``tailLogLines returns a placeholder for missing files`` () =
    let path =
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log")
    let tail = DiagnosticExtracts.tailLogLines path 5
    Assert.StartsWith("(file not present:", tail)

// ---------------------------------------------------------------------
// filterLogLinesSince
// ---------------------------------------------------------------------

[<Fact>]
let ``filterLogLinesSince keeps timestamped lines at or after cutoff`` () =
    let body =
        String.Join(
            "\n",
            [| "2026-05-11T14:00:00.000Z [INF] [Cat] before cutoff"
               "2026-05-11T14:30:00.000Z [INF] [Cat] at cutoff"
               "2026-05-11T14:45:00.000Z [INF] [Cat] after cutoff" |])
    let path = writeTempLog body
    try
        let cutoff =
            DateTimeOffset(2026, 5, 11, 14, 30, 0, TimeSpan.Zero)
        let result = DiagnosticExtracts.filterLogLinesSince path cutoff
        Assert.Contains("at cutoff", result)
        Assert.Contains("after cutoff", result)
        Assert.DoesNotContain("before cutoff", result)
    finally
        File.Delete(path)

[<Fact>]
let ``filterLogLinesSince keeps continuation lines for retained entries`` () =
    let body =
        String.Join(
            "\n",
            [| "2026-05-11T14:00:00.000Z [INF] [Cat] before cutoff"
               "  stack frame for before — should be dropped"
               "2026-05-11T14:45:00.000Z [ERR] [Cat] after cutoff"
               "  stack frame for after — should be kept" |])
    let path = writeTempLog body
    try
        let cutoff =
            DateTimeOffset(2026, 5, 11, 14, 30, 0, TimeSpan.Zero)
        let result = DiagnosticExtracts.filterLogLinesSince path cutoff
        Assert.Contains("after cutoff", result)
        Assert.Contains("stack frame for after", result)
        Assert.DoesNotContain("stack frame for before", result)
    finally
        File.Delete(path)

// ---------------------------------------------------------------------
// filterLogBySemantic
// ---------------------------------------------------------------------

[<Fact>]
let ``filterLogBySemantic matches any token in the supplied list`` () =
    let body =
        String.Join(
            "\n",
            [| "2026-05-11T14:00:00.000Z [INF] [Cat] OutputEvent. Semantic=StreamChunk Payload=hello"
               "2026-05-11T14:00:01.000Z [INF] [Cat] OutputEvent. Semantic=ErrorLine Payload=oops"
               "2026-05-11T14:00:02.000Z [INF] [Cat] OutputEvent. Semantic=WarningLine Payload=warn"
               "2026-05-11T14:00:03.000Z [INF] [Cat] OutputEvent. Semantic=SpinnerTick Payload=spin" |])
    let path = writeTempLog body
    try
        let result =
            DiagnosticExtracts.filterLogBySemantic
                path
                [ "ErrorLine"; "WarningLine" ]
        Assert.Contains("Semantic=ErrorLine", result)
        Assert.Contains("Semantic=WarningLine", result)
        Assert.DoesNotContain("Semantic=StreamChunk", result)
        Assert.DoesNotContain("Semantic=SpinnerTick", result)
    finally
        File.Delete(path)

[<Fact>]
let ``filterLogBySemantic returns empty string for empty token list`` () =
    let path = writeTempLog "anything"
    try
        let result = DiagnosticExtracts.filterLogBySemantic path []
        Assert.Equal("", result)
    finally
        File.Delete(path)

// ---------------------------------------------------------------------
// truncateForClipboard
// ---------------------------------------------------------------------

[<Fact>]
let ``truncateForClipboard returns content unchanged when within budget`` () =
    let content = "short content"
    let (out, truncated) = DiagnosticExtracts.truncateForClipboard 1000 content
    Assert.Equal(content, out)
    Assert.False(truncated)

[<Fact>]
let ``truncateForClipboard caps oversize content with footer`` () =
    let big = String.replicate 200 "x"
    let (out, truncated) = DiagnosticExtracts.truncateForClipboard 100 big
    Assert.True(truncated)
    Assert.True(Encoding.UTF8.GetByteCount(out) <= 100)
    Assert.Contains("truncated", out)

[<Fact>]
let ``truncateForClipboard handles maxBytes smaller than footer gracefully`` () =
    let (out, truncated) = DiagnosticExtracts.truncateForClipboard 5 "anything"
    Assert.True(truncated)
    Assert.Equal("", out)

// ---------------------------------------------------------------------
// formatBytesForAnnounce
// ---------------------------------------------------------------------

[<Fact>]
let ``formatBytesForAnnounce uses bytes-kilobytes-megabytes scales`` () =
    Assert.Equal("0 bytes", DiagnosticExtracts.formatBytesForAnnounce 0)
    Assert.Equal("512 bytes", DiagnosticExtracts.formatBytesForAnnounce 512)
    Assert.Contains("kilobytes", DiagnosticExtracts.formatBytesForAnnounce 2048)
    Assert.Contains("megabytes", DiagnosticExtracts.formatBytesForAnnounce (3 * 1024 * 1024))

// ---------------------------------------------------------------------
// formatExtractHeader
// ---------------------------------------------------------------------

[<Fact>]
let ``formatExtractHeader carries extractor name, source, and body`` () =
    let header =
        DiagnosticExtracts.formatExtractHeader
            fixedNow
            "MyExtractor"
            "the source description"
            "body line 1\nbody line 2"
    Assert.Contains("pty-speak extract — MyExtractor", header)
    Assert.Contains("Source: the source description", header)
    Assert.Contains("Captured: 2026-05-11 14:32:17 UTC", header)
    Assert.Contains("body line 1", header)
    Assert.Contains("body line 2", header)
    Assert.Contains("2 lines", header)

// ---------------------------------------------------------------------
// extractByTest (Cycle 47 follow-up)
// ---------------------------------------------------------------------

[<Fact>]
let ``extractByTest returns body between matching START and END markers`` () =
    let content =
        String.concat "\n"
            [ "C:\\Users\\dev> test-01-echo.cmd"
              "=== PTYSPEAK-TEST-START: test-01-echo ==="
              "Echo test follows: three numbered lines then a final message."
              "Line 1 of 3."
              "=== PTYSPEAK-TEST-END: test-01-echo ==="
              "C:\\Users\\dev>" ]
    let extracted = DiagnosticExtracts.extractByTest "test-01-echo" content
    Assert.Contains("Echo test follows", extracted)
    Assert.Contains("Line 1 of 3.", extracted)
    Assert.DoesNotContain("PTYSPEAK-TEST-START", extracted)
    Assert.DoesNotContain("PTYSPEAK-TEST-END", extracted)
    Assert.DoesNotContain("C:\\Users\\dev>", extracted)

[<Fact>]
let ``extractByTest returns placeholder when no runs found`` () =
    let content = "some session output\nno markers here\n"
    let extracted = DiagnosticExtracts.extractByTest "test-01-echo" content
    Assert.Contains("no runs of test-01-echo found", extracted)

[<Fact>]
let ``extractByTest returns placeholder for empty content`` () =
    let extracted = DiagnosticExtracts.extractByTest "test-01-echo" ""
    Assert.Contains("no runs of test-01-echo found", extracted)

[<Fact>]
let ``extractByTest returns all runs with run-number dividers when multiple`` () =
    let content =
        String.concat "\n"
            [ "=== PTYSPEAK-TEST-START: test-04-yes-no ==="
              "first run body"
              "=== PTYSPEAK-TEST-END: test-04-yes-no ==="
              "intervening session noise"
              "=== PTYSPEAK-TEST-START: test-04-yes-no ==="
              "second run body"
              "=== PTYSPEAK-TEST-END: test-04-yes-no ===" ]
    let extracted = DiagnosticExtracts.extractByTest "test-04-yes-no" content
    Assert.Contains("first run body", extracted)
    Assert.Contains("second run body", extracted)
    Assert.Contains("run 1 of 2", extracted)
    Assert.Contains("run 2 of 2", extracted)
    Assert.DoesNotContain("intervening session noise", extracted)

[<Fact>]
let ``extractByTest ignores bracket markers for other test ids`` () =
    let content =
        String.concat "\n"
            [ "=== PTYSPEAK-TEST-START: test-02-text-input ==="
              "wrong-test body"
              "=== PTYSPEAK-TEST-END: test-02-text-input ===" ]
    let extracted = DiagnosticExtracts.extractByTest "test-01-echo" content
    Assert.Contains("no runs of test-01-echo found", extracted)

[<Fact>]
let ``extractByTest surfaces in-flight run when END marker is missing`` () =
    let content =
        String.concat "\n"
            [ "=== PTYSPEAK-TEST-START: test-07-progress ==="
              "tick 1"
              "tick 2"
              "(extractor pressed before END marker fired)" ]
    let extracted = DiagnosticExtracts.extractByTest "test-07-progress" content
    Assert.Contains("tick 1", extracted)
    Assert.Contains("tick 2", extracted)
    Assert.Contains("still in flight", extracted)

[<Fact>]
let ``extractByTest tolerates trailing CR on marker lines`` () =
    // ContentHistory tail can carry CRLF line endings when the
    // PTY emits cmd's native CRLF; the extractor must match on
    // the trimmed line content, not on the literal bytes.
    let content = "=== PTYSPEAK-TEST-START: test-01-echo ===\r\nhi\r\n=== PTYSPEAK-TEST-END: test-01-echo ===\r\n"
    let extracted = DiagnosticExtracts.extractByTest "test-01-echo" content
    Assert.Contains("hi", extracted)
    Assert.DoesNotContain("no runs", extracted)
