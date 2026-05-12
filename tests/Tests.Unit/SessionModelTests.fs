module PtySpeak.Tests.Unit.SessionModelTests

open System
open Xunit
open Terminal.Core
open Terminal.Parser

// ---------------------------------------------------------------------
// SessionModel Tier 1.C — state machine + composition wiring
// ---------------------------------------------------------------------
//
// Tier 1.C ships the real `apply` state machine (replacing the
// Tier 1.A no-op stub) plus per-Q3-partial alt-screen helpers and
// the per-Q5 `finalizeIncomplete` helper that the composition root
// calls on Ctrl+Shift+1/2/3 hot-switch.
//
// Tests pin:
//   * Substrate types (`BoundaryKind`, `BoundarySource`,
//     `PromptBoundaryData`) construct cleanly + round-trip
//     through pattern matches (preserved from Tier 1.A).
//   * `ScreenNotification.PromptBoundary` is a fully-typed
//     case carrying `PromptBoundaryData` (preserved).
//   * `SessionModel.create` returns the documented initial
//     state; `createDefault` uses `DefaultMaxHistorySize`
//     (preserved).
//   * The state machine's happy path (PromptStart →
//     CommandStart → OutputStart → CommandFinished).
//   * Sequence pinning (multi-tuple flows; CommandId +
//     ExtraParams + Sources accumulate per boundary).
//   * Defensive transitions (orphan boundaries; repeated
//     boundaries; out-of-order boundaries).
//   * Ring-buffer eviction at `MaxHistorySize`.
//   * Alt-screen guard (`IsAltScreenActive` short-circuits).
//   * `finalizeIncomplete` (per Q5; shell-switch path).
//
// Heuristic-fallback wiring + composition-root behaviour ship
// in Tier 1.D + later cycles.

// ---------------------------------------------------------------------
// Test fixture builders
// ---------------------------------------------------------------------

let private boundary
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = None
      MatchedRowIndex = None }

let private boundaryWith
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (commandId: string option)
        (extras: (string * string) list)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = commandId
      ExtraParams = Map.ofList extras
      MatchedRowText = None
      MatchedRowIndex = None }

/// Tier 1.E builder — boundary with explicit `MatchedRowText`.
/// Used by PromptText-population tests that need to verify
/// the field flows from boundary into `Active.Tuple.PromptText`.
let private boundaryWithText
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (matchedText: string)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = Some matchedText
      MatchedRowIndex = None }

let private t0 = DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

// ---------------------------------------------------------------------
// BoundaryKind cases (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``BoundaryKind.PromptStart constructs cleanly`` () =
    let kind = BoundaryKind.PromptStart
    match kind with
    | BoundaryKind.PromptStart -> ()
    | _ -> Assert.Fail("Expected PromptStart")

[<Fact>]
let ``BoundaryKind.CommandStart constructs cleanly`` () =
    let kind = BoundaryKind.CommandStart
    match kind with
    | BoundaryKind.CommandStart -> ()
    | _ -> Assert.Fail("Expected CommandStart")

[<Fact>]
let ``BoundaryKind.OutputStart constructs cleanly`` () =
    let kind = BoundaryKind.OutputStart
    match kind with
    | BoundaryKind.OutputStart -> ()
    | _ -> Assert.Fail("Expected OutputStart")

[<Fact>]
let ``BoundaryKind.CommandFinished carries optional exit code`` () =
    let kindWithExit = BoundaryKind.CommandFinished (Some 1)
    let kindNoExit = BoundaryKind.CommandFinished None
    match kindWithExit, kindNoExit with
    | BoundaryKind.CommandFinished (Some 1), BoundaryKind.CommandFinished None -> ()
    | _ -> Assert.Fail("Expected exit-code variants")

// ---------------------------------------------------------------------
// BoundarySource cases (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``BoundarySource.Osc133 constructs cleanly`` () =
    let source = BoundarySource.Osc133
    match source with
    | BoundarySource.Osc133 -> ()
    | _ -> Assert.Fail("Expected Osc133")

[<Fact>]
let ``BoundarySource.HeuristicPromptRegex carries stability ms`` () =
    let source = BoundarySource.HeuristicPromptRegex 200
    match source with
    | BoundarySource.HeuristicPromptRegex stabilityMs ->
        Assert.Equal(200, stabilityMs)
    | _ -> Assert.Fail("Expected HeuristicPromptRegex")

[<Fact>]
let ``BoundarySource.HeuristicClaudeInkBox constructs cleanly`` () =
    let source = BoundarySource.HeuristicClaudeInkBox
    match source with
    | BoundarySource.HeuristicClaudeInkBox -> ()
    | _ -> Assert.Fail("Expected HeuristicClaudeInkBox")

// ---------------------------------------------------------------------
// PromptBoundaryData record (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``PromptBoundaryData record literal constructs all fields`` () =
    let now = DateTime.UtcNow
    let data : PromptBoundaryData =
        { Kind = BoundaryKind.PromptStart
          Source = BoundarySource.Osc133
          DetectedAt = now
          CommandId = Some "abc-123"
          ExtraParams = Map.ofList [ "k", "v" ]
          MatchedRowText = None
          MatchedRowIndex = None }
    Assert.Equal(BoundaryKind.PromptStart, data.Kind)
    Assert.Equal(BoundarySource.Osc133, data.Source)
    Assert.Equal(now, data.DetectedAt)
    Assert.Equal(Some "abc-123", data.CommandId)
    Assert.Equal<Map<string, string>>(Map.ofList [ "k", "v" ], data.ExtraParams)

[<Fact>]
let ``PromptBoundaryData supports None CommandId and empty ExtraParams`` () =
    let data : PromptBoundaryData =
        { Kind = BoundaryKind.CommandFinished (Some 0)
          Source = BoundarySource.HeuristicPromptRegex 100
          DetectedAt = DateTime.UtcNow
          CommandId = None
          ExtraParams = Map.empty
          MatchedRowText = None
          MatchedRowIndex = None }
    Assert.Equal(None, data.CommandId)
    Assert.True(Map.isEmpty data.ExtraParams)

[<Fact>]
let ``PromptBoundaryData carries optional MatchedRowText (Tier 1.E)`` () =
    let withText : PromptBoundaryData =
        { Kind = BoundaryKind.PromptStart
          Source = BoundarySource.HeuristicPromptRegex 100
          DetectedAt = DateTime.UtcNow
          CommandId = None
          ExtraParams = Map.empty
          MatchedRowText = Some "C:\\>"
          MatchedRowIndex = None }
    Assert.Equal(Some "C:\\>", withText.MatchedRowText)
    let withoutText = { withText with MatchedRowText = None }
    Assert.Equal(None, withoutText.MatchedRowText)

