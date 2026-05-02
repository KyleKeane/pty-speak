using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// Stage 6 PR-B — sink for keyboard / paste / focus bytes. Set
    /// by <see cref="SetPtyHost"/> from <c>Program.fs compose ()</c>
    /// after the ConPtyHost is up. Until set (and during teardown),
    /// key events drop silently — Stage 6 cannot route input
    /// without a live PTY.
    /// </summary>
    private Action<byte[]>? _writeBytes;

    /// <summary>
    /// Stage 6 PR-B — resize callback. Receives the new
    /// (cols, rows) cell dimensions after the WPF SizeChanged
    /// debounce settles; the implementation in Program.fs
    /// translates to <c>ConPtyHost.Resize</c>.
    /// </summary>
    private Action<int, int>? _resize;

    /// <summary>
    /// Stage 6 PR-B — 200ms trailing-edge debounce for
    /// SizeChanged → ResizePseudoConsole. WPF SizeChanged fires
    /// per pixel during a window drag (60Hz); resizing the PTY
    /// at that rate causes the child shell to re-layout for
    /// every tick, which floods Stage 5's output coalescer with
    /// redraws and dilutes its spinner heuristic. The timer
    /// fires on the WPF dispatcher (DispatcherTimer), so the
    /// resize callback runs on the same thread as keyboard
    /// writes — single-threaded write discipline.
    /// </summary>
    // TODO Phase 2: TOML-configurable debounce window alongside
    // the Stage 5 coalescer constants in Coalescer.fs.
    private readonly DispatcherTimer _resizeDebounceTimer;

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

        // Stage 6 PR-B — paste hook. ApplicationCommands.Paste fires
        // for right-click → Paste, Edit menu → Paste, and any future
        // CommandBinding consumer; one CommandBinding covers all
        // command-style paste sources. The keyboard gestures
        // (Ctrl+V / Shift+Insert) are NOT wired through CommandManager
        // any more — they're handled directly in OnPreviewKeyDown
        // before the encoder runs (see post-Stage-6 fix-2 below).
        // Reason: WPF's CommandManager class handler doesn't auto-
        // process InputBindings on a raw FrameworkElement, AND when
        // OnPasteCanExecute returns false (e.g. empty clipboard), the
        // unhandled gesture falls through to the encoder and emits
        // ^V to the shell — exactly what we wanted to avoid. Direct
        // handling guarantees the encoder is bypassed regardless of
        // clipboard state, with empty-clipboard becoming a silent
        // no-op rather than a ^V emission.
        CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            OnPasteExecuted,
            OnPasteCanExecute));

        // Stage 6 PR-B — resize debounce timer. Stopped initially;
        // OnRenderSizeChanged restarts it on each WPF SizeChanged tick.
        _resizeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
    }

    /// <summary>
    /// Stage 6 PR-B — wire the PTY write + resize sinks. Called
    /// once from <c>Program.fs compose ()</c> after the
    /// <c>ConPtyHost</c> spawns successfully. The <paramref name="writeBytes"/>
    /// callback is invoked from the WPF dispatcher thread (PreviewKeyDown,
    /// TextInput, paste, focus events all fire there), so the
    /// implementation can synchronously call
    /// <c>ConPtyHost.WriteBytes</c> without further marshalling.
    /// </summary>
    public void SetPtyHost(Action<byte[]> writeBytes, Action<int, int> resize)
    {
        _writeBytes = writeBytes ?? throw new ArgumentNullException(nameof(writeBytes));
        _resize = resize ?? throw new ArgumentNullException(nameof(resize));
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
    ///
    /// Post-Stage-6 fix: the underlying
    /// <see cref="AutomationNotificationProcessing"/> defaults to
    /// <see cref="AutomationNotificationProcessing.MostRecent"/>
    /// for hotkey-style announcements (Ctrl+Shift+U / D / R, the
    /// Velopack progress flow) where each new notification SHOULD
    /// supersede any in-flight one. Streaming PTY output
    /// (<c>"pty-speak.output"</c>) instead uses
    /// <see cref="AutomationNotificationProcessing.ImportantAll"/>
    /// so rapid chunks queue rather than discarding their
    /// predecessors — without this, typed-character echoes and
    /// command output were silently superseded before NVDA could
    /// speak any of them.
    /// </remarks>
    public void Announce(string message, string activityId)
    {
        var processing = activityId == ActivityIds.output
            ? AutomationNotificationProcessing.ImportantAll
            : AutomationNotificationProcessing.MostRecent;
        Announce(message, activityId, processing);
    }

    /// <summary>
    /// Underlying overload that takes an explicit
    /// <see cref="AutomationNotificationProcessing"/>. The
    /// activity-id-aware overload above selects a default per
    /// notification class; use this overload when a caller needs
    /// to override the default (rare).
    /// </summary>
    public void Announce(
        string message,
        string activityId,
        AutomationNotificationProcessing processing)
    {
        var peer = UIElementAutomationPeer.FromElement(this);
        if (peer is not null)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                processing,
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

            // Audit-cycle PR-#81 — process-cleanup diagnostic launcher
            // (shipped). Bound in `setupDiagnosticKeybinding`.
            (Key.D, ModifierKeys.Control | ModifierKeys.Shift, "Process-cleanup diagnostic"),

            // PR-#83 / PR-#91 — draft-a-new-release form launcher
            // (shipped; URL flipped to /releases/new in PR #91).
            // Bound in `setupNewReleaseKeybinding`.
            (Key.R, ModifierKeys.Control | ModifierKeys.Shift, "Draft new release form"),

            // Logging PR — open the logs folder in File Explorer.
            // Bound in `setupOpenLogsKeybinding`.
            (Key.L, ModifierKeys.Control | ModifierKeys.Shift, "Open logs folder"),

            // Future entries (NOT yet bound; commented for
            // forward-planning):
            //   (Key.M, ModifierKeys.Control | ModifierKeys.Shift,
            //    "Stage 9 earcon mute"),
            //   (Key.R, ModifierKeys.Alt | ModifierKeys.Shift,
            //    "Stage 10 review-mode toggle"),
        ];

    /// <summary>
    /// Stage 6 PR-B — keyboard input pipeline. Filter ordering is
    /// LOAD-BEARING and pinned by xUnit + behavioural tests:
    ///
    /// <list type="number">
    ///   <item><description><b>App-reserved hotkeys first.</b> Any match in
    ///   <see cref="AppReservedHotkeys"/> short-circuits and does NOT mark
    ///   the event handled, so the parent Window's InputBindings can fire
    ///   the corresponding command (Ctrl+Shift+U / D / R today; future
    ///   Ctrl+Shift+M, Alt+Shift+R when Stage 9 / 10 land).</description></item>
    ///   <item><description><b>NVDA / screen-reader modifier filter
    ///   second.</b> Bare Insert / CapsLock presses, and Numpad presses
    ///   with NumLock off, return without Handled so NVDA / JAWS / Narrator
    ///   can receive them. Conservative on purpose — the cost (a few key
    ///   presses don't reach the shell when the user genuinely meant them
    ///   for the shell) is tiny vs. the cost of breaking review-cursor
    ///   navigation (catastrophic UX for the target audience).</description></item>
    ///   <item><description><b>Translate WPF Key + ModifierKeys to
    ///   <see cref="KeyCode"/> + <see cref="KeyModifiers"/></b> via the
    ///   small adapter at the bottom of this file. Unknown keys map to
    ///   <c>KeyCode.Unhandled</c> and the encoder returns <c>None</c>
    ///   — silently dropped, no crash on a future WPF Key value.</description></item>
    ///   <item><description><b>Defer plain printable typing to
    ///   <see cref="OnPreviewTextInput"/></b>. For letters / digits /
    ///   space without Ctrl or Alt held, leave Handled = false so WPF's
    ///   text-composition pipeline (which handles IME, AltGr, dead keys
    ///   correctly) routes the keystroke into TextInput.</description></item>
    ///   <item><description><b>Encode and write</b> via
    ///   <see cref="KeyEncoding.encodeOrNull"/>. The encoder reads
    ///   <c>_screen.Modes.DECCKM</c> for arrow-key encoding (normal
    ///   <c>\x1b[A</c> vs application <c>\x1bOA</c>).</description></item>
    /// </list>
    ///
    /// If reordering is ever proposed: confirm with maintainer first.
    /// Step 1 must come before step 2 (otherwise NVDA filter would eat
    /// Ctrl+Shift+U via the app-reserved table); step 2 must come
    /// before step 3 (otherwise we'd encode bare Insert and send it
    /// to the shell, bypassing NVDA's modifier).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var pressedModifiers = Keyboard.Modifiers;

        // 1. App-reserved hotkey check.
        foreach (var (key, modifiers, _) in AppReservedHotkeys)
        {
            if (e.Key == key && pressedModifiers == modifiers)
            {
                return;
            }
        }

        // 2. NVDA / screen-reader modifier filter.
        if (IsScreenReaderCandidate(e.Key))
        {
            return;
        }

        // 2.5. App-level keyboard shortcuts that bypass the encoder.
        // Post-Stage-6 fix-2: these gestures look like Ctrl-letter
        // combos to the encoder, but their user-facing meaning
        // (paste, clear-screen) is fundamentally a UI concept that
        // doesn't translate cleanly to a single PTY byte for cmd.exe.
        // Sending the raw control byte (0x16, 0x0C) results in cmd.exe
        // echoing them back as ^V / ^L caret-notation, which is the
        // bug the maintainer hit during NVDA verification.
        //
        // Handle these explicitly here instead of relying on
        // CommandManager / InputBinding routing — that route doesn't
        // fire reliably for a custom FrameworkElement, and any
        // CanExecute=false branch falls through to the encoder.
        if (HandleAppLevelShortcut(e.Key, pressedModifiers))
        {
            e.Handled = true;
            return;
        }

        // 3. Translate.
        var keyMods = TranslateModifiers(pressedModifiers);
        var keyCode = TranslateKey(e.Key);

        // 4. Defer plain typing to OnPreviewTextInput.
        var ctrlOrAltHeld =
            keyMods.HasFlag(KeyModifiers.Control) ||
            keyMods.HasFlag(KeyModifiers.Alt);
        if (keyCode.IsChar && !ctrlOrAltHeld)
        {
            return;
        }

        // 5. Encode and write. If the screen isn't attached yet
        // (very early init / teardown) drop the key gracefully —
        // there's nowhere meaningful to send it. _screen is set
        // by Program.fs's compose() before window.Loaded fires
        // and the user is realistically able to press a key, so
        // this branch is defence in depth rather than expected.
        if (_writeBytes is null || _screen is null)
        {
            return;
        }
        var bytes = KeyEncoding.encodeOrNull(keyCode, keyMods, _screen.Modes);
        if (bytes is null)
        {
            return;
        }
        _writeBytes(bytes);
        e.Handled = true;
    }

    /// <summary>
    /// Stage 6 PR-B — IME / printable-typing input. Plain typing
    /// (letters, digits, space, AltGr-composed characters, dead-key
    /// composed characters, IME-committed text) arrives here with
    /// the final composed string. UTF-8 encode and write to the
    /// PTY directly — no need to route through KeyEncoding because
    /// these are already finished printable characters.
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        if (_writeBytes is null)
        {
            return;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
        _writeBytes(bytes);
        e.Handled = true;
    }

    /// <summary>
    /// Stage 6 PR-B — focus reporting. When the child shell has set
    /// DECSET ?1004 (BracketedPaste-mode-style focus events), emit
    /// <c>\x1b[I</c> on focus and <c>\x1b[O</c> on blur. Editors
    /// like nano / vim / Emacs / Claude Code use these to know when
    /// to suspend their cursor blink, save unsaved buffers, etc.
    /// </summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_writeBytes is null) return;
        if (_screen?.Modes.FocusReporting == true)
        {
            _writeBytes(KeyEncoding.focusGained);
        }
    }

    /// <inheritdoc cref="OnGotKeyboardFocus"/>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_writeBytes is null) return;
        if (_screen?.Modes.FocusReporting == true)
        {
            _writeBytes(KeyEncoding.focusLost);
        }
    }

    /// <summary>
    /// Stage 6 PR-B — paste handler bound to
    /// <see cref="ApplicationCommands.Paste"/>. Reads the
    /// clipboard text, runs it through
    /// <see cref="KeyEncoding.encodePaste"/> (which strips
    /// embedded <c>\x1b[201~</c> for paste-injection defence and
    /// wraps in <c>\x1b[200~</c>...<c>\x1b[201~</c> when the
    /// child has set DECSET ?2004), and writes to the PTY.
    /// </summary>
    private void OnPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_writeBytes is null) return;
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var bracketed = _screen?.Modes.BracketedPaste == true;
        var bytes = KeyEncoding.encodePaste(text, bracketed);
        _writeBytes(bytes);
        e.Handled = true;
    }

    private void OnPasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _writeBytes is not null && Clipboard.ContainsText();
        e.Handled = true;
    }

    /// <summary>
    /// Post-Stage-6 fix — app-level keyboard shortcuts that bypass
    /// the PTY encoder. These gestures look like Ctrl-letter combos
    /// to the encoder, but their user-facing meaning (paste,
    /// clear-screen) is a UI concept that doesn't translate cleanly
    /// to a single PTY byte for cmd.exe. Returns true if the
    /// gesture was handled (caller marks Handled and returns);
    /// false if not (caller continues with encoder).
    ///
    /// Currently handles:
    /// <list type="bullet">
    ///   <item><description><b>Ctrl+V / Shift+Insert</b> — paste from
    ///   clipboard via <see cref="KeyEncoding.encodePaste"/>. Empty
    ///   clipboard becomes a silent no-op rather than a <c>^V</c>
    ///   emission to the shell.</description></item>
    ///   <item><description><b>Ctrl+L</b> — send <c>cls\r</c> to the
    ///   shell. <b>Currently cmd.exe-specific.</b> The literally-correct
    ///   thing to do is send <c>0x0C</c> (form feed) and let the
    ///   shell decide; cmd.exe ignores it and echoes <c>^L</c>,
    ///   which is bad UX. PowerShell + PSReadLine and Unix shells
    ///   honour <c>0x0C</c> directly. A future stage with shell
    ///   detection (or per-shell config) will pick the right
    ///   behaviour automatically; for now we hardcode <c>cls\r</c>
    ///   because the default shell is cmd.exe. Trade-off: when the
    ///   foreground process is something that DOES interpret
    ///   <c>0x0C</c> (Claude Code's Ink, <c>less</c>,
    ///   <c>vim</c>, etc.), Ctrl+L will run <c>cls</c> as if typed
    ///   instead of triggering that program's redraw. Acceptable
    ///   compromise for the current cmd.exe-only scope; revisit
    ///   when Stage 7+ adds shell flexibility.</description></item>
    /// </list>
    /// </summary>
    private bool HandleAppLevelShortcut(Key key, ModifierKeys modifiers)
    {
        if (_writeBytes is null) return false;

        // Ctrl+V / Shift+Insert → paste.
        var isPaste =
            (key == Key.V && modifiers == ModifierKeys.Control) ||
            (key == Key.Insert && modifiers == ModifierKeys.Shift);
        if (isPaste)
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    var bracketed = _screen?.Modes.BracketedPaste == true;
                    var bytes = KeyEncoding.encodePaste(text, bracketed);
                    _writeBytes(bytes);
                }
            }
            // Silent no-op on empty clipboard — strictly better than
            // the previous ^V emission to the shell.
            return true;
        }

        // Ctrl+L → cls\r (cmd.exe-specific clear-screen).
        if (key == Key.L && modifiers == ModifierKeys.Control)
        {
            _writeBytes(System.Text.Encoding.ASCII.GetBytes("cls\r"));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Stage 6 PR-B — restart the resize debounce on every WPF
    /// SizeChanged tick. Final call to <see cref="_resize"/> happens
    /// 200ms after the last SizeChanged settles.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_resize is null)
        {
            return;
        }
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer.Stop();
        if (_resize is null) return;
        // ActualWidth/Height are in DIPs; _cellWidth/_cellHeight are
        // computed from the same FormattedText pipeline, also in DIPs,
        // so the ratio yields cell counts directly. Clamp to >= 1 so
        // a zero-size pre-layout pass doesn't ask the PTY for a 0×0
        // grid (which Win32 rejects).
        var cols = (int)Math.Max(1, ActualWidth / _cellWidth);
        var rows = (int)Math.Max(1, ActualHeight / _cellHeight);
        _resize(cols, rows);
    }

    /// <summary>
    /// Returns true when a key press should be left to the screen
    /// reader rather than forwarded to the PTY. Conservative: filters
    /// bare Insert / CapsLock (NVDA / JAWS / Narrator modifier
    /// candidates) and Numpad keys when NumLock is off (NVDA review-
    /// cursor numpad layout). Side effect: a user pressing bare Insert
    /// or CapsLock to send the corresponding shell key gets nothing,
    /// and Numpad-as-arrow with NumLock off is suppressed. Both
    /// trade-offs are accepted to preserve screen-reader navigation.
    /// </summary>
    private static bool IsScreenReaderCandidate(Key key)
    {
        if (key == Key.Insert) return true;
        if (key == Key.CapsLock) return true;
        // Numpad with NumLock off — NVDA review-cursor layout.
        var isNumpad =
            (key >= Key.NumPad0 && key <= Key.NumPad9)
            || key == Key.Decimal
            || key == Key.Multiply
            || key == Key.Add
            || key == Key.Subtract
            || key == Key.Divide
            || key == Key.Separator;
        if (isNumpad && !Keyboard.IsKeyToggled(Key.NumLock))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Translate WPF <see cref="ModifierKeys"/> to
    /// <see cref="KeyModifiers"/>. The Windows key is silently
    /// dropped — pty-speak doesn't forward it; Win+letter is OS-shell
    /// territory.
    /// </summary>
    private static KeyModifiers TranslateModifiers(ModifierKeys m)
    {
        var result = KeyModifiers.None;
        if ((m & ModifierKeys.Shift) != 0) result |= KeyModifiers.Shift;
        if ((m & ModifierKeys.Alt) != 0) result |= KeyModifiers.Alt;
        if ((m & ModifierKeys.Control) != 0) result |= KeyModifiers.Control;
        return result;
    }

    /// <summary>
    /// Translate WPF <see cref="Key"/> to <see cref="KeyCode"/>.
    /// Returns <c>KeyCode.Unhandled</c> for any key the encoder
    /// doesn't know about — the encoder then returns null and
    /// the keystroke is dropped silently rather than crashing.
    /// New WPF Key values can ship without breaking us.
    /// </summary>
    private static KeyCode TranslateKey(Key key)
    {
        // Cursor keys.
        if (key == Key.Up) return KeyCode.Up;
        if (key == Key.Down) return KeyCode.Down;
        if (key == Key.Right) return KeyCode.Right;
        if (key == Key.Left) return KeyCode.Left;
        // Editing keypad.
        if (key == Key.Delete) return KeyCode.Delete;
        if (key == Key.Home) return KeyCode.Home;
        if (key == Key.End) return KeyCode.End;
        if (key == Key.PageUp) return KeyCode.PageUp;
        if (key == Key.PageDown) return KeyCode.PageDown;
        // Note: Key.Insert is filtered upstream as a screen-reader
        // candidate; we never reach this branch for it. Listed here
        // for completeness if the filter is ever loosened.
        if (key == Key.Insert) return KeyCode.Insert;
        // Whitespace / control.
        if (key == Key.Tab) return KeyCode.Tab;
        if (key == Key.Enter) return KeyCode.Enter;
        if (key == Key.Escape) return KeyCode.Escape;
        if (key == Key.Back) return KeyCode.Backspace;
        // Function keys.
        if (key == Key.F1) return KeyCode.F1;
        if (key == Key.F2) return KeyCode.F2;
        if (key == Key.F3) return KeyCode.F3;
        if (key == Key.F4) return KeyCode.F4;
        if (key == Key.F5) return KeyCode.F5;
        if (key == Key.F6) return KeyCode.F6;
        if (key == Key.F7) return KeyCode.F7;
        if (key == Key.F8) return KeyCode.F8;
        if (key == Key.F9) return KeyCode.F9;
        if (key == Key.F10) return KeyCode.F10;
        if (key == Key.F11) return KeyCode.F11;
        if (key == Key.F12) return KeyCode.F12;
        // Letters → Char(lowercase). Encoder folds Shift for Ctrl-letter.
        if (key >= Key.A && key <= Key.Z)
        {
            return KeyCode.NewChar((char)('a' + (key - Key.A)));
        }
        // Top-row digits.
        if (key >= Key.D0 && key <= Key.D9)
        {
            return KeyCode.NewChar((char)('0' + (key - Key.D0)));
        }
        // Numpad digits when NumLock is on (NumLock-off case is filtered
        // upstream by IsScreenReaderCandidate).
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return KeyCode.NewChar((char)('0' + (key - Key.NumPad0)));
        }
        if (key == Key.Space) return KeyCode.NewChar(' ');
        // Anything else — punctuation, OEM keys, media keys, etc. — flow
        // through TextInput (which handles layout-specific characters
        // correctly) for plain typing, or get dropped here for Ctrl-combos
        // that don't map cleanly. Future-proof: new Key values land in
        // Unhandled rather than crashing.
        return KeyCode.Unhandled;
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
        // Post-Stage-6 fix: honour availableSize so the view tracks
        // the parent window's size. Previously this returned the
        // FIXED preferred size (Cols × Rows × cellSize) which meant
        // the view never resized when the window did, OnRenderSizeChanged
        // never fired, and the Stage 6 SizeChanged → ResizePseudoConsole
        // chain was dead.
        //
        // The Screen buffer stays at construction-time 30×120 cells
        // internally (full grid runtime resize is a documented Phase 2
        // stage), but cmd.exe will see and adapt to the window's
        // actual dimensions via ResizePseudoConsole, which fixes the
        // visible "text cuts off the right edge" symptom.
        //
        // When availableSize is unbounded (e.g. inside a ScrollViewer
        // or before a parent has been sized), fall back to the fixed
        // preferred size so we still claim a sensible footprint.
        if (_screen is null)
        {
            return Size.Empty;
        }
        var preferredWidth = _cellWidth * _screen.Cols;
        var preferredHeight = _cellHeight * _screen.Rows;
        var width = double.IsPositiveInfinity(availableSize.Width)
            ? preferredWidth
            : availableSize.Width;
        var height = double.IsPositiveInfinity(availableSize.Height)
            ? preferredHeight
            : availableSize.Height;
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
