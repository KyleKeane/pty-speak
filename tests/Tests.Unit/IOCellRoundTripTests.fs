module PtySpeak.Tests.Unit.IOCellRoundTripTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 51 PR-W2 — IOCell JSONL round-trip discipline (ADR 0004).
// ---------------------------------------------------------------------
//
// `SessionModel.parseFromJsonl` is the hand-rolled inverse of
// `SessionModel.formatIOCellAsJsonl`. The headline guarantee is
//
//     forall c, parseFromJsonl (formatIOCellAsJsonl c) = Ok c
//
// exercised below by a seeded generative test over many IOCells
// covering the IOCellPhase + BoundarySource DUs, option fields,
// Map fields, and escape-triggering strings, plus deterministic
// edge cases.
//
// NOTE: the playbook suggested FsCheck for the property. The
// repo pins FsCheck.Xunit 3.x but has zero precedent for its
// (reorganised-in-3.x) Gen/Arb/custom-Arbitrary API, and there
// is no local F# compiler here — guessing the 3.x generator
// surface blind is an expensive CI round-trip for no
// behavioural gain. A fixed-seed System.Random generator gives
// the same forall-style coverage, deterministic/reproducible
// failures, and uses only the Xunit surface every other test
// here uses. Flagged in the PR body for maintainer review.
//
// Control chars in fixtures are built with `char 0xNN`
// (ASCII-only source): raw NUL/ESC/DEL bytes in .fs source get
// silently stripped by tooling (CONTRIBUTING foot-gun).

// ---------------------------------------------------------------------
// Seeded generators (deterministic; no FsCheck dependency)
// ---------------------------------------------------------------------

let private nul = string (char 0x00)
let private soh = string (char 0x01)
let private esc = string (char 0x1b)
let private del = string (char 0x7f)

/// Fragments that are each a complete UTF-16 unit (BMP char or
/// a full surrogate PAIR) — concatenation can never produce a
/// lone surrogate (which the serializer refuses to emit).
/// Weighted toward characters that exercise every escape branch.
let private fragments : string[] =
    [| "a"; "b"; "X"; "7"; " "; "/"; "é"; "ß"          // plain / passthrough
       "\""; "\\"; "\n"; "\r"; "\t"; "\b"; "\f"             // named escapes
       nul; soh; esc; del                                   // control / DEL
       "👋" |]                                                // valid surrogate PAIR

let private genStr (rnd: Random) : string =
    let count = rnd.Next(0, 13)
    let sb = System.Text.StringBuilder()
    for _ in 1 .. count do
        sb.Append(fragments.[rnd.Next(0, fragments.Length)]) |> ignore
    sb.ToString()

