module PtySpeak.Tests.Unit.OutputDispatcherTests

open Xunit
open Terminal.Core

/// Stage 8a — pins the OutputDispatcher's
/// `OutputEvent → Profile.Apply → ChannelDecision[] →
/// Channel.Send` end-to-end path. The dispatcher implementation
/// lives at `src/Terminal.Core/OutputDispatcher.fs`.
///
/// **Test isolation.** ChannelRegistry + ProfileRegistry hold
/// process-wide mutable state (single-thread-init pattern; see
/// the OutputDispatcher.fs concurrency contract). Each test
/// calls `clearForTests` in its setup and restores the empty
/// state on the way out so test runs don't leak registrations
/// across each other.

let private resetRegistries () : unit =
    OutputDispatcher.ChannelRegistry.clearForTests ()
    OutputDispatcher.ProfileRegistry.clearForTests ()

let private recordingChannel (channelId: ChannelId) : Channel * ResizeArray<OutputEvent * RenderInstruction> =
    let calls = ResizeArray<OutputEvent * RenderInstruction>()
    let channel =
        { Id = channelId
          Send = fun event render -> calls.Add((event, render)) }
    channel, calls

let private buildEvent (semantic: SemanticCategory) (payload: string) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" payload

// ---- ChannelRegistry -------------------------------------------

[<Fact>]
let ``ChannelRegistry register then lookup returns the registered Channel`` () =
    resetRegistries ()
    let channel, _ = recordingChannel "test-ch"
    OutputDispatcher.ChannelRegistry.register channel
    let found = OutputDispatcher.ChannelRegistry.lookup "test-ch"
    Assert.True(found.IsSome)
    Assert.Equal("test-ch", found.Value.Id)
    resetRegistries ()

[<Fact>]
let ``ChannelRegistry lookup of unregistered channel returns None`` () =
    resetRegistries ()
    let found = OutputDispatcher.ChannelRegistry.lookup "missing"
    Assert.True(found.IsNone)

[<Fact>]
let ``ChannelRegistry register is idempotent on the same Id`` () =
    resetRegistries ()
    let first, _ = recordingChannel "ch"
    let second, _ = recordingChannel "ch"
    OutputDispatcher.ChannelRegistry.register first
    OutputDispatcher.ChannelRegistry.register second
    let found = OutputDispatcher.ChannelRegistry.lookup "ch"
    Assert.True(found.IsSome)
    // Re-registering replaces; we can't compare `Channel` records
    // directly because they hold function values, but Id staying
    // "ch" + the lookup returning Some is the registered contract.
    Assert.Equal("ch", found.Value.Id)
    resetRegistries ()

// ---- ProfileRegistry -------------------------------------------

[<Fact>]
let ``ProfileRegistry register then lookup returns the registered Profile`` () =
    resetRegistries ()
    let profile = StreamProfile.create ()
    OutputDispatcher.ProfileRegistry.register profile
    let found = OutputDispatcher.ProfileRegistry.lookup StreamProfile.id
    Assert.True(found.IsSome)
    Assert.Equal(StreamProfile.id, found.Value.Id)
    resetRegistries ()

[<Fact>]
let ``ProfileRegistry getActiveProfileSet defaults to empty list`` () =
    resetRegistries ()
    let active = OutputDispatcher.ProfileRegistry.getActiveProfileSet ()
    Assert.Empty(active)

[<Fact>]
let ``ProfileRegistry setActiveProfileSet round-trips through getActiveProfileSet`` () =
    resetRegistries ()
    let profile = StreamProfile.create ()
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    let active = OutputDispatcher.ProfileRegistry.getActiveProfileSet ()
    Assert.Equal(1, active.Length)
    Assert.Equal(StreamProfile.id, active.[0].Id)
    resetRegistries ()

// ---- StreamProfile (8a pass-through stub) ----------------------

[<Fact>]
let ``StreamProfile.Apply emits exactly one ChannelDecision targeting nvda`` () =
    let profile = StreamProfile.create ()
    let event = buildEvent SemanticCategory.StreamChunk "ls"
    let decisions = profile.Apply event
    Assert.Equal(1, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)

[<Fact>]
let ``StreamProfile.Apply emits a RenderText with the event's Payload`` () =
    let profile = StreamProfile.create ()
    let event = buildEvent SemanticCategory.StreamChunk "ls -la"
    let decisions = profile.Apply event
    match decisions.[0].Render with
    | RenderText text -> Assert.Equal("ls -la", text)
    | other -> Assert.Fail(sprintf "expected RenderText, got %A" other)

[<Fact>]
let ``StreamProfile.Apply passes through ParserError event without suppression`` () =
    // 8a's pass-through Stream profile does NOT honour
    // Background-suppression yet (per the OutputEventTypes
    // Priority docstring + the spec D.2 reconciliation note in
    // OutputEventBuilder.fs). Background events still produce
    // ChannelDecisions; the channel decides what to do.
    let profile = StreamProfile.create ()
    let event =
        OutputEvent.create
            SemanticCategory.ParserError
            Priority.Background
            "test"
            "boom"
    let decisions = profile.Apply event
    Assert.Equal(1, decisions.Length)

[<Fact>]
let ``StreamProfile.Reset is a no-op in 8a`` () =
    let profile = StreamProfile.create ()
    profile.Reset ()
    // No state to verify; the contract is "Reset doesn't throw"
    // until 8b absorbs the Coalescer and Reset clears
    // per-shell-session state.
    Assert.True(true)

// ---- End-to-end dispatch ---------------------------------------

[<Fact>]
let ``dispatch routes a StreamChunk through StreamProfile to a registered NVDA test channel`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let profile = StreamProfile.create ()
    OutputDispatcher.ProfileRegistry.register profile
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    OutputDispatcher.dispatch event
    Assert.Equal(1, calls.Count)
    let recordedEvent, recordedRender = calls.[0]
    Assert.Equal(SemanticCategory.StreamChunk, recordedEvent.Semantic)
    match recordedRender with
    | RenderText text -> Assert.Equal("hello", text)
    | other -> Assert.Fail(sprintf "expected RenderText, got %A" other)
    resetRegistries ()

[<Fact>]
let ``dispatch silently drops decisions whose channel is not registered`` () =
    // Drop instead of throw: a profile may emit decisions for a
    // channel that's deferred / disabled / not yet shipped (e.g.
    // an EarconChannel reference before 8d lands). Silent drop
    // keeps the dispatcher from blowing up the drain task.
    resetRegistries ()
    let profile = StreamProfile.create ()
    OutputDispatcher.ProfileRegistry.register profile
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    // Note: NO channel registered. The Stream profile wants nvda;
    // lookup returns None; dispatch is a no-op.
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    OutputDispatcher.dispatch event
    // No exception, no panic. Reaching this assertion is the test.
    Assert.True(true)
    resetRegistries ()

[<Fact>]
let ``dispatch with no active profile set is a no-op`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    // No profiles registered or active.
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    OutputDispatcher.dispatch event
    Assert.Equal(0, calls.Count)
    resetRegistries ()

[<Fact>]
let ``dispatch fans out across multiple profiles producing combined ChannelDecisions`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let stream1 = StreamProfile.create ()
    let stream2 = StreamProfile.create ()
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ stream1; stream2 ]
    let event = buildEvent SemanticCategory.StreamChunk "x"
    OutputDispatcher.dispatch event
    // Two profiles each emit one decision targeting the same
    // channel. The dispatcher invokes Send twice.
    Assert.Equal(2, calls.Count)
    resetRegistries ()
