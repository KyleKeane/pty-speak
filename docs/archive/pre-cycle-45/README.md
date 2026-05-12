# Pre-Cycle-45 archive

Snapshot date: **2026-05-12**

The files in this directory describe the pre-Cycle-45 substrate
pipeline and the design / audit / research activity that surrounded
it. Cycle 45 (PRs #263–#270, merged 2026-05-11 / 2026-05-12)
replaced that pipeline with `ContentHistory` + `SpeechCursor`. The
archived files are preserved for decision-history continuity, not
as current reference — anything you find here that contradicts the
live docs is stale by definition.

## What was archived

| File | Why archived |
|---|---|
| `PROJECT-PLAN-2026-05.md` | 2026-05-03 original strategic plan; superseded per dated-snapshot discipline. Current plan: [`docs/PROJECT-PLAN-2026-05-09.md`](../../PROJECT-PLAN-2026-05-09.md). |
| `PROJECT-PLAN-2026-05-revision.md` | 2026-05-07 intermediate revision; superseded by 2026-05-09. |
| `AUDIT-ATLAS-ALIGNMENT.md` | May 2026 audit Track D — parameter-atlas validation. Conducted against the pre-Cycle-45 substrate. |
| `AUDIT-BACKLOG-VALIDATION.md` | Track F — final audit deliverable. |
| `AUDIT-CODE-CONSISTENCY.md` | Track A — validates `PIPELINE-NARRATIVE.md` named entities against then-current code. |
| `AUDIT-DOC-CURRENCY.md` | Track E — validates doc currency at audit time. |
| `AUDIT-SPEC-ALIGNMENT.md` | Track C — validates spec against research-stage docs. |
| `AUDIT-TEST-INVENTORY.md` | Track B — classifies test suite coverage at audit time. |
| `PIPELINE-NARRATIVE.md` | Shared vocabulary for the 12-stage byte-to-announce pipeline. Superseded by [`spec/event-and-output-framework.md`](../../../spec/event-and-output-framework.md) (the canonical substrate vocabulary). |
| `STAGE-7-ISSUES.md` | Gap inventory surfaced during Stage 7 NVDA validation (2026-05-03). Consumed as design input by subsequent framework cycles. |
| `HISTORICAL-CONTEXT-2026-05.md` | May-2026 cleanup-cycle archaeology; rationale captured in CHANGELOG and git history. |
| `0001-linear-text-substrate.md` | RFC 0001 — formalised the `LinearTextStream` streaming-emission protocol. The substrate it describes was removed by Cycle 45; archived alongside the code it specified. |
| `research/MAY-4.md` | Maintainer-authored research seed for the output-framework cycle. Consolidated into `spec/event-and-output-framework.md`. |
| `research/Output-paradigms.md` | Tier-1 display-primitives survey. Lifted into [`docs/CANONICAL-DISPLAY-CATALOG.md`](../../CANONICAL-DISPLAY-CATALOG.md) and the spec. |
| `research/emission-paradigms.md` | Live-region detection + cadence parameters. Absorbed into the spec + RFC 0001 (now itself archived). |

## What stayed live

The Cycle 45 substrate reset deliberately preserved two layers:

- **ADR 0001** — [`docs/adr/0001-substrate-channel-dichotomy.md`](../../adr/0001-substrate-channel-dichotomy.md).
  The substrate/channel architectural framing remains
  non-negotiable.
- **`spec/`** — `overview.md`, `tech-plan.md`,
  `event-and-output-framework.md`. The design substrate is
  immutable per `CLAUDE.md`'s "Spec immutability" rule.

If you're looking for the current substrate spec, start there.

## Why these moved

Cycle 45 replaced the pre-existing `StreamPathway` /
`LinearTextStream` / `DisplayPathway` / `TuiPathway` /
`PathwaySelector` pipeline with a single `ContentHistory` +
`SpeechCursor` pipeline. The maintainer dogfood after PR #270
confirmed the new pipeline narrates command output cleanly,
handles long output through NVDA chunking, fires the
ready-for-input earcon on tuple finalise, and supports speech-
cursor history navigation.

With the substrate replaced, the narrative layer (audits,
pipeline narrative, research seeds, the RFC formalising the
old substrate, predecessor plans, stage-gap inventory) became
historical rather than active reference. Moving them here keeps
the live docs focused on the current architecture without
discarding the decision history.
