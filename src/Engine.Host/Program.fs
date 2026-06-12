module Engine.Host.Program

// RELAUNCH-SPEC §13 / ADRs 0011–0014 — the engine host: the
// one imperative file. Wires console keys (KeyMap table) →
// verbs over the transcript (capture tree) or the notebook
// (authored layer) → the Claude CLI participant → ingest →
// the universal event bus → attention/speech + spatial cues +
// diagnostics → session persistence.
//
// All decisions live in Engine.Core (pure, tested); this file
// owns ONLY: mutable state behind one lock, threads, files,
// and the audio devices. The key surface is the KeyMap table —
// see docs/engine/KEYBOARD-REFERENCE.md; ? speaks it.

open System
open System.IO
open System.Threading
open Engine.Core
open Engine.Core.EngineEvent
open Engine.Participants

/// Shared mutable host state, guarded by one lock: the console
/// thread (verbs, compose) and the turn thread (participant
/// events) both touch it.
type private HostState =
    { mutable Session: Ingest.Session
      mutable Nav: Navigator.State
      mutable Notebook: Notebook.Notebook
      mutable NotebookIndex: int
      mutable Mode: KeyMap.Mode
      mutable Queue: Attention.Queue
      mutable Speaking: bool
      mutable TurnInFlight: bool
      mutable Rate: int }

