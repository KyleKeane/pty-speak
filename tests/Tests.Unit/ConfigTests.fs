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
let ``defaultConfig resolveStreamParameters matches StreamPathway.defaultParameters exactly`` () =
    // The substrate-wide invariant: "no config = pre-C2 behaviour".
    // If StreamPathway.defaultParameters changes, the resolver
    // picks up the new defaults automatically (no constant
    // duplication in Config).
    let resolved = Config.resolveStreamParameters Config.defaultConfig
    Assert.Equal(StreamPathway.defaultParameters, resolved)

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
    Assert.Equal(StreamPathway.defaultParameters, Config.resolveStreamParameters config)

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

// ---- Per-pathway parameter override --------------------------------

[<Fact>]
let ``[pathway.stream] debounce_window_ms = 50 flows through resolveStreamParameters`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ndebounce_window_ms = 50\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(50, resolved.DebounceWindowMs)
    // Other fields fall back to defaults
    Assert.Equal(StreamPathway.defaultParameters.SpinnerWindowMs, resolved.SpinnerWindowMs)
    Assert.Equal(StreamPathway.defaultParameters.SpinnerThreshold, resolved.SpinnerThreshold)
    Assert.Equal(StreamPathway.defaultParameters.MaxAnnounceChars, resolved.MaxAnnounceChars)

[<Fact>]
let ``partial [pathway.stream] override merges with defaults field-by-field`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nspinner_threshold = 10\nmax_announce_chars = 1000\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.defaultParameters.DebounceWindowMs, resolved.DebounceWindowMs)
    Assert.Equal(StreamPathway.defaultParameters.SpinnerWindowMs, resolved.SpinnerWindowMs)
    Assert.Equal(10, resolved.SpinnerThreshold)
    Assert.Equal(1000, resolved.MaxAnnounceChars)

// ---- Unknown keys / invalid values ----------------------------------

[<Fact>]
let ``unknown parameter key in [pathway.stream] logs Warning and is dropped`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ndebounce_window_ms = 50\ncebounce = 99\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(50, resolved.DebounceWindowMs)
    Assert.True(logger.HasLevel(LogLevel.Warning))
    let warnings =
        logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    Assert.Contains(warnings, fun m -> m.Contains("cebounce"))

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

[<Fact>]
let ``negative parameter value logs Warning and falls back to default`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ndebounce_window_ms = -50\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.defaultParameters.DebounceWindowMs, resolved.DebounceWindowMs)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``zero parameter value logs Warning and falls back to default`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ndebounce_window_ms = 0\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.defaultParameters.DebounceWindowMs, resolved.DebounceWindowMs)
    Assert.True(logger.HasLevel(LogLevel.Warning))

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
let ``round-trip: comprehensive TOML parses to expected resolved values`` () =
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
              ""
              "[pathway.stream]"
              "debounce_window_ms = 100"
              "spinner_window_ms = 2000"
              "spinner_threshold = 8"
              "max_announce_chars = 750"
              "" ]
    let config, _ = loadFromText toml
    Assert.Equal("stream", Config.resolveShellPathway config "cmd")
    Assert.Equal("tui", Config.resolveShellPathway config "claude")
    Assert.Equal("stream", Config.resolveShellPathway config "powershell")
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(100, resolved.DebounceWindowMs)
    Assert.Equal(2000, resolved.SpinnerWindowMs)
    Assert.Equal(8, resolved.SpinnerThreshold)
    Assert.Equal(750, resolved.MaxAnnounceChars)

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

// ---- Phase A.2 — color_detection knob -----------------------------

[<Fact>]
let ``defaultConfig resolveStreamParameters has ColorDetection = true`` () =
    // Sensible-defaults invariant: byte-equivalent to the
    // hardcoded behaviour. Phase A.2 enables colour detection
    // by default.
    let resolved = Config.resolveStreamParameters Config.defaultConfig
    Assert.True(resolved.ColorDetection)

[<Fact>]
let ``[pathway.stream] color_detection = false flows through resolveStreamParameters`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ncolor_detection = false\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.False(resolved.ColorDetection)
    // Other fields fall back to defaults.
    Assert.Equal(StreamPathway.defaultParameters.DebounceWindowMs, resolved.DebounceWindowMs)

