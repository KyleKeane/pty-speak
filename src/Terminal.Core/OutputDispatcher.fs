namespace Terminal.Core

open System

/// Stage 8a — output framework dispatcher + registries (extended
/// in 8b with `dispatchTick` for time-driven flush).
///
/// One file holds three submodules so the abstraction surface
/// stays small. The shape mirrors the canonical extensibility
/// pattern in this repo (`HotkeyRegistry.fs` from PR-O #147 and
/// `ShellRegistry.fs` from PR-B): module-level mutable map +
/// `register` / `lookup` accessors + a tiny lock around
/// read-modify-write so registration races don't drop entries.
///
/// **Concurrency contract.** All registration (`ChannelRegistry.register`
/// + `ProfileRegistry.register` + `ProfileRegistry.setActiveProfileSet`)
/// happens on the WPF main thread at composition time, before
/// the drain task starts dispatching. After dispatch begins,
/// reads alone happen on the drain thread. The internal
/// `Map<,>` values are immutable; updates swap the reference
/// atomically under the lock; reads see either the old or new
/// reference, never partial state. Stage 9c (TOML config load)
/// converts this to a load-once-and-freeze pattern.
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.5.4 dispatcher pseudocode.
module OutputDispatcher =

    /// Channel registry — name → `Channel` lookup. Channels
    /// register themselves at composition time; profiles
    /// reference channels by `ChannelId` string in their
    /// `ChannelDecision`s.
    module ChannelRegistry =
        let private channelLock: obj = obj ()
        let mutable private channels: Map<ChannelId, Channel> = Map.empty

        /// Register a channel. Idempotent — re-registering with
        /// the same `Id` replaces the previous entry. 8a
        /// composition root registers exactly one (`NvdaChannel`).
        let register (channel: Channel) : unit =
            lock channelLock (fun () ->
                channels <- channels |> Map.add channel.Id channel)

        /// Look up a channel by ID. Returns `None` if no entry
        /// exists. The dispatcher silently drops decisions whose
        /// channel is not registered (8a contract; a future
        /// stage may surface this via the FileLogger channel
        /// once 8c lands).
        let lookup (channelId: ChannelId) : Channel option =
            Map.tryFind channelId channels

        /// Test-only — clear all registrations. Tests use this
        /// in their setup to ensure isolation across runs.
        /// Production composition root never calls this.
        let internal clearForTests () : unit =
            lock channelLock (fun () -> channels <- Map.empty)

    /// Profile registry — name → `Profile` lookup + the active
    /// profile set the dispatcher consults on each event.
    module ProfileRegistry =
        let private profileLock: obj = obj ()
        let mutable private profiles: Map<ProfileId, Profile> = Map.empty
        let mutable private activeProfileSet: Profile list = []

        /// Register a profile. Idempotent — re-registering with
        /// the same `Id` replaces the previous entry. 8a
        /// composition root registers exactly one
        /// (`StreamProfile`).
        let register (profile: Profile) : unit =
            lock profileLock (fun () ->
                profiles <- profiles |> Map.add profile.Id profile)

        /// Look up a profile by ID. Returns `None` if no entry
        /// exists.
        let lookup (profileId: ProfileId) : Profile option =
            Map.tryFind profileId profiles

        /// Set the active profile list. The dispatcher applies
        /// these in order on each event and concatenates their
        /// `ChannelDecision[]`s. Stage 8f wires this to the
        /// shell-switch path (each shell's TOML
        /// `[shell.X] profiles = [...]` becomes the active set
        /// on hot-switch).
        let setActiveProfileSet (set: Profile list) : unit =
            lock profileLock (fun () -> activeProfileSet <- set)

        /// Read the active profile set. Returns the empty list
        /// if nothing has been set (the dispatcher then no-ops).
        let getActiveProfileSet () : Profile list =
            activeProfileSet

        /// Test-only — clear all registrations. See
        /// `ChannelRegistry.clearForTests`.
        let internal clearForTests () : unit =
            lock profileLock (fun () ->
                profiles <- Map.empty
                activeProfileSet <- [])

    /// Route a `(effectiveEvent, decisions)` pair to its
    /// channels. Each ChannelDecision is sent to the channel
    /// resolved by `ChannelRegistry.lookup`; the channel's
    /// `Send` receives the effectiveEvent for activity-ID /
    /// notification-processing decisions, and the Render for
    /// the actual emission. Decisions referencing an
    /// unregistered channel are silently dropped.
    let private routePair (effectiveEvent: OutputEvent) (decisions: ChannelDecision[]) : unit =
        for decision in decisions do
            match ChannelRegistry.lookup decision.Channel with
            | Some channel -> channel.Send effectiveEvent decision.Render
            | None -> ()

    /// Dispatch one `OutputEvent` through the active profile set
    /// and into each profile-decided channel. Each profile's
    /// `Apply` returns an array of `(effectiveEvent, decisions)`
    /// pairs (most profiles return one pair carrying the input
    /// event verbatim; the Stream profile may return two pairs
    /// when a mode-change forces a pending-stream flush). The
    /// dispatcher routes each pair through `routePair`.
    let dispatch (event: OutputEvent) : unit =
        let profileSet = ProfileRegistry.getActiveProfileSet ()
        for profile in profileSet do
            let pairs = profile.Apply event
            for (effectiveEvent, decisions) in pairs do
                routePair effectiveEvent decisions

    /// Dispatch a time-driven tick. Called by the composition
    /// root's TickPump task on each `PeriodicTimer` tick. Each
    /// active profile's `Tick` returns `(effectiveEvent,
    /// decisions)` pairs the same way `Apply` does; profiles
    /// that don't accumulate (Selection, Earcon, the future
    /// Form / TUI / REPL profiles) supply
    /// `Tick = fun _ -> [||]`, and `dispatchTick` then calls
    /// `routePair` zero times for them.
    ///
    /// Stage 8b adds this. The Stream profile uses it to release
    /// pending stream content when the debounce window elapses
    /// with no new event arriving (the Stage-7 trailing-edge
    /// flush).
    let dispatchTick (now: DateTimeOffset) : unit =
        let profileSet = ProfileRegistry.getActiveProfileSet ()
        for profile in profileSet do
            let pairs = profile.Tick now
            for (effectiveEvent, decisions) in pairs do
                routePair effectiveEvent decisions
