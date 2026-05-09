module PtySpeak.Tests.Unit.MultiStateRegistryTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 27 — MultiStateRegistry pinning
// ---------------------------------------------------------------------
//
// `HotkeyRegistry.MultiStateCommand` is the parallel concept to
// `AppCommand` for operations whose UX is "select one of N
// discrete options" rather than "fire one action". The two
// migrating commands as of Cycle 27 are:
//
//   - `EarconsMode` — formerly the `MuteEarcons` toggle
//     (Ctrl+Shift+M). Options: enabled / muted.
//   - `LoggingLevel` — formerly the `ToggleDebugLog` toggle
//     (Ctrl+Shift+G). Options: information / debug.
//
// These fixtures pin the same shape contracts that
// `HotkeyRegistryTests` pins for `AppCommand`:
//
//   1. `multiStateAllCommands` matches the
//      `MultiStateCommand` DU — every case appears, no orphans.
//   2. `multiStateBuiltIns` has exactly one entry per
//      `MultiStateCommand` — every command is wireable, no
//      duplicates.
//   3. Each `MultiStateDef` has at least 2 distinct options
//      (a one-option "multi-state" command is meaningless).
//   4. OptionIds within a single `MultiStateDef` are unique
//      (a duplicate would be ambiguous in `bindMultiState`'s
//      per-option `RoutedCommand` dictionary).
//   5. The two migrating commands' OptionIds are pinned to
//      their documented values (catches accidental rename).

// ---------------------------------------------------------------------
// multiStateAllCommands matches MultiStateCommand DU
// ---------------------------------------------------------------------

[<Fact>]
let ``multiStateAllCommands round-trips every MultiStateCommand case through multiStateNameOf`` () =
    for cmd in HotkeyRegistry.multiStateAllCommands do
        let name = HotkeyRegistry.multiStateNameOf cmd
        Assert.False(System.String.IsNullOrEmpty(name),
            sprintf "MultiStateCommand has empty multiStateNameOf result: %A" cmd)

[<Fact>]
let ``multiStateAllCommands contains exactly the documented commands (Cycle 27)`` () =
    // ADR-discipline pin mirroring `HotkeyRegistryTests`'s
    // `allCommands contains exactly the documented commands`.
    // Adding a new multi-state command requires touching (a)
    // the MultiStateCommand DU, (b) `multiStateNameOf`, (c)
    // `multiStateBuiltIns`, (d) this assertion. The discipline
    // forces a reviewer to acknowledge each addition.
    let expected =
        Set.ofList
            [ // Cycle 27 — migrated from MuteEarcons toggle.
              HotkeyRegistry.EarconsMode
              // Cycle 27 — migrated from ToggleDebugLog toggle.
              HotkeyRegistry.LoggingLevel ]
    let actual = Set.ofList HotkeyRegistry.multiStateAllCommands
    Assert.Equal<Set<HotkeyRegistry.MultiStateCommand>>(expected, actual)

// ---------------------------------------------------------------------
// multiStateBuiltIns has exactly one entry per MultiStateCommand
// ---------------------------------------------------------------------

[<Fact>]
let ``every MultiStateCommand case has a multiStateBuiltIns entry`` () =
    // `multiStateOf` throws KeyNotFoundException if a command
    // is missing from `multiStateBuiltIns`. Walk every command
    // and assert success.
    for cmd in HotkeyRegistry.multiStateAllCommands do
        let def = HotkeyRegistry.multiStateOf cmd
        Assert.Equal(cmd, def.Command)
        Assert.False(System.String.IsNullOrEmpty(def.DisplayName),
            sprintf "MultiStateDef for %A has empty DisplayName" cmd)
        Assert.False(System.String.IsNullOrEmpty(def.Description),
            sprintf "MultiStateDef for %A has empty Description" cmd)

