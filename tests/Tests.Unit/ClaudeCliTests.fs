module PtySpeak.Tests.Unit.ClaudeCliTests

open Xunit
open Engine.Core.AgentEvent
open Engine.Participants

// ---------------------------------------------------------------------
// ADR 0011 E4 — Claude CLI participant (pure parts).
// ---------------------------------------------------------------------
//
// The process layer is deliberately thin and not spawned in CI;
// the argument builder and the line pump are the tested logic.

[<Fact>]
let ``arguments are per-turn stream-json with verbose`` () =
    Assert.Equal<string list>(
        [ "-p"; "fix it"; "--output-format"; "stream-json"; "--verbose" ],
        ClaudeCli.buildArguments "fix it" None)

[<Fact>]
let ``resume id is appended when present`` () =
    Assert.Equal<string list>(
        [ "-p"; "next step"; "--output-format"; "stream-json";
          "--verbose"; "--resume"; "sid-42" ],
        ClaudeCli.buildArguments "next step" (Some "sid-42"))

[<Fact>]
let ``the prompt is passed verbatim even when it looks like a flag`` () =
    // ArgumentList quoting is the process layer's job; the
    // builder must never mangle the prompt.
    let args = ClaudeCli.buildArguments "--help me \"quoted\"" None
    Assert.Equal("--help me \"quoted\"", args.[1])

[<Fact>]
let ``pumpLines parses every line until the source is exhausted`` () =
    let lines =
        [ """{"type":"system","subtype":"init","session_id":"s1"}"""
          ""
          """{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""" ]
    let mutable remaining = lines
    let readLine () =
        match remaining with
        | [] -> None
        | head :: tail ->
            remaining <- tail
            Some head
    let received = ResizeArray<AgentEvent>()
    ClaudeCli.pumpLines readLine (fun ev -> received.Add ev)
    // The blank line yields no event; the two JSON lines do.
    Assert.Equal(2, received.Count)
    match received.[0], received.[1] with
    | SessionInit ("s1", None), TurnResult (false, None, Some "s1") -> ()
    | a, b -> failwithf "unexpected events %A %A" a b

[<Fact>]
let ``pumpLines delivers malformed lines as typed ParseError`` () =
    let mutable remaining = [ "{not json" ]
    let readLine () =
        match remaining with
        | [] -> None
        | head :: tail ->
            remaining <- tail
            Some head
    let received = ResizeArray<AgentEvent>()
    ClaudeCli.pumpLines readLine (fun ev -> received.Add ev)
    match List.ofSeq received with
    | [ ParseError (_, raw) ] -> Assert.Equal("{not json", raw)
    | other -> failwithf "expected one ParseError, got %A" other
