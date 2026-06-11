namespace Engine.Voice

open System.Speech.Synthesis
open Engine.Core

/// ADR 0011 E7 — the Phase 0 self-voicing sink: Windows SAPI
/// through `System.Speech.Synthesis.SpeechSynthesizer`. In-box
/// voices, no install step, decades-stable — the bootstrap
/// implementation of the owned audio path (RELAUNCH-SPEC §4.6).
/// The §1.1 "fast, reliable narration" bar is ultimately met by
/// swapping a better TTS behind the same `ISpeechSink` seam;
/// the Phase 0 dogfood measures this sink against that bar
/// (time-to-first-phoneme, interrupt latency, drop-free reads).
///
/// `SpeakCompleted` fires for both finished and cancelled
/// utterances, so the host's drain loop advances either way.
type SapiSink() =
    let synth = new SpeechSynthesizer()
    do synth.SetOutputToDefaultAudioDevice()
    let completed = Event<unit>()
    do synth.SpeakCompleted.Add(fun _ -> completed.Trigger())

    interface ISpeechSink with
        member _.SpeakAsync(text: string) : unit =
            synth.SpeakAsync(text) |> ignore
        member _.CancelAll() : unit =
            synth.SpeakAsyncCancelAll()
        member _.UtteranceCompleted : IEvent<unit> =
            completed.Publish

    interface System.IDisposable with
        member _.Dispose() : unit =
            synth.Dispose()
