module PtySpeak.Tests.Unit.EarconProfileTests

open System
open Xunit
open Terminal.Core

/// Stage 8d.1 — pins the EarconProfile's Semantic → RenderEarcon
/// mapping for BellRang events and the contract that other
/// Semantic categories return an empty pair array (8d.2 will
/// extend the mapping with ErrorLine / WarningLine entries).
///
/// The profile implementation lives at
/// `src/Terminal.Core/EarconProfile.fs`. The pair-shape contract
/// is `(OutputEvent * ChannelDecision[])[]` per the post-8b
/// substrate; 8d's profile returns at most one pair.

let private buildEvent (semantic: SemanticCategory) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" ""

// ---- Profile identity ------------------------------------------

[<Fact>]
let ``EarconProfile.id is "earcon"`` () =
    Assert.Equal("earcon", EarconProfile.id)

[<Fact>]
let ``create returns a Profile whose Id matches the module-level id`` () =
    let profile = EarconProfile.create ()
    Assert.Equal(EarconProfile.id, profile.Id)

// ---- BellRang mapping ------------------------------------------

[<Fact>]
let ``BellRang Apply emits one pair with one ChannelDecision`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(1, decisions.Length)

[<Fact>]
let ``BellRang Apply pair targets the EarconChannel`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    Assert.Equal(EarconChannel.id, decisions.[0].Channel)

[<Fact>]
let ``BellRang Apply RenderEarcon carries the "bell-ping" earconId`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    match decisions.[0].Render with
    | RenderEarcon earconId -> Assert.Equal("bell-ping", earconId)
    | other -> Assert.Fail(sprintf "expected RenderEarcon, got %A" other)

[<Fact>]
let ``BellRang Apply effectiveEvent is the input event`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    let effectiveEvent, _ = pairs.[0]
    Assert.Equal(SemanticCategory.BellRang, effectiveEvent.Semantic)

// ---- Non-claimed cases return empty array ----------------------

[<Fact>]
let ``StreamChunk Apply returns empty pair array`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.StreamChunk
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``ParserError Apply returns empty pair array`` () =
    // 8d.1 doesn't claim ParserError. The future spec D.2
    // suppression PR may add an earcon for parser errors; for
    // now Apply skips them.
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.ParserError
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``ModeBarrier Apply returns empty pair array`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.ModeBarrier
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``AltScreenEntered Apply returns empty pair array`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.AltScreenEntered
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``ErrorLine Apply returns empty pair array (8d.2 will claim)`` () =
    // 8d.1 doesn't claim ErrorLine; 8d.2's color-detection
    // producer + earcon-palette extension will. Pinning the
    // 8d.1 behaviour so 8d.2's diff is reviewable.
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.ErrorLine
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``Custom Semantic Apply returns empty pair array`` () =
    let profile = EarconProfile.create ()
    let event = buildEvent (SemanticCategory.Custom "user-event")
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

// ---- Tick + Reset ----------------------------------------------

[<Fact>]
let ``Tick returns empty pair array (no time-driven flush)`` () =
    let profile = EarconProfile.create ()
    let pairs = profile.Tick DateTimeOffset.UtcNow
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``Reset is a no-op`` () =
    let profile = EarconProfile.create ()
    profile.Reset ()
    // Reaching this assertion without an exception is the
    // contract — Reset doesn't throw and there's no per-shell-
    // session state to verify in 8d.1.
    Assert.True(true)