// ---------------------------------------------------------------------
// ScreenNotification.PromptBoundary (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``ScreenNotification.PromptBoundary round-trips through pattern match`` () =
    let data : PromptBoundaryData =
        { Kind = BoundaryKind.OutputStart
          Source = BoundarySource.Osc133
          DetectedAt = DateTime.UtcNow
          CommandId = None
          ExtraParams = Map.empty
          MatchedRowText = None
          MatchedRowIndex = None }
    let notification = ScreenNotification.PromptBoundary data
    match notification with
    | ScreenNotification.PromptBoundary roundTripped ->
        Assert.Equal(BoundaryKind.OutputStart, roundTripped.Kind)
        Assert.Equal(BoundarySource.Osc133, roundTripped.Source)
    | _ -> Assert.Fail("Expected PromptBoundary case")

// ---------------------------------------------------------------------
// SessionModel.ActiveTupleState cases
// ---------------------------------------------------------------------

[<Fact>]
let ``ActiveTupleState cases enumerate as expected`` () =
    let states =
        [ SessionModel.ActiveTupleState.AwaitingPromptStart
          SessionModel.ActiveTupleState.AwaitingCommandStart
          SessionModel.ActiveTupleState.EditingCommand
          SessionModel.ActiveTupleState.OutputStreaming ]
    Assert.Equal(4, states.Length)

// ---------------------------------------------------------------------
// SessionModel.create + createDefault (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``SessionModel.create initialises empty History + None Active`` () =
    let model = SessionModel.create "cmd" 50
    Assert.Equal("cmd", model.ShellId)
    Assert.Equal(50, model.MaxHistorySize)
    Assert.Equal(0, model.History.Count)
    Assert.Equal(None, model.Active)

[<Fact>]
let ``SessionModel.create initialises IsAltScreenActive=false`` () =
    let model = SessionModel.create "cmd" 50
    Assert.False(model.IsAltScreenActive)

[<Fact>]
let ``SessionModel.create issues a fresh SessionId per instance`` () =
    let a = SessionModel.create "cmd" 50
    let b = SessionModel.create "cmd" 50
    Assert.NotEqual(a.SessionId, b.SessionId)

[<Fact>]
let ``SessionModel.create stamps SessionStartedAt close to now`` () =
    let before = DateTime.UtcNow
    let model = SessionModel.create "cmd" 50
    let after = DateTime.UtcNow
    Assert.True(model.SessionStartedAt >= before.AddMilliseconds(-1.0))
    Assert.True(model.SessionStartedAt <= after.AddMilliseconds(1.0))

[<Fact>]
let ``SessionModel.createDefault uses DefaultMaxHistorySize (100)`` () =
    let model = SessionModel.createDefault "powershell"
    Assert.Equal(SessionModel.DefaultMaxHistorySize, model.MaxHistorySize)
    Assert.Equal(100, model.MaxHistorySize)
    Assert.Equal("powershell", model.ShellId)

// ---------------------------------------------------------------------
// SessionTuple shape (preserved from Tier 1.A — Q4 + Q8)
// ---------------------------------------------------------------------

[<Fact>]
let ``SessionTuple.CommandText supports multi-line via embedded newlines (Q4)`` () =
    let tuple : SessionModel.SessionTuple =
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = DateTime.UtcNow
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = None
          PromptText = ">"
          CommandText = "echo a\necho b\necho c"
          OutputText = ""
          ExitCode = None
          Sources = Map.empty
          ExtraParams = Map.empty }
    let lines = tuple.CommandText.Split('\n')
    Assert.Equal(3, lines.Length)
    Assert.Equal("echo a", lines.[0])
    Assert.Equal("echo b", lines.[1])
    Assert.Equal("echo c", lines.[2])

[<Fact>]
let ``SessionTuple.ExitCode is int option (Q8 — substrate captures verbatim)`` () =
    let success : SessionModel.SessionTuple =
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = DateTime.UtcNow
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = None
          PromptText = ""
          CommandText = ""
          OutputText = ""
          ExitCode = Some 0
          Sources = Map.empty
          ExtraParams = Map.empty }
    let unreported = { success with ExitCode = None }
    let failure = { success with ExitCode = Some 127 }
    Assert.Equal(Some 0, success.ExitCode)
    Assert.Equal(None, unreported.ExitCode)
    Assert.Equal(Some 127, failure.ExitCode)

// =====================================================================
// Tier 1.C — state machine: happy path
// =====================================================================

[<Fact>]
let ``apply PromptStart from AwaitingPromptStart populates Active + advances state`` () =
    let initial = SessionModel.create "cmd" 50
    let updated = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    match updated.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart,
            active.State)
        Assert.Equal(t0, active.Tuple.PromptStartedAt)
        Assert.Equal("cmd", active.Tuple.ShellId)
        Assert.Equal(0, updated.History.Count)
    | None -> Assert.Fail("Expected Active = Some after PromptStart")

[<Fact>]
let ``apply CommandStart after PromptStart advances to EditingCommand`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    match s2.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.EditingCommand,
            active.State)
        Assert.Equal(Some (after 100), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active after CommandStart")

[<Fact>]
let ``apply OutputStart after CommandStart advances to OutputStreaming`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    match s3.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming,
            active.State)
        Assert.Equal(Some (after 200), active.Tuple.OutputStartedAt)
    | None -> Assert.Fail("Expected Active after OutputStart")

[<Fact>]
let ``apply CommandFinished moves Active to History + resets to AwaitingPromptStart`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    let s4 =
        SessionModel.apply
            s3
            (boundary (BoundaryKind.CommandFinished (Some 0)) (after 300))
            [||]
    Assert.Equal(None, s4.Active)
    Assert.Equal(1, s4.History.Count)
    let finalised = s4.History.ToArray().[0]
    Assert.Equal(Some (after 300), finalised.CommandFinishedAt)
    Assert.Equal(Some 0, finalised.ExitCode)

// =====================================================================
// Tier 1.C — state machine: sequence pinning
// =====================================================================

[<Fact>]
let ``Full A->B->C->D sequence yields one complete tuple in History`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    Assert.Equal(None, final.Active)
    Assert.Equal(1, final.History.Count)
    let tuple = final.History.ToArray().[0]
    Assert.Equal(t0, tuple.PromptStartedAt)
    Assert.Equal(Some (after 100), tuple.CommandStartedAt)
    Assert.Equal(Some (after 200), tuple.OutputStartedAt)
    Assert.Equal(Some (after 300), tuple.CommandFinishedAt)
    Assert.Equal(Some 0, tuple.ExitCode)

