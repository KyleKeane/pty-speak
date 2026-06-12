namespace Engine.Core

open System
open Tomlyn
open Tomlyn.Model

/// ADR 0014 C1 — the engine's own configuration file
/// (`engine.toml`). Pure parse: TOML text in, (config,
/// warnings) out. Follows the `Terminal.Core.Config` precedent
/// exactly: the non-throwing `Toml.Parse → HasErrors → ToModel`
/// entry, `TryGetValue` tuple-deconstruction (sidesteps the
/// F# 9 `byref<obj | null>` mismatch), and the
/// **warn-and-default discipline** — every malformed value
/// degrades to its default with a typed warning the host
/// speaks once and records in diagnostics; never a crash,
/// never silence. A missing file is pure defaults, no warning.
module EngineConfig =

    /// The schema this build reads. A file declaring a NEWER
    /// schema is rejected whole (defaults + warning) so an old
    /// build never half-reads a future format.
    [<Literal>]
    let CurrentSchemaVersion = 1

    type Config =
        { /// Overrides ENGINE_CLAUDE_PATH and the "claude.cmd"
          /// default. `[participant] claude_executable`.
          ClaudeExecutable: string option
          /// SAPI rate, clamped −10…+10. `[speech] rate`.
          SpeechRate: int
          /// Case-insensitive substring match against installed
          /// voices. `[speech] voice`.
          VoiceName: string option
          /// Navigation-read bound (chars). `[narration]
          /// move_read_cap_chars`.
          MoveReadCapChars: int
          /// Master switch for spatial cues. `[cues] enabled`.
          CuesEnabled: bool
          /// Master gain multiplier over per-cue gains,
          /// clamped 0.0…1.0. `[cues] gain`.
          CueGain: float
          /// Single-character verb rebindings. `[keys]`
          /// verb-name = "x". Applied by `KeyMap.withOverrides`
          /// (conflicts warn and keep defaults).
          KeyOverrides: Map<string, char> }

    let defaults : Config =
        { ClaudeExecutable = None
          SpeechRate = 0
          VoiceName = None
          MoveReadCapChars = 600
          CuesEnabled = true
          CueGain = 1.0
          KeyOverrides = Map.empty }

    let private tryGetTable (table: TomlTable) (key: string) : TomlTable option =
        match table.TryGetValue(key) with
        | true, (:? TomlTable as t) -> Some t
        | _ -> None

    let private tryGetString (table: TomlTable) (key: string) : string option =
        match table.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | _ -> None

    let private tryGetInt (table: TomlTable) (key: string) : int64 option =
        match table.TryGetValue(key) with
        | true, (:? int64 as i) -> Some i
        | _ -> None

    let private tryGetBool (table: TomlTable) (key: string) : bool option =
        match table.TryGetValue(key) with
        | true, (:? bool as b) -> Some b
        | _ -> None

    /// TOML floats box as `float`; integers as `int64` — accept
    /// both for a gain value (a user writing `1` means 1.0).
    let private tryGetFloat (table: TomlTable) (key: string) : float option =
        match table.TryGetValue(key) with
        | true, (:? float as f) -> Some f
        | true, (:? int64 as i) -> Some (float i)
        | _ -> None

    /// Parse TOML text. Returns the effective config plus
    /// human-readable warnings (spoken once at startup,
    /// recorded in diagnostics).
    let parse (toml: string) : Config * string list =
        let doc = Toml.Parse(toml)
        if doc.HasErrors then
            let detail =
                doc.Diagnostics
                |> Seq.map (fun d -> d.ToString())
                |> String.concat "; "
            defaults,
            [ sprintf "engine.toml is malformed; using defaults. %s" detail ]
        else
            let model = doc.ToModel()
            let warnings = ResizeArray<string>()
            let warn (message: string) = warnings.Add(message)

            let schemaOk =
                match tryGetInt model "schema_version" with
                | Some v when int v > CurrentSchemaVersion ->
                    warn (
                        sprintf
                            "engine.toml schema_version %d is newer than this build supports (%d); using defaults."
                            (int v) CurrentSchemaVersion)
                    false
                | _ -> true

            if not schemaOk then
                defaults, List.ofSeq warnings
            else
                let participant =
                    match tryGetTable model "participant" with
                    | Some t -> tryGetString t "claude_executable"
                    | None -> None

                let speechRate, voiceName =
                    match tryGetTable model "speech" with
                    | None -> defaults.SpeechRate, defaults.VoiceName
                    | Some t ->
                        let rate =
                            match tryGetInt t "rate" with
                            | None ->
                                if t.ContainsKey("rate") then
                                    warn "[speech] rate is not an integer; using 0."
                                defaults.SpeechRate
                            | Some raw ->
                                let clamped = max -10 (min 10 (int raw))
                                if int64 clamped <> raw then
                                    warn (
                                        sprintf
                                            "[speech] rate %d out of range; clamped to %d."
                                            (int raw) clamped)
                                clamped
                        rate, tryGetString t "voice"

                let moveCap =
                    match tryGetTable model "narration" with
                    | None -> defaults.MoveReadCapChars
                    | Some t ->
                        match tryGetInt t "move_read_cap_chars" with
                        | None ->
                            if t.ContainsKey("move_read_cap_chars") then
                                warn "[narration] move_read_cap_chars is not an integer; using 600."
                            defaults.MoveReadCapChars
                        | Some raw when raw < 50L ->
                            warn "[narration] move_read_cap_chars below 50; clamped to 50."
                            50
                        | Some raw -> int (min raw 100_000L)

                let cuesEnabled, cueGain =
                    match tryGetTable model "cues" with
                    | None -> defaults.CuesEnabled, defaults.CueGain
                    | Some t ->
                        let enabled =
                            match tryGetBool t "enabled" with
                            | None ->
                                if t.ContainsKey("enabled") then
                                    warn "[cues] enabled is not a boolean; using true."
                                defaults.CuesEnabled
                            | Some b -> b
                        let gain =
                            match tryGetFloat t "gain" with
                            | None ->
                                if t.ContainsKey("gain") then
                                    warn "[cues] gain is not a number; using 1.0."
                                defaults.CueGain
                            | Some g ->
                                let clamped = max 0.0 (min 1.0 g)
                                if clamped <> g then
                                    warn (
                                        sprintf
                                            "[cues] gain %g out of range; clamped to %g."
                                            g clamped)
                                clamped
                        enabled, gain

                let keyOverrides =
                    match tryGetTable model "keys" with
                    | None -> Map.empty
                    | Some t ->
                        (Map.empty, t.Keys |> List.ofSeq)
                        ||> List.fold (fun acc key ->
                            match tryGetString t key with
                            | Some v when v.Length = 1
                                          && not (Char.IsControl v.[0]) ->
                                acc |> Map.add key v.[0]
                            | Some v ->
                                warn (
                                    sprintf
                                        "[keys] %s = \"%s\" is not a single printable character; keeping the default."
                                        key v)
                                acc
                            | None ->
                                warn (
                                    sprintf
                                        "[keys] %s is not a string; keeping the default."
                                        key)
                                acc)

                { ClaudeExecutable = participant
                  SpeechRate = speechRate
                  VoiceName = voiceName
                  MoveReadCapChars = moveCap
                  CuesEnabled = cuesEnabled
                  CueGain = cueGain
                  KeyOverrides = keyOverrides },
                List.ofSeq warnings

    /// Load from a file path. Missing file = pure defaults
    /// (no warning); unreadable file = defaults + warning.
    let load (path: string) : Config * string list =
        if not (IO.File.Exists path) then
            defaults, []
        else
            try
                parse (IO.File.ReadAllText path)
            with ex ->
                defaults,
                [ sprintf "engine.toml could not be read; using defaults. %s" ex.Message ]
