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
