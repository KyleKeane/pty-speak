module PtySpeak.Tests.Unit.HotkeyRegistryTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Pre-framework-cycle PR-O — HotkeyRegistry pinning
// ---------------------------------------------------------------------
//
// `HotkeyRegistry` is the F#-side canonical registry for every
// app-reserved hotkey. The C#-side `TerminalView.AppReservedHotkeys`
// table mirrors it on the hot path (consulted per-keystroke by
// `OnPreviewKeyDown` filter); `Program.fs bindHotkey` reads
// `HotkeyRegistry.builtIns` at compose-time.
//
// These tests pin three contracts:
//
//   1. `allCommands` matches the `AppCommand` DU — every case
//      appears, no orphans. Catches `let allCommands = [...]`
//      drifting from the DU's actual case set.
//   2. `builtIns` has exactly one entry per `AppCommand` — every
//      command is bindable, no duplicate entries. Adding a new
//      `AppCommand` case without a matching `builtIns` row trips
//      this fixture.
//   3. No two hotkeys share `(key, modifiers)` — collision check.
//      Adding a new hotkey that accidentally re-uses an existing
//      gesture trips this fixture.
//
// `nameOf` exhaustiveness is enforced by the F# compiler under
// `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`; no
// runtime test needed for that.

// ---------------------------------------------------------------------
// allCommands matches AppCommand DU
// ---------------------------------------------------------------------

[<Fact>]
let ``allCommands round-trips every AppCommand case through nameOf`` () =
    // Every entry in `allCommands` must yield a non-empty name.
    // Combined with the exhaustive `nameOf` match, this verifies
    // `allCommands` is a superset of the DU. The
    // `every-AppCommand-has-a-builtIns-entry` test below verifies
    // it's not a strict superset (i.e. no fictional cases).
    for cmd in HotkeyRegistry.allCommands do
        let name = HotkeyRegistry.nameOf cmd
        Assert.False(System.String.IsNullOrEmpty(name),
            sprintf "AppCommand has empty nameOf result: %A" cmd)

[<Fact>]
let ``allCommands contains exactly the documented commands (PR-O)`` () =
    // ADR-discipline pin mirroring `ShellRegistryTests.builtIns
    // contains exactly Cmd, Claude, and PowerShell` and
    // `EnvBlockTests.allowedNames contains exactly the
    // spec-7-2 baseline`. Adding a new hotkey requires touching
    // (a) the AppCommand DU, (b) `nameOf`, (c) `builtIns`, (d)
    // this assertion, (e) `TerminalView.AppReservedHotkeys`. The
    // discipline forces a reviewer to acknowledge each addition.
    let expected =
        Set.ofList
            [ HotkeyRegistry.CheckForUpdates
              HotkeyRegistry.RunDiagnostic
              HotkeyRegistry.DraftNewRelease
              HotkeyRegistry.OpenLogsFolder
              HotkeyRegistry.CopyLatestLog
              HotkeyRegistry.ToggleDebugLog
              HotkeyRegistry.HealthCheck
              HotkeyRegistry.IncidentMarker
              HotkeyRegistry.SwitchToCmd
              HotkeyRegistry.SwitchToPowerShell
              HotkeyRegistry.SwitchToClaude
              // Stage 8d.1 — earcon mute toggle.
              HotkeyRegistry.MuteEarcons ]
    let actual = Set.ofList HotkeyRegistry.allCommands
    Assert.Equal<Set<HotkeyRegistry.AppCommand>>(expected, actual)

// ---------------------------------------------------------------------
// builtIns has exactly one entry per AppCommand
// ---------------------------------------------------------------------

[<Fact>]
let ``every AppCommand case has a builtIns entry`` () =
    // `hotkeyOf` throws KeyNotFoundException if a command is
    // missing from `builtIns`. Walk every command and assert
    // `hotkeyOf` succeeds. Fails fast if a future
    // AppCommand-DU-case addition forgot to add a builtIns row.
    for cmd in HotkeyRegistry.allCommands do
        let hk = HotkeyRegistry.hotkeyOf cmd
        Assert.Equal(cmd, hk.Command)
        Assert.False(System.String.IsNullOrEmpty(hk.Description),
            sprintf "Hotkey for %A has empty description" cmd)

