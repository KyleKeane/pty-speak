module PtySpeak.Tests.Unit.CanonicalStateTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Phase A — CanonicalState substrate behavioural pinning
// ---------------------------------------------------------------------
//
// CanonicalState wraps a Cell[][] snapshot + per-row hashes +
// a `computeDiff: previousRowHashes -> CanonicalDiff` pure
// function. These tests pin:
//   * `create` produces matching RowHashes / ContentHashes via
//     the existing Coalescer.hashRow / hashRowContent helpers.
//   * `computeDiff` returns the empty diff when previous hashes
//     match the current frame.
//   * `computeDiff` returns the full snapshot when previous
//     hashes are empty (first-call semantics).
//   * `computeDiff` returns sorted, unique ChangedRows for
//     partial frame changes.
//   * The rendered ChangedText matches the per-row sanitised +
//     trimmed contract (mirrors `Coalescer.renderRows`).

let private blankCell : Cell = Cell.blank

let private cellOf (ch: char) : Cell =
    { Ch = System.Text.Rune ch; Attrs = SgrAttrs.defaults }

let private blankRow (cols: int) : Cell[] =
    Array.create cols blankCell

let private rowOf (cols: int) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellOf s.[i]
    row

let private snapshotOf (rows: int) (cols: int) (lines: string list) : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

// ---- create ---------------------------------------------------------

[<Fact>]
let ``create populates RowHashes via Coalescer.hashRow`` () =
    let snap = snapshotOf 3 5 [ "hello"; "world"; "" ]
    let canonical = CanonicalState.create snap 0L
    Assert.Equal(3, canonical.RowHashes.Length)
    for i in 0 .. 2 do
        Assert.Equal(Coalescer.hashRow i snap.[i], canonical.RowHashes.[i])

[<Fact>]
let ``create populates ContentHashes via Coalescer.hashRowContent`` () =
    let snap = snapshotOf 2 5 [ "hello"; "world" ]
    let canonical = CanonicalState.create snap 5L
    Assert.Equal(2, canonical.ContentHashes.Length)
    for i in 0 .. 1 do
        Assert.Equal(Coalescer.hashRowContent snap.[i], canonical.ContentHashes.[i])

[<Fact>]
let ``create captures sequence number verbatim`` () =
    let canonical = CanonicalState.create (snapshotOf 1 1 [ "" ]) 42L
    Assert.Equal(42L, canonical.SequenceNumber)

// ---- computeDiff: identical state -----------------------------------

[<Fact>]
let ``computeDiff returns emptyDiff when previousRowHashes match current`` () =
    let snap = snapshotOf 3 5 [ "hello"; "world"; "" ]
    let canonical = CanonicalState.create snap 1L
    let diff = canonical.computeDiff canonical.RowHashes
    Assert.Equal(0, diff.ChangedRows.Length)
    Assert.Equal("", diff.ChangedText)

// ---- computeDiff: first-call semantics ------------------------------

[<Fact>]
let ``computeDiff with empty previousRowHashes returns every row index`` () =
    let snap = snapshotOf 3 5 [ "hello"; "world"; "!" ]
    let canonical = CanonicalState.create snap 1L
    let diff = canonical.computeDiff [||]
    Assert.Equal([| 0; 1; 2 |], diff.ChangedRows)
    Assert.Contains("hello", diff.ChangedText)
    Assert.Contains("world", diff.ChangedText)
    Assert.Contains("!", diff.ChangedText)

[<Fact>]
let ``computeDiff first-call ChangedText is rendered with newline separators`` () =
    let snap = snapshotOf 2 5 [ "row1"; "row2" ]
    let canonical = CanonicalState.create snap 0L
    let diff = canonical.computeDiff [||]
    Assert.Contains("row1", diff.ChangedText)
    Assert.Contains("row2", diff.ChangedText)
    Assert.Contains("\n", diff.ChangedText)

// ---- computeDiff: partial change ------------------------------------

