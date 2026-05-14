using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Terminal.Accessibility;
using Terminal.Core;
using Terminal.Core.Channels;

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
    /// Cycle 32b — first consumer of the <see cref="IDisplayBuffer"/>
    /// boundary interface declared in Cycle 31b. Composition root
    /// constructs an adapter wrapping the same <see cref="Screen"/>
    /// passed to <see cref="SetScreen"/>, then calls
    /// <see cref="SetDisplayBuffer"/>. Future renderers (Avalonia,
    /// GTK, AppKit) inject a different <c>IDisplayBuffer</c>
    /// implementation; the C# render loop is unchanged.
    /// </summary>
    private IDisplayBuffer? _displayBuffer;

    /// <summary>
    /// Cycle 46 PR-B — backing store for the UIA Text pattern.
    /// Replaces the screen-grid <see cref="Screen"/> snapshot
    /// the pre-PR-B Text provider read; the pre-PR-B types
    /// (<c>TerminalTextProvider</c> / <c>TerminalTextRange</c>)
    /// were deleted in PR-D. Set post-construction via
    /// <see cref="SetContentHistory"/>
    /// mirroring the existing <see cref="SetScreen"/> /
    /// <see cref="SetDisplayBuffer"/> injection pattern;
    /// <see cref="ContentHistoryTextProvider"/> captures the
    /// closure over this field so UIA queries that arrive
    /// before the wiring runs return an empty range rather
    /// than throwing.
    /// See <c>docs/adr/0002-uia-textedit-caret-output.md</c>.
    /// </summary>
    private ContentHistory.T? _contentHistory;

    /// <summary>
    /// Cycle 47 follow-up (2026-05-13) post-preview.114 — UTC
    /// timestamp of the most recent <see cref="OnPreviewKeyDown"/>
    /// firing. Read by <see cref="ContentHistoryTextProvider"/>'s
    /// keystroke-source delegate so the UIA Text-pattern view can
    /// suppress the active (unsealed) TextSpan while the user is
    /// typing — NVDA's <c>ITextProvider</c> polling otherwise
    /// announces the accreting mid-keystroke text deltas
    /// ("e", "ec", "ech", ...) as inserted text, independent of
    /// the user's "Speak typed characters" NVDA setting. Written
    /// on the WPF UI thread (where key events fire); read on the
    /// UIA RPC thread (where NVDA queries DocumentRange). Atomic
    /// 64-bit reads / writes on x64 + ARM64 — no lock needed.
    /// </summary>
    private DateTime _lastKeystrokeAtUtc = DateTime.MinValue;

    /// <summary>
    /// Public read-only view of <see cref="_lastKeystrokeAtUtc"/>
    /// for the composition root's idle-flush typing-window gate
    /// (Cycle 47 follow-up post-preview.116). Read on the WPF
    /// dispatcher thread by the idle-flush timer tick.
    /// </summary>
    public DateTime LastKeystrokeAtUtc => _lastKeystrokeAtUtc;

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

    // Cycle 25b-1a — `_copyActiveLogToClipboard` field +
    // `SetCopyLogToClipboardHandler` setter + the OemSemicolon
    // direct-handler in OnPreviewKeyDown were removed alongside
    // the Ctrl+Shift+L hotkey itself. Ctrl+Shift+D's bundle is
    // now the single paste-the-log path.

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
    // Cycle 46 PR-B widened this from `TerminalTextProvider`
    // (the screen-grid-backed implementation) to the public
    // `ITextProvider` interface so we can swap in
    // `ContentHistoryTextProvider`. PR-D deleted the legacy
    // type. The single consumer is `TerminalAutomationPeer`'s
    // constructor below, which has always taken `ITextProvider`.
    internal ITextProvider TextProvider { get; }

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

        // Cycle 46 PR-B — UIA Text pattern is backed by
        // ContentHistory (via ContentHistoryTextProvider). The
        // closure captures the post-construction field
        // `_contentHistory`; UIA queries arriving before
        // `SetContentHistory` runs resolve to null and produce
        // an empty range. PR-D deleted the legacy screen-grid
        // `TerminalTextProvider`. See
        // `docs/adr/0002-uia-textedit-caret-output.md`.
        TextProvider = new ContentHistoryTextProvider(
            () => _contentHistory,
            () => _lastKeystrokeAtUtc);

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

    // Cycle 25b-1a — `SetCopyLogToClipboardHandler` deleted with
    // the Ctrl+Shift+L hotkey itself.

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
    /// Cycle 32b — wires the <see cref="IDisplayBuffer"/> snapshot
    /// surface for the UI render path. Composition root calls this
    /// immediately after <see cref="SetScreen"/> with an adapter
    /// that wraps the same <see cref="Screen"/> instance. Mirrors
    /// the existing <see cref="SetScreen"/> / <see cref="SetPtyHost"/>
    /// post-construction-injection pattern.
    /// </summary>
    public void SetDisplayBuffer(IDisplayBuffer displayBuffer)
    {
        _displayBuffer = displayBuffer
            ?? throw new ArgumentNullException(nameof(displayBuffer));
    }

    /// <summary>
    /// Cycle 46 PR-B — wires the
    /// <see cref="ContentHistory.T"/> instance that backs the
    /// UIA Text pattern. Called once at composition from
    /// <c>Program.fs compose ()</c> after the substrate is
    /// constructed; mirrors the existing
    /// <see cref="SetScreen"/> / <see cref="SetDisplayBuffer"/>
    /// post-construction-injection pattern.
    /// </summary>
    /// <remarks>
    /// The <see cref="ContentHistoryTextProvider"/> constructed
    /// in the view's constructor captures a closure over
    /// <c>_contentHistory</c>; before this method runs, that
    /// closure resolves to null and UIA queries return an empty
    /// range rather than throwing. After this method runs, UIA
    /// queries materialise the last 256 KB of the substrate's
    /// tail through <c>ContentHistory.tailText</c>.
    /// </remarks>
    public void SetContentHistory(ContentHistory.T contentHistory)
    {
        _contentHistory = contentHistory
            ?? throw new ArgumentNullException(nameof(contentHistory));
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
        // Cycle 45c follow-up (2026-05-12) — every announce uses
        // `MostRecent`. Prior behaviour kept `ActivityIds.output`
        // on `ImportantAll`, which queues every announce and asks
        // NVDA to read them all to completion. Combined with
        // Cycle 45f's `TupleFinalOnly` streaming default the
        // result was: one `dir` produced a single ~3 KB output
        // announce; NVDA would chew through it for ~2 minutes
        // before any subsequent announce (the next command's
        // tuple-final, `Alt` to open a menu, hotkey announces)
        // could be heard. Maintainer dogfood 2026-05-12 named
        // the symptom: "I can't use the menus until that large
        // cue is cleared". `MostRecent` lets a later announce
        // displace the queued long-output read; UX prioritises
        // responsiveness over completeness for streaming output.
        // Users who want every byte read to completion can pivot
        // to manual speech-cursor navigation
        // (Ctrl+Shift+Up/Down/End — see SpeechCursor.fs).
        Announce(message, activityId, AutomationNotificationProcessing.MostRecent);
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
        // Streaming-path instrumentation. The peer-present path
        // logs at INFO — bounded by the coalescer's 200ms
        // debounce (~5 emits/sec max), so the volume is well
        // below any I/O lag threshold and the entry is the
        // primary "did the announcement actually fire?" signal
        // for streaming-silence diagnosis. The peer-NULL path
        // stays at WARN: rare, and the smoking-gun signal that
        // a UIA client never connected and notifications are
        // silently dropping. Metadata only: activityId + length;
        // never the message text itself, per SECURITY.md
        // "Logging chokepoint" policy.
        var log = Terminal.Core.Logger.get("PtySpeak.Views.TerminalView.Announce");
        var peer = UIElementAutomationPeer.FromElement(this);
        if (peer is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                log,
                "RaiseNotificationEvent firing. ActivityId={ActivityId} MsgLen={MsgLen} Processing={Processing}",
                activityId, message.Length, processing);
            // Cycle 47 follow-up (2026-05-13) post-preview.114 —
            // first 60 chars of the announce text at Debug so a
            // diagnostic-bundle paste-back can grep `MsgHead=` and
            // see exactly what NVDA was asked to read at each
            // notification raise. Already an audible signal so
            // the privacy posture is unchanged; INFO stays as the
            // metadata-only baseline (length + activityId).
            if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                var head = message.Length <= 60
                    ? message
                    : message.Substring(0, 60) + "...";
                head = Terminal.Core.AnnounceSanitiser.sanitise(head);
                Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                    log,
                    "RaiseNotificationEvent firing. ActivityId={ActivityId} MsgHead={MsgHead}",
                    activityId, head);
            }
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                processing,
                message,
                activityId);
        }
        else
        {
            // Peer is null when no UIA client (NVDA, Inspect.exe,
            // etc.) has connected yet to trigger lazy peer creation.
            // The Announce silently no-ops in this case; logging
            // it tells us *that* it happened so a streaming-silence
            // diagnosis doesn't need to guess whether the peer
            // existed.
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                log,
                "Announce skipped: peer was null. ActivityId={ActivityId} MsgLen={MsgLen}",
                activityId, message.Length);
        }
    }

    /// <summary>
    /// Cycle 37b — receives <see cref="Terminal.Core.RenderInstruction.RenderRaw"/>
    /// payloads marshalled from <see cref="Terminal.Core.NvdaChannel"/> via the
    /// composition root's <c>marshalRawPayload</c> callback, and routes
    /// <see cref="Terminal.Core.SelectionRawPayload"/> to the active
    /// <see cref="Terminal.Accessibility.TerminalAutomationPeer"/>'s
    /// <c>UpdateSelectionState</c>, which materializes / mutates / drops the
    /// virtual list peer and raises UIA events. Cycle 37a stubbed this as
    /// log-only; Cycle 37b promotes to peer-state update.
    /// </summary>
    /// <remarks>
    /// Threading: this method runs on the WPF UI thread (the composition
    /// root in <c>Program.fs</c> wraps the call in <c>Dispatcher.Invoke</c>).
    /// <c>UpdateSelectionState</c> mutates peer fields and calls
    /// <c>RaiseAutomationEvent</c> directly — both safe on the UI thread
    /// without further marshalling.
    ///
    /// If <c>UIElementAutomationPeer.FromElement</c> returns null (no UIA
    /// client has connected yet to trigger lazy peer creation), the
    /// <c>?.</c> null-conditional short-circuits and the update is silently
    /// dropped. This matches the existing <see cref="Announce"/> path's
    /// behaviour for the no-UIA-client case.
    ///
    /// Unknown payload types fall to the warning-log branch so type drift
    /// surfaces in <c>Ctrl+Shift+D</c> bundles for triage.
    /// </remarks>
    public void AnnounceRawPayload(object payload, string activityId)
    {
        if (payload is Terminal.Core.SelectionRawPayload selPayload)
        {
            var peer = UIElementAutomationPeer.FromElement(this) as TerminalAutomationPeer;
            peer?.UpdateSelectionState(selPayload);
        }
        else
        {
            var log = Terminal.Core.Logger.get("PtySpeak.Views.TerminalView.AnnounceRawPayload");
            var typeName = payload?.GetType().Name ?? "null";
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                log,
                "AnnounceRawPayload received unexpected payload type. ActivityId={ActivityId} PayloadType={PayloadType}",
                activityId, typeName);
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
    /// added in future stages append to this list AND the
    /// <c>HotkeyRegistry.builtIns</c> in
    /// <c>src/Terminal.Core/HotkeyRegistry.fs</c>; the spec §6
    /// clause is the third co-equal source of truth.
    ///
    /// Pre-framework-cycle PR-O introduced
    /// <c>HotkeyRegistry</c> as the F#-side canonical source for
    /// the dispatch path (compose-time <c>bindHotkey</c> calls
    /// in <c>Program.fs</c> read it). This C# table remains the
    /// hot-path filter source consulted on every keystroke by
    /// <see cref="OnPreviewKeyDown"/>; keeping it as a static
    /// array avoids C#/F# interop cost per key event. Maintainer
    /// convention: a new hotkey requires updating both surfaces
    /// in the same PR.
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

            // Cycle 25a — reorganized to put the most-used hotkeys
            // on letter keys and free Ctrl+Shift+; entirely:
            //   Ctrl+Shift+P: open the pty-speak data folder
            //                 (parent of logs / sessions / config;
            //                 navigable to any of them in one
            //                 arrow-key step).
            //   Ctrl+Shift+E: edit config.toml (auto-creates
            //                 with defaults if missing).
            //   Ctrl+Shift+; vacated entirely (no alias).
            // Cycle 25b: removed Ctrl+Shift+T placeholder. The
            // diagnostic suite folded into Ctrl+Shift+D (Cycle 25b
            // bundles FileLogger log + config + env into a dated
            // snapshot file + clipboard); the interactive
            // process-cleanup test moves to a future app menu
            // rather than a hotkey.
            // Cycle 25b-1a: removed Ctrl+Shift+L (CopyLatestLog).
            // Ctrl+Shift+D's bundle is now the only paste-the-log
            // path; the dedicated copy-just-the-log hotkey was
            // redundant given the bundle's coverage.
            (Key.P, ModifierKeys.Control | ModifierKeys.Shift, "Open pty-speak data folder"),
            (Key.E, ModifierKeys.Control | ModifierKeys.Shift, "Edit config.toml"),

            // Stage 7 PR-C — hot-switch the spawned shell mid-session.
            // PR-J (2026-05-03) reordered the slots and added
            // PowerShell as a third built-in. Current assignment:
            //   `Ctrl+Shift+1` → cmd.exe
            //   `Ctrl+Shift+2` → powershell.exe (PowerShell)
            //   `Ctrl+Shift+3` → claude.exe (Claude Code)
            // PowerShell sits in slot 2 deliberately: it's the
            // diagnostic control shell — always installed, no auth,
            // no terminal-capability detection — so isolating
            // shell-switch infrastructure bugs from claude-specific
            // issues is one keypress away. Future shells (WSL,
            // Python REPL, bash) claim higher digits without breaking
            // the contract.
            //
            // The handler tears down the running ConPtyHost, resolves
            // the target via `ShellRegistry.tryFind`, spawns a new
            // ConPtyHost, and re-wires `SetPtyHost` callbacks. All
            // three digits are number-row (`Key.D1`/`D2`/`D3`), NOT
            // numpad — numpad-with-NumLock-off carries NVDA review-
            // cursor commands and must stay reachable per the
            // accessibility non-negotiables in CONTRIBUTING.md. NVDA
            // collision check: `Ctrl+Shift+1`/`+2`/`+3` have no
            // default NVDA bindings (digit-only `1`/`2`/`3` and
            // `Shift+1`/`+2`/`+3` browse-mode heading-quick-nav
            // doesn't fire in focus mode, which is pty-speak's
            // mode). Spec authority: §7.5 (added by PR-C, extended
            // by PR-J per chat 2026-05-03 maintainer authorisation
            // for shell registry + hot-switch UX).
            (Key.D1, ModifierKeys.Control | ModifierKeys.Shift, "Switch to cmd shell"),
            (Key.D2, ModifierKeys.Control | ModifierKeys.Shift, "Switch to PowerShell shell"),
            (Key.D3, ModifierKeys.Control | ModifierKeys.Shift, "Switch to claude shell"),

            // Cycle 27 — `Ctrl+Shift+G` (toggle FileLogger
            // min-level) was migrated to the multi-state menu
            // paradigm. The operation now lives under
            // View → Logging Level → Information / Debug, with
            // the current level indicated via WPF
            // `MenuItem.IsChecked`. No keyboard accelerator;
            // `Ctrl+Shift+G` flows through to the shell as plain
            // text. See `HotkeyRegistry.MultiStateCommand`
            // (`Terminal.Core/HotkeyRegistry.fs`).

            // Stage 7-followup PR-F — diagnostic-surface hotkeys.
            //
            // Ctrl+Shift+H — health check: announce a one-line
            // state snapshot (shell + PID, log level, reader
            // last-byte staleness, channel queue depths). Lets a
            // screen-reader user determine in one keystroke whether
            // pty-speak is healthy or wedged, instead of inferring
            // from "is NVDA reading anything?".
            //
            // Ctrl+Shift+B — incident marker: write a clear
            // "=== INCIDENT MARKER {timestamp} ===" line into the
            // active log + announce. The user reproduces the issue,
            // then copies the log via Ctrl+Shift+; — server-side
            // grep for the marker extracts the relevant slice.
            // Replaces the env-var-and-relaunch debug capture
            // workflow with three keystrokes (G, B, ;) entirely
            // inside pty-speak.
            //
            // No NVDA collisions: Ctrl+Shift+H and Ctrl+Shift+B
            // are not default NVDA bindings.
            (Key.H, ModifierKeys.Control | ModifierKeys.Shift, "Health check"),
            (Key.B, ModifierKeys.Control | ModifierKeys.Shift, "Incident marker"),

            // Cycle 27 — `Ctrl+Shift+M` (toggle WASAPI earcons
            // mute) was migrated to the multi-state menu
            // paradigm. The operation now lives under
            // View → Earcons → Enabled / Muted, with the
            // current state indicated via WPF
            // `MenuItem.IsChecked`. No keyboard accelerator;
            // `Ctrl+Shift+M` flows through to the shell as plain
            // text. See `HotkeyRegistry.MultiStateCommand`
            // (`Terminal.Core/HotkeyRegistry.fs`).

            // Cycle 22b — copy SessionModel history to clipboard.
            // Mnemonic: Y for histor*Y*. Dumps the full session
            // history (all completed tuples + any in-flight active
            // tuple) as structured plain text, paste-friendly into
            // chat / bug reports. Companion to Ctrl+Shift+D
            // (diagnostic battery) which announces a substrate
            // summary; Ctrl+Shift+Y dumps the full content for
            // analysis.
            (Key.Y, ModifierKeys.Control | ModifierKeys.Shift, "Copy SessionModel history to clipboard"),

            // Cycle 24e — announce the active session-log file
            // path via NVDA. Mnemonic: S for *S*ession log. Verbose
            // format: announces "Session log mode <mode>;
            // path <full-path>." for SessionLog/Always or
            // "Session log mode memory_only; no file." for
            // MemoryOnly. Companion to Ctrl+Shift+L (open logs
            // folder) — Ctrl+Shift+L opens the file-logger root;
            // Ctrl+Shift+S surfaces the active SessionModel
            // persistence file. NVDA collision check: no default
            // NVDA binding for Ctrl+Shift+S; Windows reserves S
            // only inside specific apps (Photoshop), not at the
            // OS level.
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift, "Announce session-log file path"),

            // Cycle 46 post-audit (2026-05-13) — open the last
            // command output in the default text editor.
            // Mnemonic: O for *O*pen Output. Companion to the
            // 800-char tuple-final Announce cap (Program.fs
            // `OutputAnnounceCapChars`): when a long command
            // (e.g. `dir`, `git log`) produces output that
            // exceeds the cap, the user hears the trailing
            // ~800 chars; Ctrl+Shift+O writes the full
            // OutputText to a fresh timestamped file under
            // %LOCALAPPDATA%\PtySpeak\extracts\ and opens it
            // via the registered .txt handler. NVDA collision
            // check: no default NVDA binding for Ctrl+Shift+O.
            (Key.O, ModifierKeys.Control | ModifierKeys.Shift, "Open last command output in default editor"),

            // Cycle 46 post-audit (2026-05-13) — re-narrate the
            // last command output (capped at the same 800 chars
            // the auto-narrate uses). Mnemonic: A for
            // *A*nnounce. Companion to Ctrl+Shift+O — Ctrl+Shift+O
            // opens the full output in a text editor;
            // Ctrl+Shift+A re-speaks the bounded chunk through
            // NVDA. NVDA collision check: no default NVDA
            // binding for Ctrl+Shift+A.
            (Key.A, ModifierKeys.Control | ModifierKeys.Shift, "Re-narrate last command output (capped)"),

            // Cycle 48 post-PR-F (2026-05-13) — SpeechCursor
            // navigation accelerators. Per maintainer dogfood
            // of preview.118: menu-only routing felt buried.
            // Bound to Ctrl+Shift+Up/Down/End. NVDA collision
            // check: default NVDA review-cursor commands are
            // NVDA+Numpad-cluster gestures; Ctrl+Shift+arrows
            // are free.
            (Key.Up, ModifierKeys.Control | ModifierKeys.Shift, "Speech Cursor: previous entry"),
            (Key.Down, ModifierKeys.Control | ModifierKeys.Shift, "Speech Cursor: next entry"),
            (Key.End, ModifierKeys.Control | ModifierKeys.Shift, "Speech Cursor: jump to latest"),

            // Future entries (NOT yet bound; commented for
            // forward-planning):
            //   (Key.R, ModifierKeys.Alt | ModifierKeys.Shift,
            //    "Stage 10 review-mode toggle"),
            //   Higher Ctrl+Shift+digit slots (4, 5, 6, ...) reserved
            //   for additional shells per Stage 7 PR-C / PR-J.
        ];

    /// <summary>
    /// Stage 6 PR-B — keyboard input pipeline. Filter ordering is
    /// LOAD-BEARING and pinned by xUnit + behavioural tests:
    ///
    /// <list type="number">
    ///   <item><description><b>App-reserved hotkeys first.</b> Any match in
    ///   <see cref="AppReservedHotkeys"/> short-circuits and does NOT mark
    ///   the event handled, so the parent Window's InputBindings can fire
    ///   the corresponding command (Ctrl+Shift+U / D / R / L / ; today;
    ///   future Ctrl+Shift+M, Alt+Shift+R when Stage 9 / 10 land).</description></item>
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

        // Cycle 47 follow-up (2026-05-13) post-preview.114 — record
        // the keystroke time before the gesture-dispatch logic
        // below runs, so the UIA materialiser sees the typing
        // window open on the very first key in a burst. Modifier-
        // only keypresses (Ctrl, Alt, Shift) are excluded because
        // they don't generate cmd-side echo and stamping them
        // would extend the suppression window through chord
        // shortcuts (Ctrl+Shift+D etc.) where the active span
        // genuinely isn't changing.
        if (e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl
            && e.Key != Key.LeftAlt && e.Key != Key.RightAlt
            && e.Key != Key.LeftShift && e.Key != Key.RightShift
            && e.Key != Key.LWin && e.Key != Key.RWin
            && e.Key != Key.System)
        {
            _lastKeystrokeAtUtc = DateTime.UtcNow;
        }

        var pressedModifiers = Keyboard.Modifiers;

        // For Alt-modified gestures WPF reports e.Key == Key.System
        // and the actual key in e.SystemKey. We deliberately do NOT
        // unwrap that here: every reserved hotkey today is a clean
        // Ctrl+Shift gesture (no Alt path), and unwrapping was found
        // to intercept Alt+F4 — F4 reaches the encoder, gets bytes
        // produced, e.Handled becomes true, and the OS window-close
        // gesture dies. Letting Key.System fall through to
        // TranslateKey returns KeyCode.Unhandled, the encoder
        // returns null, e.Handled stays false, and WPF's default
        // Alt+F4 close handler fires. If a future Alt-modified
        // reserved hotkey (Stage 10's Alt+Shift+R review-mode
        // toggle) lands, reintroduce the unwrap with an explicit
        // Alt+F4 fall-through.

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

        // 4.5. Cycle 45 follow-up — navigation-key echo for
        // screen-reader users. Backspace / Left / Right / Home
        // don't fire UIA TextSelectionChangedEvent on our
        // TerminalAutomationPeer the way Notepad's text-edit
        // does, so NVDA's keyboard-echo path has no signal to
        // react to. We bridge the gap by announcing the
        // screen-cell character at the destination/source
        // position BEFORE forwarding the keystroke to the PTY.
        // Read-only — doesn't suppress the key event; the
        // encoder + writeBytes step below still runs.
        if (_screen is not null &&
            !ctrlOrAltHeld &&
            !pressedModifiers.HasFlag(ModifierKeys.Shift))
        {
            AnnounceNavigationEcho(e.Key);
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
    /// Cycle 45 follow-up — read-only helper that announces the
    /// screen-cell character at the navigation key's destination
    /// position. Called from <see cref="OnPreviewKeyDown"/> for
    /// Backspace / Delete / Left / Right / Home before the
    /// keystroke is encoded and forwarded to the PTY. The
    /// PTY-side processing continues unaffected; this is purely
    /// an additional NVDA signal to fill the gap left by our UIA
    /// peer not firing caret-change events.
    /// </summary>
    /// <remarks>
    /// Per-key mapping (mirrors NVDA's text-editor conventions:
    /// Backspace announces the char that just got deleted;
    /// arrows / Delete announce the char that ends up to the
    /// right of the cursor's new position):
    /// <list type="bullet">
    ///   <item><description><see cref="Key.Back"/> (Backspace) —
    ///   char that will be deleted (cell at
    ///   <c>(Cursor.Row, Cursor.Col - 1)</c>). Skipped when cursor
    ///   is already at column 0.</description></item>
    ///   <item><description><see cref="Key.Delete"/> — char that
    ///   will shift left into the cursor position (cell at
    ///   <c>(Cursor.Row, Cursor.Col + 1)</c>; read BEFORE the
    ///   delete so we capture the char that's about to become the
    ///   new "char-to-right-of-cursor"). Skipped when cursor is
    ///   at the last column.</description></item>
    ///   <item><description><see cref="Key.Left"/> — char now to
    ///   the right of the cursor after the move (cell at
    ///   <c>(Cursor.Row, Cursor.Col - 1)</c>). Skipped at column 0.</description></item>
    ///   <item><description><see cref="Key.Right"/> — char now to
    ///   the right of the cursor after the move (cell at
    ///   <c>(Cursor.Row, Cursor.Col + 1)</c>). Skipped at the last
    ///   column. (Earlier revision incorrectly read the cell
    ///   being moved PAST; corrected to match NVDA's
    ///   text-editor convention.)</description></item>
    ///   <item><description><see cref="Key.Home"/> — char at
    ///   <c>(Cursor.Row, 0)</c>.</description></item>
    /// </list>
    /// Up / Down / End / PgUp / PgDn are intentionally NOT handled
    /// here. Up/Down in cmd recall history — the screen rewrites
    /// after cmd responds (without a trailing newline), so the
    /// announce needs to fire after cmd's output settles. That
    /// requires wiring `ContentHistory.tick` into the pump's
    /// `handleTick`, which interacts with the verbosity-mode
    /// design (when does typed-echo announce, when does
    /// cmd-driven content announce). Deferred to a separate cycle
    /// that addresses both concerns together. End requires
    /// scanning for the last non-blank cell which has unclear
    /// semantics for cmd's prompt-line edit buffer.
    /// </remarks>
    private void AnnounceNavigationEcho(Key key)
    {
        if (_screen is null)
        {
            return;
        }
        var cursor = _screen.Cursor;
        var row = cursor.Row;
        var col = cursor.Col;
        int? targetCol = key switch
        {
            Key.Back => col > 0 ? col - 1 : (int?)null,
            Key.Delete => col < _screen.Cols - 1 ? col + 1 : (int?)null,
            Key.Left => col > 0 ? col - 1 : (int?)null,
            Key.Right => col < _screen.Cols - 1 ? col + 1 : (int?)null,
            // Cycle 45 backlog (docs/USER-SETTINGS.md
            // "Navigation-key announce shape"): future user
            // setting could announce the entire current line
            // (or just the typed input portion without the
            // prompt-path prefix) on Home, instead of the char
            // at column 0. Hook the configurable behaviour here
            // by branching on the user-selected mode.
            Key.Home => 0,
            _ => null,
        };
        if (targetCol is null)
        {
            return;
        }
        if (row < 0 || row >= _screen.Rows)
        {
            return;
        }
        var cell = _screen.GetCell(row, targetCol.Value);
        var text = cell.Ch.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        // NVDA itself substitutes "space" / "blank" for ASCII 0x20
        // on review-cursor reads; we forward the raw character and
        // let NVDA's TTS handle the substitution.
        Announce(text, ActivityIds.navigation);
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
        // Cycle 25b-1a — `Ctrl+Shift+;` direct handler removed
        // with the Ctrl+Shift+L hotkey itself. The semicolon
        // gesture passes through to the shell as plain text.

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
    /// <remarks>
    /// Pre-framework-cycle PR-P bumped this from <c>private</c> to
    /// <c>internal</c> so
    /// <c>tests/Tests.Unit/KeyEncodingTests.fs</c> can pin the WPF
    /// adapter directly without a live dispatcher. The Input
    /// framework cycle's echo-correlation logic depends on this
    /// translation being precise.
    /// </remarks>
    internal static KeyModifiers TranslateModifiers(ModifierKeys m)
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
    /// <remarks>
    /// Pre-framework-cycle PR-P bumped this from <c>private</c> to
    /// <c>internal</c> so
    /// <c>tests/Tests.Unit/KeyEncodingTests.fs</c> can pin the WPF
    /// adapter directly without a live dispatcher. The Input
    /// framework cycle's echo-correlation logic depends on the
    /// WPF Key → <see cref="KeyCode"/> → encoded-bytes round-trip
    /// being precise; a silent regression in this map would
    /// silently break echo dedup. See the
    /// "WPF adapter round-trip fixtures" section of that file
    /// for the parametric coverage.
    /// </remarks>
    internal static KeyCode TranslateKey(Key key)
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
        => new TerminalAutomationPeer(this, TextProvider, this.WritePtyBytes);

    /// <summary>
    /// Cycle 37b — public bridge from
    /// <see cref="Terminal.Accessibility.TerminalListItemAutomationPeer"/>'s
    /// <c>IInvokeProvider.Invoke()</c> callback to the View's private
    /// <c>_writeBytes</c> field. Wraps a null check so an Invoke
    /// firing before <see cref="SetPtyHost"/> is wired silently no-ops
    /// rather than throwing — UIA clients (NVDA, Inspect.exe) may
    /// connect to the peer before the composition root finishes
    /// wiring the PTY host.
    /// </summary>
    public void WritePtyBytes(byte[] bytes)
        => _writeBytes?.Invoke(bytes);

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

        // Cycle 32b — both fields must be set by the composition root
        // before the first render frame. SetScreen + SetDisplayBuffer
        // are called sequentially at Program.fs:731-...; in practice
        // both are always non-null here. The null-check is defense
        // in depth + satisfies C# nullable-reference-types analysis
        // (otherwise CS8602 fires on `_displayBuffer.Snapshot(...)`
        // below).
        if (_screen is null || _displayBuffer is null)
        {
            return;
        }

        // Acc/9 — take ONE locked snapshot per render frame instead of
        // calling _screen.GetCell(...) per cell, which would re-acquire
        // the screen gate up to Rows*Cols times and race with the
        // parser thread between cells.
        //
        // SessionModel Tier 1.B (Cycle 12): SnapshotRows return type
        // extended from `int64 * Cell[][]` to
        // `int64 * (int * int) * Cell[][]` (cursor position captured
        // atomically with the snapshot). From C#'s perspective the
        // F# 3-tuple becomes `Tuple<long, Tuple<int, int>, Cell[][]>`
        // — the Cell[][] moves from `.Item2` to `.Item3`. UI rendering
        // doesn't need cursor position, so `.Item2` is discarded.
        //
        // Cycle 32b: snapshot now flows through the IDisplayBuffer
        // boundary (Cycle 31b interface) instead of direct Screen
        // access. Identical tuple shape — `.Item3` access unchanged.
        // `_screen.Cols` stays direct (IDisplayBuffer is a
        // snapshot-only contract; grid sizing is host-surface
        // metadata that future renderers will read from their own
        // surface dimensions).
        var snap = _displayBuffer.Snapshot(0, _screen.Rows);
        var rows = snap.Item3;
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
