# ADR 0008 — Maximal semantic surfacing: recover and emit unambiguous semantic events, never relay computationally ambiguous content

- **Status**: Accepted (2026-05-16; maintainer directed
  elevation of the ADR 0007 D9 principle to a project-wide
  guiding principle)
- **Date**: 2026-05-16
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 52. The principle was articulated
  by the maintainer while co-authoring
  [ADR 0007](0007-canonical-iocell-history-navigation.md)
  (D9) and explicitly flagged as "a more general guiding
  principle than is appropriate for this current ADR". The
  maintainer then directed it be elevated "to the appropriate
  place". This ADR is that place; ADR 0007 D9 now references
  it as the project-wide statement of the same idea.
- **Companion docs**: [ADR 0001](0001-substrate-channel-dichotomy.md)
  (substrate/channel dichotomy — this principle is the
  *purpose* that dichotomy serves), [ADR 0004](0004-iocell-model-for-shell-interaction.md)
  Decision 4 (OutputDispatcher = the one canonical channel),
  [ADR 0007](0007-canonical-iocell-history-navigation.md) D9
  (the cell history as the fullest current expression),
  `docs/CORE-ABSTRACTION-BOUNDARY.md` (architectural framing;
  carries a pointer to this ADR).

## Context

A terminal shell encodes **minimal metadata**. It emits a
byte stream of text and control sequences whose *meaning* —
"this is a prompt", "this is the command you typed", "this is
output", "this is an error", "this is a sub-prompt question",
"this run finished with exit code 2" — was historically
recovered by a sighted human interpreting a **visual
rendering**. The semantics live in the human's reading of the
screen, not in the stream. Relayed verbatim to assistive
technology, that stream is **computationally ambiguous**: the
boundaries and roles a screen-reader user needs are not
present as data; they have to be reconstructed.

Fifty-one cycles of this project's history are, in effect, a
long demonstration that reconstructing those semantics *late*
(screen-row heuristics, byte-stream pattern-matching) is
brittle. The ADR 0001 substrate/channel dichotomy, the
ADR 0004 IOCell model, the ADR 0006 OSC-133 re-foundation,
and the ADR 0007 canonical cell history are all the same move
applied at different layers: **recover the semantics as early
and as explicitly as the available information allows, and
carry them as typed data — not as text to be re-interpreted
downstream.** This ADR names that move as the project's
guiding principle so it is not re-derived ad hoc per feature.

## Decision

**pty-speak's core purpose is to recover the maximal
unambiguous semantic structure obtainable from the shell
interaction, represent it as typed data, and emit it as
unambiguous typed events on the one canonical pipeline — from
which any number of output modalities are composed. We do not
relay computationally ambiguous content as the primary
contract.**

Concretely, this principle commits the project to:

1. **Recover, don't relay.** Wherever the information exists
   to determine a semantic boundary or role (prompt vs.
   command vs. output vs. sub-prompt vs. progress segment vs.
   exit status), recover it and carry it as typed structure.
   Prefer information sources that make the semantics
   explicit (OSC-133, shell integration, the IOCell model)
   over re-deriving them from rendered text. Verbatim text is
   a payload *inside* a typed structure, never the structure
   itself.

2. **One canonical event pipeline; many composable sinks.**
   Every recovered semantic event flows through the single
   canonical channel surface (`OutputDispatcher`, ADR 0004
   Decision 4) carrying a stable, single-purpose `ActivityId`.
   Speech (NVDA), earcon (WASAPI), and future modalities
   (multi-line braille, spatial audio, custom TTS) are
   **sinks that compose from the same event** — no modality
   is primary, none re-derives meaning from another's
   rendered output. An event a sink cannot yet use is still
   emitted (it is data; the sink is the gap).

3. **Maximal, honest, bounded.** Surface as much semantic
   structure as the available information *actually*
   supports — and no more. Where the shell genuinely does not
   provide enough to disambiguate (cmd has no exit-code hook;
   PSReadLine is disabled under a screen reader), the honest
   move is "loud silence / explicit unknown", not a
   confident-sounding guess. Fabricated semantics are worse
   than acknowledged absence. (This is the same discipline as
   ADR 0004 Decision 3's drop-on-`None`.)

4. **The representation is the most complete we can
   synthesize.** The canonical computational history
   (ADR 0007) is the fullest expression of this principle:
   the most complete semantic representation of the
   interaction that can be synthesized from the information
   available, addressable and operable, not a flattened
   relay. New surfaces are judged by the same standard.

## Consequences

- This is a **standing design constraint**, not a feature.
  Any new substrate, channel, event source, or output
  surface is evaluated against it: does it recover semantics
  as typed data and emit unambiguous canonical events, or
  does it relay ambiguous content and push re-interpretation
  downstream? The former is in-bounds; the latter requires an
  explicit, recorded justification.
- It gives a single rationale for decisions previously argued
  per-ADR: ADR 0001 (linear substrate is the recoverable
  shape), ADR 0004 (IOCell carries the recovered semantics;
  OutputDispatcher is the one pipeline), ADR 0006 (OSC-133
  makes the semantics explicit at the source), ADR 0007
  (the cell history is the composable typed representation).
  They are one principle at four layers.
- It sanctions building modality sinks (braille, spatial
  audio) against the event stream **without** waiting for a
  speech-first design — and forbids a sink that works by
  re-parsing another sink's rendered text.
- It does **not** mandate retrofitting existing surfaces in
  one pass; it sets the direction. Existing
  ambiguity-relaying paths (e.g. the legacy review-cursor
  text materialisation, ADR 0002) are demarcated and
  superseded over time (ADR 0007 D7), not ripped out
  reactively.
- `docs/CORE-ABSTRACTION-BOUNDARY.md` and ADR 0001 carry a
  pointer to this ADR so the principle is discoverable from
  the architectural-framing entry points, not only from
  ADR 0007.

## Alternatives considered

- **Leave it as ADR 0007 D9 only.** Rejected at the
  maintainer's direction: it is not specific to the cell
  history; burying a project-wide principle inside one
  feature ADR guarantees it is re-derived (or contradicted)
  by the next surface that does not read ADR 0007.
- **Amend ADR 0001 in place.** Rejected: ADR 0001 is an
  Accepted foundational record of a *specific* decision (the
  substrate/channel dichotomy); editing its body to carry a
  broader principle muddies its traceability. A new ADR that
  ADR 0001 *points to* keeps both records clean.
- **A prose section in CORE-ABSTRACTION-BOUNDARY only.**
  Rejected as the sole home: that doc is framing, not a
  decision record; the principle is a decision and belongs in
  the ADR series, with the framing doc pointing at it.
