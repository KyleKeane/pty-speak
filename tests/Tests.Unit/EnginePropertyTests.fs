module PtySpeak.Tests.Unit.EnginePropertyTests

open Xunit
open FsCheck.Xunit
open Engine.Core
open Engine.Core.MarkdownChunker

// ---------------------------------------------------------------------
// Cycle 54 hardening — property suites for the outline, the
// attention queue, and the ingest fold: the laws that must hold
// for EVERY input, not just the example fixtures.
// ---------------------------------------------------------------------

// --- SemanticOutline -------------------------------------------------

/// Arbitrary flat spec lists: bytes choose kind (heading levels
/// 1–3, paragraph, list-with-children) so nesting gets real
/// variety.
let private specOf (b: byte) : ChunkSpec =
    match int b % 6 with
    | 0 -> { Kind = Chunk.Heading 1; Text = "h1"; Children = [] }
    | 1 -> { Kind = Chunk.Heading 2; Text = "h2"; Children = [] }
    | 2 -> { Kind = Chunk.Heading 3; Text = "h3"; Children = [] }
    | 3 -> { Kind = Chunk.Paragraph; Text = "p"; Children = [] }
    | 4 ->
        { Kind = Chunk.ListBlock false
          Text = ""
          Children = [ { Kind = Chunk.ListItem; Text = "i"; Children = [] } ] }
    | _ -> { Kind = Chunk.CodeBlock None; Text = "c"; Children = [] }

let rec private flatten (specs: ChunkSpec list) : ChunkSpec list =
    specs
    |> List.collect (fun spec ->
        { spec with Children = [] } :: flatten spec.Children)

[<Property>]
let ``nest preserves every spec exactly once`` (choices: byte list) =
    let flat = choices |> List.map specOf
    let nested = SemanticOutline.nest flat
    // Flattening the nested forest yields the same kinds+texts
    // in the same left-to-right order as flattening the input
    // (the outline only re-parents; it never reorders, drops,
    // or duplicates).
    let key (s: ChunkSpec) = (s.Kind, s.Text)
    (flatten nested |> List.map key) = (flatten flat |> List.map key)

[<Property>]
let ``after nesting no heading is followed at its level by absorbable content``
        (choices: byte list) =
    // The scope law: at any level of the nested forest, a
    // non-heading element can only appear before the FIRST
    // heading of that sibling group (otherwise the preceding
    // heading should have absorbed it).
    let flat = choices |> List.map specOf
    let rec levelOk (specs: ChunkSpec list) : bool =
        let rec walk (seenHeading: bool) (rest: ChunkSpec list) =
            match rest with
            | [] -> true
            | spec :: tail ->
                match spec.Kind with
                | Chunk.Heading _ -> walk true tail
                | _ when seenHeading -> false
                | _ -> walk false tail
        walk false specs
        && specs |> List.forall (fun s -> levelOk s.Children)
    levelOk (SemanticOutline.nest flat)

// --- Attention -------------------------------------------------------

/// Bytes script a mixed enqueue sequence: even = foreground
/// (numbered), odd = ambient on one of three keys.
let private utteranceOf (i: int) (b: byte) : Attention.Utterance =
    if int b % 2 = 0 then
        Attention.Foreground (sprintf "fg-%d" i)
    else
        Attention.Ambient (
            sprintf "key-%d" (int b % 3),
            sprintf "amb-%d" i)

let private drainAll (queue: Attention.Queue) : string list =
    let rec go acc q =
        match Attention.tryDequeue q with
        | Some (text, rest) -> go (acc @ [ text ]) rest
        | None -> acc
    go [] queue

