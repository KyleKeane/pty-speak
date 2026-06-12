# Engine textbook, chapter 6 — spatial audio: the stage

> Files: `src/Engine.Core/SpatialCue.fs`;
> `src/Engine.Audio/SpatialPlayer.fs`. Tests:
> `SpatialCueTests`. Decision: ADR 0012 S3/S4; spec §7.1
> (device 2), §7.3.

## Position is meaning

Every universal-event-bus event maps to a deterministic
stereo-stage signature — `Cue { Pan; Pitch; DurationMs; Gain }`
— and the **stage layout encodes the attention contract by
position**:

| Where | What lives there |
|---|---|
| center (0.0) | the narrative thread: request landing (660 Hz), completion (880), failure (220, long) |
| near right (+0.35) | the content trickle: one 35 ms tick per sealed chunk, **pitch = the chunk's kind** |
| right (+0.6) | turn progress: 440 + 20·count Hz, capped — a rising series |
| far left (−0.8) / left (−0.5) | lifecycle / diagnostic notes |

Navigation adds direction coding: `forNav` pans a crisp tick
toward the movement (next right, previous left), pitches
descend low and ascend high, and renders a refused move (an
edge) as a dull 196 Hz tone **on the side of the attempt** —
you hear where you bumped before the voice says why.

## Uniqueness is a tested property, not a convention

`SpatialCueTests` asserts pairwise-distinct (pan, pitch)
signatures across every event family and pairwise-distinct
pitches across all chunk kinds (`pitchForKind` — headings B5,
paragraphs C5, code F5, tool errors low D#4…). Add a kind or
an event without a distinct sound and CI fails. Also pinned:
the progress series rises then caps, failure rings lower and
longer than success, and every gain ≤ 0.5 (cues sit *under*
speech — ambient by construction, never gating an utterance).

## The cue model deliberately carries stage position, not implementation

`Pan` is a place, not a renderer instruction. The v1 renderer
is constant-power stereo panning — honest spatialization that
works on any headphones with zero per-user setup. A future
HRTF renderer (true 3-D, per-user profiles) consumes the same
`Cue` values; choosing it is a host wiring change, which is
exactly the §0.1 "consumers are never privileged" claim made
mechanical.

## SpatialPlayer — the renderer

`Engine.Audio.SpatialPlayer.play` reuses the WPF app's
battle-tested earcon architecture verbatim, including its
scars: **per-play `WasapiOut`** (NAudio's `AudioClient`
throws `AUDCLNT_E_ALREADY_INITIALIZED` on a second `Init` —
the original earcon bug shipped as a lazy singleton and only
ever played one sound), a cached `MMDeviceEnumerator` (thin
COM wrapper, safe to share, not free to construct),
dispose-on-`PlaybackStopped`, and **total error swallowing**
— no audio failure may ever cross into the engine; a missing
device degrades to silence.

The synthesis chain per cue: mono `SignalGenerator` sine at
the cue's pitch/gain → `OffsetSampleProvider` bounding the
duration → 5 ms fade-in (kills the onset click) →
`PanningSampleProvider` (mono→stereo, constant-power, the
cue's pan) → 16-bit wave → `WasapiOut` shared mode.

## Listening design notes

Frequencies sit in the 200–1000 Hz earcon band — distinct as
*pitch classes* (musical intervals, not adjacent Hz) because
absolute pitch is rare but interval/contour discrimination is
near-universal. Durations: 30–45 ms for high-rate events
(ticks), 70–140 ms for punctuation (request/completion), 260
ms for failure — rate × duration keeps the stage's duty cycle
low enough that overlapping a sustained utterance feels like
texture, not collision.
