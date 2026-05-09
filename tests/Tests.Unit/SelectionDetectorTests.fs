module PtySpeak.Tests.Unit.SelectionDetectorTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Stage 8e-A — SelectionDetector substrate tests
// ---------------------------------------------------------------------
//
// Pin the detector's public contract so 8e-B (UIA peer) and 8e-C
// (arrow-key round-trip) can build on top without re-deriving
// what 8e-A guarantees:
//
//   * Per-shell short-circuit (claude-only in 8e-A; 8f wires TOML).
//   * Stable-region signal (only emits after `HighlightDetectionThresholdMs`).
//   * Initial-burst shape (1 SelectionShown + N SelectionItem; correct
//     Producer / Priority / Extensions).
//   * Selection-index update (same items + different highlight ⇒ single
//     SelectionItem; `LastEmittedSignature` gate).
//   * Suppression (identical snapshot twice ⇒ second emits nothing).
//   * Dismissal grace (region gone + `DismissalGraceMs` elapsed ⇒
//     SelectionDismissed; before grace ⇒ silence).
//   * Confidence tier (Signal-1+2 ⇒ HeuristicSGR; +keystroke ⇒
//     HeuristicSGRWithKeystroke; MinConfidence gating).
//   * Region rejection (1-row / 8-row / SGR-uniform regions ignored).
//   * Reset behaviour (state clears; re-establish stability).

// ---------------------------------------------------------------------
// Fixture builders (mirrors HeuristicPromptDetectorTests.fs:38-65)
// ---------------------------------------------------------------------

let private blankCell : Cell = Cell.blank

let private cellOf (ch: char) : Cell =
    { Ch = System.Text.Rune ch; Attrs = SgrAttrs.defaults }

/// Plain cell with a non-default background colour. Drives
/// the "highlighted row" signal via the Bg field.
let private cellWithBg (ch: char) (bg: byte) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Bg = Indexed bg } }

let private blankRow (cols: int) : Cell[] =
    Array.create cols blankCell

let private rowOf (cols: int) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellOf s.[i]
    row

/// Build a row whose non-blank cells all carry the supplied
/// indexed background colour. Used to simulate a highlighted
/// menu item.
let private highlightedRowOf (cols: int) (bg: byte) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellWithBg s.[i] bg
    row

let private snapshotOf
        (rows: int)
        (cols: int)
        (lines: Cell[] list)
        : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- line)
    arr

// ---------------------------------------------------------------------
// Time helpers
// ---------------------------------------------------------------------

let private t0 = DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)
let private noCursor = (-1, -1)

// ---------------------------------------------------------------------
// Common Claude-style 4-item snapshot used by many tests
// ---------------------------------------------------------------------
//
//   row 18: "  Edit              "    (plain)
//   row 19: "  Yes               "    (highlighted, bg=4)
//   row 20: "  Always            "    (plain)
//   row 21: "  No                "    (plain)
//
// Trailing rows are blank.
let private fourItemSnap () : Cell[][] =
    let cols = 30
    let rows = 24
    let arr = Array.init rows (fun _ -> blankRow cols)
    arr.[18] <- rowOf cols "  Edit"
    arr.[19] <- highlightedRowOf cols 4uy "  Yes"
    arr.[20] <- rowOf cols "  Always"
    arr.[21] <- rowOf cols "  No"
    arr

let private fourItemSnapShifted () : Cell[][] =
    // Same items but Always (row 20) is highlighted instead.
    let cols = 30
    let rows = 24
    let arr = Array.init rows (fun _ -> blankRow cols)
    arr.[18] <- rowOf cols "  Edit"
    arr.[19] <- rowOf cols "  Yes"
    arr.[20] <- highlightedRowOf cols 4uy "  Always"
    arr.[21] <- rowOf cols "  No"
    arr

// ---------------------------------------------------------------------
// Activation gate
// ---------------------------------------------------------------------

[<Fact>]
let ``tryDetect returns no events for cmd shell (8e-A claude-only)`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let events, _ =
        SelectionDetector.tryDetect snap noCursor t0 "cmd" detector
    Assert.Empty(events)

[<Fact>]
let ``tryDetect returns no events for powershell shell`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let events, _ =
        SelectionDetector.tryDetect snap noCursor t0 "powershell" detector
    Assert.Empty(events)