[<Fact>]
let ``Two full sequences yield 2 tuples in order`` () =
    let initial = SessionModel.create "cmd" 50
    let firstSeq =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
    let secondSeq =
        [ boundary BoundaryKind.PromptStart (after 400)
          boundary BoundaryKind.CommandStart (after 500)
          boundary BoundaryKind.OutputStart (after 600)
          boundary (BoundaryKind.CommandFinished (Some 1)) (after 700) ]
    let final =
        firstSeq @ secondSeq
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    Assert.Equal(2, final.History.Count)
    let arr = final.History.ToArray()
    Assert.Equal(t0, arr.[0].PromptStartedAt)
    Assert.Equal(after 400, arr.[1].PromptStartedAt)
    Assert.Equal(Some 0, arr.[0].ExitCode)
    Assert.Equal(Some 1, arr.[1].ExitCode)

[<Fact>]
let ``History preserves DetectedAt timestamps in order`` () =
    let initial = SessionModel.create "cmd" 50
    let seqs =
        [ for offsetMs in [ 0; 1000; 2000 ] do
            yield boundary BoundaryKind.PromptStart (after offsetMs)
            yield boundary BoundaryKind.CommandStart (after (offsetMs + 100))
            yield boundary BoundaryKind.OutputStart (after (offsetMs + 200))
            yield
                boundary
                    (BoundaryKind.CommandFinished (Some 0))
                    (after (offsetMs + 300)) ]
    let final = seqs |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    Assert.Equal(3, final.History.Count)
    let arr = final.History.ToArray()
    Assert.True(arr.[0].PromptStartedAt < arr.[1].PromptStartedAt)
    Assert.True(arr.[1].PromptStartedAt < arr.[2].PromptStartedAt)

[<Fact>]
let ``CommandId from boundaries lands on the tuple`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundaryWith BoundaryKind.PromptStart t0 (Some "id-1") []
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(Some "id-1", tuple.CommandId)

[<Fact>]
let ``ExtraParams from boundaries merge onto the tuple (later wins)`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundaryWith BoundaryKind.PromptStart t0 None [ "k1", "v1" ]
          boundaryWith BoundaryKind.CommandStart (after 100) None [ "k2", "v2" ]
          boundaryWith
              BoundaryKind.OutputStart
              (after 200)
              None
              [ "k1", "overwrite" ]
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(Some "overwrite", Map.tryFind "k1" tuple.ExtraParams)
    Assert.Equal(Some "v2", Map.tryFind "k2" tuple.ExtraParams)

[<Fact>]
let ``Sources map records (Kind, Source) for each boundary`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ { Kind = BoundaryKind.PromptStart
            Source = BoundarySource.Osc133
            DetectedAt = t0
            CommandId = None
            ExtraParams = Map.empty
            MatchedRowText = None
            MatchedRowIndex = None }
          { Kind = BoundaryKind.CommandStart
            Source = BoundarySource.HeuristicPromptRegex 100
            DetectedAt = after 100
            CommandId = None
            ExtraParams = Map.empty
            MatchedRowText = None
            MatchedRowIndex = None }
          { Kind = BoundaryKind.OutputStart
            Source = BoundarySource.HeuristicClaudeInkBox
            DetectedAt = after 200
            CommandId = None
            ExtraParams = Map.empty
            MatchedRowText = None
            MatchedRowIndex = None }
          { Kind = BoundaryKind.CommandFinished (Some 0)
            Source = BoundarySource.Osc133
            DetectedAt = after 300
            CommandId = None
            ExtraParams = Map.empty
            MatchedRowText = None
            MatchedRowIndex = None } ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(
        Some BoundarySource.Osc133,
        Map.tryFind BoundaryKind.PromptStart tuple.Sources)
    Assert.Equal(
        Some (BoundarySource.HeuristicPromptRegex 100),
        Map.tryFind BoundaryKind.CommandStart tuple.Sources)
    Assert.Equal(
        Some BoundarySource.HeuristicClaudeInkBox,
        Map.tryFind BoundaryKind.OutputStart tuple.Sources)
    Assert.Equal(
        Some BoundarySource.Osc133,
        Map.tryFind (BoundaryKind.CommandFinished (Some 0)) tuple.Sources)

// =====================================================================
// Tier 1.C — state machine: defensive transitions
// =====================================================================

[<Fact>]
let ``PromptStart while Active finalises prior as incomplete + starts new`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    // Second PromptStart while EditingCommand — interrupt + restart.
    let s3 =
        SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 200)) [||]
    Assert.Equal(1, s3.History.Count)
    let prior = s3.History.ToArray().[0]
    Assert.Equal(Some (after 200), prior.CommandFinishedAt)
    Assert.Equal(None, prior.ExitCode)
    match s3.Active with
    | Some active ->
        Assert.Equal(after 200, active.Tuple.PromptStartedAt)
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart,
            active.State)
    | None -> Assert.Fail("Expected new Active after interrupting PromptStart")

[<Fact>]
let ``CommandStart from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundary BoundaryKind.CommandStart t0)
            [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``OutputStart from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.OutputStart t0) [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``CommandFinished from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundary (BoundaryKind.CommandFinished (Some 0)) t0)
            [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``OutputStart in AwaitingCommandStart tolerates skipped CommandStart`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    // Skip CommandStart; jump straight to OutputStart.
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.OutputStart (after 100)) [||]
    match s2.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming,
            active.State)
        Assert.Equal(Some (after 100), active.Tuple.OutputStartedAt)
        Assert.Equal(None, active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active after tolerated OutputStart")

[<Fact>]
let ``Duplicate CommandStart in EditingCommand refreshes CommandStartedAt`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.CommandStart (after 200)) [||]
    match s3.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.EditingCommand,
            active.State)
        Assert.Equal(Some (after 200), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active to remain after duplicate CommandStart")

[<Fact>]
let ``Duplicate OutputStart in OutputStreaming refreshes OutputStartedAt`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.OutputStart (after 250)) [||]
    match s4.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming,
            active.State)
        Assert.Equal(Some (after 250), active.Tuple.OutputStartedAt)
    | None -> Assert.Fail("Expected Active to remain after duplicate OutputStart")

[<Fact>]
let ``CommandStart while OutputStreaming is logged + ignored (Active preserved)`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.CommandStart (after 250)) [||]
    match s4.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming,
            active.State)
        // CommandStartedAt is NOT refreshed when CommandStart fires
        // in OutputStreaming (the boundary is anomalous).
        Assert.Equal(Some (after 100), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active to remain after anomalous CommandStart")

[<Fact>]
let ``CommandFinished with exit code populates ExitCode`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 127)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(Some 127, tuple.ExitCode)

[<Fact>]
let ``CommandFinished with no exit code yields None ExitCode`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished None) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(None, tuple.ExitCode)

[<Fact>]
let ``CommandFinished from EditingCommand finalises without OutputStartedAt`` () =
    // Instant-completing alias; CommandFinished arrives before
    // OutputStart.
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 200) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    Assert.Equal(1, final.History.Count)
    let tuple = final.History.ToArray().[0]
    Assert.Equal(None, tuple.OutputStartedAt)
    Assert.Equal(Some (after 200), tuple.CommandFinishedAt)

