namespace Terminal.Core

open System
open System.IO
open Tomlyn
open Tomlyn.Model

/// Cycle 38a — canonical interaction-pair corpus.
///
/// Loads a TOML file (typically
/// `tests/fixtures/canonical-interactions.toml`) containing curated
/// `(bytes-in, expected-NVDA-out)` scenarios used by the
/// `Ctrl+Shift+D` diagnostic battery as a regression net for the
/// per-shell parser-route refactors landing in Cycles 38b-e.
///
/// **Tiered schema** (per maintainer 2026-05-10). Required v1 fields
/// match `Diagnostics.DiagnosticTest`'s shape exactly so the runner
/// in `Diagnostics.fs` can reuse `runOneTest`'s capture pipeline
/// without reshaping. Optional v2 extension fields are parsed but
/// not yet enforced by `runCorpus` in Cycle 38a; subsequent sub-
/// cycles promote each to load-bearing:
///
/// - `expected_payload_regex` — Cycle 38c (echo-suppression validation).
/// - `expected_session_tuple` — Cycle 38b (per-shell route + tuple capture).
/// - `expected_pane_routing` — Cycle 38d (three-sub-pane channel routing).
///
/// **Tomlyn API.** Mirrors `Config.fs:348-377` helper patterns. Uses
/// `TomlTable` for sub-tables, `TomlArray` for value arrays,
/// `TomlTableArray` for `[[scenario]]` array-of-tables.
module CanonicalCorpus =

    /// Cycle 38d extension — three-sub-pane channel routing
    /// identifier. Parsed in 38a so corpus authors can annotate
    /// scenarios; not yet honoured by `runCorpus` (the channel-
    /// layer routing wires through in 38d).
    type PaneRouting =
        | Input
        | CurrentOutput
        | History

    /// Cycle 38b extension — expected SessionModel tuple shape
    /// after the scenario completes. Mirrors `RecentTupleView`
    /// shape from `Diagnostics.fs:106-148`. Each field is optional
    /// so a scenario can pin only the parts it cares about (e.g.
    /// `cmd.exit.failure` pins `ExitCode = Some 1` and leaves the
    /// text fields unbound).
    type SessionTupleExpectation =
        { CommandText: string option
          OutputText: string option
          ExitCode: int option }

    /// One canonical interaction-pair scenario, parsed from a
    /// `[[scenario]]` TOML table.
    type Scenario =
        { Id: string
          Shell: string
          Description: string
          Command: string
          MustInclude: SemanticCategory[]
          MustNotInclude: SemanticCategory[]
          QuiescenceMs: int
          TimeoutMs: int
          SetupCommand: string option
          ExpectedPayloadRegex: string[]
          ExpectedSessionTuple: SessionTupleExpectation option
          ExpectedPaneRouting: PaneRouting option
          Notes: string option }

    /// Outcome of running one scenario.
    type ScenarioOutcome =
        | Pass
        | Fail of reason: string

    /// Result captured for one scenario after `runCorpus`. Even
    /// on FAIL we capture the observed semantic categories +
    /// payloads so the bundle output is diagnostically useful
    /// (the maintainer can read what NVDA actually heard versus
    /// what was expected).
    type ScenarioResult =
        { Scenario: Scenario
          Outcome: ScenarioOutcome
          ObservedSemantics: SemanticCategory[]
          ObservedPayloads: string[]
          ElapsedMs: int }

    // -----------------------------------------------------------
    // Internals — TOML helpers (mirror Config.fs:348-377)
    // -----------------------------------------------------------

    let private tryGetInt (table: TomlTable) (key: string) : int64 option =
        match table.TryGetValue(key) with
        | true, (:? int64 as i) -> Some i
        | _ -> None

    let private tryGetString (table: TomlTable) (key: string) : string option =
        match table.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | _ -> None

    let private tryGetTable (table: TomlTable) (key: string) : TomlTable option =
        match table.TryGetValue(key) with
        | true, (:? TomlTable as t) -> Some t
        | _ -> None

    /// Read a string array. Returns `Error` if the key is present
    /// but ANY element is non-string, so the corpus author gets a
    /// loud failure rather than a silently-truncated array.
    let private tryGetStringArray
            (table: TomlTable)
            (key: string)
            : Result<string[] option, string> =
        match table.TryGetValue(key) with
        | true, (:? TomlArray as arr) ->
            let buf = ResizeArray<string>()
            let mutable badIndex: int option = None
            let mutable i = 0
            for item in arr do
                match item with
                | :? string as s -> buf.Add(s)
                | _ -> if badIndex.IsNone then badIndex <- Some i
                i <- i + 1
            match badIndex with
            | Some idx ->
                Error (sprintf "%s[%d]: expected string" key idx)
            | None -> Ok (Some (buf.ToArray()))
        | true, _ -> Error (sprintf "%s: expected array of strings" key)
        | _ -> Ok None

    /// Map a `SemanticCategory` string name to the DU value.
    /// Returns `None` for unknown names so the caller can surface
    /// a context-prefixed parse error. Catalog mirrors
    /// `OutputEventTypes.fs:42-111`. `Custom of string` is
    /// deliberately excluded — corpus scenarios should not target
    /// user-extension categories in v1.
    let private semanticCategoryFromString
            (name: string)
            : SemanticCategory option =
        match name with
        | "StreamChunk" -> Some SemanticCategory.StreamChunk
        | "SelectionShown" -> Some SemanticCategory.SelectionShown
        | "SelectionItem" -> Some SemanticCategory.SelectionItem
        | "SelectionDismissed" -> Some SemanticCategory.SelectionDismissed
        | "SpinnerTick" -> Some SemanticCategory.SpinnerTick
        | "ErrorLine" -> Some SemanticCategory.ErrorLine
        | "WarningLine" -> Some SemanticCategory.WarningLine
        | "PromptDetected" -> Some SemanticCategory.PromptDetected
        | "CommandSubmitted" -> Some SemanticCategory.CommandSubmitted
        | "BellRang" -> Some SemanticCategory.BellRang
        | "HyperlinkOpened" -> Some SemanticCategory.HyperlinkOpened
        | "AltScreenEntered" -> Some SemanticCategory.AltScreenEntered
        | "ModeBarrier" -> Some SemanticCategory.ModeBarrier
        | "ParserError" -> Some SemanticCategory.ParserError
        | _ -> None

    let private parsePaneRouting (s: string) : PaneRouting option =
        match s with
        | "input" -> Some Input
        | "current_output" -> Some CurrentOutput
        | "history" -> Some History
        | _ -> None

    /// Parse a `must_include` / `must_not_include` array. Each
    /// element must be a known `SemanticCategory` name. Returns
    /// `Error` on first unknown name with a context-prefixed
    /// message.
    let private parseSemanticArray
            (scenarioId: string)
            (field: string)
            (table: TomlTable)
            : Result<SemanticCategory[], string> =
        match tryGetStringArray table field with
        | Error e -> Error (sprintf "scenario %s.%s" scenarioId e)
        | Ok None -> Ok [||]
        | Ok (Some names) ->
            let mutable err: string option = None
            let mapped =
                names
                |> Array.choose (fun name ->
                    match semanticCategoryFromString name with
                    | Some s -> Some s
                    | None ->
                        if err.IsNone then
                            err <-
                                Some
                                    (sprintf
                                        "scenario %s.%s: unknown SemanticCategory \"%s\""
                                        scenarioId field name)
                        None)
            match err with
            | Some e -> Error e
            | None -> Ok mapped

    let private parseSessionTuple
            (scenarioId: string)
            (table: TomlTable)
            : Result<SessionTupleExpectation option, string> =
        match tryGetTable table "expected_session_tuple" with
        | None -> Ok None
        | Some t ->
            let exitCode =
                tryGetInt t "exit_code"
                |> Option.map int
            let expectation =
                { CommandText = tryGetString t "command_text"
                  OutputText = tryGetString t "output_text"
                  ExitCode = exitCode }
            Ok (Some expectation)

    /// Parse the optional `expected_pane_routing` field. Returns
    /// `Ok None` if absent, `Ok (Some r)` if present with a known
    /// value, `Error` for unknown values.
    let private parseExpectedPaneRouting
            (scenarioId: string)
            (table: TomlTable)
            : Result<PaneRouting option, string> =
        match tryGetString table "expected_pane_routing" with
        | None -> Ok None
        | Some s ->
            match parsePaneRouting s with
            | Some r -> Ok (Some r)
            | None ->
                Error
                    (sprintf
                        "scenario %s.expected_pane_routing: unknown value \"%s\" (expected: input | current_output | history)"
                        scenarioId s)

    /// Parse the optional `expected_payload_regex` array. Returns
    /// `Ok [||]` if absent, `Ok arr` if present and well-formed,
    /// `Error` if the value is malformed (non-array or non-string
    /// element).
    let private parseExpectedPayloadRegex
            (scenarioId: string)
            (table: TomlTable)
            : Result<string[], string> =
        match tryGetStringArray table "expected_payload_regex" with
        | Error e -> Error (sprintf "scenario %s.%s" scenarioId e)
        | Ok None -> Ok [||]
        | Ok (Some arr) -> Ok arr

    /// Assemble the Scenario record from already-validated pieces.
    /// Pure helper; never returns `Error`.
    let private buildScenario
            (id: string)
            (shell: string)
            (command: string)
            (mustInclude: SemanticCategory[])
            (mustNotInclude: SemanticCategory[])
            (expectedPayloadRegex: string[])
            (expectedSessionTuple: SessionTupleExpectation option)
            (expectedPaneRouting: PaneRouting option)
            (table: TomlTable)
            : Scenario =
        let description =
            tryGetString table "description"
            |> Option.defaultValue ""
        let setupCommand = tryGetString table "setup_command"
        let notes = tryGetString table "notes"
        let quiescenceMs =
            tryGetInt table "quiescence_ms"
            |> Option.map int
            |> Option.defaultValue 200
        let timeoutMs =
            tryGetInt table "timeout_ms"
            |> Option.map int
            |> Option.defaultValue 1500
        { Id = id
          Shell = shell
          Description = description
          Command = command
          MustInclude = mustInclude
          MustNotInclude = mustNotInclude
          QuiescenceMs = quiescenceMs
          TimeoutMs = timeoutMs
          SetupCommand = setupCommand
          ExpectedPayloadRegex = expectedPayloadRegex
          ExpectedSessionTuple = expectedSessionTuple
          ExpectedPaneRouting = expectedPaneRouting
          Notes = notes }

    /// Parse one `[[scenario]]` table into a Scenario record.
    /// Returns `Error` on the first violation (missing required
    /// field, unknown SemanticCategory, malformed sub-table, etc.).
    /// Implementation uses `Result.bind` chain to avoid F# 9
    /// offside-rule traps with deeply-chained match expressions
    /// (per `CLAUDE.md` "Sequence-in-match-arm" gotcha).
    let private parseScenario
            (index: int)
            (table: TomlTable)
            : Result<Scenario, string> =
        let positionalCtx field msg =
            sprintf "scenario[%d].%s: %s" index field msg
        let scenarioCtx (id: string) (field: string) (msg: string) : string =
            sprintf "scenario %s.%s: %s" id field msg
        let requireString (id: string option) (field: string)
                : Result<string, string> =
            match tryGetString table field with
            | Some s -> Ok s
            | None ->
                let ctx =
                    match id with
                    | Some i -> scenarioCtx i field
                    | None -> positionalCtx field
                Error (ctx "required string field missing")
        requireString None "id"
        |> Result.bind (fun id ->
            requireString (Some id) "shell"
            |> Result.bind (fun shell ->
                requireString (Some id) "command"
                |> Result.bind (fun command ->
                    parseSemanticArray id "must_include" table
                    |> Result.bind (fun mustInclude ->
                        parseSemanticArray id "must_not_include" table
                        |> Result.bind (fun mustNotInclude ->
                            parseExpectedPayloadRegex id table
                            |> Result.bind (fun regex ->
                                parseSessionTuple id table
                                |> Result.bind (fun tuple ->
                                    parseExpectedPaneRouting id table
                                    |> Result.map (fun pane ->
                                        buildScenario
                                            id shell command
                                            mustInclude mustNotInclude
                                            regex tuple pane
                                            table))))))))

    // -----------------------------------------------------------
    // Public API
    // -----------------------------------------------------------

    /// Parse a TOML string into a Scenario array. Returns `Error`
    /// on first malformed scenario. Empty document (no
    /// `[[scenario]]` entries) is a valid Ok with an empty array.
    let parseFromString (toml: string) : Result<Scenario[], string> =
        try
            let model = Toml.ToModel(toml)
            match model.TryGetValue("scenario") with
            | false, _ -> Ok [||]
            | true, (:? TomlTableArray as arr) ->
                let buf = ResizeArray<Scenario>()
                let mutable err: string option = None
                let mutable i = 0
                for table in arr do
                    if err.IsNone then
                        match parseScenario i table with
                        | Ok s -> buf.Add(s)
                        | Error e -> err <- Some e
                    i <- i + 1
                match err with
                | Some e -> Error e
                | None -> Ok (buf.ToArray())
            | true, _ ->
                Error "scenario: expected [[scenario]] array-of-tables"
        with ex ->
            Error (sprintf "TOML parse error: %s" ex.Message)

    /// Load a Scenario array from a file path. Returns `Error` on
    /// I/O failure or malformed content. Missing file is `Error`
    /// (caller decides whether to treat as warning or fatal).
    let loadCanonicalCorpus
            (path: string)
            : Result<Scenario[], string> =
        try
            let content = File.ReadAllText(path)
            parseFromString content
        with ex ->
            Error (sprintf "Failed to read %s: %s" path ex.Message)

    /// Format a scenario's outcome for inclusion in the
    /// `Ctrl+Shift+D` diagnostic bundle's
    /// `--- CANONICAL CORPUS RESULTS ---` section. Stable,
    /// lexicographic, screen-reader-friendly multi-line block.
    let formatScenarioResult (result: ScenarioResult) : string =
        let s = result.Scenario
        let header =
            match result.Outcome with
            | Pass -> sprintf "[PASS] %s (%dms)" s.Id result.ElapsedMs
            | Fail reason ->
                sprintf "[FAIL] %s (%dms) — %s" s.Id result.ElapsedMs reason
        let semanticsLine =
            sprintf
                "       observed: [%s]"
                (result.ObservedSemantics
                 |> Array.map (sprintf "%A")
                 |> String.concat ", ")
        let mustIncludeLine =
            if Array.isEmpty s.MustInclude then None
            else
                Some
                    (sprintf
                        "       must_include: %s"
                        (s.MustInclude
                         |> Array.map (sprintf "%A")
                         |> String.concat ", "))
        let mustNotIncludeLine =
            if Array.isEmpty s.MustNotInclude then None
            else
                Some
                    (sprintf
                        "       must_not_include: %s"
                        (s.MustNotInclude
                         |> Array.map (sprintf "%A")
                         |> String.concat ", "))
        let notesLine =
            s.Notes
            |> Option.map (fun n -> sprintf "       notes: %s" n)
        [ Some header
          mustIncludeLine
          mustNotIncludeLine
          Some semanticsLine
          notesLine ]
        |> List.choose id
        |> String.concat "\n"

    /// Format an array of scenario results as the bundle section
    /// body. Includes a one-line summary header
    /// ("CORPUS: 9 PASS / 3 FAIL / 12 total") followed by each
    /// scenario result block separated by blank lines.
    let formatCorpusResultsForBundle
            (results: ScenarioResult[])
            : string =
        let total = results.Length
        let passed =
            results
            |> Array.filter (fun r ->
                match r.Outcome with Pass -> true | _ -> false)
            |> Array.length
        let failed = total - passed
        let summary =
            sprintf
                "CORPUS: %d PASS / %d FAIL / %d total"
                passed failed total
        let bodies =
            results
            |> Array.map formatScenarioResult
            |> String.concat "\n\n"
        if total = 0 then summary
        else summary + "\n\n" + bodies
