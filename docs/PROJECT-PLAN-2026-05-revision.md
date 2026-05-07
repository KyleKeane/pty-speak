# Project plan — May 2026 (revision)

> **Snapshot**: 2026-05-07
> **Status**: successor to [`PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md); supersedes the "Next stage" pointer in the original plan; companion successor under the doc's stated "Future revisions should land as new dated plans" discipline (Track E E5 resolution: Option B)
> **Companion docs**:
> - [`PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) — original 2026-05-03 plan; preserved verbatim per its own framing for decision-history continuity
> - [`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md) — Track F audit; canonical "ready-to-pick-up" list
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md), [`SESSION-MODEL.md`](SESSION-MODEL.md), [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md), [`PANE-MODEL.md`](PANE-MODEL.md), [`CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) — substrate research-stage docs (5)
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md), [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md), [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md), [`AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md), [`AUDIT-DOC-CURRENCY.md`](AUDIT-DOC-CURRENCY.md) — sister audit-track docs

## What this document is

A revision to `PROJECT-PLAN-2026-05.md` capturing the
**substrate-first shift** the maintainer authorised
2026-05-06. The original plan's "Next session starts at
Part 2 (Stage 7)" pointer is now obsolete (Stage 7
shipped 2026-05-03). This successor reflects:

- The post-Stage-7 substrate cycle (PRs #146-#184).
- Five research-stage docs shipped (PIPELINE-NARRATIVE,
  SESSION-MODEL, INTERACTION-MODEL, PANE-MODEL,
  CUSTOMIZATION-MODEL).
- Six-track audit phase shipped (Tracks A-F).
- Audit-fixup queue (this revision is the closing item).
- Reordered post-audit sequence: SessionModel Tier 1
  implementation as FIRST POST-AUDIT IMPLEMENTATION
  CYCLE.

Per the original plan's own discipline (line 16-18:
"Future revisions should land as new dated plans rather
than edits in place"), this is a separate doc; the
original is preserved verbatim.

## Why this revision exists

The original `PROJECT-PLAN-2026-05.md` was authored
2026-05-03 with status header "**Next session starts
at Part 2** (Stage 7)". Stage 7 shipped 2026-05-03;
the post-Stage-7 substrate cycle ran 2026-05-04
through 2026-05-07; the audit phase ran 2026-05-06
through 2026-05-07. The plan's "next stage" pointer
has been stale for ~4 days.

Track E audit (PR #179) surfaced this as finding
**E5** with two options:
- **Option A**: small status-header update to the
  original plan (mild violation of "no edits in place").
- **Option B**: author a successor (this doc).

Maintainer chose **Option B** during the audit
walk-through (per CHANGELOG entry for PR #182, Cluster 4
resolution).

## Status as of 2026-05-07

### Shipped since the original plan

Numerous PRs across 4 days; major themes:

**Substrate cycle (PRs #146-#169)**:
- PR-N #146 / PR-O #147 / PR-P #148: Stage 7 wrap-up
  substrate-cleanup bundle.
- PR #151: post-Stage-7 substrate spec
  (`spec/event-and-output-framework.md`).
- PR #152-#157, #155, #158: Stages 8a / 8b / 8c / 8d.1 /
  8d.2 + 8d.2 revert + EarconPlayer fix.
- PR #159: Phase A — display-pathway substrate.
- PR #160: Phase A.1 — hot-switch baseline-seed.
- PR #161: Phase B subset — alt-screen → TuiPathway
  auto-detect.
- PR #162: Phase B subset — TOML config for pathway
  selection + parameters.
- PR #163: Phase A.2 — colour-detection earcons via
  event-splitting.
- PR #164: Phase A.2 hotfix — scope colour detection
  to changed rows.
- PR #165: Extend Ctrl+Shift+D into a self-test
  battery.
- PR #166: Sub-row suffix-diff at StreamPathway emit
  (item 1).
- PR #167: USER-SETTINGS atlas augmentation (item 26).
- PR #168: Tier 1 parameters (`bulk_change_threshold`,
  `backspace_policy`, `mode_barrier_flush_policy`).
- PR #169: Shell-switch flush fix (preserve per-row
  baselines under SummaryOnly).

**Research-stage doc cycle (PRs #170-#174 + #181)**:
- PR #170: Pipeline Narrative (item 19).
- PR #171: SessionModel design (item 28).
- PR #172: Track A code-consistency audit.
- PR #173: Interaction Model (item 29).
- PR #174: Pane Model (item 30).
- PR #181: Customization Model (item 31).

**Audit phase (PRs #172, #175-#180)**:
- PR #172 + #175: Track A audit + trivial fixups.
- PR #176: Track B test inventory.
- PR #177: Track C spec alignment.
- PR #178: Track D atlas alignment.
- PR #179: Track E doc currency.
- PR #180: Track F backlog validation; **closes audit
  phase**.

**Post-audit cleanup (PRs #182-#184)**:
- PR #182: Resolve Cluster 1-4 open questions in
  research-stage docs.
- PR #183: Track C D1 spec rename
  (`StreamProfile` → `PassThroughProfile`,
  ADR-authorised).
- PR #184 (this PR): pre-implementation cleanup
  bundle (Track D atlas-side fixups + Track E
  doc-currency fixups + ARCHITECTURE refresh +
  PROJECT-PLAN successor doc).

### Open questions resolved

The 2026-05-07 audit walk-through resolved 12 of ~25
maintainer-pending questions:
- 6 INTERACTION-MODEL.md questions (all).
- 5 PANE-MODEL.md questions (all).
- 1 SESSION-MODEL.md question (Q5: shell-switch
  semantics).

Open questions remaining (~14):
- 7 SESSION-MODEL.md questions (Q1-Q4 + Q6-Q8) —
  deferred to SessionModel Tier 1 plan-mode cycle.
- 7 CUSTOMIZATION-MODEL.md questions — await next
  maintainer walk-through.

### Audit health

Per `AUDIT-BACKLOG-VALIDATION.md` snapshot 2026-05-07:
- 0 ❌ structural contradictions across all 5 prior
  audit tracks.
- Substrate is **healthy**.

## The substrate-first shift

The maintainer's 2026-05-06 directive reframed the
priority sequence:

> "I think we should not get to fix it on this
> particular screen, drawing limitation yet, let's get
> all of the core underlying computational backend
> solid, so that each event in the UI has very clear
> pathways for computational management of the
> available data."

This shifted priorities from "fix specific UX issues
as they surface" to "name + design + audit the
substrate; defer UX fixes until the substrate can
express the right semantics".

The five research-stage docs + six audit tracks
implemented this shift. The substrate is now named,
audited, and consistent. **Next step: implementation.**

## Next stage: SessionModel Tier 1 implementation

**FIRST POST-AUDIT IMPLEMENTATION CYCLE.**

Per `docs/SESSION-MODEL.md` design + the Cluster 1-4
walk-through resolutions:

- **What ships**: substrate skeleton — CanonicalState
  extension; new `ScreenNotification.PromptBoundary`
  event; OSC 133 detection + heuristic fallback for
  cmd / PowerShell / Claude; in-memory tuple list;
  per-shell-session model (per Q5 resolution); `Memory`
  persistence mode (Q4 default).
- **What's deferred to Tier 2+**: persistence to disk;
  query API; pathway integration (ReplPathway /
  ClaudeCodePathway / FormPathway / SessionConsumer);
  query-by-trace (informed by CUSTOMIZATION-MODEL).
- **Plan-mode cycle resolves**: SESSION-MODEL.md Q1-Q4 +
  Q6-Q8 (7 still-open questions) at design time before
  implementation begins.

Estimated scope: ~1500-2500 LOC + ~30-50 new tests
across 2-4 PRs. SessionModel substrate is the
foundation; subsequent pathway work consumes it.

## Sequencing post-audit

```
✅ Cycle 7: Customization Model research stage (PR #181)
✅ Cycle 8: Resolve Cluster 1-4 open questions (PR #182)
✅ Cycle 9: Track C D1 spec rename (PR #183)
✅ Cycle 10: Pre-implementation cleanup bundle (THIS PR)
─────────────────────────────────────────────────────────
Cycle 11: SessionModel Tier 1 implementation
  - Plan-mode resolves SESSION-MODEL Q1-Q4 + Q6-Q8.
  - 2-4 sequenced PRs ship the substrate skeleton.
  - NVDA validation per ACCESSIBILITY-TESTING matrix.
Cycle 12-15: SessionModel Tier 2-3 (persistence; query
  API; pathway integration).
Cycle 16+: Phase 2 input framework cycle
  (echo correlation; cursor-aware editing; InputPathway
  protocol; ReplPathway).
Cycle X+: Phase 2 output framework refinement
  (FormPathway; ClaudeCodePathway).
Cycle Y+: CustomizationModel implementation
  (alternatives registry; per-output trace; override
  rules; Pipeline Inspector pane). Likely paired with
  Phase 2 to share substrate.
Cycle Z+: Phase 3 (AI summarisation; semantic
  segmentation; spatial audio).
```

## What this revision does NOT change

- **Original plan's body** preserved verbatim per its
  own discipline. Read it for the 2026-05-03 snapshot of
  Parts 1-5 + decision history.
- **Spec immutability** — per CLAUDE.md, spec/ edits
  still require ADR-style authorisation.
- **Walking-skeleton discipline** — every cycle ships +
  validates against NVDA before the next begins.
- **Single-concern PR discipline** — each cycle is one
  PR (or a sequenced multi-PR mini-cycle for larger
  work).

## Open follow-ups (forward-looking)

These pre-existed the substrate-first shift; tracked
through the strategic backlog:

- **Screen-buffer runtime resize** (Phase 2 stage).
- **Stable-baseline tag pushes** including
  `baseline/stage-7-claude-roundtrip` per
  `docs/CHECKPOINTS.md`.
- **Velopack release cadence** — maintainer cuts
  previews from `main` post-substrate-cycle.
- **Cross-platform port** (item 21) — deferred
  indefinitely; Avalonia (NOT MAUI) is the recommended
  target if/when this happens.

## Where to read more

For the **canonical post-audit ready-to-pick-up list**:
[`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md)
§ "Ready-to-pick-up list".

For **substrate-research vocabulary** (12-stage pipeline,
SIM, three-component model, Pane abstraction, etc.):
the five research-stage docs cited in the front matter.

For **historical decision context** (why Parts 1-5 took
the shape they did): the original
`PROJECT-PLAN-2026-05.md`.

## Versioning + maintenance

This is a **dated successor** doc. When the next
substantial shift happens (e.g. SessionModel Tier 1
ships + Phase 2 begins), a further successor
(`PROJECT-PLAN-2026-MM.md`) lands; this revision
gets preserved in the same way.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Cycle 10 (Track E E5 Option B resolution) | Initial successor doc. Captures substrate-first shift; reflects shipped substrate cycle (#146-#184); enumerates audit phase (Tracks A-F); points next stage at SessionModel Tier 1 implementation. |
