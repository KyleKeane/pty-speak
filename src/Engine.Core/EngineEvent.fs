namespace Engine.Core

open System

/// RELAUNCH-SPEC §0.1 — the universal event bus, the engine's
/// instance. One typed semantic stream; every output mechanism
/// (the Phase 0 self-voicing channel first; spatial audio,
/// braille, haptics later) is merely a subscriber — never a
/// foundation, never privileged.
///
/// Unlike the WPF app's `Terminal.Core.CellEventBus` (global,
/// module-scoped — kept untouched per ADR 0011 E9), the engine
/// bus is **instance-scoped**: a host owns one bus per engine,
/// so tests and multiple engines compose without shared state.
/// The subscriber registry mirrors the battle-tested
/// token-keyed-map-under-a-lock shape.
module EngineEvent =

    /// The typed engine event vocabulary. Cases are added by
    /// the phase that wires them (the established discipline) —
    /// Phase 0 declares only what Phase 0 publishes.
    type EngineEvent =
        /// The user's composed request was captured into the
        /// tree (the §6.1 narrate-and-confirm subject).
        | RequestCaptured of Chunk.Chunk
        /// The participant session began (carries the CLI
        /// session id used for continuity).
        | SessionStarted of sessionId: string
        /// A chunk was sealed into the tree (§5.2 — only sealed
        /// chunks are ever published; navigation never observes
        /// a moving target).
        | ChunkSealed of Chunk.Chunk
        /// Ambient in-flight progress: chunks sealed so far in
        /// the current turn (§5.2's peripheral signal).
        | ResponseProgress of chunkCount: int
        /// The turn finished; `isError` is the stream's own
        /// flag. Carries the total sealed-chunk count so a
        /// completion announcement needs no tree query.
        | ResponseCompleted of isError: bool * chunkCount: int
        /// An engine-side diagnostic / lifecycle note (ambient;
        /// e.g. an unknown stream event surfaced per ADR 0008).
        | EngineNote of text: string

    /// The instance-scoped bus. Snapshot-then-fire under a
    /// lock; a throwing subscriber neither aborts the others
    /// nor propagates into the publisher (it must never break
    /// ingest). Subscriber exceptions are intentionally
    /// swallowed here — the Phase 0 host wires a logging
    /// subscriber for diagnostics rather than the bus taking a
    /// logger dependency.
    type EngineBus() =
        let gate: obj = obj ()
        let mutable nextToken = 0
        let mutable subscribers: Map<int, EngineEvent -> unit> =
            Map.empty

        /// Subscribe until the returned IDisposable is
        /// disposed. Token-keyed so disposing one subscription
        /// cannot remove another with an identical lambda.
        member _.Subscribe(handler: EngineEvent -> unit) : IDisposable =
            let token =
                lock gate (fun () ->
                    let t = nextToken
                    nextToken <- t + 1
                    subscribers <- subscribers |> Map.add t handler
                    t)
            { new IDisposable with
                member _.Dispose() =
                    lock gate (fun () ->
                        subscribers <- subscribers |> Map.remove token) }

        /// Publish one event to every current subscriber.
        member _.Publish(event: EngineEvent) : unit =
            let snapshot = lock gate (fun () -> subscribers)
            snapshot
            |> Map.iter (fun _ handler ->
                try
                    handler event
                with _ ->
                    ())
