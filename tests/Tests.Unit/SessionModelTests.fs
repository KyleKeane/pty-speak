module PtySpeak.Tests.Unit.SessionModelTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// SessionModel Tier 1.A — substrate skeleton behavioural pinning
// ---------------------------------------------------------------------
//
// Tier 1.A scope: types only. Tests pin:
//   * The substrate types (`BoundaryKind`, `BoundarySource`,
//     `PromptBoundaryData`) construct cleanly + round-trip
//     through pattern matches.
//   * `ScreenNotification.PromptBoundary` is a fully-typed
//     case carrying `PromptBoundaryData`.
//   * `SessionModel.create` returns the documented initial
//     state (empty History, no Active tuple, fresh
//     SessionId).
//   * `SessionModel.createDefault` uses
//     `DefaultMaxHistorySize`.
//   * `SessionModel.apply` is a no-op stub (Tier 1.A
//     contract): returns state unchanged for any boundary.
//
// State-machine tests (Active state transitions, History
// enqueue/eviction) ship in Tier 1.C when the real `apply`
// state machine lands.

// ---------------------------------------------------------------------
// BoundaryKind cases
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
// BoundarySource cases
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
// PromptBoundaryData record
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
// ScreenNotification.PromptBoundary
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
// SessionModel.create + createDefault
// ---------------------------------------------------------------------

[<Fact>]
let ``SessionModel.create initialises empty History + None Active`` () =
    let model = SessionModel.create "cmd" 50
    Assert.Equal("cmd", model.ShellId)
    Assert.Equal(50, model.MaxHistorySize)
    Assert.Equal(0, model.History.Count)
    Assert.Equal(None, model.Active)

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
// SessionModel.apply (Tier 1.A no-op contract)
// ---------------------------------------------------------------------

[<Fact>]
let ``SessionModel.apply returns state unchanged in Tier 1.A`` () =
    let original = SessionModel.create "cmd" 50
    let boundary : PromptBoundaryData =
        { Kind = BoundaryKind.PromptStart
          Source = BoundarySource.Osc133
          DetectedAt = DateTime.UtcNow
          CommandId = None
          ExtraParams = Map.empty }
    let updated = SessionModel.apply original boundary
    // Reference equality holds: Tier 1.A's apply is the identity.
    Assert.True(obj.ReferenceEquals(original, updated))
    // And every field matches.
    Assert.Equal(original.ShellId, updated.ShellId)
    Assert.Equal(original.SessionId, updated.SessionId)
    Assert.Equal(original.MaxHistorySize, updated.MaxHistorySize)
    Assert.Equal(0, updated.History.Count)
    Assert.Equal(None, updated.Active)

[<Fact>]
let ``SessionModel.apply is a no-op for every BoundaryKind`` () =
    let model = SessionModel.create "claude" 100
    let boundaries =
        [ BoundaryKind.PromptStart
          BoundaryKind.CommandStart
          BoundaryKind.OutputStart
          BoundaryKind.CommandFinished None
          BoundaryKind.CommandFinished (Some 0)
          BoundaryKind.CommandFinished (Some 1) ]
    let now = DateTime.UtcNow
    for kind in boundaries do
        let boundary : PromptBoundaryData =
            { Kind = kind
              Source = BoundarySource.Osc133
              DetectedAt = now
              CommandId = None
              ExtraParams = Map.empty }
        let updated = SessionModel.apply model boundary
        Assert.Equal(0, updated.History.Count)
        Assert.Equal(None, updated.Active)

// ---------------------------------------------------------------------
// SessionTuple shape (Q4 multi-line; Q8 ExitCode int option)
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
          // Multi-line command stored as one string with \n.
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
