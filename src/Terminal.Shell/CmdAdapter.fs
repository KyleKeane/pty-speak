namespace Terminal.Shell

open Terminal.Core
open Terminal.Parser

/// The cmd-family transport adapter — R1 shape (R1.4, ADR
/// 0006). It owns the VT parser and exposes `Translate` as a
/// verbatim wrapper over `Parser.feedArray`, so the reader
/// loop goes through the adapter seam with **zero behaviour
/// change**: identical `VtEvent[]` output, same single
/// parser instance for the process lifetime (the parser is
/// created once and is deliberately not reset across shell
/// switches, matching pre-R1.4 behaviour — see the
/// `switchToShell` "Parser state is NOT reset" note in
/// `Program.fs`).
///
/// `Translate` here is `byte[] -> VtEvent[]` (the live
/// pipeline's event type). The `ShellEvent` boundary in
/// `ShellEvent` / `IShellAdapter` is deliberately NOT used
/// at R1 — it becomes meaningful in R2, when OSC-133 gives
/// the adapter real prompt / command / output events to
/// emit. R1.4 only establishes the seam.
type CmdAdapter() =

    let parser = Parser.create ()

    /// Verbatim wrapper over `Parser.feedArray` — byte-for-
    /// byte identical output to the former inline
    /// `Parser.feedArray parser chunk` call in
    /// `startReaderLoop`.
    member _.Translate(bytes: byte[]) : VtEvent[] =
        Parser.feedArray parser bytes
