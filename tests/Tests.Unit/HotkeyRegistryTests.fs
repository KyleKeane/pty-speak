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
              // Cycle 25a — OpenLogsFolder removed; OpenDataFolder
              // and OpenConfig take its slot.
              HotkeyRegistry.OpenDataFolder
              HotkeyRegistry.OpenConfig
              // Cycle 25b-1a — CopyLatestLog removed (D's bundle
              // subsumes it).
              // Cycle 27 — `ToggleDebugLog` and `MuteEarcons`
              // migrated to `MultiStateCommand` (`LoggingLevel`
              // and `EarconsMode`); see
              // `MultiStateRegistryTests`. Both keyboard
              // accelerators (Ctrl+Shift+G, Ctrl+Shift+M)
              // dropped — the operations are menu-only by canon.
              HotkeyRegistry.HealthCheck
              HotkeyRegistry.IncidentMarker
              HotkeyRegistry.SwitchToCmd
              HotkeyRegistry.SwitchToPowerShell
              HotkeyRegistry.SwitchToClaude
              // Cycle 22b — copy SessionModel history to clipboard.
              HotkeyRegistry.CopyHistoryToClipboard
              // Cycle 24e — announce session-log file path.
              HotkeyRegistry.AnnounceSessionLogPath
              // Cycle 46 post-audit — open last command output
              // in default text editor (Ctrl+Shift+O).
              HotkeyRegistry.OpenLastOutput
              // Cycle 46 post-audit — re-narrate last command
              // output capped at 800 chars (Ctrl+Shift+A).
              HotkeyRegistry.AnnounceLastOutput
              // ADR 0007 Phase 2a — copy the focused cell
              // (command + output) to clipboard (Ctrl+Shift+C).
              HotkeyRegistry.CopyFocusedCell
              // ADR 0007 Phase 2b — copy one side of the
              // focused cell (menu-only).
              HotkeyRegistry.CopyFocusedCellCommand
              HotkeyRegistry.CopyFocusedCellOutput
              // ADR 0007 Phase 2c — jump to last failed cell
              // (menu-only).
              HotkeyRegistry.JumpToLastError
              // ADR 0007 Phase 3 — rerun focused input
              // (menu-only).
              HotkeyRegistry.RerunFocusedInput
              // Cycle 47 — CMD interaction test corpus (eight
              // menu-only items under Diagnostics → CMD
              // Interaction Tests; each inserts a quoted
              // script invocation into the PTY input cursor).
              HotkeyRegistry.CmdTestEcho
              HotkeyRegistry.CmdTestTextInput
              HotkeyRegistry.CmdTestNumericInput
              HotkeyRegistry.CmdTestYesNo
              HotkeyRegistry.CmdTestMultiChoice
              HotkeyRegistry.CmdTestPause
              HotkeyRegistry.CmdTestProgress
              HotkeyRegistry.CmdTestStderr
              HotkeyRegistry.CmdTestMultiInterrupt
              // Cycle 26c — first menu-only command. Surfaced
              // via Diagnostics → Test Process Cleanup; no
              // keyboard accelerator.
              HotkeyRegistry.RunProcessCleanupScript
              // Cycle 28 — Window menu commands (menu-only).
              HotkeyRegistry.CloseWindow
              HotkeyRegistry.ExitApp
              // Cycle 38a-followup — second menu-only command;
              // Diagnostics → Open Manual Tests.
              HotkeyRegistry.OpenManualTests
              // Cycle 43a — diagnostic chunk extractors. All
              // menu-only (no keyboard accelerators). Two top-level
              // items under Diagnostics + 4 proof-of-concept
              // extractors under Diagnostics → Extract → {Recency
              // | Event Type | Bundle Section | Snapshot}.
              HotkeyRegistry.CopyLatestBundle
              HotkeyRegistry.GrepDiagnostics
              HotkeyRegistry.ExtractLast50LogLines
              HotkeyRegistry.ExtractErrorsAndWarnings
              HotkeyRegistry.ExtractActiveConfig
              HotkeyRegistry.ExtractVersionHeader
              // Cycle 47 follow-up — test-bracketed extractors.
              // One per CMD test in `scripts/cmd-tests/`; surfaced
              // under Diagnostics → Extract → Test Run.
              HotkeyRegistry.ExtractTestEcho
              HotkeyRegistry.ExtractTestTextInput
              HotkeyRegistry.ExtractTestNumericInput
              HotkeyRegistry.ExtractTestYesNo
              HotkeyRegistry.ExtractTestMultiChoice
              HotkeyRegistry.ExtractTestPause
              HotkeyRegistry.ExtractTestProgress
              HotkeyRegistry.ExtractTestStderr
              // Cycle 45 Commit 2 — SpeechCursor navigation
              // (menu-only).
              HotkeyRegistry.SpeechCursorNext
              HotkeyRegistry.SpeechCursorPrevious
              HotkeyRegistry.SpeechCursorJumpToLatest
              HotkeyRegistry.SpeechCursorToggleMode
              // ADR 0007 Phase 6a-2b — pane switch.
              HotkeyRegistry.FocusHistoryPane
              HotkeyRegistry.FocusTerminalPane ]
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
    //
    // Cycle 26b — menu-only commands (`Key = None`,
    // `Modifiers = None`) are excluded from collision detection
    // by definition: they have no gesture to collide with. Only
    // gesture-bearing entries participate.
    let gestures =
        HotkeyRegistry.builtIns
        |> List.choose (fun h ->
            match h.Key, h.Modifiers with
            | Some k, Some m -> Some (k, m)
            | _ -> None)
    let unique = Set.ofList gestures
    Assert.Equal(gestures.Length, unique.Count)

