namespace Terminal.Core

open System
open Microsoft.Extensions.Logging

/// ADR 0007 D9 / Phase 6a-1 — the canonical, modality-agnostic
/// cell-event stream.
///
/// D9 locks that cell lifecycle / navigation / operation events
/// are first-class **typed** semantic events on one canonical
/// pipeline, with many composable sinks (speech, earcon, future
/// braille, the Phase 6a history list) — speech is not primary
/// with the rest bolted on. This module is that pipeline for the
/// cell layer: a single typed `CellEvent` published to N
/// subscribers. It is the cell-layer instance of ADR 0001's
/// substrate/channel dichotomy + ADR 0004 Decision 4 ("one
/// canonical unambiguous event stream, many sinks"); it runs
/// *parallel to*, not squeezed into, the byte-oriented
/// `OutputDispatcher` (cell events carry typed `CellView`s, not
/// rendered-text `OutputEvent`s — a sink must never re-derive
/// cell meaning from rendered output: ADR 0008).
///
/// Resolution of the ADR 0007 open decision "`pty-speak.cell.*`
/// ActivityId taxonomy / cell pipeline shape (Phase 6a / D9)":
/// the canonical cell pipeline is *this dedicated typed bus*
/// (not an `OutputDispatcher` route). The `pty-speak.cell.*`
/// `ActivityIds` are the per-event routing / NVDA-per-tag-config
/// tags a sink applies when it renders one. Settled here in 6a
/// per the ADR's "settle when 6a wires the first event".
///
/// Scope discipline (walking-skeleton): 6a-1 defines the
/// contract + the bus + wires the lowest-risk real publication
/// (`Focused`, off the existing user-nav handlers — additive,
/// the dogfood-validated direct announce is untouched). The
/// `Appended` seal event (the D8 list's update mechanism) and
/// the WPF list subscriber land in 6a-2, where their audible /
/// AT effect is dogfood-validated together (the D8 control-type
/// ratification gate). Only the case a shipped phase wires is
/// declared.
///
/// The subscriber registry mirrors
/// `OutputDispatcher.installEventTap` exactly (token-keyed map
/// under a lock, `IDisposable` unsubscribe, snapshot-then-fire)
/// so the concurrency reasoning is identical and already
/// battle-tested. `publish` is exception-guarded: a throwing
/// sink (a UI-thread marshal in 6a-2, say) must never break the
/// Manual-navigation path that published the event.
module CellEventBus =

    /// A typed cell event; the typed `CellView` is carried so
    /// sinks never re-derive cell meaning from rendered text
    /// (ADR 0008). Cases are added by the phase that wires them
    /// (not pre-declared — D9 "single-purpose"):
    ///
    ///   * `Focused` (6a-1) — the user moved the Manual cursor
    ///     onto a cell (off the four user-nav handlers).
    ///   * `Appended` (6a-2a) — a cell item was sealed into the
    ///     transcript (off the canonical `appendCell` seal site;
    ///     D8's list-update event). `appendCell` adds up to two
    ///     items per cell (Input then Output, whitespace-skipped)
    ///     sharing one `CellId`; one `Appended` is published per
    ///     item actually added, in append order.
    ///
    /// Later: `Operated` (Phase 6 D2 ops) / `Segment` (Phase 4)
    /// / `PaneSwitched` (6a-2b) — added when their phase ships.
    type CellEvent =
        | Focused of SpeechCursor.CellView
        | Appended of SpeechCursor.CellView

    let private log =
        Logger.get "Terminal.Core.CellEventBus"

    let private gate: obj = obj ()
    let mutable private nextToken: int = 0
    let mutable private subscribers: Map<int, CellEvent -> unit> =
        Map.empty

    /// Subscribe to every published `CellEvent` until the
    /// returned `IDisposable` is disposed. Token-keyed so
    /// disposing one subscription cannot remove another even
    /// when callers pass identical (capture-free) lambdas.
    let subscribe (handler: CellEvent -> unit) : IDisposable =
        let token =
            lock gate (fun () ->
                let t = nextToken
                nextToken <- t + 1
                subscribers <- subscribers |> Map.add t handler
                t)
        { new IDisposable with
            member _.Dispose () =
                lock gate (fun () ->
                    subscribers <- subscribers |> Map.remove token) }

    /// Fire all current subscribers for one event. The map is
    /// snapshotted under the lock so a concurrent
    /// subscribe/dispose cannot mutate the collection mid-fire;
    /// each subscriber is invoked outside the lock and guarded
    /// so one throwing sink neither aborts the others nor
    /// propagates back into the (navigation) caller.
    let publish (event: CellEvent) : unit =
        let snapshot = lock gate (fun () -> subscribers)
        snapshot
        |> Map.iter (fun _ handler ->
            try
                handler event
            with ex ->
                log.LogDebug(
                    ex,
                    "CellEventBus subscriber threw; continuing.") )

    /// Test isolation — drop all subscribers and reset the
    /// token counter. Mirrors `EarconChannel.clearForTests`.
    let clearForTests () : unit =
        lock gate (fun () ->
            subscribers <- Map.empty
            nextToken <- 0)
