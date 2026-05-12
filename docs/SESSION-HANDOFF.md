# Session handoff

A short brief for the next session (human contributor or Claude
Code). For anything historical that isn't in this doc, follow
the pointers in [§ Where to find detail](#where-to-find-detail).

This file aims to stay **under 200 lines**. If it starts to grow
past that, the older content should move to
`docs/archive/` rather than continue accumulating here. The
previous (multi-thousand-line) handoff is at
[`docs/archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md`](archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md)
and serves the decision-trail role this file used to overload.

## Current state (2026-05-12)

**Cycle 45c cleanup complete.** PRs #274–#278 merged 2026-05-12.
The pre-Cycle-45 substrate pipeline (`StreamPathway` /
`LinearTextStream` / `DisplayPathway` / `TuiPathway` /
`PathwaySelector`) is fully retired. `ContentHistory` +
`SpeechCursor` is the sole aural substrate.

**Validation gate in flight.** The maintainer is cutting a fresh
preview build and will walk the six new NVDA-matrix rows
(`45c-1` through `45c-6` in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md))
before tagging a release. Until that walk lands, treat the
post-Cycle-45c state as code-clean but UX-unconfirmed.

## Where we left off

`main` at `e51c23e` after the five Cycle-45c PRs (docs archive →
test prune → OSC 133 migration → Program.fs rewire → module
delete). Net change for the cycle: roughly **5,000+ LOC removed**.
Working tree clean; no in-flight branches; no pending fixups.

If the NVDA walk surfaces regressions, they land here as
post-Cycle-45c fixups rather than as a new cycle.

## Next stage

Candidate cycles, sourced from
[`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
(read that for the sequencing rationale + risks):

- **Cycle 45g** — `ShellPolicy` consolidation. Migrate
  `HeuristicPromptDetector` + `SelectionDetector`'s per-shell
  gates onto the `ShellPolicy` table introduced by Cycle 45f.
  Pure refactor (~200 LOC).
- **Cycle 45d** — Interactive review-cursor focus.
  `Enter` on a focused row dispatches per type (copy a prompt,
  select an item, etc.). ~150 LOC.
- **Semantic labels** — Add `Source: EntrySource` to every
  `ContentHistory.Entry`. Powers chunk-level navigation
  announces and the "inject past input" action. Foundational
  for several downstream items.
- **Spinner / red-tone fixes** — The pre-Cycle-45 spinner storm
  + false-positive red-tone earcons surfaced during Cycle 29b
  Claude dogfood. The old fixes were in `StreamPathway`;
  rewrite against ContentHistory.
- **Coalescer rename** — `Coalescer` no longer coalesces
  announce events (kept only for its `hashRow` / `hashRowContent`
  helpers that `CanonicalState` uses). Renaming would touch ~25
  sites; standalone refactor.

Pick one when ready. None of these block each other; the only
hard dependency is "semantic labels before chunk-level
navigation announces."

## Operational gotchas

Things a new session needs that aren't in
[`CLAUDE.md`](../CLAUDE.md) or
[`CONTRIBUTING.md`](../CONTRIBUTING.md):

- **No `dotnet` in the sandbox.** Read your edits twice; CI on
  Windows-latest is the only compile-and-test gate available.
- **Tag pushes return 403.** Branch pushes are fine; tag pushes
  are blocked by the local git proxy. Stage tag commands in
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) "Pending checkpoint
  tags" for the maintainer to sweep from their workstation.
- **Maintainer uses NVDA.** Don't suggest GUI dialog walks
  (System Properties → … chains, Task Manager chevrons, etc.).
  Surface keyboard- or shell-only equivalents instead.
- **CI failure log access is restricted.** The sandbox blocks
  `productionresultssa*.blob.core.windows.net` and
  `api.github.com`. When CI fails, ask the maintainer for the
  log slice rather than guessing from the diff.
- **Diagnostic-bundle chunking.** Don't ask for full
  `Ctrl+Shift+D` bundles unprompted — they can be multi-MB and
  crash iOS chat clients on paste. Use the menu items under
  `Diagnostics → Extract` to ask for targeted slices.

## Where to find detail

| If you need… | Open |
|---|---|
| Cycle-by-cycle trail | [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` |
| Active strategic plan + roadmap | [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md) |
| Pre-Cycle-45 decision history | [`docs/archive/pre-cycle-45/`](archive/pre-cycle-45/) (README inside) |
| Substrate / architecture | [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) + [`docs/adr/0001-substrate-channel-dichotomy.md`](adr/0001-substrate-channel-dichotomy.md) |
| Spec | [`spec/tech-plan.md`](../spec/tech-plan.md), [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) |
| Module map | [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) |
| Manual-test matrix | [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) |
| Doc routing index | [`docs/DOC-MAP.md`](DOC-MAP.md) |
| Per-audience entry points | [`README.md`](../README.md) "Quick links" |
| Sandbox / runtime rules (for Claude) | [`CLAUDE.md`](../CLAUDE.md) |
| PR shape, F# gotchas, accessibility rules | [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
