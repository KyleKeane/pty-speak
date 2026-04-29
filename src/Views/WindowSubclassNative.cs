using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation.Provider;

namespace PtySpeak.Views;

/// <summary>
/// Win32 window-subclass infrastructure for intercepting messages
/// destined for the WPF main window. Stage 4 host point for the
/// Text-pattern raw provider: when a UIA client sends
/// <c>WM_GETOBJECT</c> with <c>lParam</c> equal to
/// <c>UiaRootObjectId</c> (-25, used by UIA3 / Inspect.exe / NVDA)
/// or <c>OBJID_CLIENT</c> (-4, used by legacy MSAA clients), we
/// hand back our <see cref="TerminalRawProvider"/> via
/// <c>UiaReturnRawElementProvider</c>. For every other object id
/// (and for clients that don't bind a raw provider), the hook
/// calls <c>DefSubclassProc</c> so WPF's existing peer tree
/// continues to work.
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
    /// UIA object id (<c>UiaRootObjectId</c>) used by modern UIA
    /// clients (UIA3 / Inspect.exe / NVDA / FlaUI's UIA3 binding)
    /// to query for an <c>IRawElementProviderSimple</c>. From
    /// <c>UIAutomationCore.h</c>: <c>#define UiaRootObjectId -25</c>.
    /// CI iteration on PR #56 established this is the id UIA3
    /// actually uses; MSAA-style clients use <see cref="OBJID_CLIENT"/>
    /// (-4) instead, which UIA3 never queries here.
    /// </summary>
    private const int UiaRootObjectId = -25;

    /// <summary>
    /// Legacy MSAA client-area object id from <c>winuser.h</c>:
    /// <c>OBJID_CLIENT</c> = <c>(LONG)0xFFFFFFFC</c> = -4.
    /// Kept in the matched set so MSAA-only screen readers
    /// (older NVDA / JAWS legacy mode) still get our provider
    /// even though UIA3 uses <see cref="UiaRootObjectId"/>.
    /// </summary>
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

            // Object-id matching: UIA3 dispatches with
            // UiaRootObjectId (-25), MSAA dispatches with
            // OBJID_CLIENT (-4), and Windows extends each
            // differently into the 64-bit LPARAM:
            //
            //   UiaRootObjectId from UIA3 → 0xFFFFFFFFFFFFFFE7 (sign-extended -25)
            //   OBJID_CLIENT from MSAA  → 0x00000000FFFFFFFC (zero-extended)
            //
            // PR #56's diagnostic dump established the
            // sign/zero-extension discrepancy on a real
            // windows-latest runner. Casting `lParam.ToInt64()`
            // through `(int)` truncates to the low 32 bits and
            // recovers the same negative integer regardless of
            // how Windows extended it; comparing that against
            // the int-typed constants is then trivially correct.
            int objId = unchecked((int)lParam.ToInt64());
            if (_rawProvider is not null
                && (objId == UiaRootObjectId || objId == OBJID_CLIENT))
            {
                return UiaReturnRawElementProvider(hWnd, wParam, lParam, _rawProvider);
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
