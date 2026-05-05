module PtySpeak.Tests.Unit.StreamPathwayTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Phase A — StreamPathway behavioural pinning
// ---------------------------------------------------------------------
//
// StreamPathway preserves the four StreamProfile algorithms
// (per-row/frame hash dedup, per-key + cross-row spinner gates,
// leading + trailing-edge debounce, and the mode-barrier flush
// reset). The migration target is `processCanonicalState` /
// `onTimerTick` / `onModeBarrier`; the algorithm tests below
// were originally pinned in `StreamProfileTests.fs` — they're
// reproduced here verbatim with the StreamProfile.* call sites
// rewritten as StreamPathway.*. Behaviour preservation is the
// gate.
//
// What changes from the StreamProfile shape:
//   * The pathway emits `OutputEvent[]` directly (rather than
//     `Coalescer.CoalescedNotification list`). Tests assert on
//     `event.Payload` content + array length.
//   * The pathway tracks `LastEmittedRowHashes` so `computeDiff`
//     produces the diff text. First-call emits all rows;
//     subsequent emits emit changed rows only.
//   * `onModeBarrier` no longer takes a `(flag, value)` pair;
//     the pathway resets state + flushes pending unconditionally.
//   * `onParserError` is gone — parser errors don't pass through
//     the pathway in Phase A (they go through OutputEventBuilder
//     directly, dispatched via PathwayPump's other branch).

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

let private epoch = DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero)

let private at (msFromEpoch: int) : DateTimeOffset =
    epoch + TimeSpan.FromMilliseconds(float msFromEpoch)

let private canonicalAt (snapshot: Cell[][]) (seq: int64) : CanonicalState.Canonical =
    CanonicalState.create snapshot seq

// ---- Frame dedup ----------------------------------------------------

[<Fact>]
let ``two identical canonical frames in a row → only first emits`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 3 5 [ "hello" ]
    let first =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap 0L)
    Assert.Equal(1, first.Length)
    let second =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500)
            (canonicalAt snap 1L)
    Assert.Equal(0, second.Length)

[<Fact>]
let ``frame dedup releases when content actually changes`` () =
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 3 5 [ "hello" ]
    let snap2 = snapshotOf 3 5 [ "world" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    let next =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500) (canonicalAt snap2 1L)
    Assert.Equal(1, next.Length)

// ---- Leading-edge / trailing-edge debounce --------------------------

[<Fact>]
let ``first canonical state in idle period emits immediately (leading edge)`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 3 20 [ "echo hello" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap 0L)
    Assert.Equal(1, result.Length)
    Assert.Contains("echo hello", result.[0].Payload)

[<Fact>]
let ``leading-edge emit produces a StreamChunk OutputEvent`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 10 [ "abc" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap 0L)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)
    Assert.Equal(Priority.Polite, result.[0].Priority)
    Assert.Equal("stream", result.[0].Source.Producer)

[<Fact>]
let ``burst within debounce window collapses to leading + trailing emit`` () =
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 3 5 [ "a" ]
    let snap2 = snapshotOf 3 5 [ "ab" ]
    let snap3 = snapshotOf 3 5 [ "abc" ]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 50) (canonicalAt snap2 1L)
    Assert.Equal(0, r2.Length)
    let r3 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 100) (canonicalAt snap3 2L)
    Assert.Equal(0, r3.Length)
    let tick =
        StreamPathway.onTimerTick
            StreamPathway.defaultParameters state (at 250)
    Assert.Equal(1, tick.Length)
    Assert.Contains("abc", tick.[0].Payload)

[<Fact>]
let ``trailing-edge tick with nothing pending is a no-op`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 3 5 [ "x" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap 0L)
    let tick =
        StreamPathway.onTimerTick
            StreamPathway.defaultParameters state (at 250)
    Assert.Equal(0, tick.Length)

// ---- Diff-only emit (the headline Phase A behaviour) ---------------

[<Fact>]
let ``second emit ships diff text only, not the entire snapshot`` () =
    // The verbose-readback fix (GitHub #115/#139): NVDA hears
    // only the changed row, not the cmd banner re-announced.
    let state = StreamPathway.createState ()
    // 3 rows. First emit ships everything. Second emit should
    // ship only the row that changed.
    let snap1 = snapshotOf 3 10 [ "banner1"; "banner2"; "" ]
    let snap2 = snapshotOf 3 10 [ "banner1"; "banner2"; "echo hi" ]
    // Leading-edge first emit — full snapshot.
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    Assert.Contains("banner1", r1.[0].Payload)
    Assert.Contains("banner2", r1.[0].Payload)
    // After 200ms+ idle, second leading-edge emit at row 2 only.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500) (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    Assert.Contains("echo hi", r2.[0].Payload)
    Assert.DoesNotContain("banner1", r2.[0].Payload)
    Assert.DoesNotContain("banner2", r2.[0].Payload)

