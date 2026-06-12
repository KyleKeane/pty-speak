# Engine textbook, chapter 5 — attention and the voice

> Files: `src/Engine.Core/Attention.fs`, `SpeechSink.fs`;
> `src/Engine.Voice/SapiSink.fs`. Tests: `AttentionTests`.
> Decisions: ADR 0011 E6/E7, ADR 0012 S5, ADR 0014 C3; spec
> §7.3 (the attention contract), §1.1 (the quality bar),
> §4.6 (self-voicing).

## The attention contract, as data

Spec §7.3 demands two routing classes, always distinguished:
the **foreground** narrative thread (never interrupted by
state noise) and **ambient** peripheral awareness (never
derailing composition). `Attention.Queue` is that contract as
a data structure:

- Foreground is a strict FIFO. Nothing reorders it; nothing
  below it ever runs while it is non-empty.
- Ambient entries are keyed; a newer utterance with the same
  key **replaces** the queued older one (latest-wins
  coalescing — "4 chunks so far" makes "3 chunks so far"
  worthless), and ambient only surfaces when the foreground
  is empty.

Two corrections came from the launch audit (ADR 0012 S5),
both pinned by tests: a **user-initiated read supersedes
stale queued foreground** (`clearForeground` — a queued
"Response complete…" must not delay the chunk just asked
for; ambient survives), and **stop means stop** (`clear` —
nothing queued may resume after `s`).

## The routing policy

`Attention.route : EngineEvent → Utterance option` is the
single place deciding what is *worth speaking at all*:
requests confirm foreground (the §6.1 narrate-and-confirm
echo); completion is foreground and error-aware; progress and
notes are ambient on their keys; **sealed chunks are
deliberately silent** — §5.2 gives the user navigation instead
of a firehose, and the spatial tick (chapter 6) carries the
awareness. Changing what the engine says spontaneously means
changing this one function and its tests.

## ISpeechSink — the owned voice's seam

The platform-free contract: `SpeakAsync`, `CancelAll`,
`SetRate`, and `UtteranceCompleted` — the completion event is
the entire drain protocol (chapter 10): the host speaks at
most one utterance at a time and dequeues the next on
completion, which is what makes the queue's ordering
guarantees *real* at the audio device.

`SapiSink` implements it over
`System.Speech.Synthesis.SpeechSynthesizer`: in-box Windows
voices, no install step, decades-stable — the honest
bootstrap for the §1.1 bar (ADR 0011 E7 accepts SAPI's voice
quality and measures its *reliability*; a better TTS is a
sink swap, which is the point of the seam). `SpeakCompleted`
fires for finished **and cancelled** utterances, so the drain
always advances. Rate is clamped −10…+10; voice selection is
a case-insensitive substring match over installed voices,
with a no-match note surfaced rather than guessed (ADR 0014
C3).

## Why two audio paths and no mixer

Speech (SAPI) and cues (WASAPI, chapter 6) are separate
unmixed paths in v1. A shared mixer would buy ducking at the
cost of owning latency for the one channel where latency is
the product (§1.1). Cues are short (30–140 ms) and low-gain
(≤ 0.5, tested) precisely so coexistence without ducking
stays comfortable; the ADR records the tradeoff as accepted
and revisitable.
