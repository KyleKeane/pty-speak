namespace Engine.Core

open System

/// ADR 0014 C4 — the ear-first diagnostics substrate: a
/// thread-safe bounded ring recording everything a field bug
/// report needs (bus events, turn outcomes, config warnings,
/// host errors), with per-category counts, a speakable
/// summary, and a full plain-text dump. Instance-scoped (one
/// per host) like `EngineBus`.
module EngineDiagnostics =

    type Entry =
        { AtUtc: DateTime
          Category: string
          Message: string }

    /// Bounded ring: oldest entries fall off; counts are
    /// lifetime (they keep growing after eviction so the
    /// summary stays honest about volume).
    type Ring(capacity: int) =
        let gate: obj = obj ()
        let entries = System.Collections.Generic.Queue<Entry>()
        let mutable counts: Map<string, int> = Map.empty
        let mutable lastError: Entry option = None
        let startedUtc = DateTime.UtcNow

        new() = Ring(500)

        member _.StartedUtc = startedUtc

        member _.Record (category: string) (message: string) : unit =
            let entry =
                { AtUtc = DateTime.UtcNow
                  Category = category
                  Message = message }
            lock gate (fun () ->
                entries.Enqueue(entry)
                while entries.Count > capacity do
                    entries.Dequeue() |> ignore
                counts <-
                    counts
                    |> Map.change category (function
                        | Some n -> Some (n + 1)
                        | None -> Some 1)
                if category = "error" then
                    lastError <- Some entry)

        /// Lifetime count for one category (0 if never seen).
        member _.CountOf (category: string) : int =
            lock gate (fun () ->
                counts |> Map.tryFind category |> Option.defaultValue 0)

        /// Snapshot of the retained entries, oldest first.
        member _.Snapshot() : Entry list =
            lock gate (fun () -> entries |> List.ofSeq)

        /// The speakable one-breath summary (the `d` verb's
        /// first half). Counts are lifetime; the last error is
        /// included verbatim when one occurred.
        member this.Summary() : string =
            let uptime = DateTime.UtcNow - startedUtc
            let countsText =
                lock gate (fun () ->
                    if counts.IsEmpty then "No events recorded."
                    else
                        counts
                        |> Map.toList
                        |> List.sortBy fst
                        |> List.map (fun (category, n) ->
                            sprintf "%s %d" category n)
                        |> String.concat ", ")
            let errorTail =
                match lock gate (fun () -> lastError) with
                | Some e -> sprintf " Last error: %s" e.Message
                | None -> ""
            sprintf
                "Up %d minutes. %s.%s"
                (int uptime.TotalMinutes)
                countsText
                errorTail

        /// The full dump (the bug-report artifact): header +
        /// every retained entry, one per line, grep-friendly.
        member this.Dump() : string =
            let header =
                sprintf
                    "engine diagnostics dump\nstarted-utc=%s\ndumped-utc=%s\n%s\n---"
                    (startedUtc.ToString("o"))
                    (DateTime.UtcNow.ToString("o"))
                    (this.Summary())
            let lines =
                this.Snapshot()
                |> List.map (fun e ->
                    sprintf
                        "%s [%s] %s"
                        (e.AtUtc.ToString("o"))
                        e.Category
                        e.Message)
            String.concat "\n" (header :: lines)

    /// Render a bus event as a diagnostics line (category +
    /// terse payload — bodies are clipped: diagnostics traces
    /// shape, the session tree holds content).
    let describeEvent (event: EngineEvent.EngineEvent) : string * string =
        let clip (text: string) =
            if text.Length > 80 then text.Substring(0, 80) + "…"
            else text
        match event with
        | EngineEvent.RequestCaptured chunk ->
            "request", clip chunk.Text
        | EngineEvent.SessionStarted sid ->
            "session", sid
        | EngineEvent.ChunkSealed chunk ->
            "seal", sprintf "%A seq=%d" chunk.Kind chunk.CaptureSeq
        | EngineEvent.ResponseProgress count ->
            "progress", string count
        | EngineEvent.ResponseCompleted (isError, count) ->
            (if isError then "error" else "completed"),
            sprintf "chunks=%d" count
        | EngineEvent.EngineNote text ->
            "note", clip text
