# ADR 0011 — Phase 0 interaction-engine bootstrap (engine assemblies, chunk tree, Claude CLI participant, self-voicing channel)

- **Status**: Proposed — authored 2026-06-11 during an
  explicitly-authorized autonomous session (the maintainer
  instructed Claude to work unattended toward the
  [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md) goal and to
  decide open implementation questions on best recommendation).
  Every decision below is therefore **implemented but awaiting
  maintainer ratification**; each is reversible behind a seam,
  and none touches the shipped WPF app. The Phase 0 local
  dogfood (spec §13 acceptance, on the self-voicing channel per
  §14.1) is the ratification gate.
- **Date**: 2026-06-11
- **Deciders**: Claude (autonomous, per maintainer grant);
  maintainer ratifies retroactively.
- **Companion docs**:
  [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md) (the target;
  §13 Phase 0 is what this ADR builds),
  [ADR 0006](0006-three-layer-refoundation.md) (the
  transport / core / channel seam the engine reuses),
  [ADR 0008](0008-maximal-semantic-surfacing.md) (the spine
  principle), [ADR 0010](0010-interaction-strategy-structured-runner-vs-passthrough.md)
  (superseded in framing by the spec; this ADR is the spec's
  first implementation step).

## Context

The re-launch specification (2026-05-19, maintainer-ratified
core canon §0.1/§0.2) commits to **Phase 0**: the smallest
honest version of the primary loop — compose a request, send it
to a **local Claude Code CLI over its structured stream**,
ingest the response as a sealed **chunk tree**, navigate it with
the v1 verbs, and hear everything through a **self-voicing audio
channel** the system owns end-to-end. No WPF, no UIA, no NVDA on
this path (day-zero canon).

The spec deliberately left implementation-shaping questions open
(§16) or to be decided at build time. This ADR records the
choices made so they are visible, reviewable, and individually
reversible. The repo conventions (one concern per PR, CI-gated,
walking skeleton) carry over unchanged.

## Decisions

### E1 — Assembly layout: new `Engine.*` projects, additive

Four new projects; nothing existing is modified or deleted:

| Project | TFM | Role |
|---|---|---|
| `src/Engine.Core` | `net9.0` | The interaction engine's pure core: chunk-tree model, agent-event vocabulary, Claude stream-json parser, markdown chunker, ingest, navigation, attention routing, the engine event bus. **No platform types** — enforced structurally by the plain (non-Windows) TFM plus the portability lint. |
| `src/Engine.Participants` | `net9.0` | The participant seam's first instance: the Claude Code CLI process runner (spawn, line pump, lifecycle). Transport-layer only; translation logic stays pure in `Engine.Core`. |
| `src/Engine.Voice` | `net9.0-windows` | The self-voicing channel: `ISpeechSink` realized over Windows SAPI (`System.Speech`). A universal-event-bus consumer, never a foundation (§0.1). |
| `src/Engine.Host` | `net9.0-windows` | The Phase 0 console host executable wiring participant → ingest → tree → navigation → voice. The dogfoodable artifact. |

Named `Engine.*`, not `Terminal.*`: the spec's core canon says
the thing being built is the *interaction engine* and explicitly
not a terminal. `Terminal.*` assemblies continue to exist and
build (the §4.2 freeze list is quarantined, not deleted).

The dependency direction is `Engine.Host → {Engine.Participants,
Engine.Voice} → Engine.Core`, mirroring ADR 0006's
transport / core / channel discipline. `Engine.Core` references
only `FSharp.Core`, `Microsoft.Extensions.Logging.Abstractions`,
and `Markdig` (all already in the central package set) — no
`Terminal.*` project references, so the engine core cannot
silently inherit terminal-scraping types.

### E2 — Chunk grain: Markdig block-level decomposition

Spec §16 open decision 1 (chunk grain), default chosen:
**model-marked block boundaries** — the Markdown block elements
the agent already emits (heading, paragraph, list, list item,
fenced code block, block quote), decomposed with Markdig (a
dependency the repo already carries). Rationale: ADR 0008 says
recover the structure the source unambiguously provides — the
agent's markdown *is* model-marked structure; block-level is
the Wolfram-cell-like grain the spec's reference experience
names. Finer grains (sentence-level) and coarser
(heading-section-level) remain a navigator-layer choice later;
the stored grain is the source's own block structure.

### E3 — Identity: GUID chunk ids + capture sequence + authored index

Spec §16 open decision 7, v1 scheme: every chunk gets an opaque
`ChunkId` (GUID, "N" format) at ingest; every chunk carries a
per-session monotonic `CaptureSeq` (the immutable temporal
position) and a separate `AuthoredIndex` (the §5.1 authored
order, present in the model from the first commit even though
v1 only appends). Ids are never positional, so reorder /
branch / re-issue cannot invalidate an address. Cross-document
addressing is deferred (it needs the structured-memory-repo
design, not Phase 0).

### E4 — Participant transport: per-turn `claude -p` with `--resume`

