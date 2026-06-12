module PtySpeak.Tests.Unit.IngestTests

open Xunit
open Engine.Core
open Engine.Core.AgentEvent
open Engine.Core.EngineEvent

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §5.2 / ADR 0011 E5 — ingest-fold contract tests.
// ---------------------------------------------------------------------
//
// Pins the streaming rule: chunks seal (and are announced) only
// at message boundaries; in-flight turns surface ambient
// progress counts; unknown stream shapes surface as typed notes
// without polluting the tree; the latest-response-start tracking
// feeds the §6.2 jump verb.

let private sealedChunks (events: EngineEvent.EngineEvent list) =
    events
    |> List.choose (function
        | ChunkSealed c -> Some c
        | _ -> None)

[<Fact>]
let ``captureRequest appends a UserRequest and emits RequestCaptured`` () =
    let session, events = Ingest.captureRequest "do the thing" None Ingest.empty
    match events with
    | [ RequestCaptured chunk ] ->
        Assert.Equal(Chunk.UserRequest, chunk.Kind)
        Assert.Equal("do the thing", chunk.Text)
        Assert.Equal(Some chunk.Id, session.CurrentRequest)
        Assert.Equal(1, ChunkTree.count session.Tree)
    | other -> failwithf "expected RequestCaptured, got %A" other

[<Fact>]
let ``captureRequest with an anchor forks the branch under it`` () =
    let s1, ev1 = Ingest.captureRequest "main" None Ingest.empty
    let mainReq =
        match ev1 with
        | [ RequestCaptured c ] -> c
        | other -> failwithf "unexpected %A" other
    let s2, ev2 = Ingest.captureRequest "clarify?" (Some mainReq.Id) s1
    match ev2 with
    | [ RequestCaptured branch ] ->
        Assert.Equal(Some mainReq.Id, branch.Parent)
        Assert.Equal(Some branch.Id, s2.CurrentRequest)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``assistant text seals decomposed chunks under the request`` () =
    let s1, ev1 = Ingest.captureRequest "explain" None Ingest.empty
    let req =
        match ev1 with
        | [ RequestCaptured c ] -> c
        | other -> failwithf "unexpected %A" other
    let message =
        AssistantMessage [ Text "# Plan\n\nStep one is easy." ]
    let s2, events = Ingest.applyAgentEvent message s1
    let chunks = sealedChunks events
    Assert.Equal(2, List.length chunks)
    Assert.Equal(Chunk.Heading 1, chunks.[0].Kind)
    Assert.Equal(Chunk.Paragraph, chunks.[1].Kind)
    // ADR 0012 S1 — the outline nests: the section heads the
    // response under the request; its prose nests inside it.
    Assert.Equal(Some req.Id, chunks.[0].Parent)
    Assert.Equal(Some chunks.[0].Id, chunks.[1].Parent)
    // Trailing ambient progress carries the running count.
    match List.last events with
    | ResponseProgress 2 -> ()
    | other -> failwithf "expected ResponseProgress 2, got %A" other
    Assert.Equal(Some chunks.[0].Id, s2.LatestResponseStart)
    Assert.Equal(2, s2.InFlightCount)

[<Fact>]
let ``a second message in the same turn accumulates without moving the start`` () =
    let s1, _ = Ingest.captureRequest "go" None Ingest.empty
    let s2, ev2 =
        Ingest.applyAgentEvent (AssistantMessage [ Text "First." ]) s1
    let firstId = (sealedChunks ev2).Head.Id
    let s3, ev3 =
        Ingest.applyAgentEvent (AssistantMessage [ Text "Second." ]) s2
    Assert.Equal(Some firstId, s3.LatestResponseStart)
    Assert.Equal(2, s3.InFlightCount)
    match List.last ev3 with
    | ResponseProgress 2 -> ()
    | other -> failwithf "expected ResponseProgress 2, got %A" other

[<Fact>]
let ``a new turn resets the latest-response start`` () =
    let s1, _ = Ingest.captureRequest "one" None Ingest.empty
    let s2, _ =
        Ingest.applyAgentEvent (AssistantMessage [ Text "Answer one." ]) s1
    let s3, _ = Ingest.captureRequest "two" None s2
    Assert.Equal(0, s3.InFlightCount)
    let s4, ev4 =
        Ingest.applyAgentEvent (AssistantMessage [ Text "Answer two." ]) s3
    let newFirst = (sealedChunks ev4).Head.Id
    Assert.Equal(Some newFirst, s4.LatestResponseStart)
    Assert.NotEqual(s2.LatestResponseStart, s4.LatestResponseStart)

