using System.Windows;
using System.Windows.Automation.Peers;

namespace Terminal.Accessibility.Interop;

/// <summary>
/// C# shim that lets an F# subclass expose UI Automation patterns
/// without overriding <see cref="AutomationPeer.GetPatternCore"/> directly.
/// </summary>
/// <remarks>
/// <para>
/// The Stage 4 spike (PR #47) discovered that F# under
/// <c>Nullable=enable</c> cannot override
/// <see cref="AutomationPeer.GetPatternCore"/> from
/// <see cref="FrameworkElementAutomationPeer"/> directly: the F#
/// compiler reports FS0855 "no abstract or interface member was
/// found that corresponds to this override" regardless of whether
/// the return type is annotated <c>obj</c> or <c>obj | null</c>.
/// The other five parameterless <c>*Core</c> overrides
/// (<see cref="AutomationPeer.GetAutomationControlTypeCore"/> and
/// friends) compile cleanly. The shared characteristic of the
/// failing override is that it has a parameter and its base return
/// type's nullability is opaque to the F# compiler.
/// </para>
/// <para>
/// This class takes the <c>GetPatternCore</c> override hit on F#'s
/// behalf and exposes a new abstract method
/// <see cref="GetPatternForFsharp"/> that F# subclasses CAN
/// override (no parameterized inherited override resolution
/// involved — F# is just implementing a new abstract method).
/// </para>
/// <para>
/// Add new shim members here only when an F# subclass hits the same
/// class of inherited-override resolution failure. Pure
/// accessibility logic belongs in the F# project.
/// </para>
/// </remarks>
public abstract class FSharpAutomationPeerBase : FrameworkElementAutomationPeer
{
    protected FSharpAutomationPeerBase(FrameworkElement owner) : base(owner)
    {
    }

    /// <summary>
    /// Sealed so F# subclasses can't try (and fail) to re-override
    /// it — they should override <see cref="GetPatternForFsharp"/>
    /// instead.
    /// </summary>
    protected sealed override object? GetPatternCore(PatternInterface patternInterface)
        => GetPatternForFsharp(patternInterface);

    /// <summary>
    /// Returns the provider for the requested UIA pattern, or
    /// <c>null</c> if the pattern is not implemented. Called by
    /// <see cref="GetPatternCore"/>.
    /// </summary>
    protected abstract object? GetPatternForFsharp(PatternInterface patternInterface);
}
