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
/// **Detection algorithm (per-frame)**:
///
/// 1. Render row text via `CanonicalState.renderRow`
///    (sanitised + trailing-blank-trimmed).
/// 2. Test against per-shell regex (compiled, module-level
///    singleton).
/// 3. If matches AND `(now - first-match-time) ≥
///    stabilityMs` AND text differs from
///    `LastEmittedPromptText` → emit PromptStart.
/// 4. If matches but window not elapsed OR text equals
///    last-emitted → no emit, update state.
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
          LastEmittedPromptRowIndex: int option }

    /// Empty initial state.
    let create () : T =
        { PerRowMatches = Map.empty
          LastEmittedPromptText = None
          LastEmittedPromptRowIndex = None }

    /// Clear all per-row state. Called on shell-switch
    /// (fresh detector for new shell) and on alt-screen
    /// entry (boundaries during alt-screen are ignored
    /// anyway, but stale state would produce phantom
    /// boundaries on alt-screen exit).
    let reset (state: T) : T =
        { PerRowMatches = Map.empty
          LastEmittedPromptText = None
          LastEmittedPromptRowIndex = None }

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
            // Walk every row; find the first row whose text
            // matches the regex AND has been stable for the
            // shell's window. Drop per-row entries for rows
            // that no longer match (state hygiene).
            let mutable nextMatches = state.PerRowMatches
            let mutable emitted : PromptBoundaryData option = None
            let mutable emittedText : string option = None
            let mutable emittedRowIndex : int option = None
            for rowIdx in 0 .. snapshot.Length - 1 do
                let text = CanonicalState.renderRow snapshot rowIdx
                if regex.IsMatch(text) then
                    let priorMatch = Map.tryFind rowIdx nextMatches
                    match priorMatch with
                    | Some (priorText, firstSeen) when priorText = text ->
                        let elapsed = now - firstSeen
                        // Tier 1.E2.A — row-index-aware emission
                        // gate. A NEW PromptStart fires when the
                        // (text, rowIdx) pair differs from the
                        // last emitted pair. Catches:
                        //   * Different text (today's case): cd
                        //     changed the prompt path.
                        //   * Same text, different row: cmd's
                        //     stable-prompt case where output
                        //     pushed the prompt to a new row
                        //     after a command cycle.
                        // Suppresses:
                        //   * Same text, same row: cursor blink
                        //     / refresh / no real activity.
                        let isNewPrompt =
                            match
                                state.LastEmittedPromptText,
                                state.LastEmittedPromptRowIndex
                                with
                            | Some priorText, Some priorRow ->
                                priorText <> text || priorRow <> rowIdx
                            | _ -> true   // first emit
                        if elapsed.TotalMilliseconds >= float stabilityMs
                           && emitted.IsNone
                           && isNewPrompt
                        then
                            // Stable + new prompt → emit. Source
                            // stamps the shell's stability window
                            // as the integer payload.
                            emitted <-
                                Some
                                    { Kind = BoundaryKind.PromptStart
                                      Source =
                                        BoundarySource.HeuristicPromptRegex
                                            stabilityMs
                                      DetectedAt = now
                                      CommandId = None
                                      ExtraParams = Map.empty
                                      // Tier 1.E: capture the
                                      // matching row's text for
                                      // SessionModel PromptText
                                      // population.
                                      MatchedRowText = Some text
                                      // Tier 1.E2.A: capture the
                                      // matching row's index for
                                      // (a) the row-index-aware
                                      // emission gate above and
                                      // (b) Cycle 20b's CommandText
                                      // / OutputText extraction at
                                      // finalize time.
                                      MatchedRowIndex = Some rowIdx }
                            emittedText <- Some text
                            emittedRowIndex <- Some rowIdx
                    | _ ->
                        // First time seeing this text on
                        // this row, OR text changed —
                        // restart the stability timer.
                        nextMatches <-
                            Map.add rowIdx (text, now) nextMatches
                else
                    // Row no longer matches; evict stale
                    // entry to keep the map bounded.
                    if Map.containsKey rowIdx nextMatches then
                        nextMatches <- Map.remove rowIdx nextMatches

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

            let nextState =
                { PerRowMatches = nextMatches
                  LastEmittedPromptText = nextLastEmittedText
                  LastEmittedPromptRowIndex = nextLastEmittedRowIndex }
            emitted, nextState
