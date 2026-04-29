using System.Windows.Automation.Provider;
using Terminal.Accessibility;

namespace PtySpeak.Views;

/// <summary>
/// UIA <see cref="IRawElementProviderSimple"/> for the main
/// window's client area. Returned by the <c>WM_GETOBJECT</c>
/// subclass hook (<see cref="WindowSubclassNative"/>) when a UIA
/// client queries <c>OBJID_CLIENT</c>; for every other object id
/// the hook calls <c>DefSubclassProc</c>, leaving WPF's
/// peer-based tree (the <see cref="TerminalAutomationPeer"/>
/// reachable via <c>OnCreateAutomationPeer</c>) untouched.
/// </summary>
/// <remarks>
/// <para>
/// The raw-provider path is the workaround for the
/// <c>AutomationPeer.GetPatternCore</c>-not-reachable finding
/// from the Stage 4 foundation arc (#51 / #52 / #53). Instead of
/// adding the Text pattern through the WPF peer (which the
/// public reference assemblies don't permit), we install
/// ourselves as the OBJID_CLIENT provider and host
/// <see cref="ITextProvider"/> directly. Non-pattern queries
/// (focus, navigation, structure, properties NVDA hasn't asked
/// for via the Text pattern) get delegated to WPF through
/// <see cref="HostRawElementProvider"/>, so the existing
/// <c>Document</c> role from PR #51 keeps working.
/// </para>
/// <para>
/// PR scope: only <c>UIA_TextPatternId</c> (10024) is exposed
/// here. The constants come from the Win32
/// <c>UIAutomationCore.h</c> pattern-id table; we hard-code
/// rather than referencing the WinForms-only
/// <c>System.Windows.Automation.TextPattern.Pattern.Id</c>
/// to keep this assembly free of WinForms dependencies.
/// </para>
/// </remarks>
internal sealed class TerminalRawProvider : IRawElementProviderSimple
{
    /// <summary>
    /// UIA pattern id for <c>TextPattern</c>. Stable Microsoft
    /// constant from the UIA pattern-id enumeration; safe to
    /// hard-code.
    /// </summary>
    private const int UIA_TextPatternId = 10024;

    private readonly TerminalTextProvider _textProvider;
    private readonly nint _hwnd;

    public TerminalRawProvider(nint hwnd, TerminalTextProvider textProvider)
    {
        _hwnd = hwnd;
        _textProvider = textProvider;
    }

    /// <summary>
    /// We're a server-side provider that lives in the same
    /// process as the UI it describes. UIA wraps non-pattern
    /// requests through the host provider exposed below.
    /// </summary>
    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

    /// <summary>
    /// Returns the provider for a given UIA pattern id, or
    /// <c>null</c> when we don't host that pattern. Stage 4
    /// only exposes the Text pattern; the host provider handles
    /// any other patterns that WPF's peer tree advertises.
    /// </summary>
    public object? GetPatternProvider(int patternId)
    {
        if (patternId == UIA_TextPatternId)
        {
            return _textProvider;
        }
        return null;
    }

    /// <summary>
    /// We don't override any properties at the raw-provider
    /// level — they all come from the host provider's WPF peer
    /// (Document role, ClassName, Name, etc.). Returning
    /// <c>null</c> tells UIA "not specified, ask the host."
    /// </summary>
    public object? GetPropertyValue(int propertyId) => null;

    /// <summary>
    /// Hand UIA the WPF host provider for the same HWND so the
    /// existing peer tree (Document role, ClassName="TerminalView",
    /// Name="Terminal") is still reachable. Without this, our
    /// raw provider would shadow the peer entirely and PR #51's
    /// <c>AutomationPeerTests</c> would regress.
    /// </summary>
    public IRawElementProviderSimple? HostRawElementProvider =>
        AutomationInteropProvider.HostProviderFromHandle(_hwnd);
}
