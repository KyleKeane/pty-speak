using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Terminal.Accessibility;
using Terminal.Core;

namespace PtySpeak.Views;

/// <summary>
/// WPF custom control that renders a <see cref="Screen"/> as a grid of
/// monospaced text cells. Stage 3b's first visible terminal surface.
///
/// Threading: <see cref="SetScreen"/> and <see cref="InvalidateScreen"/>
/// must be called on the WPF dispatcher thread (callers from a
/// reader/parser thread must marshal via <c>Dispatcher.InvokeAsync</c>).
/// Stage 3b consumes the screen on the UI thread for simplicity; later
/// stages will move parser mutation onto a dedicated thread with a
/// snapshot-on-render contract.
///
/// Rendering strategy: per spec/tech-plan.md §3.3 we use
/// <see cref="DrawingContext"/> + <see cref="FormattedText"/> in
/// <see cref="OnRender"/> rather than nested <c>TextBlock</c>s. For
/// each row we coalesce contiguous cells with identical SGR attrs into
/// a single FormattedText run to keep allocation per redraw bounded.
/// </summary>
public class TerminalView : FrameworkElement
{
    private const string FontFamilyName = "Cascadia Mono, Consolas, Courier New";
    private const double FontSize = 14.0;

    private readonly Typeface _typeface =
        new(new FontFamily(FontFamilyName),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

    private double _cellWidth;
    private double _cellHeight;

    private Screen? _screen;

    /// <summary>
    /// UIA Text-pattern provider that exposes the current
    /// <see cref="Screen"/> contents as a single document-range
    /// string. Constructed once per view; the closure captures
    /// <c>this</c> so it sees screen attachments that happen
    /// after construction.
    ///
    /// Consumed by the F#
    /// <see cref="Terminal.Accessibility.TerminalAutomationPeer"/>
    /// returned from <see cref="OnCreateAutomationPeer"/>: the
    /// peer's <c>GetPattern</c> override returns this provider
    /// for <c>PatternInterface.Text</c>, which UIA3 clients
    /// (NVDA, Inspect.exe, FlaUI) read directly through WPF's
    /// existing peer tree. Audit-cycle PR-C deleted the
    /// alternative WM_GETOBJECT raw-provider path; this is
    /// the only Text-pattern surface now.
    /// </summary>
    // Audit-cycle PR-C lowered this from `public` to
    // `internal` matching its newly-internal type. The only
    // consumer was the deleted `TerminalRawProvider`; the
    // peer's `OnCreateAutomationPeer` call site below still
    // works because it passes the value through to
    // `TerminalAutomationPeer`'s constructor which takes
    // `ITextProvider` (a system-public interface, not the
    // internal type).
    internal TerminalTextProvider TextProvider { get; }

    /// <summary>Default background fill for the terminal grid.
    /// FrameworkElement (unlike Control / Panel) does not expose
    /// `Background` itself, so we keep our own.</summary>
    private readonly Brush _background = Brushes.Black;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Compute cell metrics once at construction. Monospaced fonts
        // give us an em-quad-aligned cell that we can reuse across
        // rows / columns. Stage 3b picks 14pt as a reasonable default;
        // configurability lands in a later UX stage.
        var sample = MakeFormattedText("M", Brushes.White);
        _cellWidth = sample.WidthIncludingTrailingWhitespace;
        _cellHeight = sample.Height;

        TextProvider = new TerminalTextProvider(() => _screen);
    }

    /// <summary>
    /// Attach a screen to render. Call once at startup; subsequent
    /// updates flow through <see cref="InvalidateScreen"/>.
    /// </summary>
    public void SetScreen(Screen screen)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Tell WPF the screen's contents may have changed and a redraw
    /// is required.
    /// </summary>
    public void InvalidateScreen()
    {
        InvalidateVisual();
    }