// ---------------------------------------------------------------------
// tryFind round-trip
// ---------------------------------------------------------------------

[<Fact>]
let ``tryFind returns the registered command for each builtIns gesture`` () =
    // For every gesture-bearing entry in `builtIns`, `tryFind`
    // with that entry's (key, modifiers) should yield the same
    // command back. Closes the round-trip from the user-settings
    // UI's perspective: select gesture → recover command.
    //
    // Cycle 26b — menu-only commands (`Key = None`) are
    // skipped: they have no gesture to round-trip.
    for hk in HotkeyRegistry.builtIns do
        match hk.Key, hk.Modifiers with
        | Some k, Some m ->
            let found = HotkeyRegistry.tryFind k m
            Assert.Equal(Some hk.Command, found)
        | _ -> ()

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
    Assert.Equal(Some (HotkeyRegistry.Letter 'U'), hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(
        Some (Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]),
        hk.Modifiers)

[<Fact>]
let ``Ctrl+Shift+G is unbound (Cycle 27 migrated ToggleDebugLog to LoggingLevel multi-state)`` () =
    // Cycle 27 — `ToggleDebugLog` migrated from `AppCommand` to
    // `MultiStateCommand.LoggingLevel`. The keyboard
    // accelerator was dropped per the maintainer's
    // hotkey-count working-memory ceiling; the operation now
    // surfaces only via View → Logging Level. The G gesture
    // must flow through to the shell as plain text.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'G') modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``Ctrl+Shift+M is unbound (Cycle 27 migrated MuteEarcons to EarconsMode multi-state)`` () =
    // Cycle 27 — `MuteEarcons` migrated from `AppCommand` to
    // `MultiStateCommand.EarconsMode`. Same rationale as
    // `Ctrl+Shift+G`: dropped accelerator + menu-only via
    // View → Earcons.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'M') modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``Ctrl+Shift+L is unbound (Cycle 25b-1a removed CopyLatestLog)`` () =
    // Cycle 25b-1a — CopyLatestLog removed entirely. The
    // Ctrl+Shift+D diagnostic battery's bundle now includes the
    // active FileLogger log slice, making a dedicated "copy just
    // the log" hotkey redundant. The L gesture must now flow
    // through to the shell as plain text.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'L') modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``OpenDataFolder is bound to Ctrl+Shift+P (Cycle 25a)`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.OpenDataFolder
    Assert.Equal(Some (HotkeyRegistry.Letter 'P'), hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(
        Some (Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]),
        hk.Modifiers)