[<Fact>]
let ``CommandId conflict on later boundary preserves earlier value`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundaryWith BoundaryKind.PromptStart t0 (Some "first") []
          boundaryWith BoundaryKind.CommandStart (after 100) (Some "second") []
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal(Some "first", tuple.CommandId)

// =====================================================================
// Tier 1.C — state machine: ring-buffer eviction
// =====================================================================

let private fullSequence (initial: SessionModel.T) (offsetMs: int) =
    [ boundary BoundaryKind.PromptStart (after offsetMs)
      boundary BoundaryKind.CommandStart (after (offsetMs + 50))
      boundary BoundaryKind.OutputStart (after (offsetMs + 100))
      boundary
          (BoundaryKind.CommandFinished (Some 0))
          (after (offsetMs + 150)) ]
    |> List.fold (fun s b -> SessionModel.apply s b [||]) initial

[<Fact>]
let ``History bounded at MaxHistorySize`` () =
    let initial = SessionModel.create "cmd" 3
    let mutable state = initial
    for i in 0 .. 4 do
        state <- fullSequence state (i * 1000)
    Assert.Equal(3, state.History.Count)

[<Fact>]
let ``MaxHistorySize=2 keeps newest two; oldest dropped (FIFO)`` () =
    let initial = SessionModel.create "cmd" 2
    let mutable state = initial
    for i in 0 .. 2 do
        state <- fullSequence state (i * 1000)
    Assert.Equal(2, state.History.Count)
    let arr = state.History.ToArray()
    // First sequence (offset 0) should have been evicted; remaining
    // should start at offset 1000.
    Assert.Equal(after 1000, arr.[0].PromptStartedAt)
    Assert.Equal(after 2000, arr.[1].PromptStartedAt)

[<Fact>]
let ``History order preserved after eviction`` () =
    let initial = SessionModel.create "cmd" 2
    let mutable state = initial
    for i in 0 .. 4 do
        state <- fullSequence state (i * 1000)
    let arr = state.History.ToArray()
    Assert.True(arr.[0].PromptStartedAt < arr.[1].PromptStartedAt)

[<Fact>]
let ``MaxHistorySize=0 keeps no history but doesn't crash`` () =
    let initial = SessionModel.create "cmd" 0
    let final = fullSequence initial 0
    Assert.Equal(0, final.History.Count)
    Assert.Equal(None, final.Active)

// =====================================================================
// Tier 1.C — Q3 alt-screen guard
// =====================================================================

[<Fact>]
let ``apply with IsAltScreenActive=true returns state unchanged`` () =
    let initial =
        SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.True(obj.ReferenceEquals(initial, updated))
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``enterAltScreen toggles flag true`` () =
    let initial = SessionModel.create "cmd" 50
    let alt = SessionModel.enterAltScreen initial
    Assert.True(alt.IsAltScreenActive)

[<Fact>]
let ``exitAltScreen toggles flag false`` () =
    let initial = SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    let normal = SessionModel.exitAltScreen initial
    Assert.False(normal.IsAltScreenActive)

[<Fact>]
let ``apply resumes after exitAltScreen`` () =
    let initial = SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    // Boundary while alt-screen — ignored.
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.Equal(None, s1.Active)
    // Exit alt-screen + send PromptStart again.
    let s2 = SessionModel.exitAltScreen s1
    let s3 =
        SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 100)) [||]
    Assert.True(s3.Active.IsSome)

// =====================================================================
// Tier 1.C — Q5 finalizeIncomplete (shell-switch helper)
// =====================================================================

[<Fact>]
let ``finalizeIncomplete with Active=Some moves to History with CommandFinishedAt set`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let finalised = SessionModel.finalizeIncomplete s2 (after 200)
    Assert.Equal(None, finalised.Active)
    Assert.Equal(1, finalised.History.Count)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal(Some (after 200), tuple.CommandFinishedAt)

[<Fact>]
let ``finalizeIncomplete with Active=None is no-op`` () =
    let initial = SessionModel.create "cmd" 50
    let finalised = SessionModel.finalizeIncomplete initial (after 100)
    Assert.Equal(None, finalised.Active)
    Assert.Equal(0, finalised.History.Count)

[<Fact>]
let ``finalizeIncomplete preserves accumulated metadata`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (boundaryWith
                BoundaryKind.PromptStart
                t0
                (Some "interrupt-test")
                [ "user", "alice" ])
            [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let finalised = SessionModel.finalizeIncomplete s2 (after 200)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal(Some "interrupt-test", tuple.CommandId)
    Assert.Equal(Some "alice", Map.tryFind "user" tuple.ExtraParams)
    Assert.Equal(Some (after 100), tuple.CommandStartedAt)

[<Fact>]
let ``finalizeIncomplete sets ExitCode=None`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let finalised = SessionModel.finalizeIncomplete s1 (after 100)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal(None, tuple.ExitCode)

// =====================================================================
// Tier 1.D — heuristic-source boundary compatibility
// =====================================================================
//
// Tier 1.D's HeuristicPromptDetector emits boundaries with
// `Source = BoundarySource.HeuristicPromptRegex stabilityMs`.
// Pin that the SessionModel state machine treats them
// identically to OSC 133 boundaries — no special-casing,
// source recorded in the Sources map verbatim.

let private heuristicBoundary
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (stabilityMs: int)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.HeuristicPromptRegex stabilityMs
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = None
      MatchedRowIndex = None }

