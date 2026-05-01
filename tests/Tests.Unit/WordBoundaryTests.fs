module PtySpeak.Tests.Unit.WordBoundaryTests

open System.Text
open Xunit
open Terminal.Core
open Terminal.Accessibility

/// Tests for `TerminalTextRange`'s word-boundary helpers
/// (`IsWordSeparator`, `WordEndFrom`, `NextWordStart`,
/// `PrevWordStart`). The audit-cycle's PR-D made these
/// `static member internal` (were `static member private`)
/// so Tests.Unit can reach them via the
/// `InternalsVisibleTo("PtySpeak.Tests.Unit")` declaration
/// PR-C added to Terminal.Accessibility.
///
/// These tests also serve as the validation that the
/// InternalsVisibleTo wiring from PR-C is working
/// end-to-end — if the test project can't resolve the
/// internal helpers, it'd be a build failure, not a runtime
/// fail, so the CI build step is the gate.

let private mkCell (c: char) : Cell =
    { Ch = Rune(int c); Attrs = SgrAttrs.defaults }

/// Build a single-row Cell[][] padded with blanks to
/// `cols` width. Convenient for testing word-boundary
/// behaviour on representative terminal-output strings.
let private mkRows (s: string) (cols: int) : Cell[][] =
    let row = Array.init cols (fun i ->
        if i < s.Length then mkCell s.[i] else Cell.blank)
    [| row |]

// ---------- IsWordSeparator ----------

[<Fact>]
let ``IsWordSeparator true for space`` () =
    Assert.True(TerminalTextRange.IsWordSeparator(mkCell ' '))

[<Fact>]
let ``IsWordSeparator true for tab`` () =
    Assert.True(TerminalTextRange.IsWordSeparator(mkCell '\t'))

[<Fact>]
let ``IsWordSeparator false for letter`` () =
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell 'a'))

[<Fact>]
let ``IsWordSeparator false for digit`` () =
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '7'))

[<Fact>]
let ``IsWordSeparator false for punctuation (path-as-word policy)`` () =
    // Whitespace-only word boundaries: punctuation stays
    // INSIDE the word so paths like `C:\Users\test>` read
    // as one word for fast NVDA navigation. If this changes,
    // docs/USER-SETTINGS.md "Word boundaries" needs to flip
    // too.
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '.'))
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '\\'))
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell ':'))
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '-'))
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '_'))
    Assert.False(TerminalTextRange.IsWordSeparator(mkCell '/'))

// ---------- NextWordStart ----------

[<Fact>]
let ``NextWordStart from word interior advances to next word`` () =
    let rows = mkRows "hello world test" 30
    // Starting at column 2 ("ll" of hello"), should advance
    // through the rest of "hello", skip the space, land on
    // "w" of "world" at column 6.
    let (r, c) = TerminalTextRange.NextWordStart rows 30 0 2
    Assert.Equal(0, r)
    Assert.Equal(6, c)

[<Fact>]
let ``NextWordStart from word start advances past current word`` () =
    let rows = mkRows "hello world" 20
    // Starting at column 0 ("h" of hello), advances to
    // "w" of "world" at column 6.
    let (r, c) = TerminalTextRange.NextWordStart rows 20 0 0
    Assert.Equal(0, r)
    Assert.Equal(6, c)

[<Fact>]
let ``NextWordStart from separator skips run and finds next word`` () =
    let rows = mkRows "a   bcd" 20
    // Starting at column 1 (the first space), should skip
    // to "b" at column 4.
    let (r, c) = TerminalTextRange.NextWordStart rows 20 0 1
    Assert.Equal(0, r)
    Assert.Equal(4, c)

[<Fact>]
let ``NextWordStart returns past-end when no more words`` () =
    let rows = mkRows "a" 20
    let (r, _) = TerminalTextRange.NextWordStart rows 20 0 0
    // Single-row buffer, single word. Past the end means
    // r >= rows.Length. With cols=20 the row is "a" + 19
    // spaces; from inside "a" we walk through "a" then
    // through the space run, then run off the end of the
    // single row (r=1, which is past rows.Length=1).
    Assert.True(r >= rows.Length, sprintf "Expected r >= %d (past document end); got r=%d" rows.Length r)

// ---------- WordEndFrom ----------

