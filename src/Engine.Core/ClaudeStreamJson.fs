namespace Engine.Core

open System
open System.Text.Json
open Engine.Core.AgentEvent

/// ADR 0011 E4 — the Claude Code CLI stream-json line parser.
/// Pure: one JSON line in, one typed `AgentEvent` out. No
/// process handling here (that is `Engine.Participants`); no
/// chunking here (that is `MarkdownChunker.fs` on ingest).
///
/// Wire format (verified against the CLI's `-p --output-format
/// stream-json --verbose` mode; the fixture corpus in
/// `ClaudeStreamJsonTests.fs` is the contract, and the Phase 0
/// dogfood re-verifies on the maintainer's machine per ADR 0011
/// E4): one JSON object per line —
///
///   {"type":"system","subtype":"init","session_id":…,"model":…}
///   {"type":"assistant","message":{"content":[{"type":"text",…}
///        | {"type":"tool_use","id":…,"name":…,"input":{…}}]}}
///   {"type":"user","message":{"content":[{"type":"tool_result",
///        "tool_use_id":…,"content":…,"is_error":…}]}}
///   {"type":"result","subtype":…,"is_error":…,"result":…}
///
/// Tolerance rules: unknown top-level types → `Unknown`;
/// unknown block types → `UnknownBlock`; malformed JSON →
/// `ParseError`. Nothing throws; nothing is silently dropped.
module ClaudeStreamJson =

    /// A property's string value, when present and a string.
    /// (`JsonElement.GetString()` is nullable-annotated; the
    /// null arm is folded into `None`.)
    let private tryStringProp
            (el: JsonElement)
            (name: string)
            : string option =
        let found, v = el.TryGetProperty(name)
        if found && v.ValueKind = JsonValueKind.String then
            match v.GetString() with
            | null -> None
            | s -> Some s
        else
            None

    let private tryBoolProp
            (el: JsonElement)
            (name: string)
            : bool option =
        let found, v = el.TryGetProperty(name)
        if not found then None
        elif v.ValueKind = JsonValueKind.True then Some true
        elif v.ValueKind = JsonValueKind.False then Some false
        else None

    let private tryObjectProp
            (el: JsonElement)
            (name: string)
            : JsonElement option =
        let found, v = el.TryGetProperty(name)
        if found && v.ValueKind = JsonValueKind.Object then Some v
        else None

    let private tryArrayProp
            (el: JsonElement)
            (name: string)
            : JsonElement option =
        let found, v = el.TryGetProperty(name)
        if found && v.ValueKind = JsonValueKind.Array then Some v
        else None

    /// Flatten a `tool_result` content value: the wire carries
    /// either a plain string or an array of `{type:"text"}`
    /// blocks. Anything else flattens to "".
    let private flattenResultContent (el: JsonElement) : string =
        match el.ValueKind with
        | JsonValueKind.String ->
            match el.GetString() with
            | null -> ""
            | s -> s
        | JsonValueKind.Array ->
            el.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.Object then
                    match tryStringProp item "type" with
                    | Some "text" -> tryStringProp item "text"
                    | _ -> None
                else None)
            |> String.concat "\n"
        | _ -> ""

    /// One assistant-message content block → typed block.
    let private parseContentBlock (el: JsonElement) : ContentBlock =
        match tryStringProp el "type" with
        | Some "text" ->
            Text (tryStringProp el "text" |> Option.defaultValue "")
        | Some "tool_use" ->
            let inputJson =
                let found, v = el.TryGetProperty("input")
                if found then v.GetRawText() else "{}"
            ToolUse (
                tryStringProp el "id" |> Option.defaultValue "",
                tryStringProp el "name" |> Option.defaultValue "",
                inputJson)
        | Some other -> UnknownBlock other
        | None -> UnknownBlock "<untyped>"

    /// The `message.content` array of an envelope, as blocks.
    let private messageBlocks (root: JsonElement) : ContentBlock list =
        match tryObjectProp root "message" with
        | None -> []
        | Some message ->
            match tryArrayProp message "content" with
            | None -> []
            | Some content ->
                content.EnumerateArray()
                |> Seq.map parseContentBlock
                |> List.ofSeq

    /// A `user` envelope's tool results (only `tool_result`
    /// blocks are typed; the CLI's user envelopes carry tool
    /// results, not human input).
    let private toolResults (root: JsonElement) : ToolResult list =
        match tryObjectProp root "message" with
        | None -> []
        | Some message ->
            match tryArrayProp message "content" with
            | None -> []
            | Some content ->
                content.EnumerateArray()
                |> Seq.choose (fun block ->
                    if block.ValueKind <> JsonValueKind.Object then
                        None
                    else
                        match tryStringProp block "type" with
                        | Some "tool_result" ->
                            let payload : ToolResult =
                                { ToolUseId =
                                    tryStringProp block "tool_use_id"
                                    |> Option.defaultValue ""
                                  Content =
                                    let found, v =
                                        block.TryGetProperty("content")
                                    if found then flattenResultContent v
                                    else ""
                                  IsError =
                                    tryBoolProp block "is_error"
                                    |> Option.defaultValue false }
                            Some payload
                        | _ -> None)
                |> List.ofSeq

    /// Parse one stream line. `None` for blank lines (the line
    /// pump may deliver trailing empties); every non-blank line
    /// yields exactly one typed event.
    let parseLine (line: string) : AgentEvent.AgentEvent option =
        if String.IsNullOrWhiteSpace line then
            None
        else
            try
                use doc = JsonDocument.Parse(line)
                let root = doc.RootElement
                if root.ValueKind <> JsonValueKind.Object then
                    Some (ParseError ("not a JSON object", line))
                else
                    match tryStringProp root "type" with
                    | Some "system" ->
                        match tryStringProp root "subtype" with
                        | Some "init" ->
                            Some (SessionInit (
                                tryStringProp root "session_id"
                                |> Option.defaultValue "",
                                tryStringProp root "model"))
                        | _ ->
                            Some (Unknown ("system", root.GetRawText()))
                    | Some "assistant" ->
                        Some (AssistantMessage (messageBlocks root))
                    | Some "user" ->
                        Some (ToolResults (toolResults root))
                    | Some "result" ->
                        Some (TurnResult (
                            tryBoolProp root "is_error"
                            |> Option.defaultValue false,
                            tryStringProp root "result",
                            tryStringProp root "session_id"))
                    | Some other ->
                        Some (Unknown (other, root.GetRawText()))
                    | None ->
                        Some (ParseError ("missing type", line))
            with :? JsonException as ex ->
                Some (ParseError (ex.Message, line))