The Claude Code CLI is invoked **per turn** as
`claude -p <prompt> --output-format stream-json --verbose`,
with `--resume <sessionId>` carrying conversation continuity
(the session id is taken from the stream's `system/init`
event). A long-lived `--input-format stream-json` bidirectional
process is a later optimization, not Phase 0 — per-turn
invocation is simpler to supervise, loses nothing semantically,
and the CLI owns its own session persistence.

The parser is **tolerant by construction**: every line is
decoded into a typed `AgentEvent`; anything outside the known
vocabulary becomes a typed `Unknown` event carrying its raw
type tag — surfaced honestly, never silently dropped and never
relayed as ambiguous text (ADR 0008). The spec's §17 working
assumption ("the CLI exposes a usable structured interface")
is treated as **format-verified at the maintainer's machine
during the Phase 0 dogfood**; the parser's fixture corpus is
the contract in the meantime, and a format drift surfaces as
`Unknown` events plus a diagnostic log line, not a crash.

### E5 — The streaming rule realized: seal at message boundary

Spec §5.2: chunks are navigable only once sealed. Phase 0 seals
at the **assistant-message boundary** — when the CLI emits a
complete assistant message, its content blocks are decomposed
(E2) and sealed as a batch; while a turn is in flight the engine
emits ambient `ResponseProgress` events (chunk count so far)
and a `ResponseCompleted` event at the result line. Partial-
message streaming (`--include-partial-messages`) is deliberately
not consumed in Phase 0: it reintroduces the moving-target
problem the streaming rule exists to prevent, for no Phase 0
benefit.

### E6 — Attention contract enforced output-side in `AttentionRouter`

Spec §0.2 names two enforcement points for foreground/ambient;
Phase 0 builds the **output-side** one as a pure `Engine.Core`
fold: foreground utterances (confirmation echoes, navigation
narration, sealed-chunk reads the user asked for) form a single
ordered non-preemptible queue; ambient events (progress,
lifecycle) are **coalesced** (latest-wins per ambient key) and
only surface between foreground utterances. The input-side
scheduler is deferred until there is more than one input source
(Phase 0 input is a single console stream, already serialized).

### E7 — Self-voicing sink: Windows SAPI behind `ISpeechSink`

Phase 0's only output channel (spec §13). The seam is
`ISpeechSink` (speak / interrupt / sink-level state), defined
next to the router; `Engine.Voice` implements it over
`System.Speech.Synthesis.SpeechSynthesizer` (SAPI — in-box
voices, no install step, decades-stable). SAPI's voice quality
and latency are accepted for Phase 0 as the *bootstrap* sink;
the §1.1 "fast, reliable narration" bar is ultimately met by
swapping better TTS behind the same seam (the sink is a bus
consumer, so this is a configuration change by construction).
The dogfood explicitly measures: time-to-first-phoneme on
confirmation, interrupt latency, and drop-free sustained reads.

### E8 — Phase 0 input: keyboard-first console host

Spec §6.1 allows "speaking (or typing)" for composition. The
Phase 0 host is a console executable reading line-oriented
composition input plus single-key navigation chords. Speech
input arrives via OS-level dictation into the same console line
(no engine work needed), with a first-class speech-input
pipeline as a later event-handler instance (§0.2). This keeps
Phase 0's risk concentrated on the loop that matters (structured
ingest + navigation + self-voicing), not on audio capture.

### E9 — Nothing discarded

The WPF app, the PTY path, and every shipped `Terminal.*`
assembly keep building and shipping exactly as today. The
engine is additive. The §4.2 freeze stands: no further
investment in terminal-scraping heuristics. The existing
`CellEventBus`/`OutputDispatcher` are untouched; the engine
event bus is the *engine's* instance of the same pattern
(instance-scoped rather than global, so tests and multiple
engines compose).

### E10 — Validation: CI tests now, maintainer dogfood as the gate

Every pure module lands with xUnit coverage in `Tests.Unit`
(which already targets Windows and references multiple
projects). The portability lint gains `Engine.Core` enforcement
(no WPF/Win32 opens, no P/Invoke, no `Terminal.Shell` and no
`Terminal.Core` dependency). The Phase 0 **acceptance** is the
spec §13 criterion, validated by the maintainer locally on the
self-voicing channel (§14.1) — a matrix row lands with the
closure PR. CI cannot validate narration; it validates
everything else.

## Consequences

- The repo gains a second, parallel product surface (the
  engine) with zero coupling to the first (the WPF terminal
  app). The two share conventions and the central package set,
  nothing else.
- The maintainer can ratify or reverse each E-decision
  independently: grain (E2) is one function, the transport
  shape (E4) is one runner, the sink (E7) is one
  implementation of one interface.
- The Phase 0 dogfood happens on the maintainer's machine with
  a local `claude` install; the cloud session can only deliver
  CI-green code plus the run instructions (`docs/` updates land
  with the closure PR).

## Status notes

- 2026-06-11: ADR authored; implementation PR sequence begins
  (model → parser → chunker → ingest/bus → navigation →
  router → participant runner → voice/host → closure audit).
