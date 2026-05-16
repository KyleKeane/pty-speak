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

    /// R2 + R4c (ADR 0005/0006) — the cmd `prompt` template
    /// that emits OSC 133 shell-integration markers (Option B:
    /// prompt-string injection; the cmd transport adapter owns
    /// the injection per ADR 0006).
    ///
    /// cmd renders this template at every prompt. The leading
    /// `;D` (R4c) is the **deferred CommandFinished boundary**:
    /// cmd has no post-exec hook, so the prior command's "I am
    /// finished" marker is emitted at the *head of the next
    /// prompt* (the standard Windows-Terminal cmd technique).
    /// Then `;A` (PromptStart) before the visible path and
    /// `;B` (CommandStart) after it. cmd still has no hook to
    /// emit OutputStart (`;C`) in the gap between Enter and the
    /// command's output, so `SessionModel.extractIOCell`'s
    /// CommandStart arm anchors the command/output split at
    /// the `;B` marker — ADR 0005 §3's "implicit C", realised
    /// consumer-side per the maintainer's 2026-05-16 decision.
    ///
    /// **No exit code on `;D`.** This is a *boundary-only*
    /// CommandFinished — `ShellEvent.CommandFinished None` for
    /// cmd. cmd's `prompt`/`PROMPT` only expands `$`-metacodes;
    /// there is **no native cmd mechanism** to render the
    /// just-finished command's `%errorlevel%` per-prompt (it
    /// would need clink or a per-command doskey wrapper — a
    /// third-party / fragile dependency in the spawn seam,
    /// explicitly out of scope, maintainer decision
    /// 2026-05-16). The *boundary* is what R6 per-line
    /// streaming needs (output-region start `;B` → end `;D`);
    /// the exit code is a documented OS-level cmd limitation,
    /// not ours. PowerShell (R5) supplies `Some <code>` via
    /// `$LASTEXITCODE`; `ShellEvent.CommandFinished of int
    /// option` was designed for exactly this asymmetry.
    ///
    /// cmd `prompt` codes: `$e` = ESC (Win10+), `$p` = cwd,
    /// `$g` = `>`, literal `\` = backslash. The OSC 133
    /// terminator is ST = ESC `\` = `$e\` (the VT parser
    /// accepts ST as well as BEL — `StateMachine.fs`). The
    /// `\\` pairs in the F# literal are single backslashes at
    /// runtime. No `%`-expansion appears anywhere in the
    /// template, so the R2 command-line-`%`-hazard the prior
    /// deferral note named is sidestepped by construction.
    static member Osc133PromptValue : string =
        "$e]133;D$e\\$e]133;A$e\\$p$g$e]133;B$e\\"

    /// R2 + R4c — wrap a resolved cmd command line with the
    /// OSC 133 `prompt` injection. No surrounding quotes: the
    /// value is space-free and contains no cmd metacharacters
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