[<Fact>]
let ``computeDiff returns only rows whose hash differs`` () =
    let prev = snapshotOf 3 5 [ "hello"; "world"; "abc" ]
    let cur = snapshotOf 3 5 [ "hello"; "BANG!"; "abc" ]
    let prevCanonical = CanonicalState.create prev 0L
    let curCanonical = CanonicalState.create cur 1L
    let diff = curCanonical.computeDiff prevCanonical.RowHashes
    Assert.Equal([| 1 |], diff.ChangedRows)
    Assert.Contains("BANG!", diff.ChangedText)
    Assert.DoesNotContain("hello", diff.ChangedText)
    Assert.DoesNotContain("abc", diff.ChangedText)

[<Fact>]
let ``computeDiff returns sorted ChangedRows for multi-row changes`` () =
    let prev = snapshotOf 4 5 [ "a"; "b"; "c"; "d" ]
    let cur = snapshotOf 4 5 [ "a"; "B"; "c"; "D" ]
    let prevCanonical = CanonicalState.create prev 0L
    let curCanonical = CanonicalState.create cur 1L
    let diff = curCanonical.computeDiff prevCanonical.RowHashes
    Assert.Equal([| 1; 3 |], diff.ChangedRows)

[<Fact>]
let ``computeDiff ChangedText respects per-row sanitisation`` () =
    // Plant a BEL inside the row content. The substrate's
    // renderChangedRows mirrors Coalescer.renderRows's per-row
    // AnnounceSanitiser.sanitise — BEL must be stripped.
    let cols = 5
    let row = blankRow cols
    row.[0] <- cellOf 'a'
    row.[1] <- { Ch = System.Text.Rune '\x07'; Attrs = SgrAttrs.defaults }
    row.[2] <- cellOf 'b'
    let snap = [| row |]
    let canonical = CanonicalState.create snap 0L
    let diff = canonical.computeDiff [||]
    Assert.False(diff.ChangedText.Contains('\x07'),
        "BEL must be stripped from changed-rows text")
    Assert.Contains("a", diff.ChangedText)
    Assert.Contains("b", diff.ChangedText)

[<Fact>]
let ``computeDiff handles previousRowHashes shorter than current snapshot`` () =
    // If a future stage resizes the screen mid-session, the
    // previous hash array could be shorter. The substrate
    // treats missing indices as "different".
    let snap = snapshotOf 3 5 [ "a"; "b"; "c" ]
    let canonical = CanonicalState.create snap 1L
    // Previous hashes: only 2 entries, both match indices 0+1.
    let truncated = [| canonical.RowHashes.[0]; canonical.RowHashes.[1] |]
    let diff = canonical.computeDiff truncated
    Assert.Equal([| 2 |], diff.ChangedRows)

[<Fact>]
let ``computeDiff trims trailing blank cells in changed-row rendering`` () =
    // Mirrors Coalescer.renderRows's trailing-blank-cell trim
    // contract. A row with text + 5 trailing blanks renders
    // without the trailing space padding.
    let snap = snapshotOf 1 10 [ "ab" ]
    let canonical = CanonicalState.create snap 0L
    let diff = canonical.computeDiff [||]
    Assert.Equal("ab", diff.ChangedText)

[<Fact>]
let ``computeDiff returns ChangedRows in ascending order regardless of frame`` () =
    // A scattered change-set across rows 0, 5, 10 should still
    // come out sorted ascending. The substrate uses a sequential
    // ResizeArray scan so this is incidentally guaranteed; the
    // test pins the contract for downstream pathways that rely
    // on sorted order.
    let prev = snapshotOf 12 5 [ for _ in 0 .. 11 -> "x" ]
    let cur =
        let arr = snapshotOf 12 5 [ for _ in 0 .. 11 -> "x" ]
        arr.[0] <- rowOf 5 "A"
        arr.[5] <- rowOf 5 "B"
        arr.[10] <- rowOf 5 "C"
        arr
    let prevCanonical = CanonicalState.create prev 0L
    let curCanonical = CanonicalState.create cur 1L
    let diff = curCanonical.computeDiff prevCanonical.RowHashes
    Assert.Equal([| 0; 5; 10 |], diff.ChangedRows)

[<Fact>]
let ``emptyDiff is the zero value`` () =
    Assert.Equal(0, CanonicalState.emptyDiff.ChangedRows.Length)
    Assert.Equal("", CanonicalState.emptyDiff.ChangedText)
