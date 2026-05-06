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

// PR #168 — many existing tests were written against PR #166's
// verbose-flush mode-barrier default. Tier 1 parameters change
// the default to `SummaryOnly` (no flush). Tests that
// specifically verify the verbose-flush behaviour use this
// helper to opt into the old policy explicitly; tests that
// verify state-clear-only behaviour use `defaultParameters`.
let private verboseFlushParameters : StreamPathway.Parameters =
    { StreamPathway.defaultParameters with
        ModeBarrierFlushPolicy = StreamPathway.Verbose }

// PR #168 — likewise, the Shrink branch of `computeRowSuffixDelta`
// used to be unconditionally Silent. The new default (announce
// the deleted segment) is captured by `defaultParameters`'s
// `BackspacePolicy = AnnounceDeletedCharacter`. Tests that
// verify the SuppressShrink behaviour use this helper.
let private suppressShrinkParameters : StreamPathway.Parameters =
    { StreamPathway.defaultParameters with
        BackspacePolicy = StreamPathway.SuppressShrink }

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
    // Burst-within-debounce semantics post-PR-#166 (suffix-
    // diff): the leading-edge emits the first frame's
    // suffix-diff vs. the empty cache (Initial → "a"). The
    // next two frames accumulate (no emit). The trailing-edge
    // emits the suffix-diff vs. the last-emit baseline ("a"),
    // which is "bc" — the new content since that baseline.
    // Cumulative announcement across both emits = "a" + "bc"
    // = "abc"; this preserves the original test intent (the
    // burst's full content reaches NVDA) while reflecting
    // the new suffix-diff semantics.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 3 5 [ "a" ]
    let snap2 = snapshotOf 3 5 [ "ab" ]
    let snap3 = snapshotOf 3 5 [ "abc" ]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    Assert.Equal("a", r1.[0].Payload)
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
    Assert.Equal("bc", tick.[0].Payload)
    // Sanity check on the original test intent — cumulative
    // payload across leading + trailing recovers the full
    // burst content "abc".
    Assert.Equal("abc", r1.[0].Payload + tick.[0].Payload)

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
    let _ = StreamPathway.onModeBarrier StreamPathway.defaultParameters state (at 100)
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
    // PR #168 — uses `verboseFlushParameters` to opt into
    // the pre-PR-#168 verbose-flush behaviour. The new
    // default (`SummaryOnly`) suppresses the flush; the
    // pre-PR-#168 verbose case is still a valid policy and
    // this test pins it.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 5 [ "hello" ]
    let snap2 = snapshotOf 1 5 [ "world" ]
    // Leading-edge emit at t=0.
    let _ =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 0) (canonicalAt snap1 0L)
    // Within debounce — accumulates.
    let r2 =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 50) (canonicalAt snap2 1L)
    Assert.Equal(0, r2.Length)
    // Mode barrier flushes the pending diff (Verbose policy).
    let result = StreamPathway.onModeBarrier verboseFlushParameters state (at 100)
    Assert.Equal(1, result.Length)
    Assert.Contains("world", result.[0].Payload)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)

[<Fact>]
let ``onModeBarrier with no pending returns no events`` () =
    let state = StreamPathway.createState ()
    let result = StreamPathway.onModeBarrier StreamPathway.defaultParameters state (at 0)
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
    let _ = StreamPathway.onModeBarrier StreamPathway.defaultParameters state (at 600)
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

// ---- Phase A.2 — colour detection ----------------------------------

let private cellWithFg (ch: char) (fg: ColorSpec) : Cell =
    { Ch = System.Text.Rune ch
      Attrs = { SgrAttrs.defaults with Fg = fg } }

let private rowOfFg (cols: int) (fg: ColorSpec) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellWithFg s.[i] fg
    row

let private redSnapshotOf (rows: int) (cols: int) (lines: string list) : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOfFg cols (Indexed 1uy) line)
    arr

let private yellowSnapshotOf (rows: int) (cols: int) (lines: string list) : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOfFg cols (Indexed 3uy) line)
    arr

