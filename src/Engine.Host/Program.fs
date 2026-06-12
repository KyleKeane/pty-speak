module Engine.Host.Program

// RELAUNCH-SPEC §13 Phase 0 / ADR 0011 E8 — the local
// bootstrap host. Wires: console keyboard → navigation verbs +
// compose loop → Claude CLI participant → ingest → chunk tree →
// engine event bus → attention router → self-voicing SAPI sink.
//
// Keyboard model (E8 — keyboard-first; speech input arrives via
// OS dictation into the same compose line):
//
//   c       compose a request (type a line, Enter sends)
//   b       branch: compose anchored at the focused chunk
//   a       return to anchor
//   g       jump to the start of the latest response
//   j / ↓   next chunk        k / ↑   previous chunk
//   l / →   descend into chunk  h / ←  ascend to parent
//   r       re-narrate the focused chunk (full body)
//   w       where am I — breadcrumb + position + depth
//   s       stop speech
//   ?       speak the key list
//   q       quit
//
// Everything spoken goes through the attention queue
// (foreground strict FIFO; ambient coalesced) into the
// self-voicing sink — no console-output dependence; the printed
// mirror text is a debugging convenience only.

open System
open System.Threading
open Engine.Core
open Engine.Core.EngineEvent
open Engine.Participants

/// Shared mutable host state, all guarded by one lock: the
/// console thread (navigation, compose) and the turn thread
/// (participant events) both touch it.
type private HostState =
    { mutable Session: Ingest.Session
      mutable Nav: Navigator.State
      mutable Queue: Attention.Queue
      mutable Speaking: bool
      mutable TurnInFlight: bool }

let private helpText =
    "Keys: c compose. b branch at focus. a return to anchor. "
    + "g latest response. j next. k previous. l descend. "
    + "h ascend. r repeat. w where am I. s stop speech. q quit."

