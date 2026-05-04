namespace Terminal.Core

open System.Text

// Stage 4.5 — expose `internal` types in Terminal.Core to the
// xUnit assembly so tests can introspect alt-screen back-buffer
// state, `TerminalModes` flags, and `Cursor.SaveStack` depth
// without polluting the public API. Mirrors the precedent in
// `src/Terminal.Accessibility/TerminalAutomationPeer.fs:22-23`.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Tests.Unit")>]
do ()

/// Marker type used by smoke tests to verify the assembly loads.
type Marker = class end

/// SGR colour for a foreground or background slot. Default tracks the
/// terminal's current default; Indexed covers the 256-colour palette
/// (0..15 = ANSI, 16..231 = 6×6×6 cube, 232..255 = grayscale); Rgb
/// holds a 24-bit truecolor specifier from `\x1b[38;2;r;g;bm`.
type ColorSpec =
    | Default
    | Indexed of byte
    | Rgb of byte * byte * byte

/// SGR attribute set carried per cell. Inverse means foreground and
/// background should swap at render time. Stage 3a covers the basic
/// 16-colour set + bold/italic/underline/inverse; truecolor and
/// 256-colour parsing arrive when the parser learns to handle their
/// SGR sub-parameter forms (out of scope for this PR — the data
/// type already supports them via ColorSpec).
[<Struct>]
type SgrAttrs =
    { Fg: ColorSpec
      Bg: ColorSpec
      Bold: bool
      Italic: bool
      Underline: bool
      Inverse: bool }

module SgrAttrs =
    /// Default attributes — reset SGR state.
    let defaults : SgrAttrs =
        { Fg = Default
          Bg = Default
          Bold = false
          Italic = false
          Underline = false
          Inverse = false }

/// A single screen cell. Empty cells carry a space rune.
[<Struct>]
type Cell =
    { Ch: Rune
      Attrs: SgrAttrs }

module Cell =
    /// A blank cell — space character with default SGR.
    let blank : Cell = { Ch = Rune(int ' '); Attrs = SgrAttrs.defaults }

/// Snapshot of a `Cursor`'s saveable state, pushed onto
/// `Cursor.SaveStack` by DECSC (`ESC 7`) and popped by DECRC
/// (`ESC 8`). Forward-compatible: Stage 6 can extend with
/// origin-mode flag, character-set selection, etc., without
/// breaking the stack shape (the field list grows; existing
/// consumers keep working).
type CursorSave =
    { Row: int
      Col: int
      Attrs: SgrAttrs }

/// Cursor position state. Row/Col are 0-indexed internally
/// even though VT sequences address them 1-indexed. Visibility
/// (DECTCEM `?25h/l`) lives in `TerminalModes.CursorVisible`,
/// not here, so there is a single source of truth.
type Cursor =
    { mutable Row: int
      mutable Col: int
      mutable SaveStack: CursorSave list }

module Cursor =
    let create () : Cursor =
        { Row = 0
          Col = 0
          SaveStack = [] }

/// Terminal mode bits centralised so Stages 5/6/7 don't smear
/// them across files. Stage 4.5 wires only `CursorVisible`
/// (DECTCEM `?25h/l`) and `AltScreen` (DECSET `?1049h/l`); the
/// others are stubbed for Stage 6 to flip on the corresponding
/// DECSET dispatches:
///
///   * `DECCKM` (`?1`) — cursor-key application mode; controls
///     whether arrows emit `\x1b[A` (Normal) or `\x1bOA`
///     (Application). Stage 6 reads it from here when
///     translating arrow keys to PTY bytes.
///   * `BracketedPaste` (`?2004`) — when set, pasted text is
///     wrapped in `\x1b[200~ ... \x1b[201~`. Stage 6 reads it.
///   * `FocusReporting` (`?1004`) — when set, the host emits
///     `\x1b[I` / `\x1b[O` on focus gain / loss. Stage 6 reads it.
type TerminalModes =
    { mutable AltScreen: bool
      mutable CursorVisible: bool
      mutable DECCKM: bool
      mutable BracketedPaste: bool
      mutable FocusReporting: bool }

