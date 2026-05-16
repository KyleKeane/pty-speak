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

    /// R2 (ADR 0005/0006) — the cmd `prompt` template that
    /// emits OSC 133 shell-integration markers (Option B:
    /// prompt-string injection; the cmd transport adapter owns
    /// the injection per ADR 0006).
    ///
    /// cmd renders this template at every prompt. It emits
    /// OSC 133 PromptStart (`;A`) before the visible path and
    /// CommandStart (`;B`) after it. cmd has no hook to emit
    /// OutputStart (`;C`) in the gap between Enter and the
    /// command's output, so `SessionModel.extractIOCell`'s R2
    /// CommandStart arm anchors the command/output split at
    /// the `;B` marker — ADR 0005 §3's "implicit C", realised
    /// consumer-side per the maintainer's 2026-05-16 decision.
    ///
    /// cmd `prompt` codes: `$e` = ESC (Win10+), `$p` = cwd,
    /// `$g` = `>`, literal `\` = backslash. The OSC 133
    /// terminator is ST = ESC `\` = `$e\` (the VT parser
    /// accepts ST as well as BEL — `StateMachine.fs`). The
    /// `\\` pairs in the F# literal are single backslashes at
    /// runtime.
    ///
    /// Exit-code (`;D`) is deferred: its live `%errorlevel%`
    /// cannot defer through cmd's command-line `%`-expansion
    /// without fragile escaping, and A/B alone is sufficient
    /// for the command/output split (the R2 dogfood gate).
    static member Osc133PromptValue : string =
        "$e]133;A$e\\$p$g$e]133;B$e\\"

    /// R2 — wrap a resolved cmd command line with the OSC 133
    /// `prompt` injection. No surrounding quotes: the value is
    /// space-free and contains no cmd metacharacters
    /// (`&|<>()@^`), so an unquoted `/K prompt <value>`
    /// sidesteps cmd's nuanced outer-quote-stripping entirely.
    ///
    /// The exact produced string is locally unverifiable (no
    /// cmd in the dev sandbox); it is pinned by
    /// `CmdAdapterTests` and validated end-to-end by the cmd
    /// dogfood (ADR 0005 §4 Stage B). If the dogfood shows the
    /// template needs adjustment it is a contained one-line
    /// change here — nothing downstream depends on *how* cmd
    /// was told to emit OSC 133, only *that* it does.
    static member IntegrateOsc133(baseCommandLine: string) : string =
        sprintf
            "%s /K prompt %s"
            baseCommandLine
            CmdAdapter.Osc133PromptValue
