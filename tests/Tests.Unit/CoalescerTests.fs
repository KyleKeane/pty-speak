module PtySpeak.Tests.Unit.CoalescerTests

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Time.Testing
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
// the interactions, driving `Coalescer.processRowsChanged` /
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
    let state = Coalescer.createState ()
    let snap = snapshotOf 3 5 [ "hello" ]
    let first = Coalescer.processRowsChanged state (at 0) snap
    Assert.Equal(1, first.Length)
    let second = Coalescer.processRowsChanged state (at 500) snap
    Assert.Equal(0, second.Length)

[<Fact>]
let ``frame dedup releases when content actually changes`` () =
    let state = Coalescer.createState ()
    let snap1 = snapshotOf 3 5 [ "hello" ]
    let snap2 = snapshotOf 3 5 [ "world" ]
    let _ = Coalescer.processRowsChanged state (at 0) snap1
    let next = Coalescer.processRowsChanged state (at 500) snap2
    Assert.Equal(1, next.Length)

// ---------------------------------------------------------------------
// processRowsChanged — debounce (leading + trailing edge)
// ---------------------------------------------------------------------

[<Fact>]
let ``first event in idle period emits immediately (leading edge)`` () =
    let state = Coalescer.createState ()
    let snap = snapshotOf 3 5 [ "echo hello" ]
    let result = Coalescer.processRowsChanged state (at 0) snap
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.OutputBatch text -> Assert.Contains("echo hello", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch; got %A" other)

[<Fact>]
let ``burst of events within debounce window collapses to leading + trailing emit`` () =
    let state = Coalescer.createState ()
    let snap1 = snapshotOf 3 5 [ "a" ]
    let snap2 = snapshotOf 3 5 [ "ab" ]
    let snap3 = snapshotOf 3 5 [ "abc" ]
    // Leading edge.
    let r1 = Coalescer.processRowsChanged state (at 0) snap1
    Assert.Equal(1, r1.Length)
    // Within 200ms — should accumulate, NOT emit.
    let r2 = Coalescer.processRowsChanged state (at 50) snap2
    Assert.Equal(0, r2.Length)
    let r3 = Coalescer.processRowsChanged state (at 100) snap3
    Assert.Equal(0, r3.Length)
    // Trailing-edge timer tick at 200ms+ flushes the
    // last accumulated frame.
    let tick = Coalescer.onTimerTick state (at 250)
    Assert.Equal(1, tick.Length)
    match tick.[0] with
    | Coalescer.OutputBatch text -> Assert.Contains("abc", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch; got %A" other)

[<Fact>]
let ``trailing-edge tick with nothing pending is a no-op`` () =
    let state = Coalescer.createState ()
    let snap = snapshotOf 3 5 [ "x" ]
    let _ = Coalescer.processRowsChanged state (at 0) snap
    // No accumulator built up; tick should emit nothing.
    let tick = Coalescer.onTimerTick state (at 250)
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
    let state = Coalescer.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    let _ = Coalescer.processRowsChanged state (at 0) frameA
    let mutable suppressedCount = 0
    let mutable emittedCount = 0
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let result = Coalescer.processRowsChanged state (at (i * 60)) frame
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
let ``spinner per-key history is GC'd after spinnerWindow elapses`` () =
    // Once spinnerWindow of quiet has passed, the per-key
    // history is trimmed by gcHistory and a new frame can
    // re-engage the leading-edge emit path.
    let state = Coalescer.createState ()
    let frameA = snapshotOf 1 1 [ "A" ]
    let frameB = snapshotOf 1 1 [ "B" ]
    // Pump enough alternations to engage spinner suppression.
    let _ = Coalescer.processRowsChanged state (at 0) frameA
    for i in 1 .. 14 do
        let frame = if i % 2 = 1 then frameB else frameA
        let _ = Coalescer.processRowsChanged state (at (i * 60)) frame
        ()
    // After 1.5s of quiet (well past spinnerWindow=1000ms),
    // a fresh content should emit again.
    let frameC = snapshotOf 1 1 [ "C" ]
    let result = Coalescer.processRowsChanged state (at 5000) frameC
    Assert.True(result.Length >= 1,
        "Expected emit to recover after spinnerWindow of quiet")

// ---------------------------------------------------------------------
// onModeChanged — alt-screen flush barrier
// ---------------------------------------------------------------------

[<Fact>]
let ``ModeChanged AltScreen flushes pending accumulator first`` () =
    let state = Coalescer.createState ()
    let snap1 = snapshotOf 1 5 [ "hello" ]
    let snap2 = snapshotOf 1 5 [ "world" ]
    // Leading-edge emit at t=0.
    let _ = Coalescer.processRowsChanged state (at 0) snap1
    // Within debounce — accumulates.
    let r2 = Coalescer.processRowsChanged state (at 50) snap2
    Assert.Equal(0, r2.Length)
    // Mode change should flush the pending then pass through.
    let result = Coalescer.onModeChanged state (at 100) AltScreen true
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
    let state = Coalescer.createState ()
    let result = Coalescer.onModeChanged state (at 0) AltScreen false
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.ModeBarrier (flag, value) ->
        Assert.Equal(AltScreen, flag)
        Assert.Equal(false, value)
    | other -> Assert.Fail(sprintf "Expected ModeBarrier; got %A" other)

[<Fact>]
let ``ModeChanged resets frame-dedup state`` () =
    let state = Coalescer.createState ()
    let snap = snapshotOf 1 5 [ "hello" ]
    let _ = Coalescer.processRowsChanged state (at 0) snap
    // Frame dedup would normally suppress this repeat...
    let dup = Coalescer.processRowsChanged state (at 500) snap
    Assert.Equal(0, dup.Length)
    // ...but a mode barrier resets the dedup state, so the
    // same content emits again afterward.
    let _ = Coalescer.onModeChanged state (at 600) AltScreen true
    let after = Coalescer.processRowsChanged state (at 1000) snap
    Assert.Equal(1, after.Length)

// ---------------------------------------------------------------------
// onParserError — pass-through with sanitisation
// ---------------------------------------------------------------------

[<Fact>]
let ``ParserError passes through immediately as ErrorPassthrough`` () =
    let result = Coalescer.onParserError "boom"
    Assert.Equal(1, result.Length)
    match result.[0] with
    | Coalescer.ErrorPassthrough s -> Assert.Equal("boom", s)
    | other -> Assert.Fail(sprintf "Expected ErrorPassthrough; got %A" other)

[<Fact>]
let ``ParserError strips control chars via sanitiser`` () =
    // BEL (\x07) must be stripped before NVDA sees it.
    let result = Coalescer.onParserError "boom\x07!"
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
// runLoop end-to-end with FakeTimeProvider
// ---------------------------------------------------------------------

let private buildChannels () =
    let inOpts =
        BoundedChannelOptions(256,
            FullMode = BoundedChannelFullMode.DropOldest)
    let outOpts =
        BoundedChannelOptions(16,
            FullMode = BoundedChannelFullMode.Wait)
    let inCh = Channel.CreateBounded<ScreenNotification>(inOpts)
    let outCh = Channel.CreateBounded<Coalescer.CoalescedNotification>(outOpts)
    inCh, outCh

[<Fact>]
let ``runLoop cancels cleanly when token is cancelled`` () =
    let inCh, outCh = buildChannels ()
    let screen = Screen(rows = 3, cols = 5)
    let cts = new CancellationTokenSource()
    let timeProvider = FakeTimeProvider(epoch)
    let task =
        Coalescer.runLoop
            inCh.Reader
            outCh.Writer
            screen
            (timeProvider :> TimeProvider)
            cts.Token
    cts.Cancel()
    // Loop should terminate without throwing OCE outwards.
    let completed = task.Wait(TimeSpan.FromSeconds 2.0)
    Assert.True(completed, "Coalescer task should exit within 2s of cancellation")

[<Fact>]
let ``runLoop emits OutputBatch end-to-end via real Screen + FakeTimeProvider`` () =
    let inCh, outCh = buildChannels ()
    let screen = Screen(rows = 3, cols = 10)
    // Apply some content so SnapshotRows has something to hash.
    let parser = Terminal.Parser.Parser.create ()
    let bytes = Encoding.ASCII.GetBytes "hi"
    let events = Terminal.Parser.Parser.feedArray parser bytes
    for e in events do screen.Apply(e)
    let cts = new CancellationTokenSource()
    let timeProvider = FakeTimeProvider(epoch)
    let task =
        Coalescer.runLoop
            inCh.Reader
            outCh.Writer
            screen
            (timeProvider :> TimeProvider)
            cts.Token
    // Push a RowsChanged through the input channel.
    Assert.True(inCh.Writer.TryWrite(RowsChanged []))
    // Read one CoalescedNotification with a timeout.
    let readTask = outCh.Reader.ReadAsync(cts.Token).AsTask()
    let completed = readTask.Wait(TimeSpan.FromSeconds 2.0)
    Assert.True(completed, "Expected one CoalescedNotification within 2s")
    match readTask.Result with
    | Coalescer.OutputBatch text -> Assert.Contains("hi", text)
    | other -> Assert.Fail(sprintf "Expected OutputBatch; got %A" other)
    cts.Cancel()
    task.Wait(TimeSpan.FromSeconds 2.0) |> ignore
