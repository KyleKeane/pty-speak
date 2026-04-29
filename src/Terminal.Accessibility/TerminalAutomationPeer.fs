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
    // F# 9's `obj | null` matches the C# nullable-annotated base
    // signature `protected override object? GetPatternCore(...)`. The
    // first attempt at this spike used `Unchecked.defaultof<obj>` with
    // no return-type annotation and got FS0855 "no abstract member
    // found that corresponds to this override" — F# couldn't unify
    // a non-nullable `obj` return with the base's nullable signature,
    // and reported it as a missing override target rather than a
    // nullability error.
    override _.GetPatternCore(patternInterface: PatternInterface) : obj | null =
        null
