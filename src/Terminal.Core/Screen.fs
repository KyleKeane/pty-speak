namespace Terminal.Core

open System.Text

/// In-memory screen buffer. Maintains a `rows × cols` grid of
/// `Cell`s, a cursor, and the current SGR attributes carried into
/// future Print events.
///
/// Stage 3a coverage:
///   * Print writes a cell at the cursor, advances Col, wraps to the
///     next row at end-of-line, scrolls when wrapping past the
///     bottom row.
///   * Execute LF (0x0A) advances Row, scrolling at the bottom.
///   * Execute CR (0x0D) returns Col to 0.
///   * Execute BS (0x08) decrements Col (clamped at 0).
///   * Execute HT (0x09) advances Col to next tab stop (every 8 cols).
///   * CSI A/B/C/D — relative cursor movement.
///   * CSI H / f — absolute cursor move (CUP / HVP).
///   * CSI J — erase display (modes 0/1/2).
///   * CSI K — erase line (modes 0/1/2).
///   * CSI m — SGR for the basic 16 ANSI colours plus 0/1/3/4/7/22/23/24/27.
///
/// Deliberately *not* covered yet (later stages refine):
///   * 256-colour and truecolor SGR (38;5;n, 38;2;r;g;b) — needs a
///     parser that splits sub-parameters by `;` vs `:`.
///   * DECSET modes (cursor visibility ?25, alt-screen ?1049, focus
///     reporting ?1004, etc.) — Stage 4/5 territory.
///   * Scrollback rotation beyond the active grid.
///   * DECSC/DECRC ESC 7/8 cursor save/restore.
type Screen(rows: int, cols: int) =
    do
        if rows <= 0 then invalidArg "rows" "rows must be positive"
        if cols <= 0 then invalidArg "cols" "cols must be positive"

    // Row-major grid. Row 0 is the top of the screen.
    let cells: Cell[,] = Array2D.create rows cols Cell.blank
    let cursor: Cursor = Cursor.create ()
    let mutable currentAttrs: SgrAttrs = SgrAttrs.defaults

    let clamp lo hi v = max lo (min hi v)

    let scrollUp () =
        // Move every row up by one; bottom row becomes blank.
        for r in 0 .. rows - 2 do
            for c in 0 .. cols - 1 do
                cells.[r, c] <- cells.[r + 1, c]
        for c in 0 .. cols - 1 do
            cells.[rows - 1, c] <- Cell.blank

    let advanceRow () =
        if cursor.Row < rows - 1 then
            cursor.Row <- cursor.Row + 1
        else
            scrollUp ()

    let writeAt (r: int) (c: int) (cell: Cell) =
        if r >= 0 && r < rows && c >= 0 && c < cols then
            cells.[r, c] <- cell

    let printRune (rune: Rune) =
        if cursor.Col >= cols then
            // Auto-wrap: advance to next row at column 0 before
            // writing.
            cursor.Col <- 0
            advanceRow ()
        writeAt cursor.Row cursor.Col { Ch = rune; Attrs = currentAttrs }
        cursor.Col <- cursor.Col + 1

    let executeC0 (b: byte) =
        match b with
        | 0x08uy ->
            // BS — backspace
            cursor.Col <- max 0 (cursor.Col - 1)
        | 0x09uy ->
            // HT — horizontal tab to next 8-column boundary
            let next = ((cursor.Col / 8) + 1) * 8
            cursor.Col <- min (cols - 1) next
        | 0x0Auy ->
            // LF — line feed (cursor down, possibly scrolling)
            advanceRow ()
        | 0x0Duy ->
            // CR — carriage return
            cursor.Col <- 0
        | _ -> ()

    let eraseDisplay (mode: int) =
        // Mode 0: cursor → end. Mode 1: start → cursor. Mode 2: all.
        // Mode 3: scrollback (no scrollback yet; treat as 2).
        match mode with
        | 0 ->
            // Clear from cursor to end of current line, then clear
            // following rows.
            for c in cursor.Col .. cols - 1 do
                cells.[cursor.Row, c] <- Cell.blank
            for r in cursor.Row + 1 .. rows - 1 do
                for c in 0 .. cols - 1 do
                    cells.[r, c] <- Cell.blank
        | 1 ->
            for r in 0 .. cursor.Row - 1 do
                for c in 0 .. cols - 1 do
                    cells.[r, c] <- Cell.blank
            for c in 0 .. cursor.Col do
                if c < cols then cells.[cursor.Row, c] <- Cell.blank
        | _ ->
            for r in 0 .. rows - 1 do
                for c in 0 .. cols - 1 do
                    cells.[r, c] <- Cell.blank

    let eraseLine (mode: int) =
        match mode with
        | 0 ->
            for c in cursor.Col .. cols - 1 do
                cells.[cursor.Row, c] <- Cell.blank
        | 1 ->
            for c in 0 .. cursor.Col do
                if c < cols then cells.[cursor.Row, c] <- Cell.blank
        | _ ->
            for c in 0 .. cols - 1 do
                cells.[cursor.Row, c] <- Cell.blank

    /// Apply a single SGR parameter to currentAttrs.
    let applySgrOne (n: int) =
        match n with
        | 0 -> currentAttrs <- SgrAttrs.defaults
        | 1 -> currentAttrs <- { currentAttrs with Bold = true }
        | 22 -> currentAttrs <- { currentAttrs with Bold = false }
        | 3 -> currentAttrs <- { currentAttrs with Italic = true }
        | 23 -> currentAttrs <- { currentAttrs with Italic = false }
        | 4 -> currentAttrs <- { currentAttrs with Underline = true }
        | 24 -> currentAttrs <- { currentAttrs with Underline = false }
        | 7 -> currentAttrs <- { currentAttrs with Inverse = true }
        | 27 -> currentAttrs <- { currentAttrs with Inverse = false }
        // Foreground colours 30..37 (basic) and 90..97 (bright)
        | n when n >= 30 && n <= 37 ->
            currentAttrs <- { currentAttrs with Fg = Indexed(byte (n - 30)) }
        | n when n >= 90 && n <= 97 ->
            currentAttrs <- { currentAttrs with Fg = Indexed(byte (n - 90 + 8)) }
        | 39 -> currentAttrs <- { currentAttrs with Fg = Default }
        // Background colours 40..47 (basic) and 100..107 (bright)
        | n when n >= 40 && n <= 47 ->
            currentAttrs <- { currentAttrs with Bg = Indexed(byte (n - 40)) }
        | n when n >= 100 && n <= 107 ->
            currentAttrs <- { currentAttrs with Bg = Indexed(byte (n - 100 + 8)) }
        | 49 -> currentAttrs <- { currentAttrs with Bg = Default }
        | _ -> ()  // 256-colour / truecolor / unsupported — no-op for now

    let applySgr (parms: int[]) =
        if parms.Length = 0 then
            // CSI m with no params is the same as CSI 0 m.
            applySgrOne 0
        else
            for p in parms do
                applySgrOne p

    let csiDispatch (parms: int[]) (finalByte: char) =
        // For most CSI sequences, missing params default to 1.
        let p0Default1 = if parms.Length = 0 || parms.[0] = 0 then 1 else parms.[0]
        let p0Default0 = if parms.Length = 0 then 0 else parms.[0]
        match finalByte with
        | 'A' ->
            // CUU — cursor up
            cursor.Row <- max 0 (cursor.Row - p0Default1)
        | 'B' ->
            // CUD — cursor down
            cursor.Row <- min (rows - 1) (cursor.Row + p0Default1)
        | 'C' ->
            // CUF — cursor forward
            cursor.Col <- min (cols - 1) (cursor.Col + p0Default1)
        | 'D' ->
            // CUB — cursor back
            cursor.Col <- max 0 (cursor.Col - p0Default1)
        | 'H' | 'f' ->
            // CUP / HVP — set cursor (1-indexed in the protocol).
            let row1 = if parms.Length >= 1 && parms.[0] > 0 then parms.[0] else 1
            let col1 = if parms.Length >= 2 && parms.[1] > 0 then parms.[1] else 1
            cursor.Row <- clamp 0 (rows - 1) (row1 - 1)
            cursor.Col <- clamp 0 (cols - 1) (col1 - 1)
        | 'J' -> eraseDisplay p0Default0
        | 'K' -> eraseLine p0Default0
        | 'm' -> applySgr parms
        | _ -> ()  // Unsupported CSI final — silently ignored

    member _.Rows = rows
    member _.Cols = cols

    /// Cursor position and visibility. Mutating this directly is
    /// fine; tests do it for setup convenience.
    member _.Cursor = cursor

    /// Currently-active SGR attributes (carried into the next Print).
    member _.CurrentAttrs = currentAttrs

    /// Read-only access to a cell. (row, col) are 0-indexed.
    member _.GetCell(row: int, col: int) : Cell = cells.[row, col]

    /// Apply a single VT event to the buffer. Stage 3a covers the
    /// minimum needed to render cmd.exe-style output; unsupported
    /// sequences are silently no-ops so the parser can continue
    /// streaming without exceptions.
    member _.Apply(event: VtEvent) =
        match event with
        | Print rune -> printRune rune
        | Execute b -> executeC0 b
        | CsiDispatch(parms, _, finalByte, _priv) ->
            // Private-marker sequences (DECSET ?h/?l) are ignored
            // in Stage 3a; alt-screen and friends arrive later.
            csiDispatch parms finalByte
        | EscDispatch _
        | OscDispatch _
        | DcsHook _
        | DcsPut _
        | DcsUnhook -> ()  // Not yet handled — Stage 4+