    /// <summary>
    /// Raise a UIA Notification event on this element so NVDA
    /// announces <paramref name="message"/> immediately. Used by
    /// Stage 11's auto-update flow to surface "Checking for
    /// updates", "Downloading...", "Restarting" etc. as the
    /// background `UpdateManager` task progresses.
    /// </summary>
    /// <remarks>
    /// `MostRecent` processing means a newer notification
    /// supersedes any in-flight one, so a fast download doesn't
    /// flood NVDA's speech queue with stale percentages — only
    /// the latest progress message gets read. The
    /// <c>activityId</c> groups the notifications so screen
    /// readers can identify them as part of one logical
    /// activity (the update flow).
    ///
    /// If no UIA client has connected yet (no peer in WPF's
    /// cache), the announce is a silent no-op rather than
    /// forcing peer creation. By the time the user has pressed
    /// Ctrl+Shift+U for the first time, NVDA / Inspect.exe will
    /// have already triggered peer construction.
    /// </remarks>
    public void Announce(string message)
    {
        // Back-compat overload — every existing call site in
        // Stage 11 / hotkey handlers passes update-flow text.
        // Stage 5's coalescer drain calls the (message, activityId)
        // overload below to pass per-event-class tags.
        Announce(message, "pty-speak.update");
    }

