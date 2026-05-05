namespace Terminal.Core

/// Phase B (subset) — Layer 3 pathway auto-selection.
///
/// Phase A's plan deferred auto-detection of pathway from
/// screen state to "Phase B paired with TOML config". Phase A.1
/// shipped the `DisplayPathway.T.SetBaseline` primitive that
/// makes mid-session pathway swap a solved problem (no leaked
/// state, no stale-diff regression). With that primitive in
/// hand, alt-screen auto-detect can ship independently of the
/// TOML config — TuiPathway becomes useful for vim / less /
/// top / full-screen fzf without the user having to set
/// `PTYSPEAK_SHELL` to a TUI-default shell.
///
/// **Decision contract.** This module is a pure decision
/// helper consulted from `Program.fs`'s `PathwayPump` when a
/// `ScreenNotification.ModeChanged` arrives. It looks at:
/// - the current pathway's `Id` (`"stream"` or `"tui"` today)
/// - the notification's `(flag, value)` pair
///
/// and returns one of three actions:
/// - `Keep` — no swap; the current pathway handles the mode
///   barrier and continues operating
/// - `SwapToTui` — alt-screen entered while not already on
///   TuiPathway
/// - `SwapToShellDefault` — alt-screen exited while currently
///   on TuiPathway; swap back to whatever the active shell's
///   default pathway is (`StreamPathway` for cmd / powershell /
///   claude in v1)
///
/// The caller resolves `SwapToShellDefault` by calling its
/// own `selectPathwayForShell currentShellId` — `PathwaySelector`
/// stays free of the `ShellRegistry` dependency from
/// `Terminal.Pty`.
///
/// **Why a separate module instead of inline match.** The
/// decision is small but the test surface benefits from
/// isolation: `Program.fs`'s `compose ()` body is hard to unit-
/// test without a full WPF + ConPty harness, but a pure
/// `(string, ScreenNotification) -> Action` function can be
/// pinned exhaustively without any of that.
///
/// **Spec reference.** The architectural-spec draft at the top
/// of `/root/.claude/plans/hello-i-lost-my-velvet-deer.md`
/// (Phase B — auto-detection of pathway from screen state).
module PathwaySelector =

    /// What to do with the active pathway in response to a
    /// `ScreenNotification.ModeChanged`. The caller is
    /// responsible for the actual swap (`OnModeBarrier` flush
    /// + `Reset` + reassign + `SetBaseline`); this module just
    /// decides which direction.
    type AltScreenAction =
        /// Keep the current pathway. The vast majority of
        /// `ModeChanged` events take this branch (bracketed-
        /// paste toggles, focus-reporting toggles, alt-screen
        /// toggles when the pathway is already aligned).
        | Keep
        /// Alt-screen entered AND the current pathway is not
        /// TuiPathway. Caller swaps to a fresh TuiPathway.
        | SwapToTui
        /// Alt-screen exited AND the current pathway IS
        /// TuiPathway. Caller swaps back to whatever the active
        /// shell's default pathway is (looks up via the
        /// caller's `selectPathwayForShell` helper).
        | SwapToShellDefault

    /// Decide what to do with the active pathway when a
    /// `ScreenNotification.ModeChanged` arrives. Pure function
    /// of `(currentPathwayId, notification)`; no side effects.
    ///
    /// **Behaviour.**
    /// - `ModeChanged(AltScreen, true)` + current pathway is
    ///   not TuiPathway → `SwapToTui`. The user just ran
    ///   `vim` / `less` / similar; switch to alt-screen-aware
    ///   suppression.
    /// - `ModeChanged(AltScreen, false)` + current pathway IS
    ///   TuiPathway → `SwapToShellDefault`. The user just
    ///   quit the alt-screen TUI; swap back to streaming.
    /// - All other `ModeChanged` events (bracketed-paste,
    ///   focus-reporting, alt-screen-toggle when pathway is
    ///   already aligned) → `Keep`.
    /// - Non-`ModeChanged` notifications → `Keep` (defensive;
    ///   callers shouldn't pass these but the function stays
    ///   total).
    let decideAltScreenAction
            (currentPathwayId: string)
            (notification: ScreenNotification)
            : AltScreenAction
            =
        match notification with
        | ModeChanged (TerminalModeFlag.AltScreen, true)
            when currentPathwayId <> TuiPathway.id ->
            SwapToTui
        | ModeChanged (TerminalModeFlag.AltScreen, false)
            when currentPathwayId = TuiPathway.id ->
            SwapToShellDefault
        | _ ->
            Keep
