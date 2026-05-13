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

## Current state (2026-05-13)

**Cycle 46 + 47 fully shipped.** Cycle 46 (PRs #287–#291) flipped
the UIA Text-pattern surface from `Document` +
`RaiseNotificationEvent` to `Edit` + caret-event; Cycle 47
(PRs #295–#303, mostly post-preview review fix-ups) layered on
the dogfood-driven adjustments. Most recent batch (this
session, post-preview.114):

| PR   | Commit    | Scope                                                              |
|------|-----------|--------------------------------------------------------------------|
| #299 | `8f9b30a` | `sanitiseForBundle` preserves `\n` `\r` `\t` in diagnostic bundle; marker labels relabelled to `begin/end prompt/output`; `CommandFinished` synthesised on PromptStart-while-AwaitingCommandStart. |
| #300 | `ddd7b8a` | Typing-window UIA suppression — `materialise` excludes the active span when last keystroke < 350 ms ago. Announce content logged at Debug (`MsgHead=`). NVDA settings recommendation in ACCESSIBILITY-TESTING.md. |
| #301 | `d6fa5d0` | Tuple-final prefix-trim against `lastAnnouncedText` so `set/p` doesn't replay the idle-flush prompt. |
| #302 | `f3524be` | `ReadyForInput` earcon dispatched on idle-flush so `set/p` / `pause` produce the audible "you can type now" click. |
| #303 | `b0d3230` | Top-level mnemonic conflicts fixed: `_Display` vs `_Data` (renamed to `D_ata`); Diagnostics `_CMD Interaction Tests` renamed to `C_MD Interaction Tests` to clear the conflict with `Test Process _Cleanup`. |

The architectural mismatch named in [ADR 0002](adr/0002-uia-textedit-caret-output.md)
is resolved at both the substrate and channel levels. The
post-Cycle-46 dogfood revisions narrowed the announce surface
(typing-window gate, prefix-trim) and broadened the audible
cue surface (mid-eval earcon) without backing out the
substrate / channel split.

**Validation gate pending.** **NVDA matrix Cycle 47-1 through
47-25** in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
covers the cycle's full set. The recent additions (47-18
through 47-25) are the post-preview.114 batch.

## Where we left off

`main` past Cycle 47 PR #303 (`b0d3230`).
Working tree clean; no in-flight branches; no pending fixups.

[`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
is in **Accepted** state with all four PRs merged.

## Next stage

**Cycle 47 dogfood batch done.** Five PRs (#299–#303) merged
this session. Next live gate: maintainer preview.115 NVDA matrix
walk against rows 47-18 through 47-25.

**Next-cycle candidates** (none block each other; pick by
priority):

- **Cycle 45g** — `ShellPolicy` consolidation (~200 LOC pure
  refactor).
- **Cycle 45d** — Interactive review-cursor focus (~150 LOC).
- **Semantic labels** — Add `Source: EntrySource` to every
  `ContentHistory.Entry`. Foundational for chunk-level
  navigation + inject-past-input.
- **Spinner / red-tone fixes** — Rewrite Cycle 29b storm
  fixes against ContentHistory.
- **Coalescer rename** — Standalone refactor (~25 sites).
- **`ActivityIds.output` retirement** — kept in source after
  Cycle 46 PR-D; still used by `SpeechCursor.renderEntry`
  (manual `Ctrl+Shift+Up/Down/End` review-cursor path, which
  stays on notifications per ADR §4) + `NvdaChannel.semanticToActivityId`.
  If both consumers move off it in a future cycle, the
  constant becomes truly unreferenced and can be deleted.

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
| Cycle-by-cycle trail | [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` |
| Cycle 46 ADR (decision + Open Question resolutions + per-PR Status notes) | [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md) |
| Cycle 46 PR-C / PR-D scoping doc (historical, now superseded by the shipped code) | [`docs/CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) |
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
