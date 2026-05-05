namespace Terminal.Audio

/// Stage 8d.1 — earcon-id vocabulary + default palette mapping
/// each id to an `EarconWaveform.Parameters` record. The palette
/// is a `Map<EarconId, EarconWaveform.Parameters>`; the
/// `EarconPlayer` looks up by id, builds a fresh sample-provider
/// chain via `EarconWaveform.synthSineEnvelope`, and feeds it to
/// the WASAPI device.
///
/// **v1 palette (8d.1).** Only `"bell-ping"` is defined — an
/// 800Hz × 100ms tone with a 10ms attack envelope. The Earcon
/// profile in `Terminal.Core/EarconProfile.fs` maps
/// `SemanticCategory.BellRang → "bell-ping"` so when a shell
/// emits BEL (0x07), the user hears the ping.
///
/// **8d.2 palette additions.** Color detection lands in 8d.2,
/// which extends the palette with `"error-tone"` (probably
/// 400Hz × 150ms — lower pitch + longer duration to feel "big")
/// and `"warning-tone"` (probably 600Hz × 120ms — middle ground).
///
/// **Phase 2.** The palette becomes user-customisable via TOML
/// (`[earcons.bell-ping] frequencyHz = 800` etc.). The 9c TOML
/// loader will overlay user values on top of `defaultPalette`.
module EarconPalette =

    /// Stable string identifier for an earcon. The Earcon
    /// profile's `Apply` produces `RenderEarcon earconId`
    /// instructions; the EarconChannel resolves the id by
    /// looking up the palette entry. String-keyed for TOML
    /// compatibility (Phase 2).
    type EarconId = string

    /// Default palette. Three entries — the audio side of the
    /// 8d sub-stages.
    /// - `bell-ping` (800Hz × 100ms): triggered by BEL (0x07).
    /// - `error-tone` (400Hz × 150ms): triggered by future
    ///   colour-detection (red rows). Currently unused by any
    ///   producer (the 8d.2 colour-detection PR was reverted
    ///   while the maintainer triaged a NVDA-silence regression).
    ///   Kept in the palette so the Ctrl+Shift+D diagnostic can
    ///   exercise the full earcon path without depending on a
    ///   coloured-shell-output trigger.
    /// - `warning-tone` (600Hz × 120ms): triggered by future
    ///   colour-detection (yellow rows). Same status as
    ///   error-tone — present in the palette for diagnostic
    ///   coverage, no producer wired yet.
    /// All earcons stay shorter than the StreamProfile's 200ms
    /// debounce window so consecutive earcons don't overlap.
    /// Tunable in Phase 2 via TOML.
    let defaultPalette : Map<EarconId, EarconWaveform.Parameters> =
        Map.ofList
            [ "bell-ping",
              { FrequencyHz = 800.0
                DurationMs = 100
                AttackMs = 10 }
              "error-tone",
              { FrequencyHz = 400.0
                DurationMs = 150
                AttackMs = 10 }
              "warning-tone",
              { FrequencyHz = 600.0
                DurationMs = 120
                AttackMs = 10 } ]
