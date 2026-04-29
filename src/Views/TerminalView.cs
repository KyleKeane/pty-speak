using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
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
    /// Returns the F# <see cref="TerminalAutomationPeer"/> so UIA
    /// clients (NVDA, Inspect.exe, FlaUI tests) see this element
    /// as a Document with the right ClassName and Name. Stage 4a
    /// ships this reduced peer; the Text-pattern exposure that
    /// would let NVDA read the buffer contents is deferred until
    /// the GetPatternCore-not-reachable investigation in
    /// <c>docs/SESSION-HANDOFF.md</c> Stage 4 sketch concludes.
    ///
    /// WPF caches the returned peer per element and reuses it for
    /// the element's lifetime — there's no need to memoize here.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
        => new TerminalAutomationPeer(this);

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

        for (int row = 0; row < _screen.Rows; row++)
        {
            RenderRow(drawingContext, row);
        }
    }

    private void RenderRow(DrawingContext dc, int row)
    {
        if (_screen is null) return;

        // Walk the row coalescing contiguous cells with identical
        // SgrAttrs. For each run we draw the background (if non-default)
        // then a single FormattedText for the run's characters.
        int runStart = 0;
        while (runStart < _screen.Cols)
        {
            var startAttrs = _screen.GetCell(row, runStart).Attrs;
            int runEnd = runStart + 1;
            while (runEnd < _screen.Cols
                && SgrAttrsEqual(_screen.GetCell(row, runEnd).Attrs, startAttrs))
            {
                runEnd++;
            }
            DrawRun(dc, row, runStart, runEnd, startAttrs);
            runStart = runEnd;
        }
    }

    private void DrawRun(
        DrawingContext dc,
        int row,
        int runStart,
        int runEnd,
        SgrAttrs attrs)
    {
        if (_screen is null) return;

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
            var rune = _screen.GetCell(row, c).Ch;
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