[<Fact>]
let ``apply with HeuristicPromptRegex source populates Active`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (heuristicBoundary BoundaryKind.PromptStart t0 100)
            [||]
    match updated.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart,
            active.State)
        Assert.Equal(t0, active.Tuple.PromptStartedAt)
    | None -> Assert.Fail("Expected Active after heuristic PromptStart")

[<Fact>]
let ``heuristic boundaries finalise tuples the same as OSC 133 boundaries`` () =
    // Two consecutive heuristic PromptStarts: prior tuple
    // finalises as incomplete with ExitCode=None.
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (heuristicBoundary BoundaryKind.PromptStart t0 100)
            [||]
    let s2 =
        SessionModel.apply
            s1
            (heuristicBoundary BoundaryKind.PromptStart (after 1000) 100)
            [||]
    Assert.Equal(1, s2.History.Count)
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal(Some (after 1000), priorTuple.CommandFinishedAt)
    Assert.Equal(None, priorTuple.ExitCode)

[<Fact>]
let ``Sources map records HeuristicPromptRegex stability ms verbatim`` () =
    let initial = SessionModel.create "powershell" 50
    let s1 =
        SessionModel.apply
            initial
            (heuristicBoundary BoundaryKind.PromptStart t0 100)
            [||]
    match s1.Active with
    | Some active ->
        Assert.Equal(
            Some (BoundarySource.HeuristicPromptRegex 100),
            Map.tryFind BoundaryKind.PromptStart active.Tuple.Sources)
    | None -> Assert.Fail("Expected Active")

// =====================================================================
// Tier 1.E — PromptText capture (boundary.MatchedRowText flows into
// Active.Tuple.PromptText on PromptStart transitions)
// =====================================================================

[<Fact>]
let ``PromptStart with MatchedRowText populates Active.Tuple.PromptText`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundaryWithText BoundaryKind.PromptStart t0 "C:\\Users\\admin>")
            [||]
    match updated.Active with
    | Some active ->
        Assert.Equal("C:\\Users\\admin>", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after PromptStart")

[<Fact>]
let ``PromptStart with None MatchedRowText leaves PromptText empty`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundary BoundaryKind.PromptStart t0)
            [||]
    match updated.Active with
    | Some active ->
        Assert.Equal("", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after PromptStart")

[<Fact>]
let ``Interrupt + restart writes new boundary's MatchedRowText to fresh tuple`` () =
    // First PromptStart with text "PS C:\>"; second PromptStart
    // (interrupt) with different text "PS C:\Projects>". The
    // restart-state machine arm uses the NEW boundary for
    // newTuple, so the new active tuple's PromptText should
    // reflect the second boundary's MatchedRowText.
    let initial = SessionModel.create "powershell" 50
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithText BoundaryKind.PromptStart t0 "PS C:\\>")
            [||]
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithText
                BoundaryKind.PromptStart
                (after 100)
                "PS C:\\Projects>")
            [||]
    match s2.Active with
    | Some active ->
        Assert.Equal("PS C:\\Projects>", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after restart")
    // And the prior tuple finalised with its original text.
    Assert.Equal(1, s2.History.Count)
    let prior = s2.History.ToArray().[0]
    Assert.Equal("PS C:\\>", prior.PromptText)

[<Fact>]
let ``MatchedRowText preserved across A->B->C->D progression`` () =
    // PromptText set on PromptStart; subsequent
    // CommandStart / OutputStart / CommandFinished do not
    // overwrite it (their MatchedRowText is None or
    // ignored).
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundaryWithText BoundaryKind.PromptStart t0 "C:\\>"
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    let tuple = final.History.ToArray().[0]
    Assert.Equal("C:\\>", tuple.PromptText)

// =====================================================================
// Tier 1.E2.B — CommandText + OutputText extraction at finalize time
// =====================================================================
//
// Cycle 20b's headline behaviour. When `apply` finalises a
// tuple (PromptStart-while-Active interrupt arm or OSC 133
// CommandFinished arm), it extracts:
//   * CommandText: the row at the prior tuple's
//     PromptRowIndex, minus the captured PromptText prefix.
//   * OutputText: rows between (oldPromptRow + 1) and
//     (newPromptRow - 1) joined with newlines.
// Defensive checks ensure extraction skips gracefully when
// row indices are missing / out of bounds / scrolled.

let private blankCell : Cell = Cell.blank

let private cellOf (ch: char) : Cell =
    { Ch = System.Text.Rune ch; Attrs = SgrAttrs.defaults }

let private blankRow (cols: int) : Cell[] =
    Array.create cols blankCell

let private rowOf (cols: int) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellOf s.[i]
    row

let private snapshotOf
        (rows: int)
        (cols: int)
        (lines: string list)
        : Cell[][]
        =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

let private boundaryWithRow
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (rowIndex: int)
        (matchedText: string)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.HeuristicPromptRegex 100
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = Some matchedText
      MatchedRowIndex = Some rowIndex }

[<Fact>]
let ``Cycle 20b — CommandText extracted from old prompt row at finalize time`` () =
    // Frame 1: prompt at row 0 with text "C:\>". Detector
    // emits PromptStart with MatchedRowIndex=0.
    // Frame 2: snapshot now has "C:\> echo hi" at row 0,
    //          "hi" at row 1, "C:\>" at row 2 (new prompt).
    //          Detector emits PromptStart with MatchedRowIndex=2.
    // Apply's interrupt arm finalises the prior tuple +
    // extracts CommandText="echo hi" + OutputText="hi".
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 5 80 [ "C:\\>"; ""; ""; ""; "" ]
    let snap2 =
        snapshotOf 5 80
            [ "C:\\> echo hi"; "hi"; "C:\\>"; ""; "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 2 "C:\\>")
            snap2
    Assert.Equal(1, s2.History.Count)
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal("echo hi", priorTuple.CommandText)
    Assert.Equal("hi", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — multi-line OutputText joined with newlines`` () =
    // dir-style output: 3 rows of output between two prompts.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 6 80 [ "C:\\>"; ""; ""; ""; ""; "" ]
    let snap2 =
        snapshotOf 6 80
            [ "C:\\> dir"
              "file1.txt"
              "file2.txt"
              "file3.txt"
              "C:\\>"
              "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 4 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal("dir", priorTuple.CommandText)
    Assert.Equal("file1.txt\nfile2.txt\nfile3.txt", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — empty OutputText when no rows between prompts`` () =
    // Adjacent prompts (clear-screen scenario). New prompt
    // at oldRow + 1; no output rows between.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 3 80 [ "C:\\>"; ""; "" ]
    let snap2 = snapshotOf 3 80 [ "C:\\>"; "C:\\>"; "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 1 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal("", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — empty CommandText when old prompt row content doesn't start with PromptText`` () =
    // Defensive: scroll happened mid-cycle; row at
    // oldPromptRowIndex no longer contains the prompt.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 3 80 [ "C:\\>"; ""; "" ]
    // Snap2's row 0 is now "stuff that doesn't start with the prompt"
    let snap2 =
        snapshotOf 3 80
            [ "scrolled output"; "more output"; "C:\\>" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 2 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal("", priorTuple.CommandText)
    // OutputText extraction still works for rows between.
    Assert.Equal("more output", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — empty CommandText + OutputText when old PromptRowIndex is None`` () =
    // Boundary without MatchedRowIndex (e.g. legacy / OSC
    // 133 unaugmented). Active.PromptRowIndex stays None;
    // extraction skips both fields.
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (boundary BoundaryKind.PromptStart t0)   // no row index
            [||]
    let s2 =
        SessionModel.apply
            s1
            (boundary BoundaryKind.PromptStart (after 1000))
            [||]
    let priorTuple = s2.History.ToArray().[0]
    Assert.Equal("", priorTuple.CommandText)
    Assert.Equal("", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — empty CommandText when old PromptRowIndex out of snapshot bounds`` () =
    // Defensive: old row index points beyond snapshot.
    // Could happen if screen resized between frames. Skip
    // extraction.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 5 80 [ ""; ""; ""; ""; "C:\\>" ]
    let snap2 = snapshotOf 3 80 [ "C:\\>"; ""; "" ]   // smaller
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 4 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 0 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    // Old prompt row 4 is OOB in snap2 (snap2.Length = 3).
    Assert.Equal("", priorTuple.CommandText)

[<Fact>]
let ``Cycle 20b — CommandText preserves spacing after prompt prefix`` () =
    // The TrimStart() inside extractContent drops leading
    // spaces; verify command text is captured cleanly.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 4 80 [ "C:\\>"; ""; ""; "" ]
    let snap2 =
        snapshotOf 4 80
            [ "C:\\>   echo   hi"; "hi"; "C:\\>"; "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 2 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    // Leading spaces stripped; intra-command spacing preserved.
    Assert.Equal("echo   hi", priorTuple.CommandText)

[<Fact>]
let ``Cycle 20b — empty rows in OutputText filtered out`` () =
    // Output with trailing blank rows — typical shell
    // padding. Empty rows shouldn't appear in OutputText.
    let initial = SessionModel.create "cmd" 50
    let snap1 = snapshotOf 6 80 [ "C:\\>"; ""; ""; ""; ""; "" ]
    let snap2 =
        snapshotOf 6 80
            [ "C:\\> echo hi"; "hi"; ""; ""; "C:\\>"; "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 4 "C:\\>")
            snap2
    let priorTuple = s2.History.ToArray().[0]
    // Empty rows 2 + 3 dropped; only "hi" remains.
    Assert.Equal("hi", priorTuple.OutputText)

[<Fact>]
let ``Cycle 20b — finalizeIncomplete (shell-switch) leaves CmdText + OutText empty`` () =
    // Shell-switch path: finalizeIncomplete passes None /
    // None / [||] for extraction context. Tuple should
    // finalise without content extraction.
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            (snapshotOf 3 80 [ "C:\\>"; ""; "" ])
    // Now finalize-incomplete (mimics shell-switch).
    let finalised =
        SessionModel.finalizeIncomplete s1 (after 100)
    Assert.Equal(1, finalised.History.Count)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal("", tuple.CommandText)
    Assert.Equal("", tuple.OutputText)

[<Fact>]
let ``Cycle 20b — Active.Tuple.PromptRowIndex captured from boundary`` () =
    // Verify the row index threads from boundary.MatchedRowIndex
    // → Active.PromptRowIndex on PromptStart.
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 5 "C:\\>")
            (snapshotOf 10 80 [ ""; ""; ""; ""; ""; "C:\\>"; ""; ""; ""; "" ])
    match s1.Active with
    | Some active ->
        Assert.Equal(Some 5, active.PromptRowIndex)
    | None -> Assert.Fail("Expected Active after PromptStart")

// ---------------------------------------------------------------------
// Cycle 22b — formatHistoryForClipboard
// ---------------------------------------------------------------------

[<Fact>]
let ``formatHistoryForClipboard empty session shows '(no entries)' marker`` () =
    let state = SessionModel.create "cmd" 100
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("=== pty-speak session history ===", text)
    Assert.Contains("Shell:             cmd", text)
    Assert.Contains("History:           0 of 100", text)
    Assert.Contains("(no entries; session has not yet captured any prompt boundaries)", text)
    // No entry block in empty case.
    Assert.DoesNotContain("--- Entry", text)
    Assert.DoesNotContain("--- Active", text)

[<Fact>]
let ``formatHistoryForClipboard renders snapshot timestamp in header`` () =
    let state = SessionModel.create "cmd" 100
    let now = DateTime(2026, 5, 8, 18, 30, 45, 123, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Snapshot:          2026-05-08T18:30:45.123Z", text)

[<Fact>]
let ``formatHistoryForClipboard with one finalised tuple shows Entry 1`` () =
    // Run a full A → finalize cycle so we get one history entry.
    let initial = SessionModel.create "cmd" 100
    let snap1 = snapshotOf 3 80 [ "C:\\>"; ""; "" ]
    let snap2 = snapshotOf 5 80 [ "C:\\> echo hi"; "hi"; ""; "C:\\>"; "" ]
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            snap1
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 3 "C:\\>")
            snap2
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now s2
    Assert.Contains("History:           1 of 100", text)
    Assert.Contains("--- Entry 1 ---", text)
    Assert.Contains("Prompt:            C:\\>", text)
    Assert.Contains("Command:           echo hi", text)
    Assert.Contains("Output:            hi", text)
    Assert.Contains("ExitCode:          (none)", text)
    // Active block also appears since s2 has a new active
    // tuple at the new prompt.
    Assert.Contains("--- Active (in flight) ---", text)
    Assert.Contains("State:             AwaitingCommandStart", text)

[<Fact>]
let ``formatHistoryForClipboard preserves multi-line CommandText verbatim`` () =
    // Synthesise a tuple with embedded newlines in CommandText
    // by enqueueing one directly via finalizeIncomplete, then
    // mutating via apply isn't easy — instead build a state by
    // hand for the formatter test.
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let history = System.Collections.Generic.Queue<SessionModel.SessionTuple>()
    let multilineCmd = "echo line1\necho line2\necho line3"
    history.Enqueue(
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = t0
          CommandStartedAt = Some (after 100)
          OutputStartedAt = Some (after 200)
          CommandFinishedAt = Some (after 300)
          PromptText = "C:\\>"
          CommandText = multilineCmd
          OutputText = "line1\nline2\nline3"
          ExitCode = Some 0
          Sources = Map.empty
          ExtraParams = Map.empty })
    let state : SessionModel.T =
        { ShellId = "cmd"
          SessionId = Guid.NewGuid()
          SessionStartedAt = t0
          History = history
          MaxHistorySize = 100
          Active = None
          IsAltScreenActive = false }
    let text = SessionModel.formatHistoryForClipboard now state
    // Full multi-line content preserved (no truncation).
    Assert.Contains("echo line1\necho line2\necho line3", text)
    Assert.Contains("line1\nline2\nline3", text)
    Assert.Contains("ExitCode:          0", text)

[<Fact>]
let ``formatHistoryForClipboard renders empty fields as '(empty)' marker`` () =
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let history = System.Collections.Generic.Queue<SessionModel.SessionTuple>()
    history.Enqueue(
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = t0
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = Some (after 100)
          PromptText = "C:\\>"
          CommandText = ""
          OutputText = ""
          ExitCode = None
          Sources = Map.empty
          ExtraParams = Map.empty })
    let state : SessionModel.T =
        { ShellId = "cmd"
          SessionId = Guid.NewGuid()
          SessionStartedAt = t0
          History = history
          MaxHistorySize = 100
          Active = None
          IsAltScreenActive = false }
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Command:           (empty)", text)
    Assert.Contains("Output:            (empty)", text)
    Assert.Contains("CommandStarted:    (none)", text)
    Assert.Contains("OutputStarted:     (none)", text)

[<Fact>]
let ``formatHistoryForClipboard renders Sources map with boundary-source provenance`` () =
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let history = System.Collections.Generic.Queue<SessionModel.SessionTuple>()
    history.Enqueue(
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = t0
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = Some (after 100)
          PromptText = "C:\\>"
          CommandText = ""
          OutputText = ""
          ExitCode = None
          Sources =
            Map.ofList
                [ BoundaryKind.PromptStart,
                  BoundarySource.HeuristicPromptRegex 100 ]
          ExtraParams = Map.empty })
    let state : SessionModel.T =
        { ShellId = "cmd"
          SessionId = Guid.NewGuid()
          SessionStartedAt = t0
          History = history
          MaxHistorySize = 100
          Active = None
          IsAltScreenActive = false }
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Source(s):         PromptStart=HeuristicPromptRegex(100ms)", text)

[<Fact>]
let ``formatHistoryForClipboard active-only (no history) renders Active block`` () =
    let initial = SessionModel.create "cmd" 100
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            (snapshotOf 3 80 [ "C:\\>"; ""; "" ])
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now s1
    Assert.Contains("History:           0 of 100", text)
    Assert.DoesNotContain("--- Entry", text)
    Assert.Contains("--- Active (in flight) ---", text)
    Assert.Contains("State:             AwaitingCommandStart", text)
    Assert.Contains("PromptRowIndex:    0", text)

[<Fact>]
let ``formatHistoryForClipboard renders shell + session id + AltScreenActive flag`` () =
    let state =
        { SessionModel.create "powershell" 50 with
            IsAltScreenActive = true }
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Shell:             powershell", text)
    Assert.Contains("History:           0 of 50", text)
    Assert.Contains("AltScreenActive:   True", text)
    Assert.Contains(string state.SessionId, text)

[<Fact>]
let ``formatHistoryForClipboard preserves full content (no truncation)`` () =
    // Cycle 22b explicit decision: clipboard format does NOT
    // truncate (unlike Diagnostics.formatTuple's 80-char cap).
    // Pin the contract — long content must survive verbatim.
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let history = System.Collections.Generic.Queue<SessionModel.SessionTuple>()
    let longCmd = String.replicate 200 "x"
    let longOut = String.replicate 500 "y"
    history.Enqueue(
        { Id = Guid.NewGuid()
          CommandId = None
          ShellId = "cmd"
          PromptStartedAt = t0
          CommandStartedAt = Some (after 100)
          OutputStartedAt = Some (after 200)
          CommandFinishedAt = Some (after 300)
          PromptText = "C:\\>"
          CommandText = longCmd
          OutputText = longOut
          ExitCode = Some 0
          Sources = Map.empty
          ExtraParams = Map.empty })
    let state : SessionModel.T =
        { ShellId = "cmd"
          SessionId = Guid.NewGuid()
          SessionStartedAt = t0
          History = history
          MaxHistorySize = 100
          Active = None
          IsAltScreenActive = false }
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains(longCmd, text)
    Assert.Contains(longOut, text)

[<Fact>]
let ``formatHistoryForClipboard history entries appear oldest first`` () =
    // Three full prompt cycles → three history entries. Verify
    // ordering: Entry 1 = oldest, Entry 3 = most recent.
    let initial = SessionModel.create "cmd" 100
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\one>")
            (snapshotOf 3 80 [ "C:\\one>"; ""; "" ])
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithRow BoundaryKind.PromptStart (after 1000) 1 "C:\\two>")
            (snapshotOf 3 80 [ "C:\\one>"; "C:\\two>"; "" ])
    let s3 =
        SessionModel.apply
            s2
            (boundaryWithRow BoundaryKind.PromptStart (after 2000) 2 "C:\\three>")
            (snapshotOf 3 80 [ "C:\\one>"; "C:\\two>"; "C:\\three>" ])
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now s3
    let oneIdx = text.IndexOf("C:\\one>")
    let twoIdx = text.IndexOf("C:\\two>")
    let threeIdx = text.IndexOf("C:\\three>")
    Assert.True(oneIdx >= 0)
    Assert.True(twoIdx > oneIdx)
    Assert.True(threeIdx > twoIdx)
    Assert.Contains("--- Entry 1 ---", text)
    Assert.Contains("--- Entry 2 ---", text)

// =====================================================================
// Cycle 45c PR-3c — Cycle 35b LinearTextStream-substrate tests
// removed alongside the substrate. The Cycle 45c ContentHistory-driven
// tests above cover the same contract; the regression-check below
// pins the public surface (`apply` / `applyAndCapture`) that 80+
// legacy facts depend on.
// =====================================================================

[<Fact>]
let ``legacy apply / applyAndCapture API unchanged (regression check)`` () =
    // Confirms the existing public surface still works exactly
    // as before — 80+ legacy facts depend on this.
    let initial = SessionModel.create "cmd" 100
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.True(s1.Active.IsSome)
    let s2, finalisedOpt =
        SessionModel.applyAndCapture
            s1 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
    Assert.True(finalisedOpt.IsSome)
    Assert.True(s2.Active.IsNone)

// =====================================================================
// Cycle 45c — ContentHistory-driven substrate
// (`applyAndCaptureWithContentHistory`). Mirror the Cycle 35b tests
// against the new substrate. PR-3c retires the LinearTextStream
// counterparts above when the substrate itself is deleted.
// =====================================================================

let private freshHistory () : ContentHistory.T =
    ContentHistory.create ContentHistory.defaultParameters

let private feedHistoryBytes
        (history: ContentHistory.T)
        (now: DateTime)
        (bytes: byte[])
        : ContentHistory.T =
    let parser = Terminal.Parser.Parser.create ()
    let events = Terminal.Parser.Parser.feedArray parser bytes
    for ev in events do
        ContentHistory.appendFromEvent history now ev |> ignore
    history

let private appendHistoryMarker
        (history: ContentHistory.T)
        (now: DateTime)
        (kind: ContentHistory.MarkerKind)
        : ContentHistory.T =
    ContentHistory.appendMarker history kind now None |> ignore
    history

[<Fact>]
let ``Cycle 45c — useContentHistory=false bypasses the substrate entirely`` () =
    // ScreenDiff mode equivalent: ContentHistory content irrelevant;
    // behaviour identical to the legacy `applyAndCapture` API.
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history = feedHistoryBytes history t0 (System.Text.Encoding.ASCII.GetBytes "irrelevant\n")
    let s1 = SessionModel.applyWithContentHistory
                initial (boundary BoundaryKind.PromptStart t0) [||]
                history false
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history false
    let _ = SessionModel.applyWithContentHistory
                s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
                history false
    Assert.True(true)

[<Fact>]
let ``Cycle 45c — useContentHistory=true + OSC 133 markers populates CommandText/OutputText from history`` () =
    let initial = SessionModel.create "claude" 100
    // Build a ContentHistory mirroring the canonical OSC 133
    // sequence: PromptStart marker → command text → OutputStart
    // marker → output text. handlePromptBoundary in Program.fs is
    // the production analogue.
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history = feedHistoryBytes history (after 5) (System.Text.Encoding.ASCII.GetBytes "echo hello\n")
    let history = appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let history = feedHistoryBytes history (after 15) (System.Text.Encoding.ASCII.GetBytes "hello world\n")
    // Drive the SessionModel cycle. The PromptStart / CommandStart
    // / CommandFinished arms below mirror the calls
    // `handlePromptBoundary` makes against incoming boundaries.
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
                history true
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    // CommandText / OutputText flowed from ContentHistory.sliceText
    // (NOT from extractContent's row-walk against the [||] snapshot).
    Assert.Contains("echo hello", tuple.CommandText)
    Assert.Contains("hello world", tuple.OutputText)

[<Fact>]
let ``Cycle 45c — useContentHistory=true but no OSC 133 markers falls back to extractContent`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    // No PromptStart / OutputStart appended → tryLatestMarker
    // returns None → extractContentFromContentHistory returns None
    // → caller falls back to extractContent.
    let history = feedHistoryBytes history t0 (System.Text.Encoding.ASCII.GetBytes "irrelevant\n")
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "C:\\>") [||]
                history true
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    // extractContent fallback against [||] snapshot returns empty
    // strings; the history's "irrelevant" content must NOT appear
    // (no markers gate the substrate path).
    Assert.DoesNotContain("irrelevant", tuple.OutputText)

