namespace Terminal.Pty

open System
open System.Diagnostics
open System.IO

/// Stage 7 PR-B — extensible registry of shells pty-speak can spawn
/// as the ConPTY child. Today's surface is `Cmd` (Windows Command
/// Prompt) and `Claude` (the maintainer's primary target workload —
/// Claude Code). The shape is deliberately designed so future shells
/// (PowerShell, WSL, Python REPL, others) plug in without touching
/// the spawn path.
///
/// Each `Shell` is identified by a `ShellId` discriminated-union case
/// and carries a `Resolve` closure that returns either the command
/// line to pass to ConPTY's `CreateProcess` (`PtyConfig.CommandLine`)
/// or a human-readable reason it couldn't be located. Resolution is
/// run at call time (not cached at the registry level) so tests can
/// inject deterministic registries; production callers (`Program.fs`'s
/// startup compose path) call `Resolve` once at session start.
///
/// **No dependencies on logging.** The registry is pure data + a thin
/// `where.exe` wrapper. The orchestration (warning on unrecognised
/// `PTYSPEAK_SHELL`, fallback to default on resolution failure) lives
/// in `src/Terminal.App/Program.fs compose ()` where the `ILogger` is
/// already in scope. Keeping `Terminal.Pty` logger-free preserves the
/// dependency boundary that's held since Stage 1.
module ShellRegistry =

    /// Discriminated-union identity of every shell pty-speak knows
    /// how to launch. Add cases here to extend the registry; each
    /// case needs a registration in `builtIns` below.
    ///
    /// Stage 7-followup PR-J added `PowerShell` (Windows
    /// PowerShell, `powershell.exe`) as a third built-in
    /// alongside `Cmd` and `Claude`. Diagnostic value: PowerShell
    /// is always installed on Windows, has zero auth or
    /// terminal-capability detection, and produces visible banner
    /// + prompt output within milliseconds of launch — making it
    /// an ideal control shell for isolating shell-switch bugs
    /// from claude.exe-specific issues.
    ///
    /// Future cases (Phase 2 territory): `Wsl of distro: string`,
    /// `Python`, `Node`, `Bash`, etc. PowerShell-Core (`pwsh.exe`,
    /// the PowerShell 7+ rename) is intentionally NOT a separate
    /// case — it's an optional install; `powershell.exe` is the
    /// always-available baseline. A Phase 2 user-settings TOML
    /// can let the `PowerShell` resolver prefer `pwsh.exe` when
    /// present.
    type ShellId =
        | Cmd
        | Claude
        | PowerShell

    /// The runtime descriptor for a shell. `Resolve` is called at
    /// most once per session by the composition root; tests inject
    /// synthetic descriptors via `tryFindIn` to bypass the
    /// production registry entirely.
    type Shell =
        { Id: ShellId
          /// Display name surfaced in user-visible log lines and
          /// (eventually) NVDA announcements when PR-C's
          /// hot-switch hotkeys land.
          DisplayName: string
          /// Lazy resolver. `Ok cmdLine` is the value to pass as
          /// `PtyConfig.CommandLine`; `Error reason` is a
          /// short, AnnounceSanitiser-safe explanation suitable
          /// for `log.LogWarning` interpolation.
          Resolve: unit -> Result<string, string> }

    /// `where.exe NAME` resolution helper. Used by shells that
    /// aren't guaranteed to be on `PATH` (`claude.exe`). Returns
    /// the absolute path of the first hit on stdout, or an
    /// `Error` with a human-readable reason. Two-second timeout
    /// caps the worst case if `where.exe` itself misbehaves.
    ///
    /// Not unit-tested directly (involves `Process.Start`); the
    /// orchestrator in `Program.fs` exercises the real path
    /// during launch and pinned manually via `docs/ACCESSIBILITY-
    /// TESTING.md` Stage 7 row in PR-D.
    let internal whereExe (name: string) : Result<string, string> =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- "where.exe"
            psi.Arguments <- name
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            // CodeQL-clean: Process.Start with explicit
            // ProcessStartInfo (no shell=true) per
            // CONTRIBUTING.md's Process.Start convention.
            //
            // F# 9 nullness: `Process.Start(ProcessStartInfo)` is
            // annotated `Process | null` (the .NET docs document
            // this — Start returns null when no resource is
            // started, e.g. attempting to start a no-op process).
            // Match-on-null and surface a usable Error so the
            // composition root's fallback path runs predictably.
            match Process.Start(psi) with
            | null ->
                Error (sprintf "%s: Process.Start returned null" name)
            | p ->
                use _ = p
                let exited = p.WaitForExit(2000)
                if not exited then
                    try p.Kill() with _ -> ()
                    Error (sprintf "%s: where.exe timed out after 2s" name)
                elif p.ExitCode = 0 then
                    let out = p.StandardOutput.ReadToEnd()
                    let firstLine =
                        out.Split([| '\n'; '\r' |])
                        |> Array.tryFind (fun l -> l.Trim().Length > 0)
                    match firstLine with
                    | Some line -> Ok (line.Trim())
                    | None -> Error (sprintf "%s: where.exe returned no path" name)
                else
                    Error (sprintf "%s: not found on PATH" name)
        with ex ->
            // AnnounceSanitiser is applied at the announcement
            // boundary in Program.fs, not here, so the raw
            // exception message is fine for the Result payload.
            Error (sprintf "%s: %s" name ex.Message)

    /// Built-in shell registry. Order is irrelevant; lookup is by
    /// `ShellId` via `tryFind`. Every entry is a closure that's
    /// safe to call repeatedly (cmd's resolver is constant; Claude's
    /// re-runs `where.exe`, which is acceptable for the at-most-
    /// once-per-session calling pattern).
    let builtIns : Map<ShellId, Shell> =
        Map.ofList
            [ Cmd,
                { Id = Cmd
                  DisplayName = "Command Prompt"
                  Resolve = fun () -> Ok "cmd.exe" }
              Claude,
                { Id = Claude
                  DisplayName = "Claude Code"
                  Resolve = fun () -> whereExe "claude" }
              PowerShell,
                { Id = PowerShell
                  DisplayName = "PowerShell"
                  // Windows PowerShell is always present on
                  // Windows 10+; the bare command name resolves
                  // through the parent's PATH (which our env-scrub
                  // preserves). Not using `where.exe powershell`
                  // because the bare resolution is faster and the
                  // command is guaranteed available.
                  Resolve = fun () -> Ok "powershell.exe" } ]

    /// Look up a shell in a registry. Production callers pass
    /// `builtIns`; tests pass a synthetic Map to inject
    /// deterministic Resolve closures.
    let tryFindIn (registry: Map<ShellId, Shell>) (id: ShellId) : Shell option =
        Map.tryFind id registry

    /// Convenience: look up in the production `builtIns` registry.
    let tryFind (id: ShellId) : Shell option =
        tryFindIn builtIns id

    /// Map a `PTYSPEAK_SHELL` env-var value to a `ShellId`.
    /// Recognised values (case-insensitive after trim): `"cmd"`,
    /// `"claude"`, `"powershell"` / `"pwsh"`. Returns `None` for
    /// unrecognised values so the caller can log a warning + fall
    /// back to the default. `null` and empty/whitespace are
    /// treated as "not set" (also returns `None`); callers
    /// distinguish via the source-side null check.
    ///
    /// `"pwsh"` is an alias for `PowerShell` because the
    /// PowerShell Core 7+ executable is named `pwsh.exe` and
    /// some users set `PTYSPEAK_SHELL=pwsh` thinking that's the
    /// canonical name. Both currently route to
    /// `powershell.exe` (Windows PowerShell, always available);
    /// a Phase 2 user-settings TOML can split this into
    /// "PowerShell prefers pwsh.exe when present" if desired.
    let parseEnvVar (value: string | null) : ShellId option =
        match value with
        | null -> None
        | v ->
            match v.Trim().ToLowerInvariant() with
            | "" -> None
            | "cmd" -> Some Cmd
            | "claude" -> Some Claude
            | "powershell" -> Some PowerShell
            | "pwsh" -> Some PowerShell
            | _ -> None
