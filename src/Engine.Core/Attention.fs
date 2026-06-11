namespace Engine.Core

open Engine.Core.EngineEvent

/// RELAUNCH-SPEC §7.3 / ADR 0011 E6 — the attention contract,
/// output-side enforcement. Two routing classes, always
/// distinguished:
///
///   * **Foreground** — the narrative thread (request
///     confirmations, completion announcements, navigation
///     reads). A single ordered, non-preemptible FIFO.
///   * **Ambient** — peripheral awareness (progress, lifecycle,
///     notes). Coalesced latest-wins per key; surfaced only
///     when no foreground utterance is waiting — ambient can
///     never interrupt the thread (§14.6).
///
/// Pure: the queue is data, the policy is a function. The host
/// drains the queue into an `ISpeechSink` as utterances finish.
module Attention =

    /// One thing to say, with its attention class.
    type Utterance =
        | Foreground of text: string
        /// `key` identifies the ambient stream (e.g.
        /// "progress") — a newer utterance with the same key
        /// replaces the queued older one.
        | Ambient of key: string * text: string

    /// The pending-speech queue. Ambient entries keep first-
    /// arrival key order but latest-wins content.
    type Queue =
        private
            { Fg: string list
              Amb: (string * string) list }

    let empty : Queue =
        { Fg = []; Amb = [] }

    let isEmpty (q: Queue) : bool =
        q.Fg.IsEmpty && q.Amb.IsEmpty

    /// Add an utterance per its class.
    let enqueue (utterance: Utterance) (q: Queue) : Queue =
        match utterance with
        | Foreground text ->
            { q with Fg = q.Fg @ [ text ] }
        | Ambient (key, text) ->
            let replaced, found =
                ((([], false)), q.Amb)
                ||> List.fold (fun (acc, found) (k, t) ->
                    if k = key then (acc @ [ (k, text) ], true)
                    else (acc @ [ (k, t) ], found))
            { q with
                Amb =
                    if found then replaced
                    else q.Amb @ [ (key, text) ] }

    /// The next utterance to speak: foreground strictly first;
    /// ambient only when no foreground waits.
    let tryDequeue (q: Queue) : (string * Queue) option =
        match q.Fg with
        | text :: rest -> Some (text, { q with Fg = rest })
        | [] ->
            match q.Amb with
            | (_, text) :: rest -> Some (text, { q with Amb = rest })
            | [] -> None

    /// The routing policy: engine event → utterance (or
    /// silence). Sealed chunks are deliberately NOT auto-spoken
    /// (§5.2 — the user navigates sealed content on demand; the
    /// ambient progress count carries in-flight awareness).
    let route (event: EngineEvent.EngineEvent) : Utterance option =
        match event with
        | RequestCaptured chunk ->
            // The §6.1 narrate-and-confirm echo.
            Some (Foreground (sprintf "Sent: %s" chunk.Text))
        | SessionStarted _ ->
            Some (Ambient ("session", "Session started."))
        | ChunkSealed _ ->
            None
        | ResponseProgress count ->
            Some (Ambient ("progress",
                           sprintf "%d chunks so far." count))
        | ResponseCompleted (false, count) ->
            Some (Foreground (
                    sprintf "Response complete, %d chunks." count))
        | ResponseCompleted (true, count) ->
            Some (Foreground (
                    sprintf "Response failed after %d chunks." count))
        | EngineNote text ->
            Some (Ambient ("note", text))
