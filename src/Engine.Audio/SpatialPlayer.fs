namespace Engine.Audio

open NAudio.Wave
open NAudio.Wave.SampleProviders
open NAudio.CoreAudioApi
open Engine.Core.SpatialCue

/// ADR 0012 S4 — renders a `SpatialCue.Cue` as a short
/// stereo-panned sine tone. Reuses the battle-tested
/// `Terminal.Audio.EarconPlayer` architecture verbatim:
///
///   * **Per-play `WasapiOut`** — NAudio's `AudioClient`
///     cannot be `Init`-ed twice on one instance
///     (`AUDCLNT_E_ALREADY_INITIALIZED`, the original earcon
///     bug); each play constructs, plays, and disposes on
///     `PlaybackStopped`.
///   * **Cached `MMDeviceEnumerator`** — thin COM wrapper,
///     safe to share, not free to construct.
///   * **Error swallowing** — audio failure must never crash
///     or block the engine: every play is try/with; failures
///     become "no sound".
///
/// The spatial part: the mono sine envelope feeds NAudio's
/// `PanningSampleProvider` (mono → stereo, constant-power
/// pan), so the cue's stage position renders on any stereo
/// output with zero per-user setup. Gains are cue-supplied
/// (well under speech level).
module SpatialPlayer =

    let private sampleRate = 44_100

    let private initLock : obj = obj ()
    let mutable private deviceEnumerator : MMDeviceEnumerator option =
        None

    /// Caller MUST hold `initLock`.
    let private ensureEnumerator () : MMDeviceEnumerator option =
        match deviceEnumerator with
        | Some _ as cached -> cached
        | None ->
            try
                let enumerator = new MMDeviceEnumerator()
                deviceEnumerator <- Some enumerator
                Some enumerator
            with _ ->
                None

    /// Build the mono tone → stereo-panned provider chain for
    /// one cue. Fresh per play (NAudio sample providers are
    /// stateful; never shared across concurrent plays).
    let private buildChain (cue: Cue) : ISampleProvider =
        let generator =
            SignalGenerator(sampleRate, 1,
                Type = SignalGeneratorType.Sin,
                Frequency = cue.Pitch,
                Gain = cue.Gain)
        let bounded =
            OffsetSampleProvider(generator,
                TakeSamples = sampleRate * cue.DurationMs / 1000)
        let fader = FadeInOutSampleProvider(bounded, true)
        // 5ms attack avoids the speaker click on a hard onset;
        // cue durations are 30ms+, so the plateau survives.
        fader.BeginFadeIn(5.0)
        let panner = PanningSampleProvider(fader)
        panner.Pan <- float32 cue.Pan
        panner :> ISampleProvider

    /// Play one cue. Non-blocking; failures are silent by
    /// design (the engine never depends on a cue landing).
    let play (cue: Cue) : unit =
        try
            lock initLock (fun () ->
                match ensureEnumerator () with
                | None -> ()
                | Some enumerator ->
                    let device =
                        enumerator.GetDefaultAudioEndpoint(
                            DataFlow.Render,
                            Role.Console)
                    let wo =
                        new WasapiOut(
                            device,
                            AudioClientShareMode.Shared,
                            true,
                            100)
                    wo.PlaybackStopped.Add(fun _ ->
                        try wo.Dispose() with _ -> ())
                    let waveProvider =
                        SampleToWaveProvider16(buildChain cue)
                    try
                        wo.Init(waveProvider :> IWaveProvider)
                        wo.Play()
                    with _ ->
                        try wo.Dispose() with _ -> ()
                        ())
        with _ ->
            ()
