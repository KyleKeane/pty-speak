namespace Engine.Core

open System
open System.Text.Json
open Engine.Core.Chunk

/// ADR 0013 N4 — session persistence: the capture tree as
/// JSONL, schema v1. One meta line (schema version, the
/// participant session id for `--resume`, the cursor anchors)
/// followed by one chunk per line in capture order. Follows the
/// `docs/IOCELL-SCHEMA.md` discipline: locked key order,
/// explicit `schemaVersion`, tolerant reader (a corrupt file is
/// a typed `Error`, never a crash), one-way migrations.
module ChunkSerde =

    [<Literal>]
    let SchemaVersion = 1

    /// Everything a restored session needs (the tree itself is
    /// rebuilt via `ChunkTree.restore`, which re-validates the
    /// structural invariants).
    type SessionFile =
        { SessionId: string option
          LatestResponseStart: ChunkId option
          CurrentRequest: ChunkId option
          Chunks: Chunk.Chunk list }

    // --- writing -----------------------------------------------------

    let private writeJson (write: Utf8JsonWriter -> unit) : string =
        use stream = new IO.MemoryStream()
        let options = JsonWriterOptions(Indented = false)
        use writer = new Utf8JsonWriter(stream, options)
        write writer
        writer.Flush()
        Text.Encoding.UTF8.GetString(stream.ToArray())

    let private kindFields
            (writer: Utf8JsonWriter)
            (kind: ChunkKind)
            : unit =
        match kind with
        | Heading level ->
            writer.WriteString("kind", "heading")
            writer.WriteNumber("level", level)
        | Paragraph -> writer.WriteString("kind", "paragraph")
        | ListBlock ordered ->
            writer.WriteString("kind", "list")
            writer.WriteBoolean("ordered", ordered)
        | ListItem -> writer.WriteString("kind", "listItem")
        | CodeBlock language ->
            writer.WriteString("kind", "code")
            match language with
            | Some lang -> writer.WriteString("language", lang)
            | None -> writer.WriteNull("language")
        | BlockQuote -> writer.WriteString("kind", "quote")
        | ThematicBreak -> writer.WriteString("kind", "break")
        | UserRequest -> writer.WriteString("kind", "request")
        | ToolUse name ->
            writer.WriteString("kind", "toolUse")
            writer.WriteString("name", name)
        | ToolResult isError ->
            writer.WriteString("kind", "toolResult")
            writer.WriteBoolean("isError", isError)
        | AgentError -> writer.WriteString("kind", "agentError")
        | SystemNote -> writer.WriteString("kind", "note")

    /// One chunk → one JSONL line (locked key order).
    let chunkToJson (chunk: Chunk.Chunk) : string =
        writeJson (fun writer ->
            writer.WriteStartObject()
            let (ChunkId id) = chunk.Id
            writer.WriteString("id", id)
            kindFields writer chunk.Kind
            writer.WriteString("text", chunk.Text)
            writer.WriteNumber("captureSeq", chunk.CaptureSeq)
            writer.WriteNumber("authoredIndex", chunk.AuthoredIndex)
            match chunk.Parent with
            | Some (ChunkId pid) -> writer.WriteString("parent", pid)
            | None -> writer.WriteNull("parent")
            writer.WriteEndObject())

    /// The whole session → JSONL text (meta line + chunks in
    /// capture order, newline-separated, trailing newline).
    let sessionToJsonl (file: SessionFile) : string =
        let meta =
            writeJson (fun writer ->
                writer.WriteStartObject()
                writer.WriteNumber("schemaVersion", SchemaVersion)
                match file.SessionId with
                | Some sid -> writer.WriteString("sessionId", sid)
                | None -> writer.WriteNull("sessionId")
                match file.LatestResponseStart with
                | Some (ChunkId id) ->
                    writer.WriteString("latestResponseStart", id)
                | None -> writer.WriteNull("latestResponseStart")
                match file.CurrentRequest with
                | Some (ChunkId id) ->
                    writer.WriteString("currentRequest", id)
                | None -> writer.WriteNull("currentRequest")
                writer.WriteEndObject())
        let chunkLines = file.Chunks |> List.map chunkToJson
        String.concat "\n" (meta :: chunkLines) + "\n"

    // --- reading -----------------------------------------------------

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

    let private tryBool (el: JsonElement) (name: string) : bool option =
        let found, v = el.TryGetProperty(name)
        if not found then None
        elif v.ValueKind = JsonValueKind.True then Some true
        elif v.ValueKind = JsonValueKind.False then Some false
        else None

    let private parseKind (el: JsonElement) : Result<ChunkKind, string> =
        match tryStr el "kind" with
        | Some "heading" ->
            match tryInt el "level" with
            | Some level -> Ok (Heading level)
            | None -> Error "heading without a level"
        | Some "paragraph" -> Ok Paragraph
        | Some "list" ->
            Ok (ListBlock (tryBool el "ordered" |> Option.defaultValue false))
        | Some "listItem" -> Ok ListItem
        | Some "code" -> Ok (CodeBlock (tryStr el "language"))
        | Some "quote" -> Ok BlockQuote
        | Some "break" -> Ok ThematicBreak
        | Some "request" -> Ok UserRequest
        | Some "toolUse" ->
            Ok (ToolUse (tryStr el "name" |> Option.defaultValue ""))
        | Some "toolResult" ->
            Ok (ToolResult (tryBool el "isError" |> Option.defaultValue false))
        | Some "agentError" -> Ok AgentError
        | Some "note" -> Ok SystemNote
        | Some other -> Error (sprintf "unknown chunk kind '%s'" other)
        | None -> Error "chunk line without a kind"

    let private parseChunkLine (line: string) : Result<Chunk.Chunk, string> =
        try
            use doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            match tryStr root "id", parseKind root with
            | None, _ -> Error "chunk line without an id"
            | _, Error e -> Error e
            | Some id, Ok kind ->
                match tryInt root "captureSeq", tryInt root "authoredIndex" with
                | Some seq, Some authored ->
                    Ok { Id = ChunkId id
                         Kind = kind
                         Text = tryStr root "text" |> Option.defaultValue ""
                         CaptureSeq = seq
                         AuthoredIndex = authored
                         Parent = tryStr root "parent" |> Option.map ChunkId }
                | _ -> Error "chunk line without capture/authored order"
        with :? JsonException as ex ->
            Error (sprintf "malformed chunk line: %s" ex.Message)

    /// Parse JSONL text back to a `SessionFile`. The first
    /// non-blank line must be the meta object with a supported
    /// schema version.
    let parseJsonl (text: string) : Result<SessionFile, string> =
        let lines =
            text.Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r'))
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.ofArray
        match lines with
        | [] -> Error "empty session file"
        | metaLine :: chunkLines ->
            try
                use doc = JsonDocument.Parse(metaLine)
                let meta = doc.RootElement
                match tryInt meta "schemaVersion" with
                | None -> Error "missing schemaVersion"
                | Some v when v > SchemaVersion ->
                    Error (
                        sprintf
                            "session schemaVersion %d is newer than this build supports (%d)"
                            v SchemaVersion)
                | Some _ ->
                    let folded =
                        ((Ok []), chunkLines)
                        ||> List.fold (fun acc line ->
                            match acc with
                            | Error e -> Error e
                            | Ok chunks ->
                                match parseChunkLine line with
                                | Ok chunk -> Ok (chunks @ [ chunk ])
                                | Error e -> Error e)
                    match folded with
                    | Error e -> Error e
                    | Ok chunks ->
                        Ok { SessionId = tryStr meta "sessionId"
                             LatestResponseStart =
                                tryStr meta "latestResponseStart"
                                |> Option.map ChunkId
                             CurrentRequest =
                                tryStr meta "currentRequest"
                                |> Option.map ChunkId
                             Chunks = chunks }
            with :? JsonException as ex ->
                Error (sprintf "malformed meta line: %s" ex.Message)
