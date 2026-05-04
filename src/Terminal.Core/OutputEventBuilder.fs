namespace Terminal.Core

/// Stage 8b — translation from raw `ScreenNotification` to raw
/// `OutputEvent`. The `TranslatorPump` task in
/// `src/Terminal.App/Program.fs` reads ScreenNotifications from
/// the parser-side `notificationChannel` and calls into this
/// module to build the OutputEvents the dispatcher routes.
///
/// **Pre-coalesce, post-translation.** Stage 8a's translator
/// ran AFTER the Coalescer (`fromCoalescedNotification`); the
/// CoalescedNotification carried post-debounce, post-cap text.
/// Stage 8b's translator runs BEFORE coalescing — the Stream
/// profile's `Apply` is now the producer of post-coalesce
/// events. So the OutputEvents this module builds carry minimal
/// payloads:
///
/// | `ScreenNotification` | `Semantic` | `Priority` | `Payload` |
/// |---|---|---|---|
/// | `RowsChanged _` | `StreamChunk` | `Polite` | `""` (empty — Stream profile reads the screen) |
/// | `ParserError msg` | `ParserError` | `Background` | sanitised + wrapped error message |
/// | `ModeChanged(AltScreen, true)` | `AltScreenEntered` | `Assertive` | `""` |
/// | `ModeChanged(AltScreen, false)` | `ModeBarrier` | `Assertive` | `""` |
/// | `ModeChanged(other, _)` | `ModeBarrier` | `Polite` | `""` |
///
/// **Sanitisation.** ParserError text is sanitised through
/// `AnnounceSanitiser.sanitise` before placement in Payload —
/// the producer-responsibility chokepoint per spec B.2.4 step 5
/// + the PR-N entry-gate contract. The Stream profile's
/// `Apply`/`Tick` flushes preserve the same sanitisation
/// invariant via `Coalescer.renderRows` per-row sanitising.
///
/// **Spec D.2 ParserError → Background note.** Carried over
/// from 8a: spec D.2 maps ParserError to Background, where
/// Background is "suppressed at profile layer" per B.5.2. The
/// 8b Stream profile's Apply does NOT suppress Background
/// events — it dispatches a ChannelDecision that announces via
/// NVDA. So 8b's behaviour matches Stage 7 (parser errors reach
/// NVDA via ActivityIds.error). The Background contract
/// activates when a future stage's profile or channel starts
/// honouring it.
module OutputEventBuilder =

    /// Stable producer identifier for translator-built events.
    /// 8b producer is "translator"; the StreamProfile's
    /// post-coalesce events use producer "stream" (set inside
    /// `StreamProfile.create`'s Apply / Tick closures).
    let internal producerId = "translator"

    /// Translate a raw `ScreenNotification` into a raw
    /// `OutputEvent` the dispatcher can route through the active
    /// profile set. The Stream profile's Apply matches on the
    /// resulting Semantic and either accumulates (StreamChunk),
    /// produces an immediate decision (ParserError), or flushes +
    /// emits a barrier (AltScreenEntered / ModeBarrier).
    let fromScreenNotification (notification: ScreenNotification) : OutputEvent =
        match notification with
        | RowsChanged _ ->
            OutputEvent.create
                SemanticCategory.StreamChunk
                Priority.Polite
                producerId
                ""
        | ParserError msg ->
            OutputEvent.create
                SemanticCategory.ParserError
                Priority.Background
                producerId
                (sprintf "Terminal parser error: %s"
                    (AnnounceSanitiser.sanitise msg))
        | ModeChanged (flag, value) ->
            let semantic, priority =
                match flag, value with
                | TerminalModeFlag.AltScreen, true ->
                    SemanticCategory.AltScreenEntered, Priority.Assertive
                | TerminalModeFlag.AltScreen, false ->
                    SemanticCategory.ModeBarrier, Priority.Assertive
                | _ ->
                    SemanticCategory.ModeBarrier, Priority.Polite
            OutputEvent.create semantic priority producerId ""
        | Bell ->
            // Stage 8d.1 — BEL (0x07) → BellRang. Empty Payload
            // (BEL is a pure signal). Priority = Assertive
            // because BEL is a purposeful user-attention event
            // that the shell deliberately emitted; the Earcon
            // profile maps to a bell-ping; the NvdaChannel
            // sees the empty payload and skips its announce
            // (so no double-up between earcon and NVDA).
            OutputEvent.create
                SemanticCategory.BellRang
                Priority.Assertive
                producerId
                ""
