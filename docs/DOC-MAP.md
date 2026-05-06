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
