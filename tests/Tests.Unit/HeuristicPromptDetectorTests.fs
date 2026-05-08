module PtySpeak.Tests.Unit.HeuristicPromptDetectorTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// SessionModel Tier 1.D — heuristic prompt-boundary detector
// ---------------------------------------------------------------------
//
// Per `docs/SESSION-MODEL.md` §245-340 + the maintainer-locked
// scope (PromptStart-only, regex-only Claude, hardcoded
// defaults), the detector observes the screen snapshot
// per-frame, matches rows against per-shell regexes, applies a
// stability window (100ms cmd/PowerShell; 200ms Claude), and
// emits at most one `BoundaryKind.PromptStart` per call.
//
// Tests pin:
//   * Per-shell regex matching (cmd / PowerShell / Claude prompt
//     shapes match; non-prompt text doesn't).
//   * Stability window (first match returns None; same text
//     after the window returns Some).
//   * Duplicate suppression (same prompt across N stable frames
//     emits ONCE; cursor-movement on stable prompt suppresses
//     re-emission; new prompt text re-emits).
//   * Multi-row scenarios (prompt at non-zero index detected;
//     blank-snapshot returns None).
//   * Source provenance (Source.HeuristicPromptRegex stamps
//     correct stability ms per shell).
//   * Reset behaviour (reset clears state; post-reset the next
//     prompt needs a fresh stability window).
//   * Unknown shell handling (returns None without state
//     mutation).
//   * Edge cases (empty snapshot, all-blank rows, large
//     stability windows).

// ---------------------------------------------------------------------
// Fixture builders (mirror CanonicalStateTests.fs:24-43)
// ---------------------------------------------------------------------

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

let private snapshotOf
        (rows: int)
        (cols: int)
        (lines: string list)
        : Cell[][]
        =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

// ---------------------------------------------------------------------
// Time helpers
// ---------------------------------------------------------------------

let private t0 = DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)
let private noCursor = (-1, -1)

// ---------------------------------------------------------------------
// Per-shell regex matching
// ---------------------------------------------------------------------

[<Fact>]
let ``cmd prompt 'C:\Users\admin>' detected after stability window`` () =
    let snap = snapshotOf 3 80 [ "some output"; ""; "C:\\Users\\admin>" ]
    let detector = HeuristicPromptDetector.create ()
    // First frame: starts the stability timer.
    let r1, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    Assert.True(r1.IsNone)
    // After 100ms: emits PromptStart.
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    match r2 with
    | Some data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
        Assert.Equal(BoundarySource.HeuristicPromptRegex 100, data.Source)
        Assert.Equal(after 100, data.DetectedAt)
        Assert.Equal(None, data.CommandId)
        Assert.True(Map.isEmpty data.ExtraParams)
    | None -> Assert.Fail("Expected PromptStart after stability window")

[<Fact>]
let ``cmd 'C:\Users\admin> dir' (extra content past >) doesn't match`` () =
    let snap = snapshotOf 1 80 [ "C:\\Users\\admin> dir" ]
    let detector = HeuristicPromptDetector.create ()
    let r1, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) s1
    Assert.True(r1.IsNone)
    Assert.True(r2.IsNone)

