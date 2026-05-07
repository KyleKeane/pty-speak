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
    /// factory + no-op `apply`. Tier 1.C wires the state
    /// machine (transitions, History pruning, etc.).
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
          /// `MaxHistorySize`. Tier 1.A constructs as empty
          /// `Queue<>`; Tier 1.C populates via `apply`.
          History: Queue<SessionTuple>
          /// Maximum tuples retained in History. Default
          /// 100 per SESSION-MODEL.md §4. Construction-time
          /// parameter; Tier 1.A doesn't enforce yet (no
          /// state machine).
          MaxHistorySize: int
          /// In-flight tuple, if any. `None` between
          /// `CommandFinished` and the next `PromptStart`.
          Active: ActiveSessionTuple option }

    /// Default history size. Per SESSION-MODEL.md §4
    /// recommendation. Tier 1.A captures the constant; Tier
    /// 1.C enforces the bound during `apply`.
    [<Literal>]
    let DefaultMaxHistorySize: int = 100

    /// Construct a new SessionModel for the given shell.
    /// Caller supplies a stable shell-key string (e.g.
    /// `"cmd"`, `"powershell"`, `"claude"`); the composition
    /// root translates `ShellId → string` at construction
    /// time per the assembly-boundary convention.
    ///
    /// **Tier 1.A note**: `apply` is a no-op stub; calling
    /// `create` then dispatching `BoundaryKind` events via
    /// `apply` returns state unchanged. Tier 1.C ships the
    /// real state machine.
    let create (shellId: string) (maxHistorySize: int) : T =
        { ShellId = shellId
          SessionId = Guid.NewGuid()
          SessionStartedAt = DateTime.UtcNow
          History = Queue<SessionTuple>()
          MaxHistorySize = maxHistorySize
          Active = None }

    /// Convenience overload: construct with the default
    /// max-history-size (100).
    let createDefault (shellId: string) : T =
        create shellId DefaultMaxHistorySize

    /// **Tier 1.A: no-op stub.** Receives a
    /// `PromptBoundaryData` event and returns the state
    /// unchanged. Tier 1.C overrides this with the real
    /// state machine (transitions, `Active` mutation,
    /// `History` enqueue + bound enforcement).
    ///
    /// The signature is locked here so future cycles can
    /// extend the implementation without protocol churn.
    let apply (state: T) (_boundary: PromptBoundaryData) : T =
        state
