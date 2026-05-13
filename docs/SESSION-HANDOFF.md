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

**Cycle 46 PR-A + PR-B merged.** PR #287 (ADR 0002) landed
2026-05-12; PR #288 (substrate-swap + ControlType flip) landed
2026-05-13. The UIA Text pattern is now backed by
`ContentHistory.tailText` (256 KB cap) instead of the screen
grid; `TerminalAutomationPeer.AutomationControlType` returns
`Edit` instead of `Document`. NVDA's native text-edit reading
path (read-all, line nav) now operates against the substrate
tail.

**No call-site change for output yet.**
`TerminalView.Announce` keeps firing `RaiseNotificationEvent`
on `ActivityIds.output`; PR-C drops that call and replaces it
with a `TextSelectionChangedEvent` raise. See
[`CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) for the
file-level PR-C / PR-D scoping.

**Validation gate in flight.** **NVDA matrix Cycle 46-PRB-1**
in [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
covers cmd `dir`, PowerShell `Get-Process`, and Claude REPL
turn. Confirm "edit" focus announce + `Insert+Down` reads
ContentHistory tail before starting PR-C.

**Cycle 45c-1 → 45c-6** matrix rows (the previous validation
gate) — assumed walked or rolled forward into 46-PRB-1; check
the matrix file for the live status.

## Where we left off

`main` at `f27d5e2` (Cycle 46 PR-B squash-merge).
Working tree clean; no in-flight branches; no pending fixups.

`docs/adr/0002-uia-textedit-caret-output.md` is in **Accepted**
state with §1–§5 resolutions baked into the Decision section
and recorded inline in the Status notes.

If the NVDA matrix walk surfaces regressions, they land here
as PR-B-followup fixups rather than progressing to PR-C.

## Next stage

**Primary track: complete Cycle 46.**
See [`docs/CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) for
file-level edits, threading concerns, test plans, and the NVDA
matrix gate definitions. Summary:

- **PR-C** — `SessionModel` raises `TextSelectionChangedEvent`
  on tuple finalise; drops the `Announce(text,
  ActivityIds.output, MostRecent)` call site at
  `Program.fs:1627–1630`. Adds a `RaiseCaretMovedToTail`
  helper to `TerminalAutomationPeer`. NVDA matrix gate
  Cycle 46-PRC-1.
- **PR-D** — `SpeechCursor` callbacks delegate to the caret
  helper (channel-side wiring; substrate-side `SpeechCursor`
  unchanged). Removes the legacy screen-grid
  `TerminalTextProvider` / `TerminalTextRange` / `SnapshotText`
  (~600 LOC). NVDA matrix gate Cycle 46-PRD-1.

**Parallel track: pre-Cycle-46 candidates from the project
plan.** None block each other:

- **Cycle 45g** — `ShellPolicy` consolidation (~200 LOC pure
  refactor).
- **Cycle 45d** — Interactive review-cursor focus (~150 LOC).
- **Semantic labels** — Add `Source: EntrySource` to every
  `ContentHistory.Entry`. Foundational for chunk-level
  navigation + inject-past-input.
- **Spinner / red-tone fixes** — Rewrite Cycle 29b storm
  fixes against ContentHistory.
- **Coalescer rename** — Standalone refactor (~25 sites).

See [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
for sequencing rationale + risks; PR-C / PR-D depend on no
other cycle and unblock the §"Cycle 46 done" milestone.

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
| Cycle 46 PR-C / PR-D file-level plan | [`docs/CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) |
| Cycle 46 ADR (decision + Open Question resolutions) | [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md) |
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
