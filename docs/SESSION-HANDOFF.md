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

**Cycle 46 fully shipped.** Four-PR sequence merged:

| PR  | Commit    | Scope                                                                |
|-----|-----------|----------------------------------------------------------------------|
| #287 (PR-A)  | (cycle gate) | ADR 0002 drafted.                                       |
| #288 (PR-B)  | `f27d5e2` | UIA Text pattern substrate-swapped (screen grid → `ContentHistory.tailText`, 256 KB cap); `AutomationControlType` flipped `Document` → `Edit`. |
| #289 (handoff) | `7241ce7` | Cycle 46 next-steps + project-plan docs.                  |
| #290 (PR-C)  | `a5dd320` | `TerminalAutomationPeer.RaiseCaretMovedToTail()` raises `TextPatternOnTextSelectionChanged`; replaces the tuple-finalise `Announce(text, ActivityIds.output, MostRecent)` call. |
| #291 (PR-D)  | `9bfdd48` | `speechCursorAnnounce` delegates to the same caret helper. Legacy screen-grid `TerminalTextProvider` / `TerminalTextRange` / `SnapshotText` deleted (~680 LOC). `WordBoundaryTests.fs` deleted; coverage in `ContentHistoryTextRangeTests`. |

The architectural mismatch named in [ADR 0002](adr/0002-uia-textedit-caret-output.md)
— "terminal output went through `RaiseNotificationEvent` (a
status-message channel) instead of UIA's text-edit caret (the
designed-for-streaming channel)" — is resolved at both the
substrate (PR-B) and channel (PR-C / PR-D) levels, with one
post-PR-D revision: terminal output now fires **both**
`Announce` and the caret-move event (ADR §4 Option ★★
Augment, not Option ★ Replace; see ADR Status notes for the
NVDA-didn't-read-on-bare-caret-event finding). The
typing-interrupts-speech win comes from PR-B's
`ControlType=Edit` flip (NVDA's native setting handles it
independently of how speech was initiated), so the audit
revision didn't undo the user-visible payoff of the cycle.

**Validation gate pending.** **NVDA matrix Cycle 46-1** in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
covers cmd `dir`, PowerShell `Get-Process`, and Claude REPL
turn. The user-visible payoff:

- Focus → NVDA announces "edit" (was "document").
- `Insert+Down` → NVDA reads `ContentHistory` tail.
- **Typing into the prompt interrupts an in-progress read**
  via NVDA's "Speech interrupt for typed character" setting —
  the original motivating pain point from the PR #282 → #286
  failure trail.
- **`Alt` interrupts the read** by reaching the menu through
  WPF's normal route.

**Cycle 45c-1 → 45c-6** matrix rows — assumed walked or
rolled forward into 46-1; check the matrix file for the
live status.

## Where we left off

`main` past Cycle 46 PR-D (`9bfdd48`).
Working tree clean; no in-flight branches; no pending fixups.

[`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
is in **Accepted** state with all four PRs merged.
[`docs/CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) is
historical — every edit it described has shipped — and is
flagged as such at the top of the file.

If the NVDA matrix walk surfaces regressions, they land as
post-Cycle-46 fixup PRs (most likely candidate per the ADR
risk register: NVDA not reacting to
`TextPatternOnTextSelectionChanged` on a read-only Edit; in
that case swap to `AutomationEvents.TextPatternOnTextChanged`
or fire both inside `RaiseCaretMovedToTail`).

## Next stage

**Cycle 46 done.** The four-PR sequence (PR-A through PR-D) is
in main; only the NVDA matrix walk + release-build smoke pass
remain before the cycle closes.

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