[<Fact>]
let ``OpenConfig is bound to Ctrl+Shift+E (Cycle 25a)`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.OpenConfig
    Assert.Equal(Some (HotkeyRegistry.Letter 'E'), hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(
        Some (Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]),
        hk.Modifiers)

[<Fact>]
let ``Ctrl+Shift+T is unbound (Cycle 25b removed RunTestMatrix placeholder)`` () =
    // Cycle 25b — the RunTestMatrix placeholder shipped in 25a is
    // removed. The diagnostic suite folds into Ctrl+Shift+D rather
    // than splitting across two hotkeys; the interactive cleanup
    // test (which required user input) moves to a future app menu.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind (HotkeyRegistry.Letter 'T') modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``Ctrl+Shift+; is unbound (Cycle 25a vacated)`` () =
    // Cycle 25a — Ctrl+Shift+; was CopyLatestLog; the binding
    // moved to Ctrl+Shift+L. The semicolon gesture must now
    // return None from tryFind.
    let modifiers = Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]
    let result = HotkeyRegistry.tryFind HotkeyRegistry.Semicolon modifiers
    Assert.Equal(None, result)

[<Fact>]
let ``shell-switch hotkeys are bound to Ctrl+Shift+ digits 1, 2, 3 in PR-J order`` () =
    // PR-J reordered: cmd=1, PowerShell=2, Claude=3 (PowerShell
    // sits next to cmd as the diagnostic control shell).
    let cmd = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToCmd
    Assert.Equal(Some (HotkeyRegistry.Digit 1), cmd.Key)
    let ps = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToPowerShell
    Assert.Equal(Some (HotkeyRegistry.Digit 2), ps.Key)
    let claude = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToClaude
    Assert.Equal(Some (HotkeyRegistry.Digit 3), claude.Key)

[<Fact>]
let ``AnnounceSessionLogPath is bound to Ctrl+Shift+S (Cycle 24e)`` () =
    // Cycle 24e — diagnostic hotkey announces the active
    // session-log file path. Mnemonic: S for Session log.
    let hk =
        HotkeyRegistry.hotkeyOf HotkeyRegistry.AnnounceSessionLogPath
    Assert.Equal(Some (HotkeyRegistry.Letter 'S'), hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(
        Some (Set.ofList [ HotkeyRegistry.Ctrl; HotkeyRegistry.Shift ]),
        hk.Modifiers)
    Assert.Equal(
        "AnnounceSessionLogPath",
        HotkeyRegistry.nameOf HotkeyRegistry.AnnounceSessionLogPath)

// ---------------------------------------------------------------------
// Cycle 26b — option-typing pinning + gestureText helper
// ---------------------------------------------------------------------

[<Fact>]
let ``every existing AppCommand from Cycle 25b still has Some Key and Some Modifiers`` () =
    // Cycle 26b option-typed Hotkey.Key + Hotkey.Modifiers to
    // accommodate menu-only commands. Regression guard: every
    // command that shipped with a default hotkey before Cycle 26b
    // must still have one. Silently demoting an existing hotkey
    // to menu-only would be a UX regression.
    // Cycle 27 — `ToggleDebugLog` and `MuteEarcons` removed
    // from `priorCommands` because they migrated to
    // `MultiStateCommand` and shed their default keyboard
    // accelerators in the same change. The remaining priors
    // must still have `Some Key` / `Some Modifiers`.
    let priorCommands =
        [ HotkeyRegistry.CheckForUpdates
          HotkeyRegistry.RunDiagnostic
          HotkeyRegistry.DraftNewRelease
          HotkeyRegistry.OpenDataFolder
          HotkeyRegistry.OpenConfig
          HotkeyRegistry.HealthCheck
          HotkeyRegistry.IncidentMarker
          HotkeyRegistry.SwitchToCmd
          HotkeyRegistry.SwitchToPowerShell
          HotkeyRegistry.SwitchToClaude
          HotkeyRegistry.CopyHistoryToClipboard
          HotkeyRegistry.AnnounceSessionLogPath ]
    for cmd in priorCommands do
        let hk = HotkeyRegistry.hotkeyOf cmd
        Assert.True(
            Option.isSome hk.Key,
            sprintf "AppCommand %A unexpectedly lost its default Key" cmd)
        Assert.True(
            Option.isSome hk.Modifiers,
            sprintf "AppCommand %A unexpectedly lost its default Modifiers" cmd)

[<Fact>]
let ``gestureText formats Ctrl+Shift+letter gestures as expected`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.CheckForUpdates
    Assert.Equal(Some "Ctrl+Shift+U", HotkeyRegistry.gestureText hk)

[<Fact>]
let ``gestureText formats Ctrl+Shift+digit gestures as expected`` () =
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.SwitchToCmd
    Assert.Equal(Some "Ctrl+Shift+1", HotkeyRegistry.gestureText hk)

