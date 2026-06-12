namespace Engine.Core

/// RELAUNCH-SPEC §4.6 / §14.11 — canonical narration rendering:
/// the chunk's typed structure rendered to speakable text by
/// the system itself (self-voicing — meaning is never re-derived
/// by an external layer). Pure; the voice channel speaks the
/// returned string verbatim.
module ChunkNarration =

    /// Render one chunk for narration. Structure is announced
    /// before content (kind, then text) so a listener can skip
    /// early; container kinds announce their child count from
    /// the tree.
    let describe
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : string =
        let childCount =
            ChunkTree.children (Some chunk.Id) tree |> List.length
        match chunk.Kind with
        | Chunk.Heading level ->
            // ADR 0012 S1 — a heading is a section now; say
            // how much it holds so descend is an informed move.
            if childCount > 0 then
                sprintf
                    "Heading level %d: %s. %d items inside."
                    level chunk.Text childCount
            else
                sprintf "Heading level %d: %s" level chunk.Text
        | Chunk.Paragraph ->
            chunk.Text
        | Chunk.ListBlock ordered ->
            let label = if ordered then "Numbered" else "Bulleted"
            sprintf "%s list, %d items." label childCount
        | Chunk.ListItem ->
            if childCount > 0 then
                sprintf "%s. Has nested content." chunk.Text
            else
                chunk.Text
        | Chunk.CodeBlock language ->
            let lineCount =
                if chunk.Text = "" then 0
                else (chunk.Text.Split('\n') |> Array.length)
            match language with
            | Some lang ->
                sprintf
                    "Code block, %s, %d lines. %s"
                    lang lineCount chunk.Text
            | None ->
                sprintf "Code block, %d lines. %s" lineCount chunk.Text
        | Chunk.BlockQuote ->
            sprintf "Quote. %s" chunk.Text
        | Chunk.ThematicBreak ->
            "Separator."
        | Chunk.UserRequest ->
            sprintf "Your request: %s" chunk.Text
        | Chunk.ToolUse name ->
            // §6.2 "semantic info about run code": what was run,
            // as structure. The raw input JSON is the chunk
            // text; spoken on demand, after the tool name.
            sprintf "Tool call, %s. Input: %s" name chunk.Text
        | Chunk.ToolResult isError ->
            if isError then
                sprintf "Tool result, error. %s" chunk.Text
            else
                sprintf "Tool result. %s" chunk.Text
        | Chunk.AgentError ->
            sprintf "Agent error. %s" chunk.Text
        | Chunk.SystemNote ->
            chunk.Text

    /// ADR 0012 S2 — "N of M" among the chunk's siblings
    /// (1-based; the screen-reader positional convention).
    let positionOf
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : int * int =
        let siblings = ChunkTree.children chunk.Parent tree
        let index =
            siblings
            |> List.tryFindIndex (fun c -> c.Id = chunk.Id)
            |> Option.defaultValue 0
        (index + 1, List.length siblings)

    /// A short label for a chunk used in breadcrumb trails;
    /// long texts are clipped — the trail is orientation, not
    /// content.
    let private trailLabel (chunk: Chunk.Chunk) : string =
        let clip (text: string) =
            if text.Length > 40 then text.Substring(0, 40) + "…"
            else text
        match chunk.Kind with
        | Chunk.Heading _ -> sprintf "section %s" (clip chunk.Text)
        | Chunk.UserRequest ->
            sprintf "your request: %s" (clip chunk.Text)
        | Chunk.ListBlock true -> "a numbered list"
        | Chunk.ListBlock false -> "a bulleted list"
        | Chunk.ListItem -> sprintf "list item %s" (clip chunk.Text)
        | Chunk.ToolUse name -> sprintf "tool call %s" name
        | Chunk.BlockQuote -> "a quote"
        | Chunk.Paragraph -> "a paragraph"
        | Chunk.CodeBlock _ -> "a code block"
        | Chunk.ThematicBreak -> "a separator"
        | Chunk.ToolResult _ -> "a tool result"
        | Chunk.AgentError -> "an agent error"
        | Chunk.SystemNote -> "a note"

    /// The focused chunk's own short label (kind-first, no
    /// body) for the where-verb.
    let private selfLabel (chunk: Chunk.Chunk) : string =
        let clip (text: string) =
            if text.Length > 40 then text.Substring(0, 40) + "…"
            else text
        match chunk.Kind with
        | Chunk.Heading level ->
            sprintf "Heading level %d: %s" level (clip chunk.Text)
        | Chunk.Paragraph -> "Paragraph"
        | Chunk.ListBlock true -> "Numbered list"
        | Chunk.ListBlock false -> "Bulleted list"
        | Chunk.ListItem -> "List item"
        | Chunk.CodeBlock (Some lang) -> sprintf "Code block, %s" lang
        | Chunk.CodeBlock None -> "Code block"
        | Chunk.BlockQuote -> "Quote"
        | Chunk.ThematicBreak -> "Separator"
        | Chunk.UserRequest -> "Your request"
        | Chunk.ToolUse name -> sprintf "Tool call %s" name
        | Chunk.ToolResult true -> "Tool error result"
        | Chunk.ToolResult false -> "Tool result"
        | Chunk.AgentError -> "Agent error"
        | Chunk.SystemNote -> "Note"

    /// ADR 0012 S2 — the where-verb: the focused chunk's kind
    /// and position, then the ancestor trail innermost-first,
    /// then the depth. The §10 orientation surface at chunk
    /// scale — computed from the tree, so it can never drift.
    let locate
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : string =
        let position, total = positionOf tree chunk
        let rec trail (current: Chunk.Chunk) (acc: string list) =
            match current.Parent with
            | None -> acc
            | Some parentId ->
                match ChunkTree.tryFind parentId tree with
                | None -> acc
                | Some parent ->
                    trail parent (acc @ [ trailLabel parent ])
        let ancestors = trail chunk []
        let inside =
            if List.isEmpty ancestors then ""
            else ", inside " + String.concat ", inside " ancestors
        sprintf
            "%s, %d of %d%s. Depth %d."
            (selfLabel chunk)
            position
            total
            inside
            (List.length ancestors + 1)

    /// The structure below the focused chunk as counts by kind
    /// — data at hand to decide the next move without reading
    /// any of it. Direct children only (descend to drill).
    let summarizeChildren
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : string =
        let children = ChunkTree.children (Some chunk.Id) tree
        if List.isEmpty children then
            "Nothing inside."
        else
            let pluralLabel (kind: Chunk.ChunkKind) : string =
                match kind with
                | Chunk.Heading _ -> "sections"
                | Chunk.Paragraph -> "paragraphs"
                | Chunk.ListBlock _ -> "lists"
                | Chunk.ListItem -> "list items"
                | Chunk.CodeBlock _ -> "code blocks"
                | Chunk.BlockQuote -> "quotes"
                | Chunk.ThematicBreak -> "separators"
                | Chunk.UserRequest -> "requests"
                | Chunk.ToolUse _ -> "tool calls"
                | Chunk.ToolResult _ -> "tool results"
                | Chunk.AgentError -> "agent errors"
                | Chunk.SystemNote -> "notes"
            let part (label: string, group: Chunk.Chunk list) =
                let n = List.length group
                let word =
                    if n = 1 then label.TrimEnd('s') else label
                sprintf "%d %s" n word
            let counts =
                children
                |> List.groupBy (fun c -> pluralLabel c.Kind)
                |> List.map part
                |> String.concat ", "
            sprintf "Contains %s." counts

    /// ADR 0012 S5 — `describe` bounded for navigation reads:
    /// a long body (a big code block, a wall of tool output) is
    /// cut at `maxChars` with an honest marker telling the
    /// listener how much remains and how to hear it all. The
    /// structure prefix always survives — the cut applies to
    /// the rendered string, and kinds put structure first.
    let describeCapped
            (maxChars: int)
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : string =
        let full = describe tree chunk
        if full.Length <= maxChars then
            full
        else
            let remaining = full.Length - maxChars
            sprintf
                "%s… Truncated; %d more characters — press r to hear all."
                (full.Substring(0, maxChars))
                remaining

    /// ADR 0012 S2 — the navigation read: bounded content plus
    /// the positional suffix ("…, 3 of 5"). The re-narrate verb
    /// uses plain `describe` (pure content, no position).
    let describeAt
            (maxChars: int)
            (tree: ChunkTree.Tree)
            (chunk: Chunk.Chunk)
            : string =
        let position, total = positionOf tree chunk
        sprintf
            "%s — %d of %d."
            (describeCapped maxChars tree chunk)
            position
            total