[<Fact>]
let ``tryDetect returns no events when stability window not yet elapsed`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let events1, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events2, _ =
        SelectionDetector.tryDetect snap noCursor (after 50) "claude" s1
    // Frame 1 starts the stability timer (no emit). Frame 2 at
    // 50ms is below the 100ms default threshold.
    Assert.Empty(events1)
    Assert.Empty(events2)

[<Fact>]
let ``tryDetect emits initial burst once stability window elapses`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    // 4 items + 1 Shown = 5 events.
    Assert.Equal(5, events.Length)

// ---------------------------------------------------------------------
// Initial-burst shape
// ---------------------------------------------------------------------

[<Fact>]
let ``initial burst contains exactly one SelectionShown`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let shownCount =
        events
        |> Array.filter (fun e -> e.Semantic = SemanticCategory.SelectionShown)
        |> Array.length
    Assert.Equal(1, shownCount)

[<Fact>]
let ``initial burst contains one SelectionItem per row`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let itemCount =
        events
        |> Array.filter (fun e -> e.Semantic = SemanticCategory.SelectionItem)
        |> Array.length
    Assert.Equal(4, itemCount)

[<Fact>]
let ``every emitted event stamps Producer = selection-detector`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    for e in events do
        Assert.Equal("selection-detector", e.Source.Producer)

[<Fact>]
let ``SelectionShown carries Priority Assertive and SelectionItem Polite`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    for e in events do
        match e.Semantic with
        | SemanticCategory.SelectionShown ->
            Assert.Equal(Priority.Assertive, e.Priority)
        | SemanticCategory.SelectionItem ->
            Assert.Equal(Priority.Polite, e.Priority)
        | _ -> ()

[<Fact>]
let ``SelectionShown extensions carry ItemCount, SelectedIndex, AllItems, TopRow, BottomRow, Source`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let shown =
        events
        |> Array.find (fun e -> e.Semantic = SemanticCategory.SelectionShown)
    Assert.Equal(box 4, shown.Extensions.[SelectionExtensions.ItemCount])
    Assert.Equal(box 1, shown.Extensions.[SelectionExtensions.SelectedIndex])
    Assert.Equal(box 18, shown.Extensions.[SelectionExtensions.TopRow])
    Assert.Equal(box 21, shown.Extensions.[SelectionExtensions.BottomRow])
    Assert.True(shown.Extensions.ContainsKey(SelectionExtensions.AllItems))
    Assert.Equal(
        box "HeuristicSGR",
        shown.Extensions.[SelectionExtensions.Source])

// ---------------------------------------------------------------------
// Selection-index update
// ---------------------------------------------------------------------

[<Fact>]
let ``selection-index change emits one SelectionItem update, no fresh Shown`` () =
    let snapA = fourItemSnap ()
    let snapB = fourItemSnapShifted ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snapA noCursor t0 "claude" detector
    let _, s2 =
        SelectionDetector.tryDetect snapA noCursor (after 150) "claude" s1
    // Now highlight moves to row 20.
    let events, _ =
        SelectionDetector.tryDetect snapB noCursor (after 200) "claude" s2
    Assert.Equal(1, events.Length)
    Assert.Equal(SemanticCategory.SelectionItem, events.[0].Semantic)
    Assert.Equal(
        box 2,
        events.[0].Extensions.[SelectionExtensions.SelectedIndex])

// ---------------------------------------------------------------------
// Suppression
// ---------------------------------------------------------------------

[<Fact>]
let ``identical snapshot twice after stable emit suppresses second`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let firstBurst, s2 =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let secondPass, _ =
        SelectionDetector.tryDetect snap noCursor (after 200) "claude" s2
    Assert.NotEmpty(firstBurst)
    Assert.Empty(secondPass)

// ---------------------------------------------------------------------
// Dismissal
// ---------------------------------------------------------------------

[<Fact>]
let ``empty snapshot after grace elapses emits SelectionDismissed`` () =
    let snap = fourItemSnap ()
    let blank = Array.init 24 (fun _ -> blankRow 30)
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let _, s2 =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    // Region disappears at 200ms; grace is 150ms, so dismissal
    // emits at 350ms or later.
    let _, s3 =
        SelectionDetector.tryDetect blank noCursor (after 200) "claude" s2
    let events, _ =
        SelectionDetector.tryDetect blank noCursor (after 400) "claude" s3
    Assert.Equal(1, events.Length)
    Assert.Equal(SemanticCategory.SelectionDismissed, events.[0].Semantic)
    Assert.Equal(Priority.Assertive, events.[0].Priority)

[<Fact>]
let ``empty snapshot before grace does not emit dismissed`` () =
    let snap = fourItemSnap ()
    let blank = Array.init 24 (fun _ -> blankRow 30)
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let _, s2 =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let _, s3 =
        SelectionDetector.tryDetect blank noCursor (after 200) "claude" s2
    let events, _ =
        SelectionDetector.tryDetect blank noCursor (after 250) "claude" s3
    // Only 50ms since region disappeared; below the 150ms grace.
    Assert.Empty(events)