[<Fact>]
let ``WordEndFrom on a word position returns one-past-last`` () =
    let rows = mkRows "hello world" 20
    // Starting at column 0 (start of "hello"), end is at
    // column 5 (the space). One-past-the-last-cell-of-the-word.
    let (r, c) = TerminalTextRange.WordEndFrom rows 20 0 0
    Assert.Equal(0, r)
    Assert.Equal(5, c)

[<Fact>]
let ``WordEndFrom on a separator returns same position (zero-width)`` () =
    let rows = mkRows "a b" 20
    // Starting at the space (col 1) returns the same
    // position immediately because we're on a separator.
    let (r, c) = TerminalTextRange.WordEndFrom rows 20 0 1
    Assert.Equal(0, r)
    Assert.Equal(1, c)

[<Fact>]
let ``WordEndFrom on a path-as-word treats punctuation as inside`` () =
    let path = "C:\\Users\\test>"
    let rows = mkRows path 30
    // The whole path reads as one word — the first
    // separator is at column len(path) (the trailing
    // padding-blank).
    let (r, c) = TerminalTextRange.WordEndFrom rows 30 0 0
    Assert.Equal(0, r)
    Assert.Equal(path.Length, c)

// ---------- PrevWordStart ----------

[<Fact>]
let ``PrevWordStart from second word returns first word start`` () =
    let rows = mkRows "hello world" 20
    // Starting at column 6 ("w" of world), going back
    // should land on column 0 ("h" of hello).
    let (r, c) = TerminalTextRange.PrevWordStart rows 20 0 6
    Assert.Equal(0, r)
    Assert.Equal(0, c)

[<Fact>]
let ``PrevWordStart from origin returns origin`` () =
    let rows = mkRows "hello" 20
    // (0, 0) is the document origin; can't go back further.
    let (r, c) = TerminalTextRange.PrevWordStart rows 20 0 0
    Assert.Equal(0, r)
    Assert.Equal(0, c)

[<Fact>]
let ``PrevWordStart from inside a word returns that word's start`` () =
    let rows = mkRows "  hello world" 30
    // Starting at column 5 (inside "hello", which begins
    // at column 2 after the two leading spaces), going
    // back lands on column 2.
    let (r, c) = TerminalTextRange.PrevWordStart rows 30 0 5
    Assert.Equal(0, r)
    Assert.Equal(2, c)

// ---------- Jagged-snapshot defence (audit-cycle SR-2) ----------
//
// `Screen.SnapshotRows` returns uniform rows today, but
// `TerminalTextRange`'s constructor doesn't enforce uniformity.
// A jagged `Cell[][]` (legitimately produced by a future
// refactor, or constructed adversarially) must not crash the
// helpers with `IndexOutOfRangeException`. SR-2 added
// `c >= rows.[r].Length` guards inside `WordEndFrom`,
// `NextWordStart`, and `PrevWordStart`. These tests pin the
// contract.

let private mkJagged () : Cell[][] =
    // Row 0: "ab" (length 2), Row 1: "" (length 0), Row 2: "c"
    // (length 1). `cols` is 4 — strictly larger than every row.
    // The helpers must not index past the actual row length even
    // though `c < cols` would suggest the cell exists.
    [|
        [| mkCell 'a'; mkCell 'b' |]
        Array.empty<Cell>
        [| mkCell 'c' |]
    |]

[<Fact>]
let ``WordEndFrom does not throw on a jagged snapshot`` () =
    let rows = mkJagged ()
    // From (0, 0) — well-formed start. Walking forward must
    // eventually halt without indexing past the actual lengths
    // of rows 1 and 2.
    let (r, c) = TerminalTextRange.WordEndFrom rows 4 0 0
    Assert.True(r >= 0)
    Assert.True(c >= 0)

[<Fact>]
let ``NextWordStart does not throw on a jagged snapshot`` () =
    let rows = mkJagged ()
    // From (0, 0) — inside "ab" on row 0. Advance must walk
    // past the empty row 1 and onto row 2 ("c") without
    // hitting `IndexOutOfRangeException`.
    let (r, c) = TerminalTextRange.NextWordStart rows 4 0 0
    Assert.True(r >= 0)
    Assert.True(c >= 0)

[<Fact>]
let ``PrevWordStart does not throw on a jagged snapshot`` () =
    let rows = mkJagged ()
    // From (2, 0) — start of "c" on row 2. Walking backward
    // must skip the empty row 1 and land in row 0 without
    // indexing past row 1's length (which is 0).
    let (r, c) = TerminalTextRange.PrevWordStart rows 4 2 0
    Assert.True(r >= 0)
    Assert.True(c >= 0)
