namespace Terminal.Core

open System
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging

/// Tier 1.D — heuristic prompt-boundary fallback.
///
/// Per `docs/SESSION-MODEL.md` §245-340: shells that don't
/// emit OSC 133 (cmd, PowerShell, Claude — all three by
/// default) still need SessionModel to populate. This
/// detector observes the screen snapshot per-frame, matches
/// rows against per-shell regex patterns, and emits
/// synthetic `PromptBoundaryData` events with
/// `BoundarySource.HeuristicPromptRegex stabilityMs`.
///
/// **Maintainer-locked scope (2026-05-08, AskUserQuestion
/// resolutions)**:
///
/// 1. **PromptStart-only emission**. Tier 1.D emits
///    `BoundaryKind.PromptStart` events only. The Cycle 13
///    state machine's "PromptStart while Active=Some"
///    transition auto-finalises the prior tuple as
///    incomplete — this gives History accumulation without
///    needing heuristic synthesis of CommandStart /
///    OutputStart / CommandFinished. Those need
///    Enter-detection (Phase 2 input framework) /
///    cursor-aware diff (Phase 2) / OSC 133 D exit codes
///    respectively.
///
/// 2. **Regex-only Claude detection**. Single regex
///    `^.*│\s>.*$` per row, same shape as cmd / PowerShell.
///    Source = `HeuristicPromptRegex 200` (longer stability
///    window for Claude's slower TUI cadence). Defers the
///    full Ink-box context check (multi-row `╭─╮│╰─╯`
///    scanning per SESSION-MODEL.md §275-280) to a
///    refinement cycle if false-positives surface.
///
/// 3. **Hardcoded defaults**. All per-shell parameters
///    (regex, stability window) baked in. No `Config.fs`
///    schema changes; no `[session_model.fallback.*]` TOML
///    sections. The substrate stays internally observable
///    only (no pathway consumes `OnPromptBoundary`
///    non-trivially yet) so user-facing knobs aren't
///    actionable today. TOML wiring per SESSION-MODEL.md
///    §282-300 + USER-SETTINGS atlas entries defer to a
///    later cycle.
///
/// **Detection algorithm (per-frame, two-pass)**:
///
/// PASS 1 — walk every row:
/// 1. Render row text via `CanonicalState.renderRow`
///    (sanitised + trailing-blank-trimmed).
/// 2. Test against per-shell regex (compiled, module-level
///    singleton).
/// 3. If matches AND `(now - first-match-time) ≥
///    stabilityMs` → record as a stable match candidate.
/// 4. If matches but window not yet elapsed OR text changed
///    → reset the per-row stability timer.
/// 5. If no longer matches → evict stale per-row entry.
///
/// PASS 2 — among all stable matches, pick the highest
/// rowIdx (the newest prompt — output flows downward in
/// every supported shell, so the bottommost matching row is
/// the active prompt). Apply the (text, rowIdx) emission
/// gate: emit if the pair differs from the last emitted
/// pair; suppress otherwise.
///
/// **Why two-pass** (Cycle 22a fix). A one-pass
/// "first-match-wins" loop produced infinite emission
/// flapping when two rows matched the regex with identical
/// text (e.g. cmd's screen with a prior prompt still
/// visible above the current one): each tick alternated
/// which row emitted because the gate `(text, rowIdx) !=
/// last-emitted` kept satisfying. Picking max-rowIdx among
/// stable matches eliminates the alternation by
/// construction.
///
/// **Threading**: detector is mutated only on the
/// PathwayPump worker thread (single-threaded for
/// notification consumption); `tryDetect` is a pure
/// function returning the next state alongside the boundary
/// option.
///
/// **State bound**: `PerRowMatches` is keyed by row index
/// and bounded by `screen.Rows` (typically 30). Stale
/// entries (rows that no longer match) get cleared on each
/// `tryDetect` call.
[<RequireQualifiedAccess>]
module HeuristicPromptDetector =

    /// Per-shell hardcoded defaults. Compiled regexes are
    /// module-level singletons (one allocation per
    /// AppDomain).
    ///
    /// **cmd**: `C:\>`, `D:\path\foo>` etc. Match enforces
    /// drive-letter prefix + literal `>` close.
    let private cmdRegex =
        Regex(@"^[A-Z]:\\.*>\s*$", RegexOptions.Compiled)

    /// **PowerShell**: `PS C:\>`, `PS C:\path>`, `PS>`
    /// (some configurations). Match enforces `PS` prefix +
    /// space + content + `>` close.
    let private powerShellRegex =
        Regex(@"^PS\s.*>\s*$", RegexOptions.Compiled)

    /// **Claude**: the Ink-box prompt indicator. Per
    /// SESSION-MODEL.md §275-280, Claude renders a TUI
    /// with a `│ >` indicator. Tier 1.D matches the
    /// indicator without verifying the surrounding
    /// box-drawing characters (regex-only per maintainer
    /// resolution).
    let private claudeRegex =
        Regex(@"^.*│\s>.*$", RegexOptions.Compiled)

    /// Stability window per shell. Claude needs longer
    /// (200ms) to absorb TUI repaint cadence; cmd /
    /// PowerShell stabilise faster (100ms).
    [<Literal>]
    let private CmdStabilityMs = 100

    [<Literal>]
    let private PowerShellStabilityMs = 100

    [<Literal>]
    let private ClaudeStabilityMs = 200

    /// Detector state. Tracks per-row first-match
    /// timestamps + the most recently emitted prompt text
    /// (for duplicate suppression).
    type T =
        { /// Per-row last-observed prompt match: row index
          /// → (rendered text, first-match timestamp).
          /// Cleared on `reset`; per-row eviction on each
          /// frame when the row no longer matches.
          PerRowMatches: Map<int, string * DateTime>
          /// Most recently emitted PromptStart's row text.
          /// Used together with `LastEmittedPromptRowIndex`
          /// to suppress duplicate emissions when the SAME
          /// prompt remains visible at the SAME row (typing
          /// at the prompt re-fires RowsChanged but the
          /// rendered row text is unchanged + cursor blinks
          /// don't move the prompt).
          LastEmittedPromptText: string option
          /// Tier 1.E2.A (Cycle 20a) — most recently emitted
          /// PromptStart's row index. Combined with
          /// `LastEmittedPromptText` for the row-index-aware
          /// emission gate: a NEW PromptStart is emitted when
          /// `(text, rowIdx)` differs from the last emitted
          /// pair. Catches the cmd stable-prompt case where
          /// the prompt text is identical across commands but
          /// the prompt has moved to a new row after a
          /// command cycle (output filled rows below the
          /// previous prompt; new prompt rendered below the
          /// output).
          LastEmittedPromptRowIndex: int option
          /// Cycle 45f-followup (2026-05-12) — dirty flag
          /// that closes the `(text, rowIdx)`-equality
          /// false-negative. Set to `true` on any frame where
          /// the previously-emitted row no longer matches the
          /// prompt regex (e.g., the user typed `echo hello`
          /// on the prompt row — the row text is now
          /// `"C:\>echo hello"`, not a clean prompt match).
          /// Cleared on emission. Forces the gate to emit the
          /// next stable match regardless of `(text, rowIdx)`
          /// equality with the last emission.
          ///
          /// Without this flag, a command run on a full
          /// screen (e.g., after `dir` filled the 30 rows)
          /// produces a redrawn prompt at the SAME row with
          /// the SAME text as the previous emission — the
          /// dedup gate suppresses it and SessionModel never
          /// seals the tuple. cmd users observed silence
          /// after the first screen-filling output.
          RowDirtyAfterEmit: bool }

    /// Empty initial state.
    let create () : T =
        { PerRowMatches = Map.empty
          LastEmittedPromptText = None
          LastEmittedPromptRowIndex = None
          RowDirtyAfterEmit = false }

    /// Clear all per-row state. Called on shell-switch
    /// (fresh detector for new shell) and on alt-screen
    /// entry (boundaries during alt-screen are ignored
    /// anyway, but stale state would produce phantom
    /// boundaries on alt-screen exit).
    let reset (state: T) : T =
        { PerRowMatches = Map.empty
          LastEmittedPromptText = None
          LastEmittedPromptRowIndex = None
          RowDirtyAfterEmit = false }

    let private logger = Logger.get "Terminal.Core.HeuristicPromptDetector"

    /// Resolve the per-shell regex + stability window from
    /// the shell-key string. Returns `None` for unknown
    /// shells (Q2 resolution: ON for cmd / PowerShell /
    /// Claude; OFF for unknown shells).
    let private shellParams
            (shellKey: string)
            : (Regex * int) option
            =
        match shellKey with
        | "cmd" -> Some (cmdRegex, CmdStabilityMs)
        | "powershell" -> Some (powerShellRegex, PowerShellStabilityMs)
        | "claude" -> Some (claudeRegex, ClaudeStabilityMs)
        | _ -> None

    /// Pure detection function. Walks the snapshot row by
    /// row, tracks per-row stability, and emits at most one
    /// `PromptBoundaryData` per call.
    ///
    /// Returns `(boundary option, updated state)`. The
    /// caller (PathwayPump's `handleRowsChanged`) feeds
    /// boundary into `SessionModel.apply` + the active
    /// pathway's `OnPromptBoundary`.
    ///
    /// `cursor` is captured for forward-compatibility with
    /// the cursor-position refinement (Phase 2); Tier 1.D
    /// doesn't use it.
    let tryDetect
            (snapshot: Cell[][])
            (_cursor: int * int)
            (shellKey: string)
            (now: DateTime)
            (state: T)
            : PromptBoundaryData option * T
            =
        match shellParams shellKey with
        | None ->
            // Unknown shell — no detection per Q2 resolution.
            None, state
        | Some (regex, stabilityMs) ->
            // Cycle 22a — two-pass detection. Pass 1 walks every
            // row, updates per-row stability state, and collects
            // every row that's currently stable + matching. Pass
            // 2 picks the highest-rowIdx among them (the newest
            // prompt — cmd / PowerShell / Claude all emit output
            // downward, so the bottommost matching row is the
            // active one) and applies the (text, rowIdx) gate.
            //
            // Cycle 20a's first-match-wins loop produced infinite
            // flapping when two rows matched the regex with
            // identical text (e.g. cmd's screen with a prior
            // prompt still visible above the active one): each
            // tick alternated which row emitted because the gate
            // `(text, rowIdx) != LastEmitted` kept satisfying.
            // Within ~5 seconds, History flooded to 100/100.
            // Picking max-rowIdx among stable matches eliminates
            // the alternation by construction — there's exactly
            // one "current prompt" per tick.
            let mutable nextMatches = state.PerRowMatches
            // PASS 1 — walk rows, update stability state,
            // collect every stable+matching row.
            let stableMatches = ResizeArray<int * string>()
            for rowIdx in 0 .. snapshot.Length - 1 do
                let text = CanonicalState.renderRow snapshot rowIdx
                if regex.IsMatch(text) then
                    let priorMatch = Map.tryFind rowIdx nextMatches
                    match priorMatch with
                    | Some (priorText, firstSeen) when priorText = text ->
                        let elapsed = now - firstSeen
                        if elapsed.TotalMilliseconds >= float stabilityMs then
                            stableMatches.Add((rowIdx, text))
                    | _ ->
                        // First time seeing this text on this
                        // row, OR text changed — restart the
                        // stability timer.
                        nextMatches <-
                            Map.add rowIdx (text, now) nextMatches
                else
                    // Row no longer matches; evict stale entry
                    // to keep the map bounded.
                    if Map.containsKey rowIdx nextMatches then
                        nextMatches <- Map.remove rowIdx nextMatches

            // Cycle 45f-followup — RowDirtyAfterEmit accumulation.
            // Look at the row the detector LAST emitted on. If that
            // row currently does NOT match the prompt regex, the
            // user has typed something on the prompt row (e.g.
            // `"C:\>echo hello"` doesn't match `^[A-Z]:\\.*>\s?$`)
            // or cmd's output has scrolled the prompt off / overwritten
            // it. Either way, the NEXT clean-prompt-match on the same
            // (text, rowIdx) pair is a genuinely-new prompt event,
            // not a no-op refresh. Persist the bit across ticks so
            // a multi-tick command cycle (typed → Enter → cmd output
            // → new prompt → stability accumulation) doesn't lose
            // it.
            let dirtyThisTick =
                match state.LastEmittedPromptRowIndex with
                | None -> false
                | Some priorRow ->
                    if priorRow < 0 || priorRow >= snapshot.Length then
                        false
                    else
                        let priorRowText =
                            CanonicalState.renderRow snapshot priorRow
                        not (regex.IsMatch(priorRowText))
            let rowDirtyAccumulated =
                state.RowDirtyAfterEmit || dirtyThisTick

            // PASS 2 — pick highest-rowIdx among stable matches,
            // apply the row-index-aware emission gate.
            let mutable emitted : PromptBoundaryData option = None
            let mutable emittedText : string option = None
            let mutable emittedRowIndex : int option = None
            if stableMatches.Count > 0 then
                let firstRow, firstText = stableMatches.[0]
                let mutable bestRow = firstRow
                let mutable bestText = firstText
                for i in 1 .. stableMatches.Count - 1 do
                    let candidateRow, candidateText = stableMatches.[i]
                    if candidateRow > bestRow then
                        bestRow <- candidateRow
                        bestText <- candidateText
                // Tier 1.E2.A — row-index-aware emission gate.
                // A NEW PromptStart fires when the (text, rowIdx)
                // pair differs from the last emitted pair OR the
                // previously-emitted row was dirtied between
                // emissions (Cycle 45f-followup).
                // Catches:
                //   * Different text: `cd` changed the prompt
                //     path.
                //   * Same text, different row: cmd's
                //     stable-prompt case where output pushed
                //     the prompt to a new row after a command
                //     cycle.
                //   * Same text, same row, dirty intermission:
                //     screen-filling output scrolled everything,
                //     the new prompt redrew on the same row as
                //     the prior emission, the user already
                //     typed + ran a command in between (the row
                //     went non-matching). This used to silently
                //     dedupe and caused cmd to go silent after
                //     `dir` or any screen-filling output.
                // Suppresses:
                //   * Same text, same row, never dirty: cursor
                //     blink / refresh / no real activity.
                let isNewPrompt =
                    match
                        state.LastEmittedPromptText,
                        state.LastEmittedPromptRowIndex
                        with
                    | Some priorText, Some priorRow ->
                        priorText <> bestText
                        || priorRow <> bestRow
                        || rowDirtyAccumulated
                    | _ -> true   // first emit
                if isNewPrompt then
                    emitted <-
                        Some
                            { Kind = BoundaryKind.PromptStart
                              Source =
                                BoundarySource.HeuristicPromptRegex
                                    stabilityMs
                              DetectedAt = now
                              CommandId = None
                              ExtraParams = Map.empty
                              // Tier 1.E: capture the matching
                              // row's text for SessionModel
                              // PromptText population.
                              MatchedRowText = Some bestText
                              // Tier 1.E2.A: capture the matching
                              // row's index for (a) the
                              // row-index-aware emission gate
                              // above and (b) Cycle 20b's
                              // CommandText / OutputText
                              // extraction at finalize time.
                              MatchedRowIndex = Some bestRow }
                    emittedText <- Some bestText
                    emittedRowIndex <- Some bestRow

            let nextLastEmittedText =
                match emittedText with
                | Some _ -> emittedText
                | None -> state.LastEmittedPromptText
            let nextLastEmittedRowIndex =
                match emittedRowIndex with
                | Some _ -> emittedRowIndex
                | None -> state.LastEmittedPromptRowIndex

            if emitted.IsSome then
                logger.LogDebug(
                    "HeuristicPromptDetector emitted PromptStart for shell {Shell} (stability {Ms}ms; rowIdx={Row}).",
                    shellKey, stabilityMs, emittedRowIndex)

            // Cycle 45f-followup — reset dirty on emit; otherwise
            // carry the accumulated value forward.
            let nextRowDirtyAfterEmit =
                if emitted.IsSome then false
                else rowDirtyAccumulated

            let nextState =
                { PerRowMatches = nextMatches
                  LastEmittedPromptText = nextLastEmittedText
                  LastEmittedPromptRowIndex = nextLastEmittedRowIndex
                  RowDirtyAfterEmit = nextRowDirtyAfterEmit }
            emitted, nextState
