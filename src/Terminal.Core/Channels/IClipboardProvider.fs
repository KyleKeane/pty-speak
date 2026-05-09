namespace Terminal.Core.Channels

/// Host clipboard abstraction for substrate code that needs to
/// copy or paste through the host's native clipboard surface.
///
/// **Why this lives in Terminal.Core:** the clipboard is a host
/// concern (System.Windows.Clipboard on WPF, Gdk.Clipboard on
/// GTK, NSPasteboard on AppKit), and substrate code in
/// Terminal.Core MUST NOT import host-specific types per
/// `docs/CORE-ABSTRACTION-BOUNDARY.md` §1 + ADR 0001. This
/// interface is the formal seam: substrate consumers (e.g., a
/// future "copy SessionTuple" feature originating in
/// Terminal.Core) accept an `IClipboardProvider` parameter; the
/// composition root (`Terminal.App/Program.fs`) provides a WPF-
/// backed implementation.
///
/// **Today's call sites (NOT cut over in Cycle 31b):**
///
/// - `Terminal.App/Program.fs:2043` — Ctrl+Shift+Y SessionModel
///   history copy. STA-thread + 3s timeout pattern.
/// - `Terminal.App/Diagnostics.fs:726` — Ctrl+Shift+D diagnostic
///   bundle copy. Same STA + 3s timeout pattern.
/// - `Views/TerminalView.cs:681,692,742,744` — paste-into-PTY
///   reads (Ctrl+V / Shift+Insert).
///
/// All five sites continue calling `System.Windows.Clipboard`
/// directly through Cycle 31. This interface formalises the
/// boundary so future cross-platform builds (Linux + GTK,
/// macOS + AppKit) have a typed seam to plug into.
///
/// **STA-thread + timeout contract (write):** WPF clipboard
/// writes require an STA-apartment thread hop; concurrent
/// access from non-STA threads throws or hangs. Implementations
/// on Windows MUST honor the STA contract internally — callers
/// of this interface should not need to know about apartment
/// states. The 3-second timeout (matches the existing
/// `Program.fs:2043` / `Diagnostics.fs:726` pattern) prevents
/// indefinite blocks when another process holds the clipboard
/// open.
///
/// `SetText` returns `true` on success, `false` on timeout or
/// other failure so callers can fall back (e.g., log the
/// content rather than dropping it silently).
type IClipboardProvider =
    /// Set clipboard text. Async to allow STA-thread marshalling.
    /// Returns true on success, false on timeout or other
    /// failure. Implementations SHOULD apply a 3-second timeout
    /// per the existing call-site pattern; longer timeouts risk
    /// blocking the UI thread on a stuck clipboard.
    abstract SetText: text: string -> Async<bool>

    /// Try to read clipboard text. Returns `None` if the
    /// clipboard contains no text or if access fails (no
    /// exception thrown to the caller). Synchronous because
    /// read paths are query-shaped — see
    /// `Views/TerminalView.cs:692` `OnPasteCanExecute` which
    /// short-circuits on an empty clipboard.
    abstract TryGetText: unit -> string option
