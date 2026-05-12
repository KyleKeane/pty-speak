namespace Terminal.Accessibility

open System
open System.Text
open System.Windows
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Terminal.Core

// Audit-cycle PR-C — restrict accessibility types to the
// internal callers that actually use them. The Stage 4 design
// shipped them as `public` because F# defaults to public, but
// the only consumer is `PtySpeak.Views` (the C# WPF library
// that constructs the peer in `TerminalView.OnCreateAutomationPeer`).
// Marking them `internal` + exposing to Views via
// `InternalsVisibleTo` prevents accidental third-party API
// dependency on these types and gives Stage 5+ contributors
// the freedom to break their signatures without an external
// breaking-change concern.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Views")>]
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Tests.Unit")>]
do ()

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
///     navigation surface — CI regressed when we matched
///     `UiaRootObjectId`. (Audit-cycle PR-C deleted the dead
///     `WindowSubclassNative` + `TerminalRawProvider` files
///     that were kept "just in case" after the pivot; if you
///     ever need that path back, see git history before this
///     PR for the implementation.)
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
///
/// **Cycle 37b** — constructor extended with `writePtyBytes:
/// Action<byte[]>` so child `TerminalListItemAutomationPeer`
/// instances can fire `IInvokeProvider.Invoke()` → `\r` byte
/// onto the PTY (Claude tool-use prompt accepts the highlighted
/// choice on Enter). The View's `OnCreateAutomationPeer` passes
/// `this.WritePtyBytes` (a public method that wraps the
/// View's private `_writeBytes` field).
type internal TerminalListAutomationPeer
    (parent: AutomationPeer,
     initialPayload: SelectionRawPayload,
     writePtyBytes: Action<byte[]>) =
    inherit AutomationPeer()

    let mutable selectedIndex : int = initialPayload.SelectedIndex
    let itemCount : int = initialPayload.ItemCount
    let allItems : string[] = initialPayload.AllItems

    /// ListItem peers built lazily on first access (typically
    /// from the UIA-thread `GetChildrenCore` call) and cached
    /// for the list's lifetime. Avoiding the `as this` /
    /// class-let-binding initialization-soundness pattern
    /// (FS0021 under TreatWarningsAsErrors); deferring
    /// construction to a `member` ensures `this` is fully bound
    /// when the items are built.
    let mutable cachedItems : TerminalListItemAutomationPeer[] | null = null

    member private this.EnsureItems() : TerminalListItemAutomationPeer[] =
        match cachedItems with
        | null ->
            let arr =
                allItems
                |> Array.mapi (fun i text ->
                    TerminalListItemAutomationPeer(this, text, i, writePtyBytes))
            cachedItems <- arr
            arr
        | arr -> arr

    member internal _.SelectedIndex
        with get () = selectedIndex
        and set v = selectedIndex <- v

    member internal _.ItemCount = itemCount

    /// Called by the parent peer when a SelectionItem event
    /// arrives. Mutates state + raises the per-item selection
    /// event so NVDA shifts focus to the new selected item.
    member this.UpdateSelection(newSelectedIndex: int) =
        let items = this.EnsureItems()
        selectedIndex <- newSelectedIndex
        if newSelectedIndex >= 0 && newSelectedIndex < items.Length then
            let target = items.[newSelectedIndex]
            target.RaiseAutomationEvent(
                AutomationEvents.SelectionItemPatternOnElementSelected)

    interface ISelectionProvider with
        member _.CanSelectMultiple = false
        member _.IsSelectionRequired = true
        member this.GetSelection() : IRawElementProviderSimple[] =
            // F# interface members typed-as-the-implementing
            // class via `member this.X`; direct call to private
            // `EnsureItems` + protected-instance
            // `ProviderFromPeer` (inherited from AutomationPeer)
            // both work without downcast.
            let items = this.EnsureItems()
            if selectedIndex >= 0 && selectedIndex < items.Length then
                let peer = items.[selectedIndex] :> AutomationPeer
                let provider = this.ProviderFromPeer(peer)
                [| provider |]
            else
                Array.empty<IRawElementProviderSimple>

    override this.GetChildrenCore() =
        let items = this.EnsureItems()
        let list = ResizeArray<AutomationPeer>(items.Length)
        for p in items do
            list.Add(p :> AutomationPeer)
        list

    override _.GetClassNameCore() = "TerminalList"
    override _.GetAutomationControlTypeCore() = AutomationControlType.List
    override _.GetNameCore() = "Selection prompt"
    override _.IsContentElementCore() = true
    override _.IsControlElementCore() = true

    // Remaining AutomationPeer abstract overrides. The list peer
    // is virtual (no FrameworkElement backing) so geometry +
    // focusability concepts don't directly apply; safe defaults
    // mirror the document peer's behaviour delegated through
    // FrameworkElementAutomationPeer in the parent.
    override _.GetBoundingRectangleCore() = System.Windows.Rect.Empty
    override _.GetClickablePointCore() = System.Windows.Point()
    override _.HasKeyboardFocusCore() = false
    override _.IsEnabledCore() = true
    override _.IsKeyboardFocusableCore() = true
    override _.IsOffscreenCore() = false
    override _.IsPasswordCore() = false
    override _.IsRequiredForFormCore() = false
    override _.SetFocusCore() = ()

    // String-valued metadata abstracts. Empty strings match the
    // .NET 9 convention for "no value" — UIA clients (NVDA,
    // Inspect.exe) interpret as unset rather than the literal
    // empty string.
    override _.GetAcceleratorKeyCore() = ""
    override _.GetAccessKeyCore() = ""
    override _.GetAutomationIdCore() = "TerminalList"
    override _.GetHelpTextCore() = ""
    override _.GetItemStatusCore() = ""
    override _.GetItemTypeCore() = ""
    // No labeled-by relationship; AutomationPeer's documented
    // semantic for "none" is null. F# 9's view of the WPF
    // reference assembly types `AutomationPeer` (rather than
    // `AutomationPeer | null`); `Unchecked.defaultof<_>`
    // produces null at runtime without the strict-nullness
    // diagnostic.
    override _.GetLabeledByCore() = Unchecked.defaultof<AutomationPeer>
    override _.GetOrientationCore() = AutomationOrientation.None

    // GetParent() is non-virtual on AutomationPeer (FS0855). The
    // parent relationship is established via the document peer's
    // GetChildrenCore returning this list peer; UIA's tree
    // walker handles the upward walk via internal framework
    // bookkeeping.

    override this.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.Selection ->
            let provider = this :> ISelectionProvider
            let result : obj | null = provider
            result
        | _ -> null