// ---- Spinner suppression -------------------------------------------

[<Fact>]
let ``per-key spinner gate fires after threshold same-row-hash hits in window`` () =
    let state = StreamPathway.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt frameA 0L)
    let mutable suppressedCount = 0
    let mutable emittedCount = 0
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let result =
            StreamPathway.processCanonicalState
                StreamPathway.defaultParameters state (at (i * 60))
                (canonicalAt frame (int64 i))
        if i % 2 = 1 then
            if result.Length = 0 then suppressedCount <- suppressedCount + 1
            else emittedCount <- emittedCount + 1
    Assert.True(suppressedCount >= 1,
        sprintf "Expected spinner-suppression to fire at least once; got %d suppressed, %d emitted"
            suppressedCount emittedCount)

[<Fact>]
let ``cross-row gate accumulates content-hash recurrence as spinner moves between rows (PR-M, Issue #117)`` () =
    let state = StreamPathway.createState ()
    let cols = 10
    let buildFrame (frameNum: int) (barRow: int) : Cell[][] =
        let arr = Array.init 5 (fun i ->
            if i = barRow then rowOf cols "BAR"
            else rowOf cols (sprintf "PAD%d-%d" i frameNum))
        arr
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt (buildFrame 0 0) 0L)
    let times = [| 100; 200; 300; 400; 500; 600 |]
    let bars =  [|   1;   2;   3;   4;   0;   1 |]
    for i in 0 .. times.Length - 1 do
        let frame = buildFrame (i + 1) bars.[i]
        let _ =
            StreamPathway.processCanonicalState
                StreamPathway.defaultParameters state (at times.[i])
                (canonicalAt frame (int64 (i + 1)))
        ()
    let barHash = Coalescer.hashRowContent (rowOf cols "BAR")
    let barCount =
        match state.HashHistory.TryGetValue barHash with
        | true, h -> h.Count
        | false, _ -> 0
    Assert.True(barCount >= 5,
        sprintf "Expected cross-row HashHistory[BAR] to have ≥%d entries; got %d"
            5 barCount)
    let perKeyBarKeys =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (_, _) -> true)
        |> Seq.length
    Assert.True(perKeyBarKeys >= 5,
        sprintf "Sanity: expected per-key gate to have entries from BAR at multiple rows; got %d"
            perKeyBarKeys)

[<Fact>]
let ``static rows do not trip per-key gate at fast typing cadence (PR-M, Issue #117)`` () =
    let state = StreamPathway.createState ()
    let cols = 5
    let buildFrame (typed: string) : Cell[][] =
        let arr = Array.init 30 (fun _ -> blankRow cols)
        arr.[0] <- rowOf cols typed
        arr
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt (buildFrame "a") 0L)
    let typeSeq = [ "ab"; "abc"; "abcd"; "abcde"; "abcdef"; "abcdefg" ]
    for i in 0 .. typeSeq.Length - 1 do
        let _ =
            StreamPathway.processCanonicalState
                StreamPathway.defaultParameters state (at ((i + 1) * 50))
                (canonicalAt (buildFrame typeSeq.[i]) (int64 (i + 1)))
        ()
    let staticRowMaxCount =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (rowIdx, _) -> rowIdx > 0)
        |> Seq.map (fun key -> state.PerRowHistory.[key].Count)
        |> Seq.fold max 0
    Assert.True(staticRowMaxCount <= 1,
        sprintf "Static-row entries should not grow past the seed frame's contribution; max count was %d"
            staticRowMaxCount)
    let row0EntryCount =
        state.PerRowHistory.Keys
        |> Seq.filter (fun (rowIdx, _) -> rowIdx = 0)
        |> Seq.length
    Assert.True(row0EntryCount >= 5,
        sprintf "Expected row 0 to accumulate multiple distinct hashes from typing; got %d" row0EntryCount)

[<Fact>]
let ``cross-row HashHistory ignores static blank rows (PR-M, Issue #117)`` () =
    let state = StreamPathway.createState ()
    let cols = 5
    let buildFrame (typed: string) : Cell[][] =
        let arr = Array.init 30 (fun _ -> blankRow cols)
        arr.[0] <- rowOf cols typed
        arr
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt (buildFrame "a") 0L)
    for i in 0 .. 5 do
        let _ =
            StreamPathway.processCanonicalState
                StreamPathway.defaultParameters state (at ((i + 1) * 50))
                (canonicalAt (buildFrame (sprintf "x%d" i)) (int64 (i + 1)))
        ()
    let blankHash = Coalescer.hashRowContent (blankRow cols)
    let blankCount =
        match state.HashHistory.TryGetValue blankHash with
        | true, h -> h.Count
        | false, _ -> 0
    Assert.True(blankCount <= 1,
        sprintf "Expected blank-content hash to have ≤1 entry post-seed; got %d" blankCount)

