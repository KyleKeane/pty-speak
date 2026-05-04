namespace Terminal.Core

/// Stage 8a — NVDA channel. Translates `OutputEvent` +
/// `RenderInstruction` into the existing
/// `TerminalView.Announce(message, activityId)` 2-arg overload
/// path, preserving the Stage-7 NotificationProcessing mapping
/// (`output → ImportantAll`, everything else → `MostRecent`,
/// implemented at `src/Views/TerminalView.cs:292-298`).
///
/// The channel is constructed with a marshalling callback the
/// caller (`src/Terminal.App/Program.fs`) binds to the WPF
/// dispatcher hop. This keeps `Terminal.Core` free of WPF
/// dependencies; the caller bridges:
///
/// ```fsharp
/// let nvda =
///     NvdaChannel.create (fun (msg, activityId) ->
///         window.Dispatcher.InvokeAsync(Action(fun () ->
///             window.TerminalSurface.Announce(msg, activityId)))
///         |> ignore)
/// ```
///
/// **8a does NOT consult `OutputEvent.Priority`.** The
/// behaviour-identical contract preserves the Stage-7 mapping
/// where `Priority` plays no role. A future sub-stage migrates
/// the channel to the 3-arg `Announce(msg, activityId,
/// processing)` overload + reads `Priority` from the event;
/// that change is its own behaviour-changing PR with its own
/// NVDA validation row.
///
/// **Empty-payload skip.** The Stage-7 drain skips
/// `Announce` for empty-message events (mode barriers carry
/// `""`); the channel preserves that — `RenderText ""` calls
/// the marshal callback zero times. Other channels (e.g. the
/// 8c FileLogger channel) may choose to log mode barriers even
/// with empty payloads.
///
/// **Earcon / Raw render skip.** `RenderEarcon` is the
/// producer-side instruction for the 8d Earcon channel;
/// `RenderRaw` is opaque per-channel data (e.g. UIA-listbox
/// metadata in 8e). NVDA channel ignores both — the dispatcher
/// fans them to their respective channels separately.
module NvdaChannel =

    /// Stable channel identifier registered with the dispatcher's
    /// `ChannelRegistry`. Profiles refer to this string in their
    /// `ChannelDecision.Channel` field.
    [<Literal>]
    let id: ChannelId = "nvda"

    /// Map a `SemanticCategory` to the activity-ID string the
    /// `TerminalView.Announce` overload routes on. The mapping
    /// follows the Stage-7 vocabulary in `Types.fs:275-333`. New
    /// sub-stages add their own producer cases (8d / 8e); the
    /// pre-claim mapping for not-yet-emitted categories defaults
    /// to `output` so an early producer accidentally landing
    /// before its NVDA-validation row would still announce on
    /// the streaming-output channel rather than nothing.
    let internal semanticToActivityId (semantic: SemanticCategory) : string =
        match semantic with
        | SemanticCategory.StreamChunk -> ActivityIds.output
        | SemanticCategory.ErrorLine
        | SemanticCategory.WarningLine
        | SemanticCategory.ParserError -> ActivityIds.error
        | SemanticCategory.AltScreenEntered
        | SemanticCategory.ModeBarrier -> ActivityIds.mode
        | SemanticCategory.SelectionShown
        | SemanticCategory.SelectionItem
        | SemanticCategory.SelectionDismissed -> ActivityIds.output
        | SemanticCategory.SpinnerTick
        | SemanticCategory.BellRang
        | SemanticCategory.HyperlinkOpened
        | SemanticCategory.PromptDetected
        | SemanticCategory.CommandSubmitted -> ActivityIds.output
        | SemanticCategory.Custom _ -> ActivityIds.output

    /// Construct a Channel that announces via the supplied
    /// marshalling callback. The callback receives `(message,
    /// activityId)` and is responsible for the WPF dispatcher
    /// hop + the actual `TerminalView.Announce` call.
    let create (marshalAnnounce: string * string -> unit) : Channel =
        { Id = id
          Send =
            fun event render ->
                let activityId = semanticToActivityId event.Semantic
                match render with
                | RenderText "" -> ()
                | RenderText text -> marshalAnnounce (text, activityId)
                | RenderText2 (_, "") -> ()
                | RenderText2 (_, precise) ->
                    // 8a always picks the Precise register —
                    // no user-facing verbosity hotkey ships
                    // until later stages.
                    marshalAnnounce (precise, activityId)
                | RenderEarcon _ -> ()
                | RenderRaw _ -> () }
