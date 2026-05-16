namespace Terminal.Shell

/// R5b (ADR 0005 §3 / 0006 R5 / `docs/CYCLE-52-R5-PLAYBOOK.md`)
/// — the PowerShell transport adapter's OSC-133 injection.
/// Sibling of `CmdAdapter`; selected by `ShellId` through the
/// R5a `SessionHost.Osc133IntegratorFor` seam (PowerShell arm).
///
/// **Mechanism.** Windows PowerShell renders its prompt by
/// calling a `prompt` function. We inject a `prompt` function
/// that emits OSC 133 around the prompt, mirroring cmd's
/// `;D;<code>` (CommandFinished — *with a real exit code*, the
/// asymmetry cmd lacks: cmd is `CommandFinished None`,
/// PowerShell is `Some $LASTEXITCODE`) → `;A` (PromptStart) →
/// visible path `>` → `;B` (CommandStart). Same `;A`/`;B`
/// framing as cmd, so the consumer hits the *same*
/// `extractIOCell` `CmdOscAB` arm with no consumer change;
/// `Osc133.tryParse` already decodes `;D;<int>`. PowerShell's
/// `prompt` runs post-exec (between command completion and the
/// next input) — the same stream position as cmd's deferred
/// `;D` — so `$LASTEXITCODE` is fresh and emitted immediately
/// (not deferred). The leading prompt emits `;D;0` with no
/// prior command → SessionModel's `None, CommandFinished` arm
/// ignores it + the R4c stray-`;D` gate suppresses the marker,
/// exactly as for cmd's leading `;D`.
///
/// **Why `-EncodedCommand`, not `-Command "…"`.** The script
/// has spaces / braces / quotes / `$`, so it cannot be a
/// space-free unquoted token like cmd's `prompt` value. Rather
/// than fight `CreateProcess` + PowerShell command-line
/// quoting, the script is passed as base64 (UTF-16LE) via
/// `-NoExit -EncodedCommand`: the base64 alphabet has no
/// spaces, quotes, or shell metacharacters, so the produced
/// command line is quoting-safe by construction — the same
/// robustness property cmd's space-free value has. `-NoExit`
/// keeps the session interactive with the `prompt` function
/// defined in the session scope.
///
/// **Screen-reader / PSReadLine.** PowerShell auto-disables
/// PSReadLine when it detects a screen reader. This is
/// *deliberately not relied upon*: the `prompt` function is a
/// core host hook, independent of PSReadLine, so `;A`/`;B`/
/// `;D;<code>` emit regardless. We do **not** attempt `;C`
/// (OutputStart) — that would need a `PSConsoleHostReadLine`/
/// PSReadLine hook which the screen-reader path disables; the
/// playbook's #1 R5 risk. PowerShell therefore routes through
/// the same `CmdOscAB` arm as cmd (no `;C`), with the bonus of
/// a real exit code. Whether `;C` is ever reachable is the R5
/// dogfood question; this adapter is the screen-reader-safe
/// baseline.
///
/// **Locally unverifiable** (no PowerShell in the dev
/// sandbox), exactly like cmd's `Osc133PromptValue` was at R2:
/// the script + the integrated command line are pinned by
/// `PowerShellAdapterTests`, and validated end-to-end by the
/// maintainer's NVDA dogfood (matrix `52-R5b`). If the dogfood
/// shows the script needs adjustment it is a contained
/// change to `Osc133InitScript` here — nothing downstream
/// depends on *how* PowerShell was told to emit OSC 133, only
/// *that* it does (the same contract cmd's value has).
type PowerShellAdapter() =

    /// The Windows-PowerShell-5.1-safe `prompt` function.
    /// **F# triple-quoted on purpose** — the script contains
    /// PowerShell `"` string delimiters and literal `\` (the
    /// OSC ST = ESC `\`); a triple-quoted F# literal takes them
    /// verbatim with zero escaping (the cmd `Osc133PromptValue`
    /// `\\`-escaping foot-gun does not apply). The script has
    /// no `"""` substring.
    ///
    /// PowerShell notes: `[char]27` = ESC (Windows PowerShell
    /// 5.1 has no `` `e ``; `powershell.exe` is 5.1, and `pwsh`
    /// is aliased to the same `ShellId`). `$LASTEXITCODE` is
    /// `$null` before the first native command, so it is
    /// coalesced to `0`. `$($PWD.Path)` is the current
    /// location. The visible prompt is `<path>>` — cmd-shaped
    /// (no `PS ` prefix, no trailing space) so the existing
    /// prompt-path-verbosity logic treats PowerShell like cmd;
    /// the exact visible text is a dogfood-tunable knob.
    static member Osc133InitScript : string =
        """function prompt { $e=[char]27; $c=if($null -eq $LASTEXITCODE){0}else{$LASTEXITCODE}; "$e]133;D;$c$e\$e]133;A$e\$($PWD.Path)>$e]133;B$e\" }"""

    /// Wrap a resolved PowerShell command line (e.g.
    /// `"powershell.exe"`) with `-NoExit -EncodedCommand
    /// <base64>`, where `<base64>` is `Osc133InitScript`
    /// encoded UTF-16LE then Base64 — exactly what
    /// `-EncodedCommand` expects. The base64 token is
    /// space/quote/metacharacter-free, so the produced command
    /// line needs no quoting (the cmd-equivalent robustness).
    static member IntegrateOsc133 (baseCommandLine: string) : string =
        let encoded =
            System.Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(
                    PowerShellAdapter.Osc133InitScript))
        sprintf "%s -NoExit -EncodedCommand %s" baseCommandLine encoded
