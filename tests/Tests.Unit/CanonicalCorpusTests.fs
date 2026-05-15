module PtySpeak.Tests.Unit.CanonicalCorpusTests

open Xunit
open Terminal.Core

/// Cycle 38a — pins the CanonicalCorpus parser contract per
/// `docs/PROJECT-PLAN-2026-05-09.md` Section 18.3.5.
///
/// The 10 facts cover required fields, optional v2 extensions,
/// missing-field errors, unknown-SemanticCategory errors, the
/// scenario-result formatter shape, and the empty-document edge
/// case. Tests use `parseFromString` directly with inline TOML
/// strings; the file-I/O `loadCanonicalCorpus` wrapper is exercised
/// only indirectly (it's a thin `File.ReadAllText` over
/// `parseFromString`).

// =====================================================================
// Required-field round-trip
// =====================================================================

[<Fact>]
let ``parseFromString preserves all required v1 fields round-trip`` () =
    let toml =
        """
        [[scenario]]
        id = "test.required"
        shell = "cmd"
        description = "narrative"
        command = "echo hi"
        must_include = ["StreamChunk"]
        must_not_include = ["ErrorLine", "WarningLine"]
        quiescence_ms = 250
        timeout_ms = 2000
        """
    match CanonicalCorpus.parseFromString toml with
    | Error e -> Assert.Fail(sprintf "expected Ok, got Error: %s" e)
    | Ok scenarios ->
        Assert.Equal(1, scenarios.Length)
        let s = scenarios.[0]
        Assert.Equal("test.required", s.Id)
        Assert.Equal("cmd", s.Shell)
        Assert.Equal("narrative", s.Description)
        Assert.Equal("echo hi", s.Command)
        Assert.Equal<SemanticCategory[]>(
            [| SemanticCategory.StreamChunk |],
            s.MustInclude)
        Assert.Equal<SemanticCategory[]>(
            [| SemanticCategory.ErrorLine; SemanticCategory.WarningLine |],
            s.MustNotInclude)
        Assert.Equal(250, s.QuiescenceMs)
        Assert.Equal(2000, s.TimeoutMs)

// =====================================================================
// Optional-field round-trip
// =====================================================================

[<Fact>]
let ``parseFromString preserves all optional v2 extension fields`` () =
    let toml =
        """
        [[scenario]]
        id = "test.optional"
        shell = "powershell"
        command = "Write-Host hi"
        must_include = ["StreamChunk"]
        must_not_include = []
        setup_command = "Write-Host setup"
        expected_payload_regex = ["^hi", "trailing\\.dot$"]
        expected_pane_routing = "current_output"
        notes = "captures every optional field"

        [scenario.expected_session_tuple]
        command_text = "Write-Host hi"
        output_text = "hi"
        exit_code = 0
        """
    match CanonicalCorpus.parseFromString toml with
    | Error e -> Assert.Fail(sprintf "expected Ok, got Error: %s" e)
    | Ok scenarios ->
        Assert.Equal(1, scenarios.Length)
        let s = scenarios.[0]
        Assert.Equal(Some "Write-Host setup", s.SetupCommand)
        Assert.Equal<string[]>(
            [| "^hi"; "trailing\\.dot$" |],
            s.ExpectedPayloadRegex)
        Assert.Equal(
            Some CanonicalCorpus.CurrentOutput,
            s.ExpectedPaneRouting)
        Assert.Equal(Some "captures every optional field", s.Notes)
        match s.ExpectedIOCell with
        | None -> Assert.Fail("expected expected_session_tuple to parse")
        | Some t ->
            Assert.Equal(Some "Write-Host hi", t.CommandText)
            Assert.Equal(Some "hi", t.OutputText)
            Assert.Equal(Some 0, t.ExitCode)

// =====================================================================
// Missing-required-field errors
// =====================================================================

[<Fact>]
let ``parseFromString fails when required field id is missing`` () =
    let toml =
        """
        [[scenario]]
        shell = "cmd"
        command = "echo"
        must_include = ["StreamChunk"]
        must_not_include = []
        """
    match CanonicalCorpus.parseFromString toml with
    | Ok _ -> Assert.Fail("expected Error, got Ok")
    | Error e -> Assert.Contains("id", e)

[<Fact>]
let ``parseFromString fails when required field command is missing`` () =
    let toml =
        """
        [[scenario]]
        id = "test.no.command"
        shell = "cmd"
        must_include = ["StreamChunk"]
        must_not_include = []
        """
    match CanonicalCorpus.parseFromString toml with
    | Ok _ -> Assert.Fail("expected Error, got Ok")
    | Error e -> Assert.Contains("command", e)

// =====================================================================
// Unknown-SemanticCategory error
// =====================================================================

[<Fact>]
let ``parseFromString fails on unknown SemanticCategory name`` () =
    let toml =
        """
        [[scenario]]
        id = "test.bad.semantic"
        shell = "cmd"
        command = "echo"
        must_include = ["NotARealCategory"]
        must_not_include = []
        """
    match CanonicalCorpus.parseFromString toml with
    | Ok _ -> Assert.Fail("expected Error, got Ok")
    | Error e ->
        Assert.Contains("NotARealCategory", e)
        Assert.Contains("unknown SemanticCategory", e)

// =====================================================================
// Unknown pane-routing error
// =====================================================================

[<Fact>]
let ``parseFromString fails on unknown expected_pane_routing value`` () =
    let toml =
        """
        [[scenario]]
        id = "test.bad.pane"
        shell = "cmd"
        command = "echo"
        must_include = []
        must_not_include = []
        expected_pane_routing = "sidebar"
        """
    match CanonicalCorpus.parseFromString toml with
    | Ok _ -> Assert.Fail("expected Error, got Ok")
    | Error e ->
        Assert.Contains("sidebar", e)
        Assert.Contains("expected_pane_routing", e)

