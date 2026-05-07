# Audit: Spec Alignment

> **Snapshot**: 2026-05-07
> **Status**: audit / inventory document — no code change; no spec change (per CLAUDE.md spec-immutability rule); recommendations tiered for follow-up cycles
> **Authoring item**: Track C of the comprehensive audit phase
> **Companion docs**:
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) — Track A (code-against-research-doc validation)
> - [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) — Track B (test-layer inventory)
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational substrate vocabulary
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking history substrate
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing
> - [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition substrate
> - [`spec/overview.md`](../spec/overview.md) — foundational architecture (read-only audit subject)
> - [`spec/tech-plan.md`](../spec/tech-plan.md) — stage-by-stage plan (read-only audit subject)
> - [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) — post-Stage-7 substrate spec (read-only audit subject)
> - [`CLAUDE.md`](../CLAUDE.md) — spec-immutability rule (no spec edits without ADR-style maintainer authorisation)

## Why this exists

Track C of the substrate-first audit. Track A (PR #172)
validated **code against research-stage docs**; Track B
(PR #176) inventoried **the test suite**. Track C closes
the loop in the third direction: validate **spec against
research-stage docs** + **spec against current code**.

The recent research-stage docs (PIPELINE-NARRATIVE,
SESSION-MODEL, INTERACTION-MODEL, PANE-MODEL,
AUDIT-CODE-CONSISTENCY, AUDIT-TEST-INVENTORY) introduced
substantial new vocabulary (Pathway, Shell Interaction
Manager, three-component model, SessionTuple, Pane,
EditDelta, BackspacePolicy, BulkChangeThreshold,
ModeBarrierFlushPolicy, etc.). Some of that vocabulary is
**absorbed** into spec (e.g. event-and-output-framework's
StreamProfile / EarconProfile match the shipped substrate);
some is **forward-looking** (SessionModel substrate is
designed but not yet implemented; spec doesn't yet
reference it); some is **net-new** since the spec was
authored (suffix-diff and BackspacePolicy ship in code but
spec doesn't describe them).

This audit identifies which is which. **Critically**: per
CLAUDE.md's spec-immutability rule, Track C does NOT edit
spec — it triages findings as:

- **Doc-fix** — research-stage doc needs a small update.
- **Spec-deviation** — code or research diverges from spec
  in a way that needs maintainer authorisation (ADR-style)
  before either spec or code is brought into alignment.
- **Spec hole** — spec doesn't yet describe substrate that
  ships in code; needs a future spec amendment when
  Phase 2/3 spec authoring resumes.
- **Forward-looking openness** — spec deliberately leaves a
  gap for future work; not drift.

## What this document is

A snapshot-dated **classification + triage report** for
spec alignment, paralleling Tracks A and B's structure.
Audit-only — no spec edits, no doc edits, no code changes.
**Recommendations are tiered**; subsequent narrower-concern
cycles either:
- Apply doc-side fixes to research-stage docs (small PRs).
- Surface spec-deviation candidates to the maintainer for
  ADR review (no code/doc change in this audit).
- Defer spec-hole filling until Phase 2/3 spec authoring.
- Acknowledge forward-looking openness without action.

## Methodology

Audit performed via direct read of all three spec docs +
sample-grep cross-checks 2026-05-07. Steps:

1. **Read `spec/overview.md`** in full (119 lines).
2. **Read `spec/tech-plan.md`** with focus on §6 (hotkeys),
   §7 (Claude Code roundtrip), §8-§10 (output / input /
   review-mode framework — superseded by
   event-and-output-framework.md).
3. **Read `spec/event-and-output-framework.md`** with focus
   on Part B (output framework), Part C (sub-stages
   8a-8f / 9a-9d), Part D (retrofit specifics).
4. **Cross-check vocabulary**: for each named entity in
   the research-stage docs, search spec for occurrences;
   classify match (✅) / partial (⚠) / missing (📋
   reserved or hole).
5. **Cross-check named substrate**: for each substrate
   component shipped in PRs #160-#169, search spec; mark
   alignment.
6. **Identify supersession / forward-looking openness**:
   spec documents itself as superseded in some places
   (event-and-output-framework supersedes tech-plan §8-§10);
   honour those.

The audit does NOT execute code or run tests. Pure static
inspection.

## Findings summary

**Spec alignment with research-stage docs (snapshot 2026-05-07):**

| Tag | Count | Meaning |
|---|---|---|
| ✅ aligned | ~22 | Spec claim matches research doc + current code. |
| ⚠ stale or holed | 7 | Spec doesn't yet describe substrate shipped post-2026-04 (PRs #160-#169). All are tech-plan.md holes; event-and-output-framework absorbs most via supersession. |
| ❌ contradiction | 0 | No findings where spec contradicts research docs. |
| 📋 forward-looking openness | 5 | Spec deliberately leaves gaps for Phase 2/3 (SessionModel, Pane Model, multi-line braille, spatial audio, AI summarisation). |

**Headline**: spec alignment is **good-to-excellent**. The
research-stage docs **extend** the spec rather than diverge
from it. Tech-plan.md is the doc most "behind" current code
because it pre-dates the substrate cycle (#160-#169). Event-
and-output-framework.md (authored 2026-05-04) is well
aligned.

**No spec contradictions found.** Recommendations focus on
holes + light vocabulary fixes.

## Per-document findings

### `spec/overview.md` (119 lines)

**Status overall**: ✅ aligned + 1 📋 forward-looking
openness.

**Coverage**: foundational architectural rationale. Five-
stage pipeline named at line 33
(`ConPtyHost → PipeReader → VtParser → SemanticMapper →
EventBus`); UIA text-attribute exposure foundational;
NAudio earcons + Velopack distribution.

**Vocabulary cross-check vs. research-stage docs:**

| Term | Spec status | Research-doc status | Verdict |
|---|---|---|---|
| Five-stage pipeline | ✅ named at line 33 | PIPELINE-NARRATIVE refines into 12 stages | ✅ Aligned (research extends spec — not drift) |
| UIA Notifications API | ✅ named at line 25 | INTERACTION-MODEL §5 mentions | ✅ Aligned |
| OSC 133 | ✅ named at line 69 (recommended) | SESSION-MODEL §3 (operationalises) | ✅ Aligned (spec reserves; SESSION-MODEL specifies) |
| Spinner re-render loop | ✅ named at line 71 | PIPELINE-NARRATIVE stage 6 (Spinner suppression) | ✅ Aligned |
| Color attribute (40008/40001) | ✅ named at line 25 | PR #163/#164 ship colour-detection earcon path | ✅ Aligned (foundational claim → shipped substrate) |
| SessionModel | ❌ not named (intentional) | SESSION-MODEL.md (forward-looking design) | 📋 Forward-looking openness — overview line 95 reserves "Terminal.Semantics, Terminal.EventBus … land in the stages that need them" |
| Shell Interaction Manager | ❌ not named | INTERACTION-MODEL §4 | 📋 Forward-looking — the SIM is an architectural framing layer above what overview.md describes |
| Pane Model | ❌ not named | PANE-MODEL.md (sketch) | 📋 Forward-looking — UI composition is Phase 2/3 territory |

**Key claims that anchor the research docs:**

- **Line 7** (NVDA diff lag + spinner loop): "*NVDA diffs
  cell grids with ~30ms lag, redrawn spinners trigger
  infinite speech loops, and Ink/React-based TUIs like
  Claude Code actively break raw-mode input assumptions.*"
  → Motivates PIPELINE-NARRATIVE's stage 6 (spinner
  suppression) + INTERACTION-MODEL's pathway-per-shell
  framing.
- **Line 25** (UIA color exposure): "*`GetAttributeValue`
  must return … `UIA_ForegroundColorAttributeId (40008)` …
  this product's single biggest differentiator.*" →
  Foundational for PR #163/#164 colour earcon path.
- **Line 69-72** (OSC 133): "*OSC 133 shell integration*" →
  Operationalised in SESSION-MODEL.md.

**Recommended actions for overview.md**: none. Document is
foundational + stable; research-stage docs extend it
appropriately.

### `spec/tech-plan.md` (1193 lines)

**Status overall**: 7 ⚠ holes + 4 ✅ aligned + 3 sections
explicitly superseded by event-and-output-framework.md.

**Coverage**: stage-by-stage implementation plan. Stages
0-7 cover shipped work (parser → screen → UIA → ConPTY →
Claude Code roundtrip). Stages 8-10 cover the output /
input / review-mode framework cycles — explicitly
superseded by event-and-output-framework.md per its Part C
section.

**Aligned sections:**

- **§6 Keyboard input contract** (lines 425-587): locks the
  AppReservedHotkeys table. Per Track A audit, the
  in-code list at `src/Views/TerminalView.cs:379-496` has
  12 entries. Spec's line 451-525 documents 6 shipped + 3
  future. Track A confirmed actual code matches. ✅
  Aligned.
- **§7 Claude Code roundtrip** (lines 643-885): hot-switch
  UX described at line 727. Maps to PIPELINE-NARRATIVE
  stage 4 + auxiliary `onModeBarrier`. ✅ Aligned.
- **§7.2 env-scrub** (PO-5): SECURITY.md tracks; not part
  of this audit's scope.
- **§4a / §4b / §5 / §5a Stages**: shipped substrate;
  matches code per Track A audit.

**Holes (7 ⚠ items)** — substrate shipped in PRs #160-#169
but tech-plan.md doesn't describe:

| # | Substrate | Shipped in | Hole detail |
|---|---|---|---|
| H1 | **Suffix-diff / EditDelta** | PR #166 | PIPELINE-NARRATIVE stage 8 (`computeRowSuffixDelta`); tech-plan does not describe sub-row diff semantics. |
| H2 | **BulkChangeThreshold** | PR #168 | PIPELINE-NARRATIVE stage 8b; tech-plan does not describe the bulk-change fallback heuristic. |
| H3 | **BackspacePolicy** | PR #168 | USER-SETTINGS.md atlas + PIPELINE-NARRATIVE; tech-plan does not describe the row-shrink handling parameter. |
| H4 | **ModeBarrierFlushPolicy** | PR #169 | PIPELINE-NARRATIVE auxiliary; tech-plan describes shell-switch at line 727 but not the explicit flush policy. |
| H5 | **Hot-switch baseline-seed** | PR #160 | Tech-plan describes hot-switch hotkeys but does NOT describe the baseline-seed reset logic. |
| H6 | **Per-pathway TOML selection** | PR #162 | event-and-output-framework.md §B.3.4 absorbs; tech-plan does not describe. |
| H7 | **Color-detection earcon path** | PR #163 + #164 hotfix | event-and-output-framework.md §B.3.4 absorbs (`SemanticEarconMap`); tech-plan §9 describes earcons but not colour-driven semantic mapping. |

All 7 are **holes**, not contradictions. Tech-plan was
authored before these substrate refinements landed; event-
and-output-framework.md absorbs most via the
SemanticCategory + Profile + Channel framework. The holes
will close naturally if a future spec authoring cycle
re-snapshots tech-plan.md against current code.

**Superseded sections** (explicitly):

- **§8 Interactive list detection** — superseded by
  event-and-output-framework.md §C.1 Stage 8e
  (SelectionDetector as a Profile producer). Spec text in
  tech-plan §8 should be marked "superseded; see
  event-and-output-framework.md §C.1" if/when the spec is
  next authored.
- **§9 Earcons + color announcement** — superseded by
  event-and-output-framework.md §B.4.2 + §C.1 Stage 8d
  (EarconChannel + EarconProfile). Same supersession note
  applies.
- **§10 Review mode + structured navigation** — superseded
  by event-and-output-framework.md §C.3 (review mode as
  first non-built-in consumer of the OutputEvent
  substrate).

**Recommended actions for tech-plan.md**: none in this
PR. Spec edits require ADR per CLAUDE.md. **Recommendation
for maintainer review**: when the spec is next authored
(plausibly after Phase 2 input framework cycle ships), a
single re-snapshot pass would close all 7 holes + add
explicit "superseded by …" notes to §8/§9/§10.

### `spec/event-and-output-framework.md` (1495 lines)

**Status overall**: ✅ aligned with current substrate +
4 📋 forward-looking openness.

**Coverage**: the post-Stage-7 substrate spec. Authored
2026-05-04; the closest spec to current substrate state.
Five parts: A (input routing), B (output framework), C
(sub-stages), D (retrofit specifics), E (out of scope +
verification + open questions).

**Aligned items:**

- **OutputEvent schema** (lines 642-730, B.2.2). 14 explicit
  + 1 future `Custom of string`. Matches
  `Terminal.Core.OutputEventTypes.SemanticCategory` per
  Track A audit.
- **Priority taxonomy** (lines 666-670):
  `Interrupt | Assertive | Polite | Background`. Matches
  `Terminal.Core.OutputEventTypes.Priority` exactly.
- **Profiles in v1**:
  - `StreamProfile` (line 824) → matches **shipped name**
    `PassThroughProfile` after the Track A drift fixup
    (PR #175). ⚠ **Vocabulary drift**: spec says
    "StreamProfile"; code says "PassThroughProfile". Track
    A's PR #175 fixed this in PIPELINE-NARRATIVE; spec
    still says StreamProfile. Documented as "drift candidate
    1" below.
  - `EarconProfile` (line 841) → ✅ matches shipped name.
- **v1 channels**:
  - `NvdaChannel` (line 917) — ✅ matches.
  - `EarconChannel` (line 954) — ✅ matches.
  - `FileLoggerChannel` (implicit; via §D.2 mapping) — ✅
    matches.
- **Threading + priority taxonomy** (§B.5, lines 990-1058)
  — ✅ aligned.
- **TOML schema sketches** (§B.3.4, lines 850-879) —
  partial alignment with current `Config.fs`. Newer
  `Config.fs` parameters (`bulk_change_threshold`,
  `backspace_policy`, `mode_barrier_flush_policy`) ship in
  PR #168; spec sketches a generic
  `[profile.<name>] params = {...}` shape that the actual
  TOML conforms to. ✅ Aligned at the schema-shape level.
- **Sub-stage breakdown 8a-8f / 9a-9d** (Part C, lines
  1143-1188) — first formal breakdown of sub-stages. Most
  shipped (8a / 8b / 8c / 8d already in code per shipped
  PRs); 8e (SelectionDetector / FormPathway) and 8f
  (per-shell profile mapping) are 📋 reserved /
  forward-looking.

**Forward-looking openness (4 📋 items, intentional gaps):**

- **SessionModel substrate** — not described; awaits
  SESSION-MODEL.md authoring (item 28; shipped 2026-05-06).
  Spec gap is intentional; SESSION-MODEL.md fills it.
- **Pane Model** — not described; PANE-MODEL.md (item 30,
  shipped 2026-05-07) is the new sister doc.
- **Spatial audio / ASIO** (lines 980-987) — explicitly
  reserved; `EarconAt of Earcon * Position3D`,
  `CompositeSink` named for Phase 3.
- **Per-pathway selection** (§E.1, lines 1300-1337,
  "Things deliberately out of scope") — explicitly
  reserved.

**Drift candidate** (1 ⚠ item):

- **D1: `StreamProfile` (spec) vs. `PassThroughProfile`
  (code)** — Track A's PR #175 renamed PIPELINE-NARRATIVE's
  references to `PassThroughProfile`. Spec at line 824
  still says "StreamProfile". This is **the only spec ⚠
  finding** in the entire audit. Triage: spec edit
  required (ADR-style maintainer authorisation per
  CLAUDE.md). Mechanical change; ~3-5 line edits in spec
  (line 824 + any in-text references).

**Recommended actions for event-and-output-framework.md**:

- **D1 (StreamProfile → PassThroughProfile rename)** —
  flag for maintainer authorisation. Once authorised, a
  small spec-edit PR mirrors the Track A doc-side fix
  pattern.
- All other findings: forward-looking openness; no action.

## Cross-cutting drift themes

Three themes recur across the per-doc findings:

### Theme 1: Vocabulary drift (mild)

- **`StreamProfile` → `PassThroughProfile`** rename
  (D1 above). Single occurrence in spec; resolvable in a
  small spec-edit PR after ADR.

No other vocabulary drift found. The research-stage docs
introduce *new* vocabulary (Pathway, SIM, three-component
model, etc.) that the spec hasn't yet absorbed — but this
is research extending spec, not drift.

### Theme 2: Tech-plan.md temporal lag

Tech-plan.md was authored before substrate cycle
PRs #160-#169 landed. It has 7 holes covering
suffix-diff, BulkChangeThreshold, BackspacePolicy,
ModeBarrierFlushPolicy, hot-switch baseline-seed,
per-pathway TOML selection, and color-detection earcon
path. None contradict current code; all need a future
spec authoring cycle to close.

**This is normal for active substrate development**. The
research-stage docs (PIPELINE-NARRATIVE, etc.) capture the
intermediate state until spec re-authoring catches up.

### Theme 3: Forward-looking openness is intentional

Spec deliberately leaves gaps for Phase 2/3:

- SessionModel substrate (now designed in SESSION-MODEL.md,
  awaits implementation).
- Pane Model (now sketched in PANE-MODEL.md, awaits
  implementation).
- AI summarisation pathway (Phase 3).
- Spatial audio + ASIO sinks (Phase 3).
- Per-content-type triggers (Phase 2/3).
- Cross-platform port (research, see strategic notes; not
  yet a backlog item with a doc).

These are NOT drift; they are explicit "deliberately out
of scope" boundaries. The research-stage docs reserve
names for them but defer implementation. Spec absorbs them
when Phase 2/3 work begins.

## Triage

Findings classified by required action:

### Tier 1 — Doc-fix (small follow-ups; no spec edit; no ADR needed)

None. Track A's PR #175 already covered the doc-side
vocabulary fixes in PIPELINE-NARRATIVE. The remaining ⚠
items in this audit are spec-side (require ADR) or
intentionally forward-looking.

### Tier 2 — Spec-deviation candidates (require maintainer ADR)

- **D1: `StreamProfile` → `PassThroughProfile`** rename in
  `spec/event-and-output-framework.md:824` (+ any in-text
  references). ~3-5 line edits. Maintainer authorises;
  small follow-up PR mirrors the rename.

### Tier 3 — Spec holes (defer to next spec authoring cycle)

The 7 tech-plan.md holes (H1-H7 above):
- H1: Suffix-diff / EditDelta
- H2: BulkChangeThreshold
- H3: BackspacePolicy
- H4: ModeBarrierFlushPolicy
- H5: Hot-switch baseline-seed
- H6: Per-pathway TOML selection
- H7: Color-detection earcon path

**Plus** explicit "superseded by event-and-output-framework.md
§…" notes for tech-plan §8 / §9 / §10.

These are best closed in a single re-snapshot pass when
spec authoring resumes (probably after Phase 2 input
framework cycle ships, when the input framework itself
needs a spec section). Maintainer ADR required.

### Tier 4 — Forward-looking openness (no action; document the openness)

- SessionModel substrate (overview.md line 95 reserves;
  SESSION-MODEL.md fills).
- Pane Model (PANE-MODEL.md sketches).
- Spatial audio / ASIO (event-and-output-framework lines
  980-987).
- Per-content-type triggers (Phase 2/3).
- AI summarisation pathway (Phase 3).

No action; the openness is correctly documented as
"deliberately out of scope" or "Phase 2/3" in spec.

## Recommendations

Tiered by priority + scope. Each item is a candidate for a
follow-up cycle.

### Immediate (small, low-risk)

- **D1 spec rename**: a 3-5 line spec-edit PR renaming
  `StreamProfile` → `PassThroughProfile` in
  `spec/event-and-output-framework.md` after maintainer
  ADR authorisation. Mirrors Track A's PR #175 pattern at
  the spec layer.

### Next-cycle (medium)

- **No code/test-extension cycles directly informed by
  Track C** — spec audit doesn't surface code or test
  gaps. Track B owns the test-extension queue.

### Substrate-implementation-gated

- **SessionModel substrate spec section** — when item 28
  implementation begins, spec gains a section describing
  OSC 133 + heuristic fallback + SessionTuple lifecycle.
- **Pane Model spec section** — when Pane abstraction
  implementation begins, spec gains a section describing
  the Pane / Pane Coordinator framework.
- **Phase 2 input framework spec section** — already
  partly in event-and-output-framework.md Part A; gains a
  full chapter when Phase 2 ships.

### Spec re-authoring cycle (deferred; coordinated)

A future "spec re-snapshot" cycle closes:
- The 7 tech-plan.md holes (H1-H7).
- Explicit supersession notes for tech-plan §8/§9/§10.
- Spec section absorbing PIPELINE-NARRATIVE / SESSION-MODEL
  / INTERACTION-MODEL / PANE-MODEL vocabulary into the
  canonical layer.

This cycle requires maintainer ADR for every change. Best
sequenced **after** Phase 2 substrate ships so the spec
re-snapshot covers a stable baseline.

## What this PR does NOT do

- **No spec edits.** Per CLAUDE.md, spec changes require
  ADR-style maintainer authorisation. This audit
  identifies; the fixes (when authorised) ship in
  separate PRs.
- **No research-stage doc edits.** Track A's PR #175
  already closed PIPELINE-NARRATIVE drift; this audit
  doesn't add to that loop.
- **No code changes.**
- **No test changes.**

## Cross-references

- [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md)
  — Track A; this doc mirrors its structure at the spec
  layer.
- [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) —
  Track B; this doc mirrors its structure at the spec
  layer.
- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — the
  research-stage doc cross-checked against spec.
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking
  substrate; spec gap noted.
- [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — the SIM
  framing; spec gap noted (intentional).
- [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition
  sketch; spec gap noted (intentional).
- [`spec/overview.md`](../spec/overview.md) — read-only
  audit subject (foundational architecture).
- [`spec/tech-plan.md`](../spec/tech-plan.md) — read-only
  audit subject (stage-by-stage plan).
- [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
  — read-only audit subject (post-Stage-7 substrate).
- [`CLAUDE.md`](../CLAUDE.md) — spec-immutability rule
  (canonical for ADR requirement).
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching +
  PR conventions.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Track C audit (Cycle 3) | Initial snapshot; 3 spec docs audited; ~22 ✅ + 7 ⚠ + 0 ❌ + 5 📋 forward-looking; 1 spec-deviation candidate (D1: StreamProfile rename) flagged for maintainer ADR; 7 tech-plan holes deferred to future spec re-authoring cycle. |

