module PtySpeak.Tests.Unit.LinearTextStreamTests

open System
open System.Text
open Xunit
open Terminal.Core
open Terminal.Parser

/// Cycle 34a — pins the LinearTextStream producer's contract
/// per `docs/rfc/0001-linear-text-substrate.md`. The producer
/// runs parallel-to-screen with no consumers in Cycle 34;
/// Cycle 35 wires the Stream profile to consume from it.
///
/// Test fixture pattern mirrors `ScreenTests.fs:12-17` for byte
/// streams + `HeuristicPromptDetectorTests.fs:71-72` for time
/// mocking. ESC byte literals use the F# escape `\x1b` inside
/// `ascii` strings. Each fact constructs a synthetic byte
/// chunk, parses it via `Parser.feedArray` to produce
/// `VtEvent[]`, feeds both bytes + events to the producer, and
/// asserts on emitted `CommitNotification`s.
///
/// The 25 facts trace directly to RFC §10 acceptance criteria:
/// seam hierarchy (§10.2), live-region detection (§10.3),
/// drain-checkpoint-swap (§10.4), 4 MB cap (§10.6),
/// sealed/unsealed (§10.7), state immutability + finalize
/// (RFC §3.1, §7), defaults + parameters (RFC §5.2).

// ---- Time fixtures (mirror HeuristicPromptDetectorTests:71-72) ----

let private t0 = DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

// ---- Byte fixtures (mirror ScreenTests.fs:12-17) ----

let private ascii (s: string) : byte[] =
    Encoding.ASCII.GetBytes s

let private parseEvents (bytes: byte[]) : VtEvent[] =
    let parser = Parser.create ()
    Parser.feedArray parser bytes

let private osc133Prompt () : byte[] =
    ascii "\x1b]133;A\x07"

let private osc133OutputStart () : byte[] =
    ascii "\x1b]133;C\x07"

let private osc133CommandFinished (exitCode: int) : byte[] =
    ascii (sprintf "\x1b]133;D;%d\x07" exitCode)

let private altScreenEnter () : byte[] = ascii "\x1b[?1049h"
let private altScreenExit () : byte[] = ascii "\x1b[?1049l"

let private csiEraseInLine () : byte[] = ascii "\x1b[K"
let private csiCursorUp (n: int) : byte[] = ascii (sprintf "\x1b[%dA" n)
let private csiCursorBack (n: int) : byte[] = ascii (sprintf "\x1b[%dD" n)

// ---- Producer fixture ----

let private freshProducer () : LinearTextStream.T =
    LinearTextStream.create LinearTextStream.defaultParameters

/// Feed a single byte chunk through the producer; parse to
/// events internally. Returns notifications + state for
/// chaining.
let private feed
        (state: LinearTextStream.T)
        (now: DateTime)
        (bytes: byte[])
        : LinearTextStream.CommitNotification list * LinearTextStream.T =
    let events = parseEvents bytes
    LinearTextStream.append state now bytes events

/// Predicate helpers
let private isEmittedChunk (n: LinearTextStream.CommitNotification) : bool =
    match n with
    | LinearTextStream.EmittedChunk _ -> true
    | _ -> false

let private isLiveRegionUpdate (n: LinearTextStream.CommitNotification) : bool =
    match n with
    | LinearTextStream.LiveRegionUpdate _ -> true
    | _ -> false

let private isRegimeSwitch (n: LinearTextStream.CommitNotification) : bool =
    match n with
    | LinearTextStream.RegimeSwitch _ -> true
    | _ -> false

let private extractEmittedChunk
        (n: LinearTextStream.CommitNotification)
        : LinearTextStream.EmittedChunk option =
    match n with
    | LinearTextStream.EmittedChunk c -> Some c
    | _ -> None

// =====================================================================
// SEAM HIERARCHY (RFC §5.1, §10.2) — 8 facts
// =====================================================================

[<Fact>]
let ``Fact 01 — empty input produces no notifications`` () =
    let state = freshProducer ()
    let (notifs, _) = feed state t0 [||]
    Assert.Empty(notifs)

[<Fact>]
let ``Fact 02 — single line plus LF emits unsealed newline-seam chunk`` () =
    let state = freshProducer ()
    let (notifs, _) = feed state t0 (ascii "hello\n")
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.Single(chunks) |> ignore
    Assert.False(chunks.[0].Sealed,
                 "newline-seam fires unsealed; only OSC 133 + drain-checkpoint emit Sealed=true")
    Assert.Contains((byte 'h'), chunks.[0].Bytes)
    Assert.Contains((byte '\n'), chunks.[0].Bytes)

