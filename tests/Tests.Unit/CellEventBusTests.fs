module PtySpeak.Tests.Unit.CellEventBusTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// ADR 0007 D9 / Phase 6a-1 — CellEventBus contract tests.
// ---------------------------------------------------------------------
//
// Pins the canonical typed cell-event bus:
//
//   * a subscriber receives published events
//   * disposing the subscription stops delivery
//   * multiple subscribers each receive every event
//   * clearForTests drops all subscribers (test isolation)
//   * a throwing subscriber neither aborts the others nor
//     propagates back into the publishing (navigation) caller
//
// No WPF / no sink rendering — 6a-1 is the pure pipeline; the
// first real subscriber (the history list) lands in 6a-2.

let private mkCell (seq: int64) : SpeechCursor.CellView =
    { CellId = Guid.NewGuid()
      CellSequence = seq
      Kind = SpeechCursor.CellKind.Output
      Text = sprintf "cell-%d" seq
      ActivityId = ActivityIds.output
      ExitCode = None }

// The cell-carrying cases share the typed `CellView`; the bus
// contract is kind-agnostic, so the helper extracts the
// sequence from either. `PaneSwitched` carries a `Pane`, not a
// cell — it is not exercised by these tests, so it maps to a
// sentinel to keep the match exhaustive.
let private cellSeq (ev: CellEventBus.CellEvent) : int64 =
    match ev with
    | CellEventBus.Focused cv
    | CellEventBus.Appended cv -> cv.CellSequence
    | CellEventBus.PaneSwitched _ -> -1L

[<Fact>]
let ``subscribe receives a published Focused event`` () =
    CellEventBus.clearForTests ()
    let received = ResizeArray<int64>()
    use _sub =
        CellEventBus.subscribe (fun ev ->
            received.Add(cellSeq ev))
    CellEventBus.publish (CellEventBus.Focused(mkCell 7L))
    Assert.Equal<int64 list>([ 7L ], List.ofSeq received)

[<Fact>]
let ``dispose stops further delivery`` () =
    CellEventBus.clearForTests ()
    let received = ResizeArray<int64>()
    let sub =
        CellEventBus.subscribe (fun ev ->
            received.Add(cellSeq ev))
    CellEventBus.publish (CellEventBus.Focused(mkCell 1L))
    sub.Dispose()
    CellEventBus.publish (CellEventBus.Focused(mkCell 2L))
    Assert.Equal<int64 list>([ 1L ], List.ofSeq received)

[<Fact>]
let ``multiple subscribers each receive the event`` () =
    CellEventBus.clearForTests ()
    let a = ResizeArray<int64>()
    let b = ResizeArray<int64>()
    use _sa = CellEventBus.subscribe (fun ev -> a.Add(cellSeq ev))
    use _sb = CellEventBus.subscribe (fun ev -> b.Add(cellSeq ev))
    CellEventBus.publish (CellEventBus.Focused(mkCell 5L))
    Assert.Equal<int64 list>([ 5L ], List.ofSeq a)
    Assert.Equal<int64 list>([ 5L ], List.ofSeq b)

[<Fact>]
let ``clearForTests drops all subscribers`` () =
    CellEventBus.clearForTests ()
    let received = ResizeArray<int64>()
    CellEventBus.subscribe (fun ev -> received.Add(cellSeq ev))
    |> ignore
    CellEventBus.clearForTests ()
    CellEventBus.publish (CellEventBus.Focused(mkCell 9L))
    Assert.Empty(received)

[<Fact>]
let ``a throwing subscriber does not break publish for the others`` () =
    CellEventBus.clearForTests ()
    let received = ResizeArray<int64>()
    use _bad =
        CellEventBus.subscribe (fun _ ->
            raise (InvalidOperationException("sink boom")))
    use _good =
        CellEventBus.subscribe (fun ev ->
            received.Add(cellSeq ev))
    // publish must not propagate the subscriber exception …
    CellEventBus.publish (CellEventBus.Focused(mkCell 3L))
    // … and the well-behaved subscriber still got the event.
    Assert.Equal<int64 list>([ 3L ], List.ofSeq received)

[<Fact>]
let ``Appended events are delivered in publish order`` () =
    CellEventBus.clearForTests ()
    let received = ResizeArray<int64>()
    use _sub =
        CellEventBus.subscribe (fun ev ->
            received.Add(cellSeq ev))
    CellEventBus.publish (CellEventBus.Appended(mkCell 1L))
    CellEventBus.publish (CellEventBus.Appended(mkCell 2L))
    Assert.Equal<int64 list>([ 1L; 2L ], List.ofSeq received)

[<Fact>]
let ``one subscriber receives both Focused and Appended`` () =
    CellEventBus.clearForTests ()
    let kinds = ResizeArray<string>()
    use _sub =
        CellEventBus.subscribe (fun ev ->
            match ev with
            | CellEventBus.Focused _ -> kinds.Add "focused"
            | CellEventBus.Appended _ -> kinds.Add "appended"
            | CellEventBus.PaneSwitched _ -> ())
    CellEventBus.publish (CellEventBus.Appended(mkCell 1L))
    CellEventBus.publish (CellEventBus.Focused(mkCell 1L))
    Assert.Equal<string list>(
        [ "appended"; "focused" ], List.ofSeq kinds)
