namespace Terminal.Core

open System
open System.IO
open Microsoft.Extensions.Logging
open Tomlyn
open Tomlyn.Model

/// Phase B (subset, "C2") — minimal TOML config substrate for
/// pathway selection + per-pathway parameter overrides.
///
/// **What this module owns.** Loading, parsing, and resolving
/// the user's `%LOCALAPPDATA%\PtySpeak\config.toml` file. The
/// loader is loud-via-log but never crashes: every error mode
/// (missing file, malformed TOML, unknown keys, schema-version
/// mismatch, out-of-range values) falls back to the hardcoded
/// defaults that match `StreamPathway.defaultParameters`
/// field-for-field. Absence of a config file is byte-equivalent
/// to pre-C2 behaviour. The maintainer is a screen-reader user;
/// every error path goes through `ILogger`, never through a GUI
/// dialog.
///
/// **What this module does NOT own.** The pathway-name →
/// constructor map (lives in `Program.fs`'s composition root —
/// it's where `StreamPathway` and `TuiPathway` get wired with
/// the resolved parameters). Hot-reload (changes require a
/// restart). Runtime config write-back (Phase 2/3 UI work).
/// The kill-switch substrate (Phase B input-bindings owns
/// that). Per-content-type triggers (Phase 2/3 — needs actual
/// pathways that consume them).
///
/// **Schema (v1).** See `docs/USER-SETTINGS.md` "Pathway
/// selection" for the user-facing reference. Two table
/// families:
/// - `[shell.<id>]` — per-shell pathway override (`pathway`
///   key is a pathway ID string).
/// - `[pathway.<id>]` — per-pathway parameter overrides
///   (`debounce_window_ms`, `spinner_window_ms`,
///   `spinner_threshold`, `max_announce_chars` for `stream`
///   today; reserved-but-empty for `tui`).
///
/// **Forward-compat.** Spec
/// `event-and-output-framework.md` A.5 sketches a future schema
/// for input-binding overrides (`[[bindings]]` /
/// `[[handlers]]` arrays at top level). The C2 loader silently
/// ignores those sections so a single `config.toml` can grow
/// cumulatively across Phase B sub-stages without
/// modification.
///
/// **Library.** Tomlyn 0.18 (xoofx, BSD 2-Clause). Uses the
/// non-throwing `Toml.Parse` entry point (returns a
/// `DocumentSyntax` carrying errors as data) rather than
/// `Toml.ToModel(string)` (throws `TomlException` on parse
/// failure). The two-step `Parse → check HasErrors → ToModel`
/// keeps the loader exception-free for the "file is malformed"
/// path.
module Config =

    /// The schema version this build supports. A `config.toml`
    /// with `schema_version = N` for `N > CurrentSchemaVersion`
    /// is rejected with an Error log + fallback to defaults
    /// (per spec A.5: "the TOML schema includes an explicit
    /// version field so older pty-speak versions can refuse to
    /// parse a TOML written for a future schema"). A missing
    /// `schema_version` key is treated as 1 with a Warning;
    /// older values are best-effort-parsed with a Warning.
    [<Literal>]
    let CurrentSchemaVersion: int = 1

    /// Per-shell pathway override. Captures the pathway ID
    /// string (`"stream"`, `"tui"`, future `"claude-code"`,
    /// etc.) the user wants for a given shell key. The
    /// pathway-name → constructor map lives in
    /// `Program.fs`'s composition root, not here — Config
    /// stays free of `StreamPathway` / `TuiPathway` direct
    /// references on the SHELL side so adding a new pathway in
    /// Phase 2 doesn't require a Config-module change.
    type ShellPathwayConfig = { PathwayId: string }

    /// Optional overrides for `StreamPathway.Parameters`. Each
    /// field is `int option` so the resolver can merge with
    /// `StreamPathway.defaultParameters` field-by-field — a
    /// user who overrides only `debounce_window_ms` keeps the
    /// other three at their hardcoded defaults.
    type StreamParameterOverrides =
        { DebounceWindowMs: int option
          SpinnerWindowMs: int option
          SpinnerThreshold: int option
          MaxAnnounceChars: int option }

    /// The top-level config record. `ShellOverrides` is keyed
    /// by lowercase shell-id strings (`"cmd"`, `"claude"`,
    /// `"powershell"` — mirrors `ShellRegistry.parseEnvVar`'s
    /// lowercase recognition). Using `string` rather than
    /// `ShellRegistry.ShellId` keeps Terminal.Core free of a
    /// `Terminal.Pty` dependency.
    type Config =
        { SchemaVersion: int
          ShellOverrides: Map<string, ShellPathwayConfig>
          StreamOverrides: StreamParameterOverrides }

    /// The all-defaults Config — equivalent to "no config file
    /// present". `defaultConfig` is the authoritative source
    /// of truth for the byte-equivalence test in
    /// `ConfigTests.fs`; if `StreamPathway.defaultParameters`
    /// changes, this module's resolver picks up the new
    /// defaults automatically (no constants are duplicated).
    let defaultConfig: Config =
        { SchemaVersion = CurrentSchemaVersion
          ShellOverrides = Map.empty
          StreamOverrides =
            { DebounceWindowMs = None
              SpinnerWindowMs = None
              SpinnerThreshold = None
              MaxAnnounceChars = None } }

    /// The default config file path —
    /// `%LOCALAPPDATA%\PtySpeak\config.toml`. Mirrors the
    /// FileLogger convention (`%LOCALAPPDATA%\PtySpeak\logs\`).
    /// Falls back to `Path.GetTempPath()` if `LOCALAPPDATA` is
    /// unset — same defensive pattern FileLogger uses.
    let defaultConfigFilePath () : string =
        let envValue =
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
        let baseDir =
            match envValue with
            | null -> Path.GetTempPath()
            | "" -> Path.GetTempPath()
            | v -> v
        Path.Combine(baseDir, "PtySpeak", "config.toml")

    /// Internal — try to read an `int64` value from a
    /// TomlTable by key. Returns `None` if absent or
    /// non-integer. Tomlyn boxes integers as `int64` (TOML's
    /// only integer width), so we cast to `int64` and
    /// downcast to `int` at the consumer site (parameter
    /// values fit comfortably in 32 bits).
    let private tryGetInt (table: TomlTable) (key: string) : int64 option =
        let mutable value : obj | null = null
        if table.TryGetValue(key, &value) then
            match value with
            | null -> None
            | :? int64 as i -> Some i
            | _ -> None
        else
            None

    /// Internal — try to read a `string` value from a
    /// TomlTable by key. Returns `None` if absent or
    /// non-string.
    let private tryGetString (table: TomlTable) (key: string) : string option =
        let mutable value : obj | null = null
        if table.TryGetValue(key, &value) then
            match value with
            | null -> None
            | :? string as s -> Some s
            | _ -> None
        else
            None

    /// Internal — try to read a sub-table by key. Returns
    /// `None` if absent or non-table.
    let private tryGetTable (table: TomlTable) (key: string) : TomlTable option =
        let mutable value : obj | null = null
        if table.TryGetValue(key, &value) then
            match value with
            | null -> None
            | :? TomlTable as t -> Some t
            | _ -> None
        else
            None

    /// Internal — known parameter keys for `[pathway.stream]`.
    /// Used to detect typos and log Warnings for unknown keys.
    let private knownStreamKeys : Set<string> =
        Set.ofList
            [ "debounce_window_ms"
              "spinner_window_ms"
              "spinner_threshold"
              "max_announce_chars" ]

    /// Internal — known shell-id keys for `[shell.<id>]`.
    /// Mirrors `ShellRegistry.parseEnvVar`'s lowercase
    /// recognition; unknown shells log a Warning and are
    /// ignored (don't crash on a future Wsl/Bash addition that
    /// hasn't shipped yet).
    let private knownShellKeys : Set<string> =
        Set.ofList [ "cmd"; "claude"; "powershell" ]

    /// Internal — parse a positive int parameter override.
    /// Returns `None` (with Warning log) for negative or zero
    /// values; the resolver then falls back to the default for
    /// that field. Negative debounce makes no sense; the user
    /// is more likely to have a typo than an intentional
    /// override, and a clamped fallback is safer than letting
    /// `TimeSpan.FromMilliseconds(-50.0)` propagate into the
    /// pathway's debounce arithmetic.
    let private parsePositiveInt
            (logger: ILogger)
            (section: string)
            (key: string)
            (raw: int64)
            : int option
            =
        if raw <= 0L then
            logger.LogWarning(
                "Config: {Section}.{Key} = {Value} is non-positive; clamping to default.",
                section, key, raw)
            None
        elif raw > int64 Int32.MaxValue then
            logger.LogWarning(
                "Config: {Section}.{Key} = {Value} exceeds Int32.MaxValue; clamping to default.",
                section, key, raw)
            None
        else
            Some (int raw)

    /// Internal — parse the `[pathway.stream]` table into
    /// `StreamParameterOverrides`. Unknown keys produce a
    /// Warning + are dropped; non-int values produce a
    /// Warning + fall back to default for that field. Returns
    /// the override record (each field `Some` if user-set
    /// + valid, `None` otherwise).
    let private parseStreamOverrides
            (logger: ILogger)
            (table: TomlTable)
            : StreamParameterOverrides
            =
        // Walk the user-supplied keys to surface typos.
        for key in table.Keys do
            if not (knownStreamKeys.Contains(key)) then
                logger.LogWarning(
                    "Config: [pathway.stream] unknown key '{Key}'; ignored.",
                    key)
        let readField (key: string) : int option =
            match tryGetInt table key with
            | None ->
                let mutable value : obj | null = null
                if table.TryGetValue(key, &value) then
                    logger.LogWarning(
                        "Config: [pathway.stream] {Key} is non-integer; ignored.",
                        key)
                None
            | Some raw -> parsePositiveInt logger "[pathway.stream]" key raw
        { DebounceWindowMs = readField "debounce_window_ms"
          SpinnerWindowMs = readField "spinner_window_ms"
          SpinnerThreshold = readField "spinner_threshold"
          MaxAnnounceChars = readField "max_announce_chars" }

    /// Internal — parse the `[shell.X]` family into
    /// `Map<string, ShellPathwayConfig>`. Unknown shell keys
    /// produce a Warning + are dropped. Each shell table must
    /// contain a `pathway` string key; shells without one are
    /// skipped (no override).
    let private parseShellOverrides
            (logger: ILogger)
            (root: TomlTable)
            : Map<string, ShellPathwayConfig>
            =
        match tryGetTable root "shell" with
        | None -> Map.empty
        | Some shellRoot ->
            let mutable result = Map.empty
            for key in shellRoot.Keys do
                if not (knownShellKeys.Contains(key)) then
                    logger.LogWarning(
                        "Config: [shell.{Key}] unknown shell id; ignored.",
                        key)
                else
                    match tryGetTable shellRoot key with
                    | None ->
                        logger.LogWarning(
                            "Config: [shell.{Key}] is not a table; ignored.",
                            key)
                    | Some shellTable ->
                        match tryGetString shellTable "pathway" with
                        | None -> ()
                        | Some pathwayId ->
                            result <-
                                result
                                |> Map.add key { PathwayId = pathwayId }
            result

    /// Internal — parse the schema_version field. Missing → 1
    /// with Warning. Newer than CurrentSchemaVersion → Error +
    /// raises so the caller can fall back to defaults. Older
    /// → best-effort with Warning.
    ///
    /// Returns the parsed version. The caller (`tryLoad`)
    /// handles the "newer than supported" case by checking
    /// the returned version and short-circuiting to defaults.
    let private parseSchemaVersion
            (logger: ILogger)
            (root: TomlTable)
            : int
            =
        match tryGetInt root "schema_version" with
        | None ->
            // Distinguish "key absent" from "key present but
            // non-integer" so the user can spot type errors.
            let mutable value : obj | null = null
            if root.TryGetValue("schema_version", &value) then
                logger.LogWarning(
                    "Config: schema_version is non-integer; treating as {Default}.",
                    CurrentSchemaVersion)
            else
                logger.LogWarning(
                    "Config: schema_version missing; treating as {Default}.",
                    CurrentSchemaVersion)
            CurrentSchemaVersion
        | Some raw ->
            let v = int raw
            if v > CurrentSchemaVersion then
                // Caller short-circuits to defaults on this branch.
                v
            elif v < CurrentSchemaVersion then
                logger.LogWarning(
                    "Config: schema_version {Version} is older than supported {Current}; best-effort parse.",
                    v, CurrentSchemaVersion)
                v
            else
                v

    /// Try to load + parse the config file at `filePath`.
    /// Always returns a `Config`; never throws. All error modes
    /// log via `logger` and fall back to defaults:
    ///
    /// | Condition | Log level | Outcome |
    /// |---|---|---|
    /// | File does not exist | Information | `defaultConfig` |
    /// | Malformed TOML | Error (with parse detail) | `defaultConfig` |
    /// | `schema_version` newer than supported | Error | `defaultConfig` |
    /// | `schema_version` older | Warning | best-effort parse |
    /// | Unknown `[shell.<key>]` | Warning | skip section |
    /// | Unknown pathway name | Warning (resolver-side) | shell falls to default |
    /// | Unknown parameter key | Warning | drop value |
    /// | Negative/zero parameter | Warning ("clamped") | use default for field |
    let tryLoad (logger: ILogger) (filePath: string) : Config =
        if not (File.Exists(filePath)) then
            logger.LogInformation(
                "Config: no file at {Path}; using defaults.",
                filePath)
            defaultConfig
        else
            let text =
                try Some (File.ReadAllText(filePath))
                with ex ->
                    logger.LogError(
                        ex,
                        "Config: read failed for {Path}; using defaults.",
                        filePath)
                    None
            match text with
            | None -> defaultConfig
            | Some content ->
                let doc = Toml.Parse(content)
                if doc.HasErrors then
                    let detail =
                        doc.Diagnostics
                        |> Seq.map (fun d -> d.ToString())
                        |> String.concat "; "
                    logger.LogError(
                        "Config: parse error in {Path}; using defaults. Detail={Detail}",
                        filePath, detail)
                    defaultConfig
                else
                    let model = doc.ToModel()
                    let version = parseSchemaVersion logger model
                    if version > CurrentSchemaVersion then
                        logger.LogError(
                            "Config: schema_version {Version} newer than supported {Current}; using defaults.",
                            version, CurrentSchemaVersion)
                        defaultConfig
                    else
                        let shellOverrides =
                            parseShellOverrides logger model
                        let streamOverrides =
                            match tryGetTable model "pathway" with
                            | None -> defaultConfig.StreamOverrides
                            | Some pathwayRoot ->
                                match tryGetTable pathwayRoot "stream" with
                                | None -> defaultConfig.StreamOverrides
                                | Some streamTable ->
                                    parseStreamOverrides logger streamTable
                        let result =
                            { SchemaVersion = version
                              ShellOverrides = shellOverrides
                              StreamOverrides = streamOverrides }
                        // One Information line summarising the
                        // resolved config so post-hoc diagnosis
                        // via Ctrl+Shift+; is trivial. The
                        // shell-pathway summary lists overrides
                        // only (defaults are silent).
                        let overrideSummary =
                            if Map.isEmpty result.ShellOverrides then
                                "no shell overrides"
                            else
                                result.ShellOverrides
                                |> Map.toSeq
                                |> Seq.map (fun (k, v) -> sprintf "%s→%s" k v.PathwayId)
                                |> String.concat ", "
                        logger.LogInformation(
                            "Config loaded from {Path}: {Overrides}.",
                            filePath, overrideSummary)
                        result

    /// Resolve the pathway ID for a given shell key (lowercase:
    /// `"cmd"`, `"claude"`, `"powershell"`). Returns the
    /// configured pathway name if present, else `"stream"` as
    /// the substrate-wide default. Caller (`Program.fs`'s
    /// `selectPathwayForShell`) maps the returned string to a
    /// `DisplayPathway.T` instance.
    let resolveShellPathway (config: Config) (shellKey: string) : string =
        match Map.tryFind shellKey config.ShellOverrides with
        | Some entry -> entry.PathwayId
        | None -> "stream"

    /// Resolve the StreamPathway parameters from a Config.
    /// Each field that's `None` in `StreamOverrides` falls
    /// back to `StreamPathway.defaultParameters`'s value.
    let resolveStreamParameters
            (config: Config)
            : StreamPathway.Parameters
            =
        let defaults = StreamPathway.defaultParameters
        let ov = config.StreamOverrides
        { DebounceWindowMs =
            Option.defaultValue defaults.DebounceWindowMs ov.DebounceWindowMs
          SpinnerWindowMs =
            Option.defaultValue defaults.SpinnerWindowMs ov.SpinnerWindowMs
          SpinnerThreshold =
            Option.defaultValue defaults.SpinnerThreshold ov.SpinnerThreshold
          MaxAnnounceChars =
            Option.defaultValue defaults.MaxAnnounceChars ov.MaxAnnounceChars }
