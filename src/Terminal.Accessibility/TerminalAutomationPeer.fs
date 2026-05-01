namespace Terminal.Accessibility

open System
open System.Text
open System.Windows
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Terminal.Core

/// Stage 4 — UIA peer that exposes `TerminalView` to the WPF
/// Automation tree as a Document with the Text pattern.
///
/// The architectural path was settled by PR #56:
///
///   * `protected virtual GetPatternCore` is unreachable from
///     external assemblies in the .NET 9 WPF reference set
///     (CS0117 / FS0855), so the spike-era plan to add patterns
///     by overriding it was a dead end.
///   * `WM_GETOBJECT` interception with a custom
///     `IRawElementProviderSimple` works for legacy MSAA
///     (`OBJID_CLIENT`) but breaks UIA3 (`UiaRootObjectId`):
///     UIA3 expects an `IRawElementProviderFragmentRoot` there,
///     and a simple provider can't supply the fragment-root
///     navigation surface — CI's `AutomationPeerTests` and
///     `WindowSubclassTests` regressed when we matched
///     `UiaRootObjectId`.
///   * `public virtual GetPattern(PatternInterface)` IS
///     reachable from external assemblies (it's the public
///     entry point that calls `GetPatternCore` internally).
///     Overriding it lets us add patterns without ever
///     touching the unreachable protected member, and the
///     pattern is added to the SAME peer that's already in
///     WPF's tree, so navigation, focus, and properties keep
///     working unchanged.
///
/// `textProvider` is supplied by the owner (`TerminalView`) so
/// the peer doesn't have to know about the screen-snapshot
/// machinery; the view holds the closure over its own `_screen`
/// field and the peer just hands the provider through to UIA.
type TerminalAutomationPeer(owner: FrameworkElement, textProvider: ITextProvider) =
    inherit FrameworkElementAutomationPeer(owner)

    override _.GetAutomationControlTypeCore() = AutomationControlType.Document
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true
    override _.IsContentElementCore() = true

    /// Add the Text pattern to this peer. For every other
    /// pattern interface we defer to the base implementation
    /// so the inherited behaviour (LegacyIAccessible, Window,
    /// etc. coming from `FrameworkElementAutomationPeer`) is
    /// preserved. Return type matches
    /// `AutomationPeer.GetPattern`'s annotation (`object?`) so
    /// F# 9 nullability accepts the base call's possibly-null
    /// return.
    ///
    /// The Text branch uses an explicit `obj | null` type
    /// annotation on a temporary binding so F# 9's nullability
    /// analysis widens `textProvider`'s declared
    /// `ITextProvider` (currently treated as non-nullable in
    /// the WPF reference assembly's annotation set) to the
    /// nullable return type of the override. Without the
    /// annotation, F# rejects both the bare upcast `:> obj`
    /// (FS3261, "this expression is nullable") and the
    /// pattern-match-on-null form (FS3261, "the type does
    /// not support null"). The annotation form sidesteps
    /// both: it's a widening assignment, not a narrowing
    /// pattern match.
    override _.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.Text ->
            let result : obj | null = textProvider
            result
        | _ -> base.GetPattern(patternInterface)

/// Snapshot-rendering helpers shared by `TerminalTextRange`.
/// Kept module-level so test code (and any future range types)
/// can reuse the row-flattening rule without going through the
/// range API.
module SnapshotText =

    /// Flatten a `Cell[][]` snapshot into a single string with
    /// `\n` between rows. Trailing whitespace inside a row is
    /// preserved — UIA Text-pattern semantics treat each cell as
    /// a content position regardless of whether it currently
    /// holds a printable rune. NVDA's "say all" gracefully
    /// handles the trailing spaces.
    let render (rows: Cell[][]) : string =
        if rows.Length = 0 then
            ""
        else
            let sb = StringBuilder()
            for r in 0 .. rows.Length - 1 do
                if r > 0 then sb.Append('\n') |> ignore
                let row = rows.[r]
                for c in 0 .. row.Length - 1 do
                    sb.Append(row.[c].Ch.ToString()) |> ignore
            sb.ToString()

