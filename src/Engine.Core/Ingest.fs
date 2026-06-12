namespace Engine.Core

open Engine.Core.AgentEvent
open Engine.Core.EngineEvent

/// RELAUNCH-SPEC §5.2 / ADR 0011 E5 — the ingest fold. Typed
/// participant events in, (new session state, engine events to
/// publish) out. Pure: no bus, no I/O — the host publishes the
/// returned events, which keeps every sealing rule unit-testable.
///
/// The streaming rule, realized: chunks enter the tree (and are
/// announced as `ChunkSealed`) only at assistant-message /
/// tool-result boundaries — never mid-stream. In-flight turns
/// surface only ambient `ResponseProgress` counts.
module Ingest =

    /// Engine session state (the capture layer's live cursor —
    /// the tree itself is the §5.3 capture layer).
    type Session =
        { Tree: ChunkTree.Tree
          /// The participant's session id (for `--resume`).
          SessionId: string option
          /// The `UserRequest` chunk the in-flight turn answers;
          /// sealed response chunks nest under it.
          CurrentRequest: Chunk.ChunkId option
          /// First chunk of the latest response — the §6.2
          /// "jump to start of latest agent response" target.
          LatestResponseStart: Chunk.ChunkId option
          /// Chunks sealed so far in the in-flight turn.
          InFlightCount: int }

    let empty : Session =
        { Tree = ChunkTree.empty
          SessionId = None
          CurrentRequest = None
          LatestResponseStart = None
          InFlightCount = 0 }

    /// Append one chunk under `parent`, tolerating the
    /// (structurally unreachable) missing-parent error by
    /// appending at top level instead — ingest must never lose
    /// content.
    let private appendSafe
            (parent: Chunk.ChunkId option)
            (kind: Chunk.ChunkKind)
            (text: string)
            (tree: ChunkTree.Tree)
            : Chunk.Chunk * ChunkTree.Tree =
        match ChunkTree.append parent kind text tree with
        | Ok (chunk, tree') -> chunk, tree'
        | Error _ ->
            match ChunkTree.append None kind text tree with
            | Ok (chunk, tree') -> chunk, tree'
            | Error msg ->
                // Top-level append cannot fail by construction.
                failwith msg

    /// Append a chunk-spec forest (depth-first, authored order)
    /// under `parent`. Returns appended chunks in seal order.
    let rec private appendSpecs
            (parent: Chunk.ChunkId option)
            (specs: MarkdownChunker.ChunkSpec list)
            (tree: ChunkTree.Tree)
            : Chunk.Chunk list * ChunkTree.Tree =
        ((([], tree)), specs)
        ||> List.fold (fun (acc, t) spec ->
            let chunk, t1 = appendSafe parent spec.Kind spec.Text t
            let childChunks, t2 =
                appendSpecs (Some chunk.Id) spec.Children t1
            (acc @ (chunk :: childChunks), t2))

    /// Capture the user's composed request as a typed act into
    /// the tree (the §0.2 interaction-manager move). `anchor`
    /// is `None` for the main thread; `Some chunkId` forks a
    /// clarification branch under that chunk (§5.1). A new turn
    /// begins: the in-flight counters reset.
    let captureRequest
            (text: string)
            (anchor: Chunk.ChunkId option)
            (session: Session)
            : Session * EngineEvent list =
        let chunk, tree =
            appendSafe anchor Chunk.UserRequest text session.Tree
        let session' =
            { session with
                Tree = tree
                CurrentRequest = Some chunk.Id
                InFlightCount = 0 }
        session', [ RequestCaptured chunk ]

    /// Seal a batch of chunks: emits one `ChunkSealed` per
    /// chunk plus one trailing ambient progress count, and
    /// records the latest-response start on the first batch of
    /// a turn.
    let private sealBatch
            (chunks: Chunk.Chunk list)
            (tree: ChunkTree.Tree)
            (session: Session)
            : Session * EngineEvent list =
        match chunks with
        | [] -> { session with Tree = tree }, []
        | first :: _ ->
            let latestStart =
                if session.InFlightCount = 0 then Some first.Id
                else session.LatestResponseStart
            let count = session.InFlightCount + List.length chunks
            let session' =
                { session with
                    Tree = tree
                    LatestResponseStart = latestStart
                    InFlightCount = count }
            let events =
                (chunks |> List.map ChunkSealed)
                @ [ ResponseProgress count ]
            session', events

    /// Fold one typed participant event into the session.
    let applyAgentEvent
            (event: AgentEvent.AgentEvent)
            (session: Session)
            : Session * EngineEvent list =
        match event with
        | SessionInit (sid, _model) ->
            { session with SessionId = Some sid },
            [ SessionStarted sid ]
        | AssistantMessage blocks ->
            let parent = session.CurrentRequest
            let chunks, tree, notes =
                ((([], session.Tree, []): Chunk.Chunk list * ChunkTree.Tree * EngineEvent list),
                 blocks)
                ||> List.fold (fun (acc, t, ns) block ->
                    match block with
                    | Text text ->
                        // ADR 0012 S1 — recover the section
                        // structure before append: the tree
                        // carries the document's real outline.
                        let specs =
                            MarkdownChunker.decompose text
                            |> SemanticOutline.nest
                        let sealed_, t' = appendSpecs parent specs t
                        (acc @ sealed_, t', ns)
                    | ToolUse (_id, name, inputJson) ->
                        let chunk, t' =
                            appendSafe
                                parent
                                (Chunk.ToolUse name)
                                inputJson
                                t
                        (acc @ [ chunk ], t', ns)
                    | UnknownBlock blockType ->
                        // Surfaced ambient, not entombed in the
                        // tree (ADR 0008: typed and visible).
                        let note =
                            EngineNote (
                                sprintf
                                    "Unrecognized content block type: %s"
                                    blockType)
                        (acc, t, ns @ [ note ]))
            let session', sealEvents = sealBatch chunks tree session
            session', sealEvents @ notes
        | ToolResults results ->
            let parent = session.CurrentRequest
            let chunks, tree =
                ((([], session.Tree)), results)
                ||> List.fold (fun (acc, t) r ->
                    let chunk, t' =
                        appendSafe
                            parent
                            (Chunk.ToolResult r.IsError)
                            r.Content
                            t
                    (acc @ [ chunk ], t'))
            sealBatch chunks tree session
        | TurnResult (isError, _resultText, sid) ->
            // The result text duplicates the final assistant
            // message — not re-appended (no double content).
            let session' =
                match sid with
                | Some s -> { session with SessionId = Some s }
                | None -> session
            session',
            [ ResponseCompleted (isError, session'.InFlightCount) ]
        | Unknown (eventType, _raw) ->
            session,
            [ EngineNote (
                sprintf "Unrecognized stream event type: %s" eventType) ]
        | ParseError (message, _line) ->
            session,
            [ EngineNote (sprintf "Stream parse error: %s" message) ]
