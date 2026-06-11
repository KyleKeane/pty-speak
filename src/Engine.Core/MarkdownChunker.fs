namespace Engine.Core

open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines

/// ADR 0011 E2 — markdown → chunk decomposition (the chunk
/// grain). An assistant text block is decomposed at the block
/// boundaries the model itself marked (heading, paragraph,
/// list, list item, fenced code, quote, thematic break): the
/// structure is already present in the source and is kept,
/// never flattened (ADR 0008 at conversation granularity,
/// RELAUNCH-SPEC §5.1).
///
/// Pure: text in, `ChunkSpec` forest out. The tree (ids,
/// capture order) is assigned by the ingest layer appending
/// the specs; this module never touches `ChunkTree`.
///
/// `Engine.Core.Chunk` is deliberately NOT opened — Markdig's
/// `ListBlock` / `CodeBlock` syntax types share names with the
/// `ChunkKind` cases, so the kinds are `Chunk.`-qualified
/// throughout.
module MarkdownChunker =

    /// A chunk-to-be: kind + narration text + nested children
    /// (list items under their list; nested lists under their
    /// item). Ids / ordering are assigned at append.
    type ChunkSpec =
        { Kind: Chunk.ChunkKind
          Text: string
          Children: ChunkSpec list }

    /// Flatten an inline (sub)tree to plain narration text.
    /// Literals, code spans, and line breaks carry text;
    /// containers (emphasis, links) recurse; anything else
    /// contributes nothing.
    let rec private inlineToText (il: Inline) : string =
        match il with
        | :? LiteralInline as lit -> lit.Content.ToString()
        | :? CodeInline as code -> code.Content
        | :? LineBreakInline -> "\n"
        | :? ContainerInline as container ->
            container
            |> Seq.cast<Inline>
            |> Seq.map inlineToText
            |> String.concat ""
        | _ -> ""

    /// A leaf block's processed inline text ("" when the parser
    /// attached no inline tree, e.g. HTML blocks).
    let private inlinesOf (leaf: LeafBlock) : string =
        match leaf.Inline with
        | null -> ""
        | inl -> inlineToText (inl :> Inline)

    /// One Markdig block → zero or more chunk specs. Fenced
    /// code is matched before the indented-code base class.
    let rec private blockToSpecs (block: Block) : ChunkSpec list =
        match block with
        | :? HeadingBlock as h ->
            [ { Kind = Chunk.Heading h.Level
                Text = inlinesOf h
                Children = [] } ]
        | :? FencedCodeBlock as f ->
            let language =
                match f.Info with
                | null -> None
                | "" -> None
                | s -> Some s
            [ { Kind = Chunk.CodeBlock language
                Text = f.Lines.ToString()
                Children = [] } ]
        | :? Markdig.Syntax.CodeBlock as c ->
            [ { Kind = Chunk.CodeBlock None
                Text = c.Lines.ToString()
                Children = [] } ]
        | :? Markdig.Syntax.ListBlock as list ->
            let items =
                list
                |> Seq.cast<Block>
                |> Seq.choose (fun child ->
                    match child with
                    | :? ListItemBlock as item ->
                        Some (listItemSpec item)
                    | _ -> None)
                |> List.ofSeq
            [ { Kind = Chunk.ListBlock list.IsOrdered
                Text = ""
                Children = items } ]
        | :? QuoteBlock as q ->
            let inner =
                q
                |> Seq.cast<Block>
                |> Seq.collect blockToSpecs
                |> List.ofSeq
            let text =
                inner
                |> List.map (fun s -> s.Text)
                |> List.filter (fun t ->
                    not (System.String.IsNullOrWhiteSpace t))
                |> String.concat "\n"
            [ { Kind = Chunk.BlockQuote
                Text = text
                Children = [] } ]
        | :? ThematicBreakBlock ->
            [ { Kind = Chunk.ThematicBreak
                Text = ""
                Children = [] } ]
        | :? ParagraphBlock as p ->
            [ { Kind = Chunk.Paragraph
                Text = inlinesOf p
                Children = [] } ]
        | :? LeafBlock as leaf ->
            // HTML blocks and similar — keep whatever text the
            // inline tree provides; empties are filtered at the
            // top level.
            [ { Kind = Chunk.Paragraph
                Text = inlinesOf leaf
                Children = [] } ]
        | :? ContainerBlock as container ->
            container
            |> Seq.cast<Block>
            |> Seq.collect blockToSpecs
            |> List.ofSeq
        | _ -> []

    /// One list item: its paragraph text becomes the item's
    /// narration text; nested blocks (sub-lists, code) become
    /// children.
    and private listItemSpec (item: ListItemBlock) : ChunkSpec =
        let childBlocks = item |> Seq.cast<Block> |> List.ofSeq
        let textParts =
            childBlocks
            |> List.choose (fun b ->
                match b with
                | :? ParagraphBlock as p -> Some (inlinesOf p)
                | _ -> None)
        let nested =
            childBlocks
            |> List.collect (fun b ->
                match b with
                | :? ParagraphBlock -> []
                | other -> blockToSpecs other)
        { Kind = Chunk.ListItem
          Text = textParts |> String.concat "\n"
          Children = nested }

    /// Decompose markdown into a chunk-spec forest. Whitespace-
    /// only leaves are dropped; structural kinds (lists, breaks)
    /// survive with empty text. A whitespace-only input yields
    /// the empty forest.
    let decompose (markdown: string) : ChunkSpec list =
        if System.String.IsNullOrWhiteSpace markdown then
            []
        else
            let doc = Markdown.Parse(markdown)
            doc
            |> Seq.cast<Block>
            |> Seq.collect blockToSpecs
            |> Seq.filter (fun spec ->
                match spec.Kind with
                | Chunk.ListBlock _ -> not spec.Children.IsEmpty
                | Chunk.ThematicBreak -> true
                | _ ->
                    not (System.String.IsNullOrWhiteSpace spec.Text))
            |> List.ofSeq
