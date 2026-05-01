module PtySpeak.Tests.Unit.ScreenTests

open System.Text
open Xunit
open Terminal.Core
open Terminal.Parser

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

let private feed (screen: Screen) (bytes: byte[]) =
    let parser = Parser.create ()
    let events = Parser.feedArray parser bytes
    for e in events do screen.Apply(e)

let private ascii (s: string) = Encoding.ASCII.GetBytes s

let private rowText (screen: Screen) (row: int) : string =
    let sb = StringBuilder()
    for c in 0 .. screen.Cols - 1 do
        sb.Append(screen.GetCell(row, c).Ch.ToString()) |> ignore
    sb.ToString()

// ---------------------------------------------------------------------
// Construction + invariants
// ---------------------------------------------------------------------

[<Fact>]
let ``fresh screen is all blank cells`` () =
    let screen = Screen(rows = 3, cols = 5)
    for r in 0 .. 2 do
        for c in 0 .. 4 do
            Assert.Equal(' ', screen.GetCell(r, c).Ch.ToString().[0])

[<Fact>]
let ``fresh screen cursor is at 0,0 visible`` () =
    let screen = Screen(rows = 3, cols = 5)
    Assert.Equal(0, screen.Cursor.Row)
    Assert.Equal(0, screen.Cursor.Col)
    // Stage 4.5 PR-A: cursor visibility moved from
    // `Cursor.Visible` (dead-coded field, removed) to
    // `Modes.CursorVisible` (DECTCEM `?25h/l`-driven).
    Assert.True(screen.Modes.CursorVisible)

[<Fact>]
let ``Screen constructor rejects nonpositive dimensions`` () =
    Assert.Throws<System.ArgumentException>(fun () -> Screen(0, 5) |> ignore)
    |> ignore
    Assert.Throws<System.ArgumentException>(fun () -> Screen(5, 0) |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// Print + cursor advance
// ---------------------------------------------------------------------

[<Fact>]
let ``Print writes char at cursor and advances column`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "Hi")
    Assert.Equal('H', screen.GetCell(0, 0).Ch.ToString().[0])
    Assert.Equal('i', screen.GetCell(0, 1).Ch.ToString().[0])
    Assert.Equal(0, screen.Cursor.Row)
    Assert.Equal(2, screen.Cursor.Col)

[<Fact>]
let ``Print past end of row auto-wraps to next row`` () =
    let screen = Screen(rows = 2, cols = 3)
    feed screen (ascii "abcXY")
    Assert.Equal("abc", rowText screen 0)
    Assert.Equal("XY ", rowText screen 1)
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(2, screen.Cursor.Col)

[<Fact>]
let ``Print past end of last row scrolls`` () =
    let screen = Screen(rows = 2, cols = 3)
    feed screen (ascii "abcdefXY")
    // Row 0 should now be "def" (was "abc", scrolled up because
    // "def" wrapped onto row 1, then "XY" wrapped again, scrolling).
    Assert.Equal("def", rowText screen 0)
    Assert.Equal("XY ", rowText screen 1)

// ---------------------------------------------------------------------
// C0 controls
// ---------------------------------------------------------------------

[<Fact>]
let ``LF moves cursor down without changing column`` () =
    let screen = Screen(rows = 3, cols = 5)
    feed screen (ascii "ab\ncd")
    Assert.Equal('a', screen.GetCell(0, 0).Ch.ToString().[0])
    Assert.Equal('b', screen.GetCell(0, 1).Ch.ToString().[0])
    Assert.Equal('c', screen.GetCell(1, 2).Ch.ToString().[0])
    Assert.Equal('d', screen.GetCell(1, 3).Ch.ToString().[0])

[<Fact>]
let ``CR moves cursor to column 0`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "ab\rXY")
    Assert.Equal("XY   ", rowText screen 0)

