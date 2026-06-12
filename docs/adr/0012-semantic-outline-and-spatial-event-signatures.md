# ADR 0012 — Semantic outline, positional orientation, and spatial event signatures

- **Status**: Proposed — authored 2026-06-11 in the same
  maintainer-authorized autonomous session that shipped Phase 0
  (ADR 0011). Implemented behind the same additive discipline;
  the Phase 0 local dogfood (`P0-ENGINE-1`, now covering these
  behaviours too) is the ratification gate.
- **Date**: 2026-06-11
- **Deciders**: Claude (autonomous, per maintainer grant);
  maintainer ratifies retroactively.
- **Companion docs**: [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md)
  (§5 chunk tree, §6 navigation, §7 multimodal I/O —
  especially §7.1 device target 2 "spatial audio" and §7.3 the
  foreground/ambient attention contract),
  [ADR 0008](0008-maximal-semantic-surfacing.md),
  [ADR 0011](0011-phase0-interaction-engine-bootstrap.md).

## Context

Phase 0 ships a flat-per-response chunk forest: a response's
blocks (headings, paragraphs, lists, code) are siblings under
the request chunk, with hierarchy only inside lists. For an
audio-only, keyboard-only workflow this under-uses the verbs:
`next`/`previous` step through *every* block, and `descend`
has nothing to enter except lists. The structure the model
emits — heading-scoped sections — is present in the source and
is currently *flattened away* at the section level, which is
precisely the ADR 0008 anti-pattern.

Separately, the spec names two orientation mechanisms the
Phase 0 build does not yet provide: positional context (a
screen-reader user expects "3 of 5" the way a sighted user
expects a scrollbar) and the §10 orientation surface ("where
am I"), plus §7.1's second output device: **spatial audio**
carrying event identity and the foreground/ambient distinction
by position. The universal event bus makes this a pure mapping
plus one more consumer — exactly what the bus exists for.

## Decisions

### S1 — Heading-scoped semantic outline at ingest

`SemanticOutline.nest` (pure, `Engine.Core`) transforms the
chunker's flat block list into a section tree before append: a
heading absorbs every following block — including
deeper headings — as children, until the next heading of equal
or shallower level. The chunk tree therefore carries the
document's real outline: top level = sections, `descend` =
enter a section, `next` at the top = section-to-section
movement. Content before the first heading stays top-level
(honest: the source put it there). Heading narration announces
the section size ("Heading level 2: Setup. 4 items inside.").
This is recovery of structure the source unambiguously
provides (ADR 0008) — no inference, no heuristics.

### S2 — Positional orientation: "N of M" + the where-verb

- Every navigation move is narrated with its sibling position
  appended ("…, 3 of 5") — `ChunkNarration.describeAt`. The
  re-narrate verb (`r`) stays position-free (pure content).
- A new **where-verb** (`w` in the host) speaks the breadcrumb:
  the focused chunk's kind + position, then each ancestor as a
  short label ("inside section Setup, inside your request: …,
  depth 3") — `ChunkNarration.locate`. This is the §10
  orientation surface at chunk scale, computed from the tree
  (never drifts).

### S3 — Spatial signatures for every universal-event-bus event

`SpatialCue` (pure, `Engine.Core`) maps every `EngineEvent` to
a deterministic stereo-stage signature — `Pan` (−1 hard left …
+1 hard right), `Pitch` (Hz), `DurationMs`, `Gain` — and maps
navigation outcomes to direction-coded cues. The **stage
layout** encodes the §7.3 attention contract by position:

| Event family | Pan (stage position) | Pitch policy |
|---|---|---|
| Narrative / foreground (request capture, completion) | 0.0 (center) | capture 660 Hz; completion 880 Hz; failure 220 Hz, longer |
| Content trickle (`ChunkSealed`) | +0.35 (near right) | **unique pitch per `ChunkKind`** (headings 988, paragraphs 523, list 587, item 659, code 698, quote 494, tool-use 784, tool-result 740 / error 311, …), 35 ms, low gain |
| Turn progress (`ResponseProgress`) | +0.6 (right) | 440 Hz + 20·count (capped) — progression audible as a rising series |
| Lifecycle (`SessionStarted`) | −0.8 (far left) | 392 Hz |
| Diagnostics (`EngineNote`) | −0.5 (left) | 330 Hz |
| Navigation moved | ±0.3 toward the movement direction; descend lower-pitched, ascend higher | 392–587 Hz, 30 ms |
| Navigation edge | same side as the attempt | 196 Hz dull, 90 ms |

Uniqueness is a **tested property**: every event family has a
distinct (pan band, pitch) signature, and every `ChunkKind`
seal tick is pitch-distinct — the ear can identify the event
class without speech. Speech remains the primary channel;
cues are parallel, short, and low-gain (ambient by
construction — they never gate or delay an utterance).

### S4 — Renderer: `Engine.Audio` stereo panner (HRTF later)

A new `Engine.Audio` assembly (`net9.0-windows`, NAudio — the
already-pinned dependency, reusing the battle-tested
per-play-`WasapiOut` pattern from `Terminal.Audio`, including
the `AUDCLNT_E_ALREADY_INITIALIZED` lesson) renders a cue as a
mono sine envelope through NAudio's `PanningSampleProvider`
(constant-power stereo pan). Stereo panning is the honest v1
of "unique spatial audio per event": it carries the full stage
layout on any headphones/speakers with zero per-user setup.
True 3-D HRTF rendering is a renderer swap behind the same
`SpatialCue` data (the cue model deliberately carries stage
position, not implementation), tracked as the spec's §16
open decision 8 evolves.

### S5 — Attention-layer corrections (audit findings)

- A **user-initiated read supersedes stale queued foreground**:
  `speakNow` clears pending foreground before enqueueing (a
  queued "Response complete…" must not delay the chunk the
  user just asked for). Ambient is preserved.
- The **stop verb empties the room**: `s` cancels current
  speech *and* clears the whole queue (previously queued
  utterances would resume speaking after the cancel —
  surprising for a stop).
- **Long bodies are capped with an honest marker**: navigation
  reads use `describeAt` capped (600 chars) with an explicit
  "Truncated; N more characters — press r for all." suffix;
  `r` reads the full body. (The WPF app's 800-char
  `OutputAnnounceCapChars` precedent, adapted.)

## Consequences

- `descend`/`ascend` become the primary movement for long
  responses; `j`/`k` at the top level is section-to-section.
- The bus gains its second real consumer (the spatial-cue
  sink), validating the §0.1 "many consumers, none privileged"
  shape with working code.
- The dogfood walk in [`docs/ENGINE-PHASE0.md`](../ENGINE-PHASE0.md)
  gains: outline navigation, `w`, the stage layout, and a
  cue-identification check.
- Risks accepted: SAPI speech + WASAPI cues are two unmixed
  audio paths (no ducking in v1); cue rates are low
  (per-message seals, not per-byte) so overlap is rare.

## Status notes

- 2026-06-11: authored; implementation lands in the same
  session (audit fixes → outline → orientation → cues →
  renderer + host wiring → docs).
- 2026-06-12: **Implemented & CI-green** — #457 S5 audit
  fixes · #458 S1 outline · #459 S2 orientation · #460 S3
  cue model · #461 S4 renderer + host wiring + docs. The
  `P0-ENGINE-1` dogfood walk (steps 8–9) is the ratification
  gate.
