module PtySpeak.Tests.Unit.PowerShellAdapterTests

open Xunit
open Terminal.Shell

// ---------------------------------------------------------------------
// R5b (ADR 0005 §3 / 0006 R5) — PowerShell OSC-133 injection.
//
// Like cmd's `Osc133PromptValue`, the PowerShell `prompt`-function
// script + the integrated command line are NOT locally verifiable
// (no PowerShell in the dev sandbox) — they are pinned here so a
// regression fails loudly in CI before a release+dogfood cycle is
// spent on it, and validated end-to-end by the maintainer's NVDA
// dogfood (matrix `52-R5b`). The script is the dogfood-tunable knob.
// ---------------------------------------------------------------------

[<Fact>]
let ``Osc133InitScript is the pinned WinPS-5.1 prompt function`` () =
    // Triple-quoted expectation — verbatim, zero escaping (the
    // script has PowerShell `"` + literal `\` ST bytes).
    let expected =
        """function prompt { $e=[char]27; $c=if($null -eq $LASTEXITCODE){0}else{$LASTEXITCODE}; "$e]133;D;$c$e\$e]133;A$e\$($PWD.Path)>$e]133;B$e\" }"""
    Assert.Equal(expected, PowerShellAdapter.Osc133InitScript)
    // Structural intent (fails loudly even if the literal above
    // is "fixed" to match a broken edit):
    let s = PowerShellAdapter.Osc133InitScript
    Assert.StartsWith("function prompt {", s)
    Assert.Contains("[char]27", s)              // WinPS 5.1 ESC
    Assert.Contains("$LASTEXITCODE", s)         // real exit code
    Assert.Contains("]133;D;", s)               // CommandFinished + code
    Assert.Contains("]133;A", s)                // PromptStart
    Assert.Contains("]133;B", s)                // CommandStart
    Assert.Contains("$($PWD.Path)", s)          // visible path
    Assert.DoesNotContain("]133;C", s)          // no OutputStart (screen-reader-safe baseline)

[<Fact>]
let ``IntegrateOsc133 wraps with -NoExit -EncodedCommand, quoting-safe`` () =
    let integrated = PowerShellAdapter.IntegrateOsc133 "powershell.exe"
    Assert.StartsWith("powershell.exe -NoExit -EncodedCommand ", integrated)
    // The encoded token must be quoting-safe (base64 alphabet —
    // no space / quote / cmd metacharacter), the cmd-equivalent
    // robustness property.
    let marker = "-NoExit -EncodedCommand "
    let token =
        integrated.Substring(integrated.IndexOf(marker) + marker.Length)
    Assert.DoesNotContain(" ", token)
    Assert.DoesNotContain("\"", token)
    // -EncodedCommand expects base64 of UTF-16LE; it must
    // round-trip back to exactly the init script.
    let decoded =
        System.Text.Encoding.Unicode.GetString(
            System.Convert.FromBase64String(token))
    Assert.Equal(PowerShellAdapter.Osc133InitScript, decoded)

[<Fact>]
let ``IntegrateOsc133 preserves an arbitrary base path verbatim`` () =
    let integrated =
        PowerShellAdapter.IntegrateOsc133
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
    Assert.StartsWith(
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoExit -EncodedCommand ",
        integrated)
