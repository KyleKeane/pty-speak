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
    Assert.True(screen.Cursor.Visible)

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
