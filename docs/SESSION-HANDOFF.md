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
> [`adr/0005-osc133-shell-integration.md`](adr/0005-osc133-shell-integration.md)**
> (Proposed — the Cycle 52 pivot). Cycle 51 is shipped; the
> CYCLE-51-PLAYBOOK is now historical. Do NOT implement
> Cycle 52 until the maintainer accepts ADR 0005.

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
- **Cycle 52 = OSC 133 shell integration**
  ([ADR 0005](adr/0005-osc133-shell-integration.md),
  **Proposed 2026-05-15**). Inject shell init so cmd /
  PowerShell emit OSC 133; the already-shipped
  `Osc133.tryParse` → `Screen.Apply` → `extractIOCell`
  clean arm becomes primary; `HeuristicPromptDetector`
  demoted to fallback. Net-subtractive (retires PR-AB +
  PR-X/Y announce scaffolding). **Not yet accepted — no
  implementation until the maintainer signs off on
  ADR 0005.**
- **Cycle 49 + post-49 hotfixes (PRs #313–#331)** shipped
  earlier; detail in [`CHANGELOG.md`](../CHANGELOG.md) /
  `git log` per the
  [DOC-MAP §"Archiving stale onboarding narrative"](DOC-MAP.md#archiving-stale-onboarding-narrative)
  discipline.

## Next stage

**Cycle 52 — OSC 133 shell integration.** Design + decision
in [ADR 0005](adr/0005-osc133-shell-integration.md)
(**Proposed**; do not implement until accepted). Stages,
once accepted: **A** ADR (this) → **B** cmd `PROMPT`
emitter (A/B + deferred D) → **C** precedence rule + retire
PR-AB / PR-X/Y scaffolding → **D** PowerShell emitter (full
A/B/C/D) → **E** feature unlock (per-line progress streaming,
prompt-path-verbosity fix, clean SpeechCursor command items)
→ **F** claude/other-shell decision + Cycle closure audit.
Each stage independently CI-gated + dogfood-validated
(walking skeleton). Stage B's maintainer cmd dogfood is the
decisive gate. NVDA matrix rows per stage in
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
