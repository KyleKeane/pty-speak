module PtySpeak.Tests.Unit.ContentHistoryTextRangeTests

open System
open System.Text
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Xunit
open Terminal.Core
open Terminal.Accessibility

// ---------------------------------------------------------------------
// Cycle 46 PR-B — ContentHistoryTextRange / ContentHistoryTextProvider.
// ---------------------------------------------------------------------
//
// Pin the offset arithmetic, line / word boundary walks, and the
// UIA `ITextRangeProvider` contract for the new substrate-swapped
// Text-pattern providers. PR-B has no live-pipeline wiring; these
// tests are the entire pre-NVDA-matrix safety net.
//
// Coverage targets (per ADR 0002 PR-B section):
//   * Materialiser: null-history + empty-history + content-history
//   * Range Clone / Compare / CompareEndpoints
//   * GetText: full / partial / maxLength truncation
//   * ExpandToEnclosingUnit: Character / Word / Line / Document /
//     Paragraph (→Line) / Page (→Line) / Format (→Line)
//   * Move(Character, ±N) clamping at document bounds
//   * Move(Line, ±N) clamping at top / bottom
//   * Move(Word, ±N) skipping separators
//   * MoveEndpointByUnit moving one endpoint, including endpoint-
//     crossing (range collapses)
//   * MoveEndpointByRange
//   * Provider DocumentRange covers full materialised tail
//   * Provider DocumentRange resolves to empty range when history
//     is null

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

let private t0 = DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc)

let private freshHistory () : ContentHistory.T =
    ContentHistory.create ContentHistory.defaultParameters

/// Typed null binding for tests that need to feed a null
/// history to the provider. Using an explicit `T | null`
/// binding sidesteps any F# 9 null-literal type-inference
/// ambiguity at the call site.
let private nullHistory : ContentHistory.T | null = null

let private feedText (state: ContentHistory.T) (text: string) : unit =
    for ch in text do
        let ev =
            if ch = '\n' then Execute 0x0Auy
            else Print (Rune ch)
        ContentHistory.appendFromEvent state t0 ev |> ignore

let private rangeFor
        (text: string)
        (startOff: int)
        (endOff: int)
        : ITextRangeProvider =
    ContentHistoryTextRange(text, startOff, endOff)
    :> ITextRangeProvider

let private innerRange (r: ITextRangeProvider) : ContentHistoryTextRange =
    r :?> ContentHistoryTextRange

// ---------------------------------------------------------------------
// Materialiser
// ---------------------------------------------------------------------

[<Fact>]
let ``materialise returns empty string when history is null`` () =
    let result = ContentHistoryMaterialiser.materialise null
    Assert.Equal("", result)

[<Fact>]
let ``materialise returns empty string for fresh history`` () =
    let state = freshHistory ()
    let result = ContentHistoryMaterialiser.materialise state
    Assert.Equal("", result)

[<Fact>]
let ``materialise returns active span text after Print events`` () =
    let state = freshHistory ()
    feedText state "abc"
    let result = ContentHistoryMaterialiser.materialise state
    Assert.Equal("abc", result)

[<Fact>]
let ``materialise reconstructs across newlines`` () =
    let state = freshHistory ()
    feedText state "line1\nline2\nline3"
    let result = ContentHistoryMaterialiser.materialise state
    Assert.Equal("line1\nline2\nline3", result)

[<Fact>]
let ``materialise renders Marker entries as navigable boundary lines`` () =
    // Cycle 47 follow-up: the UIA Text-pattern view inserts
    // semantic-boundary markers as separate lines so NVDA's
    // review cursor can navigate between commands. This test
    // is the channel-side analogue of the substrate-side
    // ContentHistoryTests `tailTextWithMarkers renders ...`
    // tests; pinning it here ensures the materialiser is
    // calling the markers-aware tail and not silently
    // regressing to plain tailText.
    let state = freshHistory ()
    feedText state "echo hi\n"
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart t0 None
    |> ignore
    feedText state "hi\n"
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.CommandFinished t0 None
    |> ignore
    let result = ContentHistoryMaterialiser.materialise state
    Assert.Contains("--- output begins ---", result)
    Assert.Contains("--- output ends ---", result)
    Assert.Contains("echo hi", result)
    Assert.Contains("hi", result)

