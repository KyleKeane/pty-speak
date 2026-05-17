namespace Terminal.Core

/// Pre-framework-cycle PR-O — extensible registry of every
/// app-reserved hotkey pty-speak ships. Mirrors the
/// `Terminal.Pty.ShellRegistry` shape: a discriminated-union
/// `AppCommand` identifies each command and a `Hotkey` record
/// captures the default key + modifiers + description. The
/// registry is the **canonical F#-side source of truth** for
/// the app-level hotkey set; `src/Views/TerminalView.cs`'s
/// `AppReservedHotkeys` table is a parallel C#-side mirror
/// kept in sync by maintainer convention (each new hotkey must
/// be added to both surfaces).
///
/// Why two surfaces: `AppReservedHotkeys` is consulted on the
/// hot path (`OnPreviewKeyDown` filter, fired per keystroke);
/// keeping it as a static C# array avoids C#/F# interop cost
/// per key event. `HotkeyRegistry` is consulted at startup
/// (compose-time `bindHotkey` calls) and by tests + future
/// Phase 2 user-settings UI.
///
/// **Why not directly use `System.Windows.Input.Key` /
/// `ModifierKeys`?** Terminal.Core is `<UseWPF>` -free by
/// design — the project boundary keeps WPF dependencies in
/// Terminal.App / Views. Mirrors `KeyEncoding`'s pattern
/// (own `KeyCode` DU; WPF translation lives in
/// `src/Views/TerminalView.cs`'s `TranslateKey`). The
/// translation for HotkeyRegistry lives in
/// `src/Terminal.App/Program.fs bindHotkey` and is the only
/// place WPF types appear at the hotkey seam.
///
/// **Phase 2 evolution.** The `Hotkey` record's `Key` and
/// `Modifiers` fields are the natural override points for a
/// user-settings TOML config (`hotkeys.checkForUpdates =
/// "Ctrl+Alt+U"`); the user-settings substrate looks up an
/// `AppCommand`, finds the default `Hotkey`, and overrides
/// `Key` / `Modifiers` from TOML. The dispatch path
/// (compose-time `bindHotkey`) stays unchanged.
module HotkeyRegistry =

    /// Modifier flags for hotkey gestures. Bit-flag style via
    /// a `Set` so the comparison is order-independent.
    /// `Win` / `Cmd` is intentionally not modeled — Win+letter
    /// is OS-shell territory and pty-speak doesn't claim those.
    type Modifier =
        | Ctrl
        | Shift
        | Alt

    /// Key identifier without WPF dependency. Limited to the
    /// keys today's hotkeys actually use; extending this DU
    /// requires updating `Program.fs translateHotkeyKey` and
    /// the consistency test in `tests/Tests.Unit/HotkeyRegistryTests.fs`.
    type HotkeyKey =
        /// Letter A-Z. Stored as the uppercase character.
        | Letter of char
        /// Number-row digit 1-9 (NOT numpad — numpad-with-NumLock-off
        /// is reserved for NVDA review-cursor commands per
        /// Stage 6 / `CONTRIBUTING.md` non-negotiables).
        | Digit of int
        /// `;` / `:` key (`OemSemicolon` in WPF). Used by
        /// `Ctrl+Shift+;` log-copy hotkey.
        | Semicolon
        /// Arrow / navigation keys. Cycle 48 post-PR-F binds
        /// `Ctrl+Shift+Up/Down/End` to SpeechCursor Previous /
        /// Next / JumpToLatest. NVDA collision check: default
        /// NVDA review-cursor gestures use the Numpad cluster,
        /// not Ctrl+Shift+arrow.
        | Up
        | Down
        | End
        /// ADR 0007 Phase 6a-2b — `Ctrl+Shift+Left`/`Right`
        /// pane switch (cell-history list ⇄ terminal). NVDA
        /// collision check: NVDA's review-cursor defaults are
        /// the Numpad cluster; `Ctrl+Shift+arrow` is free.
        | Left
        | Right

    /// Identity of every app-reserved hotkey command. New
    /// hotkeys append a case here and add a corresponding
    /// `builtIns` row + `AppReservedHotkeys` row in
    /// `src/Views/TerminalView.cs`. The exhaustive-match
    /// `nameOf` function below catches a missing case at
    /// compile time under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
    type AppCommand =
        // Stage 11 — Velopack auto-update.
        | CheckForUpdates
        // Stage 4b — process-cleanup diagnostic launcher.
        | RunDiagnostic
        // PR-#83 / PR-#91 — draft a new release form.
        | DraftNewRelease
        // Cycle 25a — open the pty-speak data folder
        // (`%LOCALAPPDATA%\PtySpeak\`). Replaces OpenLogsFolder
        // because the parent gives one-keystroke access to all of
        // logs / sessions / config rather than just logs.
        | OpenDataFolder
        // Cycle 25a — auto-create config.toml with defaults if
        // missing, then open in default app.
        | OpenConfig
        // Stage 7-followup PR-F + PR-J liveness probe — health check.
        | HealthCheck
        // Stage 7-followup PR-F — incident marker.
        | IncidentMarker
        // Stage 7 PR-C — switch spawned shell to cmd.
        | SwitchToCmd
        // Stage 7-followup PR-J — switch spawned shell to PowerShell.
        | SwitchToPowerShell
        // Stage 7 PR-C / PR-J — switch spawned shell to claude.
        | SwitchToClaude
        // Cycle 22b — copy SessionModel history to clipboard.
        | CopyHistoryToClipboard
        // Cycle 24e — announce active session-log file path.
        | AnnounceSessionLogPath
        // Cycle 46 post-audit (2026-05-13) — open the latest
        // tuple's full OutputText in the default text editor.
        // Companion to the 800-char tuple-final Announce cap;
        // when the cap truncates a long output, this hotkey
        // surfaces the full text on demand. Writes a fresh
        // timestamped file under
        // %LOCALAPPDATA%\PtySpeak\extracts\ and shell-executes
        // it (registered .txt handler).
        | OpenLastOutput
        // Cycle 46 post-audit (2026-05-13) — re-narrate the
        // latest tuple's OutputText (capped at the same 800
        // chars the boundary handler's auto-narrate uses). For
        // the user who missed the auto-Announce (was speaking,
        // typing, switched window). Companion to OpenLastOutput
        // when the user wants spoken rather than text-editor
        // surface.
        | AnnounceLastOutput
        // ADR 0007 Phase 2a (2026-05-16) — copy the focused
        // SpeechCursor cell's command + output to the
        // clipboard. The per-cell analogue of
        // `CopyHistoryToClipboard` (all) / `OpenLastOutput`
        // (last): this acts on whichever cell the Manual
        // cursor (Ctrl+Shift+Up/Down/End) is parked on.
        | CopyFocusedCell
        // ADR 0007 Phase 2b (2026-05-17) — copy just one side
        // of the focused cell (command-only / output-only).
        // Menu-only (no accelerator): the maintainer's stated
        // hotkey-surface concern + the D5a "shrink the custom
        // hotkey surface" note — Ctrl+Shift+C (whole cell) is
        // the keyboarded common case; these are the precise
        // menu variants.
        | CopyFocusedCellCommand
        | CopyFocusedCellOutput
        // ADR 0007 Phase 2c (2026-05-17) — jump the Manual
        // cursor to the most recent failed (non-zero exit)
        // cell. Menu-only, same rationale as 2b.
        | JumpToLastError
        // ADR 0007 Phase 3 (2026-05-17) — re-submit the
        // focused input cell's command as a fresh command.
        // Menu-only; two-step arm/confirm gate in the
        // handler (no auto-run on navigation).
        | RerunFocusedInput
        // Cycle 47 — CMD interaction tests. Each menu item
        // writes a quoted invocation of the corresponding
        // `scripts/cmd-tests/*.cmd` script to the PTY input
        // cursor (no Enter); the user reviews + presses Enter
        // to run. No keyboard accelerators (menu-only); too
        // many to assign reasonable mnemonics. Mirrors the
        // shape of `RunProcessCleanupScript`.
        | CmdTestEcho
        | CmdTestTextInput
        | CmdTestNumericInput
        | CmdTestYesNo
        | CmdTestMultiChoice
        | CmdTestPause
        | CmdTestProgress
        | CmdTestStderr
        | CmdTestMultiInterrupt
        // Cycle 26c — first menu-only command. Interactive
        // process-cleanup test (`scripts/test-process-cleanup.ps1`)
        // surfaced via Diagnostics → Test Process Cleanup. No
        // default keyboard accelerator; relieves the noted
        // hotkey-count working-memory ceiling for additional
        // diagnostic scripts.
        | RunProcessCleanupScript
        // Cycle 28 — Window menu commands. Both menu-only.
        // CloseWindow shows `Alt+F4` as InputGestureText (the OS
        // gesture WPF Window already handles natively); the
        // menu item itself wires `Window.Close()` so the menu
        // path is symmetric with the keyboard path. ExitApp
        // calls `Application.Current.Shutdown()` for an explicit
        // app-level exit (in a single-window app the visible
        // behaviour is identical to CloseWindow today; the
        // separate slot future-proofs against multi-pane Phase 2
        // plans where Close-Window-vs-Exit-App becomes meaningful).
        | CloseWindow
        | ExitApp
        // Cycle 38a-followup — second menu-only command.
        // Filters `docs/ACCESSIBILITY-TESTING.md` to sections
        // marked `<!-- DOGFOOD -->` via `ManualTestsHtml.filterAndConvert`,
        // writes the HTML to `%LOCALAPPDATA%\PtySpeak\manual-tests.html`,
        // and opens it in the default browser. NVDA browse-mode H
        // key jumps headings; D key jumps the `<main>` landmark.
        | OpenManualTests
        // Cycle 43a — diagnostic chunk extractors. All menu-only;
        // no keyboard accelerators (accelerator budget is saturated
        // per the maintainer's hotkey-count working-memory ceiling,
        // and chunking is power-user enough that menu access is the
        // right shape). The full catalog lives under
        // `Diagnostics → Extract → {Recency | Event Type | Bundle
        // Section | Snapshot}` with first-cut representatives in
        // 43a; subsequent extractors plug into the same shape in
        // 43b. The two top-level items
        // (`CopyLatestBundle` and `GrepDiagnostics`) bypass the
        // submenu hierarchy because they're the load-bearing
        // triage tools — every chunking session starts by either
        // copying the current state in one keystroke or grepping
        // it with a pattern.
        | CopyLatestBundle
        | GrepDiagnostics
        | ExtractLast50LogLines
        | ExtractErrorsAndWarnings
        | ExtractActiveConfig
        | ExtractVersionHeader
        // Cycle 47 follow-up (2026-05-13) — test-bracketed
        // extractors. Each one pulls the `=== PTYSPEAK-TEST-START:
        // <id> === ... === PTYSPEAK-TEST-END: <id> ===` slice
        // for the matching CMD test out of the ContentHistory
        // tail. Companion to `CmdTest*` (which writes the script
        // invocation to the PTY input cursor); pair the two to
        // run + extract a focused triage slice without touching
        // the surrounding session output. All menu-only;
        // surfaced under Diagnostics → Extract → Test Run.
        | ExtractTestEcho
        | ExtractTestTextInput
        | ExtractTestNumericInput
        | ExtractTestYesNo
        | ExtractTestMultiChoice
        | ExtractTestPause
        | ExtractTestProgress
        | ExtractTestStderr
        // Cycle 45 Commit 2 — SpeechCursor navigation. All
        // menu-only (no keyboard accelerators in 45; future
        // cycles can layer accelerators on after the muscle
        // memory settles). Surfaced under Display → Speech
        // Cursor → {Next | Previous | Jump to Latest | Toggle
        // Mode}.
        | SpeechCursorNext
        | SpeechCursorPrevious
        | SpeechCursorJumpToLatest
        | SpeechCursorToggleMode
        // ADR 0007 Phase 6a-2b — pane switch between the live
        // terminal surface and the focusable cell-history list.
        // Gesture-bearing (Ctrl+Shift+Left / Ctrl+Shift+Right)
        // AND menu-surfaced for discoverability.
        | FocusHistoryPane
        | FocusTerminalPane

    /// Stable string name for a command, used as the
    /// `RoutedCommand` name passed to WPF and as a TOML key
    /// for future user-settings overrides. Exhaustive match
    /// is the F# compile-time pin against accidentally adding
    /// an `AppCommand` case without a name.
    let nameOf (cmd: AppCommand) : string =
        match cmd with
        | CheckForUpdates -> "CheckForUpdates"
        | RunDiagnostic -> "RunDiagnostic"
        | DraftNewRelease -> "DraftNewRelease"
        | OpenDataFolder -> "OpenDataFolder"
        | OpenConfig -> "OpenConfig"
        | HealthCheck -> "HealthCheck"
        | IncidentMarker -> "IncidentMarker"
        | SwitchToCmd -> "SwitchToCmd"
        | SwitchToPowerShell -> "SwitchToPowerShell"
        | SwitchToClaude -> "SwitchToClaude"
        | CopyHistoryToClipboard -> "CopyHistoryToClipboard"
        | AnnounceSessionLogPath -> "AnnounceSessionLogPath"
        | OpenLastOutput -> "OpenLastOutput"
        | AnnounceLastOutput -> "AnnounceLastOutput"
        | CopyFocusedCell -> "CopyFocusedCell"
        | CopyFocusedCellCommand -> "CopyFocusedCellCommand"
        | CopyFocusedCellOutput -> "CopyFocusedCellOutput"
        | JumpToLastError -> "JumpToLastError"
        | RerunFocusedInput -> "RerunFocusedInput"
        | CmdTestEcho -> "CmdTestEcho"
        | CmdTestTextInput -> "CmdTestTextInput"
        | CmdTestNumericInput -> "CmdTestNumericInput"
        | CmdTestYesNo -> "CmdTestYesNo"
        | CmdTestMultiChoice -> "CmdTestMultiChoice"
        | CmdTestPause -> "CmdTestPause"
        | CmdTestProgress -> "CmdTestProgress"
        | CmdTestStderr -> "CmdTestStderr"
        | CmdTestMultiInterrupt -> "CmdTestMultiInterrupt"
        | RunProcessCleanupScript -> "RunProcessCleanupScript"
        | CloseWindow -> "CloseWindow"
        | ExitApp -> "ExitApp"
        | OpenManualTests -> "OpenManualTests"
        | CopyLatestBundle -> "CopyLatestBundle"
        | GrepDiagnostics -> "GrepDiagnostics"
        | ExtractLast50LogLines -> "ExtractLast50LogLines"
        | ExtractErrorsAndWarnings -> "ExtractErrorsAndWarnings"
        | ExtractActiveConfig -> "ExtractActiveConfig"
        | ExtractVersionHeader -> "ExtractVersionHeader"
        | ExtractTestEcho -> "ExtractTestEcho"
        | ExtractTestTextInput -> "ExtractTestTextInput"
        | ExtractTestNumericInput -> "ExtractTestNumericInput"
        | ExtractTestYesNo -> "ExtractTestYesNo"
        | ExtractTestMultiChoice -> "ExtractTestMultiChoice"
        | ExtractTestPause -> "ExtractTestPause"
        | ExtractTestProgress -> "ExtractTestProgress"
        | ExtractTestStderr -> "ExtractTestStderr"
        | SpeechCursorNext -> "SpeechCursorNext"
        | SpeechCursorPrevious -> "SpeechCursorPrevious"
        | SpeechCursorJumpToLatest -> "SpeechCursorJumpToLatest"
        | SpeechCursorToggleMode -> "SpeechCursorToggleMode"
        | FocusHistoryPane -> "FocusHistoryPane"
        | FocusTerminalPane -> "FocusTerminalPane"

    /// Default key binding for a command. Mirrors the
    /// `AppReservedHotkeys` table in
    /// `src/Views/TerminalView.cs`. Phase 2 user-settings will
    /// override per-user.
    ///
    /// Cycle 26b — `Key` and `Modifiers` are `option`-typed.
    /// `Some k, Some m` is a gesture-bearing command (most
    /// commands today); `None, None` is a menu-only command
    /// (e.g. `RunProcessCleanupScript` in Cycle 26c) that
    /// surfaces only via the app menu, no default keyboard
    /// shortcut. `bindHotkey` skips the `KeyBinding`
    /// registration in the menu-only case but still registers
    /// the `CommandBinding` so the `RoutedCommand` can be
    /// invoked from `MenuItem.Command`. Adding or removing a
    /// default accelerator on an existing command is now a
    /// one-field edit (`Some` ↔ `None`) rather than a
    /// DU-shape change.
    type Hotkey =
        { Command: AppCommand
          Key: HotkeyKey option
          Modifiers: Set<Modifier> option
          Description: string }

    let private ctrlShift : Set<Modifier> = Set.ofList [ Ctrl; Shift ]

    /// All registered hotkeys. Order is irrelevant; lookup is
    /// by `AppCommand` via `hotkeyOf` or by `(key, modifiers)`
    /// via `tryFind`. Adding a hotkey requires (a) extending
    /// `AppCommand`, (b) updating `nameOf`, (c) appending a
    /// row here, (d) adding a matching row to
    /// `TerminalView.AppReservedHotkeys`. The exhaustive
    /// `nameOf` match catches (b); the `HotkeyRegistryTests`
    /// fixtures pin (a) + (c) + (d).
    let builtIns : Hotkey list =
        [ { Command = CheckForUpdates
            Key = Some (Letter 'U')
            Modifiers = Some ctrlShift
            Description = "Velopack auto-update (Stage 11)" }
          { Command = RunDiagnostic
            Key = Some (Letter 'D')
            Modifiers = Some ctrlShift
            Description = "Run full automated diagnostic suite (Cycle 25b — bundles dump to clipboard + dated snapshot file)" }
          { Command = DraftNewRelease
            Key = Some (Letter 'R')
            Modifiers = Some ctrlShift
            Description = "Draft a new release on GitHub" }
          { Command = OpenDataFolder
            Key = Some (Letter 'P')
            Modifiers = Some ctrlShift
            Description = "Open the pty-speak data folder (Cycle 25a; parent of logs / sessions / config)" }
          { Command = OpenConfig
            Key = Some (Letter 'E')
            Modifiers = Some ctrlShift
            Description = "Edit config.toml (Cycle 25a; auto-creates with defaults if missing)" }
          { Command = HealthCheck
            Key = Some (Letter 'H')
            Modifiers = Some ctrlShift
            Description = "Health-check announce (shell + PID + alive + queue depths)" }
          { Command = IncidentMarker
            Key = Some (Letter 'B')
            Modifiers = Some ctrlShift
            Description = "Incident-marker boundary line in active log" }
          { Command = SwitchToCmd
            Key = Some (Digit 1)
            Modifiers = Some ctrlShift
            Description = "Switch to cmd shell" }
          { Command = SwitchToPowerShell
            Key = Some (Digit 2)
            Modifiers = Some ctrlShift
            Description = "Switch to PowerShell shell (PR-J — diagnostic control shell)" }
          { Command = SwitchToClaude
            Key = Some (Digit 3)
            Modifiers = Some ctrlShift
            Description = "Switch to Claude shell" }
          { Command = CopyHistoryToClipboard
            Key = Some (Letter 'Y')
            Modifiers = Some ctrlShift
            Description = "Copy SessionModel history to clipboard (Cycle 22b)" }
          { Command = AnnounceSessionLogPath
            Key = Some (Letter 'S')
            Modifiers = Some ctrlShift
            Description = "Announce the active session-log file path (Cycle 24e)" }
          // Cycle 46 post-audit (2026-05-13) — open last
          // command output in the default text editor.
          // Companion to the tuple-final Announce cap.
          { Command = OpenLastOutput
            Key = Some (Letter 'O')
            Modifiers = Some ctrlShift
            Description = "Open last command output in default text editor (Cycle 46 post-audit)" }
          // Cycle 46 post-audit (2026-05-13) — re-narrate the
          // latest tuple's OutputText (capped at the same
          // 800 chars as the auto-narrate).
          { Command = AnnounceLastOutput
            Key = Some (Letter 'A')
            Modifiers = Some ctrlShift
            Description = "Re-narrate last command output (capped at 800 chars; Cycle 46 post-audit)" }
          // ADR 0007 Phase 2a (2026-05-16) — copy the focused
          // SpeechCursor cell (command + output) to clipboard.
          { Command = CopyFocusedCell
            Key = Some (Letter 'C')
            Modifiers = Some ctrlShift
            Description = "Copy the focused cell (command + output) to the clipboard (ADR 0007 Phase 2a)" }
          // ADR 0007 Phase 2b (2026-05-17) — copy one side of
          // the focused cell. Menu-only (Key = None).
          { Command = CopyFocusedCellCommand
            Key = None
            Modifiers = None
            Description = "Copy the focused cell's command to the clipboard (ADR 0007 Phase 2b)" }
          { Command = CopyFocusedCellOutput
            Key = None
            Modifiers = None
            Description = "Copy the focused cell's output to the clipboard (ADR 0007 Phase 2b)" }
          // ADR 0007 Phase 2c (2026-05-17) — jump to last
          // failed cell. Menu-only (Key = None).
          { Command = JumpToLastError
            Key = None
            Modifiers = None
            Description = "Jump the speech cursor to the most recent failed command (ADR 0007 Phase 2c)" }
          // ADR 0007 Phase 3 (2026-05-17) — rerun focused
          // input. Menu-only (Key = None); two-step confirm.
          { Command = RerunFocusedInput
            Key = None
            Modifiers = None
            Description = "Re-submit the focused input cell's command as a fresh command; two-step confirm (ADR 0007 Phase 3)" }
          // Cycle 47 — CMD interaction test corpus. All menu-
          // only (no keyboard accelerators). Each item writes
          // an invocation of the corresponding `.cmd` script
          // to the PTY input cursor; the user reviews + Enters
          // to run.
          { Command = CmdTestEcho
            Key = None
            Modifiers = None
            Description = "CMD test: simple echo (Cycle 47)" }
          { Command = CmdTestTextInput
            Key = None
            Modifiers = None
            Description = "CMD test: text input via set /p (Cycle 47)" }
          { Command = CmdTestNumericInput
            Key = None
            Modifiers = None
            Description = "CMD test: numeric input + set /a calculation (Cycle 47)" }
          { Command = CmdTestYesNo
            Key = None
            Modifiers = None
            Description = "CMD test: yes/no choice via choice /c YN (Cycle 47)" }
          { Command = CmdTestMultiChoice
            Key = None
            Modifiers = None
            Description = "CMD test: multi-option choice via choice /c 1234 (Cycle 47)" }
          { Command = CmdTestPause
            Key = None
            Modifiers = None
            Description = "CMD test: pause / continue (Cycle 47)" }
          { Command = CmdTestProgress
            Key = None
            Modifiers = None
            Description = "CMD test: progress loop with timeout (Cycle 47)" }
          { Command = CmdTestStderr
            Key = None
            Modifiers = None
            Description = "CMD test: stderr output (Cycle 47)" }
          { Command = CmdTestMultiInterrupt
            Key = None
            Modifiers = None
            Description = "CMD test: multi-interrupt watermark composition (Cycle 52 R3c)" }
          // Cycle 26c — menu-only; no default keyboard accelerator.
          // Surfaced as Diagnostics → Test Process Cleanup.
          { Command = RunProcessCleanupScript
            Key = None
            Modifiers = None
            Description = "Interactive process-cleanup test (test-process-cleanup.ps1; Cycle 26c)" }
          // Cycle 28 — Window menu commands. Both menu-only.
          // The Alt+F4 OS gesture is handled by WPF Window
          // natively; the menu items duplicate the same
          // operations for discoverability via Alt-menu
          // navigation.
          { Command = CloseWindow
            Key = None
            Modifiers = None
            Description = "Close the main window (Cycle 28; Alt+F4 also works as the OS gesture)" }
          { Command = ExitApp
            Key = None
            Modifiers = None
            Description = "Exit pty-speak (Cycle 28; Application.Current.Shutdown)" }
          // Cycle 38a-followup — second menu-only command after
          // RunProcessCleanupScript. Surfaced as Diagnostics →
          // Open Manual Tests.
          { Command = OpenManualTests
            Key = None
            Modifiers = None
            Description = "Open the dogfood-filtered manual-tests HTML quickref in the default browser (Cycle 38a-followup)" }
          // Cycle 43a — diagnostic chunk extractors. All menu-only.
          // The two top-level items live under Diagnostics directly;
          // the four `Extract*` items live under
          // Diagnostics → Extract → {Recency | Event Type | Bundle
          // Section | Snapshot} (one representative per sub-submenu
          // in 43a; the remaining catalog ships in 43b).
          { Command = CopyLatestBundle
            Key = None
            Modifiers = None
            Description = "Copy the latest lightweight diagnostic bundle to the clipboard (Cycle 43a; no battery run, no test commands — fast current-state snapshot)" }
          { Command = GrepDiagnostics
            Key = None
            Modifiers = None
            Description = "Open a dialog to grep the lightweight diagnostic bundle for a pattern; copies matches to the clipboard (Cycle 43a)" }
          { Command = ExtractLast50LogLines
            Key = None
            Modifiers = None
            Description = "Extract the last 50 lines of the active FileLogger log to the clipboard (Cycle 43a; Recency)" }
          { Command = ExtractErrorsAndWarnings
            Key = None
            Modifiers = None
            Description = "Extract FileLogger entries with Semantic=ErrorLine, WarningLine, or ParserError (Cycle 43a; Event Type)" }
          { Command = ExtractActiveConfig
            Key = None
            Modifiers = None
            Description = "Extract the active config.toml to the clipboard (Cycle 43a; Bundle Section)" }
          { Command = ExtractVersionHeader
            Key = None
            Modifiers = None
            Description = "Extract the version + environment header (version, OS, .NET, PID) to the clipboard (Cycle 43a; Snapshot)" }
          // Cycle 47 follow-up (2026-05-13) — test-bracketed
          // extractors. Each one pulls every
          // `=== PTYSPEAK-TEST-START: <id> === ... === PTYSPEAK-TEST-END:
          // <id> ===` slice for the matching CMD test out of the
          // ContentHistory tail. All menu-only.
          { Command = ExtractTestEcho
            Key = None
            Modifiers = None
            Description = "Extract every test-01-echo bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestTextInput
            Key = None
            Modifiers = None
            Description = "Extract every test-02-text-input bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestNumericInput
            Key = None
            Modifiers = None
            Description = "Extract every test-03-numeric-input bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestYesNo
            Key = None
            Modifiers = None
            Description = "Extract every test-04-yes-no bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestMultiChoice
            Key = None
            Modifiers = None
            Description = "Extract every test-05-multi-choice bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestPause
            Key = None
            Modifiers = None
            Description = "Extract every test-06-pause bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestProgress
            Key = None
            Modifiers = None
            Description = "Extract every test-07-progress bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          { Command = ExtractTestStderr
            Key = None
            Modifiers = None
            Description = "Extract every test-08-stderr bracketed slice from the ContentHistory tail (Cycle 47 follow-up; Test Run)" }
          // Cycle 45 Commit 2 — SpeechCursor navigation. Menu-only.
          //
          // Cycle 45 backlog (docs/USER-SETTINGS.md "Speech-cursor
          // keyboard accelerators"): the maintainer flagged
          // `Ctrl+Up` / `Ctrl+Down` as natural gestures for
          // Previous / Next, and `Ctrl+Shift+Up/Down` for
          // chunk-level jumps (depends on semantic-label work).
          // Switching from menu-only to gesture-bearing is just
          // a `Key = None` → `Some (Letter / arrow)` edit + the
          // matching TerminalView.AppReservedHotkeys mirror row
          // (the Cycle 26b parity test pins both surfaces).
          // Verify no NVDA collision before binding —
          // `Ctrl+Up/Down` should be free in screen-reader mode
          // (NVDA's default review-cursor commands are
          // `NVDA+Up/Down`).
          { Command = SpeechCursorNext
            Key = Some Down
            Modifiers = Some ctrlShift
            Description = "Speech Cursor: move to the next entry" }
          { Command = SpeechCursorPrevious
            Key = Some Up
            Modifiers = Some ctrlShift
            Description = "Speech Cursor: move to the previous entry" }
          { Command = SpeechCursorJumpToLatest
            Key = Some End
            Modifiers = Some ctrlShift
            Description = "Speech Cursor: jump to the latest entry" }
          { Command = SpeechCursorToggleMode
            Key = None
            Modifiers = None
            Description = "Speech Cursor: toggle AutoDrive / Manual mode (menu-only)" }
          // ADR 0007 Phase 6a-2b — pane switch. Spatially
          // correct (maintainer dogfood 2026-05-17): the
          // terminal is the LEFT pane (Grid.Column 0), the
          // cell-history list the RIGHT pane (Grid.Column 2),
          // so Ctrl+Shift+Left → terminal, Ctrl+Shift+Right →
          // history. NVDA announces the newly-focused control
          // natively; the handlers publish
          // CellEventBus.PaneSwitched in parallel for the
          // non-speech (earcon) cue (ADR 0007 6a-2b conformance).
          { Command = FocusTerminalPane
            Key = Some Left
            Modifiers = Some ctrlShift
            Description = "Focus the terminal pane (left)" }
          { Command = FocusHistoryPane
            Key = Some Right
            Modifiers = Some ctrlShift
            Description = "Focus the cell-history pane (right)" } ]

    /// Look up the default Hotkey for a command. Throws
    /// `KeyNotFoundException` if the registry is incomplete —
    /// `HotkeyRegistryTests.every AppCommand case has a builtIns entry`
    /// pins this against accidental case-addition without
    /// matching builtIns row.
    let hotkeyOf (cmd: AppCommand) : Hotkey =
        builtIns
        |> List.tryFind (fun h -> h.Command = cmd)
        |> Option.defaultWith (fun () ->
            raise (
                System.Collections.Generic.KeyNotFoundException(
                    sprintf
                        "HotkeyRegistry.builtIns missing entry for AppCommand.%s"
                        (nameOf cmd))))

    /// Look up an `AppCommand` by `(key, modifiers)`. Useful
    /// for future user-settings UIs displaying the binding for
    /// a chosen gesture; not used by the dispatch path (which
    /// goes the other way: command → key).
    ///
    /// Cycle 26b — menu-only commands (`Hotkey.Key = None`)
    /// are excluded from `tryFind` results since they have no
    /// gesture to look up by definition. Only gesture-bearing
    /// `Some` entries match.
    let tryFind (key: HotkeyKey) (modifiers: Set<Modifier>) : AppCommand option =
        builtIns
        |> List.tryFind (fun h ->
            h.Key = Some key && h.Modifiers = Some modifiers)
        |> Option.map (fun h -> h.Command)

    /// Format a `Hotkey`'s gesture as a human-readable string
    /// for display in `MenuItem.InputGestureText` (which NVDA
    /// reads when the menu item is focused). Returns `None`
    /// for menu-only commands (`Key = None` / `Modifiers = None`)
    /// so the menu item can omit the shortcut display.
    ///
    /// Format mirrors the convention used in CLAUDE.md and
    /// CHANGELOG entries: `"Ctrl+Shift+U"`, `"Ctrl+Shift+1"`,
    /// `"Ctrl+Shift+;"`. Modifier order is fixed
    /// (Ctrl > Alt > Shift) so the rendered text is stable
    /// regardless of `Set` enumeration order.
    let gestureText (hk: Hotkey) : string option =
        match hk.Key, hk.Modifiers with
        | Some key, Some mods ->
            let modParts =
                [ if Set.contains Ctrl mods then yield "Ctrl"
                  if Set.contains Alt mods then yield "Alt"
                  if Set.contains Shift mods then yield "Shift" ]
            let keyText =
                match key with
                | Letter c -> string (System.Char.ToUpperInvariant c)
                | Digit n -> string n
                | Semicolon -> ";"
                | Up -> "Up"
                | Down -> "Down"
                | End -> "End"
                | Left -> "Left"
                | Right -> "Right"
            Some (System.String.Join("+", modParts @ [ keyText ]))
        | _ -> None

    /// All registered AppCommand cases as a list. Manually
    /// maintained; pinned by the
    /// `HotkeyRegistryTests.allCommands matches AppCommand DU`
    /// fixture which round-trips each case through `nameOf`.
    /// Used by tests + future Phase 2 user-settings UI to
    /// enumerate the bindable command set.
    let allCommands : AppCommand list =
        [ CheckForUpdates
          RunDiagnostic
          DraftNewRelease
          OpenDataFolder
          OpenConfig
          HealthCheck
          IncidentMarker
          SwitchToCmd
          SwitchToPowerShell
          SwitchToClaude
          CopyHistoryToClipboard
          AnnounceSessionLogPath
          OpenLastOutput
          AnnounceLastOutput
          CopyFocusedCell
          CopyFocusedCellCommand
          CopyFocusedCellOutput
          JumpToLastError
          RerunFocusedInput
          CmdTestEcho
          CmdTestTextInput
          CmdTestNumericInput
          CmdTestYesNo
          CmdTestMultiChoice
          CmdTestPause
          CmdTestProgress
          CmdTestStderr
          CmdTestMultiInterrupt
          RunProcessCleanupScript
          CloseWindow
          ExitApp
          OpenManualTests
          CopyLatestBundle
          GrepDiagnostics
          ExtractLast50LogLines
          ExtractErrorsAndWarnings
          ExtractActiveConfig
          ExtractVersionHeader
          ExtractTestEcho
          ExtractTestTextInput
          ExtractTestNumericInput
          ExtractTestYesNo
          ExtractTestMultiChoice
          ExtractTestPause
          ExtractTestProgress
          ExtractTestStderr
          SpeechCursorNext
          SpeechCursorPrevious
          SpeechCursorJumpToLatest
          SpeechCursorToggleMode
          FocusHistoryPane
          FocusTerminalPane ]

    // ---------------------------------------------------------------
    // Cycle 27 — Multi-state command paradigm
    // ---------------------------------------------------------------
    //
    // `MultiStateCommand` is the parallel concept to `AppCommand`
    // for operations whose UX is "select one of N discrete options"
    // rather than "fire one action". The two surfaces it adds:
    //
    //   - A parent `MenuItem` whose sub-items are the options.
    //   - Each option exposes its checked / not-checked state via
    //     WPF `MenuItem.IsCheckable=true` + `IsChecked`, which
    //     surfaces UIA TogglePattern that NVDA reads as
    //     "menu item, checked" / "menu item, not checked". A
    //     screen-reader user can therefore tell at a glance which
    //     option is currently active.
    //
    // The existing `AppCommand` / `Hotkey` / `bindHotkey`
    // framework above is unchanged and continues to be the path
    // for single-action commands (gesture-bearing or menu-only).
    // The two surfaces are deliberately parallel — a future
    // multi-state command needing per-option keyboard
    // accelerators is a clean extension to `MultiStateOption`
    // without disturbing single-action commands.
    //
    // **Cycle 27 migrations.** `EarconsMode` (formerly the
    // `MuteEarcons` Ctrl+Shift+M toggle) and `LoggingLevel`
    // (formerly the `ToggleDebugLog` Ctrl+Shift+G toggle) are
    // both binary-state operations whose previous flip-toggle
    // UX provided no on-screen indication of the current state.
    // Surfacing them as multi-state menu items makes the current
    // state legible. Both keyboard hotkeys are dropped per the
    // maintainer's hotkey-count working-memory ceiling; future
    // multi-state commands are menu-only by canon.

    /// Identity of every multi-state command. Parallel to
    /// `AppCommand`; lookup goes through `multiStateOf` (analog
    /// of `hotkeyOf`).
    type MultiStateCommand =
        // Cycle 27 — Migrated from the Ctrl+Shift+M toggle.
        // Options: enabled / muted.
        | EarconsMode
        // Cycle 27 — Migrated from the Ctrl+Shift+G toggle.
        // Options: information / debug.
        | LoggingLevel
        // Cycle 45f — per-shell streaming announce mode. Pick
        // applies to the currently-active shell's runtime
        // override (Layer 3 of the three-layer settings model);
        // hot-switching reverts to that shell's TOML / compiled
        // default. Options: tuple_final / line_by_line / off.
        | OutputVerbosity
        // Cycle 45f — per-shell prompt-path verbosity. Same
        // scoping as OutputVerbosity. Options: suppress /
        // final_dir_only / full.
        | PromptPathVerbosity

    /// Stable string name for a multi-state command. Used as
    /// the XAML field-name prefix (`MenuItem_<name>`) for the
    /// parent menu item and as the prefix for each per-option
    /// `RoutedCommand` name.
    let multiStateNameOf (cmd: MultiStateCommand) : string =
        match cmd with
        | EarconsMode -> "EarconsMode"
        | LoggingLevel -> "LoggingLevel"
        | OutputVerbosity -> "OutputVerbosity"
        | PromptPathVerbosity -> "PromptPathVerbosity"

    /// One option within a multi-state command. `OptionId` is
    /// the stable snake_case identifier used in XAML field
    /// naming (`MenuItem_<Cmd>_<OptionId>`), per-option
    /// `RoutedCommand` naming, log lines, and (future) TOML
    /// config keys. `DisplayName` is the user-facing label
    /// rendered in the menu and read by NVDA.
    type MultiStateOption =
        { OptionId: string
          DisplayName: string }

    /// Multi-state command definition: identity, display
    /// label, the ordered option list, and a description.
    /// Mirrors `Hotkey`'s shape for `AppCommand` minus the
    /// keyboard-gesture fields (multi-state is menu-only by
    /// canon as of Cycle 27; future per-option gestures are a
    /// `MultiStateOption` extension when needed).
    type MultiStateDef =
        { Command: MultiStateCommand
          DisplayName: string
          Options: MultiStateOption list
          Description: string }

    /// All registered multi-state commands. Mirrors
    /// `builtIns` for `AppCommand`. Adding a multi-state
    /// command requires (a) extending `MultiStateCommand`,
    /// (b) updating `multiStateNameOf`, (c) appending a row
    /// here, (d) appending to `multiStateAllCommands`. The
    /// exhaustive `multiStateNameOf` match catches (b);
    /// `MultiStateRegistryTests` fixtures pin (a) + (c) + (d).
    let multiStateBuiltIns : MultiStateDef list =
        [ { Command = EarconsMode
            DisplayName = "Earcons"
            Options =
              [ { OptionId = "enabled"; DisplayName = "Enabled" }
                { OptionId = "muted"; DisplayName = "Muted" } ]
            Description = "Earcons enabled / muted (migrated from Ctrl+Shift+M; menu-only since Cycle 27)" }
          { Command = LoggingLevel
            DisplayName = "Logging Level"
            Options =
              [ { OptionId = "information"; DisplayName = "Information" }
                { OptionId = "debug"; DisplayName = "Debug" } ]
            Description = "FileLogger min-level Information / Debug (migrated from Ctrl+Shift+G; menu-only since Cycle 27)" }
          { Command = OutputVerbosity
            DisplayName = "Output Verbosity"
            Options =
              [ { OptionId = "tuple_final"; DisplayName = "Tuple Final" }
                { OptionId = "line_by_line"; DisplayName = "Line By Line" }
                { OptionId = "off"; DisplayName = "Off" } ]
            Description = "Per-shell streaming announce mode (Cycle 45f). Runtime override on the active shell; persists across hot-switches until app restart. See docs/USER-SETTINGS.md \"Verbosity\"." }
          { Command = PromptPathVerbosity
            DisplayName = "Prompt Path"
            Options =
              [ { OptionId = "suppress"; DisplayName = "Suppress" }
                { OptionId = "final_dir_only"; DisplayName = "Final Directory Only" }
                { OptionId = "full"; DisplayName = "Full" }
                { OptionId = "full_on_change"; DisplayName = "Full On Directory Change" }
                { OptionId = "final_on_change"; DisplayName = "Final Dir On Change, Full When Same" }
                { OptionId = "full_on_change_silent"; DisplayName = "Full On Change, Silent When Same" }
                { OptionId = "final_on_change_silent"; DisplayName = "Final Dir On Change, Silent When Same" } ]
            Description = "Per-shell prompt-path verbosity on PromptStart marker (Cycle 45f; Cycle 52 R6b added 'Full On Directory Change'; R6b-followup added the mirror 'Final Dir On Change, Full When Same' and the two silent-when-unchanged variants — context-aware: one rendering when the dir changed, another when unchanged). Same scoping as Output Verbosity." } ]

    /// Look up the `MultiStateDef` for a `MultiStateCommand`.
    /// Throws `KeyNotFoundException` if missing — pinned by
    /// the `MultiStateRegistryTests.every MultiStateCommand
    /// case has a builtIns entry` fixture.
    let multiStateOf (cmd: MultiStateCommand) : MultiStateDef =
        multiStateBuiltIns
        |> List.tryFind (fun d -> d.Command = cmd)
        |> Option.defaultWith (fun () ->
            raise (
                System.Collections.Generic.KeyNotFoundException(
                    sprintf
                        "HotkeyRegistry.multiStateBuiltIns missing entry for MultiStateCommand.%s"
                        (multiStateNameOf cmd))))

    /// All registered multi-state commands. Manually
    /// maintained; pinned by `MultiStateRegistryTests`.
    let multiStateAllCommands : MultiStateCommand list =
        [ EarconsMode
          LoggingLevel
          OutputVerbosity
          PromptPathVerbosity ]
