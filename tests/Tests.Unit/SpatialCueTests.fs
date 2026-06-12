module PtySpeak.Tests.Unit.SpatialCueTests

open Xunit
open Engine.Core
open Engine.Core.EngineEvent
open Engine.Core.SpatialCue

// ---------------------------------------------------------------------
// ADR 0012 S3 — spatial-signature tests. Uniqueness is the
// load-bearing property: the ear must be able to identify the
// event class (and the sealed chunk's kind) without speech.
// ---------------------------------------------------------------------

let private mkChunk (kind: Chunk.ChunkKind) : Chunk.Chunk =
    let parentless =
        ChunkTree.append None kind "x" ChunkTree.empty
    match parentless with
    | Ok (c, _) -> c
    | Error e -> failwith e

let private cueOf (ev: EngineEvent) : Cue =
    match forEvent ev with
    | Some c -> c
    | None -> failwithf "expected a cue for %A" ev

/// One representative event per family.
let private familyEvents : (string * EngineEvent) list =
    [ "request", RequestCaptured (mkChunk Chunk.UserRequest)
      "session", SessionStarted "s"
      "seal", ChunkSealed (mkChunk Chunk.Paragraph)
      "progress", ResponseProgress 3
      "completed", ResponseCompleted (false, 5)
      "failed", ResponseCompleted (true, 5)
      "note", EngineNote "n" ]

[<Fact>]
let ``every event family has a cue`` () =
    for _, ev in familyEvents do
        Assert.True((forEvent ev).IsSome)

[<Fact>]
let ``every event family has a unique pan-pitch signature`` () =
    let signatures =
        familyEvents
        |> List.map (fun (name, ev) ->
            let cue = cueOf ev
            name, (cue.Pan, cue.Pitch))
    let distinct =
        signatures |> List.map snd |> List.distinct
    Assert.Equal(List.length signatures, List.length distinct)

[<Fact>]
let ``every chunk kind seals with a distinct pitch`` () =
    let kinds : Chunk.ChunkKind list =
        [ Chunk.Heading 1; Chunk.Paragraph; Chunk.ListBlock true
          Chunk.ListItem; Chunk.CodeBlock None; Chunk.BlockQuote
          Chunk.ThematicBreak; Chunk.UserRequest
          Chunk.ToolUse "Bash"; Chunk.ToolResult false
          Chunk.ToolResult true; Chunk.AgentError; Chunk.SystemNote ]
    let pitches = kinds |> List.map pitchForKind
    Assert.Equal(List.length pitches, List.length (List.distinct pitches))

[<Fact>]
let ``the stage layout encodes the attention contract`` () =
    // Narrative thread = center; ambient = off to the sides.
    Assert.Equal(0.0, (cueOf (RequestCaptured (mkChunk Chunk.UserRequest))).Pan)
    Assert.Equal(0.0, (cueOf (ResponseCompleted (false, 1))).Pan)
    Assert.True((cueOf (ResponseProgress 1)).Pan > 0.0)
    Assert.True((cueOf (ChunkSealed (mkChunk Chunk.Paragraph))).Pan > 0.0)
    Assert.True((cueOf (SessionStarted "s")).Pan < 0.0)
    Assert.True((cueOf (EngineNote "n")).Pan < 0.0)

[<Fact>]
let ``progress pitch rises with the count and caps`` () =
    let pitchAt n = (cueOf (ResponseProgress n)).Pitch
    Assert.True(pitchAt 2 > pitchAt 1)
    Assert.True(pitchAt 12 > pitchAt 6)
    Assert.Equal(pitchAt 12, pitchAt 50)

[<Fact>]
let ``failure rings low and long relative to success`` () =
    let okCue = cueOf (ResponseCompleted (false, 1))
    let errCue = cueOf (ResponseCompleted (true, 1))
    Assert.True(errCue.Pitch < okCue.Pitch)
    Assert.True(errCue.DurationMs > okCue.DurationMs)

[<Fact>]
let ``nav cues pan toward the movement and edges ring dull`` () =
    Assert.True((forNav Next true).Pan > 0.0)
    Assert.True((forNav Previous true).Pan < 0.0)
    Assert.Equal(0.0, (forNav Descend true).Pan)
    Assert.True((forNav Descend true).Pitch < (forNav Ascend true).Pitch)
    let edge = forNav Next false
    Assert.True(edge.Pitch < (forNav Next true).Pitch)
    Assert.True(edge.Pan > 0.0)

[<Fact>]
let ``cue gains sit under speech`` () =
    for _, ev in familyEvents do
        Assert.True((cueOf ev).Gain <= 0.5)
    Assert.True((forNav Next true).Gain <= 0.5)
