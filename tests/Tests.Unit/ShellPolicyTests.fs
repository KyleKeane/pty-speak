module PtySpeak.Tests.Unit.ShellPolicyTests

open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 45f Commit 1 — ShellPolicy pure-function tests.
// ---------------------------------------------------------------------
//
// Pin the policy table's contract:
//
//   * defaults exists for cmd / powershell / claude
//   * each default row matches today's hardcoded behaviour
//     (TupleFinalOnly + Suppress) so Commit 2's wiring is a
//     no-regression change
//   * forShell maps known keys to their defaults
//   * forShell on unknown shell synthesises a cmd-shaped record
//     with the requested ShellKey preserved
//   * trimPromptPath handles each PromptPathMode, edge cases
//     (empty / whitespace / root-only / no-separator), and the
//     common shell prompt shapes (cmd, PowerShell, bash)

// ---- defaults table -------------------------------------------------

[<Fact>]
let ``defaults contains cmd / powershell / claude entries`` () =
    Assert.True(ShellPolicy.defaults.ContainsKey "cmd")
    Assert.True(ShellPolicy.defaults.ContainsKey "powershell")
    Assert.True(ShellPolicy.defaults.ContainsKey "claude")

[<Fact>]
let ``every default row uses TupleFinalOnly + Suppress`` () =
    for kv in ShellPolicy.defaults do
        Assert.Equal(ShellPolicy.TupleFinalOnly, kv.Value.Streaming)
        Assert.Equal(ShellPolicy.Suppress, kv.Value.PromptPath)

[<Fact>]
let ``cmd default has prompt regex + stability`` () =
    let cmd = ShellPolicy.defaults.["cmd"]
    Assert.True(cmd.PromptRegex.IsSome)
    Assert.True(cmd.PromptStabilityMs.IsSome)
    Assert.False(cmd.SelectionEnabled)

[<Fact>]
let ``claude default enables selection detector`` () =
    let claude = ShellPolicy.defaults.["claude"]
    Assert.True(claude.SelectionEnabled)

[<Fact>]
let ``cmd + powershell default IdleFlushMs is Some 350`` () =
    // Cycle 47 follow-up — idle-flush is enabled by default
    // for cmd / PowerShell at a 350 ms threshold so intra-
    // script `set /p` prompts speak before the user has to
    // guess what's being asked.
    Assert.Equal(Some 350, ShellPolicy.defaults.["cmd"].IdleFlushMs)
    Assert.Equal(Some 350, ShellPolicy.defaults.["powershell"].IdleFlushMs)

[<Fact>]
let ``claude default IdleFlushMs is None`` () =
    // Claude streams per-token frequently enough that the
    // idle-flush threshold rarely triggers, and any flush
    // would partially overlap a per-token `LineByLine`
    // announce. Disabled by default.
    Assert.Equal(None, ShellPolicy.defaults.["claude"].IdleFlushMs)

// ---- forShell -------------------------------------------------------

[<Fact>]
let ``forShell on a known key returns the default row`` () =
    let cmd = ShellPolicy.forShell "cmd"
    Assert.Equal("cmd", cmd.ShellKey)
    Assert.Equal(ShellPolicy.TupleFinalOnly, cmd.Streaming)

[<Fact>]
let ``forShell on an unknown key returns cmd shape with the requested key`` () =
    let bash = ShellPolicy.forShell "bash"
    Assert.Equal("bash", bash.ShellKey)
    // Fields mirror the cmd defaults.
    let cmd = ShellPolicy.defaults.["cmd"]
    Assert.Equal(cmd.Streaming, bash.Streaming)
    Assert.Equal(cmd.PromptPath, bash.PromptPath)
    Assert.Equal(cmd.SelectionEnabled, bash.SelectionEnabled)

// ---- trimPromptPath: Suppress / Full --------------------------------

[<Fact>]
let ``Suppress returns None for any non-empty text`` () =
    Assert.Equal(None, ShellPolicy.trimPromptPath ShellPolicy.Suppress "C:\\>")
    Assert.Equal(None, ShellPolicy.trimPromptPath ShellPolicy.Suppress "claude>")

[<Fact>]
let ``Full returns Some text verbatim for non-empty input`` () =
    Assert.Equal(
        Some "C:\\Users\\Kyle>",
        ShellPolicy.trimPromptPath ShellPolicy.Full "C:\\Users\\Kyle>")

[<Fact>]
let ``Empty or whitespace input returns None under every mode`` () =
    for mode in [ ShellPolicy.Suppress; ShellPolicy.FinalDirOnly; ShellPolicy.Full ] do
        Assert.Equal(None, ShellPolicy.trimPromptPath mode "")
        Assert.Equal(None, ShellPolicy.trimPromptPath mode "   ")

// ---- trimPromptPath: FinalDirOnly ----------------------------------

[<Fact>]
let ``FinalDirOnly trims cmd Windows path to last directory plus delim`` () =
    Assert.Equal(
        Some "Local>",
        ShellPolicy.trimPromptPath
            ShellPolicy.FinalDirOnly
            "C:\\Users\\Kyle\\AppData\\Local\\>")

[<Fact>]
let ``FinalDirOnly trims cmd path without trailing backslash`` () =
    Assert.Equal(
        Some "Documents>",
        ShellPolicy.trimPromptPath
            ShellPolicy.FinalDirOnly
            "C:\\Users\\Kyle\\Documents>")

[<Fact>]
let ``FinalDirOnly trims PowerShell PS-prefixed path`` () =
    Assert.Equal(
        Some "Kyle>",
        ShellPolicy.trimPromptPath
            ShellPolicy.FinalDirOnly
            "PS C:\\Users\\Kyle>")

[<Fact>]
let ``FinalDirOnly trims bash-style path with dollar sign`` () =
    Assert.Equal(
        Some "repo$",
        ShellPolicy.trimPromptPath
            ShellPolicy.FinalDirOnly
            "~/repo$")

[<Fact>]
let ``FinalDirOnly preserves root-only prompts verbatim`` () =
    // `C:\>` has no inner directory to extract — return as-is.
    Assert.Equal(
        Some "C:\\>",
        ShellPolicy.trimPromptPath ShellPolicy.FinalDirOnly "C:\\>")

[<Fact>]
let ``FinalDirOnly preserves short prompts without path separators`` () =
    Assert.Equal(
        Some "claude>",
        ShellPolicy.trimPromptPath ShellPolicy.FinalDirOnly "claude>")
    Assert.Equal(
        Some ">>>",
        ShellPolicy.trimPromptPath ShellPolicy.FinalDirOnly ">>>")

[<Fact>]
let ``FinalDirOnly handles trailing whitespace`` () =
    Assert.Equal(
        Some "Local>",
        ShellPolicy.trimPromptPath
            ShellPolicy.FinalDirOnly
            "C:\\Users\\Kyle\\Local>   ")
