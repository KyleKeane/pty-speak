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

    /// Default palette. v1 (8d.1) shipped only the bell-ping
    /// entry; v2 (8d.2) adds error-tone + warning-tone for
    /// SGR-coloured streaming output detected by the
    /// StreamProfile. Tunable in Phase 2 via TOML.
    ///
    /// Frequency / duration choices follow the spec §9.3 palette
    /// intent (alarm low / confirm high / warn middle):
    /// - bell-ping (800Hz × 100ms) — high pitch + short duration;
    ///   reads as a "ping" or "ding". Triggered by BEL (0x07).
    /// - error-tone (400Hz × 150ms) — low pitch + longer
    ///   duration; reads as "alarm" or "wrong". Triggered by
    ///   red-dominant streaming output (StreamProfile detection).
    /// - warning-tone (600Hz × 120ms) — middle pitch + middle
    ///   duration; sits between bell-ping and error-tone.
    ///   Triggered by yellow-dominant streaming output.
    /// All three are shorter than the StreamProfile's 200ms
    /// debounce window so consecutive earcons don't overlap.
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