let private genOpt (rnd: Random) (g: Random -> 'a) : 'a option =
    if rnd.Next(0, 10) < 3 then None else Some (g rnd)

/// UTC DateTime with full 100ns-tick precision (the wire format
/// is `.fffffffZ`, lossless from Windows ticks). Day capped at
/// 28 to dodge month-length edges; year stays 4-digit.
let private genUtc (rnd: Random) : DateTime =
    DateTime(
        rnd.Next(1, 10000),
        rnd.Next(1, 13),
        rnd.Next(1, 29),
        rnd.Next(0, 24),
        rnd.Next(0, 60),
        rnd.Next(0, 60),
        DateTimeKind.Utc)
        .AddTicks(int64 (rnd.Next(0, 10000000)))

let private genInt64 (rnd: Random) : int64 =
    match rnd.Next(0, 5) with
    | 0 -> 0L
    | 1 -> -1L
    | 2 -> Int64.MaxValue
    | 3 -> Int64.MinValue
    | _ -> int64 (rnd.Next(0, 1000000))

let private genInt (rnd: Random) : int =
    match rnd.Next(0, 5) with
    | 0 -> 0
    | 1 -> -1
    | 2 -> Int32.MaxValue
    | 3 -> Int32.MinValue
    | _ -> rnd.Next(-1000, 1001)

let private genPhase (rnd: Random) : SessionModel.IOCellPhase =
    match rnd.Next(0, 4) with
    | 0 -> SessionModel.IOCellPhase.Composing
    | 1 -> SessionModel.IOCellPhase.Executing
    | 2 -> SessionModel.IOCellPhase.Sealed
    | _ -> SessionModel.IOCellPhase.AwaitingSubPromptResponse (genStr rnd)

let private genSource (rnd: Random) : BoundarySource =
    match rnd.Next(0, 3) with
    | 0 -> BoundarySource.Osc133
    | 1 -> BoundarySource.HeuristicPromptRegex (rnd.Next(0, 100000))
    | _ -> BoundarySource.HeuristicClaudeInkBox

/// Sources keys are drawn from the four payload-less boundary
/// forms. `CommandFinished` is canonicalised to `None` — the
/// serializer emits the payload-less name (the exit code lives
/// on `ExitCode`), so only `CommandFinished None` round-trips
/// by construction.
let private genSources (rnd: Random) : Map<BoundaryKind, BoundarySource> =
    let keys =
        [ BoundaryKind.PromptStart
          BoundaryKind.CommandStart
          BoundaryKind.OutputStart
          BoundaryKind.CommandFinished None ]
    keys
    |> List.choose (fun k ->
        if rnd.Next(0, 2) = 0 then Some (k, genSource rnd) else None)
    |> Map.ofList

let private genExtraParams (rnd: Random) : Map<string, string> =
    let count = rnd.Next(0, 5)
    [ for _ in 1 .. count -> (genStr rnd, genStr rnd) ]
    |> Map.ofList

let private genIOCell (rnd: Random) : SessionModel.IOCell =
    { Id = Guid.NewGuid()
      CellSequence = genInt64 rnd
      CommandId = genOpt rnd genStr
      Phase = genPhase rnd
      ShellId = genStr rnd
      PromptStartedAt = genUtc rnd
      CommandStartedAt = genOpt rnd genUtc
      OutputStartedAt = genOpt rnd genUtc
      CommandFinishedAt = genOpt rnd genUtc
      PromptText = genStr rnd
      CommandText = genStr rnd
      OutputText = genStr rnd
      ExitCode = genOpt rnd genInt
      Sources = genSources rnd
      ExtraParams = genExtraParams rnd }

// ---------------------------------------------------------------------
// The round-trip property
// ---------------------------------------------------------------------

[<Fact>]
let ``parseFromJsonl (formatIOCellAsJsonl c) = Ok c for 1000 generated cells`` () =
    let rnd = Random(20260515)   // fixed seed -> reproducible
    for iteration in 1 .. 1000 do
        let c = genIOCell rnd
        let line = SessionModel.formatIOCellAsJsonl c
        match SessionModel.parseFromJsonl line with
        | Ok back ->
            if back <> c then
                Assert.Fail(
                    sprintf
                        "round-trip mismatch at iteration %d\nexpected: %A\nactual:   %A"
                        iteration c back)
        | Error e ->
            Assert.Fail(
                sprintf
                    "round-trip parse failed at iteration %d: %A\ncell: %A"
                    iteration e c)

// ---------------------------------------------------------------------
// Deterministic edge coverage
// ---------------------------------------------------------------------

let private mkCell : SessionModel.IOCell =
    { Id = Guid.Parse("11111111-2222-3333-4444-555555555555")
      CellSequence = 0L
      CommandId = None
      Phase = SessionModel.IOCellPhase.Sealed
      ShellId = "cmd"
      PromptStartedAt = DateTime(2026, 5, 9, 14, 23, 45, DateTimeKind.Utc)
      CommandStartedAt = None
      OutputStartedAt = None
      CommandFinishedAt = None
      PromptText = "C:\\>"
      CommandText = ""
      OutputText = ""
      ExitCode = None
      Sources = Map.empty
      ExtraParams = Map.empty }

let private rt (c: SessionModel.IOCell) : SessionModel.IOCell =
    match SessionModel.parseFromJsonl (SessionModel.formatIOCellAsJsonl c) with
    | Ok back -> back
    | Error e -> failwithf "expected Ok, got Error %A" e

[<Fact>]
let ``minimal cell round-trips`` () =
    Assert.Equal(mkCell, rt mkCell)

[<Fact>]
let ``every IOCellPhase round-trips`` () =
    for p in
        [ SessionModel.IOCellPhase.Composing
          SessionModel.IOCellPhase.Executing
          SessionModel.IOCellPhase.Sealed
          SessionModel.IOCellPhase.AwaitingSubPromptResponse "Continue? [Y,N]?" ] do
        let c = { mkCell with Phase = p }
        Assert.Equal(c, rt c)

[<Fact>]
let ``escape-heavy strings round-trip verbatim`` () =
    let tricky =
        "quote=\" backslash=\\ nl=\n cr=\r tab=\t bs=\b ff=\f "
        + "nul=" + nul + " soh=" + soh + " esc=" + esc + " del=" + del
        + " slash=/ unicode=café emoji=👋"
    let c =
        { mkCell with
            CommandId = Some tricky
            ShellId = tricky
            PromptText = tricky
            CommandText = tricky
            OutputText = tricky
            Phase = SessionModel.IOCellPhase.AwaitingSubPromptResponse tricky
            ExtraParams = Map.ofList [ tricky, tricky; "k2", "v2" ] }
    Assert.Equal(c, rt c)

[<Fact>]
let ``option fields round-trip None and Some`` () =
    let someEverything =
        { mkCell with
            CommandId = Some "aid-42"
            CommandStartedAt =
                Some (DateTime(2026, 5, 9, 14, 23, 45, 50, DateTimeKind.Utc))
            OutputStartedAt =
                Some (DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            CommandFinishedAt =
                Some (DateTime(9999, 12, 28, 23, 59, 59, DateTimeKind.Utc)
                          .AddTicks(9999999L))
            ExitCode = Some -1 }
    Assert.Equal(someEverything, rt someEverything)
    Assert.Equal(mkCell, rt mkCell)   // the all-None baseline

[<Fact>]
let ``Sources with every boundary + source variant round-trips`` () =
    let c =
        { mkCell with
            Sources =
                Map.ofList
                    [ BoundaryKind.PromptStart, BoundarySource.Osc133
                      BoundaryKind.CommandStart,
                          BoundarySource.HeuristicPromptRegex 250
                      BoundaryKind.OutputStart,
                          BoundarySource.HeuristicClaudeInkBox
                      BoundaryKind.CommandFinished None,
                          BoundarySource.HeuristicPromptRegex 0 ] }
    Assert.Equal(c, rt c)

[<Fact>]
let ``exitCode extremes round-trip`` () =
    for code in [ 0; -1; 127; Int32.MaxValue; Int32.MinValue ] do
        let c = { mkCell with ExitCode = Some code }
        Assert.Equal(c, rt c)

[<Fact>]
let ``CellSequence extremes round-trip`` () =
    for n in [ 0L; -1L; 42L; Int64.MaxValue; Int64.MinValue ] do
        let c = { mkCell with CellSequence = n }
        Assert.Equal(c, rt c)

// ---------------------------------------------------------------------
// Rejection paths
// ---------------------------------------------------------------------

[<Fact>]
let ``schemaVersion 1 is rejected with UnsupportedSchemaVersion`` () =
    let v2 = SessionModel.formatIOCellAsJsonl mkCell
    let v1 = v2.Replace("\"schemaVersion\":2,", "\"schemaVersion\":1,")
    match SessionModel.parseFromJsonl v1 with
    | Error (SessionModel.UnsupportedSchemaVersion 1) -> ()
    | other ->
        Assert.Fail(sprintf "expected UnsupportedSchemaVersion 1, got %A" other)

[<Fact>]
let ``structurally malformed input is rejected with Malformed`` () =
    for bad in
        [ "not json at all"
          "{"
          "{}"                                  // missing schemaVersion
          "[1,2,3]"                             // not an object
          "{\"schemaVersion\":2}"               // missing the rest
          "{\"schemaVersion\":\"two\"}" ] do    // schemaVersion not a number
        match SessionModel.parseFromJsonl bad with
        | Error (SessionModel.Malformed _) -> ()
        | other ->
            Assert.Fail(sprintf "expected Malformed for %s, got %A" bad other)

[<Fact>]
let ``a lone UTF-16 surrogate is refused by the writer and rejected by the reader`` () =
    let lone = String([| char 0xD800 |])   // lone high surrogate
    Assert.Throws<InvalidOperationException>(fun () ->
        SessionModel.formatIOCellAsJsonl { mkCell with OutputText = lone }
        |> ignore)
    |> ignore
    let payload = "{\"schemaVersion\":2,\"x\":\"" + lone + "\"}"
    match SessionModel.parseFromJsonl payload with
    | Error (SessionModel.Malformed _) -> ()
    | other -> Assert.Fail(sprintf "expected Malformed, got %A" other)

[<Fact>]
let ``trailing newline from the formatter is tolerated`` () =
    let line = SessionModel.formatIOCellAsJsonl mkCell
    Assert.EndsWith("\n", line)
    match SessionModel.parseFromJsonl line with
    | Ok back -> Assert.Equal(mkCell, back)
    | Error e -> Assert.Fail(sprintf "expected Ok, got %A" e)
