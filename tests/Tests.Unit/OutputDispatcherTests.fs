module PtySpeak.Tests.Unit.OutputDispatcherTests

open System
open Xunit
open Terminal.Core

/// Stage 8a/8b — pins the OutputDispatcher's
/// `OutputEvent → Profile.Apply → (effectiveEvent,
/// ChannelDecision[])[] → Channel.Send` end-to-end path. The
/// dispatcher implementation lives at
/// `src/Terminal.Core/OutputDispatcher.fs`.
///
/// **Test isolation.** ChannelRegistry + ProfileRegistry hold
/// process-wide mutable state (single-thread-init pattern; see
/// the OutputDispatcher.fs concurrency contract). Each test
/// calls `clearForTests` in its setup and restores the empty
/// state on the way out so test runs don't leak registrations
/// across each other.
///
/// **Stage 8b changes (2026-05-04).** Profile.Apply now returns
/// `(OutputEvent * ChannelDecision[])[]` — pairs of (effective
/// event, decisions). Profile.Tick is new (same shape).
/// dispatchTick is the dispatcher entry point for time-driven
/// flush. StreamProfile.create takes Parameters + Screen.
///
/// Most StreamProfile-specific behaviour pins live in
/// `StreamProfileTests.fs`; this file uses a synthetic
/// pass-through profile for testing the dispatcher itself.

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

/// Synthetic pass-through profile for dispatcher tests. Apply
/// returns one pair containing the input event + a single
/// RenderText decision targeting the supplied channel ID. Tick
/// is a no-op. The pattern mirrors what the 8a StreamProfile did
/// before 8b's Coalescer absorption.
let private passthroughProfile (profileId: ProfileId) (channelId: ChannelId) : Profile =
    { Id = profileId
      Apply =
        fun event ->
            [|
                event,
                [| { Channel = channelId; Render = RenderText event.Payload } |]
            |]
      Tick = fun _ -> [||]
      Reset = fun () -> () }

/// Synthetic Tick-emitting profile. Apply is a no-op; Tick
/// returns one pair containing a synthesised event with the
/// supplied payload + a single RenderText decision. Used to
/// pin dispatchTick's routing.
let private tickEmittingProfile
        (profileId: ProfileId)
        (channelId: ChannelId)
        (payload: string)
        : Profile =
    { Id = profileId
      Apply = fun _ -> [||]
      Tick =
        fun _ ->
            let event = buildEvent SemanticCategory.StreamChunk payload
            [|
                event,
                [| { Channel = channelId; Render = RenderText payload } |]
            |]
      Reset = fun () -> () }

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
    Assert.Equal("ch", found.Value.Id)
    resetRegistries ()

// ---- ProfileRegistry -------------------------------------------

[<Fact>]
let ``ProfileRegistry register then lookup returns the registered Profile`` () =
    resetRegistries ()
    let profile = passthroughProfile "test-prof" NvdaChannel.id
    OutputDispatcher.ProfileRegistry.register profile
    let found = OutputDispatcher.ProfileRegistry.lookup "test-prof"
    Assert.True(found.IsSome)
    Assert.Equal("test-prof", found.Value.Id)
    resetRegistries ()

[<Fact>]
let ``ProfileRegistry getActiveProfileSet defaults to empty list`` () =
    resetRegistries ()
    let active = OutputDispatcher.ProfileRegistry.getActiveProfileSet ()
    Assert.Empty(active)

[<Fact>]
let ``ProfileRegistry setActiveProfileSet round-trips through getActiveProfileSet`` () =
    resetRegistries ()
    let profile = passthroughProfile "test-prof" NvdaChannel.id
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    let active = OutputDispatcher.ProfileRegistry.getActiveProfileSet ()
    Assert.Equal(1, active.Length)
    Assert.Equal("test-prof", active.[0].Id)
    resetRegistries ()

// ---- End-to-end dispatch (Apply path) --------------------------

