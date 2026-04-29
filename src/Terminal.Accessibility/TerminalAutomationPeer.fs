namespace Terminal.Accessibility

open System
open System.Text
open System.Windows
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Terminal.Core
open Terminal.Accessibility.Interop

/// Stage 4a — minimal UIA surface.
///
/// Layout:
///   * `TerminalTextRange` implements `ITextRangeProvider` for a
///     single immutable snapshot of N rows. `GetText` works (rows
///     joined with `\n`); navigation methods (`Move`,
///     `MoveEndpointByUnit`, `Compare`, `Clone`,
///     `ExpandToEnclosingUnit`) are stubs that return `0` /
///     `false` / `()` — PR 4b implements them.
///   * `TerminalTextProvider` implements `ITextProvider` for a
///     single screen. `DocumentRange` returns a range covering all
///     rows, captured under `Screen.SnapshotRows` so the read is
///     thread-safe against the WPF-Dispatcher mutator.
///   * `TerminalAutomationPeer` extends `FSharpAutomationPeerBase`
///     (the C# shim from Terminal.Accessibility.Interop) so it can
///     expose `PatternInterface.Text` without hitting the F#
///     inherited-override resolution failure documented in the
///     spike (PR #47 → SESSION-HANDOFF.md Stage 4 sketch).
///
/// The peer reads `Screen` lazily via the owning `TerminalView` so
/// UIA queries that arrive before the screen is wired (theoretical
/// race — UIA typically queries lazily) get an empty document
/// rather than a crash.

/// Encodes a snapshot of `Cell[][]` rows as a flat string, joining
/// rows with `\n`. Trailing blank cells in each row are preserved
/// so column offsets are stable across calls; PR 4b's navigation
/// will operate on the same encoding.
module internal SnapshotText =

    let render (rows: Cell[][]) : string =
        if rows.Length = 0 then
            ""
        else
            let sb = StringBuilder()
            for r in 0 .. rows.Length - 1 do
                if r > 0 then
                    sb.Append('\n') |> ignore
                let row = rows.[r]
                for c in 0 .. row.Length - 1 do
                    sb.Append(row.[c].Ch.ToString()) |> ignore
            sb.ToString()

/// `ITextRangeProvider` over an immutable row snapshot. The range
/// covers `rows[0..]` joined with `\n`; PR 4b refines this with
/// per-endpoint character offsets so `Move` / `MoveEndpointByUnit`
/// can advance one unit at a time without recomputing the
/// snapshot.
type internal TerminalTextRange(sequence: int64, rows: Cell[][]) =

    /// Sequence number captured at construction. Stage 5 uses this
    /// to detect ranges over stale snapshots and invalidate them
    /// before raising `TextChanged` / `Notification` events.
    member _.Sequence = sequence

    /// The snapshot the range was constructed over. Internal so
    /// tests + PR 4b can read it without re-snapshotting.
    member _.Rows = rows

    interface ITextRangeProvider with
        member this.Clone() =
            TerminalTextRange(sequence, rows) :> ITextRangeProvider

        member _.Compare(_: ITextRangeProvider) = false
        member _.CompareEndpoints(_, _, _) = 0
        member _.ExpandToEnclosingUnit(_: TextUnit) = ()
        member _.FindAttribute(_, _, _) = Unchecked.defaultof<ITextRangeProvider>
        member _.FindText(_, _, _) = Unchecked.defaultof<ITextRangeProvider>
        member _.GetAttributeValue(_) = AutomationElementIdentifiers.NotSupported
        member _.GetBoundingRectangles() = Array.empty<double>
        member _.GetEnclosingElement() = Unchecked.defaultof<IRawElementProviderSimple>

        member _.GetText(maxLength: int) =
            let rendered = SnapshotText.render rows
            if maxLength < 0 || maxLength >= rendered.Length then
                rendered
            else
                rendered.Substring(0, maxLength)

        member _.Move(_, _) = 0
        member _.MoveEndpointByUnit(_, _, _) = 0
        member _.MoveEndpointByRange(_, _, _) = ()
        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()
        member _.ScrollIntoView(_: bool) = ()
        member _.GetChildren() = Array.empty<IRawElementProviderSimple>

/// `ITextProvider` for a single `Screen`. `DocumentRange` returns a
/// `TerminalTextRange` over a fresh `SnapshotRows` capture, so each
/// UIA call sees a consistent point-in-time view of the buffer.
///
/// `screenSource` is `System.Func<Screen | null>` (NOT F#'s native
/// `unit -> Screen | null`) because the consumer is C#
/// (`TerminalView.OnCreateAutomationPeer` passes a `() => _screen`
/// lambda). C# auto-converts the lambda to `Func<Screen?>`; F#'s
/// own function type (`FSharpFunc<unit, Screen>`) wouldn't accept
/// the lambda without an explicit `FuncConvert.ToFSharpFunc` wrap
/// at the call site. The thunk shape lets us defer the
/// screen-lookup until the screen is reachable (Stage 3b sets the
/// screen on `Window.Loaded`, after the WPF AutomationPeer might
/// already exist).
type internal TerminalTextProvider(screenSource: System.Func<Screen | null>) =

    interface ITextProvider with
        member _.DocumentRange =
            match screenSource.Invoke() with
            | null ->
                TerminalTextRange(0L, Array.empty<Cell[]>) :> ITextRangeProvider
            | screen ->
                let seq, rows = screen.SnapshotRows(0, screen.Rows)
                TerminalTextRange(seq, rows) :> ITextRangeProvider

        member _.SupportedTextSelection = SupportedTextSelection.None
        member _.GetSelection() = Array.empty<ITextRangeProvider>
        member _.GetVisibleRanges() = Array.empty<ITextRangeProvider>
        member _.RangeFromChild(_) = Unchecked.defaultof<ITextRangeProvider>
        member _.RangeFromPoint(_) = Unchecked.defaultof<ITextRangeProvider>

/// WPF `AutomationPeer` for `TerminalView`. Inherits from the C#
/// shim (`FSharpAutomationPeerBase`) so the `GetPatternCore`
/// override lands in C# and F# only has to override the new
/// abstract `GetPatternForFsharp`. See the spike write-up in
/// `docs/SESSION-HANDOFF.md` for the why.
type TerminalAutomationPeer
        (owner: FrameworkElement, screenSource: System.Func<Screen | null>) =
    inherit FSharpAutomationPeerBase(owner)

    let textProvider = TerminalTextProvider(screenSource) :> ITextProvider

    override _.GetAutomationControlTypeCore() = AutomationControlType.Document
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true
    override _.IsContentElementCore() = true

    override _.GetPatternForFsharp(patternInterface: PatternInterface) : obj | null =
        if patternInterface = PatternInterface.Text then
            box textProvider
        else
            null