[<EntryPoint>]
let main _argv =
    let gate = obj ()
    let state : HostState =
        { Session = Ingest.empty
          Nav = Navigator.initial
          Queue = Attention.empty
          Speaking = false
          TurnInFlight = false }
    let bus = EngineBus()
    use sink = new Engine.Voice.SapiSink()
    let speech = sink :> ISpeechSink

    let claudeConfig : ClaudeCli.Config =
        let fromEnv =
            Environment.GetEnvironmentVariable "ENGINE_CLAUDE_PATH"
        match fromEnv with
        | null ->
            { ClaudeCli.defaultConfig with ExecutablePath = "claude.cmd" }
        | v when String.IsNullOrWhiteSpace v ->
            { ClaudeCli.defaultConfig with ExecutablePath = "claude.cmd" }
        | v ->
            { ClaudeCli.defaultConfig with ExecutablePath = v }

    // --- speech drain ------------------------------------------------
    let rec speakNext () =
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

    /// A user-initiated read preempts whatever is being spoken
    /// AND whatever narrative was queued (ADR 0012 S5): cancel
    /// first, drop stale pending foreground, then queue this.
    /// Ambient survives — awareness is not the user's target.
    let speakNow (text: string) =
        speech.CancelAll ()
        lock gate (fun () ->
            state.Queue <-
                Attention.enqueue
                    (Attention.Foreground text)
                    (Attention.clearForeground state.Queue))
        speakNext ()

    // --- bus → attention --------------------------------------------
    bus.Subscribe(fun ev ->
        match Attention.route ev with
        | Some utterance -> enqueue utterance
        | None -> ())
    |> ignore

    // --- bus → spatial stage (ADR 0012 S3/S4) -------------------------
    // The second universal-event-bus consumer: every event
    // renders as its deterministic stereo-stage signature, in
    // parallel with (never gating) speech.
    bus.Subscribe(fun ev ->
        match SpatialCue.forEvent ev with
        | Some cue -> Engine.Audio.SpatialPlayer.play cue
        | None -> ())
    |> ignore

    // --- participant turn -------------------------------------------
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
                            "Could not run the participant: %s. Set ENGINE_CLAUDE_PATH to the claude executable."
                            message))
        let thread = Thread(worker)
        thread.IsBackground <- true
        thread.Start()

    // --- navigation --------------------------------------------------
    // Navigation reads are bounded (ADR 0012 S5); `r` reads
    // the full body.
    let moveReadCapChars = 600

    let narrateMove (move: Navigator.Move) =
        match move with
        | Navigator.Moved chunk ->
            // ADR 0012 S2 — moves carry their position.
            let text =
                lock gate (fun () ->
                    ChunkNarration.describeAt
                        moveReadCapChars
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
        // Direction-coded cue first (instant, non-verbal),
        // then the spoken read (ADR 0012 S3).
        let moved =
            match move with
            | Navigator.Moved _ -> true
            | _ -> false
        Engine.Audio.SpatialPlayer.play (SpatialCue.forNav direction moved)
        narrateMove move

    /// Returns true when a turn was actually started — the
    /// branch path pushes its anchor only on success, so an
    /// aborted compose (busy / empty line) leaves no stale
    /// anchor on the stack.
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

    // --- main key loop -----------------------------------------------
    enqueue (Attention.Foreground (
        "Engine ready. " + helpText))

    let mutable running = true
    while running do
        let key = Console.ReadKey(true)
        match key.Key with
        | ConsoleKey.Q -> running <- false
        | ConsoleKey.C -> compose None |> ignore
        | ConsoleKey.B ->
            // Branch: anchor at the focused chunk (§5.1); the
            // anchor stack remembers where to return. Pushed
            // only when the branch request is actually sent —
            // an aborted compose must not leave a stale anchor.
            let anchor = lock gate (fun () -> state.Nav.Current)
            match anchor with
            | Some id ->
                if compose (Some id) then
                    lock gate (fun () ->
                        state.Nav <- Navigator.pushAnchor state.Nav)
            | None ->
                speakNow "Focus a chunk first, then press b to branch from it."
        | ConsoleKey.A ->
            navigate SpatialCue.ReturnToAnchor Navigator.returnToAnchor
        | ConsoleKey.G ->
            let latest = lock gate (fun () -> state.Session.LatestResponseStart)
            navigate SpatialCue.Jump (Navigator.jumpToLatestResponse latest)
        | ConsoleKey.J | ConsoleKey.DownArrow ->
            navigate SpatialCue.Next Navigator.next
        | ConsoleKey.K | ConsoleKey.UpArrow ->
            navigate SpatialCue.Previous Navigator.previous
        | ConsoleKey.L | ConsoleKey.RightArrow ->
            navigate SpatialCue.Descend Navigator.descend
        | ConsoleKey.H | ConsoleKey.LeftArrow ->
            navigate SpatialCue.Ascend Navigator.ascend
        | ConsoleKey.R ->
            let chunk =
                lock gate (fun () ->
                    Navigator.current state.Nav state.Session.Tree
                    |> Option.map (fun c ->
                        ChunkNarration.describe state.Session.Tree c))
            match chunk with
            | Some text -> speakNow text
            | None -> speakNow "Nothing is focused."
        | ConsoleKey.W ->
            // ADR 0012 S2 — the where-verb: breadcrumb +
            // position + depth, from the tree (never drifts).
            let located =
                lock gate (fun () ->
                    Navigator.current state.Nav state.Session.Tree
                    |> Option.map (fun c ->
                        ChunkNarration.locate state.Session.Tree c))
            match located with
            | Some text -> speakNow text
            | None -> speakNow "Nothing is focused."
        | ConsoleKey.S ->
            // Stop means stop (ADR 0012 S5): silence the
            // current utterance AND everything queued behind
            // it — nothing may resume speaking after a stop.
            lock gate (fun () ->
                state.Queue <- Attention.clear state.Queue)
            speech.CancelAll ()
        | _ when key.KeyChar = '?' -> speakNow helpText
        | _ -> ()

    speech.CancelAll ()
    0
