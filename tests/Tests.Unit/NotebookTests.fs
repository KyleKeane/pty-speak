module PtySpeak.Tests.Unit.NotebookTests

open Xunit
open Engine.Core
open Engine.Core.Notebook

// ---------------------------------------------------------------------
// ADR 0013 N1/N3 — notebook contract: reference-not-copy
// pinning, total editing operations (no input throws), honest
// move/remove reporting, narration through the live tree, and
// the markdown export shape.
// ---------------------------------------------------------------------

let private ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "fixture: %s" e

let private contentTexts (tree: ChunkTree.Tree) (nb: Notebook) =
    nb.Cells |> List.map (describeCell tree)

[<Fact>]
let ``pin narrative and section append in order`` () =
    let chunk, tree =
        ok (ChunkTree.append None Chunk.Paragraph "finding" ChunkTree.empty)
    let nb =
        empty
        |> addSection "Results"
        |> pin chunk.Id
        |> addNarrative "This matters because..."
    Assert.Equal(3, count nb)
    Assert.Equal<string list>(
        [ "Section: Results"
          "Pinned: finding"
          "This matters because..." ],
        contentTexts tree nb)

[<Fact>]
let ``pinning is a reference not a copy`` () =
    // The narration renders THROUGH the tree: a pinned heading
    // announces its live child count, proving no snapshot was
    // taken at pin time.
    let h, tree =
        ok (ChunkTree.append None (Chunk.Heading 1) "Plan" ChunkTree.empty)
    let nb = empty |> pin h.Id
    let _, tree =
        ok (ChunkTree.append (Some h.Id) Chunk.Paragraph "later child" tree)
    Assert.Equal<string list>(
        [ "Pinned: Heading level 1: Plan. 1 items inside." ],
        contentTexts tree nb)

[<Fact>]
let ``removeAt is total and reports honestly`` () =
    let nb = empty |> addNarrative "a" |> addNarrative "b"
    let nb1, removed1 = removeAt 0 nb
    Assert.True(removed1)
    Assert.Equal(1, count nb1)
    let nb2, removed2 = removeAt 5 nb1
    Assert.False(removed2)
    Assert.Equal(1, count nb2)
    let nb3, removed3 = removeAt -1 nb2
    Assert.False(removed3)
    Assert.Equal(1, count nb3)

[<Fact>]
let ``moveUp and moveDown swap neighbours and stop at edges`` () =
    let tree = ChunkTree.empty
    let nb =
        empty |> addNarrative "1" |> addNarrative "2" |> addNarrative "3"
    let nb1, moved1 = moveUp 1 nb
    Assert.True(moved1)
    Assert.Equal<string list>([ "2"; "1"; "3" ], contentTexts tree nb1)
    let nb2, moved2 = moveUp 0 nb1
    Assert.False(moved2)
    Assert.Equal<string list>([ "2"; "1"; "3" ], contentTexts tree nb2)
    let nb3, moved3 = moveDown 2 nb2
    Assert.False(moved3)
    let nb4, moved4 = moveDown 0 nb3
    Assert.True(moved4)
    Assert.Equal<string list>([ "1"; "2"; "3" ], contentTexts tree nb4)

[<Fact>]
let ``markdown export renders sections narrative and pinned kinds`` () =
    let req, tree =
        ok (ChunkTree.append None Chunk.UserRequest "find primes" ChunkTree.empty)
    let code, tree =
        ok (ChunkTree.append
                (Some req.Id)
                (Chunk.CodeBlock (Some "python"))
                "print(2)"
                tree)
    let nb =
        empty
        |> addSection "Prime hunt"
        |> addNarrative "We started with the obvious."
        |> pin req.Id
        |> pin code.Id
    let md = toMarkdown tree nb
    Assert.Contains("## Prime hunt", md)
    Assert.Contains("We started with the obvious.", md)
    Assert.Contains("**Request:** find primes", md)
    Assert.Contains("```python\nprint(2)\n```", md)

[<Fact>]
let ``markdown export re-renders a pinned list from the live tree`` () =
    let list, tree =
        ok (ChunkTree.append None (Chunk.ListBlock false) "" ChunkTree.empty)
    let _, tree = ok (ChunkTree.append (Some list.Id) Chunk.ListItem "alpha" tree)
    let _, tree = ok (ChunkTree.append (Some list.Id) Chunk.ListItem "beta" tree)
    let md = toMarkdown tree (empty |> pin list.Id)
    Assert.Equal("- alpha\n- beta", md)

[<Fact>]
let ``markdown export is re-ingestable by the engine's own chunker`` () =
    // ADR 0013 N3's closing of the loop: export → decompose →
    // the structure survives.
    let h, tree =
        ok (ChunkTree.append None (Chunk.Heading 2) "Findings" ChunkTree.empty)
    let nb =
        empty
        |> addSection "Narrative"
        |> addNarrative "Plain prose survives."
        |> pin h.Id
    let md = toMarkdown tree nb
    let specs = MarkdownChunker.decompose md
    Assert.Equal<Chunk.ChunkKind list>(
        [ Chunk.Heading 2; Chunk.Paragraph; Chunk.Heading 3 ],
        specs |> List.map (fun s -> s.Kind))