[<Fact>]
let ``Cycle 45c — useContentHistory=false ignores OSC 133 markers (history has them)`` () =
    let initial = SessionModel.create "claude" 100
    let history = freshHistory ()
    // Markers ARE present; but useContentHistory=false. The
    // substrate path must NOT be consulted.
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history = feedHistoryBytes history (after 5) (System.Text.Encoding.ASCII.GetBytes "echo from-history\n")
    let history = appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let history = feedHistoryBytes history (after 15) (System.Text.Encoding.ASCII.GetBytes "history-output\n")
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
                history false  // useContentHistory = false
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history false
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history false
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    Assert.DoesNotContain("from-history", tuple.CommandText)
    Assert.DoesNotContain("history-output", tuple.OutputText)

[<Fact>]
let ``Cycle 45c — applyAndCaptureWithContentHistory preserves Active state through PromptStart cycle`` () =
    // Behavioural smoke test mirroring the Cycle 35b state-machine
    // test. The substrate-aware path must leave SessionModel in
    // the same active-state as the legacy path for non-finalize
    // boundaries.
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let s1 = SessionModel.applyWithContentHistory
                initial (boundary BoundaryKind.PromptStart t0) [||]
                history true
    Assert.True(s1.Active.IsSome)
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    Assert.True(s2.Active.IsSome)
    Assert.Equal(SessionModel.ActiveTupleState.EditingCommand, s2.Active.Value.State)
    let s3 = SessionModel.applyWithContentHistory
                s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
                history true
    Assert.True(s3.Active.IsSome)
    Assert.Equal(SessionModel.ActiveTupleState.OutputStreaming, s3.Active.Value.State)

