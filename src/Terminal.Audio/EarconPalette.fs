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

    /// Default palette. v1 ships a single bell-ping entry. The
    /// 800Hz frequency was chosen as a clearly-audible mid-tone
    /// that doesn't conflict with NVDA's speech band; the 100ms
    /// duration is short enough to feel like a "bell" rather
    /// than a "tone". Tunable in Phase 2 via TOML.
    let defaultPalette : Map<EarconId, EarconWaveform.Parameters> =
        Map.ofList
            [ "bell-ping",
              { FrequencyHz = 800.0
                DurationMs = 100
                AttackMs = 10 } ]
