using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation.Provider;

namespace PtySpeak.Views;

/// <summary>
/// Win32 window-subclass infrastructure for intercepting messages
/// destined for the WPF main window. Stage 4 host point for the
/// MSAA-side Text-pattern path: when a legacy MSAA client sends
/// <c>WM_GETOBJECT</c> with <c>lParam == OBJID_CLIENT</c> we hand
/// back our <see cref="TerminalRawProvider"/> via
/// <c>UiaReturnRawElementProvider</c>. UIA3 / NVDA arrive via
/// <c>UiaRootObjectId</c> instead, which we deliberately do
/// <em>not</em> intercept here — see the <see cref="OBJID_CLIENT"/>
/// docstring and <c>TerminalAutomationPeer</c> for the UIA3 path.
/// For every other message, the hook calls
/// <c>DefSubclassProc</c> so WPF's existing peer tree continues
/// to work.
/// </summary>
/// <remarks>
/// <para>
/// The standard <c>SetWindowSubclass</c> / <c>DefSubclassProc</c>
/// API in <c>comctl32.dll</c> is the documented WPF-friendly way
/// to subclass a window without the <c>SetWindowLongPtr</c>
/// race conditions of the older approach. The subclass proc gets
/// added to the window's subclass chain; it processes the message
/// it cares about and calls <c>DefSubclassProc</c> to defer to
/// the rest of the chain.
/// </para>
/// <para>
/// Logging: the side-channel temp file written from PR A
/// (`%TEMP%/ptyspeak-wm-getobject-&lt;pid&gt;.log`) is preserved
/// so the existing
/// <c>tests/Tests.Ui/WindowSubclassTests.fs</c> regression test
/// keeps passing. Every <c>WM_GETOBJECT</c> still appends an
/// entry; the difference is that for OBJID_CLIENT we now also
/// return a real provider rather than just deferring.
/// </para>
/// </remarks>
internal static class WindowSubclassNative
{
    private const uint WM_GETOBJECT = 0x003D;

    /// <summary>
    /// Legacy MSAA client-area object id from <c>winuser.h</c>:
    /// <c>OBJID_CLIENT</c> = <c>(LONG)0xFFFFFFFC</c> = -4. Some
    /// MSAA-only screen-reader paths still query with this id.
    /// </summary>
    /// <remarks>
    /// PR #56 originally also matched <c>UiaRootObjectId</c> (-25,
    /// the modern UIA3 client query) and returned our provider
    /// for it. CI proved that approach broke the entire UIA tree:
    /// <c>UIA3Automation.FromHandle</c> returned an unexpected
    /// COM HRESULT, and the peer-tree tests
    /// (<c>AutomationPeerTests</c>, <c>WindowSubclassTests</c>)
    /// regressed. The root cause is that
    /// <c>WM_GETOBJECT(UiaRootObjectId)</c> expects a provider
    /// implementing <c>IRawElementProviderFragmentRoot</c> with a
    /// real navigation surface — <c>IRawElementProviderSimple</c>
    /// alone is insufficient because UIA can't traverse from it
    /// into the WPF host tree even when
    /// <c>HostRawElementProvider</c> is wired up.
    ///
    /// The proper Stage 4 path therefore goes through the WPF
    /// peer tree instead of intercepting <c>UiaRootObjectId</c>:
    /// see the <c>GetPattern</c>-override exploration in
    /// <see cref="TerminalAutomationPeer"/>. This hook is kept
    /// for the legacy MSAA path only, which is a strict
    /// improvement (no regression) over the no-hook baseline.
    /// </remarks>
    private const int OBJID_CLIENT = -4;

    /// <summary>
    /// Stable id used to identify this subclass on the chain;
    /// must match the value passed to <c>RemoveWindowSubclass</c>
    /// on cleanup. The bit pattern is just a memorable constant —
    /// <c>SetWindowSubclass</c> uses it as an opaque key.
    /// </summary>
    private static readonly nuint SubclassId = 0xCAFE_BABE;

    /// <summary>
    /// Subclass proc keeps a reference here so the marshaller's
    /// thunk pointer stays alive for the lifetime of the
    /// installation. If the delegate is GC'd while the subclass
    /// is still on the chain, the next message dispatch crashes
    /// the process with an access violation. The retention is
    /// process-global because we install on exactly one window
    /// (<see cref="MainWindow"/>) and uninstall on its
    /// <c>Closed</c> event.
    /// </summary>
    private static SubclassProc? _retainedProc;