[<Fact>]
let ``BS decrements column`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "abc\b\bX")
    Assert.Equal("aXc  ", rowText screen 0)

[<Fact>]
let ``HT advances column to next 8-boundary`` () =
    let screen = Screen(rows = 1, cols = 16)
    feed screen [| byte 'a'; 0x09uy; byte 'b' |]
    Assert.Equal('a', screen.GetCell(0, 0).Ch.ToString().[0])
    Assert.Equal('b', screen.GetCell(0, 8).Ch.ToString().[0])

// ---------------------------------------------------------------------
// CSI cursor movement
// ---------------------------------------------------------------------

[<Fact>]
let ``CSI 5;3H moves cursor to row 5 col 3 (1-indexed)`` () =
    let screen = Screen(rows = 10, cols = 10)
    feed screen (ascii "[5;3H")
    Assert.Equal(4, screen.Cursor.Row)
    Assert.Equal(2, screen.Cursor.Col)

[<Fact>]
let ``CSI A B C D move cursor relative`` () =
    let screen = Screen(rows = 10, cols = 10)
    feed screen (ascii "[5;5H")  // home to 5,5 (0-indexed 4,4)
    feed screen (ascii "[2A")    // up 2
    Assert.Equal(2, screen.Cursor.Row)
    feed screen (ascii "[3B")    // down 3
    Assert.Equal(5, screen.Cursor.Row)
    feed screen (ascii "[2C")    // right 2
    Assert.Equal(6, screen.Cursor.Col)
    feed screen (ascii "[1D")    // left 1
    Assert.Equal(5, screen.Cursor.Col)

[<Fact>]
let ``CSI cursor movement clamps at edges`` () =
    let screen = Screen(rows = 3, cols = 3)
    feed screen (ascii "[100A")
    Assert.Equal(0, screen.Cursor.Row)
    feed screen (ascii "[100B")
    Assert.Equal(2, screen.Cursor.Row)
    feed screen (ascii "[100C")
    Assert.Equal(2, screen.Cursor.Col)

// ---------------------------------------------------------------------
// CSI erase
// ---------------------------------------------------------------------

[<Fact>]
let ``CSI 2J clears the whole screen`` () =
    let screen = Screen(rows = 2, cols = 4)
    feed screen (ascii "ab\ncd")
    feed screen (ascii "[2J")
    for r in 0 .. 1 do
        Assert.Equal("    ", rowText screen r)

[<Fact>]
let ``CSI 0K clears from cursor to end of line`` () =
    let screen = Screen(rows = 1, cols = 6)
    feed screen (ascii "abcdef")
    feed screen (ascii "[1;3H")  // back to col 3 (0-indexed 2)
    feed screen (ascii "[0K")
    Assert.Equal("ab    ", rowText screen 0)

// ---------------------------------------------------------------------
// SGR
// ---------------------------------------------------------------------

[<Fact>]
let ``CSI 31m sets foreground to red for following Prints`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "[31mR")
    let cell = screen.GetCell(0, 0)
    Assert.Equal('R', cell.Ch.ToString().[0])
    Assert.Equal(Indexed 1uy, cell.Attrs.Fg)

[<Fact>]
let ``CSI 1m sets bold and CSI 0m resets all`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "[1;31mA[0mB")
    Assert.True(screen.GetCell(0, 0).Attrs.Bold)
    Assert.Equal(Indexed 1uy, screen.GetCell(0, 0).Attrs.Fg)
    Assert.False(screen.GetCell(0, 1).Attrs.Bold)
    Assert.Equal(Default, screen.GetCell(0, 1).Attrs.Fg)

[<Fact>]
let ``CSI 90m sets bright foreground`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "[91mR")  // bright red
    Assert.Equal(Indexed 9uy, screen.GetCell(0, 0).Attrs.Fg)

