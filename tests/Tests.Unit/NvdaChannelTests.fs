module PtySpeak.Tests.Unit.NvdaChannelTests

open Xunit
open Terminal.Core

/// Stage 8a — pins the NVDA channel's `Semantic → ActivityId`
/// mapping and the empty-payload skip + RenderEarcon /
/// RenderRaw skip contracts. The mapping must reproduce the
/// Stage-7 drain's activity-ID vocabulary exactly so the post-
/// retrofit NVDA reading is identical.
///
/// **8a does NOT consult `Priority`.** The behaviour-identical
/// contract preserves Stage 7's
/// `TerminalView.Announce(msg, activityId)` 2-arg overload
/// (`src/Views/TerminalView.cs:292-298`), which picks
/// `ImportantAll` for `pty-speak.output` and `MostRecent` for
/// everything else. Tests that pin Priority → Processing
/// arrive when a future stage migrates the channel to the
/// 3-arg overload + reads Priority from the event.
///
/// The channel implementation lives at
/// `src/Terminal.Core/NvdaChannel.fs`. The marshal callback
/// signature `(string * string) -> unit` is what the
/// composition root in `src/Terminal.App/Program.fs` binds to
/// the WPF dispatcher hop; the tests call `create` with a
/// recording callback and assert against the recorded calls.

let private makeRecorder () : ResizeArray<string * string> * (string * string -> unit) =
    let calls = ResizeArray<string * string>()
    let recorder (msg, activityId) = calls.Add((msg, activityId))
    calls, recorder

let private buildEvent (semantic: SemanticCategory) (payload: string) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" payload

// ---- Semantic → ActivityId mapping -----------------------------

[<Fact>]
let ``StreamChunk routes to ActivityIds.output`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.StreamChunk "ls"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(1, calls.Count)
    Assert.Equal(("ls", ActivityIds.output), calls.[0])

[<Fact>]
let ``ParserError routes to ActivityIds.error`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.ParserError "boom"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("boom", ActivityIds.error), calls.[0])

[<Fact>]
let ``ErrorLine routes to ActivityIds.error`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.ErrorLine "npm ERR!"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("npm ERR!", ActivityIds.error), calls.[0])

[<Fact>]
let ``WarningLine routes to ActivityIds.error`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.WarningLine "deprecated API"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("deprecated API", ActivityIds.error), calls.[0])

[<Fact>]
let ``AltScreenEntered routes to ActivityIds.mode`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.AltScreenEntered "x"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("x", ActivityIds.mode), calls.[0])

[<Fact>]
let ``ModeBarrier routes to ActivityIds.mode`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.ModeBarrier "y"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("y", ActivityIds.mode), calls.[0])

[<Fact>]
let ``Custom Semantic routes to ActivityIds.output as the pre-claim default`` () =
    // 8a pre-claim mapping — `Custom of string` defaults to the
    // streaming-output activity ID so an early third-party
    // producer accidentally landing before its NVDA-validation
    // row would still announce on the streaming channel rather
    // than nothing.
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent (SemanticCategory.Custom "git-prompt-segment") "main *"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(("main *", ActivityIds.output), calls.[0])

// ---- Empty-payload skip ----------------------------------------

[<Fact>]
let ``RenderText with empty string does not invoke the marshal callback`` () =
    // Stage 7 drain skipped Announce on empty messages (mode
    // barriers carry "") — the channel preserves that contract.
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.ModeBarrier ""
    channel.Send event (RenderText "")
    Assert.Equal(0, calls.Count)

[<Fact>]
let ``RenderText2 with empty Precise register does not invoke the callback`` () =
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.StreamChunk "approx"
    channel.Send event (RenderText2 ("approx", ""))
    Assert.Equal(0, calls.Count)

[<Fact>]
let ``RenderText2 picks the Precise register for the marshal callback`` () =
    // 8a always renders Precise — no user-facing verbosity
    // hotkey ships until later stages.
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    channel.Send event (RenderText2 ("approx-form", "precise-form"))
    Assert.Equal(("precise-form", ActivityIds.output), calls.[0])

// ---- Earcon / Raw skip -----------------------------------------

[<Fact>]
let ``RenderEarcon does not invoke the NVDA marshal callback`` () =
    // Earcons go to the EarconChannel (8d), not NVDA.
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.BellRang ""
    channel.Send event (RenderEarcon "bell-ping")
    Assert.Equal(0, calls.Count)

[<Fact>]
let ``RenderRaw does not invoke the NVDA marshal callback`` () =
    // Raw payloads are channel-specific; the NVDA channel
    // ignores them.
    let calls, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    let event = buildEvent SemanticCategory.SelectionShown ""
    channel.Send event (RenderRaw (box "uia-listbox-metadata"))
    Assert.Equal(0, calls.Count)

// ---- Channel identity ------------------------------------------

[<Fact>]
let ``NvdaChannel.id is the stable string registered with the dispatcher`` () =
    Assert.Equal("nvda", NvdaChannel.id)

[<Fact>]
let ``create returns a Channel whose Id matches the module-level id`` () =
    let _, recorder = makeRecorder ()
    let channel = NvdaChannel.create recorder
    Assert.Equal(NvdaChannel.id, channel.Id)