[<Fact>]
let ``isRedFg true for Indexed 1 and 9, false otherwise`` () =
    Assert.True(StreamPathway.isRedFg (Indexed 1uy))
    Assert.True(StreamPathway.isRedFg (Indexed 9uy))
    Assert.False(StreamPathway.isRedFg (Indexed 2uy))
    Assert.False(StreamPathway.isRedFg Default)
    Assert.False(StreamPathway.isRedFg (Rgb (255uy, 0uy, 0uy)))

[<Fact>]
let ``isYellowFg true for Indexed 3 and 11, false otherwise`` () =
    Assert.True(StreamPathway.isYellowFg (Indexed 3uy))
    Assert.True(StreamPathway.isYellowFg (Indexed 11uy))
    Assert.False(StreamPathway.isYellowFg (Indexed 4uy))
    Assert.False(StreamPathway.isYellowFg Default)

[<Fact>]
let ``rowDominantColor red majority returns red`` () =
    // 5 non-blank red cells → 100% red → "red".
    let row = rowOfFg 10 (Indexed 1uy) "Error"
    Assert.Equal(Some "red", StreamPathway.rowDominantColor row)

[<Fact>]
let ``rowDominantColor below 50 percent threshold returns None`` () =
    // 2 red cells out of 4 non-blank → exactly 50% — NOT > 50%.
    let row = blankRow 10
    row.[0] <- cellWithFg 'E' (Indexed 1uy)
    row.[1] <- cellWithFg 'R' (Indexed 1uy)
    row.[2] <- cellOf 'O'
    row.[3] <- cellOf 'K'
    Assert.Equal(None, StreamPathway.rowDominantColor row)

[<Fact>]
let ``rowDominantColor blank row returns None`` () =
    let row = blankRow 10
    Assert.Equal(None, StreamPathway.rowDominantColor row)

[<Fact>]
let ``snapshotDominantColor — red wins over yellow`` () =
    let snap =
        Array.init 3 (fun i ->
            if i = 0 then rowOfFg 5 (Indexed 1uy) "red"
            elif i = 1 then rowOfFg 5 (Indexed 3uy) "yel"
            else blankRow 5)
    Assert.Equal(Some "red", StreamPathway.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor — yellow wins when no red`` () =
    let snap =
        [| rowOfFg 5 (Indexed 3uy) "warn"; blankRow 5 |]
    Assert.Equal(Some "yellow", StreamPathway.snapshotDominantColor snap)

[<Fact>]
let ``snapshotDominantColor — plain returns None`` () =
    let snap = snapshotOf 2 5 [ "abc"; "def" ]
    Assert.Equal(None, StreamPathway.snapshotDominantColor snap)

[<Fact>]
let ``red snapshot leading-edge emit returns 2 events with ErrorLine`` () =
    let state = StreamPathway.createState ()
    let snap = redSnapshotOf 1 10 [ "Error" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap 0L)
    Assert.Equal(2, result.Length)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)
    Assert.Contains("Error", result.[0].Payload)
    Assert.Equal(SemanticCategory.ErrorLine, result.[1].Semantic)
    Assert.Equal("", result.[1].Payload)
    Assert.Equal(Priority.Assertive, result.[1].Priority)

[<Fact>]
let ``yellow snapshot leading-edge emit returns 2 events with WarningLine`` () =
    let state = StreamPathway.createState ()
    let snap = yellowSnapshotOf 1 10 [ "Warn" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap 0L)
    Assert.Equal(2, result.Length)
    Assert.Equal(SemanticCategory.WarningLine, result.[1].Semantic)
    Assert.Equal("", result.[1].Payload)

[<Fact>]
let ``plain snapshot leading-edge emit returns 1 event (regression guard)`` () =
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 10 [ "hello" ]
    let result =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap 0L)
    Assert.Equal(1, result.Length)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)

