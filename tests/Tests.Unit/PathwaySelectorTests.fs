module PtySpeak.Tests.Unit.PathwaySelectorTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Phase B (subset) — PathwaySelector behavioural pinning
// ---------------------------------------------------------------------
//
// PathwaySelector.decideAltScreenAction is a pure function over
// (currentPathwayId, ScreenNotification) → AltScreenAction. The
// PathwayPump consults it on every ModeChanged event to decide
// whether to swap pathways. These tests pin the swap matrix +
// the no-swap defaults.

[<Fact>]
let ``alt-screen entry from stream pathway returns SwapToTui`` () =
    let action =
        PathwaySelector.decideAltScreenAction
            "stream"
            (ModeChanged (TerminalModeFlag.AltScreen, true))
    Assert.Equal(PathwaySelector.SwapToTui, action)

[<Fact>]
let ``alt-screen entry from tui pathway returns Keep`` () =
    // Already on TuiPathway — no swap needed.
    let action =
        PathwaySelector.decideAltScreenAction
            "tui"
            (ModeChanged (TerminalModeFlag.AltScreen, true))
    Assert.Equal(PathwaySelector.Keep, action)

[<Fact>]
let ``alt-screen exit from tui pathway returns SwapToShellDefault`` () =
    let action =
        PathwaySelector.decideAltScreenAction
            "tui"
            (ModeChanged (TerminalModeFlag.AltScreen, false))
    Assert.Equal(PathwaySelector.SwapToShellDefault, action)

[<Fact>]
let ``alt-screen exit from stream pathway returns Keep`` () =
    // Already on StreamPathway — no swap needed.
    let action =
        PathwaySelector.decideAltScreenAction
            "stream"
            (ModeChanged (TerminalModeFlag.AltScreen, false))
    Assert.Equal(PathwaySelector.Keep, action)

[<Fact>]
let ``bracketed-paste toggle returns Keep regardless of pathway`` () =
    // Non-alt-screen mode flips don't trigger pathway swaps.
    let action1 =
        PathwaySelector.decideAltScreenAction
            "stream"
            (ModeChanged (TerminalModeFlag.BracketedPaste, true))
    Assert.Equal(PathwaySelector.Keep, action1)
    let action2 =
        PathwaySelector.decideAltScreenAction
            "tui"
            (ModeChanged (TerminalModeFlag.BracketedPaste, false))
    Assert.Equal(PathwaySelector.Keep, action2)

[<Fact>]
let ``focus-reporting toggle returns Keep regardless of pathway`` () =
    let action1 =
        PathwaySelector.decideAltScreenAction
            "stream"
            (ModeChanged (TerminalModeFlag.FocusReporting, true))
    Assert.Equal(PathwaySelector.Keep, action1)
    let action2 =
        PathwaySelector.decideAltScreenAction
            "tui"
            (ModeChanged (TerminalModeFlag.FocusReporting, false))
    Assert.Equal(PathwaySelector.Keep, action2)

[<Fact>]
let ``RowsChanged notification returns Keep`` () =
    // Defensive: PathwayPump only calls decideAltScreenAction
    // for ModeChanged, but the function stays total.
    let action =
        PathwaySelector.decideAltScreenAction
            "stream"
            (RowsChanged [])
    Assert.Equal(PathwaySelector.Keep, action)

[<Fact>]
let ``Bell notification returns Keep`` () =
    let action =
        PathwaySelector.decideAltScreenAction
            "stream"
            ScreenNotification.Bell
    Assert.Equal(PathwaySelector.Keep, action)

[<Fact>]
let ``ParserError notification returns Keep`` () =
    let action =
        PathwaySelector.decideAltScreenAction
            "stream"
            (ParserError "boom")
    Assert.Equal(PathwaySelector.Keep, action)

[<Fact>]
let ``unknown pathway id treats alt-screen entry as SwapToTui`` () =
    // Any pathway other than "tui" gets swapped on alt-screen
    // entry. A future ClaudeCodePathway with id = "claude-code"
    // would currently swap to TuiPathway when entering alt-
    // screen — until ClaudeCodePathway gains its own alt-
    // screen-aware behaviour. Pinning this contract so the
    // future change is visible at PR-review time.
    let action =
        PathwaySelector.decideAltScreenAction
            "claude-code"
            (ModeChanged (TerminalModeFlag.AltScreen, true))
    Assert.Equal(PathwaySelector.SwapToTui, action)

[<Fact>]
let ``unknown pathway id treats alt-screen exit as Keep`` () =
    // Inverse of the above: a pathway not currently on TuiPathway
    // doesn't need to swap "back" on alt-screen exit. (The user
    // never went to TuiPathway in the first place.)
    let action =
        PathwaySelector.decideAltScreenAction
            "claude-code"
            (ModeChanged (TerminalModeFlag.AltScreen, false))
    Assert.Equal(PathwaySelector.Keep, action)
