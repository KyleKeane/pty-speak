namespace Terminal.Accessibility

open System
open System.Windows.Automation
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Terminal.Core

/// Cycle 46 PR-B — UIA Text-pattern providers backed by
/// `Terminal.Core.ContentHistory` rather than the screen grid.
/// The pre-Cycle-46 screen-grid `TerminalTextProvider` +
/// `TerminalTextRange` were deleted in PR-D; the types here
/// are the live UIA Text-pattern surface.
///
/// See `docs/adr/0002-uia-textedit-caret-output.md` for the
/// full rationale; the short version is "screen-grid
/// TextProvider is the wrong substrate for linear streaming
/// output".
///
/// Threading: the providers are accessed from the UIA RPC
/// thread. `ContentHistory.tailText` takes the substrate's
/// internal lock so the read is safe alongside writes from the
/// PTY reader thread.
module internal ContentHistoryMaterialiser =

    /// 256 KB tail cap per ADR 0002 PR-B tail-cap decision.
    /// Roughly 5–10 minutes of SAPI speech at normal rate;
    /// bounded materialisation cost. Configurable later if
    /// needed.
    [<Literal>]
    let TailCapBytes = 262144

    /// Materialise the current `ContentHistory` tail to a
    /// single string. Returns the empty string if `history`
    /// is null (the early-startup case before
    /// `TerminalView.SetContentHistory` has been called).
    let materialise (history: ContentHistory.T | null) : string =
        match history with
        | null -> ""
        | h -> ContentHistory.tailText h TailCapBytes

