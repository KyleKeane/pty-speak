namespace Terminal.Core

open System
open System.Text

/// Stage 6 PR-B — keyboard encoding.
///
/// Pure F# encoder: `KeyCode * KeyModifiers * TerminalModes -> byte[] option`.
/// `Some bytes` means "send these to the PTY"; `None` means "drop this key".
///
/// **Why a private DU instead of WPF's `System.Windows.Input.Key`?**
/// Decoupling the encoder from WPF buys us:
///   * Tests run from F# without spinning up WPF.
///   * A future Linux / macOS port (Avalonia, MAUI, Tauri, web) reuses
///     this module unchanged — only the platform adapter that
///     translates `Key` → `KeyCode` changes.
///   * A future TUI mode (e.g. a CLI agent without a window) reuses
///     this for synthetic-key paths.
///
/// Encoding tables follow the xterm convention as documented in
/// `man xterm` "PC-Style Function Keys" + "VT220-Style Function
/// Keys", with DECCKM application-mode arrow keys per `Modes.DECCKM`.
/// SGR-modifier protocol (`\x1b[1;<mod>A` for modified cursor keys,
/// `\x1b[<n>;<mod>~` for modified editing/F-keys) is the modern
/// xterm default that bash, zsh, fish, PowerShell, and Claude Code
/// all consume correctly.
///
/// The `KeyCode.Char` case is for printable characters that arrive
/// via `PreviewKeyDown` (Ctrl-combos primarily — plain typing routes
/// through `TextInput` which UTF-8-encodes directly without going
/// through this module). The `KeyCode.Unhandled` case is the
/// future-proofing escape hatch: any key the adapter doesn't
/// recognise produces a clean `None` rather than a crash, so a new
/// WPF `Key` value (or any new platform key value) cannot break the
/// encoder without an explicit code change.

[<RequireQualifiedAccess>]
type KeyCode =
    // Cursor keys (DECCKM-aware)
    | Up
    | Down
    | Right
    | Left
    // Editing keypad
    | Insert
    | Delete
    | Home
    | End
    | PageUp
    | PageDown
    // Function keys
    | F1 | F2 | F3 | F4
    | F5 | F6 | F7 | F8
    | F9 | F10 | F11 | F12
    // Whitespace / control
    | Tab
    | Enter
    | Escape
    | Backspace
    /// Printable character. Only used for Ctrl/Alt-modified printable
    /// characters arriving via `PreviewKeyDown`; plain typing routes
    /// through `TextInput` and bypasses this module.
    | Char of char
    /// Future-proofing: any unmapped key. Encoder returns `None`.
    | Unhandled

/// Modifier flags. Mirrors `System.Windows.Input.ModifierKeys` shape
/// but is platform-neutral.
[<System.Flags>]
type KeyModifiers =
    | None    = 0
    | Shift   = 1
    | Alt     = 2
    | Control = 4

