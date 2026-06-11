module PtySpeak.Tests.Unit.EngineBusTests

open Xunit
open Engine.Core.EngineEvent

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §0.1 — universal event bus (engine instance).
// ---------------------------------------------------------------------
//
// Mirrors the CellEventBus contract suite, instance-scoped:
// delivery, unsubscribe, multi-subscriber fan-out, instance
// isolation, and the throwing-subscriber guard.

[<Fact>]
let ``a subscriber receives published events in order`` () =
    let bus = EngineBus()
    let received = ResizeArray<EngineEvent>()
    use _sub = bus.Subscribe(fun ev -> received.Add ev)
    bus.Publish(SessionStarted "s1")
    bus.Publish(ResponseProgress 1)
    Assert.Equal(2, received.Count)
    match received.[0], received.[1] with
    | SessionStarted "s1", ResponseProgress 1 -> ()
    | a, b -> failwithf "unexpected %A %A" a b

[<Fact>]
let ``disposing the subscription stops delivery`` () =
    let bus = EngineBus()
    let mutable count = 0
    let sub = bus.Subscribe(fun _ -> count <- count + 1)
    bus.Publish(ResponseProgress 1)
    sub.Dispose()
    bus.Publish(ResponseProgress 2)
    Assert.Equal(1, count)

[<Fact>]
let ``every subscriber receives every event`` () =
    let bus = EngineBus()
    let mutable a = 0
    let mutable b = 0
    use _s1 = bus.Subscribe(fun _ -> a <- a + 1)
    use _s2 = bus.Subscribe(fun _ -> b <- b + 1)
    bus.Publish(EngineNote "x")
    Assert.Equal(1, a)
    Assert.Equal(1, b)

[<Fact>]
let ``buses are instance-isolated`` () =
    let bus1 = EngineBus()
    let bus2 = EngineBus()
    let mutable count = 0
    use _sub = bus1.Subscribe(fun _ -> count <- count + 1)
    bus2.Publish(EngineNote "elsewhere")
    Assert.Equal(0, count)

[<Fact>]
let ``a throwing subscriber neither aborts others nor the publisher`` () =
    let bus = EngineBus()
    let mutable healthy = 0
    use _bad = bus.Subscribe(fun _ -> failwith "sink crash")
    use _good = bus.Subscribe(fun _ -> healthy <- healthy + 1)
    bus.Publish(EngineNote "still works")
    Assert.Equal(1, healthy)
