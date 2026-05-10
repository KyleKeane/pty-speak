namespace Terminal.Core

open System
open System.Text

/// Cycle 38c — strips cmd / PowerShell input echo from NVDA
/// announcements while preserving the full payload in the
/// FileLogger audit trail.
///
/// **Active set role.** Replaces `PassThroughProfile` in cmd /
/// PowerShell's active profile set (per Cycle 38b's per-shell
/// route table). For `StreamChunk` events, consults the shared
/// `EchoCorrelator` to find the echo prefix and strips it before
/// fanning the announce. For all other events, behaves
/// IDENTICALLY to `PassThroughProfile` (NVDA + FileLogger
/// RenderText). Other profiles in the active set
/// (`EarconProfile`, `SelectionProfile`) run normally.
///
/// **Per-shell gating** is at the active-set level, not inside
/// this profile. Claude's active set still uses
/// `PassThroughProfile` (no echo issue; OSC 133 marks input
/// versus output explicitly). If this profile ends up in Claude's
/// set by mistake, the correlator simply doesn't have matching
/// bytes (Claude's user input goes through different keystroke
/// translation) and the profile degrades gracefully to
/// PassThrough behaviour.
///
/// **FileLogger audit annotation.** When a payload is FULLY
/// echo, the NVDA decision is dropped but the FileLogger gets a
/// `(suppressed echo: ...)` annotation so `Ctrl+Shift+D` bundles
/// preserve the suppression trail for post-hoc debugging. For
/// PARTIAL echoes, FileLogger gets the full original payload
/// (so the audit trail matches what the shell actually wrote).
module EchoSuppressorProfile =

    [<Literal>]
    let id: ProfileId = "echo-suppressor"

    let private nvdaTextDecision (text: string) : ChannelDecision =
        { Channel = NvdaChannel.id
          Render = RenderText text }

    let private fileLoggerTextDecision (text: string) : ChannelDecision =
        { Channel = FileLoggerChannel.id
          Render = RenderText text }

    /// Build the profile wrapping a specific `EchoCorrelator`
    /// instance. The composition root constructs ONE correlator
    /// per process, wires its `recordWrite` into the WriteBytes
    /// path, and passes the same instance here.
    let create (correlator: EchoCorrelator.T) : Profile =
        let apply (event: OutputEvent) : (OutputEvent * ChannelDecision[])[] =
            match event.Semantic with
            | SemanticCategory.StreamChunk
              when not (String.IsNullOrEmpty event.Payload) ->
                let now = DateTime.UtcNow
                let matchedBytes =
                    EchoCorrelator.matchAndConsumeEchoPrefix
                        correlator now event.Payload
                let payloadBytes = Encoding.UTF8.GetBytes(event.Payload)
                if matchedBytes = 0 then
                    // No echo prefix; behave as PassThrough catch-all.
                    [| event,
                       [| nvdaTextDecision event.Payload
                          fileLoggerTextDecision event.Payload |] |]
                elif matchedBytes >= payloadBytes.Length then
                    // Entire payload is echo. Drop NVDA decision;
                    // emit FileLogger with audit annotation.
                    [| event,
                       [| fileLoggerTextDecision
                            (sprintf "(suppressed echo: %s)" event.Payload) |] |]
                else
                    // Partial echo. Strip prefix from NVDA
                    // payload; FileLogger gets the full original.
                    let strippedPayload =
                        try
                            Encoding.UTF8.GetString(
                                payloadBytes,
                                matchedBytes,
                                payloadBytes.Length - matchedBytes)
                        with _ ->
                            // UTF-8 multi-byte char split: fall
                            // back to character-index slice.
                            event.Payload.Substring(
                                min matchedBytes event.Payload.Length)
                    [| event,
                       [| nvdaTextDecision strippedPayload
                          fileLoggerTextDecision event.Payload |] |]
            | _ ->
                // Non-StreamChunk or empty-payload: behave as
                // PassThrough catch-all.
                [| event,
                   [| nvdaTextDecision event.Payload
                      fileLoggerTextDecision event.Payload |] |]
        { Id = id
          Apply = apply
          Tick = fun _ -> [||]
          Reset = fun () -> () }
