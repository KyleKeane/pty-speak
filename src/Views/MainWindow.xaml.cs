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
            WindowSubclassNative.InstallHook(hwnd);
        };

        Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowSubclassNative.UninstallHook(hwnd);
        };
    }
}
