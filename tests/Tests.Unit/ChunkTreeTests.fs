module PtySpeak.Tests.Unit.ChunkTreeTests

open Xunit
open Engine.Core
open Engine.Core.Chunk

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §5 / ADR 0011 — chunk-tree model contract tests.
// ---------------------------------------------------------------------
//
// Pins the locked data model's invariants:
//
//   * stable ids — every append allocates a fresh durable id
//   * capture order — monotonic, immutable, session-wide
//   * authored order — per-parent insertion index in v1
//   * tree shape — children / parent / sibling navigation is
//     total (option-returning) and never throws
//   * branching — a chunk appended under an anchor nests there
//     and the main thread's top level is untouched (§8.1's
//     no-drift invariant at the model layer)
//
// Pure model only — ingest (sealing) and navigation verbs land
// in their own PRs with their own tests.

/// Unwrap an append that the test arranged to be valid.
let private ok (r: Result<Chunk.Chunk * ChunkTree.Tree, string>) =
    match r with
    | Ok v -> v
    | Error e -> failwithf "expected Ok, got Error %s" e

[<Fact>]
let ``empty tree has no chunks and no top-level children`` () =
    Assert.Equal(0, ChunkTree.count ChunkTree.empty)
    Assert.Empty(ChunkTree.children None ChunkTree.empty)

[<Fact>]
let ``append assigns fresh ids and monotonic capture sequence`` () =
    let a, t1 = ok (ChunkTree.append None UserRequest "first" ChunkTree.empty)
    let b, t2 = ok (ChunkTree.append None Paragraph "second" t1)
    Assert.NotEqual<ChunkId>(a.Id, b.Id)
    Assert.Equal(0, a.CaptureSeq)
    Assert.Equal(1, b.CaptureSeq)
    Assert.Equal(2, ChunkTree.count t2)

[<Fact>]
let ``authored index is the per-parent insertion position`` () =
    let req, t1 = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let c1, t2 = ok (ChunkTree.append (Some req.Id) Paragraph "p1" t1)
    let c2, t3 = ok (ChunkTree.append (Some req.Id) Paragraph "p2" t2)
    // A second top-level chunk gets top-level index 1, not 3 —
    // authored order is per parent, capture order is global.
    let top2, _t4 = ok (ChunkTree.append None UserRequest "req2" t3)
    Assert.Equal(0, c1.AuthoredIndex)
    Assert.Equal(1, c2.AuthoredIndex)
    Assert.Equal(1, top2.AuthoredIndex)
    Assert.Equal(3, top2.CaptureSeq)

[<Fact>]
let ``append under a missing parent is an Error not an exception`` () =
    let missing = newId ()
    match ChunkTree.append (Some missing) Paragraph "orphan" ChunkTree.empty with
    | Error _ -> ()
    | Ok _ -> failwith "expected Error for a missing parent"

[<Fact>]
let ``children returns authored order and parent inverts it`` () =
    let req, t1 = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let c1, t2 = ok (ChunkTree.append (Some req.Id) (Heading 2) "h" t1)
    let c2, t3 = ok (ChunkTree.append (Some req.Id) Paragraph "p" t2)
    let kids = ChunkTree.children (Some req.Id) t3
    Assert.Equal<ChunkId list>(
        [ c1.Id; c2.Id ],
        kids |> List.map (fun c -> c.Id))
    match ChunkTree.parent c1.Id t3 with
    | Some p -> Assert.Equal<ChunkId>(req.Id, p.Id)
    | None -> failwith "expected a parent"
    Assert.True((ChunkTree.parent req.Id t3).IsNone)

[<Fact>]
let ``sibling navigation walks authored order and stops at edges`` () =
    let req, t1 = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let c1, t2 = ok (ChunkTree.append (Some req.Id) Paragraph "p1" t1)
    let c2, t3 = ok (ChunkTree.append (Some req.Id) Paragraph "p2" t2)
    let c3, t4 = ok (ChunkTree.append (Some req.Id) Paragraph "p3" t3)
    let nextOf id = ChunkTree.nextSibling id t4 |> Option.map (fun c -> c.Id)
    let prevOf id = ChunkTree.prevSibling id t4 |> Option.map (fun c -> c.Id)
    Assert.Equal(Some c2.Id, nextOf c1.Id)
    Assert.Equal(Some c3.Id, nextOf c2.Id)
    Assert.Equal(None, nextOf c3.Id)
    Assert.Equal(Some c1.Id, prevOf c2.Id)
    Assert.Equal(None, prevOf c1.Id)

[<Fact>]
let ``descend reaches the first child only when one exists`` () =
    let req, t1 = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let list, t2 = ok (ChunkTree.append (Some req.Id) (ListBlock false) "list" t1)
    let item, t3 = ok (ChunkTree.append (Some list.Id) ListItem "item one" t2)
    Assert.Equal(
        Some item.Id,
        ChunkTree.firstChild list.Id t3 |> Option.map (fun c -> c.Id))
    Assert.True((ChunkTree.firstChild item.Id t3).IsNone)

[<Fact>]
let ``a branch anchored at a chunk does not disturb the main thread`` () =
    // Spec §5.1/§8.1 — a clarification is a child branch under
    // its anchor; the spine being navigated is untouched.
    let req, t1 = ok (ChunkTree.append None UserRequest "main req" ChunkTree.empty)
    let p1, t2 = ok (ChunkTree.append (Some req.Id) Paragraph "answer A" t1)
    let p2, t3 = ok (ChunkTree.append (Some req.Id) Paragraph "answer B" t2)
    // Branch: "what do you mean here?" anchored at p1.
    let branchReq, t4 =
        ok (ChunkTree.append (Some p1.Id) UserRequest "what do you mean?" t3)
    let _branchAns, t5 =
        ok (ChunkTree.append (Some branchReq.Id) Paragraph "I mean..." t4)
    // Main-thread shape is unchanged: top level is still just
    // the request, whose children are still exactly A then B.
    Assert.Equal<ChunkId list>(
        [ req.Id ],
        ChunkTree.children None t5 |> List.map (fun c -> c.Id))
    Assert.Equal<ChunkId list>(
        [ p1.Id; p2.Id ],
        ChunkTree.children (Some req.Id) t5 |> List.map (fun c -> c.Id))
    // The branch nests under its anchor.
    Assert.Equal(
        Some branchReq.Id,
        ChunkTree.firstChild p1.Id t5 |> Option.map (fun c -> c.Id))

[<Fact>]
let ``capture order is the global temporal transcript`` () =
    let req, t1 = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let kid, t2 = ok (ChunkTree.append (Some req.Id) Paragraph "p" t1)
    let top, t3 = ok (ChunkTree.append None UserRequest "req2" t2)
    Assert.Equal<ChunkId list>(
        [ req.Id; kid.Id; top.Id ],
        ChunkTree.inCaptureOrder t3 |> List.map (fun c -> c.Id))