// ---------------------------------------------------------------------
// Clone / Compare / CompareEndpoints
// ---------------------------------------------------------------------

[<Fact>]
let ``Clone preserves endpoints and shares materialised string`` () =
    let original = rangeFor "abcdef" 1 4
    let cloned = original.Clone()
    let clonedInner = innerRange cloned
    Assert.Equal(1, clonedInner.StartOffset)
    Assert.Equal(4, clonedInner.EndOffset)
    Assert.True(original.Compare(cloned))

[<Fact>]
let ``Clone is an independent instance`` () =
    let original = rangeFor "abcdef" 1 4
    let cloned = original.Clone()
    Assert.False(obj.ReferenceEquals(original, cloned))

[<Fact>]
let ``Compare returns false for different endpoints over same string`` () =
    let text = "abcdef"
    let r1 = rangeFor text 0 3
    let r2 = rangeFor text 0 4
    Assert.False(r1.Compare(r2))

[<Fact>]
let ``Compare returns false for ranges over different string instances`` () =
    // F# interns string literals, so two `"abcdef"` literals
    // resolve to the same `String` instance and would Compare
    // equal under `obj.ReferenceEquals(r.Materialised,
    // materialised)`. Force two distinct instances by routing
    // one through `StringBuilder.ToString()`, which always
    // allocates a fresh `String`.
    let r1 = rangeFor "abcdef" 0 3
    let r2 = rangeFor (StringBuilder("abcdef").ToString()) 0 3
    Assert.False(r1.Compare(r2))

[<Fact>]
let ``CompareEndpoints returns 0 for equal endpoints`` () =
    let text = "abcdef"
    let r1 = rangeFor text 2 5
    let r2 = rangeFor text 2 5
    Assert.Equal(
        0,
        r1.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            r2,
            TextPatternRangeEndpoint.Start))

[<Fact>]
let ``CompareEndpoints returns negative when this Start precedes other Start`` () =
    let text = "abcdef"
    let r1 = rangeFor text 1 5
    let r2 = rangeFor text 3 5
    let result =
        r1.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            r2,
            TextPatternRangeEndpoint.Start)
    Assert.True(result < 0)

[<Fact>]
let ``CompareEndpoints returns positive when this End follows other Start`` () =
    let text = "abcdef"
    let r1 = rangeFor text 1 5
    let r2 = rangeFor text 1 5
    let result =
        r1.CompareEndpoints(
            TextPatternRangeEndpoint.End,
            r2,
            TextPatternRangeEndpoint.Start)
    Assert.True(result > 0)

// ---------------------------------------------------------------------
// GetText
// ---------------------------------------------------------------------

[<Fact>]
let ``GetText returns the substring between endpoints`` () =
    let r = rangeFor "hello world" 6 11
    Assert.Equal("world", r.GetText(-1))

[<Fact>]
let ``GetText with maxLength truncates the result`` () =
    let r = rangeFor "hello world" 0 11
    Assert.Equal("hello", r.GetText(5))

[<Fact>]
let ``GetText with maxLength larger than content returns full content`` () =
    let r = rangeFor "abc" 0 3
    Assert.Equal("abc", r.GetText(100))

[<Fact>]
let ``GetText on empty range returns empty string`` () =
    let r = rangeFor "abc" 1 1
    Assert.Equal("", r.GetText(-1))

[<Fact>]
let ``GetText handles inverted endpoints by treating them as a normal range`` () =
    // UIA never asks for inverted endpoints, but defensively we
    // shouldn't throw if the caller is buggy. The screen-grid
    // implementation also defended against this implicitly via
    // its clamp.
    let r = rangeFor "abc" 2 0
    Assert.Equal("ab", r.GetText(-1))

// ---------------------------------------------------------------------
// ExpandToEnclosingUnit
// ---------------------------------------------------------------------

