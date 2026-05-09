module PtySpeak.Tests.Unit.SessionModelJsonlTests

open System
open System.Text.Json
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 24b — formatTupleAsJsonl behavioural pinning
// ---------------------------------------------------------------------
//
// formatTupleAsJsonl is the wire format for decades of on-disk
// session persistence. Tests pin every encoding decision so the
// byte-for-byte output cannot drift across F# / .NET versions
// without a CI-visible failure.
//
// Wire format reference: docs/SESSION-MODEL.md §"On-disk wire
// format (Cycle 24b)".
//
// Key invariants exercised:
//   * Field order is fixed and deterministic.
//   * "schemaVersion":1 is the FIRST key on every record.
//   * Option<T> -> null for None, value for Some.
//   * DateTime serialises with 100ns ticks (.fffffffZ).
//   * Sources is an array sorted by explicit boundary ordinal.
//   * BoundarySource is always a tagged object.
//   * ExtraParams iterates in ordinal-sorted key order.
//   * String escapes follow RFC 8259 + DEL (0x7F).
//   * Lone UTF-16 surrogates throw at emit time.
//   * Trailing terminator is exactly one '\n', never '\r\n'.
//   * Every emitted line parses cleanly as JSON (oracle test).

// ---------------------------------------------------------------------
// Test fixture builders
// ---------------------------------------------------------------------

let private fixedGuid =
    Guid.Parse("11111111-2222-3333-4444-555555555555")

let private t0 = DateTime(2026, 5, 9, 14, 23, 45, DateTimeKind.Utc)

/// Minimal SessionTuple — every field at a known fixed value.
/// Tests vary one field at a time off this baseline.
let private baseTuple : SessionModel.SessionTuple =
    { Id = fixedGuid
      CommandId = None
      ShellId = "powershell"
      PromptStartedAt = t0
      CommandStartedAt = None
      OutputStartedAt = None
      CommandFinishedAt = Some (t0.AddMilliseconds(100.0))
      PromptText = "PS>"
      CommandText = ""
      OutputText = ""
      ExitCode = None
      Sources = Map.empty
      ExtraParams = Map.empty }

/// Strip the trailing '\n' — handy when the caller wants the JSON
/// object as a String for JsonDocument.Parse.
let private stripNewline (s: string) : string =
    Assert.EndsWith("\n", s)
    s.Substring(0, s.Length - 1)

// ---------------------------------------------------------------------
// Field presence + ordering
// ---------------------------------------------------------------------

[<Fact>]
let ``minimal tuple emits all 14 fields in fixed order with schemaVersion first`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    let json = stripNewline line
    let expected =
        "{\"schemaVersion\":1,"
        + "\"id\":\"11111111-2222-3333-4444-555555555555\","
        + "\"commandId\":null,"
        + "\"shellId\":\"powershell\","
        + "\"promptStartedAt\":\"2026-05-09T14:23:45.0000000Z\","
        + "\"commandStartedAt\":null,"
        + "\"outputStartedAt\":null,"
        + "\"commandFinishedAt\":\"2026-05-09T14:23:45.1000000Z\","
        + "\"promptText\":\"PS>\","
        + "\"commandText\":\"\","
        + "\"outputText\":\"\","
        + "\"exitCode\":null,"
        + "\"sources\":[],"
        + "\"extraParams\":{}}"
    Assert.Equal(expected, json)

[<Fact>]
let ``schemaVersion is the first key on every record`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.StartsWith("{\"schemaVersion\":1,", line)

[<Fact>]
let ``JsonlSchemaVersion constant equals 1`` () =
    // Pinned: future schema changes increment this. Old files
    // remain readable; replay tools branch on the value.
    Assert.Equal(1, SessionModel.JsonlSchemaVersion)

// ---------------------------------------------------------------------
// Trailing terminator
// ---------------------------------------------------------------------

[<Fact>]
let ``emitted line ends with exactly one LF (never CRLF)`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.EndsWith("\n", line)
    Assert.False(line.Contains("\r"), "Output must not contain a CR (would corrupt byte stability on Windows).")

// ---------------------------------------------------------------------
// Guid format
// ---------------------------------------------------------------------

[<Fact>]
let ``Guid emits in lowercase D format (8-4-4-4-12)`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"id\":\"11111111-2222-3333-4444-555555555555\"", line)

// ---------------------------------------------------------------------
// Option<string> — CommandId None vs Some
// ---------------------------------------------------------------------

[<Fact>]
let ``commandId None emits the JSON null literal`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"commandId\":null,", line)

[<Fact>]
let ``commandId Some "abc" emits the quoted escaped string`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with CommandId = Some "abc-123" }
    Assert.Contains("\"commandId\":\"abc-123\",", line)

