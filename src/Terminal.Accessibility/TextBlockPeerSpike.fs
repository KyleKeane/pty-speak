namespace Terminal.Accessibility

open System.Windows
open System.Windows.Automation.Peers
open System.Windows.Controls

/// Spike — does `TextBlockAutomationPeer.GetPatternCore` have a
/// different visibility surface than
/// `FrameworkElementAutomationPeer.GetPatternCore`?
///
/// The PR #48 diagnostic established that
/// `FrameworkElementAutomationPeer.GetPatternCore` is not
/// reachable from any external assembly in the .NET 9 WPF
/// reference assembly set (CS0117 from C#, FS0855 from F#).
/// `TextBlockAutomationPeer` is a sibling subclass of
/// `UIElementAutomationPeer` and exposes the Text pattern in
/// production WPF — so its inherited `GetPatternCore` either has
/// the same visibility limit (in which case the entire
/// AutomationPeer extension model is closed to external code in
/// .NET 9 and we pivot to `IRawElementProviderSimple`) or
/// `TextBlockAutomationPeer` re-exposes the override target at a
/// less restrictive protection level (in which case Stage 4 can
/// build on it).
///
/// This file declares but never instantiates a class that
/// inherits from `TextBlockAutomationPeer` and overrides
/// `GetPatternCore`. F#'s override resolution happens at
/// type-definition time, so just having the declaration in the
/// project's compilation unit is sufficient — no UI thread, no
/// `TextBlock` instance, no runtime needed. CI compile pass tells
/// us whether the override is reachable.
///
/// Outcome policy: this file is a throwaway spike. If CI passes,
/// we've confirmed `TextBlockAutomationPeer.GetPatternCore` is
/// reachable and the next PR builds the real Stage 4 Text-pattern
/// exposure on top of it. If CI fails (FS0855), the Text-pattern
/// path through any `*AutomationPeer` subclass is closed and the
/// next PR pivots to implementing `IRawElementProviderSimple`
/// directly. Either way, this file is deleted.
type internal TextBlockPeerSpike(owner: TextBlock) =
    inherit TextBlockAutomationPeer(owner)

    override _.GetPatternCore(patternInterface: PatternInterface) : obj | null =
        null
