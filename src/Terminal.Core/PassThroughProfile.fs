namespace Terminal.Core

/// Phase A — pass-through routing profile. Fans every
/// OutputEvent the dispatcher receives out to NvdaChannel +
/// FileLoggerChannel as `RenderText` decisions carrying
/// `event.Payload` verbatim.
///
/// **Why a separate profile.** Stage 8b's StreamProfile fused
/// two concerns: the coalescing / dedup / debounce algorithm
/// (now lives in StreamPathway) AND the channel fan-out (which
/// targets NvdaChannel + FileLoggerChannel for every event the
/// dispatcher routes). Phase A splits these — the pathway owns
/// the algorithm, this profile owns the routing. The split
/// matches the spec's Layer 3 / Layer 4 boundary: pathways
/// produce OutputEvents (semantic decisions), profiles produce
/// ChannelDecisions (rendering decisions).
///
/// **Behaviour-identical to the old StreamProfile catch-all.**
/// The Stage 8b StreamProfile.Apply ended with:
/// ```
/// | _ ->
///     [|
///         event,
///         textDecisions event.Payload
///     |]
/// ```
/// — fan every uncaught event to NVDA + FileLogger. This
/// profile is exactly that catch-all, lifted to a stand-alone
/// profile so the active set composes cleanly:
///   `[ passThroughProfile; earconProfile ]`
/// EarconProfile claims BellRang; passThrough fans every event
/// (BellRang included — its empty payload means NVDA skips and
/// FileLogger records the event for the audit trail). No
/// double-announce because NvdaChannel skips empty payloads.
///
/// **Cross-channel ordering.** Spec B.5.4 doesn't pin profile
/// ordering as load-bearing for v1; the active set order
/// determines which profile's decisions reach the channels
/// first, but channels are independent — NVDA's reading
/// schedule and FileLogger's write schedule don't interlock.
/// The composition root registers `[ passThroughProfile;
/// earconProfile ]` so for BellRang the FileLogger entry lands
/// before the EarconChannel plays the bell-ping (microseconds
/// of skew, irrelevant for the user).
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.3 (Profile abstraction). The pass-through pattern
/// is implicit in the spec's "profile is a pure function with
/// per-instance state" framing — a stateless routing profile
/// is the trivial case.
module PassThroughProfile =

    [<Literal>]
    let id: ProfileId = "passthrough"

    /// Build a NVDA-channel decision rendering the supplied
    /// text. Mirrors the old StreamProfile's nvdaTextDecision.
    let private nvdaTextDecision (text: string) : ChannelDecision =
        { Channel = NvdaChannel.id
          Render = RenderText text }

    /// Build a FileLogger-channel decision rendering the
    /// supplied text. Mirrors the old StreamProfile's
    /// fileLoggerTextDecision.
    let private fileLoggerTextDecision (text: string) : ChannelDecision =
        { Channel = FileLoggerChannel.id
          Render = RenderText text }

    /// Construct the pass-through profile. Stateless — no
    /// per-instance fields, `Reset` is a no-op, `Tick` returns
    /// no decisions (this profile doesn't accumulate).
    let create () : Profile =
        { Id = id
          Apply =
            fun event ->
                [|
                    event,
                    [|
                        nvdaTextDecision event.Payload
                        fileLoggerTextDecision event.Payload
                    |]
                |]
          Tick =
            fun _ ->
                [||]
          Reset =
            fun () -> () }
