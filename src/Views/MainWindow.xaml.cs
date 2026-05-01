using System.Reflection;
using System.Windows;
using System.Windows.Automation;

namespace PtySpeak.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Inject the running assembly's informational version into
        // both the visible Title and the AutomationProperties.Name
        // (the latter is what NVDA reads on focus / NVDA+T). Lets
        // the user audibly confirm which version is running —
        // important after Stage 11's Ctrl+Shift+U self-update so
        // the post-restart announcement reflects the new version.
        //
        // Uses `AssemblyInformationalVersionAttribute` rather than
        // `Assembly.GetName().Version` because the System.Version
        // type doesn't carry prerelease suffixes ("0.0.1-preview.26"
        // is not a valid System.Version; it parses as 0.0.1.0).
        // The release workflow's `dotnet publish /p:Version=...`
        // step injects the InformationalVersion at build time from
        // the GitHub release tag, so installed builds carry the
        // exact version string the user can match against the
        // GitHub Release page.
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            // Strip any "+commit-sha" suffix the build pipeline
            // adds (SourceLink / deterministic build trailer);
            // it's noise for the user's audible announcement.
            var plusIdx = version.IndexOf('+');
            if (plusIdx > 0) version = version.Substring(0, plusIdx);

            Title = $"pty-speak {version}";
            AutomationProperties.SetName(this, $"pty-speak terminal {version}");
        }

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

        // Audit-cycle PR-C deleted the SourceInitialized +
        // Closed handlers that installed and uninstalled the
        // WM_GETOBJECT subclass hook (`WindowSubclassNative`).
        // The Stage 4 architectural pivot to
        // `AutomationPeer.GetPattern` override (PR #56) made
        // the subclass hook a "kept just in case" MSAA-only
        // fallback path; the audit found no real consumers and
        // the maintainer authorised deletion. The Document-role
        // peer + Text pattern surface UIA3 clients use lives in
        // `TerminalAutomationPeer` (Terminal.Accessibility) and
        // is reached via `TerminalView.OnCreateAutomationPeer`
        // — no native-interop window subclass involved.
    }
}