/// UIA `ITextRangeProvider` over a materialised
/// `ContentHistory` tail. Endpoints are character offsets
/// `[0, materialised.Length]` (half-open at `endOffset`).
///
/// Mutability: `startOffset` / `endOffset` are mutable
/// because UIA's `ITextRangeProvider` surface mutates ranges
/// in place — `ExpandToEnclosingUnit`, `Move`,
/// `MoveEndpointByUnit`, `MoveEndpointByRange`, and `Select`
/// are all `void` and required to alter the receiver.
type internal ContentHistoryTextRange
    (materialised: string,
     initialStartOffset: int,
     initialEndOffset: int) =

    let mutable startOffset = initialStartOffset
    let mutable endOffset = initialEndOffset

    /// The materialised string this range operates over. Two
    /// ranges compare equal only when they wrap the same
    /// string instance (used by `Compare`).
    member _.Materialised = materialised
    member _.StartOffset = startOffset
    member _.EndOffset = endOffset

    /// Clamp an offset to `[0, length]`. The one-past-end
    /// offset is the legal "after the last character"
    /// position UIA endpoints use.
    static member private Clamp (length: int) (offset: int) : int =
        if offset < 0 then 0
        elif offset > length then length
        else offset

    /// Whitespace test for word-boundary detection. Carries
    /// forward the pre-Cycle-46 `TerminalTextRange.IsWordSeparator`
    /// semantics: space and tab are separators; punctuation is
    /// NOT a separator
    /// so paths and shell prompts (`C:\Users\test>`) read as
    /// single words. `\n` is handled separately at line
    /// boundaries (it terminates a word) so it isn't included
    /// here.
    static member private IsWordSep (c: char) : bool =
        c = ' ' || c = '\t'

    /// Word-or-newline separator predicate. Used at word
    /// boundaries where newlines terminate the current word.
    static member private IsWordOrNewline (c: char) : bool =
        c = '\n' || ContentHistoryTextRange.IsWordSep c

    /// Find the offset of the start of the line containing
    /// `offset`. Line 0 starts at 0; subsequent lines start
    /// at the position immediately after each `\n`. The
    /// position past a trailing `\n` is a valid "empty
    /// trailing line" start.
    static member internal LineStartOf (text: string) (offset: int) : int =
        let clamped = ContentHistoryTextRange.Clamp text.Length offset
        if clamped = 0 then 0
        else
            let prev = text.LastIndexOf('\n', clamped - 1)
            if prev < 0 then 0
            else prev + 1

    /// Find the offset of the start of the line immediately
    /// after the line containing `offset`. Returns
    /// `text.Length` if no newline follows (the line at
    /// `offset` is the last line and there is no further
    /// line). Used as the line-shape end of a `Line`-unit
    /// range — the trailing `\n` is included in the range.
    static member internal LineEndOf (text: string) (offset: int) : int =
        let clamped = ContentHistoryTextRange.Clamp text.Length offset
        if clamped >= text.Length then text.Length
        else
            let next = text.IndexOf('\n', clamped)
            if next < 0 then text.Length
            else next + 1

    /// Find the offset one past the end of the word at
    /// `offset`. If `offset` is on a separator (space, tab,
    /// or `\n`), returns `offset` (zero-width word).
    static member internal WordEndFrom (text: string) (offset: int) : int =
        let length = text.Length
        let mutable i = ContentHistoryTextRange.Clamp length offset
        let mutable stop = false
        while not stop && i < length do
            if ContentHistoryTextRange.IsWordOrNewline text.[i] then
                stop <- true
            else
                i <- i + 1
        i

    /// Find the offset of the next word-start strictly after
    /// `offset`. Skips remainder of current word, then run
    /// of separators (including `\n`), then lands on the
    /// first non-separator character. Returns `text.Length`
    /// if no further word exists.
    static member internal NextWordStart (text: string) (offset: int) : int =
        let length = text.Length
        let isSep c = ContentHistoryTextRange.IsWordOrNewline c
        let mutable i = ContentHistoryTextRange.Clamp length offset
        // Skip the remainder of the current word (if we're
        // currently inside one).
        while i < length && not (isSep text.[i]) do
            i <- i + 1
        // Skip the separator run.
        while i < length && isSep text.[i] do
            i <- i + 1
        i

    /// Find the offset of the previous word-start strictly
    /// before `offset`. Returns 0 if no earlier word exists.
    /// The step-back-one at entry is what makes this
    /// "strictly before": calling `PrevWordStart text k`
    /// where `k` is itself a word start returns the start of
    /// the previous word, not `k`.
    static member internal PrevWordStart (text: string) (offset: int) : int =
        let isSep c = ContentHistoryTextRange.IsWordOrNewline c
        let mutable i = ContentHistoryTextRange.Clamp text.Length offset
        // Step back one to begin scanning the position BEFORE
        // the current one.
        if i > 0 then i <- i - 1
        // Skip backwards through any separator run we land in.
        while i > 0 && isSep text.[i] do
            i <- i - 1
        // Walk back to the start of this word: while the
        // character before `i` is a non-separator, step back.
        while i > 0 && not (isSep text.[i - 1]) do
            i <- i - 1
        i

    /// Walk one endpoint forward / backward by `count`
    /// line-units, starting from `cur`. Returns the new
    /// position + the number of units actually moved (clamped
    /// at document boundaries). Shared between `Move(Line)`
    /// and `MoveEndpointByUnit(_, Line)`.
    static member private WalkLines
            (text: string) (cur: int) (count: int) : int * int =
        let length = text.Length
        let mutable c = ContentHistoryTextRange.LineStartOf text cur
        let mutable moved = 0
        if count > 0 then
            let mutable i = 0
            while i < count do
                let nl =
                    if c >= length then -1
                    else text.IndexOf('\n', c)
                if nl < 0 then
                    i <- count
                else
                    c <- nl + 1
                    moved <- moved + 1
                    i <- i + 1
        else
            let mutable i = 0
            while i > count do
                if c = 0 then
                    i <- count
                else
                    c <- ContentHistoryTextRange.LineStartOf text (c - 1)
                    moved <- moved - 1
                    i <- i - 1
        (c, moved)

    /// Walk one endpoint forward / backward by `count`
    /// word-units. Returns the new position + the number of
    /// units actually moved. Shared between `Move(Word)` and
    /// `MoveEndpointByUnit(_, Word)`.
    static member private WalkWords
            (text: string) (cur: int) (count: int) : int * int =
        let length = text.Length
        let mutable c = cur
        let mutable moved = 0
        if count > 0 then
            let mutable i = 0
            while i < count do
                let n = ContentHistoryTextRange.NextWordStart text c
                if n >= length then
                    i <- count
                else
                    c <- n
                    moved <- moved + 1
                    i <- i + 1
        else
            let mutable i = 0
            while i > count do
                let p = ContentHistoryTextRange.PrevWordStart text c
                if p = c then
                    i <- count
                else
                    c <- p
                    moved <- moved - 1
                    i <- i - 1
        (c, moved)

    /// Walk one endpoint by `count` characters. int64 widening
    /// guards against hostile / accidental `int.MinValue`
    /// underflowing past the `max 0` clamp — same defensive
    /// pattern the pre-Cycle-46 `TerminalTextRange.Move(Character)`
    /// used (carried forward).
    static member private WalkChars
            (text: string) (cur: int) (count: int) : int * int =
        let length = text.Length
        let curC = ContentHistoryTextRange.Clamp length cur
        let target64 =
            max 0L (min (int64 length) (int64 curC + int64 count))
        let target = int target64
        (target, target - curC)

    interface ITextRangeProvider with

        member _.Clone() =
            ContentHistoryTextRange(materialised, startOffset, endOffset)
            :> ITextRangeProvider

        member _.Compare(other: ITextRangeProvider) : bool =
            match other with
            | :? ContentHistoryTextRange as r ->
                // Reference-equality on the materialised
                // string AND endpoint equality. The string
                // check rules out comparing ranges from
                // different DocumentRange captures (each
                // capture materialises a fresh string from
                // the substrate; two captures may have
                // identical text but they're different
                // instances).
                obj.ReferenceEquals(r.Materialised, materialised)
                && r.StartOffset = startOffset
                && r.EndOffset = endOffset
            | _ -> false

        member _.CompareEndpoints
                (thisEndpoint, otherProvider, otherEndpoint) =
            let thisOff =
                if thisEndpoint = TextPatternRangeEndpoint.Start
                then startOffset else endOffset
            match otherProvider with
            | :? ContentHistoryTextRange as r ->
                let otherOff =
                    if otherEndpoint = TextPatternRangeEndpoint.Start
                    then r.StartOffset else r.EndOffset
                compare thisOff otherOff
            | _ -> 0

        member _.ExpandToEnclosingUnit(unit: TextUnit) =
            let length = materialised.Length
            match unit with
            | TextUnit.Character ->
                let s = ContentHistoryTextRange.Clamp length startOffset
                let e = if s < length then s + 1 else s
                startOffset <- s
                endOffset <- e
            | TextUnit.Document ->
                startOffset <- 0
                endOffset <- length
            | TextUnit.Word ->
                if length = 0 then
                    startOffset <- 0
                    endOffset <- 0
                else
                    let s0 = ContentHistoryTextRange.Clamp length startOffset
                    let onSep =
                        s0 < length
                        && ContentHistoryTextRange.IsWordOrNewline materialised.[s0]
                    let s =
                        if onSep then
                            ContentHistoryTextRange.NextWordStart materialised s0
                        else
                            s0
                    let e = ContentHistoryTextRange.WordEndFrom materialised s
                    startOffset <- s
                    endOffset <- e
            | _ ->
                // Line / Paragraph / Page / Format → enclose
                // the line at Start. `Paragraph` / `Page` /
                // `Format` have no useful definition over raw
                // terminal output; the pre-Cycle-46
                // `TerminalTextRange` degraded them to `Line`
                // and we carry that convention forward.
                let s = ContentHistoryTextRange.LineStartOf materialised startOffset
                let e = ContentHistoryTextRange.LineEndOf materialised s
                startOffset <- s
                endOffset <- e

        member _.FindAttribute(_: int, _: obj, _: bool) =
            Unchecked.defaultof<ITextRangeProvider>

        member _.FindText(_: string, _: bool, _: bool) =
            Unchecked.defaultof<ITextRangeProvider>

        member _.GetAttributeValue(_: int) =
            // Carry forward the pre-Cycle-46
            // `TerminalTextRange.GetAttributeValue` semantics:
            // return the NotSupported sentinel for every
            // attribute. NVDA's text-edit reading path doesn't
            // require any specific attribute on PR-B; if a
            // later cycle surfaces a need (e.g. IsReadOnly for
            // a "read-only edit" verbalisation), add the
            // specific attribute here.
            AutomationElementIdentifiers.NotSupported

        member _.GetBoundingRectangles() = Array.empty<double>

        member _.GetEnclosingElement() =
            Unchecked.defaultof<IRawElementProviderSimple>

        member _.GetText(maxLength: int) =
            let len = materialised.Length
            let s = ContentHistoryTextRange.Clamp len startOffset
            let e = ContentHistoryTextRange.Clamp len endOffset
            let lo, hi = if s <= e then s, e else e, s
            let rendered = materialised.Substring(lo, hi - lo)
            if maxLength < 0 || maxLength >= rendered.Length then
                rendered
            else
                rendered.Substring(0, maxLength)

        member _.Move(unit: TextUnit, count: int) : int =
            let length = materialised.Length
            if length = 0 || count = 0 then 0
            else
                match unit with
                | TextUnit.Character ->
                    let newOff, moved =
                        ContentHistoryTextRange.WalkChars
                            materialised startOffset count
                    let e =
                        if newOff < length then newOff + 1
                        else newOff
                    startOffset <- newOff
                    endOffset <- e
                    moved
                | TextUnit.Word ->
                    let newOff, moved =
                        ContentHistoryTextRange.WalkWords
                            materialised startOffset count
                    // Reshape to enclose the word at the
                    // landing position (matches
                    // `ExpandToEnclosingUnit(Word)` logic).
                    let s, e =
                        if newOff >= length then (length, length)
                        else
                            let onSep =
                                ContentHistoryTextRange.IsWordOrNewline
                                    materialised.[newOff]
                            let s =
                                if onSep then
                                    ContentHistoryTextRange.NextWordStart
                                        materialised newOff
                                else newOff
                            let e =
                                ContentHistoryTextRange.WordEndFrom
                                    materialised s
                            (s, e)
                    startOffset <- s
                    endOffset <- e
                    moved
                | _ ->
                    // Line / Paragraph / Page / Format → line.
                    let newOff, moved =
                        ContentHistoryTextRange.WalkLines
                            materialised startOffset count
                    let e =
                        ContentHistoryTextRange.LineEndOf materialised newOff
                    startOffset <- newOff
                    endOffset <- e
                    moved

        member _.MoveEndpointByUnit
                (endpoint: TextPatternRangeEndpoint,
                 unit: TextUnit,
                 count: int) : int =
            let length = materialised.Length
            if length = 0 || count = 0 then 0
            else
                let isStart = endpoint = TextPatternRangeEndpoint.Start
                let cur = if isStart then startOffset else endOffset
                let newOff, moved =
                    match unit with
                    | TextUnit.Character ->
                        ContentHistoryTextRange.WalkChars
                            materialised cur count
                    | TextUnit.Word ->
                        ContentHistoryTextRange.WalkWords
                            materialised cur count
                    | _ ->
                        ContentHistoryTextRange.WalkLines
                            materialised cur count
                // UIA contract: when endpoints cross, the
                // other endpoint pulls to match (the range
                // collapses to the moved point).
                if isStart then
                    startOffset <- newOff
                    if startOffset > endOffset then
                        endOffset <- startOffset
                else
                    endOffset <- newOff
                    if startOffset > endOffset then
                        startOffset <- endOffset
                moved

        member _.MoveEndpointByRange
                (thisEndpoint: TextPatternRangeEndpoint,
                 otherProvider: ITextRangeProvider,
                 otherEndpoint: TextPatternRangeEndpoint) =
            match otherProvider with
            | :? ContentHistoryTextRange as r ->
                let otherOff =
                    if otherEndpoint = TextPatternRangeEndpoint.Start
                    then r.StartOffset else r.EndOffset
                if thisEndpoint = TextPatternRangeEndpoint.Start then
                    startOffset <- otherOff
                    if startOffset > endOffset then
                        endOffset <- startOffset
                else
                    endOffset <- otherOff
                    if startOffset > endOffset then
                        startOffset <- endOffset
            | _ -> ()

        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()
        member _.ScrollIntoView(_: bool) = ()
        member _.GetChildren() = Array.empty<IRawElementProviderSimple>

