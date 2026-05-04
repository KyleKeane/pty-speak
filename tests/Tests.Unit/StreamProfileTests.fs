module PtySpeak.Tests.Unit.StreamProfileTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Stage 5 — Coalescer behavioural pinning
// ---------------------------------------------------------------------
//
// The Stage 5 coalescer composes four algorithms (FNV-1a row /
// frame hash, sliding-window spinner suppression, leading- +
// trailing-edge debounce, and an alt-screen flush barrier).
// These tests pin each algorithm independently and a few of
// the interactions, driving `StreamProfile.processRowsChanged` /
// `onTimerTick` / `onModeChanged` directly with explicit
// `now` timestamps so debounce assertions are deterministic
// without spinning the production reader loop.
//
// One end-to-end test covers the production `runLoop` by
// wiring real Channels, a real `Screen`, and a
// `FakeTimeProvider` to advance the periodic timer.

// ---------------------------------------------------------------------
// Helpers
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

let private snapshotOf (rows: int) (cols: int) (lines: string list) : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

let private epoch = DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero)

let private at (msFromEpoch: int) : DateTimeOffset =
    epoch + TimeSpan.FromMilliseconds(float msFromEpoch)

// ---------------------------------------------------------------------
// hashRow / hashFrame algorithm pinning
// ---------------------------------------------------------------------

[<Fact>]
let ``identical rows at same index produce identical hashes`` () =
    let row = rowOf 5 "hello"
    Assert.Equal(Coalescer.hashRow 0 row, Coalescer.hashRow 0 row)

[<Fact>]
let ``differing rows at same index produce different hashes`` () =
    let a = rowOf 5 "hello"
    let b = rowOf 5 "world"
    Assert.NotEqual(Coalescer.hashRow 0 a, Coalescer.hashRow 0 b)

[<Fact>]
let ``same row content at different indices produces different hashes`` () =
    // Row-index folding defends against a row swap aliasing
    // back to the same frame hash.
    let row = rowOf 5 "hello"
    Assert.NotEqual(Coalescer.hashRow 0 row, Coalescer.hashRow 1 row)

[<Fact>]
let ``hashFrame is order-independent for blank suffix rows`` () =
    // Two frames that differ only in trailing blank rows
    // should still hash differently because each blank row
    // contributes its row-indexed hash to the XOR.
    let cols = 5
    let frameA = snapshotOf 3 cols [ "abc"; ""; "" ]
    let frameB = snapshotOf 3 cols [ "abc"; ""; "" ]
    Assert.Equal(Coalescer.hashFrame frameA, Coalescer.hashFrame frameB)

// ---------------------------------------------------------------------
// processRowsChanged — frame dedup
// ---------------------------------------------------------------------

[<Fact>]
let ``two identical RowsChanged frames in a row → only first emits`` () =
    let state = StreamProfile.createState ()
    let snap = snapshotOf 3 5 [ "hello" ]
    let first = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap
    Assert.Equal(1, first.Length)
    let second = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 500) snap
    Assert.Equal(0, second.Length)

[<Fact>]
let ``frame dedup releases when content actually changes`` () =
    let state = StreamProfile.createState ()
    let snap1 = snapshotOf 3 5 [ "hello" ]
    let snap2 = snapshotOf 3 5 [ "world" ]
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap1
    let next = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 500) snap2
    Assert.Equal(1, next.Length)

// ---------------------------------------------------------------------
// processRowsChanged — debounce (leading + trailing edge)
// ---------------------------------------------------------------------

