module PtySpeak.Tests.Unit.SelectionProfileTests

open System
open Xunit
open Terminal.Core

/// Stage 8e-A part 2 (Cycle 29b) — pins the SelectionProfile's
/// Apply contract:
///   * id is "selection".
///   * SelectionShown / SelectionItem / SelectionDismissed each
///     emit one pair with THREE ChannelDecisions: NVDA RenderText
///     (Option A bridge so NVDA continues reading text during the
///     37a interim window), FileLogger RenderText (audit trail),
///     and NVDA RenderRaw carrying the SelectionRawPayload UIA-
///     free metadata for Cycle 37b's Terminal.Accessibility peer
///     to consume.
///   * The first two decisions carry `RenderText` payloads
///     constructed from `Extensions` data (empty-payload trick
///     mirrors 8d.2's EarconProfile pattern).
///   * The third decision carries a `RenderRaw` payload of type
///     `SelectionRawPayload` per Cycle 37a contract.
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

// Cycle 37a — extract the SelectionRawPayload from a
// ChannelDecision; fail loudly if it isn't a `RenderRaw`
// carrying a `SelectionRawPayload`. The fallback returns a
// sentinel record so the function's return type satisfies F# 9
// nullness — `Assert.Fail` throws, so the sentinel is never
// observed by callers.
let private renderRawSentinel : SelectionRawPayload =
    { Kind = "<test-fail>"
      ItemCount = 0
      SelectedIndex = -1
      ItemIndex = -1
      AllItems = [||]
      ItemText = "" }

let private renderRaw (decision: ChannelDecision) : SelectionRawPayload =
    match decision.Render with
    | RenderRaw payload ->
        match payload with
        | :? SelectionRawPayload as p -> p
        | _ ->
            // %A formatter handles `string | null` (F# 9
            // nullness on Type.FullName) without coercion.
            Assert.Fail(
                sprintf
                    "expected SelectionRawPayload, got %A"
                    (payload.GetType().FullName))
            renderRawSentinel
    | other ->
        Assert.Fail(sprintf "expected RenderRaw, got %A" other)
        renderRawSentinel

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
let ``SelectionShown Apply emits one pair with three ChannelDecisions (NVDA text + FileLogger text + NVDA raw, Cycle 37a)`` () =
    let profile = SelectionProfile.create ()
    let event =
        selectionShownEvent [| "Edit"; "Yes"; "Always"; "No" |] 1
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(3, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal(NvdaChannel.id, decisions.[2].Channel)

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

[<Fact>]
let ``SelectionShown emits RenderRaw with Kind="shown" and AllItems populated (Cycle 37a)`` () =
    let profile = SelectionProfile.create ()
    let event =
        selectionShownEvent [| "Edit"; "Yes"; "Always"; "No" |] 1
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let payload = renderRaw decisions.[2]
    Assert.Equal("shown", payload.Kind)
    Assert.Equal(4, payload.ItemCount)
    Assert.Equal(1, payload.SelectedIndex)
    Assert.Equal(-1, payload.ItemIndex)
    Assert.Equal<string[]>(
        [| "Edit"; "Yes"; "Always"; "No" |],
        payload.AllItems)
    Assert.Equal("", payload.ItemText)

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
let ``SelectionItem emits NVDA text + FileLogger text + NVDA raw triple (Cycle 37a)`` () =
    let profile = SelectionProfile.create ()
    let event = selectionItemEvent "Yes" 1 1 4
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(3, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal(NvdaChannel.id, decisions.[2].Channel)

[<Fact>]
let ``SelectionItem emits RenderRaw with Kind="item" and ItemText/ItemIndex populated (Cycle 37a)`` () =
    let profile = SelectionProfile.create ()
    // selectedIdx = 1, itemIdx = 0 → "Edit", not the selected item.
    let event = selectionItemEvent "Edit" 0 1 4
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    let payload = renderRaw decisions.[2]
    Assert.Equal("item", payload.Kind)
    Assert.Equal(4, payload.ItemCount)
    Assert.Equal(1, payload.SelectedIndex)
    Assert.Equal(0, payload.ItemIndex)
    Assert.Equal<string[]>([||], payload.AllItems)
    Assert.Equal("Edit", payload.ItemText)

// ---------------------------------------------------------------------
// SelectionDismissed rendering
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionDismissed renders literal "selection dismissed" + RenderRaw with Kind="dismissed" (Cycle 37a)`` () =
    let profile = SelectionProfile.create ()
    let event = selectionDismissedEvent ()
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    Assert.Equal(3, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal(NvdaChannel.id, decisions.[2].Channel)
    Assert.Equal("selection dismissed", renderText decisions.[0])
    Assert.Equal("selection dismissed", renderText decisions.[1])
    let payload = renderRaw decisions.[2]
    Assert.Equal("dismissed", payload.Kind)
    Assert.Equal(0, payload.ItemCount)
    Assert.Equal(-1, payload.SelectedIndex)
    Assert.Equal(-1, payload.ItemIndex)
    Assert.Equal<string[]>([||], payload.AllItems)
    Assert.Equal("", payload.ItemText)

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
