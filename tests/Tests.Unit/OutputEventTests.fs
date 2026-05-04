module PtySpeak.Tests.Unit.OutputEventTests

open Xunit
open Terminal.Core

/// Stage 8a — pins the v1 OutputEvent + Profile + Channel +
/// ChannelDecision schema. Future stages (8b–8f) extend the
/// substrate but must not break these contracts: any change in
/// `Version`, `Extensions` defaulting, or DU exhaustiveness is a
/// breaking change to the framework consumers (third-party
/// channels, FileLogger structured-log line shape, future TOML
/// schema).
///
/// The OutputEvent schema lives at
/// `src/Terminal.Core/OutputEventTypes.fs` and the
/// `OutputEvent.create` convenience constructor sits there too.
///
/// Test conventions follow `CoalescerTests.fs`: backtick-named
/// `[<Fact>]`s, single primary assertion per test, and a clear
/// `// Arrange / Act / Assert` rhythm where the test body is
/// non-trivial.

[<Fact>]
let ``OutputEvent.create populates v1 default Version`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.Equal(1, event.Version)

[<Fact>]
let ``OutputEvent.create populates empty Extensions map`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(Map.isEmpty event.Extensions)

[<Fact>]
let ``OutputEvent.create populates Verbosity Precise`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.Equal(VerbosityRegister.Precise, event.Verbosity)

[<Fact>]
let ``OutputEvent.create leaves SpatialHint as None`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(event.SpatialHint.IsNone)

[<Fact>]
let ``OutputEvent.create leaves RegionHint as None`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(event.RegionHint.IsNone)

[<Fact>]
let ``OutputEvent.create leaves StructuralContext as None`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(event.StructuralContext.IsNone)

[<Fact>]
let ``OutputEvent.create leaves Source.Shell as None`` () =
    // 8a does not populate Shell — 8f wires it.
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(event.Source.Shell.IsNone)

[<Fact>]
let ``OutputEvent.create leaves Source.CorrelationId as None`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "test-producer"
            "hello"
    Assert.True(event.Source.CorrelationId.IsNone)

[<Fact>]
let ``OutputEvent.create wires Producer through Source`` () =
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "drain"
            "hello"
    Assert.Equal("drain", event.Source.Producer)

[<Fact>]
let ``OutputEvent.create preserves Payload verbatim`` () =
    // The caller is responsible for AnnounceSanitiser.sanitise
    // before placing text in Payload — the constructor does NOT
    // re-sanitise (per spec B.2.4 producer responsibility 5).
    let event =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            "drain"
            "hello world"
    Assert.Equal("hello world", event.Payload)

// ---- Builder mapping (Stage 8b: ScreenNotification → OutputEvent) ----
//
// Stage 8a's translator ran AFTER coalescing; the test inputs were
// CoalescedNotifications carrying post-debounce text. Stage 8b's
// translator runs BEFORE coalescing, so the test inputs are now raw
// ScreenNotifications and the OutputEvent payloads are minimal — the
// Stream profile produces post-coalesce text inside its Apply / Tick
// closures (covered by StreamProfileTests.fs).

[<Fact>]
let ``fromScreenNotification maps RowsChanged to StreamChunk + Polite`` () =
    let event =
        OutputEventBuilder.fromScreenNotification (RowsChanged [ 0; 1 ])
    Assert.Equal(SemanticCategory.StreamChunk, event.Semantic)
    Assert.Equal(Priority.Polite, event.Priority)

[<Fact>]
let ``fromScreenNotification RowsChanged carries empty Payload`` () =
    // The Stream profile reads the screen at Apply time; the
    // OutputEvent itself doesn't ferry row content. Empty Payload
    // is the contract.
    let event =
        OutputEventBuilder.fromScreenNotification (RowsChanged [])
    Assert.Equal("", event.Payload)

[<Fact>]
let ``fromScreenNotification maps ParserError to ParserError + Background`` () =
    let event =
        OutputEventBuilder.fromScreenNotification (ParserError "boom")
    Assert.Equal(SemanticCategory.ParserError, event.Semantic)
    Assert.Equal(Priority.Background, event.Priority)

[<Fact>]
let ``fromScreenNotification preserves the Stage 7 ErrorPassthrough wrapping`` () =
    // The Stage 7 drain produced "Terminal parser error: <msg>";
    // the framework retrofit must keep the same wrapping so the
    // NVDA reading is identical post-retrofit.
    let event =
        OutputEventBuilder.fromScreenNotification (ParserError "decoder failed")
    Assert.Equal("Terminal parser error: decoder failed", event.Payload)

[<Fact>]
let ``fromScreenNotification ParserError sanitises the message`` () =
    // The translator sanitises through AnnounceSanitiser.sanitise
    // before wrapping (the producer-responsibility chokepoint per
    // spec B.2.4 step 5 + the PR-N entry-gate contract). The C0
    // BEL (0x07) in the input must be stripped from the Payload.
    let event =
        OutputEventBuilder.fromScreenNotification (ParserError "boom\x07!")
    Assert.Equal("Terminal parser error: boom!", event.Payload)

[<Fact>]
let ``fromScreenNotification maps ModeChanged AltScreen true to AltScreenEntered + Assertive`` () =
    let event =
        OutputEventBuilder.fromScreenNotification (
            ModeChanged (TerminalModeFlag.AltScreen, true))
    Assert.Equal(SemanticCategory.AltScreenEntered, event.Semantic)
    Assert.Equal(Priority.Assertive, event.Priority)

[<Fact>]
let ``fromScreenNotification maps ModeChanged AltScreen false to ModeBarrier + Assertive`` () =
    let event =
        OutputEventBuilder.fromScreenNotification (
            ModeChanged (TerminalModeFlag.AltScreen, false))
    Assert.Equal(SemanticCategory.ModeBarrier, event.Semantic)
    Assert.Equal(Priority.Assertive, event.Priority)

[<Fact>]
let ``fromScreenNotification maps non-AltScreen ModeChanged to ModeBarrier + Polite`` () =
    let event =
        OutputEventBuilder.fromScreenNotification (
            ModeChanged (TerminalModeFlag.BracketedPaste, true))
    Assert.Equal(SemanticCategory.ModeBarrier, event.Semantic)
    Assert.Equal(Priority.Polite, event.Priority)

[<Fact>]
let ``fromScreenNotification ModeChanged carries empty Payload`` () =
    // Stage 5 mode-barrier announcement is the empty string — see
    // ActivityIds.mode docstring at Types.fs:290-294.
    let event =
        OutputEventBuilder.fromScreenNotification (
            ModeChanged (TerminalModeFlag.AltScreen, true))
    Assert.Equal("", event.Payload)

[<Fact>]
let ``fromScreenNotification stamps translator as the producer`` () =
    // Stage 8a producer was "drain" (the post-coalesce drain task);
    // Stage 8b producer is "translator" (pre-coalesce
    // ScreenNotification → OutputEvent translation). The Stream
    // profile's Apply re-stamps post-coalesce events with producer
    // "stream" (covered in StreamProfileTests).
    let event =
        OutputEventBuilder.fromScreenNotification (RowsChanged [])
    Assert.Equal("translator", event.Source.Producer)