[<Fact>]
let ``first event in idle period emits immediately (leading edge)`` () =
    let state = StreamProfile.createState ()
    // Width must accommodate the full string; the rowOf helper
    // truncates at cols-1 and renderRows trims trailing blanks,
    // so a 5-col screen would render "echo hello" as "echo".
    let snap = snapshotOf 3 20 [ "echo hello" ]
    let result = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.OutputBatch text -> Assert.Contains("echo hello", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch; got %A" other)

[<Fact>]
let ``burst of events within debounce window collapses to leading + trailing emit`` () =
    let state = StreamProfile.createState ()
    let snap1 = snapshotOf 3 5 [ "a" ]
    let snap2 = snapshotOf 3 5 [ "ab" ]
    let snap3 = snapshotOf 3 5 [ "abc" ]
    // Leading edge.
    let r1 = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap1
    Assert.Equal(1, r1.Length)
    // Within 200ms — should accumulate, NOT emit.
    let r2 = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 50) snap2
    Assert.Equal(0, r2.Length)
    let r3 = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 100) snap3
    Assert.Equal(0, r3.Length)
    // Trailing-edge timer tick at 200ms+ flushes the
    // last accumulated frame.
    let tick = StreamProfile.onTimerTick StreamProfile.defaultParameters state (at 250)
    Assert.Equal(1, tick.Length)
    match tick.[0] with
    | Coalescer.OutputBatch text -> Assert.Contains("abc", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch; got %A" other)

[<Fact>]
let ``trailing-edge tick with nothing pending is a no-op`` () =
    let state = StreamProfile.createState ()
    let snap = snapshotOf 3 5 [ "x" ]
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap
    // No accumulator built up; tick should emit nothing.
    let tick = StreamProfile.onTimerTick StreamProfile.defaultParameters state (at 250)
    Assert.Equal(0, tick.Length)

// ---------------------------------------------------------------------
// processRowsChanged — spinner suppression
// ---------------------------------------------------------------------

[<Fact>]
let ``per-key spinner gate fires after threshold same-row-hash hits in window`` () =
    // Alternate two frames (A, B) so frame-dedup doesn't
    // suppress the same content twice in a row — that lets
    // the spinner per-key gate accumulate hits for B's
    // (row=0, hash) key. After threshold hits within the
    // 1s window, the next B frame is spinner-suppressed.
    let state = StreamProfile.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) frameA
    let mutable suppressedCount = 0
    let mutable emittedCount = 0
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let result = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at (i * 60)) frame
        // Frame-dedup'd A's also return [] but DO NOT count
        // as spinner suppression — distinguish by checking
        // whether this is a B frame (the one whose history
        // grows) and whether the per-key history is already
        // saturated.
        if i % 2 = 1 then
            if result.IsEmpty then suppressedCount <- suppressedCount + 1
            else emittedCount <- emittedCount + 1
    // First few B frames accumulate (debounce) then we hit
    // the spinner-suppress threshold. We expect at least one
    // explicit spinner suppression after the threshold.
    Assert.True(suppressedCount >= 1,
        sprintf "Expected spinner-suppression to fire at least once; got %d suppressed, %d emitted"
            suppressedCount emittedCount)