[<Fact>]
let ``tool use seals a ToolUse chunk carrying the input json`` () =
    let s1, _ = Ingest.captureRequest "run it" None Ingest.empty
    let message =
        AssistantMessage [ ToolUse ("t1", "Bash", """{"command":"dir"}""") ]
    let _s2, events = Ingest.applyAgentEvent message s1
    match sealedChunks events with
    | [ c ] ->
        Assert.Equal(Chunk.ToolUse "Bash", c.Kind)
        Assert.Contains("command", c.Text)
    | other -> failwithf "expected one ToolUse chunk, got %A" other

[<Fact>]
let ``tool results seal ToolResult chunks`` () =
    let s1, _ = Ingest.captureRequest "run it" None Ingest.empty
    let results : AgentEvent.ToolResult list =
        [ { ToolUseId = "t1"; Content = "out"; IsError = false }
          { ToolUseId = "t2"; Content = "boom"; IsError = true } ]
    let _s2, events = Ingest.applyAgentEvent (ToolResults results) s1
    match sealedChunks events with
    | [ ok_; err ] ->
        Assert.Equal(Chunk.ToolResult false, ok_.Kind)
        Assert.Equal("out", ok_.Text)
        Assert.Equal(Chunk.ToolResult true, err.Kind)
    | other -> failwithf "expected two ToolResult chunks, got %A" other

[<Fact>]
let ``turn result completes with the sealed count and error flag`` () =
    let s1, _ = Ingest.captureRequest "go" None Ingest.empty
    let s2, _ =
        Ingest.applyAgentEvent (AssistantMessage [ Text "Done deal." ]) s1
    let _s3, events =
        Ingest.applyAgentEvent (TurnResult (false, Some "Done deal.", Some "sid-1")) s2
    match events with
    | [ ResponseCompleted (false, 1) ] -> ()
    | other -> failwithf "expected ResponseCompleted (false, 1), got %A" other

[<Fact>]
let ``turn result text is not re-appended to the tree`` () =
    let s1, _ = Ingest.captureRequest "go" None Ingest.empty
    let s2, _ =
        Ingest.applyAgentEvent (AssistantMessage [ Text "Answer." ]) s1
    let before = ChunkTree.count s2.Tree
    let s3, _ =
        Ingest.applyAgentEvent (TurnResult (false, Some "Answer.", None)) s2
    Assert.Equal(before, ChunkTree.count s3.Tree)

[<Fact>]
let ``session init records the id and announces the session`` () =
    let session, events =
        Ingest.applyAgentEvent (SessionInit ("sid-9", Some "model")) Ingest.empty
    Assert.Equal(Some "sid-9", session.SessionId)
    match events with
    | [ SessionStarted "sid-9" ] -> ()
    | other -> failwithf "expected SessionStarted, got %A" other

[<Fact>]
let ``unknown events and parse errors become notes not chunks`` () =
    let s1, ev1 =
        Ingest.applyAgentEvent (Unknown ("stream_event", "{}")) Ingest.empty
    let s2, ev2 =
        Ingest.applyAgentEvent (ParseError ("bad json", "{oops")) s1
    Assert.Equal(0, ChunkTree.count s2.Tree)
    match ev1, ev2 with
    | [ EngineNote n1 ], [ EngineNote n2 ] ->
        Assert.Contains("stream_event", n1)
        Assert.Contains("bad json", n2)
    | other -> failwithf "expected notes, got %A" other

[<Fact>]
let ``unknown content blocks surface ambient and stay out of the tree`` () =
    let s1, _ = Ingest.captureRequest "go" None Ingest.empty
    let message =
        AssistantMessage [ UnknownBlock "thinking"; Text "Visible." ]
    let s2, events = Ingest.applyAgentEvent message s1
    // One sealed chunk (the text), one ambient note (the block).
    Assert.Equal(1, sealedChunks events |> List.length)
    Assert.True(
        events
        |> List.exists (function
            | EngineNote n -> n.Contains "thinking"
            | _ -> false))
    Assert.Equal(2, ChunkTree.count s2.Tree)