module TerminalModes =
    /// Fresh `TerminalModes` instance with the default mode set:
    /// alt-screen off, cursor visible, every other bit at its
    /// DECSET-default-reset value.
    ///
    /// **Must be a `unit -> TerminalModes` factory, NOT a
    /// `let defaults : TerminalModes = { ... }` static value.**
    /// `TerminalModes` is a regular record (reference semantics),
    /// so a static `let` binding would return the SAME instance
    /// to every caller — and mutations from one `Screen` instance
    /// would leak into every other `Screen`. The static-value
    /// pattern is safe for `SgrAttrs.defaults` and `Cell.blank`
    /// because those are `[<Struct>]` (value semantics, copied
    /// on assignment), but for `TerminalModes` we need a fresh
    /// instance per construction. This mirrors `Cursor.create ()`.
    let create () : TerminalModes =
        { AltScreen = false
          CursorVisible = true
          DECCKM = false
          BracketedPaste = false
          FocusReporting = false }

/// Events emitted by the VT500 state machine in `Terminal.Parser`.
///
/// Mirrors the callback set defined by Paul Williams' DEC ANSI parser
/// (https://vt100.net/emu/dec_ansi_parser.html) and alacritty/vte's
/// `Perform` trait. Each variant captures the parsed structure
/// without any screen-buffer or rendering interpretation; that work
/// belongs to `Screen` (Stage 3) and `Terminal.Semantics` (Stage 5+).
///
/// Caps used by the parser (matching alacritty/vte):
///   * MAX_INTERMEDIATES = 2   — bytes in the 0x20..0x2F range during
///                                CSI / DCS / ESC sequences. Anything
///                                beyond two is dropped silently.
///   * MAX_OSC_PARAMS    = 16  — semicolon-separated OSC fields.
///   * MAX_OSC_RAW       = 1024 — total bytes in the OSC payload
///                                across all params.
type VtEvent =
    /// A single Unicode scalar value to render at the cursor. Multi-
    /// byte UTF-8 sequences are assembled inside the parser; invalid
    /// sequences emit U+FFFD (replacement character).
    | Print of Rune
    /// A C0 (0x00-0x1F) or C1 (0x80-0x9F) control byte that wasn't
    /// itself the start of an escape sequence (e.g., BEL, BS, HT, LF,
    /// CR). Consumers handle these directly.
    | Execute of byte
    /// CSI sequence (`ESC [`). `parms` are the numeric parameters
    /// (defaulted to 0 when omitted), `intermediates` are 0x20..0x2F
    /// bytes that appeared between the parameters and the final byte,
    /// `finalByte` is the dispatch byte (0x40..0x7E), and `priv` is
    /// the optional private-marker byte (`?`, `>`, `<`, `=`) that may
    /// appear immediately after `ESC [` for vendor extensions like
    /// DECSET.
    | CsiDispatch of parms: int[] * intermediates: byte[] * finalByte: char * priv: char option
    /// Bare ESC sequence (`ESC <intermediates> <final>`). Used by
    /// DECKPAM (`ESC =`), DECKPNM (`ESC >`), DECSC (`ESC 7`),
    /// DECRC (`ESC 8`), and similar.
    | EscDispatch of intermediates: byte[] * finalByte: char
    /// OSC sequence (`ESC ]`). `parms` are the semicolon-separated
    /// payload fields preserved as raw bytes. `bellTerminated` is true
    /// when the sequence ended with BEL (0x07), false when it ended
    /// with ST (`ESC \`); some hosts care about the distinction (e.g.
    /// xterm OSC 52).
    | OscDispatch of parms: byte[][] * bellTerminated: bool
    /// DCS hook (`ESC P`). Begins a DCS pass-through string that is
    /// terminated by ST (`ESC \`). Stage 1's parser emits the
    /// hook/put/unhook events but doesn't interpret the payload —
    /// Sixel, ReGIS, and friends are deferred.
    | DcsHook of parms: int[] * intermediates: byte[] * finalByte: char
    /// A single byte of DCS payload between DcsHook and DcsUnhook.
    | DcsPut of byte
    /// End of a DCS sequence (ST received).
    | DcsUnhook

/// Bus messages routed between subsystems. Stages 4-9 expand this DU.
type BusMessage =
    | Placeholder