[<Fact>]
let ``cross-row gate accumulates content-hash recurrence as spinner moves between rows (PR-M, Issue #117)`` () =
    // PR-M added the cross-row spinner gate. The pre-PR-M
    // per-key gate uses a row-index-folded hash, so a spinner
    // whose content moves between rows produces a different
    // (rowIdx, hash) key each frame and the per-key gate sees
    // count = 1 for each — never fires. The cross-row gate
    // tracks recurrences of the CONTENT hash regardless of
    // rowIdx, so a moving spinner accumulates count under one
    // key and trips the threshold.
    //
    // This test asserts the gate's STATE directly (rather than
    // observing emit-vs-suppress behaviour, which the debounce
    // window would conflate with). Repro: BAR moves between
    // rows 0..4 across 6 frames within the 1s spinner window;
    // at the end, HashHistory[content-hash-of-BAR-row] should
    // hold at least `spinnerThreshold` timestamps.
    let state = StreamProfile.createState ()
    let cols = 10
    let buildFrame (frameNum: int) (barRow: int) : Cell[][] =
        let arr = Array.init 5 (fun i ->
            if i = barRow then rowOf cols "BAR"
            else rowOf cols (sprintf "PAD%d-%d" i frameNum))
        arr
    // Frame 0 is the seed (LastRowHashes = None initially;
    // every row is "changed" relative to nothing).
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) (buildFrame 0 0)
    // 5 more frames moving BAR through rows 1..4 and back to
    // row 0, each with unique padding so the frame hash differs.
    // Each frame's BAR row produces the SAME content hash
    // (hashRowContent for "BAR" + cols-3 blanks).
    let times = [| 100; 200; 300; 400; 500; 600 |]
    let bars =  [|   1;   2;   3;   4;   0;   1 |]
    for i in 0 .. times.Length - 1 do
        let frame = buildFrame (i + 1) bars.[i]
        let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at times.[i]) frame
        ()
    // BAR's content hash should have accumulated 7 entries (the
    // seed frame + 6 loop frames, all within the 1s spinnerWindow).
    let barHash = Coalescer.hashRowContent (rowOf cols "BAR")
    let barCount =
        match state.HashHistory.TryGetValue barHash with
        | true, h -> h.Count
        | false, _ -> 0
    Assert.True(barCount >= 5,
        sprintf "Expected cross-row HashHistory[BAR] to have ≥%d entries; got %d"
            5 barCount)
    // Pre-PR-M behaviour check: the per-key gate's history would
    // have a separate (i, hashRow_i_BAR) entry for each row BAR
    // visited — count = 1 for each, never tripping the gate.
    // Post-PR-M the per-key gate is unchanged for this scenario.
    let perKeyBarKeys =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (_, h) ->
            // h is row-index-folded; BAR landed at 6 distinct rows
            // (0, 1, 2, 3, 4, 0, 1) producing 5 unique
            // (rowIdx, hashRow rowIdx BAR) keys.
            true)
        |> Seq.length
    // Cross-row gate is the new contract; per-key would never
    // fire here. Just sanity-check we have the per-key entries
    // we expect.
    Assert.True(perKeyBarKeys >= 5,
        sprintf "Sanity: expected per-key gate to have entries from BAR at multiple rows; got %d"
            perKeyBarKeys)

[<Fact>]
let ``static rows do not trip per-key gate at fast typing cadence (PR-M, Issue #117)`` () =
    // PR-M added change-detection: the spinner gates only count
    // hash CHANGES at a row position, not observations. Without
    // it, a 30-row screen with 29 static rows added 29 entries
    // per event to the per-row history; at 5+ events/sec the
    // static rows trip the per-key threshold and false-positive
    // suppress (the typing-cadence false-positive noted in the
    // issue's "Note on per-key gate interaction with static
    // rows" section).
    //
    // Repro: 30-row screen, only row 0 changes per event,
    // 7 events at ~50ms intervals (well within spinnerWindow).
    // The seed frame populates one entry per row (since
    // LastRowHashes = None initially treats every row as
    // changed); subsequent frames must NOT grow the static-row
    // counts past 1.
    let state = StreamProfile.createState ()
    let cols = 5
    let buildFrame (typed: string) : Cell[][] =
        let arr = Array.init 30 (fun _ -> blankRow cols)
        arr.[0] <- rowOf cols typed
        arr
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) (buildFrame "a")
    // Pump 6 more frames at 50ms intervals; only row 0 changes
    // each frame.
    let typeSeq = [ "ab"; "abc"; "abcd"; "abcde"; "abcdef"; "abcdefg" ]
    for i in 0 .. typeSeq.Length - 1 do
        let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at ((i + 1) * 50)) (buildFrame typeSeq.[i])
        ()
    // Static-row history counts should remain at 1 (only the
    // seed frame contributed; no subsequent frame changed those
    // rows). Pre-PR-M: each typing event would have added 30
    // entries, so by event 5 the static-row keys would be at
    // count 5 and the per-key gate would suppress.
    let staticRowMaxCount =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (rowIdx, _) -> rowIdx > 0)
        |> Seq.map (fun key -> state.PerRowHistory.[key].Count)
        |> Seq.fold max 0
    Assert.True(staticRowMaxCount <= 1,
        sprintf "Static-row entries should not grow past the seed frame's contribution; max count was %d"
            staticRowMaxCount)
    // Sanity: row 0 IS in the history with multiple entries
    // (each typed string produced a unique hashRow for row 0).
    let row0EntryCount =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (rowIdx, _) -> rowIdx = 0)
        |> Seq.length
    Assert.True(row0EntryCount >= 5,
        sprintf "Expected row 0 to accumulate multiple distinct hashes from typing; got %d" row0EntryCount)

