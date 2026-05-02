module PtySpeak.Tests.Unit.KeyEncodingTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Stage 6 PR-B — KeyEncoding behavioural pinning
// ---------------------------------------------------------------------
//
// `KeyEncoding.encode` is a pure function from
// `KeyCode * KeyModifiers * TerminalModes` to `byte[] option`. These
// tests pin the xterm-style encoding tables that bash, zsh, fish,
// PowerShell, and Claude Code all consume correctly. The encoder
// does not depend on WPF; tests run from F# without spinning up a
// WPF dispatcher.

let private modesNoneSet () : TerminalModes = TerminalModes.create()

let private modesDecckm () : TerminalModes =
    let m = TerminalModes.create()
    m.DECCKM <- true
    m

let private encode key m modes = KeyEncoding.encode key m modes

let private bytesEq (expected: byte[]) (actual: byte[] option) =
    match actual with
    | Some bs -> Assert.Equal<byte[]>(expected, bs)
    | None -> Assert.Fail("Expected Some bytes; got None")

// ---------------------------------------------------------------------
// Cursor keys — DECCKM normal vs application
// ---------------------------------------------------------------------

[<Fact>]
let ``Up in DECCKM normal mode encodes as ESC [ A`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'A' |]
        (encode KeyCode.Up KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Up in DECCKM application mode encodes as ESC O A`` () =
    bytesEq [| 0x1Buy; byte 'O'; byte 'A' |]
        (encode KeyCode.Up KeyModifiers.None (modesDecckm ()))

[<Fact>]
let ``Down DECCKM normal encodes as ESC [ B`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'B' |]
        (encode KeyCode.Down KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Right DECCKM normal encodes as ESC [ C`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'C' |]
        (encode KeyCode.Right KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Left DECCKM application encodes as ESC O D`` () =
    bytesEq [| 0x1Buy; byte 'O'; byte 'D' |]
        (encode KeyCode.Left KeyModifiers.None (modesDecckm ()))

// Modified cursor keys ALWAYS use CSI form (ESC [ 1 ; mod final),
// regardless of DECCKM mode — this is the xterm convention bash and
// PowerShell rely on.

[<Fact>]
let ``Shift+Up encodes as ESC [ 1 ; 2 A regardless of DECCKM`` () =
    let expected = "\x1b[1;2A"B
    bytesEq expected (encode KeyCode.Up KeyModifiers.Shift (modesNoneSet ()))
    bytesEq expected (encode KeyCode.Up KeyModifiers.Shift (modesDecckm ()))

[<Fact>]
let ``Ctrl+Right encodes as ESC [ 1 ; 5 C`` () =
    bytesEq "\x1b[1;5C"B
        (encode KeyCode.Right KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Alt+Left encodes as ESC [ 1 ; 3 D`` () =
    bytesEq "\x1b[1;3D"B
        (encode KeyCode.Left KeyModifiers.Alt (modesNoneSet ()))

[<Fact>]
let ``Ctrl+Shift+Down encodes as ESC [ 1 ; 6 B`` () =
    bytesEq "\x1b[1;6B"B
        (encode KeyCode.Down (KeyModifiers.Control ||| KeyModifiers.Shift)
            (modesNoneSet ()))

// ---------------------------------------------------------------------
// Editing keypad
// ---------------------------------------------------------------------

[<Fact>]
let ``Insert encodes as ESC [ 2 ~`` () =
    bytesEq "\x1b[2~"B (encode KeyCode.Insert KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Delete encodes as ESC [ 3 ~`` () =
    bytesEq "\x1b[3~"B (encode KeyCode.Delete KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``PageUp encodes as ESC [ 5 ~`` () =
    bytesEq "\x1b[5~"B (encode KeyCode.PageUp KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``PageDown encodes as ESC [ 6 ~`` () =
    bytesEq "\x1b[6~"B (encode KeyCode.PageDown KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Modified Insert: Shift+Insert encodes as ESC [ 2 ; 2 ~`` () =
    bytesEq "\x1b[2;2~"B (encode KeyCode.Insert KeyModifiers.Shift (modesNoneSet ()))

[<Fact>]
let ``Home encodes as ESC [ H (no tilde, modern xterm)`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'H' |]
        (encode KeyCode.Home KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``End encodes as ESC [ F`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'F' |]
        (encode KeyCode.End KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Ctrl+Home encodes as ESC [ 1 ; 5 H`` () =
    bytesEq "\x1b[1;5H"B (encode KeyCode.Home KeyModifiers.Control (modesNoneSet ()))

// ---------------------------------------------------------------------
// Function keys
// ---------------------------------------------------------------------

[<Fact>]
let ``F1-F4 use SS3 form (ESC O P/Q/R/S)`` () =
    bytesEq [| 0x1Buy; byte 'O'; byte 'P' |]
        (encode KeyCode.F1 KeyModifiers.None (modesNoneSet ()))
    bytesEq [| 0x1Buy; byte 'O'; byte 'Q' |]
        (encode KeyCode.F2 KeyModifiers.None (modesNoneSet ()))
    bytesEq [| 0x1Buy; byte 'O'; byte 'R' |]
        (encode KeyCode.F3 KeyModifiers.None (modesNoneSet ()))
    bytesEq [| 0x1Buy; byte 'O'; byte 'S' |]
        (encode KeyCode.F4 KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``F5-F12 use CSI form with the documented numbers`` () =
    bytesEq "\x1b[15~"B (encode KeyCode.F5 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[17~"B (encode KeyCode.F6 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[18~"B (encode KeyCode.F7 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[19~"B (encode KeyCode.F8 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[20~"B (encode KeyCode.F9 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[21~"B (encode KeyCode.F10 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[23~"B (encode KeyCode.F11 KeyModifiers.None (modesNoneSet ()))
    bytesEq "\x1b[24~"B (encode KeyCode.F12 KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Modified F1 uses CSI form (ESC [ 1 ; mod P)`` () =
    bytesEq "\x1b[1;5P"B (encode KeyCode.F1 KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Modified F5 uses CSI form (ESC [ 15 ; mod ~)`` () =
    bytesEq "\x1b[15;3~"B (encode KeyCode.F5 KeyModifiers.Alt (modesNoneSet ()))

// ---------------------------------------------------------------------
// Whitespace / control
// ---------------------------------------------------------------------

[<Fact>]
let ``Tab encodes as 0x09`` () =
    bytesEq [| 0x09uy |] (encode KeyCode.Tab KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Shift+Tab encodes as ESC [ Z`` () =
    bytesEq [| 0x1Buy; byte '['; byte 'Z' |]
        (encode KeyCode.Tab KeyModifiers.Shift (modesNoneSet ()))

[<Fact>]
let ``Enter encodes as 0x0D (CR only; cmd handles)`` () =
    bytesEq [| 0x0Duy |] (encode KeyCode.Enter KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Alt+Enter encodes as ESC + CR`` () =
    bytesEq [| 0x1Buy; 0x0Duy |] (encode KeyCode.Enter KeyModifiers.Alt (modesNoneSet ()))

[<Fact>]
let ``Escape encodes as 0x1B`` () =
    bytesEq [| 0x1Buy |] (encode KeyCode.Escape KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Backspace encodes as 0x7F (DEL, modern xterm default)`` () =
    bytesEq [| 0x7Fuy |] (encode KeyCode.Backspace KeyModifiers.None (modesNoneSet ()))

// ---------------------------------------------------------------------
// Char + Ctrl/Alt encoding
// ---------------------------------------------------------------------

[<Fact>]
let ``Bare 'a' encodes as 0x61`` () =
    bytesEq [| 0x61uy |] (encode (KeyCode.Char 'a') KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Ctrl+'a' encodes as 0x01`` () =
    bytesEq [| 0x01uy |] (encode (KeyCode.Char 'a') KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Ctrl+'A' (uppercase) also encodes as 0x01 — Shift is absorbed for Ctrl-letter`` () =
    bytesEq [| 0x01uy |] (encode (KeyCode.Char 'A') KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Alt+'a' encodes as ESC + 'a' (xterm convention)`` () =
    bytesEq [| 0x1Buy; 0x61uy |]
        (encode (KeyCode.Char 'a') KeyModifiers.Alt (modesNoneSet ()))

[<Fact>]
let ``Ctrl+Alt+'a' encodes as ESC + 0x01`` () =
    bytesEq [| 0x1Buy; 0x01uy |]
        (encode (KeyCode.Char 'a') (KeyModifiers.Control ||| KeyModifiers.Alt)
            (modesNoneSet ()))

[<Fact>]
let ``Ctrl+@ encodes as NUL (0x00)`` () =
    bytesEq [| 0x00uy |] (encode (KeyCode.Char '@') KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Ctrl+[ encodes as ESC (0x1B)`` () =
    bytesEq [| 0x1Buy |] (encode (KeyCode.Char '[') KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Ctrl+? encodes as DEL (0x7F)`` () =
    bytesEq [| 0x7Fuy |] (encode (KeyCode.Char '?') KeyModifiers.Control (modesNoneSet ()))

[<Fact>]
let ``Non-ASCII char returns None (TextInput owns these)`` () =
    Assert.Equal(None, encode (KeyCode.Char 'é') KeyModifiers.None (modesNoneSet ()))

[<Fact>]
let ``Unhandled key returns None`` () =
    Assert.Equal(None, encode KeyCode.Unhandled KeyModifiers.None (modesNoneSet ()))

// ---------------------------------------------------------------------
// Bracketed paste — wrapping + paste-injection defence
// ---------------------------------------------------------------------

[<Fact>]
let ``Plain paste with ?2004 set wraps in brackets`` () =
    let result = KeyEncoding.encodePaste "hello" true
    Assert.Equal<byte[]>("\x1b[200~hello\x1b[201~"B, result)

[<Fact>]
let ``Plain paste with ?2004 clear is raw bytes`` () =
    let result = KeyEncoding.encodePaste "hello" false
    Assert.Equal<byte[]>("hello"B, result)

[<Fact>]
let ``Embedded ESC [ 201 ~ is stripped from paste content (injection defence)`` () =
    // Attack scenario: clipboard contains "safe text\x1b[201~rm -rf /"
    // — without stripping, the bracketed-paste frame closes early and
    // "rm -rf /" runs as if typed.
    let input = "safe text\x1b[201~rm -rf /"
    let result = KeyEncoding.encodePaste input true
    let expected = "\x1b[200~safe textrm -rf /\x1b[201~"B
    Assert.Equal<byte[]>(expected, result)

[<Fact>]
let ``Embedded ESC [ 201 ~ is stripped even when ?2004 is clear`` () =
    // Defence in depth — no legitimate shell content contains this
    // exact byte sequence; stripping never has a behavioural cost.
    let result = KeyEncoding.encodePaste "before\x1b[201~after" false
    Assert.Equal<byte[]>("beforeafter"B, result)

[<Fact>]
let ``Multi-line paste preserves newlines`` () =
    let result = KeyEncoding.encodePaste "line1\nline2" true
    Assert.Equal<byte[]>("\x1b[200~line1\nline2\x1b[201~"B, result)

[<Fact>]
let ``Unicode paste survives as UTF-8`` () =
    // U+00E9 = é = 0xC3 0xA9 in UTF-8.
    let result = KeyEncoding.encodePaste "café" false
    Assert.Equal<byte[]>([| 0x63uy; 0x61uy; 0x66uy; 0xC3uy; 0xA9uy |], result)

// ---------------------------------------------------------------------
// Focus reporting bytes — pinned values
// ---------------------------------------------------------------------

[<Fact>]
let ``focusGained is ESC [ I`` () =
    Assert.Equal<byte[]>([| 0x1Buy; byte '['; byte 'I' |], KeyEncoding.focusGained)

[<Fact>]
let ``focusLost is ESC [ O`` () =
    Assert.Equal<byte[]>([| 0x1Buy; byte '['; byte 'O' |], KeyEncoding.focusLost)

// ---------------------------------------------------------------------
// modifierParam — xterm SGR-modifier protocol
// ---------------------------------------------------------------------

[<Fact>]
let ``modifierParam: None=1, Shift=2, Alt=3, Ctrl=5, Ctrl+Shift+Alt=8`` () =
    Assert.Equal(1, KeyEncoding.modifierParam KeyModifiers.None)
    Assert.Equal(2, KeyEncoding.modifierParam KeyModifiers.Shift)
    Assert.Equal(3, KeyEncoding.modifierParam KeyModifiers.Alt)
    Assert.Equal(4, KeyEncoding.modifierParam (KeyModifiers.Shift ||| KeyModifiers.Alt))
    Assert.Equal(5, KeyEncoding.modifierParam KeyModifiers.Control)
    Assert.Equal(6, KeyEncoding.modifierParam (KeyModifiers.Control ||| KeyModifiers.Shift))
    Assert.Equal(7, KeyEncoding.modifierParam (KeyModifiers.Control ||| KeyModifiers.Alt))
    Assert.Equal(8, KeyEncoding.modifierParam
                       (KeyModifiers.Control ||| KeyModifiers.Shift ||| KeyModifiers.Alt))

// ---------------------------------------------------------------------
// encodeOrNull — C# interop wrapper
// ---------------------------------------------------------------------

[<Fact>]
let ``encodeOrNull returns bytes for handled keys`` () =
    let result = KeyEncoding.encodeOrNull KeyCode.Up KeyModifiers.None (modesNoneSet ())
    Assert.NotNull(result)

[<Fact>]
let ``encodeOrNull returns null for Unhandled`` () =
    let result = KeyEncoding.encodeOrNull KeyCode.Unhandled KeyModifiers.None (modesNoneSet ())
    Assert.Null(result)