/// UIA `ITextRangeProvider` over an immutable row snapshot
/// with mutable `(row, col)` endpoints.
///
/// The range covers cells in `[Start, End)` half-open, where
/// each endpoint is a `(row, col)` position with `col` in
/// `[0, cols]` — `col == cols` is the legal "end of row"
/// position equivalent to `(row + 1, 0)` for `row < rows - 1`.
///
/// Endpoints are *mutable* because UIA's `ITextRangeProvider`
/// surface mutates ranges in place (`ExpandToEnclosingUnit`,
/// `Move`, `MoveEndpointByUnit`, `MoveEndpointByRange`,
/// `Select` are all `void` — they are required to alter the
/// receiver, not return a new range). The original PR-C
/// implementation kept the range immutable and stubbed every
/// mutating method as a no-op; that broke NVDA's review
/// cursor (preview.20 smoke: read-current-line returned
/// "blank" because NVDA's `ExpandToEnclosingUnit(Line)` was
/// silently dropped, leaving the range collapsed at start
/// with no text to read).
type TerminalTextRange(
        sequence: int64,
        rows: Cell[][],
        cols: int,
        initialStartRow: int,
        initialStartCol: int,
        initialEndRow: int,
        initialEndCol: int) =

    let mutable startRow = initialStartRow
    let mutable startCol = initialStartCol
    let mutable endRow = initialEndRow
    let mutable endCol = initialEndCol

    /// Number of rows in the snapshot. `rows.Length` for non-empty
    /// snapshots; 0 for the early-startup empty range.
    let rowCount = rows.Length

    member _.Sequence = sequence
    member _.Rows = rows
    member _.Cols = cols
    member _.StartRow = startRow
    member _.StartCol = startCol
    member _.EndRow = endRow
    member _.EndCol = endCol
    member _.RowCount = rowCount

    /// Compare two positions; returns -1 / 0 / 1 like
    /// `compare` on tuples but inlined so the hot path
    /// doesn't allocate.
    static member private ComparePos
            (aRow: int, aCol: int, bRow: int, bCol: int) : int =
        if aRow < bRow then -1
        elif aRow > bRow then 1
        elif aCol < bCol then -1
        elif aCol > bCol then 1
        else 0

    /// Clamp a position to the valid range `[0, rows] × [0, cols]`,
    /// where `(rows, 0)` is the legal one-past-end position.
    static member private ClampPos
            (rowCount: int, cols: int, r: int, c: int) : int * int =
        let r = max 0 (min rowCount r)
        let c =
            if r = rowCount then 0
            else max 0 (min cols c)
        (r, c)

    /// Render `[start, end)` of `rows` into a string using
    /// `\n` between rows, matching `SnapshotText.render`'s
    /// row-join rule. Cells beyond `cols` (impossible for
    /// well-formed snapshots, but defensive against jagged
    /// arrays) are skipped.
    static member private GetTextInRange
            (rows: Cell[][]) (cols: int)
            (sr: int) (sc: int) (er: int) (ec: int) : string =
        let sb = StringBuilder()
        let mutable r = sr
        while r <= er && r < rows.Length do
            let firstCol = if r = sr then sc else 0
            let lastColExclusive =
                if r = er then ec
                else cols
            let row = rows.[r]
            let mutable c = firstCol
            while c < lastColExclusive && c < row.Length do
                sb.Append(row.[c].Ch.ToString()) |> ignore
                c <- c + 1
            if r < er then
                sb.Append('\n') |> ignore
            r <- r + 1
        sb.ToString()

    /// Whitespace test for word-boundary detection. We treat
    /// `' '` (space, U+0020) and `\t` (tab, U+0009) as the
    /// only word separators. Newlines aren't relevant because
    /// the row dimension already separates them; cells inside
    /// the buffer never contain `\n` directly. Punctuation is
    /// NOT a separator — "C:\\Users\\test>" is read as a
    /// single word, matching how most terminal users mentally
    /// parse paths and prompts. A future stage with a real
    /// SGR-aware tokenizer can refine this.
    static member private IsWordSeparator(cell: Cell) : bool =
        let n = cell.Ch.Value
        n = int ' ' || n = int '\t'

    /// Find the position one past the end of the word that
    /// starts at `(r, c)`. If `(r, c)` is on a separator,
    /// returns the same position (zero-width). Walks forward
    /// across rows so a word that wraps the implicit row
    /// boundary is still returned as one word — though in
    /// practice cmd.exe's output rarely produces wrap-words
    /// because cmd.exe's own rendering already breaks at
    /// row boundaries.
    static member private WordEndFrom
            (rows: Cell[][]) (cols: int)
            (r: int) (c: int) : int * int =
        let mutable r = r
        let mutable c = c
        let mutable stop = false
        while not stop && r < rows.Length do
            if c >= cols then
                r <- r + 1
                c <- 0
            elif TerminalTextRange.IsWordSeparator(rows.[r].[c]) then
                stop <- true
            else
                c <- c + 1
        (r, c)

    /// Find the position of the next word start strictly
    /// after `(r, c)`. Skips any separators after the
    /// current position, then any non-separators (the
    /// remainder of the current word if we're inside one),
    /// then the run of separators that follow, landing on
    /// the first non-separator cell after that run. Returns
    /// `(rowCount, 0)` (one past the document) if no
    /// further word exists.
    static member private NextWordStart
            (rows: Cell[][]) (cols: int)
            (r: int) (c: int) : int * int =
        let mutable r = r
        let mutable c = c
        let advanceOne () =
            c <- c + 1
            if c >= cols then
                c <- 0
                r <- r + 1
        // If we're currently on a non-separator cell, skip
        // forward through the rest of this word first.
        let mutable inWord =
            r < rows.Length
            && c < cols
            && not (TerminalTextRange.IsWordSeparator(rows.[r].[c]))
        while inWord do
            advanceOne ()
            inWord <-
                r < rows.Length
                && c < cols
                && not (TerminalTextRange.IsWordSeparator(rows.[r].[c]))
        // Now skip the separator run.
        let mutable inSep =
            r < rows.Length
            && c < cols
            && TerminalTextRange.IsWordSeparator(rows.[r].[c])
        while inSep do
            advanceOne ()
            inSep <-
                r < rows.Length
                && c < cols
                && TerminalTextRange.IsWordSeparator(rows.[r].[c])
        // Either we're at the start of a new word, or we
        // walked off the end of the document.
        if r >= rows.Length then (rows.Length, 0)
        else (r, c)

    /// Find the position of the previous word start, scanning
    /// strictly backward from `(r, c)`. Walks back through any
    /// separator run, then back through the current word's
    /// cells until either the cell BEFORE us is a separator
    /// or we hit `(0, 0)`. Returns `(0, 0)` if no earlier word
    /// boundary exists.
    static member private PrevWordStart
            (rows: Cell[][]) (cols: int)
            (r: int) (c: int) : int * int =
        let mutable r = r
        let mutable c = c
        let retreatOne () =
            if c = 0 then
                if r = 0 then ()
                else
                    r <- r - 1
                    c <- cols - 1
            else
                c <- c - 1
        let atOrigin () = r = 0 && c = 0
        // Step back one to start, since we want the position
        // BEFORE the current one.
        retreatOne ()
        // Skip separator run going backward.
        while not (atOrigin ())
              && r < rows.Length
              && c < cols
              && TerminalTextRange.IsWordSeparator(rows.[r].[c]) do
            retreatOne ()
        // Now we're on a non-separator (or at origin). Walk
        // back to the start of this word.
        let mutable lastNonSep = (r, c)
        let mutable continueBack = true
        while continueBack && not (atOrigin ()) do
            // Peek one back; if it's a separator, stop here.
            let pr = if c = 0 then r - 1 else r
            let pc = if c = 0 then cols - 1 else c - 1
            if pr < 0
               || pr >= rows.Length
               || pc < 0
               || pc >= cols
               || TerminalTextRange.IsWordSeparator(rows.[pr].[pc]) then
                continueBack <- false
            else
                retreatOne ()
                lastNonSep <- (r, c)
        lastNonSep

    interface ITextRangeProvider with

        member _.Clone() =
            TerminalTextRange(
                sequence, rows, cols,
                startRow, startCol, endRow, endCol)
            :> ITextRangeProvider

        member _.Compare(other: ITextRangeProvider) : bool =
            // Two ranges compare equal when they wrap the same
            // snapshot AND have identical endpoints. Endpoint
            // equality matters because NVDA clones a range,
            // navigates the clone, then asks "did anything
            // happen?" — without endpoint comparison every
            // clone would be reported as "same as original."
            match other with
            | :? TerminalTextRange as r ->
                obj.ReferenceEquals(r.Rows, rows)
                && r.StartRow = startRow && r.StartCol = startCol
                && r.EndRow = endRow && r.EndCol = endCol
            | _ -> false

        member _.CompareEndpoints
                (thisEndpoint, otherProvider, otherEndpoint) =
            let (thisR, thisC) =
                if thisEndpoint = TextPatternRangeEndpoint.Start
                then (startRow, startCol)
                else (endRow, endCol)
            match otherProvider with
            | :? TerminalTextRange as r ->
                let (otherR, otherC) =
                    if otherEndpoint = TextPatternRangeEndpoint.Start
                    then (r.StartRow, r.StartCol)
                    else (r.EndRow, r.EndCol)
                TerminalTextRange.ComparePos(thisR, thisC, otherR, otherC)
            | _ -> 0

        member _.ExpandToEnclosingUnit(unit: TextUnit) =
            // Reshape the range to enclose the unit at `Start`.
            // For Line we pick the row of `Start`; for Word we
            // pick the contiguous non-whitespace run at or
            // after `Start`; for Character we pick a 1-cell
            // range; for Document we pick everything. Paragraph
            // and Page still degrade to Line — terminal output
            // doesn't have well-defined paragraph or page
            // semantics, and forcing a definition here would be
            // arbitrary. Refine in a later stage if needed.
            match unit with
            | TextUnit.Character ->
                // 1-cell range starting at the current Start.
                let (sr, sc) =
                    TerminalTextRange.ClampPos(rowCount, cols, startRow, startCol)
                let (er, ec) =
                    if sc + 1 <= cols then (sr, sc + 1)
                    elif sr + 1 < rowCount then (sr + 1, 0)
                    else (rowCount, 0)
                startRow <- sr
                startCol <- sc
                endRow <- er
                endCol <- ec
            | TextUnit.Document ->
                startRow <- 0
                startCol <- 0
                endRow <- rowCount
                endCol <- 0
            | TextUnit.Word ->
                // If Start is on a separator, walk forward to
                // the next word; otherwise the current word
                // begins where it does. End is the position
                // one past the last non-separator cell of that
                // word.
                if rowCount = 0 then
                    startRow <- 0
                    startCol <- 0
                    endRow <- 0
                    endCol <- 0
                else
                    let (sr, sc) =
                        TerminalTextRange.ClampPos(rowCount, cols, startRow, startCol)
                    let onSep =
                        sr < rowCount
                        && sc < cols
                        && TerminalTextRange.IsWordSeparator(rows.[sr].[sc])
                    let (wr, wc) =
                        if onSep then
                            TerminalTextRange.NextWordStart rows cols sr sc
                        else
                            (sr, sc)
                    let (er, ec) =
                        if wr >= rowCount then (rowCount, 0)
                        else TerminalTextRange.WordEndFrom rows cols wr wc
                    startRow <- wr
                    startCol <- wc
                    endRow <- er
                    endCol <- ec
            | _ ->
                // Line / Paragraph / Page → enclose the row at
                // Start.
                let (sr, _) =
                    TerminalTextRange.ClampPos(rowCount, cols, startRow, startCol)
                if rowCount = 0 then
                    startRow <- 0
                    startCol <- 0
                    endRow <- 0
                    endCol <- 0
                else
                    startRow <- sr
                    startCol <- 0
                    if sr + 1 < rowCount then
                        endRow <- sr + 1
                        endCol <- 0
                    else
                        endRow <- sr
                        endCol <- cols
        member _.FindAttribute(_: int, _: obj, _: bool) =
            Unchecked.defaultof<ITextRangeProvider>
        member _.FindText(_: string, _: bool, _: bool) =
            Unchecked.defaultof<ITextRangeProvider>
        member _.GetAttributeValue(_: int) =
            // UIA convention: NotSupported sentinel for any
            // attribute we don't expose. Stage 4 doesn't expose
            // SGR yet — that's a later milestone.
            AutomationElementIdentifiers.NotSupported

        member _.GetBoundingRectangles() = Array.empty<double>
        member _.GetEnclosingElement() = Unchecked.defaultof<IRawElementProviderSimple>

        member _.GetText(maxLength: int) =
            let rendered =
                TerminalTextRange.GetTextInRange
                    rows cols startRow startCol endRow endCol
            if maxLength < 0 || maxLength >= rendered.Length then
                rendered
            else
                rendered.Substring(0, maxLength)

        member this.Move(unit: TextUnit, count: int) =
            // Per UIA contract, `Move` translates the entire
            // range by `count` units, preserving the unit
            // shape. We collapse to start, move that endpoint,
            // then expand back to the unit. Returns the
            // number of units actually moved (clamped at
            // document boundaries).
            if rowCount = 0 || count = 0 then 0
            else
                match unit with
                | TextUnit.Character ->
                    let stepsRequested = count
                    let totalCells = rowCount * cols
                    // Position-as-cell-index: r*cols + c
                    let curIdx =
                        (max 0 (min (rowCount - 1) startRow)) * cols
                        + (max 0 (min cols startCol))
                    let targetIdx = max 0 (min totalCells (curIdx + stepsRequested))
                    let actualMoved = targetIdx - curIdx
                    let nr = targetIdx / cols
                    let nc = targetIdx % cols
                    startRow <- nr
                    startCol <- nc
                    // 1-cell range
                    let (er, ec) =
                        if nc + 1 <= cols then (nr, nc + 1)
                        elif nr + 1 < rowCount then (nr + 1, 0)
                        else (rowCount, 0)
                    endRow <- er
                    endCol <- ec
                    actualMoved
                | TextUnit.Word ->
                    // Walk forward / backward through word
                    // starts; each NextWordStart / PrevWordStart
                    // call counts as one unit moved. After the
                    // walk, expand to the word at that position
                    // so the resulting range is Word-shaped.
                    let mutable r = startRow
                    let mutable c = startCol
                    let mutable moved = 0
                    if count > 0 then
                        let mutable i = 0
                        while i < count do
                            let (nr, nc) =
                                TerminalTextRange.NextWordStart rows cols r c
                            if nr >= rowCount then
                                i <- count  // stop loop
                            else
                                r <- nr
                                c <- nc
                                moved <- moved + 1
                                i <- i + 1
                    else
                        let mutable i = 0
                        while i > count do
                            // We're at (r, c); find prev word start
                            let (pr, pc) =
                                TerminalTextRange.PrevWordStart rows cols r c
                            if (pr, pc) = (r, c) then
                                i <- count  // already at origin, stop
                            else
                                r <- pr
                                c <- pc
                                moved <- moved - 1
                                i <- i - 1
                    // Now reshape range to enclose the word at (r, c).
                    let onSep =
                        r < rowCount
                        && c < cols
                        && TerminalTextRange.IsWordSeparator(rows.[r].[c])
                    let (wr, wc) =
                        if onSep then
                            TerminalTextRange.NextWordStart rows cols r c
                        else
                            (r, c)
                    let (er, ec) =
                        if wr >= rowCount then (rowCount, 0)
                        else TerminalTextRange.WordEndFrom rows cols wr wc
                    startRow <- wr
                    startCol <- wc
                    endRow <- er
                    endCol <- ec
                    moved
                | _ ->
                    // Line / Paragraph / Page → line.
                    let curRow = max 0 (min (rowCount - 1) startRow)
                    let target = max 0 (min (rowCount - 1) (curRow + count))
                    let actualMoved = target - curRow
                    startRow <- target
                    startCol <- 0
                    if target + 1 < rowCount then
                        endRow <- target + 1
                        endCol <- 0
                    else
                        endRow <- target
                        endCol <- cols
                    actualMoved

        member _.MoveEndpointByUnit
                (endpoint: TextPatternRangeEndpoint,
                 unit: TextUnit,
                 count: int) =
            // Move only one endpoint by `count` units, keeping
            // the other fixed. If the endpoints cross, UIA's
            // contract is to also pull the other endpoint to
            // match (the range collapses to the moved point).
            if rowCount = 0 || count = 0 then 0
            else
                let isStart = endpoint = TextPatternRangeEndpoint.Start
                let (curR, curC) =
                    if isStart then (startRow, startCol) else (endRow, endCol)
                let (newR, newC, actualMoved) =
                    match unit with
                    | TextUnit.Character ->
                        let totalCells = rowCount * cols
                        let curIdx =
                            (max 0 (min (rowCount - 1) curR)) * cols
                            + (max 0 (min cols curC))
                        let targetIdx = max 0 (min totalCells (curIdx + count))
                        let moved = targetIdx - curIdx
                        let nr = targetIdx / cols
                        let nc = targetIdx % cols
                        (nr, nc, moved)
                    | TextUnit.Word ->
                        // Walk this endpoint by word boundaries
                        // without touching the other. Each
                        // step is one NextWordStart /
                        // PrevWordStart call.
                        let mutable r = curR
                        let mutable c = curC
                        let mutable moved = 0
                        if count > 0 then
                            let mutable i = 0
                            while i < count do
                                let (nr, nc) =
                                    TerminalTextRange.NextWordStart rows cols r c
                                if nr >= rowCount then
                                    i <- count
                                else
                                    r <- nr
                                    c <- nc
                                    moved <- moved + 1
                                    i <- i + 1
                        else
                            let mutable i = 0
                            while i > count do
                                let (pr, pc) =
                                    TerminalTextRange.PrevWordStart rows cols r c
                                if (pr, pc) = (r, c) then
                                    i <- count
                                else
                                    r <- pr
                                    c <- pc
                                    moved <- moved - 1
                                    i <- i - 1
                        (r, c, moved)
                    | _ ->
                        let target = max 0 (min rowCount (curR + count))
                        let moved = target - curR
                        // Line endpoints land at column 0
                        // (start of next line) for Line units.
                        (target, 0, moved)
                if isStart then
                    startRow <- newR
                    startCol <- newC
                    if TerminalTextRange.ComparePos(startRow, startCol, endRow, endCol) > 0 then
                        endRow <- startRow
                        endCol <- startCol
                else
                    endRow <- newR
                    endCol <- newC
                    if TerminalTextRange.ComparePos(startRow, startCol, endRow, endCol) > 0 then
                        startRow <- endRow
                        startCol <- endCol
                actualMoved

        member _.MoveEndpointByRange
                (thisEndpoint: TextPatternRangeEndpoint,
                 otherProvider: ITextRangeProvider,
                 otherEndpoint: TextPatternRangeEndpoint) =
            match otherProvider with
            | :? TerminalTextRange as r ->
                let (otherR, otherC) =
                    if otherEndpoint = TextPatternRangeEndpoint.Start
                    then (r.StartRow, r.StartCol)
                    else (r.EndRow, r.EndCol)
                if thisEndpoint = TextPatternRangeEndpoint.Start then
                    startRow <- otherR
                    startCol <- otherC
                    if TerminalTextRange.ComparePos(startRow, startCol, endRow, endCol) > 0 then
                        endRow <- startRow
                        endCol <- startCol
                else
                    endRow <- otherR
                    endCol <- otherC
                    if TerminalTextRange.ComparePos(startRow, startCol, endRow, endCol) > 0 then
                        startRow <- endRow
                        startCol <- endCol
            | _ -> ()
        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()
        member _.ScrollIntoView(_: bool) = ()
        member _.GetChildren() = Array.empty<IRawElementProviderSimple>

