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

> **NEW SESSION START HERE →
> [`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md)**
> (**Accepted 2026-05-15**; reads ADR 0005 as its mechanism
> spec). Cycle 51 is shipped; the CYCLE-51-PLAYBOOK is now
> historical. **Cycle 52 R1 in progress** — architecture map
> then behaviour-identical extraction; each R-stage stays
> independently CI- + dogfood-gated.

- **`main` HEAD = `fa8885c`** (Cycle 51 PR-AE, #344).
- **Cycle 51 (IOCell pivot) — SHIPPED.** PR-V (ADR 0004) →
  PR-W / PR-W2 (IOCell + schemaVersion-2 + round-trip
  reader) → then the dogfood-driven sequence **PR-X**
  (Seq-watermark narration, history-scroll + persisted-JSONL
  fix) → **PR-Y** (single-key sub-prompt question strip) →
  **PR-Z** (history-recall wrap) → **PR-AA** (sub-prompt
  preamble + startup banner) → **PR-AB** (fast-type echo
  strip) → **PR-AC** (switch banner) → **PR-AD** (SpeechCursor
  fed from sealed IOCells) → **PR-AE** (cursor-anchored
  prompt detection — the heuristic root-cause fix). All
  merged #337–#344. Per-PR detail: [`CHANGELOG.md`](../CHANGELOG.md)
  + `git log`. `CYCLE-51-PLAYBOOK.md` is now HISTORICAL.
- **Cycle 51 outcome / why Cycle 52 pivots.** PR-AE removed
  the acute corruption (phantom-`PromptStart` storms). The
  2026-05-15 preview.141 dogfood confirmed the *residual*
  issues (progress narrates only at completion;
  history-recalled command capture is garbage; prompt-path
  verbosity eats echo output) are the **heuristic
  screen-scrape architectural ceiling**, not regressions —
  cmd emits no boundary protocol. The maintainer chose to
  **exit heuristic screen-scraping** rather than keep
  patching the announce layer.
- **Cycle 52 = three-layer re-foundation**
  ([ADR 0006](adr/0006-three-layer-refoundation.md),
  **Proposed 2026-05-15**) with OSC 133
  ([ADR 0005](adr/0005-osc133-shell-integration.md))
  folded in as its transport mechanism. The 2026-05-15
  strategic review found the 51-cycle brittleness was
  structural: shell-specific reconstruction
  (`HeuristicPromptDetector`) leaked into `Terminal.Core`
  and there is no single orchestration point. ADR 0006
  locks a transport (`ShellAdapter`) / pure core /
  accessibility-channel boundary + a one-file `SessionHost`.
  **F# kept, WPF kept, MAUI explicitly out of scope**
  (maintainer decision 2026-05-15 — channel stays an
  interface for design hygiene only). ADR 0006's R0–R7
  supersedes ADR 0005's A–F. **Accepted 2026-05-15;
  R1 in progress** (architecture map → behaviour-identical
  extraction; each R-stage independently CI- + dogfood-
  gated).
- **Cycle 49 + post-49 hotfixes (PRs #313–#331)** shipped
  earlier; detail in [`CHANGELOG.md`](../CHANGELOG.md) /
  `git log` per the
  [DOC-MAP §"Archiving stale onboarding narrative"](DOC-MAP.md#archiving-stale-onboarding-narrative)
  discipline.

## Next stage

**Cycle 52 — three-layer re-foundation.** Design + decision
in [ADR 0006](adr/0006-three-layer-refoundation.md)
(**Accepted 2026-05-15; R1 in progress**), with
[ADR 0005](adr/0005-osc133-shell-integration.md) as the
OSC-133 mechanism spec. Stages, once accepted: **R0** ADR
(this) → **R1** extract `ShellAdapter` + `SessionHost`,
**zero behaviour change** (gate: behaviour-identical
regression dogfood) → **R2** cmd OSC-133 adapter → **R3**
precedence + delete PR-AB / PR-X/Y core scaffolding → **R4**
purify `Terminal.Core` + portability-lint enforcement (the
structural anti-re-leak gate) → **R5** PowerShell adapter →
**R6** feature unlock (per-line progress, prompt-verbosity
fix, clean SpeechCursor) → **R7** claude adapter decision +
Cycle closure audit. Each stage independently CI-gated +
dogfood-validated. R1 + R4 are the structural gates; R2's
cmd OSC-133 dogfood is the first semantic proof. NVDA matrix
rows per stage in
[`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).

**Deferred (independent; revisit after Cycle 52):**

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
| **Cycle 52 re-foundation (START HERE)** | [`docs/adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md) |
| Cycle 52 OSC-133 mechanism spec | [`docs/adr/0005-osc133-shell-integration.md`](adr/0005-osc133-shell-integration.md) |
| Cycle 51 (shipped) locked decisions | [`docs/adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md) |
| Cycle 51 execution map (HISTORICAL) | [`docs/CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md) |
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
