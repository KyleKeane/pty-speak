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

**Cycle 48 fully shipped.** Six-PR sequence (PR-A → PR-F)
implementing the `ShellInteraction` semantic state machine per
[ADR 0003](adr/0003-shell-interaction-state-machine.md):

| PR   | Commit    | Scope                                                              |
|------|-----------|--------------------------------------------------------------------|
| #306 (PR-A)  | `f0fbbe8` | ADR 0003 drafted + §9 open questions resolved in-chat. |
| #307 (PR-B)  | `3c2a372` | `ShellInteraction` state machine (`Composing` / `Executing`), wired in observe-only mode (logs transitions but doesn't change announce routing). |
| #308 (PR-C)  | `5d34369` | `ContentHistory.Entry.Source : EntrySource` substrate-schema change. Bundle's `Stats:` line extends with per-source counts. |
| #309 (PR-D)  | `96b6e56` | `UserInputBuffer` byte-stream wiring via `writePtyBytes` wrapper. `EnterPressed` transition carries the captured command text. |
| #310 (PR-E)  | `459a0b2` | Sub-prompt announce via state machine (`SubPromptIdle` transition fires `Announce` + earcon). SpeechCursor filters `UserInputEcho` entries in both AutoDrive AND Manual. Idle-flush announce body retired. |
| (this PR, PR-F) | (closure audit) | Docs sweep: ADR status, SESSION-HANDOFF, CLAUDE.md sequencing, PROJECT-PLAN change log. |

Cycle 46 + 47 history retained in the CHANGELOG + the
ADR-0002 / ADR-0003 records.

The architectural mismatch named in
[ADR 0003](adr/0003-shell-interaction-state-machine.md) —
"pty-speak models the byte stream; it should model the
interaction" — is resolved by the two-state machine on top
of ContentHistory / Screen / SessionModel. Idle-flush
chatter goes away by construction; sub-prompt detection
fires only on the explicit "idle + last-byte-not-LF" signal;
SpeechCursor never replays user-typed echo.

**Validation gate pending.** **NVDA matrix Cycle 48-B1 →
48-E8** in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
covers the cycle. Maintainer dogfood against preview.118
(the build cut after this PR-F merges) is the gating signal.

## Where we left off

`main` past Cycle 48 PR-E (`459a0b2`), with this PR-F closure
audit landing on top. Working tree clean; no in-flight
branches; no pending fixups.

[`docs/adr/0003-shell-interaction-state-machine.md`](adr/0003-shell-interaction-state-machine.md)
is in **Accepted / Implemented** state with all six PRs
merged. The Cycle 47 dead code preserved as defence-in-depth
in PR-E (the `tupleFinaliseAnnounce` prefix-trim path) stays
in source for one preview cycle; if PR-E's audible behaviour
holds in preview.118 dogfood, a follow-up PR can delete the
dead branches.

## Next stage

**Cycle 48 done.** The six-PR sequence (PR-A → PR-F) is in
main. Next live gate: maintainer preview.118 dogfood. Audible
listening criteria per matrix rows 48-E1 → 48-E8.

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
