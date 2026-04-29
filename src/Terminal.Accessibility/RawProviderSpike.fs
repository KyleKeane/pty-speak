namespace Terminal.Accessibility

open System.Windows.Automation.Peers
open System.Windows.Automation.Provider

/// Foundation spike #3 (last of three before committing to
/// Issue #49 option 1) — confirms F# can implement
/// `System.Windows.Automation.Provider.IRawElementProviderSimple`
/// as cleanly as it implemented `ITextProvider` /
/// `ITextRangeProvider` in PR #47. This is the interface the
/// option-1 Text-pattern implementation will host.
///
/// The four interface members:
///   * `ProviderOptions` (property)
///   * `GetPatternProvider(int)` (method, returns the pattern
///     provider for a given UIA pattern ID, null if not supported)
///   * `GetPropertyValue(int)` (method, returns the value of a
///     UIA property, null if not supported)
///   * `HostRawElementProvider` (property, returns the WPF host
///     provider so UIA can hand off non-pattern queries to the
///     standard tree)
///
/// All members are stubs; the goal is the compile pass alone.
/// F#'s override resolution and explicit-interface implementation
/// machinery decide whether this works at type-definition time,
/// so no runtime / instantiation needed.
///
/// Discard policy: throwaway diagnostic infrastructure. If CI
/// passes, F# can host the interface and the option-1
/// implementation PR can begin. The follow-up PR deletes this
/// file. If CI fails, the failure mode tells us what's
/// incompatible before any of the WM_GETOBJECT plumbing is
/// written.

type internal RawProviderSpike() =
    interface IRawElementProviderSimple with
        member _.ProviderOptions =
            ProviderOptions.ServerSideProvider
        member _.GetPatternProvider(_: int) =
            Unchecked.defaultof<obj>
        member _.GetPropertyValue(_: int) =
            Unchecked.defaultof<obj>
        member _.HostRawElementProvider =
            Unchecked.defaultof<IRawElementProviderSimple>
