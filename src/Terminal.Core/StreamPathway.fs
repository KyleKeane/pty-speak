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
    /// Backspace policy — what `computeRowSuffixDelta` does
    /// when a row shrinks (backspace, line clear, Ctrl+W
    /// word-delete, etc.). PR #168 (Tier 1 parameters from
    /// `docs/USER-SETTINGS.md`'s "Suffix-diff parameters"
    /// section). Default `AnnounceDeletedCharacter` replaces
    /// PR #166's silent-on-shrink default, which assumed
    /// NVDA's keyboard echo would handle backspace audibility
    /// — confirmed wrong on 2026-05-06 release-build
    /// validation.
    ///
    /// The case name `SuppressShrink` (rather than `Silent`)
    /// avoids collision with `EditDelta.Silent`. The TOML key
    /// for this case stays `"silent"`; the rename is internal
    /// only.
    type BackspacePolicy =
        /// PR #166's original behaviour: shrink → no emit.
        /// Useful only if NVDA gains a "speak deletions"
        /// setting in the future. TOML value: `"silent"`.
        | SuppressShrink
        /// Default. Row shrink emits the deleted segment
        /// (the part of `previousText` beyond the longest-
        /// common-prefix with `currentText`). For a single
        /// backspace, that's one character; for Ctrl+W it
        /// would be the deleted word. TOML value:
        /// `"announce_deleted_character"`.
        | AnnounceDeletedCharacter
        /// Reserved for future word-boundary-aware behaviour.
        /// In v1.1, treated identically to
        /// `AnnounceDeletedCharacter` (the deleted segment is
        /// emitted as-is). TOML value: `"announce_deleted_word"`.
        | AnnounceDeletedWord

    /// Mode-barrier flush policy — what `onModeBarrier` does
    /// with any pending diff at shell-switch / alt-screen /
    /// mode-change. PR #168. Default `SummaryOnly` replaces
    /// PR #166's verbose flush — confirmed unpleasant on
    /// 2026-05-06 release-build validation (1200-character
    /// flushes of stale shell history).
    type ModeBarrierFlushPolicy =
        /// PR #166's original behaviour. Emit the previous
        /// shell's full screen as a flush event before the new
        /// shell's startup. Preserves verbose context at
        /// discontinuities; jarring in practice.
        | Verbose
        /// Default. Suppress the previous-shell flush; rely
        /// on the App-layer "switching to X" announce + the
        /// new shell's startup output for context. Subsumes
        /// strategic backlog items 23 + 24.
        | SummaryOnly
        /// Silent on barrier; the new shell's startup output
        /// is the user's only signal that anything changed.
        /// At the StreamPathway level, identical behaviour to
        /// `SummaryOnly`; the difference materialises at the
        /// App-layer (which announces "switching to X" under
        /// `SummaryOnly` but stays silent under `Suppressed`).
        | Suppressed

    /// Cycle 35a — substrate mode selection. Controls which
    /// path produces the announce string in
    /// `processCanonicalState`:
    ///
    /// - `Linear` — read from the LinearTextStream producer
    ///   (Cycle 34a; RFC 0001) via `assembleSuffixFromStream`.
    ///   The byte stream is the substrate-of-truth; suffix
    ///   tracking via `LastLinearWatermark`.
    /// - `ScreenDiff` — existing PR #166 row-by-row suffix
    ///   diff over `Cell[][]` snapshots via
    ///   `assembleSuffixPayload`. Baselines via
    ///   `LastEmittedRowHashes` + `LastEmittedRowText`.
    /// - `Auto` — `Linear` for non-alt-screen frames,
    ///   `ScreenDiff` for alt-screen TUIs (where the grid is
    ///   canonical per CORE-ABSTRACTION-BOUNDARY.md §1.4).
    ///
    /// Default in 35a: `ScreenDiff` (existing behaviour);
    /// 35b flips default to `Auto` after the §3 advanced-CMD
    /// 8-row matrix passes manual NVDA validation. TOML key:
    /// `[pathway.stream] substrate_mode` =
    /// "linear" | "screen-diff" | "auto".
    type SubstrateMode =
        | Linear
        | ScreenDiff
        | Auto

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
          ColorDetection: bool
          /// PR #168 — when the row-level diff reports more
          /// changed rows than this threshold in a single
          /// frame, the suffix-diff stage bypasses per-row LCP
          /// and emits the full `ChangedText` (the pre-PR-#166
          /// verbose path). Catches scrolls, screen clears,
          /// and TUI repaints with one heuristic. Default 3 is
          /// conservative: typing produces 1 changed row;
          /// Enter usually produces 1-2; small pastes 2-3.
          /// Scrolls affect ~30 rows — engages the fallback.
          /// TOML key: `[pathway.stream] bulk_change_threshold`.
          BulkChangeThreshold: int
          /// PR #168 — what to do when a row shrinks (backspace
          /// etc.). See `BackspacePolicy`. Default
          /// `AnnounceDeletedCharacter`. TOML key:
          /// `[pathway.stream] backspace_policy`.
          BackspacePolicy: BackspacePolicy
          /// PR #168 — what to do at mode-barrier flush time.
          /// See `ModeBarrierFlushPolicy`. Default
          /// `SummaryOnly`. TOML key:
          /// `[pathway.stream] mode_barrier_flush_policy`.
          ModeBarrierFlushPolicy: ModeBarrierFlushPolicy
          /// Cycle 35a — substrate mode selection. See
          /// `SubstrateMode` DU above. Default in 35a:
          /// `ScreenDiff` (existing behaviour). TOML key:
          /// `[pathway.stream] substrate_mode`.
          SubstrateMode: SubstrateMode }

    let defaultParameters: Parameters =
        { DebounceWindowMs = 200
          SpinnerWindowMs = 1000
          SpinnerThreshold = 5
          MaxAnnounceChars = 500
          ColorDetection = true
          BulkChangeThreshold = 3
          BackspacePolicy = AnnounceDeletedCharacter
          ModeBarrierFlushPolicy = SummaryOnly
          // Cycle 35b — flipped from `ScreenDiff` to `Auto` so
          // all users get linear-substrate announces for non-
          // alt-screen content + screen-diff for alt-screen TUIs
          // without a TOML opt-in. Users who hit a Linear-mode
          // regression can revert via
          // `[pathway.stream] substrate_mode = "screen-diff"`.
          // See `docs/USER-SETTINGS.md` for details.
          SubstrateMode = Auto }

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
          /// Per-row rendered text at the moment of last emit.
          /// PR #166 — added for sub-row suffix-diff (the
          /// announcement payload-assembly stage compares each
          /// changed row's current text against this baseline
          /// to compute the longest-common-prefix and emit only
          /// the suffix that's new since last emit). Updated
          /// at exactly the same conditional-branch sites as
          /// `LastEmittedRowHashes` — leading-edge emit,
          /// trailing-edge tick, mode-barrier flush, baseline
          /// seed, and `resetState`. Empty array means "no
          /// emit history yet"; the next emit treats every row
          /// as Initial (its full text is the suffix).
          mutable LastEmittedRowText: string[]
          mutable LastFrameHash: uint64 voption
          mutable LastFlushAt: DateTimeOffset voption
          mutable PendingDiff: CanonicalState.CanonicalDiff voption
          mutable PendingFrameHash: uint64 voption
          mutable PendingColor: string voption
          /// PR #166 — snapshot reference held during the
          /// debounce-accumulate window so the trailing-edge
          /// tick can compute the suffix-diff against the
          /// latest snapshot without re-receiving it from the
          /// pump. Cleared at every emit + reset site
          /// alongside `PendingDiff`. Reference-only (no copy);
          /// stays alive only for the debounce window.
          mutable PendingSnapshot: Cell[][] voption
          mutable LastRowHashes: uint64[] voption
          /// Cycle 35a — last byte offset emitted via the
          /// linear substrate path (Cycle 34a producer's
          /// committed buffer). Updated after every successful
          /// linear-mode emit; reset to 0 in `resetState`.
          /// Independent of `LastEmittedRowHashes` (which
          /// gates the screen-diff path). When SubstrateMode
          /// resolves to Linear, `assembleSuffixFromStream`
          /// reads `LinearTextStream.suffixSince` from this
          /// watermark and updates it on emit.
          mutable LastLinearWatermark: int64
          /// Cycle 40 — the most-recent `PromptBoundaryData`
          /// received via `OnPromptBoundary`. Consumed at the
          /// next StreamChunk emit: if the boundary's
          /// `MatchedRowText` appears at the tail of the
          /// payload, the pathway splits the payload into an
          /// output portion (StreamChunk → output panel) and a
          /// prompt portion (PromptDetected → input panel).
          /// Cleared after consumption (single-shot per
          /// boundary detection). `ValueNone` is the steady
          /// state.
          mutable LastPromptBoundary: PromptBoundaryData voption
          PerRowHistory: Dictionary<SpinnerKey, ResizeArray<DateTimeOffset>>
          HashHistory: Dictionary<uint64, ResizeArray<DateTimeOffset>> }

    let internal createState () : State =
        { LastEmittedRowHashes = [||]
          LastEmittedRowText = [||]
          LastFrameHash = ValueNone
          LastFlushAt = ValueNone
          PendingDiff = ValueNone
          PendingFrameHash = ValueNone
          PendingColor = ValueNone
          PendingSnapshot = ValueNone
          LastRowHashes = ValueNone
          LastLinearWatermark = 0L
          LastPromptBoundary = ValueNone
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
            // Cycle 38a-followup — `Ctrl+Shift+;` was retired in
            // Cycle 25b-1a. `Ctrl+Shift+D` bundles the diagnostic
            // snapshot, whose `--- FILELOGGER ACTIVE LOG ---`
            // section carries the UNTRUNCATED payload via
            // `FileLoggerChannel` (the announce layer's truncation
            // doesn't gate the log layer). The bundle is copied to
            // clipboard for paste-back to triage chat.
            sprintf
                "%s ...announcement truncated; %d more characters available — press Ctrl+Shift+D to copy the diagnostic bundle (full payload in the FileLogger log section)."
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

    /// Cycle 40 — build a PromptDetected event carrying the
    /// prompt-row text in `Extensions["prompt.text"]`. The
    /// event's Payload is EMPTY by convention (the empty-
    /// payload trick from Cycle 8d.2 / 29b) so `NvdaChannel`
    /// skips the auto-announce. FileLogger logs the
    /// structured payload (with the prompt text in
    /// Extensions) for the audit trail. Routed to
    /// `ActivityIds.inputPanel` per `NvdaChannel`'s mapping.
    let private promptDetectedFromText (promptText: string) : OutputEvent =
        let baseEvt =
            OutputEvent.create
                SemanticCategory.PromptDetected
                Priority.Polite
                id
                ""
        // F# 9 strict-nullness: `box` returns `obj | null`, but the
        // Extensions map's value type is `obj` (non-nullable).
        // Mirrors the `boxNN` helper pattern from SelectionDetector
        // / SelectionProfile (Cycle 29a/b) per CLAUDE.md's "FS3261
        // is the canonical CI failure" gotcha.
        { baseEvt with
            Extensions =
                Map.ofList
                    [ "prompt.text", nonNull (box promptText) ] }

    /// Cycle 40 — split a StreamChunk payload at the prompt
    /// boundary, when a boundary is cached and its
    /// `MatchedRowText` is a SUFFIX of the payload. Returns
    /// `(outputPortion, promptPortion)` where `promptPortion`
    /// is `None` when no split applies (the caller emits the
    /// payload as a single StreamChunk).
    ///
    /// **Match rule.** The boundary's `MatchedRowText` (e.g.
    /// `"C:\\path>"`) must appear at the END of the payload,
    /// possibly preceded by whitespace / CRLF. The pathway
    /// strips trailing whitespace after `MatchedRowText` when
    /// computing the split point so a payload like
    /// `"hello\r\nC:\\path>"` splits at the line break.
    let internal splitAtPromptBoundary
            (payload: string)
            (boundary: PromptBoundaryData)
            : string * string option =
        match boundary.MatchedRowText with
        | None -> payload, None
        | Some promptText when promptText.Length = 0 ->
            payload, None
        | Some promptText ->
            // Look for the prompt text near the tail. Trim
            // trailing whitespace from the payload first so a
            // payload like "hello\r\nC:\\path> " still splits
            // at the prompt's start.
            let trimmedPayload = payload.TrimEnd()
            if trimmedPayload.EndsWith(promptText) then
                let splitIdx = trimmedPayload.Length - promptText.Length
                let outputPortion =
                    if splitIdx <= 0 then ""
                    else trimmedPayload.Substring(0, splitIdx).TrimEnd()
                outputPortion, Some promptText
            else
                payload, None

    /// Cycle 40 — build the OutputEvent array for an emitted
    /// payload, accounting for the cached prompt boundary and
    /// any dominant-colour event. Centralises the split logic
    /// used by all three emit sites (leading-edge, trailing-
    /// edge tick, mode-barrier flush).
    ///
    /// **Order of events** matters for FileLogger ordering:
    /// `[streamChunk; promptDetected; color]`. NVDA dispatches
    /// each independently; the order is purely for the audit
    /// trail.
    let private buildEmittedEvents
            (state: State)
            (capped: string)
            (dominantColor: OutputEvent option)
            : OutputEvent[] =
        let outputPortion, promptPortion =
            match state.LastPromptBoundary with
            | ValueSome boundary ->
                let result = splitAtPromptBoundary capped boundary
                // Single-shot consumption: clear the cache so
                // a later StreamChunk that doesn't contain the
                // prompt doesn't accidentally trigger a split.
                state.LastPromptBoundary <- ValueNone
                result
            | ValueNone -> capped, None
        let events = ResizeArray<OutputEvent>()
        if not (String.IsNullOrEmpty outputPortion) then
            events.Add(streamOutputEvent outputPortion)
        match promptPortion with
        | Some promptText -> events.Add(promptDetectedFromText promptText)
        | None -> ()
        match dominantColor with
        | Some evt -> events.Add(evt)
        | None -> ()
        events.ToArray()

    // -------------------------------------------------------------
    // Sub-row suffix-diff (PR #166).
    //
    // Goal: when row N of the screen changes from "> echo h" to
    // "> echo hi", emit only the new "i" rather than the full
    // "> echo hi" again. The current row-level diff
    // (CanonicalState.computeDiff) tells us WHICH rows changed
    // and gives us the ChangedText of all of them concatenated.
    // The suffix-diff stage takes that information PLUS the
    // per-row text we last emitted (cached in
    // `state.LastEmittedRowText`) and produces the cumulative
    // new content per row.
    //
    // Algorithm: per-row longest-common-prefix. If a row shrank
    // (backspace) we stay silent. If too many rows changed at
    // once (scroll, screen-clear, TUI repaint), we fall back to
    // emitting the full ChangedText — see BulkChangeThreshold.
    //
    // Spec: docs/SESSION-HANDOFF.md vocabulary glossary stages
    // 8 (sub-row suffix detection), 8b (bulk-change fallback),
    // and 9 (announcement payload assembly).
    // -------------------------------------------------------------

    /// Bulk-change fallback threshold. If the row-level diff
    /// reports more rows changed than this, we bypass the
    /// per-row suffix-diff stage and emit the full ChangedText
    /// (the previous verbose behaviour). Catches scroll, screen
    /// clears, and TUI repaints with one heuristic — the
    /// maintainer benefits from full context at those
    /// discontinuities, where suffix-diff would produce
    /// gibberish from a stale baseline.
    ///
    /// 3 is conservative: typing produces 1 changed row;
    /// Enter usually produces 1-2; small pastes 2-3 — they
    /// all stay on the suffix-diff path. Scroll affects ~30
    /// rows; screen clear all of them — both engage the
    /// fallback.
    ///
    /// PR #168 — moved from a top-level `let private` constant
    /// to `Parameters.BulkChangeThreshold`. The default value
    /// (3) lives in `defaultParameters` above; the constant
    /// binding is removed to avoid two sources of truth.

    /// Result of the sub-row suffix-diff stage for one row.
    type internal EditDelta =
        /// Suffix to announce. Always non-empty when this case
        /// is selected. May be a single character ("i" after
        /// typing 'i'), a multi-character segment, or the full
        /// row text (for first-emit / Initial cases).
        | Suffix of string
        /// No announcement for this row. Used when the row is
        /// unchanged, shrank (backspace; rely on NVDA keyboard
        /// echo), or differs only in attributes (no displayable
        /// text difference).
        | Silent

    /// Length of the longest common prefix of two strings.
    /// Returns 0 for "no common prefix" (including when either
    /// input is empty), `min(a.Length, b.Length)` when one is
    /// a prefix of the other.
    let internal longestCommonPrefixLength (a: string) (b: string) : int =
        let n = min a.Length b.Length
        let mutable i = 0
        while i < n && a.[i] = b.[i] do
            i <- i + 1
        i

    /// Compute the EditDelta for one row. Pure function;
    /// doesn't touch state. Cases:
    /// - identical text → `Silent` (no displayable change)
    /// - previous empty → `Suffix current` (Initial — first
    ///   time we've emitted this row)
    /// - current shorter than previous (Shrink — backspace,
    ///   line clear, Ctrl+W) → behaviour depends on
    ///   `backspacePolicy`:
    ///     - `SuppressShrink`: returns `Silent` (PR #166's
    ///       behaviour; relies on NVDA keyboard echo for
    ///       backspace feedback, which doesn't actually fire
    ///       for screen-content shrinks)
    ///     - `AnnounceDeletedCharacter` /
    ///       `AnnounceDeletedWord`: returns `Suffix
    ///       deletedText` where `deletedText` is the part of
    ///       `previousText` beyond the longest-common-prefix
    /// - common prefix covers all of current → `Silent`
    ///   (attribute-only differences land here when the
    ///   per-row hash differs but the rendered text is the
    ///   same)
    /// - otherwise → `Suffix (current beyond common prefix)`
    let internal computeRowSuffixDelta
            (backspacePolicy: BackspacePolicy)
            (currentText: string)
            (previousText: string)
            : EditDelta
            =
        if currentText = previousText then
            Silent
        elif previousText.Length = 0 then
            Suffix currentText
        elif currentText.Length < previousText.Length then
            // Shrink case — apply policy.
            match backspacePolicy with
            | SuppressShrink -> Silent
            | AnnounceDeletedCharacter | AnnounceDeletedWord ->
                let commonLen = longestCommonPrefixLength currentText previousText
                let deleted = previousText.Substring(commonLen)
                if String.IsNullOrEmpty deleted then Silent
                else Suffix deleted
        else
            let commonLen = longestCommonPrefixLength currentText previousText
            if commonLen = currentText.Length then
                Silent
            else
                Suffix (currentText.Substring(commonLen))

    /// Render every row of `snapshot` to its announcement-text
    /// form. Used when updating `state.LastEmittedRowText` at
    /// emit sites — caches every row's current text so the
    /// next emit can compute per-row suffix-diffs against a
    /// known baseline. Cost: O(rows × cols) on every emit;
    /// cheap given the 30×120 default screen size.
    let private renderAllRows (snapshot: Cell[][]) : string[] =
        let result = Array.zeroCreate<string> snapshot.Length
        for i in 0 .. snapshot.Length - 1 do
            result.[i] <- CanonicalState.renderRow snapshot i
        result

    /// Stage 9 — announcement-payload assembly. Decides
    /// between bulk-change fallback (verbose ChangedText) and
    /// per-row suffix-diff. Returns the final string the
    /// StreamChunk OutputEvent will carry.
    ///
    /// Payload-empty contract: if every changed row produced
    /// `Silent` (e.g. all rows shrank, or all attribute-only
    /// differences), the returned string is `""`. The caller
    /// MUST check for empty before emitting — emitting an
    /// empty StreamChunk would still cause downstream
    /// processing (FileLoggerChannel logs it; NvdaChannel
    /// short-circuits empty per `NvdaChannel.fs:87` but the
    /// CONTRACT is to not emit empty payloads).
    ///
    /// TODO(Cycle 39): screen-diff-substrate legacy. Cycle 35b's
    /// default flip routes most non-alt-screen frames through
    /// `assembleSuffixFromStream` instead; this row-walk
    /// machinery survives only for the ScreenDiff branch of
    /// Auto mode (alt-screen TUIs) and for users who explicitly
    /// opt into `substrate_mode = "screen-diff"`. Removable
    /// when alt-screen TUI handling moves to Linear (or to a
    /// dedicated TuiPathway-internal screen-diff module). See
    /// Section 13 of `we-do-not-need-fluffy-simon.md`.
    let private assembleSuffixPayload
            (parameters: Parameters)
            (snapshot: Cell[][])
            (diff: CanonicalState.CanonicalDiff)
            (lastRowText: string[])
            : string
            =
        if diff.ChangedRows.Length > parameters.BulkChangeThreshold then
            // Stage 8b — bulk-change fallback. Defer to the
            // existing ChangedText; downstream behaviour is the
            // pre-PR-#166 verbose emit.
            diff.ChangedText
        else
            // Stage 8 — per-row suffix-diff.
            let parts = ResizeArray<string>()
            for rowIdx in diff.ChangedRows do
                if rowIdx >= 0 && rowIdx < snapshot.Length then
                    let currentText = CanonicalState.renderRow snapshot rowIdx
                    let previousText =
                        if rowIdx < lastRowText.Length then lastRowText.[rowIdx]
                        else ""
                    let delta =
                        computeRowSuffixDelta
                            parameters.BackspacePolicy
                            currentText
                            previousText
                    match delta with
                    | Suffix text -> parts.Add(text)
                    | Silent -> ()
            String.concat "\n" parts

    /// Cycle 35a — assemble the announce payload from the
    /// LinearTextStream producer's committed buffer, slicing
    /// from the last announce watermark. Returns the
    /// `(payloadText, newWatermark)` tuple; the caller updates
    /// `state.LastLinearWatermark` to `newWatermark` only when
    /// it actually emits the payload (so a suppressed emit
    /// doesn't lose the bytes).
    ///
    /// The producer's tail-mask semantics (RFC §5.3) already
    /// collapse spinner overwrites; this function just decodes
    /// the resulting bytes (UTF-8) and applies the existing
    /// `MaxAnnounceChars` cap. Sanitisation is applied via
    /// `AnnounceSanitiser.sanitise` — strips C0 / DEL / C1
    /// controls so raw ANSI escape sequences don't reach
    /// downstream channels.
    ///
    /// Returns `("", lastWatermark)` if no new bytes since
    /// last emit (caller suppresses); else
    /// `(capped-sanitised text, newWatermark)`.
    let private assembleSuffixFromStream
            (parameters: Parameters)
            (linearStream: LinearTextStream.T)
            (lastWatermark: int64)
            : string * int64
            =
        let bytes = LinearTextStream.suffixSince linearStream lastWatermark
        if bytes.Length = 0 then "", lastWatermark
        else
            let text = System.Text.Encoding.UTF8.GetString(bytes)
            let cleaned = AnnounceSanitiser.sanitise text
            // Cap to MaxAnnounceChars; the existing screen-diff
            // path applies the cap downstream via the same
            // helper. Inline here for symmetry.
            let capped =
                if cleaned.Length > parameters.MaxAnnounceChars then
                    cleaned.Substring(0, parameters.MaxAnnounceChars)
                else
                    cleaned
            let newWatermark = lastWatermark + int64 bytes.Length
            capped, newWatermark

    /// Cycle 35a — resolve the runtime substrate mode from the
    /// configured `SubstrateMode` + the canonical's alt-screen
    /// flag. `Auto` routes to `Linear` for non-alt-screen
    /// frames (where the byte stream is canonical) and
    /// `ScreenDiff` for alt-screen frames (where the grid is
    /// canonical per CORE-ABSTRACTION-BOUNDARY.md §1.4).
    let private resolveSubstrateMode
            (configured: SubstrateMode)
            (isAltScreenActive: bool)
            : SubstrateMode
            =
        match configured with
        | Linear -> Linear
        | ScreenDiff -> ScreenDiff
        | Auto -> if isAltScreenActive then ScreenDiff else Linear

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
            (linearStream: LinearTextStream.T)
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
            // Cycle 35c — resolve substrate mode early so the
            // spinner gate can be skipped on the Linear path.
            // Per RFC 0001 §5.3 #3, LinearTextStream's tail-mask
            // already collapses spinner-class output (bare \r,
            // ESC[K transition the current row to LATEST
            // semantics). The Path B Levenshtein gate is a
            // defensive measure for the screen-diff substrate
            // where every spinner frame produces a row-hash
            // difference; on Linear it's redundant and could
            // mask legitimate rapid-repeat content (e.g.,
            // heartbeat lines a tool intentionally prints
            // every N ms).
            let resolvedMode =
                resolveSubstrateMode
                    parameters.SubstrateMode
                    canonical.IsAltScreenActive
            let suppress =
                if resolvedMode = Linear then false
                else isSpinnerSuppressed parameters state now rowHashes contentHashes
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
                        state.PendingSnapshot <- ValueNone
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
                        state.PendingSnapshot <- ValueNone
                        // PR #166 — sub-row suffix-diff. Compute
                        // the payload BEFORE updating
                        // LastEmittedRowText (which is the
                        // baseline the suffix-diff reads).
                        // Cycle 35a — dispatch by SubstrateMode.
                        // Linear: read from LinearTextStream
                        // since last watermark. ScreenDiff:
                        // existing row-by-row suffix diff.
                        // Auto: Linear if non-alt-screen; else
                        // ScreenDiff. Watermark advance happens
                        // only on emit (suppressed/empty payloads
                        // don't lose bytes). Cycle 35c — the
                        // outer `resolvedMode` (hoisted before
                        // the spinner gate) is reused here; no
                        // need to recompute.
                        let payload, watermarkUpdate =
                            match resolvedMode with
                            | Linear ->
                                let text, newWm =
                                    assembleSuffixFromStream
                                        parameters
                                        linearStream
                                        state.LastLinearWatermark
                                text, ValueSome newWm
                            | ScreenDiff
                            | Auto ->  // unreachable; resolved above
                                let text =
                                    assembleSuffixPayload
                                        parameters
                                        canonical.Snapshot
                                        diff
                                        state.LastEmittedRowText
                                text, ValueNone
                        state.LastEmittedRowHashes <- rowHashes
                        state.LastEmittedRowText <- renderAllRows canonical.Snapshot
                        let capped = capAnnounce parameters payload
                        if String.IsNullOrEmpty capped then
                            // Suffix-diff produced no audible
                            // content (every changed row was
                            // Silent — typically backspace, or
                            // attribute-only differences). The
                            // dominant-colour earcon for this
                            // frame is also dampened: silent +
                            // colour-tone would announce a tone
                            // for content the user can't hear.
                            logger.LogDebug(
                                "Suppressed (suffix-diff Silent, leading-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows}",
                                frameHash, diff.ChangedRows.Length)
                            [||]
                        else
                            logger.LogInformation(
                                "Emit StreamChunk (leading-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len} Color={Color}",
                                frameHash, diff.ChangedRows.Length, capped.Length,
                                (match dominantColor with Some c -> c | None -> "none"))
                            // Cycle 35a — advance the linear
                            // watermark only on actual emit.
                            // ScreenDiff path leaves watermark
                            // unchanged (ValueNone).
                            match watermarkUpdate with
                            | ValueSome wm ->
                                state.LastLinearWatermark <- wm
                            | ValueNone -> ()
                            // Phase A.2 — emit a supplementary
                            // ErrorLine/WarningLine event when the
                            // diff's changed rows are colour-dominant.
                            // EarconProfile claims it semantically;
                            // NvdaChannel skips the empty payload so no
                            // double-announce.
                            //
                            // Cycle 40 — buildEmittedEvents also
                            // consults `state.LastPromptBoundary`:
                            // if a prompt boundary is cached and
                            // its MatchedRowText is a suffix of the
                            // payload, the StreamChunk is split into
                            // output portion + PromptDetected event.
                            // PromptDetected routes to inputPanel
                            // (empty payload → silent auto-announce).
                            let colorEvt =
                                dominantColor |> Option.bind colorOutputEvent
                            buildEmittedEvents state capped colorEvt
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
                    // PR #166 — stash the snapshot so the
                    // trailing-edge tick can compute the
                    // suffix-diff payload at flush time. Latest
                    // snapshot wins (matches PendingDiff
                    // semantics — only the most recent pending
                    // frame survives until the trailing-edge
                    // window expires).
                    state.PendingSnapshot <- ValueSome canonical.Snapshot
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
            (linearStream: LinearTextStream.T)
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
                // PR #166 — capture pending snapshot too.
                let pendingColor = state.PendingColor
                let pendingSnapshot = state.PendingSnapshot
                state.LastFrameHash <- ValueSome hash
                state.LastFlushAt <- ValueSome now
                state.PendingDiff <- ValueNone
                state.PendingFrameHash <- ValueNone
                state.PendingColor <- ValueNone
                state.PendingSnapshot <- ValueNone
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
                    // PR #166 — sub-row suffix-diff at trailing
                    // edge. Computed once (not per-accumulate
                    // keystroke); uses the captured-pending
                    // snapshot. If the snapshot is unexpectedly
                    // absent (defensive — should always be set
                    // when PendingDiff is set), fall back to the
                    // pre-PR-166 verbose `ChangedText`.
                    // Cycle 35a — trailing-edge dispatch by
                    // SubstrateMode. The pendingSnapshot's
                    // alt-screen state from the original frame
                    // is captured in `pendingAltScreen` (added
                    // by Cycle 35a alongside PendingSnapshot in
                    // the leading-edge accumulate path). For
                    // simplicity in 35a, the trailing-edge
                    // dispatch uses the configured mode without
                    // alt-screen autoresolution — matches
                    // typical usage (alt-screen toggles are
                    // rare during a single debounce window).
                    let resolvedMode =
                        match parameters.SubstrateMode with
                        | Linear -> Linear
                        | ScreenDiff -> ScreenDiff
                        | Auto -> Linear  // default to Linear in trailing edge
                    let payload, watermarkUpdate =
                        match resolvedMode with
                        | Linear ->
                            let text, newWm =
                                assembleSuffixFromStream
                                    parameters
                                    linearStream
                                    state.LastLinearWatermark
                            text, ValueSome newWm
                        | ScreenDiff
                        | Auto ->  // unreachable
                            let p =
                                match pendingSnapshot with
                                | ValueSome snap ->
                                    let pp =
                                        assembleSuffixPayload parameters snap diff state.LastEmittedRowText
                                    state.LastEmittedRowText <- renderAllRows snap
                                    pp
                                | ValueNone ->
                                    diff.ChangedText
                            p, ValueNone
                    let capped = capAnnounce parameters payload
                    if String.IsNullOrEmpty capped then
                        logger.LogDebug(
                            "Suppressed (suffix-diff Silent, trailing-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows}",
                            hash, diff.ChangedRows.Length)
                        [||]
                    else
                        // Cycle 35a — advance linear watermark
                        // only on actual emit (matches leading-
                        // edge contract).
                        match watermarkUpdate with
                        | ValueSome wm -> state.LastLinearWatermark <- wm
                        | ValueNone -> ()
                        let colorEvt =
                            match pendingColor with
                            | ValueSome c -> colorOutputEvent c
                            | ValueNone -> None
                        logger.LogInformation(
                            "Emit StreamChunk (trailing-edge). FrameHash=0x{Hash:X16} ChangedRows={Rows} TextLen={Len} Color={Color}",
                            hash, diff.ChangedRows.Length, capped.Length,
                            (match pendingColor with ValueSome c -> c | ValueNone -> "none"))
                        // Cycle 40 — consult cached prompt boundary
                        // and split if applicable (see leading-edge
                        // emit site at line ~898 for context).
                        buildEmittedEvents state capped colorEvt
            else
                [||]
        | _ -> [||]

    /// Mode-change barrier handler. Called from the PathwayPump
    /// when a `ScreenNotification.ModeChanged` arrives. Flushes
    /// any pending diff (so the user hears the pre-mode-change
    /// content) and resets row-hash baselines (so the
    /// post-mode-change first emit treats every row as
    /// "new").
    ///
    /// PR #168 — `ModeBarrierFlushPolicy` controls whether the
    /// flush is verbose (PR #166's behaviour, emit
    /// `diff.ChangedText` for the previous shell's full
    /// screen) or suppressed (default `SummaryOnly` /
    /// `Suppressed` — return `[||]`, rely on the App-layer
    /// shell-switch announce + the new shell's startup output
    /// for context). At the StreamPathway level
    /// `SummaryOnly` and `Suppressed` are identical; the
    /// difference materialises at the App-layer
    /// shell-switch announce (which is the maintainer's
    /// future TOML-driven concern, not a StreamPathway
    /// concern).
    let internal onModeBarrier
            (parameters: Parameters)
            (state: State)
            (now: DateTimeOffset)
            : OutputEvent[]
            =
        let flushed =
            match parameters.ModeBarrierFlushPolicy with
            | Verbose ->
                match state.PendingDiff with
                | ValueSome diff when diff.ChangedRows.Length > 0 ->
                    // Cycle 40 — buildEmittedEvents consults
                    // the cached prompt boundary for split, then
                    // emits the StreamChunk (with no
                    // color event at mode-barrier flushes per
                    // the existing PendingColor-clear semantics
                    // documented just below).
                    buildEmittedEvents state diff.ChangedText None
                | _ -> [||]
            | SummaryOnly | Suppressed ->
                [||]
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
        state.PendingSnapshot <- ValueNone
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueSome now
        state.LastRowHashes <- ValueNone
        // PR #169 — under `SummaryOnly` / `Suppressed`,
        // preserve `LastEmittedRowHashes` and `LastEmittedRowText`
        // so the next `processCanonicalState` pass diffs against
        // pre-barrier state. PR #168's change to the default
        // suppressed `onModeBarrier`'s explicit flush, but the
        // post-barrier first emit was still re-announcing the
        // previous shell's content via `processCanonicalState`'s
        // bulk-change fallback (diff against empty hashes →
        // "all 30 rows changed" → > BulkChangeThreshold →
        // emit ChangedText verbose). Confirmed in 2026-05-06
        // release-build validation: 1610-character emits of
        // stale `dir` listings on shell-switch.
        //
        // Preserving the baselines means the post-barrier first
        // emit's diff sees only the rows that ACTUALLY changed
        // since pre-barrier — typically the rows the new shell
        // paints over. Rows still showing the previous shell's
        // content (between barrier and new-shell-paint) match
        // the cached state and don't emit. When the new shell
        // paints, those rows DO change, and suffix-diff catches
        // the new content correctly.
        //
        // Under `Verbose`, the explicit flush already announces
        // the previous content, so clearing the cache is
        // appropriate — post-barrier emits SHOULD treat every
        // row as Initial under that policy, matching the
        // verbose-flush expectation that the user hears
        // "previous content + new content".
        match parameters.ModeBarrierFlushPolicy with
        | Verbose ->
            state.LastEmittedRowHashes <- [||]
            state.LastEmittedRowText <- [||]
        | SummaryOnly | Suppressed ->
            ()  // Preserve baselines for grounded post-barrier diff.
        state.PerRowHistory.Clear()
        state.HashHistory.Clear()
        flushed

    /// Internal — clear all mutable state to its initial shape.
    /// Shared by both `Reset` (active-shell switch) and the
    /// post-flush portion of `OnModeBarrier`.
    let private resetState (state: State) : unit =
        state.LastEmittedRowHashes <- [||]
        state.LastEmittedRowText <- [||]
        state.LastFrameHash <- ValueNone
        state.LastFlushAt <- ValueNone
        state.PendingDiff <- ValueNone
        state.PendingFrameHash <- ValueNone
        state.PendingColor <- ValueNone
        state.PendingSnapshot <- ValueNone
        state.LastRowHashes <- ValueNone
        // Cycle 35a — reset linear watermark so the next emit
        // treats the producer's entire committed buffer as
        // unreceived suffix.
        state.LastLinearWatermark <- 0L
        // Cycle 40 — clear cached prompt boundary so a stale
        // entry from the prior shell can't trigger a split on
        // the new shell's first chunk.
        state.LastPromptBoundary <- ValueNone
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
        // PR #166 — eagerly render the seeded snapshot so
        // suffix-diff has a baseline. Without this, the first
        // emit after a hot-switch-with-baseline would treat
        // every row as Initial (full content as "suffix") and
        // re-announce the entire shell screen — defeating the
        // verbose-readback fix that hot-switch is supposed to
        // sidestep.
        state.LastEmittedRowText <-
            let result = Array.zeroCreate<string> canonical.Snapshot.Length
            for i in 0 .. canonical.Snapshot.Length - 1 do
                result.[i] <- CanonicalState.renderRow canonical.Snapshot i
            result

    /// Construct a StreamPathway. The pathway captures
    /// `parameters` + `linearStream` in its closure; v1 ships
    /// a single pathway-instance per shell session, recycled
    /// via `Reset` on shell-switch.
    ///
    /// Cycle 35a — `linearStream` is the LinearTextStream
    /// producer (Cycle 34a) used by the Linear / Auto
    /// substrate-mode dispatch. Required even when SubstrateMode
    /// = ScreenDiff (the closure captures it for symmetry; the
    /// ScreenDiff branch never reads it). Composition root
    /// constructs the producer once and passes the same
    /// reference to both pathways at hot-switch time.
    let create
            (parameters: Parameters)
            (linearStream: LinearTextStream.T)
            : DisplayPathway.T =
        let state = createState ()
        { Id = id
          Consume =
            fun canonical ->
                let now = DateTimeOffset.UtcNow
                processCanonicalState parameters state now canonical linearStream
          Tick =
            fun now ->
                onTimerTick parameters state now linearStream
          OnModeBarrier =
            fun now ->
                onModeBarrier parameters state now
          Reset =
            fun () -> resetState state
          SetBaseline =
            fun canonical -> seedBaseline state canonical
          OnPromptBoundary =
            // Cycle 40 — cache the boundary so the NEXT
            // `processCanonicalState` / `onTimerTick` /
            // `onModeBarrier` emit can split the StreamChunk
            // payload at the prompt boundary. Single-shot: the
            // cache clears after the next emit consumes it
            // (see `buildEmittedEvents`). Until consumed, the
            // boundary's `MatchedRowText` is the suffix the
            // pathway looks for in the upcoming payload.
            fun boundary ->
                state.LastPromptBoundary <- ValueSome boundary
                [||] }

    /// Test-only — expose the state for direct manipulation in
    /// the test harness. Production code never calls this.
    let internal createWithExposedState
            (parameters: Parameters)
            (linearStream: LinearTextStream.T)
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
                    processCanonicalState parameters state now canonical linearStream
              Tick =
                fun now ->
                    onTimerTick parameters state now linearStream
              OnModeBarrier =
                fun now ->
                    onModeBarrier parameters state now
              Reset =
                fun () -> resetState state
              SetBaseline =
                fun canonical -> seedBaseline state canonical
              OnPromptBoundary =
                // Cycle 40 — cache the boundary for the next
                // emit's payload split (mirrors the production
                // `create` factory above).
                fun boundary ->
                    state.LastPromptBoundary <- ValueSome boundary
                    [||] }
        pathway, state