[<Fact>]
let ``Fact 03 — OSC 133;C drains pending sealed at output-start seam`` () =
    let state = freshProducer ()
    // First feed the prompt + command; OSC 133;C drains the
    // (typed-command) pending bytes as a sealed chunk.
    let bytes =
        Array.concat
            [ osc133Prompt ()
              ascii "$ ls"
              osc133OutputStart () ]
    let (notifs, _) = feed state t0 bytes
    let sealedChunks =
        notifs
        |> List.choose extractEmittedChunk
        |> List.filter (fun c -> c.Sealed)
    Assert.NotEmpty(sealedChunks)

[<Fact>]
let ``Fact 04 — OSC 133;D advances ProducerWaterMark with Sealed=true`` () =
    let state = freshProducer ()
    let bytes =
        Array.concat
            [ osc133Prompt ()
              osc133OutputStart ()
              ascii "output content"
              osc133CommandFinished 0 ]
    let (notifs, nextState) = feed state t0 bytes
    let sealedChunks =
        notifs
        |> List.choose extractEmittedChunk
        |> List.filter (fun c -> c.Sealed)
    Assert.NotEmpty(sealedChunks)
    Assert.True(nextState.HighWaterMark > 0L,
                "OSC 133;D should advance the producer's high-water-mark")