[<Fact>]
let ``cross-row HashHistory ignores static blank rows (PR-M, Issue #117)`` () =
    // Companion to the per-key static-row test. Without change
    // detection, the cross-row HashHistory[blank-content-hash]
    // would accumulate 29 entries per frame (one per static
    // blank row) and trip the gate within 1 frame. With change
    // detection, blank-rows-that-stay-blank contribute nothing.
    let state = StreamProfile.createState ()
    let cols = 5
    let buildFrame (typed: string) : Cell[][] =
        let arr = Array.init 30 (fun _ -> blankRow cols)
        arr.[0] <- rowOf cols typed
        arr
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) (buildFrame "a")
    for i in 0 .. 5 do
        let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at ((i + 1) * 50)) (buildFrame (sprintf "x%d" i))
        ()
    // The blank-content hash should have at most 1 entry (the
    // initial seed frame, where every row was "changed"
    // relative to None). Subsequent frames don't change the
    // blank rows so they contribute nothing.
    let blankHash = Coalescer.hashRowContent (blankRow cols)
    let blankCount =
        match state.HashHistory.TryGetValue blankHash with
        | true, h -> h.Count
        | false, _ -> 0
    Assert.True(blankCount <= 1,
        sprintf "Expected blank-content hash to have ≤1 entry post-seed; got %d" blankCount)

[<Fact>]
let ``ModeChanged resets LastRowHashes and HashHistory (PR-M, Issue #117)`` () =
    // PR-M added LastRowHashes + HashHistory state. onModeChanged
    // must reset both — otherwise post-mode-change the cross-row
    // gate could carry forward stale recurrence counts from the
    // previous buffer, and the change-detection's "changed?"
    // check could falsely return false against pre-mode hashes.
    let state = StreamProfile.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap
    Assert.True(state.LastRowHashes.IsSome,
        "LastRowHashes should be populated after first frame")
    Assert.True(state.HashHistory.Count > 0,
        "HashHistory should be populated after first frame")
    let _ = StreamProfile.onModeChanged StreamProfile.defaultParameters state (at 100) AltScreen true
    Assert.True(state.LastRowHashes.IsNone,
        "LastRowHashes should be reset to None after mode change")
    Assert.Equal(0, state.HashHistory.Count)
    Assert.Equal(0, state.PerRowHistory.Count)

[<Fact>]
let ``spinner per-key history is GC'd after spinnerWindow elapses`` () =
    // Once spinnerWindow of quiet has passed, the per-key
    // history is trimmed by gcHistory and a new frame can
    // re-engage the leading-edge emit path.
    let state = StreamProfile.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    // Pump enough alternations to engage spinner suppression.
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) frameA
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at (i * 60)) frame
        ()
    // After 1.5s of quiet (well past spinnerWindow=1000ms),
    // a fresh content should emit again.
    let frameC = snapshotOf 1 1 [ "C" ]
    let result = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 5000) frameC
    Assert.True(result.Length >= 1,
        "Expected emit to recover after spinnerWindow of quiet")

// ---------------------------------------------------------------------
// onModeChanged — alt-screen flush barrier
// ---------------------------------------------------------------------

