namespace Terminal.Core

open System

/// Cycle 34a — the linear-text producer per
/// `docs/rfc/0001-linear-text-substrate.md`. Maintains an
/// append-only byte buffer of parser-emitted output, committed
/// at seam-hierarchy boundaries (RFC §5.1: semantic prompt seam
/// > newline > idle quantum > max-bytes > max-time). Live-region
/// overwrite-class bytes (`\r` without `\n`, `ESC[K`, `ESC[A`,
/// `ESC[<n>D`) land in a tail mask with LATEST semantics; only
/// the most recent state of each tail-masked row commits.
///
/// **Cycle 34a scope:** module + tests only. The producer runs
/// **parallel-to-screen** with no consumers — emitted
/// `CommitNotification`s are constructed and returned to the
/// caller, but no downstream code subscribes. Cycle 34b wires
/// the producer at `Program.fs:108` (parser-emit edge) and adds
/// the `Ctrl+Shift+D` diagnostic-bundle integration; Cycle 35
/// flips the Stream profile to consume from the producer.
///
/// **Threading:** the producer's `append` / `tick` / `finalize`
/// functions run on the PathwayPump worker thread (single-
/// threaded for notification consumption per
/// `HeuristicPromptDetector.fs:79-82`). State is held in a
/// mutable record holding `ResizeArray<byte>` buffers for the
/// committed and pending sections; the public `T` type is the
/// state record. Callers chain state through the
/// `(notifications, T)` return tuple, mirroring the existing
/// detector idiom.
///
/// **Why bytes AND events?** `append` takes both `bytes: byte[]`
/// (the raw PTY chunk; the producer's substrate-of-truth) and
/// `events: VtEvent[]` (the parser's already-computed
/// classification; used for seam + live-region decisions).
/// Bytes drive the buffer; events drive the semantics. Both
/// originate from the same `Parser.feedArray parser chunk`
/// call at `Program.fs:108` (Cycle 34b), so passing both is
/// zero additional work for the caller.
module LinearTextStream =

    // --------------------------------------------------------
    // Public types
    // --------------------------------------------------------

    /// Per-row tail-mask classification per RFC §5.3. `Append`
    /// rows extend the canonical substrate; `TailMask` rows hold
    /// only the latest state until a seam crossing or until
    /// `live_region_debounce_ms` elapses; `Frozen` rows are
    /// excluded entirely (alt-screen regime active).
    type TailMaskClass =
        | Append
        | TailMask
        | Frozen

    /// Tunable parameters per RFC §5.2 cadence-parameters table.
    /// Defaults match the table verbatim; future micro-cycle
    /// externalises to TOML `[coalescer]` per the Cycle 32a
    /// `[profile.selection]` precedent.
    type Parameters =
        { /// Milliseconds without bytes before idle-quantum seam
          /// fires. Default 150 ms.
          IdleQuantumMs: int
          /// Buffered-bytes ceiling before a forced max-bytes
          /// emit. Default 4096.
          MaxBytesPerEmit: int
          /// Time since last emit before a forced max-time emit.
          /// Default 2000 ms.
          MaxTimeWithoutEmitMs: int
          /// Debounce window for tail-mask updates (collapses
          /// 10 Hz spinners to ~4 Hz). Default 250 ms.
          LiveRegionDebounceMs: int
          /// Settle time after alt-screen exit before resuming
          /// linear-substrate commits. Default 500 ms.
          RegimeSwitchDrainMs: int
          /// Per-tuple buffer cap before drop-oldest semantics
          /// engage. Default 4 MB (4 * 1024 * 1024 bytes).
          PerTupleCapBytes: int }

    /// Default parameters per RFC §5.2 lift from
    /// `docs/research/emission-paradigms.md` §3.C.
    let defaultParameters: Parameters =
        { IdleQuantumMs = 150
          MaxBytesPerEmit = 4096
          MaxTimeWithoutEmitMs = 2000
          LiveRegionDebounceMs = 250
          RegimeSwitchDrainMs = 500
          PerTupleCapBytes = 4 * 1024 * 1024 }

    /// A finalized chunk produced when the high-water-mark
    /// advances at a seam crossing. `Sealed=true` means this is
    /// the authoritative final state of the byte range
    /// (semantic prompt seam, max-time at logical boundary,
    /// alt-screen drain checkpoint). `Sealed=false` means more
    /// bytes may amend or overwrite this region (idle quantum
    /// emit; max-bytes mid-stream).
    type EmittedChunk =
        { Bytes: byte[]
          Sealed: bool
          Truncated: bool
          ProducerWaterMark: int64
          EmittedAt: DateTime }

    /// Live-region tail-mask LATEST state. Spinners, progress
    /// bars, `--More--` prompts, tab-completion overwrites
    /// produce these. Carries the most recent state of the
    /// masked row; intermediate states are dropped per Reactor
    /// `BufferOverflowStrategy.LATEST`.
    type LiveRegionUpdate =
        { LatestBytes: byte[]
          UpdatedAt: DateTime }

    /// Regime transitions per RFC §6 drain-checkpoint-swap.
    /// `EnterAltScreen` fires on `ESC[?1049h`; the linear
    /// substrate freezes. `ExitAltScreen` fires on
    /// `ESC[?1049l`; `ResumeAt` is `now + RegimeSwitchDrainMs`,
    /// after which new bytes resume extending the substrate.
    type RegimeSwitchKind =
        | EnterAltScreen
        | ExitAltScreen of resumeAt: DateTime

    /// Notification emitted by `append` / `tick` /
    /// `checkpointAndFreeze`. Multiple may emerge per call
    /// (e.g., a prompt seam inside a max-bytes-flushed chunk).
    type CommitNotification =
        | EmittedChunk of EmittedChunk
        | LiveRegionUpdate of LiveRegionUpdate
        | RegimeSwitch of RegimeSwitchKind

    /// Finalized SessionTuple-shaped chunk produced by
    /// `finalizeHighWaterMark` at prompt-boundary fire.
    /// Replaces `SessionModel.fs:338-375` `extractContent`'s
    /// row-walk in Cycle 35; in Cycle 34a this function exists
    /// but is unwired runtime-side.
    type FinalizedChunk =
        { /// Bytes between PromptStart and CommandStart
          /// (the user's typed command line).
          CommandText: string
          /// Bytes between CommandStart and CommandFinished
          /// (the command's stdout / stderr).
          OutputText: string
          /// True if the 4 MB per-tuple cap was hit during
          /// accumulation. Consumers that announce can prefix
          /// "[output truncated]".
          Truncated: bool
          /// Always true at finalize time (the seam IS the
          /// finalize trigger).
          Sealed: bool
          /// Producer's monotonic watermark for diagnostic
          /// correlation.
          ProducerWaterMark: int64 }

    // --------------------------------------------------------
    // Internal state record
    // --------------------------------------------------------

    /// Per-row tail-mask state. Holds the latest bytes for each
    /// tail-masked row; `Append` rows are not in the map.
    /// Logical row indices (producer-internal); not the screen
    /// grid's row count.
    type private TailMaskState = Map<int, byte[]>

    /// Whether a particular tick should emit a LiveRegionUpdate
    /// for the current tail-mask state. Tracked per-row to
    /// honor `LiveRegionDebounceMs` per RFC §5.2.
    type private TailMaskTimestamps = Map<int, DateTime>

    /// Mutable producer state. The `T` opaque type wraps this.
    /// Buffers are `ResizeArray<byte>` for O(1) append + O(N)
    /// drop-oldest; remaining fields use F# `mutable` for
    /// in-place updates. The state record itself is held by
    /// the caller; `append` / `tick` mutate the record's
    /// fields and return the same reference + accumulated
    /// notifications.
    [<Sealed>]
    type T internal (parameters: Parameters) =
        let committed = ResizeArray<byte>()
        let pending = ResizeArray<byte>()
        let mutable tailMask : TailMaskState = Map.empty
        let mutable tailMaskTimestamps : TailMaskTimestamps = Map.empty
        let mutable currentRow = 0
        let mutable highWaterMark = 0L
        let mutable lastEmittedAt = DateTime.MinValue
        let mutable lastByteAt = DateTime.MinValue
        let mutable frozen = false
        let mutable truncated = false
        /// Position in the committed buffer marking the start
        /// of the current command's output (set by OSC 133;C,
        /// cleared by OSC 133;D).
        let mutable outputStartOffset = 0L
        /// Position in the committed buffer marking the start
        /// of the current command (set by OSC 133;A, cleared
        /// by OSC 133;C).
        let mutable promptStartOffset = 0L

        member internal _.Committed = committed
        member internal _.Pending = pending
        member internal _.Parameters = parameters
        member internal _.HighWaterMark
            with get () = highWaterMark
            and set v = highWaterMark <- v
        member internal _.LastEmittedAt
            with get () = lastEmittedAt
            and set v = lastEmittedAt <- v
        member internal _.LastByteAt
            with get () = lastByteAt
            and set v = lastByteAt <- v
        member internal _.Frozen
            with get () = frozen
            and set v = frozen <- v
        member internal _.Truncated
            with get () = truncated
            and set v = truncated <- v
        member internal _.CurrentRow
            with get () = currentRow
            and set v = currentRow <- v
        member internal _.TailMask
            with get () = tailMask
            and set v = tailMask <- v
        member internal _.TailMaskTimestamps
            with get () = tailMaskTimestamps
            and set v = tailMaskTimestamps <- v
        member internal _.OutputStartOffset
            with get () = outputStartOffset
            and set v = outputStartOffset <- v
        member internal _.PromptStartOffset
            with get () = promptStartOffset
            and set v = promptStartOffset <- v

    // --------------------------------------------------------
    // Construction
    // --------------------------------------------------------

    /// Create a fresh producer with the given parameters.
    /// Caller binds the result to a `mutable` cell at the
    /// composition root and chains `append` / `tick` calls.
    let create (parameters: Parameters) : T = T(parameters)

    // --------------------------------------------------------
    // OSC 133 + CSI seam detection
    // --------------------------------------------------------

    /// OSC 133 sub-command per FinalTerm / iTerm2 spec.
    type private Osc133Cmd =
        | PromptStart           // A
        | CommandInputStart     // B (prompt-end / command-input)
        | CommandOutputStart    // C
        | CommandFinished of exitCode: int option  // D[;<exit>]
        | NotOsc133

    /// Parse an OscDispatch's parms array as OSC 133. Returns
    /// `NotOsc133` for non-133 OSC sequences (passthrough).
    let private classifyOsc133 (parms: byte[][]) : Osc133Cmd =
        if parms.Length < 2 then NotOsc133
        else
            let category =
                System.Text.Encoding.ASCII.GetString(parms.[0])
            if category <> "133" then NotOsc133
            else
                let cmdBytes = parms.[1]
                if cmdBytes.Length = 0 then NotOsc133
                else
                    match char cmdBytes.[0] with
                    | 'A' -> PromptStart
                    | 'B' -> CommandInputStart
                    | 'C' -> CommandOutputStart
                    | 'D' ->
                        if parms.Length >= 3 then
                            let exitStr =
                                System.Text.Encoding.ASCII.GetString(parms.[2])
                            match Int32.TryParse(exitStr) with
                            | true, code -> CommandFinished (Some code)
                            | false, _ -> CommandFinished None
                        else
                            CommandFinished None
                    | _ -> NotOsc133

    /// Alt-screen toggle detection from a CsiDispatch.
    /// `ESC[?1049h` → enter; `ESC[?1049l` → exit. Also handles
    /// the legacy 47h / 1047h DECSET codes.
    let private classifyAltScreenToggle
            (parms: int[])
            (intermediates: byte[])
            (finalByte: char)
            (priv: char option)
            : RegimeSwitchKind option =
        match priv, intermediates.Length with
        | Some '?', 0 when parms.Length >= 1 ->
            let code = parms.[0]
            let isAltScreenCode = code = 1049 || code = 1047 || code = 47
            if not isAltScreenCode then None
            else
                match finalByte with
                | 'h' -> Some EnterAltScreen
                | 'l' -> Some (ExitAltScreen DateTime.MinValue) // resumeAt filled later
                | _ -> None
        | _ -> None

    // --------------------------------------------------------
    // Live-region detection per RFC §5.3
    // --------------------------------------------------------

    /// Classify an event's effect on the current row's tail-mask
    /// state. Per RFC §5.3:
    ///
    /// 1. ESC[?1049h/l — regime switch (handled separately).
    /// 2. ESC[2J — full erase; freeze + new substrate.
    /// 3. Bare \r without immediately following \n — tail-mask.
    /// 4. ESC[K — tail-mask.
    /// 5. ESC[A + printable on prior row — tail-mask target row.
    /// 6. ESC[<n>D + printable — tail-mask current row.
    /// 7. ESC M (RI) at row 0 — scroll, not extend.
    type private LiveRegionEffect =
        | NoEffect
        | EnterTailMask        // current row becomes tail-masked
        | LeaveTailMask        // newline / new row exits tail-mask
        | FullErase             // ESC[2J — entire substrate frozen
        | CursorUpThenPrintable of targetRow: int

    /// Classify a single VtEvent for live-region effect.
    /// `previousEvent` lets us detect the "bare \r without \n"
    /// pattern (RFC §5.3 #3) — \r alone is tail-mask; \r\n is
    /// just a newline.
    let private classifyLiveRegion
            (event: VtEvent)
            (previousEvent: VtEvent option)
            (currentRow: int)
            : LiveRegionEffect =
        match event with
        | Execute b when b = 0x0Duy ->
            // Bare CR. We don't yet know if a \n follows; the
            // caller defers tail-mask transition until the next
            // event resolves the ambiguity (handled in append).
            EnterTailMask
        | Execute b when b = 0x0Auy ->
            // LF. If preceded by CR, this is just \r\n
            // (newline boundary, no tail-mask). If standalone,
            // also just a newline. Either way: no tail-mask.
            LeaveTailMask
        | CsiDispatch (_, _, 'K', _) ->
            // EL (Erase in Line) — tail-mask current row.
            EnterTailMask
        | CsiDispatch (parms, _, 'J', _) when parms.Length >= 1 && parms.[0] = 2 ->
            // ED 2 — full-screen erase. Freeze the substrate.
            FullErase
        | CsiDispatch (parms, _, 'A', _) ->
            // CUU (Cursor Up). The next printable byte
            // overwrites the target row. Mark the target row
            // for tail-mask. `parms.[0]` is the count; default 1.
            let n = if parms.Length >= 1 then parms.[0] else 1
            let target = max 0 (currentRow - (max 1 n))
            CursorUpThenPrintable target
        | CsiDispatch (_, _, 'D', _) ->
            // CUB (Cursor Back). Subsequent printable overwrites
            // current row. Tail-mask.
            EnterTailMask
        | _ -> NoEffect

    // --------------------------------------------------------
    // Buffer operations
    // --------------------------------------------------------

    /// Append bytes to the committed buffer; if the per-tuple
    /// cap is reached, drop the oldest excess bytes from the
    /// head and flag truncation. Mutates `state.Committed` and
    /// may set `state.Truncated`.
    let private appendCommittedWithCap (state: T) (bytes: byte[]) : unit =
        state.Committed.AddRange(bytes)
        let cap = state.Parameters.PerTupleCapBytes
        if state.Committed.Count > cap then
            let excess = state.Committed.Count - cap
            state.Committed.RemoveRange(0, excess)
            state.Truncated <- true

    /// Drain the pending buffer into a sealed/unsealed
    /// EmittedChunk. Returns the chunk + clears pending.
    ///
    /// **Tail-mask handling depends on `sealed'`.** Sealed
    /// drains (semantic prompt seam, drain-checkpoint) flush
    /// the tail-mask LATEST state alongside pending. Unsealed
    /// drains (newline boundary, idle quantum, max-bytes,
    /// max-time) flush ONLY pending; tail-mask survives until
    /// the next sealed seam OR the next tick-debounce
    /// LiveRegionUpdate emission. This preserves the live-
    /// region semantics: a spinner row continues to live in
    /// tail-mask across newline boundaries that affect OTHER
    /// rows.
    let private drainPending
            (state: T)
            (now: DateTime)
            (sealed': bool)
            : CommitNotification list =
        let hasPending = state.Pending.Count > 0
        let flushTailMaskToo = sealed' && not state.TailMask.IsEmpty
        if not hasPending && not flushTailMaskToo then
            []
        else
            // Sealed drains: append tail-mask LATEST states
            // to pending before committing.
            if flushTailMaskToo then
                let tailBytes =
                    state.TailMask
                    |> Map.toSeq
                    |> Seq.sortBy fst
                    |> Seq.collect (fun (_, bs) -> bs)
                    |> Array.ofSeq
                if tailBytes.Length > 0 then
                    state.Pending.AddRange(tailBytes)
                state.TailMask <- Map.empty
                state.TailMaskTimestamps <- Map.empty

            let bytes = state.Pending.ToArray()
            state.Pending.Clear()
            appendCommittedWithCap state bytes
            state.HighWaterMark <- state.HighWaterMark + int64 bytes.Length
            state.LastEmittedAt <- now

            let chunk =
                { Bytes = bytes
                  Sealed = sealed'
                  Truncated = state.Truncated
                  ProducerWaterMark = state.HighWaterMark
                  EmittedAt = now }
            [ EmittedChunk chunk ]

    // --------------------------------------------------------
    // Append (the main entrypoint)
    // --------------------------------------------------------

    /// Feed bytes + parser-emitted events into the producer.
    /// `bytes` accumulates in the substrate buffer; `events`
    /// drive seam + live-region decisions per RFC §5. Returns
    /// notifications emitted during this call + the producer
    /// instance (same reference; mutated in-place).
    ///
    /// Synchronous on the calling thread (PathwayPump worker
    /// per `HeuristicPromptDetector.fs:79-82`). The Cycle 34b
    /// composition root calls this from
    /// `Program.fs:108`-equivalent inside `startReaderLoop`.
    let append
            (state: T)
            (now: DateTime)
            (bytes: byte[])
            (events: VtEvent[])
            : CommitNotification list * T =
        if state.Frozen then
            // Alt-screen regime active. Bytes don't extend the
            // substrate; events are still scanned for the
            // exit toggle.
            let mutable notifs = []
            let mutable previousEvent : VtEvent option = None
            for event in events do
                match event with
                | CsiDispatch (parms, intermediates, finalByte, priv) ->
                    match classifyAltScreenToggle parms intermediates finalByte priv with
                    | Some (ExitAltScreen _) ->
                        let resumeAt =
                            now.AddMilliseconds(
                                float state.Parameters.RegimeSwitchDrainMs)
                        state.Frozen <- false
                        notifs <-
                            RegimeSwitch (ExitAltScreen resumeAt) :: notifs
                    | _ -> ()
                | _ -> ()
                previousEvent <- Some event
            state.LastByteAt <- now
            (List.rev notifs, state)
        else
            state.LastByteAt <- now

            // Default path: append bytes verbatim to pending.
            // Then walk events to:
            //   (a) detect OSC 133 semantic seams
            //   (b) detect alt-screen toggle (regime switch)
            //   (c) detect live-region / tail-mask transitions
            //   (d) advance currentRow on newline boundaries
            let mutable notifs = []
            let mutable seamSealed = false   // any seam crossed?
            let mutable promptSeamCrossed = false
            let mutable encounteredLF = false
            let mutable previousEvent : VtEvent option = None

            // Split-byte accumulation: bytes go into pending
            // first; later we may move some to tail-mask.
            state.Pending.AddRange(bytes)

            for event in events do
                match event with
                | OscDispatch (parms, _) ->
                    match classifyOsc133 parms with
                    | PromptStart ->
                        // Drain any pending output into a
                        // sealed chunk; mark prompt-start
                        // offset.
                        let chunkNotifs = drainPending state now true
                        notifs <- chunkNotifs @ notifs
                        state.PromptStartOffset <- state.HighWaterMark
                        promptSeamCrossed <- true
                        seamSealed <- true
                    | CommandInputStart ->
                        // End of prompt zone, start of typed
                        // command. Buffer continues; no seam.
                        ()
                    | CommandOutputStart ->
                        // Output zone begins. Drain pending
                        // (which contains the typed command)
                        // sealed.
                        let chunkNotifs = drainPending state now true
                        notifs <- chunkNotifs @ notifs
                        state.OutputStartOffset <- state.HighWaterMark
                        promptSeamCrossed <- true
                        seamSealed <- true
                    | CommandFinished _ ->
                        // Output zone ends. Drain pending
                        // (output) sealed.
                        let chunkNotifs = drainPending state now true
                        notifs <- chunkNotifs @ notifs
                        promptSeamCrossed <- true
                        seamSealed <- true
                    | NotOsc133 -> ()

                | CsiDispatch (parms, intermediates, finalByte, priv) ->
                    match classifyAltScreenToggle parms intermediates finalByte priv with
                    | Some EnterAltScreen ->
                        // Drain pending sealed; freeze.
                        let chunkNotifs = drainPending state now true
                        notifs <-
                            (RegimeSwitch EnterAltScreen) :: chunkNotifs @ notifs
                        state.Frozen <- true
                    | Some _ ->
                        // ExitAltScreen here would mean we're
                        // already not frozen; ignore.
                        ()
                    | None ->
                        // Non-alt-screen CSI; check for live-
                        // region effect.
                        match classifyLiveRegion event previousEvent state.CurrentRow with
                        | EnterTailMask ->
                            // Move pending into the tail-mask
                            // for the current row.
                            let pendingBytes = state.Pending.ToArray()
                            state.Pending.Clear()
                            state.TailMask <-
                                Map.add state.CurrentRow pendingBytes state.TailMask
                            state.TailMaskTimestamps <-
                                Map.add state.CurrentRow now state.TailMaskTimestamps
                        | FullErase ->
                            // Drain pending sealed; mark frozen.
                            let chunkNotifs = drainPending state now true
                            notifs <- chunkNotifs @ notifs
                            state.Frozen <- true
                        | CursorUpThenPrintable target ->
                            // Move current pending into the
                            // target row's tail-mask + set
                            // timestamp so tick can fire a
                            // LiveRegionUpdate after debounce.
                            // Per Cycle 34a's rough scope, the
                            // bytes captured here include
                            // pre-CUU content; future cycles
                            // can refine the byte routing.
                            let pendingBytes = state.Pending.ToArray()
                            state.Pending.Clear()
                            state.TailMask <-
                                Map.add target pendingBytes state.TailMask
                            state.TailMaskTimestamps <-
                                Map.add target now state.TailMaskTimestamps
                        | LeaveTailMask
                        | NoEffect -> ()

                | Execute b when b = 0x0Auy ->
                    // LF. Logical newline → potential newline
                    // seam. Increment row counter.
                    encounteredLF <- true
                    state.CurrentRow <- state.CurrentRow + 1

                | Execute b when b = 0x0Duy ->
                    // CR. Check next event for LF; if absent,
                    // tail-mask. We can't know yet since events
                    // are processed sequentially — defer the
                    // decision via the previousEvent tracking
                    // already in classifyLiveRegion.
                    ()

                | _ -> ()

                previousEvent <- Some event

            // Newline seam: if any LF arrived AND no stronger
            // seam (semantic prompt) crossed, emit pending
            // bytes as an unsealed chunk at the last LF
            // boundary.
            if encounteredLF && not promptSeamCrossed then
                let chunkNotifs = drainPending state now false
                notifs <- chunkNotifs @ notifs

            // Max-bytes ceiling: if pending exceeds the cap
            // mid-stream, force an unsealed emit.
            if state.Pending.Count >= state.Parameters.MaxBytesPerEmit then
                let chunkNotifs = drainPending state now false
                notifs <- chunkNotifs @ notifs

            (List.rev notifs, state)

    // --------------------------------------------------------
    // Tick (time-driven flush)
    // --------------------------------------------------------

    /// Force-tick the producer with the current time. Drains
    /// pending bytes if the idle-quantum or max-time-without-
    /// emit thresholds are crossed. Also emits LiveRegionUpdate
    /// notifications for tail-masked rows that have settled
    /// past `LiveRegionDebounceMs`.
    ///
    /// Caller invokes this from a periodic timer (e.g., the
    /// PathwayPump's existing 50ms tick). In Cycle 34a no such
    /// timer is wired; `tick` is only called from tests.
    let tick (state: T) (now: DateTime) : CommitNotification list * T =
        if state.Frozen then
            ([], state)
        else
            let mutable notifs = []

            // Idle-quantum check: bytes arrived but haven't
            // committed in idle_quantum_ms.
            let idleElapsed =
                now - state.LastByteAt
            let pendingHasContent = state.Pending.Count > 0

            // Max-time check: no emit in max_time_without_emit.
            let timeSinceEmit =
                now - state.LastEmittedAt
            let maxTimeElapsed =
                timeSinceEmit.TotalMilliseconds
                >= float state.Parameters.MaxTimeWithoutEmitMs

            let idleElapsedMs =
                idleElapsed.TotalMilliseconds
            let idleTriggered =
                pendingHasContent
                && idleElapsedMs >= float state.Parameters.IdleQuantumMs

            if idleTriggered || (maxTimeElapsed && pendingHasContent) then
                let chunkNotifs = drainPending state now false
                notifs <- chunkNotifs @ notifs

            // Tail-mask debounce: emit LiveRegionUpdate for any
            // row whose tail-mask has settled past
            // LiveRegionDebounceMs since the last update.
            let debounceMs =
                float state.Parameters.LiveRegionDebounceMs
            let liveUpdates =
                state.TailMaskTimestamps
                |> Map.toSeq
                |> Seq.choose (fun (row, lastUpdate) ->
                    if (now - lastUpdate).TotalMilliseconds >= debounceMs
                    then
                        match Map.tryFind row state.TailMask with
                        | Some bs when bs.Length > 0 ->
                            Some (row, { LatestBytes = bs; UpdatedAt = now })
                        | _ -> None
                    else None)
                |> Seq.toList

            for (row, update) in liveUpdates do
                notifs <- LiveRegionUpdate update :: notifs
                // Clear the timestamp so we don't re-emit until
                // the row's tail-mask is updated again.
                state.TailMaskTimestamps <-
                    Map.remove row state.TailMaskTimestamps

            (List.rev notifs, state)

    // --------------------------------------------------------
    // Drain-checkpoint-swap (RFC §6) — stub for Cycle 34a
    // --------------------------------------------------------

    /// Manual checkpoint entrypoint. Cycle 34a exposes this for
    /// tests; Cycle 35 wires it on alt-screen entry from
    /// PathwayPump. Drains pending sealed; sets frozen.
    let checkpointAndFreeze
            (state: T)
            (now: DateTime)
            : CommitNotification list * T =
        let chunkNotifs = drainPending state now true
        let regimeNotif = RegimeSwitch EnterAltScreen
        state.Frozen <- true
        (chunkNotifs @ [ regimeNotif ], state)

    /// Manual resume entrypoint. Cycle 34a exposes this for
    /// tests; Cycle 35 wires it on alt-screen exit. Clears
    /// frozen; subsequent appends extend the substrate after
    /// `RegimeSwitchDrainMs` settle (returned in the
    /// `ExitAltScreen` notification's `resumeAt`).
    let resumeFromFreeze (state: T) (now: DateTime) : T =
        state.Frozen <- false
        state.LastByteAt <-
            now.AddMilliseconds(
                float state.Parameters.RegimeSwitchDrainMs)
        state

    // --------------------------------------------------------
    // SessionTuple finalize (Cycle 35 cutover; exists in 34a)
    // --------------------------------------------------------

    /// Slice the committed buffer between the last finalize
    /// watermark and the current high-water-mark; return as a
    /// FinalizedChunk shaped for SessionTuple. Cycle 34a
    /// exposes this but it's unwired runtime-side; Cycle 35
    /// flips `SessionModel.applyAndCapture` to call this
    /// instead of `extractContent`.
    ///
    /// The CommandText / OutputText split uses
    /// `PromptStartOffset` / `OutputStartOffset` markers set
    /// during OSC 133 parsing.
    let finalizeHighWaterMark (state: T) : FinalizedChunk * T =
        let totalBytes = state.Committed.Count
        let promptStart = int state.PromptStartOffset
        let outputStart = int state.OutputStartOffset

        let commandText =
            if outputStart > promptStart && promptStart >= 0 then
                let cmdLen = outputStart - promptStart
                if cmdLen > 0 && promptStart + cmdLen <= totalBytes then
                    let bytes =
                        Array.sub
                            (state.Committed.ToArray())
                            promptStart
                            cmdLen
                    System.Text.Encoding.UTF8.GetString(bytes)
                else ""
            else ""

        let outputText =
            if outputStart >= 0 && outputStart < totalBytes then
                let outLen = totalBytes - outputStart
                let bytes =
                    Array.sub
                        (state.Committed.ToArray())
                        outputStart
                        outLen
                System.Text.Encoding.UTF8.GetString(bytes)
            else ""

        let chunk =
            { CommandText = commandText
              OutputText = outputText
              Truncated = state.Truncated
              Sealed = true
              ProducerWaterMark = state.HighWaterMark }

        // Reset per-tuple state for the next command.
        state.PromptStartOffset <- state.HighWaterMark
        state.OutputStartOffset <- state.HighWaterMark
        state.Truncated <- false

        (chunk, state)

    // --------------------------------------------------------
    // Diagnostic accessor (for Cycle 34b Ctrl+Shift+D)
    // --------------------------------------------------------

    /// Return up to `kilobytes * 1024` bytes from the
    /// committed buffer's tail. Less if the buffer is shorter.
    /// Cycle 34b uses this for the `--- LINEAR STREAM ---`
    /// section of the diagnostic bundle.
    let getLastBytes (state: T) (kilobytes: int) : byte[] =
        let want = kilobytes * 1024
        let available = state.Committed.Count
        let take = min want available
        let start = available - take
        if take <= 0 then [||]
        else
            let result = Array.zeroCreate take
            // ResizeArray.CopyTo doesn't take a slice; use
            // index-based copy.
            for i in 0 .. take - 1 do
                result.[i] <- state.Committed.[start + i]
            result
