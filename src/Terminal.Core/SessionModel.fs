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

    /// State of the active IOCell. Mirrors the
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
    /// Lifecycle phase of an IOCell. The active cell carries
    /// one of {Composing, Executing, AwaitingSubPromptResponse};
    /// every cell in `History` carries Sealed.
    ///
    /// v1 (Cycle 51 PR-W) populates `Composing` for the active
    /// cell and `Sealed` for cells in `History`. `Executing` and
    /// `AwaitingSubPromptResponse` are reserved — the DU shape
    /// is locked now (ADR 0004) so the on-disk wire format
    /// (schemaVersion 2) is forward-stable; a later PR drives
    /// the intermediate transitions.
    [<RequireQualifiedAccess>]
    type IOCellPhase =
        | Composing
        | Executing
        | AwaitingSubPromptResponse of subPromptText: string
        | Sealed

    type IOCell =
        { /// Unique cell identifier; survives serialisation.
          /// Assigned at CELL CREATION (the PromptStart-driven
          /// transition that opens the cell), never at seal —
          /// the active cell IS the same identity it'll have
          /// once sealed (ADR 0004 Decision 1).
          Id: Guid
          /// Monotonic per-shell-session cell index, starting
          /// at 0. Assigned at cell creation alongside `Id`.
          /// Resets to 0 on shell hot-switch: a fresh
          /// `SessionModel.T` is constructed per shell session
          /// (per Q5 resolution 2026-05-07), so the counter
          /// resets naturally. NEW in v1 (ADR 0004).
          CellSequence: int64
          /// OSC 133 `aid=<command-id>` parameter, if shell
          /// supplied one. Lets cross-session correlation
          /// happen without timing-window heuristics. None
          /// when not supplied (most cases today).
          CommandId: string option
          /// Lifecycle phase. `Composing` while active;
          /// `Sealed` once moved to `History`. NEW in v1
          /// (ADR 0004).
          Phase: IOCellPhase
          /// Which shell produced this cell. String-keyed
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

    /// Active (in-flight) IOCell. Mutable view: the
    /// tuple's fields are populated incrementally as
    /// `BoundaryKind` events arrive. When `CommandFinished`
    /// arrives the tuple is finalised + moved to History.
    type ActiveIOCell =
        { /// The tuple data (in-progress; some fields may
          /// be unset).
          Tuple: IOCell
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
          History: Queue<IOCell>
          /// Maximum tuples retained in History. Default
          /// 100 per SESSION-MODEL.md §4. Tier 1.C enforces
          /// the bound during `apply`'s tuple-finalisation
          /// branch.
          MaxHistorySize: int
          /// In-flight tuple, if any. `None` between
          /// `CommandFinished` and the next `PromptStart`.
          Active: ActiveIOCell option
          /// **Tier 1.C — Q3 partial**: when `true`, `apply`
          /// returns state unchanged (boundary events are
          /// ignored during alt-screen / TUI sessions per Q3
          /// resolution 2026-05-07). Tier 1.D wires
          /// `enterAltScreen` / `exitAltScreen` to the
          /// PathwayPump's `ScreenNotification.ModeChanged`
          /// arm; Tier 1.C ships the field + helpers but does
          /// not yet toggle them from composition root.
          IsAltScreenActive: bool
          /// Next `CellSequence` to assign when a new cell is
          /// created. Monotonic from 0; incremented each time
          /// the PromptStart transition opens a cell. Resets
          /// implicitly to 0 on shell hot-switch (a fresh `T`
          /// is constructed per shell session). NEW in v1
          /// (ADR 0004).
          NextCellSequence: int64 }

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
          History = Queue<IOCell>()
          MaxHistorySize = maxHistorySize
          Active = None
          IsAltScreenActive = false
          NextCellSequence = 0L }

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

    /// Build a brand-new IOCell with `PromptStartedAt`
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
            (cellSequence: int64)
            (boundary: PromptBoundaryData)
            : IOCell
            =
        { Id = Guid.NewGuid()
          CellSequence = cellSequence
          CommandId = boundary.CommandId
          Phase = IOCellPhase.Composing
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
            (active: ActiveIOCell)
            (boundary: PromptBoundaryData)
            : ActiveIOCell
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

    /// Finalise the active cell and move it to the History
    /// ring buffer. Enforces `MaxHistorySize` by dequeueing
    /// the oldest cell before enqueueing the new one; when
    /// `MaxHistorySize` is `0` the cell is dropped silently
    /// (no history retained).
    ///
    /// **ADR 0004 Decision 3 — ContentHistory is the sole
    /// extraction substrate; drop-on-None.** The
    /// `linearOverride` THUNK, when invoked, returns
    /// `Some (commandText, outputText)` if ContentHistory has
    /// an authoritative slice for this cell, or `None`. There
    /// is **no row-walk fallback** post-pivot (Cycle 51 PR-W
    /// deleted the screen-row extractor): a `None` result —
    /// whether from a present-but-empty thunk or from the legacy
    /// no-ContentHistory callers (`apply` / `applyAndCapture`
    /// / `finalizeIncomplete`, all of which pass `None`) —
    /// means the cell does NOT finalize. It is dropped (no
    /// History enqueue, `None` returned) and logged at
    /// Information. Loud silence beats a stale-scrollback
    /// garbage announce (maintainer 2026-05-14).
    ///
    /// The thunk is evaluated lazily here (not at
    /// `applyAndCaptureWithContentHistory` entry) so the
    /// substrate is read exactly when a cell finalises, not
    /// on intermediate CommandStart / OutputStart boundaries.
    ///
    /// Return: `Some finalised` only when the cell actually
    /// landed in History; `None` when dropped (no
    /// ContentHistory slice) or `MaxHistorySize <= 0`. The
    /// SessionLogWriter dispatches off the `Some` branch.
    ///
    /// `oldPromptRowIndex` / `newPromptRowIndex` / `snapshot`
    /// are vestigial screen-row plumbing — no longer read
    /// post-pivot; PR-X removes the surrounding screen-row
    /// machinery per the Cycle 51 playbook §6.
    let private finalizeAndEnqueue
            (state: T)
            (tuple: IOCell)
            (finishedAt: DateTime)
            (exitCode: int option)
            (oldPromptRowIndex: int option)
            (newPromptRowIndex: int option)
            (snapshot: Cell[][])
            (linearOverride: (unit -> (string * string) option) option)
            : T * IOCell option
            =
        let extracted =
            match linearOverride with
            | Some thunk -> thunk ()
            | None -> None
        match extracted with
        | None ->
            // ADR 0004 Decision 3 — drop-on-None. No
            // ContentHistory slice (no PromptStart Seq, or a
            // legacy no-ContentHistory caller); the cell does
            // NOT finalize. Loud silence beats a
            // stale-scrollback garbage announce.
            logger.LogInformation(
                "IOCell dropped: no PromptStart Seq in ContentHistory. CellSequence={CellSequence} ShellId={ShellId}",
                tuple.CellSequence,
                tuple.ShellId)
            { state with Active = None }, None
        | Some (commandText, outputText) ->
            logger.LogDebug(
                "SessionModel finalize extraction Path=content-history CmdLen={CmdLen} OutLen={OutLen}",
                commandText.Length,
                outputText.Length)
            let finalised =
                { tuple with
                    CommandFinishedAt = Some finishedAt
                    ExitCode = exitCode
                    CommandText = commandText
                    OutputText = outputText
                    Phase = IOCellPhase.Sealed }
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
            : T * IOCell option
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
    /// new state and the freshly-finalised IOCell (when
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
            : T * IOCell option
            =
        applyAndCaptureCore state boundary snapshot None

    /// Cycle 35b — the shared body. Takes an optional
    /// `linearOverride` THUNK that, when invoked, returns
    /// `Some (cmd, out)` (ContentHistory slice authoritative)
    /// or `None` (drop-on-None per ADR 0004 Decision 3; no
    /// row-walk fallback post-pivot). The thunk is evaluated
    /// lazily inside `finalizeAndEnqueue` so ContentHistory is
    /// only read when an actual IOCell finalize occurs (NOT on
    /// intermediate PromptStart / CommandStart / OutputStart
    /// boundaries that don't enqueue a cell).
    and private applyAndCaptureCore
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (linearOverride: (unit -> (string * string) option) option)
            : T * IOCell option
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
                // ADR 0004 — Id + CellSequence assigned at cell
                // creation (here), not at seal.
                let tuple =
                    newTuple state.ShellId state.NextCellSequence boundary
                let active =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart
                      // Tier 1.E2.B: capture the row index for
                      // future finalize-time output extraction.
                      PromptRowIndex = boundary.MatchedRowIndex }
                { state with
                    Active = Some active
                    NextCellSequence = state.NextCellSequence + 1L }, None
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
                // ADR 0004 — the interrupting prompt opens a new
                // cell; assign Id + CellSequence at creation.
                let tuple =
                    newTuple
                        state.ShellId withPrior.NextCellSequence boundary
                let nextActive =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart
                      PromptRowIndex = boundary.MatchedRowIndex }
                { withPrior with
                    Active = Some nextActive
                    NextCellSequence = withPrior.NextCellSequence + 1L }, finalisedOpt
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
    // Cycle 45c — ContentHistory-driven substrate-aware public
    // surface. (Cycle 35b's `applyAndCaptureWithSubstrate` /
    // `applyWithSubstrate` + `extractContentFromLinearStream`
    // were deleted in Cycle 45c PR-3c alongside the LinearTextStream
    // substrate they peeked. The replacements below take a
    // `ContentHistory.T` + `useContentHistory: bool`.)
    // -----------------------------------------------------------------
    //
    // `applyAndCaptureWithContentHistory` is the runtime entry point
    // used by the composition root's `handlePromptBoundary`. It
    // queries the ContentHistory substrate for the latest PromptStart
    // + OutputStart markers and slices the bracketed text via
    // `ContentHistory.sliceText` to produce the authoritative
    // `(commandText, outputText)` for the IOCell. Per ADR 0004
    // Decision 3 there is no row-walk fallback: when no PromptStart
    // Seq is present the thunk returns `None` and the cell drops.
    //
    // The legacy `apply` / `applyAndCapture` surface is preserved
    // with original signatures for the 80+ existing test callers
    // and for consumers that don't have a ContentHistory in scope.
    // Post-pivot those callers (passing `None` for the override)
    // never finalise a cell — drop-on-None applies (ADR 0004).

    /// Cycle 45c / 51 — ContentHistory text extraction at
    /// IOCell-seal time (the sole extractor post-pivot, ADR
    /// 0004). Locates the latest PromptStart (+ OutputStart if
    /// the shell emits OSC 133) marker in ContentHistory and
    /// slices the bracketed text.
    ///
    /// Returns `Some (commandText, outputText)` when a
    /// PromptStart Seq is present; `None` otherwise — the
    /// drop-on-None signal (the cell does NOT finalize; loud
    /// silence beats stale-scrollback garbage). There is no
    /// row-walk fallback.
    ///
    /// Three arms (precedence top-to-bottom):
    ///   * PromptStart + OutputStart (literal ;C — forward-
    ///     compatible ideal; no shell emits ;C today): clean
    ///     Seq-slice split [A,C) / [C,tail).
    ///   * PromptStart + CommandStart, no OutputStart (R2 cmd
    ///     OSC-133 A/B — ADR 0005/0006, Option B): the [A,B)
    ///     region is the prompt path; anchor the PR-X
    ///     watermark split at the CommandStart marker so the
    ///     typed command + output (after ;B) split cleanly.
    ///     Shell-emitted ;B ⇒ OSC-133 provenance, NOT the
    ///     heuristic fallback.
    ///   * PromptStart only (no OSC 133 at all — pre-R2 cmd /
    ///     vanilla PowerShell / claude): slice PromptStart→
    ///     tail, trim the trailing new-prompt text, PR-X
    ///     watermark or first-newline split. `EntrySource` is
    ///     NOT consulted — the 2026-05-14 bundle showed it
    ///     mis-tags post-response output in heuristic-only cmd
    ///     (ADR 0004 Context).
    let private extractIOCell
            (history: ContentHistory.T)
            (newPromptText: string option)
            (commandEnterSeq: int64)
            : (string * string) option
            =
        // The trailing-next-prompt trim is shared by the
        // heuristic split regardless of which marker anchors
        // the slice lower bound.
        // Cycle 52 boundary-fix P2′ — whitespace/render-tolerant
        // trailing-next-prompt strip. The deferred-`;D` flush
        // (`HI ;D ;A <path> ;B`) arrives as ONE PTY chunk, so the
        // reader thread appends the command output AND the next
        // prompt's path text to ContentHistory together, before
        // any boundary is handled (Program.fs startReaderLoop) —
        // and this boundary's own marker is appended AFTER
        // extractIOCell runs. So at thunk time there is no marker
        // or Seq that fences the trailing prompt; the only signal
        // is `newPromptText` (the boundary's MatchedRowText).
        // The pre-P2′ exact `s.EndsWith(p)` bled the path into
        // OutputText because MatchedRowText is a SNAPSHOT-rendered
        // row (padded / render-variant) while the slice is raw
        // bytes — the suffix never matched exactly (C1/C2 bleed,
        // docs/boundary-capture/). Strip tolerantly: trim trailing
        // CR/LF/space/tab from the slice, then drop a trailing
        // occurrence of the trimmed prompt text (allowing a few
        // residual control bytes after it).
        let stripNextPrompt (s: string) : string =
            match newPromptText with
            | Some p when not (System.String.IsNullOrWhiteSpace p) ->
                let pt = p.Trim()
                let st = s.TrimEnd('\r', '\n', ' ', '\t')
                if pt.Length > 0 && st.EndsWith(pt) then
                    st.Substring(0, st.Length - pt.Length)
                elif pt.Length > 0 then
                    let idx = st.LastIndexOf(pt)
                    if idx >= 0 && idx >= st.Length - pt.Length - 4 then
                        st.Substring(0, idx)
                    else s
                else s
            | _ -> s
        // P2′ — timing-independent cmd OSC-133 A/B split. Anchor
        // at the reliable `;B` (CommandStart) marker of THIS
        // cell's prompt (it is in ContentHistory at thunk time —
        // appended when this cell's prompt boundary was handled;
        // the next prompt's markers are NOT yet appended, so
        // `commandStart.Seq` is correct, not the next prompt's).
        // The `[;A,;B)` prompt path is excluded by construction.
        // Robust-strip the trailing next-prompt FIRST, then split
        // the typed command from output at the first CRLF; the
        // command is the LAST non-empty CR-segment of that first
        // line so doskey / PSReadLine in-place history-scroll
        // reprints (bare `\r`) resolve to the line that actually
        // ran. NO `commandEnterSeq` dependency → immune to the
        // slow/fast echo-timing race that truncated the command
        // (`ECHO H`) and produced the nondeterministic bleed.
        let cmdAbSplit (csSeq: int64) : (string * string) option =
            let raw =
                ContentHistory.sliceText
                    history csSeq System.Int64.MaxValue
            let body =
                ((stripNextPrompt raw).TrimStart('\r', '\n'))
                    .TrimEnd('\r', '\n')
            let lastCrSeg (seg: string) : string =
                seg.Split('\r')
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l.Length > 0)
                |> Array.tryLast
                |> Option.defaultValue ""
            if System.String.IsNullOrWhiteSpace body then None
            else
                match body.IndexOf('\n') with
                | -1 ->
                    // No newline — `cd`-style command with no
                    // output. Attribute to CommandText.
                    Some (lastCrSeg body, "")
                | i ->
                    let cmd = lastCrSeg (body.Substring(0, i))
                    let out =
                        body.Substring(i + 1).Trim([| '\r'; '\n' |])
                    Some (cmd, out)
        // The PR-X watermark split, parameterised by the slice
        // lower-bound Seq. Anchored at `promptStart.Seq` it is
        // the pre-R2 heuristic-only behaviour (byte-identical);
        // anchored at `commandStart.Seq` it is the R2 cmd
        // OSC-133 A/B clean split (the [A,B) prompt-path region
        // is excluded by construction, since the lower bound is
        // the CommandStart marker that follows the path).
        let heuristicSplit (lowerBound: int64) : (string * string) option =
            if commandEnterSeq > lowerBound then
                // Cycle 51 PR-X — Seq-watermark split. The
                // command-Enter watermark (captured at the
                // byte-level Enter in Program.fs) is the exact
                // boundary between "what the user composed at the
                // prompt" (incl. every history-scroll redraw —
                // cmd reprints the line in place with CR on each
                // Up/Down, and ContentHistory accumulates them
                // linearly) and "what the command produced". The
                // command is the LAST non-empty line up to the
                // watermark (the line that actually executed);
                // the output is everything after it, minus the
                // trailing next-prompt. This is immune to the
                // history-scroll accumulation that the
                // first-newline split (fallback below) mis-attributes
                // wholesale into OutputText.
                let cmdRegion =
                    ContentHistory.sliceText
                        history lowerBound commandEnterSeq
                let outRegion =
                    ContentHistory.sliceText
                        history commandEnterSeq System.Int64.MaxValue
                let cmd =
                    cmdRegion.Replace("\r", "\n").Split('\n')
                    |> Array.map (fun l -> l.Trim())
                    |> Array.filter (fun l -> l.Length > 0)
                    |> Array.tryLast
                    |> Option.defaultValue ""
                let out =
                    let tail = stripNextPrompt outRegion
                    (tail.TrimStart('\r', '\n')).TrimEnd('\r', '\n')
                if System.String.IsNullOrWhiteSpace cmd
                   && System.String.IsNullOrWhiteSpace out then
                    None
                else
                    Some (cmd, out)
            else
                // Fallback (legacy callers / no usable watermark,
                // e.g. before the first command Enter): the
                // original first-newline heuristic. cmd's wire
                // format is "echo hi\r\nhi\r\n<new-prompt>" — first
                // line is the typed command (cmd echoes the
                // keystrokes back); the rest is shell output.
                let raw =
                    ContentHistory.sliceText
                        history lowerBound System.Int64.MaxValue
                let withoutNewPrompt = stripNextPrompt raw
                let trimmed =
                    (withoutNewPrompt.TrimStart('\r', '\n'))
                        .TrimEnd('\r', '\n')
                if System.String.IsNullOrWhiteSpace trimmed then None
                else
                    let nlIdx = trimmed.IndexOf('\n')
                    if nlIdx < 0 then
                        // No newline — `cd` etc. that print
                        // nothing. Attribute to CommandText so
                        // OutputText stays empty.
                        Some (trimmed, "")
                    else
                        let cmd =
                            trimmed.Substring(0, nlIdx).TrimEnd('\r')
                        let out =
                            trimmed.Substring(nlIdx + 1)
                        Some (cmd, out)
        let lenOf (r: (string * string) option) : int * int =
            match r with
            | Some (c, o) -> c.Length, o.Length
            | None -> 0, 0
        match
            ContentHistory.tryLatestMarker
                history ContentHistory.MarkerKind.PromptStart,
            ContentHistory.tryLatestMarker
                history ContentHistory.MarkerKind.OutputStart,
            ContentHistory.tryLatestMarker
                history ContentHistory.MarkerKind.CommandStart
        with
        | Some promptStart, Some outputStart, _ ->
            // OSC 133 path — shell emitted both PromptStart and
            // OutputStart, so we have a clean (command, output)
            // split. (No shell emits a literal ;C today; this
            // arm is the forward-compatible ideal.)
            let commandText =
                ContentHistory.sliceText
                    history promptStart.Seq outputStart.Seq
            let outputText =
                ContentHistory.sliceText
                    history outputStart.Seq System.Int64.MaxValue
            logger.LogDebug(
                "extractIOCell arm. Arm={Arm} PromptSeq={PromptSeq} OutputSeq={OutputSeq} CmdLen={CmdLen} OutLen={OutLen}",
                "CleanOscAC",
                promptStart.Seq,
                outputStart.Seq,
                commandText.Length,
                outputText.Length)
            Some (commandText, outputText)
        | Some promptStart, None, Some commandStart ->
            // R2 (ADR 0005/0006, Option B) — cmd OSC-133 A/B
            // clean arm. cmd's `prompt`-only integration emits
            // PromptStart (;A) before the prompt path and
            // CommandStart (;B) after it, but has no hook to
            // emit OutputStart (;C) between Enter and the
            // command's output. Per the maintainer's 2026-05-16
            // decision the consumer realises ADR 0005 §3's
            // "implicit C": anchor the command/output split at
            // the authoritative CommandStart marker — the [A,B)
            // region is the prompt path, the typed command +
            // output follow ;B. Reuses the proven PR-X watermark
            // split (history-scroll-immune) rather than the
            // PromptStart-only first-line heuristic. Provenance
            // is OSC 133 (the ;B marker is shell-emitted), so
            // this is a clean arm, not the heuristic fallback.
            // P2′ — timing-independent split anchored at the
            // reliable `;B` CommandStart marker (see `cmdAbSplit`).
            // Replaces the racy `commandEnterSeq` watermark that
            // truncated the command under slow typing (`ECHO H`)
            // and the exact-`EndsWith` strip that bled the prompt
            // path into OutputText.
            let result = cmdAbSplit commandStart.Seq
            let cl, ol = lenOf result
            logger.LogDebug(
                "extractIOCell arm. Arm={Arm} PromptSeq={PromptSeq} CommandSeq={CommandSeq} CommandEnterSeq={CommandEnterSeq} CmdLen={CmdLen} OutLen={OutLen}",
                "CmdAbTI",
                promptStart.Seq,
                commandStart.Seq,
                commandEnterSeq,
                cl,
                ol)
            result
        | Some promptStart, None, None ->
            // Heuristic-only path (no OSC 133 at all — pre-R2
            // cmd / vanilla PowerShell / claude). Byte-identical
            // to the pre-R2 `Some promptStart, None` arm.
            let result = heuristicSplit promptStart.Seq
            let cl, ol = lenOf result
            logger.LogDebug(
                "extractIOCell arm. Arm={Arm} PromptSeq={PromptSeq} CommandEnterSeq={CommandEnterSeq} CmdLen={CmdLen} OutLen={OutLen}",
                "Heuristic",
                promptStart.Seq,
                commandEnterSeq,
                cl,
                ol)
            result
        | _ -> None

    /// Cycle 45c / 51 — ContentHistory-driven finalize. The
    /// runtime entry point `handlePromptBoundary` calls.
    ///
    /// `useContentHistory = true` → invoke the `extractIOCell`
    /// thunk inside `finalizeAndEnqueue`. `false` → no override
    /// (the thunk is `None`); post-pivot that means drop-on-None
    /// at finalize (ADR 0004 — there is no row-walk fallback).
    /// The boolean is retained for future shells that might opt
    /// out of the substrate; current shells (cmd / PowerShell /
    /// claude) all pass `true`.
    ///
    /// Cycle 51 PR-X — `commandEnterSeq` is the ContentHistory
    /// Seq captured at the byte-level command Enter (Program.fs).
    /// `extractIOCell`'s heuristic-only arm uses it as the
    /// command/output boundary so history-scroll redraws (which
    /// accumulate linearly between PromptStart and the executed
    /// command) are excluded from OutputText. Pass `-1L` (or any
    /// value ≤ the cell's PromptStart Seq) to get the legacy
    /// first-newline heuristic instead.
    let applyAndCaptureWithContentHistory
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (history: ContentHistory.T)
            (useContentHistory: bool)
            (commandEnterSeq: int64)
            : T * IOCell option
            =
        // Thunk-lazy: the override only invokes `extractIOCell`
        // when `finalizeAndEnqueue` actually fires (the
        // CommandFinished / interrupt-PromptStart boundary).
        // Other boundary kinds skip the thunk.
        let contentOverride : (unit -> (string * string) option) option =
            if useContentHistory then
                // Pass the new boundary's MatchedRowText so the
                // heuristic-only path can trim it off the blob's
                // tail (see comment in extractIOCell).
                let newPromptText = boundary.MatchedRowText
                Some (fun () ->
                    extractIOCell history newPromptText commandEnterSeq)
            else
                None
        applyAndCaptureCore state boundary snapshot contentOverride

    /// Cycle 45c — state-only wrapper around
    /// `applyAndCaptureWithContentHistory`.
    let applyWithContentHistory
            (state: T)
            (boundary: PromptBoundaryData)
            (snapshot: Cell[][])
            (history: ContentHistory.T)
            (useContentHistory: bool)
            (commandEnterSeq: int64)
            : T
            =
        applyAndCaptureWithContentHistory
            state boundary snapshot history useContentHistory
            commandEnterSeq
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
            (tuple: IOCell)
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
            (active: ActiveIOCell)
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
    // Cycle 24b / 51 — JSONL serializer for IOCell.
    // -----------------------------------------------------------------
    //
    // Pure function `formatIOCellAsJsonl : IOCell -> string`
    // that emits one JSONL line (a JSON object followed by a literal
    // `\n` terminator). No I/O — Cycle 24c wires the file writer
    // against the Active→History transition seam in
    // `Terminal.App.Program`.
    //
    // **Wire format pinned in `docs/IOCELL-SCHEMA.md` §"On-disk
    // wire format" — needs to remain stable for decades.**
    // Locked decisions:
    //
    // 1. Per-record `"schemaVersion":2` as the first key
    //    (Cycle 51 PR-W bumped 1 → 2 for the IOCell rename +
    //    the `cellSequence` + `phase` fields). Future schema
    //    changes increment further; replay tools branch on it.
    //    schemaVersion=1 and =2 are mutually unreadable — the
    //    migration is one-way per ADR 0004.
    // 2. `BoundarySource` always serialises as a tagged object
    //    `{"kind":"<case>", ...payload}` — uniform shape across
    //    all DU cases; future cases land cleanly.
    // 2b. `phase` serialises as a tagged DU object by the same
    //    rule: `{"kind":"composing"}`, `{"kind":"executing"}`,
    //    `{"kind":"awaitingSubPromptResponse","subPromptText":"…"}`,
    //    `{"kind":"sealed"}`. `cellSequence` is a JSON number
    //    (int64).
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
    /// the value to apply per-version deserializers. Cycle 51
    /// PR-W bumped 1 → 2 (IOCell rename + `cellSequence` +
    /// `phase`).
    [<Literal>]
    let JsonlSchemaVersion : int = 2

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

    /// `IOCellPhase` as a tagged DU object (same rule as
    /// `formatBoundarySourceTaggedObject`). Cycle 51 PR-W.
    let private formatPhaseTaggedObject (phase: IOCellPhase) : string =
        match phase with
        | IOCellPhase.Composing -> "{\"kind\":\"composing\"}"
        | IOCellPhase.Executing -> "{\"kind\":\"executing\"}"
        | IOCellPhase.AwaitingSubPromptResponse subPromptText ->
            let sb = System.Text.StringBuilder()
            sb.Append("{\"kind\":\"awaitingSubPromptResponse\",\"subPromptText\":\"")
              |> ignore
            sb.Append(escapeJsonString subPromptText) |> ignore
            sb.Append("\"}") |> ignore
            sb.ToString()
        | IOCellPhase.Sealed -> "{\"kind\":\"sealed\"}"

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

    /// Serialize one `IOCell` as one JSONL line — a JSON
    /// object followed by a single literal `\n` terminator.
    /// Pure (no I/O); safe to call from any thread; total (never
    /// returns null; throws only on lone UTF-16 surrogates per
    /// `escapeJsonString`).
    ///
    /// **Wire format is decades-stable** — see the section
    /// docstring above for locked decisions and
    /// `docs/IOCELL-SCHEMA.md` §"On-disk wire format"
    /// for the canonical reference.
    ///
    /// Trailing terminator is the literal string `"\n"`, never
    /// `Environment.NewLine` — the latter would emit `\r\n` on
    /// Windows and silently break byte-for-byte stability across
    /// platforms. Cycle 24c writer concatenates lines without
    /// further processing.
    let formatIOCellAsJsonl (tuple: IOCell) : string =
        let sb = System.Text.StringBuilder()
        sb.Append('{') |> ignore
        sb.Append("\"schemaVersion\":") |> ignore
        sb.Append(System.String.Format(
            jsonInvariant, "{0}", JsonlSchemaVersion)) |> ignore
        sb.Append(",\"id\":\"") |> ignore
        sb.Append(tuple.Id.ToString("D")) |> ignore
        sb.Append("\",\"cellSequence\":") |> ignore
        sb.Append(System.String.Format(
            jsonInvariant, "{0}", tuple.CellSequence)) |> ignore
        sb.Append(",\"commandId\":") |> ignore
        sb.Append(formatJsonOptionString tuple.CommandId) |> ignore
        sb.Append(",\"shellId\":\"") |> ignore
        sb.Append(escapeJsonString tuple.ShellId) |> ignore
        sb.Append("\",\"phase\":") |> ignore
        sb.Append(formatPhaseTaggedObject tuple.Phase) |> ignore
        sb.Append(",\"promptStartedAt\":\"") |> ignore
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

    // -----------------------------------------------------------------
    // Cycle 51 PR-W2 — round-trip reader. Hand-rolled (no JSON
    // library; matches the Cycle 24b serializer discipline). The
    // exact inverse of `formatIOCellAsJsonl`. No user-facing
    // surface — exercised only by the FsCheck round-trip property
    // + deterministic edge tests. ADR 0004 §"Round-trip
    // discipline": branches on `schemaVersion` (only 2 in v1;
    // `Error` for anything else); any future schema change that
    // isn't round-trip-faithful fails CI immediately.
    // -----------------------------------------------------------------

    /// Why a JSONL line could not be parsed back into an `IOCell`.
    type IOCellParseError =
        /// `schemaVersion` was present but not the supported value
        /// (2 in v1). One-way migration per ADR 0004 — v1 and v2
        /// are mutually unreadable.
        | UnsupportedSchemaVersion of int
        /// The line was not a well-formed v2 IOCell record
        /// (structural error, missing/!typed field, bad timestamp
        /// / guid / number, or a lone UTF-16 surrogate — the
        /// reader rejects the same payload the serializer refuses
        /// to emit).
        | Malformed of string

    exception JsonParseException of string

    type private JsonValue =
        | JNull
        | JStr of string
        | JNum of string
        | JObj of (string * JsonValue) list
        | JArr of JsonValue list

    /// Minimal recursive-descent JSON value parser. Only the
    /// productions `formatIOCellAsJsonl` emits (object, array,
    /// string, integer number, `null`) are recognised — booleans
    /// and floats are intentionally absent. Raises
    /// `JsonParseException` on any structural problem.
    let private parseJson (s: string) : JsonValue =
        let n = s.Length
        let mutable i = 0
        let fail (msg: string) : 'a = raise (JsonParseException msg)
        let peek () = if i < n then s.[i] else ' '
        let skipWs () =
            while i < n
                  && (s.[i] = ' ' || s.[i] = '\t'
                      || s.[i] = '\n' || s.[i] = '\r') do
                i <- i + 1
        let expect (c: char) : unit =
            if i < n && s.[i] = c then i <- i + 1
            else fail (sprintf "expected '%c' at index %d" c i)
        let hasLoneSurrogate (str: string) : bool =
            let mutable k = 0
            let mutable bad = false
            while k < str.Length && not bad do
                let ch = str.[k]
                if System.Char.IsHighSurrogate ch then
                    if k + 1 < str.Length
                       && System.Char.IsLowSurrogate str.[k + 1]
                    then k <- k + 2
                    else bad <- true
                elif System.Char.IsLowSurrogate ch then bad <- true
                else k <- k + 1
            bad
        let parseStr () : string =
            expect '"'
            let sb = System.Text.StringBuilder()
            let mutable fin = false
            while not fin do
                if i >= n then fail "unterminated string"
                let c = s.[i]
                if c = '"' then
                    i <- i + 1
                    fin <- true
                elif c = '\\' then
                    i <- i + 1
                    if i >= n then fail "dangling escape"
                    let e = s.[i]
                    i <- i + 1
                    match e with
                    | '"' -> sb.Append('"') |> ignore
                    | '\\' -> sb.Append('\\') |> ignore
                    | '/' -> sb.Append('/') |> ignore
                    | 'b' -> sb.Append('\b') |> ignore
                    | 'f' -> sb.Append('\f') |> ignore
                    | 'n' -> sb.Append('\n') |> ignore
                    | 'r' -> sb.Append('\r') |> ignore
                    | 't' -> sb.Append('\t') |> ignore
                    | 'u' ->
                        if i + 4 > n then fail "truncated \\u escape"
                        let hex = s.Substring(i, 4)
                        i <- i + 4
                        let mutable code = 0
                        if System.Int32.TryParse(
                            hex,
                            System.Globalization.NumberStyles.HexNumber,
                            jsonInvariant,
                            &code)
                        then sb.Append(char code) |> ignore
                        else fail "invalid \\u hex"
                    | _ -> fail "unknown string escape"
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            let result = sb.ToString()
            if hasLoneSurrogate result then
                fail "lone UTF-16 surrogate in string"
            result
        let rec parseValue () : JsonValue =
            skipWs ()
            if i >= n then fail "unexpected end of input"
            else
                match s.[i] with
                | '"' -> JStr (parseStr ())
                | '{' -> parseObj ()
                | '[' -> parseArr ()
                | 'n' ->
                    if i + 4 <= n && s.Substring(i, 4) = "null" then
                        i <- i + 4
                        JNull
                    else fail "expected null"
                | c when c = '-' || (c >= '0' && c <= '9') ->
                    let start = i
                    if s.[i] = '-' then i <- i + 1
                    while i < n && s.[i] >= '0' && s.[i] <= '9' do
                        i <- i + 1
                    if i = start || (i = start + 1 && s.[start] = '-') then
                        fail "malformed number"
                    JNum (s.Substring(start, i - start))
                | _ -> fail (sprintf "unexpected char at index %d" i)
        and parseObj () : JsonValue =
            expect '{'
            skipWs ()
            let items = ResizeArray<string * JsonValue>()
            if peek () = '}' then
                i <- i + 1
            else
                let mutable go = true
                while go do
                    skipWs ()
                    let k = parseStr ()
                    skipWs ()
                    expect ':'
                    let v = parseValue ()
                    items.Add((k, v))
                    skipWs ()
                    if peek () = ',' then i <- i + 1
                    elif peek () = '}' then
                        i <- i + 1
                        go <- false
                    else fail "expected ',' or '}' in object"
            JObj (List.ofSeq items)
        and parseArr () : JsonValue =
            expect '['
            skipWs ()
            let items = ResizeArray<JsonValue>()
            if peek () = ']' then
                i <- i + 1
            else
                let mutable go = true
                while go do
                    let v = parseValue ()
                    items.Add(v)
                    skipWs ()
                    if peek () = ',' then i <- i + 1
                    elif peek () = ']' then
                        i <- i + 1
                        go <- false
                    else fail "expected ',' or ']' in array"
            JArr (List.ofSeq items)
        let v = parseValue ()
        skipWs ()
        if i <> n then fail "trailing content after JSON value"
        v

    /// Parse one JSONL line (the exact output of
    /// `formatIOCellAsJsonl`, with or without its trailing `\n`)
    /// back into an `IOCell`. Hand-rolled inverse; total (never
    /// throws — all failures surface as `Result.Error`).
    let parseFromJsonl (line: string) : Result<IOCell, IOCellParseError> =
        try
            let trimmed =
                if line.EndsWith("\n") then
                    line.Substring(0, line.Length - 1)
                else line
            match parseJson trimmed with
            | JObj fields ->
                let get (k: string) : JsonValue =
                    match
                        List.tryFind (fun (kk, _) -> kk = k) fields
                    with
                    | Some (_, v) -> v
                    | None ->
                        raise (JsonParseException (sprintf "missing field '%s'" k))
                let field (obj: (string * JsonValue) list) (k: string) : JsonValue =
                    match List.tryFind (fun (kk, _) -> kk = k) obj with
                    | Some (_, v) -> v
                    | None ->
                        raise (JsonParseException (sprintf "missing field '%s'" k))
                let asStr (j: JsonValue) : string =
                    match j with
                    | JStr x -> x
                    | _ -> raise (JsonParseException "expected a string")
                let asInt (j: JsonValue) : int =
                    match j with
                    | JNum x ->
                        (try System.Int32.Parse(x, jsonInvariant)
                         with _ -> raise (JsonParseException "expected int32"))
                    | _ -> raise (JsonParseException "expected a number")
                let asInt64 (j: JsonValue) : int64 =
                    match j with
                    | JNum x ->
                        (try System.Int64.Parse(x, jsonInvariant)
                         with _ -> raise (JsonParseException "expected int64"))
                    | _ -> raise (JsonParseException "expected a number")
                let sv = asInt (get "schemaVersion")
                if sv <> JsonlSchemaVersion then
                    Error (UnsupportedSchemaVersion sv)
                else
                    let parseTs (str: string) : DateTime =
                        DateTime.ParseExact(
                            str,
                            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                            jsonInvariant,
                            System.Globalization.DateTimeStyles.AssumeUniversal
                            ||| System.Globalization.DateTimeStyles.AdjustToUniversal)
                    let optTs (j: JsonValue) : DateTime option =
                        match j with
                        | JNull -> None
                        | JStr x -> Some (parseTs x)
                        | _ -> raise (JsonParseException "expected timestamp|null")
                    let optStr (j: JsonValue) : string option =
                        match j with
                        | JNull -> None
                        | JStr x -> Some x
                        | _ -> raise (JsonParseException "expected string|null")
                    let optInt (j: JsonValue) : int option =
                        match j with
                        | JNull -> None
                        | JNum _ -> Some (asInt j)
                        | _ -> raise (JsonParseException "expected int|null")
                    let phase =
                        match get "phase" with
                        | JObj pf ->
                            (match asStr (field pf "kind") with
                             | "composing" -> IOCellPhase.Composing
                             | "executing" -> IOCellPhase.Executing
                             | "sealed" -> IOCellPhase.Sealed
                             | "awaitingSubPromptResponse" ->
                                 IOCellPhase.AwaitingSubPromptResponse
                                     (asStr (field pf "subPromptText"))
                             | other ->
                                 raise (JsonParseException
                                     (sprintf "unknown phase kind '%s'" other)))
                        | _ -> raise (JsonParseException "phase is not an object")
                    let boundaryOf (name: string) : BoundaryKind =
                        match name with
                        | "PromptStart" -> BoundaryKind.PromptStart
                        | "CommandStart" -> BoundaryKind.CommandStart
                        | "OutputStart" -> BoundaryKind.OutputStart
                        // The serializer emits the payload-less
                        // name; the exit code lives on ExitCode,
                        // not here. Canonical reconstruction.
                        | "CommandFinished" -> BoundaryKind.CommandFinished None
                        | other ->
                            raise (JsonParseException
                                (sprintf "unknown boundary '%s'" other))
                    let sourceOf (j: JsonValue) : BoundarySource =
                        match j with
                        | JObj sf ->
                            (match asStr (field sf "kind") with
                             | "Osc133" -> BoundarySource.Osc133
                             | "HeuristicClaudeInkBox" ->
                                 BoundarySource.HeuristicClaudeInkBox
                             | "HeuristicPromptRegex" ->
                                 BoundarySource.HeuristicPromptRegex
                                     (asInt (field sf "stabilityMs"))
                             | other ->
                                 raise (JsonParseException
                                     (sprintf "unknown source kind '%s'" other)))
                        | _ -> raise (JsonParseException "source is not an object")
                    let sources =
                        match get "sources" with
                        | JArr arr ->
                            arr
                            |> List.map (fun e ->
                                match e with
                                | JObj ef ->
                                    boundaryOf (asStr (field ef "boundary")),
                                    sourceOf (field ef "source")
                                | _ ->
                                    raise (JsonParseException
                                        "sources entry is not an object"))
                            |> Map.ofList
                        | _ -> raise (JsonParseException "sources is not an array")
                    let extraParams =
                        match get "extraParams" with
                        | JObj ef ->
                            ef
                            |> List.map (fun (k, v) -> k, asStr v)
                            |> Map.ofList
                        | _ ->
                            raise (JsonParseException
                                "extraParams is not an object")
                    let cell : IOCell =
                        { Id = System.Guid.Parse(asStr (get "id"))
                          CellSequence = asInt64 (get "cellSequence")
                          CommandId = optStr (get "commandId")
                          Phase = phase
                          ShellId = asStr (get "shellId")
                          PromptStartedAt =
                              parseTs (asStr (get "promptStartedAt"))
                          CommandStartedAt = optTs (get "commandStartedAt")
                          OutputStartedAt = optTs (get "outputStartedAt")
                          CommandFinishedAt = optTs (get "commandFinishedAt")
                          PromptText = asStr (get "promptText")
                          CommandText = asStr (get "commandText")
                          OutputText = asStr (get "outputText")
                          ExitCode = optInt (get "exitCode")
                          Sources = sources
                          ExtraParams = extraParams }
                    Ok cell
            | _ ->
                Error (Malformed "top-level value is not a JSON object")
        with
        | JsonParseException msg -> Error (Malformed msg)
        | :? System.FormatException as ex -> Error (Malformed ex.Message)
        | :? System.OverflowException as ex -> Error (Malformed ex.Message)
