module PtySpeak.Tests.Unit.NavigatorTests

open Xunit
open Engine.Core
open Engine.Core.Chunk
open Engine.Core.Navigator

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §6.2 / §6.4 — navigation-verb contract tests.
// ---------------------------------------------------------------------
//
// Pins the v1 verbs over a fixed small tree and the
// non-ejection invariant: an Edge outcome never moves focus,
// and no verb can focus a chunk outside the tree.

/// Fixture: req ── [p1; list ── [item1; item2]; p2]
type private Fixture =
    { Tree: ChunkTree.Tree
      Req: Chunk.Chunk
      P1: Chunk.Chunk
      List: Chunk.Chunk
      Item1: Chunk.Chunk
      Item2: Chunk.Chunk
      P2: Chunk.Chunk }

let private ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "fixture build failed: %s" e

let private fixture () : Fixture =
    let req, t = ok (ChunkTree.append None UserRequest "req" ChunkTree.empty)
    let p1, t = ok (ChunkTree.append (Some req.Id) Paragraph "first" t)
    let list, t = ok (ChunkTree.append (Some req.Id) (ListBlock false) "" t)
    let item1, t = ok (ChunkTree.append (Some list.Id) ListItem "alpha" t)
    let item2, t = ok (ChunkTree.append (Some list.Id) ListItem "beta" t)
    let p2, t = ok (ChunkTree.append (Some req.Id) Paragraph "last" t)
    { Tree = t; Req = req; P1 = p1; List = list
      Item1 = item1; Item2 = item2; P2 = p2 }

let private movedTo (expected: ChunkId) (move: Move) =
    match move with
    | Moved c -> Assert.Equal<ChunkId>(expected, c.Id)
    | other -> failwithf "expected Moved, got %A" other

[<Fact>]
let ``verbs report NothingFocused before any focus`` () =
    let f = fixture ()
    let _, move = Navigator.next Navigator.initial f.Tree
    match move with
    | NothingFocused -> ()
    | other -> failwithf "expected NothingFocused, got %A" other

[<Fact>]
let ``jump to latest response focuses its first chunk`` () =
    let f = fixture ()
    let state, move =
        Navigator.jumpToLatestResponse (Some f.P1.Id) Navigator.initial f.Tree
    movedTo f.P1.Id move
    Assert.Equal(Some f.P1.Id, state.Current)

[<Fact>]
let ``jump with no response yet is an Edge and keeps focus`` () =
    let f = fixture ()
    let state, move =
        Navigator.jumpToLatestResponse None Navigator.initial f.Tree
    match move with
    | Edge _ -> Assert.Equal(None, state.Current)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``next and previous walk siblings and stop at edges without moving`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.P1.Id Navigator.initial f.Tree
    let s1, m1 = Navigator.next s0 f.Tree
    movedTo f.List.Id m1
    let s2, m2 = Navigator.next s1 f.Tree
    movedTo f.P2.Id m2
    // Edge: no next — focus must not move (§6.4).
    let s3, m3 = Navigator.next s2 f.Tree
    match m3 with
    | Edge _ -> Assert.Equal(Some f.P2.Id, s3.Current)
    | other -> failwithf "expected Edge, got %A" other
    let s4, m4 = Navigator.previous s3 f.Tree
    movedTo f.List.Id m4
    Assert.Equal(Some f.List.Id, s4.Current)

[<Fact>]
let ``descend enters children and ascend returns to the parent`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.List.Id Navigator.initial f.Tree
    let s1, m1 = Navigator.descend s0 f.Tree
    movedTo f.Item1.Id m1
    let s2, m2 = Navigator.ascend s1 f.Tree
    movedTo f.List.Id m2
    // A leaf has nothing inside: Edge, focus unchanged.
    let s3, _ = Navigator.focus f.Item2.Id s2 f.Tree
    let s4, m4 = Navigator.descend s3 f.Tree
    match m4 with
    | Edge _ -> Assert.Equal(Some f.Item2.Id, s4.Current)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``ascend from the top level is an Edge`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.Req.Id Navigator.initial f.Tree
    let s1, move = Navigator.ascend s0 f.Tree
    match move with
    | Edge _ -> Assert.Equal(Some f.Req.Id, s1.Current)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``current re-narrates the focused chunk`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.Item1.Id Navigator.initial f.Tree
    match Navigator.current s0 f.Tree with
    | Some c -> Assert.Equal("alpha", c.Text)
    | None -> failwith "expected a focused chunk"

[<Fact>]
let ``anchor push and return restore the exact branch origin`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.P1.Id Navigator.initial f.Tree
    let s1 = Navigator.pushAnchor s0
    // Wander away (a side branch would move focus further).
    let s2, _ = Navigator.focus f.Item2.Id s1 f.Tree
    let s3, move = Navigator.returnToAnchor s2 f.Tree
    movedTo f.P1.Id move
    Assert.Empty(s3.AnchorStack)

[<Fact>]
let ``return with no anchor is an Edge`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.P1.Id Navigator.initial f.Tree
    let s1, move = Navigator.returnToAnchor s0 f.Tree
    match move with
    | Edge _ -> Assert.Equal(Some f.P1.Id, s1.Current)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``first and last sibling jump within the level`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.List.Id Navigator.initial f.Tree
    let s1, m1 = Navigator.lastSibling s0 f.Tree
    movedTo f.P2.Id m1
    let s2, m2 = Navigator.firstSibling s1 f.Tree
    movedTo f.P1.Id m2
    // Already first: re-focuses self (a re-announce), not an edge.
    let _, m3 = Navigator.firstSibling s2 f.Tree
    movedTo f.P1.Id m3

[<Fact>]
let ``nth sibling addresses the level directly and edges honestly`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.P1.Id Navigator.initial f.Tree
    let s1, m1 = Navigator.nthSibling 3 s0 f.Tree
    movedTo f.P2.Id m1
    let _, m2 = Navigator.nthSibling 9 s1 f.Tree
    match m2 with
    | Edge text -> Assert.Contains("9", text)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``findNext searches forward case-insensitively and wraps`` () =
    let f = fixture ()
    // Focus the last paragraph; "ALPHA" lives earlier (item1) —
    // the search must wrap and still find it.
    let s0, _ = Navigator.focus f.P2.Id Navigator.initial f.Tree
    let s1, m1 = Navigator.findNext "ALPHA" s0 f.Tree
    movedTo f.Item1.Id m1
    // No match: an Edge naming the query, focus unchanged.
    let s2, m2 = Navigator.findNext "zebra" s1 f.Tree
    match m2 with
    | Edge text ->
        Assert.Contains("zebra", text)
        Assert.Equal(Some f.Item1.Id, s2.Current)
    | other -> failwithf "expected Edge, got %A" other

[<Fact>]
let ``findNext with no focus starts from the beginning`` () =
    let f = fixture ()
    let _, move = Navigator.findNext "first" Navigator.initial f.Tree
    movedTo f.P1.Id move

[<Fact>]
let ``anchors nest as a stack`` () =
    let f = fixture ()
    let s0, _ = Navigator.focus f.P1.Id Navigator.initial f.Tree
    let s1 = Navigator.pushAnchor s0
    let s2, _ = Navigator.focus f.List.Id s1 f.Tree
    let s3 = Navigator.pushAnchor s2
    let s4, _ = Navigator.focus f.Item2.Id s3 f.Tree
    let s5, m1 = Navigator.returnToAnchor s4 f.Tree
    movedTo f.List.Id m1
    let _s6, m2 = Navigator.returnToAnchor s5 f.Tree
    movedTo f.P1.Id m2