[<Fact>]
let ``ColorDetection = false suppresses second event on red snapshot`` () =
    let state = StreamPathway.createState ()
    let snap = redSnapshotOf 1 10 [ "Error" ]
    let parameters =
        { StreamPathway.defaultParameters with ColorDetection = false }
    let result =
        StreamPathway.processCanonicalState
            parameters state (at 0) (canonicalAt snap 0L)
    Assert.Equal(1, result.Length)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)

[<Fact>]
let ``red snapshot accumulated within debounce → onTimerTick emits both events`` () =
    let state = StreamPathway.createState ()
    let snap1 = redSnapshotOf 1 10 [ "E" ]
    let snap2 = redSnapshotOf 1 10 [ "Err" ]
    // Leading-edge at t=0 — emits 2 events for snap1.
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    Assert.Equal(2, r1.Length)
    // Within debounce — accumulates snap2, no immediate emit.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 50) (canonicalAt snap2 1L)
    Assert.Equal(0, r2.Length)
    // Trailing-edge at t=300 — flushes pending diff + colour.
    let tick =
        StreamPathway.onTimerTick StreamPathway.defaultParameters state (at 300)
    Assert.Equal(2, tick.Length)
    Assert.Equal(SemanticCategory.StreamChunk, tick.[0].Semantic)
    Assert.Equal(SemanticCategory.ErrorLine, tick.[1].Semantic)
    // PendingColor cleared after the flush.
    Assert.Equal(ValueNone, state.PendingColor)

[<Fact>]
let ``onModeBarrier with PendingColor clears it without emitting colour event`` () =
    // PR #168 — uses `verboseFlushParameters` because this
    // test specifically verifies "the flush emits StreamChunk
    // but NOT the colour event". With the new SummaryOnly
    // default no flush would happen at all, so the
    // colour-event-suppression assertion would be trivially
    // true (no events emitted period). The Verbose path is
    // where the suppression matters.
    let state = StreamPathway.createState ()
    let snap1 = redSnapshotOf 1 10 [ "E" ]
    let snap2 = redSnapshotOf 1 10 [ "Err" ]
    // Leading-edge emit (resets PendingColor inside).
    let _ =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 0) (canonicalAt snap1 0L)
    // Within debounce — accumulates with PendingColor = ValueSome "red".
    let _ =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 50) (canonicalAt snap2 1L)
    Assert.Equal(ValueSome "red", state.PendingColor)
    // Mode barrier flushes the pending diff but NOT the colour event.
    let result = StreamPathway.onModeBarrier verboseFlushParameters state (at 100)
    Assert.Equal(1, result.Length)
    Assert.Equal(SemanticCategory.StreamChunk, result.[0].Semantic)
    Assert.Equal(ValueNone, state.PendingColor)

// ---- Phase A.2 hotfix — colour detection scoped to diff -----------

[<Fact>]
let ``changedRowsDominantColor — only walks rows in the changedRows index`` () =
    // Two rows: row 0 red, row 1 plain. The "all rows" scan
    // (snapshotDominantColor) returns Some "red"; the
    // "changed rows only" scan with changedRows = [|1|] must
    // return None because only the plain row is in scope.
    let row0 = rowOfFg 10 (Indexed 1uy) "Error"
    let row1 = rowOf 10 "okay"
    let snap = [| row0; row1 |]
    Assert.Equal(Some "red", StreamPathway.snapshotDominantColor snap)
    Assert.Equal(None, StreamPathway.changedRowsDominantColor snap [| 1 |])
    Assert.Equal(Some "red", StreamPathway.changedRowsDominantColor snap [| 0 |])
    Assert.Equal(Some "red", StreamPathway.changedRowsDominantColor snap [| 0; 1 |])

