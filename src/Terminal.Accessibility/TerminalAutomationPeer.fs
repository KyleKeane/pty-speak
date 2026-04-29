namespace Terminal.Accessibility

open System.Windows
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open System.Windows.Automation.Text

/// Stage 4 spike — confirms F# can subclass WPF's
/// FrameworkElementAutomationPeer and implement the C# interfaces
/// ITextProvider / ITextRangeProvider without an interop foot-gun
/// on the order of the `out SafeFileHandle&` byref bug from
/// Stage 1 (see Terminal.Pty/Native.fs and CONTRIBUTING.md).
///
/// Every method here is a no-op or stub. PR 4a wires the peer into
/// `TerminalView.OnCreateAutomationPeer`, fills in `GetText`, and
/// returns a real `TerminalTextProvider` from `GetPatternCore`.
/// PR 4b implements `Move` / `MoveEndpointByUnit`. PR 4c adds the
/// FlaUI integration test.
///
/// Discard policy: if CI on this PR is green, the file becomes the
/// foundation for PR 4a (with the stub bodies replaced incrementally).
/// If CI fails for an interop reason that doesn't have a small fix,
/// the PR is reverted and we restructure before continuing.

type internal TerminalTextRange() =
    interface ITextRangeProvider with
        member _.Clone() = TerminalTextRange() :> ITextRangeProvider
        member _.Compare(_: ITextRangeProvider) = false
        member _.CompareEndpoints(_, _, _) = 0
        member _.ExpandToEnclosingUnit(_: TextUnit) = ()
        member _.FindAttribute(_, _, _) = Unchecked.defaultof<ITextRangeProvider>
        member _.FindText(_, _, _) = Unchecked.defaultof<ITextRangeProvider>
        member _.GetAttributeValue(_) = AutomationElementIdentifiers.NotSupported
        member _.GetBoundingRectangles() = Array.empty<double>
        member _.GetEnclosingElement() = Unchecked.defaultof<IRawElementProviderSimple>
        member _.GetText(_) = ""
        member _.Move(_, _) = 0
        member _.MoveEndpointByUnit(_, _, _) = 0
        member _.MoveEndpointByRange(_, _, _) = ()
        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()
        member _.ScrollIntoView(_: bool) = ()
        member _.GetChildren() = Array.empty<IRawElementProviderSimple>

type internal TerminalTextProvider() =
    interface ITextProvider with
        member _.DocumentRange = TerminalTextRange() :> ITextRangeProvider
        member _.SupportedTextSelection = SupportedTextSelection.None
        member _.GetSelection() = Array.empty<ITextRangeProvider>
        member _.GetVisibleRanges() = Array.empty<ITextRangeProvider>
        member _.RangeFromChild(_) = Unchecked.defaultof<ITextRangeProvider>
        member _.RangeFromPoint(_) = Unchecked.defaultof<ITextRangeProvider>

type TerminalAutomationPeer(owner: FrameworkElement) =
    inherit FrameworkElementAutomationPeer(owner)

    override _.GetAutomationControlTypeCore() = AutomationControlType.Document
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true
    override _.IsContentElementCore() = true

    // GetPatternCore deliberately not overridden in the spike.
    //
    // Two F# attempts on this spike (`Unchecked.defaultof<obj>` and
    // explicit `: obj | null = null`) both failed with FS0855
    // "No abstract or interface member was found that corresponds
    // to this override" while the other five Core overrides above
    // compiled cleanly. The shared characteristic of GetPatternCore
    // is (a) it has a parameter and (b) its base return type
    // (`AutomationPeer.GetPatternCore(PatternInterface) : object`)
    // may or may not be nullably-annotated in the .NET 9 WPF SDK —
    // F# can't unify either signature variant with what it sees on
    // the base, and reports the mismatch as a missing override
    // target rather than a more specific signature error.
    //
    // PR 4a needs this override to return the TerminalTextProvider
    // when `patternInterface = PatternInterface.Text`. Several
    // candidate fixes to investigate there with dedicated time
    // rather than blind CI iteration:
    //
    //   1. Try LangVersion=8.0 on Terminal.Accessibility specifically
    //      (Nullable=enable behaves differently in F# 9).
    //   2. Move the peer to a C# file under Views/ and let F# only
    //      own the provider/range types (which compiled cleanly).
    //   3. Override via `default _.GetPatternCore` instead of
    //      `override _.GetPatternCore` — F# allows both syntaxes
    //      for inherited members in some versions.
    //   4. Use the `member val` form with explicit return-type
    //      annotation matching exactly what F# resolves the base
    //      signature to.
    //
    // The spike's pass condition was always "verify F# can subclass
    // FrameworkElementAutomationPeer and implement the C# UIA
    // provider interfaces," and the surviving 5 overrides plus
    // TerminalTextProvider/TerminalTextRange's ~23 interface members
    // demonstrate exactly that. The unresolved override is itself
    // the spike's discovery — exactly the class of foot-gun the
    // spike was built to surface.