[<Property>]
let ``foreground order is preserved and precedes all ambient`` (choices: byte list) =
    let queue =
        ((Attention.empty, 0), choices)
        ||> List.fold (fun (q, i) b ->
            (Attention.enqueue (utteranceOf i b) q, i + 1))
        |> fst
    let drained = drainAll queue
    let isFg (s: string) = s.StartsWith "fg-"
    let fgDrained = drained |> List.filter isFg
    let fgExpected =
        choices
        |> List.mapi (fun i b -> i, b)
        |> List.filter (fun (_, b) -> int b % 2 = 0)
        |> List.map (fun (i, _) -> sprintf "fg-%d" i)
    // 1) every foreground survives, in order;
    fgDrained = fgExpected
    // 2) no ambient ever precedes a foreground;
    && (let firstAmb = drained |> List.tryFindIndex (isFg >> not)
        let lastFg = drained |> List.tryFindIndexBack isFg
        match firstAmb, lastFg with
        | Some a, Some f -> a > f
        | _ -> true)

[<Property>]
let ``ambient holds at most one utterance per key — the newest`` (choices: byte list) =
    let queue =
        ((Attention.empty, 0), choices)
        ||> List.fold (fun (q, i) b ->
            (Attention.enqueue (utteranceOf i b) q, i + 1))
        |> fst
    let ambient =
        drainAll queue |> List.filter (fun s -> s.StartsWith "amb-")
    let expectedNewestPerKey =
        choices
        |> List.mapi (fun i b -> i, b)
        |> List.filter (fun (_, b) -> int b % 2 = 1)
        |> List.groupBy (fun (_, b) -> int b % 3)
        |> List.map (fun (_, group) ->
            let i, _ = List.last group
            sprintf "amb-%d" i)
    (List.sort ambient) = (List.sort expectedNewestPerKey)

// --- Ingest ----------------------------------------------------------

/// Bytes script agent events; the fold must never throw, and
/// the tree must grow by exactly the sealed count.
let private agentEventOf (b: byte) : AgentEvent.AgentEvent =
    match int b % 7 with
    | 0 -> AgentEvent.SessionInit ("sid", None)
    | 1 ->
        AgentEvent.AssistantMessage
            [ AgentEvent.Text "# H\n\npara one\n\npara two" ]
    | 2 ->
        AgentEvent.AssistantMessage
            [ AgentEvent.ToolUse ("t", "Bash", "{}")
              AgentEvent.UnknownBlock "thinking" ]
    | 3 ->
        let result : AgentEvent.ToolResult =
            { ToolUseId = "t"; Content = "out"; IsError = false }
        AgentEvent.ToolResults [ result ]
    | 4 -> AgentEvent.TurnResult (false, Some "done", Some "sid")
    | 5 -> AgentEvent.Unknown ("mystery", "{}")
    | _ -> AgentEvent.ParseError ("bad", "{oops")

[<Property>]
let ``the ingest fold never throws and seals exactly what it announces``
        (choices: byte list) =
    let session, _ = Ingest.captureRequest "go" None Ingest.empty
    let finalSession, sealedCount =
        ((session, 0), choices)
        ||> List.fold (fun (s, sealed_) b ->
            let s', events = Ingest.applyAgentEvent (agentEventOf b) s
            let batch =
                events
                |> List.sumBy (function
                    | EngineEvent.ChunkSealed _ -> 1
                    | _ -> 0)
            (s', sealed_ + batch))
    // The request chunk + every announced seal = the tree.
    ChunkTree.count finalSession.Tree = 1 + sealedCount

[<Property>]
let ``capture order in the tree matches seal announcement order``
        (choices: byte list) =
    let session, _ = Ingest.captureRequest "go" None Ingest.empty
    let finalSession, sealedIds =
        ((session, []), choices)
        ||> List.fold (fun (s, ids) b ->
            let s', events = Ingest.applyAgentEvent (agentEventOf b) s
            let batchIds =
                events
                |> List.choose (function
                    | EngineEvent.ChunkSealed c -> Some c.Id
                    | _ -> None)
            (s', ids @ batchIds))
    let treeIdsAfterRequest =
        ChunkTree.inCaptureOrder finalSession.Tree
        |> List.map (fun c -> c.Id)
        |> List.skip 1
    treeIdsAfterRequest = sealedIds