[<Fact>]
let ``ExpandToEnclosingUnit Document covers full text`` () =
    let r = rangeFor "hello world\nfoo" 5 6
    r.ExpandToEnclosingUnit(TextUnit.Document)
    let inner = innerRange r
    Assert.Equal(0, inner.StartOffset)
    Assert.Equal(15, inner.EndOffset)
    Assert.Equal("hello world\nfoo", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Character produces a 1-char range`` () =
    let r = rangeFor "abcdef" 3 3
    r.ExpandToEnclosingUnit(TextUnit.Character)
    Assert.Equal("d", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Character at end of text produces empty range`` () =
    let r = rangeFor "abc" 3 3
    r.ExpandToEnclosingUnit(TextUnit.Character)
    let inner = innerRange r
    Assert.Equal(3, inner.StartOffset)
    Assert.Equal(3, inner.EndOffset)

[<Fact>]
let ``ExpandToEnclosingUnit Line covers entire single-line text`` () =
    let r = rangeFor "abc" 1 1
    r.ExpandToEnclosingUnit(TextUnit.Line)
    Assert.Equal("abc", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Line at line 1 of multi-line text covers that line including trailing newline``
        () =
    let r = rangeFor "abc\ndef\nghi" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Line)
    Assert.Equal("def\n", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Line at last line of text without trailing newline covers that line``
        () =
    let r = rangeFor "abc\ndef" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Line)
    Assert.Equal("def", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Paragraph degrades to Line`` () =
    let r = rangeFor "abc\ndef\nghi" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Paragraph)
    Assert.Equal("def\n", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Page degrades to Line`` () =
    let r = rangeFor "abc\ndef\nghi" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Page)
    Assert.Equal("def\n", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Format degrades to Line`` () =
    let r = rangeFor "abc\ndef\nghi" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Format)
    Assert.Equal("def\n", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Word at start of a word covers the word`` () =
    let r = rangeFor "hello world" 6 6
    r.ExpandToEnclosingUnit(TextUnit.Word)
    Assert.Equal("world", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Word on separator walks to next word`` () =
    // Per ADR 0002 word semantics: ' ' and '\t' are separators;
    // punctuation is not. Starting on a space, expand walks
    // forward to the next non-separator and encloses the
    // resulting word.
    let r = rangeFor "hello world" 5 5
    r.ExpandToEnclosingUnit(TextUnit.Word)
    Assert.Equal("world", r.GetText(-1))

[<Fact>]
let ``ExpandToEnclosingUnit Word on empty text produces empty range`` () =
    let r = rangeFor "" 0 0
    r.ExpandToEnclosingUnit(TextUnit.Word)
    let inner = innerRange r
    Assert.Equal(0, inner.StartOffset)
    Assert.Equal(0, inner.EndOffset)

// ---------------------------------------------------------------------
// Move(Character, ±N)
// ---------------------------------------------------------------------

[<Fact>]
let ``Move Character +1 advances by one character`` () =
    let r = rangeFor "abcdef" 0 1
    let moved = r.Move(TextUnit.Character, 1)
    Assert.Equal(1, moved)
    Assert.Equal("b", r.GetText(-1))

[<Fact>]
let ``Move Character +N clamps at document end`` () =
    let r = rangeFor "abc" 0 1
    let moved = r.Move(TextUnit.Character, 100)
    Assert.Equal(3, moved)
    let inner = innerRange r
    Assert.Equal(3, inner.StartOffset)
    Assert.Equal(3, inner.EndOffset)

[<Fact>]
let ``Move Character -1 retreats by one character`` () =
    let r = rangeFor "abcdef" 2 3
    let moved = r.Move(TextUnit.Character, -1)
    Assert.Equal(-1, moved)
    Assert.Equal("b", r.GetText(-1))

[<Fact>]
let ``Move Character -N clamps at document start`` () =
    let r = rangeFor "abcdef" 2 3
    let moved = r.Move(TextUnit.Character, -100)
    Assert.Equal(-2, moved)
    Assert.Equal("a", r.GetText(-1))

[<Fact>]
let ``Move Character handles int MinValue without underflow`` () =
    // Audit-cycle SR-2: int64 widening in the offset
    // arithmetic guards against a hostile int.MinValue
    // underflowing the `max 0` clamp.
    let r = rangeFor "abc" 1 2
    let moved = r.Move(TextUnit.Character, Int32.MinValue)
    Assert.Equal(-1, moved)
    let inner = innerRange r
    Assert.Equal(0, inner.StartOffset)

[<Fact>]
let ``Move Character returns zero on empty text`` () =
    let r = rangeFor "" 0 0
    Assert.Equal(0, r.Move(TextUnit.Character, 5))

[<Fact>]
let ``Move Character returns zero when count is zero`` () =
    let r = rangeFor "abc" 1 2
    Assert.Equal(0, r.Move(TextUnit.Character, 0))

// ---------------------------------------------------------------------
// Move(Line, ±N)
// ---------------------------------------------------------------------

[<Fact>]
let ``Move Line +1 advances to next line`` () =
    let r = rangeFor "abc\ndef\nghi" 0 4
    let moved = r.Move(TextUnit.Line, 1)
    Assert.Equal(1, moved)
    Assert.Equal("def\n", r.GetText(-1))

[<Fact>]
let ``Move Line +N clamps at last line`` () =
    let r = rangeFor "abc\ndef\nghi" 0 4
    let moved = r.Move(TextUnit.Line, 100)
    Assert.Equal(2, moved)
    Assert.Equal("ghi", r.GetText(-1))

[<Fact>]
let ``Move Line -1 retreats to previous line`` () =
    let r = rangeFor "abc\ndef\nghi" 4 8
    let moved = r.Move(TextUnit.Line, -1)
    Assert.Equal(-1, moved)
    Assert.Equal("abc\n", r.GetText(-1))

[<Fact>]
let ``Move Line -N clamps at first line`` () =
    let r = rangeFor "abc\ndef\nghi" 8 11
    let moved = r.Move(TextUnit.Line, -100)
    Assert.Equal(-2, moved)
    Assert.Equal("abc\n", r.GetText(-1))

[<Fact>]
let ``Move Line cannot advance past last line of single-line text`` () =
    let r = rangeFor "abc" 0 3
    let moved = r.Move(TextUnit.Line, 1)
    Assert.Equal(0, moved)
    Assert.Equal("abc", r.GetText(-1))

// ---------------------------------------------------------------------
// Move(Word, ±N)
// ---------------------------------------------------------------------

[<Fact>]
let ``Move Word +1 advances to next word`` () =
    let r = rangeFor "hello world foo" 0 5
    let moved = r.Move(TextUnit.Word, 1)
    Assert.Equal(1, moved)
    Assert.Equal("world", r.GetText(-1))

[<Fact>]
let ``Move Word advances across newlines`` () =
    let r = rangeFor "alpha\nbeta\ngamma" 0 5
    let moved = r.Move(TextUnit.Word, 1)
    Assert.Equal(1, moved)
    Assert.Equal("beta", r.GetText(-1))

[<Fact>]
let ``Move Word -1 retreats to previous word`` () =
    let r = rangeFor "alpha beta gamma" 11 16
    let moved = r.Move(TextUnit.Word, -1)
    Assert.Equal(-1, moved)
    Assert.Equal("beta", r.GetText(-1))

[<Fact>]
let ``Move Word clamps when no further word exists`` () =
    let r = rangeFor "only" 0 4
    let moved = r.Move(TextUnit.Word, 5)
    Assert.Equal(0, moved)

// ---------------------------------------------------------------------
// MoveEndpointByUnit
// ---------------------------------------------------------------------

[<Fact>]
let ``MoveEndpointByUnit moves only the specified endpoint`` () =
    let r = rangeFor "abcdefgh" 2 4
    let moved =
        r.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Character,
            2)
    Assert.Equal(2, moved)
    let inner = innerRange r
    Assert.Equal(2, inner.StartOffset)
    Assert.Equal(6, inner.EndOffset)

[<Fact>]
let ``MoveEndpointByUnit collapses range when Start crosses End`` () =
    let r = rangeFor "abcdefgh" 2 4
    let _ =
        r.MoveEndpointByUnit(
            TextPatternRangeEndpoint.Start,
            TextUnit.Character,
            5)
    let inner = innerRange r
    // Start moved past End — both endpoints collapse to the
    // moved position per UIA contract.
    Assert.Equal(7, inner.StartOffset)
    Assert.Equal(7, inner.EndOffset)

[<Fact>]
let ``MoveEndpointByUnit collapses range when End crosses Start`` () =
    let r = rangeFor "abcdefgh" 4 6
    let _ =
        r.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Character,
            -5)
    let inner = innerRange r
    Assert.Equal(1, inner.StartOffset)
    Assert.Equal(1, inner.EndOffset)

[<Fact>]
let ``MoveEndpointByUnit Line moves end to next line start`` () =
    let r = rangeFor "abc\ndef\nghi" 0 0
    let _ =
        r.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Line,
            1)
    let inner = innerRange r
    Assert.Equal(0, inner.StartOffset)
    Assert.Equal(4, inner.EndOffset)
    Assert.Equal("abc\n", r.GetText(-1))

// ---------------------------------------------------------------------
// MoveEndpointByRange
// ---------------------------------------------------------------------

[<Fact>]
let ``MoveEndpointByRange aligns endpoints to another range`` () =
    let text = "abcdefgh"
    let r1 = rangeFor text 0 2
    let r2 = rangeFor text 5 7
    r1.MoveEndpointByRange(
        TextPatternRangeEndpoint.End,
        r2,
        TextPatternRangeEndpoint.End)
    let inner = innerRange r1
    Assert.Equal(0, inner.StartOffset)
    Assert.Equal(7, inner.EndOffset)

[<Fact>]
let ``MoveEndpointByRange collapses if pulled endpoint crosses anchor`` () =
    let text = "abcdefgh"
    let r1 = rangeFor text 4 6
    let r2 = rangeFor text 1 2
    r1.MoveEndpointByRange(
        TextPatternRangeEndpoint.Start,
        r2,
        TextPatternRangeEndpoint.Start)
    let inner = innerRange r1
    Assert.Equal(1, inner.StartOffset)
    Assert.Equal(6, inner.EndOffset)

// ---------------------------------------------------------------------
// Selection no-ops + GetChildren
// ---------------------------------------------------------------------

[<Fact>]
let ``Selection mutations are no-ops on a read-only range`` () =
    // Just calls through to confirm no exceptions thrown.
    let r = rangeFor "abc" 0 2
    r.Select()
    r.AddToSelection()
    r.RemoveFromSelection()
    r.ScrollIntoView(true)
    Assert.Equal("ab", r.GetText(-1))

[<Fact>]
let ``GetChildren returns empty array`` () =
    let r = rangeFor "abc" 0 3
    Assert.Empty(r.GetChildren())

[<Fact>]
let ``GetBoundingRectangles returns empty array`` () =
    let r = rangeFor "abc" 0 3
    Assert.Empty(r.GetBoundingRectangles())

// ---------------------------------------------------------------------
// ContentHistoryTextProvider
// ---------------------------------------------------------------------

[<Fact>]
let ``Provider DocumentRange covers full materialised tail`` () =
    let state = freshHistory ()
    feedText state "abc\ndef"
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> state))
        :> ITextProvider
    let doc = provider.DocumentRange
    Assert.Equal("abc\ndef", doc.GetText(-1))