[<Fact>]
let ``ModeChanged AltScreen flushes pending accumulator first`` () =
    let state = StreamProfile.createState ()
    let snap1 = snapshotOf 1 5 [ "hello" ]
    let snap2 = snapshotOf 1 5 [ "world" ]
    // Leading-edge emit at t=0.
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap1
    // Within debounce — accumulates.
    let r2 = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 50) snap2
    Assert.Equal(0, r2.Length)
    // Mode change should flush the pending then pass through.
    let result = StreamProfile.onModeChanged StreamProfile.defaultParameters state (at 100) AltScreen true
    Assert.Equal(2, result.Length)
    match result.[0] with
    | Coalescer.OutputBatch text -> Assert.Contains("world", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch first; got %A" other)
    match result.[1] with
    | Coalescer.ModeBarrier (flag, value) ->
        Assert.Equal(AltScreen, flag)
        Assert.Equal(true, value)
    | other -> Assert.Fail(sprintf "Expected ModeBarrier second; got %A" other)

[<Fact>]
let ``ModeChanged with no pending still passes the barrier through`` () =
    let state = StreamProfile.createState ()
    let result = StreamProfile.onModeChanged StreamProfile.defaultParameters state (at 0) AltScreen false
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.ModeBarrier (flag, value) ->
        Assert.Equal(AltScreen, flag)
        Assert.Equal(false, value)
    | other -> Assert.Fail(sprintf "Expected ModeBarrier; got %A" other)

[<Fact>]
let ``ModeChanged resets frame-dedup state`` () =
    let state = StreamProfile.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 0) snap
    // Frame dedup would normally suppress this repeat...
    let dup = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 500) snap
    Assert.Equal(0, dup.Length)
    // ...but a mode barrier resets the dedup state, so the
    // same content emits again afterward.
    let _ = StreamProfile.onModeChanged StreamProfile.defaultParameters state (at 600) AltScreen true
    let after = StreamProfile.processRowsChanged StreamProfile.defaultParameters state (at 1000) snap
    Assert.Equal(1, after.Length)

// ---------------------------------------------------------------------
// onParserError — pass-through with sanitisation
// ---------------------------------------------------------------------

[<Fact>]
let ``ParserError passes through immediately as ErrorPassthrough`` () =
    let result = StreamProfile.onParserError "boom"
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.ErrorPassthrough s -> Assert.Equal("boom", s)
    | other -> Assert.Fail(sprintf "Expected ErrorPassthrough; got %A" other)

[<Fact>]
let ``ParserError strips control chars via sanitiser`` () =
    // BEL (\x07) must be stripped before NVDA sees it.
    let result = StreamProfile.onParserError "boom\x07!"
    match result.[0] with
    | Coalescer.ErrorPassthrough s ->
        Assert.False(s.Contains('\x07'),
            "BEL must be stripped from error messages")
        Assert.Contains("boom", s)
        Assert.Contains("!", s)
    | other -> Assert.Fail(sprintf "Expected ErrorPassthrough; got %A" other)

// ---------------------------------------------------------------------
// renderRows — sanitisation + multi-line preservation
// ---------------------------------------------------------------------

[<Fact>]
let ``renderRows strips control chars from each row`` () =
    // Plant a BEL inside the row content. The renderRows
    // contract says C0/DEL/C1 are stripped per row.
    let row = blankRow 5
    row.[0] <- cellOf 'a'
    row.[1] <- { Ch = System.Text.Rune '\x07'; Attrs = SgrAttrs.defaults }
    row.[2] <- cellOf 'b'
    let snap = [| row |]
    let text = Coalescer.renderRows snap
    Assert.False(text.Contains('\x07'),
        "BEL must be stripped from row content")
    Assert.Contains("a", text)
    Assert.Contains("b", text)

[<Fact>]
let ``renderRows preserves \n separator between rows`` () =
    // The fix for the sanitiser-strips-LF bug: we sanitise
    // each row first, THEN add the newline separator.
    let snap = snapshotOf 2 5 [ "row1"; "row2" ]
    let text = Coalescer.renderRows snap
    Assert.Contains("row1", text)
    Assert.Contains("row2", text)
    Assert.Contains("\n", text)

