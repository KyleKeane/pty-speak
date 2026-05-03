module PtySpeak.Tests.Unit.ShellSwitchTests

open Xunit
open Terminal.Pty

// ---------------------------------------------------------------------
// Stage 7 PR-C — shell hot-switch deterministic-coverage fixtures
// ---------------------------------------------------------------------
//
// The actual hot-switch coordinator lives in
// `src/Terminal.App/Program.fs compose ()` and exercises the WPF
// dispatcher + ConPtyHost lifecycle, neither of which is reachable
// from the unit-test environment (no WPF dispatcher; ConPTY is
// Windows-only and blocking). These tests pin the deterministic
// pieces the coordinator relies on:
//
//   1. The `ShellId` discriminated union shape — exhaustive
//      pattern-match coverage for the three built-ins (Cmd,
//      Claude, PowerShell). A future addition (WSL, Python REPL)
//      lights up the `_` arm and forces the contributor to update
//      both the registry and the `Program.fs
//      setupShellSwitchKeybindings` keybinding table.
//   2. Hotkey-to-shell mapping — pinning that `Ctrl+Shift+1` maps
//      to `Cmd`, `Ctrl+Shift+2` to `PowerShell`, and `Ctrl+Shift+3`
//      to `Claude` (the order PR-J established for putting the
//      diagnostic control shell next to cmd). The mapping lives
//      in Program.fs as three `RoutedCommand` + `KeyBinding`
//      pairs; this fixture documents the contract via a parallel
//      table that the orchestrator's review must match.
//   3. Synthetic-resolver behaviour — confirming that a registry
//      whose `Resolve` returns `Error` does not produce a
//      crash-inducing exception path. The orchestrator's "log
//      warning + announce failure" branch depends on this Result
//      shape staying intact.
//
// What's NOT covered here (by design):
//   * Actual ConPtyHost teardown + respawn (Windows-only,
//     non-deterministic; covered by PR-D's NVDA matrix row).
//   * Announcement timing / 700ms delay (WPF dispatcher dependent;
//     covered manually in the matrix row).
//   * Cross-shell parser/screen residue (Stage 4a substrate
//     concern; covered by PR-D inventory entries if the visual
//     mix is too awkward in practice).

// ---------------------------------------------------------------------
// 1. ShellId exhaustive pattern coverage
// ---------------------------------------------------------------------

[<Fact>]
let ``ShellId pattern match is exhaustive over Cmd, Claude, PowerShell`` () =
    // If a future contributor adds a new ShellId case (e.g. Wsl,
    // Python), this match becomes non-exhaustive and the compiler
    // emits FS0025. Under TreatWarningsAsErrors=true, that fails
    // the build — forcing the contributor to update both
    // `ShellRegistry.builtIns` (the data) AND this fixture (the
    // test contract) AND `Program.fs setupShellSwitchKeybindings`
    // (the UX) in lockstep.
    let label (id: ShellRegistry.ShellId) : string =
        match id with
        | ShellRegistry.Cmd -> "cmd"
        | ShellRegistry.Claude -> "claude"
        | ShellRegistry.PowerShell -> "powershell"
    Assert.Equal("cmd", label ShellRegistry.Cmd)
    Assert.Equal("claude", label ShellRegistry.Claude)
    Assert.Equal("powershell", label ShellRegistry.PowerShell)

// ---------------------------------------------------------------------
// 2. Hotkey-to-shell mapping contract
// ---------------------------------------------------------------------
//
// Pins the map that `Program.fs setupShellSwitchKeybindings`
// implements imperatively. The keys are documented as
// `Key.D1` / `Key.D2` / `Key.D3` (number-row digits, NOT numpad)
// per the AppReservedHotkeys table in `src/Views/TerminalView.cs`.
// PR-J reordered: cmd stays at +1; PowerShell takes +2 (the
// diagnostic control shell sits next to cmd); claude moves to +3.

let private hotkeyMapping : (string * ShellRegistry.ShellId) list =
    [ "Ctrl+Shift+1", ShellRegistry.Cmd
      "Ctrl+Shift+2", ShellRegistry.PowerShell
      "Ctrl+Shift+3", ShellRegistry.Claude ]

[<Fact>]
let ``Ctrl+Shift+1 maps to Cmd shell`` () =
    let target =
        hotkeyMapping
        |> List.tryFind (fun (g, _) -> g = "Ctrl+Shift+1")
        |> Option.map snd
    Assert.Equal(Some ShellRegistry.Cmd, target)

[<Fact>]
let ``Ctrl+Shift+2 maps to PowerShell shell`` () =
    let target =
        hotkeyMapping
        |> List.tryFind (fun (g, _) -> g = "Ctrl+Shift+2")
        |> Option.map snd
    Assert.Equal(Some ShellRegistry.PowerShell, target)