[<Fact>]
let ``onModeBarrier resets LastRowHashes and HashHistory (PR-M, Issue #117)`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap 0L)
    Assert.True(state.LastRowHashes.IsSome,
        "LastRowHashes should be populated after first frame")
    Assert.True(state.HashHistory.Count > 0,
        "HashHistory should be populated after first frame")
    let _ = StreamPathway.onModeBarrier state (at 100)
    Assert.True(state.LastRowHashes.IsNone,
        "LastRowHashes should be reset to None after mode barrier")
    Assert.Equal(0, state.HashHistory.Count)
    Assert.Equal(0, state.PerRowHistory.Count)

[<Fact>]
let ``spinner per-key history is GC'd after spinnerWindow elapses`` () =
    let state = StreamPathway.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt frameA 0L)
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let _ =
            StreamPathway.processCanonicalState
                StreamPathway.defaultParameters state (at (i * 60))
                (canonicalAt frame (int64 i))
        ()
    let frameC = snapshotOf 1 1 [ "C" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 5000) (canonicalAt frameC 100L)
    Assert.True(result.Length >= 1,
        "Expected emit to recover after spinnerWindow of quiet")

// ---- Mode-barrier semantics ----------------------------------------

[<Fact>]
let ``onModeBarrier flushes pending accumulator first`` () =
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 5 [ "hello" ]
    let snap2 = snapshotOf 1 5 [ "world" ]
    // Leading-edge emit at t=0.
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    // Within debounce — accumulates.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 50) (canonicalAt snap2 1L)
    Assert.Equal(0, r2.Length)
    // Mode barrier flushes the pending diff.
    let result = StreamPathway.onModeBarrier state (at 100)
    Assert.Equal(1, result.Length)
    Assert.Contains("world", result.[0].Payload)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)

[<Fact>]
let ``onModeBarrier with no pending returns no events`` () =
    let state = StreamPathway.createState ()
    let result = StreamPathway.onModeBarrier state (at 0)
    Assert.Equal(0, result.Length)

