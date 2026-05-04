namespace Terminal.Core

open Microsoft.Extensions.Logging

/// Stage 8c — FileLogger channel. Translates `OutputEvent` +
/// `RenderInstruction` into a structured `ILogger.LogInformation`
/// call so the rolling FileLogger sink (`Terminal.Core.FileLogger`,
/// itself unchanged from Stage 7) records every event the Stream
/// profile emits. The `Ctrl+Shift+;` clipboard-copy flow (PR-F)
/// then carries the full event trail for post-hoc diagnosis.
///
/// The channel is constructed with an `ILogger` parameter for
/// testability. Production composition
/// (`src/Terminal.App/Program.fs`) passes
/// `Logger.get "Terminal.Core.FileLoggerChannel"` (which routes
/// through the configured factory to the production
/// `FileLoggerSink`); tests pass a recording-logger fake to
/// capture LogInformation calls and assert on the
/// structured-template arguments.
///
/// **Empty-payload contract — CONTRARY to NvdaChannel.**
/// NvdaChannel skips `Announce` for empty-payload events (mode
/// barriers carry `""`); FileLoggerChannel does NOT skip. The
/// capture-everything-for-diagnosis behaviour is the spec-intended
/// contract — mode barriers, parser errors, every event lands in
/// the log even if the user-visible NVDA reading was silent. This
/// becomes load-bearing when the spec D.2 ParserError → Background
/// suppression activates in a future PR: ParserError events stop
/// reaching NVDA but continue to land in the FileLogger channel,
/// so post-hoc diagnosis sees them.
///
/// **RenderInstruction handling.** The channel extracts a payload
/// string from the `RenderInstruction` and writes it as the
/// `Payload` field of the structured log entry:
///
/// - `RenderText text` → `text`
/// - `RenderText2 (_, precise)` → `precise` (Precise register;
///   same convention NvdaChannel uses)
/// - `RenderEarcon earconId` → `"[earcon=<id>]"` placeholder so
///   the log captures earcon emission events even though the
///   actual playback goes through the 8d Earcon channel
/// - `RenderRaw _` → `"[raw payload]"` placeholder for opaque
///   per-channel data (e.g. UIA-listbox metadata from the 8e
///   Selection profile)
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.4.2 (FileLogger channel description) + Part C.1 (8c
/// row).
module FileLoggerChannel =

    /// Stable channel identifier registered with the dispatcher's
    /// `ChannelRegistry`. Profiles refer to this string in their
    /// `ChannelDecision.Channel` field.
    [<Literal>]
    let id: ChannelId = "filelogger"

    /// Extract a payload string from a `RenderInstruction` for the
    /// structured log entry's `Payload` field. Mirrors
    /// `NvdaChannel`'s render handling but does NOT skip on empty
    /// payloads — see module docstring for the
    /// capture-everything contract.
    let internal formatPayload (render: RenderInstruction) : string =
        match render with
        | RenderText text -> text
        | RenderText2 (_, precise) -> precise
        | RenderEarcon earconId -> sprintf "[earcon=%s]" earconId
        | RenderRaw _ -> "[raw payload]"

    /// Construct a Channel that writes a structured log line per
    /// event via the supplied `ILogger`. The structured template
    /// fields (Semantic / Priority / Verbosity / Producer / Shell
    /// / PayloadLen / Payload) feed the existing FileLogger sink's
    /// MPSC bounded channel — non-blocking from the dispatcher's
    /// perspective per spec B.5.3.
    let create (logger: ILogger) : Channel =
        { Id = id
          Send =
            fun event render ->
                let payload = formatPayload render
                let shell =
                    event.Source.Shell
                    |> Option.defaultValue ""
                logger.LogInformation(
                    "OutputEvent. Semantic={Semantic} Priority={Priority} Verbosity={Verbosity} Producer={Producer} Shell={Shell} PayloadLen={PayloadLen} Payload={Payload}",
                    event.Semantic,
                    event.Priority,
                    event.Verbosity,
                    event.Source.Producer,
                    shell,
                    payload.Length,
                    payload) }
