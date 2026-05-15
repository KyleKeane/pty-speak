namespace Terminal.Shell

open System

/// Configuration for spawning a shell. Kept minimal in R1 so
/// the contract is stable while the implementation is wired
/// across later R1 steps; fields are added as adapters need
/// them (OSC-133 injection knobs land in R2).
type SpawnConfig =
    { /// The resolved executable command line, passed
      /// verbatim to `CreateProcess` (e.g. `"cmd.exe"`).
      CommandLine: string
      /// Initial PTY width in columns.
      Cols: int
      /// Initial PTY height in rows.
      Rows: int }

/// Why a spawn failed.
type SpawnError =
    | ExecutableNotFound of name: string
    | SpawnFailed of reason: string

/// A live shell process + its PTY, owned by the adapter.
/// Opaque in R1 — the concrete ConPTY handle is wired in
/// behind this in a later R1 step. Disposing it tears down
/// the PTY and child process.
type RunningShell =
    inherit IDisposable
    abstract member ProcessId: int

/// The transport-layer contract (ADR 0006's "ShellAdapter").
/// One implementation per shell family (cmd / PowerShell /
/// claude). An adapter owns spawn, OSC-133 injection, the
/// PTY, and the stateful byte→`ShellEvent` translation.
///
/// `Translate` is stateful per session, so an adapter
/// instance is single-use and, by contract, driven from a
/// single reader loop (not thread-safe). Nothing above this
/// interface knows which shell is running.
type IShellAdapter =
    inherit IDisposable
    /// Spawn the configured shell + its PTY.
    abstract member Spawn: SpawnConfig -> Result<RunningShell, SpawnError>
    /// Translate the next chunk of raw PTY bytes into zero or
    /// more boundary/output events. Stateful across calls.
    abstract member Translate: byte[] -> ShellEvent list
    /// Write user input bytes to the shell's PTY.
    abstract member WriteInput: byte[] -> unit
