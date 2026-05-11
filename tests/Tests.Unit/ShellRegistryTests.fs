module PtySpeak.Tests.Unit.ShellRegistryTests

open Xunit
open Terminal.Pty

// ---------------------------------------------------------------------
// Stage 7 PR-B — ShellRegistry pinning
// ---------------------------------------------------------------------
//
// `ShellRegistry` is the extensibility seam Stage 7's hot-switch-
// hotkey UX (PR-C) and Phase-2's user-settings menu eventually plug
// into. These tests pin two contracts:
//
//   1. `parseEnvVar` — pure mapping from `PTYSPEAK_SHELL` text to
//      `ShellId option`. Recognises "cmd" / "claude" / "powershell" /
//      "pwsh" case-insensitively after trim; returns `None` for null,
//      empty, whitespace, or anything else (so the caller can warn-
//      and-fall-back). PR-J added the PowerShell + pwsh aliases.
//   2. `builtIns` registry contains exactly the cmd + claude +
//      PowerShell entries Stage 7 ships with after PR-J. Adding a
//      shell entry requires updating this assertion (matches the
//      AllowedNames-pin pattern in `EnvBlockTests.fs`).
//
// `whereExe` involves `Process.Start` and isn't unit-tested here —
// the real path is exercised in PR-D's manual NVDA matrix row.
// `tryFindIn` exists specifically to let tests inject synthetic
// registries without touching `builtIns`; we use it below to verify
// the lookup contract.

// ---------------------------------------------------------------------
// parseEnvVar — recognised values
// ---------------------------------------------------------------------

[<Fact>]
let ``parseEnvVar recognises "cmd"`` () =
    Assert.Equal(Some ShellRegistry.Cmd, ShellRegistry.parseEnvVar "cmd")

[<Fact>]
let ``parseEnvVar recognises "claude"`` () =
    Assert.Equal(Some ShellRegistry.Claude, ShellRegistry.parseEnvVar "claude")

[<Fact>]
let ``parseEnvVar recognises "powershell"`` () =
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "powershell")

[<Fact>]
let ``parseEnvVar recognises "pwsh" as PowerShell alias`` () =
    // `pwsh.exe` is the PowerShell Core 7+ executable name. PR-J
    // routes both names to the same `PowerShell` ShellId for now;
    // a Phase 2 user-settings TOML can split them into "prefer
    // pwsh.exe when present" if desired.
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "pwsh")

[<Fact>]
let ``parseEnvVar is case-insensitive`` () =
    Assert.Equal(Some ShellRegistry.Cmd, ShellRegistry.parseEnvVar "CMD")
    Assert.Equal(Some ShellRegistry.Cmd, ShellRegistry.parseEnvVar "Cmd")
    Assert.Equal(Some ShellRegistry.Claude, ShellRegistry.parseEnvVar "CLAUDE")
    Assert.Equal(Some ShellRegistry.Claude, ShellRegistry.parseEnvVar "Claude")
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "PowerShell")
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "POWERSHELL")
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "PWSH")

[<Fact>]
let ``parseEnvVar trims surrounding whitespace`` () =
    Assert.Equal(Some ShellRegistry.Cmd, ShellRegistry.parseEnvVar "  cmd  ")
    Assert.Equal(Some ShellRegistry.Claude, ShellRegistry.parseEnvVar "\tclaude\n")
    Assert.Equal(Some ShellRegistry.PowerShell, ShellRegistry.parseEnvVar "  pwsh  ")

// ---------------------------------------------------------------------
// parseEnvVar — unrecognised values
// ---------------------------------------------------------------------

[<Fact>]
let ``parseEnvVar returns None for null`` () =
    Assert.Equal(None, ShellRegistry.parseEnvVar null)

[<Fact>]
let ``parseEnvVar returns None for empty string`` () =
    Assert.Equal(None, ShellRegistry.parseEnvVar "")

[<Fact>]
let ``parseEnvVar returns None for whitespace`` () =
    Assert.Equal(None, ShellRegistry.parseEnvVar "   ")
    Assert.Equal(None, ShellRegistry.parseEnvVar "\t\n")

[<Fact>]
let ``parseEnvVar returns None for unrecognised values`` () =
    // Values like "wsl" / "bash" / "node" / "garbage" all return
    // None today; future shells would be added by extending
    // `parseEnvVar`'s match arms. PR-J moved "powershell" / "pwsh"
    // out of this list and into the recognised set.
    Assert.Equal(None, ShellRegistry.parseEnvVar "wsl")
    Assert.Equal(None, ShellRegistry.parseEnvVar "bash")
    Assert.Equal(None, ShellRegistry.parseEnvVar "node")
    Assert.Equal(None, ShellRegistry.parseEnvVar "garbage")

[<Fact>]
let ``parseEnvVar does not match substrings (cmd.exe)`` () =
    // "cmd.exe" should NOT match the "cmd" arm — recognised values
    // are short identifiers, not paths. A user typing the full
    // executable name should get None and fall through to the
    // warning log.
    Assert.Equal(None, ShellRegistry.parseEnvVar "cmd.exe")
    Assert.Equal(None, ShellRegistry.parseEnvVar "claude.exe")

