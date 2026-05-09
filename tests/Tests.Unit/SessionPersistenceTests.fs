module PtySpeak.Tests.Unit.SessionPersistenceTests

open System
open Xunit
open Microsoft.Extensions.Logging
open Tomlyn
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 24a — SessionPersistence behavioural pinning
// ---------------------------------------------------------------------
//
// SessionPersistence.parseFromTable never throws — every error path
// logs via the supplied ILogger and falls back to defaultConfig.
// These tests exercise the parser's contract against synthetic TOML
// strings parsed via Tomlyn.
//
// Cycle 24a is schema-only — Cycle 24c is the sub-cycle that adds
// the file writer + I/O behaviour these knobs control.

// ---------------------------------------------------------------------
// Test fixtures
// ---------------------------------------------------------------------

/// Minimal ILogger that records (level, message) pairs.
/// Mirrors `ConfigTests.RecordingLogger`; duplicated rather than
/// shared because the original is `private` to that test file
/// (each test file owns its own fixtures by convention).
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

/// Parse TOML text into a root TomlTable; tests then call
/// `SessionPersistence.parseFromTable` against the resulting
/// table. Mirrors the production path
/// (`Config.tryLoad` → `Toml.Parse` → `ToModel` →
/// `parseFromTable`) without touching the filesystem.
let private parseRoot (toml: string) : Tomlyn.Model.TomlTable =
    let doc = Toml.Parse(toml)
    Assert.False(doc.HasErrors, "Test TOML must parse cleanly")
    doc.ToModel()

let private parse (toml: string) : SessionPersistence.PersistenceConfig * RecordingLogger =
    let logger = RecordingLogger()
    let table = parseRoot toml
    let config = SessionPersistence.parseFromTable (logger :> ILogger) table
    config, logger

// ---------------------------------------------------------------------
// defaultConfig
// ---------------------------------------------------------------------

[<Fact>]
let ``defaultConfig is MemoryOnly with default size 64MB and Jsonl format`` () =
    Assert.Equal(SessionPersistence.MemoryOnly, SessionPersistence.defaultConfig.Mode)
    Assert.Equal(SessionPersistence.Jsonl, SessionPersistence.defaultConfig.Format)
    Assert.Equal<string option>(None, SessionPersistence.defaultConfig.OutputDir)
    Assert.Equal(64, SessionPersistence.defaultConfig.MaxSessionSizeMb)

// ---------------------------------------------------------------------
// Missing table → defaultConfig
// ---------------------------------------------------------------------

