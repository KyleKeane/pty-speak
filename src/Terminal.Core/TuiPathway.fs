namespace Terminal.Core

open System
open Microsoft.Extensions.Logging

/// Phase A — TUI display pathway. The selection-time pathway for
/// shells that drive an alt-screen TUI (`vim`, `less`, `top`,
/// fzf in full-screen mode). Emits NO StreamChunk events while
/// alt-screen is active — the user navigates via NVDA's review
/// cursor / browse mode rather than streaming announcements,
/// which matches the implicit Stage-5 / 4.5 behaviour pty-speak
/// shipped before Phase A.
///
/// **Why a pathway and not a profile filter.** Stage 4.5 / 5
/// implemented alt-screen suppression as a Coalescer-side
/// suppression clause (`Coalescer.processRowsChanged` early-
/// returned when AltScreen was set). That fused the suppression
/// to a single algorithm; per-shell selection wasn't possible
/// because every shell shared the Coalescer. Lifting it to a
/// Layer-3 pathway makes the choice explicit + per-shell
/// selectable: vim / less / fzf shells get TuiPathway, cmd /
/// powershell / claude get StreamPathway. v1 still hardcodes the
/// per-shell mapping in `Program.fs`; Phase B's TOML config
/// replaces the hardcoding.
///
/// **Mode-barrier behaviour.** When `OnModeBarrier` fires
/// (alt-screen toggle in / out, bracketed-paste toggle, focus-
/// reporting toggle), the pathway emits an empty barrier
/// OutputEvent so the channel sees the transition in the log
/// trail. The semantic category is `ModeBarrier` (not
/// `AltScreenEntered`) because the pathway can't reliably
/// distinguish the two from the canonical-state substrate alone
/// — the mode flag is supplied by the caller via the
/// `OnModeBarrier` invocation, but the v1 pathway interface
/// passes only `DateTimeOffset`. A future Phase A.2 may extend
/// the interface with the `(flag, value)` pair if log-side
/// disambiguation becomes load-bearing.
///
/// **Tick behaviour.** No-op. TuiPathway never accumulates;
/// nothing to flush.
///
/// **Reset behaviour.** No-op. TuiPathway has no per-session
/// state.
///
/// **Spec reference.** The architectural-spec draft at the top
/// of `/root/.claude/plans/hello-i-lost-my-velvet-deer.md`
/// (Layer 3 — TuiPathway).
module TuiPathway =

    [<Literal>]
    let id: string = "tui"

    /// Build the empty mode-barrier OutputEvent emitted when
    /// the active shell flips a screen mode. Producer-stamp is
    /// "tui" so the log trail attributes the barrier to this
    /// pathway. Empty payload — NvdaChannel skips it (no
    /// audible double-up) and FileLoggerChannel records it
    /// (audit trail).
    let private modeBarrierEvent () : OutputEvent =
        OutputEvent.create
            SemanticCategory.ModeBarrier
            Priority.Polite
            id
            ""

    /// Construct a TuiPathway. The pathway is stateless beyond
    /// the producer-id capture; one instance per shell session
    /// is the convention but multi-instance is harmless.
    let create () : DisplayPathway.T =
        let logger = Logger.get "Terminal.Core.TuiPathway"
        { Id = id
          Consume =
            fun _canonical ->
                // No streaming output — alt-screen navigation
                // is review-cursor driven, not announcement
                // driven.
                logger.LogDebug("Suppressed (alt-screen pathway).")
                [||]
          Tick =
            fun _now ->
                // No accumulator; nothing to flush.
                [||]
          OnModeBarrier =
            fun _now ->
                logger.LogInformation("Mode barrier (TuiPathway).")
                [| modeBarrierEvent () |]
          Reset =
            fun () ->
                // No state to reset.
                () }