/// UIA `ITextProvider` whose `DocumentRange` returns the entire
/// terminal buffer as a single text range.
///
/// `screenSource` is a delegate (rather than a direct `Screen`
/// reference) so the WPF view can lazily resolve the screen at
/// each UIA call — Stage 3b attaches the screen after the view
/// is constructed, so a captured-at-construction reference would
/// be `null` for early UIA queries. The delegate is invoked on
/// the UIA RPC thread; `Screen.SnapshotRows` takes the screen's
/// internal lock so the read is safe across threads.
type TerminalTextProvider(screenSource: Func<Screen | null>) =

    /// Build a range that covers every cell currently in the
    /// screen. Returns an empty range when `screenSource`
    /// resolves to `null` (the early-startup case).
    member private _.CaptureFullRange() : ITextRangeProvider =
        match screenSource.Invoke() with
        | null ->
            TerminalTextRange(
                0L, Array.empty<Cell[]>, 0,
                0, 0, 0, 0)
            :> ITextRangeProvider
        | screen ->
            let seqNum, rows = screen.SnapshotRows(0, screen.Rows)
            // Document range: start = (0, 0), end = (rows, 0)
            // i.e. one-past-last-row, matching UIA's
            // half-open range convention.
            TerminalTextRange(
                seqNum, rows, screen.Cols,
                0, 0, rows.Length, 0)
            :> ITextRangeProvider

    interface ITextProvider with

        member this.DocumentRange = this.CaptureFullRange()

        member _.SupportedTextSelection = SupportedTextSelection.None

        member _.GetSelection() =
            // No selection model in Stage 4 yet; UIA convention
            // is to return an empty array rather than null.
            Array.empty<ITextRangeProvider>

        member _.GetVisibleRanges() =
            // Stage 4 treats the whole buffer as visible — there
            // is no scrollback yet and the view always renders
            // every row.
            Array.empty<ITextRangeProvider>

        member _.RangeFromChild(_: IRawElementProviderSimple) =
            Unchecked.defaultof<ITextRangeProvider>

        member _.RangeFromPoint(_: System.Windows.Point) =
            Unchecked.defaultof<ITextRangeProvider>
