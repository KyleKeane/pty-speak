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

## Current state (2026-05-14)

**Cycle 49 fully shipped.** Eight-PR sequence (PR-A → PR-I,
plus closure audit) refining the speech / review-cursor /
sub-prompt narration on top of Cycle 48's `ShellInteraction`
state machine:

| PR   | Commit    | Scope                                                              |
|------|-----------|--------------------------------------------------------------------|
| #313 (PR-A) | `8242e56` | `SpeechCursor` manual nav (`Ctrl+Shift+Up/Down/End`) skips entries whose render returns `None` (Newline, Overwrite, empty TextSpan, `UserInputEcho`, boundary markers without payload). One stop per audible chunk. |
| #314 (PR-B) | `36acad6` | Post-Enter delta announce for sub-prompt responses. Captures the on-screen preamble at `EnterPressed` so tuple-finalise only narrates the script's response. (PR-B's text-prefix-trim was later superseded by PR-F's line-count slice.) |
| #315 (PR-C) | `1579ff7` | `TerminalAutomationPeer.RaiseTextChanged` fires after every Announce site so NVDA's review cursor invalidates its cached `DocumentRange` and re-pulls the latest tail without needing a fresh command. |
| #316 (PR-D) | `4b315ae` | `SpeechCursor.renderEntryForManualNav` decouples manual-nav rendering from per-shell `PromptPath` policy. `PromptStart` markers with payload always surface as navigable entries (`FinalDirOnly`-trimmed) regardless of auto-drive verbosity. |
| #317 (PR-F) | `2f8a05a` | Two refinements: (1) sub-prompt announce narrates only the LAST non-empty line of the accumulator (the actual prompt text), not every preamble line. (2) Post-Enter delta uses line-count slicing on `tuple.OutputText` instead of PR-B's text prefix-trim — robust to per-row content drift between EnterPressed and tuple-finalise. |
| #318 (PR-G) | `335dbb9` | Removed the tuple-final `lastAnnouncedText` prefix-trim relic. Pre-PR-G it silenced duplicate-command output (running `echo hi` twice → second run silent) and produced the "unpredictable" silence pattern over a session. Only sub-prompt line-count slicing remains. |
| #319 (PR-H) | `b9e250f` | Reshaped `test-01-echo.cmd` with explicit `Line 1 of 3` / `Line 2 of 3` / `Line 3 of 3` labels + framed intro and final messages. Maintainer recognition issue from the pre-Cycle-49 implicit Line 1 / Line 8 labelling. |
| #320 (PR-I) | `904051c` | Up / Down arrow history-recall announce. Detects arrow byte sequences (`\x1B [ A/B`, `\x1B O A/B`), debounces 100 ms, reads the prompt row, strips the prompt-path prefix, and announces via a new `pty-speak.input-assistant` activity ID. CORE-ABSTRACTION-BOUNDARY.md §"input assistant" reserved peer pane's first concrete user. |
| (this PR-Z) | (closure audit) | Docs sweep: SESSION-HANDOFF (this section), PROJECT-PLAN change log, CLAUDE.md sequencing, ACCESSIBILITY-TESTING matrix consolidation, CYCLE-49-PLAN HISTORICAL banner. |

Cycle 48 history retained in the CHANGELOG + ADR-0003.

`main` past Cycle 49 PR-I (`904051c`), with this PR-Z closure
audit landing on top. Working tree clean; no in-flight
branches; no pending fixups.

**Validation gate**: maintainer dogfood against the post-PR-I
release build is the gating signal. Live items verified
2026-05-14 during cycle:
- Simple echo (`test-01-echo`) narration ✓
- Sub-prompt (`test-02-text-input`) prompt + post-Enter delta ✓
- Duplicate command (e.g. `echo hi` twice) ✓
- Menu narration (was a downstream regression of the
  duplicate-suppression bug; resolved by PR-G/H) ✓
- History recall via Up/Down arrow — pending release-build
  re-test post-PR-I.

## Next stage

**Cycle 49 done.** All maintainer-reported audible defects
resolved. Next live gate: release-build dogfood of PR-I's
history-recall announce.

**Next-cycle candidates** (none block each other; pick by
priority):

- **`EntrySource.DraftInputRecall`** (deferred from Cycle 49
  E3) — tag history-recall draft rewrites in `ContentHistory`
  so the review cursor can distinguish them from typed input.
  PR-I solved the audible problem with a simpler screen-read
  approach; this is a substrate refinement.
- **Cycle 45g** — `ShellPolicy` consolidation (~200 LOC pure
  refactor).
- **Cycle 45d** — Interactive review-cursor focus (~150 LOC).
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