[<Fact>]
let ``red row outside diff scope produces no ErrorLine emit`` () =
    // The Phase A.2 hotfix regression. Sequence:
    //   1. snap1 has row 0 = red "Error", row 1 = plain "okay".
    //      Leading-edge emit: 2 events (StreamChunk + ErrorLine).
    //   2. snap2 has row 0 unchanged red "Error", row 1 = plain
    //      "later". Only row 1 changed.
    //   3. Second leading-edge (at debounce + 1ms): 1 event
    //      (StreamChunk only — no ErrorLine). The red row is
    //      OUTSIDE the diff's ChangedRows so the supplementary
    //      colour event must NOT fire.
    //
    // Pre-hotfix: snapshotDominantColor walks all rows, sees red
    //   row 0, returns Some "red", emits ErrorLine on every
    //   keystroke at the new prompt. This is the bug the hotfix
    //   addresses.
    let state = StreamPathway.createState ()
    let row0 = rowOfFg 10 (Indexed 1uy) "Error"
    let row1Original = rowOf 10 "okay"
    let row1Changed = rowOf 10 "later"
    let snap1 = [| row0; row1Original |]
    let snap2 = [| row0; row1Changed |]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap1 0L)
    Assert.Equal(2, r1.Length)
    Assert.Equal(SemanticCategory.ErrorLine, r1.[1].Semantic)
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500)
            (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    Assert.Equal(SemanticCategory.StreamChunk, r2.[0].Semantic)
    // The diff's changed text contains only the row that
    // actually changed (row 1).
    Assert.Contains("later", r2.[0].Payload)
    Assert.DoesNotContain("Error", r2.[0].Payload)

// ---- Sub-row suffix-diff (PR #166 — item 1) ---------------------

[<Fact>]
let ``longestCommonPrefixLength — empty inputs return 0`` () =
    Assert.Equal(0, StreamPathway.longestCommonPrefixLength "" "")
    Assert.Equal(0, StreamPathway.longestCommonPrefixLength "" "abc")
    Assert.Equal(0, StreamPathway.longestCommonPrefixLength "abc" "")

[<Fact>]
let ``longestCommonPrefixLength — identical strings return their length`` () =
    Assert.Equal(3, StreamPathway.longestCommonPrefixLength "abc" "abc")
    Assert.Equal(0, StreamPathway.longestCommonPrefixLength "" "")
    Assert.Equal(1, StreamPathway.longestCommonPrefixLength "a" "a")

[<Fact>]
let ``longestCommonPrefixLength — diverging strings`` () =
    Assert.Equal(2, StreamPathway.longestCommonPrefixLength "abc" "abd")
    Assert.Equal(0, StreamPathway.longestCommonPrefixLength "abc" "xyz")
    Assert.Equal(1, StreamPathway.longestCommonPrefixLength "ab" "ax")

[<Fact>]
let ``longestCommonPrefixLength — one is prefix of the other`` () =
    Assert.Equal(3, StreamPathway.longestCommonPrefixLength "abc" "abcdef")
    Assert.Equal(3, StreamPathway.longestCommonPrefixLength "abcdef" "abc")

// PR #168 — `computeRowSuffixDelta` takes a `BackspacePolicy` as
// its first argument. Most cases (Identical / Initial / Append /
// Replace) are policy-agnostic; the policy only affects the
// Shrink branch. Tests pass `AnnounceDeletedCharacter` (the new
// default) where the policy doesn't matter.

[<Fact>]
let ``computeRowSuffixDelta — identical text returns Silent`` () =
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "abc" "abc"
    Assert.Equal(StreamPathway.Silent, result)

[<Fact>]
let ``computeRowSuffixDelta — empty previous returns Suffix of full current`` () =
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "Hello" ""
    Assert.Equal(StreamPathway.Suffix "Hello", result)

[<Fact>]
let ``computeRowSuffixDelta — append at end returns suffix only`` () =
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "> echo hi" "> echo h"
    Assert.Equal(StreamPathway.Suffix "i", result)

[<Fact>]
let ``computeRowSuffixDelta — multi-character append returns full new suffix`` () =
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "PtySpeakDiagPlain" "PtySpeak"
    Assert.Equal(StreamPathway.Suffix "DiagPlain", result)

[<Fact>]
let ``computeRowSuffixDelta — Shrink under SuppressShrink returns Silent`` () =
    // PR #166's original behaviour. Preserved as opt-in
    // policy under PR #168.
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.SuppressShrink "> echo h" "> echo hi"
    Assert.Equal(StreamPathway.Silent, result)

[<Fact>]
let ``computeRowSuffixDelta — Shrink under AnnounceDeletedCharacter returns Suffix of deleted segment`` () =
    // PR #168's new default. Single-character delete:
    // previous "> echo hi", current "> echo h" → deleted "i"
    // → Suffix "i".
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "> echo h" "> echo hi"
    Assert.Equal(StreamPathway.Suffix "i", result)

[<Fact>]
let ``computeRowSuffixDelta — Shrink under AnnounceDeletedCharacter handles multi-character delete`` () =
    // Ctrl+W or similar produces a longer delete.
    // previous "echo world", current "echo " → deleted "world"
    // → Suffix "world". User hears the whole deleted segment.
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "echo " "echo world"
    Assert.Equal(StreamPathway.Suffix "world", result)

[<Fact>]
let ``computeRowSuffixDelta — Shrink under AnnounceDeletedWord behaves identically to AnnounceDeletedCharacter in v1_1`` () =
    // PR #168 reserves AnnounceDeletedWord for future
    // word-boundary work. v1.1 treats both identically.
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedWord "echo " "echo world"
    Assert.Equal(StreamPathway.Suffix "world", result)

[<Fact>]
let ``computeRowSuffixDelta — replace case returns suffix beyond common prefix`` () =
    // V1 over-reports mid-line edits — documents the
    // limitation as expected behaviour. Cursor-aware diff
    // (v2) would scope the announce to the cursor position.
    let result =
        StreamPathway.computeRowSuffixDelta
            StreamPathway.AnnounceDeletedCharacter "abXc" "abc"
    Assert.Equal(StreamPathway.Suffix "Xc", result)

[<Fact>]
let ``typing at prompt: suffix-diff emits only new character, not full row`` () =
    // The everyday case PR #166 fixes. snap1 has "> echo h",
    // snap2 has "> echo hi". Leading-edge emit on snap2
    // (after debounce window elapsed) should emit "i", not
    // "> echo hi".
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 20 [ "> echo h" ]
    let snap2 = snapshotOf 1 20 [ "> echo hi" ]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    Assert.Equal("> echo h", r1.[0].Payload)
    // Wait past debounce window so the next emit is leading-edge,
    // not accumulate.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500)
            (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    Assert.Equal(SemanticCategory.StreamChunk, r2.[0].Semantic)
    // The fix: payload is just "i", not "> echo hi".
    Assert.Equal("i", r2.[0].Payload)

[<Fact>]
let ``multi-keystroke debounce accumulates suffix at trailing-edge flush`` () =
    // Three keystrokes within the 200ms debounce window.
    // Leading-edge emits the first; the next two accumulate;
    // trailing-edge tick should emit the cumulative suffix.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 20 [ "> echo h" ]
    let snap2 = snapshotOf 1 20 [ "> echo hi" ]
    let snap3 = snapshotOf 1 20 [ "> echo hi " ]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    Assert.Equal("> echo h", r1.[0].Payload)
    // Within debounce — accumulates.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 50)
            (canonicalAt snap2 1L)
    Assert.Empty(r2)
    let r3 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 100)
            (canonicalAt snap3 2L)
    Assert.Empty(r3)
    // Trailing-edge tick after debounce window elapses.
    let tick = StreamPathway.onTimerTick StreamPathway.defaultParameters state (at 250)
    Assert.Equal(1, tick.Length)
    // Cumulative suffix from the last-emitted "> echo h" to
    // current "> echo hi ": just "i ". Note trailing space is
    // preserved here because rowOf doesn't trim — the screen
    // grid contains spaces beyond, but renderRow trims those.
    // The suffix is what's beyond the common prefix in the
    // RENDERED text.
    Assert.Equal("i", tick.[0].Payload.TrimEnd())

[<Fact>]
let ``bulk-change fallback engages when more than 3 rows change`` () =
    // 5 changed rows triggers bulk-change fallback. Payload
    // should be the full ChangedText (verbose), not per-row
    // suffixes.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 5 10 [ ""; ""; ""; ""; "" ]
    let snap2 = snapshotOf 5 10 [ "row0"; "row1"; "row2"; "row3"; "row4" ]
    // First emit primes the cache.
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap1 0L)
    // Second emit — 5 rows change, above threshold of 3.
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500)
            (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    // Bulk-change fallback emits the full ChangedText, which
    // contains every changed row joined by '\n'.
    Assert.Contains("row0", r2.[0].Payload)
    Assert.Contains("row1", r2.[0].Payload)
    Assert.Contains("row4", r2.[0].Payload)

[<Fact>]
let ``backspace case under SuppressShrink policy produces empty payload and no emit`` () =
    // After typing "> echo hi", backspace produces
    // "> echo h" — Shrink case. Under the legacy
    // `SuppressShrink` policy (PR #166's behaviour),
    // pty-speak emits nothing; the user relies on NVDA's
    // keyboard echo for backspace feedback. PR #168 changed
    // the default to `AnnounceDeletedCharacter` (see the
    // companion test below), but the SuppressShrink path is
    // still a valid policy and pinned here.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 20 [ "> echo hi" ]
    let snap2 = snapshotOf 1 20 [ "> echo h" ]
    let r1 =
        StreamPathway.processCanonicalState
            suppressShrinkParameters state (at 0)
            (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    let r2 =
        StreamPathway.processCanonicalState
            suppressShrinkParameters state (at 500)
            (canonicalAt snap2 1L)
    // No emit — empty payload short-circuits.
    Assert.Empty(r2)

[<Fact>]
let ``backspace case under default AnnounceDeletedCharacter policy emits the deleted segment`` () =
    // PR #168 — new default. After backspacing the `i`
    // from `> echo hi`, the StreamPathway emits a
    // StreamChunk with payload `i` (the segment of the
    // previous-emit text beyond the longest-common-prefix
    // with the current text).
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 20 [ "> echo hi" ]
    let snap2 = snapshotOf 1 20 [ "> echo h" ]
    let r1 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap1 0L)
    Assert.Equal(1, r1.Length)
    let r2 =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 500)
            (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    Assert.Equal(SemanticCategory.StreamChunk, r2.[0].Semantic)
    Assert.Equal("i", r2.[0].Payload)

[<Fact>]
let ``first emit on a fresh pathway treats every row as Initial`` () =
    // Empty LastEmittedRowText + first snapshot. Suffix per row
    // = full row text. Concatenated → full snapshot text.
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 20 [ "Hello world" ]
    let r =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0)
            (canonicalAt snap 0L)
    Assert.Equal(1, r.Length)
    Assert.Equal("Hello world", r.[0].Payload)