// =====================================================================
// Cycle 45c fixup (2026-05-12) — heuristic-only path: when only
// PromptStart markers are present (no OSC 133 OutputStart), slice
// the blob between the prior PromptStart and the tail. Reproduces
// the maintainer's "second echo hi after dir is silent" report:
// the prior `extractContent` row-walk fails when output scrolls
// enough that consecutive prompts share a row index, but the
// ContentHistory has the typed-input + output bytes accurately.
// =====================================================================

[<Fact>]
let ``Cycle 45c fixup — PromptStart-only path slices blob between consecutive prompts`` () =
    // Simulate cmd: prior PromptStart marker in history; then typed
    // input "echo hi", newline, output "hi", newline, new prompt text.
    // The boundary fires with MatchedRowText = the new prompt text.
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    // Prior prompt's PromptStart marker
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    // User-typed input + shell echo + output + new prompt text
    let history = feedHistoryBytes history (after 5) (System.Text.Encoding.ASCII.GetBytes "echo hi\nhi\nC:\\>")
    // Drive the SessionModel cycle. handlePromptBoundary calls
    // applyAndCaptureWithContentHistory with the new boundary; the
    // thunk runs extractContentFromContentHistory which should
    // detect "PromptStart present, OutputStart absent" and slice.
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
                history true
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundaryWithText (BoundaryKind.CommandFinished (Some 0)) (after 200) "C:\\>")
            [||]
            history true
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    // The blob "echo hi\nhi\nC:\\>" with new-prompt trim → "echo hi\nhi"
    // → split on first newline → ("echo hi", "hi").
    Assert.Equal("echo hi", tuple.CommandText)
    Assert.Equal("hi", tuple.OutputText)