    /// <summary>
    /// Side-channel log path, computed once at install time so
    /// the FlaUI test can read it back.
    /// </summary>
    private static string? _logPath;

    /// <summary>
    /// Raw provider returned for OBJID_CLIENT. Set by
    /// <see cref="InstallHook"/>; left null when the hook is
    /// installed without a provider (e.g. early-startup tests
    /// that exercise the spike path only).
    /// </summary>
    private static IRawElementProviderSimple? _rawProvider;

    /// <summary>
    /// Subclass procedure signature per
    /// https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nc-commctrl-subclassproc.
    /// </summary>
    public delegate nint SubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass,
        nint dwRefData);

    [DllImport("comctl32.dll")]
    public static extern nint DefSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool RemoveWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass);

    /// <summary>
    /// Computes the path that the hook writes to when
    /// <c>WM_GETOBJECT</c> fires. Includes the current process id
    /// so concurrent test runs don't collide.
    /// </summary>
    public static string LogPathForProcess(int processId) =>
        Path.Combine(
            Path.GetTempPath(),
            $"ptyspeak-wm-getobject-{processId}.log");

    /// <summary>
    /// Install the subclass on <paramref name="hwnd"/>. Truncates
    /// any prior log file from earlier runs so the FlaUI test
    /// only sees entries from this process.
    /// </summary>
    /// <param name="rawProvider">
    /// Provider returned to UIA when <c>WM_GETOBJECT</c> arrives
    /// with <c>lParam == OBJID_CLIENT</c>. Pass <c>null</c> to
    /// keep the spike-only behaviour (log entry, then defer).
    /// </param>
    public static void InstallHook(nint hwnd, IRawElementProviderSimple? rawProvider = null)
    {
        _rawProvider = rawProvider;
        _logPath = LogPathForProcess(System.Environment.ProcessId);

        // Best-effort log truncation. Failure is not fatal — the
        // worst case is the test sees a stale file from a previous
        // run, which the test mitigates by tracking line counts
        // rather than just checking existence.
        try { File.WriteAllText(_logPath, string.Empty); }
        catch { /* swallowed by design */ }

        _retainedProc = SubclassProcImpl;
        SetWindowSubclass(hwnd, _retainedProc, SubclassId, nint.Zero);
    }

    /// <summary>
    /// Remove the subclass installed by <see cref="InstallHook"/>.
    /// Idempotent — safe to call from <c>Window.Closed</c>.
    /// </summary>
    public static void UninstallHook(nint hwnd)
    {
        if (_retainedProc is not null)
        {
            RemoveWindowSubclass(hwnd, _retainedProc, SubclassId);
            _retainedProc = null;
        }
        _rawProvider = null;
    }

    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    private static extern nint UiaReturnRawElementProvider(
        nint hwnd,
        nint wParam,
        nint lParam,
        IRawElementProviderSimple el);

    private static nint SubclassProcImpl(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nint dwRefData)
    {
        if (uMsg == WM_GETOBJECT && _logPath is not null)
        {
            // Best-effort logging. Even if the I/O throws, we
            // must call DefSubclassProc so the message dispatch
            // continues — failing here would silently break UIA.
            try
            {
                File.AppendAllText(
                    _logPath,
                    $"WM_GETOBJECT lParam=0x{lParam.ToInt64():X16} at {System.DateTime.UtcNow:O}\n");
            }
            catch { /* swallowed by design */ }

            // Object-id matching: only OBJID_CLIENT (-4) is
            // safe to intercept — the modern UIA3 query
            // (UiaRootObjectId, -25) requires a provider
            // implementing IRawElementProviderFragmentRoot, and
            // returning a simple provider for it breaks UIA's
            // tree navigation entirely (CI confirmed). Casting
            // `lParam.ToInt64()` through `(int)` truncates to
            // the low 32 bits and recovers the negative
            // integer regardless of how Windows extended it
            // (sign- vs zero-extended LPARAM).
            int objId = unchecked((int)lParam.ToInt64());
            if (_rawProvider is not null && objId == OBJID_CLIENT)
            {
                return UiaReturnRawElementProvider(hWnd, wParam, lParam, _rawProvider);
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
