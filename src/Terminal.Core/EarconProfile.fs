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

    /// Stage 8d.2 — read the StreamProfile's colour-detection
    /// metadata from `OutputEvent.Extensions["dominantColor"]`.
    /// Returns the colour string ("red" / "yellow") if present
    /// and well-typed; `None` otherwise. Defensive against
    /// non-string Extensions values (which a future producer
    /// might emit) — bad-typed entries fall through to None
    /// rather than crashing the dispatcher.
    let private readDominantColor (event: OutputEvent) : string option =
        match Map.tryFind "dominantColor" event.Extensions with
        | Some boxed ->
            match boxed with
            | :? string as s -> Some s
            | _ -> None
        | None -> None

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
                | SemanticCategory.StreamChunk ->
                    // Stage 8d.2 — colour-detected streaming
                    // output. The StreamProfile stamps
                    // `Extensions["dominantColor"] = "red" |
                    // "yellow"` on its synthesised StreamChunk
                    // events when SGR-coloured rows dominate the
                    // post-coalesce snapshot. The Earcon profile
                    // maps red → 400Hz error-tone (lower pitch
                    // for higher urgency) and yellow → 600Hz
                    // warning-tone. Plain streaming (no colour
                    // metadata) emits nothing — earcons are
                    // supplementary, not per-event.
                    match readDominantColor event with
                    | Some "red" ->
                        [|
                            event,
                            [| earconDecision "error-tone" |]
                        |]
                    | Some "yellow" ->
                        [|
                            event,
                            [| earconDecision "warning-tone" |]
                        |]
                    | _ -> [||]
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
