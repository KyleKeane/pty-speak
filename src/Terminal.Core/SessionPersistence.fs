namespace Terminal.Core

open System
open Microsoft.Extensions.Logging
open Tomlyn.Model

/// Cycle 24a — TOML schema substrate for SessionModel persistence.
/// **Schema only; no I/O.** Cycle 24c is the sub-cycle that wires
/// the file writer; Cycle 24d adds `Always` synchronous-flush mode
/// + secrets sanitisation. This module just parses the
/// `[session_model.persistence]` table into a typed record so the
/// composition root can log the resolved mode and downstream
/// cycles have a config surface to consume.
///
/// **Pre-decided design** (per `docs/SESSION-MODEL.md` §"Persistence
/// modes" + the Cycle 23 Q4 resolution that picked `MemoryOnly` as
/// the default for privacy reasons):
///
/// - **Modes**: `memory_only` (default; History stays in RAM only),
///   `session_log` (flush each tuple on Active→History transition),
///   `always` (synchronous flush; audit-grade durability).
/// - **Format**: JSONL (newline-delimited JSON, one tuple per line).
///   Reserved as a DU so future binary / sqlite alternatives can
///   land without a schema-version bump.
/// - **Path**:
///   `%LOCALAPPDATA%\PtySpeak\sessions\session-<SessionId>.jsonl`
///   (resolved at write time in Cycle 24c; not consumed here).
/// - **Size cap**: `max_session_size_mb` bounds a single session
///   file before rotation. Default 64MB.
///
/// **Why this lives in its own module** rather than inside
/// `Config.fs`: keeps SessionModel-shaped TOML parsing colocated
/// with the rest of the SessionModel substrate (`SessionModel.fs`
/// is the prior file in the compile order). The Tomlyn helpers
/// here are intentionally a small private subset duplicating
/// `Config.fs`'s `tryGetTable` / `tryGetString` / `tryGetInt`
/// pattern — those helpers are `private` to `Config` and `Config`
/// compiles AFTER this module, so reuse via cross-module access
/// isn't available. The duplication is ~25 lines of trivial
/// type-test boilerplate; promoting it into a shared
/// `TomlHelpers` module is reserved for if/when a third consumer
/// appears.
module SessionPersistence =

    /// Persistence mode controlling when (and whether) tuples are
    /// flushed from in-memory `History` to disk. Cycle 24a only
    /// parses the value; Cycle 24c (`session_log`) and Cycle 24d
    /// (`always`) wire the actual flush behaviour.
    type PersistenceMode =
        /// Default. History stays in RAM; nothing written to disk.
        | MemoryOnly
        /// Each completed `SessionTuple` is flushed to disk on
        /// Active→History transition (asynchronous, bounded
        /// channel; Cycle 24c).
        | SessionLog
        /// Synchronous flush on Active→History; the transition
        /// blocks until the write completes. Audit-grade
        /// durability (Cycle 24d).
        | Always

    /// Persistence wire format. Single variant today; reserved as
    /// a DU so future formats (binary, sqlite, parquet) can land
    /// without a `schema_version` bump on the consumer side.
    type PersistenceFormat =
        | Jsonl

    /// Parsed `[session_model.persistence]` table.
    /// `OutputDir = None` means "use the default
    /// `%LOCALAPPDATA%\PtySpeak\sessions\` path"; Cycle 24c
    /// resolves the actual path at write time.
    type PersistenceConfig =
        { Mode: PersistenceMode
          OutputDir: string option
          Format: PersistenceFormat
          MaxSessionSizeMb: int }

    /// The all-defaults PersistenceConfig — equivalent to "no
    /// `[session_model.persistence]` table present". Privacy-by-
    /// default: `MemoryOnly` so a brand-new install never writes
    /// session content to disk without explicit opt-in.
    let defaultConfig: PersistenceConfig =
        { Mode = MemoryOnly
          OutputDir = None
          Format = Jsonl
          MaxSessionSizeMb = 64 }

    /// Internal — try to read a sub-table by key. Mirrors
    /// `Config.fs`'s private `tryGetTable`; duplicated because
    /// `Config.fs` compiles after this module.
    let private tryGetTable (table: TomlTable) (key: string) : TomlTable option =
        match table.TryGetValue(key) with
        | true, (:? TomlTable as t) -> Some t
        | _ -> None

    /// Internal — try to read a string value. Mirrors
    /// `Config.fs`'s private `tryGetString`.
    let private tryGetString (table: TomlTable) (key: string) : string option =
        match table.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | _ -> None

    /// Internal — try to read an int64 value. Tomlyn boxes TOML
    /// integers as `int64` (the only TOML integer width). Mirrors
    /// `Config.fs`'s private `tryGetInt`.
    let private tryGetInt (table: TomlTable) (key: string) : int64 option =
        match table.TryGetValue(key) with
        | true, (:? int64 as i) -> Some i
        | _ -> None

    /// Internal — recognised keys for the
    /// `[session_model.persistence]` table. Used to surface typos
    /// via warn-and-ignore.
    /// String form of a `PersistenceMode` for log messages and
    /// diagnostic dumps. Stable identifiers (no localisation) so
    /// log-grepping is reliable across builds.
    let modeToString (mode: PersistenceMode) : string =
        match mode with
        | MemoryOnly -> "memory_only"
        | SessionLog -> "session_log"
        | Always -> "always"

    /// String form of a `PersistenceFormat` for log messages.
    let formatToString (format: PersistenceFormat) : string =
        match format with
        | Jsonl -> "jsonl"

    let private knownKeys : Set<string> =
        Set.ofList
            [ "mode"
              "output_dir"
              "format"
              "max_session_size_mb" ]

    /// Internal — parse the `mode` value. Unknown / non-string /
    /// missing values fall back to default with a Warning where
    /// applicable.
    let private parseMode
            (logger: ILogger)
            (table: TomlTable)
            : PersistenceMode
            =
        match tryGetString table "mode" with
        | None ->
            if table.ContainsKey("mode") then
                logger.LogWarning(
                    "Config: [session_model.persistence] mode is non-string; using default 'memory_only'.")
            defaultConfig.Mode
        | Some raw ->
            match raw.ToLowerInvariant() with
            | "memory_only" -> MemoryOnly
            | "session_log" -> SessionLog
            | "always" -> Always
            | other ->
                logger.LogWarning(
                    "Config: [session_model.persistence] mode = '{Value}' is not one of 'memory_only' / 'session_log' / 'always'; using default 'memory_only'.",
                    other)
                defaultConfig.Mode

    /// Internal — parse the `format` value. Single recognised
    /// value today (`jsonl`); reserved for future expansion.
    let private parseFormat
            (logger: ILogger)
            (table: TomlTable)
            : PersistenceFormat
            =
        match tryGetString table "format" with
        | None ->
            if table.ContainsKey("format") then
                logger.LogWarning(
                    "Config: [session_model.persistence] format is non-string; using default 'jsonl'.")
            defaultConfig.Format
        | Some raw ->
            match raw.ToLowerInvariant() with
            | "jsonl" -> Jsonl
            | other ->
                logger.LogWarning(
                    "Config: [session_model.persistence] format = '{Value}' is not one of 'jsonl'; using default 'jsonl'.",
                    other)
                defaultConfig.Format

    /// Internal — parse `max_session_size_mb`. Negative / zero /
    /// out-of-Int32-range values fall back to the default with a
    /// Warning.
    let private parseMaxSessionSizeMb
            (logger: ILogger)
            (table: TomlTable)
            : int
            =
        match tryGetInt table "max_session_size_mb" with
        | None ->
            if table.ContainsKey("max_session_size_mb") then
                logger.LogWarning(
                    "Config: [session_model.persistence] max_session_size_mb is non-integer; using default {Default}.",
                    defaultConfig.MaxSessionSizeMb)
            defaultConfig.MaxSessionSizeMb
        | Some raw ->
            if raw <= 0L then
                logger.LogWarning(
                    "Config: [session_model.persistence] max_session_size_mb = {Value} is non-positive; using default {Default}.",
                    raw, defaultConfig.MaxSessionSizeMb)
                defaultConfig.MaxSessionSizeMb
            elif raw > int64 Int32.MaxValue then
                logger.LogWarning(
                    "Config: [session_model.persistence] max_session_size_mb = {Value} exceeds Int32.MaxValue; using default {Default}.",
                    raw, defaultConfig.MaxSessionSizeMb)
                defaultConfig.MaxSessionSizeMb
            else
                int raw

    /// Internal — parse `output_dir`. Empty string is treated as
    /// "no override" (silent; an empty TOML string is more likely
    /// a placeholder than a configuration intent). Non-string
    /// values warn-and-ignore.
    let private parseOutputDir
            (logger: ILogger)
            (table: TomlTable)
            : string option
            =
        match tryGetString table "output_dir" with
        | None ->
            if table.ContainsKey("output_dir") then
                logger.LogWarning(
                    "Config: [session_model.persistence] output_dir is non-string; using default path.")
            defaultConfig.OutputDir
        | Some raw ->
            if String.IsNullOrWhiteSpace(raw) then
                defaultConfig.OutputDir
            else
                Some raw

    /// Parse the `[session_model.persistence]` sub-table out of
    /// the supplied root TOML table. Missing root key →
    /// `defaultConfig`; missing sub-table → `defaultConfig`. All
    /// per-field errors warn-and-fall-back; this function never
    /// throws.
    ///
    /// Mirrors the warn-on-unknown-key + log-and-drop-bad-value
    /// pattern from `Config.parseStartupOverrides`.
    let parseFromTable
            (logger: ILogger)
            (root: TomlTable)
            : PersistenceConfig
            =
        // Cycle 24f — log every branch at Information so a
        // maintainer can diagnose the difference between
        // "section absent → silent defaults" and "section
        // parsed cleanly to memory_only" by reading the
        // FileLogger log alone. Pre-24f both cases were
        // observationally identical (the only line emitted
        // was the composition root's `SessionModel persistence
        // mode: memory_only ...`).
        match tryGetTable root "session_model" with
        | None ->
            logger.LogInformation(
                "Config: no [session_model] section in TOML; using session-persistence defaults (mode={Mode}).",
                modeToString defaultConfig.Mode)
            defaultConfig
        | Some sessionModelTable ->
            match tryGetTable sessionModelTable "persistence" with
            | None ->
                logger.LogInformation(
                    "Config: [session_model] present but no [session_model.persistence] sub-section; using session-persistence defaults (mode={Mode}).",
                    modeToString defaultConfig.Mode)
                defaultConfig
            | Some persistenceTable ->
                for key in persistenceTable.Keys do
                    if not (knownKeys.Contains(key)) then
                        logger.LogWarning(
                            "Config: [session_model.persistence] unknown key '{Key}'; ignored.",
                            key)
                let parsed =
                    { Mode = parseMode logger persistenceTable
                      OutputDir = parseOutputDir logger persistenceTable
                      Format = parseFormat logger persistenceTable
                      MaxSessionSizeMb = parseMaxSessionSizeMb logger persistenceTable }
                logger.LogInformation(
                    "Config: [session_model.persistence] section parsed; mode={Mode}, output_dir={OutputDir}, format={Format}, max_session_size_mb={MaxSessionSizeMb}.",
                    modeToString parsed.Mode,
                    (match parsed.OutputDir with
                     | Some d -> d
                     | None -> "<default>"),
                    formatToString parsed.Format,
                    parsed.MaxSessionSizeMb)
                parsed
