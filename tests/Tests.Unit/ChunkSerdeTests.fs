module PtySpeak.Tests.Unit.ChunkSerdeTests

open Xunit
open FsCheck.Xunit
open Engine.Core
open Engine.Core.Chunk
open Engine.Core.ChunkSerde

// ---------------------------------------------------------------------
// ADR 0013 N4 — session JSONL contract: round-trip fidelity
// (example-based AND property-based over arbitrary trees),
// schema gating, tolerant typed errors, restore validation.
// ---------------------------------------------------------------------

let private ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "unexpected Error: %s" e

/// Same scripted-builder as ChunkTreePropertyTests: bytes pick
/// parents, kinds rotate through the vocabulary.
let private kinds : ChunkKind list =
    [ Heading 1; Paragraph; ListBlock true; ListItem
      CodeBlock (Some "fsharp"); CodeBlock None; BlockQuote
      ThematicBreak; UserRequest; ToolUse "Bash"
      ToolResult false; ToolResult true; AgentError; SystemNote ]

let private build (choices: byte list) : ChunkTree.Tree =
    ((ChunkTree.empty, []), choices)
    ||> List.fold (fun (tree, ids) choice ->
        let parent =
            match ids with
            | [] -> None
            | _ when int choice % (List.length ids + 1) = 0 -> None
            | _ -> ids |> List.tryItem (int choice % List.length ids)
        let kind = kinds.[int choice % List.length kinds]
        match ChunkTree.append parent kind (sprintf "t%d" (int choice)) tree with
        | Ok (chunk, tree') -> (tree', ids @ [ chunk.Id ])
        | Error _ -> (tree, ids))
    |> fst

let private sessionOf (tree: ChunkTree.Tree) : SessionFile =
    { SessionId = Some "sid-1"
      LatestResponseStart =
        ChunkTree.inCaptureOrder tree
        |> List.tryLast
        |> Option.map (fun c -> c.Id)
      CurrentRequest = None
      Chunks = ChunkTree.inCaptureOrder tree }

[<Fact>]
let ``a session round-trips through jsonl exactly`` () =
    let req, tree = ok (ChunkTree.append None UserRequest "do it" ChunkTree.empty)
    let _, tree = ok (ChunkTree.append (Some req.Id) (Heading 2) "Plan" tree)
    let _, tree = ok (ChunkTree.append (Some req.Id) (CodeBlock (Some "py")) "x=1\n" tree)
    let original = sessionOf tree
    let parsed = ok (parseJsonl (sessionToJsonl original))
    Assert.Equal(original.SessionId, parsed.SessionId)
    Assert.Equal(original.LatestResponseStart, parsed.LatestResponseStart)
    Assert.Equal<Chunk.Chunk list>(original.Chunks, parsed.Chunks)

[<Fact>]
let ``restore rebuilds an identical navigable tree`` () =
    let req, tree = ok (ChunkTree.append None UserRequest "r" ChunkTree.empty)
    let kid, tree = ok (ChunkTree.append (Some req.Id) Paragraph "p" tree)
    let parsed = ok (parseJsonl (sessionToJsonl (sessionOf tree)))
    let restored = ok (ChunkTree.restore parsed.Chunks)
    Assert.Equal(ChunkTree.count tree, ChunkTree.count restored)
    Assert.Equal(
        Some kid.Id,
        ChunkTree.firstChild req.Id restored |> Option.map (fun c -> c.Id))

[<Property>]
let ``round-trip preserves every chunk for arbitrary trees`` (choices: byte list) =
    let tree = build choices
    let parsed = parseJsonl (sessionToJsonl (sessionOf tree))
    match parsed with
    | Error _ -> false
    | Ok file ->
        match ChunkTree.restore file.Chunks with
        | Error _ -> false
        | Ok restored ->
            ChunkTree.inCaptureOrder restored = ChunkTree.inCaptureOrder tree

[<Fact>]
let ``special characters survive the trip`` () =
    let _, tree =
        ok (ChunkTree.append
                None Paragraph
                "quotes \" backslash \\ newline \n tab \t unicode ⏎"
                ChunkTree.empty)
    let parsed = ok (parseJsonl (sessionToJsonl (sessionOf tree)))
    Assert.Equal(
        "quotes \" backslash \\ newline \n tab \t unicode ⏎",
        parsed.Chunks.Head.Text)

[<Fact>]
let ``a newer schema version is a typed error`` () =
    match parseJsonl "{\"schemaVersion\":99}\n" with
    | Error e -> Assert.Contains("newer", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``a corrupt chunk line is a typed error not a crash`` () =
    let text = "{\"schemaVersion\":1,\"sessionId\":null,\"latestResponseStart\":null,\"currentRequest\":null}\n{not json"
    match parseJsonl text with
    | Error e -> Assert.Contains("malformed", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``an empty file is a typed error`` () =
    match parseJsonl "" with
    | Error e -> Assert.Contains("empty", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``restore rejects an orphan chunk`` () =
    let chunk : Chunk.Chunk =
        { Id = newId ()
          Kind = Paragraph
          Text = "orphan"
          CaptureSeq = 0
          AuthoredIndex = 0
          Parent = Some (newId ()) }
    match ChunkTree.restore [ chunk ] with
    | Error e -> Assert.Contains("parent", e)
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``restore rejects out-of-order capture sequence`` () =
    let a : Chunk.Chunk =
        { Id = newId (); Kind = Paragraph; Text = "a"
          CaptureSeq = 5; AuthoredIndex = 0; Parent = None }
    let b : Chunk.Chunk =
        { Id = newId (); Kind = Paragraph; Text = "b"
          CaptureSeq = 5; AuthoredIndex = 1; Parent = None }
    match ChunkTree.restore [ a; b ] with
    | Error e -> Assert.Contains("capture", e)
    | Ok _ -> failwith "expected Error"
