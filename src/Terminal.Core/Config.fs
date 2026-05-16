namespace Terminal.Core

open System
open System.IO
open System.Text
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
    type ShellPathwayConfig =
        { PathwayId: string
          /// Cycle 38b — per-shell profile-set override. `None`
          /// means the shell uses the composition-root default of
          /// `["passthrough", "earcon", "selection"]`. `Some [|...|]`
          /// overrides with the listed profile IDs (validated
          /// against the registered `ProfileId`s at composition
          /// time; unknown IDs are logged + dropped).
          Profiles: string[] option
          /// Cycle 45f — per-shell streaming announce mode.
          /// `None` falls back to `ShellPolicy.forShell` defaults.
          /// TOML values: `"tuple_final"`, `"line_by_line"`,
          /// `"off"`. Unknown / non-string values log a Warning
          /// and fall back.
          Verbosity: ShellPolicy.StreamingMode option
          /// Cycle 45f — per-shell prompt-path verbosity. `None`
          /// falls back to `ShellPolicy.forShell` defaults. TOML
          /// values: `"suppress"`, `"final_dir_only"`, `"full"`.
          /// Unknown / non-string values log a Warning and fall
          /// back.
          PromptPath: ShellPolicy.PromptPathMode option }

    /// Cycle 19 — `[startup]` TOML section: composition-root
    /// startup-shell override. When `DefaultShell` is `Some`,
    /// the composition root's `resolveStartupShell` uses it
    /// in PREFERENCE to the `PTYSPEAK_SHELL` env var. Use
    /// case: maintainer has `PTYSPEAK_SHELL=claude` set from
    /// prior testing + wants cmd as durable default without
    /// manipulating env vars (per the 2026-05-08 testing
    /// session). String-keyed (lowercase shell id) per the
    /// existing `ShellOverrides` discipline; valid values
    /// match `ShellRegistry.parseEnvVar`'s recognition list
    /// (`"cmd"`, `"powershell"`, `"pwsh"`, `"claude"`).
    /// Unknown values logged + dropped at parse time.
    type StartupOverrides =
        { DefaultShell: string option }

    /// Cycle 25a — `[logging]` table. Single key today
    /// (`min_level`); reserved for future logging knobs (e.g.
    /// `retention_days`, `max_file_size_mb`). The minimum log
    /// level is captured at composition-root time and applied
    /// to the `FileLoggerSink` after the sink is constructed
    /// (the sink starts at the env-var-or-Information default
    /// because it must be ready BEFORE Config loads — Config
    /// loading itself emits log lines via the sink).
    ///
    /// **Precedence**: `PTYSPEAK_LOG_LEVEL` env var (read at
    /// startup) > TOML `min_level` > built-in default
    /// (`Information`). Mirrors the established
    /// `[startup] default_shell` / `PTYSPEAK_SHELL` pattern.
    /// `Ctrl+Shift+G` runtime toggle overrides both for the
    /// running session; doesn't persist.
    type LoggingOverrides =
        { /// Resolved minimum log level. `None` when the user
          /// hasn't specified a value in TOML; the composition
          /// root then leaves the sink at its env-var-or-default
          /// level. `Some <level>` when TOML specified a value;
          /// the composition root applies it via
          /// `sink.SetMinLevel` IF the env var was not set.
          MinLevel: LogLevel option }

    /// Cycle 32a — `[profile.selection]` table overrides for
    /// `SelectionDetector.Parameters` (defined at
    /// `SelectionDetector.fs:128-148`). `None` fields fall back
    /// to `SelectionDetector.defaultParameters` at the resolver
    /// (`resolveSelectionParameters` below). The detector is
    /// constructed once at startup with the resolved parameters
    /// (`Program.fs` composition root); no hot-reload — matches
    /// every other section except `[session_model.persistence]`,
    /// which gets reloaded on shell-switch.
    type SelectionParameterOverrides =
        { HighlightDetectionThresholdMs: int option
          DismissalGraceMs: int option
          KeystrokeCorrelationWindowMs: int option
          MinConfidence: SelectionDetector.SelectionSource option }

    /// The top-level config record. `ShellOverrides` is keyed
    /// by lowercase shell-id strings (`"cmd"`, `"claude"`,
    /// `"powershell"` — mirrors `ShellRegistry.parseEnvVar`'s
    /// lowercase recognition). Using `string` rather than
    /// `ShellRegistry.ShellId` keeps Terminal.Core free of a
    /// `Terminal.Pty` dependency.
    type Config =
        { SchemaVersion: int
          ShellOverrides: Map<string, ShellPathwayConfig>
          StartupOverrides: StartupOverrides
          /// Cycle 24a — `[session_model.persistence]` table.
          /// Pure config substrate; Cycles 24b-d wire actual I/O.
          SessionPersistence: SessionPersistence.PersistenceConfig
          /// Cycle 25a — `[logging]` table. Resolved at
          /// composition-root time; the FileLogger sink's
          /// `SetMinLevel` is called post-Config-load when this
          /// is `Some` AND `PTYSPEAK_LOG_LEVEL` is unset.
          LoggingOverrides: LoggingOverrides
          /// Cycle 32a — `[profile.selection]` table.
          /// Resolved via `resolveSelectionParameters`;
          /// composition root passes the result to
          /// `SelectionDetector.create` at startup.
          SelectionOverrides: SelectionParameterOverrides }

    /// The all-defaults Config — equivalent to "no config file
    /// present".
    let defaultConfig: Config =
        { SchemaVersion = CurrentSchemaVersion
          ShellOverrides = Map.empty
          StartupOverrides =
            { DefaultShell = None }
          SessionPersistence = SessionPersistence.defaultConfig
          LoggingOverrides = { MinLevel = None }
          SelectionOverrides =
            { HighlightDetectionThresholdMs = None
              DismissalGraceMs = None
              KeystrokeCorrelationWindowMs = None
              MinConfidence = None } }

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

    /// Cycle 25a — boilerplate `config.toml` content for the
    /// open-config hotkey's create-then-open flow. Every value
    /// matches the in-process `defaultConfig`, so writing this
    /// file produces no observable behaviour change on the next
    /// launch — but the maintainer now has every knob discoverable
    /// in one file with inline comments. Written verbatim with
    /// `\r\n` line endings and `UTF8Encoding(false)` so the
    /// parser sees the bytes byte-for-byte.
    let internal defaultsTemplate : string =
        String.concat "\r\n" [
            "# pty-speak configuration."
            "# Auto-generated by Ctrl+Shift+E when no config existed."
            "# Edit any value, save, and either restart or use Ctrl+Shift+1/2/3"
            "# to reload the dynamic sections (currently: session_model.persistence)."
            ""
            "schema_version = 1"
            ""
            "[session_model.persistence]"
            "# mode: how IOCells are persisted to disk."
            "#   \"memory_only\" — no file written; History is in-process only."
            "#   \"session_log\" — async append (bounded-channel writer)."
            "#   \"always\"      — synchronous flush per tuple (audit-grade)."
            "mode = \"session_log\""
            ""
            "# output_dir: directory for session-log files."
            "# Default %LOCALAPPDATA%\\PtySpeak\\sessions; uncomment to override."
            "# output_dir = \"C:\\\\absolute\\\\path\\\\here\""
            ""
            "# max_session_size_mb: rotation threshold (deferred; currently unused)."
            "max_session_size_mb = 64"
            ""
            "[startup]"
            "# default_shell: which shell to spawn at startup."
            "# Allowed: \"cmd\" | \"powershell\" | \"pwsh\" | \"claude\""
            "# PTYSPEAK_SHELL env var overrides this if set."
            "default_shell = \"cmd\""
            ""
            "[logging]"
            "# min_level: minimum log level captured by the FileLogger."
            "# Allowed: \"Trace\" | \"Debug\" | \"Information\" | \"Warning\" | \"Error\" | \"Critical\" | \"None\""
            "# PTYSPEAK_LOG_LEVEL env var overrides this if set."
            "# Ctrl+Shift+G runtime-toggles between Debug and Information for the"
            "# current session only; doesn't persist."
            "min_level = \"Information\""
            ""
            "# Cycle 38b — per-shell profile-set overrides."
            "# Each [shell.<key>] table may specify `profiles = [...]`"
            "# to override the composition-root default. Profile IDs:"
            "#   \"passthrough\" — fan every OutputEvent to NVDA + FileLogger."
            "#   \"earcon\"      — bell-ping / error-tone / warning-tone."
            "#   \"selection\"   — Claude tool-use prompts as ControlType.List."
            "# Default (applied when `profiles` is unset for any shell):"
            "#   [\"passthrough\", \"earcon\", \"selection\"]"
            "# Uncomment + edit to override."
            "# [shell.cmd]"
            "# profiles = [\"passthrough\", \"earcon\", \"selection\"]"
            ""
            "# Cycle 45f — per-shell verbosity modes."
            "# Each [shell.<key>] table may specify `verbosity` and"
            "# `prompt_path` to govern how NVDA narrates that shell's"
            "# output. Both keys are independent and optional; omitted"
            "# keys fall back to the compiled defaults (cmd / PowerShell"
            "# = \"tuple_final\" + \"suppress\"; claude = same)."
            "#"
            "# verbosity values:"
            "#   \"tuple_final\"  — narrate IOCell.OutputText on"
            "#                     each tuple seal (cmd / PowerShell"
            "#                     default; matches Cycle 45 behaviour)."
            "#   \"line_by_line\" — narrate each TextSpan as it seals."
            "#                     Intended for streaming-heavy shells"
            "#                     (Claude / `ping -t`). Opting cmd in"
            "#                     to this re-introduces cmd's"
            "#                     edit-conflation regression (PR #268)."
            "#   \"off\"          — suppress every streaming + tuple"
            "#                     announce. Manual SpeechCursor"
            "#                     navigation is the only way to hear"
            "#                     output."
            "#"
            "# prompt_path values:"
            "#   \"suppress\"        — narrate nothing on PromptStart"
            "#                        (default; the user knows where"
            "#                        they are)."
            "#   \"final_dir_only\"  — trim path-like prompts to the"
            "#                        last directory + delimiter"
            "#                        (e.g. \"Local>\" from"
            "#                        \"C:\\Users\\Kyle\\Local>\")."
            "#   \"full\"            — narrate the prompt verbatim."
            "#"
            "# Menu changes via `View → Output Verbosity` /"
            "# `View → Prompt Path` are runtime overrides (Layer 3);"
            "# this TOML file is Layer 2 and persists across restarts."
            "# See docs/USER-SETTINGS.md \"Verbosity\" for the full"
            "# three-layer resolution model."
            "# [shell.claude]"
            "# verbosity = \"line_by_line\""
            "# prompt_path = \"suppress\""
            ""
        ]

    /// Cycle 25a — write `defaultsTemplate` to `filePath` if no
    /// file exists there; idempotent (refuses to overwrite an
    /// existing file). Used by the open-config hotkey's
    /// create-then-open flow.
    ///
    /// **Encoding**: `UTF8Encoding(false)` (no BOM) — Tomlyn's
    /// parser is BOM-tolerant in 0.18 but the matching parser/
    /// writer convention across this codebase is no-BOM (mirrors
    /// `SessionLogWriter`'s `UTF8Encoding(false)`). A BOM here
    /// would NOT cause silent invisibility (the maintainer's
    /// `[sessionmodel._persistence]` typo from the Cycle 24 debug
    /// loop was character-position, NOT BOM) but staying
    /// consistent prevents future encoding mismatches.
    ///
    /// **Directory**: creates the parent directory tree
    /// idempotently (mirrors `FileLogger`'s defensive pattern).
    /// On `%LOCALAPPDATA%\PtySpeak\` this is normally already
    /// present (FileLogger created it earlier in startup), but
    /// the helper is callable in tests against fresh temp paths.
    ///
    /// **Returns**: `true` if a fresh file was written; `false`
    /// if a file already existed at `filePath` (caller proceeds
    /// to open the pre-existing file). Throws on I/O error so
    /// the caller can surface to NVDA via `AnnounceSanitiser`.
    let writeDefaults (filePath: string) : bool =
        if File.Exists(filePath) then false
        else
            // F# 9 nullness — Path.GetDirectoryName returns
            // `string | null`; match explicitly so the compiler
            // sees the non-null path before passing to
            // Directory.CreateDirectory (which expects string).
            match Path.GetDirectoryName(filePath) with
            | null -> ()
            | "" -> ()
            | dir -> Directory.CreateDirectory(dir) |> ignore
            let encoding = UTF8Encoding(false)
            File.WriteAllText(filePath, defaultsTemplate, encoding)
            true

    /// Internal — try to read an `int64` value from a
    /// TomlTable by key. Returns `None` if absent or
    /// non-integer. Tomlyn boxes integers as `int64` (TOML's
    /// only integer width), so we match `:? int64` and
    /// downcast to `int` at the consumer site (parameter
    /// values fit comfortably in 32 bits). Uses F#'s
    /// tuple-deconstruction syntax for `out` parameters so
    /// the F# 9 nullness checker doesn't fire on a
    /// `byref<obj | null>` mismatch with Tomlyn's
    /// `out object` parameter signature.
    let private tryGetInt (table: TomlTable) (key: string) : int64 option =
        match table.TryGetValue(key) with
        | true, (:? int64 as i) -> Some i
        | _ -> None

    /// Internal — try to read a `string` value from a
    /// TomlTable by key. Returns `None` if absent or
    /// non-string.
    let private tryGetString (table: TomlTable) (key: string) : string option =
        match table.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | _ -> None

    /// Internal — try to read a sub-table by key. Returns
    /// `None` if absent or non-table.
    let private tryGetTable (table: TomlTable) (key: string) : TomlTable option =
        match table.TryGetValue(key) with
        | true, (:? TomlTable as t) -> Some t
        | _ -> None

    /// Internal — try to read a `bool` value from a TomlTable
    /// by key. Returns `None` if absent or non-bool. Tomlyn
    /// boxes booleans as native `bool`. Same tuple-deconstruct
    /// pattern as `tryGetInt` to avoid the F# 9 nullness
    /// `byref<obj | null>` mismatch with Tomlyn's `out object`
    /// signature.
    let private tryGetBool (table: TomlTable) (key: string) : bool option =
        match table.TryGetValue(key) with
        | true, (:? bool as b) -> Some b
        | _ -> None

    /// Cycle 38b — try to read a string-array value from a
    /// TomlTable by key. Returns `None` if absent OR if the
    /// value is non-array OR if any element is non-string.
    /// (Strict so a malformed `profiles = [42, "x"]` doesn't
    /// silently truncate; caller logs the situation.)
    let private tryGetStringArray
            (table: TomlTable)
            (key: string)
            : string[] option =
        match table.TryGetValue(key) with
        | true, (:? TomlArray as arr) ->
            let buf = ResizeArray<string>()
            let mutable ok = true
            for item in arr do
                match item with
                | :? string as s -> buf.Add(s)
                | _ -> ok <- false
            if ok then Some (buf.ToArray()) else None
        | _ -> None

    /// Cycle 24g — emit a JSON-shaped hierarchical dump of a
    /// parsed `TomlTable` so a maintainer can compare what
    /// Tomlyn actually understood against what they wrote in
    /// `config.toml`. Pins the difference between "section
    /// absent from the parsed model" and "section present but
    /// keyed differently than my reader expected" without
    /// requiring per-key trace logging on every parse path.
    ///
    /// Format choices:
    ///
    /// - JSON-shaped (not literal TOML re-emit) so subtleties
    ///   like dotted-key vs. nested-table representation are
    ///   visible. A maintainer reading the dump sees the
    ///   logical structure the parser landed on, NOT the
    ///   surface syntax it originated from.
    /// - Two-space indent per level. Keys quoted; embedded
    ///   quotes / control chars escaped per JSON rules. Arrays
    ///   inline. Sub-tables nested.
    /// - Unrecognised value types render as
    ///   `"<<TypeName: value>>"` so future Tomlyn types don't
    ///   crash the dump and any oddity is greppable.
    /// - Non-deterministic key order: Tomlyn's `TomlTable`
    ///   preserves insertion order from the parsed source, so
    ///   the dump's ordering matches the user's file ordering.
    let internal snapshotAsJson (root: TomlTable) : string =
        let sb = StringBuilder()
        let escape (s: string) : string =
            let inner = StringBuilder(s.Length + 2)
            for c in s do
                match c with
                | '"' -> inner.Append("\\\"") |> ignore
                | '\\' -> inner.Append("\\\\") |> ignore
                | '\n' -> inner.Append("\\n") |> ignore
                | '\r' -> inner.Append("\\r") |> ignore
                | '\t' -> inner.Append("\\t") |> ignore
                | c when c < ' ' ->
                    inner.AppendFormat(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "\\u{0:x4}",
                        int c)
                    |> ignore
                | c -> inner.Append(c) |> ignore
            inner.ToString()
        let rec writeValue (indent: int) (value: obj | null) : unit =
            match value with
            | null ->
                sb.Append("null") |> ignore
            | :? TomlTable as t ->
                writeTable indent t
            | :? TomlArray as arr ->
                sb.Append('[') |> ignore
                let mutable first = true
                for item in arr do
                    if not first then sb.Append(", ") |> ignore
                    first <- false
                    writeValue (indent + 1) item
                sb.Append(']') |> ignore
            | :? TomlTableArray as arr ->
                sb.Append('[') |> ignore
                let mutable first = true
                for item in arr do
                    if not first then sb.Append(", ") |> ignore
                    first <- false
                    writeTable (indent + 1) item
                sb.Append(']') |> ignore
            | :? string as s ->
                sb.Append('"').Append(escape s).Append('"') |> ignore
            | :? bool as b ->
                sb.Append(if b then "true" else "false") |> ignore
            | :? int64 as i ->
                sb.Append(string i) |> ignore
            | :? double as d ->
                sb.Append(
                    d.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                |> ignore
            | other ->
                sb.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "\"<<{0}: {1}>>\"",
                    other.GetType().Name,
                    escape (string other))
                |> ignore
        and writeTable (indent: int) (t: TomlTable) : unit =
            let entries = t |> Seq.toList
            if List.isEmpty entries then
                sb.Append("{}") |> ignore
            else
                sb.AppendLine("{") |> ignore
                let prefix = String(' ', (indent + 1) * 2)
                entries
                |> List.iteri (fun i kvp ->
                    sb.Append(prefix)
                        .Append('"')
                        .Append(escape kvp.Key)
                        .Append("\": ")
                    |> ignore
                    writeValue (indent + 1) kvp.Value
                    if i < entries.Length - 1 then
                        sb.AppendLine(",") |> ignore
                    else
                        sb.AppendLine() |> ignore)
                sb.Append(String(' ', indent * 2)).Append('}') |> ignore
        writeTable 0 root
        sb.ToString()

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
                        // Cycle 38b — `profiles = [...]` is OPTIONAL
                        // and orthogonal to `pathway`. A shell may
                        // override one, the other, both, or neither.
                        // We materialise an entry in `result` if
                        // EITHER field is set; if only `profiles`
                        // is set, `PathwayId` falls through to the
                        // default `"stream"` (matching the prior
                        // behaviour for a shell that omitted
                        // `pathway`).
                        // Cycle 45f — also accepts `verbosity` and
                        // `prompt_path` enum-string keys; same
                        // option-typed semantics.
                        let pathwayId =
                            tryGetString shellTable "pathway"
                            |> Option.defaultValue "stream"
                        let profiles =
                            match tryGetStringArray shellTable "profiles" with
                            | Some arr -> Some arr
                            | None ->
                                // Distinguish "key absent" (None ok)
                                // from "key present but malformed"
                                // (warn so the maintainer notices).
                                match shellTable.TryGetValue("profiles") with
                                | true, _ ->
                                    logger.LogWarning(
                                        "Config: [shell.{Key}] `profiles` is not a string array; ignored.",
                                        key)
                                    None
                                | _ -> None
                        // Cycle 45f enum-string parsers — same
                        // shape as the [pathway.stream]
                        // backspace_policy / mode_barrier_flush
                        // parsers. Unknown / non-string values
                        // log a Warning and return None (falls
                        // back to ShellPolicy.forShell default).
                        let readVerbosity () : ShellPolicy.StreamingMode option =
                            match tryGetString shellTable "verbosity" with
                            | None ->
                                if shellTable.ContainsKey("verbosity") then
                                    logger.LogWarning(
                                        "Config: [shell.{Key}] verbosity is non-string; ignored.",
                                        key)
                                None
                            | Some "tuple_final" -> Some ShellPolicy.TupleFinalOnly
                            | Some "line_by_line" -> Some ShellPolicy.LineByLine
                            | Some "off" -> Some ShellPolicy.Off
                            | Some other ->
                                logger.LogWarning(
                                    "Config: [shell.{Key}] verbosity = '{Value}' is not one of 'tuple_final' / 'line_by_line' / 'off'; ignored.",
                                    key, other)
                                None
                        let readPromptPath () : ShellPolicy.PromptPathMode option =
                            match tryGetString shellTable "prompt_path" with
                            | None ->
                                if shellTable.ContainsKey("prompt_path") then
                                    logger.LogWarning(
                                        "Config: [shell.{Key}] prompt_path is non-string; ignored.",
                                        key)
                                None
                            | Some "suppress" -> Some ShellPolicy.Suppress
                            | Some "final_dir_only" -> Some ShellPolicy.FinalDirOnly
                            | Some "full" -> Some ShellPolicy.Full
                            | Some "full_on_change" ->
                                Some ShellPolicy.FullOnChangeElseFinal
                            | Some other ->
                                logger.LogWarning(
                                    "Config: [shell.{Key}] prompt_path = '{Value}' is not one of 'suppress' / 'final_dir_only' / 'full' / 'full_on_change'; ignored.",
                                    key, other)
                                None
                        let verbosity = readVerbosity ()
                        let promptPath = readPromptPath ()
                        let hasOverride =
                            tryGetString shellTable "pathway" |> Option.isSome
                            || profiles |> Option.isSome
                            || verbosity |> Option.isSome
                            || promptPath |> Option.isSome
                        if hasOverride then
                            result <-
                                result
                                |> Map.add
                                    key
                                    { PathwayId = pathwayId
                                      Profiles = profiles
                                      Verbosity = verbosity
                                      PromptPath = promptPath }
            result

    /// Cycle 19 — parse `[startup]` table. Recognises only
    /// `default_shell` today; unknown keys logged + dropped.
    /// Validates the value against the same shell-id allowlist
    /// `parseShellOverrides` uses (`knownShellKeys`); unknown
    /// shell names are logged + dropped (composition root
    /// falls through to env-var or cmd default).
    let private parseStartupOverrides
            (logger: ILogger)
            (root: TomlTable)
            : StartupOverrides
            =
        match tryGetTable root "startup" with
        | None -> defaultConfig.StartupOverrides
        | Some startupTable ->
            // Warn on unknown keys to help typo-spotting.
            let knownStartupKeys = Set.ofList [ "default_shell" ]
            for key in startupTable.Keys do
                if not (knownStartupKeys.Contains(key)) then
                    logger.LogWarning(
                        "Config: [startup] unknown key '{Key}'; ignored.",
                        key)
            let defaultShell =
                match tryGetString startupTable "default_shell" with
                | None -> None
                | Some value ->
                    let lowered = value.ToLowerInvariant()
                    if knownShellKeys.Contains(lowered) then
                        Some lowered
                    else
                        logger.LogWarning(
                            "Config: [startup] default_shell '{Value}' is not a recognised shell id; ignored. Recognised: {Known}.",
                            value,
                            String.concat ", " knownShellKeys)
                        None
            { DefaultShell = defaultShell }

    /// Cycle 25a — parse `[logging]` table. Recognises only
    /// `min_level` today; unknown keys logged + dropped.
    /// Validates against `Microsoft.Extensions.Logging.LogLevel`
    /// names case-insensitively, mirroring the existing
    /// `PTYSPEAK_LOG_LEVEL` env-var parser at
    /// `FileLogger.fs:envOverrideLogLevel`.
    let private parseLoggingOverrides
            (logger: ILogger)
            (root: TomlTable)
            : LoggingOverrides
            =
        match tryGetTable root "logging" with
        | None -> defaultConfig.LoggingOverrides
        | Some loggingTable ->
            let knownLoggingKeys = Set.ofList [ "min_level" ]
            for key in loggingTable.Keys do
                if not (knownLoggingKeys.Contains(key)) then
                    logger.LogWarning(
                        "Config: [logging] unknown key '{Key}'; ignored.",
                        key)
            let minLevel =
                match tryGetString loggingTable "min_level" with
                | None ->
                    if loggingTable.ContainsKey("min_level") then
                        logger.LogWarning(
                            "Config: [logging] min_level is non-string; using default.")
                    None
                | Some raw ->
                    let mutable parsed = LogLevel.Information
                    if Enum.TryParse<LogLevel>(raw, true, &parsed) then
                        Some parsed
                    else
                        logger.LogWarning(
                            "Config: [logging] min_level = '{Value}' is not one of Trace / Debug / Information / Warning / Error / Critical / None; using default.",
                            raw)
                        None
            { MinLevel = minLevel }

    /// Cycle 32a — parse `[profile.selection]` table for the
    /// SelectionDetector tunable parameters. Recognised keys:
    /// `highlight_detection_threshold_ms`, `dismissal_grace_ms`,
    /// `keystroke_correlation_window_ms` (all positive integers,
    /// milliseconds), and `min_confidence`
    /// (`"heuristic_sgr"` | `"heuristic_sgr_with_keystroke"`,
    /// case-insensitive). Unknown keys log + drop; non-positive
    /// values log + fall back to defaults; unrecognised
    /// `min_confidence` values log + fall back to default.
    let private parseProfileSelectionOverrides
            (logger: ILogger)
            (root: TomlTable)
            : SelectionParameterOverrides
            =
        match tryGetTable root "profile" with
        | None -> defaultConfig.SelectionOverrides
        | Some profileTable ->
            match tryGetTable profileTable "selection" with
            | None -> defaultConfig.SelectionOverrides
            | Some selectionTable ->
                let knownKeys =
                    Set.ofList
                        [ "highlight_detection_threshold_ms"
                          "dismissal_grace_ms"
                          "keystroke_correlation_window_ms"
                          "min_confidence" ]
                for key in selectionTable.Keys do
                    if not (knownKeys.Contains(key)) then
                        logger.LogWarning(
                            "Config: [profile.selection] unknown key '{Key}'; ignored.",
                            key)

                let parsePositiveMs (key: string) : int option =
                    match tryGetInt selectionTable key with
                    | None ->
                        if selectionTable.ContainsKey(key) then
                            logger.LogWarning(
                                "Config: [profile.selection] {Key} is non-integer; using default.",
                                key)
                        None
                    | Some raw ->
                        // Reuses the existing positive-int validator
                        // (Config.fs:506) which logs "non-positive;
                        // clamping to default" for raw <= 0 and
                        // "exceeds Int32.MaxValue; clamping to default"
                        // for overflow.
                        parsePositiveInt logger "[profile.selection]" key raw

                let parseMinConfidence () : SelectionDetector.SelectionSource option =
                    match tryGetString selectionTable "min_confidence" with
                    | None ->
                        if selectionTable.ContainsKey("min_confidence") then
                            logger.LogWarning(
                                "Config: [profile.selection] min_confidence is non-string; using default.")
                        None
                    | Some raw ->
                        match raw.Trim().ToLowerInvariant() with
                        | "heuristic_sgr" ->
                            Some SelectionDetector.HeuristicSGR
                        | "heuristic_sgr_with_keystroke" ->
                            Some SelectionDetector.HeuristicSGRWithKeystroke
                        | _ ->
                            logger.LogWarning(
                                "Config: [profile.selection] min_confidence = '{Value}' is not 'heuristic_sgr' or 'heuristic_sgr_with_keystroke'; using default.",
                                raw)
                            None

                { HighlightDetectionThresholdMs =
                    parsePositiveMs "highlight_detection_threshold_ms"
                  DismissalGraceMs =
                    parsePositiveMs "dismissal_grace_ms"
                  KeystrokeCorrelationWindowMs =
                    parsePositiveMs "keystroke_correlation_window_ms"
                  MinConfidence = parseMinConfidence () }

    /// Cycle 19 — resolve the startup-shell preference. Returns
    /// `Some shellKey` when `[startup] default_shell` was set
    /// to a recognised shell id; `None` otherwise (composition
    /// root then falls through to `PTYSPEAK_SHELL` env var or
    /// the cmd built-in default).
    let resolveDefaultShell (config: Config) : string option =
        config.StartupOverrides.DefaultShell

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
            if root.ContainsKey("schema_version") then
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
                    // Cycle 24g — log the raw parsed TOML model
                    // BEFORE any per-section reader runs. Lets a
                    // maintainer compare Tomlyn's interpretation
                    // against their `config.toml` content, which
                    // pins encoding / dotted-key / typo issues
                    // that are otherwise invisible (the TOML
                    // parses cleanly, the section just isn't
                    // where the per-section reader looks).
                    logger.LogInformation(
                        "Config: parsed TOML model snapshot from {Path}:\n{Snapshot}",
                        filePath,
                        snapshotAsJson model)
                    let version = parseSchemaVersion logger model
                    if version > CurrentSchemaVersion then
                        logger.LogError(
                            "Config: schema_version {Version} newer than supported {Current}; using defaults.",
                            version, CurrentSchemaVersion)
                        defaultConfig
                    else
                        let shellOverrides =
                            parseShellOverrides logger model
                        let startupOverrides =
                            parseStartupOverrides logger model
                        let sessionPersistence =
                            SessionPersistence.parseFromTable logger model
                        let loggingOverrides =
                            parseLoggingOverrides logger model
                        let selectionOverrides =
                            parseProfileSelectionOverrides logger model
                        let result =
                            { SchemaVersion = version
                              ShellOverrides = shellOverrides
                              StartupOverrides = startupOverrides
                              SessionPersistence = sessionPersistence
                              LoggingOverrides = loggingOverrides
                              SelectionOverrides = selectionOverrides }
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
                        let startupSummary =
                            match result.StartupOverrides.DefaultShell with
                            | Some shell -> sprintf "default_shell=%s" shell
                            | None -> "no startup overrides"
                        let loggingSummary =
                            match result.LoggingOverrides.MinLevel with
                            | Some level ->
                                sprintf "logging.min_level=%O" level
                            | None -> "no logging overrides"
                        logger.LogInformation(
                            "Config loaded from {Path}: {Overrides}; {Startup}; {Logging}.",
                            filePath, overrideSummary, startupSummary, loggingSummary)
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

    /// Cycle 38b — resolve the per-shell profile-set override.
    /// Returns `Some` when `[shell.<key>] profiles = [...]` is
    /// set in `config.toml`; `None` otherwise (composition root
    /// falls back to its built-in default for the shell).
    /// Caller (`resolveProfilesForShell` in `Program.fs`)
    /// translates the string IDs into `Profile` instances via
    /// `OutputDispatcher.ProfileRegistry.lookup`; unknown IDs
    /// are logged + dropped, never crash.
    let resolveShellProfiles
            (config: Config)
            (shellKey: string)
            : string[] option =
        match Map.tryFind shellKey config.ShellOverrides with
        | Some entry -> entry.Profiles
        | None -> None

    /// Cycle 45f — resolve the per-shell policy (Layer 1 +
    /// Layer 2 of the three-layer settings model). Starts from
    /// `ShellPolicy.forShell shellKey` (Layer 1 — compiled
    /// defaults) and overlays any TOML-supplied `verbosity` /
    /// `prompt_path` keys (Layer 2). Layer 3 (runtime overrides
    /// from the menu) lives in `Terminal.App.Program.fs` and is
    /// applied on top of this result at shell-switch time.
    let resolveShellPolicy
            (config: Config)
            (shellKey: string)
            : ShellPolicy.T =
        let baseline = ShellPolicy.forShell shellKey
        match Map.tryFind shellKey config.ShellOverrides with
        | None -> baseline
        | Some entry ->
            { baseline with
                Streaming =
                    Option.defaultValue baseline.Streaming entry.Verbosity
                PromptPath =
                    Option.defaultValue baseline.PromptPath entry.PromptPath }

    /// Cycle 32a — resolve the SelectionDetector parameters from
    /// a Config. Each field that's `None` in `SelectionOverrides`
    /// falls back to `SelectionDetector.defaultParameters`'s
    /// value. The composition root passes the result to
    /// `SelectionDetector.create` once at startup; no hot-reload.
    let resolveSelectionParameters
            (config: Config)
            : SelectionDetector.Parameters
            =
        let defaults = SelectionDetector.defaultParameters
        let ov = config.SelectionOverrides
        { HighlightDetectionThresholdMs =
            Option.defaultValue
                defaults.HighlightDetectionThresholdMs
                ov.HighlightDetectionThresholdMs
          DismissalGraceMs =
            Option.defaultValue defaults.DismissalGraceMs ov.DismissalGraceMs
          KeystrokeCorrelationWindowMs =
            Option.defaultValue
                defaults.KeystrokeCorrelationWindowMs
                ov.KeystrokeCorrelationWindowMs
          MinConfidence =
            Option.defaultValue defaults.MinConfidence ov.MinConfidence }
