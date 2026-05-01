module PtySpeak.Tests.Unit.AnnounceSanitiserTests

open Xunit
open Terminal.Core

/// Direct unit tests for `AnnounceSanitiser.sanitise`, the
/// control-character chokepoint introduced in audit-cycle SR-2.
/// `UpdateMessagesTests` exercises the function via its consumer
/// (`announcementForException`); these tests pin the contract
/// directly so a future caller can rely on the documented
/// behaviour without inferring it through the consumer.
///
/// Strip rule (per SECURITY.md row TC-5 + maintainer decision
/// item 1): remove every C0 control (U+0000..U+001F), DEL
/// (U+007F), and C1 control (U+0080..U+009F). Printable Unicode,
/// including BMP and non-BMP characters via UTF-16 surrogates,
/// is preserved verbatim.

[<Fact>]
let ``empty input returns empty`` () =
    Assert.Equal("", AnnounceSanitiser.sanitise "")

[<Fact>]
let ``pure-ASCII printable input is unchanged`` () =
    let s = "Hello, world! Path: C:\\Users\\test (#1)"
    Assert.Equal(s, AnnounceSanitiser.sanitise s)

[<Fact>]
let ``strips C0 control characters`` () =
    // 0x00..0x1F including BEL (0x07), TAB (0x09), LF (0x0A),
    // CR (0x0D), ESC (0x1B). Today's announcements are
    // single-line; if Stage 5 needs `\n` to pass through it
    // can flip the rule then.
    let input = "a\x00b\x07c\x09d\x0Ae\x0Df\x1Bg\x1Fh"
    let expected = "abcdefgh"
    Assert.Equal(expected, AnnounceSanitiser.sanitise input)

[<Fact>]
let ``strips DEL (0x7F)`` () =
    let input = "before\x7Fafter"
    Assert.Equal("beforeafter", AnnounceSanitiser.sanitise input)

[<Fact>]
let ``strips C1 control characters`` () =
    // U+0080..U+009F. Includes CSI (U+009B), ST (U+009C),
    // OSC (U+009D) — the 8-bit-encoded versions of the
    // sequences SR-1 already caps in the parser.
    let input = "xyzwq"
    Assert.Equal("xyzwq", AnnounceSanitiser.sanitise input)

[<Fact>]
let ``preserves BiDi override (printable Unicode)`` () =
    // U+202E is RIGHT-TO-LEFT OVERRIDE — a printable Unicode
    // character (in the General-Punctuation block), NOT a
    // control byte. The strip-all-controls rule preserves it.
    // A stricter homograph-defence policy belongs in a
    // separate sanitiser if Stage 5 wants one.
    let input = "abc‮def"
    Assert.Equal(input, AnnounceSanitiser.sanitise input)

[<Fact>]
let ``preserves multi-byte UTF-8 high-plane characters`` () =
    // U+1F600 GRINNING FACE — non-BMP, encoded as a UTF-16
    // surrogate pair. The sanitiser walks `char` (UTF-16
    // code units), so each surrogate's code-point value is
    // in the surrogate range (0xD800..0xDFFF), well outside
    // the C0/DEL/C1 strip ranges.
    let emoji = "😀"  // U+1F600
    let input = sprintf "before %s after" emoji
    Assert.Equal(input, AnnounceSanitiser.sanitise input)

[<Fact>]
let ``preserves combining marks`` () =
    // U+0301 COMBINING ACUTE ACCENT. Not a control character;
    // sanitiser must leave combining marks intact so the
    // rendered "é" still composes correctly.
    let input = "é"
    Assert.Equal(input, AnnounceSanitiser.sanitise input)

[<Fact>]
let ``strips a long control-byte run without losing surrounding context`` () =
    // Defensive against any StringBuilder-grow off-by-one
    // when a long control-only run sits between two
    // printable runs.
    let ctrls = String.replicate 200 "\x07"
    let input = sprintf "alpha%somega" ctrls
    Assert.Equal("alphaomega", AnnounceSanitiser.sanitise input)
