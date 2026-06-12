module PtySpeak.Tests.Unit.EngineDiagnosticsTests

open Xunit
open Engine.Core
open Engine.Core.EngineDiagnostics

// ---------------------------------------------------------------------
// ADR 0014 C4 — diagnostics ring contract: bounded retention,
// lifetime counts, last-error tracking, speakable summary,
// grep-friendly dump, bus-event rendering.
// ---------------------------------------------------------------------

[<Fact>]
let ``recording accumulates entries and counts`` () =
    let ring = Ring()
    ring.Record "note" "one"
    ring.Record "note" "two"
    ring.Record "seal" "x"
    Assert.Equal(2, ring.CountOf "note")
    Assert.Equal(1, ring.CountOf "seal")
    Assert.Equal(0, ring.CountOf "error")
    Assert.Equal(3, ring.Snapshot() |> List.length)

[<Fact>]
let ``the ring is bounded but counts are lifetime`` () =
    let ring = Ring(3)
    for i in 1 .. 10 do
        ring.Record "note" (string i)
    let kept = ring.Snapshot()
    Assert.Equal(3, List.length kept)
    // Oldest evicted; the tail survives.
    Assert.Equal<string list>(
        [ "8"; "9"; "10" ],
        kept |> List.map (fun e -> e.Message))
    Assert.Equal(10, ring.CountOf "note")

[<Fact>]
let ``the summary speaks counts and the last error`` () =
    let ring = Ring()
    ring.Record "completed" "chunks=4"
    ring.Record "error" "participant exited with code 1"
    let summary = ring.Summary()
    Assert.Contains("completed 1", summary)
    Assert.Contains("error 1", summary)
    Assert.Contains("participant exited with code 1", summary)

[<Fact>]
let ``the summary without events says so`` () =
    Assert.Contains("No events recorded", Ring().Summary())

[<Fact>]
let ``the dump carries the header and every retained line`` () =
    let ring = Ring()
    ring.Record "note" "alpha"
    ring.Record "seal" "beta"
    let dump = ring.Dump()
    Assert.Contains("engine diagnostics dump", dump)
    Assert.Contains("[note] alpha", dump)
    Assert.Contains("[seal] beta", dump)

[<Fact>]
let ``bus events render with stable categories`` () =
    let chunk =
        match ChunkTree.append None Chunk.Paragraph "hello world" ChunkTree.empty with
        | Ok (c, _) -> c
        | Error e -> failwith e
    Assert.Equal(
        "request",
        fst (describeEvent (EngineEvent.RequestCaptured chunk)))
    Assert.Equal(
        "seal",
        fst (describeEvent (EngineEvent.ChunkSealed chunk)))
    Assert.Equal(
        "error",
        fst (describeEvent (EngineEvent.ResponseCompleted (true, 2))))
    Assert.Equal(
        "completed",
        fst (describeEvent (EngineEvent.ResponseCompleted (false, 2))))

[<Fact>]
let ``event bodies are clipped in diagnostics`` () =
    let long = String.replicate 30 "abcdefghij"
    let chunk =
        match ChunkTree.append None Chunk.UserRequest long ChunkTree.empty with
        | Ok (c, _) -> c
        | Error e -> failwith e
    let _, body = describeEvent (EngineEvent.RequestCaptured chunk)
    Assert.True(body.Length <= 82)
    Assert.EndsWith("…", body)
