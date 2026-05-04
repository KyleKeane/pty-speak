module PtySpeak.Tests.Unit.EarconChannelTests

open Xunit
open Terminal.Core

/// Stage 8d.1 — pins the EarconChannel's RenderEarcon dispatch
/// behaviour, mute state, and the contract that non-Earcon
/// RenderInstruction cases (RenderText / RenderText2 /
/// RenderRaw) are skipped.
///
/// The channel implementation lives at
/// `src/Terminal.Core/EarconChannel.fs`. The play callback
/// signature `string -> unit` is what the composition root in
/// `src/Terminal.App/Program.fs` binds to
/// `Terminal.Audio.EarconPlayer.play
/// EarconPalette.defaultPalette`; tests pass a recording
/// callback and assert on the recorded earcon-id strings.

let private makeRecorder () : ResizeArray<string> * (string -> unit) =
    let calls = ResizeArray<string>()
    let recorder (earconId: string) = calls.Add(earconId)
    calls, recorder

let private buildEvent (semantic: SemanticCategory) (payload: string) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" payload

// ---- Channel identity ------------------------------------------

[<Fact>]
let ``EarconChannel.id is "earcon"`` () =
    Assert.Equal("earcon", EarconChannel.id)

[<Fact>]
let ``create returns a Channel whose Id matches the module-level id`` () =
    let _, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    Assert.Equal(EarconChannel.id, channel.Id)

// ---- RenderEarcon dispatch -------------------------------------

[<Fact>]
let ``RenderEarcon invokes the play callback with the earcon ID`` () =
    EarconChannel.clearForTests ()
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.BellRang ""
    channel.Send event (RenderEarcon "bell-ping")
    Assert.Equal(1, calls.Count)
    Assert.Equal("bell-ping", calls.[0])

[<Fact>]
let ``RenderEarcon multiple distinct ids each invoke play once`` () =
    EarconChannel.clearForTests ()
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.BellRang ""
    channel.Send event (RenderEarcon "bell-ping")
    channel.Send event (RenderEarcon "error-tone")
    Assert.Equal(2, calls.Count)
    Assert.Equal("bell-ping", calls.[0])
    Assert.Equal("error-tone", calls.[1])

// ---- Non-Earcon Render skip ------------------------------------

[<Fact>]
let ``RenderText does NOT invoke the play callback`` () =
    EarconChannel.clearForTests ()
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    channel.Send event (RenderText "hello")
    Assert.Equal(0, calls.Count)

[<Fact>]
let ``RenderText2 does NOT invoke the play callback`` () =
    EarconChannel.clearForTests ()
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    channel.Send event (RenderText2 ("approx", "precise"))
    Assert.Equal(0, calls.Count)

[<Fact>]
let ``RenderRaw does NOT invoke the play callback`` () =
    EarconChannel.clearForTests ()
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.SelectionShown ""
    channel.Send event (RenderRaw ("opaque" :> obj))
    Assert.Equal(0, calls.Count)

// ---- Mute state ------------------------------------------------

[<Fact>]
let ``initial mute state is false`` () =
    EarconChannel.clearForTests ()
    Assert.False(EarconChannel.isMuted ())

[<Fact>]
let ``toggle flips state from false to true`` () =
    EarconChannel.clearForTests ()
    let result = EarconChannel.toggle ()
    Assert.True(result)
    Assert.True(EarconChannel.isMuted ())

[<Fact>]
let ``toggle from true returns to false`` () =
    EarconChannel.clearForTests ()
    let _ = EarconChannel.toggle () // → true
    let result = EarconChannel.toggle () // → false
    Assert.False(result)
    Assert.False(EarconChannel.isMuted ())

[<Fact>]
let ``RenderEarcon does NOT invoke play when muted`` () =
    EarconChannel.clearForTests ()
    let _ = EarconChannel.toggle () // mute
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.BellRang ""
    channel.Send event (RenderEarcon "bell-ping")
    Assert.Equal(0, calls.Count)
    EarconChannel.clearForTests ()

[<Fact>]
let ``RenderEarcon resumes invoking play after un-mute`` () =
    EarconChannel.clearForTests ()
    let _ = EarconChannel.toggle () // mute
    let _ = EarconChannel.toggle () // un-mute
    let calls, recorder = makeRecorder ()
    let channel = EarconChannel.create recorder
    let event = buildEvent SemanticCategory.BellRang ""
    channel.Send event (RenderEarcon "bell-ping")
    Assert.Equal(1, calls.Count)

[<Fact>]
let ``clearForTests resets mute to false`` () =
    EarconChannel.clearForTests ()
    let _ = EarconChannel.toggle () // mute
    EarconChannel.clearForTests ()
    Assert.False(EarconChannel.isMuted ())