/// Cycle 37b — virtual UIA peer for a single item within a
/// detected selection list. Implements `ISelectionItemProvider`
/// (NVDA's "is this the selected one?" interrogation +
/// PositionInSet/SizeOfSet) and `IInvokeProvider` (single-key
/// activation: NVDA in focus mode pressing Enter on the
/// selected item writes `\r` to the PTY, which Claude
/// interprets as "press the highlighted choice"). Per
/// `docs/CANONICAL-DISPLAY-CATALOG.md` §2.14 ConfirmationPrompt
/// hybrid contract.
///
/// `parent` is `TerminalListAutomationPeer` so this peer can
/// query the parent's mutable `SelectedIndex` (no per-item
/// state; the listbox owns the cursor). Mutual recursion via
/// `and` resolves the forward reference from the parent's
/// `itemPeers` field.
and internal TerminalListItemAutomationPeer
    (parent: TerminalListAutomationPeer,
     text: string,
     index: int,
     writePtyBytes: Action<byte[]>) =
    inherit AutomationPeer()

    interface ISelectionItemProvider with
        member _.IsSelected = parent.SelectedIndex = index
        member this.SelectionContainer =
            this.ProviderFromPeer(parent :> AutomationPeer)
        // Selection mutation from UIA is read-only in 37b — the
        // PTY drives selection via arrow-key echoes, which the
        // detector re-fires as SelectionItem events. Stage 8e-C
        // generalizes this to UIA-driven Select() that writes
        // arrow bytes to the PTY.
        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()

    interface IInvokeProvider with
        member _.Invoke() =
            // Send Enter byte (`\r` = 0x0D) to PTY. Claude's
            // tool-use prompt accepts the highlighted choice on
            // Enter. cmd `choice` and other shells with
            // different activation keys are out of scope for
            // 37b (SelectionDetector is shellKey-gated to
            // "claude").
            writePtyBytes.Invoke([| 0x0Duy |])

    override _.GetClassNameCore() = "TerminalListItem"
    override _.GetAutomationControlTypeCore() = AutomationControlType.ListItem
    override _.GetNameCore() = text
    override _.IsContentElementCore() = true
    override _.IsControlElementCore() = true
    override _.GetPositionInSetCore() = index + 1
    override _.GetSizeOfSetCore() = parent.ItemCount

    // Remaining AutomationPeer abstract overrides. ListItem peers
    // are virtual; focus semantics defer to the PTY-side cursor.
    override _.GetBoundingRectangleCore() = System.Windows.Rect.Empty
    override _.GetClickablePointCore() = System.Windows.Point()
    override _.HasKeyboardFocusCore() = false
    override _.IsEnabledCore() = true
    override _.IsKeyboardFocusableCore() = true
    override _.IsOffscreenCore() = false
    override _.IsPasswordCore() = false
    override _.IsRequiredForFormCore() = false
    override _.SetFocusCore() = ()

    // ListItem leaf-node: no children. Empty list (not null)
    // matches the .NET 9 non-null `List<AutomationPeer>` return
    // type per the F# 9 view of the WPF reference assembly.
    override _.GetChildrenCore() = System.Collections.Generic.List<AutomationPeer>()

    // String-valued metadata abstracts. ItemText is reserved
    // for the ListItem's name; AutomationId differentiates
    // siblings within the parent list peer.
    override _.GetAcceleratorKeyCore() = ""
    override _.GetAccessKeyCore() = ""
    override _.GetAutomationIdCore() = sprintf "TerminalListItem[%d]" index
    override _.GetHelpTextCore() = ""
    override _.GetItemStatusCore() = ""
    override _.GetItemTypeCore() = ""
    override _.GetLabeledByCore() = Unchecked.defaultof<AutomationPeer>
    override _.GetOrientationCore() = AutomationOrientation.None

    // GetParent() is non-virtual on AutomationPeer (FS0855). The
    // parent relationship is established via the list peer's
    // GetChildrenCore returning this item peer.

    override this.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.SelectionItem ->
            let provider = this :> ISelectionItemProvider
            let result : obj | null = provider
            result
        | PatternInterface.Invoke ->
            let provider = this :> IInvokeProvider
            let result : obj | null = provider
            result
        | _ -> null

