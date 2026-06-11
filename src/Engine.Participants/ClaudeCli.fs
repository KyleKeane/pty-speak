namespace Engine.Participants

open System
open System.Diagnostics
open Engine.Core

/// RELAUNCH-SPEC §4.3 / §12 / ADR 0011 E4 — the first concrete
/// participant: the local Claude Code CLI over its structured
/// stream-json interface. Per-turn invocation (`-p <prompt>`)
/// with `--resume <sessionId>` continuity; the CLI owns its own
/// session persistence.
///
/// Boundary discipline (ADR 0006 shape): this module owns ONLY
/// process spawn + the stdout line pump. Parsing is
/// `Engine.Core.ClaudeStreamJson` (pure, tested there); the
/// line-pump fold is `pumpLines` (pure over a reader function,
/// tested in `ClaudeCliTests`). A participant's reply is INPUT
/// to the engine (§0.2 boundary rule) — `onEvent` hands each
/// typed event to the host, which routes it through ingest.
module ClaudeCli =

    /// Participant configuration. `ExecutablePath` defaults to
    /// "claude" (PATH resolution); on Windows the npm shim is
    /// "claude.cmd" — `Process.UseShellExecute=false` still
    /// resolves .cmd via CreateProcess only when the extension
    /// is explicit, so the host may pass the full shim name.
    type Config =
        { ExecutablePath: string
          WorkingDirectory: string option }

    let defaultConfig : Config =
        { ExecutablePath = "claude"
          WorkingDirectory = None }

    /// One finished turn, transport-level. Stream-level errors
    /// arrive as typed events through `onEvent`; this is the
    /// process-level outcome.
    type TurnOutcome =
        { ExitCode: int
          StdErr: string }

    /// Pure: the per-turn CLI argument list (ADR 0011 E4).
    /// `--verbose` is required by the CLI when combining `-p`
    /// with stream-json output.
    let buildArguments
            (prompt: string)
            (resumeSessionId: string option)
            : string list =
        [ "-p"
          prompt
          "--output-format"
          "stream-json"
          "--verbose" ]
        @ (match resumeSessionId with
           | Some sid -> [ "--resume"; sid ]
           | None -> [])

    /// Pure fold over a line source: read until the source is
    /// exhausted (`None`), handing every parsed event to
    /// `onEvent`. Factored out of the process layer so the pump
    /// is testable without spawning anything.
    let pumpLines
            (readLine: unit -> string option)
            (onEvent: AgentEvent.AgentEvent -> unit)
            : unit =
        let rec go () =
            match readLine () with
            | None -> ()
            | Some line ->
                dispatch line
                go ()
        and dispatch (line: string) =
            match ClaudeStreamJson.parseLine line with
            | Some ev -> onEvent ev
            | None -> ()
        go ()

    /// Run one turn against the local CLI. Blocking — the host
    /// runs it off the interaction thread. Events are delivered
    /// on the calling thread, in stream order.
    let runTurn
            (config: Config)
            (prompt: string)
            (resumeSessionId: string option)
            (onEvent: AgentEvent.AgentEvent -> unit)
            : Result<TurnOutcome, string> =
        let psi = ProcessStartInfo(FileName = config.ExecutablePath)
        buildArguments prompt resumeSessionId
        |> List.iter psi.ArgumentList.Add
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        match config.WorkingDirectory with
        | Some dir -> psi.WorkingDirectory <- dir
        | None -> ()
        try
            match Process.Start(psi) with
            | null ->
                Error "the participant process did not start"
            | proc ->
                use proc = proc
                // Drain stderr concurrently — a full stderr
                // pipe would deadlock the stdout pump.
                let stdErr = Text.StringBuilder()
                proc.ErrorDataReceived.Add(fun args ->
                    match args.Data with
                    | null -> ()
                    | data -> stdErr.AppendLine(data) |> ignore)
                proc.BeginErrorReadLine()
                let readLine () : string option =
                    match proc.StandardOutput.ReadLine() with
                    | null -> None
                    | line -> Some line
                pumpLines readLine onEvent
                proc.WaitForExit()
                Ok { ExitCode = proc.ExitCode
                     StdErr = stdErr.ToString() }
        with ex ->
            Error ex.Message