/// UIA `ITextProvider` whose `DocumentRange` materialises a
/// fresh `ContentHistory` tail (capped at 256 KB) on each
/// call. UIA clients pull `DocumentRange` when they need
/// fresh content; PR-C will additionally raise
/// `TextSelectionChangedEvent` on tuple finalisation so NVDA's
/// caret advances to the new tail.
///
/// `historySource` is a delegate (rather than a direct
/// `ContentHistory.T` reference) so `TerminalView` can lazily
/// resolve the substrate at each UIA call — the view's
/// `_contentHistory` field is set post-construction via
/// `SetContentHistory`, mirroring the existing `SetScreen` /
/// `SetDisplayBuffer` injection pattern.
type internal ContentHistoryTextProvider
    (historySource: Func<ContentHistory.T | null>) =

    /// Build a range covering the entire current materialised
    /// tail. Returns an empty range when `historySource`
    /// resolves to null (the early-startup case before
    /// `TerminalView.SetContentHistory` has been called).
    member private _.CaptureFullRange() : ITextRangeProvider =
        let history = historySource.Invoke()
        let text = ContentHistoryMaterialiser.materialise history
        ContentHistoryTextRange(text, 0, text.Length)
        :> ITextRangeProvider

    interface ITextProvider with

        member this.DocumentRange = this.CaptureFullRange()

        member _.SupportedTextSelection = SupportedTextSelection.None

        member _.GetSelection() =
            // PR-B doesn't expose a selection model. UIA
            // convention is empty array, not null.
            Array.empty<ITextRangeProvider>

        member _.GetVisibleRanges() =
            // We treat the materialised tail as fully visible
            // — there's no separate scroll viewport. (The
            // pre-Cycle-46 screen-grid `TerminalTextProvider`
            // also returned empty here.)
            Array.empty<ITextRangeProvider>

        member _.RangeFromChild(_: IRawElementProviderSimple) =
            Unchecked.defaultof<ITextRangeProvider>

        member _.RangeFromPoint(_: System.Windows.Point) =
            Unchecked.defaultof<ITextRangeProvider>