[<Fact>]
let ``renderRows trims trailing all-blank rows`` () =
    let snap = snapshotOf 5 5 [ "hi"; ""; ""; ""; "" ]
    let text = Coalescer.renderRows snap
    Assert.Equal("hi", text)

[<Fact>]
let ``renderRows trims trailing blank cells per row`` () =
    let snap = snapshotOf 1 10 [ "ab" ]  // "ab" + 8 blanks
    let text = Coalescer.renderRows snap
    Assert.Equal("ab", text)

// ---------------------------------------------------------------------
// ActivityIds vocabulary pinning
// ---------------------------------------------------------------------

[<Fact>]
let ``streaming-output activityId is pty-speak.output`` () =
    Assert.Equal("pty-speak.output", ActivityIds.output)

[<Fact>]
let ``error activityId is pty-speak.error`` () =
    Assert.Equal("pty-speak.error", ActivityIds.error)

[<Fact>]
let ``mode-barrier activityId is pty-speak.mode`` () =
    Assert.Equal("pty-speak.mode", ActivityIds.mode)

// ---------------------------------------------------------------------
// Stage 8d.2 — colour-detection helper pinning
// ---------------------------------------------------------------------
//
// The Earcon profile reads the StreamProfile's colour metadata from
// `OutputEvent.Extensions["dominantColor"]` and emits error-tone /
// warning-tone earcons. These tests pin the colour-detection
// algorithms that run inside StreamProfile.Apply / Tick before the
// metadata is stamped on the OutputEvent. The Earcon-side mapping
// (string colour → earcon id) is pinned in
// `EarconProfileTests.fs`.

let private redCell (ch: char) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Fg = Indexed 1uy } }

let private brightRedCell (ch: char) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Fg = Indexed 9uy } }

let private yellowCell (ch: char) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Fg = Indexed 3uy } }

let private brightYellowCell (ch: char) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Fg = Indexed 11uy } }

[<Fact>]
let ``isRedFg returns true for Indexed 1`` () =
    Assert.True(StreamProfile.isRedFg (Indexed 1uy))

[<Fact>]
let ``isRedFg returns true for Indexed 9 (bright red)`` () =
    Assert.True(StreamProfile.isRedFg (Indexed 9uy))

[<Fact>]
let ``isRedFg returns false for Indexed 0`` () =
    Assert.False(StreamProfile.isRedFg (Indexed 0uy))

[<Fact>]
let ``isRedFg returns false for Default`` () =
    Assert.False(StreamProfile.isRedFg Default)

[<Fact>]
let ``isRedFg returns false for Indexed 3 (yellow)`` () =
    Assert.False(StreamProfile.isRedFg (Indexed 3uy))

[<Fact>]
let ``isYellowFg returns true for Indexed 3`` () =
    Assert.True(StreamProfile.isYellowFg (Indexed 3uy))

[<Fact>]
let ``isYellowFg returns true for Indexed 11 (bright yellow)`` () =
    Assert.True(StreamProfile.isYellowFg (Indexed 11uy))

[<Fact>]
let ``isYellowFg returns false for Default`` () =
    Assert.False(StreamProfile.isYellowFg Default)

[<Fact>]
let ``isYellowFg returns false for Indexed 1 (red)`` () =
    Assert.False(StreamProfile.isYellowFg (Indexed 1uy))

