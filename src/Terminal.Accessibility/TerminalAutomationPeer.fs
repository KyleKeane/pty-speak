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
    /// return. The Text branch pattern-matches the constructor
    /// parameter on null so the upcast to non-nullable `obj` is
    /// narrowed safely — `textProvider` is `ITextProvider` from
    /// an externally-annotated assembly and F# treats the
    /// upcast source as nullable by default, which conflicts
    /// with `:> obj` (non-nullable target) without the match.
    override _.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.Text ->
            match textProvider with
            | null -> null
            | tp -> tp :> obj
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

/// UIA `ITextRangeProvider` over an immutable row snapshot.
///
/// Stage 4 (this PR) ships only `GetText`, `Clone`, and
/// `Compare` with real behaviour — those three are what NVDA
/// calls during initial document read-out. Navigation
/// (`Move`, `MoveEndpointByUnit`), endpoint manipulation,
/// selection, and attribute queries are stubbed to satisfy the
/// interface so UIA marshalling doesn't fault, but they don't
/// influence what NVDA reads in this PR. PR C (the FlaUI
/// Text-pattern integration test) verifies that `GetText`
/// returns the expected snapshot under a real UIA client; the
/// stubbed methods are exercised in later stages when caret
/// movement / SGR exposure work begins.
///
/// `Sequence` records the `Screen.SequenceNumber` at capture
/// time so a future stale-detection check can compare against
/// the current screen state. Stage 4 doesn't use it yet — the
/// snapshot is materialised once per UIA query and discarded —
/// but storing it now keeps the range structure aligned with
/// the substrate that Screen.fs already exposes.
type TerminalTextRange(sequence: int64, rows: Cell[][]) =

    member _.Sequence = sequence
    member _.Rows = rows

    interface ITextRangeProvider with

        member _.Clone() =
            TerminalTextRange(sequence, rows) :> ITextRangeProvider

        member _.Compare(other: ITextRangeProvider) : bool =
            // Two ranges are equal when they wrap the same
            // snapshot identity. Reference equality is enough
            // for Stage 4 because each `DocumentRange` call
            // returns a freshly captured snapshot — clones
            // share the underlying `rows` reference.
            match other with
            | :? TerminalTextRange as r ->
                obj.ReferenceEquals(r.Rows, rows)
            | _ -> false

        member _.CompareEndpoints(_: TextPatternRangeEndpoint, _: ITextRangeProvider, _: TextPatternRangeEndpoint) = 0
        member _.ExpandToEnclosingUnit(_: TextUnit) = ()
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
            let rendered = SnapshotText.render rows
            if maxLength < 0 || maxLength >= rendered.Length then
                rendered
            else
                rendered.Substring(0, maxLength)

        member _.Move(_: TextUnit, _: int) = 0
        member _.MoveEndpointByUnit(_: TextPatternRangeEndpoint, _: TextUnit, _: int) = 0
        member _.MoveEndpointByRange(_: TextPatternRangeEndpoint, _: ITextRangeProvider, _: TextPatternRangeEndpoint) = ()
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
            TerminalTextRange(0L, Array.empty<Cell[]>) :> ITextRangeProvider
        | screen ->
            let seqNum, rows = screen.SnapshotRows(0, screen.Rows)
            TerminalTextRange(seqNum, rows) :> ITextRangeProvider

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