[<Fact>]
let ``onModeBarrier under Verbose policy flushes pending, then clears LastEmittedRowText`` () =
    // PR #168 — uses `verboseFlushParameters` to opt into
    // the pre-PR-#168 verbose-flush behaviour (the test
    // documents that policy's mechanics — the SummaryOnly
    // / Suppressed default emits nothing on barrier).
    // Mode barriers are discontinuities. The flushed
    // pending diff uses ChangedText (verbose), not
    // suffix-diff. After the barrier, LastEmittedRowText
    // is empty so the next emit treats every row as
    // Initial.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 20 [ "> echo h" ]
    let snap2 = snapshotOf 1 20 [ "> echo hi" ]
    // Leading-edge emit primes the cache.
    let _ =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 0)
            (canonicalAt snap1 0L)
    // Within debounce — accumulate (PendingDiff set).
    let _ =
        StreamPathway.processCanonicalState
            verboseFlushParameters state (at 50)
            (canonicalAt snap2 1L)
    // Mode barrier flushes pending. Verbose payload
    // (ChangedText) — not suffix-diff'd.
    let flushed = StreamPathway.onModeBarrier verboseFlushParameters state (at 100)
    Assert.Equal(1, flushed.Length)
    // Verbose flush carries the full row content.
    Assert.Equal("> echo hi", flushed.[0].Payload)
    // After barrier, cache is cleared.
    Assert.Equal(0, state.LastEmittedRowText.Length)