[<Fact>]
let ``gestureText returns None for menu-only commands`` () =
    // Synthesise a menu-only Hotkey shape (no production
    // command is menu-only as of Cycle 26b; Cycle 26c adds
    // RunProcessCleanupScript). The format helper must
    // return None for any (None, None) pair.
    //
    // Type annotation on the binding is required because
    // `HotkeyRegistry` is a module inside `Terminal.Core`
    // (not auto-opened), so a bare record literal can't
    // infer `Hotkey` from field labels alone — see
    // CLAUDE.md "Record literal type inference fails when
    // the record module is not auto-opened" (FS0039 gotcha).
    let menuOnly : HotkeyRegistry.Hotkey =
        { Command = HotkeyRegistry.CheckForUpdates  // command identity is irrelevant here
          Key = None
          Modifiers = None
          Description = "test fixture" }
    Assert.Equal(None, HotkeyRegistry.gestureText menuOnly)

[<Fact>]
let ``RunProcessCleanupScript is menu-only (Cycle 26c — None Key, None Modifiers)`` () =
    // Cycle 26c — first menu-only AppCommand. Has no default
    // keyboard accelerator; surfaced only via the Diagnostics →
    // Test Process Cleanup menu item. Pin the (None, None)
    // shape so a future PR doesn't accidentally promote it to
    // gesture-bearing without an explicit decision.
    let hk =
        HotkeyRegistry.hotkeyOf HotkeyRegistry.RunProcessCleanupScript
    Assert.Equal(None, hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(None, hk.Modifiers)
    Assert.Equal(
        "RunProcessCleanupScript",
        HotkeyRegistry.nameOf HotkeyRegistry.RunProcessCleanupScript)
    // gestureText must return None for menu-only commands so
    // MenuItem.InputGestureText is left blank in XAML.
    Assert.Equal(None, HotkeyRegistry.gestureText hk)

[<Fact>]
let ``CloseWindow is menu-only (Cycle 28 — None Key, None Modifiers)`` () =
    // Cycle 28 — Window menu Close Window. Menu-only by
    // design; the Alt+F4 OS gesture is handled by WPF Window
    // natively, not via an AppReservedHotkeys entry. The XAML
    // hardcodes `InputGestureText="Alt+F4"` for visual display
    // since `gestureText` returns None for menu-only commands.
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.CloseWindow
    Assert.Equal(None, hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(None, hk.Modifiers)
    Assert.Equal("CloseWindow", HotkeyRegistry.nameOf HotkeyRegistry.CloseWindow)
    Assert.Equal(None, HotkeyRegistry.gestureText hk)

[<Fact>]
let ``ExitApp is menu-only (Cycle 28 — None Key, None Modifiers)`` () =
    // Cycle 28 — Window menu Exit. Menu-only; calls
    // `Application.Current.Shutdown()`.
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.ExitApp
    Assert.Equal(None, hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(None, hk.Modifiers)
    Assert.Equal("ExitApp", HotkeyRegistry.nameOf HotkeyRegistry.ExitApp)
    Assert.Equal(None, HotkeyRegistry.gestureText hk)

[<Fact>]
let ``OpenManualTests is menu-only (Cycle 38a-followup — None Key, None Modifiers)`` () =
    // Cycle 38a-followup — second menu-only AppCommand after
    // RunProcessCleanupScript. Surfaced via Diagnostics → Open
    // Manual Tests. Mirrors the (None, None) shape pin so a
    // future PR doesn't accidentally promote it to a gesture-
    // bearing command without an explicit decision.
    let hk = HotkeyRegistry.hotkeyOf HotkeyRegistry.OpenManualTests
    Assert.Equal(None, hk.Key)
    Assert.Equal<Set<HotkeyRegistry.Modifier> option>(None, hk.Modifiers)
    Assert.Equal("OpenManualTests", HotkeyRegistry.nameOf HotkeyRegistry.OpenManualTests)
    Assert.Equal(None, HotkeyRegistry.gestureText hk)

[<Fact>]
let ``gestureText modifier order is Ctrl+Alt+Shift regardless of Set enumeration`` () =
    // Pin the stable rendering order so menu-displayed gestures
    // and CHANGELOG-cited gestures stay textually identical.
    let hk : HotkeyRegistry.Hotkey =
        { Command = HotkeyRegistry.CheckForUpdates
          Key = Some (HotkeyRegistry.Letter 'X')
          Modifiers = Some (Set.ofList [ HotkeyRegistry.Shift; HotkeyRegistry.Alt; HotkeyRegistry.Ctrl ])
          Description = "test fixture" }
    Assert.Equal(Some "Ctrl+Alt+Shift+X", HotkeyRegistry.gestureText hk)