[<Fact>]
let ``dispatch routes a StreamChunk through a passthrough profile to a registered NVDA test channel`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let profile = passthroughProfile "test" NvdaChannel.id
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
    // keeps the dispatcher from blowing up the pump task.
    resetRegistries ()
    let profile = passthroughProfile "test" NvdaChannel.id
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    // Note: NO channel registered. The profile wants nvda;
    // lookup returns None; dispatch is a no-op.
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    OutputDispatcher.dispatch event
    Assert.True(true)
    resetRegistries ()

[<Fact>]
let ``dispatch with no active profile set is a no-op`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    OutputDispatcher.dispatch event
    Assert.Equal(0, calls.Count)
    resetRegistries ()

[<Fact>]
let ``dispatch fans out across multiple profiles producing combined ChannelDecisions`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let p1 = passthroughProfile "p1" NvdaChannel.id
    let p2 = passthroughProfile "p2" NvdaChannel.id
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ p1; p2 ]
    let event = buildEvent SemanticCategory.StreamChunk "x"
    OutputDispatcher.dispatch event
    Assert.Equal(2, calls.Count)
    resetRegistries ()

[<Fact>]
let ``dispatch with multi-pair Apply routes each pair's effectiveEvent to channel.Send`` () =
    // A profile may return multiple (effectiveEvent,
    // decisions) pairs from Apply — e.g., the Stream profile
    // when a mode-change forces a flush of pending stream
    // content. Each pair carries its own effectiveEvent so
    // NvdaChannel's Semantic → ActivityId mapping picks the
    // right ID for that pair.
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let multiPairProfile : Profile =
        { Id = "multi"
          Apply =
            fun event ->
                let streamEvent =
                    OutputEvent.create
                        SemanticCategory.StreamChunk
                        Priority.Polite
                        "test"
                        "flushed"
                [|
                    streamEvent,
                    [| { Channel = NvdaChannel.id
                         Render = RenderText "flushed" } |]
                    event,
                    [| { Channel = NvdaChannel.id
                         Render = RenderText "barrier" } |]
                |]
          Tick = fun _ -> [||]
          Reset = fun () -> () }
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ multiPairProfile ]
    let event = buildEvent SemanticCategory.AltScreenEntered ""
    OutputDispatcher.dispatch event
    Assert.Equal(2, calls.Count)
    let event0, _ = calls.[0]
    let event1, _ = calls.[1]
    Assert.Equal(SemanticCategory.StreamChunk, event0.Semantic)
    Assert.Equal(SemanticCategory.AltScreenEntered, event1.Semantic)
    resetRegistries ()

// ---- End-to-end dispatchTick (Tick path) -----------------------

[<Fact>]
let ``dispatchTick is a no-op when no profiles are active`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    OutputDispatcher.dispatchTick DateTimeOffset.UtcNow
    Assert.Equal(0, calls.Count)
    resetRegistries ()

[<Fact>]
let ``dispatchTick is a no-op for profiles whose Tick returns empty array`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let profile = passthroughProfile "p" NvdaChannel.id
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    OutputDispatcher.dispatchTick DateTimeOffset.UtcNow
    Assert.Equal(0, calls.Count)
    resetRegistries ()

[<Fact>]
let ``dispatchTick routes Tick decisions through ChannelRegistry`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let profile = tickEmittingProfile "tick-prof" NvdaChannel.id "tick-payload"
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ profile ]
    OutputDispatcher.dispatchTick DateTimeOffset.UtcNow
    Assert.Equal(1, calls.Count)
    let _, render = calls.[0]
    match render with
    | RenderText text -> Assert.Equal("tick-payload", text)
    | other -> Assert.Fail(sprintf "expected RenderText, got %A" other)
    resetRegistries ()

[<Fact>]
let ``dispatchTick fans out across multiple Tick-emitting profiles`` () =
    resetRegistries ()
    let nvda, calls = recordingChannel NvdaChannel.id
    OutputDispatcher.ChannelRegistry.register nvda
    let p1 = tickEmittingProfile "p1" NvdaChannel.id "a"
    let p2 = tickEmittingProfile "p2" NvdaChannel.id "b"
    OutputDispatcher.ProfileRegistry.setActiveProfileSet [ p1; p2 ]
    OutputDispatcher.dispatchTick DateTimeOffset.UtcNow
    Assert.Equal(2, calls.Count)
    resetRegistries ()
