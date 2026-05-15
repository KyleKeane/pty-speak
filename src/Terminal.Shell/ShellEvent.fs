namespace Terminal.Shell

/// The transport↔core boundary type (ADR 0006). A
/// `ShellAdapter` translates raw PTY bytes into a stream of
/// these; the session core consumes only `ShellEvent` —
/// never raw bytes, screen coordinates, or shell identity.
///
/// R1 introduces the type. The cmd adapter's first
/// implementation (a later R1 step) wraps the *existing*
/// parser + heuristic path verbatim and emits `Raw` for
/// everything, so behaviour is identical until R2 starts
/// emitting precise `PromptStart` / `CommandStart` /
/// `OutputChunk` / `CommandFinished` from OSC 133.
type ShellEvent =
    /// A new prompt is about to be drawn (OSC 133 `;A`, or
    /// the heuristic equivalent).
    | PromptStart
    /// The user submitted a command (OSC 133 `;B`).
    | CommandStart
    /// A chunk of command output (the OSC 133 `;C`…`;D`
    /// region).
    | OutputChunk of bytes: byte[]
    /// The command finished; carries the exit code when the
    /// shell reports one (OSC 133 `;D` / `;D;<code>`).
    | CommandFinished of exitCode: int option
    /// Bytes the adapter did not classify — passed through
    /// verbatim so the core's existing parser/screen path
    /// still sees them. This is the arm that preserves R1
    /// behaviour-identity.
    | Raw of raw: byte[]
