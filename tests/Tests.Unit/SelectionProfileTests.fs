module PtySpeak.Tests.Unit.SelectionProfileTests

open System
open Xunit
open Terminal.Core

/// Stage 8e-A part 2 (Cycle 29b) — pins the SelectionProfile's
/// Apply contract:
///   * id is "selection".
///   * SelectionShown / SelectionItem / SelectionDismissed each
///     emit one pair with two ChannelDecisions (NVDA + FileLogger).
///   * Decisions carry `RenderText` payloads constructed from
///     `Extensions` data (empty-payload trick mirrors 8d.2's
///     EarconProfile pattern).
///   * Foreign Semantic categories return `[||]` (purely
///     additive observer).
///   * Tick + Reset are no-ops.

// F# 9 strict-nullness coercion. `box x` returns `obj | null`,
// but `OutputEvent.Extensions` is `Map<string, obj>` (non-
// nullable). Mirrors the production `SelectionDetector.boxNN`
// helper. Cycle 29a CI surfaced FS3261 from the same pattern;
// avoid the trap.
let private boxNN (value: 'T) : obj = nonNull (box value)

// ---------------------------------------------------------------------
// Fixture builders
// ---------------------------------------------------------------------

let private selectionShownEvent
        (allItems: string[])
        (selectedIdx: int)
        : OutputEvent =
    let baseEvt =
        OutputEvent.create
            SemanticCategory.SelectionShown
            Priority.Assertive
            "selection-detector"
            ""
    let extensions =
        Map.ofList
            [ SelectionExtensions.ItemCount, boxNN allItems.Length
              SelectionExtensions.SelectedIndex, boxNN selectedIdx
              SelectionExtensions.AllItems, boxNN allItems
              SelectionExtensions.TopRow, boxNN 18
              SelectionExtensions.BottomRow, boxNN (18 + allItems.Length - 1)
              SelectionExtensions.Source, boxNN "HeuristicSGR" ]
    { baseEvt with Extensions = extensions }

let private selectionItemEvent
        (itemText: string)
        (itemIdx: int)
        (selectedIdx: int)
        (itemCount: int)
        : OutputEvent =
    let baseEvt =
        OutputEvent.create
            SemanticCategory.SelectionItem
            Priority.Polite
            "selection-detector"
            ""
    let extensions =
        Map.ofList
            [ SelectionExtensions.ItemCount, boxNN itemCount
              SelectionExtensions.SelectedIndex, boxNN selectedIdx
              SelectionExtensions.ItemIndex, boxNN itemIdx
              SelectionExtensions.ItemText, boxNN itemText
              SelectionExtensions.Source, boxNN "HeuristicSGR" ]
    { baseEvt with Extensions = extensions }

let private selectionDismissedEvent () : OutputEvent =
    OutputEvent.create
        SemanticCategory.SelectionDismissed
        Priority.Assertive
        "selection-detector"
        ""

let private otherEvent (semantic: SemanticCategory) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" ""

// Extract the rendered text from a ChannelDecision; fail loudly
// if it isn't a `RenderText`.
let private renderText (decision: ChannelDecision) : string =
    match decision.Render with
    | RenderText t -> t
    | other ->
        Assert.Fail(sprintf "expected RenderText, got %A" other)
        ""

// ---------------------------------------------------------------------
// Profile identity
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionProfile.id is "selection"`` () =
    Assert.Equal("selection", SelectionProfile.id)

[<Fact>]
let ``create returns a Profile whose Id matches the module-level id`` () =
    let profile = SelectionProfile.create ()
    Assert.Equal(SelectionProfile.id, profile.Id)

// ---------------------------------------------------------------------
// SelectionShown rendering
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionShown Apply emits one pair with two ChannelDecisions (NVDA + FileLogger)`` () =
    let profile = SelectionProfile.create ()
    let event =
        selectionShownEvent [| "Edit"; "Yes"; "Always"; "No" |] 1
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)

[<Fact>]
let ``SelectionShown rendered text formats list with selected item highlighted`` () =
    let profile = SelectionProfile.create ()
    let event =
        selectionShownEvent [| "Edit"; "Yes"; "Always"; "No" |] 1
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let text = renderText decisions.[0]
    Assert.Equal(
        "selection prompt: Edit, Yes, Always, No (selected: Yes)",
        text)

[<Fact>]
let ``SelectionShown falls back to count summary when AllItems missing`` () =
    let profile = SelectionProfile.create ()
    let baseEvt =
        OutputEvent.create
            SemanticCategory.SelectionShown
            Priority.Assertive
            "selection-detector"
            ""
    let extensions =
        Map.ofList [ SelectionExtensions.ItemCount, boxNN 4 ]
    let event = { baseEvt with Extensions = extensions }
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let text = renderText decisions.[0]
    Assert.Equal("selection prompt, 4 items", text)

// ---------------------------------------------------------------------
// SelectionItem rendering
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionItem (this item == selected) prefixes "selected: "`` () =
    let profile = SelectionProfile.create ()
    // selectedIdx = 1, itemIdx = 1 → THIS item IS the selected one.
    let event = selectionItemEvent "Yes" 1 1 4
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let text = renderText decisions.[0]
    Assert.Equal("selected: Yes, 2 of 4", text)

[<Fact>]
let ``SelectionItem (this item != selected) renders "%s, %d of %d"`` () =
    let profile = SelectionProfile.create ()
    // selectedIdx = 1, itemIdx = 0 → this item is NOT selected.
    let event = selectionItemEvent "Edit" 0 1 4
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let text = renderText decisions.[0]
    Assert.Equal("Edit, 1 of 4", text)

[<Fact>]
let ``SelectionItem emits NVDA + FileLogger pair`` () =
    let profile = SelectionProfile.create ()
    let event = selectionItemEvent "Yes" 1 1 4
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)

// ---------------------------------------------------------------------
// SelectionDismissed rendering
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionDismissed renders literal "selection dismissed"`` () =
    let profile = SelectionProfile.create ()
    let event = selectionDismissedEvent ()
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal("selection dismissed", renderText decisions.[0])
    Assert.Equal("selection dismissed", renderText decisions.[1])

// ---------------------------------------------------------------------
// Foreign Semantic categories — purely additive
// ---------------------------------------------------------------------

[<Fact>]
let ``StreamChunk Apply returns empty pair array`` () =
    let profile = SelectionProfile.create ()
    let event = otherEvent SemanticCategory.StreamChunk
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``BellRang Apply returns empty pair array`` () =
    let profile = SelectionProfile.create ()
    let event = otherEvent SemanticCategory.BellRang
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``ParserError Apply returns empty pair array`` () =
    let profile = SelectionProfile.create ()
    let event = otherEvent SemanticCategory.ParserError
    let pairs = profile.Apply event
    Assert.Equal(0, pairs.Length)

// ---------------------------------------------------------------------
// Tick + Reset
// ---------------------------------------------------------------------

[<Fact>]
let ``Tick returns empty pair array (no time-driven flush)`` () =
    let profile = SelectionProfile.create ()
    let pairs = profile.Tick DateTimeOffset.UtcNow
    Assert.Equal(0, pairs.Length)

[<Fact>]
let ``Reset is a no-op`` () =
    let profile = SelectionProfile.create ()
    profile.Reset ()
    Assert.True(true)