[<Fact>]
let ``CSI m with no params is equivalent to CSI 0m`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "[1mA[mB")
    Assert.True(screen.GetCell(0, 0).Attrs.Bold)
    Assert.False(screen.GetCell(0, 1).Attrs.Bold)

// ---------------------------------------------------------------------
// Stage 4 substrate: SequenceNumber + SnapshotRows
// ---------------------------------------------------------------------

[<Fact>]
let ``fresh screen has SequenceNumber 0`` () =
    let screen = Screen(rows = 3, cols = 5)
    Assert.Equal(0L, screen.SequenceNumber)

[<Fact>]
let ``Apply increments SequenceNumber once per event`` () =
    let screen = Screen(rows = 3, cols = 5)
    let parser = Parser.create ()
    let events = Parser.feedArray parser (ascii "abc")
    let before = screen.SequenceNumber
    for e in events do screen.Apply(e)
    Assert.Equal(before + int64 events.Length, screen.SequenceNumber)

[<Fact>]
let ``SnapshotRows returns an immutable copy of the requested rows`` () =
    let screen = Screen(rows = 3, cols = 4)
    feed screen (ascii "ab\ncd")
    let _seq, rows = screen.SnapshotRows(0, 2)
    Assert.Equal(2, rows.Length)
    Assert.Equal(4, rows.[0].Length)
    Assert.Equal('a', rows.[0].[0].Ch.ToString().[0])
    Assert.Equal('b', rows.[0].[1].Ch.ToString().[0])

    // Mutating the screen after the snapshot must not affect the
    // returned rows — the snapshot is a deep copy. The
    // ESC (0x1B) byte before the [ is what makes the parser
    // recognize CSI; without it "[1;1H" parses as five Print
    // events and the cursor never returns to (0,0).
    feed screen (ascii "[1;1HZ")
    Assert.Equal('Z', screen.GetCell(0, 0).Ch.ToString().[0])
    Assert.Equal('a', rows.[0].[0].Ch.ToString().[0])

[<Fact>]
let ``SnapshotRows pairs the snapshot with the sequence number at capture time`` () =
    let screen = Screen(rows = 2, cols = 3)
    feed screen (ascii "ab")
    let seq1, _ = screen.SnapshotRows(0, screen.Rows)
    feed screen (ascii "c")
    let seq2, _ = screen.SnapshotRows(0, screen.Rows)
    Assert.True(seq2 > seq1, sprintf "expected %d > %d" seq2 seq1)

[<Fact>]
let ``SnapshotRows rejects out-of-range arguments`` () =
    let screen = Screen(rows = 3, cols = 3)
    Assert.Throws<System.ArgumentException>(fun () ->
        screen.SnapshotRows(-1, 1) |> ignore) |> ignore
    Assert.Throws<System.ArgumentException>(fun () ->
        screen.SnapshotRows(0, -1) |> ignore) |> ignore
    Assert.Throws<System.ArgumentException>(fun () ->
        screen.SnapshotRows(0, 4) |> ignore) |> ignore
    Assert.Throws<System.ArgumentException>(fun () ->
        screen.SnapshotRows(2, 2) |> ignore) |> ignore

[<Fact>]
let ``SnapshotRows with count=0 returns an empty array without affecting the sequence`` () =
    let screen = Screen(rows = 3, cols = 3)
    feed screen (ascii "ab")
    let before = screen.SequenceNumber
    let _seq, rows = screen.SnapshotRows(1, 0)
    Assert.Empty(rows)
    Assert.Equal(before, screen.SequenceNumber)

[<Fact>]
let ``concurrent snapshots and applies never tear`` () =
    // Producer thread feeds bytes through the parser and applies
    // them; the test thread continuously snapshots rows. Snapshots
    // must be well-shaped and SequenceNumber monotonic — the lock
    // around Apply / SnapshotRows is what guarantees both.
    let screen = Screen(rows = 4, cols = 8)
    let parser = Parser.create ()
    let producer = System.Threading.Tasks.Task.Run(fun () ->
        let payload = ascii "Hello World!\n"
        for _ in 1 .. 1000 do
            let events = Parser.feedArray parser payload
            for e in events do screen.Apply(e))
    // At least one snapshot before the producer's exit so the test
    // can't degenerate to zero iterations on a fast machine.
    let firstSeq, firstRows = screen.SnapshotRows(0, screen.Rows)
    Assert.Equal(4, firstRows.Length)
    let mutable lastSeq = firstSeq
    while not producer.IsCompleted do
        let seq, rows = screen.SnapshotRows(0, screen.Rows)
        Assert.True(seq >= lastSeq, sprintf "sequence regressed: %d < %d" seq lastSeq)
        lastSeq <- seq
        Assert.Equal(4, rows.Length)
        for r in rows do Assert.Equal(8, r.Length)
        // Yield between iterations so a slow CI scheduler doesn't
        // starve the producer task; .NET's Monitor will yield on
        // contended Apply/SnapshotRows anyway, but the explicit hint
        // keeps the loop non-pathological if the lock briefly goes
        // uncontested.
        System.Threading.Thread.Yield() |> ignore
    producer.Wait()
    let finalSeq, _ = screen.SnapshotRows(0, screen.Rows)
    Assert.True(finalSeq >= lastSeq)

// ---------------------------------------------------------------------
// Stage 4.5 PR-A — VT mode coverage
// ---------------------------------------------------------------------
//
// Tests for DECTCEM (?25h/l), DECSC/DECRC (ESC 7 / ESC 8),
// 256-colour and truecolor SGR via the new applySgr walker, and
// the OSC 52 defensive drop. These exercise the catch-all-arm
// fills the parser was already emitting events for; before
// Stage 4.5 PR-A, all of these were silently no-op'd at the
// `Screen` layer.

[<Fact>]
let ``DECTCEM ?25l hides cursor; ?25h shows it`` () =
    let screen = Screen(rows = 3, cols = 5)
    Assert.True(screen.Modes.CursorVisible)
    feed screen (ascii "\x1b[?25l")
    Assert.False(screen.Modes.CursorVisible)
    feed screen (ascii "\x1b[?25h")
    Assert.True(screen.Modes.CursorVisible)

[<Fact>]
let ``DECTCEM only fires on private-marker dispatch (?25, not 25)`` () =
    // CSI 25 h without the `?` private marker is a different
    // sequence entirely (in fact undefined / a no-op for us).
    // Confirms `csiPrivateDispatch` and `csiDispatch` stay
    // separate.
    let screen = Screen(rows = 3, cols = 5)
    feed screen (ascii "\x1b[25l")
    Assert.True(screen.Modes.CursorVisible)

[<Fact>]
let ``DECSC then DECRC restores cursor position`` () =
    let screen = Screen(rows = 3, cols = 10)
    // Move to (1, 4), DECSC, move elsewhere, DECRC, expect (1, 4).
    feed screen (ascii "\x1b[2;5H")
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(4, screen.Cursor.Col)
    feed screen (ascii "\x1b7")
    feed screen (ascii "\x1b[1;1H")
    Assert.Equal(0, screen.Cursor.Row)
    feed screen (ascii "\x1b8")
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(4, screen.Cursor.Col)

[<Fact>]
let ``DECSC also saves SGR attrs; DECRC restores them`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "\x1b[1m")  // bold on
    Assert.True(screen.CurrentAttrs.Bold)
    feed screen (ascii "\x1b7")    // DECSC: save bold + position
    feed screen (ascii "\x1b[22m") // bold off
    Assert.False(screen.CurrentAttrs.Bold)
    feed screen (ascii "\x1b8")    // DECRC: restore bold
    Assert.True(screen.CurrentAttrs.Bold)

