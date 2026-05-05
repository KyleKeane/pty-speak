namespace Terminal.Core

/// Stage 8d.1 — Earcon profile. Maps `OutputEvent.Semantic` to
/// `RenderEarcon earconId` ChannelDecisions targeting the
/// `EarconChannel`. Other Semantic categories return `[||]` —
/// the profile is a purely additive observer for events it
/// claims, not a router for everything.
///
/// **8d.1 mapping (Bell only):**
/// - `BellRang` → `RenderEarcon "bell-ping"`
///
/// **8d.2 will add:**
/// - `ErrorLine` → `RenderEarcon "error-tone"`
/// - `WarningLine` → `RenderEarcon "warning-tone"`
///
/// 8d.2 also requires a producer that emits ErrorLine /
/// WarningLine OutputEvents based on row colors detected in
/// the screen snapshot — that's the "color detection" piece
/// the spec validation row references.
///
/// **Co-existence with StreamProfile.** When the dispatcher
/// fans out to the active profile set, both StreamProfile and
/// EarconProfile receive each event. For `BellRang` events:
/// StreamProfile's catch-all branch produces NVDA + FileLogger
/// decisions for the empty payload (NvdaChannel skips empty;
/// FileLogger logs the event). EarconProfile produces the
/// earcon decision. Total: bell ping plays + log entry; NVDA
/// stays silent (no double-up because empty payload).
///
/// **NVDA-channel suppression deferred.** Spec B.3.2 mentions
/// the Earcon profile "emits NVDA-channel suppression for
/// events the earcon channel claims". 8d.1 doesn't need this
/// because BellRang events have empty Payload and NvdaChannel
/// already skips empty. 8d.2's color-detection (which would
/// emit Payload with the colored row text) needs explicit
/// suppression to avoid the user hearing both the earcon AND
/// NVDA reading the colored row — that's a substrate API
/// extension (cross-profile suppression markers) that lands
/// with 8d.2.
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.3.2 (Earcon profile row).
module EarconProfile =

    /// Stable profile identifier registered with the dispatcher's
    /// `ProfileRegistry`. The 9c TOML config keys
    /// `[profile.earcon]` on this string.
    [<Literal>]
    let id: ProfileId = "earcon"

    /// Build a NVDA-irrelevant earcon decision for the supplied
    /// earcon id. Mirrors NvdaChannel / FileLoggerChannel's
    /// per-channel decision-builder helpers.
    let private earconDecision (earconId: string) : ChannelDecision =
        { Channel = EarconChannel.id
          Render = RenderEarcon earconId }

    /// Construct the Earcon profile. No parameters in 8d.1 (no
    /// per-instance state); 8d.2 / Phase 2 may add a
    /// `Parameters` record for things like a per-shell mute
    /// state or a custom palette override.
    let create () : Profile =
        { Id = id
          Apply =
            fun event ->
                match event.Semantic with
                | SemanticCategory.BellRang ->
                    [|
                        event,
                        [| earconDecision "bell-ping" |]
                    |]
                | SemanticCategory.ErrorLine ->
                    // Phase A.2 — re-introduces 8d.2's red →
                    // 400Hz error-tone earcon. StreamPathway
                    // emits an empty-payload `ErrorLine`
                    // OutputEvent alongside the StreamChunk
                    // when the frame is red-dominant.
                    // NvdaChannel skips the empty payload
                    // (NvdaChannel.fs:87 RenderText "" → no
                    // marshalAnnounce); EarconChannel plays the
                    // tone via the palette's "error-tone"
                    // entry. No double-announce.
                    [|
                        event,
                        [| earconDecision "error-tone" |]
                    |]
                | SemanticCategory.WarningLine ->
                    // Phase A.2 — yellow → 600Hz warning-tone.
                    // Same event-splitting design as ErrorLine.
                    [|
                        event,
                        [| earconDecision "warning-tone" |]
                    |]
                | _ ->
                    // No earcon for other Semantic categories.
                    [||]
          Tick =
            fun _ ->
                // No time-driven flush — earcons are
                // event-driven only.
                [||]
          Reset =
            fun () -> () }
