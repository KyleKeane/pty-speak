namespace Terminal.Core

open System
open Microsoft.Extensions.Logging

/// Stage 8e-A — selection-prompt detector. Producer side of the
/// Selection profile sub-cycle that closes
/// `docs/STAGE-7-ISSUES.md` `[output-selection]` "tool-use prompt
/// reads as flat text instead of listbox".
///
/// **What this module does.** Watches each screen snapshot for a
/// stable rectangular region with one row carrying a distinct
/// SGR background (the "highlighted" item). Emits
/// `OutputEvent`s with `SemanticCategory.SelectionShown` / `SelectionItem`
/// / `SelectionDismissed` carrying structured metadata via
/// `SelectionExtensions` keys. The companion `SelectionProfile`
/// (Cycle 29b) consumes these and emits NVDA-channel +
/// FileLogger ChannelDecisions; the UIA listbox peer (Cycle 8e-B)
/// promotes them to UIA listbox semantics.
///
/// **Detection algorithm (per-frame, two-pass).** Mirrors the
/// shape of `HeuristicPromptDetector` exactly, with the geometric
/// region detection adapted from spec `tech-plan.md` §8.1:
///
/// PASS 1 — classify every row and group into candidate regions.
///   For each row:
///   1. Render via `CanonicalState.renderRow` (sanitised +
///      trailing-blank-trimmed).
///   2. Classify: `Blank` (empty after trim) / `Highlighted`
///      (>= `HighlightCellPercentThreshold` of non-blank cells share a
///      non-baseline `Attrs.Bg` OR carry the SGR Inverse bit) /
///      `Plain` (non-blank, non-highlighted).
///   3. Walk runs of contiguous non-blank rows; a run is a
///      candidate region if it has exactly ONE Highlighted row
///      and `MinRegionRows .. MaxRegionRows` total non-blank rows.
///
/// PASS 2 — pick BOTTOMMOST candidate (Claude renders prompts at
/// screen bottom; mirrors `HeuristicPromptDetector`'s max-rowIdx
/// pattern), apply signal aggregation:
///   * Signal #1 — stable: `(now - FirstSeenAt).TotalMs >= HighlightDetectionThresholdMs`.
///   * Signal #2 — SGR-distinct: satisfied by PASS 1 construction.
///   * Signal #3 — keystroke correlation: `RecentKeystrokes` has
///     Up/Down within last `KeystrokeCorrelationWindowMs` AND
///     `SelectedRowIdx` differs from previous candidate's →
///     upgrade `Confidence` to `HeuristicSGRWithKeystroke`.
///   * Signal #4 — item separators: each item-row's rendered
///     text trim must be non-empty and non-pure-punctuation.
///
/// **Emission rules.**
/// - First time `Confidence >= MinConfidence`: emit one
///   `SelectionShown` + N `SelectionItem` events sharing one
///   correlation id (timestamp ticks).
/// - Same items + same `SelectedRowIdx`: suppress via
///   `LastEmittedSignature` gate.
/// - Same items + different `SelectedRowIdx`: emit single
///   `SelectionItem` for newly-selected row.
/// - Candidate disappears (region non-rectangular / no
///   highlighted row) for `> DismissalGraceMs`: emit
///   `SelectionDismissed` (empty Payload) + reset state.
///
/// **Sanitisation chokepoint.** Every payload runs
/// `AnnounceSanitiser.sanitise` BEFORE `OutputEvent` construction
/// per spec §B.2.4 step 5. `CanonicalState.renderRow` already
/// sanitises (per its docstring), but we re-run on item-text in
/// case the sanitiser contract changes (defense-in-depth; the
/// idempotent sanitiser pays only ~10ns when input is already
/// clean).
///
/// **Performance budget** (< 1ms per pulse per spec §Design
/// Principles): single screen scan + O(rows) pass; reuses
/// `CanonicalState.renderRow` (already cached path on the
/// PathwayPump). Mirrors `HeuristicPromptDetector.tryDetect`
/// profile.
///
/// **Threading.** Detector is mutated only on the PathwayPump
/// worker thread (single-threaded for notification consumption).
/// `tryDetect` is a pure function returning the next state
/// alongside the emitted events.
///
/// **Per-shell gating.** `tryDetect` short-circuits with
/// `([||], state)` when `shellKey` is not `"claude"`. Mirrors
/// `HeuristicPromptDetector.shellParams`. Cycle 29c (Stage 8e-A
/// scope completion) replaces this with TOML-driven per-shell
/// profile-set selection.
///
/// **Wiring status (Cycle 29a).** This file ships standalone —
/// the detector compiles, is fully unit-tested in
/// `SelectionDetectorTests`, and exports a clean public API
/// (`create` / `tryDetect` / `feedKeystroke` / `reset`). It is
/// NOT yet called from `Program.fs`; that wiring lands in Cycle
/// 29b alongside `SelectionProfile.fs`.
[<RequireQualifiedAccess>]
module SelectionDetector =

    /// Keystroke direction hint fed via `feedKeystroke`. The
    /// detector only cares about Up/Down (selection-list
    /// navigation); `Other` covers all other keys (typing,
    /// modifier-only, etc.).
    type Direction =
        | Up
        | Down
        | Other

    /// Confidence tier for an emitted selection event. Mirror
    /// of `BoundarySource` in `HeuristicPromptDetector`.
    type SelectionSource =
        | HeuristicSGR
        | HeuristicSGRWithKeystroke

    /// Convert a `SelectionSource` to its string form for
    /// `Extensions["selection.source"]`.
    let sourceToString (source: SelectionSource) : string =
        match source with
        | HeuristicSGR -> "HeuristicSGR"
        | HeuristicSGRWithKeystroke -> "HeuristicSGRWithKeystroke"

    /// Parse a `SelectionSource` from its string form. Used by
    /// the Cycle 29c TOML loader. Unknown strings fall back to
    /// `HeuristicSGR` (the more permissive default).
    let sourceOfString (s: string) : SelectionSource =
        match s with
        | "HeuristicSGRWithKeystroke" -> HeuristicSGRWithKeystroke
        | _ -> HeuristicSGR

    /// Tunable detection parameters. Stage 8e-A wires these
    /// hardcoded; Cycle 29c will load from the
    /// `[profile.selection]` TOML section.
    type Parameters =
        { /// Minimum age (ms) of a candidate region before it
          /// satisfies Signal #1 (stable region). Default 100ms
          /// per spec tech-plan §8.1.
          HighlightDetectionThresholdMs: int
          /// Minimum age (ms) of a candidate-disappearance
          /// before it triggers `SelectionDismissed`. Default
          /// 150ms — gives the parser time to absorb a
          /// transient repaint without emitting spurious
          /// dismissals.
          DismissalGraceMs: int
          /// Maximum age (ms) of a recent keystroke that
          /// still counts toward Signal #3. Default 250ms.
          KeystrokeCorrelationWindowMs: int
          /// Minimum confidence tier required for emission.
          /// `HeuristicSGR` = signals 1+2 only (more
          /// permissive; surfaces gaps quickly).
          /// `HeuristicSGRWithKeystroke` = signals 1+2+3 (more
          /// strict; useful if Signal-1+2-only false positives
          /// prove intolerable).
          MinConfidence: SelectionSource }

    /// Default parameter values per spec tech-plan §8.1 +
    /// `event-and-output-framework.md` §B.3.3.
    let defaultParameters : Parameters =
        { HighlightDetectionThresholdMs = 100
          DismissalGraceMs = 150
          KeystrokeCorrelationWindowMs = 250
          MinConfidence = HeuristicSGR }

    /// Cell percentage threshold for considering a row
    /// "highlighted": >= 40% of non-blank cells must share a
    /// non-baseline Bg (or carry the SGR Inverse bit).
    [<Literal>]
    let private HighlightCellPercentThreshold = 0.40

    /// Maximum cells to scan per row. Bounded compute per the
    /// performance budget; covers Claude's typical 80-120 col
    /// layout without scanning the entire row.
    [<Literal>]
    let private MaxCellsScanPerRow = 80

    /// Minimum / maximum candidate region span (rows). 1-row
    /// regions are too short (no list); >6-row regions are too
    /// tall (multi-line content, not a Claude prompt).
    [<Literal>]
    let private MinRegionRows = 2

    [<Literal>]
    let private MaxRegionRows = 6

    /// Maximum recent keystrokes retained for Signal #3.
    /// ~500ms of typing at 100 wpm. Bounded ring; oldest
    /// entries drop off when full.
    [<Literal>]
    let private RecentKeystrokeCapacity = 8

    /// A detected candidate region. Tracked across frames;
    /// each `tryDetect` call may extend (same region observed
    /// again, possibly with a new selectedIdx), dismiss (region
    /// disappeared and grace elapsed), or replace (different
    /// region detected).
    ///
    /// Public access is forced by F# rule that public type
    /// `T`'s field types must be at least as accessible as `T`
    /// itself (FS0410). Treat `Candidate` as effectively
    /// internal — callers should use the `create` / `tryDetect`
    /// /  `feedKeystroke` / `reset` API and not construct
    /// candidates directly.
    type Candidate =
        { TopRow: int
          BottomRow: int
          Items: string[]
          SelectedRowIdx: int
          FirstSeenAt: DateTime
          LastConfirmedAt: DateTime
          Confidence: SelectionSource }

    /// Suppression signature for the (items-hash,
    /// selectedRowIdx) gate. Identical signatures suppress
    /// re-emission; mismatched ones permit a single
    /// `SelectionItem` for the newly-selected row.
    ///
    /// Public for the same FS0410 reason as `Candidate`.
    type SelectionSignature =
        { ItemsHash: int
          SelectedRowIdx: int }

    /// Detector state. See `tryDetect` for thread-safety
    /// contract.
    type T =
        { /// Bounded ring of recent keystrokes; head is most
          /// recent. List length <= `RecentKeystrokeCapacity`.
          RecentKeystrokes: (DateTime * Direction) list
          /// Current candidate. `None` between dismissal and the
          /// next stable-region detection.
          Candidate: Candidate option
          /// Last-emitted signature. Suppresses identical
          /// re-emits.
          LastEmittedSignature: SelectionSignature option
          /// When the last candidate was confirmed visible; used
          /// to compute dismissal grace period.
          LastCandidateConfirmedAt: DateTime option
          /// Tunable parameters captured at create time.
          Parameters: Parameters }

    /// Construct a fresh detector with the supplied parameters.
    let create (parameters: Parameters) : T =
        { RecentKeystrokes = []
          Candidate = None
          LastEmittedSignature = None
          LastCandidateConfirmedAt = None
          Parameters = parameters }

    /// Clear all state. Called on shell-switch or alt-screen
    /// entry. The `Parameters` field is preserved (parameters
    /// are tied to the detector instance, not the session).
    let reset (state: T) : T =
        { state with
            RecentKeystrokes = []
            Candidate = None
            LastEmittedSignature = None
            LastCandidateConfirmedAt = None }

    /// Append a keystroke direction. Caller invokes from the
    /// keystroke handler in `Program.fs` (Cycle 29b wiring).
    /// Cycle 29a tests exercise this directly.
    let feedKeystroke
            (direction: Direction)
            (now: DateTime)
            (state: T)
            : T =
        let appended = (now, direction) :: state.RecentKeystrokes
        let bounded =
            if List.length appended <= RecentKeystrokeCapacity then
                appended
            else
                appended |> List.truncate RecentKeystrokeCapacity
        { state with RecentKeystrokes = bounded }

    let private logger =
        Logger.get "Terminal.Core.SelectionDetector"

    /// Per-shell short-circuit. Cycle 29a hardcodes
    /// claude-only; Cycle 29c will replace via TOML.
    let private shouldDetect (shellKey: string) : bool =
        shellKey = "claude"

    /// Per-row classification. Drives PASS 1's region grouping.
    type private RowClass =
        | Blank
        | Plain
        | Highlighted

    /// Test whether a cell counts as "highlighted" (non-default
    /// Bg or Inverse). The detection uses cell BACKGROUND, not
    /// foreground, since Claude's selection-highlight is a
    /// distinct background colour.
    let private isHighlighted (cell: Cell) : bool =
        if cell.Attrs.Inverse then true
        else
            match cell.Attrs.Bg with
            | Default -> false
            | Indexed _ -> true
            | Rgb _ -> true

    /// Test whether a cell is "non-blank" (the rune is anything
    /// other than space). Blank cells don't count toward the
    /// highlight-percentage denominator.
    let private isNonBlank (cell: Cell) : bool =
        cell.Ch.Value <> int ' '

    /// Classify a row by scanning up to `MaxCellsScanPerRow`
    /// cells. Returns `Blank` if no non-blank cells; `Highlighted`
    /// if >= `HighlightCellPercentThreshold` of non-blank cells
    /// are highlighted; `Plain` otherwise.
    let private classifyRow (row: Cell[]) : RowClass =
        let scanLen = min row.Length MaxCellsScanPerRow
        let mutable nonBlank = 0
        let mutable highlighted = 0
        for i in 0 .. scanLen - 1 do
            let cell = row.[i]
            if isNonBlank cell then
                nonBlank <- nonBlank + 1
                if isHighlighted cell then
                    highlighted <- highlighted + 1
        if nonBlank = 0 then
            Blank
        else
            let ratio = float highlighted / float nonBlank
            if ratio >= HighlightCellPercentThreshold then
                Highlighted
            else
                Plain

    /// Test whether trimmed text is "pure punctuation"
    /// (Signal #4 reject criterion). A row whose only contents
    /// are box-drawing or punctuation chars isn't a list item.
    let private isPurePunctuation (text: string) : bool =
        if String.IsNullOrWhiteSpace text then true
        else
            let mutable hasLetterOrDigit = false
            for ch in text do
                if Char.IsLetterOrDigit ch then
                    hasLetterOrDigit <- true
            not hasLetterOrDigit

    /// Group of contiguous non-blank rows considered a
    /// candidate. Tracks the rendered item text per row +
    /// which row carries the highlight.
    type private RegionGroup =
        { TopRow: int
          BottomRow: int
          ItemTexts: string[]
          HighlightedRowIdx: int }

    /// PASS 1 — classify every row, walk runs of non-blank rows,
    /// extract candidate regions. Returns ALL candidate regions;
    /// PASS 2 picks the bottommost. We collect them all to keep
    /// PASS 1 a pure scan; tests can introspect intermediate state
    /// if needed in future.
    ///
    /// **Ref cells over `let mutable`.** F# 9 +
    /// `TreatWarningsAsErrors=true` is strict about closures
    /// capturing locally-mutable bindings. The `flushRun`
    /// closure reads `runStart` / `highlightCountInRun` /
    /// `lastHighlightedRow`, so we use `ref` cells which always
    /// compile cleanly.
    let private findCandidateRegions
            (snapshot: Cell[][])
            : RegionGroup list =
        let rowCount = snapshot.Length
        let classifications = Array.create rowCount Blank
        for i in 0 .. rowCount - 1 do
            classifications.[i] <- classifyRow snapshot.[i]
        let regions = ResizeArray<RegionGroup>()
        let runStart = ref -1
        let highlightCountInRun = ref 0
        let lastHighlightedRow = ref -1
        let flushRun (endExclusive: int) =
            let start = !runStart
            if start >= 0 then
                let runEnd = endExclusive - 1
                let runLen = runEnd - start + 1
                if runLen >= MinRegionRows
                   && runLen <= MaxRegionRows
                   && !highlightCountInRun = 1 then
                    let itemTexts =
                        Array.init runLen (fun offset ->
                            let rowIdx = start + offset
                            (CanonicalState.renderRow snapshot rowIdx).Trim())
                    let allItemsValid =
                        itemTexts
                        |> Array.forall (fun text ->
                            not (isPurePunctuation text))
                    if allItemsValid then
                        let highlightedIdx =
                            !lastHighlightedRow - start
                        regions.Add(
                            { TopRow = start
                              BottomRow = runEnd
                              ItemTexts = itemTexts
                              HighlightedRowIdx = highlightedIdx })
        for i in 0 .. rowCount - 1 do
            match classifications.[i] with
            | Blank ->
                flushRun i
                runStart := -1
                highlightCountInRun := 0
                lastHighlightedRow := -1
            | Plain ->
                if !runStart < 0 then
                    runStart := i
            | Highlighted ->
                if !runStart < 0 then
                    runStart := i
                highlightCountInRun := !highlightCountInRun + 1
                lastHighlightedRow := i
        flushRun rowCount
        List.ofSeq regions

    /// FNV-1a 32-bit hash of the items array, used as the
    /// suppression-signature key. Cheap + collision-tolerant
    /// enough for the (typically <10 items, never repeated
    /// session-to-session) selection-prompt use case.
    let private hashItems (items: string[]) : int =
        let mutable hash = 2166136261u
        for item in items do
            for ch in item do
                hash <- (hash ^^^ uint32 (int ch)) * 16777619u
        int hash

    /// Test whether a recent keystroke (Up or Down) lies within
    /// the correlation window. Used by Signal #3.
    let private hasRecentArrowKey
            (now: DateTime)
            (windowMs: int)
            (recent: (DateTime * Direction) list)
            : bool =
        let cutoff = now - TimeSpan.FromMilliseconds(float windowMs)
        recent
        |> List.exists (fun (ts, dir) ->
            ts >= cutoff
            && (dir = Up || dir = Down))

    /// Stable producer identifier (mirrors
    /// `OutputEventBuilder.producerId = "translator"`).
    [<Literal>]
    let private producerId = "selection-detector"

    /// F# 9 strict-nullness coercion. `box x` returns
    /// `obj | null`, but `OutputEvent.Extensions` is
    /// `Map<string, obj>` (non-nullable obj). Each value we
    /// box (int / string / string[] / etc.) is provably
    /// non-null at the call site, so `nonNull` is safe — the
    /// only way `box` of a non-null primitive returns `null`
    /// is on a `null` reference type, which we never pass.
    let private boxNN (value: 'T) : obj = nonNull (box value)

    /// Construct a base `OutputEvent` for selection events.
    /// Builds via `OutputEvent.create` (v1 defaults factory)
    /// then layers Shell + CorrelationId + Extensions on top.
    /// Sanitises the payload at the entry gate per spec
    /// §B.2.4 step 5.
    let private baseEvent
            (semantic: SemanticCategory)
            (priority: Priority)
            (payload: string)
            (correlationId: int64)
            (extensions: Map<string, obj>)
            : OutputEvent =
        let evt =
            OutputEvent.create
                semantic
                priority
                producerId
                (AnnounceSanitiser.sanitise payload)
        { evt with
            Source =
                { evt.Source with
                    Shell = Some "claude"
                    CorrelationId = Some correlationId }
            Extensions = extensions }

    /// Build the initial-burst events for a freshly-detected
    /// region: one `SelectionShown` + N `SelectionItem`s.
    let private buildShownBurst
            (group: RegionGroup)
            (confidence: SelectionSource)
            (correlationId: int64)
            : OutputEvent[] =
        let itemCount = group.ItemTexts.Length
        let selectedIdx = group.HighlightedRowIdx
        let sourceStr = sourceToString confidence
        let shownExtensions =
            Map.ofList
                [ SelectionExtensions.ItemCount, boxNN itemCount
                  SelectionExtensions.SelectedIndex, boxNN selectedIdx
                  SelectionExtensions.AllItems, boxNN group.ItemTexts
                  SelectionExtensions.TopRow, boxNN group.TopRow
                  SelectionExtensions.BottomRow, boxNN group.BottomRow
                  SelectionExtensions.Source, boxNN sourceStr ]
        let shownPayload =
            sprintf "selection prompt, %d items" itemCount
        let shown =
            baseEvent
                SemanticCategory.SelectionShown
                Priority.Assertive
                shownPayload
                correlationId
                shownExtensions
        let items =
            group.ItemTexts
            |> Array.mapi (fun idx text ->
                let payload =
                    if idx = selectedIdx then
                        sprintf "selected: %s, %d of %d" text (idx + 1) itemCount
                    else
                        sprintf "%s, %d of %d" text (idx + 1) itemCount
                let itemExtensions =
                    Map.ofList
                        [ SelectionExtensions.ItemCount, boxNN itemCount
                          SelectionExtensions.SelectedIndex, boxNN idx
                          SelectionExtensions.ItemText, boxNN text
                          SelectionExtensions.Source, boxNN sourceStr ]
                baseEvent
                    SemanticCategory.SelectionItem
                    Priority.Polite
                    payload
                    correlationId
                    itemExtensions)
        Array.append [| shown |] items

    /// Build a single `SelectionItem` event for a
    /// selection-index update (same items, different
    /// selectedIdx).
    let private buildItemUpdate
            (group: RegionGroup)
            (confidence: SelectionSource)
            (correlationId: int64)
            : OutputEvent =
        let itemCount = group.ItemTexts.Length
        let selectedIdx = group.HighlightedRowIdx
        let text = group.ItemTexts.[selectedIdx]
        let sourceStr = sourceToString confidence
        let payload =
            sprintf "%s, %d of %d" text (selectedIdx + 1) itemCount
        let extensions =
            Map.ofList
                [ SelectionExtensions.ItemCount, boxNN itemCount
                  SelectionExtensions.SelectedIndex, boxNN selectedIdx
                  SelectionExtensions.ItemText, boxNN text
                  SelectionExtensions.Source, boxNN sourceStr ]
        baseEvent
            SemanticCategory.SelectionItem
            Priority.Polite
            payload
            correlationId
            extensions

    /// Build the `SelectionDismissed` event. Empty payload per
    /// the spec convention; profile decides whether to render
    /// "selection dismissed" text or stay silent.
    let private buildDismissed
            (correlationId: int64)
            : OutputEvent =
        baseEvent
            SemanticCategory.SelectionDismissed
            Priority.Assertive
            ""
            correlationId
            Map.empty

    /// Pick the bottommost candidate region (Claude renders
    /// prompts at screen bottom). Mirrors
    /// `HeuristicPromptDetector`'s max-rowIdx pattern.
    let private pickBottomCandidate
            (candidates: RegionGroup list)
            : RegionGroup option =
        match candidates with
        | [] -> None
        | _ ->
            let mutable best = List.head candidates
            for c in candidates do
                if c.BottomRow > best.BottomRow then
                    best <- c
            Some best

    /// Pure detection function. Walks the snapshot, updates
    /// internal state, returns the (events, next-state) pair.
    /// Caller (Cycle 29b's `Program.fs handleRowsChanged`)
    /// routes events into `OutputDispatcher.dispatch`.
    ///
    /// `_cursor` is captured for forward-compatibility with
    /// cursor-position refinement (Phase 2); 8e-A doesn't use it.
    let tryDetect
            (snapshot: Cell[][])
            (_cursor: int * int)
            (now: DateTime)
            (shellKey: string)
            (state: T)
            : OutputEvent[] * T =
        if not (shouldDetect shellKey) then
            [||], state
        else
            let candidates = findCandidateRegions snapshot
            let bottom = pickBottomCandidate candidates
            let parameters = state.Parameters
            let correlationId = now.Ticks
            match bottom, state.Candidate with
            | None, None ->
                // No candidate visible, none tracked; nothing to do.
                [||], state
            | None, Some _ ->
                // Region disappeared. Wait `DismissalGraceMs` before
                // emitting Dismissed; if the region returns within
                // grace, we resume tracking it.
                match state.LastCandidateConfirmedAt with
                | None ->
                    [||], state
                | Some lastSeen ->
                    let elapsed = now - lastSeen
                    if elapsed.TotalMilliseconds >= float parameters.DismissalGraceMs then
                        let dismissed = buildDismissed correlationId
                        let nextState =
                            { state with
                                Candidate = None
                                LastEmittedSignature = None
                                LastCandidateConfirmedAt = None }
                        [| dismissed |], nextState
                    else
                        [||], state
            | Some group, None ->
                // First time seeing this region. Open a candidate
                // and start the stability timer; emission waits
                // until the next call once stability elapses.
                let newCandidate =
                    { TopRow = group.TopRow
                      BottomRow = group.BottomRow
                      Items = group.ItemTexts
                      SelectedRowIdx = group.HighlightedRowIdx
                      FirstSeenAt = now
                      LastConfirmedAt = now
                      Confidence = HeuristicSGR }
                let nextState =
                    { state with
                        Candidate = Some newCandidate
                        LastCandidateConfirmedAt = Some now }
                [||], nextState
            | Some group, Some prior ->
                // Region observed again. Update confidence + emit
                // if stable + permitted by signature gate.
                let stableMs =
                    (now - prior.FirstSeenAt).TotalMilliseconds
                let isStable =
                    stableMs >= float parameters.HighlightDetectionThresholdMs
                let priorIdx = prior.SelectedRowIdx
                let newIdx = group.HighlightedRowIdx
                let arrowCorrelated =
                    priorIdx <> newIdx
                    && hasRecentArrowKey
                        now
                        parameters.KeystrokeCorrelationWindowMs
                        state.RecentKeystrokes
                let confidence =
                    if arrowCorrelated then
                        HeuristicSGRWithKeystroke
                    else
                        // Signal #3 only upgrades; once we're at
                        // Confirmed we stay there for the duration
                        // of this region.
                        match prior.Confidence with
                        | HeuristicSGRWithKeystroke -> HeuristicSGRWithKeystroke
                        | HeuristicSGR -> HeuristicSGR
                let meetsThreshold =
                    match parameters.MinConfidence, confidence with
                    | HeuristicSGR, _ -> true
                    | HeuristicSGRWithKeystroke, HeuristicSGRWithKeystroke -> true
                    | HeuristicSGRWithKeystroke, HeuristicSGR -> false
                let signature =
                    { ItemsHash = hashItems group.ItemTexts
                      SelectedRowIdx = newIdx }
                let updatedCandidate =
                    { TopRow = group.TopRow
                      BottomRow = group.BottomRow
                      Items = group.ItemTexts
                      SelectedRowIdx = newIdx
                      FirstSeenAt = prior.FirstSeenAt
                      LastConfirmedAt = now
                      Confidence = confidence }
                if not (isStable && meetsThreshold) then
                    let nextState =
                        { state with
                            Candidate = Some updatedCandidate
                            LastCandidateConfirmedAt = Some now }
                    [||], nextState
                else
                    match state.LastEmittedSignature with
                    | None ->
                        // Initial burst.
                        let events =
                            buildShownBurst group confidence correlationId
                        logger.LogDebug(
                            "SelectionDetector emitted initial burst (items={Count}, selectedIdx={Idx}, confidence={Confidence}).",
                            group.ItemTexts.Length, newIdx, sourceToString confidence)
                        let nextState =
                            { state with
                                Candidate = Some updatedCandidate
                                LastEmittedSignature = Some signature
                                LastCandidateConfirmedAt = Some now }
                        events, nextState
                    | Some priorSig when priorSig = signature ->
                        // Same items + same selectedIdx; suppress.
                        let nextState =
                            { state with
                                Candidate = Some updatedCandidate
                                LastCandidateConfirmedAt = Some now }
                        [||], nextState
                    | Some priorSig when priorSig.ItemsHash = signature.ItemsHash ->
                        // Same items, different selectedIdx.
                        let update =
                            buildItemUpdate group confidence correlationId
                        let nextState =
                            { state with
                                Candidate = Some updatedCandidate
                                LastEmittedSignature = Some signature
                                LastCandidateConfirmedAt = Some now }
                        [| update |], nextState
                    | Some _ ->
                        // Different items entirely; treat as a
                        // fresh selection. Re-emit the full burst.
                        let events =
                            buildShownBurst group confidence correlationId
                        let nextState =
                            { state with
                                Candidate = Some updatedCandidate
                                LastEmittedSignature = Some signature
                                LastCandidateConfirmedAt = Some now }
                        events, nextState
