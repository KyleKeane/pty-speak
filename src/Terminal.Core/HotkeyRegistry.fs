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
        // Cycle 25a — open the pty-speak data folder
        // (`%LOCALAPPDATA%\PtySpeak\`). Replaces OpenLogsFolder
        // because the parent gives one-keystroke access to all of
        // logs / sessions / config rather than just logs.
        | OpenDataFolder
        // Cycle 25a — auto-create config.toml with defaults if
        // missing, then open in default app.
        | OpenConfig
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
        // Stage 8d.1 — toggle WASAPI Earcons mute on/off.
        | MuteEarcons
        // Cycle 22b — copy SessionModel history to clipboard.
        | CopyHistoryToClipboard
        // Cycle 24e — announce active session-log file path.
        | AnnounceSessionLogPath

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
        | ToggleDebugLog -> "ToggleDebugLog"
        | HealthCheck -> "HealthCheck"
        | IncidentMarker -> "IncidentMarker"
        | SwitchToCmd -> "SwitchToCmd"
        | SwitchToPowerShell -> "SwitchToPowerShell"
        | SwitchToClaude -> "SwitchToClaude"
        | MuteEarcons -> "MuteEarcons"
        | CopyHistoryToClipboard -> "CopyHistoryToClipboard"
        | AnnounceSessionLogPath -> "AnnounceSessionLogPath"

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
          { Command = ToggleDebugLog
            Key = Some (Letter 'G')
            Modifiers = Some ctrlShift
            Description = "Toggle FileLogger Information ↔ Debug" }
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
          { Command = MuteEarcons
            Key = Some (Letter 'M')
            Modifiers = Some ctrlShift
            Description = "Mute / unmute WASAPI earcons (Stage 8d.1)" }
          { Command = CopyHistoryToClipboard
            Key = Some (Letter 'Y')
            Modifiers = Some ctrlShift
            Description = "Copy SessionModel history to clipboard (Cycle 22b)" }
          { Command = AnnounceSessionLogPath
            Key = Some (Letter 'S')
            Modifiers = Some ctrlShift
            Description = "Announce the active session-log file path (Cycle 24e)" } ]

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
          ToggleDebugLog
          HealthCheck
          IncidentMarker
          SwitchToCmd
          SwitchToPowerShell
          SwitchToClaude
          MuteEarcons
          CopyHistoryToClipboard
          AnnounceSessionLogPath ]
