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
| [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) | Next Claude Code session | Session-to-session continuity | Mutable state: "Where we left off", in-flight branches, pre-digested implementation sketches for the next stage. |
| [`spec/overview.md`](../spec/overview.md), [`spec/tech-plan.md`](../spec/tech-plan.md) | Architecture review | When changing design | Immutable spec: external research and stage-by-stage plan. ADR-style authorisation required for edits. |
| [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) | Architecture review (post-Stage-7 substrate) | When implementing sub-stages 8a-8f / 9a-9d, or when reasoning about event routing / output channels / profiles / the InputSource and OutputEvent schemas | Substrate spec for the post-Stage-7 framework cycles. Supersedes `spec/tech-plan.md` §8 / §9 in-place; reframes §10 as the first non-built-in framework consumer. Answers MAY-4.md's consolidated questions with v1 commitments (rationale + tradeoffs) on RawInput envelope, Intent layer, dispatcher, OutputEvent + Profile + Channel, threading + priority taxonomy, TOML schema. |
| [`docs/PROJECT-PLAN-YYYY-MM.md`](PROJECT-PLAN-2026-05.md) | Strategic planning | When planning a cycle | Dated cycle plans (e.g. May-2026 cleanup → Stage 7 → framework cycles). Status-as-of-date when stale. |
| [`docs/HISTORICAL-CONTEXT-*.md`](.) | Debugging archived patterns | Backup reference only | NOT primary handoff. Historical decisions retained for archaeology. |
| [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) | First-time code navigator | Code orientation | Module-by-module map of the codebase. |
| [`docs/ROADMAP.md`](ROADMAP.md) | Quick "what's next" scan | High-level glance | Stage list + ship status. |
| [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) | Maintainer at release / rollback | Release cut or rollback | Stable-baseline tags + queued tag pushes (sandbox can't push tags). |
| [`docs/CONPTY-NOTES.md`](CONPTY-NOTES.md) | Platform-specific debug | ConPTY-issue triage | Win32 ConPTY quirks + workarounds. |
| [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) | Contributor adding a hardcoded constant | When introducing config-shaped values | Catalog of values that should eventually be user-configurable. |
| [`docs/PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) | Anyone reasoning about where new behaviour lives | When adding a feature OR when navigating the substrate | Shared vocabulary for the 12-stage output pipeline; event taxonomy mapping every UI event to its pathway; substrate inventory naming what exists vs. what's reserved for future research-stage items; two end-to-end traces (keystroke + output byte); seams catalogue for future capabilities. Authoritative for stage / pathway / event-type names. |
| [`docs/SESSION-MODEL.md`](SESSION-MODEL.md) | Anyone designing or implementing the SessionModel substrate | Before implementation cycle; when adding a SessionModel-aware pathway | Forward-looking design for the SessionModel substrate (item 28). OSC 133 protocol; per-shell heuristic fallback (cmd / PowerShell / Claude Code); data model (SessionModel + SessionTuple + ActiveSessionTuple); pipeline integration (Stage 3.5 insertion); pathway-by-pathway integration (StreamPathway, TuiPathway, ReplPathway, FormPathway, ClaudeCodePathway, AiInterpretedPathway, SessionConsumer); persistence story (memory-only / session-log / always); query API; 7-tier implementation precedence; 8 open questions for maintainer review. |
| [`docs/AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) | Anyone auditing the substrate against the doc | When validating that PIPELINE-NARRATIVE matches code | Track A of the comprehensive audit phase. Validates every named entity in PIPELINE-NARRATIVE.md against code; tags findings ✅/⚠/❌. 80+ entities verified across pipeline stages, pathway taxonomy, event taxonomy, substrate components, substrate gaps, vocabulary glossary. Snapshot 2026-05-06: 81 ✅, 8 ⚠ (all doc-side drift, no code changes needed), 0 ❌. |
| [`docs/AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) | Anyone reasoning about test coverage / fragility | When proposing new tests OR when substrate semantics change OR when investigating test brittleness | Track B of the comprehensive audit phase. Classifies all 531 tests across 27 test files (algorithm-correctness / output-shape / interaction / mixed); per-file ✅/⚠/❌ status; substrate coverage gap analysis cross-referencing the 5 research-stage docs; 4-tier recommendations for follow-up cycles (~25-37 property test candidates; ~5 diagnostic battery extensions; fixture corpus structure; SessionModel/Pane/echo-correlation test reservations). Snapshot 2026-05-07: 19 ✅, 8 ⚠ (output-shape clusters in StreamPathway/EarconProfile/FileLoggerChannel; low coverage in ConPtyHost), 0 ❌. |
| [`docs/AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) | Anyone reasoning about spec/ vs research-doc alignment | Before authorising a spec edit OR when reasoning about whether a substrate refinement needs spec amendment | Track C of the comprehensive audit phase. Cross-checks spec/overview.md (119 lines), spec/tech-plan.md (1193 lines), spec/event-and-output-framework.md (1495 lines) against the 5 research-stage docs + current code. Snapshot 2026-05-07: ~22 ✅ aligned, 7 ⚠ (all tech-plan.md holes covering substrate shipped in PRs #160-#169), 0 ❌ contradictions, 5 📋 forward-looking openness (SessionModel, Pane Model, spatial audio, AI summarisation, per-content-type triggers — intentional gaps). Triages findings as doc-fix (none; Track A closed those) / spec-deviation requiring ADR (1 item: StreamProfile → PassThroughProfile rename) / spec hole (7 tech-plan items deferred to future spec re-authoring cycle) / forward-looking openness (no action). Per CLAUDE.md spec-immutability rule, this audit identifies but does NOT edit spec. |
| [`docs/AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md) | Anyone reasoning about parameter-atlas vs code-constant alignment | Before adding a new parameter to USER-SETTINGS.md OR when investigating atlas drift | Track D of the comprehensive audit phase. Cross-checks USER-SETTINGS.md (1547-line parameter atlas) against code constants in Config.fs / StreamPathway.fs / FileLogger.fs / EarconPalette.fs / ConPtyHost.fs / Program.fs. Snapshot 2026-05-07: 17 ✅ aligned (8 shipped TOML parameters + earcon palette + heartbeat + retention), 5 ⚠ (3 stale-section findings from PR #167 atlas authored before PR #168 shipped the parameters; 2 low-priority orphans for ChannelCapacity and ConPTY buffer size), 0 ❌ contradictions, 11 📋 forward-looking openness (Phase 2 echo correlation / cursor announcement / ActivityId routing / earcon palette tuning / keybindings TOML / etc.). Headline: alignment is good; PR #167/#168 staleness is a documentation-process gap not architectural. Tier 1 follow-up PR recommended (~50-100 LOC mechanical doc edits to USER-SETTINGS.md). |
| [`docs/AUDIT-DOC-CURRENCY.md`](AUDIT-DOC-CURRENCY.md) | Anyone reasoning about doc currency / staleness | Before refreshing developer-reference docs OR when investigating "where we left off" drift | Track E of the comprehensive audit phase. Inventories all 33 docs (27 in docs/ + 5 root + 1 research seed) for currency markers, cross-reference health, and "Where we left off" accuracy. Snapshot 2026-05-07: 26 ✅ aligned, 5 ⚠ stale (E1: ROADMAP Stage 7 marked "pending" — shipped 2026-05-03; E2-E4: ARCHITECTURE.md currency note + headers reference Stage 3b baseline; E6: SESSION-HANDOFF "Where we left off" doesn't reflect substrate cycle PRs #160-#178 or audit phase), 0 ❌ contradictions, 2 📋 forward-looking openness (RFC-OUTPUT-FRAMEWORK / RFC-INPUT-FRAMEWORK referenced as planned future deliverables). Cross-reference health 100% (zero broken links spot-checked). DOC-MAP consistency 100% (no orphans). Headline: doc currency is good; 5 ⚠ findings cluster on substrate-cycle refresh that hasn't reached older developer-reference docs. 1 open question (E5: PROJECT-PLAN-2026-05.md status pointer — Option A small status-header update vs. Option B successor plan doc). Tier 1 follow-up PR + separate ARCHITECTURE refresh cycle recommended. |
| [`docs/AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md) | Anyone reasoning about strategic-backlog status | When picking up the next implementation cycle OR when reorganising the backlog | Track F of the comprehensive audit phase — **FINAL audit-track deliverable**. Walks all 30 numbered strategic backlog items + 6 audit-track sub-items. Snapshot 2026-05-07: 11 ✅ shipped (items 1, 3, 19, 24, 26, 29 + audit Tracks A-E), 5 ✅+ partially shipped (items 5, 23, 25, 28, 30 — research stage shipped; implementation pending), 11 📋 pending (items 2, 4, 7, 8, 9, 10, 11, 12, 13, 15-18, 20, 22), 3 ⏸ deferred (items 14 in-app menu DEFER per maintainer; 21 cross-platform Avalonia port; intentional historicals), 2 🔄 superseded (items 6 8d.2 re-investigation moot after PR #163/#164; 27 scrollback reframed as downstream of SessionModel), 0 ❓ orphaned. Surfaces ready-to-pick-up list (Tier 1 audit-fixup queue ~5 PRs; Tier 2 test extensions per Track B; Tier 3 backlog refinements; Tier 4 substrate-implementation gates). Catalogs audit-fixup queue (~350-700 LOC across 4-5 follow-up PRs). Recommends 4 reorganization items (R1-R4). With this PR's merge the audit phase formally closes; substrate is healthy. |
| [`docs/CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) | Anyone reasoning about user-introspectable + user-customizable pipeline architecture | When designing pipeline stages OR when adding user-facing customization features | Snapshot 2026-05-07. Forward-looking research-stage doc capturing the maintainer's 2026-05-07 architectural directive: every pipeline operation should be user-introspectable + user-customizable. Names the 5-point principle (named/addressable; swappable; inspectable per-output; user-authorable as override rules; context-keyed). Two illustrative use cases: output-side display correction (e.g. fix a misdisplayed list inline via dropdown); input-side autocomplete with ordered/toggleable sources. Specifies what existing + future substrate must NOT preclude (alternatives registry per stage; per-output trace capture; override-rule persistence + context-keying engine; Pipeline Inspector pane; authoring UX). Sketches pipeline-trace data model + override-rule data model + introspection UI. 6 substrate gaps (all forward-looking; no current implementation). 7 open questions surfaced for maintainer. Sister doc to PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL / PANE-MODEL at the user-customization layer. Item 31 in strategic backlog. |
| [`docs/INTERACTION-MODEL.md`](INTERACTION-MODEL.md) | Anyone reasoning about pty-speak's higher-level architecture | When deciding "where does this feature go?" at the architectural layer | Snapshot 2026-05-07. Names the **Shell Interaction Manager (SIM)** as the conceptual abstraction owning the bidirectional shell-program conversation. Defines the **three-component model** (Input Composition Surface / Active Output / Historical Document) for how the SIM organises interaction internally. Frames pty-speak's data shape as a **structured computational document** (Jupyter / Wolfram analogy: where it transfers + where it doesn't). Specifies the **interactive element taxonomy** mapping every `SemanticCategory` placeholder to triggers / producers / consumers / status. Sister doc to PIPELINE-NARRATIVE (operational lens) + SESSION-MODEL (history lens); INTERACTION-MODEL is the architectural-framing lens. Authoritative for SIM responsibilities, three-component vocabulary, structured-document framing, interactive-element categories, and per-question doc-routing matrix. |
| [`docs/PANE-MODEL.md`](PANE-MODEL.md) | Anyone reasoning about UI composition / multi-pane workspace | When proposing a new pane (file tree / AI assistance / etc.) or when designing the workspace framework | Snapshot 2026-05-07. Forward-looking sketch (deliberately briefer than INTERACTION-MODEL — sketch, not full spec). Names the **Pane** abstraction + **Pane Coordinator** orchestrator + **Workspace** container. Defines a **six-concern pane contract** (identity / content source / rendering / accessibility surface / input handling / lifecycle). Catalogs five pane types: shell ✅ (today's app, owned by SIM); file tree, cherry-picked I/O pairs from SessionModel, language docs, AI assistance — all 📋 reserved. Sketches three coordination protocols (pane → shell action; shell → pane state; pane ↔ pane). Names six accessibility hard problems (focus routing, NVDA review cursor, ActivityId scoping, pane-switch announcement, per-pane UIA pattern sets, compounded caret/UIA tension). Catalogs six substrate gaps + five maintainer-pending open questions. Sister doc to INTERACTION-MODEL at the UI-composition layer. |
| [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) | Maintainer cutting a release | Manual NVDA pass | Stage-by-stage NVDA validation matrix. |
| [`docs/STAGE-N-ISSUES.md`](STAGE-7-ISSUES.md) | Framework-cycle research phases | Design input for the cycle | Inventory of gaps surfaced during a stage's NVDA validation; framework-taxonomy categorised. |
| [`docs/ACCESSIBILITY-INTERACTION-MODEL.md`](ACCESSIBILITY-INTERACTION-MODEL.md) | Contributor reasoning about caret / focus / UIA tension | When designing review-mode or input-encoding work | Maintainer-requested skeleton mapping the design space for blind-developer terminal interaction (caret tension, modal-dialog seam, NVDA-notification round-trips). Technical depth, not philosophical; the maintainer flesh-outs specifics over time. |
| [`docs/research/`](research/) | Framework-cycle research authors | Pre-cycle prior-art + tradeoff seeds | Maintainer-authored or Claude-authored research that informs cycle architecture proposals. NOT prescriptive — gathers prior art, articulates tradeoffs, surfaces questions. The `docs/research/MAY-4.md` seed (2026-05-04) is the first inhabitant. |
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
4. [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) — cycle plan
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

1. [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) — current
   strategic plan
2. [`docs/ROADMAP.md`](ROADMAP.md) — high-level stage list
3. [`docs/STAGE-7-ISSUES.md`](STAGE-7-ISSUES.md) — design input for
   Output / Input framework cycles
4. [`docs/research/MAY-4.md`](research/MAY-4.md) — maintainer-authored
   prior-art and tradeoff seed for the three Output / Input
   framework concerns (universal event routing, output framework,
   navigable streaming response queue) plus a linguistic-design
   rubric. Not prescriptive; the consolidated questions list at
   the bottom is the natural starting point for proposal-phase
   conversation
5. [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
   — substrate spec answering MAY-4.md's consolidated questions
   with v1 commitments. The active source for sub-stage 8a-8f /
   9a-9d sequencing
6. Open issues in GitHub — feature backlog

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
