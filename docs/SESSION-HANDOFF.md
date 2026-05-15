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

## Current state (2026-05-15)

> **NEW SESSION START HERE → [`CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md).**
> That doc is the turn-by-turn execution guide (exact
> `file:line` anchors, the rejected thesis you must not
> re-introduce, the bundle evidence). This section is just
> the bird's-eye; the playbook is the map.

- **`main` HEAD = `6b3ff6a`.** Working tree clean; no
  in-flight code branches; no pending fixups. The spike
  branch `spike/cycle51-iocell-substrate-exploration`
  (`de8bf81`) is on remote as a **graveyard** — do not
  cherry-pick from it (playbook §0).
- **Cycle 51 (IOCell pivot) in flight.** Phase 0 spike ✅
  (superseded by dogfood-bundle analysis). **PR-V (ADR
  0004) ✅ merged** (#332, `6b3ff6a`). **PR-W is next**,
  then PR-W2 → PR-X → PR-Y → PR-Z. Decision record:
  [`adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md).
- **Cycle 49 + post-49 hotfixes (PRs #313–#331) shipped**
  through `ae33bc9`. Per-PR detail lives in
  [`CHANGELOG.md`](../CHANGELOG.md) + `git log` +
  [`CYCLE-49-PLAN.md`](CYCLE-49-PLAN.md) (HISTORICAL) —
  not repeated here per the
  [DOC-MAP §"Archiving stale onboarding narrative"](DOC-MAP.md#archiving-stale-onboarding-narrative)
  discipline.
- **Why the pivot**: the maintainer's 2026-05-14
  preview.135 dogfood (4-run test-04) went "completely
  haywire". Root cause is structural — screen-row
  coordinates as a source of truth on a scrolling buffer.
  The bundle also falsified the initial
  classification-by-`EntrySource` thesis (playbook §4).
  PR-X is the load-bearing fix.

## Next stage

**Cycle 51 (IOCell pivot).** Full sequencing, exact code
anchors, the rejected-thesis context, and the per-PR
execution maps are in
[`CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md); the locked
decisions are in
[ADR 0004](adr/0004-iocell-model-for-shell-interaction.md).
Do not re-narrate them here (DOC-MAP archiving discipline).

One-line state: PR-V ✅ merged → **PR-W next** → PR-W2 →
**PR-X (the load-bearing fix)** → PR-Y → PR-Z. Each PR is
independently CI-gated and ships through the maintainer's
release-build NVDA dogfood; the acceptance gate for PR-X is
the 4-run test-04 reproducer no longer going haywire. Cycle
51 NVDA matrix row lives in
[`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).

**Next-cycle candidates** (after Cycle 51 ships; none block
each other):

- **`EntrySource.DraftInputRecall`** (deferred from Cycle 49
  E3) — substrate refinement; no audible bug behind it
  after PR-I's screen-read approach.
- **Sub-prompts as nested IOCells** (ADR 0006; only if
  Cycle 51 dogfood surfaces the need).
- **Cell-navigation hotkeys** (`h`, `o`, `Alt+Up/Down`) per
  CANONICAL-DISPLAY-CATALOG §1.6.
- **User-facing replay UI** — leverages the
  `parseFromJsonl` reader shipped in PR-W2.
- **PowerShell support** — depends on PS-side heuristic
  emission emitting reliable PromptStart markers.
- **Cycle 45g** — `ShellPolicy` consolidation (~200 LOC
  pure refactor).
- **Spatial audio channel** + **custom TTS channel** —
  Cycle 51 proves the extensibility surface; the actual
  sink impls are their own projects.
- **Velopack staleness investigation** (PR-M).

See [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
for sequencing rationale + risks.

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
- **GitHub MCP can disconnect mid-session.** Observed
  2026-05-13: MCP token failed to retrieve during the PR-B
  fixup cycle, blocking auto-merge + check-status queries.
  Fallback: ask the maintainer to merge via the GitHub UI
  "Squash and merge" button. Webhook events kept flowing
  independently. See CLAUDE.md "Sandbox / runtime constraints".
- **Diagnostic-bundle chunking.** Don't ask for full
  `Ctrl+Shift+D` bundles unprompted — they can be multi-MB and
  crash iOS chat clients on paste. Use the menu items under
  `Diagnostics → Extract` to ask for targeted slices.

## Where to find detail

| If you need… | Open |
|---|---|
| **Cycle 51 execution map (START HERE)** | [`docs/CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md) |
| Cycle 51 locked decisions | [`docs/adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md) |
| Cycle-by-cycle trail | [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` |
| Doc-archiving discipline | [`docs/DOC-MAP.md`](DOC-MAP.md#archiving-stale-onboarding-narrative) |
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