[<Fact>]
let ``Cycle 45c fixup — PromptStart-only path with empty newPromptText doesn't crash`` () =
    // When the boundary has no MatchedRowText (e.g. OSC 133 source),
    // the trim path is skipped. The blob then includes any trailing
    // text that may have been added; it's still split on newline.
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history = feedHistoryBytes history (after 5) (System.Text.Encoding.ASCII.GetBytes "ls\nfile1\nfile2\n")
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
                history true
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    // CommandFinished boundary with no MatchedRowText (MatchedRowText=None).
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    Assert.Equal("ls", tuple.CommandText)
    Assert.Contains("file1", tuple.OutputText)
    Assert.Contains("file2", tuple.OutputText)

[<Fact>]
let ``Cycle 45c fixup — OSC 133 path still wins when both markers present`` () =
    // Regression pin: if both PromptStart AND OutputStart are
    // present, the OSC 133 split must win — not the heuristic
    // blob path.
    let initial = SessionModel.create "claude" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history = feedHistoryBytes history (after 5) (System.Text.Encoding.ASCII.GetBytes "explain this\n")
    let history = appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let history = feedHistoryBytes history (after 15) (System.Text.Encoding.ASCII.GetBytes "Here is the explanation.\n")
    let s1 = SessionModel.applyWithContentHistory
                initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
                history true
    let s2 = SessionModel.applyWithContentHistory
                s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
                history true
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true
    Assert.True(finalisedOpt.IsSome)
    let tuple = finalisedOpt.Value
    // OSC 133 split: command between PromptStart and OutputStart,
    // output between OutputStart and tail.
    Assert.Contains("explain this", tuple.CommandText)
    Assert.Contains("Here is the explanation", tuple.OutputText)
    // The command must NOT appear in the output (OSC 133 split is
    // clean).
    Assert.DoesNotContain("explain this", tuple.OutputText)
