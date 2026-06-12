module PtySpeak.Tests.Unit.AttentionTests

open Xunit
open Engine.Core
open Engine.Core.Attention
open Engine.Core.EngineEvent

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §7.3 / ADR 0011 E6 — attention contract tests.
// ---------------------------------------------------------------------
//
// Pins the two-class output discipline: foreground FIFO is
// strict and non-preemptible; ambient coalesces latest-wins per
// key and never outranks foreground; the routing policy keeps
// sealed chunks silent (§5.2) and confirms requests foreground.

let private drain (q: Queue) : string list =
    let rec go acc q =
        match tryDequeue q with
        | Some (text, rest) -> go (acc @ [ text ]) rest
        | None -> acc
    go [] q

[<Fact>]
let ``empty queue dequeues nothing`` () =
    Assert.True(isEmpty empty)
    Assert.True((tryDequeue empty).IsNone)

[<Fact>]
let ``foreground is strict FIFO`` () =
    let q =
        empty
        |> enqueue (Foreground "one")
        |> enqueue (Foreground "two")
        |> enqueue (Foreground "three")
    Assert.Equal<string list>([ "one"; "two"; "three" ], drain q)

[<Fact>]
let ``ambient never outranks queued foreground`` () =
    let q =
        empty
        |> enqueue (Ambient ("progress", "2 chunks so far."))
        |> enqueue (Foreground "Sent: hello")
        |> enqueue (Ambient ("note", "fyi"))
        |> enqueue (Foreground "Response complete, 3 chunks.")
    Assert.Equal<string list>(
        [ "Sent: hello"
          "Response complete, 3 chunks."
          "2 chunks so far."
          "fyi" ],
        drain q)

[<Fact>]
let ``ambient coalesces latest-wins per key`` () =
    let q =
        empty
        |> enqueue (Ambient ("progress", "1 chunks so far."))
        |> enqueue (Ambient ("note", "first note"))
        |> enqueue (Ambient ("progress", "5 chunks so far."))
    Assert.Equal<string list>(
        [ "5 chunks so far."; "first note" ],
        drain q)

[<Fact>]
let ``distinct ambient keys are all preserved`` () =
    let q =
        empty
        |> enqueue (Ambient ("a", "alpha"))
        |> enqueue (Ambient ("b", "beta"))
    Assert.Equal<string list>([ "alpha"; "beta" ], drain q)

// --- routing policy -------------------------------------------------

let private mkChunk (text: string) : Chunk.Chunk =
    match ChunkTree.append None Chunk.UserRequest text ChunkTree.empty with
    | Ok (c, _) -> c
    | Error e -> failwith e

[<Fact>]
let ``a captured request is confirmed foreground`` () =
    match route (RequestCaptured (mkChunk "fix the bug")) with
    | Some (Foreground text) -> Assert.Equal("Sent: fix the bug", text)
    | other -> failwithf "expected foreground confirm, got %A" other

[<Fact>]
let ``sealed chunks are silent by policy`` () =
    Assert.True((route (ChunkSealed (mkChunk "x"))).IsNone)

[<Fact>]
let ``progress is ambient on the progress key`` () =
    match route (ResponseProgress 4) with
    | Some (Ambient ("progress", text)) ->
        Assert.Equal("4 chunks so far.", text)
    | other -> failwithf "expected ambient progress, got %A" other

[<Fact>]
let ``completion is foreground and error-aware`` () =
    match route (ResponseCompleted (false, 7)) with
    | Some (Foreground "Response complete, 7 chunks.") -> ()
    | other -> failwithf "unexpected %A" other
    match route (ResponseCompleted (true, 2)) with
    | Some (Foreground "Response failed after 2 chunks.") -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``notes are ambient`` () =
    match route (EngineNote "Unrecognized stream event type: x") with
    | Some (Ambient ("note", _)) -> ()
    | other -> failwithf "expected ambient note, got %A" other

// --- ADR 0012 S5 audit fixes ----------------------------------------

[<Fact>]
let ``clearForeground drops pending narrative but keeps ambient`` () =
    let q =
        empty
        |> enqueue (Foreground "stale completion")
        |> enqueue (Ambient ("progress", "3 chunks so far."))
        |> enqueue (Foreground "another stale")
    Assert.Equal<string list>(
        [ "3 chunks so far." ],
        drain (clearForeground q))

[<Fact>]
let ``clear drops everything`` () =
    let q =
        empty
        |> enqueue (Foreground "a")
        |> enqueue (Ambient ("k", "b"))
    Assert.True(isEmpty (clear q))
