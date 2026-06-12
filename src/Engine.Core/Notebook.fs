namespace Engine.Core

open System

/// ADR 0013 N1/N3 — the authored layer (RELAUNCH-SPEC §5.3):
/// the notebook the user *thinks in*, built over the immutable
/// capture tree. Cells are **references** (pinned chunks) plus
/// the user's own narrative and section structure — never
/// copies, so the transcript stays the single source of truth
/// and the two layers cannot collapse (§14.5) by construction.
///
/// Every operation is pure and **total**: out-of-range indices
/// are no-ops (editing by ear must never throw), and move
/// operations report whether they moved so the host can cue
/// edge-vs-moved exactly like tree navigation.
module Notebook =

    type CellContent =
        /// A reference into the capture tree; renders through
        /// the live tree at narration/export time.
        | PinnedChunk of Chunk.ChunkId
        /// The user's authored words — the connective tissue of
        /// the computational narrative.
        | Narrative of text: string
        /// Higher-order structure (exports as a markdown
        /// heading).
        | SectionHeader of title: string

    type Cell =
        { Id: string
          Content: CellContent }

    type Notebook =
        { Cells: Cell list }

    let empty : Notebook =
        { Cells = [] }

    let count (notebook: Notebook) : int =
        List.length notebook.Cells

    let private newCell (content: CellContent) : Cell =
        { Id = Guid.NewGuid().ToString("N")
          Content = content }

    /// Append a pinned reference to a capture chunk.
    let pin (chunkId: Chunk.ChunkId) (notebook: Notebook) : Notebook =
        { Cells = notebook.Cells @ [ newCell (PinnedChunk chunkId) ] }

    /// Append an authored narrative cell.
    let addNarrative (text: string) (notebook: Notebook) : Notebook =
        { Cells = notebook.Cells @ [ newCell (Narrative text) ] }

    /// Append a section header.
    let addSection (title: string) (notebook: Notebook) : Notebook =
        { Cells = notebook.Cells @ [ newCell (SectionHeader title) ] }

    let tryItem (index: int) (notebook: Notebook) : Cell option =
        List.tryItem index notebook.Cells

    /// Remove the cell at `index`; out of range = unchanged.
    /// Reports whether a cell was removed.
    let removeAt (index: int) (notebook: Notebook) : Notebook * bool =
        if index < 0 || index >= count notebook then
            notebook, false
        else
            { Cells =
                notebook.Cells
                |> List.mapi (fun i cell -> i, cell)
                |> List.filter (fun (i, _) -> i <> index)
                |> List.map snd },
            true

    let private swap (i: int) (j: int) (cells: Cell list) : Cell list =
        let arr = List.toArray cells
        let tmp = arr.[i]
        arr.[i] <- arr.[j]
        arr.[j] <- tmp
        List.ofArray arr

    /// Move the cell at `index` one position earlier; at the
    /// top or out of range = unchanged (reported).
    let moveUp (index: int) (notebook: Notebook) : Notebook * bool =
        if index <= 0 || index >= count notebook then
            notebook, false
        else
            { Cells = swap (index - 1) index notebook.Cells }, true

    /// Move the cell at `index` one position later; at the end
    /// or out of range = unchanged (reported).
    let moveDown (index: int) (notebook: Notebook) : Notebook * bool =
        if index < 0 || index >= count notebook - 1 then
            notebook, false
        else
            { Cells = swap index (index + 1) notebook.Cells }, true

    /// Narrate one cell. Pinned chunks render through the live
    /// tree (full canonical narration); a dangling reference —
    /// structurally impossible today (the tree is append-only)
    /// but possible across file edits — degrades honestly.
    let describeCell
            (tree: ChunkTree.Tree)
            (cell: Cell)
            : string =
        match cell.Content with
        | PinnedChunk chunkId ->
            match ChunkTree.tryFind chunkId tree with
            | Some chunk ->
                sprintf "Pinned: %s" (ChunkNarration.describe tree chunk)
            | None -> "Pinned chunk is no longer in this session."
        | Narrative text -> text
        | SectionHeader title -> sprintf "Section: %s" title

    /// ADR 0013 N3 — render the authored sequence as plain
    /// markdown: publishable, diffable, and — because markdown
    /// is the engine's own ingest grain — re-ingestable, so a
    /// narrative composed today can seed tomorrow's
    /// conversation.
    let toMarkdown
            (tree: ChunkTree.Tree)
            (notebook: Notebook)
            : string =
        let renderChunk (chunk: Chunk.Chunk) : string =
            match chunk.Kind with
            | Chunk.Heading _ -> sprintf "### %s" chunk.Text
            | Chunk.Paragraph -> chunk.Text
            | Chunk.CodeBlock language ->
                let fence =
                    match language with
                    | Some lang -> "```" + lang
                    | None -> "```"
                sprintf "%s\n%s\n```" fence (chunk.Text.TrimEnd('\n'))
            | Chunk.ListBlock _ ->
                ChunkTree.children (Some chunk.Id) tree
                |> List.map (fun item -> sprintf "- %s" item.Text)
                |> String.concat "\n"
            | Chunk.ListItem -> sprintf "- %s" chunk.Text
            | Chunk.BlockQuote -> sprintf "> %s" chunk.Text
            | Chunk.ThematicBreak -> "---"
            | Chunk.UserRequest -> sprintf "**Request:** %s" chunk.Text
            | Chunk.ToolUse name ->
                sprintf
                    "**Tool call (%s):**\n```json\n%s\n```"
                    name (chunk.Text.TrimEnd('\n'))
            | Chunk.ToolResult isError ->
                let label = if isError then "Tool error" else "Tool result"
                sprintf
                    "**%s:**\n```\n%s\n```"
                    label (chunk.Text.TrimEnd('\n'))
            | Chunk.AgentError -> sprintf "**Agent error:** %s" chunk.Text
            | Chunk.SystemNote -> chunk.Text
        let renderCell (cell: Cell) : string =
            match cell.Content with
            | SectionHeader title -> sprintf "## %s" title
            | Narrative text -> text
            | PinnedChunk chunkId ->
                match ChunkTree.tryFind chunkId tree with
                | Some chunk -> renderChunk chunk
                | None -> "<!-- pinned chunk missing -->"
        notebook.Cells
        |> List.map renderCell
        |> String.concat "\n\n"