// ---------------------------------------------------------------------
// Option<DateTime> — CommandStartedAt / OutputStartedAt /
// CommandFinishedAt — None vs Some
// ---------------------------------------------------------------------

[<Fact>]
let ``commandStartedAt None emits the JSON null literal`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"commandStartedAt\":null,", line)

[<Fact>]
let ``commandStartedAt Some emits a quoted ISO-8601 7-tick UTC timestamp`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                CommandStartedAt = Some (t0.AddMilliseconds(50.0)) }
    Assert.Contains(
        "\"commandStartedAt\":\"2026-05-09T14:23:45.0500000Z\",",
        line)

[<Fact>]
let ``outputStartedAt None emits the JSON null literal`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"outputStartedAt\":null,", line)

[<Fact>]
let ``outputStartedAt Some emits a quoted timestamp`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                OutputStartedAt = Some (t0.AddMilliseconds(75.0)) }
    Assert.Contains(
        "\"outputStartedAt\":\"2026-05-09T14:23:45.0750000Z\",",
        line)

[<Fact>]
let ``commandFinishedAt None emits the JSON null literal`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with CommandFinishedAt = None }
    Assert.Contains("\"commandFinishedAt\":null,", line)

[<Fact>]
let ``commandFinishedAt Some emits a quoted timestamp`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains(
        "\"commandFinishedAt\":\"2026-05-09T14:23:45.1000000Z\",",
        line)

// ---------------------------------------------------------------------
// DateTime: 100ns tick precision (lossless from Windows clock)
// ---------------------------------------------------------------------

[<Fact>]
let ``DateTime emits with 7-digit fractional seconds (100ns ticks)`` () =
    // Construct a DateTime with a non-millisecond-aligned tick
    // value to prove the format preserves sub-millisecond precision.
    let oddTicks =
        DateTime(2026, 5, 9, 14, 23, 45, DateTimeKind.Utc)
            .AddTicks(1234567L)
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with PromptStartedAt = oddTicks }
    Assert.Contains(
        "\"promptStartedAt\":\"2026-05-09T14:23:45.1234567Z\",",
        line)

[<Fact>]
let ``DateTime in local kind is converted to UTC defensively`` () =
    // PromptStartedAt is documented as UTC but be defensive: if a
    // future caller hands us local time, the formatter must still
    // produce a UTC timestamp.
    let local =
        DateTime(2026, 5, 9, 14, 23, 45, DateTimeKind.Local)
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with PromptStartedAt = local }
    // The exact UTC value depends on the test machine's timezone;
    // assert the format suffix is always 'Z'.
    let extracted =
        let idx = line.IndexOf("\"promptStartedAt\":\"")
        let start = idx + "\"promptStartedAt\":\"".Length
        let endIdx = line.IndexOf('"', start)
        line.Substring(start, endIdx - start)
    Assert.EndsWith("Z", extracted)
    Assert.Equal(28, extracted.Length)   // "yyyy-MM-ddTHH:mm:ss.fffffffZ" length

// ---------------------------------------------------------------------
// Option<int> — ExitCode None vs Some
// ---------------------------------------------------------------------

[<Fact>]
let ``exitCode None emits the JSON null literal`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"exitCode\":null,", line)

[<Fact>]
let ``exitCode Some 0 emits the unquoted integer 0`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with ExitCode = Some 0 }
    Assert.Contains("\"exitCode\":0,", line)

[<Fact>]
let ``exitCode Some -1 emits a signed integer`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with ExitCode = Some -1 }
    Assert.Contains("\"exitCode\":-1,", line)

[<Fact>]
let ``exitCode Some Int32.MaxValue emits the unquoted integer`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with ExitCode = Some Int32.MaxValue }
    Assert.Contains("\"exitCode\":2147483647,", line)

// ---------------------------------------------------------------------
// BoundarySource cases (tagged-object always)
// ---------------------------------------------------------------------

[<Fact>]
let ``BoundarySource Osc133 emits the bare tagged object`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.PromptStart, BoundarySource.Osc133 ] }
    Assert.Contains(
        "\"sources\":[{\"boundary\":\"PromptStart\",\"source\":{\"kind\":\"Osc133\"}}],",
        line)

[<Fact>]
let ``BoundarySource HeuristicPromptRegex emits stabilityMs payload`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.PromptStart,
                          BoundarySource.HeuristicPromptRegex 500 ] }
    Assert.Contains(
        "\"source\":{\"kind\":\"HeuristicPromptRegex\",\"stabilityMs\":500}",
        line)