[<Fact>]
let ``Ctrl+Shift+3 maps to Claude shell`` () =
    let target =
        hotkeyMapping
        |> List.tryFind (fun (g, _) -> g = "Ctrl+Shift+3")
        |> Option.map snd
    Assert.Equal(Some ShellRegistry.Claude, target)

[<Fact>]
let ``hotkey mapping covers exactly the registered shells`` () =
    // Pinning the mapping's keyset against `ShellRegistry.builtIns`
    // guards against the failure mode where a shell is added to
    // the registry but its hotkey is forgotten — which would
    // mean the user can't reach it via Ctrl+Shift+digit and the
    // only entry point would be `PTYSPEAK_SHELL` at startup.
    let registeredShells =
        ShellRegistry.builtIns
        |> Map.toSeq
        |> Seq.map fst
        |> Set.ofSeq
    let hotkeyShells =
        hotkeyMapping
        |> List.map snd
        |> Set.ofList
    Assert.Equal<Set<ShellRegistry.ShellId>>(registeredShells, hotkeyShells)

// ---------------------------------------------------------------------
// 3. Resolver-failure path stays Result-shaped (no exception leak)
// ---------------------------------------------------------------------

[<Fact>]
let ``synthetic resolver returning Error does not throw`` () =
    // The coordinator's "resolve failure" branch
    // (`match shell.Resolve() with | Error reason -> ...`)
    // assumes the resolver returns Result, never throws. Tests
    // construct synthetic registries via tryFindIn; this fixture
    // pins that the contract is honoured.
    let claudeShell : ShellRegistry.Shell =
        { Id = ShellRegistry.Claude
          DisplayName = "synthetic Claude"
          Resolve = fun () -> Error "synthetic-failure" }
    let synthetic = Map.ofList [ ShellRegistry.Claude, claudeShell ]
    let shell = (ShellRegistry.tryFindIn synthetic ShellRegistry.Claude).Value
    // The Resolve invocation must complete without throwing —
    // the Result wraps the failure inline.
    let result = shell.Resolve()
    match result with
    | Error msg -> Assert.Equal("synthetic-failure", msg)
    | Ok _ -> Assert.Fail("Expected Error; got Ok")

[<Fact>]
let ``synthetic resolver returning Ok produces the command line`` () =
    // The coordinator's success branch:
    //   match shell.Resolve() with | Ok cmdLine -> spawn newCfg
    // depends on Ok carrying a non-empty command line string
    // (the value passed to ConPtyHost.start as
    // PtyConfig.CommandLine).
    let cmdShell : ShellRegistry.Shell =
        { Id = ShellRegistry.Cmd
          DisplayName = "synthetic cmd"
          Resolve = fun () -> Ok "fake-cmd.exe /K echo hi" }
    let synthetic = Map.ofList [ ShellRegistry.Cmd, cmdShell ]
    let shell = (ShellRegistry.tryFindIn synthetic ShellRegistry.Cmd).Value
    match shell.Resolve() with
    | Ok cmdLine ->
        Assert.Equal("fake-cmd.exe /K echo hi", cmdLine)
        Assert.True(cmdLine.Length > 0)
    | Error _ -> Assert.Fail("Expected Ok; got Error")

// ---------------------------------------------------------------------
// 4. Display-name pinning for announcements
// ---------------------------------------------------------------------
//
// The coordinator's announce strings interpolate
// `shell.DisplayName` ("Switching to {DisplayName}." /
// "Switched to {DisplayName}."). NVDA reads these aloud, so the
// names must be human-natural (not internal identifiers like
// "cmd_v2"). This fixture pins the announce-bound name shape.

[<Fact>]
let ``Cmd DisplayName reads naturally as Command Prompt`` () =
    let cmd = ShellRegistry.builtIns |> Map.find ShellRegistry.Cmd
    Assert.Equal("Command Prompt", cmd.DisplayName)

[<Fact>]
let ``Claude DisplayName reads naturally as Claude Code`` () =
    let claude = ShellRegistry.builtIns |> Map.find ShellRegistry.Claude
    Assert.Equal("Claude Code", claude.DisplayName)

[<Fact>]
let ``PowerShell DisplayName reads naturally as PowerShell`` () =
    // Pinned because future "PowerShell Core" / "Windows
    // PowerShell" naming churn could break NVDA's announce
    // pronunciation; the bare "PowerShell" reads cleanly under
    // the default eSpeak voice.
    let ps = ShellRegistry.builtIns |> Map.find ShellRegistry.PowerShell
    Assert.Equal("PowerShell", ps.DisplayName)