[<Fact>]
let ``cmd 'lowercase>' (no drive letter) doesn't match`` () =
    let snap = snapshotOf 1 80 [ "lowercase>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) detector
    Assert.True(r.IsNone)

[<Fact>]
let ``cmd empty snapshot returns None`` () =
    let snap = snapshotOf 1 80 [ "" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) detector
    Assert.True(r.IsNone)

[<Fact>]
let ``powershell 'PS C:\>' detected after stability window`` () =
    let snap = snapshotOf 1 80 [ "PS C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "powershell" t0 detector
    let r2, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" (after 100) s1
    match r2 with
    | Some data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
        Assert.Equal(BoundarySource.HeuristicPromptRegex 100, data.Source)
    | None -> Assert.Fail("Expected PromptStart for PowerShell")

[<Fact>]
let ``powershell 'PS C:\Projects\foo>' detected`` () =
    let snap = snapshotOf 1 80 [ "PS C:\\Projects\\foo>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "powershell" t0 detector
    let r2, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" (after 100) s1
    Assert.True(r2.IsSome)

[<Fact>]
let ``powershell 'PSReadLine>' (no space after PS) doesn't match`` () =
    let snap = snapshotOf 1 80 [ "PSReadLine>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" (after 200) detector
    Assert.True(r.IsNone)

[<Fact>]
let ``claude '│ > ' detected after 200ms stability window`` () =
    let snap = snapshotOf 1 80 [ "│ > " ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" t0 detector
    // Claude needs 200ms; 100ms isn't enough.
    let r2, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" (after 100) s1
    Assert.True(r2.IsNone)
    let r3, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" (after 200) s2
    match r3 with
    | Some data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
        Assert.Equal(BoundarySource.HeuristicPromptRegex 200, data.Source)
    | None -> Assert.Fail("Expected PromptStart for Claude after 200ms")

[<Fact>]
let ``claude '| > ' (ASCII pipe instead of box-drawing) doesn't match`` () =
    // ASCII pipe '|' (U+007C) != box-drawing '│' (U+2502).
    let snap = snapshotOf 1 80 [ "| > " ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" (after 300) detector
    Assert.True(r.IsNone)

// ---------------------------------------------------------------------
// Stability window
// ---------------------------------------------------------------------

[<Fact>]
let ``first match returns None (window not yet elapsed)`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    Assert.True(r.IsNone)

[<Fact>]
let ``same text after stabilityMs - 1ms returns None`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 99) s1
    Assert.True(r.IsNone)

[<Fact>]
let ``same text at exactly stabilityMs returns Some`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)

[<Fact>]
let ``same text after stabilityMs * 2 returns None (already emitted)`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let _, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) s2
    Assert.True(r.IsNone)

// ---------------------------------------------------------------------
// Duplicate suppression
// ---------------------------------------------------------------------

[<Fact>]
let ``same prompt across many stable frames emits exactly once`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let mutable state = detector
    let mutable emitCount = 0
    for ms in [ 0; 50; 100; 150; 200; 250; 300; 350 ] do
        let r, s =
            HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after ms) state
        state <- s
        if r.IsSome then emitCount <- emitCount + 1
    Assert.Equal(1, emitCount)

[<Fact>]
let ``cursor moving on stable prompt row (same text) suppresses re-emit`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap (0, 5) "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    // Cursor moves; same row content. No new emit.
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap (0, 6) "cmd" (after 200) s2
    Assert.True(r2.IsNone)

[<Fact>]
let ``new prompt text after prior emit re-emits as fresh boundary`` () =
    let snap1 = snapshotOf 1 80 [ "C:\\>" ]
    let snap2 = snapshotOf 1 80 [ "C:\\foo>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    // New prompt text — restart timer.
    let _, s3 =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 200) s2
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 300) s3
    Assert.True(r2.IsSome)

// ---------------------------------------------------------------------
// Multi-row scenarios
// ---------------------------------------------------------------------

[<Fact>]
let ``prompt at row 5 (not last) detected correctly`` () =
    let snap =
        snapshotOf 10 80 [ ""; ""; ""; ""; ""; "C:\\>"; ""; ""; ""; "" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)

[<Fact>]
let ``multiple rows match regex; highest-rowIdx stable one emits`` () =
    // Cycle 22a: among multiple simultaneously-stable matches,
    // the detector picks the highest row index (the newest
    // prompt — output flows downward, so the bottommost match
    // is the active prompt). Earlier matches are scrollback
    // noise.
    let snap = snapshotOf 3 80 [ "C:\\>"; ""; "D:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)
    let boundary = r.Value
    // Highest-rowIdx wins: row 2 ("D:\\>") not row 0 ("C:\\>").
    Assert.Equal(Some 2, boundary.MatchedRowIndex)
    Assert.Equal(Some "D:\\>", boundary.MatchedRowText)

[<Fact>]
let ``two stable rows with IDENTICAL text emits once; subsequent ticks do not flap`` () =
    // Cycle 22a regression guard. The release log 2026-05-08
    // showed the detector alternating between rowIdx=6 and
    // rowIdx=13 every ~50ms when cmd had a prior prompt
    // visible above the current one — both rows passed the
    // regex with identical text, so the (text, rowIdx) gate
    // kept satisfying as the row choice flipped. This test
    // pins the fix: among multiple stable matches with
    // identical text, the detector emits ONCE (for the
    // highest row), then suppresses on subsequent ticks.
    let snap = snapshotOf 14 80
                [ ""; ""; ""; ""; ""; ""
                  "C:\\Users\\admin>"  // row 6 — old prompt
                  ""; ""; ""; ""; ""; ""
                  "C:\\Users\\admin>"  // row 13 — current prompt
                ]
    let detector = HeuristicPromptDetector.create ()
    // Tick 1: rows seen for the first time; no emit.
    let r1, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    Assert.True(r1.IsNone)
    // Tick 2: stability window elapsed; both rows stable.
    // Highest row 13 emits.
    let r2, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r2.IsSome)
    Assert.Equal(Some 13, r2.Value.MatchedRowIndex)
    // Tick 3: same snapshot, both rows still stable. Highest
    // row 13 still equals last-emitted (text, rowIdx) → no
    // emit. The alternation bug would have emitted row 6 here.
    let r3, s3 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 150) s2
    Assert.True(r3.IsNone)
    // Tick 4: still no emit. Pin the no-flap invariant across
    // multiple ticks.
    let r4, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) s3
    Assert.True(r4.IsNone)

[<Fact>]
let ``three stable rows; highest emits`` () =
    let snap = snapshotOf 16 80
                [ ""; ""; ""
                  "C:\\>"  // row 3
                  ""; ""; ""
                  "C:\\>"  // row 7
                  ""; ""; ""; ""; ""; ""; ""
                  "C:\\>"  // row 15
                ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)
    Assert.Equal(Some 15, r.Value.MatchedRowIndex)

[<Fact>]
let ``highest-row prompt scrolls off; lower remaining match emits`` () =
    // Two stable matches at rows 5 and 13; highest (13) emits.
    // Then row 13 scrolls off (becomes blank); only row 5
    // matches now. Row 5's content is identical to what was
    // at row 13, so (text, rowIdx) gate fires (different row).
    let snapBoth = snapshotOf 14 80
                    [ ""; ""; ""; ""; ""
                      "C:\\>"  // row 5
                      ""; ""; ""; ""; ""; ""; ""
                      "C:\\>"  // row 13
                    ]
    let snapOneScrolledOff = snapshotOf 14 80
                              [ ""; ""; ""; ""; ""
                                "C:\\>"  // row 5
                                ""; ""; ""; ""; ""; ""; ""; ""
                              ]
    let detector = HeuristicPromptDetector.create ()
    // Tick 1: first sighting, no emit.
    let _, s1 =
        HeuristicPromptDetector.tryDetect snapBoth noCursor "cmd" t0 detector
    // Tick 2: stable, highest (13) emits.
    let r2, s2 =
        HeuristicPromptDetector.tryDetect snapBoth noCursor "cmd" (after 100) s1
    Assert.True(r2.IsSome)
    Assert.Equal(Some 13, r2.Value.MatchedRowIndex)
    // Tick 3: row 13 scrolls off. Row 5 was already stable
    // (carried over from prior snap). Now row 5 is the only
    // stable match → emit row 5.
    let r3, _ =
        HeuristicPromptDetector.tryDetect snapOneScrolledOff noCursor "cmd" (after 200) s2
    Assert.True(r3.IsSome)
    Assert.Equal(Some 5, r3.Value.MatchedRowIndex)

[<Fact>]
let ``two stable rows with DIFFERENT text emits highest`` () =
    // Variant of the cycle 22a regression test, but text
    // differs across the rows. Confirms highest-rowIdx wins
    // regardless of text similarity.
    let snap = snapshotOf 8 80
                [ ""
                  "C:\\>"  // row 1
                  ""; ""; ""; ""; ""
                  "D:\\Projects>"  // row 7
                ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)
    Assert.Equal(Some 7, r.Value.MatchedRowIndex)
    Assert.Equal(Some "D:\\Projects>", r.Value.MatchedRowText)

[<Fact>]
let ``all-blank snapshot returns None`` () =
    let snap = snapshotOf 5 80 [ ""; ""; ""; ""; "" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) detector
    Assert.True(r.IsNone)

[<Fact>]
let ``zero-row snapshot returns None`` () =
    let snap : Cell[][] = [||]
    let detector = HeuristicPromptDetector.create ()
    let r, s =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    Assert.True(r.IsNone)
    Assert.True(Map.isEmpty s.PerRowMatches)

// ---------------------------------------------------------------------
// Source provenance
// ---------------------------------------------------------------------

[<Fact>]
let ``cmd source stamps HeuristicPromptRegex 100`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(BoundarySource.HeuristicPromptRegex 100, data.Source)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``powershell source stamps HeuristicPromptRegex 100`` () =
    let snap = snapshotOf 1 80 [ "PS C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(BoundarySource.HeuristicPromptRegex 100, data.Source)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``claude source stamps HeuristicPromptRegex 200`` () =
    let snap = snapshotOf 1 80 [ "│ > " ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" (after 200) s1
    match r with
    | Some data ->
        Assert.Equal(BoundarySource.HeuristicPromptRegex 200, data.Source)
    | None -> Assert.Fail("Expected emit")

// ---------------------------------------------------------------------
// Reset behaviour
// ---------------------------------------------------------------------

[<Fact>]
let ``reset clears PerRowMatches`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    Assert.False(Map.isEmpty s1.PerRowMatches)
    let resetState = HeuristicPromptDetector.reset s1
    Assert.True(Map.isEmpty resetState.PerRowMatches)

[<Fact>]
let ``reset clears LastEmittedPromptText`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let _, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(s2.LastEmittedPromptText.IsSome)
    let resetState = HeuristicPromptDetector.reset s2
    Assert.True(resetState.LastEmittedPromptText.IsNone)

[<Fact>]
let ``after reset, the same prompt text re-emits with fresh stability window`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let _, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    // First emit happened; reset.
    let resetState = HeuristicPromptDetector.reset s2
    let _, s3 =
        HeuristicPromptDetector.tryDetect
            snap noCursor "cmd" (after 200) resetState
    // Window restarts: at +200ms (only 0ms after reset) no emit.
    let r, s4 =
        HeuristicPromptDetector.tryDetect
            snap noCursor "cmd" (after 250) s3
    Assert.True(r.IsNone)
    // After fresh 100ms window from re-detection start (after 200): re-emits.
    let r2, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "cmd" (after 300) s4
    Assert.True(r2.IsSome)

// ---------------------------------------------------------------------
// Unknown shell handling (Q2: ON only for cmd / PowerShell / Claude)
// ---------------------------------------------------------------------

[<Fact>]
let ``unknown shell key returns None and leaves state unchanged`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, s =
        HeuristicPromptDetector.tryDetect
            snap noCursor "bash" (after 1000) detector
    Assert.True(r.IsNone)
    Assert.True(Map.isEmpty s.PerRowMatches)

[<Fact>]
let ``empty shell key returns None`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, s =
        HeuristicPromptDetector.tryDetect snap noCursor "" t0 detector
    Assert.True(r.IsNone)
    Assert.True(Map.isEmpty s.PerRowMatches)

[<Fact>]
let ``mixed-case 'CMD' returns None (key is case-sensitive)`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "CMD" (after 200) detector
    Assert.True(r.IsNone)

// ---------------------------------------------------------------------
// State hygiene — stale per-row entry eviction
// ---------------------------------------------------------------------

[<Fact>]
let ``row that stops matching evicts its PerRowMatches entry`` () =
    let promptSnap = snapshotOf 1 80 [ "C:\\>" ]
    let outputSnap = snapshotOf 1 80 [ "no prompt here" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect promptSnap noCursor "cmd" t0 detector
    Assert.False(Map.isEmpty s1.PerRowMatches)
    let _, s2 =
        HeuristicPromptDetector.tryDetect
            outputSnap noCursor "cmd" (after 50) s1
    Assert.True(Map.isEmpty s2.PerRowMatches)

// ---------------------------------------------------------------------
// Tier 1.E — MatchedRowText population (detector captures the
// matching row's text inline so SessionModel can populate
// PromptText without a separate snapshot capture)
// ---------------------------------------------------------------------

[<Fact>]
let ``emitted boundary carries MatchedRowText = Some matching row text (cmd)`` () =
    let snap = snapshotOf 1 80 [ "C:\\Users\\admin>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(Some "C:\\Users\\admin>", data.MatchedRowText)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``emitted boundary's MatchedRowText matches CanonicalState.renderRow output`` () =
    // The detector should render the row identically to
    // CanonicalState.renderRow — that's the canonical
    // sanitised + trimmed contract.
    let snap = snapshotOf 3 80 [ ""; "PS C:\\Projects>"; "" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "powershell" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(Some "PS C:\\Projects>", data.MatchedRowText)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``Claude emit carries MatchedRowText from Ink-box prompt row`` () =
    let snap = snapshotOf 1 80 [ "│ > " ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "claude" (after 200) s1
    match r with
    | Some data ->
        Assert.Equal(Some "│ >", data.MatchedRowText)
    | None -> Assert.Fail("Expected emit")

// ---------------------------------------------------------------------
// Tier 1.E2.A — row-index-aware emission gate
// ---------------------------------------------------------------------
//
// Cycle 20a's headline behaviour change. The detector now
// emits when (text, rowIdx) differs from the last emitted
// pair, not just when text differs. Catches cmd's stable-
// prompt case where text is identical across commands but
// the prompt has moved to a new row after a command cycle.

[<Fact>]
let ``emitted boundary carries MatchedRowIndex = Some (matching row)`` () =
    // Prompt at row 0; verify the emitted boundary stamps
    // MatchedRowIndex = Some 0.
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(Some 0, data.MatchedRowIndex)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``emitted boundary's MatchedRowIndex matches non-zero matching row`` () =
    // Prompt at row 5; verify MatchedRowIndex = Some 5.
    let snap =
        snapshotOf 10 80 [ ""; ""; ""; ""; ""; "C:\\>"; ""; ""; ""; "" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    match r with
    | Some data ->
        Assert.Equal(Some 5, data.MatchedRowIndex)
    | None -> Assert.Fail("Expected emit")

[<Fact>]
let ``same prompt text at SAME row suppresses re-emit (regression guard)`` () =
    // Pre-Cycle-20a behaviour: identical text + identical
    // row → suppress. Verify still suppressed.
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let _, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    // Same snapshot, same row, same text — should NOT re-emit.
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 200) s2
    Assert.True(r.IsNone)

[<Fact>]
let ``same prompt text at DIFFERENT row emits new PromptStart (Cycle 20a headline)`` () =
    // Frame 1: prompt at row 0. Detector emits.
    // Frame 2: same prompt text at row 5 (output filled rows
    // 1-4; new prompt rendered below). Detector should emit
    // a NEW PromptStart even though the text is identical.
    let snap1 = snapshotOf 10 80 [ "C:\\>" ]
    let snap2 =
        snapshotOf 10 80
            [ "C:\\>"; "echo hi"; "hi"; ""; ""; "C:\\>"; ""; ""; ""; "" ]
    let detector = HeuristicPromptDetector.create ()
    // First emit at row 0.
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    Assert.Equal(Some 0, r1.Value.MatchedRowIndex)
    // Second snapshot: prompt at row 5. Detector should
    // see a new (text, rowIdx) pair and emit.
    let _, s3 =
        HeuristicPromptDetector.tryDetect
            snap2 noCursor "cmd" (after 200) s2
    let r2, _ =
        HeuristicPromptDetector.tryDetect
            snap2 noCursor "cmd" (after 300) s3
    Assert.True(r2.IsSome)
    Assert.Equal(Some 5, r2.Value.MatchedRowIndex)

[<Fact>]
let ``different prompt text at SAME row emits (Cycle 20a regression guard)`` () =
    // Pre-Cycle-20a behaviour: text changed → emit. Verify
    // still works under the new (text, rowIdx) gate.
    let snap1 = snapshotOf 1 80 [ "C:\\>" ]
    let snap2 = snapshotOf 1 80 [ "D:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    let _, s3 =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 200) s2
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 300) s3
    Assert.True(r2.IsSome)

[<Fact>]
let ``different text at DIFFERENT row emits (both signals fire)`` () =
    // cd .. + scroll: text AND row change. Sanity check.
    let snap1 = snapshotOf 5 80 [ "C:\\>"; ""; ""; ""; "" ]
    let snap2 =
        snapshotOf 5 80
            [ "C:\\>"; "cd .."; ""; "C:\\Users>"; "" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    Assert.Equal(Some 0, r1.Value.MatchedRowIndex)
    let _, s3 =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 200) s2
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 300) s3
    Assert.True(r2.IsSome)
    Assert.Equal(Some 3, r2.Value.MatchedRowIndex)

[<Fact>]
let ``reset clears LastEmittedPromptRowIndex (post-reset re-emits identical (text, row))`` () =
    let snap = snapshotOf 1 80 [ "C:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let _, s2 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(s2.LastEmittedPromptRowIndex.IsSome)
    // Reset clears the row-index tracker.
    let resetState = HeuristicPromptDetector.reset s2
    Assert.True(resetState.LastEmittedPromptRowIndex.IsNone)
    // Post-reset, the same (text, rowIdx) pair re-emits with
    // a fresh stability window.
    let _, s3 =
        HeuristicPromptDetector.tryDetect
            snap noCursor "cmd" (after 200) resetState
    let r, _ =
        HeuristicPromptDetector.tryDetect
            snap noCursor "cmd" (after 300) s3
    Assert.True(r.IsSome)

[<Fact>]
let ``initial state has LastEmittedPromptRowIndex = None`` () =
    let detector = HeuristicPromptDetector.create ()
    Assert.True(detector.LastEmittedPromptRowIndex.IsNone)

[<Fact>]
let ``stable prompt at row 0 then prompt scrolled to row 1 emits twice`` () =
    // Sanity: prompt scrolled UP (e.g. due to history-pop) is
    // also a (text, rowIdx) change → emit.
    let snap1 = snapshotOf 5 80 [ "C:\\>"; ""; ""; ""; "" ]
    let snap2 = snapshotOf 5 80 [ ""; "C:\\>"; ""; ""; "" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" t0 detector
    let r1, s2 =
        HeuristicPromptDetector.tryDetect snap1 noCursor "cmd" (after 100) s1
    Assert.True(r1.IsSome)
    let _, s3 =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 200) s2
    let r2, _ =
        HeuristicPromptDetector.tryDetect snap2 noCursor "cmd" (after 300) s3
    Assert.True(r2.IsSome)
    Assert.Equal(Some 1, r2.Value.MatchedRowIndex)