// =====================================================================
// Empty document
// =====================================================================

[<Fact>]
let ``parseFromString returns empty array for document with no scenarios`` () =
    match CanonicalCorpus.parseFromString "" with
    | Error e -> Assert.Fail(sprintf "expected Ok empty, got Error: %s" e)
    | Ok scenarios -> Assert.Empty(scenarios)

[<Fact>]
let ``parseFromString returns empty array for document with only comments`` () =
    let toml = "# only a comment\n# nothing else\n"
    match CanonicalCorpus.parseFromString toml with
    | Error e -> Assert.Fail(sprintf "expected Ok empty, got Error: %s" e)
    | Ok scenarios -> Assert.Empty(scenarios)

// =====================================================================
// Multiple scenarios
// =====================================================================

[<Fact>]
let ``parseFromString parses multiple scenarios in order`` () =
    let toml =
        """
        [[scenario]]
        id = "first"
        shell = "cmd"
        command = "echo a"
        must_include = ["StreamChunk"]
        must_not_include = []

        [[scenario]]
        id = "second"
        shell = "powershell"
        command = "Write-Host b"
        must_include = ["StreamChunk"]
        must_not_include = ["ErrorLine"]
        """
    match CanonicalCorpus.parseFromString toml with
    | Error e -> Assert.Fail(sprintf "expected Ok, got Error: %s" e)
    | Ok scenarios ->
        Assert.Equal(2, scenarios.Length)
        Assert.Equal("first", scenarios.[0].Id)
        Assert.Equal("cmd", scenarios.[0].Shell)
        Assert.Equal("second", scenarios.[1].Id)
        Assert.Equal("powershell", scenarios.[1].Shell)

// =====================================================================
// Default values for missing optional integers
// =====================================================================

[<Fact>]
let ``parseFromString applies default quiescence_ms and timeout_ms when omitted`` () =
    let toml =
        """
        [[scenario]]
        id = "test.defaults"
        shell = "cmd"
        command = "echo"
        must_include = []
        must_not_include = []
        """
    match CanonicalCorpus.parseFromString toml with
    | Error e -> Assert.Fail(sprintf "expected Ok, got Error: %s" e)
    | Ok scenarios ->
        Assert.Equal(1, scenarios.Length)
        Assert.Equal(200, scenarios.[0].QuiescenceMs)
        Assert.Equal(1500, scenarios.[0].TimeoutMs)

// =====================================================================
// formatScenarioResult / formatCorpusResultsForBundle shape
// =====================================================================

[<Fact>]
let ``formatScenarioResult renders PASS scenario with id, elapsed ms, and observed line`` () =
    let scenario : CanonicalCorpus.Scenario =
        { Id = "fixture.pass"
          Shell = "cmd"
          Description = ""
          Command = "echo hi"
          MustInclude = [| SemanticCategory.StreamChunk |]
          MustNotInclude = [| SemanticCategory.ErrorLine |]
          QuiescenceMs = 200
          TimeoutMs = 1500
          SetupCommand = None
          ExpectedPayloadRegex = [||]
          ExpectedIOCell = None
          ExpectedPaneRouting = None
          Notes = None }
    let result : CanonicalCorpus.ScenarioResult =
        { Scenario = scenario
          Outcome = CanonicalCorpus.Pass
          ObservedSemantics = [| SemanticCategory.StreamChunk |]
          ObservedPayloads = [| "hi" |]
          ElapsedMs = 147 }
    let formatted = CanonicalCorpus.formatScenarioResult result
    Assert.Contains("[PASS] fixture.pass (147ms)", formatted)
    Assert.Contains("must_include: StreamChunk", formatted)
    Assert.Contains("must_not_include: ErrorLine", formatted)
    Assert.Contains("observed: [StreamChunk]", formatted)

[<Fact>]
let ``formatCorpusResultsForBundle includes the pass-fail summary header`` () =
    let scenario : CanonicalCorpus.Scenario =
        { Id = "summary.test"
          Shell = "cmd"
          Description = ""
          Command = "echo"
          MustInclude = [||]
          MustNotInclude = [||]
          QuiescenceMs = 200
          TimeoutMs = 1500
          SetupCommand = None
          ExpectedPayloadRegex = [||]
          ExpectedIOCell = None
          ExpectedPaneRouting = None
          Notes = None }
    let pass : CanonicalCorpus.ScenarioResult =
        { Scenario = scenario
          Outcome = CanonicalCorpus.Pass
          ObservedSemantics = [||]
          ObservedPayloads = [||]
          ElapsedMs = 50 }
    let fail : CanonicalCorpus.ScenarioResult =
        { Scenario = { scenario with Id = "summary.fail" }
          Outcome = CanonicalCorpus.Fail "missing=StreamChunk"
          ObservedSemantics = [||]
          ObservedPayloads = [||]
          ElapsedMs = 1500 }
    let formatted =
        CanonicalCorpus.formatCorpusResultsForBundle [| pass; fail |]
    Assert.Contains("CORPUS: 1 PASS / 1 FAIL / 2 total", formatted)

// =====================================================================
// Empty results bundle
// =====================================================================

[<Fact>]
let ``formatCorpusResultsForBundle on empty array reports zero counts`` () =
    let formatted = CanonicalCorpus.formatCorpusResultsForBundle [||]
    Assert.Equal("CORPUS: 0 PASS / 0 FAIL / 0 total", formatted)
