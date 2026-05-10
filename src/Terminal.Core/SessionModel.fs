namespace Terminal.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging

/// Tier 1.A — SessionModel substrate skeleton.
///
/// The SessionModel is the structured-history substrate per
/// `docs/SESSION-MODEL.md`: it captures (prompt, command,
/// output, exit-code) tuples sourced from OSC 133 escape
/// sequences with heuristic fallback. Sits at "Stage 3.5"
/// between notification emission (Stage 3) and canonical-state
/// synthesis (Stage 4) per the PIPELINE-NARRATIVE 12-stage
/// vocabulary.
///
/// **Tier 1.A scope: types only**. The state machine is
/// deferred to Tier 1.C; Tier 1.A ships a placeholder `apply`
/// function that returns state unchanged. This lets future
/// PRs override the function without protocol churn — every
/// caller gets the same `T` shape today + tomorrow.
///
/// **Tier 1 sequencing**:
/// - Tier 1.A (this file) — types + `create` + no-op `apply`
/// - Tier 1.B — OSC 133 producer in `Screen.Apply`;
///   `CanonicalState.CursorPosition` field added
/// - Tier 1.C — heuristic fallback module + non-trivial
///   `apply` state machine + composition wiring in Program.fs
/// - Tier 1.D — diagnostic battery extension + corpus tests
///
/// **Per-shell-session model**. Each Ctrl+Shift+1/2/3
/// hot-switch creates a fresh SessionModel instance for the
/// new shell (per Q5 resolution 2026-05-07). Unified history
/// across shells is a future opt-in TOML setting.
///
/// **Why string-keyed shell**. The substrate stays free of
/// `Terminal.Pty.ShellRegistry`'s `ShellId` DU dependency to
/// preserve the existing assembly boundary (mirrors the
/// `Terminal.Core.Config` precedent). The composition root
/// in `Terminal.App.Program` translates `ShellId → string` at
/// SessionModel construction time.
[<RequireQualifiedAccess>]
module SessionModel =

    /// State of the active SessionTuple. Mirrors the
    /// boundary-kind progression but as a state machine: the
    /// active tuple advances through these states as
    /// `BoundaryKind` events arrive. After
    /// `BoundaryKind.CommandFinished`, the tuple is moved to
    /// the History queue (no `Complete` state needed).
    ///
    /// Tier 1.A captures the type; Tier 1.C wires transitions.
    type ActiveTupleState =
        /// Initial state. No prompt boundary has been
        /// observed yet; the SessionModel is waiting for the
        /// first `PromptStart`.
        | AwaitingPromptStart
        /// `PromptStart` received; the user is editing at the
        /// prompt. Waiting for `CommandStart` (Enter).
        | AwaitingCommandStart
        /// `CommandStart` received; the command has been
        /// submitted. Waiting for `OutputStart` (the shell
        /// echoes the command then begins streaming output).
        | EditingCommand
        /// `OutputStart` received; the command's output is
        /// streaming. Waiting for `CommandFinished`.
        | OutputStreaming

    /// One completed shell-interaction tuple. Immutable;
    /// constructed when `CommandFinished` arrives.
    ///
    /// **Per Q4 resolution 2026-05-07**: multi-line commands
    /// (here-docs, `\` continuation) are stored as ONE string
    /// with embedded newlines in `CommandText`. Easier
    /// serialisation; pathways re-split if line-level access
    /// is needed.
    ///
    /// **Per Q8 resolution 2026-05-07**: `ExitCode` is
    /// `int option` — the substrate captures the value
    /// verbatim (or `None` when the shell didn't report
    /// one). Pathways interpret semantically (e.g. "exit 0
    /// = success" mapping happens in pathway code, not
    /// substrate).
    type SessionTuple =
        { /// Unique tuple identifier; survives serialisation.
          Id: Guid
          /// OSC 133 `aid=<command-id>` parameter, if shell
          /// supplied one. Lets cross-session correlation
          /// happen without timing-window heuristics. None
          /// when not supplied (most cases today).
          CommandId: string option
          /// Which shell produced this tuple. String-keyed
          /// per assembly-boundary discipline (see module
          /// docstring).
          ShellId: string
          /// Wall-clock when `PromptStart` was detected.
          PromptStartedAt: DateTime
          /// Wall-clock when `CommandStart` was detected.
          /// `None` when the tuple closed before
          /// `CommandStart` (e.g. user pressed Ctrl+C at the
          /// prompt; rare).
          CommandStartedAt: DateTime option
          /// Wall-clock when `OutputStart` was detected.
          /// `None` when the command finished before
          /// `OutputStart` (e.g. instant-completing alias).
          OutputStartedAt: DateTime option
          /// Wall-clock when `CommandFinished` was detected.
          /// `None` when the tuple is still active (only
          /// possible on the `Active` field; finalised tuples
          /// in `History` always have this set).
          CommandFinishedAt: DateTime option
          /// Text rendered at the prompt before
          /// `CommandStart`. Captured at the
          /// `PromptStart → CommandStart` transition.
          PromptText: string
          /// Text the user typed between the prompt and
          /// Enter. Multi-line via embedded newlines (Q4).
          CommandText: string
          /// Output bytes (rendered) between `OutputStart`
          /// and `CommandFinished`.
          OutputText: string
          /// Process exit code. None when not reported by
          /// the shell. Substrate captures verbatim (Q8).
          ExitCode: int option
          /// Detection-source provenance per boundary kind.
          /// Records which `BoundarySource` produced each
          /// transition — useful for diagnostics +
          /// confidence-aware future pathway logic.
          Sources: Map<BoundaryKind, BoundarySource>
          /// Other OSC 133 parameters (`k=`, `cl=`, custom
          /// shell extensions) preserved verbatim. Tier 1
          /// stores but doesn't interpret.
          ExtraParams: Map<string, string> }

    /// Active (in-flight) SessionTuple. Mutable view: the
    /// tuple's fields are populated incrementally as
    /// `BoundaryKind` events arrive. When `CommandFinished`
    /// arrives the tuple is finalised + moved to History.
    type ActiveSessionTuple =
        { /// The tuple data (in-progress; some fields may
          /// be unset).
          Tuple: SessionTuple
          /// State machine cursor.
          State: ActiveTupleState
          /// **Tier 1.E2.B (Cycle 20b)** — row index where
          /// the prompt was matched at PromptStart time.
          /// Captured from the boundary's `MatchedRowIndex`
          /// field (populated by `HeuristicPromptDetector` at
          /// emit time, or by `Program.fs.handlePromptBoundary`
          /// OSC 133 augmentation from the cursor row).
          /// Used at finalize-time by `finalizeAndEnqueue`
          /// to bracket the rows-between-old-and-new-prompt
          /// range for OutputText extraction + to locate the
          /// old-prompt row for CommandText extraction.
          /// `None` when boundary lacked `MatchedRowIndex`
          /// (defensive — extraction gracefully skips).
          PromptRowIndex: int option }

    /// SessionModel state for a single shell session. One
    /// instance per Ctrl+Shift+1/2/3 hot-switch (per Q5
    /// resolution 2026-05-07).
    ///
    /// **Tier 1.A scope**: skeleton record + `create`
    /// factory + no-op `apply`. **Tier 1.C** ships the real
    /// state machine (transitions, History ring-buffer
    /// enforcement, alt-screen guard).
    type T =
        { /// Which shell this SessionModel tracks. String-
          /// keyed (see module docstring).
          ShellId: string
          /// Stable session identifier. Survives across
          /// individual tuple boundaries; re-rolled on shell
          /// hot-switch.
          SessionId: Guid
          /// Wall-clock when this SessionModel was created
          /// (typically at shell-spawn or hot-switch).
          SessionStartedAt: DateTime
          /// Bounded ring buffer of completed tuples. Oldest
          /// tuples evicted when the queue exceeds
          /// `MaxHistorySize`. Tier 1.C populates via
          /// `apply`'s state machine.
          History: Queue<SessionTuple>
          /// Maximum tuples retained in History. Default
          /// 100 per SESSION-MODEL.md §4. Tier 1.C enforces
          /// the bound during `apply`'s tuple-finalisation
          /// branch.
          MaxHistorySize: int
          /// In-flight tuple, if any. `None` between
          /// `CommandFinished` and the next `PromptStart`.
          Active: ActiveSessionTuple option
          /// **Tier 1.C — Q3 partial**: when `true`, `apply`
          /// returns state unchanged (boundary events are
          /// ignored during alt-screen / TUI sessions per Q3
          /// resolution 2026-05-07). Tier 1.D wires
          /// `enterAltScreen` / `exitAltScreen` to the
          /// PathwayPump's `ScreenNotification.ModeChanged`
          /// arm; Tier 1.C ships the field + helpers but does
          /// not yet toggle them from composition root.
          IsAltScreenActive: bool }

    /// Default history size. Per SESSION-MODEL.md §4
    /// recommendation. Tier 1.C enforces the bound during
    /// `apply`.
    [<Literal>]
    let DefaultMaxHistorySize: int = 100

    /// Construct a new SessionModel for the given shell.
    /// Caller supplies a stable shell-key string (e.g.
    /// `"cmd"`, `"powershell"`, `"claude"`); the composition
    /// root translates `ShellId → string` at construction
    /// time per the assembly-boundary convention.
    let create (shellId: string) (maxHistorySize: int) : T =
        { ShellId = shellId
          SessionId = Guid.NewGuid()
          SessionStartedAt = DateTime.UtcNow
          History = Queue<SessionTuple>()
          MaxHistorySize = maxHistorySize
          Active = None
          IsAltScreenActive = false }

    /// Convenience overload: construct with the default
    /// max-history-size (100).
    let createDefault (shellId: string) : T =
        create shellId DefaultMaxHistorySize

    /// **Tier 1.C — Q3 partial**: mark the session as in
    /// alt-screen mode. While active, `apply` returns the
    /// state unchanged (boundaries ignored during vim /
    /// less / TUI sessions per Q3 resolution 2026-05-07).
    /// Tier 1.D wires this to the PathwayPump's
    /// `ScreenNotification.ModeChanged` arm.
    let enterAltScreen (state: T) : T =
        { state with IsAltScreenActive = true }

    /// **Tier 1.C — Q3 partial**: clear the alt-screen
    /// flag; subsequent boundaries advance the state
    /// machine again.
    let exitAltScreen (state: T) : T =
        { state with IsAltScreenActive = false }

    /// FNV-1a-style log helper bound at module level so
    /// state-machine warnings reach FileLogger via the
    /// existing `Logger.get` indirection. Logger captures
    /// the call-site context for diagnostics.
    let private logger = Logger.get "Terminal.Core.SessionModel"

    /// Build a brand-new SessionTuple with `PromptStartedAt`
    /// set + the supplied boundary's CommandId / ExtraParams /
    /// Sources captured. Other timestamps are `None` until
    /// later boundaries advance the active tuple.
    ///
    /// **Tier 1.E** — PromptText capture: the boundary's
    /// `MatchedRowText` (when populated by
    /// `HeuristicPromptDetector` or
    /// `Program.fs.handlePromptBoundary`'s OSC 133
    /// augmentation) populates the new tuple's
    /// `PromptText`. `None` falls back to `""` (matches
    /// pre-Tier-1.E behaviour for boundaries lacking
    /// snapshot context).
    let private newTuple
            (shellId: string)
            (boundary: PromptBoundaryData)
            : SessionTuple
            =
        { Id = Guid.NewGuid()
          CommandId = boundary.CommandId
          ShellId = shellId
          PromptStartedAt = boundary.DetectedAt
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = None
          PromptText =
              boundary.MatchedRowText |> Option.defaultValue ""
          CommandText = ""
          OutputText = ""
          ExitCode = None
          Sources = Map.ofList [ boundary.Kind, boundary.Source ]
          ExtraParams = boundary.ExtraParams }

    /// Merge a boundary's metadata into an existing active
    /// tuple. CommandId hoists if previously `None`;
    /// duplicate sets log a warning. ExtraParams merge with
    /// later-boundary keys overwriting earlier values
    /// (boundaries are ordered; later wins). Sources
    /// accumulate the (Kind, Source) pair.
    let private mergeBoundary
            (active: ActiveSessionTuple)
            (boundary: PromptBoundaryData)
            : ActiveSessionTuple
            =
        let tuple = active.Tuple
        let commandId =
            match tuple.CommandId, boundary.CommandId with
            | None, Some _ -> boundary.CommandId
            | Some existing, Some incoming when existing <> incoming ->
                logger.LogWarning(
                    "SessionModel boundary CommandId conflict: existing={Existing} incoming={Incoming}; preserving existing.",
                    existing, incoming)
                Some existing
            | _ -> tuple.CommandId
        let extraParams =
            // Later boundary overwrites; F# Map.add does this.
            Map.fold (fun acc k v -> Map.add k v acc)
                tuple.ExtraParams boundary.ExtraParams
        let sources = Map.add boundary.Kind boundary.Source tuple.Sources
        { active with
            Tuple =
                { tuple with
                    CommandId = commandId
                    ExtraParams = extraParams
                    Sources = sources } }

    /// Move an active tuple to the History ring buffer with
    /// the supplied finalisation timestamp. Enforces
    /// **Tier 1.E2.B (Cycle 20b)** — extract `CommandText` +
    /// `OutputText` from screen state at finalize time.
    ///
    /// **CommandText**: the row at `oldPromptRowIndex` in
    /// the current snapshot likely contains the prompt + the
    /// user's typed command (e.g. `C:\> echo hi`). Strip the
    /// captured `oldPromptText` prefix; what remains is the
    /// command. Defensive: returns `""` when the row index
    /// is missing / out of bounds, the captured PromptText
    /// is empty, or the rendered row doesn't start with the
    /// captured prompt (scroll happened mid-cycle).
    ///
    /// **OutputText**: rows between `oldPromptRowIndex + 1`
    /// and `newPromptRowIndex - 1` (inclusive) joined with
    /// newlines. Empty rows filtered out (typical shell
    /// output has trailing-blank rows that aren't
    /// meaningful). Defensive: returns `""` when row indices
    /// are missing, when newRow ≤ oldRow + 1 (no rows
    /// between), or when row indices are out of snapshot
    /// bounds.
    ///
    /// Returns `(commandText, outputText)`. Pure function;
    /// no side effects.
    ///
    /// TODO(Cycle 39): screen-diff-substrate legacy. Cycle 35b's
    /// `applyAndCaptureWithSubstrate` routes around this row-
    /// walk when the LinearTextStream substrate has OSC 133
    /// markers; this helper survives only as the fallback for
    /// OSC-133-less shells (vanilla cmd, vanilla PowerShell).
    /// Removable when Cycle 39's preconditions are met (broad
    /// OSC 133 coverage OR an OSC-133-injecting shim cycle
    /// ships). See Section 13 of `we-do-not-need-fluffy-simon.md`.
    let private extractContent
            (oldPromptText: string)
            (oldPromptRowIndex: int option)
            (newPromptRowIndex: int option)
            (snapshot: Cell[][])
            : string * string
            =
        let commandText =
            match oldPromptText, oldPromptRowIndex with
            | "", _ -> ""
            | _, None -> ""
            | _, Some rowIdx when rowIdx < 0 || rowIdx >= snapshot.Length ->
                ""
            | promptText, Some rowIdx ->
                let rendered = CanonicalState.renderRow snapshot rowIdx
                if rendered.StartsWith(promptText) then
                    rendered.Substring(promptText.Length).TrimStart()
                else
                    // Row content doesn't start with the prompt
                    // we captured (scroll happened mid-cycle);
                    // skip rather than producing garbage.
                    ""
        let outputText =
            match oldPromptRowIndex, newPromptRowIndex with
            | Some oldRow, Some newRow when
                oldRow >= 0
                && newRow > oldRow + 1
                && newRow <= snapshot.Length
                ->
                let lines = ResizeArray<string>()
                for rowIdx in oldRow + 1 .. newRow - 1 do
                    if rowIdx < snapshot.Length then
                        let line = CanonicalState.renderRow snapshot rowIdx
                        if not (System.String.IsNullOrEmpty line) then
                            lines.Add(line)
                String.concat "\n" lines
            | _ -> ""
        commandText, outputText

    /// `MaxHistorySize` by dequeueing the oldest tuple
    /// before enqueueing the new one. When `MaxHistorySize`
    /// is `0`, the tuple is dropped silently (no history
    /// retained).
    ///
    /// **Tier 1.E2.B (Cycle 20b)** — extracts `CommandText`
    /// + `OutputText` from snapshot when the extraction
    /// context is supplied (heuristic interrupt-arm or OSC
    /// 133 CommandFinished arm):
    ///   * `oldPromptRowIndex`: row where the active
    ///     tuple's prompt was emitted (from
    ///     `ActiveSessionTuple.PromptRowIndex`).
    ///   * `newPromptRowIndex`: row where the NEW prompt is
    ///     (from the incoming boundary's `MatchedRowIndex`;
    ///     `None` for `CommandFinished` arms).
    ///   * `snapshot`: current screen state at finalize
    ///     time.
    /// `finalizeIncomplete` (shell-switch) passes `None /
    /// None / [||]` so extraction gracefully returns empty
    /// strings.
    /// Cycle 24c — return type extended from `T` to
    /// `T * SessionTuple option`. The option is `Some
    /// finalised` whenever the tuple actually landed in
    /// History (i.e. the normal case); it's `None` only when
    /// `MaxHistorySize <= 0` — a degenerate config in which
    /// the tuple is discarded entirely. Cycle 24c's writer
    /// dispatches off the `Some` branch; persisting tuples
    /// the user explicitly opted out of tracking would be a
    /// surprise.
    /// Cycle 35b — `linearOverride`: when `Some (cmd, out)`, the
    /// caller has authoritative CommandText/OutputText from the
    /// LinearTextStream substrate (OSC 133 markers were observed
    /// during the tuple's lifetime). When `None`, fall back to
    /// the legacy `extractContent` row-walk so OSC-133-less
    /// shells (vanilla cmd, vanilla PowerShell) keep working.
    /// See Section 13 of the strategic plan for the eventual-
    /// removal sketch.
    /// TODO(Cycle 39): remove the linearOverride None branch +
    /// `extractContent` itself once OSC 133 coverage is broad
    /// enough (or an OSC-133-injecting shim ships) per
    /// Section 13 of the strategic plan.
    let private finalizeAndEnqueue
            (state: T)
            (tuple: SessionTuple)
            (finishedAt: DateTime)
            (exitCode: int option)
            (oldPromptRowIndex: int option)
            (newPromptRowIndex: int option)
            (snapshot: Cell[][])
            (linearOverride: (string * string) option)
            : T * SessionTuple option
            =
        let commandText, outputText =
            match linearOverride with
            | Some (cmd, out) -> cmd, out
            | None ->
                extractContent
                    tuple.PromptText
                    oldPromptRowIndex
                    newPromptRowIndex
                    snapshot
        let finalised =
            { tuple with
                CommandFinishedAt = Some finishedAt
                ExitCode = exitCode
                CommandText = commandText
                OutputText = outputText }
        if state.MaxHistorySize <= 0 then
            { state with Active = None }, None
        else
            // Ring-buffer eviction: drop oldest until under
            // the cap, then enqueue.
            while state.History.Count >= state.MaxHistorySize do
                state.History.Dequeue() |> ignore
            state.History.Enqueue(finalised)
            { state with Active = None }, Some finalised

    /// **Tier 1.C — Q5 helper**: finalise any in-flight
    /// active tuple as incomplete (`CommandFinishedAt =
    /// finishedAt`, `ExitCode = None`) and move it to the
    /// History. Used by the composition root on shell
    /// hot-switch (per Q5 resolution: per-shell-session
    /// SessionModel; shell-switch finalises prior shell's
    /// active tuple as interrupted before recreating a
    /// fresh SessionModel).
    ///
    /// **Tier 1.E2.B**: passes `None / None / [||]` for the
    /// extraction context (no new prompt or snapshot at
    /// shell-switch time); CommandText / OutputText stay
    /// empty for incomplete tuples.
    let rec finalizeIncomplete (state: T) (finishedAt: DateTime) : T =
        finalizeIncompleteAndCapture state finishedAt |> fst

    /// Cycle 24c — capture variant of `finalizeIncomplete`.
    /// Returns both the new state and the finalised tuple
    /// (when there was an Active to finalise) so the
    /// composition root can dispatch the tuple to the
    /// SessionLogWriterSink. The state-only `finalizeIncomplete`
    /// wrapper above preserves the existing public API for
    /// 15+ existing test callers.
    and finalizeIncompleteAndCapture
            (state: T)
            (finishedAt: DateTime)
            : T * SessionTuple option
            =
        match state.Active with
        | None -> state, None
        | Some active ->
            // Cycle 35b — `None` linearOverride: shell-switch
            // finalize doesn't have a substrate context handy
            // (and the legacy semantics never produced rich
            // CommandText/OutputText for incomplete tuples
            // anyway — `[||]` snapshot was always the input).
            finalizeAndEnqueue
                state active.Tuple finishedAt None
                None None [||]
                None

    /// **Tier 1.C — real state machine.** Applies a
    /// `PromptBoundaryData` event to the SessionModel,
    /// producing the next state. Pure function (records
    /// are immutable; `History` mutates inside the function
    /// but the queue itself is captured by reference and
    /// the returned `T` shares that queue — callers are
    /// expected to treat the returned `T` as the canonical
    /// state and discard prior references).
    ///
    /// Transitions per SESSION-MODEL.md §4 + the Tier 1.C
    /// plan's transition table. Defensive transitions log
    /// at Warning level + soft-fail (preserve substrate
    /// health; never crash).
    ///
    /// **Q3 alt-screen guard**: when `IsAltScreenActive`,
    /// returns state unchanged. The composition root is
    /// expected to toggle `enterAltScreen` / `exitAltScreen`
    /// (Tier 1.D wiring).
    ///
    /// **Tier 1.E2.B (Cycle 20b)** — `snapshot` parameter
    /// added. Forwarded to `finalizeAndEnqueue` for content
    /// extraction at finalize time. Callers without
    /// snapshot context (legacy / tests) pass `[||]`;
    /// extraction gracefully skips because row-bounds checks
    /// fail.
    let rec apply
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            : T
            =
        applyAndCapture state boundary snapshot |> fst

    /// Cycle 24c — capture variant of `apply`. Returns both the
    /// new state and the freshly-finalised SessionTuple (when
    /// this call resulted in an Active→History transition) so
    /// the composition root can dispatch to the
    /// SessionLogWriterSink. The state-only `apply` wrapper
    /// above preserves the existing public API for the 81+
    /// existing test callers.
    ///
    /// Two arms produce a `Some tuple` return:
    ///   * `Some active, BoundaryKind.PromptStart` — interrupt;
    ///     prior tuple finalised as incomplete before the new
    ///     one starts.
    ///   * `Some active, BoundaryKind.CommandFinished _` —
    ///     normal completion path.
    /// All other arms return `None` for the tuple.
    ///
    /// Cycle 35b — thin wrapper around `applyAndCaptureCore`
    /// that always passes `None` for the `linearOverride`
    /// (legacy screen-diff-substrate behaviour). The 81+
    /// existing test callers continue to use this signature
    /// unchanged. New production code wires through
    /// `applyAndCaptureWithSubstrate` (defined below) which
    /// computes a substrate-aware override before calling Core.
    and applyAndCapture
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            : T * SessionTuple option
            =
        applyAndCaptureCore state boundary snapshot None

    /// Cycle 35b — the shared body. Takes an optional
    /// `linearOverride: (commandText, outputText)` that, when
    /// `Some`, replaces the `extractContent` row-walk inside
    /// `finalizeAndEnqueue`. `applyAndCapture` (legacy) passes
    /// `None`; `applyAndCaptureWithSubstrate` (substrate-aware)
    /// computes the override from `LinearTextStream` when
    /// substrate-mode resolves to Linear AND OSC 133 markers
    /// were observed.
    and private applyAndCaptureCore
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (linearOverride: (string * string) option)
            : T * SessionTuple option
            =
        if state.IsAltScreenActive then
            // Q3 — boundaries ignored during alt-screen.
            state, None
        else
            let kind = boundary.Kind
            let detectedAt = boundary.DetectedAt
            match state.Active, kind with
            // === AwaitingPromptStart ===
            | None, BoundaryKind.PromptStart ->
                let tuple = newTuple state.ShellId boundary
                let active =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart
                      // Tier 1.E2.B: capture the row index for
                      // future finalize-time output extraction.
                      PromptRowIndex = boundary.MatchedRowIndex }
                { state with Active = Some active }, None
            | None, BoundaryKind.CommandStart ->
                logger.LogWarning(
                    "SessionModel CommandStart with no Active tuple; ignored.")
                state, None
            | None, BoundaryKind.OutputStart ->
                logger.LogWarning(
                    "SessionModel OutputStart with no Active tuple; ignored.")
                state, None
            | None, BoundaryKind.CommandFinished _ ->
                logger.LogWarning(
                    "SessionModel CommandFinished with no Active tuple; ignored.")
                state, None
            // === Active = Some — replaces / advances by kind ===
            | Some active, BoundaryKind.PromptStart ->
                // Interrupted: finalise prior as incomplete,
                // start a new tuple.
                logger.LogInformation(
                    "SessionModel PromptStart while Active={State}; finalising prior as incomplete.",
                    active.State)
                // Tier 1.E2.B: pass extraction context so
                // CommandText + OutputText are extracted from
                // snapshot using the prior tuple's
                // PromptRowIndex (where the old prompt sat)
                // and the incoming boundary's MatchedRowIndex
                // (where the new prompt is).
                let withPrior, finalisedOpt =
                    finalizeAndEnqueue
                        state active.Tuple detectedAt None
                        active.PromptRowIndex
                        boundary.MatchedRowIndex
                        snapshot
                        linearOverride
                let tuple = newTuple state.ShellId boundary
                let nextActive =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart
                      PromptRowIndex = boundary.MatchedRowIndex }
                { withPrior with Active = Some nextActive }, finalisedOpt
            | Some active, BoundaryKind.CommandStart ->
                let merged = mergeBoundary active boundary
                match active.State with
                | ActiveTupleState.AwaitingCommandStart ->
                    let nextTuple =
                        { merged.Tuple with
                            CommandStartedAt = Some detectedAt }
                    let nextActive =
                        { merged with
                            Tuple = nextTuple
                            State = ActiveTupleState.EditingCommand }
                    { state with Active = Some nextActive }, None
                | ActiveTupleState.EditingCommand ->
                    // Re-submit / re-emit; refresh timestamp.
                    logger.LogWarning(
                        "SessionModel duplicate CommandStart in EditingCommand; refreshing CommandStartedAt.")
                    let nextTuple =
                        { merged.Tuple with
                            CommandStartedAt = Some detectedAt }
                    { state with Active = Some { merged with Tuple = nextTuple } }, None
                | ActiveTupleState.OutputStreaming
                | ActiveTupleState.AwaitingPromptStart ->
                    logger.LogWarning(
                        "SessionModel CommandStart in unexpected state {State}; ignored.",
                        active.State)
                    { state with Active = Some merged }, None
            | Some active, BoundaryKind.OutputStart ->
                let merged = mergeBoundary active boundary
                match active.State with
                | ActiveTupleState.AwaitingCommandStart ->
                    // Skipped CommandStart; tolerate.
                    logger.LogWarning(
                        "SessionModel OutputStart with no prior CommandStart; tolerating skip.")
                    let nextTuple =
                        { merged.Tuple with
                            OutputStartedAt = Some detectedAt }
                    let nextActive =
                        { merged with
                            Tuple = nextTuple
                            State = ActiveTupleState.OutputStreaming }
                    { state with Active = Some nextActive }, None
                | ActiveTupleState.EditingCommand ->
                    let nextTuple =
                        { merged.Tuple with
                            OutputStartedAt = Some detectedAt }
                    let nextActive =
                        { merged with
                            Tuple = nextTuple
                            State = ActiveTupleState.OutputStreaming }
                    { state with Active = Some nextActive }, None
                | ActiveTupleState.OutputStreaming ->
                    logger.LogWarning(
                        "SessionModel duplicate OutputStart in OutputStreaming; refreshing OutputStartedAt.")
                    let nextTuple =
                        { merged.Tuple with
                            OutputStartedAt = Some detectedAt }
                    { state with Active = Some { merged with Tuple = nextTuple } }, None
                | ActiveTupleState.AwaitingPromptStart ->
                    // Should not be observable; guarded above
                    // by `None, OutputStart` arm.
                    state, None
            | Some active, BoundaryKind.CommandFinished exitCode ->
                let merged = mergeBoundary active boundary
                // Tier 1.E2.B: OSC 133 CommandFinished arm
                // can extract CommandText (the prior prompt
                // row's content minus PromptText prefix) but
                // not OutputText (no new prompt yet to
                // bracket the output range against). Passes
                // `None` for newPromptRowIndex; extraction
                // produces ("cmd", "") in this path.
                finalizeAndEnqueue
                    state merged.Tuple detectedAt exitCode
                    active.PromptRowIndex
                    None
                    snapshot
                    linearOverride

    // -----------------------------------------------------------------
    // Cycle 35b — Substrate-aware public surface.
    // -----------------------------------------------------------------
    //
    // `applyAndCaptureWithSubstrate` and `applyWithSubstrate` are
    // the runtime entry points used by the composition root once
    // Cycle 35b's default flip lands. They peek the
    // LinearTextStream substrate to construct an authoritative
    // `(commandText, outputText)` override; falling back to the
    // legacy `extractContent` row-walk when the substrate has no
    // OSC 133 markers (vanilla cmd / vanilla PowerShell).
    //
    // The legacy `apply` / `applyAndCapture` surface is preserved
    // with original signatures for the 81+ existing test callers
    // and for any external consumer that doesn't have a
    // LinearTextStream in scope.
    //
    // TODO(Cycle 39): once the preconditions in Section 13 of
    // `we-do-not-need-fluffy-simon.md` are met (broad OSC 133
    // coverage, OR an OSC-133-injecting shim ships, AND ≥4 weeks
    // of dogfood), collapse the two surfaces back into a single
    // `apply` / `applyAndCapture` whose signature includes the
    // substrate parameters. At that point `extractContent` and
    // the `linearOverride` fallback both go away.

    /// Cycle 35b — peek the linear stream's per-tuple OSC 133
    /// state. Returns `Some (commandText, outputText)` when
    /// markers are present (linear path is authoritative); else
    /// `None` so the caller falls back to `extractContent`.
    let private extractContentFromLinearStream
            (linearStream: LinearTextStream.T)
            : (string * string) option
            =
        if not (LinearTextStream.hasOsc133Markers linearStream) then
            None
        else
            let chunk, _ = LinearTextStream.finalizeHighWaterMark linearStream
            Some (chunk.CommandText, chunk.OutputText)

    /// Cycle 35b — substrate-aware finalize. The caller (a
    /// composition root or pathway-pump callback) resolves the
    /// substrate mode against `StreamPathway.SubstrateMode` and
    /// `canonical.IsAltScreenActive` (mirroring
    /// `StreamPathway.resolveSubstrateMode`) and passes the
    /// boolean result here. SessionModel doesn't reference
    /// `StreamPathway.SubstrateMode` directly because the
    /// compile order (SessionModel.fs precedes StreamPathway.fs
    /// in `Terminal.Core.fsproj`) wouldn't allow it; passing the
    /// boolean is functionally equivalent and keeps the
    /// dependency direction clean.
    ///
    /// `useLinear = true` AND OSC 133 markers observed → linear
    /// path (override is `Some (cmd, out)`). Otherwise →
    /// `extractContent` row-walk fallback.
    let applyAndCaptureWithSubstrate
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (linearStream: LinearTextStream.T)
            (useLinear: bool)
            : T * SessionTuple option
            =
        let linearOverride =
            if useLinear then
                extractContentFromLinearStream linearStream
            else
                None
        applyAndCaptureCore state boundary snapshot linearOverride

    /// Cycle 35b — state-only wrapper around
    /// `applyAndCaptureWithSubstrate`. Mirrors `apply`'s
    /// relationship to `applyAndCapture`.
    let applyWithSubstrate
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (linearStream: LinearTextStream.T)
            (useLinear: bool)
            : T
            =
        applyAndCaptureWithSubstrate
            state boundary snapshot linearStream useLinear
        |> fst

    // -----------------------------------------------------------------
    // Cycle 22b — Ctrl+Shift+Y clipboard formatter.
    // -----------------------------------------------------------------

    /// Render an ISO-8601 UTC timestamp suitable for log /
    /// clipboard inspection. Mirrors the format
    /// `Diagnostics.formatTimestamp` uses but lives here so
    /// the formatter is self-contained inside Terminal.Core.
    let private formatTimestamp (dt: DateTime) : string =
        dt.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture)

    let private formatTimestampOpt (dt: DateTime option) : string =
        match dt with
        | Some t -> formatTimestamp t
        | None -> "(none)"

    let private formatExitCode (code: int option) : string =
        match code with
        | Some c -> string c
        | None -> "(none)"

    let private formatBoundarySource (src: BoundarySource) : string =
        match src with
        | BoundarySource.Osc133 -> "Osc133"
        | BoundarySource.HeuristicPromptRegex stabilityMs ->
            sprintf "HeuristicPromptRegex(%dms)" stabilityMs
        | BoundarySource.HeuristicClaudeInkBox ->
            "HeuristicClaudeInkBox"

    let private formatBoundaryKind (kind: BoundaryKind) : string =
        match kind with
        | BoundaryKind.PromptStart -> "PromptStart"
        | BoundaryKind.CommandStart -> "CommandStart"
        | BoundaryKind.OutputStart -> "OutputStart"
        | BoundaryKind.CommandFinished _ -> "CommandFinished"

    let private formatSources
            (sources: Map<BoundaryKind, BoundarySource>)
            : string
            =
        if Map.isEmpty sources then
            "(none)"
        else
            sources
            |> Map.toList
            |> List.map (fun (k, v) ->
                sprintf "%s=%s" (formatBoundaryKind k) (formatBoundarySource v))
            |> String.concat ", "

    let private formatExtraParams (extra: Map<string, string>) : string =
        if Map.isEmpty extra then
            "(none)"
        else
            extra
            |> Map.toList
            |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
            |> String.concat ", "

    let private formatActiveState (state: ActiveTupleState) : string =
        match state with
        | AwaitingPromptStart -> "AwaitingPromptStart"
        | AwaitingCommandStart -> "AwaitingCommandStart"
        | EditingCommand -> "EditingCommand"
        | OutputStreaming -> "OutputStreaming"

    /// Render an empty-string field as a parenthesised marker
    /// rather than a blank line, so the clipboard output is
    /// unambiguous when fields are unpopulated (typical for
    /// in-progress active tuples or finalize-as-incomplete
    /// finalisations).
    let private formatEmptyAware (text: string) : string =
        if System.String.IsNullOrEmpty text then "(empty)" else text

    let private appendTuple
            (sb: System.Text.StringBuilder)
            (index: int)
            (tuple: SessionTuple)
            : unit
            =
        sb.AppendFormat("--- Entry {0} ---\n", index) |> ignore
        sb.AppendFormat("Id:                {0}\n", tuple.Id) |> ignore
        sb.AppendFormat(
            "PromptStarted:     {0}\n",
            formatTimestamp tuple.PromptStartedAt) |> ignore
        sb.AppendFormat(
            "CommandStarted:    {0}\n",
            formatTimestampOpt tuple.CommandStartedAt) |> ignore
        sb.AppendFormat(
            "OutputStarted:     {0}\n",
            formatTimestampOpt tuple.OutputStartedAt) |> ignore
        sb.AppendFormat(
            "CommandFinished:   {0}\n",
            formatTimestampOpt tuple.CommandFinishedAt) |> ignore
        sb.AppendFormat(
            "ExitCode:          {0}\n",
            formatExitCode tuple.ExitCode) |> ignore
        sb.AppendFormat(
            "Source(s):         {0}\n",
            formatSources tuple.Sources) |> ignore
        sb.AppendFormat(
            "ExtraParams:       {0}\n",
            formatExtraParams tuple.ExtraParams) |> ignore
        sb.AppendFormat(
            "Prompt:            {0}\n",
            formatEmptyAware tuple.PromptText) |> ignore
        sb.AppendFormat(
            "Command:           {0}\n",
            formatEmptyAware tuple.CommandText) |> ignore
        sb.AppendFormat(
            "Output:            {0}\n",
            formatEmptyAware tuple.OutputText) |> ignore
        sb.Append('\n') |> ignore

    let private appendActive
            (sb: System.Text.StringBuilder)
            (active: ActiveSessionTuple)
            : unit
            =
        let tuple = active.Tuple
        sb.Append("--- Active (in flight) ---\n") |> ignore
        sb.AppendFormat(
            "State:             {0}\n",
            formatActiveState active.State) |> ignore
        sb.AppendFormat("Id:                {0}\n", tuple.Id) |> ignore
        sb.AppendFormat(
            "PromptRowIndex:    {0}\n",
            (match active.PromptRowIndex with
             | Some r -> string r
             | None -> "(none)")) |> ignore
        sb.AppendFormat(
            "PromptStarted:     {0}\n",
            formatTimestamp tuple.PromptStartedAt) |> ignore
        sb.AppendFormat(
            "CommandStarted:    {0}\n",
            formatTimestampOpt tuple.CommandStartedAt) |> ignore
        sb.AppendFormat(
            "OutputStarted:     {0}\n",
            formatTimestampOpt tuple.OutputStartedAt) |> ignore
        sb.AppendFormat(
            "Source(s):         {0}\n",
            formatSources tuple.Sources) |> ignore
        sb.AppendFormat(
            "ExtraParams:       {0}\n",
            formatExtraParams tuple.ExtraParams) |> ignore
        sb.AppendFormat(
            "Prompt:            {0}\n",
            formatEmptyAware tuple.PromptText) |> ignore
        sb.AppendFormat(
            "Command:           {0}\n",
            formatEmptyAware tuple.CommandText) |> ignore
        sb.AppendFormat(
            "Output:            {0}\n",
            formatEmptyAware tuple.OutputText) |> ignore
        sb.Append('\n') |> ignore

    /// Render a `SessionModel.T` to clipboard-friendly
    /// structured plain text. Used by Ctrl+Shift+Y to dump
    /// the full session history (plus any in-flight active
    /// tuple) for paste-into-chat workflows.
    ///
    /// **No truncation**. Unlike `Diagnostics.formatTuple`
    /// (which caps PromptText at 40 chars + CmdText/OutText
    /// at 80 chars to keep the diagnostic log line
    /// scannable), this formatter preserves full content.
    /// 100 tuples × ~500 chars ≈ 50KB clipboard payload —
    /// well within Windows clipboard limits.
    ///
    /// The `now` parameter is the snapshot timestamp shown
    /// in the header. Caller passes `DateTime.UtcNow` at
    /// hotkey-press time; tests pass a fixed value for
    /// determinism.
    let formatHistoryForClipboard (now: DateTime) (state: T) : string =
        let sb = System.Text.StringBuilder()
        sb.Append("=== pty-speak session history ===\n") |> ignore
        sb.AppendFormat(
            "Snapshot:          {0}\n", formatTimestamp now) |> ignore
        sb.AppendFormat(
            "Session:           {0}\n", state.SessionId) |> ignore
        sb.AppendFormat(
            "Shell:             {0}\n", state.ShellId) |> ignore
        sb.AppendFormat(
            "SessionStarted:    {0}\n",
            formatTimestamp state.SessionStartedAt) |> ignore
        sb.AppendFormat(
            "History:           {0} of {1}\n",
            state.History.Count, state.MaxHistorySize) |> ignore
        sb.AppendFormat(
            "AltScreenActive:   {0}\n", state.IsAltScreenActive) |> ignore
        sb.Append('\n') |> ignore
        if state.History.Count = 0 && Option.isNone state.Active then
            sb.Append(
                "(no entries; session has not yet captured any prompt boundaries)\n") |> ignore
        else
            // History first (oldest → newest), then active.
            // Queue.ToArray() preserves enqueue order; index
            // 0 = oldest, last = most recent.
            let entries = state.History.ToArray()
            for i in 0 .. entries.Length - 1 do
                appendTuple sb (i + 1) entries.[i]
            match state.Active with
            | Some active -> appendActive sb active
            | None -> ()
        sb.ToString()

    // -----------------------------------------------------------------
    // Cycle 24b — JSONL serializer for SessionTuple.
    // -----------------------------------------------------------------
    //
    // Pure function `formatTupleAsJsonl : SessionTuple -> string`
    // that emits one JSONL line (a JSON object followed by a literal
    // `\n` terminator). No I/O — Cycle 24c wires the file writer
    // against the Active→History transition seam in
    // `Terminal.App.Program`.
    //
    // **Wire format pinned in `docs/SESSION-MODEL.md` §"On-disk
    // wire format (Cycle 24b)" — needs to remain stable for
    // decades.** Locked decisions:
    //
    // 1. Per-record `"schemaVersion":1` as the first key. Future
    //    schema changes increment the value; old files always
    //    remain readable; replay tools branch on it.
    // 2. `BoundarySource` always serialises as a tagged object
    //    `{"kind":"<case>", ...payload}` — uniform shape across
    //    all DU cases; future cases land cleanly.
    // 3. `Sources` serialises as a JSON ARRAY of records, not as
    //    a JSON object keyed by `BoundaryKind`. Avoids the latent
    //    collision bug where `BoundaryKind.CommandFinished _`
    //    payload variants would alias to the same JSON key.
    //    Sorted by an explicit `boundaryOrdinal` (NOT by F# DU
    //    compare semantics — those are an implementation detail
    //    we don't want to depend on for decades-stable byte
    //    output).
    // 4. Hand-rolled (no JSON library). The codebase has zero JSON
    //    dependencies; F# `option` and DUs would require custom
    //    System.Text.Json converters that grow per type;
    //    hand-rolling gives byte-stable output across .NET
    //    versions and full control over field ordering. Mirrors
    //    the Tomlyn-uses-only-what-we-need pattern from Cycle 24a.
    //
    // String-escape policy: RFC 8259 minimum (named escapes for
    // `"`, `\`, `\b`, `\f`, `\n`, `\r`, `\t`; `\u00XX` for any
    // other byte in `0x00-0x1F`) PLUS DEL (0x7F → ``,
    // deliberate superset; many parsers and viewers mis-handle
    // bare DEL). C1 controls (0x80-0x9F), forward slash, U+2028
    // / U+2029 pass through unescaped. Lone UTF-16 surrogates
    // throw — loud failure beats silent corruption in a
    // ten-year-old log.

    /// Schema version emitted on every JSONL record. Increment
    /// when the on-disk shape changes; replay tools branch on
    /// the value to apply per-version deserializers.
    [<Literal>]
    let JsonlSchemaVersion : int = 1

    let private jsonInvariant : System.Globalization.CultureInfo =
        System.Globalization.CultureInfo.InvariantCulture

    /// Escape a string per the JSON-string production in RFC 8259
    /// + DEL (0x7F). Throws `InvalidOperationException` on lone
    /// UTF-16 surrogates — silent corruption of decades-old logs
    /// is the worst possible failure mode; a thrown exception at
    /// emit time forces upstream code to fix the surrogate-
    /// producing bug.
    let private escapeJsonString (s: string) : string =
        let sb = System.Text.StringBuilder(s.Length + 2)
        let len = s.Length
        let mutable i = 0
        while i < len do
            let c = s.[i]
            let cInt = int c
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\b' -> sb.Append("\\b") |> ignore
            | '\f' -> sb.Append("\\f") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            | _ when cInt < 0x20 || cInt = 0x7F ->
                sb.Append(System.String.Format(
                    jsonInvariant, "\\u{0:x4}", cInt)) |> ignore
            | _ when System.Char.IsHighSurrogate(c) ->
                if i + 1 < len && System.Char.IsLowSurrogate(s.[i + 1]) then
                    sb.Append(c) |> ignore
                    sb.Append(s.[i + 1]) |> ignore
                    i <- i + 1
                else
                    raise (System.InvalidOperationException(
                        sprintf "JSONL serializer: lone high surrogate at index %d" i))
            | _ when System.Char.IsLowSurrogate(c) ->
                raise (System.InvalidOperationException(
                    sprintf "JSONL serializer: lone low surrogate at index %d" i))
            | _ ->
                sb.Append(c) |> ignore
            i <- i + 1
        sb.ToString()

    /// ISO-8601 UTC with 100ns ticks (`yyyy-MM-ddTHH:mm:ss.fffffffZ`)
    /// — lossless from the Windows clock. Diverges from the
    /// human-readable `formatTimestamp` (3-digit ms) which is for
    /// clipboard/log inspection; the JSONL formatter is forensic
    /// data and must round-trip exactly.
    let private formatJsonTimestamp (dt: DateTime) : string =
        dt.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            jsonInvariant)

    let private formatJsonOptionTimestamp (dt: DateTime option) : string =
        match dt with
        | Some t ->
            let sb = System.Text.StringBuilder(35)
            sb.Append('"') |> ignore
            sb.Append(formatJsonTimestamp t) |> ignore
            sb.Append('"') |> ignore
            sb.ToString()
        | None -> "null"

    let private formatJsonOptionString (s: string option) : string =
        match s with
        | Some v ->
            let sb = System.Text.StringBuilder(v.Length + 2)
            sb.Append('"') |> ignore
            sb.Append(escapeJsonString v) |> ignore
            sb.Append('"') |> ignore
            sb.ToString()
        | None -> "null"

    let private formatJsonOptionInt (i: int option) : string =
        match i with
        | Some v -> System.String.Format(jsonInvariant, "{0}", v)
        | None -> "null"

    /// Payload-less name of a `BoundaryKind` case. Used as both
    /// the array entry's `"boundary"` value AND the `boundaryOrdinal`
    /// sort axis — these MUST stay in sync when new cases are
    /// added (the F# compiler's match-exhaustiveness check is the
    /// forcing function).
    let private formatBoundaryKindName (kind: BoundaryKind) : string =
        match kind with
        | BoundaryKind.PromptStart -> "PromptStart"
        | BoundaryKind.CommandStart -> "CommandStart"
        | BoundaryKind.OutputStart -> "OutputStart"
        | BoundaryKind.CommandFinished _ -> "CommandFinished"

    /// Deterministic sort key for `Sources` array serialization.
    /// Don't depend on F# DU compare semantics for a decades-stable
    /// byte-for-byte output format.
    let private boundaryOrdinal (kind: BoundaryKind) : int =
        match kind with
        | BoundaryKind.PromptStart -> 0
        | BoundaryKind.CommandStart -> 1
        | BoundaryKind.OutputStart -> 2
        | BoundaryKind.CommandFinished _ -> 3

    let private formatBoundarySourceTaggedObject
            (src: BoundarySource)
            : string
            =
        match src with
        | BoundarySource.Osc133 ->
            "{\"kind\":\"Osc133\"}"
        | BoundarySource.HeuristicPromptRegex stabilityMs ->
            System.String.Format(
                jsonInvariant,
                "{{\"kind\":\"HeuristicPromptRegex\",\"stabilityMs\":{0}}}",
                stabilityMs)
        | BoundarySource.HeuristicClaudeInkBox ->
            "{\"kind\":\"HeuristicClaudeInkBox\"}"

    let private formatSourcesArray
            (sources: Map<BoundaryKind, BoundarySource>)
            : string
            =
        let sb = System.Text.StringBuilder()
        sb.Append('[') |> ignore
        let entries =
            sources
            |> Map.toList
            |> List.sortBy (fst >> boundaryOrdinal)
        let mutable first = true
        for (kind, src) in entries do
            if not first then sb.Append(',') |> ignore
            first <- false
            sb.Append("{\"boundary\":\"") |> ignore
            sb.Append(formatBoundaryKindName kind) |> ignore
            sb.Append("\",\"source\":") |> ignore
            sb.Append(formatBoundarySourceTaggedObject src) |> ignore
            sb.Append('}') |> ignore
        sb.Append(']') |> ignore
        sb.ToString()

    let private formatExtraParamsObject
            (extras: Map<string, string>)
            : string
            =
        let sb = System.Text.StringBuilder()
        sb.Append('{') |> ignore
        let mutable first = true
        // F# `Map<string, _>` iterates in sorted-key order via
        // ordinal string compare per spec; deterministic across
        // .NET versions.
        for KeyValue (k, v) in extras do
            if not first then sb.Append(',') |> ignore
            first <- false
            sb.Append('"') |> ignore
            sb.Append(escapeJsonString k) |> ignore
            sb.Append("\":\"") |> ignore
            sb.Append(escapeJsonString v) |> ignore
            sb.Append('"') |> ignore
        sb.Append('}') |> ignore
        sb.ToString()

    /// Serialize one `SessionTuple` as one JSONL line — a JSON
    /// object followed by a single literal `\n` terminator.
    /// Pure (no I/O); safe to call from any thread; total (never
    /// returns null; throws only on lone UTF-16 surrogates per
    /// `escapeJsonString`).
    ///
    /// **Wire format is decades-stable** — see the section
    /// docstring above for locked decisions and
    /// `docs/SESSION-MODEL.md` §"On-disk wire format (Cycle 24b)"
    /// for the canonical reference.
    ///
    /// Trailing terminator is the literal string `"\n"`, never
    /// `Environment.NewLine` — the latter would emit `\r\n` on
    /// Windows and silently break byte-for-byte stability across
    /// platforms. Cycle 24c writer concatenates lines without
    /// further processing.
    let formatTupleAsJsonl (tuple: SessionTuple) : string =
        let sb = System.Text.StringBuilder()
        sb.Append('{') |> ignore
        sb.Append("\"schemaVersion\":") |> ignore
        sb.Append(System.String.Format(
            jsonInvariant, "{0}", JsonlSchemaVersion)) |> ignore
        sb.Append(",\"id\":\"") |> ignore
        sb.Append(tuple.Id.ToString("D")) |> ignore
        sb.Append("\",\"commandId\":") |> ignore
        sb.Append(formatJsonOptionString tuple.CommandId) |> ignore
        sb.Append(",\"shellId\":\"") |> ignore
        sb.Append(escapeJsonString tuple.ShellId) |> ignore
        sb.Append("\",\"promptStartedAt\":\"") |> ignore
        sb.Append(formatJsonTimestamp tuple.PromptStartedAt) |> ignore
        sb.Append("\",\"commandStartedAt\":") |> ignore
        sb.Append(formatJsonOptionTimestamp tuple.CommandStartedAt) |> ignore
        sb.Append(",\"outputStartedAt\":") |> ignore
        sb.Append(formatJsonOptionTimestamp tuple.OutputStartedAt) |> ignore
        sb.Append(",\"commandFinishedAt\":") |> ignore
        sb.Append(formatJsonOptionTimestamp tuple.CommandFinishedAt) |> ignore
        sb.Append(",\"promptText\":\"") |> ignore
        sb.Append(escapeJsonString tuple.PromptText) |> ignore
        sb.Append("\",\"commandText\":\"") |> ignore
        sb.Append(escapeJsonString tuple.CommandText) |> ignore
        sb.Append("\",\"outputText\":\"") |> ignore
        sb.Append(escapeJsonString tuple.OutputText) |> ignore
        sb.Append("\",\"exitCode\":") |> ignore
        sb.Append(formatJsonOptionInt tuple.ExitCode) |> ignore
        sb.Append(",\"sources\":") |> ignore
        sb.Append(formatSourcesArray tuple.Sources) |> ignore
        sb.Append(",\"extraParams\":") |> ignore
        sb.Append(formatExtraParamsObject tuple.ExtraParams) |> ignore
        sb.Append('}') |> ignore
        sb.Append('\n') |> ignore
        sb.ToString()