/// Earcons (audio cues). Stage 7 expands this DU.
type Earcon =
    | Placeholder

/// Accessibility markers / regions. Stage 6 expands this DU.
type AccessibilityMarker =
    | Placeholder

/// Notifications emitted by the parser/screen subsystem onto
/// a `Channel<ScreenNotification>` consumed by the UIA peer
/// for `RaiseNotificationEvent`. The audit-cycle PR-B added
/// this DU as the **seam** Stage 5's coalescer plugs into:
/// today every parser-applied batch produces one
/// `RowsChanged`, and Stage 5 will insert a coalescer between
/// the parser and this channel that batches / dedups /
/// rate-limits without changing the channel's contract.
///
/// The companion `ParserError` case closes the cross-cutting
/// "parser exceptions are silently swallowed" gap that the
/// audit identified in `startReaderLoop`'s `with | _ -> ()`
/// branch — instead of dropping unexpected exceptions, the
/// reader publishes one and NVDA hears about it.
type ScreenNotification =
    /// Rows in the screen buffer changed since the last
    /// notification. The list is the row indices that changed
    /// in this batch (typically contiguous, but not required).
    /// Stage 5's coalescer collapses many of these into one
    /// before they reach the UIA peer.
    ///
    /// Today the parser publishes `RowsChanged []` (empty list
    /// — "something changed, you decide what to read"). Stage 5's
    /// coalescer reads `Screen.SequenceNumber` + `SnapshotRows`
    /// to compute the actual row diffs.
    | RowsChanged of int list
    /// The parser / reader loop hit an unexpected exception
    /// that would otherwise have been swallowed. Surfaced via
    /// NVDA so the user knows something went wrong rather
    /// than the terminal silently freezing.
    | ParserError of string
    /// A `TerminalModes` flag changed value. Stage 4.5 PR-B's
    /// alt-screen toggle (`?1049h/l`) is emitted as
    /// `ModeChanged(AltScreen, true/false)` so Stage 5's
    /// coalescer can treat it as a hard flush barrier (the
    /// buffer just changed wholesale; pending coalesced text
    /// must drain first; frame-hash + spinner state must
    /// reset). Stage 6 emits the same shape for DECCKM,
    /// bracketed paste, and focus reporting transitions.
    /// Emitted **after** the `lock gate` release in
    /// `Screen.enterAltScreen` / `exitAltScreen` so the channel
    /// publish doesn't extend the gate's hold.
    | ModeChanged of flag: TerminalModeFlag * value: bool
    /// Stage 8d.1 — BEL (0x07) byte received. The shell emitted
    /// the bell character; the terminal frontend is expected to
    /// produce an audible cue. Pure signal — no Cell-buffer
    /// mutation. The Stage 8d Earcon profile (in
    /// `src/Terminal.Core/EarconProfile.fs`) maps the resulting
    /// `OutputEvent.BellRang` (built by
    /// `OutputEventBuilder.fromScreenNotification`) to a
    /// `RenderEarcon "bell-ping"` ChannelDecision; the Earcon
    /// channel (in `src/Terminal.Core/EarconChannel.fs` +
    /// `src/Terminal.Audio/EarconPlayer.fs`) plays the WASAPI
    /// sine tone. Emitted **after** the Screen.Apply lock
    /// release, same pattern as ModeChanged, so the channel
    /// publish doesn't extend the gate's hold.
    | Bell

/// Discriminator for which `TerminalModes` bit just flipped.
/// Mirrors the field names on the `TerminalModes` record so the
/// coalescer + future Stage 6 input layer can pattern-match on
/// the specific mode rather than always re-reading the record.
and TerminalModeFlag =
    | AltScreen
    | CursorVisible
    | DECCKM
    | BracketedPaste
    | FocusReporting

