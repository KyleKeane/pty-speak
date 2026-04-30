using System.Windows;
using System.Windows.Interop;

namespace PtySpeak.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Install the WM_GETOBJECT subclass hook as soon as the
        // HWND exists. SourceInitialized fires after CreateWindow
        // but before the window is shown, which is the documented
        // earliest safe point for native interop. The companion
        // Closed handler removes the subclass so the retained
        // delegate can be released — strictly belt-and-suspenders
        // since the process is exiting anyway, but keeps the
        // pattern clean for future windows that don't have the
        // process-exit guarantee.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var rawProvider = new TerminalRawProvider(
                hwnd,
                TerminalSurface.TextProvider);
            WindowSubclassNative.InstallHook(hwnd, rawProvider);
        };

        Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowSubclassNative.UninstallHook(hwnd);
        };

        // Move keyboard focus into the TerminalSurface as soon
        // as the window is loaded. Without this, focus stays on
        // the Window itself: NVDA announces "pty-speak terminal,
        // window" and the review cursor anchors to the Window
        // (which has no Text pattern), so NVDA can never reach
        // the Document-role TerminalView peer's text content
        // even though the pattern surface is wired correctly.
        // Preview.21 install smoke established this exact
        // failure mode — Test 1 of the smoke matrix announced
        // "window" instead of "document," and Tests 2-4 (review
        // cursor read / navigate) all returned "blank" because
        // the cursor was attached to the wrong element.
        //
        // `Loaded` is the documented earliest point at which
        // `Focus()` succeeds reliably (the visual tree is
        // realised, focus scopes resolved). The compose seam in
        // Terminal.App's `Program.fs` already handles screen
        // attachment in `Window.Loaded`; this fits naturally
        // alongside.
        Loaded += (_, _) => TerminalSurface.Focus();
    }
}
