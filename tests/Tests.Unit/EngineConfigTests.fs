module PtySpeak.Tests.Unit.EngineConfigTests

open Xunit
open Engine.Core
open Engine.Core.EngineConfig

// ---------------------------------------------------------------------
// ADR 0014 C1 — engine.toml parse contract. The load-bearing
// property is the warn-and-default discipline: NO input text
// can crash the parse or silently change behaviour — every
// degradation is a typed warning.
// ---------------------------------------------------------------------

[<Fact>]
let ``empty text is pure defaults with no warnings`` () =
    let config, warnings = EngineConfig.parse ""
    Assert.Equal(EngineConfig.defaults, config)
    Assert.Empty(warnings)

[<Fact>]
let ``a full valid file parses every section`` () =
    let toml =
        """
schema_version = 1

[participant]
claude_executable = "C:\\tools\\claude.exe"

[speech]
rate = 3
voice = "Zira"

[narration]
move_read_cap_chars = 900

[cues]
enabled = false
gain = 0.5

[keys]
pin = "z"
"""
    let config, warnings = EngineConfig.parse toml
    Assert.Empty(warnings)
    Assert.Equal(Some "C:\\tools\\claude.exe", config.ClaudeExecutable)
    Assert.Equal(3, config.SpeechRate)
    Assert.Equal(Some "Zira", config.VoiceName)
    Assert.Equal(900, config.MoveReadCapChars)
    Assert.False(config.CuesEnabled)
    Assert.Equal(0.5, config.CueGain)
    Assert.Equal(Some 'z', config.KeyOverrides |> Map.tryFind "pin")

[<Fact>]
let ``malformed toml degrades to defaults with one warning`` () =
    let config, warnings = EngineConfig.parse "[speech\nrate = "
    Assert.Equal(EngineConfig.defaults, config)
    match warnings with
    | [ w ] -> Assert.Contains("malformed", w)
    | other -> failwithf "expected one warning, got %A" other

[<Fact>]
let ``a newer schema version is rejected whole`` () =
    let config, warnings =
        EngineConfig.parse "schema_version = 99\n[speech]\nrate = 5"
    Assert.Equal(EngineConfig.defaults, config)
    Assert.True(
        warnings |> List.exists (fun w -> w.Contains "newer"))

[<Fact>]
let ``out-of-range rate clamps with a warning`` () =
    let config, warnings = EngineConfig.parse "[speech]\nrate = 40"
    Assert.Equal(10, config.SpeechRate)
    Assert.True(warnings |> List.exists (fun w -> w.Contains "clamped"))

[<Fact>]
let ``wrong-typed values warn and keep defaults`` () =
    let toml =
        "[speech]\nrate = \"fast\"\n[cues]\nenabled = \"yes\"\ngain = \"loud\""
    let config, warnings = EngineConfig.parse toml
    Assert.Equal(0, config.SpeechRate)
    Assert.True(config.CuesEnabled)
    Assert.Equal(1.0, config.CueGain)
    Assert.Equal(3, List.length warnings)

[<Fact>]
let ``cue gain accepts integers and clamps range`` () =
    let config, _ = EngineConfig.parse "[cues]\ngain = 1"
    Assert.Equal(1.0, config.CueGain)
    let config2, warnings2 = EngineConfig.parse "[cues]\ngain = 7.5"
    Assert.Equal(1.0, config2.CueGain)
    Assert.True(warnings2 |> List.exists (fun w -> w.Contains "clamped"))

[<Fact>]
let ``too-small narration cap clamps to the floor`` () =
    let config, warnings =
        EngineConfig.parse "[narration]\nmove_read_cap_chars = 5"
    Assert.Equal(50, config.MoveReadCapChars)
    Assert.True(warnings |> List.exists (fun w -> w.Contains "50"))

[<Fact>]
let ``multi-character key overrides warn and are dropped`` () =
    let config, warnings = EngineConfig.parse "[keys]\npin = \"zz\""
    Assert.True(config.KeyOverrides.IsEmpty)
    Assert.True(
        warnings
        |> List.exists (fun w -> w.Contains "single printable"))

[<Fact>]
let ``unknown sections and keys are ignored silently`` () =
    // Forward compatibility: an older build must not nag about
    // keys a newer build added (the schema_version gate handles
    // true incompatibility).
    let config, warnings =
        EngineConfig.parse "[future_section]\nmystery = 1"
    Assert.Equal(EngineConfig.defaults, config)
    Assert.Empty(warnings)