[<Fact>]
let ``onModeBarrier resets frame-dedup state`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap 0L)
    // Frame dedup would normally suppress this repeat...
    let dup =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500) (canonicalAt snap 1L)
    Assert.Equal(0, dup.Length)
    // ...but a mode barrier resets the dedup state, so the
    // same content emits again afterward.
    let _ = StreamPathway.onModeBarrier state (at 600)
    let after =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 1000) (canonicalAt snap 2L)
    Assert.Equal(1, after.Length)

// ---- Pathway-level wiring (Consume / Tick / Reset) -----------------

[<Fact>]
let ``Consume emits the first canonical frame in full`` () =
    let pathway = StreamPathway.create StreamPathway.defaultParameters
    let snap = snapshotOf 2 10 [ "row1"; "row2" ]
    let canonical = CanonicalState.create snap 0L
    let result = pathway.Consume canonical
    Assert.Equal(1, result.Length)
    Assert.Contains("row1", result.[0].Payload)
    Assert.Contains("row2", result.[0].Payload)

[<Fact>]
let ``Reset clears state so next Consume re-emits in full`` () =
    let pathway, state = StreamPathway.createWithExposedState StreamPathway.defaultParameters
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ = pathway.Consume (CanonicalState.create snap 0L)
    Assert.True(state.LastRowHashes.IsSome)
    pathway.Reset ()
    Assert.True(state.LastRowHashes.IsNone)
    Assert.Equal<uint64[]>([||], state.LastEmittedRowHashes)
    let result = pathway.Consume (CanonicalState.create snap 1L)
    Assert.Equal(1, result.Length)
    Assert.Contains("hello", result.[0].Payload)

[<Fact>]
let ``pathway Id is "stream"`` () =
    let pathway = StreamPathway.create StreamPathway.defaultParameters
    Assert.Equal("stream", pathway.Id)

// ---- SetBaseline (hot-switch baseline-seed) ------------------------

[<Fact>]
let ``SetBaseline seeds LastEmittedRowHashes from the supplied canonical state`` () =
    // Use createWithExposedState so the test can inspect the
    // mutable State directly — SetBaseline writes through the
    // closure, not through any observable return value.
    let pathway, state = StreamPathway.createWithExposedState StreamPathway.defaultParameters
    let snap = snapshotOf 3 10 [ "claude prompt"; "row two"; "row three" ]
    let canonical = CanonicalState.create snap 0L
    pathway.SetBaseline canonical
    Assert.Equal<uint64[]>(canonical.RowHashes, state.LastEmittedRowHashes)
    Assert.True(state.LastRowHashes.IsSome)
    Assert.Equal<uint64[]>(canonical.RowHashes, state.LastRowHashes.Value)

[<Fact>]
let ``SetBaseline emits no events`` () =
    // SetBaseline updates internal state ONLY — no OutputEvents.
    // The signature returns `unit`; this test pins that the
    // state mutation doesn't piggy-back on Consume's emission
    // path.
    let pathway = StreamPathway.create StreamPathway.defaultParameters
    let snap = snapshotOf 2 10 [ "stale claude content" ]
    let canonical = CanonicalState.create snap 0L
    pathway.SetBaseline canonical  // returns unit; no events to inspect
    // A subsequent Consume of the SAME snapshot should now
    // return zero events because the baseline matches.
    let result = pathway.Consume canonical
    Assert.Equal(0, result.Length)

[<Fact>]
let ``SetBaseline + Consume of changed frame emits diff-only`` () =
    // The headline hot-switch fix: after SetBaseline against
    // the pre-switch screen, the next Consume emits only the
    // rows the new shell painted, not the entire stale screen.
    let pathway = StreamPathway.create StreamPathway.defaultParameters
    // Pre-switch screen — claude content.
    let preSwitch =
        snapshotOf 5 20
            [ "claude code chat"
              "context: working on..."
              "blah blah blah"
              ""
              "" ]
    pathway.SetBaseline (CanonicalState.create preSwitch 100L)
    // New cmd shell paints its prompt at row 4 (assume cmd
    // overwrites only that row); rows 0-3 still hold claude
    // content because the screen buffer isn't cleared.
    let postSwitch =
        snapshotOf 5 20
            [ "claude code chat"
              "context: working on..."
              "blah blah blah"
              ""
              "C:\\>" ]
    let result = pathway.Consume (CanonicalState.create postSwitch 101L)
    Assert.Equal(1, result.Length)
    // The user should hear "C:\>" — NOT the claude content.
    Assert.Contains("C:\\>", result.[0].Payload)
    Assert.DoesNotContain("claude", result.[0].Payload)
    Assert.DoesNotContain("blah", result.[0].Payload)

[<Fact>]
let ``SetBaseline overrides a prior baseline`` () =
    // If the user hot-switches twice in quick succession,
    // SetBaseline must always take the latest canonical's
    // hashes (not mix with prior state).
    let pathway, state = StreamPathway.createWithExposedState StreamPathway.defaultParameters
    let snap1 = snapshotOf 1 5 [ "first" ]
    let snap2 = snapshotOf 1 5 [ "second" ]
    let canonical1 = CanonicalState.create snap1 0L
    let canonical2 = CanonicalState.create snap2 1L
    pathway.SetBaseline canonical1
    pathway.SetBaseline canonical2
    Assert.Equal<uint64[]>(canonical2.RowHashes, state.LastEmittedRowHashes)

// ---- ActivityIds vocabulary pinning (preserved from StreamProfileTests) -----

[<Fact>]
let ``streaming-output activityId is pty-speak.output`` () =
    Assert.Equal("pty-speak.output", ActivityIds.output)

[<Fact>]
let ``error activityId is pty-speak.error`` () =
    Assert.Equal("pty-speak.error", ActivityIds.error)

[<Fact>]
let ``mode-barrier activityId is pty-speak.mode`` () =
    Assert.Equal("pty-speak.mode", ActivityIds.mode)

// ---- renderRows / hashRow / hashFrame algorithm pinning ------------
// (preserved from StreamProfileTests — these test the Coalescer
//  helpers the substrate uses, not the StreamProfile that's gone.)

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
    let row = rowOf 5 "hello"
    Assert.NotEqual(Coalescer.hashRow 0 row, Coalescer.hashRow 1 row)

[<Fact>]
let ``hashFrame is order-independent for blank suffix rows`` () =
    let cols = 5
    let frameA = snapshotOf 3 cols [ "abc"; ""; "" ]
    let frameB = snapshotOf 3 cols [ "abc"; ""; "" ]
    Assert.Equal(Coalescer.hashFrame frameA, Coalescer.hashFrame frameB)

[<Fact>]
let ``renderRows strips control chars from each row`` () =
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
    let snap = snapshotOf 1 10 [ "ab" ]
    let text = Coalescer.renderRows snap
    Assert.Equal("ab", text)