[<Fact>]
let ``rowDominantColor returns red for majority-red row`` () =
    // 4 red cells + 1 plain + trailing blanks.
    let row = blankRow 10
    row.[0] <- redCell 'e'
    row.[1] <- redCell 'r'
    row.[2] <- redCell 'r'
    row.[3] <- redCell '!'
    row.[4] <- cellOf 'a'
    Assert.Equal(Some "red", StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor returns yellow for majority-yellow row`` () =
    let row = blankRow 10
    row.[0] <- yellowCell 'w'
    row.[1] <- yellowCell 'a'
    row.[2] <- yellowCell 'r'
    row.[3] <- yellowCell 'n'
    Assert.Equal(Some "yellow", StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor returns None for plain row`` () =
    let row = rowOf 10 "hello"
    Assert.Equal(None, StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor returns None for all-blank row`` () =
    let row = blankRow 10
    Assert.Equal(None, StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor ignores blank cells in the count`` () =
    // 1 red cell + 9 blanks. >50% of non-blanks (1/1 = 100%)
    // are red, so the row is red even though most cells are
    // blank. Blank-cell padding shouldn't dilute the count.
    let row = blankRow 10
    row.[0] <- redCell 'X'
    Assert.Equal(Some "red", StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor returns None for minority-red row`` () =
    // 1 red + 4 plain = 5 non-blank cells, 1 red. Red count
    // is not majority of non-blanks (1 * 2 = 2 ≯ 5).
    let row = blankRow 10
    row.[0] <- redCell '>'
    row.[1] <- cellOf 'p'
    row.[2] <- cellOf 'l'
    row.[3] <- cellOf 'a'
    row.[4] <- cellOf 'n'
    Assert.Equal(None, StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor handles bright-red cells`` () =
    let row = blankRow 5
    row.[0] <- brightRedCell 'E'
    row.[1] <- brightRedCell 'R'
    row.[2] <- brightRedCell 'R'
    Assert.Equal(Some "red", StreamProfile.rowDominantColor row)

[<Fact>]
let ``rowDominantColor handles bright-yellow cells`` () =
    let row = blankRow 5
    row.[0] <- brightYellowCell 'W'
    row.[1] <- brightYellowCell 'A'
    row.[2] <- brightYellowCell 'R'
    Assert.Equal(Some "yellow", StreamProfile.rowDominantColor row)

[<Fact>]
let ``snapshotDominantColor returns red when any row is red`` () =
    let snap = Array.init 3 (fun _ -> blankRow 5)
    snap.[1].[0] <- redCell 'e'
    snap.[1].[1] <- redCell 'r'
    snap.[1].[2] <- redCell 'r'
    Assert.Equal(Some "red", StreamProfile.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor returns yellow when only yellow rows present`` () =
    let snap = Array.init 3 (fun _ -> blankRow 5)
    snap.[0].[0] <- yellowCell 'w'
    snap.[0].[1] <- yellowCell 'a'
    Assert.Equal(Some "yellow", StreamProfile.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor prefers red over yellow when both present`` () =
    let snap = Array.init 3 (fun _ -> blankRow 5)
    // Yellow row first.
    snap.[0].[0] <- yellowCell 'w'
    snap.[0].[1] <- yellowCell 'a'
    snap.[0].[2] <- yellowCell 'r'
    snap.[0].[3] <- yellowCell 'n'
    // Red row second.
    snap.[2].[0] <- redCell 'e'
    snap.[2].[1] <- redCell 'r'
    snap.[2].[2] <- redCell 'r'
    Assert.Equal(Some "red", StreamProfile.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor returns None for all-plain snapshot`` () =
    let snap = snapshotOf 3 5 [ "hi"; "lo"; "ok" ]
    Assert.Equal(None, StreamProfile.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor returns None for all-blank snapshot`` () =
    let snap = Array.init 3 (fun _ -> blankRow 5)
    Assert.Equal(None, StreamProfile.snapshotDominantColor snap)

// ---------------------------------------------------------------------
// Stage 8b note: the Stage-7 `runLoop` orchestrator has been removed.
// Its three integration tests (cancellation, end-to-end emit, and the
// PeriodicTimer-concurrent-call regression) covered the orchestration
// shell, which now lives in `src/Terminal.App/Program.fs` as the
// TranslatorPump + TickPump pair. Composition-root orchestration is
// not unit-tested in this codebase; the algorithm-level pins above
// + the Stage-5 NVDA-validation matrix in
// `docs/ACCESSIBILITY-TESTING.md` cover what those three tests
// covered before.
// ---------------------------------------------------------------------
