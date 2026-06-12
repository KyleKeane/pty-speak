namespace Engine.Core

open Engine.Core.Chunk

/// RELAUNCH-SPEC §5 — the chunk tree. An immutable,
/// append-only-in-v1 tree of sealed chunks with stable ids and
/// both orderings (capture + authored) maintained on append.
///
/// Design notes (ADR 0011):
///   * Pure data + total functions; no I/O, no platform types,
///     no mutation. Every operation returns a new tree.
///   * Only **sealed** chunks enter the tree (the §5.2
///     streaming rule is enforced upstream by the ingest
///     layer): navigation never observes a moving target by
///     construction.
///   * Lookups are `option`-returning, never throwing — a
///     navigation layer must be able to probe freely.
module ChunkTree =

    /// The tree. `Children` is keyed by parent (`None` = the
    /// main-thread top level) and holds child ids in authored
    /// order — for v1 that is also insertion order.
    type Tree =
        private
            { ById: Map<ChunkId, Chunk.Chunk>
              Children: Map<ChunkId option, ChunkId list>
              NextCaptureSeq: int }

    /// The empty tree.
    let empty : Tree =
        { ById = Map.empty
          Children = Map.empty
          NextCaptureSeq = 0 }

    /// Total number of chunks.
    let count (tree: Tree) : int =
        tree.ById.Count

    /// Look a chunk up by id.
    let tryFind (id: ChunkId) (tree: Tree) : Chunk.Chunk option =
        tree.ById |> Map.tryFind id

    /// The ordered (authored-order) children of a parent;
    /// `None` parent = the main-thread top level.
    let children (parent: ChunkId option) (tree: Tree) : Chunk.Chunk list =
        match tree.Children |> Map.tryFind parent with
        | None -> []
        | Some ids ->
            ids
            |> List.choose (fun id -> tree.ById |> Map.tryFind id)

    /// The parent chunk of `id`, when it has one.
    let parent (id: ChunkId) (tree: Tree) : Chunk.Chunk option =
        match tree.ById |> Map.tryFind id with
        | None -> None
        | Some chunk ->
            match chunk.Parent with
            | None -> None
            | Some pid -> tree.ById |> Map.tryFind pid

    /// Append a sealed chunk under `parentId` (which must exist
    /// when `Some`, else `Error`). Assigns the id, the capture
    /// sequence, and the authored index. Returns the stored
    /// chunk + the new tree.
    let append
            (parentId: ChunkId option)
            (kind: ChunkKind)
            (text: string)
            (tree: Tree)
            : Result<Chunk.Chunk * Tree, string> =
        let parentMissing =
            match parentId with
            | None -> false
            | Some pid -> not (tree.ById |> Map.containsKey pid)
        if parentMissing then
            Error "append: parent chunk not present in the tree"
        else
            let siblings =
                match tree.Children |> Map.tryFind parentId with
                | Some ids -> ids
                | None -> []
            let chunk : Chunk.Chunk =
                { Id = newId ()
                  Kind = kind
                  Text = text
                  CaptureSeq = tree.NextCaptureSeq
                  AuthoredIndex = List.length siblings
                  Parent = parentId }
            let tree' =
                { ById = tree.ById |> Map.add chunk.Id chunk
                  Children =
                    tree.Children
                    |> Map.add parentId (siblings @ [ chunk.Id ])
                  NextCaptureSeq = tree.NextCaptureSeq + 1 }
            Ok (chunk, tree')

    /// The next sibling (authored order) of `id`, if any.
    let nextSibling (id: ChunkId) (tree: Tree) : Chunk.Chunk option =
        match tree.ById |> Map.tryFind id with
        | None -> None
        | Some chunk ->
            let siblings = children chunk.Parent tree
            siblings
            |> List.tryFindIndex (fun c -> c.Id = id)
            |> Option.bind (fun i -> siblings |> List.tryItem (i + 1))

    /// The previous sibling (authored order) of `id`, if any.
    let prevSibling (id: ChunkId) (tree: Tree) : Chunk.Chunk option =
        match tree.ById |> Map.tryFind id with
        | None -> None
        | Some chunk ->
            let siblings = children chunk.Parent tree
            siblings
            |> List.tryFindIndex (fun c -> c.Id = id)
            |> Option.bind (fun i ->
                if i = 0 then None
                else siblings |> List.tryItem (i - 1))

    /// First child (authored order) of `id`, if any — the
    /// "descend" navigation verb's target.
    let firstChild (id: ChunkId) (tree: Tree) : Chunk.Chunk option =
        children (Some id) tree |> List.tryHead

    /// All chunks in capture (temporal) order — the immutable
    /// transcript view (spec §5.3 capture layer).
    let inCaptureOrder (tree: Tree) : Chunk.Chunk list =
        tree.ById
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun c -> c.CaptureSeq)

    /// ADR 0013 N4 — rebuild a tree from previously-serialized
    /// chunks (capture order), VALIDATING the structural
    /// invariants as it goes: parents must precede their
    /// children, capture order must be strictly increasing,
    /// ids must be unique, and each authored index must match
    /// its arrival position among its siblings. A corrupt file
    /// is a typed `Error`, never a crash and never a silently
    /// wrong tree.
    let restore (chunks: Chunk.Chunk list) : Result<Tree, string> =
        let rec go (tree: Tree) (remaining: Chunk.Chunk list) =
            match remaining with
            | [] -> Ok tree
            | chunk :: rest ->
                let parentMissing =
                    match chunk.Parent with
                    | None -> false
                    | Some pid -> not (tree.ById |> Map.containsKey pid)
                if parentMissing then
                    Error "restore: a chunk's parent does not precede it"
                elif tree.ById |> Map.containsKey chunk.Id then
                    Error "restore: duplicate chunk id"
                elif chunk.CaptureSeq < tree.NextCaptureSeq then
                    Error "restore: capture order is not strictly increasing"
                else
                    let siblings =
                        match tree.Children |> Map.tryFind chunk.Parent with
                        | Some ids -> ids
                        | None -> []
                    if chunk.AuthoredIndex <> List.length siblings then
                        Error "restore: authored index inconsistent with sibling order"
                    else
                        go
                            { ById = tree.ById |> Map.add chunk.Id chunk
                              Children =
                                tree.Children
                                |> Map.add chunk.Parent (siblings @ [ chunk.Id ])
                              NextCaptureSeq = chunk.CaptureSeq + 1 }
                            rest
        go empty chunks
