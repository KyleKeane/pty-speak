namespace Engine.Core

open System
open System.Text.Json
open Engine.Core.Notebook

/// ADR 0013 N4 — notebook persistence: the authored sequence
/// as schema-v1 JSONL (meta line + one cell per line), same
/// discipline as `ChunkSerde`: locked key order, tolerant
/// typed-error reader, one-way migrations. Pinned cells
/// serialize their chunk id only — the content stays in the
/// session file (reference-not-copy survives the disk).
module NotebookSerde =

    [<Literal>]
    let SchemaVersion = 1

    let private writeJson (write: Utf8JsonWriter -> unit) : string =
        use stream = new IO.MemoryStream()
        use writer =
            new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        write writer
        writer.Flush()
        Text.Encoding.UTF8.GetString(stream.ToArray())

    let private cellToJson (cell: Cell) : string =
        writeJson (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("id", cell.Id)
            match cell.Content with
            | PinnedChunk (Chunk.ChunkId chunkId) ->
                writer.WriteString("cell", "pinned")
                writer.WriteString("chunk", chunkId)
            | Narrative text ->
                writer.WriteString("cell", "narrative")
                writer.WriteString("text", text)
            | SectionHeader title ->
                writer.WriteString("cell", "section")
                writer.WriteString("title", title)
            writer.WriteEndObject())

    /// The whole notebook → JSONL (meta + cells, trailing
    /// newline).
    let toJsonl (notebook: Notebook) : string =
        let meta =
            writeJson (fun writer ->
                writer.WriteStartObject()
                writer.WriteNumber("schemaVersion", SchemaVersion)
                writer.WriteNumber("cellCount", count notebook)
                writer.WriteEndObject())
        let lines = notebook.Cells |> List.map cellToJson
        String.concat "\n" (meta :: lines) + "\n"

    let private tryStr (el: JsonElement) (name: string) : string option =
        let found, v = el.TryGetProperty(name)
        if found && v.ValueKind = JsonValueKind.String then
            match v.GetString() with
            | null -> None
            | s -> Some s
        else None

    let private tryInt (el: JsonElement) (name: string) : int option =
        let found, v = el.TryGetProperty(name)
        if found && v.ValueKind = JsonValueKind.Number then
            let ok, i = v.TryGetInt32()
            if ok then Some i else None
        else None

    let private parseCellLine (line: string) : Result<Cell, string> =
        try
            use doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            match tryStr root "id" with
            | None -> Error "notebook cell without an id"
            | Some id ->
                match tryStr root "cell" with
                | Some "pinned" ->
                    match tryStr root "chunk" with
                    | Some chunkId ->
                        Ok { Id = id
                             Content = PinnedChunk (Chunk.ChunkId chunkId) }
                    | None -> Error "pinned cell without a chunk id"
                | Some "narrative" ->
                    Ok { Id = id
                         Content =
                            Narrative (
                                tryStr root "text"
                                |> Option.defaultValue "") }
                | Some "section" ->
                    Ok { Id = id
                         Content =
                            SectionHeader (
                                tryStr root "title"
                                |> Option.defaultValue "") }
                | Some other ->
                    Error (sprintf "unknown notebook cell kind '%s'" other)
                | None -> Error "notebook cell without a kind"
        with :? JsonException as ex ->
            Error (sprintf "malformed notebook line: %s" ex.Message)

    /// Parse JSONL text back to a notebook. Typed errors,
    /// never a crash; an empty file is the empty notebook's
    /// honest serialization, not an error.
    let parseJsonl (text: string) : Result<Notebook, string> =
        let lines =
            text.Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r'))
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.ofArray
        match lines with
        | [] -> Error "empty notebook file"
        | metaLine :: cellLines ->
            try
                use doc = JsonDocument.Parse(metaLine)
                match tryInt doc.RootElement "schemaVersion" with
                | None -> Error "missing schemaVersion"
                | Some v when v > SchemaVersion ->
                    Error (
                        sprintf
                            "notebook schemaVersion %d is newer than this build supports (%d)"
                            v SchemaVersion)
                | Some _ ->
                    let folded =
                        ((Ok []), cellLines)
                        ||> List.fold (fun acc line ->
                            match acc with
                            | Error e -> Error e
                            | Ok cells ->
                                match parseCellLine line with
                                | Ok cell -> Ok (cells @ [ cell ])
                                | Error e -> Error e)
                    match folded with
                    | Error e -> Error e
                    | Ok cells -> Ok { Cells = cells }
            with :? JsonException as ex ->
                Error (sprintf "malformed notebook meta line: %s" ex.Message)
