namespace Terminal.Core

/// Stage 8a — Stream profile (pass-through stub).
///
/// 8a's Stream profile is the substrate seam between the
/// existing Coalescer drain and the new framework: every
/// `OutputEvent` produces exactly one `ChannelDecision`
/// targeting the NVDA channel with a `RenderText` of the
/// event's payload. No debounce, no spinner-suppress, no
/// max-announce-chars cap — those still live in the existing
/// `Coalescer.runLoop` + drain caller in 8a.
///
/// **8b absorbs the Coalescer.** When stage 8b lands, this
/// module gains the `Parameters` record (DebounceWindowMs /
/// SpinnerWindowMs / SpinnerThreshold / MaxAnnounceChars per
/// the PR-N docstring contract in `Coalescer.fs:82-114`) and
/// `Apply` becomes the real coalescing logic. The `create`
/// signature evolves to take parameters; the 8a no-arg
/// constructor remains for backward-compat where the absent
/// args read the PR-N module-default constants.
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.3.2 (the "Stream profile" row of the v1 profile
/// table).
module StreamProfile =

    /// Stable profile identifier registered with the
    /// dispatcher's `ProfileRegistry`. The TOML
    /// `[profile.stream]` section in 9c keys on this string.
    [<Literal>]
    let id: ProfileId = "stream"

    /// Construct a Stream profile instance. 8a takes no
    /// parameters; 8b adds the `Parameters` record argument
    /// when the Coalescer absorbs.
    let create () : Profile =
        { Id = id
          Apply =
            fun event ->
                [| { Channel = NvdaChannel.id
                     Render = RenderText event.Payload } |]
          Reset = fun () -> () }
