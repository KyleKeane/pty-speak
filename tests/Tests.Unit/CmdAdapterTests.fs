module PtySpeak.Tests.Unit.CmdAdapterTests

open Xunit
open Terminal.Shell

// ---------------------------------------------------------------------
// R2 + R4c (ADR 0005/0006, Option B) — cmd OSC-133 `prompt` injection.
// ---------------------------------------------------------------------
//
// `CmdAdapter.IntegrateOsc133` builds the command line that makes
// cmd emit OSC 133 deferred-CommandFinished (;D) + PromptStart (;A)
// + CommandStart (;B). The exact string is NOT locally verifiable —
// there is no cmd.exe in the dev sandbox, and cmd's `prompt`-code +
// quoting semantics can only be confirmed by the maintainer's cmd
// dogfood (ADR 0005 §4 Stage B). These fixtures pin the string so a
// regression fails loudly in CI long before a release+dogfood cycle
// is spent on it, and document WHY each token is shaped the way it
// is.

[<Fact>]
let ``Osc133PromptValue is the deferred-D + A/B FinalTerm template with ST terminators`` () =
    // cmd `prompt` codes: $e=ESC, $p=cwd, $g='>', literal '\'.
    // OSC 133 terminator is ST = ESC '\' = `$e\`. The template
    // emits, in order: ;D (R4c — the PRIOR command's deferred
    // CommandFinished boundary; cmd has no post-exec hook so it
    // rides the head of the NEXT prompt) ;A <visible path> ;B.
    // No ;C (cmd has no post-Enter/pre-output hook). The ;D is
    // BOUNDARY-ONLY — no `;<exitcode>` param: cmd's prompt cannot
    // render %errorlevel% natively (would need clink; out of
    // scope), so there is no %-expansion anywhere → the R2
    // command-line-%-hazard is sidestepped by construction.
    let v = CmdAdapter.Osc133PromptValue
    Assert.Equal("$e]133;D$e\\$e]133;A$e\\$p$g$e]133;B$e\\", v)
    // Structural intent (fails loudly if a future edit breaks the
    // OSC framing even if the literal above is "fixed" to match):
    Assert.StartsWith("$e]133;D$e\\$e]133;A$e\\", v) // deferred ;D then PromptStart
    Assert.EndsWith("$e]133;B$e\\", v)   // CommandStart + ST
    Assert.Contains("$p$g", v)            // visible path + '>'
    Assert.Contains("133;D", v)           // R4c — deferred CommandFinished
    Assert.DoesNotContain("133;C", v)     // no OutputStart
    Assert.DoesNotContain("133;D;", v)    // boundary-only ;D — NO exit code param
    Assert.DoesNotContain("%errorlevel%", v)

[<Fact>]
let ``IntegrateOsc133 wraps the base command line with /K prompt, unquoted`` () =
    // No surrounding quotes: the value is space-free and has no
    // cmd metacharacters, so `/K prompt <value>` sidesteps cmd's
    // nuanced outer-quote-stripping entirely.
    let integrated = CmdAdapter.IntegrateOsc133 "cmd.exe"
    Assert.Equal(
        "cmd.exe /K prompt $e]133;D$e\\$e]133;A$e\\$p$g$e]133;B$e\\",
        integrated)

[<Fact>]
let ``IntegrateOsc133 preserves an arbitrary base path verbatim`` () =
    // The fn is pure (the cmd-only gate lives at the SessionHost /
    // switchToShell call sites, not here); it must not mangle a
    // resolved base path.
    let integrated =
        CmdAdapter.IntegrateOsc133 "C:\\Windows\\System32\\cmd.exe"
    Assert.StartsWith("C:\\Windows\\System32\\cmd.exe /K prompt ", integrated)
    Assert.EndsWith(CmdAdapter.Osc133PromptValue, integrated)