/// Stage 4 / Cycle 37b — UIA peer that exposes `TerminalView`
/// to the WPF Automation tree as a Document with the Text
/// pattern, plus (Cycle 37b) child `TerminalListAutomationPeer`
/// instances when a Claude tool-use selection prompt is active.
type internal TerminalAutomationPeer
    (owner: FrameworkElement,
     textProvider: ITextProvider,
     writePtyBytes: Action<byte[]>) =
    inherit FrameworkElementAutomationPeer(owner)

    /// Cycle 37b — currently-active list peer, materialized when
    /// `UpdateSelectionState` receives a `"shown"` payload + dropped
    /// when it receives a `"dismissed"` payload. The peer's
    /// presence drives `IsContentElementCore` (false while
    /// active per spec §8.5 dedup) and `GetChildrenCore` (returns
    /// the list peer as the sole child while active).
    let mutable currentListPeer : TerminalListAutomationPeer option = None

    override _.GetAutomationControlTypeCore() = AutomationControlType.Document
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true

    /// Cycle 37b — full-document content-element suppression
    /// while a list peer is materialized. Per
    /// `spec/tech-plan.md` §8.5 dedup: NVDA reads the list peer
    /// (and only the list peer) for the selection rows. The
    /// pragmatic full-document form (chosen 2026-05-10) trades
    /// off NVDA reading-cursor history browse during a prompt;
    /// per-range exclusion via `GetVisibleRanges` can iterate
    /// post-merge if the trade-off bites.
    override _.IsContentElementCore() =
        match currentListPeer with
        | Some _ -> false
        | None -> true

    /// Cycle 37b — return the active list peer as the sole
    /// child while a selection is active; defer to base
    /// implementation otherwise. This is the hook that makes
    /// the virtual list peer visible in NVDA's UIA tree walk.
    override this.GetChildrenCore() =
        match currentListPeer with
        | Some lp ->
            let list = ResizeArray<AutomationPeer>(1)
            list.Add(lp :> AutomationPeer)
            list
        | None -> base.GetChildrenCore()

    /// Cycle 37b — promotes the 37a stub to peer-state update.
    /// Called from `TerminalView.AnnounceRawPayload` on the WPF
    /// UI thread (via the 37a `Dispatcher.Invoke` wrapper).
    /// Mutates `currentListPeer` + raises StructureChanged on
    /// the parent + delegates per-item selection to the active
    /// list peer.
    member this.UpdateSelectionState(payload: SelectionRawPayload) =
        match payload.Kind with
        | "shown" ->
            let lp = TerminalListAutomationPeer(this, payload, writePtyBytes)
            currentListPeer <- Some lp
            this.RaiseAutomationEvent(AutomationEvents.StructureChanged)
        | "item" ->
            match currentListPeer with
            | Some lp -> lp.UpdateSelection(payload.SelectedIndex)
            | None ->
                // SelectionItem arrived without a preceding
                // SelectionShown — defensive skip. Per the
                // detector burst protocol, SelectionShown
                // always precedes SelectionItem; this branch
                // catches state drift only.
                ()
        | "dismissed" ->
            currentListPeer <- None
            this.RaiseAutomationEvent(AutomationEvents.StructureChanged)
        | _ ->
            // Unknown Kind — forward-compat: future selection
            // kinds (e.g. multi-select pickers) land here without
            // throwing.
            ()

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
            // Cycle 45 backlog (docs/USER-SETTINGS.md "NVDA Read
            // Current Line follows the cmd cursor"): the
            // ITextProvider currently exposes the screen grid as
            // a single Document range usable for the review
            // cursor, but does NOT track a system-caret position.
            // NVDA's "Read Current Line" (default NVDA+Up Arrow)
            // and its keyboard-echo path both want a caret —
            // without one they fall back to "wherever the review
            // cursor last sat", which drifts away from the cmd
            // input cursor's actual row.
            //
            // Proper fix: implement
            // `ITextRangeProvider.GetCaretRange` (or equivalent)
            // backed by `Screen.Cursor.Row` / `Screen.Cursor.Col`,
            // and fire `AutomationEvents.TextSelectionChangedEvent`
            // when those values change (Screen already exposes
            // `SequenceNumber` and `ModeChanged`; a parallel
            // CursorChanged event would feed this peer). That
            // unifies multiple Cycle 45-era issues — nav-echo
            // (#265 / #266 wouldn't be needed; NVDA's native
            // keyboard echo would Just Work), read-current-line,
            // and review-cursor-vs-input-cursor coherence. Out of
            // scope for Cycle 45; scope as its own cycle when
            // bandwidth allows.
            let result : obj | null = textProvider
            result
        | _ -> base.GetPattern(patternInterface)

