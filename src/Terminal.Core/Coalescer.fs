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
///   * **Spinner heuristic — two gates.** Both gates use
///     CHANGE-DETECTION (only count hash CHANGES at a row
///     position, not observations) so a screen with N static
///     rows doesn't trip suppression at high typing cadence.
///     - **Per-`(rowIdx, hash)` gate.** Catches same-position
///       cycling spinners (`|/-\`, `( *)`, etc.) — the same
///       `(rowIdx, hash)` tuple recurs each cycle.
///     - **Per-hash gate (any-hash-anywhere).** Catches moving
///       spinners (scrolling progress bars, Ink reconciler
///       reflows) where the same content lands at different
///       rows each frame — the `(rowIdx, hash)` key is unique
///       each frame so the per-key gate misses, but the bare
///       hash recurs across rows. Issue #117 — the original
///       Stage 5 design included this gate but its count-of-
///       total-entries threshold was incompatible with the
///       per-row scan; the redesigned gate counts unique-hash
///       observations only on row-change.
///     Either gate firing suppresses the whole frame.
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

    // PR-N substrate-cleanup contract (2026-05-04). The three
    // constants below — `debounceWindow`, `spinnerWindow`,
    // `spinnerThreshold` — are **Stream-profile defaults**, not
    // universal terminal-output tuning knobs. The Output framework
    // cycle (Part 3 of `docs/PROJECT-PLAN-2026-05.md`) introduces
    // per-profile presentation strategies (Stream / Form /
    // Selection / TUI / REPL / Earcon); each profile is expected
    // to construct its own `Coalescer.State` instance with
    // caller-supplied thresholds rather than reading these module
    // globals. Today's Coalescer IS the Stream profile — the
    // current values reflect that role's defaults. When the
    // framework lands, these values stay as Stream-profile
    // fallbacks; new profiles override via their own
    // construction parameters and pass their own thresholds
    // through the gate functions. Phase 2's TOML config will
    // expose these per the USER-SETTINGS.md "Verbosity / NVDA
    // narration" candidate; the framework refactor is the
    // intermediate seam, not the user-config surface.

    /// Stream-profile default; Phase 2 TOML candidate. See
    /// PR-N substrate-cleanup contract above.
    let internal debounceWindow = TimeSpan.FromMilliseconds 200.0

    /// Stream-profile default; Phase 2 TOML candidate. See
    /// PR-N substrate-cleanup contract above.
    let internal spinnerWindow = TimeSpan.FromMilliseconds 1000.0

    /// Stream-profile default. Suppress an `(rowIdx, hash)` if
    /// it appeared this many times within `spinnerWindow`. Five
    /// flips per second (e.g. `|/-\` cycle) is the textbook
    /// spinner. Phase 2 TOML candidate. See PR-N substrate-
    /// cleanup contract above.
    let internal spinnerThreshold = 5

    let private fnvOffsetBasis = 0xcbf29ce484222325UL
    let private fnvPrime = 0x00000100000001B3UL
    let private rowSwapMix = 0x9E3779B97F4A7C15UL

    /// PR-M (Issue #117) — content-only FNV-1a 64-bit over the
    /// cells, with NO row-index folding. Used by the cross-row
    /// spinner gate, which needs to recognise the same content
    /// landing at different rows across frames as the same hash.
    /// `hashRow` (with row-index fold) is used everywhere else
    /// for frame-hash computation + per-key spinner detection.
    let rec internal hashRowContent (cells: Cell[]) : uint64 =
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
        h

    /// FNV-1a 64-bit folded with the row index. See module
    /// docstring §"Per-row hash".
    and internal hashRow (rowIdx: int) (cells: Cell[]) : uint64 =
        hashRowContent cells ^^^ (uint64 rowIdx * rowSwapMix)

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
    /// index it came from. A spinner that overwrites the same
    /// cell with cycling characters (`|/-\` etc.) generates
    /// the same `(rowIdx, hash)` tuple repeatedly across each
    /// cycle, which the per-key history catches once the
    /// recurrence count crosses the threshold.
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
          /// PR-M (Issue #117) — per-row hashes from the previous
          /// frame, indexed by row. Feeds change-detection so the
          /// two spinner gates only count hash CHANGES at a row
          /// position, not observations. Without this, a screen
          /// with N static rows added N entries per event to the
          /// per-row history; at 5+ events/sec the static-row keys
          /// tripped suppression even when no spinner existed.
          /// `ValueNone` on first frame; `ValueSome` thereafter.
          mutable LastRowHashes: uint64[] voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          /// PR-M (Issue #117) — per-hash recurrence history
          /// independent of `rowIdx`. Catches CROSS-ROW spinners
          /// that the per-`(rowIdx, hash)` gate misses: a moving
          /// progress indicator scrolling vertically lands the
          /// same content at a different row each frame, so each
          /// `(rowIdx, hash)` key sees count = 1 and the per-key
          /// gate never fires. The any-hash-anywhere gate sees
          /// the recurring hash across rows and fires after
          /// `spinnerThreshold` recurrences within `spinnerWindow`.
          /// Like the per-key gate, only counts CHANGES (a row
          /// transitioning TO this hash), so static content
          /// doesn't accumulate.
          HashHistory: Dictionary<uint64, ResizeArray<DateTimeOffset>> }

    let internal createState () : State =
        { LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingFrame = ValueNone
          PendingHash = ValueNone
          LastRowHashes = ValueNone
          PerRowHistory = Dictionary()
          HashHistory = Dictionary() }

    /// Trim history older than `spinnerWindow` from BOTH
    /// dictionaries. Mutates in place. Called on every event
    /// arrival to keep histories bounded.
    let private gcHistory (state: State) (now: DateTimeOffset) =
        let cutoff = now - spinnerWindow
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

    /// PR-M (Issue #117) — walk all per-row hashes for the
    /// current frame and decide whether either spinner gate
    /// should suppress.
    ///
    /// **Change-detection.** For each row, only `Add(now)` an
    /// observation to the gates if that row's hash CHANGED since
    /// the previous frame. A static row contributes nothing to
    /// either gate; a row that flips from blank to content
    /// contributes once per transition. This protects high-
    /// cadence typing scenarios (where many rows are static and
    /// only the prompt-row changes per event) from tripping the
    /// per-key gate via static-row recurrence.
    ///
    /// **Per-key gate** (existing): suppresses if the
    /// `(rowIdx, hash)` tuple recurred ≥ `spinnerThreshold` times
    /// in `spinnerWindow`. Catches same-position cycling
    /// spinners (`|/-\`, `( *)( *)`).
    ///
    /// **Cross-row gate** (new): suppresses if any single hash
    /// recurred ≥ `spinnerThreshold` times in `spinnerWindow`
    /// REGARDLESS of which row it landed at. Catches moving
    /// spinners (scrolling progress bars, Ink reconciler
    /// reflows where the spinner content lands at different
    /// rows each frame).
    ///
    /// Returns true if either gate fires. Caller is responsible
    /// for updating `state.LastRowHashes` after this call so the
    /// next frame's change-detection has the correct comparison
    /// baseline.
    let private isSpinnerSuppressed
            (state: State)
            (now: DateTimeOffset)
            (rowHashes: uint64[])
            (contentHashes: uint64[]) : bool =
        let mutable suppress = false
        // PR-M: within-frame dedup for the cross-row gate. A
        // screen with N rows of identical content (e.g. blank
        // padding above the prompt) would otherwise contribute
        // N Adds to HashHistory[content-hash] in a single frame,
        // and a screen with ≥5 blank padding rows would trip
        // the gate on the seed frame. The intent of the gate is
        // "this content appeared in N FRAMES" (not "N rows of
        // one frame"), so collapse within-frame duplicates.
        let crossSeenThisFrame = HashSet<uint64>()
        for i in 0 .. rowHashes.Length - 1 do
            let rh = rowHashes.[i]
            let changed =
                match state.LastRowHashes with
                | ValueNone -> true
                | ValueSome arr when i >= arr.Length -> true
                | ValueSome arr -> arr.[i] <> rh
            if changed then
                // Per-`(rowIdx, hash)` gate: uses the row-index-
                // folded hash so the same content at different
                // rows produces different keys. Catches cycling
                // spinners that overwrite the same cell. No
                // within-frame dedup needed: each (rowIdx, hash)
                // tuple is naturally unique within a frame
                // because rowIdx is unique.
                let key = (i, rh)
                let perRowHist =
                    match state.PerRowHistory.TryGetValue key with
                    | true, x -> x
                    | false, _ ->
                        let x = ResizeArray()
                        state.PerRowHistory.[key] <- x
                        x
                perRowHist.Add(now)
                if perRowHist.Count >= spinnerThreshold then
                    suppress <- true
                // Cross-row gate (within-frame deduped): uses
                // the CONTENT-only hash so the same content
                // moving between rows still maps to the same
                // key. Catches scrolling progress bars and Ink
                // reflows.
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
                    if crossHist.Count >= spinnerThreshold then
                        suppress <- true
        suppress

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
        // Streaming-path instrumentation. Per-event entries are
        // emitted at Debug so the production default (Information)
        // sees no per-frame I/O. Set the min-level to Debug via
        // env-var override when reproducing a streaming-silence
        // bug; the trail then distinguishes "Coalescer dropped
        // the event by design" from "Coalescer didn't see the
        // event" from "Coalescer emitted but Drain didn't pick
        // up". Information-level was tried in PR #109 but
        // produced enough log I/O at typing speed to lag the WPF
        // dispatcher visibly; demoted to Debug here.
        let logger = Logger.get "Terminal.Core.Coalescer.processRowsChanged"
        // PR-M (Issue #117): compute per-row hashes ONCE and reuse
        // for both frame-hash and spinner-gate checks. The
        // pre-PR-M code walked snapshot twice (hashFrame + the
        // suppress loop); the precompute is cheaper, and the
        // change-detection logic in `isSpinnerSuppressed` needs
        // the full array anyway.
        let rowHashes =
            Array.init snapshot.Length (fun i -> hashRow i snapshot.[i])
        // PR-M: content-only hashes for the cross-row gate.
        let contentHashes =
            Array.init snapshot.Length (fun i -> hashRowContent snapshot.[i])
        let frameHash =
            let mutable h = 0UL
            for rh in rowHashes do h <- h ^^^ rh
            h
        // Frame-level dedup: identical content → suppress
        // entirely. This is the layer that composes with
        // Ink's full-frame redraws.
        match state.LastFrameHash with
        | ValueSome prev when prev = frameHash ->
            logger.LogDebug(
                "Suppressed (frame-dedup). FrameHash=0x{Hash:X16}", frameHash)
            []
        | _ ->
            gcHistory state now
            // PR-M: spinner check now considers BOTH per-key
            // (cycling-character spinners) AND cross-row (moving
            // spinners) gates. Either gate firing suppresses the
            // whole frame.
            let suppress =
                isSpinnerSuppressed state now rowHashes contentHashes
            // PR-M: update LastRowHashes AFTER the suppress check
            // (which read the previous values for change-detection)
            // and BEFORE the early return so the next frame's
            // change-detection has the correct baseline regardless
            // of whether we emit.
            state.LastRowHashes <- ValueSome rowHashes
            if suppress then
                // Update LastFrameHash so we don't re-suppress
                // when content actually changes again.
                state.LastFrameHash <- ValueSome frameHash
                logger.LogDebug(
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
                    // INFO — emit entries are bounded by the
                    // 200ms debounce (~5/sec max) and are the
                    // primary signal that the streaming pipeline
                    // is alive. Suppress / accumulate entries
                    // stay at Debug.
                    logger.LogInformation(
                        "Emit OutputBatch (leading-edge). FrameHash=0x{Hash:X16} TextLen={Len}",
                        frameHash, rendered.Length)
                    [ OutputBatch rendered ]
                else
                    // Within debounce: accumulate; trailing
                    // edge will flush.
                    state.PendingFrame <- ValueSome snapshot
                    state.PendingHash <- ValueSome frameHash
                    logger.LogDebug(
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
    ///
    /// **Framework-cycle contract (PR-N).** Profile-detection
    /// logic (Output framework cycle, Part 3 of
    /// `docs/PROJECT-PLAN-2026-05.md`) MUST NOT assume screen
    /// content is stable across a mode change. ModeChanged
    /// signals that the semantic context has shifted (alt-screen
    /// toggle, cursor-key mode change, etc.); the screen buffer
    /// behind the next read may be the alternate-buffer's
    /// pre-existing state, blank, or the previous primary
    /// buffer — none of which are reliable indicators of the
    /// new profile. Profiles must wait for the first
    /// post-`ModeBarrier` `RowsChanged` to inspect content; a
    /// profile-classifier reading the snapshot at the
    /// `ModeBarrier` itself will see stale visual state and
    /// mis-classify.
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
        // wholesale (alt-screen swap, etc.). PR-M: also reset
        // LastRowHashes (so post-mode-change first frame treats
        // every row as changed and re-engages the gates) and
        // HashHistory (the cross-row gate's accumulated counts
        // are about a different screen).
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueSome now
        state.LastRowHashes <- ValueNone
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()
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