[<Fact>]
let ``DECRC on empty stack lands at (0,0) with default attrs`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "\x1b[1m\x1b[2;5H")
    Assert.Equal(1, screen.Cursor.Row)
    Assert.True(screen.CurrentAttrs.Bold)
    // No DECSC pushed; DECRC should restore defaults per
    // xterm convention.
    feed screen (ascii "\x1b8")
    Assert.Equal(0, screen.Cursor.Row)
    Assert.Equal(0, screen.Cursor.Col)
    Assert.False(screen.CurrentAttrs.Bold)

[<Fact>]
let ``DECSC pushes onto a stack; multiple DECRC pop in LIFO order`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "\x1b[1;1H\x1b7")  // save (0, 0)
    feed screen (ascii "\x1b[2;5H\x1b7")  // save (1, 4)
    feed screen (ascii "\x1b[3;9H")       // move to (2, 8)
    feed screen (ascii "\x1b8")           // restore (1, 4)
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(4, screen.Cursor.Col)
    feed screen (ascii "\x1b8")           // restore (0, 0)
    Assert.Equal(0, screen.Cursor.Row)
    Assert.Equal(0, screen.Cursor.Col)

[<Fact>]
let ``256-colour SGR sets Fg to Indexed n`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "\x1b[38;5;42m")
    match screen.CurrentAttrs.Fg with
    | Indexed b -> Assert.Equal(42uy, b)
    | other -> Assert.Fail(sprintf "Expected Indexed 42uy, got %A" other)

[<Fact>]
let ``256-colour SGR background works via 48;5;n`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "\x1b[48;5;200m")
    match screen.CurrentAttrs.Bg with
    | Indexed b -> Assert.Equal(200uy, b)
    | other -> Assert.Fail(sprintf "Expected Indexed 200uy, got %A" other)

