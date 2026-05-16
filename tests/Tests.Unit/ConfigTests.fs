module PtySpeak.Tests.Unit.ConfigTests

open System
open System.IO
open Xunit
open Microsoft.Extensions.Logging
open Tomlyn
open Terminal.Core

// ---------------------------------------------------------------------
// Phase B (subset, "C2") — Config behavioural pinning
// ---------------------------------------------------------------------
//
// Config.tryLoad never throws — every error path logs via the
// supplied ILogger and falls back to defaultConfig. These tests
// exercise the loader's parse + validate + resolve contract
// against synthetic TOML strings written to a temp file.

/// Minimal ILogger that records (level, message) pairs.
/// Mirrors the pattern in FileLoggerChannelTests; F# 9 nullness
/// constraints handled per the comments there.
type private NoopScope() =
    interface IDisposable with
        member _.Dispose() = ()

type private RecordingLogger() =
    let calls = ResizeArray<LogLevel * string>()
    member _.Calls = calls
    member _.HasLevel (level: LogLevel) : bool =
        calls |> Seq.exists (fun (l, _) -> l = level)
    member _.MessagesAtLevel (level: LogLevel) : string seq =
        calls
        |> Seq.choose (fun (l, m) -> if l = level then Some m else None)
    interface ILogger with
        member _.BeginScope<'TState when 'TState : not null>
                (_state: 'TState) : IDisposable =
            (new NoopScope()) :> IDisposable
        member _.IsEnabled(_: LogLevel) : bool = true
        member _.Log<'TState>
                (level: LogLevel,
                 _eventId: EventId,
                 state: 'TState,
                 ex: exn | null,
                 formatter: Func<'TState, exn | null, string>) : unit =
            let message = formatter.Invoke(state, ex)
            calls.Add((level, message))

/// Write the supplied TOML text to a fresh temp file, run
/// `Config.tryLoad`, and return the (config, logger). The temp
/// file is deleted on the way out.
let private loadFromText (toml: string) : Config.Config * RecordingLogger =
    let logger = RecordingLogger()
    let path =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-config-test-%s.toml" (Guid.NewGuid().ToString("N")))
    try
        File.WriteAllText(path, toml)
        let config = Config.tryLoad (logger :> ILogger) path
        config, logger
    finally
        try File.Delete(path) with _ -> ()

// ---- defaultConfig + resolver byte-equivalence ---------------------

[<Fact>]
let ``defaultConfig resolveShellPathway returns "stream" for any shell key`` () =
    Assert.Equal("stream", Config.resolveShellPathway Config.defaultConfig "cmd")
    Assert.Equal("stream", Config.resolveShellPathway Config.defaultConfig "claude")
    Assert.Equal("stream", Config.resolveShellPathway Config.defaultConfig "powershell")

// ---- tryLoad: missing file ------------------------------------------

[<Fact>]
let ``tryLoad on missing file returns defaultConfig and logs Information`` () =
    let logger = RecordingLogger()
    let path =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-missing-%s.toml" (Guid.NewGuid().ToString("N")))
    let config = Config.tryLoad (logger :> ILogger) path
    Assert.Equal(Config.defaultConfig, config)
    Assert.True(logger.HasLevel(LogLevel.Information))

// ---- tryLoad: malformed TOML ---------------------------------------

[<Fact>]
let ``tryLoad on malformed TOML returns defaultConfig and logs Error`` () =
    let toml = "this is not = valid toml ["
    let config, logger = loadFromText toml
    Assert.Equal(Config.defaultConfig, config)
    Assert.True(logger.HasLevel(LogLevel.Error))

// ---- tryLoad: minimal valid TOML -----------------------------------

[<Fact>]
let ``tryLoad on minimal valid TOML returns default-shape Config`` () =
    let toml = "schema_version = 1\n"
    let config, _ = loadFromText toml
    Assert.Equal(1, config.SchemaVersion)
    Assert.Equal<Map<string, Config.ShellPathwayConfig>>(Map.empty, config.ShellOverrides)

// ---- Per-shell pathway override ------------------------------------

[<Fact>]
let ``[shell.cmd] pathway = "stream" parses correctly`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\npathway = \"stream\"\n"
    let config, _ = loadFromText toml
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")

[<Fact>]
let ``[shell.claude] pathway = "tui" parses correctly`` () =
    let toml =
        "schema_version = 1\n[shell.claude]\npathway = \"tui\"\n"
    let config, _ = loadFromText toml
    Assert.Equal("tui", Config.resolveShellPathway config "claude")
    // Other shells fall back to default
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")

// ---- Cycle 38b — per-shell profile-set override -------------------

[<Fact>]
let ``[shell.cmd] profiles = [...] resolves to the string array`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nprofiles = [\"passthrough\", \"earcon\"]\n"
    let config, _ = loadFromText toml
    let result = Config.resolveShellProfiles config "cmd"
    Assert.True(result.IsSome)
    Assert.Equal<string[]>(
        [| "passthrough"; "earcon" |],
        result.Value)

[<Fact>]
let ``[shell.cmd] without profiles returns None for resolveShellProfiles`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\npathway = \"stream\"\n"
    let config, _ = loadFromText toml
    Assert.Equal(None, Config.resolveShellProfiles config "cmd")

[<Fact>]
let ``[shell.cmd] with both pathway and profiles preserves both`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\npathway = \"tui\"\nprofiles = [\"passthrough\"]\n"
    let config, _ = loadFromText toml
    Assert.Equal("tui", Config.resolveShellPathway config "cmd")
    let profiles = Config.resolveShellProfiles config "cmd"
    Assert.True(profiles.IsSome)
    Assert.Equal<string[]>([| "passthrough" |], profiles.Value)

[<Fact>]
let ``[shell.cmd] with only profiles (no pathway) defaults pathway to stream`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nprofiles = [\"earcon\"]\n"
    let config, _ = loadFromText toml
    // pathway field absent → resolver returns the "stream" default.
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")
    let profiles = Config.resolveShellProfiles config "cmd"
    Assert.True(profiles.IsSome)
    Assert.Equal<string[]>([| "earcon" |], profiles.Value)

[<Fact>]
let ``resolveShellProfiles returns None for a shell with no override section`` () =
    let toml = "schema_version = 1\n"
    let config, _ = loadFromText toml
    Assert.Equal(None, Config.resolveShellProfiles config "cmd")

// ---- Cycle 45f — per-shell verbosity / prompt_path ----------------

[<Fact>]
let ``[shell.claude] verbosity = "line_by_line" resolves through resolveShellPolicy`` () =
    let toml =
        "schema_version = 1\n[shell.claude]\nverbosity = \"line_by_line\"\n"
    let config, _ = loadFromText toml
    let policy = Config.resolveShellPolicy config "claude"
    Assert.Equal(ShellPolicy.LineByLine, policy.Streaming)
    // PromptPath not overridden — falls back to the compiled default.
    Assert.Equal(ShellPolicy.Suppress, policy.PromptPath)

[<Fact>]
let ``[shell.cmd] prompt_path = "final_dir_only" resolves through resolveShellPolicy`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nprompt_path = \"final_dir_only\"\n"
    let config, _ = loadFromText toml
    let policy = Config.resolveShellPolicy config "cmd"
    Assert.Equal(ShellPolicy.FinalDirOnly, policy.PromptPath)
    // Streaming not overridden — compiled default (TupleFinalOnly).
    Assert.Equal(ShellPolicy.TupleFinalOnly, policy.Streaming)

[<Fact>]
let ``[shell.cmd] prompt_path = "full_on_change" resolves through resolveShellPolicy`` () =
    // Cycle 52 R6b — the new context-aware mode.
    let toml =
        "schema_version = 1\n[shell.cmd]\nprompt_path = \"full_on_change\"\n"
    let config, _ = loadFromText toml
    let policy = Config.resolveShellPolicy config "cmd"
    Assert.Equal(ShellPolicy.FullOnChangeElseFinal, policy.PromptPath)
    Assert.Equal(ShellPolicy.TupleFinalOnly, policy.Streaming)

[<Fact>]
let ``[shell.cmd] both verbosity and prompt_path resolve together`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nverbosity = \"off\"\nprompt_path = \"full\"\n"
    let config, _ = loadFromText toml
    let policy = Config.resolveShellPolicy config "cmd"
    Assert.Equal(ShellPolicy.Off, policy.Streaming)
    Assert.Equal(ShellPolicy.Full, policy.PromptPath)

[<Fact>]
let ``unknown verbosity value logs Warning and falls back`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nverbosity = \"banana\"\n"
    let config, logger = loadFromText toml
    let policy = Config.resolveShellPolicy config "cmd"
    Assert.Equal(ShellPolicy.TupleFinalOnly, policy.Streaming)
    Assert.True(logger.HasLevel(LogLevel.Warning))
    let warnings =
        logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    Assert.Contains(warnings, fun m -> m.Contains("verbosity"))

[<Fact>]
let ``unknown prompt_path value logs Warning and falls back`` () =
    let toml =
        "schema_version = 1\n[shell.cmd]\nprompt_path = \"banana\"\n"
    let config, logger = loadFromText toml
    let policy = Config.resolveShellPolicy config "cmd"
    Assert.Equal(ShellPolicy.Suppress, policy.PromptPath)
    Assert.True(logger.HasLevel(LogLevel.Warning))
    let warnings =
        logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    Assert.Contains(warnings, fun m -> m.Contains("prompt_path"))

[<Fact>]
let ``resolveShellPolicy with no TOML override returns compiled defaults`` () =
    let toml = "schema_version = 1\n"
    let config, _ = loadFromText toml
    let cmdPolicy = Config.resolveShellPolicy config "cmd"
    let claudePolicy = Config.resolveShellPolicy config "claude"
    Assert.Equal(ShellPolicy.TupleFinalOnly, cmdPolicy.Streaming)
    Assert.Equal(ShellPolicy.Suppress, cmdPolicy.PromptPath)
    Assert.Equal(ShellPolicy.TupleFinalOnly, claudePolicy.Streaming)
    Assert.True(claudePolicy.SelectionEnabled)

// ---- Unknown keys / invalid values ----------------------------------

[<Fact>]
let ``unknown shell section [shell.bash] logs Warning and is ignored`` () =
    let toml =
        "schema_version = 1\n[shell.bash]\npathway = \"stream\"\n"
    let config, logger = loadFromText toml
    Assert.False(Map.containsKey "bash" config.ShellOverrides)
    Assert.True(logger.HasLevel(LogLevel.Warning))
    let warnings =
        logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    Assert.Contains(warnings, fun m -> m.Contains("bash"))

// ---- Schema versioning --------------------------------------------

[<Fact>]
let ``schema_version too new logs Error and returns defaultConfig`` () =
    let toml =
        sprintf "schema_version = %d\n[shell.cmd]\npathway = \"tui\"\n" 999
    let config, logger = loadFromText toml
    Assert.Equal(Config.defaultConfig, config)
    Assert.True(logger.HasLevel(LogLevel.Error))

[<Fact>]
let ``schema_version too old logs Warning and best-effort parses`` () =
    // schema_version = 0 is older than CurrentSchemaVersion (1).
    // The loader proceeds to parse the rest of the TOML.
    let toml =
        "schema_version = 0\n[shell.cmd]\npathway = \"tui\"\n"
    let config, logger = loadFromText toml
    Assert.Equal("tui", Config.resolveShellPathway config "cmd")
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``missing schema_version logs Warning and treats as 1`` () =
    let toml = "[shell.cmd]\npathway = \"tui\"\n"
    let config, logger = loadFromText toml
    Assert.Equal(Config.CurrentSchemaVersion, config.SchemaVersion)
    Assert.Equal("tui", Config.resolveShellPathway config "cmd")
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---- Round-trip --------------------------------------------------

[<Fact>]
let ``round-trip: shell-pathway TOML parses to expected resolved values`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              ""
              "[shell.cmd]"
              "pathway = \"stream\""
              ""
              "[shell.claude]"
              "pathway = \"tui\""
              ""
              "[shell.powershell]"
              "pathway = \"stream\""
              "" ]
    let config, _ = loadFromText toml
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")
    Assert.Equal("tui", Config.resolveShellPathway config "claude")
    Assert.Equal("stream", Config.resolveShellPathway config "powershell")

// ---- Forward-compat: ignore unknown sections ----------------------

[<Fact>]
let ``TOML containing [[bindings]] (future input-binding spec section) parses cleanly`` () =
    // Spec event-and-output-framework.md A.5 sketches a future
    // input-binding schema with [[bindings]] arrays at top level.
    // C2's loader silently ignores those sections so a single
    // config.toml can grow cumulatively across Phase B sub-stages.
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              ""
              "[shell.cmd]"
              "pathway = \"stream\""
              ""
              "[[bindings]]"
              "intent = \"SwitchToCmd\""
              "gesture = \"Ctrl+Alt+1\""
              ""
              "[[bindings]]"
              "intent = \"SwitchToClaude\""
              "gesture = \"Ctrl+Alt+3\""
              "" ]
    let config, logger = loadFromText toml
    // The shell override still applies.
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")
    // No Warnings or Errors fired for the [[bindings]] sections.
    Assert.False(logger.HasLevel(LogLevel.Error))
    let warnings = logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    Assert.DoesNotContain(warnings, fun m -> m.Contains("bindings"))

// ---- defaultConfigFilePath -----------------------------------------

[<Fact>]
let ``defaultConfigFilePath ends with PtySpeak\config.toml`` () =
    let path = Config.defaultConfigFilePath ()
    Assert.EndsWith(@"PtySpeak\config.toml", path)

// =====================================================================
// Cycle 19 — `[startup] default_shell` TOML override
// =====================================================================

[<Fact>]
let ``defaultConfig resolveDefaultShell returns None`` () =
    Assert.Equal(None, Config.resolveDefaultShell Config.defaultConfig)

[<Fact>]
let ``[startup] default_shell = "cmd" parses + resolves`` () =
    let toml =
        "schema_version = 1\n[startup]\ndefault_shell = \"cmd\"\n"
    let config, _ = loadFromText toml
    Assert.Equal(Some "cmd", Config.resolveDefaultShell config)

[<Fact>]
let ``[startup] default_shell case-insensitive (uppercase folds to lowercase)`` () =
    let toml =
        "schema_version = 1\n[startup]\ndefault_shell = \"PowerShell\"\n"
    let config, _ = loadFromText toml
    Assert.Equal(Some "powershell", Config.resolveDefaultShell config)

[<Fact>]
let ``[startup] default_shell unknown value logs Warning + drops override`` () =
    let toml =
        "schema_version = 1\n[startup]\ndefault_shell = \"bash\"\n"
    let config, logger = loadFromText toml
    Assert.Equal(None, Config.resolveDefaultShell config)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``[startup] missing default_shell key returns None`` () =
    let toml = "schema_version = 1\n[startup]\n"
    let config, _ = loadFromText toml
    Assert.Equal(None, Config.resolveDefaultShell config)

[<Fact>]
let ``[startup] unknown key logs Warning but does not corrupt parse`` () =
    let toml =
        "schema_version = 1\n[startup]\ndefault_shell = \"cmd\"\nunknown_setting = 42\n"
    let config, logger = loadFromText toml
    Assert.Equal(Some "cmd", Config.resolveDefaultShell config)
    Assert.True(logger.HasLevel(LogLevel.Warning))


// ---------------------------------------------------------------------
// Cycle 24g — snapshotAsJson: TOML model dump
// ---------------------------------------------------------------------
//
// Pinned for the diagnostic surface that lets a maintainer compare
// what Tomlyn understood against what they wrote in config.toml.
// The dump is JSON-shaped so subtleties like dotted-key vs. nested-
// table representation are visible.

let private parseToTable (toml: string) : Tomlyn.Model.TomlTable =
    let doc = Tomlyn.Toml.Parse(toml)
    Assert.False(doc.HasErrors, "Test TOML must parse cleanly")
    doc.ToModel()

[<Fact>]
let ``snapshotAsJson on empty TOML returns "{}"`` () =
    let model = parseToTable ""
    Assert.Equal("{}", Config.snapshotAsJson model)

[<Fact>]
let ``snapshotAsJson emits string values quoted`` () =
    let model = parseToTable "name = \"hello\"\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"name\": \"hello\"", json)

[<Fact>]
let ``snapshotAsJson emits int64 values as bare numbers`` () =
    let model = parseToTable "n = 42\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"n\": 42", json)

[<Fact>]
let ``snapshotAsJson emits bool values as lowercase literals`` () =
    let model = parseToTable "flag = true\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"flag\": true", json)

[<Fact>]
let ``snapshotAsJson nests dotted-key headers as sub-tables`` () =
    // The bug we're chasing: Tomlyn's representation of
    // `[a.b]`-headers. snapshotAsJson should show the model
    // exactly as the rest of the parser sees it. With a clean
    // Tomlyn install + UTF-8 input, this should produce nested
    // tables.
    let model = parseToTable "[session_model.persistence]\nmode = \"session_log\"\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"session_model\":", json)
    Assert.Contains("\"persistence\":", json)
    Assert.Contains("\"mode\": \"session_log\"", json)

[<Fact>]
let ``snapshotAsJson escapes embedded quotes and backslashes`` () =
    let model = parseToTable "path = \"C:\\\\Users\\\\test\"\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"path\": \"C:\\\\Users\\\\test\"", json)

[<Fact>]
let ``snapshotAsJson emits nested table with two-space indent`` () =
    let model = parseToTable "[outer]\n[outer.inner]\nkey = \"value\"\n"
    let json = Config.snapshotAsJson model
    // Outer level: top "{" + 2-space indent for "outer" key
    Assert.StartsWith("{", json)
    Assert.Contains("  \"outer\":", json)
    // Inner level: 4-space indent for "inner.key"
    Assert.Contains("    \"inner\":", json)
    Assert.Contains("      \"key\": \"value\"", json)

[<Fact>]
let ``snapshotAsJson preserves multiple sibling keys as comma-separated entries`` () =
    let model = parseToTable "a = 1\nb = 2\nc = 3\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"a\": 1,", json)
    Assert.Contains("\"b\": 2,", json)
    Assert.Contains("\"c\": 3", json)

[<Fact>]
let ``snapshotAsJson emits empty sub-table inline as {}`` () =
    let model = parseToTable "[empty]\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"empty\": {}", json)

[<Fact>]
let ``snapshotAsJson handles arrays as bracketed inline lists`` () =
    let model = parseToTable "items = [1, 2, 3]\n"
    let json = Config.snapshotAsJson model
    Assert.Contains("\"items\": [1, 2, 3]", json)

// ---------------------------------------------------------------------
// Cycle 25a — [logging] section parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``[logging] missing returns None min_level`` () =
    let toml = "schema_version = 1\n"
    let config, _ = loadFromText toml
    Assert.Equal(None, config.LoggingOverrides.MinLevel)

[<Fact>]
let ``[logging] min_level = "Debug" parses as LogLevel.Debug`` () =
    let toml = "schema_version = 1\n[logging]\nmin_level = \"Debug\"\n"
    let config, _ = loadFromText toml
    Assert.Equal(Some LogLevel.Debug, config.LoggingOverrides.MinLevel)

[<Fact>]
let ``[logging] min_level is case-insensitive (matches PTYSPEAK_LOG_LEVEL precedent)`` () =
    let toml = "schema_version = 1\n[logging]\nmin_level = \"warning\"\n"
    let config, _ = loadFromText toml
    Assert.Equal(Some LogLevel.Warning, config.LoggingOverrides.MinLevel)

[<Fact>]
let ``[logging] min_level with invalid string logs Warning and returns None`` () =
    let toml =
        "schema_version = 1\n[logging]\nmin_level = \"banana\"\n"
    let config, logger = loadFromText toml
    Assert.Equal(None, config.LoggingOverrides.MinLevel)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``[logging] min_level non-string logs Warning and returns None`` () =
    let toml = "schema_version = 1\n[logging]\nmin_level = 42\n"
    let config, logger = loadFromText toml
    Assert.Equal(None, config.LoggingOverrides.MinLevel)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``[logging] unknown key logs Warning but does not corrupt parse`` () =
    let toml =
        "schema_version = 1\n[logging]\nmin_level = \"Trace\"\nunknown_setting = 42\n"
    let config, logger = loadFromText toml
    Assert.Equal(Some LogLevel.Trace, config.LoggingOverrides.MinLevel)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// Cycle 25a — Config.writeDefaults
// ---------------------------------------------------------------------
//
// The open-config hotkey writes a defaults-populated config.toml
// when one doesn't exist. Tests verify: (a) the file is written
// with UTF-8 no-BOM; (b) the file is idempotent (won't overwrite);
// (c) the written content round-trips through Config.tryLoad to
// the same Config record as defaultConfig (modulo schema version);
// (d) the create-then-parse path produces no Warnings (no
// "unknown key" diagnostics, since the template uses only known
// keys); (e) the file ends with a newline (matches typical TOML
// file conventions).

let private freshConfigTempPath () : string =
    let tmpDir =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-config-test-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory(tmpDir) |> ignore
    Path.Combine(tmpDir, "config.toml")

[<Fact>]
let ``writeDefaults creates a fresh file at the target path`` () =
    let path = freshConfigTempPath ()
    Assert.False(File.Exists(path))
    let wrote = Config.writeDefaults path
    Assert.True(wrote, "writeDefaults should return true on fresh write")
    Assert.True(File.Exists(path), "File should exist after writeDefaults")

[<Fact>]
let ``writeDefaults refuses to overwrite an existing file`` () =
    let path = freshConfigTempPath ()
    File.WriteAllText(path, "schema_version = 99\n")
    let wrote = Config.writeDefaults path
    Assert.False(wrote, "writeDefaults should return false when file exists")
    let content = File.ReadAllText(path)
    Assert.Contains("schema_version = 99", content)

[<Fact>]
let ``writeDefaults uses UTF-8 without BOM`` () =
    let path = freshConfigTempPath ()
    Config.writeDefaults path |> ignore
    let bytes = File.ReadAllBytes(path)
    // UTF-8 BOM is 0xEF 0xBB 0xBF. Cycle 25a's writer uses
    // UTF8Encoding(false) so these three bytes must NOT be the
    // file's prefix.
    Assert.True(bytes.Length > 3, "File should have non-trivial content")
    Assert.False(
        bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy,
        "writeDefaults must produce no UTF-8 BOM prefix")

[<Fact>]
let ``writeDefaults round-trips through Config.tryLoad without Warnings`` () =
    let path = freshConfigTempPath ()
    Config.writeDefaults path |> ignore
    let logger = RecordingLogger()
    let loaded = Config.tryLoad (logger :> ILogger) path
    Assert.False(
        logger.HasLevel(LogLevel.Warning),
        sprintf "writeDefaults template should parse without warnings; got: %A"
            (logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList))
    Assert.False(
        logger.HasLevel(LogLevel.Error),
        "writeDefaults template should parse without errors")
    // Verify the documented defaults round-trip.
    Assert.Equal(
        SessionPersistence.SessionLog,
        loaded.SessionPersistence.Mode)
    Assert.Equal(Some "cmd", loaded.StartupOverrides.DefaultShell)
    Assert.Equal(Some LogLevel.Information, loaded.LoggingOverrides.MinLevel)

[<Fact>]
let ``writeDefaults creates parent directory tree if missing`` () =
    // Mirrors FileLogger's defensive Directory.CreateDirectory
    // pattern. On a fresh install, the %LOCALAPPDATA%\PtySpeak\
    // directory may not exist yet.
    let nestedPath =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "pty-speak-nested-%s" (Guid.NewGuid().ToString("N")),
            "deeper",
            "config.toml")
    Assert.False(Directory.Exists(Path.GetDirectoryName(nestedPath)))
    let wrote = Config.writeDefaults nestedPath
    Assert.True(wrote)
    Assert.True(File.Exists(nestedPath))

// ---------------------------------------------------------------------
// Cycle 32a — `[profile.selection]` TOML loader
// ---------------------------------------------------------------------
//
// Pins the SelectionParameterOverrides parser + resolver added in
// Cycle 32a. The detector itself (`SelectionDetector.Parameters` +
// `defaultParameters` shipped in Cycle 29a) is unchanged; tests
// here only exercise the Config-side plumbing.

[<Fact>]
let ``[profile.selection] absent — resolveSelectionParameters returns SelectionDetector.defaultParameters`` () =
    let toml = "schema_version = 1\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    Assert.Equal(SelectionDetector.defaultParameters, resolved)

[<Fact>]
let ``[profile.selection] all four keys present override all defaults`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              "[profile.selection]"
              "highlight_detection_threshold_ms = 200"
              "dismissal_grace_ms = 300"
              "keystroke_correlation_window_ms = 500"
              "min_confidence = \"heuristic_sgr_with_keystroke\""
              "" ]
    let config, _ = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    Assert.Equal(200, resolved.HighlightDetectionThresholdMs)
    Assert.Equal(300, resolved.DismissalGraceMs)
    Assert.Equal(500, resolved.KeystrokeCorrelationWindowMs)
    Assert.Equal(
        SelectionDetector.HeuristicSGRWithKeystroke,
        resolved.MinConfidence)

[<Fact>]
let ``[profile.selection] single key overrides; others fall back to defaults`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              "[profile.selection]"
              "keystroke_correlation_window_ms = 400"
              "" ]
    let config, _ = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    let defaults = SelectionDetector.defaultParameters
    Assert.Equal(
        defaults.HighlightDetectionThresholdMs,
        resolved.HighlightDetectionThresholdMs)
    Assert.Equal(defaults.DismissalGraceMs, resolved.DismissalGraceMs)
    Assert.Equal(400, resolved.KeystrokeCorrelationWindowMs)
    Assert.Equal(defaults.MinConfidence, resolved.MinConfidence)

[<Fact>]
let ``[profile.selection] unrecognised min_confidence string logs Warning and defaults`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              "[profile.selection]"
              "min_confidence = \"fictional_tier\""
              "" ]
    let config, logger = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    Assert.Equal(SelectionDetector.HeuristicSGR, resolved.MinConfidence)
    Assert.True(
        logger.MessagesAtLevel(LogLevel.Warning)
        |> Seq.exists (fun msg ->
            msg.Contains("min_confidence") && msg.Contains("fictional_tier")),
        sprintf
            "expected a Warning mentioning min_confidence + fictional_tier; got: %A"
            (logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList))

[<Fact>]
let ``[profile.selection] non-integer threshold logs Warning and defaults`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              "[profile.selection]"
              "highlight_detection_threshold_ms = \"not_a_number\""
              "" ]
    let config, logger = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    Assert.Equal(
        SelectionDetector.defaultParameters.HighlightDetectionThresholdMs,
        resolved.HighlightDetectionThresholdMs)
    Assert.True(
        logger.MessagesAtLevel(LogLevel.Warning)
        |> Seq.exists (fun msg ->
            msg.Contains("highlight_detection_threshold_ms")),
        "expected a Warning mentioning highlight_detection_threshold_ms")

[<Fact>]
let ``[profile.selection] non-positive threshold logs Warning and defaults`` () =
    let toml =
        String.concat "\n"
            [ "schema_version = 1"
              "[profile.selection]"
              "dismissal_grace_ms = 0"
              "keystroke_correlation_window_ms = -50"
              "" ]
    let config, logger = loadFromText toml
    let resolved = Config.resolveSelectionParameters config
    let defaults = SelectionDetector.defaultParameters
    Assert.Equal(defaults.DismissalGraceMs, resolved.DismissalGraceMs)
    Assert.Equal(
        defaults.KeystrokeCorrelationWindowMs,
        resolved.KeystrokeCorrelationWindowMs)
    // parsePositiveInt logs "non-positive; clamping to default"
    // for both 0 and negative values; both keys triggered it.
    let warningCount =
        logger.MessagesAtLevel(LogLevel.Warning)
        |> Seq.filter (fun msg ->
            msg.Contains("[profile.selection]")
            && msg.Contains("non-positive"))
        |> Seq.length
    Assert.Equal(2, warningCount)