// ---------------------------------------------------------------------
// Confidence tier
// ---------------------------------------------------------------------

[<Fact>]
let ``confidence is HeuristicSGR without keystroke correlation`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let shown =
        events
        |> Array.find (fun e -> e.Semantic = SemanticCategory.SelectionShown)
    Assert.Equal(
        box "HeuristicSGR",
        shown.Extensions.[SelectionExtensions.Source])

[<Fact>]
let ``confidence upgrades to HeuristicSGRWithKeystroke after Down arrow correlates with highlight move`` () =
    let snapA = fourItemSnap ()
    let snapB = fourItemSnapShifted ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snapA noCursor t0 "claude" detector
    let _, s2 =
        SelectionDetector.tryDetect snapA noCursor (after 150) "claude" s1
    // Down arrow lands 50ms before the next paint that moves
    // the highlight.
    let s2' =
        SelectionDetector.feedKeystroke
            SelectionDetector.Direction.Down
            (after 180)
            s2
    let events, _ =
        SelectionDetector.tryDetect snapB noCursor (after 200) "claude" s2'
    Assert.NotEmpty(events)
    let item = events.[0]
    Assert.Equal(
        box "HeuristicSGRWithKeystroke",
        item.Extensions.[SelectionExtensions.Source])

// ---------------------------------------------------------------------
// Region rejection
// ---------------------------------------------------------------------

[<Fact>]
let ``single-row highlighted region does not trigger detection`` () =
    let cols = 30
    let rows = 24
    let arr = Array.init rows (fun _ -> blankRow cols)
    arr.[19] <- highlightedRowOf cols 4uy "  Yes"
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect arr noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect arr noCursor (after 200) "claude" s1
    Assert.Empty(events)

[<Fact>]
let ``8-row span exceeds MaxRegionRows and does not trigger`` () =
    let cols = 30
    let rows = 24
    let arr = Array.init rows (fun _ -> blankRow cols)
    arr.[14] <- rowOf cols "  Item1"
    arr.[15] <- rowOf cols "  Item2"
    arr.[16] <- rowOf cols "  Item3"
    arr.[17] <- highlightedRowOf cols 4uy "  Item4"
    arr.[18] <- rowOf cols "  Item5"
    arr.[19] <- rowOf cols "  Item6"
    arr.[20] <- rowOf cols "  Item7"
    arr.[21] <- rowOf cols "  Item8"
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect arr noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect arr noCursor (after 200) "claude" s1
    Assert.Empty(events)

[<Fact>]
let ``SGR-uniform multi-row block (no highlighted row) does not trigger`` () =
    let cols = 30
    let rows = 24
    let arr = Array.init rows (fun _ -> blankRow cols)
    arr.[18] <- rowOf cols "  Edit"
    arr.[19] <- rowOf cols "  Yes"
    arr.[20] <- rowOf cols "  Always"
    arr.[21] <- rowOf cols "  No"
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect arr noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect arr noCursor (after 200) "claude" s1
    Assert.Empty(events)

// ---------------------------------------------------------------------
// Reset
// ---------------------------------------------------------------------

[<Fact>]
let ``reset clears state, requiring re-establishment of stability`` () =
    let snap = fourItemSnap ()
    let detector = SelectionDetector.create SelectionDetector.defaultParameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let _, s2 =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s1
    let s3 = SelectionDetector.reset s2
    // Immediately after reset, even at the same `after 150`
    // timestamp, no emit (the candidate was cleared).
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 150) "claude" s3
    Assert.Empty(events)

// ---------------------------------------------------------------------
// MinConfidence gating
// ---------------------------------------------------------------------

[<Fact>]
let ``MinConfidence HeuristicSGRWithKeystroke suppresses keystroke-less emission`` () =
    let snap = fourItemSnap ()
    let parameters =
        { SelectionDetector.defaultParameters with
            MinConfidence = SelectionDetector.HeuristicSGRWithKeystroke }
    let detector = SelectionDetector.create parameters
    let _, s1 =
        SelectionDetector.tryDetect snap noCursor t0 "claude" detector
    let events, _ =
        SelectionDetector.tryDetect snap noCursor (after 200) "claude" s1
    // Without a keystroke we never reach the With-Keystroke
    // tier, so MinConfidence gating suppresses emission.
    Assert.Empty(events)
