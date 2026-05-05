module PtySpeak.Tests.Unit.ConfigTests

open System
open System.IO
open Xunit
open Microsoft.Extensions.Logging
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
