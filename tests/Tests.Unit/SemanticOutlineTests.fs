module PtySpeak.Tests.Unit.SemanticOutlineTests

open Xunit
open Engine.Core
open Engine.Core.MarkdownChunker

// ---------------------------------------------------------------------
// ADR 0012 S1 — heading-scoped outline tests.
// ---------------------------------------------------------------------
//
// Pins the nesting law: a heading absorbs following specs
// (including deeper headings) until a heading of equal or
// shallower level; pre-heading content stays top-level; list
// children survive untouched.

let private spec kind text : ChunkSpec =
    { Kind = kind; Text = text; Children = [] }

let private kindsOf (specs: ChunkSpec list) =
    specs |> List.map (fun s -> s.Kind)

[<Fact>]
let ``no headings means no change`` () =
    let flat =
        [ spec Chunk.Paragraph "a"
          spec (Chunk.ListBlock false) "l"
          spec Chunk.Paragraph "b" ]
    Assert.Equal<ChunkSpec list>(flat, SemanticOutline.nest flat)

[<Fact>]
let ``a heading absorbs following blocks until the next equal heading`` () =
    let flat =
        [ spec (Chunk.Heading 1) "One"
          spec Chunk.Paragraph "p1"
          spec Chunk.Paragraph "p2"
          spec (Chunk.Heading 1) "Two"
          spec Chunk.Paragraph "p3" ]
    match SemanticOutline.nest flat with
    | [ one; two ] ->
        Assert.Equal(Chunk.Heading 1, one.Kind)
        Assert.Equal<Chunk.ChunkKind list>(
            [ Chunk.Paragraph; Chunk.Paragraph ],
            kindsOf one.Children)
        Assert.Equal("Two", two.Text)
        Assert.Equal<Chunk.ChunkKind list>(
            [ Chunk.Paragraph ],
            kindsOf two.Children)
    | other -> failwithf "expected two sections, got %A" other

[<Fact>]
let ``deeper headings nest inside their parent section`` () =
    let flat =
        [ spec (Chunk.Heading 1) "Top"
          spec Chunk.Paragraph "intro"
          spec (Chunk.Heading 2) "Sub A"
          spec Chunk.Paragraph "a-body"
          spec (Chunk.Heading 2) "Sub B"
          spec Chunk.Paragraph "b-body" ]
    match SemanticOutline.nest flat with
    | [ top ] ->
        Assert.Equal("Top", top.Text)
        match top.Children with
        | [ intro; subA; subB ] ->
            Assert.Equal(Chunk.Paragraph, intro.Kind)
            Assert.Equal("Sub A", subA.Text)
            Assert.Equal<string list>(
                [ "a-body" ],
                subA.Children |> List.map (fun s -> s.Text))
            Assert.Equal("Sub B", subB.Text)
        | other -> failwithf "expected intro + two subsections, got %A" other
    | other -> failwithf "expected one top section, got %A" other

[<Fact>]
let ``a shallower heading closes the deeper scope`` () =
    let flat =
        [ spec (Chunk.Heading 2) "Deep first"
          spec Chunk.Paragraph "deep-body"
          spec (Chunk.Heading 1) "Then shallow"
          spec Chunk.Paragraph "shallow-body" ]
    match SemanticOutline.nest flat with
    | [ deep; shallow ] ->
        Assert.Equal("Deep first", deep.Text)
        Assert.Equal(1, List.length deep.Children)
        Assert.Equal("Then shallow", shallow.Text)
        Assert.Equal(1, List.length shallow.Children)
    | other -> failwithf "expected two top sections, got %A" other

[<Fact>]
let ``content before the first heading stays top-level`` () =
    let flat =
        [ spec Chunk.Paragraph "preamble"
          spec (Chunk.Heading 1) "Body"
          spec Chunk.Paragraph "inside" ]
    match SemanticOutline.nest flat with
    | [ pre; body ] ->
        Assert.Equal("preamble", pre.Text)
        Assert.Empty(pre.Children)
        Assert.Equal<string list>(
            [ "inside" ],
            body.Children |> List.map (fun s -> s.Text))
    | other -> failwithf "expected preamble + section, got %A" other

[<Fact>]
let ``list children survive nesting untouched`` () =
    let listSpec =
        { Kind = Chunk.ListBlock true
          Text = ""
          Children =
            [ spec Chunk.ListItem "one"; spec Chunk.ListItem "two" ] }
    let flat = [ spec (Chunk.Heading 1) "H"; listSpec ]
    match SemanticOutline.nest flat with
    | [ h ] ->
        match h.Children with
        | [ l ] ->
            Assert.Equal<string list>(
                [ "one"; "two" ],
                l.Children |> List.map (fun s -> s.Text))
        | other -> failwithf "expected the list, got %A" other
    | other -> failwithf "expected one section, got %A" other

[<Fact>]
let ``skipped levels still nest by relative depth`` () =
    // H1 then H3 (no H2): the H3 is deeper, so it nests.
    let flat =
        [ spec (Chunk.Heading 1) "Top"
          spec (Chunk.Heading 3) "Jumped"
          spec Chunk.Paragraph "body"
          spec (Chunk.Heading 1) "Next" ]
    match SemanticOutline.nest flat with
    | [ top; next ] ->
        Assert.Equal("Next", next.Text)
        match top.Children with
        | [ jumped ] ->
            Assert.Equal("Jumped", jumped.Text)
            Assert.Equal(1, List.length jumped.Children)
        | other -> failwithf "expected nested H3, got %A" other
    | other -> failwithf "expected two top sections, got %A" other

[<Fact>]
let ``end-to-end: a realistic response becomes a navigable outline`` () =
    let md =
        "Intro before any section.\n\n"
        + "# Plan\n\nFirst step below.\n\n"
        + "## Details\n\n- alpha\n- beta\n\n"
        + "# Result\n\nAll good."
    let outline = MarkdownChunker.decompose md |> SemanticOutline.nest
    match outline with
    | [ intro; plan; result ] ->
        Assert.Equal(Chunk.Paragraph, intro.Kind)
        Assert.Equal(Chunk.Heading 1, plan.Kind)
        // Plan: paragraph + the Details subsection.
        match plan.Children with
        | [ p; details ] ->
            Assert.Equal(Chunk.Paragraph, p.Kind)
            Assert.Equal(Chunk.Heading 2, details.Kind)
            match details.Children with
            | [ list ] -> Assert.Equal(Chunk.ListBlock false, list.Kind)
            | other -> failwithf "expected one list, got %A" other
        | other -> failwithf "expected paragraph + subsection, got %A" other
        Assert.Equal("Result", result.Text)
    | other -> failwithf "expected intro + 2 sections, got %A" other
