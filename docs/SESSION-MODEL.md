# SessionModel substrate — renamed

> **Cycle 51 PR-W (2026-05-15): renamed to IOCell.** The unit
> of shell interaction is now `SessionModel.IOCell` (ADR 0004
> Decision 1). The decades-stable on-disk wire format —
> `schemaVersion 2`, the `cellSequence` + `phase` fields, the
> hand-rolled serializer discipline — now lives in
> [`IOCELL-SCHEMA.md`](IOCELL-SCHEMA.md).

This file is intentionally a short pointer stub. Read instead:

- **Wire format / in-memory shape** →
  [`IOCELL-SCHEMA.md`](IOCELL-SCHEMA.md).
- **The decision record** (why the pivot, what is locked) →
  [`adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md).
- **Architectural framing** (substrate / channel / interaction
  / IOCell) → [`CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md)
  and [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md).

The pre-pivot research-stage design doc (2026-05-06 — OSC 133
protocol notes, per-shell heuristic-fallback design, the
pathway-integration archaeology, the 8 open questions) is
preserved in git history at the commit before this stub
(`git log --follow -- docs/SESSION-MODEL.md`). It was
"design archaeology" by its own Cycle-45c header; the live
substrate is `ContentHistory` + `SpeechCursor` in
`src/Terminal.Core/`, and the cell model is locked by ADR
0004. The broader cross-doc reframe (this file's ~13
referrers) is the Cycle 51 PR-Z closure-audit's job.