[<Fact>]
let ``[pathway.stream] color_detection = true is parsed as override (still true)`` () =
    // Even when matching the default, the override is recorded
    // — semantically a no-op, but the user-set flag survives
    // round-trip.
    let toml =
        "schema_version = 1\n[pathway.stream]\ncolor_detection = true\n"
    let config, _ = loadFromText toml
    Assert.Equal(Some true, config.StreamOverrides.ColorDetection)
    Assert.True((Config.resolveStreamParameters config).ColorDetection)

[<Fact>]
let ``non-bool color_detection logs Warning and falls back to default`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\ncolor_detection = 42\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.True(resolved.ColorDetection)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---- PR #168 — Tier 1 parameters ---------------------------------

[<Fact>]
let ``defaultConfig resolveStreamParameters has BulkChangeThreshold = 3`` () =
    let resolved = Config.resolveStreamParameters Config.defaultConfig
    Assert.Equal(3, resolved.BulkChangeThreshold)

[<Fact>]
let ``defaultConfig resolveStreamParameters has BackspacePolicy = AnnounceDeletedCharacter`` () =
    let resolved = Config.resolveStreamParameters Config.defaultConfig
    Assert.Equal(StreamPathway.AnnounceDeletedCharacter, resolved.BackspacePolicy)

[<Fact>]
let ``defaultConfig resolveStreamParameters has ModeBarrierFlushPolicy = SummaryOnly`` () =
    let resolved = Config.resolveStreamParameters Config.defaultConfig
    Assert.Equal(StreamPathway.SummaryOnly, resolved.ModeBarrierFlushPolicy)

[<Fact>]
let ``[pathway.stream] bulk_change_threshold = 5 flows through resolveStreamParameters`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nbulk_change_threshold = 5\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(5, resolved.BulkChangeThreshold)

[<Fact>]
let ``[pathway.stream] backspace_policy = silent maps to SuppressShrink`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nbackspace_policy = \"silent\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.SuppressShrink, resolved.BackspacePolicy)

[<Fact>]
let ``[pathway.stream] backspace_policy = announce_deleted_character maps to AnnounceDeletedCharacter`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nbackspace_policy = \"announce_deleted_character\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.AnnounceDeletedCharacter, resolved.BackspacePolicy)

[<Fact>]
let ``[pathway.stream] backspace_policy = announce_deleted_word maps to AnnounceDeletedWord`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nbackspace_policy = \"announce_deleted_word\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.AnnounceDeletedWord, resolved.BackspacePolicy)

[<Fact>]
let ``unknown backspace_policy value logs Warning and falls back to default`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nbackspace_policy = \"chirp\"\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.AnnounceDeletedCharacter, resolved.BackspacePolicy)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``[pathway.stream] mode_barrier_flush_policy = verbose maps to Verbose`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nmode_barrier_flush_policy = \"verbose\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.Verbose, resolved.ModeBarrierFlushPolicy)

[<Fact>]
let ``[pathway.stream] mode_barrier_flush_policy = summary_only maps to SummaryOnly`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nmode_barrier_flush_policy = \"summary_only\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.SummaryOnly, resolved.ModeBarrierFlushPolicy)

[<Fact>]
let ``[pathway.stream] mode_barrier_flush_policy = suppressed maps to Suppressed`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nmode_barrier_flush_policy = \"suppressed\"\n"
    let config, _ = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.Suppressed, resolved.ModeBarrierFlushPolicy)

[<Fact>]
let ``unknown mode_barrier_flush_policy value logs Warning and falls back to default`` () =
    let toml =
        "schema_version = 1\n[pathway.stream]\nmode_barrier_flush_policy = \"warbly\"\n"
    let config, logger = loadFromText toml
    let resolved = Config.resolveStreamParameters config
    Assert.Equal(StreamPathway.SummaryOnly, resolved.ModeBarrierFlushPolicy)
    Assert.True(logger.HasLevel(LogLevel.Warning))

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
