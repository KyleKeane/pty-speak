namespace Engine.Core

/// RELAUNCH-SPEC §6.2 — the v1 navigation verbs, as pure
/// transitions over the chunk tree. The non-ejection invariant
/// (§6.4) holds by construction: every verb either moves focus
/// to an existing chunk or reports an edge while leaving focus
/// unchanged — focus can never land outside the content model.
module Navigator =

    /// Navigation state: the focused chunk plus the anchor
    /// stack (§5.1 anchor + return; a stack so branches nest).
    type State =
        { Current: Chunk.ChunkId option
          AnchorStack: Chunk.ChunkId list }

    let initial : State =
        { Current = None
          AnchorStack = [] }

    /// A verb's outcome. `Edge` carries a short canonical
    /// description ("no next chunk") the voice channel renders;
    /// focus is unchanged on `Edge` / `NothingFocused`.
    type Move =
        | Moved of Chunk.Chunk
        | Edge of description: string
        | NothingFocused

    /// Focus a specific chunk (host-driven, e.g. after a seal
    /// or a return-to-anchor).
    let focus
            (id: Chunk.ChunkId)
            (state: State)
            (tree: ChunkTree.Tree)
            : State * Move =
        match ChunkTree.tryFind id tree with
        | Some chunk -> { state with Current = Some chunk.Id }, Moved chunk
        | None -> state, Edge "that chunk is gone"

    /// §6.2 "jump to the start of the latest agent response".
    /// `latestStart` comes from `Ingest.Session`.
    let jumpToLatestResponse
            (latestStart: Chunk.ChunkId option)
            (state: State)
            (tree: ChunkTree.Tree)
            : State * Move =
        match latestStart with
        | None -> state, Edge "no response yet"
        | Some id -> focus id state tree

    let private moveVia
            (step: Chunk.ChunkId -> ChunkTree.Tree -> Chunk.Chunk option)
            (edgeDescription: string)
            (state: State)
            (tree: ChunkTree.Tree)
            : State * Move =
        match state.Current with
        | None -> state, NothingFocused
        | Some id ->
            match step id tree with
            | Some chunk ->
                { state with Current = Some chunk.Id }, Moved chunk
            | None -> state, Edge edgeDescription

    /// Next sibling chunk (authored order).
    let next (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia ChunkTree.nextSibling "no next chunk" state tree

    /// Previous sibling chunk.
    let previous (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia ChunkTree.prevSibling "no previous chunk" state tree

    /// Descend into the focused chunk's children.
    let descend (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia ChunkTree.firstChild "no content inside" state tree

    /// Ascend to the focused chunk's parent.
    let ascend (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia
            (fun id t -> ChunkTree.parent id t)
            "already at the top level"
            state
            tree

    /// First sibling at the focused chunk's level (re-focusing
    /// self when already first — a re-announce, by design).
    let firstSibling (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia
            (fun id t ->
                match ChunkTree.tryFind id t with
                | None -> None
                | Some chunk ->
                    ChunkTree.children chunk.Parent t |> List.tryHead)
            "nothing at this level"
            state
            tree

    /// Last sibling at the focused chunk's level.
    let lastSibling (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia
            (fun id t ->
                match ChunkTree.tryFind id t with
                | None -> None
                | Some chunk ->
                    ChunkTree.children chunk.Parent t |> List.tryLast)
            "nothing at this level"
            state
            tree

    /// Jump to the 1-based Nth sibling at the focused chunk's
    /// level (the digit keys — direct address within a row).
    let nthSibling (n: int) (state: State) (tree: ChunkTree.Tree) : State * Move =
        moveVia
            (fun id t ->
                match ChunkTree.tryFind id t with
                | None -> None
                | Some chunk ->
                    ChunkTree.children chunk.Parent t
                    |> List.tryItem (n - 1))
            (sprintf "there is no item %d at this level" n)
            state
            tree

    /// Find the next chunk whose text contains `query`
    /// (case-insensitive), searching capture order forward from
    /// the focused chunk and wrapping; the §6.2 exploration
    /// companion — jump by content when structure isn't enough.
    let findNext (query: string) (state: State) (tree: ChunkTree.Tree) : State * Move =
        let all = ChunkTree.inCaptureOrder tree
        if List.isEmpty all then
            state, Edge "nothing to search yet"
        else
            let matches (chunk: Chunk.Chunk) =
                chunk.Text.Contains(
                    query,
                    System.StringComparison.OrdinalIgnoreCase)
            let startIndex =
                match state.Current with
                | None -> -1
                | Some id ->
                    all
                    |> List.tryFindIndex (fun c -> c.Id = id)
                    |> Option.defaultValue -1
            let total = List.length all
            let found =
                Seq.init total (fun offset ->
                    all.[(startIndex + 1 + offset) % total])
                |> Seq.tryFind matches
            match found with
            | Some chunk ->
                { state with Current = Some chunk.Id }, Moved chunk
            | None -> state, Edge (sprintf "no match for %s" query)

    /// The focused chunk, for re-narration (§6.2).
    let current (state: State) (tree: ChunkTree.Tree) : Chunk.Chunk option =
        state.Current
        |> Option.bind (fun id -> ChunkTree.tryFind id tree)

    /// Remember the focused chunk as a branch anchor (§5.1) —
    /// called when the host starts a side branch from here.
    let pushAnchor (state: State) : State =
        match state.Current with
        | None -> state
        | Some id -> { state with AnchorStack = id :: state.AnchorStack }

    /// §6.2 "return to anchor" — back to the exact chunk the
    /// branch was spun from.
    let returnToAnchor
            (state: State)
            (tree: ChunkTree.Tree)
            : State * Move =
        match state.AnchorStack with
        | [] -> state, Edge "no anchor to return to"
        | anchor :: rest ->
            let popped = { state with AnchorStack = rest }
            focus anchor popped tree
