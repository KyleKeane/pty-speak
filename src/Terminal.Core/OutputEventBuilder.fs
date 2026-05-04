namespace Terminal.Core

/// Stage 8a — translation from the existing
/// `Coalescer.CoalescedNotification` DU to the new framework
/// `OutputEvent`. The drain task in
/// `src/Terminal.App/Program.fs` calls into this module instead
/// of constructing the `(message, activityId)` tuple inline.
///
/// Mapping per spec
/// `spec/event-and-output-framework.md` Part D.2 (with `OutputBatch`
/// taking the `RowsChanged` slot, since the Coalescer in 8a is
/// the producer of post-debounce `OutputBatch` events that 8b
/// will absorb into the Stream profile):
///
/// | `CoalescedNotification` | `Semantic` | `Priority` |
/// |---|---|---|
/// | `OutputBatch text` | `StreamChunk` | `Polite` |
/// | `ErrorPassthrough s` | `ParserError` | `Background` |
/// | `ModeBarrier(AltScreen, true)` | `AltScreenEntered` | `Assertive` |
/// | `ModeBarrier(AltScreen, false)` | `ModeBarrier` | `Assertive` |
/// | `ModeBarrier(other, _)` | `ModeBarrier` | `Polite` |
///
/// Note: spec D.2 maps `ParserError → Background`, where
/// Background is "suppressed at profile layer; never emitted as
/// UIA notification" per spec B.5.2. Stage 8a's Stream profile
/// is a pass-through and does NOT honour that suppression — see
/// `OutputEventTypes.fs` `Priority` documentation. This means
/// 8a behaviour is identical to Stage 7: parser errors continue
/// to reach NVDA via `pty-speak.error`. The Background contract
/// activates in 8b/8c when profiles + channels start consulting
/// `Priority`.
///
/// All payloads are already sanitised by the time they reach
/// this builder: `OutputBatch` text comes from
/// `Coalescer.renderRows` which sanitises per-row, and
/// `ErrorPassthrough` text comes from `Coalescer.onParserError`
/// which sanitises. The builder just adds the spec D.2 metadata.
module OutputEventBuilder =

    /// Stable producer identifier the framework dispatch path
    /// records on every event the drain emits. 8a producer is
    /// "drain"; 8b moves this to "stream" when the Coalescer
    /// absorbs as the Stream profile.
    let internal producerId = "drain"

    /// Translate a `CoalescedNotification` into an `OutputEvent`.
    /// Caller (Program.fs drain) passes the post-cap text for
    /// `OutputBatch` so the 500-char announce stopgap stays in
    /// the drain (where it lives today). 8b moves the cap into
    /// `StreamProfile.Apply` once the profile owns the
    /// max-announce-chars parameter.
    let fromCoalescedNotification
        (coalesced: Coalescer.CoalescedNotification)
        : OutputEvent
        =
        match coalesced with
        | Coalescer.OutputBatch text ->
            OutputEvent.create
                SemanticCategory.StreamChunk
                Priority.Polite
                producerId
                text
        | Coalescer.ErrorPassthrough s ->
            // Preserves the Stage 7 drain's exact wrapping so
            // NVDA reads the same text post-retrofit.
            OutputEvent.create
                SemanticCategory.ParserError
                Priority.Background
                producerId
                (sprintf "Terminal parser error: %s" s)
        | Coalescer.ModeBarrier (flag, value) ->
            let semantic, priority =
                match flag, value with
                | TerminalModeFlag.AltScreen, true ->
                    SemanticCategory.AltScreenEntered, Priority.Assertive
                | TerminalModeFlag.AltScreen, false ->
                    SemanticCategory.ModeBarrier, Priority.Assertive
                | _ ->
                    SemanticCategory.ModeBarrier, Priority.Polite
            // Stage 5's mode-barrier announcement is the empty
            // string — see `Types.fs:290-294` (`ActivityIds.mode`).
            // The empty payload survives behaviour-identically;
            // a future stage may flip this to a verbosity-aware
            // description.
            OutputEvent.create semantic priority producerId ""