/// Stable activity-ID vocabulary for `RaiseNotificationEvent`'s
/// `activityId` parameter. NVDA can be configured per-tag (mute
/// updates, prioritise errors, etc.); using a constant module
/// prevents typo'd magic strings from drifting across stages.
///
/// Each tag is namespaced with `pty-speak.` so user-visible
/// NVDA configuration screens distinguish our notifications
/// from any other application's.
///
/// **Pairing contract (PR-N).** `ActivityIds` and
/// `AnnounceSanitiser` (in `src/Terminal.Core/AnnounceSanitiser.fs`)
/// are paired: every UIA Notification this app raises MUST be
/// (1) sanitised through `AnnounceSanitiser.sanitise` first to
/// strip C0 / DEL / C1 / BiDi / Trojan-Source codepoints, and
/// (2) tagged with a stable `ActivityIds.*` value so per-tag
/// NVDA verbosity configuration works. New notification sources
/// added in future stages (Output framework cycle Part 3
/// per-profile alerts; Input framework cycle Part 4 echo /
/// suggestion announces; Stage 10 review-mode quick-nav cues)
/// MUST follow this two-step pattern. Skipping sanitisation
/// surfaces PTY-originated control bytes to NVDA which verbalises
/// them ("escape bracket one A"); skipping the activityId tag
/// breaks per-class user configuration. The drain task in
/// `Program.fs compose ()` enforces this for the Coalescer's
/// `OutputBatch` / `ErrorPassthrough` / `ModeBarrier` channels;
/// future channels added at the same seam inherit the
/// enforcement automatically.
module ActivityIds =
    /// Streaming PTY output (Stage 5 coalescer's primary channel).
    let output = "pty-speak.output"
    /// Velopack auto-update flow (Stage 11 `Ctrl+Shift+U`).
    let update = "pty-speak.update"
    /// Parser / reader-loop exception surfaced via the
    /// notification channel.
    let error = "pty-speak.error"
    /// Diagnostic launcher (`Ctrl+Shift+D` → process-cleanup
    /// PowerShell script).
    let diagnostic = "pty-speak.diagnostic"
    /// "Draft a new release" form launcher (`Ctrl+Shift+R`).
    /// Opens the GitHub `/releases/new` page so the maintainer
    /// can cut a preview without leaving pty-speak.
    let newRelease = "pty-speak.new-release"
    /// Terminal-mode transition (alt-screen, etc.) — emitted
    /// when Stage 5's coalescer flushes around a `ModeChanged`
    /// event. Today the announcement is empty (silent flush
    /// barrier); a future stage may flip to a verbosity-aware
    /// message.
    let mode = "pty-speak.mode"
    /// Shell hot-switch announcements (Stage 7 PR-C
    /// `Ctrl+Shift+1` / `Ctrl+Shift+2`). Emits the
    /// "Switching to {target}." pre-launch cue and the
    /// "Switched to {target}." post-spawn confirmation. Tagged
    /// distinctly so users can configure NVDA's notification
    /// processing for shell-switch announcements separately
    /// from streaming output.
    let shellSwitch = "pty-speak.shell-switch"

    /// Debug-logging toggle announcements (Stage 7-followup
    /// PR-E `Ctrl+Shift+G`). Emits "Debug logging on." /
    /// "Debug logging off." after each toggle so the user can
    /// confirm the current state without checking a log file.
    /// Distinct ActivityId so users can configure NVDA's
    /// notification processing for diagnostic-config
    /// announcements separately from streaming output.
    let logToggle = "pty-speak.log-toggle"

    /// Health-check announcements (Stage 7-followup PR-F
    /// `Ctrl+Shift+H`). Emits a one-line state snapshot — shell
    /// name + PID, log level, reader liveness (last-byte
    /// staleness), notification-channel and coalesced-channel
    /// queue depths. Lets a screen-reader user determine in one
    /// keystroke whether pty-speak is healthy or wedged, instead
    /// of inferring from "is NVDA reading anything?". Distinct
    /// ActivityId so diagnostic-config announcements stay
    /// configurable separately from streaming output.
    let healthCheck = "pty-speak.health-check"

    /// Incident-marker announcements (Stage 7-followup PR-F
    /// `Ctrl+Shift+B`). Emits a confirmation that the user just
    /// dropped a marker line into the active log file, plus the
    /// instruction to reproduce the issue and copy the log via
    /// `Ctrl+Shift+;`. Distinct ActivityId so the marker
    /// announcement isn't suppressed by streaming-output
    /// processing.
    let incidentMarker = "pty-speak.incident-marker"
