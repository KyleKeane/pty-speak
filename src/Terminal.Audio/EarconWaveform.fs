namespace Terminal.Audio

open NAudio.Wave
open NAudio.Wave.SampleProviders

/// Stage 8d.1 — pure-F# earcon waveform synthesis. Produces an
/// `ISampleProvider` representing a sine tone of the requested
/// frequency + duration with a linear attack envelope to avoid
/// the speaker-clicking artefact at start.
///
/// **8d.1 envelope simplification.** NAudio's
/// `FadeInOutSampleProvider.BeginFadeOut` triggers immediately
/// on call; there's no ahead-of-time "fade out at sample N"
/// scheduling without a timer (which risks GC issues on the
/// short-lived provider chain). 8d.1 ships fade-in only; the
/// abrupt cut at the end of a 100ms tone produces a faint
/// click that's acceptable for v1. A follow-up PR can swap in
/// a custom `ISampleProvider` that applies the release ramp
/// per-sample if the click proves audible in practice.
///
/// **Architecture.** Synthesis is parametric: each call to
/// `synthSineEnvelope` returns a fresh
/// `(SignalGenerator → OffsetSampleProvider →
/// FadeInOutSampleProvider)` chain that NAudio's `WasapiOut`
/// consumes. The provider chain is short-lived per playback;
/// nothing is cached across calls (NAudio's sample providers
/// are stateful and not safe to share across concurrent plays).
module EarconWaveform =

    /// Parameters for sine-tone synthesis. `FrequencyHz` is the
    /// fundamental tone (typical earcon range 200-2000Hz);
    /// `DurationMs` bounds the total envelope length;
    /// `AttackMs` is the fade-in portion (subtracted from the
    /// steady-state plateau).
    type Parameters =
        { FrequencyHz: float
          DurationMs: int
          AttackMs: int }

    /// Sample format the synthesis chain produces. WASAPI
    /// shared-mode mixing accepts any common format and
    /// resamples internally; 44.1kHz mono float32 is a good
    /// default — minimises CPU, matches NAudio's default
    /// `SignalGenerator` output.
    let internal sampleRate = 44_100
    let internal channels = 1

    /// Build a fresh `ISampleProvider` chain that plays one
    /// envelope of a sine tone matching the supplied parameters.
    /// Caller passes the result to `WasapiOut.Init` + `Play`.
    let synthSineEnvelope (parameters: Parameters) : ISampleProvider =
        let generator =
            SignalGenerator(sampleRate, channels,
                Type = SignalGeneratorType.Sin,
                Frequency = parameters.FrequencyHz,
                Gain = 0.5)
        let bounded =
            OffsetSampleProvider(generator,
                TakeSamples =
                    sampleRate * channels * parameters.DurationMs / 1000)
        let fader = FadeInOutSampleProvider(bounded, true)
        fader.BeginFadeIn(float parameters.AttackMs)
        fader :> ISampleProvider
