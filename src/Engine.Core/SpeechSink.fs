namespace Engine.Core

open System

/// RELAUNCH-SPEC §4.6 / §14.11 / ADR 0011 E7 — the self-voicing
/// channel's sink contract. The engine owns the audio path
/// end-to-end; a sink renders canonical text to audio and
/// reports utterance completion so the host can drain the
/// attention queue. Implementations are universal-event-bus
/// consumers' back-ends (Phase 0: `Engine.Voice.SapiSink` over
/// Windows SAPI); the contract itself is platform-free.
type ISpeechSink =
    inherit IDisposable
    /// Queue one utterance after anything already speaking.
    abstract member SpeakAsync: text: string -> unit
    /// Stop all current and queued speech immediately (the
    /// user's interrupt verb — §7.1 "interruptible").
    abstract member CancelAll: unit -> unit
    /// Fires when an utterance finishes (spoken to the end or
    /// cancelled); the host's drain trigger.
    abstract member UtteranceCompleted: IEvent<unit>
