using System.IO;
using System.Runtime.InteropServices;

namespace PtySpeak.Views;

/// <summary>
/// Win32 window-subclass infrastructure for intercepting messages
/// destined for the WPF main window. Stage-4 follow-up spike (PR
/// after the foundation arc #51/#52/#53) verifies that
/// <c>WM_GETOBJECT</c> is reachable from a subclass proc under
/// WPF's message pump on <c>windows-latest</c> CI runners — the
/// pre-condition for Issue #49 option 1's
/// <c>IRawElementProviderSimple</c> exposure path.
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
/// For the spike, the hook only observes <c>WM_GETOBJECT</c> and
/// writes a side-channel entry to a temp file so the FlaUI test
/// can verify the hook fired. The hook returns
/// <c>DefSubclassProc</c> for everything (no message override),
/// so WPF's existing UIA tree continues to work unchanged.
/// PR B (raw-provider implementation) replaces the
/// <c>WM_GETOBJECT</c> handler with one that returns our own
/// <c>IRawElementProviderSimple</c> via
/// <c>UiaReturnRawElementProvider</c>; until then this is purely
/// a "did the message reach our hook?" probe.
/// </para>
/// </remarks>
internal static class WindowSubclassNative
{
    private const uint WM_GETOBJECT = 0x003D;

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
    public static void InstallHook(nint hwnd)
    {
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
    }

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
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
