# DOC-MAP.md — Which file should I open?

This is the canonical "which doc owns what" index for `pty-speak`.
Each doc has **exactly one audience and one stage of contribution**;
cross-references go between them rather than content duplicating
into them.

The audit that produced this file (Stage 7-followup PR-L,
2026-05-03 maintainer authorisation; pre-designed in
[`SESSION-HANDOFF.md`](SESSION-HANDOFF.md)
"Queued before Output framework cycle starts") collapsed
overlap between `CLAUDE.md`, `CONTRIBUTING.md`, and
`SESSION-HANDOFF.md` so each duplicated rule lives in exactly one
place. The other docs already had clean ownership; they're listed
here for completeness and to give every audience a single
navigation anchor.

## The table

| Doc | Audience | When to read | One-line purpose |
|---|---|---|---|
| [`README.md`](../README.md) | Anyone hitting the GitHub repo | First contact | Project pitch + status + per-audience routing into the rest of the docs. Defers author bio + values frame to `docs/PROJECT-CONTEXT.md` and end-user install steps to `docs/INSTALL.md` so the README stays a tight orientation. |
| [`docs/PROJECT-CONTEXT.md`](PROJECT-CONTEXT.md) | Human reader wanting wider context | After README, when curious about author + values | Author bio (Dr. Kyle Keane, Bristol, MIT history) + the workarounds Kyle uses to make this work in practice + WHO ICF values frame; the human-context companion to `CLAUDE.md`'s Claude-runtime layer. |
| [`docs/INSTALL.md`](INSTALL.md) | End user wanting to install the latest preview | Before first run | Step-by-step download + install + update flow with the SmartScreen workaround for unsigned previews. References `scripts/install-latest-preview.ps1` for the scripted alternative and `docs/BUILD.md` for build-from-source. |
| [`CLAUDE.md`](../CLAUDE.md) | Claude Code agents | Every session start (auto-loaded) | Claude-runtime-specific rules (sandbox quirks, MCP behaviour, ask-for-CI-logs, webhook auto-subscribe); indexes the canonical rules in `CONTRIBUTING.md`. |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | Human contributors opening PRs | When opening a PR | Canonical home for: PR shape, branch naming, fixup-commit rhythm, F# / .NET 9 gotchas, accessibility non-negotiables, P/Invoke conventions. |
| [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) | Next Claude Code session (or human picking up the work) | Session-to-session continuity | TLDR brief (≤200 lines): current state, where we left off, next-stage candidates, sandbox gotchas, pointers out. Stays short by the §"Archiving stale onboarding narrative" discipline — closed-cycle detail moves to CHANGELOG + `docs/archive/`, not accumulated here. |
| [`docs/CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md) | Next session executing the Cycle 51 IOCell pivot | Before writing any Cycle 51 code | Turn-by-turn execution guide for PR-W → PR-Z: exact `file:line` anchors, the rejected classification-by-`EntrySource` thesis (do not re-introduce), the dogfood-bundle evidence, F# gotchas, operational protocol. Companion to ADR 0004 (decisions) + SESSION-HANDOFF (bird's-eye). PR-Z marks it HISTORICAL → `docs/archive/`. |
| [`spec/overview.md`](../spec/overview.md), [`spec/tech-plan.md`](../spec/tech-plan.md) | Architecture review | When changing design | Immutable spec: external research and stage-by-stage plan. ADR-style authorisation required for edits. |
| [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) | Architecture review (post-Stage-7 substrate) | When implementing sub-stages 8a-8f / 9a-9d, or when reasoning about event routing / output channels / profiles / the InputSource and OutputEvent schemas | Substrate spec for the post-Stage-7 framework cycles. Supersedes `spec/tech-plan.md` §8 / §9 in-place; reframes §10 as the first non-built-in framework consumer. Answers MAY-4.md's consolidated questions with v1 commitments (rationale + tradeoffs) on RawInput envelope, Intent layer, dispatcher, OutputEvent + Profile + Channel, threading + priority taxonomy, TOML schema. |
| [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md) | Strategic planning | When planning a cycle (current) | Snapshot 2026-05-12 (post-Cycle-45c). Captures the ContentHistory/SpeechCursor substrate consolidation + candidate next cycles (45g, 45d, semantic-labels, etc.). Predecessor revisions archived under `docs/archive/pre-cycle-45/` per Track E E5 dated-snapshot discipline. |
| [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) | First-time code navigator | Code orientation | Module-by-module map of the codebase. |
| [`docs/ROADMAP.md`](ROADMAP.md) | Quick "what's next" scan | High-level glance | Stage list + ship status. |
| [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) | Maintainer at release / rollback | Release cut or rollback | Stable-baseline tags + queued tag pushes (sandbox can't push tags). |
| [`docs/CONPTY-NOTES.md`](CONPTY-NOTES.md) | Platform-specific debug | ConPTY-issue triage | Win32 ConPTY quirks + workarounds. |
| [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) | Contributor adding a hardcoded constant | When introducing config-shaped values | Catalog of values that should eventually be user-configurable. |
| [`docs/SESSION-MODEL.md`](SESSION-MODEL.md) | Anyone designing or implementing the SessionModel substrate | Before implementation cycle; when adding a SessionModel-aware pathway | Forward-looking design for the SessionModel substrate (item 28). OSC 133 protocol; per-shell heuristic fallback (cmd / PowerShell / Claude Code); data model (SessionModel + SessionTuple + ActiveSessionTuple); pipeline integration (Stage 3.5 insertion); pathway-by-pathway integration (StreamPathway, TuiPathway, ReplPathway, FormPathway, ClaudeCodePathway, AiInterpretedPathway, SessionConsumer); persistence story (memory-only / session-log / always); query API; 7-tier implementation precedence; 8 open questions for maintainer review. |
| [`docs/archive/pre-cycle-45/`](archive/pre-cycle-45/) | Decision-history archaeology | When tracing pre-Cycle-45 substrate-pipeline decisions | Archive of the pre-Cycle-45 narrative layer: predecessor PROJECT-PLAN revisions, the 6-track May-2026 audit, PIPELINE-NARRATIVE.md (12-stage pipeline vocabulary; superseded by spec), STAGE-7-ISSUES gap inventory, HISTORICAL-CONTEXT-2026-05, RFC 0001 (LinearTextStream substrate spec; substrate replaced by ContentHistory in Cycle 45), and 3 research seeds. See `archive/pre-cycle-45/README.md` for the per-file rationale. |
| [`docs/CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) | Anyone reasoning about user-introspectable + user-customizable pipeline architecture | When designing pipeline stages OR when adding user-facing customization features | Snapshot 2026-05-07. Forward-looking research-stage doc capturing the maintainer's 2026-05-07 architectural directive: every pipeline operation should be user-introspectable + user-customizable. Names the 5-point principle (named/addressable; swappable; inspectable per-output; user-authorable as override rules; context-keyed). Two illustrative use cases: output-side display correction (e.g. fix a misdisplayed list inline via dropdown); input-side autocomplete with ordered/toggleable sources. Specifies what existing + future substrate must NOT preclude (alternatives registry per stage; per-output trace capture; override-rule persistence + context-keying engine; Pipeline Inspector pane; authoring UX). Sketches pipeline-trace data model + override-rule data model + introspection UI. 6 substrate gaps (all forward-looking; no current implementation). 7 open questions surfaced for maintainer. Sister doc to PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL / PANE-MODEL at the user-customization layer. Item 31 in strategic backlog. |
| [`docs/INTERACTION-MODEL.md`](INTERACTION-MODEL.md) | Anyone reasoning about pty-speak's higher-level architecture | When deciding "where does this feature go?" at the architectural layer | Snapshot 2026-05-07. Names the **Shell Interaction Manager (SIM)** as the conceptual abstraction owning the bidirectional shell-program conversation. Defines the **three-component model** (Input Composition Surface / Active Output / Historical Document) for how the SIM organises interaction internally. Frames pty-speak's data shape as a **structured computational document** (Jupyter / Wolfram analogy: where it transfers + where it doesn't). Specifies the **interactive element taxonomy** mapping every `SemanticCategory` placeholder to triggers / producers / consumers / status. Sister doc to PIPELINE-NARRATIVE (operational lens) + SESSION-MODEL (history lens); INTERACTION-MODEL is the architectural-framing lens. Authoritative for SIM responsibilities, three-component vocabulary, structured-document framing, interactive-element categories, and per-question doc-routing matrix. |
| [`docs/PANE-MODEL.md`](PANE-MODEL.md) | Anyone reasoning about UI composition / multi-pane workspace | When proposing a new pane (file tree / AI assistance / etc.) or when designing the workspace framework | Snapshot 2026-05-07. Forward-looking sketch (deliberately briefer than INTERACTION-MODEL — sketch, not full spec). Names the **Pane** abstraction + **Pane Coordinator** orchestrator + **Workspace** container. Defines a **six-concern pane contract** (identity / content source / rendering / accessibility surface / input handling / lifecycle). Catalogs five pane types: shell ✅ (today's app, owned by SIM); file tree, cherry-picked I/O pairs from SessionModel, language docs, AI assistance — all 📋 reserved. Sketches three coordination protocols (pane → shell action; shell → pane state; pane ↔ pane). Names six accessibility hard problems (focus routing, NVDA review cursor, ActivityId scoping, pane-switch announcement, per-pane UIA pattern sets, compounded caret/UIA tension). Catalogs six substrate gaps + five maintainer-pending open questions. Sister doc to INTERACTION-MODEL at the UI-composition layer. |
| [`docs/CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) | Anyone reasoning about cross-thread communication patterns | When introducing a new boundary (channel / event / direct call decision) OR when reviewing for consistency | Snapshot 2026-05-08. Formalises the maintainer's 2026-05-08 channel-based-communication architectural principle (applied concretely in Cycle 17 / PR #192). Inventories 3 production channels (`pumpChannel`, `ConPtyHost.Stdout`, `FileLogger.channel`) + 3 F# Events (`screen.ModeChanged` / `Bell` / `PromptBoundary`) bridged via `Event.Add → Channel.TryWrite`. Defines a 3-question decision framework + 4-bucket categorisation (Channel / Event / direct call / mutable-plus-lock). Catalogs 5 anti-patterns. Names 3 future channel candidates: input-keystroke (Phase 2 input framework), persistence-flush (Tier 2), AI-summarisation (Tier 3). 5 open questions. Sister doc to PIPELINE-NARRATIVE / INTERACTION-MODEL / SESSION-MODEL / PANE-MODEL / CUSTOMIZATION-MODEL — informs Phase 2 / Tier 2 / Tier 3 implementation cycles before they consume the principle. Item 32 in strategic backlog. |
| [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) | Maintainer cutting a release | Manual NVDA pass | Stage-by-stage NVDA validation matrix. |
| [`docs/ACCESSIBILITY-INTERACTION-MODEL.md`](ACCESSIBILITY-INTERACTION-MODEL.md) | Contributor reasoning about caret / focus / UIA tension | When designing review-mode or input-encoding work | Maintainer-requested skeleton mapping the design space for blind-developer terminal interaction (caret tension, modal-dialog seam, NVDA-notification round-trips). Technical depth, not philosophical; the maintainer flesh-outs specifics over time. |
| [`docs/LOGGING.md`](LOGGING.md) | Anyone reasoning about the file logger | When tuning logs / triaging | What is and isn't logged; FileLogger architecture; log-rotation policy. |
| [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) | Maintainer cutting a release | Release time | Velopack release workflow + GitHub Releases UI sequence. |
| [`docs/BUILD.md`](BUILD.md) | Local-build first-timer | Setting up a dev environment | `dotnet build` / `dotnet test` recipe. |
| [`CHANGELOG.md`](../CHANGELOG.md) | Release history | Every PR + release cut | Keep-A-Changelog format, `[Unreleased]` accumulator. |
| [`SECURITY.md`](../SECURITY.md) | Threat-model review | Security questions | PO/TC/SR row-by-row threat model + mitigation status. |

## Per-audience entry points

### "I'm Claude Code, starting a new session"

1. [`CLAUDE.md`](../CLAUDE.md) (auto-loaded; read it first)
2. [`README.md`](../README.md) — what the project is + shipped stages
3. [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) — "Where we left off"
4. [`docs/PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-12.md) — current cycle plan (2026-05-09 successor)
5. [`spec/tech-plan.md`](../spec/tech-plan.md) §N for the active stage
6. [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
   when the active stage is in the framework cycles (sub-stages 8a-8f or 9a-9d) or Stage 10
7. [`CONTRIBUTING.md`](../CONTRIBUTING.md) for canonical PR + F# rules

### "I'm a human contributor opening a PR"

1. [`README.md`](../README.md) — orientation
2. [`CONTRIBUTING.md`](../CONTRIBUTING.md) — PR shape, branch naming,
   commit conventions, F# / .NET 9 gotchas, accessibility
   non-negotiables, P/Invoke conventions
3. [`docs/BUILD.md`](BUILD.md) — local dev setup
4. [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) — code orientation
5. [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) — NVDA
   acceptance criteria for your change
6. [`spec/tech-plan.md`](../spec/tech-plan.md) — what the design says

### "I'm the maintainer cutting a release"

1. [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) — Velopack flow
2. [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) —
   manual NVDA pass for the stages in this preview
3. [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — pending tag pushes
4. [`CHANGELOG.md`](../CHANGELOG.md) — promote `[Unreleased]` to
   the new version section

### "I'm doing security review"

1. [`SECURITY.md`](../SECURITY.md) — PO/TC/SR row table
2. [`spec/tech-plan.md`](../spec/tech-plan.md) §7.2 — env-scrub
3. `tests/Tests.Unit/EnvBlockTests.fs` — security pinning

### "I'm planning the next cycle"

1. [`docs/PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-12.md) — current
   strategic plan (2026-05-09 successor revision); predecessor
   revisions archived under
   [`docs/archive/pre-cycle-45/`](archive/pre-cycle-45/)
2. [`docs/ROADMAP.md`](ROADMAP.md) — high-level stage list
3. [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
   — substrate spec, the canonical source for event routing /
   output channels / profiles
4. [`docs/archive/pre-cycle-45/`](archive/pre-cycle-45/) — when
   tracing pre-Cycle-45 substrate decisions (audits, RFC 0001,
   PIPELINE-NARRATIVE, STAGE-7-ISSUES, research seeds). Not
   primary handoff
5. Open issues in GitHub — feature backlog

## Maintenance discipline

Adding a new doc to the repo means:

1. Adding it to the **table above** with audience, when-to-read,
   and one-line purpose.
2. Listing it in the appropriate **per-audience entry-point** list
   if its audience overlaps with an existing one.
3. Confirming none of its content duplicates an existing doc — if
   it does, either consolidate or add a "see X" pointer in the
   smaller doc.

Removing or merging docs requires updating both the table and the
entry points, plus any cross-references in the docs themselves.

The duplication audit that produced this file is the precedent —
content drift compounds quickly when each session adds material to
"the doc that feels relevant" without checking which doc owns the
topic.

## Archiving stale onboarding narrative

**Problem this solves**: the living onboarding docs
([`SESSION-HANDOFF.md`](SESSION-HANDOFF.md), the `CLAUDE.md`
§"Current sequencing" block, and any in-flight `CYCLE-N-*.md`
plan/playbook) accumulate per-cycle detail. A new contributor
(human or Claude) reading a handoff that still narrates a
three-cycles-old PR sequence as "current" wastes time
reconstructing what is actually true now. Onboarding threads
must describe the **present**, not the archaeology.

**The rule**: a doc whose job is "where are we now / start
here" carries only the *current* cycle in narrative form.
The moment a cycle closes, its blow-by-blow detail moves to a
durable record and the living doc keeps a ≤2-line pointer.

**Durable records (where closed-cycle detail lives)** — these
are append-only and never trimmed:

- `CHANGELOG.md` `[Unreleased]` / released sections — the
  canonical per-PR ship record.
- `git log` — the commit-level truth.
- The cycle's own `CYCLE-N-*.md` plan/playbook, marked
  HISTORICAL and moved to `docs/archive/` by that cycle's
  closure audit.
- ADRs — the decision record; closure flips
  Proposed → Accepted / Implemented rather than deleting.

**The archive procedure** (run as part of every
CLAUDE.md §"Cycle closure audit", or whenever an onboarding
doc exceeds its size budget):

1. **Identify the stale span** — any section of a living
   onboarding doc that narrates a *closed* cycle in more than
   a 2-line summary (per-PR tables, validation-gate
   checklists, "next: PR-X" transitional language).
2. **Confirm it is preserved elsewhere** — the same facts
   must already be in CHANGELOG / git / the cycle's archived
   plan / an ADR. Onboarding docs hold *no unique history*;
   if a fact lives only there, move it to the right durable
   record first, then trim.
3. **Replace with a pointer** — collapse the span to ≤2 lines:
   "Cycle N shipped (<PRs/commits>); detail in CHANGELOG +
   `docs/archive/CYCLE-N-*.md`." Link the durable record.
4. **Move (don't delete) cycle plans/playbooks** — when a
   cycle closes, its `CYCLE-N-PLAYBOOK.md` / `CYCLE-N-PLAN.md`
   gets a HISTORICAL banner at the top (one line: "Historical
   — Cycle N shipped <date>; see CHANGELOG. Kept for the
   planning↔shipped delta.") and moves to `docs/archive/`.
   Update this DOC-MAP table + every cross-reference.
5. **Size budgets** (enforced, not aspirational):
   `SESSION-HANDOFF.md` ≤ 200 lines; `CLAUDE.md` §"Current
   sequencing" ≤ one cycle's worth of bullets. Exceeding the
   budget is the trigger to run steps 1–4 even mid-cycle.

**Precedent**: `docs/archive/pre-cycle-45/` (the multi-
thousand-line pre-Cycle-45 handoff + superseded plans),
`CYCLE-49-PLAN.md` (HISTORICAL-bannered), and the Cycle 46
retro (`CYCLE-46-NEXT-STEPS.md`) which is the cautionary
tale: transitional "PR-D is next" language left un-archived
forced later sessions to reverse-engineer the truth. The
cost of NOT archiving is paid by every subsequent
onboarding read.
