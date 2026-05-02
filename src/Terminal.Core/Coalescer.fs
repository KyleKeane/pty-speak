namespace Terminal.Core

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
// Logging-PR — needed for the LogInformation / LogError
// extension methods on ILogger (defined in the
// LoggerExtensions static class in this namespace).
open Microsoft.Extensions.Logging

/// Stage 5 — streaming-output coalescer.
///
/// Sits between the parser-side `notificationChannel`
/// (`Channel<ScreenNotification>`, capacity 256, DropOldest)
/// and the existing UIA drain task in `Program.fs compose ()`.
/// Reads every `ScreenNotification` the parser publishes,
/// coalesces them via debounce + dedup + frame-hash, and
/// emits at most one `ScreenNotification` per ~200ms window
/// to a downstream `coalescedChannel`.
///
/// The drain task is unchanged — it reads the downstream
/// channel and calls `TerminalSurface.Announce(message,
/// activityId)` per item.
///
/// Algorithms in detail:
///
///   * **Per-row hash.** FNV-1a 64-bit over each cell
///     (`Cell.Ch.Value` + `Cell.Width` + flattened
///     `SgrAttrs`), folded with the row index so a row
///     swap doesn't alias.
///   * **Frame hash.** XOR of per-row hashes. Two
///     consecutive `RowsChanged` events with identical
///     screen content produce identical frame hashes
///     and the second is suppressed (composes with
///     Claude Ink's full-frame redraws — without this
///     layer, every row hash changes per redraw and
///     per-row dedup never fires).
///   * **Spinner heuristic.** Sliding window keyed by
///     `(rowIdx, hash)` AND a generic
///     "any-hash-anywhere ≥5 in last 1s" gate. Suppresses
///     repeated frames at high rate (e.g. spinners that
///     wrap row indices).
///   * **Debounce.** Leading-edge + trailing-edge: first
///     event in an idle period (no flush in last 200ms)
///     emits immediately for fast single-event UX
///     (`echo hello`); subsequent events accumulate and
///     drain on the next 200ms timer tick.
///   * **Alt-screen flush barrier.** `ModeChanged
///     (AltScreen, _)` events flush the pending
///     accumulator first, reset frame-hash + spinner
///     state, then pass through the `ModeChanged`
///     itself so the drain can announce the transition.
///   * **Sanitisation.** Every emit text passes through
///     `AnnounceSanitiser.sanitise` so PTY-originated
///     control chars can't reach NVDA's notification
///     handler.
///
/// Threading: the coalescer runs as a single
/// `Task.Run` reader loop; the input channel is
/// SPSC (parser → coalescer); the output channel is
/// SPSC (coalescer → drain). The coalescer is
/// pure-state otherwise (no Dispatcher dependency,
/// no UI thread requirement).
module Coalescer =

    /// Hardcoded for Stage 5. Phase 2's TOML config will
    /// expose this per the USER-SETTINGS.md "Verbosity /
    /// NVDA narration" candidate.
    // TODO Phase 2: user-configurable via TOML
    let internal debounceWindow = TimeSpan.FromMilliseconds 200.0

    /// Spinner-detection sliding-window length.
    // TODO Phase 2: user-configurable via TOML
    let internal spinnerWindow = TimeSpan.FromMilliseconds 1000.0

    /// Suppress an `(rowIdx, hash)` if it appeared this
    /// many times within `spinnerWindow`. Five flips per
    /// second (e.g. `|/-\` cycle) is the textbook spinner.
    // TODO Phase 2: user-configurable
    let internal spinnerThreshold = 5

    let private fnvOffsetBasis = 0xcbf29ce484222325UL
    let private fnvPrime = 0x00000100000001B3UL
    let private rowSwapMix = 0x9E3779B97F4A7C15UL

    /// FNV-1a 64-bit folded with the row index. See module
    /// docstring §"Per-row hash".
    let rec internal hashRow (rowIdx: int) (cells: Cell[]) : uint64 =
        let mutable h = fnvOffsetBasis
        for cell in cells do
            // Cell.Ch is a Rune (int code point).
            h <- h ^^^ uint64 cell.Ch.Value
            h <- h * fnvPrime
            // Defensive against future Cell.Width additions
            // (Cell type today has no Width field; if Stage
            // 5+ adds wide-char support, this branch begins
            // to mix it in). Until then, this is a constant
            // contribution and mathematically a no-op for
            // dedup purposes.
            //
            // Stage 5 ships without Width; commented out
            // until the Cell type grows that field.
            // h <- h ^^^ uint64 cell.Width
            // h <- h * fnvPrime
            h <- h ^^^ hashAttrs cell.Attrs
            h <- h * fnvPrime
        h ^^^ (uint64 rowIdx * rowSwapMix)

    /// Flatten SgrAttrs into a uint64 fingerprint. Stable
    /// across Cell instances with identical attrs.
    and internal hashAttrs (attrs: SgrAttrs) : uint64 =
        let colorHash (c: ColorSpec) : uint64 =
            match c with
            | Default -> 0UL
            | Indexed b -> 0x100UL ||| uint64 b
            | Rgb (r, g, b) ->
                0x1000000UL ||| (uint64 r <<< 16) ||| (uint64 g <<< 8) ||| uint64 b
        let mutable h = 0UL
        h <- h ^^^ colorHash attrs.Fg
        h <- h * fnvPrime
        h <- h ^^^ colorHash attrs.Bg
        h <- h * fnvPrime
        let flags =
            (if attrs.Bold then 1UL else 0UL)
            ||| (if attrs.Italic then 2UL else 0UL)
            ||| (if attrs.Underline then 4UL else 0UL)
            ||| (if attrs.Inverse then 8UL else 0UL)
        h <- h ^^^ flags
        h * fnvPrime

    /// Compose row hashes into a frame hash. Order-independent
    /// XOR is correct here because each row hash already
    /// encodes its index via `^^^ (uint64 rowIdx * rowSwapMix)`.
    let internal hashFrame (rows: Cell[][]) : uint64 =
        let mutable h = 0UL
        for i in 0 .. rows.Length - 1 do
            h <- h ^^^ hashRow i rows.[i]
        h

    /// Render an array of `Cell[]` rows into the announcement
    /// string NVDA reads. Each row is sanitised individually
    /// through `AnnounceSanitiser.sanitise` (so PTY-originated
    /// BEL, ESC, BiDi, etc. are stripped from the row content)
    /// then joined with `\n`. The separator is added AFTER
    /// sanitisation so the row structure survives — sanitise
    /// strips `\n` as a C0 control, which would otherwise
    /// collapse multi-line output into a single line and
    /// defeat NVDA's per-line speech pause.
    let internal renderRows (rows: Cell[][]) : string =
        let sb = StringBuilder()
        // Drop trailing all-blank rows so a half-full screen
        // doesn't speak a wall of empty padding lines.
        let mutable lastRow = -1
        for r in 0 .. rows.Length - 1 do
            for c in 0 .. rows.[r].Length - 1 do
                if rows.[r].[c].Ch.Value <> int ' ' then lastRow <- r
        for r in 0 .. lastRow do
            if r > 0 then sb.Append('\n') |> ignore
            let row = rows.[r]
            // Find the rightmost non-blank cell to skip padding.
            let mutable lastCh = -1
            for c in 0 .. row.Length - 1 do
                if row.[c].Ch.Value <> int ' ' then lastCh <- c
            let rowSb = StringBuilder()
            for c in 0 .. lastCh do
                rowSb.Append(row.[c].Ch.ToString()) |> ignore
            sb.Append(AnnounceSanitiser.sanitise (rowSb.ToString())) |> ignore
        sb.ToString()

    /// One emitted announcement: the text plus the
    /// `activityId` the drain passes to
    /// `RaiseNotificationEvent`. Stage 5 always uses
    /// `ActivityIds.output` for streaming text;
    /// `ActivityIds.error` for parser errors;
    /// `ActivityIds.mode` for mode-change barriers.
    type CoalescedNotification =
        | OutputBatch of text: string
        | ErrorPassthrough of message: string
        | ModeBarrier of flag: TerminalModeFlag * value: bool

    /// Spinner-suppression key: the per-row hash plus the row
    /// index it came from. The "any-hash-anywhere" generic
    /// gate uses just the hash (key-by-uint64-hash without
    /// the index).
    type private SpinnerKey = int * uint64

    /// Coalescer's mutable state. Owned by the single reader
    /// loop task; no concurrent access. Tests construct one
    /// directly and drive it via `tryEmit` to assert
    /// algorithmic behaviour without spinning a real loop.
    type State =
        { mutable LastFrameHash: uint64 voption
          mutable LastFlushAt: DateTimeOffset voption
          mutable PendingFrame: Cell[][] voption
          mutable PendingHash: uint64 voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          AllHashHistory: ResizeArray<DateTimeOffset> }

    let internal createState () : State =
        { LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingFrame = ValueNone
          PendingHash = ValueNone
          PerRowHistory = Dictionary()
          AllHashHistory = ResizeArray() }

    /// Trim history older than `spinnerWindow`. Mutates in
    /// place. Called on every event arrival to keep histories
    /// bounded.
    let private gcHistory (state: State) (now: DateTimeOffset) =
        let cutoff = now - spinnerWindow
        let staleKeys = ResizeArray()
        for kvp in state.PerRowHistory do
            kvp.Value.RemoveAll(fun ts -> ts < cutoff) |> ignore
            if kvp.Value.Count = 0 then staleKeys.Add(kvp.Key)
        for k in staleKeys do
            state.PerRowHistory.Remove(k) |> ignore
        state.AllHashHistory.RemoveAll(fun ts -> ts < cutoff) |> ignore

    /// Test whether this frame should be suppressed by the
    /// spinner heuristic. Records the timestamps even on
    /// suppress so a long-running spinner stays suppressed.
    let private isSpinnerSuppressed
            (state: State)
            (now: DateTimeOffset)
            (rowIdx: int)
            (rowHash: uint64) : bool =
        let key = (rowIdx, rowHash)
        let history =
            match state.PerRowHistory.TryGetValue key with
            | true, h -> h
            | false, _ ->
                let h = ResizeArray()
                state.PerRowHistory.[key] <- h
                h
        history.Add(now)
        state.AllHashHistory.Add(now)
        history.Count >= spinnerThreshold
        || state.AllHashHistory.Count >= spinnerThreshold * 4

    /// Decide what (if anything) to emit for an incoming
    /// `RowsChanged []` notification. Reads the current
    /// screen snapshot, computes hashes, applies dedup +
    /// spinner + debounce, returns the `CoalescedNotification`
    /// list to push downstream (zero or one item).
    ///
    /// Public for unit testability — production code calls
    /// this from the `runLoop` reader task; tests drive it
    /// directly with a `FakeTimeProvider`.
    let processRowsChanged
            (state: State)
            (now: DateTimeOffset)
            (snapshot: Cell[][]) : CoalescedNotification list =
        // Streaming-path instrumentation. Defaults to NullLogger
        // before Logger.configure runs (production sets this in
        // Program.fs compose(); tests that don't configure get
        // NullLogger and pay no cost). Each branch below logs the
        // suppression reason or the emit so the diagnosis trail
        // distinguishes "Coalescer dropped the event by design"
        // from "Coalescer didn't see the event" from "Coalescer
        // emitted but Drain didn't pick up".
        let logger = Logger.get "Terminal.Core.Coalescer.processRowsChanged"
        let frameHash = hashFrame snapshot
        // Frame-level dedup: identical content → suppress
        // entirely. This is the layer that composes with
        // Ink's full-frame redraws.
        match state.LastFrameHash with
        | ValueSome prev when prev = frameHash ->
            logger.LogInformation(
                "Suppressed (frame-dedup). FrameHash=0x{Hash:X16}", frameHash)
            []
        | _ ->
            gcHistory state now
            // Spinner check: walk per-row hashes; if ANY row
            // is in spinner-suppress and this is a fast
            // repeat, suppress the whole frame.
            let mutable suppress = false
            for i in 0 .. snapshot.Length - 1 do
                let rh = hashRow i snapshot.[i]
                if isSpinnerSuppressed state now i rh then
                    suppress <- true
            if suppress then
                // Update LastFrameHash so we don't re-suppress
                // when content actually changes again.
                state.LastFrameHash <- ValueSome frameHash
                logger.LogInformation(
                    "Suppressed (spinner). FrameHash=0x{Hash:X16}", frameHash)
                []
            else
                // Debounce decision: leading-edge or queue?
                let elapsed =
                    match state.LastFlushAt with
                    | ValueNone -> debounceWindow + debounceWindow
                    | ValueSome t -> now - t
                if elapsed >= debounceWindow then
                    // Leading-edge: emit immediately.
                    state.LastFrameHash <- ValueSome frameHash
                    state.LastFlushAt <- ValueSome now
                    state.PendingFrame <- ValueNone
                    state.PendingHash <- ValueNone
                    let rendered = renderRows snapshot
                    logger.LogInformation(
                        "Emit OutputBatch (leading-edge). FrameHash=0x{Hash:X16} TextLen={Len}",
                        frameHash, rendered.Length)
                    [ OutputBatch rendered ]
                else
                    // Within debounce: accumulate; trailing
                    // edge will flush.
                    state.PendingFrame <- ValueSome snapshot
                    state.PendingHash <- ValueSome frameHash
                    logger.LogInformation(
                        "Accumulated (within debounce window). FrameHash=0x{Hash:X16}",
                        frameHash)
                    []

    /// Trailing-edge timer tick. If anything is pending and
    /// the debounce window has elapsed, emit it.
    let onTimerTick
            (state: State)
            (now: DateTimeOffset) : CoalescedNotification list =
        let logger = Logger.get "Terminal.Core.Coalescer.onTimerTick"
        match state.PendingFrame, state.PendingHash with
        | ValueSome snapshot, ValueSome hash ->
            let elapsed =
                match state.LastFlushAt with
                | ValueNone -> debounceWindow
                | ValueSome t -> now - t
            if elapsed >= debounceWindow then
                state.LastFrameHash <- ValueSome hash
                state.LastFlushAt <- ValueSome now
                state.PendingFrame <- ValueNone
                state.PendingHash <- ValueNone
                let rendered = renderRows snapshot
                logger.LogInformation(
                    "Emit OutputBatch (trailing-edge). FrameHash=0x{Hash:X16} TextLen={Len}",
                    hash, rendered.Length)
                [ OutputBatch rendered ]
            else
                []
        | _ -> []

    /// Mode-change barrier: flush any pending accumulator
    /// (synthesising a snapshot from the saved pending
    /// frame), reset frame-hash + spinner state, then
    /// pass through the ModeChanged signal so the drain
    /// can announce the transition.
    let onModeChanged
            (state: State)
            (now: DateTimeOffset)
            (flag: TerminalModeFlag)
            (value: bool) : CoalescedNotification list =
        let flushed =
            match state.PendingFrame with
            | ValueSome snapshot ->
                state.PendingFrame <- ValueNone
                state.PendingHash <- ValueNone
                [ OutputBatch (renderRows snapshot) ]
            | ValueNone -> []
        // Reset coalescer state — the buffer just changed
        // wholesale (alt-screen swap, etc.).
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueSome now
        state.PerRowHistory.Clear()
        state.AllHashHistory.Clear()
        flushed @ [ ModeBarrier (flag, value) ]

    /// Pass-through for parser errors. No coalescing — errors
    /// are immediate by design (they signal something the
    /// user needs to know about now).
    let onParserError (message: string) : CoalescedNotification list =
        [ ErrorPassthrough (AnnounceSanitiser.sanitise message) ]

    /// Production reader loop. Drains the input channel,
    /// dispatches each notification through the appropriate
    /// `process*` function, and writes resulting
    /// `CoalescedNotification`s to the output channel. Runs
    /// the trailing-edge timer in parallel via
    /// `Task.WhenAny` over the read + the timer task.
    let runLoop
            (input: ChannelReader<ScreenNotification>)
            (output: ChannelWriter<CoalescedNotification>)
            (screen: Screen)
            (timeProvider: TimeProvider)
            (ct: CancellationToken) : Task =
        Task.Run(fun () ->
            task {
                let logger = Logger.get "Terminal.Core.Coalescer"
                logger.LogInformation(
                    "runLoop starting. debounceWindow={Debounce} spinnerWindow={Spinner} spinnerThreshold={Threshold}",
                    debounceWindow,
                    spinnerWindow,
                    spinnerThreshold)
                let state = createState ()
                let mutable lastSnapshotSeq = -1L
                use timer = new PeriodicTimer(debounceWindow, timeProvider)
                let writeOne (notification: CoalescedNotification) =
                    task {
                        let! _ = output.WriteAsync(notification, ct).AsTask()
                        return ()
                    }
                let writeAll (xs: CoalescedNotification list) =
                    task {
                        for n in xs do
                            do! writeOne n
                    }
                let processNotification (n: ScreenNotification) =
                    task {
                        let now = timeProvider.GetUtcNow()
                        match n with
                        | RowsChanged _ ->
                            // RowsChanged carries no row-info
                            // today; the coalescer reads the
                            // snapshot itself and dedups via
                            // sequence number + frame hash.
                            let seq, snapshot = screen.SnapshotRows(0, screen.Rows)
                            if seq <> lastSnapshotSeq then
                                lastSnapshotSeq <- seq
                                let emits = processRowsChanged state now snapshot
                                do! writeAll emits
                        | ParserError msg ->
                            do! writeAll (onParserError msg)
                        | ModeChanged (flag, value) ->
                            do! writeAll (onModeChanged state now flag value)
                    }
                try
                    let! _ = input.WaitToReadAsync(ct).AsTask()
                    let mutable keepGoing = true
                    // PeriodicTimer.WaitForNextTickAsync throws
                    // InvalidOperationException ("Operation is not
                    // valid due to the state of the object") if
                    // called concurrently — a second call before the
                    // previous returns. Each loop iteration's
                    // Task.WhenAny wins on EITHER timer or reader; if
                    // the reader wins, the timer's wait is still
                    // pending. Without this state-tracking, the next
                    // iteration would invoke WaitForNextTickAsync
                    // again on a still-pending timer and crash.
                    //
                    // Fix: keep the pending timer task across
                    // iterations. Only start a new
                    // WaitForNextTickAsync after the previous one
                    // has fired.
                    let mutable pendingTimerWait : Task | null = null
                    while keepGoing && not ct.IsCancellationRequested do
                        // Drain everything currently available.
                        let mutable peek = Unchecked.defaultof<ScreenNotification>
                        while input.TryRead(&peek) do
                            do! processNotification peek
                        // Reuse the still-pending timer task from a
                        // previous reader-wins iteration; otherwise
                        // start a fresh wait.
                        let timerWait : Task =
                            match pendingTimerWait with
                            | null -> timer.WaitForNextTickAsync(ct).AsTask() :> Task
                            | t -> t
                        pendingTimerWait <- timerWait
                        let readWait = input.WaitToReadAsync(ct).AsTask()
                        let! winner = Task.WhenAny(timerWait, readWait :> Task)
                        if obj.ReferenceEquals(winner, timerWait) then
                            // Timer tick fired: clear the pending
                            // slot so the next iteration starts a
                            // fresh wait, and flush any pending
                            // accumulator.
                            pendingTimerWait <- null
                            let now = timeProvider.GetUtcNow()
                            let emits = onTimerTick state now
                            do! writeAll emits
                        else
                            // Reader won; timer wait stays in
                            // pendingTimerWait for next iteration.
                            // Did the channel close?
                            let! got = readWait
                            if not got then keepGoing <- false
                with
                | :? OperationCanceledException ->
                    logger.LogInformation("runLoop cancelled cleanly.")
                | ex ->
                    // Log the FULL exception (type, message, stack
                    // trace, inner exception chain) BEFORE the
                    // sanitised user-facing announcement. This is
                    // the diagnostic path for the post-Stage-6
                    // intermittent crash.
                    logger.LogError(
                        ex,
                        "Coalescer runLoop crashed at lastSnapshotSeq={LastSeq}.",
                        lastSnapshotSeq)
                    // Surface as parser-error so the user hears
                    // about coalescer-internal failures rather
                    // than the channel just going silent.
                    do! writeOne (ErrorPassthrough
                        (sprintf "Coalescer crashed: %s"
                            (AnnounceSanitiser.sanitise ex.Message)))
            } :> Task)