[<Fact>]
let ``BoundarySource HeuristicPromptRegex with stabilityMs 0 still emits the field`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.PromptStart,
                          BoundarySource.HeuristicPromptRegex 0 ] }
    Assert.Contains("\"stabilityMs\":0", line)

[<Fact>]
let ``BoundarySource HeuristicClaudeInkBox emits the bare tagged object`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.PromptStart,
                          BoundarySource.HeuristicClaudeInkBox ] }
    Assert.Contains(
        "\"source\":{\"kind\":\"HeuristicClaudeInkBox\"}",
        line)

// ---------------------------------------------------------------------
// Sources array — ordering, payload-less BoundaryKind names
// ---------------------------------------------------------------------

[<Fact>]
let ``Sources entries sort by explicit boundaryOrdinal regardless of insertion order`` () =
    // Insert in reverse order; expect output sorted PromptStart,
    // CommandStart, OutputStart, CommandFinished.
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.CommandFinished (Some 0), BoundarySource.Osc133
                          BoundaryKind.OutputStart, BoundarySource.Osc133
                          BoundaryKind.CommandStart, BoundarySource.Osc133
                          BoundaryKind.PromptStart, BoundarySource.Osc133 ] }
    let expectedSources =
        "\"sources\":["
        + "{\"boundary\":\"PromptStart\",\"source\":{\"kind\":\"Osc133\"}},"
        + "{\"boundary\":\"CommandStart\",\"source\":{\"kind\":\"Osc133\"}},"
        + "{\"boundary\":\"OutputStart\",\"source\":{\"kind\":\"Osc133\"}},"
        + "{\"boundary\":\"CommandFinished\",\"source\":{\"kind\":\"Osc133\"}}"
        + "],"
    Assert.Contains(expectedSources, line)

[<Fact>]
let ``Sources entry for CommandFinished uses payload-less boundary name`` () =
    // CommandFinished carries an int option payload; the JSON
    // "boundary" value is the bare DU case name (the actual exit
    // code lives on tuple.ExitCode and isn't duplicated here).
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.CommandFinished (Some 137),
                          BoundarySource.Osc133 ] }
    Assert.Contains(
        "{\"boundary\":\"CommandFinished\",\"source\":{\"kind\":\"Osc133\"}}",
        line)
    Assert.DoesNotContain("137", line.Substring(line.IndexOf("\"sources\"")))

[<Fact>]
let ``empty Sources emits an empty array`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.Contains("\"sources\":[],", line)

// ---------------------------------------------------------------------
// ExtraParams — empty / populated / ordering / non-ASCII
// ---------------------------------------------------------------------

[<Fact>]
let ``empty ExtraParams emits an empty object`` () =
    let line = SessionModel.formatTupleAsJsonl baseTuple
    Assert.EndsWith("\"extraParams\":{}}\n", line)

[<Fact>]
let ``ExtraParams keys sort in ordinal string order regardless of insertion order`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                ExtraParams =
                    Map.ofList
                        [ "z-last", "1"
                          "a-first", "2"
                          "m-middle", "3" ] }
    Assert.Contains(
        "\"extraParams\":{\"a-first\":\"2\",\"m-middle\":\"3\",\"z-last\":\"1\"}",
        line)

[<Fact>]
let ``ExtraParams keys with non-ASCII characters round-trip via UTF-8 passthrough`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                ExtraParams = Map.ofList [ "café", "value" ] }
    Assert.Contains("\"extraParams\":{\"café\":\"value\"}", line)

// ---------------------------------------------------------------------
// String escape rules
// ---------------------------------------------------------------------

[<Fact>]
let ``double-quote escapes as backslash double-quote`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with CommandText = "say \"hello\"" }
    Assert.Contains("\"commandText\":\"say \\\"hello\\\"\"", line)

[<Fact>]
let ``backslash escapes as double backslash`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with CommandText = "C:\\Users" }
    Assert.Contains("\"commandText\":\"C:\\\\Users\"", line)

[<Fact>]
let ``newline escapes as backslash-n`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "line1\nline2" }
    Assert.Contains("\"outputText\":\"line1\\nline2\"", line)

[<Fact>]
let ``carriage return escapes as backslash-r`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "a\rb" }
    Assert.Contains("\"outputText\":\"a\\rb\"", line)

[<Fact>]
let ``tab and other named control chars use named escapes`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "\t\b\f" }
    Assert.Contains("\"outputText\":\"\\t\\b\\f\"", line)

[<Fact>]
let ``NUL byte escapes as u0000`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = " " }
    Assert.Contains("\"outputText\":\"\\u0000\"", line)

[<Fact>]
let ``raw ANSI ESC (0x1B) escapes as u001b`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                OutputText = "[31mred[0m" }
    Assert.Contains(
        "\"outputText\":\"\\u001b[31mred\\u001b[0m\"",
        line)

