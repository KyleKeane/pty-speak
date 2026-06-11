module PtySpeak.Tests.Unit.MarkdownChunkerTests

open Xunit
open Engine.Core
open Engine.Core.MarkdownChunker

// ---------------------------------------------------------------------
// ADR 0011 E2 — markdown → chunk-spec decomposition tests.
// ---------------------------------------------------------------------
//
// Pins the chunk grain: block-level boundaries the model itself
// marked. The chunker is pure (text → spec forest); ids and
// ordering are the tree's concern, not tested here.

[<Fact>]
let ``whitespace-only input decomposes to nothing`` () =
    Assert.Empty(MarkdownChunker.decompose "")
    Assert.Empty(MarkdownChunker.decompose "   \n  ")

[<Fact>]
let ``plain prose becomes one paragraph spec`` () =
    match MarkdownChunker.decompose "Just a sentence." with
    | [ spec ] ->
        Assert.Equal(Chunk.Paragraph, spec.Kind)
        Assert.Equal("Just a sentence.", spec.Text)
        Assert.Empty(spec.Children)
    | other -> failwithf "expected one paragraph, got %A" other

[<Fact>]
let ``heading and paragraphs split at block boundaries`` () =
    let md = "# Title\n\nFirst para.\n\nSecond para."
    match MarkdownChunker.decompose md with
    | [ h; p1; p2 ] ->
        Assert.Equal(Chunk.Heading 1, h.Kind)
        Assert.Equal("Title", h.Text)
        Assert.Equal(Chunk.Paragraph, p1.Kind)
        Assert.Equal("First para.", p1.Text)
        Assert.Equal("Second para.", p2.Text)
    | other -> failwithf "expected three specs, got %A" other

[<Fact>]
let ``emphasis and inline code flatten into narration text`` () =
    match MarkdownChunker.decompose "Use **the** `dotnet` tool." with
    | [ p ] -> Assert.Equal("Use the dotnet tool.", p.Text)
    | other -> failwithf "expected one paragraph, got %A" other

[<Fact>]
let ``fenced code block keeps language and verbatim body`` () =
    let md = "```fsharp\nlet x = 1\nlet y = 2\n```"
    match MarkdownChunker.decompose md with
    | [ c ] ->
        Assert.Equal(Chunk.CodeBlock (Some "fsharp"), c.Kind)
        Assert.Equal("let x = 1\nlet y = 2", c.Text.TrimEnd())
    | other -> failwithf "expected one code block, got %A" other

[<Fact>]
let ``fence without info string has no language`` () =
    match MarkdownChunker.decompose "```\nplain\n```" with
    | [ c ] -> Assert.Equal(Chunk.CodeBlock None, c.Kind)
    | other -> failwithf "expected one code block, got %A" other

[<Fact>]
let ``bulleted list becomes a list block with item children`` () =
    let md = "- alpha\n- beta\n- gamma"
    match MarkdownChunker.decompose md with
    | [ list ] ->
        Assert.Equal(Chunk.ListBlock false, list.Kind)
        Assert.Equal<string list>(
            [ "alpha"; "beta"; "gamma" ],
            list.Children |> List.map (fun i -> i.Text))
        for item in list.Children do
            Assert.Equal(Chunk.ListItem, item.Kind)
    | other -> failwithf "expected one list, got %A" other

[<Fact>]
let ``ordered list is marked ordered`` () =
    match MarkdownChunker.decompose "1. one\n2. two" with
    | [ list ] -> Assert.Equal(Chunk.ListBlock true, list.Kind)
    | other -> failwithf "expected one list, got %A" other

[<Fact>]
let ``nested list nests under its parent item`` () =
    let md = "- outer\n  - inner one\n  - inner two"
    match MarkdownChunker.decompose md with
    | [ list ] ->
        match list.Children with
        | [ outer ] ->
            Assert.Equal("outer", outer.Text)
            match outer.Children with
            | [ nested ] ->
                Assert.Equal(Chunk.ListBlock false, nested.Kind)
                Assert.Equal<string list>(
                    [ "inner one"; "inner two" ],
                    nested.Children |> List.map (fun i -> i.Text))
            | other -> failwithf "expected one nested list, got %A" other
        | other -> failwithf "expected one outer item, got %A" other
    | other -> failwithf "expected one list, got %A" other

[<Fact>]
let ``block quote flattens to one quote spec`` () =
    match MarkdownChunker.decompose "> quoted wisdom" with
    | [ q ] ->
        Assert.Equal(Chunk.BlockQuote, q.Kind)
        Assert.Equal("quoted wisdom", q.Text)
    | other -> failwithf "expected one quote, got %A" other

[<Fact>]
let ``thematic break survives with empty text`` () =
    let md = "before\n\n---\n\nafter"
    match MarkdownChunker.decompose md with
    | [ p1; hr; p2 ] ->
        Assert.Equal(Chunk.Paragraph, p1.Kind)
        Assert.Equal(Chunk.ThematicBreak, hr.Kind)
        Assert.Equal("after", p2.Text)
    | other -> failwithf "expected para/break/para, got %A" other

[<Fact>]
let ``a realistic agent response decomposes block by block`` () =
    let md =
        "# Plan\n\nTwo steps are needed.\n\n"
        + "1. Read the file\n2. Fix the bug\n\n"
        + "```fsharp\nlet fix () = ()\n```\n\n"
        + "Done."
    let specs = MarkdownChunker.decompose md
    Assert.Equal<Chunk.ChunkKind list>(
        [ Chunk.Heading 1
          Chunk.Paragraph
          Chunk.ListBlock true
          Chunk.CodeBlock (Some "fsharp")
          Chunk.Paragraph ],
        specs |> List.map (fun s -> s.Kind))
