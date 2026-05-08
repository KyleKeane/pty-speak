module PtySpeak.Tests.Unit.SessionModelTests

open System
open Xunit
open Terminal.Core

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
      ExtraParams = Map.empty }

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
      ExtraParams = Map.ofList extras }

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
          ExtraParams = Map.ofList [ "k", "v" ] }
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
          ExtraParams = Map.empty }
    Assert.Equal(None, data.CommandId)
    Assert.True(Map.isEmpty data.ExtraParams)

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
          ExtraParams = Map.empty }
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
    let updated = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200))
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200))
    let s4 =
        SessionModel.apply
            s3
            (boundary (BoundaryKind.CommandFinished (Some 0)) (after 300))
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
        |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
    let final = seqs |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
            ExtraParams = Map.empty }
          { Kind = BoundaryKind.CommandStart
            Source = BoundarySource.HeuristicPromptRegex 100
            DetectedAt = after 100
            CommandId = None
            ExtraParams = Map.empty }
          { Kind = BoundaryKind.OutputStart
            Source = BoundarySource.HeuristicClaudeInkBox
            DetectedAt = after 200
            CommandId = None
            ExtraParams = Map.empty }
          { Kind = BoundaryKind.CommandFinished (Some 0)
            Source = BoundarySource.Osc133
            DetectedAt = after 300
            CommandId = None
            ExtraParams = Map.empty } ]
        |> List.fold SessionModel.apply initial
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    // Second PromptStart while EditingCommand — interrupt + restart.
    let s3 =
        SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 200))
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
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``OutputStart from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.OutputStart t0)
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``CommandFinished from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundary (BoundaryKind.CommandFinished (Some 0)) t0)
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``OutputStart in AwaitingCommandStart tolerates skipped CommandStart`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    // Skip CommandStart; jump straight to OutputStart.
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.OutputStart (after 100))
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.CommandStart (after 200))
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200))
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.OutputStart (after 250))
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200))
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.CommandStart (after 250))
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
        |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
        |> List.fold SessionModel.apply initial
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
    |> List.fold SessionModel.apply initial

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
        SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
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
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    Assert.Equal(None, s1.Active)
    // Exit alt-screen + send PromptStart again.
    let s2 = SessionModel.exitAltScreen s1
    let s3 =
        SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 100))
    Assert.True(s3.Active.IsSome)

// =====================================================================
// Tier 1.C — Q5 finalizeIncomplete (shell-switch helper)
// =====================================================================

[<Fact>]
let ``finalizeIncomplete with Active=Some moves to History with CommandFinishedAt set`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
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
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100))
    let finalised = SessionModel.finalizeIncomplete s2 (after 200)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal(Some "interrupt-test", tuple.CommandId)
    Assert.Equal(Some "alice", Map.tryFind "user" tuple.ExtraParams)
    Assert.Equal(Some (after 100), tuple.CommandStartedAt)

[<Fact>]
let ``finalizeIncomplete sets ExitCode=None`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0)
    let finalised = SessionModel.finalizeIncomplete s1 (after 100)
    let tuple = finalised.History.ToArray().[0]
    Assert.Equal(None, tuple.ExitCode)