[<EntryPoint>]
let main _argv =
    // --- paths ---------------------------------------------------------
    let dataRoot =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "PtySpeak")
    let sessionsDir = Path.Combine(dataRoot, "engine-sessions")
    let diagnosticsDir = Path.Combine(dataRoot, "engine-diagnostics")
    let configPath = Path.Combine(dataRoot, "engine.toml")
    let latestSessionPath =
        Path.Combine(sessionsDir, "session-latest.jsonl")
    let latestNotebookPath =
        Path.Combine(sessionsDir, "notebook-latest.jsonl")
    let runStamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")

    // --- config + keymap (ADR 0014 C1/C2) ------------------------------
    let config, configWarnings = EngineConfig.load configPath
    let bindings, keyWarnings =
        KeyMap.withOverrides config.KeyOverrides KeyMap.defaults
    let startupWarnings = configWarnings @ keyWarnings

    // --- diagnostics (ADR 0014 C4) --------------------------------------
    let diag = EngineDiagnostics.Ring()
    startupWarnings |> List.iter (fun w -> diag.Record "config" w)

    let gate = obj ()
    let state : HostState =
        { Session = Ingest.empty
          Nav = Navigator.initial
          Notebook = Notebook.empty
          NotebookIndex = -1
          Mode = KeyMap.Transcript
          Queue = Attention.empty
          Speaking = false
          TurnInFlight = false
          Rate = config.SpeechRate }
    let bus = EngineBus()
    use sink = new Engine.Voice.SapiSink(config.SpeechRate, config.VoiceName)
    let speech = sink :> ISpeechSink

    let claudeConfig : ClaudeCli.Config =
        let fromEnv =
            match Environment.GetEnvironmentVariable "ENGINE_CLAUDE_PATH" with
            | null -> None
            | v when String.IsNullOrWhiteSpace v -> None
            | v -> Some v
        let resolved =
            config.ClaudeExecutable
            |> Option.orElse fromEnv
            |> Option.defaultValue "claude.cmd"
        { ClaudeCli.defaultConfig with ExecutablePath = resolved }

    // --- speech drain ----------------------------------------------------
    let speakNext () =
        let toSpeak =
            lock gate (fun () ->
                if state.Speaking then None
                else
                    match Attention.tryDequeue state.Queue with
                    | Some (text, rest) ->
                        state.Queue <- rest
                        state.Speaking <- true
                        Some text
                    | None -> None)
        match toSpeak with
        | Some text ->
            Console.WriteLine(text)
            speech.SpeakAsync text
        | None -> ()

    speech.UtteranceCompleted.Add(fun () ->
        lock gate (fun () -> state.Speaking <- false)
        speakNext ())

    let enqueue (utterance: Attention.Utterance) =
        lock gate (fun () ->
            state.Queue <- Attention.enqueue utterance state.Queue)
        speakNext ()

    /// User-initiated speech: cancel, supersede stale pending
    /// foreground, keep ambient (ADR 0012 S5).
    let speakNow (text: string) =
        speech.CancelAll ()
        lock gate (fun () ->
            state.Queue <-
                Attention.enqueue
                    (Attention.Foreground text)
                    (Attention.clearForeground state.Queue))
        speakNext ()

    // --- spatial cues (config-gated, master gain — ADR 0014 C1) ---------
    let playCue (cue: SpatialCue.Cue) =
        if config.CuesEnabled then
            Engine.Audio.SpatialPlayer.play
                { cue with Gain = cue.Gain * config.CueGain }

    // --- bus consumers ---------------------------------------------------
    bus.Subscribe(fun ev ->
        match Attention.route ev with
        | Some utterance -> enqueue utterance
        | None -> ())
    |> ignore

    bus.Subscribe(fun ev ->
        match SpatialCue.forEvent ev with
        | Some cue -> playCue cue
        | None -> ())
    |> ignore

    // Diagnostics + the session event log (ADR 0014 C4): one
    // line per bus event — the replay of what the user heard.
    let eventLogPath =
        Path.Combine(sessionsDir, sprintf "events-%s.log" runStamp)
    bus.Subscribe(fun ev ->
        let category, message = EngineDiagnostics.describeEvent ev
        diag.Record category message
        try
            Directory.CreateDirectory(sessionsDir) |> ignore
            File.AppendAllText(
                eventLogPath,
                sprintf
                    "%s [%s] %s\n"
                    (DateTime.UtcNow.ToString("o"))
                    category
                    message)
        with _ -> ())
    |> ignore

    // --- session persistence (ADR 0013 N4) -------------------------------
    let saveSession (announce: bool) =
        let file : ChunkSerde.SessionFile =
            lock gate (fun () ->
                { SessionId = state.Session.SessionId
                  LatestResponseStart = state.Session.LatestResponseStart
                  CurrentRequest = state.Session.CurrentRequest
                  Chunks = ChunkTree.inCaptureOrder state.Session.Tree })
        if List.isEmpty file.Chunks then
            if announce then speakNow "Nothing to save yet."
        else
            try
                Directory.CreateDirectory(sessionsDir) |> ignore
                let text = ChunkSerde.sessionToJsonl file
                File.WriteAllText(
                    Path.Combine(
                        sessionsDir,
                        sprintf "session-%s.jsonl" runStamp),
                    text)
                File.WriteAllText(latestSessionPath, text)
                // The notebook persists beside the session
                // (ADR 0013 N4) — references stay valid because
                // the session file carries every chunk.
                let notebookText =
                    lock gate (fun () ->
                        NotebookSerde.toJsonl state.Notebook)
                File.WriteAllText(latestNotebookPath, notebookText)
                if announce then
                    speakNow (
                        sprintf "Saved, %d chunks."
                            (List.length file.Chunks))
            with ex ->
                diag.Record "error" (sprintf "save failed: %s" ex.Message)
                if announce then
                    speakNow "Save failed; press d for details."

    // Helpers extracted so the restore match arms stay single
    // expressions (the sequence-in-match-arm gotcha).
    let applyRestoredNotebook (notebook: Notebook.Notebook) : string =
        lock gate (fun () ->
            state.Notebook <- notebook
            state.NotebookIndex <-
                min state.NotebookIndex (Notebook.count notebook - 1))
        if Notebook.count notebook > 0 then
            sprintf " Notebook restored, %d cells." (Notebook.count notebook)
        else ""

    let notebookRestoreFailure (detail: string) : string =
        diag.Record "error" (sprintf "notebook restore: %s" detail)
        " The notebook file could not be read; starting with an empty notebook."

    let openLastSession () =
        if not (File.Exists latestSessionPath) then
            speakNow "No saved session found."
        else
            let restored =
                try
                    match ChunkSerde.parseJsonl
                              (File.ReadAllText latestSessionPath) with
                    | Error e -> Error e
                    | Ok file ->
                        match ChunkTree.restore file.Chunks with
                        | Error e -> Error e
                        | Ok tree -> Ok (file, tree)
                with ex -> Error ex.Message
            match restored with
            | Error e ->
                diag.Record "error" (sprintf "session restore: %s" e)
                speakNow (sprintf "Could not open the last session: %s" e)
            | Ok (file, tree) ->
                let notebookNote =
                    if File.Exists latestNotebookPath then
                        try
                            match NotebookSerde.parseJsonl
                                      (File.ReadAllText latestNotebookPath) with
                            | Ok notebook -> applyRestoredNotebook notebook
                            | Error e -> notebookRestoreFailure e
                        with ex -> notebookRestoreFailure ex.Message
                    else ""
                lock gate (fun () ->
                    state.Session <-
                        { Tree = tree
                          SessionId = file.SessionId
                          CurrentRequest = file.CurrentRequest
                          LatestResponseStart = file.LatestResponseStart
                          InFlightCount = 0 }
                    state.Nav <- Navigator.initial)
                speakNow (
                    (sprintf
                        "Session restored, %d chunks. Press g to jump to the latest response."
                        (ChunkTree.count tree))
                    + notebookNote)

    // --- participant turns ------------------------------------------------
    let publishAll (events: EngineEvent list) =
        events |> List.iter (fun e -> bus.Publish e)

    let startTurn (prompt: string) (anchor: Chunk.ChunkId option) =
        let captureEvents =
            lock gate (fun () ->
                let session', events =
                    Ingest.captureRequest prompt anchor state.Session
                state.Session <- session'
                state.TurnInFlight <- true
                events)
        publishAll captureEvents
        let resume = lock gate (fun () -> state.Session.SessionId)
        let worker () =
            let outcome =
                ClaudeCli.runTurn claudeConfig prompt resume (fun agentEvent ->
                    let events =
                        lock gate (fun () ->
                            let session', events =
                                Ingest.applyAgentEvent agentEvent state.Session
                            state.Session <- session'
                            events)
                    publishAll events)
            lock gate (fun () -> state.TurnInFlight <- false)
            match outcome with
            | Ok o when o.ExitCode <> 0 ->
                let detail =
                    let trimmed = o.StdErr.Trim()
                    if trimmed.Length > 300 then trimmed.Substring(0, 300)
                    else trimmed
                bus.Publish(
                    EngineNote (
                        sprintf "Participant exited with code %d. %s"
                            o.ExitCode detail))
            | Ok _ -> ()
            | Error message ->
                bus.Publish(
                    EngineNote (
                        sprintf
                            "Could not run the participant: %s. Set ENGINE_CLAUDE_PATH or [participant] claude_executable."
                            message))
            // Auto-save after every turn (ADR 0013 N4).
            saveSession false
        let thread = Thread(worker)
        thread.IsBackground <- true
        thread.Start()

    // --- transcript narration ---------------------------------------------
    let narrateMove (move: Navigator.Move) =
        match move with
        | Navigator.Moved chunk ->
            let text =
                lock gate (fun () ->
                    ChunkNarration.describeAt
                        config.MoveReadCapChars
                        state.Session.Tree
                        chunk)
            speakNow text
        | Navigator.Edge description ->
            speakNow description
        | Navigator.NothingFocused ->
            speakNow
                "Nothing is focused yet. Press g to jump to the latest response."

    let navigate
            (direction: SpatialCue.NavDirection)
            (verb: Navigator.State -> ChunkTree.Tree -> Navigator.State * Navigator.Move) =
        let move =
            lock gate (fun () ->
                let nav', move = verb state.Nav state.Session.Tree
                state.Nav <- nav'
                move)
        let moved =
            match move with
            | Navigator.Moved _ -> true
            | _ -> false
        playCue (SpatialCue.forNav direction moved)
        narrateMove move

    /// Returns true when a turn actually started — the branch
    /// path pushes its anchor only on success.
    let compose (anchor: Chunk.ChunkId option) : bool =
        let busy = lock gate (fun () -> state.TurnInFlight)
        if busy then
            speakNow "A response is still in progress. Wait for it to complete."
            false
        else
            speech.CancelAll ()
            Console.Write("> ")
            match Console.ReadLine() with
            | null -> false
            | line when String.IsNullOrWhiteSpace line ->
                speakNow "Nothing sent."
                false
            | line ->
                startTurn line anchor
                true

    /// Read one line for a notebook insertion ("" = aborted).
    let readLine (promptLabel: string) : string option =
        speech.CancelAll ()
        Console.Write(promptLabel)
        match Console.ReadLine() with
        | null -> None
        | line when String.IsNullOrWhiteSpace line -> None
        | line -> Some line

    // --- notebook verbs (ADR 0013 N2) --------------------------------------
    let describeNotebookCell (index: int) : string option =
        lock gate (fun () ->
            Notebook.tryItem index state.Notebook
            |> Option.map (fun cell ->
                sprintf
                    "%s — %d of %d."
                    (Notebook.describeCell state.Session.Tree cell)
                    (index + 1)
                    (Notebook.count state.Notebook)))

    let notebookEmptyHint () =
        speakNow "The notebook is empty. Press p in the transcript to pin a chunk."

    let notebookStep (delta: int) (direction: SpatialCue.NavDirection) =
        let outcome =
            lock gate (fun () ->
                let count = Notebook.count state.Notebook
                if count = 0 then None
                else
                    let target = state.NotebookIndex + delta
                    if target < 0 || target >= count then
                        Some (state.NotebookIndex, false)
                    else
                        state.NotebookIndex <- target
                        Some (target, true))
        match outcome with
        | None ->
            playCue (SpatialCue.forNav direction false)
            notebookEmptyHint ()
        | Some (index, moved) ->
            playCue (SpatialCue.forNav direction moved)
            if moved then
                describeNotebookCell index |> Option.iter speakNow
            else
                speakNow (if delta > 0 then "no next cell" else "no previous cell")

    let notebookJump (toLast: bool) =
        let outcome =
            lock gate (fun () ->
                let count = Notebook.count state.Notebook
                if count = 0 then None
                else
                    let target = if toLast then count - 1 else 0
                    state.NotebookIndex <- target
                    Some target)
        match outcome with
        | None -> notebookEmptyHint ()
        | Some index ->
            playCue (SpatialCue.forNav (if toLast then SpatialCue.Next else SpatialCue.Previous) true)
            describeNotebookCell index |> Option.iter speakNow

    let notebookReorder (up: bool) =
        let outcome =
            lock gate (fun () ->
                let index = state.NotebookIndex
                let notebook', moved =
                    if up then Notebook.moveUp index state.Notebook
                    else Notebook.moveDown index state.Notebook
                state.Notebook <- notebook'
                if moved then
                    state.NotebookIndex <- if up then index - 1 else index + 1
                (state.NotebookIndex, moved, Notebook.count notebook'))
        let index, moved, count = outcome
        playCue (
            SpatialCue.forNav
                (if up then SpatialCue.Previous else SpatialCue.Next)
                moved)
        if count = 0 then notebookEmptyHint ()
        elif moved then
            speakNow (
                sprintf "%s. Cell %d of %d."
                    (if up then "Moved up" else "Moved down")
                    (index + 1) count)
        else
            speakNow (if up then "Already first." else "Already last.")

    let notebookRemove () =
        let outcome =
            lock gate (fun () ->
                let notebook', removed =
                    Notebook.removeAt state.NotebookIndex state.Notebook
                state.Notebook <- notebook'
                let count = Notebook.count notebook'
                if removed then
                    state.NotebookIndex <- min state.NotebookIndex (count - 1)
                (removed, count))
        match outcome with
        | true, count ->
            speakNow (sprintf "Removed. %d cells remain." count)
        | false, 0 -> notebookEmptyHint ()
        | false, _ -> speakNow "Nothing is selected."

    let notebookInsert (asSection: bool) =
        let label = if asSection then "section title> " else "narrative> "
        match readLine label with
        | None -> speakNow "Nothing added."
        | Some text ->
            let count =
                lock gate (fun () ->
                    state.Notebook <-
                        if asSection then Notebook.addSection text state.Notebook
                        else Notebook.addNarrative text state.Notebook
                    let count = Notebook.count state.Notebook
                    state.NotebookIndex <- count - 1
                    count)
            speakNow (
                sprintf "%s added. Cell %d of %d."
                    (if asSection then "Section" else "Narrative")
                    count count)

    let exportNotebook () =
        let rendered =
            lock gate (fun () ->
                if Notebook.count state.Notebook = 0 then None
                else
                    Some (
                        Notebook.toMarkdown
                            state.Session.Tree
                            state.Notebook))
        match rendered with
        | None -> notebookEmptyHint ()
        | Some markdown ->
            try
                Directory.CreateDirectory(sessionsDir) |> ignore
                let path =
                    Path.Combine(
                        sessionsDir,
                        sprintf "notebook-%s.md"
                            (DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")))
                File.WriteAllText(path, markdown + "\n")
                speakNow (sprintf "Notebook exported to %s" path)
            with ex ->
                diag.Record "error" (sprintf "export failed: %s" ex.Message)
                speakNow "Export failed; press d for details."

    // --- verb dispatch ------------------------------------------------------
    let mutable running = true

    let runVerb (verb: KeyMap.Verb) =
        let mode = lock gate (fun () -> state.Mode)
        match verb with
        | KeyMap.Compose -> compose None |> ignore
        | KeyMap.Branch ->
            let anchor = lock gate (fun () -> state.Nav.Current)
            match anchor with
            | Some id ->
                if compose (Some id) then
                    lock gate (fun () ->
                        state.Nav <- Navigator.pushAnchor state.Nav)
            | None ->
                speakNow "Focus a chunk first, then press b to branch from it."
        | KeyMap.ReturnAnchor ->
            navigate SpatialCue.ReturnToAnchor Navigator.returnToAnchor
        | KeyMap.JumpLatest ->
            let latest =
                lock gate (fun () -> state.Session.LatestResponseStart)
            navigate SpatialCue.Jump (Navigator.jumpToLatestResponse latest)
        | KeyMap.Next ->
            if mode = KeyMap.Transcript then
                navigate SpatialCue.Next Navigator.next
            else notebookStep 1 SpatialCue.Next
        | KeyMap.Previous ->
            if mode = KeyMap.Transcript then
                navigate SpatialCue.Previous Navigator.previous
            else notebookStep -1 SpatialCue.Previous
        | KeyMap.Descend ->
            navigate SpatialCue.Descend Navigator.descend
        | KeyMap.Ascend ->
            navigate SpatialCue.Ascend Navigator.ascend
        | KeyMap.FirstSibling ->
            if mode = KeyMap.Transcript then
                navigate SpatialCue.Previous Navigator.firstSibling
            else notebookJump false
        | KeyMap.LastSibling ->
            if mode = KeyMap.Transcript then
                navigate SpatialCue.Next Navigator.lastSibling
            else notebookJump true
        | KeyMap.Repeat ->
            if mode = KeyMap.Transcript then
                let text =
                    lock gate (fun () ->
                        Navigator.current state.Nav state.Session.Tree
                        |> Option.map (fun c ->
                            ChunkNarration.describe state.Session.Tree c))
                match text with
                | Some t -> speakNow t
                | None -> speakNow "Nothing is focused."
            else
                let text =
                    lock gate (fun () ->
                        Notebook.tryItem state.NotebookIndex state.Notebook
                        |> Option.map (fun cell ->
                            Notebook.describeCell state.Session.Tree cell))
                match text with
                | Some t -> speakNow t
                | None -> notebookEmptyHint ()
        | KeyMap.Where ->
            if mode = KeyMap.Transcript then
                let located =
                    lock gate (fun () ->
                        Navigator.current state.Nav state.Session.Tree
                        |> Option.map (fun c ->
                            ChunkNarration.locate state.Session.Tree c))
                match located with
                | Some text -> speakNow text
                | None -> speakNow "Nothing is focused."
            else
                let position =
                    lock gate (fun () ->
                        let count = Notebook.count state.Notebook
                        if count = 0 then None
                        else Some (state.NotebookIndex + 1, count))
                match position with
                | Some (n, m) ->
                    speakNow (sprintf "Cell %d of %d, in the notebook." n m)
                | None -> notebookEmptyHint ()
        | KeyMap.Pin ->
            let pinned =
                lock gate (fun () ->
                    match state.Nav.Current with
                    | Some id ->
                        state.Notebook <- Notebook.pin id state.Notebook
                        Some (Notebook.count state.Notebook)
                    | None -> None)
            match pinned with
            | Some count ->
                speakNow (
                    sprintf "Pinned. %d cells in the notebook." count)
            | None -> speakNow "Focus a chunk first, then press p to pin it."
        | KeyMap.Rerun ->
            let request =
                lock gate (fun () ->
                    Navigator.current state.Nav state.Session.Tree
                    |> Option.bind (fun c ->
                        match c.Kind with
                        | Chunk.UserRequest -> Some (c.Text, c.Parent)
                        | _ -> None))
            match request with
            | Some (text, parent) ->
                let busy = lock gate (fun () -> state.TurnInFlight)
                if busy then
                    speakNow "A response is still in progress. Wait for it to complete."
                else
                    speakNow (sprintf "Rerunning: %s" text)
                    startTurn text parent
            | None ->
                speakNow
                    "Focus a request to rerun it. Requests announce as: Your request."
        | KeyMap.ToggleNotebook ->
            let announcement =
                lock gate (fun () ->
                    match state.Mode with
                    | KeyMap.Transcript ->
                        state.Mode <- KeyMap.NotebookMode
                        let count = Notebook.count state.Notebook
                        if count = 0 then
                            "Notebook. It is empty — press n for the transcript, or pin chunks with p there."
                        else
                            state.NotebookIndex <-
                                max 0 (min state.NotebookIndex (count - 1))
                            sprintf "Notebook, %d cells." count
                    | KeyMap.NotebookMode ->
                        state.Mode <- KeyMap.Transcript
                        "Transcript.")
            speakNow announcement
        | KeyMap.NotebookMoveUp -> notebookReorder true
        | KeyMap.NotebookMoveDown -> notebookReorder false
        | KeyMap.NotebookRemove -> notebookRemove ()
        | KeyMap.NotebookNarrative -> notebookInsert false
        | KeyMap.NotebookSection -> notebookInsert true
        | KeyMap.ExportNotebook -> exportNotebook ()
        | KeyMap.SaveSession -> saveSession true
        | KeyMap.OpenLastSession ->
            let busy = lock gate (fun () -> state.TurnInFlight)
            if busy then
                speakNow "A response is still in progress. Wait for it to complete."
            else openLastSession ()
        | KeyMap.Diagnostics ->
            let summary = diag.Summary()
            let pathNote =
                try
                    Directory.CreateDirectory(diagnosticsDir) |> ignore
                    let path =
                        Path.Combine(
                            diagnosticsDir,
                            sprintf "dump-%s.txt"
                                (DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")))
                    File.WriteAllText(path, diag.Dump() + "\n")
                    sprintf " Dump written to %s" path
                with _ -> " Dump could not be written."
            speakNow (summary + pathNote)
        | KeyMap.RateUp | KeyMap.RateDown ->
            let rate =
                lock gate (fun () ->
                    let delta = if verb = KeyMap.RateUp then 1 else -1
                    state.Rate <- max -10 (min 10 (state.Rate + delta))
                    state.Rate)
            speech.SetRate rate
            speakNow (sprintf "Rate %d." rate)
        | KeyMap.Stop ->
            lock gate (fun () ->
                state.Queue <- Attention.clear state.Queue)
            speech.CancelAll ()
        | KeyMap.Help ->
            speakNow (KeyMap.helpFor mode bindings)
        | KeyMap.Quit -> running <- false

    // --- startup -------------------------------------------------------------
    match sink.VoiceNote with
    | Some note -> diag.Record "config" note
    | None -> ()

    let startupExtras =
        [ if not (List.isEmpty startupWarnings) then
            yield
                sprintf
                    "%d configuration warning%s; press d for details."
                    (List.length startupWarnings)
                    (if List.length startupWarnings = 1 then "" else "s")
          match sink.VoiceNote with
          | Some note -> yield note
          | None -> ()
          if File.Exists latestSessionPath then
            yield "A previous session is available. Press o to reopen it." ]

    enqueue (
        Attention.Foreground (
            "Engine ready. " + KeyMap.helpFor KeyMap.Transcript bindings))
    startupExtras
    |> List.iter (fun text -> enqueue (Attention.Foreground text))

    // --- key loop --------------------------------------------------------------
    while running do
        let key = Console.ReadKey(true)
        let mode = lock gate (fun () -> state.Mode)
        match key.Key with
        | ConsoleKey.DownArrow -> runVerb KeyMap.Next
        | ConsoleKey.UpArrow -> runVerb KeyMap.Previous
        | ConsoleKey.RightArrow when mode = KeyMap.Transcript ->
            runVerb KeyMap.Descend
        | ConsoleKey.LeftArrow when mode = KeyMap.Transcript ->
            runVerb KeyMap.Ascend
        | _ ->
            match KeyMap.tryFind mode key.KeyChar bindings with
            | Some verb -> runVerb verb
            | None -> ()

    saveSession false
    speech.CancelAll ()
    0