/// Snapshot-rendering helpers shared by `TerminalTextRange`.
/// Kept module-level so test code (and any future range types)
/// can reuse the row-flattening rule without going through the
/// range API.
module internal SnapshotText =

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
type internal TerminalTextRange(
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
    static member internal IsWordSeparator(cell: Cell) : bool =
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
    ///
    /// Audit-cycle SR-2: every `rows.[r].[c]` access is guarded
    /// against jagged snapshots (`c >= rows.[r].Length`). Today's
    /// `Screen.SnapshotRows` returns uniform rows, but
    /// `TerminalTextRange` doesn't enforce uniformity at the
    /// constructor and a future refactor (e.g. ragged scrollback)
    /// would re-open an `IndexOutOfRangeException` DoS class.
    static member internal WordEndFrom
            (rows: Cell[][]) (cols: int)
            (r: int) (c: int) : int * int =
        let mutable r = r
        let mutable c = c
        let mutable stop = false
        while not stop && r < rows.Length do
            if c >= cols || c >= rows.[r].Length then
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
    static member internal NextWordStart
            (rows: Cell[][]) (cols: int)
            (r: int) (c: int) : int * int =
        let mutable r = r
        let mutable c = c
        // Audit-cycle SR-2: jagged-snapshot guard. Treat
        // `c >= rows.[r].Length` as end-of-row so a short row
        // can't trigger `IndexOutOfRangeException` at the
        // `rows.[r].[c]` access below.
        let onValidCell () =
            r < rows.Length
            && c < cols
            && c < rows.[r].Length
        let advanceOne () =
            c <- c + 1
            if c >= cols then
                c <- 0
                r <- r + 1
            elif r < rows.Length && c >= rows.[r].Length then
                c <- 0
                r <- r + 1
        // If we're currently on a non-separator cell, skip
        // forward through the rest of this word first.
        let mutable inWord =
            onValidCell ()
            && not (TerminalTextRange.IsWordSeparator(rows.[r].[c]))
        while inWord do
            advanceOne ()
            inWord <-
                onValidCell ()
                && not (TerminalTextRange.IsWordSeparator(rows.[r].[c]))
        // Now skip the separator run.
        let mutable inSep =
            onValidCell ()
            && TerminalTextRange.IsWordSeparator(rows.[r].[c])
        while inSep do
            advanceOne ()
            inSep <-
                onValidCell ()
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
    static member internal PrevWordStart
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
        // Audit-cycle SR-2: jagged-snapshot guard. Each access
        // to `rows.[r].[c]` (and the peek-back `rows.[pr].[pc]`)
        // is guarded against `c >= rows.[r].Length` so a short
        // row can't trigger `IndexOutOfRangeException`.
        // Step back one to start, since we want the position
        // BEFORE the current one.
        retreatOne ()
        // Skip separator run going backward.
        while not (atOrigin ())
              && r < rows.Length
              && c < cols
              && c < rows.[r].Length
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
               || pc >= rows.[pr].Length
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
                    // Audit-cycle SR-2: widen to int64 before the
                    // `curIdx + count` add so a hostile or
                    // accidental `count = int.MinValue` can't
                    // underflow int32 silently and slip past the
                    // `max 0` clamp. Same observed clamping
                    // behaviour for legitimate inputs; underflow
                    // class disappears.
                    let totalCells = rowCount * cols
                    // Position-as-cell-index: r*cols + c
                    let curIdx =
                        (max 0 (min (rowCount - 1) startRow)) * cols
                        + (max 0 (min cols startCol))
                    let target64 =
                        max 0L (min (int64 totalCells) (int64 curIdx + int64 count))
                    let targetIdx = int target64
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
                        // Audit-cycle SR-2: int64 widening, see
                        // matching note in `Move`.
                        let totalCells = rowCount * cols
                        let curIdx =
                            (max 0 (min (rowCount - 1) curR)) * cols
                            + (max 0 (min cols curC))
                        let target64 =
                            max 0L (min (int64 totalCells) (int64 curIdx + int64 count))
                        let targetIdx = int target64
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
type internal TerminalTextProvider(screenSource: Func<Screen | null>) =

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
            // SessionModel Tier 1.B added cursor position to the
            // SnapshotRows tuple; UIA peer doesn't need cursor
            // position for ITextRangeProvider, so discard with `_`.
            let seqNum, _, rows = screen.SnapshotRows(0, screen.Rows)
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