[<Fact>]
let ``truecolor SGR sets Fg to Rgb (r, g, b)`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "\x1b[38;2;100;200;50m")
    match screen.CurrentAttrs.Fg with
    | Rgb(r, g, b) ->
        Assert.Equal(100uy, r)
        Assert.Equal(200uy, g)
        Assert.Equal(50uy, b)
    | other -> Assert.Fail(sprintf "Expected Rgb(100,200,50), got %A" other)

[<Fact>]
let ``truecolor SGR background works via 48;2;r;g;b`` () =
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "\x1b[48;2;10;20;30m")
    match screen.CurrentAttrs.Bg with
    | Rgb(r, g, b) ->
        Assert.Equal(10uy, r)
        Assert.Equal(20uy, g)
        Assert.Equal(30uy, b)
    | other -> Assert.Fail(sprintf "Expected Rgb(10,20,30), got %A" other)

[<Fact>]
let ``Print after 256-colour SGR carries Indexed Fg into the cell`` () =
    let screen = Screen(rows = 1, cols = 3)
    feed screen (ascii "\x1b[38;5;7mX")
    let cell = screen.GetCell(0, 0)
    match cell.Attrs.Fg with
    | Indexed 7uy -> ()
    | other -> Assert.Fail(sprintf "Expected cell Fg = Indexed 7uy, got %A" other)

[<Fact>]
let ``malformed 38;5 at end of params does not throw`` () =
    // Walker's bounds-guard should degrade to "ignore" rather
    // than read past the array. Pin this contract — hostile
    // input parity with audit-cycle SR-1.
    let screen = Screen(rows = 1, cols = 3)
    let before = screen.CurrentAttrs.Fg
    feed screen (ascii "\x1b[38;5m")
    // Behaviour: walker hits `38` arm but bounds-guard fails,
    // falls through to `applySgrOne 38` which is the catch-all
    // (no-op). Then `5` is `applySgrOne 5` which is also a
    // no-op (no SGR 5 handler — blink is intentionally
    // unsupported). Fg unchanged.
    Assert.Equal(before, screen.CurrentAttrs.Fg)

[<Fact>]
let ``malformed 38;2;100;200 (missing blue) does not throw`` () =
    let screen = Screen(rows = 1, cols = 3)
    let before = screen.CurrentAttrs.Fg
    feed screen (ascii "\x1b[38;2;100;200m")
    Assert.Equal(before, screen.CurrentAttrs.Fg)

[<Fact>]
let ``mixed SGR with truecolor in the middle still applies the rest`` () =
    // [1;38;2;10;20;30;4m → bold on, Fg = Rgb(10,20,30), underline on.
    // Confirms the walker's `walk (i + 5)` advance leaves the
    // following params (4) addressable.
    let screen = Screen(rows = 1, cols = 3)
    feed screen (ascii "\x1b[1;38;2;10;20;30;4m")
    Assert.True(screen.CurrentAttrs.Bold)
    Assert.True(screen.CurrentAttrs.Underline)
    match screen.CurrentAttrs.Fg with
    | Rgb(10uy, 20uy, 30uy) -> ()
    | other -> Assert.Fail(sprintf "Expected Rgb(10,20,30), got %A" other)

[<Fact>]
let ``OSC 52 sequence increments SequenceNumber but does not mutate cells`` () =
    // The Apply arm explicitly drops every OSC dispatch with a
    // SECURITY-CRITICAL comment (see SECURITY.md TC-2). The
    // sequence number bumps because Apply was called; the
    // buffer must be unchanged.
    let screen = Screen(rows = 1, cols = 5)
    feed screen (ascii "Hello")
    let before, snap0 = screen.SnapshotRows(0, 1)
    // Feed an OSC 52 set-clipboard sequence (BEL-terminated):
    //   ESC ] 52 ; c ; <base64> BEL
    feed screen (ascii "\x1b]52;c;ZXZpbA==\x07")
    let after, snap1 = screen.SnapshotRows(0, 1)
    Assert.True(after > before, "SequenceNumber should bump on OSC")
    // Cells unchanged.
    for c in 0 .. snap0.[0].Length - 1 do
        Assert.Equal(snap0.[0].[c], snap1.[0].[c])

// ---------------------------------------------------------------------
// Stage 4.5 PR-B — alt-screen 1049 back-buffer
// ---------------------------------------------------------------------
//
// Tests for DECSET ?1049h/l. Pointer-swap semantics: alt-screen
// is its own buffer; primary content is preserved by reference
// (no copy), so exiting alt-screen surfaces the unchanged
// primary buffer. Cursor + SGR attrs are saved on enter and
// restored on exit (xterm `?1049` is a buffer swap + DECSC
// rolled into one).

[<Fact>]
let ``alt-screen toggle flips Modes.AltScreen flag`` () =
    let screen = Screen(rows = 3, cols = 5)
    Assert.False(screen.Modes.AltScreen)
    feed screen (ascii "\x1b[?1049h")
    Assert.True(screen.Modes.AltScreen)
    feed screen (ascii "\x1b[?1049l")
    Assert.False(screen.Modes.AltScreen)

[<Fact>]
let ``alt-screen entry preserves primary content (pointer-swap, no copy)`` () =
    let screen = Screen(rows = 3, cols = 8)
    feed screen (ascii "primary!")
    Assert.Equal("primary!", rowText screen 0)
    // Enter alt-screen and write something completely
    // different on it.
    feed screen (ascii "\x1b[?1049h")
    feed screen (ascii "ALTSCRN!")
    Assert.Equal("ALTSCRN!", rowText screen 0)
    // Exit alt-screen — primary should be untouched because
    // nothing wrote to it during the alt session.
    feed screen (ascii "\x1b[?1049l")
    Assert.Equal("primary!", rowText screen 0)

[<Fact>]
let ``alt-screen entry clears the alt buffer`` () =
    // Alt buffer starts blank on every enter; if we enter,
    // write, exit, and re-enter, the alt buffer should be
    // blank on the second entry (xterm convention).
    let screen = Screen(rows = 3, cols = 5)
    feed screen (ascii "\x1b[?1049h")
    feed screen (ascii "first")
    Assert.Equal("first", rowText screen 0)
    feed screen (ascii "\x1b[?1049l")
    feed screen (ascii "\x1b[?1049h")
    // Second entry should clear the alt buffer.
    Assert.Equal("     ", rowText screen 0)

[<Fact>]
let ``alt-screen entry resets cursor to (0, 0) with default attrs`` () =
    let screen = Screen(rows = 3, cols = 10)
    // Set up non-trivial primary state: bold attr + cursor at (1, 5).
    feed screen (ascii "\x1b[1m\x1b[2;6H")
    Assert.True(screen.CurrentAttrs.Bold)
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(5, screen.Cursor.Col)
    feed screen (ascii "\x1b[?1049h")
    // On enter: cursor at (0, 0), default attrs.
    Assert.Equal(0, screen.Cursor.Row)
    Assert.Equal(0, screen.Cursor.Col)
    Assert.False(screen.CurrentAttrs.Bold)

[<Fact>]
let ``alt-screen exit restores cursor + SGR attrs from primary`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "\x1b[1m\x1b[2;6H")  // bold + (1, 5)
    feed screen (ascii "\x1b[?1049h")
    // Mess with state inside alt-screen.
    feed screen (ascii "\x1b[3;1H\x1b[3m")  // (2, 0) + italic
    feed screen (ascii "\x1b[?1049l")
    // Exit: cursor and bold attrs restored from primary.
    Assert.Equal(1, screen.Cursor.Row)
    Assert.Equal(5, screen.Cursor.Col)
    Assert.True(screen.CurrentAttrs.Bold)
    Assert.False(screen.CurrentAttrs.Italic)

