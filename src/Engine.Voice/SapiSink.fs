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
type SapiSink(initialRate: int, voiceName: string option) =
    let synth = new SpeechSynthesizer()
    do synth.SetOutputToDefaultAudioDevice()
    do synth.Rate <- max -10 (min 10 initialRate)
    // ADR 0014 C3 — voice selection: case-insensitive substring
    // match over installed voices; a miss is surfaced as a note
    // (spoken by the host once), never guessed at.
    let voiceNote =
        match voiceName with
        | None -> None
        | Some requested ->
            let installed =
                try
                    synth.GetInstalledVoices()
                    |> Seq.filter (fun v -> v.Enabled)
                    |> Seq.map (fun v -> v.VoiceInfo.Name)
                    |> List.ofSeq
                with _ -> []
            let matched =
                installed
                |> List.tryFind (fun name ->
                    name.Contains(
                        requested,
                        System.StringComparison.OrdinalIgnoreCase))
            match matched with
            | Some name ->
                try
                    synth.SelectVoice(name)
                    None
                with _ ->
                    Some (
                        sprintf
                            "Voice %s could not be selected; using the default voice."
                            requested)
            | None ->
                Some (
                    sprintf
                        "No installed voice matches %s; using the default voice."
                        requested)
    let completed = Event<unit>()
    do synth.SpeakCompleted.Add(fun _ -> completed.Trigger())

    new() = new SapiSink(0, None)

    /// Set when a requested voice was not applied — the host
    /// speaks it once at startup.
    member _.VoiceNote : string option = voiceNote

    interface ISpeechSink with
        member _.SpeakAsync(text: string) : unit =
            synth.SpeakAsync(text) |> ignore
        member _.CancelAll() : unit =
            synth.SpeakAsyncCancelAll()
        member _.SetRate(rate: int) : unit =
            synth.Rate <- max -10 (min 10 rate)
        member _.UtteranceCompleted : IEvent<unit> =
            completed.Publish

    interface System.IDisposable with
        member _.Dispose() : unit =
            synth.Dispose()
