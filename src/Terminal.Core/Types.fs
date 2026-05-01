namespace Terminal.Core

open System.Text

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

/// Cursor position and visibility state. Row/Col are 0-indexed
/// internally even though VT sequences address them 1-indexed.
type Cursor =
    { mutable Row: int
      mutable Col: int
      mutable Visible: bool
      mutable SaveStack: (int * int) list }

module Cursor =
    let create () : Cursor =
        { Row = 0
          Col = 0
          Visible = true
          SaveStack = [] }

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
    | RowsChanged of int list
    /// The parser / reader loop hit an unexpected exception
    /// that would otherwise have been swallowed. Surfaced via
    /// NVDA so the user knows something went wrong rather
    /// than the terminal silently freezing.
    | ParserError of string
