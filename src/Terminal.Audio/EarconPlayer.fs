namespace Terminal.Audio

open System
open NAudio.Wave
open NAudio.Wave.SampleProviders
open NAudio.CoreAudioApi
open Microsoft.Extensions.Logging
open Terminal.Core

/// Stage 8d.1 — WASAPI playback glue for earcons.
///
/// Holds a singleton `WasapiOut` instance, initialised lazily on
/// first call to `play`. NAudio's `WasapiOut` runs its own audio
/// thread internally — `Play()` returns immediately once the
/// engine is primed and the audio thread feeds samples on its
/// own schedule. So `play` is non-blocking from the dispatcher's
/// perspective, satisfying the channel "do not block the
/// dispatcher" contract (spec B.5.3).
///
/// **Lazy init.** The WasapiOut + MMDeviceEnumerator are
/// constructed on first use rather than at app startup. This
/// avoids paying the audio-subsystem initialisation cost for
/// users who never trigger an earcon (e.g., shell sessions
/// that never emit BEL). The lazy init is gated by a `lock` so
/// concurrent first-plays don't double-construct.
///
/// **Single-stream model.** WasapiOut plays ONE sample provider
/// at a time. If a second `play` arrives while the previous is
/// still rendering, the first is stopped and the second begins.
/// In practice earcons are short (100ms) and rare (BEL triggers
/// only when a shell emits 0x07); collisions are unlikely. A
/// future PR can add a mixer (`MixingSampleProvider`) if
/// overlapping earcons become a real use case.
///
/// **Error swallowing.** Audio failures must NEVER crash the
/// app or block the dispatcher. Every `play` call is wrapped in
/// `try/with`; exceptions are logged at Information level and
/// silently dropped. Common failure modes: no audio device, WASAPI
/// init failure (e.g., headless CI), driver permission errors.
/// All of these become "no sound" for the user — the rest of the
/// app continues running.
module EarconPlayer =

    /// Helper: fetch the current logger. Called per-log-site
    /// rather than cached at module-init because
    /// `Terminal.Core.Logger` returns a NullLogger sentinel
    /// before `Logger.configure` runs (composition root,
    /// `Program.fs` line ~788) and the real factory-backed
    /// logger after. Module-init for Terminal.Audio happens
    /// when `compose` touches the EarconPlayer (after
    /// configure), but we use the per-call lookup pattern that
    /// the rest of the codebase uses (see e.g.
    /// `Coalescer.processRowsChanged`).
    let private getLogger () : ILogger =
        Logger.get "Terminal.Audio.EarconPlayer"

    let private initLock : obj = obj ()
    let mutable private wasapiOut : WasapiOut option = None
    let mutable private deviceEnumerator : MMDeviceEnumerator option = None

    /// Initialise the WASAPI output device on first use. Caller
    /// MUST hold `initLock`. Returns the cached instance on
    /// subsequent calls. Returns `None` if the audio subsystem
    /// is unavailable (no device, permission denied, headless
    /// runner, etc.).
    let private ensureWasapiOut () : WasapiOut option =
        match wasapiOut with
        | Some _ as cached -> cached
        | None ->
            try
                let enumerator = new MMDeviceEnumerator()
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
                deviceEnumerator <- Some enumerator
                wasapiOut <- Some wo
                (getLogger ()).LogInformation(
                    "WasapiOut initialised. Device={Device}",
                    device.FriendlyName)
                Some wo
            with ex ->
                (getLogger ()).LogInformation(
                    ex,
                    "WasapiOut initialisation failed; earcons will be silent. Reason: {Reason}",
                    ex.Message)
                None

    /// Play the earcon identified by `earconId` using the
    /// supplied palette. Non-blocking: returns immediately
    /// after starting the playback. Errors are logged and
    /// silently swallowed — earcon failures don't crash the
    /// dispatcher.
    let play
            (palette: Map<EarconPalette.EarconId, EarconWaveform.Parameters>)
            (earconId: EarconPalette.EarconId)
            : unit
            =
        match Map.tryFind earconId palette with
        | None ->
            (getLogger ()).LogInformation(
                "No earcon registered for id; skipping. EarconId={EarconId}",
                earconId)
        | Some parameters ->
            try
                lock initLock (fun () ->
                    match ensureWasapiOut () with
                    | None -> ()
                    | Some wo ->
                        // Stop the in-flight earcon (if any)
                        // before initialising the next. WasapiOut
                        // doesn't queue; calling Init twice
                        // without Stop produces undefined
                        // behaviour.
                        if wo.PlaybackState = PlaybackState.Playing then
                            wo.Stop()
                        let waveform =
                            EarconWaveform.synthSineEnvelope parameters
                        // WasapiOut.Init takes IWaveProvider;
                        // wrap the ISampleProvider chain in a
                        // 16-bit PCM converter (NAudio's
                        // standard adapter). 16-bit is the
                        // universally-supported lowest-CPU
                        // option for short tones; spatial
                        // future-stages may upgrade to 32-bit
                        // float for better dynamic range.
                        let waveProvider = SampleToWaveProvider16(waveform)
                        wo.Init(waveProvider :> IWaveProvider)
                        wo.Play()
                        (getLogger ()).LogDebug(
                            "Earcon play started. EarconId={EarconId} FreqHz={FreqHz} DurationMs={DurationMs}",
                            earconId,
                            parameters.FrequencyHz,
                            parameters.DurationMs))
            with ex ->
                (getLogger ()).LogInformation(
                    ex,
                    "Earcon play failed; suppressing. EarconId={EarconId} Reason={Reason}",
                    earconId,
                    ex.Message)