[<Fact>]
let ``empty TOML returns defaultConfig`` () =
    let config, logger = parse ""
    Assert.Equal(SessionPersistence.defaultConfig, config)
    Assert.False(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``missing [session_model] table returns defaultConfig`` () =
    let config, logger = parse "[unrelated]\nfoo = \"bar\"\n"
    Assert.Equal(SessionPersistence.defaultConfig, config)
    Assert.False(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``[session_model] table without [persistence] sub-table returns defaultConfig`` () =
    let config, logger = parse "[session_model]\nplaceholder = true\n"
    Assert.Equal(SessionPersistence.defaultConfig, config)
    Assert.False(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// mode parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``mode = "memory_only" parses as MemoryOnly`` () =
    let toml = "[session_model.persistence]\nmode = \"memory_only\"\n"
    let config, _ = parse toml
    Assert.Equal(SessionPersistence.MemoryOnly, config.Mode)

[<Fact>]
let ``mode = "session_log" parses as SessionLog`` () =
    let toml = "[session_model.persistence]\nmode = \"session_log\"\n"
    let config, _ = parse toml
    Assert.Equal(SessionPersistence.SessionLog, config.Mode)

[<Fact>]
let ``mode = "always" parses as Always`` () =
    let toml = "[session_model.persistence]\nmode = \"always\"\n"
    let config, _ = parse toml
    Assert.Equal(SessionPersistence.Always, config.Mode)

[<Fact>]
let ``mode is case-insensitive`` () =
    let toml = "[session_model.persistence]\nmode = \"SESSION_LOG\"\n"
    let config, _ = parse toml
    Assert.Equal(SessionPersistence.SessionLog, config.Mode)

[<Fact>]
let ``unknown mode value warns and falls back to MemoryOnly`` () =
    let toml = "[session_model.persistence]\nmode = \"garbage\"\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.MemoryOnly, config.Mode)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``non-string mode value warns and falls back to MemoryOnly`` () =
    let toml = "[session_model.persistence]\nmode = 42\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.MemoryOnly, config.Mode)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// format parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``format = "jsonl" parses as Jsonl`` () =
    let toml = "[session_model.persistence]\nformat = \"jsonl\"\n"
    let config, _ = parse toml
    Assert.Equal(SessionPersistence.Jsonl, config.Format)

[<Fact>]
let ``unknown format warns and falls back to Jsonl`` () =
    let toml = "[session_model.persistence]\nformat = \"xml\"\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.Jsonl, config.Format)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// output_dir parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``output_dir round-trips when set to a non-empty string`` () =
    let toml = "[session_model.persistence]\noutput_dir = \"D:\\\\sessions\"\n"
    let config, _ = parse toml
    Assert.Equal<string option>(Some "D:\\sessions", config.OutputDir)

[<Fact>]
let ``output_dir empty string falls back to default (None)`` () =
    let toml = "[session_model.persistence]\noutput_dir = \"\"\n"
    let config, logger = parse toml
    Assert.Equal<string option>(None, config.OutputDir)
    Assert.False(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``non-string output_dir warns and falls back to None`` () =
    let toml = "[session_model.persistence]\noutput_dir = 42\n"
    let config, logger = parse toml
    Assert.Equal<string option>(None, config.OutputDir)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// max_session_size_mb parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``max_session_size_mb round-trips when positive`` () =
    let toml = "[session_model.persistence]\nmax_session_size_mb = 256\n"
    let config, _ = parse toml
    Assert.Equal(256, config.MaxSessionSizeMb)

[<Fact>]
let ``zero max_session_size_mb warns and falls back to default`` () =
    let toml = "[session_model.persistence]\nmax_session_size_mb = 0\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.defaultConfig.MaxSessionSizeMb, config.MaxSessionSizeMb)
    Assert.True(logger.HasLevel(LogLevel.Warning))

[<Fact>]
let ``negative max_session_size_mb warns and falls back to default`` () =
    let toml = "[session_model.persistence]\nmax_session_size_mb = -10\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.defaultConfig.MaxSessionSizeMb, config.MaxSessionSizeMb)
    Assert.True(logger.HasLevel(LogLevel.Warning))

// ---------------------------------------------------------------------
// unknown key warning
// ---------------------------------------------------------------------

[<Fact>]
let ``unknown key under [session_model.persistence] warns but other fields still parse`` () =
    let toml =
        "[session_model.persistence]\n"
        + "mode = \"session_log\"\n"
        + "rotate_at_midnight = true\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.SessionLog, config.Mode)
    Assert.True(logger.HasLevel(LogLevel.Warning))
    let warnings = logger.MessagesAtLevel(LogLevel.Warning) |> Seq.toList
    let mentionsUnknownKey =
        warnings |> List.exists (fun m -> m.Contains("rotate_at_midnight"))
    Assert.True(mentionsUnknownKey, "Expected a Warning mentioning the unknown key 'rotate_at_midnight'.")

// ---------------------------------------------------------------------
// modeToString / formatToString round-trips
// ---------------------------------------------------------------------

[<Fact>]
let ``modeToString produces stable identifiers`` () =
    Assert.Equal("memory_only", SessionPersistence.modeToString SessionPersistence.MemoryOnly)
    Assert.Equal("session_log", SessionPersistence.modeToString SessionPersistence.SessionLog)
    Assert.Equal("always", SessionPersistence.modeToString SessionPersistence.Always)

[<Fact>]
let ``formatToString produces stable identifiers`` () =
    Assert.Equal("jsonl", SessionPersistence.formatToString SessionPersistence.Jsonl)

// ---------------------------------------------------------------------
// Composition: full table parses end-to-end
// ---------------------------------------------------------------------

[<Fact>]
let ``full [session_model.persistence] table parses every field`` () =
    let toml =
        "[session_model.persistence]\n"
        + "mode = \"always\"\n"
        + "output_dir = \"C:\\\\custom\\\\path\"\n"
        + "format = \"jsonl\"\n"
        + "max_session_size_mb = 128\n"
    let config, logger = parse toml
    Assert.Equal(SessionPersistence.Always, config.Mode)
    Assert.Equal<string option>(Some "C:\\custom\\path", config.OutputDir)
    Assert.Equal(SessionPersistence.Jsonl, config.Format)
    Assert.Equal(128, config.MaxSessionSizeMb)
    Assert.False(logger.HasLevel(LogLevel.Warning))
