namespace Terminal.Accessibility

open System.Windows
open System.Windows.Automation.Peers

/// Stage 4a (reduced scope) — UIA peer that exposes
/// `TerminalView` to the WPF Automation tree as a Document with
/// the right ClassName and Name. NVDA / Inspect.exe will find the
/// element, see its role, and read the static identity.
///
/// Stage 4a originally aimed to also expose the Text pattern by
/// overriding `AutomationPeer.GetPatternCore`. Investigation in
/// PR #48 (and the spike that preceded it) showed that
/// `AutomationPeer.GetPatternCore` is not reachable from any
/// external assembly in the .NET 9 WPF reference assembly set —
/// the C# compiler reports CS0117 "FrameworkElementAutomationPeer
/// does not contain a definition for 'GetPatternCore'" when an
/// override or even a `base.GetPatternCore(...)` call is
/// attempted, and the F# spike's FS0855 was the same finding via
/// a different error code. Microsoft's documented examples that
/// override the method appear to compile only inside Microsoft's
/// own WPF assemblies (where the protected members are visible);
/// the public reference assemblies surface the type without the
/// overridable protected member.
///
/// Text-pattern exposure is therefore deferred to a follow-up
/// (tracked in `docs/SESSION-HANDOFF.md` Stage 4 sketch). The
/// likely path is implementing `IRawElementProviderSimple`
/// directly on `TerminalView`, bypassing the `AutomationPeer`
/// hierarchy that wraps the protected metadata. That work
/// happens with focused investigation rather than CI iteration.
///
/// The five Core overrides below DO compile cleanly — proven by
/// the spike PR #47. They give the peer everything Stage 4a's
/// reduced scope needs: a Document role, a stable ClassName for
/// FlaUI lookup, a meaningful Name, and visibility in the
/// control + content trees.
type TerminalAutomationPeer(owner: FrameworkElement) =
    inherit FrameworkElementAutomationPeer(owner)

    override _.GetAutomationControlTypeCore() = AutomationControlType.Document
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true
    override _.IsContentElementCore() = true
