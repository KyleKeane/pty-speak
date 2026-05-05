namespace Terminal.Audio

open System
open NAudio.Wave
open NAudio.Wave.SampleProviders
open NAudio.CoreAudioApi
open Microsoft.Extensions.Logging
open Terminal.Core

/// Stage 8d.1 — WASAPI playback glue for earcons.
/// Re-architected 2026-05-05 after the post-8d.1 release-build
/// logs surfaced `AUDCLNT_E_ALREADY_INITIALIZED` (0x88890002)
/// errors on the second + third calls to `WasapiOut.Init`.
///
/// **Per-play WasapiOut.** Each call to `play` constructs a
/// fresh `WasapiOut` instance, calls `Init`, calls `Play`, and
/// registers a `PlaybackStopped` handler that disposes the
/// instance once the bounded sample provider exhausts. NAudio's
/// underlying `AudioClient.Initialize` cannot be called twice
/// on the same `WasapiOut` — it throws
/// `AUDCLNT_E_ALREADY_INITIALIZED` — so the lazy-singleton
/// pattern we shipped initially in 8d.1 was wrong: only the
/// first earcon ever played; the second + third silently
/// failed (and the original 8d.1 release wasn't end-to-end-
/// validated for the multi-earcon path because we shipped
/// before the diagnostic that exercises it).
///
/// `MMDeviceEnumerator` is still cached because it's a thin
/// COM wrapper around device enumeration that's safe to share
/// across `WasapiOut` instances and not free to construct.
///
/// **Concurrency.** A single `lock initLock` brackets the
/// device-acquire + WasapiOut-create + Init + Play sequence
/// so two concurrent `play` calls don't race during
/// initialisation. The audio playback itself runs on NAudio's
/// internal audio thread; multiple in-flight `WasapiOut`
/// instances can coexist and play in parallel (each owns its
/// own `AudioClient`). For our use case (BEL + diagnostic +
/// future colour-detection), play rates are well below
/// once-per-150ms, so overlap is rare.
///
/// **Error swallowing.** Audio failures must NEVER crash the
/// app or block the dispatcher. Every `play` call is wrapped
/// in `try/with`; exceptions are logged at Information level
/// and silently dropped. Common failure modes: no audio
/// device, WASAPI init failure, driver permission errors,
/// the AUDCLNT_E_ALREADY_INITIALIZED bug above (now fixed).
/// All of these become "no sound" for the user — the rest of
/// the app continues running.
module EarconPlayer =

    /// Helper: fetch the current logger. Called per-log-site
    /// rather than cached at module-init because
    /// `Terminal.Core.Logger` returns a NullLogger sentinel
    /// before `Logger.configure` runs.
    let private getLogger () : ILogger =
        Logger.get "Terminal.Audio.EarconPlayer"

    let private initLock : obj = obj ()
    let mutable private deviceEnumerator : MMDeviceEnumerator option = None

    /// Lazily acquire the device enumerator on first play.
    /// Caller MUST hold `initLock`. Returns `None` if the audio
    /// subsystem is unavailable.
    let private ensureEnumerator () : MMDeviceEnumerator option =
        match deviceEnumerator with
        | Some _ as cached -> cached
        | None ->
            try
                let enumerator = new MMDeviceEnumerator()
                deviceEnumerator <- Some enumerator
                (getLogger ()).LogInformation(
                    "MMDeviceEnumerator initialised.")
                Some enumerator
            with ex ->
                (getLogger ()).LogInformation(
                    ex,
                    "MMDeviceEnumerator initialisation failed; earcons will be silent. Reason: {Reason}",
                    ex.Message)
                None

    /// Play the earcon identified by `earconId` using the
    /// supplied palette. Non-blocking: returns immediately
    /// after `WasapiOut.Play` (the audio thread feeds samples
    /// asynchronously). Errors are logged and silently
    /// swallowed.
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
                    match ensureEnumerator () with
                    | None -> ()
                    | Some enumerator ->
                        let device =
                            enumerator.GetDefaultAudioEndpoint(
                                DataFlow.Render,
                                Role.Console)
                        // Fresh WasapiOut per play. NAudio's
                        // AudioClient.Initialize throws
                        // AUDCLNT_E_ALREADY_INITIALIZED if Init
                        // is called twice on the same WasapiOut.
                        // Each play is short (≤150ms tone); the
                        // construct/dispose overhead is acceptable.
                        let wo =
                            new WasapiOut(
                                device,
                                AudioClientShareMode.Shared,
                                true,
                                100)
                        // Dispose-on-stop chain: when the bounded
                        // sample provider exhausts, NAudio fires
                        // PlaybackStopped on its audio thread; we
                        // dispose the WasapiOut to release the
                        // AudioClient handle. Wrapped in try/with
                        // because Dispose can race with the GC
                        // finalizer and multiple Dispose calls
                        // can throw; we treat the cleanup as
                        // best-effort.
                        wo.PlaybackStopped.Add(fun _ ->
                            try wo.Dispose() with _ -> ())
                        let waveform =
                            EarconWaveform.synthSineEnvelope parameters
                        let waveProvider = SampleToWaveProvider16(waveform)
                        try
                            wo.Init(waveProvider :> IWaveProvider)
                            wo.Play()
                            (getLogger ()).LogDebug(
                                "Earcon play started. EarconId={EarconId} FreqHz={FreqHz} DurationMs={DurationMs}",
                                earconId,
                                parameters.FrequencyHz,
                                parameters.DurationMs)
                        with ex ->
                            // Init or Play failed before
                            // PlaybackStopped could fire to clean
                            // up; dispose explicitly here, then
                            // rethrow to the outer handler.
                            try wo.Dispose() with _ -> ()
                            reraise ())
            with ex ->
                (getLogger ()).LogInformation(
                    ex,
                    "Earcon play failed; suppressing. EarconId={EarconId} Reason={Reason}",
                    earconId,
                    ex.Message)
