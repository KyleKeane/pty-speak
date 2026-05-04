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
        // Logging-PR — open logs folder in File Explorer.
        | OpenLogsFolder
        // Logging-restructure PR (#111) — copy active log to clipboard.
        | CopyLatestLog
        // Stage 7-followup PR-E — toggle FileLogger min-level.
        | ToggleDebugLog
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
        | OpenLogsFolder -> "OpenLogsFolder"
        | CopyLatestLog -> "CopyLatestLog"
        | ToggleDebugLog -> "ToggleDebugLog"
        | HealthCheck -> "HealthCheck"
        | IncidentMarker -> "IncidentMarker"
        | SwitchToCmd -> "SwitchToCmd"
        | SwitchToPowerShell -> "SwitchToPowerShell"
        | SwitchToClaude -> "SwitchToClaude"

    /// Default key binding for a command. Mirrors the
    /// `AppReservedHotkeys` table in
    /// `src/Views/TerminalView.cs`. Phase 2 user-settings will
    /// override per-user.
    type Hotkey =
        { Command: AppCommand
          Key: HotkeyKey
          Modifiers: Set<Modifier>
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
            Key = Letter 'U'
            Modifiers = ctrlShift
            Description = "Velopack auto-update (Stage 11)" }
          { Command = RunDiagnostic
            Key = Letter 'D'
            Modifiers = ctrlShift
            Description = "Process-cleanup diagnostic launcher (PR-J adds inline shell-process snapshot)" }
          { Command = DraftNewRelease
            Key = Letter 'R'
            Modifiers = ctrlShift
            Description = "Draft a new release on GitHub" }
          { Command = OpenLogsFolder
            Key = Letter 'L'
            Modifiers = ctrlShift
            Description = "Open logs folder in File Explorer" }
          { Command = CopyLatestLog
            Key = Semicolon
            Modifiers = ctrlShift
            Description = "Copy active session log to clipboard" }
          { Command = ToggleDebugLog
            Key = Letter 'G'
            Modifiers = ctrlShift
            Description = "Toggle FileLogger Information ↔ Debug" }
          { Command = HealthCheck
            Key = Letter 'H'
            Modifiers = ctrlShift
            Description = "Health-check announce (shell + PID + alive + queue depths)" }
          { Command = IncidentMarker
            Key = Letter 'B'
            Modifiers = ctrlShift
            Description = "Incident-marker boundary line in active log" }
          { Command = SwitchToCmd
            Key = Digit 1
            Modifiers = ctrlShift
            Description = "Switch to cmd shell" }
          { Command = SwitchToPowerShell
            Key = Digit 2
            Modifiers = ctrlShift
            Description = "Switch to PowerShell shell (PR-J — diagnostic control shell)" }
          { Command = SwitchToClaude
            Key = Digit 3
            Modifiers = ctrlShift
            Description = "Switch to Claude shell" } ]

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
    let tryFind (key: HotkeyKey) (modifiers: Set<Modifier>) : AppCommand option =
        builtIns
        |> List.tryFind (fun h -> h.Key = key && h.Modifiers = modifiers)
        |> Option.map (fun h -> h.Command)

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
          OpenLogsFolder
          CopyLatestLog
          ToggleDebugLog
          HealthCheck
          IncidentMarker
          SwitchToCmd
          SwitchToPowerShell
          SwitchToClaude ]
