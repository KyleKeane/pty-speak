module PtySpeak.Tests.Unit.NotebookSerdeTests

open Xunit
open Engine.Core
open Engine.Core.Notebook

// ---------------------------------------------------------------------
// ADR 0013 N4 — notebook JSONL: round-trip, pinned ids survive
// as references, schema gating, typed errors.
// ---------------------------------------------------------------------

[<Fact>]
let ``a notebook round-trips through jsonl exactly`` () =
    let chunkId = Chunk.newId ()
    let original =
        empty
        |> addSection "Findings"
        |> pin chunkId
        |> addNarrative "with \"quotes\" and\nnewlines"
    match NotebookSerde.parseJsonl (NotebookSerde.toJsonl original) with
    | Ok restored ->
        Assert.Equal(3, count restored)
        Assert.Equal<Cell list>(original.Cells, restored.Cells)
    | Error e -> failwithf "unexpected Error: %s" e

[<Fact>]
let ``a pinned cell still points at the same chunk after the trip`` () =
    let chunk, tree =
        match ChunkTree.append None Chunk.Paragraph "kept" ChunkTree.empty with
        | Ok v -> v
        | Error e -> failwith e
    let restored =
        match NotebookSerde.parseJsonl
                  (NotebookSerde.toJsonl (empty |> pin chunk.Id)) with
        | Ok nb -> nb
        | Error e -> failwith e
    // Renders through the live tree — reference semantics held.
    Assert.Equal<string list>(
        [ "Pinned: kept" ],
        restored.Cells |> List.map (describeCell tree))

[<Fact>]
let ``the empty notebook round-trips`` () =
    match NotebookSerde.parseJsonl (NotebookSerde.toJsonl empty) with
    | Ok restored -> Assert.Equal(0, count restored)
    | Error e -> failwithf "unexpected Error: %s" e

[<Fact>]
let ``a newer schema version is a typed error`` () =
    match NotebookSerde.parseJsonl "{\"schemaVersion\":99}\n" with
    | Error e -> Assert.Contains("newer", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``a corrupt cell line is a typed error`` () =
    match NotebookSerde.parseJsonl
              "{\"schemaVersion\":1,\"cellCount\":1}\n{nope" with
    | Error e -> Assert.Contains("malformed", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``an unknown cell kind is a typed error`` () =
    let text =
        "{\"schemaVersion\":1,\"cellCount\":1}\n"
        + "{\"id\":\"x\",\"cell\":\"hologram\"}"
    match NotebookSerde.parseJsonl text with
    | Error e -> Assert.Contains("hologram", e)
    | Ok _ -> failwith "expected Error"