// ---- Tier 1 parameters (PR #168) ----------------------------------

[<Fact>]
let ``onModeBarrier under SummaryOnly default returns no flush events`` () =
    // PR #168 — new default. Mode barrier with pending diff
    // suppresses the previous-shell flush; the App-layer
    // shell-switch announce + the new shell's startup
    // output carry the context.
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 1 5 [ "hello" ]
    let snap2 = snapshotOf 1 5 [ "world" ]
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 0) (canonicalAt snap1 0L)
    let _ =
        StreamPathway.processCanonicalState
            StreamPathway.defaultParameters state (at 50) (canonicalAt snap2 1L)
    // Default ModeBarrierFlushPolicy = SummaryOnly. Flush
    // suppressed — pending diff is cleared without an emit.
    let result = StreamPathway.onModeBarrier StreamPathway.defaultParameters state (at 100)
    Assert.Empty(result)

[<Fact>]
let ``onModeBarrier under Suppressed policy returns no flush events`` () =
    // PR #168 — `Suppressed` policy is identical to
    // `SummaryOnly` at the StreamPathway level (the
    // difference materialises at the App-layer
    // shell-switch announce).
    let suppressedParameters =
        { StreamPathway.defaultParameters with
            ModeBarrierFlushPolicy = StreamPathway.Suppressed }
    let state = StreamPathway.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ =
        StreamPathway.processCanonicalState
            suppressedParameters state (at 0) (canonicalAt snap 0L)
    let result = StreamPathway.onModeBarrier suppressedParameters state (at 100)
    Assert.Empty(result)