[<Fact>]
let ``DEL (0x7F) escapes as u007f (deliberate superset of RFC 8259)`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "" }
    Assert.Contains("\"outputText\":\"\\u007f\"", line)

[<Fact>]
let ``forward slash passes through unescaped (RFC 8259 minimum)`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with CommandText = "git/branch" }
    Assert.Contains("\"commandText\":\"git/branch\"", line)

[<Fact>]
let ``C1 control range (0x80-0x9F) passes through as UTF-8`` () =
    // 0x85 is NEL (next line) — a C1 control. RFC 8259 only
    // mandates escape for U+0000-U+001F, so it passes through.
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "" }
    Assert.DoesNotContain("\\u0085", line)

[<Fact>]
let ``multi-byte UTF-8 characters pass through unescaped`` () =
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with PromptText = "café" }
    Assert.Contains("\"promptText\":\"café\"", line)

[<Fact>]
let ``emoji surrogate pairs pass through as the original UTF-16 sequence`` () =
    // U+1F44B WAVING HAND SIGN; high+low surrogate pair D83D + DC4B
    let line =
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = "👋" }
    Assert.Contains("\"outputText\":\"👋\"", line)

// ---------------------------------------------------------------------
// Lone surrogates throw (loud failure beats silent corruption)
// ---------------------------------------------------------------------

[<Fact>]
let ``lone high surrogate in a string throws InvalidOperationException`` () =
    let loneHigh = String([| char 0xD83D |])
    Assert.Throws<InvalidOperationException>(fun () ->
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = loneHigh }
        |> ignore) |> ignore

[<Fact>]
let ``lone low surrogate in a string throws InvalidOperationException`` () =
    let loneLow = String([| char 0xDC4B |])
    Assert.Throws<InvalidOperationException>(fun () ->
        SessionModel.formatTupleAsJsonl
            { baseTuple with OutputText = loneLow }
        |> ignore) |> ignore

// ---------------------------------------------------------------------
// Determinism: byte-identical output regardless of Map insertion order
// ---------------------------------------------------------------------

[<Fact>]
let ``Sources insertion order does not affect output bytes`` () =
    let a =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.PromptStart, BoundarySource.Osc133
                          BoundaryKind.CommandStart, BoundarySource.Osc133 ] }
    let b =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                Sources =
                    Map.ofList
                        [ BoundaryKind.CommandStart, BoundarySource.Osc133
                          BoundaryKind.PromptStart, BoundarySource.Osc133 ] }
    Assert.Equal(a, b)

[<Fact>]
let ``ExtraParams insertion order does not affect output bytes`` () =
    let a =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                ExtraParams =
                    Map.ofList
                        [ "k1", "v1"
                          "k2", "v2"
                          "k3", "v3" ] }
    let b =
        SessionModel.formatTupleAsJsonl
            { baseTuple with
                ExtraParams =
                    Map.ofList
                        [ "k3", "v3"
                          "k1", "v1"
                          "k2", "v2" ] }
    Assert.Equal(a, b)

// ---------------------------------------------------------------------
// JsonDocument oracle — every emitted line must be valid JSON.
// Catches escape bugs the per-field tests miss.
// ---------------------------------------------------------------------

[<Fact>]
let ``every emitted line parses cleanly via System.Text.Json.JsonDocument`` () =
    let trickyTuple =
        { baseTuple with
            CommandId = Some "cmd-with-\"quotes\""
            ShellId = "weird/shell\\name"
            CommandStartedAt = Some (t0.AddMilliseconds(50.0))
            OutputStartedAt = Some (t0.AddMilliseconds(75.0))
            PromptText = "PS C:\\Users\\test>"
            CommandText = "echo \"hello\nworld\"\twith\ttabs"
            OutputText =
                "[31mred[0m\nline2\r\n 👋"
            ExitCode = Some 137
            Sources =
                Map.ofList
                    [ BoundaryKind.PromptStart, BoundarySource.Osc133
                      BoundaryKind.CommandStart,
                          BoundarySource.HeuristicPromptRegex 500
                      BoundaryKind.OutputStart,
                          BoundarySource.HeuristicClaudeInkBox
                      BoundaryKind.CommandFinished (Some 137),
                          BoundarySource.Osc133 ]
            ExtraParams =
                Map.ofList
                    [ "k", "value with \"quotes\""
                      "cl", "extra\nline"
                      "café", "ünïcödé" ] }
    let line = SessionModel.formatTupleAsJsonl trickyTuple
    let json = stripNewline line
    // Should not throw — that's the assertion.
    use doc = JsonDocument.Parse(json)
    Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind)
    Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32())
    Assert.Equal("weird/shell\\name", doc.RootElement.GetProperty("shellId").GetString())
    Assert.Equal(137, doc.RootElement.GetProperty("exitCode").GetInt32())
