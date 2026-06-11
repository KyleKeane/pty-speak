module PtySpeak.Tests.Unit.ClaudeStreamJsonTests

open Xunit
open Engine.Core
open Engine.Core.AgentEvent

// ---------------------------------------------------------------------
// ADR 0011 E4 — Claude CLI stream-json parser contract tests.
// ---------------------------------------------------------------------
//
// The fixture lines mirror the CLI's `-p --output-format
// stream-json --verbose` output shape; this corpus is the
// parser's contract until the Phase 0 dogfood re-verifies the
// wire format on the maintainer's machine (ADR 0011 E4). The
// tolerance rules are the load-bearing part: unknown types are
// surfaced typed, malformed lines become ParseError, and no
// input shape throws.

let private parsed (line: string) : AgentEvent.AgentEvent =
    match ClaudeStreamJson.parseLine line with
    | Some ev -> ev
    | None -> failwith "expected an event for a non-blank line"

[<Fact>]
let ``blank and whitespace lines produce no event`` () =
    Assert.True((ClaudeStreamJson.parseLine "").IsNone)
    Assert.True((ClaudeStreamJson.parseLine "   ").IsNone)

[<Fact>]
let ``system init yields SessionInit with session id and model`` () =
    let line =
        """{"type":"system","subtype":"init","cwd":"C:\\work","session_id":"abc-123","tools":["Bash"],"model":"claude-sonnet-4-6"}"""
    match parsed line with
    | SessionInit (sid, model) ->
        Assert.Equal("abc-123", sid)
        Assert.Equal(Some "claude-sonnet-4-6", model)
    | other -> failwithf "expected SessionInit, got %A" other

[<Fact>]
let ``assistant text message yields typed Text blocks`` () =
    let line =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"# Hi\n\nHello there."}]},"session_id":"abc-123"}"""
    match parsed line with
    | AssistantMessage [ Text t ] ->
        Assert.Equal("# Hi\n\nHello there.", t)
    | other -> failwithf "expected one Text block, got %A" other

[<Fact>]
let ``assistant tool_use block carries id name and raw input json`` () =
    let line =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"dir"}}]}}"""
    match parsed line with
    | AssistantMessage [ ToolUse (id, name, inputJson) ] ->
        Assert.Equal("toolu_01", id)
        Assert.Equal("Bash", name)
        Assert.Contains("\"command\"", inputJson)
    | other -> failwithf "expected one ToolUse block, got %A" other

[<Fact>]
let ``mixed content keeps block order and types unknown blocks`` () =
    let line =
        """{"type":"assistant","message":{"content":[{"type":"text","text":"before"},{"type":"thinking","thinking":"..."},{"type":"text","text":"after"}]}}"""
    match parsed line with
    | AssistantMessage [ Text "before"; UnknownBlock "thinking"; Text "after" ] -> ()
    | other -> failwithf "expected text/unknown/text, got %A" other

[<Fact>]
let ``user tool_result with string content yields ToolResults`` () =
    let line =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"file1.txt\nfile2.txt","is_error":false}]}}"""
    match parsed line with
    | ToolResults [ r ] ->
        Assert.Equal("toolu_01", r.ToolUseId)
        Assert.Equal("file1.txt\nfile2.txt", r.Content)
        Assert.False(r.IsError)
    | other -> failwithf "expected one ToolResult, got %A" other

[<Fact>]
let ``user tool_result with text-block-array content is flattened`` () =
    let line =
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_02","content":[{"type":"text","text":"line A"},{"type":"text","text":"line B"}],"is_error":true}]}}"""
    match parsed line with
    | ToolResults [ r ] ->
        Assert.Equal("line A\nline B", r.Content)
        Assert.True(r.IsError)
    | other -> failwithf "expected one ToolResult, got %A" other

[<Fact>]
let ``result line yields TurnResult with text and session`` () =
    let line =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":4200,"num_turns":3,"result":"Done.","session_id":"abc-123","total_cost_usd":0.012}"""
    match parsed line with
    | TurnResult (isError, text, sid) ->
        Assert.False(isError)
        Assert.Equal(Some "Done.", text)
        Assert.Equal(Some "abc-123", sid)
    | other -> failwithf "expected TurnResult, got %A" other

[<Fact>]
let ``error result keeps the error flag`` () =
    let line =
        """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"abc-123"}"""
    match parsed line with
    | TurnResult (true, None, Some "abc-123") -> ()
    | other -> failwithf "expected error TurnResult, got %A" other

[<Fact>]
let ``unknown top-level type is surfaced typed not dropped`` () =
    let line = """{"type":"stream_event","event":{"foo":1}}"""
    match parsed line with
    | Unknown ("stream_event", raw) -> Assert.Contains("foo", raw)
    | other -> failwithf "expected Unknown, got %A" other

[<Fact>]
let ``malformed json becomes ParseError carrying the line`` () =
    let line = """{"type":"assistant","message":"""
    match parsed line with
    | ParseError (_, raw) -> Assert.Equal(line, raw)
    | other -> failwithf "expected ParseError, got %A" other

[<Fact>]
let ``json without a type field becomes ParseError`` () =
    match parsed """{"foo":"bar"}""" with
    | ParseError (msg, _) -> Assert.Contains("type", msg)
    | other -> failwithf "expected ParseError, got %A" other

[<Fact>]
let ``non-object json becomes ParseError`` () =
    match parsed "[1,2,3]" with
    | ParseError _ -> ()
    | other -> failwithf "expected ParseError, got %A" other

[<Fact>]
let ``a multi-line transcript parses to the expected event sequence`` () =
    // A miniature end-to-end turn: init → assistant(tool_use) →
    // tool_result → assistant(text) → result.
    let transcript =
        [ """{"type":"system","subtype":"init","session_id":"s1","model":"m"}"""
          """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"x.fs"}}]}}"""
          """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"let x = 1"}]}}"""
          """{"type":"assistant","message":{"content":[{"type":"text","text":"The file defines x."}]}}"""
          """{"type":"result","subtype":"success","is_error":false,"result":"The file defines x.","session_id":"s1"}""" ]
    let events = transcript |> List.choose ClaudeStreamJson.parseLine
    Assert.Equal(5, List.length events)
    match events with
    | [ SessionInit ("s1", Some "m");
        AssistantMessage [ ToolUse ("t1", "Read", _) ];
        ToolResults [ _ ];
        AssistantMessage [ Text "The file defines x." ];
        TurnResult (false, Some _, Some "s1") ] -> ()
    | other -> failwithf "unexpected event sequence: %A" other
