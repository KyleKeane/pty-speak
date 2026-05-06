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
          MaxAnnounceChars: int
          /// Phase A.2 — toggle SGR colour-detection emission.
          /// When `true` (default), red-dominant frames emit a
          /// supplementary `ErrorLine` OutputEvent (claimed by
          /// EarconProfile → 400Hz error-tone) alongside the
          /// normal `StreamChunk`; yellow-dominant frames emit
          /// a `WarningLine` (600Hz warning-tone). When `false`,
          /// the helpers don't run + only `StreamChunk` events
          /// emit, identical to the post-revert behaviour. The
          /// TOML config schema exposes this as
          /// `[pathway.stream] color_detection`.
          ColorDetection: bool }

    let defaultParameters: Parameters =
        { DebounceWindowMs = 200
          SpinnerWindowMs = 1000
          SpinnerThreshold = 5
          MaxAnnounceChars = 500
          ColorDetection = true }

    type private SpinnerKey = int * uint64

    /// The pathway's mutable state. One instance per `create`
    /// call (per shell session in production). Combines:
    /// - `lastEmittedRowHashes`: NEW in Phase A — the row
    ///   hashes from the pathway's last emit, used as the
    ///   baseline for the next diff computation.
    /// - The Stage 8b StreamProfile.State fields, preserved
    ///   verbatim so the existing dedup + spinner + debounce
    ///   algorithms work unchanged.
    /// - `PendingColor`: Phase A.2 — captures the dominant
    ///   colour computed at accumulate-time (within debounce
    ///   window) so the trailing-edge `onTimerTick` flush can
    ///   emit the supplementary `ErrorLine`/`WarningLine`
    ///   OutputEvent alongside the flushed `StreamChunk`.
    ///   Cleared in `resetState`, `onModeBarrier`, and on every
    ///   leading-edge or trailing-edge emit.
    type State =
        { mutable LastEmittedRowHashes: uint64[]
          mutable LastFrameHash: uint64 voption
          mutable LastFlushAt: DateTimeOffset voption
          mutable PendingDiff: CanonicalState.CanonicalDiff voption
          mutable PendingFrameHash: uint64 voption
          mutable PendingColor: string voption
          mutable LastRowHashes: uint64[] voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          HashHistory: Dictionary<uint64, ResizeArray<DateTimeOffset>> }

    let internal createState () : State =
        { LastEmittedRowHashes = [||]
          LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingDiff = ValueNone
          PendingFrameHash = ValueNone
          PendingColor = ValueNone
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

    /// Phase A.2 — SGR colour-detection helpers. Red-dominant
    /// rows produce an `ErrorLine` supplementary OutputEvent
    /// (EarconProfile claims → 400Hz error-tone); yellow ditto
    /// `WarningLine` → 600Hz warning-tone. v1 only matches the
    /// standard 16-colour ANSI palette (`Indexed 1`/`Indexed 9`
    /// for red, `Indexed 3`/`Indexed 11` for yellow); 256-cube
    /// reds (`Indexed 196` etc.) and Truecolor RGB-distance
    /// matching are deferred per the original 8d.2 plan.
    ///
    /// **Re-introduction context.** PR #156 (8d.2) shipped this
    /// via `Extensions["dominantColor"]` stamping on the
    /// StreamProfile-synthesized event; the EarconProfile snoop
    /// path was structurally broken (it only saw the RAW
    /// translator event, not the synthesized one). The Phase A
    /// substrate makes event-splitting trivial: emit two events
    /// per coloured frame, EarconProfile claims via Semantic
    /// rather than Extensions. NvdaChannel skips the empty
    /// `ErrorLine`/`WarningLine` payload (NvdaChannel.fs:87) so
    /// no double-announce.
    let internal isRedFg (fg: ColorSpec) : bool =
        match fg with
        | Indexed 1uy -> true
        | Indexed 9uy -> true
        | _ -> false

    let internal isYellowFg (fg: ColorSpec) : bool =
        match fg with
        | Indexed 3uy -> true
        | Indexed 11uy -> true
        | _ -> false

    /// Per-row classification: walk non-blank cells and count
    /// red/yellow foregrounds. >50% of non-blank cells red →
    /// "red"; else >50% yellow → "yellow"; else None. Blank
    /// cells (space rune) are excluded from the count so
    /// end-of-line padding doesn't dilute the classification.
    let internal rowDominantColor (row: Cell[]) : string option =
        let mutable nonBlank = 0
        let mutable red = 0
        let mutable yellow = 0
        for cell in row do
            if cell.Ch.Value <> int ' ' then
                nonBlank <- nonBlank + 1
                if isRedFg cell.Attrs.Fg then
                    red <- red + 1
                elif isYellowFg cell.Attrs.Fg then
                    yellow <- yellow + 1
        if nonBlank = 0 then
            None
        elif red * 2 > nonBlank then
            Some "red"
        elif yellow * 2 > nonBlank then
            Some "yellow"
        else
            None

    /// Per-snapshot classification: any-red wins; else any-
    /// yellow; else None. Red wins over yellow because it's
    /// the higher-urgency tone (400Hz lower-pitched earcon).
    let internal snapshotDominantColor (snapshot: Cell[][]) : string option =
        let mutable hasRed = false
        let mutable hasYellow = false
        for row in snapshot do
            match rowDominantColor row with
            | Some "red" -> hasRed <- true
            | Some "yellow" -> hasYellow <- true
            | _ -> ()
        if hasRed then Some "red"
        elif hasYellow then Some "yellow"
        else None

    /// Phase A.2 hotfix — same precedence logic as
    /// `snapshotDominantColor` but scoped to the rows that
    /// CHANGED in the most recent diff (`diff.ChangedRows`)
    /// rather than every row of the snapshot. Without this
    /// scoping the earcon path treated "any red anywhere on
    /// screen" as a trigger — so a red error message rendered
    /// once would fire `error-tone` on every subsequent
    /// keystroke (the new prompt row was the only changed row,
    /// but the snapshot still contained the leftover red row).
    /// The intent of the colour earcon is to supplement *new*
    /// content; only changed rows count.
    let internal changedRowsDominantColor
            (snapshot: Cell[][])
            (changedRows: int[])
            : string option
            =
        let mutable hasRed = false
        let mutable hasYellow = false
        for rowIdx in changedRows do
            if rowIdx >= 0 && rowIdx < snapshot.Length then
                match rowDominantColor snapshot.[rowIdx] with
                | Some "red" -> hasRed <- true
                | Some "yellow" -> hasYellow <- true
                | _ -> ()
        if hasRed then Some "red"
        elif hasYellow then Some "yellow"
        else None

    /// Build the StreamChunk OutputEvent for a flushed diff.
    /// Producer-stamp is "stream" (the StreamPathway origin).
    let private streamOutputEvent (text: string) : OutputEvent =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            id
            text

    /// Phase A.2 — build the supplementary colour-event for a
    /// flushed diff. Returns `None` for plain frames (caller
    /// emits only the StreamChunk); `Some ErrorLine` for red,
    /// `Some WarningLine` for yellow. Empty payload — NVDA
    /// skips its announce (NvdaChannel.fs:87 RenderText "" →
    /// no marshalAnnounce); EarconProfile claims via Semantic
    /// and emits the earcon decision; FileLogger records the
    /// event for the audit trail. `Priority.Assertive` matches
    /// the urgency intent (bell-class), mirroring `Bell`'s
    /// priority in `OutputEventBuilder.fromScreenNotification`.
    let private colorOutputEvent (color: string) : OutputEvent option =
        match color with
        | "red" ->
            Some (OutputEvent.create
                    SemanticCategory.ErrorLine
                    Priority.Assertive
                    id
                    "")
        | "yellow" ->
            Some (OutputEvent.create
                    SemanticCategory.WarningLine
                    Priority.Assertive
                    id
                    "")
        | _ -> None

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
        // Phase A.2 hotfix — colour detection moved INSIDE the
        // emit branches so it runs against the diff's
        // `ChangedRows` rather than the whole snapshot. See
        // `changedRowsDominantColor` for the rationale; the
        // earlier "scan all rows" version made stale red text
        // re-trigger error-tone on every keystroke at a new
        // prompt.
        let computeColor (changedRows: int[]) : string option =
            if parameters.ColorDetection then
                changedRowsDominantColor canonical.Snapshot changedRows
            else
                None
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
                        state.PendingColor <- ValueNone
                        logger.LogDebug(
                            "Suppressed (empty-diff). FrameHash=0x{Hash:X16}", frameHash)
                        [||]
                    else
                        let dominantColor = computeColor diff.ChangedRows
                        state.LastFrameHash <- ValueSome frameHash
                        state.LastFlushAt <- ValueSome now
                        state.PendingDiff <- ValueNone
                        state.PendingFrameHash <- ValueNone
                        state.PendingColor <- ValueNone
                        state.LastEmittedRowHashes <- rowHashes
                        let capped = capAnnounce parameters diff.ChangedText
                        logger.LogInformation(
                            "Emit StreamChunk (leading-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len} Color={Color}",
                            frameHash, diff.ChangedRows.Length, capped.Length,
                            (match dominantColor with Some c -> c | None -> "none"))
                        // Phase A.2 — emit a supplementary
                        // ErrorLine/WarningLine event when the
                        // diff's changed rows are colour-dominant.
                        // EarconProfile claims it semantically;
                        // NvdaChannel skips the empty payload so no
                        // double-announce.
                        match dominantColor |> Option.bind colorOutputEvent with
                        | Some colorEvt -> [| streamOutputEvent capped; colorEvt |]
                        | None -> [| streamOutputEvent capped |]
                else
                    // Within debounce: accumulate the diff;
                    // trailing edge will flush. Stash the dominant
                    // colour alongside so the trailing-edge tick
                    // emits both events together. Overwrite any
                    // previous PendingColor — latest pending frame
                    // wins, matching PendingDiff semantics.
                    let pendingDiff = canonical.computeDiff state.LastEmittedRowHashes
                    let dominantColor = computeColor pendingDiff.ChangedRows
                    state.PendingDiff <- ValueSome pendingDiff
                    state.PendingFrameHash <- ValueSome frameHash
                    state.PendingColor <-
                        match dominantColor with
                        | Some c -> ValueSome c
                        | None -> ValueNone
                    logger.LogDebug(
                        "Accumulated (within debounce window). FrameHash=0x{Hash:X16} Color={Color}",
                        frameHash,
                        (match dominantColor with Some c -> c | None -> "none"))
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
                // Phase A.2 — capture the pending colour BEFORE
                // resetting state so it survives the same
                // condition-block that promotes the diff.
                let pendingColor = state.PendingColor
                state.LastFrameHash <- ValueSome hash
                state.LastFlushAt <- ValueSome now
                state.PendingDiff <- ValueNone
                state.PendingFrameHash <- ValueNone
                state.PendingColor <- ValueNone
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
                    let colorEvt =
                        match pendingColor with
                        | ValueSome c -> colorOutputEvent c
                        | ValueNone -> None
                    logger.LogInformation(
                        "Emit StreamChunk (trailing-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len} Color={Color}",
                        hash, diff.ChangedRows.Length, capped.Length,
                        (match pendingColor with ValueSome c -> c | ValueNone -> "none"))
                    match colorEvt with
                    | Some evt -> [| streamOutputEvent capped; evt |]
                    | None -> [| streamOutputEvent capped |]
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
        // Phase A.2 — clear PendingColor WITHOUT emitting the
        // supplementary ErrorLine/WarningLine. Mode barriers are
        // themselves discontinuities (alt-screen toggle, vim
        // exiting); emitting an error-tone for the flushed
        // pre-barrier diff would be misleading because the user
        // already heard the live coloured output. The barrier
        // itself dispatches separately via OutputEventBuilder
        // → PathwayPump.handleModeChanged.
        state.PendingDiff <- ValueNone
        state.PendingFrameHash <- ValueNone
        state.PendingColor <- ValueNone
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
        state.PendingColor <- ValueNone
        state.LastRowHashes <- ValueNone
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()

    /// Internal — seed the pathway's diff baseline + spinner-
    /// gate "previous hashes" with the supplied canonical
    /// state's hashes. Used by `SetBaseline` (hot-switch). No
    /// emission; this is purely state-setting. The next
    /// `Consume` will compute its diff against this seeded
    /// baseline rather than against `[||]`.
    let internal seedBaseline (state: State) (canonical: CanonicalState.Canonical) : unit =
        state.LastEmittedRowHashes <- canonical.RowHashes
        // Also prime LastRowHashes so the spinner-suppress
        // "this row changed?" check on the next Consume sees
        // the seeded frame as "no rows changed yet". Without
        // this, the first Consume after Reset+SetBaseline
        // would still mark every row as "changed" against
        // ValueNone and accumulate spinner-history entries
        // for the seeded frame's content.
        state.LastRowHashes <- ValueSome canonical.RowHashes

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
            fun () -> resetState state
          SetBaseline =
            fun canonical -> seedBaseline state canonical }

    /// Test-only — expose the state for direct manipulation in
    /// the test harness. Production code never calls this.
    let internal createWithExposedState
            (parameters: Parameters)
            : DisplayPathway.T * State
            =
        let state = createState ()
        // Type annotation needed: the function returns a tuple,
        // so F#'s record-type inference can't flow back through
        // the tuple shape to identify `DisplayPathway.T` from the
        // field labels alone — without the annotation, F# picks
        // the first record-in-scope with an `Id` field (Profile)
        // and fails on Consume / OnModeBarrier / Reset. (The
        // sister `create` function below works without an
        // annotation because its return type IS DisplayPathway.T
        // directly, which seeds inference.)
        let pathway : DisplayPathway.T =
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
                fun () -> resetState state
              SetBaseline =
                fun canonical -> seedBaseline state canonical }
        pathway, state
