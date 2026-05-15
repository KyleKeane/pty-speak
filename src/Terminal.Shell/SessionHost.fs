namespace Terminal.Shell

/// The single orchestration point (ADR 0006). It will select
/// a shell adapter, own the active session / detector /
/// SpeechCursor state, wire the adapter's `ShellEvent` stream
/// to the session core, and own shell-switch reset — the
/// logic currently smeared across `Terminal.App/Program.fs`.
///
/// R1.1 introduces the type and reserves the seam only. The
/// wiring is moved out of `Program.fs` in later R1 steps (see
/// `docs/CYCLE-52-R1-ARCHITECTURE-MAP.md` §"Recommended R1
/// commit order", steps 3–4). Until then this is
/// intentionally inert, so R1.1 stays a pure,
/// zero-behaviour-change addition: nothing constructs or
/// depends on it yet.
type SessionHost private () =

    /// Placeholder factory. Real adapter selection + spawn
    /// wiring lands in a subsequent R1 step; the constructor
    /// is private and the factory `internal` so no caller can
    /// take a dependency on an unfinished contract before
    /// then.
    static member internal Create() : SessionHost =
        SessionHost()