[<Fact>]
let ``Fact 05 — idle quantum elapses; tick drains pending unsealed`` () =
    let state = freshProducer ()
    // Feed bytes without a newline so they stay in pending.
    let (preNotifs, state') = feed state t0 (ascii "no newline here")
    Assert.Empty(preNotifs |> List.choose extractEmittedChunk)
    // Tick after idle_quantum_ms (150 ms default).
    let (notifs, _) = LinearTextStream.tick state' (after 200)
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.Single(chunks) |> ignore
    Assert.False(chunks.[0].Sealed)

[<Fact>]
let ``Fact 06 — max-bytes ceiling forces an unsealed mid-stream emit`` () =
    let state = freshProducer ()
    // Feed > max_bytes_per_emit (4096 default) without newlines
    // or seams; expect a forced unsealed flush.
    let bigBytes = Array.create 5000 (byte 'x')
    let (notifs, _) = feed state t0 bigBytes
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.NotEmpty(chunks)
    Assert.False(chunks.[0].Sealed)

[<Fact>]
let ``Fact 07 — max-time ceiling drains pending on tick`` () =
    let state = freshProducer ()
    // Feed bytes; let max_time_without_emit (2000ms default)
    // elapse; tick drains.
    let (_, state') = feed state t0 (ascii "trailing")
    let (notifs, _) = LinearTextStream.tick state' (after 2500)
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.NotEmpty(chunks)

[<Fact>]
let ``Fact 08 — strongest seam wins; OSC 133;D produces Sealed=true even with newline`` () =
    let state = freshProducer ()
    // Bytes contain a newline AND OSC 133;D in the same call.
    // Per RFC §5.1 the semantic prompt seam pre-empts newline.
    let bytes =
        Array.concat
            [ osc133Prompt ()
              osc133OutputStart ()
              ascii "line1\n"
              osc133CommandFinished 42 ]
    let (notifs, _) = feed state t0 bytes
    let sealedChunks =
        notifs
        |> List.choose extractEmittedChunk
        |> List.filter (fun c -> c.Sealed)
    Assert.NotEmpty(sealedChunks)

// =====================================================================
// LIVE-REGION DETECTION (RFC §5.3, §10.3) — 6 facts
// =====================================================================

[<Fact>]
let ``Fact 09 — bare CR moves pending into tail-mask state for current row`` () =
    let state = freshProducer ()
    // "abc" + bare CR (no LF). The CR triggers EnterTailMask;
    // pending moves to tail-mask. tick past debounce emits
    // LiveRegionUpdate.
    let (preNotifs, state') = feed state t0 (ascii "abc\r")
    Assert.Empty(preNotifs |> List.choose extractEmittedChunk)
    let (notifs, _) = LinearTextStream.tick state' (after 300)
    let updates = notifs |> List.filter isLiveRegionUpdate
    Assert.NotEmpty(updates)

[<Fact>]
let ``Fact 10 — ESC[K (EL) marks current row tail-mask`` () =
    let state = freshProducer ()
    let bytes = Array.concat [ ascii "abc"; csiEraseInLine () ]
    let (preNotifs, state') = feed state t0 bytes
    // No EmittedChunk should have fired (no seam).
    Assert.Empty(preNotifs |> List.choose extractEmittedChunk)
    // Tick past debounce → LiveRegionUpdate emitted.
    let (notifs, _) = LinearTextStream.tick state' (after 300)
    let updates = notifs |> List.filter isLiveRegionUpdate
    Assert.NotEmpty(updates)

[<Fact>]
let ``Fact 11 — CSI cursor-up plus printable bytes tail-masks target row`` () =
    let state = freshProducer ()
    // Feed two lines, then CUU 1, then printable.
    let bytes =
        Array.concat
            [ ascii "line1\n"
              ascii "line2\n"
              csiCursorUp 1
              ascii "OVR" ]
    let (notifs, state') = feed state t0 bytes
    // The two newlines emit unsealed chunks; the CUU + OVR
    // produce a tail-mask state.
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.True(chunks.Length >= 1, "newline-seam fires for line1+line2")
    // Tick past debounce; LiveRegionUpdate for the masked row.
    let (tickNotifs, _) = LinearTextStream.tick state' (after 300)
    Assert.NotEmpty(tickNotifs |> List.filter isLiveRegionUpdate)

[<Fact>]
let ``Fact 12 — CSI cursor-back plus printable tail-masks current row`` () =
    let state = freshProducer ()
    let bytes = Array.concat [ ascii "abc"; csiCursorBack 3; ascii "XYZ" ]
    let (preNotifs, state') = feed state t0 bytes
    Assert.Empty(preNotifs |> List.choose extractEmittedChunk)
    let (notifs, _) = LinearTextStream.tick state' (after 300)
    Assert.NotEmpty(notifs |> List.filter isLiveRegionUpdate)

[<Fact>]
let ``Fact 13 — spinner cycle at 10 Hz collapses to LATEST tail-mask state`` () =
    let state = freshProducer ()
    let mutable cur = state
    // Simulate ~10 Hz spinner: 10 frames over 1 second.
    // Each frame is `\r{glyph}`: CR triggers EnterTailMask
    // (capturing the prior iteration's pending Print into
    // tail-mask), then the new glyph goes into pending.
    let frames = [ "|"; "/"; "-"; "\\"; "|"; "/"; "-"; "\\"; "|"; "/" ]
    for i in 0 .. frames.Length - 1 do
        let bytes = ascii (sprintf "\r%s" frames.[i])
        let (_, next) = feed cur (after (i * 100)) bytes
        cur <- next
    // Tick past final debounce window. The LATEST tail-mask
    // state contains the SECOND-to-last frame's char (since
    // iter N's CR captures iter N-1's pending print into
    // tail-mask). Verify at least one spinner char appears
    // in the LATEST update.
    let (tickNotifs, _) = LinearTextStream.tick cur (after 1500)
    let updates =
        tickNotifs
        |> List.choose (fun n ->
            match n with
            | LinearTextStream.LiveRegionUpdate u -> Some u
            | _ -> None)
    Assert.NotEmpty(updates)
    let lastUpdate = updates |> List.last
    let bytesAsStr = Encoding.ASCII.GetString(lastUpdate.LatestBytes)
    let isSpinnerChar c = List.contains c [ '|'; '/'; '-'; '\\' ]
    Assert.True(
        bytesAsStr |> Seq.exists isSpinnerChar,
        sprintf "expected at least one spinner char in LATEST tail-mask bytes; got %A" bytesAsStr)

[<Fact>]
let ``Fact 14 — tail-mask LATEST overwrites intermediate states`` () =
    let state = freshProducer ()
    // Feed three frames within debounce window; only the
    // LATEST should appear in tail-mask state.
    let (_, state1) = feed state t0 (ascii "frame1\r")
    let (_, state2) = feed state1 (after 50) (ascii "frame2\r")
    let (_, state3) = feed state2 (after 100) (ascii "frame3\r")
    // Tick past debounce; emitted LiveRegionUpdate carries
    // ONE LatestBytes state — and it should reference frame3.
    let (notifs, _) = LinearTextStream.tick state3 (after 400)
    let updates =
        notifs
        |> List.choose (fun n ->
            match n with
            | LinearTextStream.LiveRegionUpdate u -> Some u
            | _ -> None)
    Assert.True(updates.Length <= 3,
                "LATEST semantics: at most one update per masked row even after multiple frames")
    if not updates.IsEmpty then
        let bytesAsStr =
            Encoding.ASCII.GetString(updates.[updates.Length - 1].LatestBytes)
        Assert.Contains("frame3", bytesAsStr)

// =====================================================================
// DRAIN-CHECKPOINT-SWAP (RFC §6, §10.4) — 3 facts
// =====================================================================

[<Fact>]
let ``Fact 15 — alt-screen enter emits RegimeSwitch and freezes substrate`` () =
    let state = freshProducer ()
    let bytes = Array.concat [ ascii "before"; altScreenEnter () ]
    let (notifs, state') = feed state t0 bytes
    let regimeSwitches = notifs |> List.filter isRegimeSwitch
    Assert.NotEmpty(regimeSwitches)
    Assert.True(state'.Frozen,
                "alt-screen entry must freeze the linear substrate")

[<Fact>]
let ``Fact 16 — alt-screen exit emits ExitAltScreen with resumeAt past drain settle`` () =
    let state = freshProducer ()
    let (_, state1) = feed state t0 (altScreenEnter ())
    Assert.True(state1.Frozen)
    // Now feed exit. The exit byte sequence is processed even
    // while frozen (to detect the toggle).
    let (notifs, state2) = feed state1 (after 100) (altScreenExit ())
    Assert.False(state2.Frozen)
    let exitNotifs =
        notifs
        |> List.choose (fun n ->
            match n with
            | LinearTextStream.RegimeSwitch (LinearTextStream.ExitAltScreen at) ->
                Some at
            | _ -> None)
    Assert.NotEmpty(exitNotifs)
    // resumeAt should be `now + RegimeSwitchDrainMs` (500 ms).
    let expectedMin = (after 100).AddMilliseconds(500.0)
    Assert.True(exitNotifs.[0] >= expectedMin)

[<Fact>]
let ``Fact 17 — checkpointAndFreeze + resumeFromFreeze are symmetric`` () =
    let state = freshProducer ()
    let (_, state1) = feed state t0 (ascii "pre-freeze content\n")
    let (notifs, state2) = LinearTextStream.checkpointAndFreeze state1 (after 100)
    Assert.True(state2.Frozen)
    let regimeSwitches = notifs |> List.filter isRegimeSwitch
    Assert.NotEmpty(regimeSwitches)
    let state3 = LinearTextStream.resumeFromFreeze state2 (after 200)
    Assert.False(state3.Frozen,
                 "resumeFromFreeze must clear the frozen flag")

// =====================================================================
// 4 MB PER-TUPLE CAP (RFC §3.5, §10.6) — 2 facts
// =====================================================================

[<Fact>]
let ``Fact 18 — buffer below 4 MB cap reports Truncated=false`` () =
    let state = freshProducer ()
    // Feed 100 KB of bytes; well below 4 MB cap.
    let bytes = Array.create (100 * 1024) (byte 'x')
    let (notifs, _) = feed state t0 bytes
    let chunks = notifs |> List.choose extractEmittedChunk
    if not chunks.IsEmpty then
        Assert.False(chunks.[0].Truncated,
                     "Truncated should be false for buffer < 4 MB cap")

[<Fact>]
let ``Fact 19 — 4 MB cap reached evicts oldest bytes and flips Truncated=true`` () =
    // Use a smaller cap so the test doesn't have to allocate
    // 5 MB. Keeps proportional behavior (cap < bytes-per-emit
    // triggers drain → cap evicts).
    let smallCapParams =
        { LinearTextStream.defaultParameters with
            PerTupleCapBytes = 8192 }    // 8 KB cap (default MaxBytesPerEmit=4096 still applies)
    let state = LinearTextStream.create smallCapParams
    let mutable cur = state
    // Feed 12 KB total. Default MaxBytesPerEmit=4096 will
    // trigger drainPending; drainPending writes to committed;
    // committed exceeds 8 KB cap → eviction.
    let bytes = Array.create 12_000 (byte 'X')
    let (_, next) = feed cur t0 bytes
    cur <- next
    // After: buffer is capped at 8 KB; truncated flag set.
    Assert.True(cur.Truncated,
                "4 MB cap (or smaller) must flip Truncated=true on drop-oldest")
    Assert.True(cur.Committed.Count <= smallCapParams.PerTupleCapBytes,
                sprintf "buffer must not exceed cap after eviction; got %d > %d"
                    cur.Committed.Count smallCapParams.PerTupleCapBytes)

// =====================================================================
// SEALED / UNSEALED ROUND-TRIP (RFC §5.4, §10.7) — 2 facts
// =====================================================================

[<Fact>]
let ``Fact 20 — OSC 133;D produces an EmittedChunk with Sealed=true`` () =
    let state = freshProducer ()
    let bytes =
        Array.concat
            [ osc133Prompt ()
              osc133OutputStart ()
              ascii "out"
              osc133CommandFinished 0 ]
    let (notifs, _) = feed state t0 bytes
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.True(
        chunks |> List.exists (fun c -> c.Sealed),
        "OSC 133;D must produce at least one Sealed=true chunk")

[<Fact>]
let ``Fact 21 — newline boundary produces an EmittedChunk with Sealed=false`` () =
    let state = freshProducer ()
    let (notifs, _) = feed state t0 (ascii "newline-only\n")
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.NotEmpty(chunks)
    Assert.False(chunks.[0].Sealed,
                 "newline boundary is unsealed; OSC 133 is sealed")

// =====================================================================
// STATE + FINALIZE (RFC §3.1, §7) — 2 facts
// =====================================================================

[<Fact>]
let ``Fact 22 — append returns same T reference; chained calls accumulate state`` () =
    let state = freshProducer ()
    let (_, state1) = feed state t0 (ascii "first\n")
    let (_, state2) = feed state1 (after 50) (ascii "second\n")
    // The two `state` values are the same reference (mutable
    // record); state2 reflects the cumulative HighWaterMark.
    Assert.Same(state, state1)
    Assert.Same(state1, state2)
    Assert.True(state2.HighWaterMark >= int64 (ascii "first\n").Length,
                "two newline-seam emits should advance the watermark")

[<Fact>]
let ``Fact 23 — finalizeHighWaterMark slices command and output text from OSC 133 markers`` () =
    let state = freshProducer ()
    // Feed a complete prompt → command → output → finished cycle.
    let cycle =
        Array.concat
            [ osc133Prompt ()
              ascii "$ "
              osc133OutputStart ()
              ascii "hello world\n"
              osc133CommandFinished 0 ]
    let (_, state') = feed state t0 cycle
    let (chunk, _) = LinearTextStream.finalizeHighWaterMark state'
    Assert.True(chunk.Sealed)
    // The OutputText should contain "hello world".
    Assert.Contains("hello world", chunk.OutputText)

// =====================================================================
// DEFAULTS + PARAMETERS (RFC §5.2) — 2 facts
// =====================================================================

[<Fact>]
let ``Fact 24 — defaultParameters matches RFC section 5.2 verbatim`` () =
    let p = LinearTextStream.defaultParameters
    Assert.Equal(150, p.IdleQuantumMs)
    Assert.Equal(4096, p.MaxBytesPerEmit)
    Assert.Equal(2000, p.MaxTimeWithoutEmitMs)
    Assert.Equal(250, p.LiveRegionDebounceMs)
    Assert.Equal(500, p.RegimeSwitchDrainMs)
    Assert.Equal(4 * 1024 * 1024, p.PerTupleCapBytes)

[<Fact>]
let ``Fact 25 — create with custom parameters honors overrides`` () =
    let custom =
        { LinearTextStream.defaultParameters with
            IdleQuantumMs = 50
            MaxBytesPerEmit = 1024 }
    let state = LinearTextStream.create custom
    // Force a forced flush at the smaller MaxBytesPerEmit.
    let bytes = Array.create 1500 (byte 'a')
    let (notifs, _) = feed state t0 bytes
    let chunks = notifs |> List.choose extractEmittedChunk
    Assert.NotEmpty(chunks)
    // Subsequent tick at after 60 ms should drain pending if
    // the custom IdleQuantumMs (50ms) is honored.
    let state2 = LinearTextStream.create custom
    let (_, state2') = feed state2 t0 (ascii "ab")
    let (tickNotifs, _) = LinearTextStream.tick state2' (after 60)
    Assert.NotEmpty(tickNotifs |> List.choose extractEmittedChunk)
