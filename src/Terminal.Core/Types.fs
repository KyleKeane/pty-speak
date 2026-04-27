namespace Terminal.Core

open System.Text

/// Marker type used by smoke tests to verify the assembly loads.
type Marker = class end

/// A single screen cell. Stage 3 fills in the real fields.
type Cell = { Placeholder: unit }

/// The screen buffer. Stage 3 fills in the real fields.
type ScreenBuffer = { Placeholder: unit }

/// Events emitted by the VT500 state machine in `Terminal.Parser`.
///
/// Mirrors the callback set defined by Paul Williams' DEC ANSI parser
/// (https://vt100.net/emu/dec_ansi_parser.html) and alacritty/vte's
/// `Perform` trait. Each variant captures the parsed structure
/// without any screen-buffer or rendering interpretation; that work
/// belongs to `Terminal.Semantics` (Stage 3+).
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
