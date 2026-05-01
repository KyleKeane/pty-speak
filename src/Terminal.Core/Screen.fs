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
/// Stage 4 substrate (no UIA peer yet):
///   * `SequenceNumber` increments on every Apply call. Stage 4's
///     `ITextRangeProvider` reads it together with `SnapshotRows`
///     to detect stale ranges across UIA RPC threads. The counter
///     bumps for non-mutating events too (e.g. `DcsPut`); that's
///     intentional — staleness "the buffer might have changed" is
///     conservative and safe.
///   * `SnapshotRows(start, count)` returns an immutable copy of
///     the requested rows plus the sequence number at the moment
///     of capture, taken under the same gate as Apply so callers
///     never observe a torn buffer.
///
/// Stage 4.5 PR-A coverage (Claude Code rendering substrate):
///   * 256-colour SGR (`\x1b[38;5;n m`, `\x1b[48;5;n m`) — emits
///     `ColorSpec.Indexed n`. Walker in `applySgr` consumes
///     sub-parameters from the `;`-split param array.
///   * Truecolor SGR (`\x1b[38;2;r;g;b m`, `\x1b[48;2;r;g;b m`) —
///     emits `ColorSpec.Rgb (r, g, b)`. Same walker.
///   * DECTCEM (`\x1b[?25h` / `\x1b[?25l`) — toggles
///     `Modes.CursorVisible`. Wired via `csiPrivateDispatch`
///     when the parser passes the `?` private marker.
///   * DECSC / DECRC (`ESC 7` / `ESC 8`) — push and pop
///     `Cursor.SaveStack`. Saves position + SGR attrs.
///   * `TerminalModes` record exposed via `Screen.Modes` for
///     Stage 5/6/7 mode-bit reads.
///
/// Stage 4.5 PR-B coverage (alt-screen):
///   * Alt-screen (`\x1b[?1049h` / `\x1b[?1049l`) — second
///     `Cell[,]` back-buffer; switching toggles `activeBuffer`.
///     Primary buffer is preserved by reference (no copy);
///     cursor + attrs saved on enter and restored on exit.
///
/// Deliberately *not* covered yet (later stages refine):
///   * Colon-separated SGR sub-params (`38:5:n`, `38:2:r:g:b`) —
///     parser today splits on `;` only. Stage 6 territory.
///   * DECCKM (`?1`) cursor-key application mode — `TerminalModes`
///     stub; Stage 6 keyboard layer reads it.
///   * Bracketed paste (`?2004`) — same.
///   * Focus reporting (`?1004`) — same.
///   * OSC 0/2 title narration, OSC 8 hyperlink scheme allowlist —
///     Phase 2 / post-Stage-10 territory respectively.
///   * Scrollback rotation beyond the active grid.
type Screen(rows: int, cols: int) =
    do
        if rows <= 0 then invalidArg "rows" "rows must be positive"
        if cols <= 0 then invalidArg "cols" "cols must be positive"

    // Stage 4.5 PR-B: alt-screen 1049 back-buffer.
    // Two row-major grids (primary + alt). All cell reads /
    // writes go through `activeBuffer`, which is one of the
    // two depending on alt-screen state. Primary content is
    // preserved by *reference* during alt-screen sessions —
    // we never copy primary on enter. On exit we just stop
    // pointing at alt; primary is unchanged because nothing
    // wrote to it during the alt-screen session.
    //
    // `savedPrimary` captures the cursor + SGR state that the
    // primary buffer was in at the moment of `?1049h`, so
    // `?1049l` can restore them. The xterm convention is that
    // `?1049` saves/restores cursor + attrs as part of the
    // buffer swap (it's a DECSC-equivalent baked in).
    let primaryBuffer: Cell[,] = Array2D.create rows cols Cell.blank
    let altBuffer: Cell[,] = Array2D.create rows cols Cell.blank
    let mutable activeBuffer: Cell[,] = primaryBuffer
    let mutable savedPrimary: (int * int * SgrAttrs) option = None
    let cursor: Cursor = Cursor.create ()
    let mutable currentAttrs: SgrAttrs = SgrAttrs.defaults

    // Stage 4.5 PR-A: mode bits (cursor visibility, alt-screen,
    // DECCKM, bracketed paste, focus reporting). Centralised
    // here so Stage 5/6/7 don't smear them across files. PR-B
    // wires `AltScreen`; Stage 6 wires the keyboard-side bits.
    let mutable modes: TerminalModes = TerminalModes.defaults

    // Mutation gate. Apply takes this lock; SnapshotRows takes it to
    // capture (sequence, rows) atomically. Stage 3b feeds Apply on the
    // WPF dispatcher; Stage 4's UIA peer reads snapshots from the UIA
    // RPC thread, so the lock is the boundary between them.
    let gate = obj ()
    let mutable sequenceNumber: int64 = 0L

    let clamp lo hi v = max lo (min hi v)

    let scrollUp () =
        // Move every row up by one; bottom row becomes blank.
        for r in 0 .. rows - 2 do
            for c in 0 .. cols - 1 do
                activeBuffer.[r, c] <- activeBuffer.[r + 1, c]
        for c in 0 .. cols - 1 do
            activeBuffer.[rows - 1, c] <- Cell.blank

    let advanceRow () =
        if cursor.Row < rows - 1 then
            cursor.Row <- cursor.Row + 1
        else
            scrollUp ()

    let writeAt (r: int) (c: int) (cell: Cell) =
        if r >= 0 && r < rows && c >= 0 && c < cols then
            activeBuffer.[r, c] <- cell

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
                activeBuffer.[cursor.Row, c] <- Cell.blank
            for r in cursor.Row + 1 .. rows - 1 do
                for c in 0 .. cols - 1 do
                    activeBuffer.[r, c] <- Cell.blank
        | 1 ->
            for r in 0 .. cursor.Row - 1 do
                for c in 0 .. cols - 1 do
                    activeBuffer.[r, c] <- Cell.blank
            for c in 0 .. cursor.Col do
                if c < cols then activeBuffer.[cursor.Row, c] <- Cell.blank
        | _ ->
            for r in 0 .. rows - 1 do
                for c in 0 .. cols - 1 do
                    activeBuffer.[r, c] <- Cell.blank

    let eraseLine (mode: int) =
        match mode with
        | 0 ->
            for c in cursor.Col .. cols - 1 do
                activeBuffer.[cursor.Row, c] <- Cell.blank
        | 1 ->
            for c in 0 .. cursor.Col do
                if c < cols then activeBuffer.[cursor.Row, c] <- Cell.blank
        | _ ->
            for c in 0 .. cols - 1 do
                activeBuffer.[cursor.Row, c] <- Cell.blank

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

    /// Walk the SGR parameter array consuming sub-parameter
    /// sequences for 256-colour (`38;5;n`, `48;5;n`) and
    /// truecolor (`38;2;r;g;b`, `48;2;r;g;b`); other params
    /// fall through to `applySgrOne`. Tail-recursive so the F#
    /// compiler turns this into a `while` loop in IL.
    ///
    /// Bounds-guards are inline on each match arm so a malformed
    /// trailer (e.g. `38;5` at end-of-array) degrades to "ignore"
    /// rather than throw. This matches the hostile-input
    /// posture audit-cycle SR-1 set elsewhere.
    ///
    /// TODO Stage 6: colon-separated sub-params (`38:5:n`,
    /// `38:2:r:g:b`) require parser-side support — the
    /// `StateMachine` currently splits parameters on `;` only.
    /// When the parser learns `:`, this walker stays the same;
    /// the parser presents the sub-params as additional
    /// elements of `parms`.
    let applySgr (parms: int[]) =
        if parms.Length = 0 then
            // CSI m with no params is the same as CSI 0 m.
            applySgrOne 0
        else
            let rec walk i =
                if i >= parms.Length then () else
                match parms.[i] with
                | 38 when i + 4 < parms.Length && parms.[i + 1] = 2 ->
                    currentAttrs <-
                        { currentAttrs with
                            Fg = Rgb(byte parms.[i + 2], byte parms.[i + 3], byte parms.[i + 4]) }
                    walk (i + 5)
                | 38 when i + 2 < parms.Length && parms.[i + 1] = 5 ->
                    currentAttrs <-
                        { currentAttrs with Fg = Indexed(byte parms.[i + 2]) }
                    walk (i + 3)
                | 48 when i + 4 < parms.Length && parms.[i + 1] = 2 ->
                    currentAttrs <-
                        { currentAttrs with
                            Bg = Rgb(byte parms.[i + 2], byte parms.[i + 3], byte parms.[i + 4]) }
                    walk (i + 5)
                | 48 when i + 2 < parms.Length && parms.[i + 1] = 5 ->
                    currentAttrs <-
                        { currentAttrs with Bg = Indexed(byte parms.[i + 2]) }
                    walk (i + 3)
                | n ->
                    applySgrOne n
                    walk (i + 1)
            walk 0

    /// Switch the active buffer from primary to alt and reset
    /// alt-screen state (cleared buffer, cursor at origin,
    /// default attrs). Called when DECSET `?1049h` arrives.
    /// Idempotent: if alt-screen is already active, this is a
    /// no-op.
    ///
    /// Save semantics match xterm's `?1049`: the cursor
    /// position and SGR attrs of the primary buffer at the
    /// moment of swap are captured into `savedPrimary` so
    /// `?1049l` can restore them. The primary buffer's *cells*
    /// are not copied — they stay in `primaryBuffer` because
    /// nothing writes to it during the alt session.
    let enterAltScreen () =
        if not modes.AltScreen then
            savedPrimary <- Some (cursor.Row, cursor.Col, currentAttrs)
            // Clear alt buffer (xterm convention: alt-screen
            // starts blank).
            for r in 0 .. rows - 1 do
                for c in 0 .. cols - 1 do
                    altBuffer.[r, c] <- Cell.blank
            activeBuffer <- altBuffer
            cursor.Row <- 0
            cursor.Col <- 0
            currentAttrs <- SgrAttrs.defaults
            modes.AltScreen <- true

    /// Switch the active buffer back to primary and restore
    /// the cursor + SGR attrs that were saved on enter. Called
    /// when DECSET `?1049l` arrives. Idempotent: if alt-screen
    /// is not active, this is a no-op.
    ///
    /// Defensive: if `savedPrimary` is somehow `None` while
    /// `modes.AltScreen` is `true` (shouldn't happen — they
    /// flip atomically — but guard anyway), fall back to
    /// (0, 0) with default attrs rather than crash.
    let exitAltScreen () =
        if modes.AltScreen then
            match savedPrimary with
            | Some (savedRow, savedCol, savedAttrs) ->
                cursor.Row <- savedRow
                cursor.Col <- savedCol
                currentAttrs <- savedAttrs
            | None ->
                cursor.Row <- 0
                cursor.Col <- 0
                currentAttrs <- SgrAttrs.defaults
            savedPrimary <- None
            activeBuffer <- primaryBuffer
            modes.AltScreen <- false

    /// CSI private-marker dispatch: handles sequences emitted
    /// when the parser sees a `?` private byte (DECSET / DECRESET
    /// for terminal modes). Stage 4.5 PR-A wires DECTCEM (`?25h/l`,
    /// cursor visibility); PR-B wires alt-screen (`?1049h/l`).
    /// Stage 6 will add DECCKM (`?1`), bracketed paste (`?2004`),
    /// and focus reporting (`?1004`).
    ///
    /// Unknown private modes are silently dropped — they're
    /// non-malicious extensions whose UIA implications need a
    /// stage to land.
    let csiPrivateDispatch (parms: int[]) (finalByte: char) =
        if parms.Length = 0 then () else
        let n = parms.[0]
        match finalByte, n with
        | 'h', 25 -> modes.CursorVisible <- true
        | 'l', 25 -> modes.CursorVisible <- false
        | 'h', 1049 -> enterAltScreen ()
        | 'l', 1049 -> exitAltScreen ()
        // Stage 6 will add: ?1 (DECCKM), ?2004 (bracketed paste),
        //                   ?1004 (focus reporting)
        | _ -> ()

    /// ESC dispatch (bare `ESC <intermediates> <final>`): handles
    /// DECSC (`ESC 7`) and DECRC (`ESC 8`) for cursor save/restore.
    /// Other ESC sequences (DECKPAM `ESC =`, DECKPNM `ESC >`, etc.)
    /// are silently dropped — Stage 6 territory.
    let escDispatch (intermediates: byte[]) (finalByte: char) =
        if intermediates.Length = 0 then
            match finalByte with
            | '7' ->
                // DECSC — push current cursor + attrs onto the stack.
                cursor.SaveStack <-
                    { Row = cursor.Row
                      Col = cursor.Col
                      Attrs = currentAttrs }
                    :: cursor.SaveStack
            | '8' ->
                // DECRC — pop and restore. Empty-stack behaviour:
                // xterm restores to (0, 0) with default attrs;
                // alacritty matches; we do the same.
                match cursor.SaveStack with
                | [] ->
                    cursor.Row <- 0
                    cursor.Col <- 0
                    currentAttrs <- SgrAttrs.defaults
                | top :: rest ->
                    cursor.Row <- top.Row
                    cursor.Col <- top.Col
                    currentAttrs <- top.Attrs
                    cursor.SaveStack <- rest
            | _ -> ()

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
        | _ ->
            // Audit-cycle SR-1: this catch-all is intentional and
            // SECURITY-CRITICAL. It silently drops final bytes
            // for the response-generating sequences listed in
            // SECURITY.md row TC-1 (DSR `n`, DA1/2/3 `c`, CPR
            // built on DSR, DECRQM `$p`, DECRQSS `$q`, title
            // report `21 t`, font-size report). Re-enabling any
            // of these (writing back through the PTY, building
            // a response packet, etc.) re-opens the CVE class
            // covering CVE-2003-0063, CVE-2022-45872,
            // CVE-2024-50349/52005. Reviewers MUST block any PR
            // that adds a handler in this match without a
            // matching SECURITY.md update describing the new
            // mitigation strategy.
            ()

    member _.Rows = rows
    member _.Cols = cols

    /// Cursor position and save stack. Mutating this directly is
    /// fine; tests do it for setup convenience. Cursor visibility
    /// (DECTCEM) lives on `Modes`, not here.
    member _.Cursor = cursor

    /// Currently-active SGR attributes (carried into the next Print).
    member _.CurrentAttrs = currentAttrs

    /// Stage 4.5 PR-A: terminal mode bits (cursor visibility,
    /// alt-screen, DECCKM, bracketed paste, focus reporting).
    /// Stage 5/6/7 read this when they need a mode-bit decision;
    /// today only `CursorVisible` is wired (DECTCEM `?25h/l`).
    member _.Modes = modes

    /// Read-only access to a cell. (row, col) are 0-indexed.
    member _.GetCell(row: int, col: int) : Cell = activeBuffer.[row, col]

    /// Monotonic counter incremented on every Apply. Stage 4 ranges
    /// store this at construction; if it has changed when the range
    /// is later queried, the range is known to be stale. Reads under
    /// the same lock as Apply / SnapshotRows so that 64-bit loads
    /// can't tear on architectures without atomic int64 reads.
    member _.SequenceNumber : int64 = lock gate (fun () -> sequenceNumber)

    /// Atomically capture an immutable copy of `count` rows starting
    /// at `startRow`, paired with the sequence number at capture
    /// time. Each returned row is a fresh `Cell[]` of length `Cols`;
    /// callers may retain it indefinitely.
    member _.SnapshotRows(startRow: int, count: int) : int64 * Cell[][] =
        if startRow < 0 || startRow >= rows then
            invalidArg "startRow" (sprintf "startRow must be in [0, %d); got %d" rows startRow)
        if count < 0 then
            invalidArg "count" (sprintf "count must be non-negative; got %d" count)
        if startRow + count > rows then
            invalidArg "count" (sprintf "startRow + count must be <= %d; got %d + %d" rows startRow count)
        lock gate (fun () ->
            let snapshot = Array.init count (fun i ->
                let r = startRow + i
                Array.init cols (fun c -> activeBuffer.[r, c]))
            sequenceNumber, snapshot)

    /// Apply a single VT event to the buffer. Stage 3a covers the
    /// minimum needed to render cmd.exe-style output; unsupported
    /// sequences are silently no-ops so the parser can continue
    /// streaming without exceptions.
    member _.Apply(event: VtEvent) =
        lock gate (fun () ->
            sequenceNumber <- sequenceNumber + 1L
            match event with
            | Print rune -> printRune rune
            | Execute b -> executeC0 b
            | CsiDispatch(parms, _, finalByte, priv) ->
                // Stage 4.5 PR-A: route private-marker CSI to a
                // dedicated dispatch so DECTCEM / alt-screen /
                // DECCKM stay separate from the public-marker
                // CSI vocabulary in `csiDispatch`.
                match priv with
                | Some '?' -> csiPrivateDispatch parms finalByte
                | _ -> csiDispatch parms finalByte
            | EscDispatch(intermediates, finalByte) ->
                // Stage 4.5 PR-A: DECSC (`ESC 7`) and DECRC
                // (`ESC 8`) cursor save/restore; other ESC
                // sequences are silently dropped.
                escDispatch intermediates finalByte
            | OscDispatch(parms, _) ->
                // SECURITY-CRITICAL: silently dropping all OSC
                // dispatches today. The OSC 52 case
                // (parms.[0] = "52"B) is a known hostile-input
                // vector — a child writing
                //     `\x1b]52;c;<base64>\x07`
                // could write attacker-controlled bytes into the
                // user's clipboard if we forwarded it to the OS
                // clipboard API. See SECURITY.md row TC-2 and
                // audit-cycle SR-1 (PR #76, the parser-side
                // hardening that caps OSC payload growth).
                //
                // Other OSC dispatches (0/2 title, 8 hyperlinks)
                // are non-malicious but their UIA exposure is
                // deferred:
                //   - OSC 0/2: Phase 2 verbosity-profile
                //     decision (does the user want title
                //     narration?).
                //   - OSC 8: requires `ITextProvider2` migration,
                //     which Stage 4 explicitly deferred and which
                //     the strategic review §B sequenced as a
                //     post-Stage-10 PR.
                //
                // Reviewers: re-enabling any OSC dispatch here
                // MUST come with a SECURITY.md update describing
                // the new mitigation strategy plus a
                // security-test row. Do NOT collapse the explicit
                // arm back into a generic catch-all — the comment
                // is grep-bait for future audits.
                ignore parms
            | DcsHook _
            | DcsPut _
            | DcsUnhook -> ())  // Not yet handled — Stage 4+