    /// <summary>
    /// Stage 5 overload — accepts an explicit
    /// <paramref name="activityId"/> so each notification class
    /// (streaming output, update flow, errors, diagnostic
    /// launcher, releases browser, mode transitions) gets a
    /// stable tag for NVDA's per-tag verbosity configuration.
    /// </summary>
    /// <remarks>
    /// Stage 5's `Coalescer` drain passes
    /// <c>"pty-speak.output"</c>; Stage 11's update flow passes
    /// <c>"pty-speak.update"</c> via the back-compat overload
    /// above. The vocabulary is centralised in F# at
    /// <c>Terminal.Core.ActivityIds</c>.
    /// </remarks>
    public void Announce(string message, string activityId)
    {
        var peer = UIElementAutomationPeer.FromElement(this);
        if (peer is not null)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent,
                message,
                activityId);
        }
    }

    /// <summary>
    /// App-reserved hotkey list. Stage 6 (keyboard input to PTY)
    /// MUST preserve every entry here — its <c>PreviewKeyDown</c>
    /// filter must NOT mark these key combinations
    /// <c>e.Handled = true</c>, so WPF's <c>InputBindings</c>
    /// machinery on the parent window processes them before any
    /// forwarding to the PTY child. The corresponding clause in
    /// <c>spec/tech-plan.md</c> §6 ("App-reserved hotkey
    /// preservation contract") makes this contract normative.
    ///
    /// Each entry is documented with the stage that owns it and
    /// the binding's command target. New app-level hotkeys
    /// added in future stages append to this list AND the spec
    /// §6 clause; the two are co-equal sources of truth.
    /// </summary>
    public static readonly (Key Key, ModifierKeys Modifiers, string Description)[]
        AppReservedHotkeys =
        [
            // Stage 11 — Velopack auto-update (shipped, PR #63).
            // Bound in `setupAutoUpdateKeybinding` in
            // `src/Terminal.App/Program.fs`.
            (Key.U, ModifierKeys.Control | ModifierKeys.Shift, "Stage 11 self-update"),

            // Future entries (NOT yet bound; commented for
            // forward-planning):
            //   (Key.M, ModifierKeys.Control | ModifierKeys.Shift,
            //    "Stage 9 earcon mute"),
            //   (Key.R, ModifierKeys.Alt | ModifierKeys.Shift,
            //    "Stage 10 review-mode toggle"),
        ];

    /// <summary>
    /// Pre-Stage-5 routing stub for keyboard input. Today this
    /// override does nothing visible — Stage 6 (keyboard input
    /// to PTY) will fill it in with the NVDA-modifier filter
    /// and PTY-forwarding pipeline. The stub exists now to
    /// guarantee that when Stage 6 lands, the
    /// <see cref="AppReservedHotkeys"/> contract is honoured:
    /// the override checks each reserved hotkey first and
    /// leaves <c>e.Handled</c> alone for them, ensuring WPF's
    /// <c>InputBindings</c> see them before any PTY forwarding.
    ///
    /// Until Stage 6 ships, no key forwarding happens at all
    /// (the cmd.exe child receives no input) — that's the
    /// pre-Stage-6 reality. NVDA review-cursor commands aren't
    /// keyboard input to the PTY; they're handled by NVDA
    /// itself and don't reach this method.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // App-reserved hotkey check: any match short-circuits
        // and explicitly does NOT mark the event handled, so
        // the parent Window's InputBindings can process the
        // gesture and the corresponding command fires
        // (Ctrl+Shift+U → runUpdateFlow today).
        var pressedModifiers = Keyboard.Modifiers;
        foreach (var (key, modifiers, _) in AppReservedHotkeys)
        {
            if (e.Key == key && pressedModifiers == modifiers)
            {
                // Explicitly leave Handled = false so
                // InputBindings see this key combination.
                return;
            }
        }

        // No PTY forwarding until Stage 6 ships. Future Stage 6
        // implementation must respect the reserved-hotkey
        // contract (see spec/tech-plan.md §6).
    }

    /// <summary>
    /// Returns the F# <see cref="TerminalAutomationPeer"/> so UIA
    /// clients (NVDA, Inspect.exe, FlaUI tests) see this element
    /// as a Document with the right ClassName, Name, and Text
    /// pattern. The peer's `GetPattern` override returns
    /// <see cref="TextProvider"/> for `PatternInterface.Text`,
    /// which lets NVDA / UIA3 read the buffer contents directly
    /// through WPF's existing peer tree — no
    /// <c>WM_GETOBJECT</c> interception or fragment-root
    /// implementation needed.
    ///
    /// WPF caches the returned peer per element and reuses it for
    /// the element's lifetime — there's no need to memoize here.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
        => new TerminalAutomationPeer(this, TextProvider);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_screen is null)
        {
            return Size.Empty;
        }
        var width = _cellWidth * _screen.Cols;
        var height = _cellHeight * _screen.Rows;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Always paint the background first so the grid has a consistent
        // dark surface even when no screen is attached yet.
        drawingContext.DrawRectangle(_background, null, new Rect(RenderSize));

        if (_screen is null)
        {
            return;
        }

        // Acc/9 — take ONE locked snapshot per render frame instead of
        // calling _screen.GetCell(...) per cell, which would re-acquire
        // the screen gate up to Rows*Cols times and race with the
        // parser thread between cells.
        var snap = _screen.SnapshotRows(0, _screen.Rows);
        var rows = snap.Item2;
        var cols = _screen.Cols;

        for (int row = 0; row < rows.Length; row++)
        {
            RenderRow(drawingContext, row, rows[row], cols);
        }
    }

    private void RenderRow(DrawingContext dc, int row, Cell[] cells, int cols)
    {
        // Walk the row coalescing contiguous cells with identical
        // SgrAttrs. For each run we draw the background (if non-default)
        // then a single FormattedText for the run's characters.
        int runStart = 0;
        while (runStart < cols)
        {
            var startAttrs = cells[runStart].Attrs;
            int runEnd = runStart + 1;
            while (runEnd < cols
                && SgrAttrsEqual(cells[runEnd].Attrs, startAttrs))
            {
                runEnd++;
            }
            DrawRun(dc, row, runStart, runEnd, startAttrs, cells);
            runStart = runEnd;
        }
    }

    private void DrawRun(
        DrawingContext dc,
        int row,
        int runStart,
        int runEnd,
        SgrAttrs attrs,
        Cell[] cells)
    {
        var fg = ResolveBrush(attrs.Fg, isForeground: true);
        var bg = ResolveBrush(attrs.Bg, isForeground: false);

        if (attrs.Inverse)
        {
            (fg, bg) = (bg, fg);
        }

        // Background fill for the whole run if it's not the default.
        if (!attrs.Bg.IsDefault || attrs.Inverse)
        {
            var x = runStart * _cellWidth;
            var y = row * _cellHeight;
            var width = (runEnd - runStart) * _cellWidth;
            dc.DrawRectangle(bg, null, new Rect(x, y, width, _cellHeight));
        }

        // Build run text.
        var sb = new System.Text.StringBuilder(runEnd - runStart);
        for (int c = runStart; c < runEnd; c++)
        {
            var rune = cells[c].Ch;
            sb.Append(rune.ToString());
        }

        var ft = MakeFormattedText(sb.ToString(), fg);
        if (attrs.Bold)
        {
            ft.SetFontWeight(FontWeights.Bold);
        }
        if (attrs.Italic)
        {
            ft.SetFontStyle(FontStyles.Italic);
        }

        var origin = new Point(runStart * _cellWidth, row * _cellHeight);
        dc.DrawText(ft, origin);

        if (attrs.Underline)
        {
            // Manual underline at the baseline so we don't depend on
            // FormattedText.SetTextDecorations being honoured under
            // every WPF rendering mode.
            var y = row * _cellHeight + _cellHeight - 1.5;
            var x1 = runStart * _cellWidth;
            var x2 = runEnd * _cellWidth;
            var pen = new Pen(fg, 1.0);
            dc.DrawLine(pen, new Point(x1, y), new Point(x2, y));
        }
    }

    private FormattedText MakeFormattedText(string text, Brush fg)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            fg,
            dpi);
    }

    private static bool SgrAttrsEqual(SgrAttrs a, SgrAttrs b)
    {
        return a.Bold == b.Bold
            && a.Italic == b.Italic
            && a.Underline == b.Underline
            && a.Inverse == b.Inverse
            && ColorSpecEqual(a.Fg, b.Fg)
            && ColorSpecEqual(a.Bg, b.Bg);
    }

    private static bool ColorSpecEqual(ColorSpec a, ColorSpec b)
    {
        if (a.IsDefault && b.IsDefault) return true;
        if (a.IsIndexed && b.IsIndexed) return ((ColorSpec.Indexed)a).Item == ((ColorSpec.Indexed)b).Item;
        if (a.IsRgb && b.IsRgb)
        {
            var ra = (ColorSpec.Rgb)a;
            var rb = (ColorSpec.Rgb)b;
            return ra.Item1 == rb.Item1 && ra.Item2 == rb.Item2 && ra.Item3 == rb.Item3;
        }
        return false;
    }

    private static Brush ResolveBrush(ColorSpec spec, bool isForeground)
    {
        if (spec.IsDefault)
        {
            return isForeground ? Brushes.White : Brushes.Black;
        }
        if (spec.IsIndexed)
        {
            var idx = ((ColorSpec.Indexed)spec).Item;
            return Ansi16ToBrush(idx);
        }
        if (spec.IsRgb)
        {
            var rgb = (ColorSpec.Rgb)spec;
            return new SolidColorBrush(Color.FromRgb(rgb.Item1, rgb.Item2, rgb.Item3));
        }
        return isForeground ? Brushes.White : Brushes.Black;
    }

    private static Brush Ansi16ToBrush(byte idx)
    {
        // Standard xterm 16-colour palette. Colours 0..7 are normal,
        // 8..15 are bright. Anything beyond is left at white as a
        // visible "we didn't handle this yet" signal until 256-colour
        // / truecolor SGR parsing lands.
        return idx switch
        {
            0 => Brushes.Black,
            1 => Brushes.Red,
            2 => Brushes.Green,
            3 => Brushes.Olive,
            4 => Brushes.Blue,
            5 => Brushes.Purple,
            6 => Brushes.Teal,
            7 => Brushes.LightGray,
            8 => Brushes.DimGray,
            9 => Brushes.OrangeRed,
            10 => Brushes.LimeGreen,
            11 => Brushes.Yellow,
            12 => Brushes.RoyalBlue,
            13 => Brushes.Magenta,
            14 => Brushes.Cyan,
            15 => Brushes.White,
            _ => Brushes.White,
        };
    }
}