[<Fact>]
let ``alt-screen entry is idempotent (?1049h while already in alt is a no-op)`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "primary")
    feed screen (ascii "\x1b[?1049h")
    feed screen (ascii "altone")
    Assert.Equal("altone    ", rowText screen 0)
    // Second ?1049h must not clear the alt buffer (the
    // already-in-alt branch short-circuits before the clear
    // loop).
    feed screen (ascii "\x1b[?1049h")
    Assert.Equal("altone    ", rowText screen 0)

[<Fact>]
let ``alt-screen exit is idempotent (?1049l while not in alt is a no-op)`` () =
    let screen = Screen(rows = 3, cols = 10)
    feed screen (ascii "primary")
    Assert.False(screen.Modes.AltScreen)
    feed screen (ascii "\x1b[?1049l")
    Assert.False(screen.Modes.AltScreen)
    // Primary content unchanged.
    Assert.Equal("primary   ", rowText screen 0)

[<Fact>]
let ``SnapshotRows during alt-screen returns alt content; post-exit returns primary`` () =
    let screen = Screen(rows = 1, cols = 8)
    feed screen (ascii "primary!")
    feed screen (ascii "\x1b[?1049h")
    feed screen (ascii "altscrn!")
    let _, snapDuring = screen.SnapshotRows(0, 1)
    let cellChars =
        snapDuring.[0]
        |> Array.map (fun c -> c.Ch.ToString())
        |> String.concat ""
    Assert.Equal("altscrn!", cellChars)
    feed screen (ascii "\x1b[?1049l")
    let _, snapAfter = screen.SnapshotRows(0, 1)
    let cellCharsAfter =
        snapAfter.[0]
        |> Array.map (fun c -> c.Ch.ToString())
        |> String.concat ""
    Assert.Equal("primary!", cellCharsAfter)

[<Fact>]
let ``SequenceNumber bumps on ?1049h and ?1049l`` () =
    // Stage 5's coalescer must treat alt-screen toggles as
    // flush barriers — the row content can change wholesale
    // between primary and alt, so a debounce window
    // straddling a swap would mis-attribute rows. The bump is
    // automatic because Apply unconditionally bumps for every
    // event; this test pins the contract.
    let screen = Screen(rows = 1, cols = 5)
    let seq0 = screen.SequenceNumber
    feed screen (ascii "\x1b[?1049h")
    let seq1 = screen.SequenceNumber
    Assert.True(seq1 > seq0)
    feed screen (ascii "\x1b[?1049l")
    let seq2 = screen.SequenceNumber
    Assert.True(seq2 > seq1)