[<Fact>]
let ``Provider DocumentRange returns empty range when history is null`` () =
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> nullHistory))
        :> ITextProvider
    let doc = provider.DocumentRange
    Assert.Equal("", doc.GetText(-1))

[<Fact>]
let ``Provider DocumentRange materialises a fresh range on each call`` () =
    let state = freshHistory ()
    feedText state "first"
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> state))
        :> ITextProvider
    let first = provider.DocumentRange
    feedText state "\nsecond"
    let second = provider.DocumentRange
    Assert.Equal("first", first.GetText(-1))
    Assert.Equal("first\nsecond", second.GetText(-1))

[<Fact>]
let ``Provider GetSelection returns empty array`` () =
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> nullHistory))
        :> ITextProvider
    Assert.Empty(provider.GetSelection())

[<Fact>]
let ``Provider GetVisibleRanges returns empty array`` () =
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> nullHistory))
        :> ITextProvider
    Assert.Empty(provider.GetVisibleRanges())

[<Fact>]
let ``Provider SupportedTextSelection reports None`` () =
    let provider =
        ContentHistoryTextProvider(Func<ContentHistory.T | null>(fun () -> nullHistory))
        :> ITextProvider
    Assert.Equal(
        System.Windows.Automation.SupportedTextSelection.None,
        provider.SupportedTextSelection)