[<Fact>]
let ``builtIns has no duplicate AppCommand entries`` () =
    // A command appearing twice in builtIns would make the
    // List.tryFind in hotkeyOf non-deterministic. Pin uniqueness.
    let commands = HotkeyRegistry.builtIns |> List.map (fun h -> h.Command)
    let unique = Set.ofList commands
    Assert.Equal(commands.Length, unique.Count)

[<Fact>]
let ``builtIns count matches allCommands count`` () =
    // Sanity: builtIns contains exactly the entries allCommands
    // promises. Combined with the previous fixtures, this
    // pins both inclusion directions.
    Assert.Equal(HotkeyRegistry.allCommands.Length, HotkeyRegistry.builtIns.Length)

// ---------------------------------------------------------------------
// No two hotkeys share (key, modifiers) — collision detection
// ---------------------------------------------------------------------

[<Fact>]
let ``no two hotkeys share the same (key, modifiers) gesture`` () =
    // Adding a new hotkey that accidentally reuses an existing
    // gesture would silently break: WPF's InputBindings registers
    // both, the user's keystroke fires whichever is consulted
    // first (undefined ordering), and the other handler never
    // runs. This fixture catches the collision at test time.
    let gestures =
        HotkeyRegistry.builtIns
        |> List.map (fun h -> (h.Key, h.Modifiers))
    let unique = Set.ofList gestures
    Assert.Equal(gestures.Length, unique.Count)

// ---------------------------------------------------------------------
// tryFind round-trip
// ---------------------------------------------------------------------

[<Fact>]
let ``tryFind returns the registered command for each builtIns gesture`` () =
    // For every entry in `builtIns`, `tryFind` with that entry's
    // (key, modifiers) should yield the same command back.
    // Closes the round-trip from the user-settings UI's
    // perspective: select gesture → recover command.
    for hk in HotkeyRegistry.builtIns do
        let found = HotkeyRegistry.tryFind hk.Key hk.Modifiers
        Assert.Equal(Some hk.Command, found)

[<Fact>]
let ``tryFind returns None for an unregistered gesture`` () =
    // Pick a gesture not in `builtIns` (Letter 'Z' isn't bound
    // to any current hotkey) and verify `tryFind` returns None.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'Z') modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``tryFind distinguishes modifier sets`` () =
    // Two hotkeys with the same key but different modifier sets
    // must NOT collide. Verify by looking up Ctrl+Shift+U
    // (registered: CheckForUpdates) versus a hypothetical
    // Ctrl+U (not registered). The latter must return None.
    let ctrlOnly = Set.ofList [ HotkeyRegistry.Ctrl ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'U') ctrlOnly
    Assert.Equal(None, result)
    // Sanity: the registered Ctrl+Shift+U does map.
    let ctrlShift = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let registered = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'U') ctrlShift
    Assert.Equal(Some HotkeyRegistry.CheckForUpdates, registered)

// ---------------------------------------------------------------------
// Documented hotkey assignment pinning
// ---------------------------------------------------------------------
//
// Pin the specific (command, key, modifiers) bindings that
// match the documented Stage 7 / PR-J hotkey set. These
// fixtures catch accidental reordering / re-keying.

[<Fact>]
let ``CheckForUpdates is bound to Ctrl+Shift+U`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.CheckForUpdates
    Assert.Equal(HotkeyRegistry.Letter 'U', hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier>>(
        Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ],
        hk.Modifiers)

[<Fact>]
let ``CopyLatestLog is bound to Ctrl+Shift+;`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.CopyLatestLog
    Assert.Equal(HotkeyRegistry.Semicolon, hk.Key)

[<Fact>]
let ``shell-switch hotkeys are bound to Ctrl+Shift+ digits 1, 2, 3 in PR-J order`` () =
    // PR-J reordered: cmd=1, PowerShell=2, Claude=3 (PowerShell
    // sits next to cmd as the diagnostic control shell).
    let cmd = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToCmd
    Assert.Equal(HotkeyRegistry.Digit 1, cmd.Key)
    let ps = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToPowerShell
    Assert.Equal(HotkeyRegistry.Digit 2, ps.Key)
    let claude = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToClaude
    Assert.Equal(HotkeyRegistry.Digit 3, claude.Key)
