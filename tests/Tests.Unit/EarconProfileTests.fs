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
let ``StreamChunk Apply with no Extensions returns empty pair array`` () =
    // Plain streaming output (no SGR colour) — no earcon. 8d.2's
    // colour-detection only fires when the StreamProfile stamps
    // `Extensions["dominantColor"]` on the event.
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
let ``ErrorLine Apply returns empty pair array`` () =
    // 8d.2 chose to emit colour earcons via StreamChunk +
    // Extensions["dominantColor"] metadata rather than via a
    // separate ErrorLine Semantic. ErrorLine remains a reserved
    // category in OutputEventTypes.fs; no producer emits it in
    // 8d.2; the EarconProfile correspondingly returns empty.
    // A future PR may adopt the spec D.2 ErrorLine contract;
    // this assertion will need updating then.
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

// ---- Stage 8d.2 — colour-detected StreamChunk → earcon ---------

let private buildStreamChunkWithColor (color: string) : OutputEvent =
    // The StreamProfile stamps `Extensions["dominantColor"]` on
    // its synthesised StreamChunk events when SGR-coloured rows
    // dominate the post-coalesce snapshot. Tests build the same
    // shape directly to exercise the EarconProfile's reading.
    // `:> obj` upcast preserves non-null for `Map<string, obj>`;
    // F# 9 `box` returns `obj | null` (FS3261 regression risk).
    let baseEvent = buildEvent SemanticCategory.StreamChunk
    { baseEvent with
        Extensions = Map.ofList [ "dominantColor", (color :> obj) ] }

let private buildStreamChunkWithNonStringExtensionValue () : OutputEvent =
    // Tests the defensive `:? string` pattern in
    // `EarconProfile.readDominantColor`: a non-string value
    // should fall through to None rather than crash. Use a
    // bare `obj()` instance (non-null reference type) — avoids
    // F# 9's `box` returning `obj | null` (FS3261 risk).
    let baseEvent = buildEvent SemanticCategory.StreamChunk
    let nonStringValue : obj = obj ()
    { baseEvent with
        Extensions = Map.ofList [ "dominantColor", nonStringValue ] }

[<Fact>]
let ``StreamChunk with dominantColor=red emits one decision targeting EarconChannel`` () =
    let profile = EarconProfile.create ()
    let event = buildStreamChunkWithColor "red"
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(1, decisions.Length)
    Assert.Equal(EarconChannel.id, decisions.[0].Channel)

[<Fact>]
let ``StreamChunk with dominantColor=red emits RenderEarcon error-tone`` () =
    let profile = EarconProfile.create ()
    let event = buildStreamChunkWithColor "red"
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    match decisions.[0].Render with
    | RenderEarcon earconId -> Assert.Equal("error-tone", earconId)
    | other -> Assert.Fail(sprintf "expected RenderEarcon, got %A" other)

[<Fact>]
let ``StreamChunk with dominantColor=yellow emits RenderEarcon warning-tone`` () =
    let profile = EarconProfile.create ()
    let event = buildStreamChunkWithColor "yellow"
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    match decisions.[0].Render with
    | RenderEarcon earconId -> Assert.Equal("warning-tone", earconId)
    | other -> Assert.Fail(sprintf "expected RenderEarcon, got %A" other)

[<Fact>]
let ``StreamChunk with dominantColor=green returns empty pair array`` () =
    // Only red + yellow trigger earcons. Other colour values
    // are silently ignored — a future palette could add
    // success-tone for green, but 8d.2 doesn't.
    let profile = EarconProfile.create ()
    let event = buildStreamChunkWithColor "green"
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``StreamChunk with non-string dominantColor returns empty (defensive)`` () =
    // A future producer might mistakenly box the wrong type
    // into Extensions. The EarconProfile's `readDominantColor`
    // pattern-matches on `:? string`; non-string values fall
    // through to None, and the profile emits no decisions —
    // never crashes the dispatcher.
    let profile = EarconProfile.create ()
    let event = buildStreamChunkWithNonStringExtensionValue ()
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``StreamChunk with empty Extensions returns empty pair array`` () =
    // Already covered by the ``StreamChunk Apply with no
    // Extensions returns empty pair array`` test above; this
    // case is the explicit complement to the colour-bearing
    // tests above. Pinning the symmetry.
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.StreamChunk
    Assert.Equal(0, event.Extensions.Count)
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``BellRang Apply continues to emit bell-ping (8d.1 behaviour preserved)`` () =
    // Regression pin: 8d.2's StreamChunk colour mapping doesn't
    // accidentally swallow the BellRang case. The BellRang
    // mapping comes first in the match expression; this test
    // re-asserts what `BellRang Apply RenderEarcon carries the
    // "bell-ping" earconId` already covers, framed as a
    // 8d.2-stage regression check.
    let profile = EarconProfile.create ()
    let event = buildEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    match decisions.[0].Render with
    | RenderEarcon earconId -> Assert.Equal("bell-ping", earconId)
    | other -> Assert.Fail(sprintf "expected RenderEarcon, got %A" other)
