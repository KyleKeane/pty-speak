module PtySpeak.Tests.Unit.ChunkTreePropertyTests

open FsCheck.Xunit
open Engine.Core
open Engine.Core.Chunk

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §5 / ADR 0011 E3 — chunk-tree invariants under
// arbitrary append sequences (FsCheck).
// ---------------------------------------------------------------------
//
// `build` folds an arbitrary byte list into a tree: each byte
// picks the next chunk's parent (0 mod (n+1) → top level, else
// one of the existing chunks). The properties then assert the
// model's structural invariants hold for EVERY reachable v1
// tree, not just the example-based fixtures.

let private build (choices: byte list) : ChunkTree.Tree * ChunkId list =
    ((ChunkTree.empty, []), choices)
    ||> List.fold (fun (tree, ids) choice ->
        let parent =
            match ids with
            | [] -> None
            | _ when int choice % (List.length ids + 1) = 0 -> None
            | _ -> ids |> List.tryItem (int choice % List.length ids)
        match ChunkTree.append parent Paragraph (sprintf "c%d" (int choice)) tree with
        | Ok (chunk, tree') -> (tree', ids @ [ chunk.Id ])
        | Error _ -> (tree, ids))

[<Property>]
let ``count equals the number of appended chunks`` (choices: byte list) =
    let tree, ids = build choices
    ChunkTree.count tree = List.length ids

[<Property>]
let ``capture order is exactly the insertion order`` (choices: byte list) =
    let tree, ids = build choices
    (ChunkTree.inCaptureOrder tree |> List.map (fun c -> c.Id)) = ids

[<Property>]
let ``every chunk sits in its parent's child list at its authored index``
        (choices: byte list) =
    let tree, _ = build choices
    ChunkTree.inCaptureOrder tree
    |> List.forall (fun c ->
        let siblings = ChunkTree.children c.Parent tree
        (siblings
         |> List.tryItem c.AuthoredIndex
         |> Option.map (fun s -> s.Id)) = Some c.Id)

[<Property>]
let ``every child listed under a parent points back to it`` (choices: byte list) =
    let tree, ids = build choices
    ids
    |> List.forall (fun id ->
        ChunkTree.children (Some id) tree
        |> List.forall (fun child -> child.Parent = Some id))

[<Property>]
let ``next and previous sibling are inverse where defined`` (choices: byte list) =
    let tree, _ = build choices
    ChunkTree.inCaptureOrder tree
    |> List.forall (fun c ->
        match ChunkTree.nextSibling c.Id tree with
        | None -> true
        | Some next ->
            match ChunkTree.prevSibling next.Id tree with
            | Some back -> back.Id = c.Id
            | None -> false)

[<Property>]
let ``descend lands on the first authored child`` (choices: byte list) =
    let tree, ids = build choices
    ids
    |> List.forall (fun id ->
        match ChunkTree.children (Some id) tree with
        | [] -> (ChunkTree.firstChild id tree).IsNone
        | first :: _ ->
            (ChunkTree.firstChild id tree
             |> Option.map (fun c -> c.Id)) = Some first.Id)
