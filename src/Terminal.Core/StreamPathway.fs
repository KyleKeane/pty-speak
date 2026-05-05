namespace Terminal.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging

/// Phase A — Stream display pathway. Replaces the verbose-
/// readback behaviour of the Stage 8b StreamProfile.
///
/// **Behaviour change vs. StreamProfile:** the old StreamProfile
/// rendered the FULL screen snapshot on every emit and shipped
/// it as the StreamChunk Payload — the verbose-readback issue
/// (GitHub #115/#139). StreamPathway computes the canonical
/// diff (only rows that changed since the pathway last
/// emitted), renders only those rows, and ships the diff text
/// as the StreamChunk Payload. NVDA reads the new content
/// instead of re-reading the entire screen.
///
/// **Algorithm reuse.** The existing dedup + spinner-suppress
/// + debounce algorithms from PR-M / Stage 8b are preserved
/// verbatim — they live alongside the new diff computation.
/// The change is at the EMISSION layer: the algorithms still
/// decide WHEN to emit; the pathway changes WHAT to emit
/// (changed-rows text vs. full snapshot).
///
/// **Pathway state vs. substrate state.** The pathway holds:
/// - `lastSeenRowHashes`: the row hashes the pathway emitted
///   against last time. Used as the baseline for the next
///   diff computation. Empty on first call (every row is
///   "new" — emits full snapshot).
/// - The existing `StreamProfile.State`-equivalent fields:
///   LastFrameHash, LastFlushAt, PendingFrame, PendingHash,
///   PerRowHistory, HashHistory. These drive dedup +
///   spinner-suppress + debounce.
///
/// **Mode-change handling.** `Consume` doesn't see mode
/// changes directly — those flow through ScreenNotification
/// / OutputEventBuilder, separately from canonical-state
/// updates. The PathwayPump in Program.fs handles mode events
/// by calling the pathway's `OnModeBarrier` helper (exposed
/// internally) which flushes pending state + resets row
/// hashes. The next Consume after a mode barrier emits the
/// post-flush snapshot in full (acts as a first-call again).
///
/// **Spec reference.** The architectural-spec draft at the
/// top of `/root/.claude/plans/hello-i-lost-my-velvet-deer.md`
/// (Layer 3 pathway interface).
module StreamPathway =

    [<Literal>]
    let id: string = "stream"

    /// Per-instance parameters mirroring the Stage 8b
    /// StreamProfile.Parameters. The 200ms debounce window from
    /// PR-N is preserved as the default; the 500-char cap (PR-H
    /// stopgap) is kept but its load-bearing role diminishes
    /// post-Phase-A because most diffs are small (one or two
    /// rows of new text).
    type Parameters =
        { DebounceWindowMs: int
          SpinnerWindowMs: int
          SpinnerThreshold: int
          MaxAnnounceChars: int }

    let defaultParameters: Parameters =
        { DebounceWindowMs = 200
          SpinnerWindowMs = 1000
          SpinnerThreshold = 5
          MaxAnnounceChars = 500 }

    type private SpinnerKey = int * uint64

    /// The pathway's mutable state. One instance per `create`
    /// call (per shell session in production). Combines:
    /// - `lastEmittedRowHashes`: NEW in Phase A — the row
    ///   hashes from the pathway's last emit, used as the
    ///   baseline for the next diff computation.
    /// - The Stage 8b StreamProfile.State fields, preserved
    ///   verbatim so the existing dedup + spinner + debounce
    ///   algorithms work unchanged.
    type State =
        { mutable LastEmittedRowHashes: uint64[]
          mutable LastFrameHash: uint64 voption
          mutable LastFlushAt: DateTimeOffset voption
          mutable PendingDiff: CanonicalState.CanonicalDiff voption
          mutable PendingFrameHash: uint64 voption
          mutable LastRowHashes: uint64[] voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          HashHistory: Dictionary<uint64, ResizeArray<DateTimeOffset>> }

    let internal createState () : State =
        { LastEmittedRowHashes = [||]
          LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingDiff = ValueNone
          PendingFrameHash = ValueNone
          LastRowHashes = ValueNone
          PerRowHistory = Dictionary()
          HashHistory = Dictionary() }

    let private gcHistory
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            : unit =
        let cutoff =
            now - TimeSpan.FromMilliseconds(float parameters.SpinnerWindowMs)
        let stalePerRow = ResizeArray()
        for kvp in state.PerRowHistory do
            kvp.Value.RemoveAll(fun ts -> ts < cutoff) |> ignore
            if kvp.Value.Count = 0 then stalePerRow.Add(kvp.Key)
        for k in stalePerRow do
            state.PerRowHistory.Remove(k) |> ignore
        let staleHash = ResizeArray()
        for kvp in state.HashHistory do
            kvp.Value.RemoveAll(fun ts -> ts < cutoff) |> ignore
            if kvp.Value.Count = 0 then staleHash.Add(kvp.Key)
        for k in staleHash do
            state.HashHistory.Remove(k) |> ignore

    let private isSpinnerSuppressed
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            (rowHashes: uint64[])
            (contentHashes: uint64[])
            : bool =
        let mutable suppress = false
        let crossSeenThisFrame = HashSet<uint64>()
        for i in 0 .. rowHashes.Length - 1 do
            let rh = rowHashes.[i]
            let changed =
                match state.LastRowHashes with
                | ValueNone -> true
                | ValueSome arr when i >= arr.Length -> true
                | ValueSome arr -> arr.[i] <> rh
            if changed then
                let key = (i, rh)
                let perRowHist =
                    match state.PerRowHistory.TryGetValue key with
                    | true, x -> x
                    | false, _ ->
                        let x = ResizeArray()
                        state.PerRowHistory.[key] <- x
                        x
                perRowHist.Add(now)
                if perRowHist.Count >= parameters.SpinnerThreshold then
                    suppress <- true
                let ch = contentHashes.[i]
                if crossSeenThisFrame.Add(ch) then
                    let crossHist =
                        match state.HashHistory.TryGetValue ch with
                        | true, x -> x
                        | false, _ ->
                            let x = ResizeArray()
                            state.HashHistory.[ch] <- x
                            x
                    crossHist.Add(now)
                    if crossHist.Count >= parameters.SpinnerThreshold then
                        suppress <- true
        suppress

    let private capAnnounce (parameters: Parameters) (text: string) : string =
        let limit = parameters.MaxAnnounceChars
        if text.Length <= limit then
            text
        else
            let head = text.Substring(0, limit)
            let extra = text.Length - limit
            sprintf
                "%s ...announcement truncated; %d more characters available — press Ctrl+Shift+; to copy full log."
                head
                extra

    /// Build the StreamChunk OutputEvent for a flushed diff.
    /// Producer-stamp is "stream" (the StreamPathway origin).
    let private streamOutputEvent (text: string) : OutputEvent =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            id
            text

    /// Decide what (if anything) to emit for an incoming
    /// canonical state. Mirrors the Stage 8b
    /// StreamProfile.processRowsChanged structure (frame-dedup
    /// → spinner-suppress → debounce) but emits the diff text
    /// rather than the full snapshot.
    ///
    /// Public-internal for the test harness.
    let internal processCanonicalState
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            (canonical: CanonicalState.Canonical)
            : OutputEvent[]
            =
        let logger = Logger.get "Terminal.Core.StreamPathway.processCanonicalState"
        let rowHashes = canonical.RowHashes
        let contentHashes = canonical.ContentHashes
        let frameHash =
            let mutable h = 0UL
            for rh in rowHashes do h <- h ^^^ rh
            h
        match state.LastFrameHash with
        | ValueSome prev when prev = frameHash ->
            logger.LogDebug(
                "Suppressed (frame-dedup). FrameHash=0x{Hash:X16}", frameHash)
            [||]
        | _ ->
            gcHistory parameters state now
            let suppress =
                isSpinnerSuppressed parameters state now rowHashes contentHashes
            state.LastRowHashes <- ValueSome rowHashes
            if suppress then
                state.LastFrameHash <- ValueSome frameHash
                logger.LogDebug(
                    "Suppressed (spinner). FrameHash=0x{Hash:X16}", frameHash)
                [||]
            else
                let debounceWindow =
                    TimeSpan.FromMilliseconds(float parameters.DebounceWindowMs)
                let elapsed =
                    match state.LastFlushAt with
                    | ValueNone -> debounceWindow + debounceWindow
                    | ValueSome t -> now - t
                if elapsed >= debounceWindow then
                    // Leading-edge: compute diff, emit immediately.
                    let diff = canonical.computeDiff state.LastEmittedRowHashes
                    if diff.ChangedRows.Length = 0 then
                        // No rows changed since last emit despite
                        // frame-hash differing — this can happen
                        // if the only change was a position-aware
                        // hash difference that hashRowContent
                        // doesn't see, or if the diff baseline
                        // already covered everything in the
                        // current snapshot. Skip emission.
                        state.LastFrameHash <- ValueSome frameHash
                        state.LastFlushAt <- ValueSome now
                        state.PendingDiff <- ValueNone
                        state.PendingFrameHash <- ValueNone
                        logger.LogDebug(
                            "Suppressed (empty-diff). FrameHash=0x{Hash:X16}", frameHash)
                        [||]
                    else
                        state.LastFrameHash <- ValueSome frameHash
                        state.LastFlushAt <- ValueSome now
                        state.PendingDiff <- ValueNone
                        state.PendingFrameHash <- ValueNone
                        state.LastEmittedRowHashes <- rowHashes
                        let capped = capAnnounce parameters diff.ChangedText
                        logger.LogInformation(
                            "Emit StreamChunk (leading-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len}",
                            frameHash, diff.ChangedRows.Length, capped.Length)
                        [| streamOutputEvent capped |]
                else
                    // Within debounce: accumulate the diff;
                    // trailing edge will flush.
                    state.PendingDiff <- ValueSome (canonical.computeDiff state.LastEmittedRowHashes)
                    state.PendingFrameHash <- ValueSome frameHash
                    logger.LogDebug(
                        "Accumulated (within debounce window). FrameHash=0x{Hash:X16}",
                        frameHash)
                    [||]

    /// Trailing-edge timer tick. If a debounced diff is pending
    /// and the debounce window has elapsed, emit it. The
    /// Phase A pathway pump in Program.fs calls this from its
    /// PeriodicTimer (replaces the Stage 8b TickPump).
    let internal onTimerTick
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            : OutputEvent[]
            =
        let logger = Logger.get "Terminal.Core.StreamPathway.onTimerTick"
        match state.PendingDiff, state.PendingFrameHash with
        | ValueSome diff, ValueSome hash ->
            let debounceWindow =
                TimeSpan.FromMilliseconds(float parameters.DebounceWindowMs)
            let elapsed =
                match state.LastFlushAt with
                | ValueNone -> debounceWindow
                | ValueSome t -> now - t
            if elapsed >= debounceWindow then
                state.LastFrameHash <- ValueSome hash
                state.LastFlushAt <- ValueSome now
                state.PendingDiff <- ValueNone
                state.PendingFrameHash <- ValueNone
                if diff.ChangedRows.Length = 0 then
                    [||]
                else
                    // Promote the pending diff's row hashes by
                    // recomputing them from the current state's
                    // LastRowHashes (which were captured during
                    // processCanonicalState's accumulate branch).
                    match state.LastRowHashes with
                    | ValueSome rh -> state.LastEmittedRowHashes <- rh
                    | ValueNone -> ()
                    let capped = capAnnounce parameters diff.ChangedText
                    logger.LogInformation(
                        "Emit StreamChunk (trailing-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len}",
                        hash, diff.ChangedRows.Length, capped.Length)
                    [| streamOutputEvent capped |]
            else
                [||]
        | _ -> [||]

    /// Mode-change barrier handler. Called from the PathwayPump
    /// when a `ScreenNotification.ModeChanged` arrives. Flushes
    /// any pending diff (so the user hears the pre-mode-change
    /// content) and resets row-hash baselines (so the
    /// post-mode-change first emit treats every row as
    /// "new").
    let internal onModeBarrier
            (state: State)
            (now: DateTimeOffset)
            : OutputEvent[]
            =
        let flushed =
            match state.PendingDiff with
            | ValueSome diff when diff.ChangedRows.Length > 0 ->
                [| streamOutputEvent diff.ChangedText |]
            | _ -> [||]
        state.PendingDiff <- ValueNone
        state.PendingFrameHash <- ValueNone
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueSome now
        state.LastRowHashes <- ValueNone
        state.LastEmittedRowHashes <- [||]
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()
        flushed

    /// Internal — clear all mutable state to its initial shape.
    /// Shared by both `Reset` (active-shell switch) and the
    /// post-flush portion of `OnModeBarrier`.
    let private resetState (state: State) : unit =
        state.LastEmittedRowHashes <- [||]
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueNone
        state.PendingDiff <- ValueNone
        state.PendingFrameHash <- ValueNone
        state.LastRowHashes <- ValueNone
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()

    /// Construct a StreamPathway. The pathway captures
    /// `parameters` in its closure; v1 ships a single
    /// pathway-instance per shell session, recycled via
    /// `Reset` on shell-switch.
    let create (parameters: Parameters) : DisplayPathway.T =
        let state = createState ()
        { Id = id
          Consume =
            fun canonical ->
                let now = DateTimeOffset.UtcNow
                processCanonicalState parameters state now canonical
          Tick =
            fun now ->
                onTimerTick parameters state now
          OnModeBarrier =
            fun now ->
                onModeBarrier state now
          Reset =
            fun () -> resetState state }

    /// Test-only — expose the state for direct manipulation in
    /// the test harness. Production code never calls this.
    let internal createWithExposedState
            (parameters: Parameters)
            : DisplayPathway.T * State
            =
        let state = createState ()
        let pathway =
            { Id = id
              Consume =
                fun canonical ->
                    let now = DateTimeOffset.UtcNow
                    processCanonicalState parameters state now canonical
              Tick =
                fun now ->
                    onTimerTick parameters state now
              OnModeBarrier =
                fun now ->
                    onModeBarrier state now
              Reset =
                fun () -> resetState state }
        pathway, state
