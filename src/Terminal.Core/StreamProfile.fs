namespace Terminal.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging

/// Stage 8b — Stream profile (Coalescer absorbs as a Profile
/// instance).
///
/// The four algorithms (per-row hash + frame-hash dedup, the
/// per-`(rowIdx, hash)` and cross-row spinner gates, leading-
/// and trailing-edge debounce, alt-screen flush barrier) move
/// out of `Coalescer.fs` and into this module. Constants that
/// were Stage 7 module globals (debounceWindow / spinnerWindow
/// / spinnerThreshold / maxAnnounceChars) become per-instance
/// parameters per the PR-N substrate-cleanup contract previously
/// docstring'd at `Coalescer.fs:82-99`.
///
/// `Coalescer.fs` retains: the `CoalescedNotification` DU
/// (algorithm intermediate type), and the five pure helpers
/// (`hashRowContent`, `hashRow`, `hashAttrs`, `hashFrame`,
/// `renderRows`). Tests that pin the algorithm internals
/// (`tests/Tests.Unit/StreamProfileTests.fs`, renamed from
/// `CoalescerTests.fs`) call `StreamProfile.processRowsChanged`
/// / `onTimerTick` / `onModeChanged` / `onParserError` directly
/// against a fresh State + explicit Parameters; the Profile
/// record's `Apply` + `Tick` are constructed in `create` and
/// wire those algorithms to the dispatcher.
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.3 (Profile abstraction). The spec is silent on
/// time-driven flush; 8b adds `Profile.Tick` and a multi-pair
/// `Apply` return type as substrate extensions per the
/// `OutputEventTypes.fs` Profile docstring.
module StreamProfile =

    /// Stable profile identifier registered with the dispatcher's
    /// `ProfileRegistry`. The 9c TOML config keys
    /// `[profile.stream]` on this string.
    [<Literal>]
    let id: ProfileId = "stream"

    /// Per-instance Stream-profile parameters. Caller-supplied
    /// at construction time so different callers (test harness,
    /// future per-shell mappings, the next sub-stage's TOML
    /// loader) can override the defaults independently. The PR-N
    /// substrate-cleanup contract: these were Stage 7 module
    /// globals; Stage 8b lifts them to per-instance parameters
    /// without changing the values themselves. The 9c TOML loader
    /// will override these per the `[profile.stream]` section
    /// when it ships.
    type Parameters =
        { DebounceWindowMs: int
          SpinnerWindowMs: int
          SpinnerThreshold: int
          MaxAnnounceChars: int }

    /// Stage 7 / PR-N defaults. Stage 8b ships behaviour-identical
    /// with these values; 8b.2's TOML loader will override per
    /// `[profile.stream]` when present.
    let defaultParameters: Parameters =
        { DebounceWindowMs = 200
          SpinnerWindowMs = 1000
          SpinnerThreshold = 5
          MaxAnnounceChars = 500 }

    /// Spinner-suppression key: the per-row hash plus the row
    /// index it came from. A spinner that overwrites the same
    /// cell with cycling characters (`|/-\` etc.) generates the
    /// same `(rowIdx, hash)` tuple repeatedly across each cycle,
    /// which the per-key history catches once the recurrence
    /// count crosses the threshold.
    type private SpinnerKey = int * uint64

    /// The Stream profile's mutable state. One instance per
    /// `create` call (per shell session in production); no
    /// concurrent access (the WPF Dispatcher serialises Apply
    /// + Tick invocations on the drain task / TickPump task).
    /// PR-M's `LastRowHashes` + `HashHistory` are preserved
    /// verbatim — the cross-row spinner gate logic is unchanged
    /// across the 8b refactor.
    type State =
        { mutable LastFrameHash: uint64 voption
          mutable LastFlushAt: DateTimeOffset voption
          mutable PendingFrame: Cell[][] voption
          mutable PendingHash: uint64 voption
          mutable LastRowHashes: uint64[] voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          HashHistory: Dictionary<uint64, ResizeArray<DateTimeOffset>> }

    /// Construct a fresh State with all mutables `ValueNone` and
    /// empty dictionaries.
    let internal createState () : State =
        { LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingFrame = ValueNone
          PendingHash = ValueNone
          LastRowHashes = ValueNone
          PerRowHistory = Dictionary()
          HashHistory = Dictionary() }

    /// Trim history older than `parameters.SpinnerWindowMs` from
    /// BOTH dictionaries. Mutates in place.
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

    /// Walk all per-row hashes and decide whether either spinner
    /// gate should suppress. PR-M change-detection: only count
    /// observations on row-hash CHANGES, not on every row.
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

    /// Apply the announce-length cap (Stage 7 PR-H stopgap, GitHub
    /// issue #139). 8b lifts the cap from the Stage 7 drain
    /// (Program.fs ~916-950) into the Stream profile per the PR-N
    /// docstring contract; the threshold remains user-configurable
    /// via Parameters.MaxAnnounceChars in the 9c TOML loader.
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

    /// Decide what (if anything) to emit for an incoming
    /// `RowsChanged` snapshot. Computes hashes, applies dedup +
    /// spinner + debounce, returns 0 or 1 `CoalescedNotification`.
    /// Public-internal for the test harness — production code
    /// reaches it via `create`'s `Apply` closure.
    let internal processRowsChanged
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            (snapshot: Cell[][])
            : Coalescer.CoalescedNotification list =
        let logger = Logger.get "Terminal.Core.StreamProfile.processRowsChanged"
        let rowHashes =
            Array.init snapshot.Length (fun i -> Coalescer.hashRow i snapshot.[i])
        let contentHashes =
            Array.init snapshot.Length (fun i -> Coalescer.hashRowContent snapshot.[i])
        let frameHash =
            let mutable h = 0UL
            for rh in rowHashes do h <- h ^^^ rh
            h
        match state.LastFrameHash with
        | ValueSome prev when prev = frameHash ->
            logger.LogDebug(
                "Suppressed (frame-dedup). FrameHash=0x{Hash:X16}", frameHash)
            []
        | _ ->
            gcHistory parameters state now
            let suppress =
                isSpinnerSuppressed parameters state now rowHashes contentHashes
            state.LastRowHashes <- ValueSome rowHashes
            if suppress then
                state.LastFrameHash <- ValueSome frameHash
                logger.LogDebug(
                    "Suppressed (spinner). FrameHash=0x{Hash:X16}", frameHash)
                []
            else
                let debounceWindow =
                    TimeSpan.FromMilliseconds(float parameters.DebounceWindowMs)
                let elapsed =
                    match state.LastFlushAt with
                    | ValueNone -> debounceWindow + debounceWindow
                    | ValueSome t -> now - t
                if elapsed >= debounceWindow then
                    state.LastFrameHash <- ValueSome frameHash
                    state.LastFlushAt <- ValueSome now
                    state.PendingFrame <- ValueNone
                    state.PendingHash <- ValueNone
                    let rendered = Coalescer.renderRows snapshot |> capAnnounce parameters
                    logger.LogInformation(
                        "Emit OutputBatch (leading-edge). FrameHash=0x{Hash:X16} TextLen={Len}",
                        frameHash, rendered.Length)
                    [ Coalescer.OutputBatch rendered ]
                else
                    state.PendingFrame <- ValueSome snapshot
                    state.PendingHash <- ValueSome frameHash
                    logger.LogDebug(
                        "Accumulated (within debounce window). FrameHash=0x{Hash:X16}",
                        frameHash)
                    []

    /// Trailing-edge timer tick. If anything is pending and the
    /// debounce window has elapsed, emit it.
    let internal onTimerTick
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            : Coalescer.CoalescedNotification list =
        let logger = Logger.get "Terminal.Core.StreamProfile.onTimerTick"
        match state.PendingFrame, state.PendingHash with
        | ValueSome snapshot, ValueSome hash ->
            let debounceWindow =
                TimeSpan.FromMilliseconds(float parameters.DebounceWindowMs)
            let elapsed =
                match state.LastFlushAt with
                | ValueNone -> debounceWindow
                | ValueSome t -> now - t
            if elapsed >= debounceWindow then
                state.LastFrameHash <- ValueSome hash
                state.LastFlushAt <- ValueSome now
                state.PendingFrame <- ValueNone
                state.PendingHash <- ValueNone
                let rendered = Coalescer.renderRows snapshot |> capAnnounce parameters
                logger.LogInformation(
                    "Emit OutputBatch (trailing-edge). FrameHash=0x{Hash:X16} TextLen={Len}",
                    hash, rendered.Length)
                [ Coalescer.OutputBatch rendered ]
            else
                []
        | _ -> []

    /// Mode-change barrier: flush any pending accumulator
    /// (synthesising a snapshot from the saved pending frame),
    /// reset frame-hash + spinner state, then pass through the
    /// ModeChanged signal so the channel can announce the
    /// transition. Algorithm preserved verbatim from the Stage 7
    /// `Coalescer.onModeChanged`; the per-instance `parameters`
    /// argument is unused here today (no parameter influences
    /// mode-barrier semantics) but the signature is kept aligned
    /// with the other algorithm functions for symmetry.
    let internal onModeChanged
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            (flag: TerminalModeFlag)
            (value: bool)
            : Coalescer.CoalescedNotification list =
        let flushed =
            match state.PendingFrame with
            | ValueSome snapshot ->
                state.PendingFrame <- ValueNone
                state.PendingHash <- ValueNone
                let rendered =
                    Coalescer.renderRows snapshot |> capAnnounce parameters
                [ Coalescer.OutputBatch rendered ]
            | ValueNone -> []
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueSome now
        state.LastRowHashes <- ValueNone
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()
        flushed @ [ Coalescer.ModeBarrier (flag, value) ]

    /// Pass-through for parser errors. No coalescing — errors are
    /// immediate by design (they signal something the user needs
    /// to know about now).
    let internal onParserError (message: string) : Coalescer.CoalescedNotification list =
        [ Coalescer.ErrorPassthrough (AnnounceSanitiser.sanitise message) ]

    /// Build a streaming-output OutputEvent for a flushed batch
    /// of post-coalesce text. The synthesized event carries
    /// `Semantic = StreamChunk` so the NvdaChannel routes it to
    /// `ActivityIds.output` regardless of which input event
    /// triggered the flush (a normal RowsChanged, a mode change
    /// flushing pending content, or a Tick-driven trailing-edge
    /// flush).
    let private streamOutputEvent (text: string) : OutputEvent =
        OutputEvent.create
            SemanticCategory.StreamChunk
            Priority.Polite
            id
            text

    /// Build a parser-error OutputEvent.
    let private parserErrorEvent (sanitised: string) : OutputEvent =
        OutputEvent.create
            SemanticCategory.ParserError
            Priority.Background
            id
            (sprintf "Terminal parser error: %s" sanitised)

    /// Build a NVDA-channel decision rendering the supplied text.
    let private nvdaTextDecision (text: string) : ChannelDecision =
        { Channel = NvdaChannel.id
          Render = RenderText text }

    /// Translate a `CoalescedNotification` into an
    /// `(effectiveEvent, ChannelDecision[])` pair the dispatcher
    /// can route. The effectiveEvent is the synthesised
    /// OutputEvent that NvdaChannel reads `Semantic` from for
    /// activity-ID mapping.
    let private coalescedToPair
            (inputEvent: OutputEvent)
            (coalesced: Coalescer.CoalescedNotification)
            : OutputEvent * ChannelDecision[] =
        match coalesced with
        | Coalescer.OutputBatch text ->
            let event = streamOutputEvent text
            event, [| nvdaTextDecision text |]
        | Coalescer.ErrorPassthrough s ->
            let event = parserErrorEvent s
            event, [| nvdaTextDecision event.Payload |]
        | Coalescer.ModeBarrier _ ->
            // The mode barrier reuses the input event's Semantic
            // / Priority (AltScreenEntered / ModeBarrier with the
            // appropriate Priority) so NvdaChannel routes via
            // `ActivityIds.mode`. Stage 5's barrier announcement
            // is the empty string per `Types.fs:290-294`; the
            // empty payload survives behaviour-identically.
            inputEvent, [| nvdaTextDecision "" |]

    /// Construct a Stream profile instance. `screen` is captured
    /// in the closure so `Apply` can call `screen.SnapshotRows`
    /// when handling raw `StreamChunk` events. `parameters` is
    /// captured so the algorithm functions read consistent
    /// thresholds even if the Parameters record is later mutated
    /// (today nothing mutates it, but the closure-capture seals
    /// the contract).
    let create (parameters: Parameters) (screen: Screen) : Profile =
        let state = createState ()
        let lastSnapshotSeq = ref -1L
        { Id = id
          Apply =
            fun event ->
                let now = DateTimeOffset.UtcNow
                match event.Semantic with
                | SemanticCategory.StreamChunk ->
                    // Raw rows-changed input: read the screen,
                    // dedup by sequence number, run the algorithm.
                    let seq, snapshot = screen.SnapshotRows(0, screen.Rows)
                    if seq = lastSnapshotSeq.Value then
                        [||]
                    else
                        lastSnapshotSeq.Value <- seq
                        let coalesced =
                            processRowsChanged parameters state now snapshot
                        coalesced
                        |> List.map (coalescedToPair event)
                        |> List.toArray
                | SemanticCategory.ParserError ->
                    // Parser errors come through pre-sanitised
                    // (the producer in Program.fs's TranslatorPump
                    // sanitises before building the OutputEvent).
                    // Apply produces the error decision directly
                    // — no internal coalescing.
                    [|
                        event,
                        [| nvdaTextDecision event.Payload |]
                    |]
                | SemanticCategory.AltScreenEntered
                | SemanticCategory.ModeBarrier ->
                    // Mode change: flush pending stream content
                    // + emit the barrier. The flag/value passed
                    // to onModeChanged are SYNTHETIC — the
                    // original (TerminalModeFlag, bool) was lost
                    // in the spec-D.2 mapping in TranslatorPump.
                    // The reset behaviour is the same regardless
                    // of which mode flipped, so any synthetic
                    // value works; we use (AltScreen, true) for
                    // AltScreenEntered and (AltScreen, false)
                    // for ModeBarrier.
                    let flag = TerminalModeFlag.AltScreen
                    let value = event.Semantic = SemanticCategory.AltScreenEntered
                    let coalesced =
                        onModeChanged parameters state now flag value
                    coalesced
                    |> List.map (coalescedToPair event)
                    |> List.toArray
                | _ ->
                    // Other semantic categories (SelectionShown /
                    // BellRang / etc.) don't have a Stream-profile
                    // producer in 8b. Pass through with a single
                    // pair containing the input event and a
                    // matching RenderText decision. Future profiles
                    // (8d Earcon, 8e Selection) will produce these
                    // and the Stream profile will return [||] for
                    // them (so they don't double-up on NVDA).
                    [|
                        event,
                        [| nvdaTextDecision event.Payload |]
                    |]
          Tick =
            fun now ->
                let coalesced = onTimerTick parameters state now
                coalesced
                |> List.map (fun c ->
                    // Tick only emits OutputBatch (trailing-edge
                    // flush of pending stream content), but we
                    // pattern-match defensively. The synthesised
                    // event for OutputBatch carries Semantic =
                    // StreamChunk so NvdaChannel routes via
                    // ActivityIds.output.
                    match c with
                    | Coalescer.OutputBatch text ->
                        let event = streamOutputEvent text
                        event, [| nvdaTextDecision text |]
                    | Coalescer.ErrorPassthrough _
                    | Coalescer.ModeBarrier _ ->
                        // Tick never produces these; defensive
                        // fall-through.
                        let event = streamOutputEvent ""
                        event, [||])
                |> List.toArray
          Reset =
            fun () ->
                state.LastFrameHash <- ValueNone
                state.LastFlushAt <- ValueNone
                state.PendingFrame <- ValueNone
                state.PendingHash <- ValueNone
                state.LastRowHashes <- ValueNone
                state.PerRowHistory.Clear()
                state.HashHistory.Clear()
                lastSnapshotSeq.Value <- -1L }
