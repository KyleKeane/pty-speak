namespace Terminal.Core

open System.Text

/// Strip control characters from strings before they're handed
/// to NVDA / UIA's `RaiseNotificationEvent`. Audit-cycle SR-2.
///
/// `RaiseNotificationEvent`'s `displayString` parameter is read
/// directly by screen readers; an exception message containing
/// BiDi overrides (U+202E), BEL (0x07), or ANSI escape
/// sequences (0x1B) confuses NVDA's notification handler — at
/// best causing audible artifacts, at worst spoofing the
/// announcement direction (homograph attack via RTL override).
///
/// The sanitiser is the single chokepoint we route every
/// announcement string through. Today's call sites:
///
///   * `Terminal.App.Program.runUpdateFlow`'s `ParserError`
///     construction (interpolates `ex.Message` from the reader
///     loop, which itself contains arbitrary parser exceptions).
///   * `Terminal.Core.UpdateMessages.announcementForException`'s
///     four interpolations of `ex.Message` from the Velopack
///     update flow.
///
/// Stage 5's streaming-output coalescer should also consume
/// this sanitiser before raising any per-row notification.
///
/// See SECURITY.md row TC-5 (control-character stripping) and
/// the maintainer-confirmed strip-all decision (cycle decision
/// item 1: strip ALL of C0 0x00-0x1F, DEL 0x7F, C1 0x80-0x9F).
module AnnounceSanitiser =

    /// Strip every control character from `s`:
    ///   * C0 controls: U+0000..U+001F
    ///   * DEL:         U+007F
    ///   * C1 controls: U+0080..U+009F
    ///
    /// Returns a fresh string with the offending code points
    /// removed. Printable Unicode (including non-BMP characters
    /// via UTF-16 surrogates and combining marks) is preserved.
    /// `null` input returns `null`; empty input returns empty.
    ///
    /// Stage 5 may revisit if multi-line announcements need
    /// `\n` (U+000A) to pass through; today's announcements
    /// are all single-line so the strip-all default is safe.
    let sanitise (s: string) : string =
        if isNull s then null
        elif s.Length = 0 then s
        else
            let sb = StringBuilder(s.Length)
            for ch in s do
                let code = int ch
                let isControl =
                    code < 0x20
                    || code = 0x7F
                    || (code >= 0x80 && code <= 0x9F)
                if not isControl then
                    sb.Append(ch) |> ignore
            sb.ToString()
