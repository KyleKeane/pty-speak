module PtySpeak.Tests.Unit.DiagnosticGrepTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 43a — DiagnosticGrep pure-function tests.
// ---------------------------------------------------------------------
//
// Pin the formatter shape, regex compile behaviour, and edge cases
// for `Terminal.Core/DiagnosticGrep.fs`. The grep dialog
// (`Views/GrepDialog.xaml`) collects options + pattern from the
// user, then the orchestrator in `Terminal.App/Program.fs`
// regenerates a lightweight bundle in-memory and calls
// `DiagnosticGrep.formatGrep` to produce the clipboard payload.
// The contracts below are what that wiring depends on.

let private sampleSource : string =
    String.concat "\n"
        [ "line 0: alpha"
          "line 1: beta"
          "line 2: GAMMA UPPERCASE"
          "line 3: gamma lowercase"
          "line 4: delta"
          "line 5: epsilon"
          "line 6: zeta"
          "line 7: eta"
          "line 8: theta"
          "line 9: iota"
          "line 10: kappa" ]

// ---------------------------------------------------------------------
// countMatches
// ---------------------------------------------------------------------

[<Fact>]
let ``countMatches returns zero for an empty pattern`` () =
    let opts = { DiagnosticGrep.defaultOptions with Pattern = "" }
    Assert.Equal(0, DiagnosticGrep.countMatches opts sampleSource)

[<Fact>]
let ``countMatches is case-insensitive by default`` () =
    let opts = { DiagnosticGrep.defaultOptions with Pattern = "gamma" }
    // Lines 2 (UPPERCASE) and 3 (lowercase) both match when
    // case-insensitive (default).
    Assert.Equal(2, DiagnosticGrep.countMatches opts sampleSource)

[<Fact>]
let ``countMatches respects the case-sensitive flag`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "gamma"
            CaseSensitive = true }
    // Only line 3 matches the lowercase pattern when case-sensitive.
    Assert.Equal(1, DiagnosticGrep.countMatches opts sampleSource)

[<Fact>]
let ``countMatches handles regex mode`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "^line [0-2]:"
            TreatAsRegex = true }
    Assert.Equal(3, DiagnosticGrep.countMatches opts sampleSource)

[<Fact>]
let ``countMatches returns zero on a bad regex`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "(unclosed"
            TreatAsRegex = true }
    Assert.Equal(0, DiagnosticGrep.countMatches opts sampleSource)

// ---------------------------------------------------------------------
// formatGrep
// ---------------------------------------------------------------------

[<Fact>]
let ``formatGrep banner reports the pattern and options`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "alpha"
            CaseSensitive = true
            TreatAsRegex = false
            ContextLines = 2 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("pty-speak grep — pattern: alpha", result)
    Assert.Contains("regex=false", result)
    Assert.Contains("case=true", result)
    Assert.Contains("context=2", result)

[<Fact>]
let ``formatGrep emits a banner and footer for an empty pattern`` () =
    let opts = { DiagnosticGrep.defaultOptions with Pattern = "" }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("empty pattern", result)

[<Fact>]
let ``formatGrep emits a banner with the regex error message`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "(unclosed"
            TreatAsRegex = true }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("regex error", result)

[<Fact>]
let ``formatGrep reports zero matches with a clear no-matches line`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with Pattern = "no-such-pattern" }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("No matches for no-such-pattern", result)

[<Fact>]
let ``formatGrep marks matched lines with the >>> prefix`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "kappa"
            ContextLines = 1 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains(">>> line 10: kappa", result)

[<Fact>]
let ``formatGrep emits one block per match with sequential numbering`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "gamma"
            ContextLines = 0 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("Match 1 of 2", result)
    Assert.Contains("Match 2 of 2", result)

[<Fact>]
let ``formatGrep emits the right context-line count above and below`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "delta"
            ContextLines = 2 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    // delta is on line 4 (1-indexed: line 5). Context = 2 means
    // we should see lines 2, 3, the match, 5, 6.
    Assert.Contains("line 2: GAMMA UPPERCASE", result)
    Assert.Contains("line 3: gamma lowercase", result)
    Assert.Contains(">>> line 4: delta", result)
    Assert.Contains("line 5: epsilon", result)
    Assert.Contains("line 6: zeta", result)

[<Fact>]
let ``formatGrep clamps context at the start of the source`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "alpha"
            ContextLines = 5 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    // alpha is line 1 (1-indexed). Context window asks for 5
    // lines before but only 0 exist; formatter should handle the
    // clamp without throwing or going negative.
    Assert.Contains(">>> line 0: alpha", result)

[<Fact>]
let ``formatGrep summary footer reports the total match count`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with Pattern = "line" }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("Summary: 11 matches", result)

[<Fact>]
let ``formatGrep regex mode honours anchors`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "^line 5:"
            TreatAsRegex = true
            ContextLines = 0 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains(">>> line 5: epsilon", result)
    Assert.Contains("Summary: 1 matches", result)

[<Fact>]
let ``formatGrep regex mode respects case-insensitive default`` () =
    let opts =
        { DiagnosticGrep.defaultOptions with
            Pattern = "GAMMA"
            TreatAsRegex = true
            ContextLines = 0 }
    let result = DiagnosticGrep.formatGrep opts sampleSource
    Assert.Contains("Summary: 2 matches", result)