[<Fact>]
let ``multiStateBuiltIns has no duplicate MultiStateCommand entries`` () =
    let commands =
        HotkeyRegistry.multiStateBuiltIns |> List.map (fun d -> d.Command)
    let unique = Set.ofList commands
    Assert.Equal(commands.Length, unique.Count)

[<Fact>]
let ``multiStateBuiltIns count matches multiStateAllCommands count`` () =
    Assert.Equal(
        HotkeyRegistry.multiStateAllCommands.Length,
        HotkeyRegistry.multiStateBuiltIns.Length)

// ---------------------------------------------------------------------
// Each MultiStateDef has at least 2 distinct options
// ---------------------------------------------------------------------

[<Fact>]
let ``every MultiStateDef has at least 2 options`` () =
    // A one-option multi-state command is degenerate (the user
    // can't switch to anything). Pin minimum option count at 2.
    for def in HotkeyRegistry.multiStateBuiltIns do
        Assert.True(
            def.Options.Length >= 2,
            sprintf
                "MultiStateDef for %A has fewer than 2 options (got %d)"
                def.Command def.Options.Length)

[<Fact>]
let ``every MultiStateDef has unique OptionIds`` () =
    // Duplicate OptionIds would make `bindMultiState`'s
    // per-option Dictionary.[id] silently overwrite the
    // earlier RoutedCommand. Pin uniqueness within each def.
    for def in HotkeyRegistry.multiStateBuiltIns do
        let ids = def.Options |> List.map (fun o -> o.OptionId)
        let unique = Set.ofList ids
        Assert.Equal(ids.Length, unique.Count)

[<Fact>]
let ``every MultiStateOption has a non-empty OptionId and DisplayName`` () =
    // OptionId becomes part of the XAML field name + the
    // RoutedCommand name; an empty string would silently break
    // the reflection-driven menu-wiring loop. DisplayName is
    // user-facing.
    for def in HotkeyRegistry.multiStateBuiltIns do
        for opt in def.Options do
            Assert.False(
                System.String.IsNullOrEmpty(opt.OptionId),
                sprintf "MultiStateOption in %A has empty OptionId" def.Command)
            Assert.False(
                System.String.IsNullOrEmpty(opt.DisplayName),
                sprintf
                    "MultiStateOption %s in %A has empty DisplayName"
                    opt.OptionId def.Command)

// ---------------------------------------------------------------------
// Documented OptionId pinning per migrating command
// ---------------------------------------------------------------------

[<Fact>]
let ``EarconsMode options are pinned to enabled, muted (Cycle 27)`` () =
    // The OptionId strings are stable across all surfaces
    // (XAML field naming, RoutedCommand naming, log lines, and
    // future TOML keys). Renaming requires a coordinated edit
    // across HotkeyRegistry + MainWindow.xaml + Program.fs;
    // this fixture pins the documented values so silent drift
    // surfaces at test time.
    let def = HotkeyRegistry.multiStateOf HotkeyRegistry.EarconsMode
    let ids = def.Options |> List.map (fun o -> o.OptionId)
    Assert.Equal<string list>([ "enabled"; "muted" ], ids)

[<Fact>]
let ``LoggingLevel options are pinned to information, debug (Cycle 27)`` () =
    let def = HotkeyRegistry.multiStateOf HotkeyRegistry.LoggingLevel
    let ids = def.Options |> List.map (fun o -> o.OptionId)
    Assert.Equal<string list>([ "information"; "debug" ], ids)

[<Fact>]
let ``EarconsMode multiStateNameOf returns "EarconsMode"`` () =
    Assert.Equal("EarconsMode", HotkeyRegistry.multiStateNameOf HotkeyRegistry.EarconsMode)

[<Fact>]
let ``LoggingLevel multiStateNameOf returns "LoggingLevel"`` () =
    Assert.Equal("LoggingLevel", HotkeyRegistry.multiStateNameOf HotkeyRegistry.LoggingLevel)