module KeyEncoding =

    let private esc = byte 0x1B
    let private bracket = byte '['
    let private O = byte 'O'

    /// xterm SGR-style modifier parameter:
    ///   1 = none, 2 = Shift, 3 = Alt, 4 = Shift+Alt,
    ///   5 = Ctrl, 6 = Ctrl+Shift, 7 = Ctrl+Alt, 8 = Ctrl+Shift+Alt.
    let internal modifierParam (m: KeyModifiers) : int =
        let mutable p = 1
        if m.HasFlag KeyModifiers.Shift then p <- p + 1
        if m.HasFlag KeyModifiers.Alt then p <- p + 2
        if m.HasFlag KeyModifiers.Control then p <- p + 4
        p

    /// Encode a cursor key (Up/Down/Right/Left) honouring DECCKM and
    /// modifiers.
    ///
    ///   * DECCKM normal, no modifier: `\x1b[A` (and B/C/D)
    ///   * DECCKM application, no modifier: `\x1bOA` (SS3 form)
    ///   * Any modifier (DECCKM-independent): `\x1b[1;<mod>A`
    ///     (xterm convention: modified cursor keys do NOT use SS3
    ///     even in application mode)
    let private encodeCursor (final: char) (m: KeyModifiers) (modes: TerminalModes) : byte[] =
        if m <> KeyModifiers.None then
            let p = modifierParam m
            Encoding.ASCII.GetBytes(sprintf "\x1b[1;%d%c" p final)
        elif modes.DECCKM then
            [| esc; O; byte final |]
        else
            [| esc; bracket; byte final |]

    /// Encode an editing-keypad / F5+ key with `\x1b[<n>~` shape.
    /// Modified form is `\x1b[<n>;<mod>~`.
    let private encodeTilde (n: int) (m: KeyModifiers) : byte[] =
        if m = KeyModifiers.None then
            Encoding.ASCII.GetBytes(sprintf "\x1b[%d~" n)
        else
            let p = modifierParam m
            Encoding.ASCII.GetBytes(sprintf "\x1b[%d;%d~" n p)

    /// Encode F1-F4 with `\x1bO<P/Q/R/S>` shape (SS3 form).
    /// Modified form is `\x1b[1;<mod><P/Q/R/S>` (CSI form).
    let private encodeF1to4 (final: char) (m: KeyModifiers) : byte[] =
        if m = KeyModifiers.None then
            [| esc; O; byte final |]
        else
            let p = modifierParam m
            Encoding.ASCII.GetBytes(sprintf "\x1b[1;%d%c" p final)

    /// Encode a printable ASCII character with optional Ctrl/Alt
    /// modifiers. Returns `None` for non-ASCII (those should arrive
    /// via the platform's text-input path, not through KeyEncoding).
    ///
    ///   * Bare `'a'` → `[| 0x61 |]`
    ///   * `Ctrl+'a'` → `[| 0x01 |]`
    ///   * `Alt+'a'` → `[| 0x1b; 0x61 |]` (ESC-prefix; xterm convention)
    ///   * `Ctrl+Alt+'a'` → `[| 0x1b; 0x01 |]`
    ///   * `Ctrl+'@'` → `[| 0x00 |]` (NUL); `Ctrl+'['` → `[| 0x1b |]`;
    ///     `Ctrl+'\\'` → `[| 0x1c |]`; `Ctrl+']'` → `[| 0x1d |]`;
    ///     `Ctrl+'?'` → `[| 0x7f |]` (DEL).
    ///
    /// Shift is implicitly absorbed for Ctrl-letter (`Ctrl+A` and
    /// `Ctrl+Shift+A` both produce `0x01`); for non-letter Ctrl
    /// combos Shift passes through to the underlying byte.
    let private encodeChar (c: char) (m: KeyModifiers) : byte[] option =
        if int c > 0x7F then None
        else
            let ctrl = m.HasFlag KeyModifiers.Control
            let alt = m.HasFlag KeyModifiers.Alt
            let ascii =
                if ctrl && c >= 'a' && c <= 'z' then byte (int c - int 'a' + 1)
                elif ctrl && c >= 'A' && c <= 'Z' then byte (int c - int 'A' + 1)
                elif ctrl && c = '@' then 0uy
                elif ctrl && c = '[' then byte 0x1B
                elif ctrl && c = '\\' then byte 0x1C
                elif ctrl && c = ']' then byte 0x1D
                elif ctrl && c = '^' then byte 0x1E
                elif ctrl && c = '_' then byte 0x1F
                elif ctrl && c = '?' then byte 0x7F
                elif ctrl && c = ' ' then 0uy
                else byte c
            if alt then Some [| esc; ascii |]
            else Some [| ascii |]

    /// Main entry: encode `(key, modifiers, modes)` into bytes for
    /// the PTY. `None` means "don't forward this key to the child".
    let encode (key: KeyCode) (m: KeyModifiers) (modes: TerminalModes) : byte[] option =
        match key with
        | KeyCode.Up    -> Some (encodeCursor 'A' m modes)
        | KeyCode.Down  -> Some (encodeCursor 'B' m modes)
        | KeyCode.Right -> Some (encodeCursor 'C' m modes)
        | KeyCode.Left  -> Some (encodeCursor 'D' m modes)
        | KeyCode.Insert   -> Some (encodeTilde 2 m)
        | KeyCode.Delete   -> Some (encodeTilde 3 m)
        | KeyCode.PageUp   -> Some (encodeTilde 5 m)
        | KeyCode.PageDown -> Some (encodeTilde 6 m)
        | KeyCode.Home ->
            // Modern xterm default: \x1b[H (no tilde). Modified: \x1b[1;<mod>H.
            if m = KeyModifiers.None then Some [| esc; bracket; byte 'H' |]
            else
                let p = modifierParam m
                Some (Encoding.ASCII.GetBytes(sprintf "\x1b[1;%dH" p))
        | KeyCode.End ->
            if m = KeyModifiers.None then Some [| esc; bracket; byte 'F' |]
            else
                let p = modifierParam m
                Some (Encoding.ASCII.GetBytes(sprintf "\x1b[1;%dF" p))
        | KeyCode.F1 -> Some (encodeF1to4 'P' m)
        | KeyCode.F2 -> Some (encodeF1to4 'Q' m)
        | KeyCode.F3 -> Some (encodeF1to4 'R' m)
        | KeyCode.F4 -> Some (encodeF1to4 'S' m)
        | KeyCode.F5  -> Some (encodeTilde 15 m)
        | KeyCode.F6  -> Some (encodeTilde 17 m)
        | KeyCode.F7  -> Some (encodeTilde 18 m)
        | KeyCode.F8  -> Some (encodeTilde 19 m)
        | KeyCode.F9  -> Some (encodeTilde 20 m)
        | KeyCode.F10 -> Some (encodeTilde 21 m)
        | KeyCode.F11 -> Some (encodeTilde 23 m)
        | KeyCode.F12 -> Some (encodeTilde 24 m)
        | KeyCode.Tab ->
            if m.HasFlag KeyModifiers.Shift then
                Some [| esc; bracket; byte 'Z' |]
            elif m.HasFlag KeyModifiers.Alt then
                Some [| esc; byte '\t' |]
            else
                Some [| byte '\t' |]
        | KeyCode.Enter ->
            if m.HasFlag KeyModifiers.Alt then
                Some [| esc; byte '\r' |]
            else
                Some [| byte '\r' |]
        | KeyCode.Escape ->
            if m.HasFlag KeyModifiers.Alt then
                Some [| esc; esc |]
            else
                Some [| esc |]
        | KeyCode.Backspace ->
            // xterm modern default: 0x7f (DEL). bash, zsh, PowerShell,
            // and Claude Code all expect this. Some legacy shells want
            // 0x08 (BS) — those will need a Phase 2 setting.
            if m.HasFlag KeyModifiers.Alt then
                Some [| esc; byte 0x7F |]
            else
                Some [| byte 0x7F |]
        | KeyCode.Char c -> encodeChar c m
        | KeyCode.Unhandled -> None

    /// Wrap clipboard text for paste forwarding.
    ///
    /// **Paste-injection defence (accessibility-first stance, diverges
    /// from xterm).** Strips embedded `\x1b[201~` from the clipboard
    /// content before wrapping. xterm doesn't strip — but for screen-
    /// reader users who can't easily inspect their clipboard before
    /// pasting, an attacker-crafted paste containing `\x1b[201~`
    /// followed by a malicious command would close the bracket-paste
    /// frame early and execute the post-paste portion as if it were
    /// typed. The cost of stripping is essentially zero (no
    /// legitimate shell content contains that exact byte sequence);
    /// the security benefit is meaningful for our threat model.
    /// SECURITY.md tracks this as a deliberate posture divergence.
    ///
    /// Returns the bytes to send to the PTY:
    ///   * When `bracketedPaste` is `true`:
    ///     `\x1b[200~<sanitised>\x1b[201~`
    ///   * When `false`: raw `<sanitised>` bytes (still with
    ///     `\x1b[201~` stripped — defence in depth, no behavioural
    ///     downside).
    let encodePaste (text: string) (bracketedPaste: bool) : byte[] =
        let safeText = text.Replace("\x1b[201~", "")
        let bodyBytes = Encoding.UTF8.GetBytes(safeText)
        if bracketedPaste then
            let prefix = Encoding.ASCII.GetBytes("\x1b[200~")
            let suffix = Encoding.ASCII.GetBytes("\x1b[201~")
            Array.concat [ prefix; bodyBytes; suffix ]
        else
            bodyBytes

    /// Focus-reporting bytes per `?1004` mode. The view's GotKeyboardFocus
    /// / LostKeyboardFocus handlers send these only when
    /// `Modes.FocusReporting` is true.
    let focusGained : byte[] = [| esc; bracket; byte 'I' |]
    let focusLost : byte[] = [| esc; bracket; byte 'O' |]

    /// C#-friendly entry point: returns the bytes directly, or `null`
    /// when the encoder returned `None` (unmapped key). Avoids
    /// `FSharpOption<byte[]>` boilerplate at the C# call site in
    /// `TerminalView.OnPreviewKeyDown`. F#-side callers should
    /// continue to use `encode` and pattern-match the `option`.
    let encodeOrNull (key: KeyCode) (m: KeyModifiers) (modes: TerminalModes) : byte[] | null =
        match encode key m modes with
        | Some bytes -> bytes
        | None -> null
