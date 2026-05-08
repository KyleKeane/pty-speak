namespace Terminal.Core

open System
open System.Collections.Generic

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
          State: ActiveTupleState }

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
          PromptText = ""
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
    /// `MaxHistorySize` by dequeueing the oldest tuple
    /// before enqueueing the new one. When `MaxHistorySize`
    /// is `0`, the tuple is dropped silently (no history
    /// retained).
    let private finalizeAndEnqueue
            (state: T)
            (tuple: SessionTuple)
            (finishedAt: DateTime)
            (exitCode: int option)
            : T
            =
        let finalised =
            { tuple with
                CommandFinishedAt = Some finishedAt
                ExitCode = exitCode }
        if state.MaxHistorySize <= 0 then
            { state with Active = None }
        else
            // Ring-buffer eviction: drop oldest until under
            // the cap, then enqueue.
            while state.History.Count >= state.MaxHistorySize do
                state.History.Dequeue() |> ignore
            state.History.Enqueue(finalised)
            { state with Active = None }

    /// **Tier 1.C — Q5 helper**: finalise any in-flight
    /// active tuple as incomplete (`CommandFinishedAt =
    /// finishedAt`, `ExitCode = None`) and move it to the
    /// History. Used by the composition root on shell
    /// hot-switch (per Q5 resolution: per-shell-session
    /// SessionModel; shell-switch finalises prior shell's
    /// active tuple as interrupted before recreating a
    /// fresh SessionModel).
    let finalizeIncomplete (state: T) (finishedAt: DateTime) : T =
        match state.Active with
        | None -> state
        | Some active ->
            finalizeAndEnqueue state active.Tuple finishedAt None

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
    let apply (state: T) (boundary: PromptBoundaryData) : T =
        if state.IsAltScreenActive then
            // Q3 — boundaries ignored during alt-screen.
            state
        else
            let kind = boundary.Kind
            let detectedAt = boundary.DetectedAt
            match state.Active, kind with
            // === AwaitingPromptStart ===
            | None, BoundaryKind.PromptStart ->
                let tuple = newTuple state.ShellId boundary
                let active =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart }
                { state with Active = Some active }
            | None, BoundaryKind.CommandStart ->
                logger.LogWarning(
                    "SessionModel CommandStart with no Active tuple; ignored.")
                state
            | None, BoundaryKind.OutputStart ->
                logger.LogWarning(
                    "SessionModel OutputStart with no Active tuple; ignored.")
                state
            | None, BoundaryKind.CommandFinished _ ->
                logger.LogWarning(
                    "SessionModel CommandFinished with no Active tuple; ignored.")
                state
            // === Active = Some — replaces / advances by kind ===
            | Some active, BoundaryKind.PromptStart ->
                // Interrupted: finalise prior as incomplete,
                // start a new tuple.
                logger.LogInformation(
                    "SessionModel PromptStart while Active={State}; finalising prior as incomplete.",
                    active.State)
                let withPrior =
                    finalizeAndEnqueue state active.Tuple detectedAt None
                let tuple = newTuple state.ShellId boundary
                let nextActive =
                    { Tuple = tuple
                      State = ActiveTupleState.AwaitingCommandStart }
                { withPrior with Active = Some nextActive }
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
                    { state with Active = Some nextActive }
                | ActiveTupleState.EditingCommand ->
                    // Re-submit / re-emit; refresh timestamp.
                    logger.LogWarning(
                        "SessionModel duplicate CommandStart in EditingCommand; refreshing CommandStartedAt.")
                    let nextTuple =
                        { merged.Tuple with
                            CommandStartedAt = Some detectedAt }
                    { state with Active = Some { merged with Tuple = nextTuple } }
                | ActiveTupleState.OutputStreaming
                | ActiveTupleState.AwaitingPromptStart ->
                    logger.LogWarning(
                        "SessionModel CommandStart in unexpected state {State}; ignored.",
                        active.State)
                    { state with Active = Some merged }
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
                    { state with Active = Some nextActive }
                | ActiveTupleState.EditingCommand ->
                    let nextTuple =
                        { merged.Tuple with
                            OutputStartedAt = Some detectedAt }
                    let nextActive =
                        { merged with
                            Tuple = nextTuple
                            State = ActiveTupleState.OutputStreaming }
                    { state with Active = Some nextActive }
                | ActiveTupleState.OutputStreaming ->
                    logger.LogWarning(
                        "SessionModel duplicate OutputStart in OutputStreaming; refreshing OutputStartedAt.")
                    let nextTuple =
                        { merged.Tuple with
                            OutputStartedAt = Some detectedAt }
                    { state with Active = Some { merged with Tuple = nextTuple } }
                | ActiveTupleState.AwaitingPromptStart ->
                    // Should not be observable; guarded above
                    // by `None, OutputStart` arm.
                    state
            | Some active, BoundaryKind.CommandFinished exitCode ->
                let merged = mergeBoundary active boundary
                finalizeAndEnqueue state merged.Tuple detectedAt exitCode