[<Fact>]
let ``BulkChangeThreshold is configurable via Parameters`` () =
    // PR #168 — was a top-level `let private = 3` constant;
    // now a Parameters field. With threshold = 1, even a
    // 2-row diff engages the bulk-change fallback.
    let aggressiveParameters =
        { StreamPathway.defaultParameters with
            BulkChangeThreshold = 1 }
    let state = StreamPathway.createState ()
    let snap1 = snapshotOf 2 5 [ ""; "" ]
    let snap2 = snapshotOf 2 5 [ "row0"; "row1" ]
    let _ =
        StreamPathway.processCanonicalState
            aggressiveParameters state (at 0) (canonicalAt snap1 0L)
    let r2 =
        StreamPathway.processCanonicalState
            aggressiveParameters state (at 500) (canonicalAt snap2 1L)
    Assert.Equal(1, r2.Length)
    // Bulk-change fallback engaged because 2 > threshold 1.
    // Payload is the full ChangedText (verbose) with both
    // rows joined by '\n'.
    Assert.Contains("row0", r2.[0].Payload)
    Assert.Contains("row1", r2.[0].Payload)

[<Fact>]
let ``BulkChangeThreshold = 30 keeps even large diffs on suffix-diff path`` () =
    // PR #168 — opposite extreme. Threshold above the
    // typical row count keeps everything on the suffix
    // path. With a 5-row first emit (Initial = full text),
    // the per-row suffixes still concatenate to the full
    // content, so payload is the same as bulk-change in
    // this Initial case. The test mainly exercises the
    // parametrization wiring.
    let permissiveParameters =
        { StreamPathway.defaultParameters with
            BulkChangeThreshold = 30 }
    let state = StreamPathway.createState ()
    let snap = snapshotOf 5 10 [ "a"; "b"; "c"; "d"; "e" ]
    let r =
        StreamPathway.processCanonicalState
            permissiveParameters state (at 0) (canonicalAt snap 0L)
    Assert.Equal(1, r.Length)
    Assert.Contains("a", r.[0].Payload)
    Assert.Contains("e", r.[0].Payload)
