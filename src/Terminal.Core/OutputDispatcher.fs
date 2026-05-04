namespace Terminal.Core

/// Stage 8a — output framework dispatcher + registries.
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

    /// Dispatch one `OutputEvent` through the active profile set
    /// and into each profile-decided channel. Channels are
    /// invoked sequentially in `ChannelDecision[]` order; each
    /// channel's own `Send` is responsible for any internal
    /// queueing or dispatcher-thread marshalling. Decisions
    /// referencing an unregistered channel are silently dropped.
    ///
    /// 8a profile contract: the Stream profile returns one
    /// decision per event (NVDA channel + `RenderText`). 8b/8c/8d
    /// add fan-out; the dispatcher is unchanged.
    let dispatch (event: OutputEvent) : unit =
        let profileSet = ProfileRegistry.getActiveProfileSet ()
        for profile in profileSet do
            let decisions = profile.Apply event
            for decision in decisions do
                match ChannelRegistry.lookup decision.Channel with
                | Some channel -> channel.Send event decision.Render
                | None -> ()
