namespace Engine.Core

/// ADR 0014 C2 — the declarative keymap: every verb is a union
/// case, the bindings are ONE table, and everything else —
/// dispatch, conflict checking, generated help, user overrides
/// — derives from it. The host's key loop is a lookup, never a
/// hand-matched ladder, so help and documentation cannot drift
/// from behaviour.
module KeyMap =

    /// The two host modes (ADR 0013 N2).
    type Mode =
        | Transcript
        | NotebookMode

    /// Every keyboard verb the engine understands. Adding one
    /// is the development guide's worked example: one case
    /// here, one row in `defaults`, one handler in the host.
    type Verb =
        | Compose
        | Branch
        | ReturnAnchor
        | JumpLatest
        | Next
        | Previous
        | Descend
        | Ascend
        | FirstSibling
        | LastSibling
        | Repeat
        | Where
        | Find
        | StructureSummary
        | Pin
        | Rerun
        | ToggleNotebook
        | NotebookMoveUp
        | NotebookMoveDown
        | NotebookRemove
        | NotebookNarrative
        | NotebookSection
        | ExportNotebook
        | SaveSession
        | OpenLastSession
        | Diagnostics
        | RateUp
        | RateDown
        | Stop
        | Help
        | Quit

    /// The `[keys]` override name for a verb (stable, lowercase,
    /// documented in CONFIGURATION.md).
    let verbName (verb: Verb) : string =
        match verb with
        | Compose -> "compose"
        | Branch -> "branch"
        | ReturnAnchor -> "return_anchor"
        | JumpLatest -> "jump_latest"
        | Next -> "next"
        | Previous -> "previous"
        | Descend -> "descend"
        | Ascend -> "ascend"
        | FirstSibling -> "first_sibling"
        | LastSibling -> "last_sibling"
        | Repeat -> "repeat"
        | Where -> "where"
        | Find -> "find"
        | StructureSummary -> "structure_summary"
        | Pin -> "pin"
        | Rerun -> "rerun"
        | ToggleNotebook -> "toggle_notebook"
        | NotebookMoveUp -> "notebook_move_up"
        | NotebookMoveDown -> "notebook_move_down"
        | NotebookRemove -> "notebook_remove"
        | NotebookNarrative -> "notebook_narrative"
        | NotebookSection -> "notebook_section"
        | ExportNotebook -> "export_notebook"
        | SaveSession -> "save_session"
        | OpenLastSession -> "open_last_session"
        | Diagnostics -> "diagnostics"
        | RateUp -> "rate_up"
        | RateDown -> "rate_down"
        | Stop -> "stop"
        | Help -> "help"
        | Quit -> "quit"

    type Binding =
        { Verb: Verb
          Key: char
          Modes: Mode list
          Description: string }

    let private both = [ Transcript; NotebookMode ]

    /// The single source of truth. Arrow keys are additionally
    /// hardwired in the host to the four tree moves (the
    /// universal fallback that survives any remapping).
    let defaults : Binding list =
        [ { Verb = Compose; Key = 'c'; Modes = [ Transcript ]
            Description = "compose a request" }
          { Verb = Branch; Key = 'b'; Modes = [ Transcript ]
            Description = "branch at the focused chunk" }
          { Verb = ReturnAnchor; Key = 'a'; Modes = [ Transcript ]
            Description = "return to the branch anchor" }
          { Verb = JumpLatest; Key = 'g'; Modes = [ Transcript ]
            Description = "jump to the latest response" }
          { Verb = Next; Key = 'j'; Modes = both
            Description = "next" }
          { Verb = Previous; Key = 'k'; Modes = both
            Description = "previous" }
          { Verb = Descend; Key = 'l'; Modes = [ Transcript ]
            Description = "descend into the chunk" }
          { Verb = Ascend; Key = 'h'; Modes = [ Transcript ]
            Description = "ascend to the parent" }
          { Verb = FirstSibling; Key = 't'; Modes = both
            Description = "first item at this level" }
          { Verb = LastSibling; Key = 'e'; Modes = both
            Description = "last item at this level" }
          { Verb = Repeat; Key = 'r'; Modes = both
            Description = "repeat in full" }
          { Verb = Where; Key = 'w'; Modes = both
            Description = "where am I" }
          { Verb = Find; Key = 'f'; Modes = [ Transcript ]
            Description = "find text from here (Enter alone repeats the last search)" }
          { Verb = StructureSummary; Key = 'z'; Modes = [ Transcript ]
            Description = "what is inside the focused chunk" }
          { Verb = Pin; Key = 'p'; Modes = [ Transcript ]
            Description = "pin the focused chunk to the notebook" }
          { Verb = Rerun; Key = 'y'; Modes = [ Transcript ]
            Description = "rerun the focused request" }
          { Verb = ToggleNotebook; Key = 'n'; Modes = both
            Description = "switch between transcript and notebook" }
          { Verb = NotebookMoveUp; Key = '['; Modes = [ NotebookMode ]
            Description = "move this cell up" }
          { Verb = NotebookMoveDown; Key = ']'; Modes = [ NotebookMode ]
            Description = "move this cell down" }
          { Verb = NotebookRemove; Key = 'x'; Modes = [ NotebookMode ]
            Description = "remove this cell" }
          { Verb = NotebookNarrative; Key = 'i'; Modes = [ NotebookMode ]
            Description = "insert a narrative cell" }
          { Verb = NotebookSection; Key = 'u'; Modes = [ NotebookMode ]
            Description = "insert a section header" }
          { Verb = ExportNotebook; Key = 'm'; Modes = [ NotebookMode ]
            Description = "export the notebook as markdown" }
          { Verb = SaveSession; Key = 'v'; Modes = both
            Description = "save the session" }
          { Verb = OpenLastSession; Key = 'o'; Modes = [ Transcript ]
            Description = "open the last saved session" }
          { Verb = Diagnostics; Key = 'd'; Modes = both
            Description = "speak diagnostics and write the dump file" }
          { Verb = RateUp; Key = '+'; Modes = both
            Description = "speech rate up" }
          { Verb = RateDown; Key = '-'; Modes = both
            Description = "speech rate down" }
          { Verb = Stop; Key = 's'; Modes = both
            Description = "stop speech" }
          { Verb = Help; Key = '?'; Modes = both
            Description = "speak this key list" }
          { Verb = Quit; Key = 'q'; Modes = both
            Description = "quit" } ]

    /// Per-mode duplicate-key detection. Empty list = valid.
    let validate (bindings: Binding list) : string list =
        [ Transcript; NotebookMode ]
        |> List.collect (fun mode ->
            bindings
            |> List.filter (fun b -> b.Modes |> List.contains mode)
            |> List.groupBy (fun b -> b.Key)
            |> List.choose (fun (key, group) ->
                if List.length group > 1 then
                    Some (
                        sprintf
                            "key '%c' is bound to multiple verbs in %A mode: %s"
                            key
                            mode
                            (group
                             |> List.map (fun b -> verbName b.Verb)
                             |> String.concat ", "))
                else None))

    /// Apply `[keys]` overrides one at a time; an override that
    /// would create a conflict (or names no verb) is skipped
    /// with a warning and the default stands (ADR 0014 C2).
    let withOverrides
            (overrides: Map<string, char>)
            (bindings: Binding list)
            : Binding list * string list =
        ((bindings, []), overrides |> Map.toList)
        ||> List.fold (fun (current, warnings) (name, key) ->
            match current |> List.tryFind (fun b -> verbName b.Verb = name) with
            | None ->
                current,
                warnings
                @ [ sprintf "[keys] %s does not name a verb; ignored." name ]
            | Some target ->
                let candidate =
                    current
                    |> List.map (fun b ->
                        if b.Verb = target.Verb then { b with Key = key }
                        else b)
                match validate candidate with
                | [] -> candidate, warnings
                | conflicts ->
                    current,
                    warnings
                    @ [ sprintf
                            "[keys] %s = '%c' conflicts; keeping the default. %s"
                            name key (String.concat "; " conflicts) ])

    /// Dispatch: the key pressed in the current mode.
    let tryFind
            (mode: Mode)
            (key: char)
            (bindings: Binding list)
            : Verb option =
        bindings
        |> List.tryFind (fun b ->
            b.Key = key && (b.Modes |> List.contains mode))
        |> Option.map (fun b -> b.Verb)

    /// Generated help for one mode — `?` can never drift from
    /// the table.
    let helpFor (mode: Mode) (bindings: Binding list) : string =
        let entries =
            bindings
            |> List.filter (fun b -> b.Modes |> List.contains mode)
            |> List.map (fun b -> sprintf "%c %s" b.Key b.Description)
            |> String.concat ". "
        sprintf "Keys: %s." entries