// ---------------------------------------------------------------------
// builtIns registry — pin shell set
// ---------------------------------------------------------------------

[<Fact>]
let ``builtIns contains exactly Cmd, Claude, and PowerShell`` () =
    // Pinning the registry's keyset protects against accidental
    // additions that would broaden the spawn surface beyond what
    // Stage 7 authorises. Adding a shell requires a spec PR + this
    // assertion update — same ADR-style discipline as
    // `EnvBlockTests.allowedNames contains exactly the spec-7-2 baseline`.
    // PR-J added PowerShell as the third built-in.
    let expected =
        Set.ofList
            [ ShellRegistry.Cmd
              ShellRegistry.Claude
              ShellRegistry.PowerShell ]
    let actual =
        ShellRegistry.builtIns
        |> Map.toSeq
        |> Seq.map fst
        |> Set.ofSeq
    Assert.Equal<Set<ShellRegistry.ShellId>>(expected, actual)

[<Fact>]
let ``Cmd entry resolves to cmd.exe`` () =
    let cmd =
        ShellRegistry.builtIns
        |> Map.find ShellRegistry.Cmd
    Assert.Equal("Command Prompt", cmd.DisplayName)
    Assert.Equal<Result<string, string>>(Ok "cmd.exe", cmd.Resolve())

[<Fact>]
let ``Claude entry has DisplayName "Claude Code"`` () =
    // `Resolve` for Claude calls `where.exe claude` which behaves
    // differently on each test environment; we only pin the metadata
    // here. The real resolution is exercised in PR-D's NVDA matrix
    // row.
    let claude =
        ShellRegistry.builtIns
        |> Map.find ShellRegistry.Claude
    Assert.Equal("Claude Code", claude.DisplayName)
    Assert.Equal(ShellRegistry.Claude, claude.Id)

[<Fact>]
let ``PowerShell entry resolves to powershell.exe`` () =
    // Windows PowerShell is always present on Windows 10+ so the
    // resolver returns a constant `Ok "powershell.exe"` and the
    // bare command name resolves through the parent's PATH (which
    // the env-scrub preserves). Pinning this protects against an
    // accidental flip to `pwsh.exe` in the production registry —
    // pwsh is an optional install and would break startup on
    // machines without it.
    let ps =
        ShellRegistry.builtIns
        |> Map.find ShellRegistry.PowerShell
    Assert.Equal("PowerShell", ps.DisplayName)
    Assert.Equal(ShellRegistry.PowerShell, ps.Id)
    Assert.Equal<Result<string, string>>(Ok "powershell.exe", ps.Resolve())

// ---------------------------------------------------------------------
// tryFindIn — synthetic registry injection
// ---------------------------------------------------------------------

[<Fact>]
let ``tryFindIn returns Some when the id is registered`` () =
    // F# 9 record-literal inference can't see the `Shell` record's
    // fields without the type annotation: `Shell` lives inside
    // `module ShellRegistry` (not auto-opened), so `Id`/`DisplayName`/
    // `Resolve` aren't unqualified-resolvable from the test scope.
    // The annotation pins the record's type and the field names
    // resolve cleanly.
    let shell : ShellRegistry.Shell =
        { Id = ShellRegistry.Cmd
          DisplayName = "fake-cmd"
          Resolve = fun () -> Ok "fake.exe" }
    let registry = Map.ofList [ ShellRegistry.Cmd, shell ]
    let found = ShellRegistry.tryFindIn registry ShellRegistry.Cmd
    Assert.True(found.IsSome)
    Assert.Equal("fake-cmd", found.Value.DisplayName)

[<Fact>]
let ``tryFindIn returns None when the id is missing`` () =
    let registry : Map<ShellRegistry.ShellId, ShellRegistry.Shell> =
        Map.empty
    Assert.Equal(None, ShellRegistry.tryFindIn registry ShellRegistry.Cmd)
    Assert.Equal(None, ShellRegistry.tryFindIn registry ShellRegistry.Claude)

[<Fact>]
let ``tryFindIn allows tests to inject deterministic Resolve closures`` () =
    // The reason `tryFindIn` exists separate from `tryFind`: PR-B's
    // production resolver hits `where.exe` for Claude, which is
    // non-deterministic across test environments. Tests construct a
    // synthetic registry with predictable closures and verify the
    // higher-level orchestration (which is in `Program.fs compose ()`
    // for now; will move into a testable helper if PR-C's hot-switch
    // logic justifies the extraction).
    let claudeShell : ShellRegistry.Shell =
        { Id = ShellRegistry.Claude
          DisplayName = "synthetic Claude"
          Resolve = fun () -> Error "synthetic-failure-for-test" }
    let synthetic = Map.ofList [ ShellRegistry.Claude, claudeShell ]
    let claude = (ShellRegistry.tryFindIn synthetic ShellRegistry.Claude).Value
    match claude.Resolve() with
    | Error reason -> Assert.Equal("synthetic-failure-for-test", reason)
    | Ok _ -> Assert.Fail("Expected Error from synthetic resolver; got Ok")
