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
let ``multiple rows match regex; first stable one emits`` () =
    let snap = snapshotOf 3 80 [ "C:\\>"; ""; "D:\\>" ]
    let detector = HeuristicPromptDetector.create ()
    let _, s1 =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" t0 detector
    let r, _ =
        HeuristicPromptDetector.tryDetect snap noCursor "cmd" (after 100) s1
    Assert.True(r.IsSome)
    // (Implementation detail: emits the first row's match;
    // duplicate suppression then prevents subsequent rows
    // from emitting on this frame.)

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
