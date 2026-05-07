# Audit: Doc Currency

> **Snapshot**: 2026-05-07
> **Status**: audit / inventory document — no code change; no doc edits in this PR; recommendations tiered for follow-up cycles
> **Authoring item**: Track E of the comprehensive audit phase
> **Companion docs**:
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) — Track A
> - [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) — Track B
> - [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) — Track C
> - [`AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md) — Track D
> - [`DOC-MAP.md`](DOC-MAP.md) — index doc; consistency-checked subject
> - [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching + PR conventions

## Why this exists

Track E of the substrate-first audit closes the loop in
the documentation-state direction: validate that **every
doc in the repo reflects current state** — snapshot dates
sensible, cross-references work, "where we left off"
matches reality.

Across the substrate cycle (PRs #160-#178) and the
research-stage doc cycle (PRs #170-#174 + audit Tracks
A-D), a substantial amount of work has shipped. Some
older docs were authored before this work and haven't
been updated; this audit identifies which.

The findings cluster as:
- **Stale "current state" headers** in older
  developer-reference docs (ARCHITECTURE.md, ROADMAP.md,
  SESSION-HANDOFF.md "Where we left off").
- **Intentionally historical docs** correctly tagged
  (PROJECT-PLAN-2026-05.md, HISTORICAL-CONTEXT-2026-05.md,
  STAGE-7-ISSUES.md).
- **Forward-looking openness** for unimplemented future
  deliverables (RFC-OUTPUT-FRAMEWORK / RFC-INPUT-FRAMEWORK
  referenced in PROJECT-PLAN; correctly absent today).
- **Recently-authored docs are current** (research-stage
  docs PIPELINE-NARRATIVE / SESSION-MODEL /
  INTERACTION-MODEL / PANE-MODEL; audit-track docs
  AUDIT-CODE-CONSISTENCY / AUDIT-TEST-INVENTORY /
  AUDIT-SPEC-ALIGNMENT / AUDIT-ATLAS-ALIGNMENT).

**Headline**: doc currency is **good**. The 5 ⚠ findings
all concern the same root cause: developer-reference docs
that snapshot a Stage-3b or Stage-7-just-shipped baseline
and weren't refreshed when the substrate cycle (#160-#178)
landed. All findings are mechanically fixable in a small
follow-up PR.

## What this document is

A snapshot-dated **classification + triage report** for
doc currency, paralleling Tracks A / B / C / D. Audit-only;
no doc edits, no code changes. Recommendations are tiered;
a small follow-up PR can apply the doc-side fixes
mechanically (with judgement on framing, since "Where we
left off" updates carry more nuance than mechanical
constant fixups).

## Methodology

Audit performed via direct read 2026-05-07. Steps:

1. **Inventory all docs** under `docs/` + root-level
   (README.md, CLAUDE.md, CONTRIBUTING.md, CHANGELOG.md,
   SECURITY.md) + `docs/research/MAY-4.md`.
2. **Read each doc's status / currency markers** —
   "Snapshot:" lines, "Status:" lines, "Where we left
   off" sections, "Currency note" headers.
3. **Cross-check stage-status claims** (e.g. ROADMAP.md
   Stage table) against actual git history of merged PRs.
4. **Spot-check cross-reference integrity** — sample
   markdown links across docs; verify target files exist.
5. **Verify DOC-MAP.md consistency** — every listed doc
   exists; no orphans (docs in `docs/` not listed in
   DOC-MAP.md); per-audience entry-point lists current.

The audit does NOT execute code or rewrite any file. Pure
static inspection.

## Findings summary

**Doc currency (snapshot 2026-05-07):**

| Tag | Count | Meaning |
|---|---|---|
| ✅ aligned | 26 | Doc reflects current state OR is intentionally historical / forward-looking and correctly tagged. |
| ⚠ stale | 5 | Doc claim doesn't reflect current state and should be updated. |
| ❌ contradiction | 0 | No structural contradictions. |
| 📋 forward-looking openness | 2 | RFC-OUTPUT-FRAMEWORK / RFC-INPUT-FRAMEWORK referenced as planned future deliverables; correctly absent today. |

**Headline**: doc currency is **good**. All 5 ⚠ findings
cluster on a common root cause: post-substrate-cycle
(#160-#178) refresh hasn't reached older
developer-reference docs.

## Doc inventory

**Total: 33 docs** (27 in `docs/` + 5 root-level + 1
research seed at `docs/research/MAY-4.md`).

### docs/ directory (28 files counting AUDIT-DOC-CURRENCY itself)

| Doc | LOC | Status |
|---|---:|---|
| `ACCESSIBILITY-INTERACTION-MODEL.md` | 1146 | ✅ current (forward-looking design skeleton) |
| `ACCESSIBILITY-TESTING.md` | 740 | ✅ current (NVDA validation matrix) |
| `ARCHITECTURE.md` | 206 | ⚠ stale (E2-E4: Stage 3b as baseline) |
| `AUDIT-ATLAS-ALIGNMENT.md` | 412 | ✅ current (Track D, snapshot 2026-05-07) |
| `AUDIT-CODE-CONSISTENCY.md` | 654 | ✅ current (Track A, snapshot 2026-05-06) |
| `AUDIT-SPEC-ALIGNMENT.md` | 504 | ✅ current (Track C, snapshot 2026-05-07) |
| `AUDIT-TEST-INVENTORY.md` | 699 | ✅ current (Track B, snapshot 2026-05-07) |
| `BUILD.md` | 120 | ✅ current (build recipe) |
| `CHECKPOINTS.md` | 159 | ✅ current (rollback baseline tags) |
| `CONPTY-NOTES.md` | 89 | ✅ current (Win32 ConPTY quirks) |
| `DOC-MAP.md` | 134 | ✅ current (index; verified consistent) |
| `HISTORICAL-CONTEXT-2026-05.md` | 204 | ✅ intentionally historical (correctly tagged) |
| `INSTALL.md` | 88 | ✅ current (end-user install) |
| `INTERACTION-MODEL.md` | 1429 | ✅ current (research-stage, snapshot 2026-05-07) |
| `LOGGING.md` | 214 | ✅ current (FileLogger architecture) |
| `PANE-MODEL.md` | 1132 | ✅ current (research-stage, snapshot 2026-05-07) |
| `PIPELINE-NARRATIVE.md` | 1775 | ✅ current (research-stage; PR #175 fixups applied) |
| `PROJECT-CONTEXT.md` | 157 | ✅ current (author bio + values) |
| `PROJECT-PLAN-2026-05.md` | 812 | ⚠ stale-snapshot-pointer (E5: "Next session starts at Part 2 (Stage 7)" pre-substrate-cycle); body intentionally historical per its own framing |
| `RELEASE-PROCESS.md` | 483 | ✅ current (Velopack workflow) |
| `ROADMAP.md` | 195 | ⚠ stale (E1: Stage 7 row marked "pending"; Stage 7 shipped 2026-05-03) |
| `SESSION-HANDOFF.md` | 1376 | ⚠ stale (E6: "Where we left off" describes pre-substrate-cycle state) |
| `SESSION-MODEL.md` | 1453 | ✅ current (research-stage, snapshot 2026-05-06) |
| `STAGE-7-ISSUES.md` | 478 | ✅ intentionally historical (Stage 7 inventory; consumed by framework cycles) |
| `UPDATE-FAILURES.md` | 114 | ✅ current (Velopack troubleshooting) |
| `USER-SETTINGS.md` | 1547 | ⚠ stale-section-headers (per Track D PR #178 findings; not re-flagged here) |

### Root-level docs

| Doc | LOC | Status |
|---|---:|---|
| `README.md` | 246 | ✅ current (project pitch + status) |
| `CLAUDE.md` | 419 | ✅ current (Claude-runtime rules) |
| `CONTRIBUTING.md` | 505 | ✅ current (PR shape + F# gotchas) |
| `CHANGELOG.md` | 6586 | ✅ current (latest entries through PR #178) |
| `SECURITY.md` | 592 | ✅ current (PO/TC/SR threat model) |

### Research seed

| Doc | LOC | Status |
|---|---:|---|
| `docs/research/MAY-4.md` | 427 | ✅ current (maintainer-authored research seed) |

## Per-doc analysis (drift findings)

### E1: ROADMAP.md — Stage 7 row stale

**Doc**: `docs/ROADMAP.md` line 53.

**Current text**:
```
| 7 | Run Claude Code end-to-end | Roundtrip prompt → response, NVDA reads it | pending — sequenced as **Part 2 (validation gate)** of the [May-2026 plan](PROJECT-PLAN-2026-05.md); ships before the framework cycles to surface the gap inventory that drives their design. |
```

**Reality**: Stage 7 shipped 2026-05-03 (per
SESSION-HANDOFF.md line 31, which lists 11 sequenced PRs
A-K + PR-L + PR-M = #131-#145 all merged). The status
should be **shipped**.

**Triage**: Tier 1 (doc-fix). Update Stage 7 row status to
"shipped" with a brief note crediting PR-A through PR-M.

### E2: ARCHITECTURE.md — Currency note stale

**Doc**: `docs/ARCHITECTURE.md` lines 9-14.

**Current text**:
> **Currency note.** The diagrams and module table below
> are the *target* architecture from `spec/overview.md`.
> As of `main` at Stage 3b, only the rows annotated
> **(implemented)** are in code; the rest land in the
> stages noted in parentheses.

**Reality**: As of `main` at 2026-05-07, Stages 0-7 + 11
are shipped, plus the post-Stage-7 substrate cycle
(#160-#178) including pathway substrate, color-detection,
diagnostic battery, suffix-diff, parameter atlas, and 5
research-stage docs. Currency note describes Stage 3b
state.

**Triage**: Tier 1 (doc-fix). Update the currency note to
reflect Stage 7 + 11 shipped + post-Stage-7 substrate
work. The module table itself (lines 91-104) needs
review:
- Some "future" modules in the table never materialised
  as separate projects (e.g. `Terminal.Semantics` never
  shipped as a project; the semantics work landed inside
  `Terminal.Core` via Coalescer + StreamPathway).
- New modules from the substrate cycle aren't in the
  table (CanonicalState, DisplayPathway, StreamPathway,
  PassThroughProfile, EarconProfile, etc.).

A full module-table refresh is larger than a Tier 1 fix
— recommend a separate "ARCHITECTURE.md refresh" cycle
(small) that audits + updates the table comprehensively.

### E3: ARCHITECTURE.md — Current-pipeline header stale

**Doc**: `docs/ARCHITECTURE.md` line 18.

**Current text**: `### Current pipeline (Stage 3b on main)`.

**Reality**: Same as E2 — current pipeline includes
Stages 0-7 + 11 + the post-Stage-7 substrate work.

**Triage**: Tier 1 (doc-fix). Update header to reflect
current state. Closely paired with E2.

### E4: ARCHITECTURE.md — Threading-model header stale

**Doc**: `docs/ARCHITECTURE.md` line 111.

**Current text**: `### Today (Stage 3b on main)`.

**Reality**: Same as E2 / E3. Threading model has evolved
(PathwayPump thread; diagnostic event-tap mechanism per
PR #165; etc.).

**Triage**: Tier 1 (doc-fix; bundled with E2 + E3). The
threading-model section may need substantive update too;
recommend handling as part of the "ARCHITECTURE.md
refresh" cycle.

### E5: PROJECT-PLAN-2026-05.md — Status pointer stale

**Doc**: `docs/PROJECT-PLAN-2026-05.md` line 40.

**Current text**: `> **Next session starts at Part 2** (Stage 7).`

**Reality**: Multiple substantive cycles have shipped
since this snapshot pointer was written (Stage 7 itself
shipped 2026-05-03; substrate cycle PRs #160-#178 followed;
audit phase Tracks A-D shipped). The pointer is stale.

**Note**: The doc's own framing (line 16-18) explicitly
says "Future revisions should land as new dated plans
(`PROJECT-PLAN-2026-MM.md`) rather than edits in place".
Tension: should the status pointer be edited (mild
violation of the "no edits in place" rule) OR should a
successor plan doc capture the substrate-first shift?

**Triage**: Tier 1 OR Tier 4 (judgement call):

- **Option A**: Small Tier 1 update to the status header
  (lines 20-43) to note that Stage 7 + the substrate
  cycle have shipped + the audit phase is in progress;
  preserve body verbatim.
- **Option B**: Tier 4 — author a successor
  `PROJECT-PLAN-2026-05-revision.md` (or similar dated
  successor) capturing the substrate-first shift; leave
  the original untouched per its own framing.

**Recommendation**: Option A is lighter; Option B is more
faithful to the doc's stated discipline. **Surface as an
open question** to the maintainer; don't commit a
direction in the audit doc itself.

### E6: SESSION-HANDOFF.md — "Where we left off" stale

**Doc**: `docs/SESSION-HANDOFF.md` lines 31-34.

**Current text** (table cells):
- "Last merged stages": describes Stages 0-7 + 11; lists
  PRs A-M (#131-#145) through 2026-05-03/04. Doesn't
  mention substrate cycle (PRs #160-#178) or research-
  stage doc cycle (PRs #170-#174 + audit Tracks A-D).
- "Last shipped release": `v0.0.1-preview.43`.
- "In-flight branch":
  `claude/event-and-output-framework-spec` — long since
  merged.
- "Next stage": Sub-stage 8a per
  `spec/event-and-output-framework.md` Part C — but the
  substrate-first shift (2026-05-06) reframed priorities
  toward the audit phase + SessionModel substrate; "Next
  stage" pointer is stale.

**Reality**: The maintainer's substrate-first directive
2026-05-06 (per SESSION-MODEL.md, INTERACTION-MODEL.md,
PANE-MODEL.md, AUDIT-* docs) materially changed the
"next" pointer. SESSION-HANDOFF should reflect:
- Current "in-flight" = audit phase (Tracks A-D shipped;
  Track E in audit; Track F next).
- Current "next" = (after audit complete) maintainer
  open-questions resolution + SessionModel Tier 1
  implementation.
- Substrate cycle PRs #160-#178 should be summarised in
  "Last merged stages" section.

**Triage**: Tier 1 (doc-fix; substantive). Update "Where
we left off" table to reflect current state. This is the
**most important** Track E fix — SESSION-HANDOFF.md is
the canonical "where are we" doc per CLAUDE.md.

## Cross-reference health

Spot-checked 10+ markdown links across `docs/`. **Result:
zero broken links** found.

| Source link | Target | Status |
|---|---|---|
| `DOC-MAP.md` → `INSTALL.md` | docs/INSTALL.md | ✅ |
| `DOC-MAP.md` → `spec/overview.md` | spec/overview.md | ✅ |
| `DOC-MAP.md` → `spec/event-and-output-framework.md` | spec/event-and-output-framework.md | ✅ |
| `SESSION-HANDOFF.md` → `PROJECT-PLAN-2026-05.md` | docs/PROJECT-PLAN-2026-05.md | ✅ |
| `ROADMAP.md` → `PROJECT-PLAN-2026-05.md` | docs/PROJECT-PLAN-2026-05.md | ✅ |
| `INTERACTION-MODEL.md` → `PIPELINE-NARRATIVE.md` | docs/PIPELINE-NARRATIVE.md | ✅ |
| `PANE-MODEL.md` → `INTERACTION-MODEL.md` | docs/INTERACTION-MODEL.md | ✅ |
| `AUDIT-CODE-CONSISTENCY.md` → `PIPELINE-NARRATIVE.md` | docs/PIPELINE-NARRATIVE.md | ✅ |
| `AUDIT-SPEC-ALIGNMENT.md` → `spec/tech-plan.md` | spec/tech-plan.md | ✅ |
| `AUDIT-ATLAS-ALIGNMENT.md` → `USER-SETTINGS.md` | docs/USER-SETTINGS.md | ✅ |

## DOC-MAP.md consistency

**Status**: 100% consistent.

Verified:
- Every doc listed in DOC-MAP.md table exists at the
  cited path.
- Every doc in `docs/` is listed in DOC-MAP.md (no
  orphans).
- Per-audience entry-point lists (lines 56-113) include
  recent additions (research-stage docs, audit-track
  docs, MAY-4.md research seed,
  spec/event-and-output-framework.md).
- Recent additions (INTERACTION-MODEL, PANE-MODEL,
  AUDIT-* docs) added incrementally as their PRs landed.

**Note**: `AUDIT-DOC-CURRENCY.md` (this doc) will be
added to DOC-MAP.md when this PR ships; not a current
finding.

## Triage

Findings classified by required action:

### Tier 1 — Doc-fix (mechanical)

Three small mechanical fixes, plus one substantive update:

| # | Finding | Doc | Edit type |
|---|---|---|---|
| E1 | Stage 7 row marked "pending" | `ROADMAP.md` line 53 | Update Stage 7 status to "shipped" |
| E2-E4 | Currency note + headers reference Stage 3b | `ARCHITECTURE.md` lines 9-14, 18, 111 | Update currency note + section headers; recommend separate "ARCHITECTURE.md refresh" cycle for the module table itself |
| E6 | "Where we left off" describes pre-substrate state | `SESSION-HANDOFF.md` lines 31-34 | Substantive update: recap substrate cycle + audit phase; update "next" pointer; preserve historical context |

Suggested follow-up PR: `docs(audit): Track E — apply
doc-currency fixups (ROADMAP / ARCHITECTURE /
SESSION-HANDOFF)`. Estimated PR size: ~100-200 LOC of
doc edits.

### Tier 2 — Open question for maintainer

| # | Finding | Doc | Question |
|---|---|---|---|
| E5 | Status pointer stale (intentionally-historical doc) | `PROJECT-PLAN-2026-05.md` line 40 | Option A (small status-header update; mild violation of "no edits in place") OR Option B (successor `PROJECT-PLAN-2026-05-revision.md` capturing substrate-first shift; leave original untouched). Maintainer judgement. |

### Tier 3 — No action (intentional historicals)

- `HISTORICAL-CONTEXT-2026-05.md` — explicitly tagged
  supplementary; correct as-is.
- `STAGE-7-ISSUES.md` — finalised May 3 inventory;
  consumed by framework cycles; correct as-is.
- `PROJECT-PLAN-2026-05.md` body — preserved verbatim per
  the doc's own framing; correct as-is.

### Tier 4 — Forward-looking openness

- `RFC-OUTPUT-FRAMEWORK.md` and `RFC-INPUT-FRAMEWORK.md`
  referenced in `PROJECT-PLAN-2026-05.md` Part 3.2 / 4.2
  but don't exist yet — correct (planned for future RFC
  phases).
- `OUTPUT-FRAMEWORK-PRIOR-ART.md` referenced in
  `PROJECT-PLAN-2026-05.md` line 336-340 — replaced by
  `MAY-4.md`; the plan documents this decision-shift
  correctly. No action.

### Tier 5 — ARCHITECTURE.md refresh cycle (separate, larger)

ARCHITECTURE.md's module table (lines 91-104) +
threading-model section (lines 106-138) need substantive
update to match the post-Stage-7 substrate state. Beyond
the scope of E2-E4's mechanical header fixes. Recommended
as its own small refresh cycle, owned by the same
follow-up sequence as Track E doc-side fixes.

## Cross-cutting themes

### Theme 1: Substrate-cycle refresh hasn't reached
developer-reference docs (E1, E2, E3, E4, E6)

Five findings cluster on a common root cause: the
post-Stage-7 substrate cycle (#160-#178) shipped a lot
of work; the developer-reference docs (ARCHITECTURE,
ROADMAP, SESSION-HANDOFF "Where we left off") snapshot a
Stage 7-just-shipped baseline and weren't refreshed.

This is a **documentation-process gap**, not an
architectural concern. A small follow-up PR (or two:
the mechanical fixes + an ARCHITECTURE refresh) closes
it.

Going forward, when a substantial substrate PR ships,
the same PR (or an immediate follow-up) should refresh
SESSION-HANDOFF.md "Where we left off" + relevant
developer-reference doc currency notes.

### Theme 2: "Intentionally historical" framework working

Three docs (PROJECT-PLAN-2026-05.md,
HISTORICAL-CONTEXT-2026-05.md, STAGE-7-ISSUES.md) are
explicitly tagged historical and are correctly handled.
The pattern (date-tag the doc + add a status header at
top + preserve body verbatim) is working well; readers
know to treat these as snapshots, not live docs.

### Theme 3: Recently-authored docs are clean

All research-stage docs (PIPELINE-NARRATIVE,
SESSION-MODEL, INTERACTION-MODEL, PANE-MODEL) and audit-
track docs (AUDIT-CODE-CONSISTENCY,
AUDIT-TEST-INVENTORY, AUDIT-SPEC-ALIGNMENT,
AUDIT-ATLAS-ALIGNMENT) are current — all carry
`Snapshot: YYYY-MM-DD` lines, all have accurate
cross-references. The new convention is working.

## Recommendations

### Immediate (small, mechanical)

- **Track E doc-side fixup PR** applying E1, E2-E4 (header-
  level), E6 (~100-200 LOC). Mirrors Track A's PR #175
  pattern.

### Next-cycle (medium)

- **ARCHITECTURE.md refresh cycle** — substantive update
  to the module table + threading-model section to match
  post-Stage-7 substrate state. Estimated ~150-300 LOC.
- **Maintainer decision on E5** — Option A (status-
  header update) vs. Option B (successor plan doc).

### Future (substrate-implementation-gated)

- When new docs are added (e.g. RFC-OUTPUT-FRAMEWORK.md
  ships during the spec re-authoring cycle per
  PROJECT-PLAN Part 3.2), DOC-MAP table entries get
  added in the same PR.
- When substantial implementation PRs ship (e.g.
  SessionModel Tier 1, Phase 2 input framework),
  SESSION-HANDOFF "Where we left off" gets updated in
  the same PR or immediately after.

## What this PR does NOT do

- **No doc edits** — Tier 1 + Tier 5 fixes deferred to
  follow-up PRs (single-concern discipline).
- **No code changes**.
- **No spec changes** (per CLAUDE.md spec-immutability).
- **No new docs** (the audit doc itself is the only new
  file; the fixes ship later).

## Cross-references

- [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md)
  — Track A.
- [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) —
  Track B.
- [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) —
  Track C.
- [`AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md)
  — Track D.
- [`DOC-MAP.md`](DOC-MAP.md) — index doc; consistency-
  verified.
- [`SESSION-HANDOFF.md`](SESSION-HANDOFF.md) — primary
  finding (E6).
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — primary finding
  (E2-E4).
- [`ROADMAP.md`](ROADMAP.md) — primary finding (E1).
- [`PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) —
  open question (E5).
- [`HISTORICAL-CONTEXT-2026-05.md`](HISTORICAL-CONTEXT-2026-05.md)
  — intentionally historical; correct as-is.
- [`STAGE-7-ISSUES.md`](STAGE-7-ISSUES.md) —
  intentionally historical; correct as-is.
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching +
  PR conventions.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Track E audit (Cycle 5) | Initial snapshot; 33 docs inventoried (27 in docs/ + 5 root + 1 research seed); 26 ✅ aligned, 5 ⚠ stale (E1 ROADMAP Stage 7 row; E2-E4 ARCHITECTURE.md currency + headers; E6 SESSION-HANDOFF Where-we-left-off), 0 ❌ contradictions, 2 📋 forward-looking openness; 1 open question (E5 PROJECT-PLAN status pointer); cross-reference health 100%; DOC-MAP consistency 100%. Recommended Tier 1 follow-up PR (~100-200 LOC) + separate ARCHITECTURE refresh cycle (~150-300 LOC). |

