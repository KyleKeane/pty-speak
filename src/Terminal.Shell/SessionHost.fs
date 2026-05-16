namespace Terminal.Shell

open System
open Microsoft.Extensions.Logging
open Terminal.Core
open Terminal.Pty

/// The single orchestration point (ADR 0006). It will select
/// a shell adapter, own the active session / detector /
/// SpeechCursor state, wire the adapter's `ShellEvent` stream
/// to the session core, and own shell-switch reset — the
/// logic currently smeared across `Terminal.App/Program.fs`.
///
/// R1.1 introduced the type and reserved the seam. R1.3 gives
/// it its first real responsibility: the startup
/// shell-resolution decision (`ResolveStartupShell`), lifted
/// verbatim from `Program.fs` so behaviour is identical (same
/// precedence, same log templates emitted through the
/// passed-in `ILogger`, same fallback). Adapter selection /
/// reader-loop wiring move here in later R1 steps (see
/// `docs/CYCLE-52-R1-ARCHITECTURE-MAP.md` §"Recommended R1
/// commit order").
type SessionHost private () =

    /// Resolve the shell to spawn at startup. Behaviour-
    /// identical extraction of the former `Program.fs`
    /// `resolveStartupShell` closure (ADR 0006 — the single
    /// orchestration point owns shell selection).
    ///
    /// Precedence (highest → lowest): `[startup] default_shell`
    /// TOML → `PTYSPEAK_SHELL` env var → cmd built-in default.
    /// Use case: maintainer has `PTYSPEAK_SHELL=claude` set
    /// from prior testing + wants cmd as durable default
    /// without manipulating env vars; `[startup]
    /// default_shell = "cmd"` wins over the env var.
    ///
    /// `log` is the caller's existing logger instance, so the
    /// emitted category + templates are byte-identical to the
    /// pre-R1.3 output (the diagnostic bundle greps these).
    static member ResolveStartupShell(config: Config.Config, log: ILogger) : ShellRegistry.Shell * string =
        let envVar = Environment.GetEnvironmentVariable("PTYSPEAK_SHELL")
        // Distinguish "unset" from "set to garbage" so the
        // log line is actionable. `null` / empty / whitespace
        // is the common case (no env var set); a non-empty
        // unrecognised value is a typo or stale config and
        // earns a warning. Extracted to a helper so the
        // arm body of `parseEnvVar`'s `None` case stays a
        // single expression — F# 9 + `TreatWarningsAsErrors`
        // can be brittle about sequence-in-match-arm
        // shapes, and the helper sidesteps that risk.
        let logIfUnrecognised () : unit =
            match envVar with
            | null -> ()
            | v when System.String.IsNullOrWhiteSpace(v) -> ()
            | v ->
                log.LogWarning(
                    "PTYSPEAK_SHELL=\"{Value}\" not recognised; falling back to cmd.exe. Recognised values: cmd, claude, powershell, pwsh.",
                    v)
        // Cycle 19 — TOML config takes precedence. When set,
        // we use it directly without consulting the env
        // var. The Config-side parser already validated the
        // value against `knownShellKeys`; here we just map
        // `string → ShellId` via `parseEnvVar`.
        let configShell =
            match Config.resolveDefaultShell config with
            | Some shellKey ->
                match ShellRegistry.parseEnvVar shellKey with
                | Some id ->
                    log.LogInformation(
                        "Startup shell resolved from [startup] default_shell = \"{Shell}\" (overriding PTYSPEAK_SHELL).",
                        shellKey)
                    Some id
                | None ->
                    // Defensive — Config-side parser already
                    // filtered against `knownShellKeys`, so this
                    // arm shouldn't fire. Log + fall through.
                    log.LogWarning(
                        "Config: [startup] default_shell = \"{Shell}\" passed schema validation but parseEnvVar rejected it; falling through to PTYSPEAK_SHELL.",
                        shellKey)
                    None
            | None -> None
        let requested =
            match configShell with
            | Some id -> id
            | None ->
                match ShellRegistry.parseEnvVar envVar with
                | Some id -> id
                | None ->
                    logIfUnrecognised ()
                    ShellRegistry.Cmd
        // `tryFind` only returns None for ids not registered in
        // `builtIns`; both Cmd and Claude are registered, so this
        // is unreachable for the requested id, but the cmd-fallback
        // is shared with the resolution-failure branch below.
        let cmdShell =
            match ShellRegistry.tryFind ShellRegistry.Cmd with
            | Some s -> s
            | None -> failwith "Cmd not registered in ShellRegistry.builtIns"
        let resolvedShell, resolvedCmdLine =
            match ShellRegistry.tryFind requested with
            | None ->
                cmdShell, "cmd.exe"
            | Some shell ->
                match shell.Resolve() with
                | Ok cmdLine ->
                    shell, cmdLine
                | Error reason ->
                    log.LogWarning(
                        "Shell {Shell} unavailable: {Reason}. Falling back to {Fallback}.",
                        shell.DisplayName,
                        reason,
                        cmdShell.DisplayName)
                    let fallbackCmd =
                        match cmdShell.Resolve() with
                        | Ok c -> c
                        | Error _ -> "cmd.exe"
                    cmdShell, fallbackCmd
        // R2 + R4c (ADR 0005/0006, Option B) — cmd OSC-133
        // prompt injection (deferred `;D` + `;A`/`;B`; R4c
        // added the boundary-only deferred CommandFinished).
        // Applied once at the single orchestration return so
        // all three resolution outcomes (registered-miss /
        // resolved-cmd / fallback-to-cmd) get it, and non-cmd
        // shells (claude / PowerShell) are byte-identical. The
        // cmd transport adapter owns the injection (ADR 0006);
        // SessionHost only gates it on the resolved ShellId.
        let integrated =
            (SessionHost.Osc133IntegratorFor resolvedShell.Id)
                resolvedCmdLine
        // Behaviour-identical: the cmd-specific log fires
        // exactly when it did pre-R5a (cmd only). R5b adds a
        // distinct PowerShell injection log alongside its
        // `Osc133IntegratorFor` arm, so this cmd line stays
        // stable + greppable in the diagnostic bundle.
        if resolvedShell.Id = ShellRegistry.Cmd then
            log.LogInformation(
                "R2 cmd OSC-133 prompt injection applied (startup). Base={Base} Integrated={Integrated}",
                resolvedCmdLine,
                integrated)
        resolvedShell, integrated

    /// R5a (ADR 0006 / [`docs/CYCLE-52-R5-PLAYBOOK.md`]) — the
    /// shell-adapter SELECTION seam. The ONLY shell-specific
    /// transport behaviour wired today is the OSC-133
    /// command-line injection: cmd wraps via
    /// `CmdAdapter.IntegrateOsc133`; every other shell is
    /// identity — **byte-identical to the pre-R5a `else`
    /// branch** at both spawn sites (startup
    /// `ResolveStartupShell` + `Program.fs` `switchToShell`).
    /// Centralised at the single orchestration point so the
    /// per-`ShellId` dispatch lives in ONE place and **R5b
    /// adds PowerShell in exactly this one arm**.
    ///
    /// Deliberately NOT the full `IShellAdapter` (Spawn /
    /// WriteInput / byte→`ShellEvent` Translate): spawn stays
    /// `ConPtyHost.start`-direct, input `host.WriteBytes`-
    /// direct, and the single shared VT parser is NOT reset
    /// across shell switches (all unchanged — behaviour-
    /// identical, the R1 discipline). Broadening this toward
    /// the full `IShellAdapter` contract is the R6 target;
    /// rationale + the recon that corrected the R5a scope are
    /// in the playbook.
    static member Osc133IntegratorFor
            (shellId: ShellRegistry.ShellId)
            : string -> string
            =
        match shellId with
        | ShellRegistry.Cmd ->
            (fun cmdLine -> CmdAdapter.IntegrateOsc133 cmdLine)
        | _ ->
            (fun cmdLine -> cmdLine)

    /// Placeholder factory. Real adapter selection + spawn
    /// wiring lands in a subsequent R1 step; the constructor
    /// is private and the factory `internal` so no caller can
    /// take a dependency on an unfinished contract before
    /// then.
    static member internal Create() : SessionHost =
        SessionHost()
