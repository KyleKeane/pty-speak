# Session handoff

This document is the bridge between Claude Code coding sessions on
this repo. It captures the things a new session needs that **aren't**
already in the existing docs — the working conventions, the
sandbox-specific gotchas, and the "where we left off" pointer.

A new session should read this **first**, then move to
[`spec/tech-plan.md`](../spec/tech-plan.md),
[`docs/CHECKPOINTS.md`](CHECKPOINTS.md), and
[`CHANGELOG.md`](../CHANGELOG.md) in that order.

## Where we left off

| | |
|---|---|
| **Last merged stage** | Stage 3b (WPF `TerminalView` + end-to-end `ConPtyHost → Parser → Screen → TerminalView`) |
| **Last shipped release** | [`v0.0.1-preview.15`](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.15) — Stage 0 (empty-window installer, NVDA-validated) |
| **Released since** | Nothing. Stages 1, 2, 3a, 3b are on `main` but not yet shipped as a preview. The Stage-3b installer is the next natural smoke target. |
| **Next stage** | **Stage 4** — UIA exposure (first NVDA milestone). See sketch below. |

The end-to-end pipeline is wired: launching the app spawns `cmd.exe`
under ConPTY, parses its output, applies VtEvents to a 30×120
`Screen`, and renders the buffer in a custom WPF `FrameworkElement`.
Visual smoke on Windows is **pending** — the maintainer hasn't
installed the post-Stage-3 build yet.

## Pending action items (maintainer)

These can only be done from a workstation with normal git +
GitHub-website access. They've accumulated because the development
sandbox can't push tags and the GitHub MCP server occasionally
disconnects mid-session.

1. **Push the four pending baseline tags.** Exact commands are in
   [`docs/CHECKPOINTS.md`](CHECKPOINTS.md#pending-checkpoint-tags).
   Tags don't trigger any workflow; they're rollback handles only.

2. **Optional: cut a `v0.0.1-preview.16` release.** Validates Stage 3b
   visually (cmd.exe text appearing in the WPF window) and gives you
   an installer to run NVDA against. Procedure in
   [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md#cutting-a-release);
   make sure to add a `## [0.0.1-preview.16]` section to
   `CHANGELOG.md` first or the new release-time gate will refuse it.

3. **Re-enable the GitHub MCP server** in the next Claude Code thread
   if it's not auto-reconnected. Without it the agent can't
   programmatically check PR status, merge PRs, or post issue
   comments — falls back to asking the maintainer.

## Working conventions on this repo

Pulled from observed practice, not just `CONTRIBUTING.md`. The
written rules in `CONTRIBUTING.md` are still authoritative; this
list adds the things that come up in agent workflows.

### PR shape

- **One concern per PR.** When tempted to bundle two improvements,
  split them. Past frustrations on this repo trace back to oversized
  PRs with multiple moving parts. The user explicitly asked for
  smaller PRs after the Stage 0 diagnostic loop.
- **Branch naming**: `feature/<stage-or-feature-slug>`,
  `fix/<short-slug>`, `chore/<short-slug>`, `docs/<short-slug>`.
- **PR title in Conventional Commits form**: `feat: …`, `fix(...): …`,
  `ci: …`, `docs: …`, `chore: …`. There's no automated check yet but
  the merge history is uniformly conventional and readers expect it.
- **Squash-merge by default.** All PRs to date have been squashed;
  the merge commit subject becomes the canonical history line.
- **PR body**: brief Summary + What changed + Test plan checklist.
  Stage-implementation PRs also include a "What this PR deliberately
  omits" section pointing at the next stage(s) for deferred items.

### CHANGELOG

- Every behaviour-changing PR adds a bullet under
  `## [Unreleased]` → `### Added` / `### Changed` / `### Removed`.
- When a release is cut, the unreleased entries get rewritten into
  a clean per-release section with a one-paragraph narrative
  (template: see the existing `[0.0.1-preview.15]` entry).
- The release workflow now **gates on a matching
  `## [<version>]` section** existing before it builds — don't tag
  a release without updating CHANGELOG first.

### Checkpoints + rollback

- Every shipped stage adds a row to `docs/CHECKPOINTS.md`'s
  "Current checkpoints" table.
- If the agent can't push the tag itself (sandbox limitation), it
  also adds a row under "Pending checkpoint tags" with the exact
  push commands so the maintainer can sweep them later.
- The label `stable-baseline` on the merging PR is the
  searchable filter (`is:pr label:stable-baseline`).

### Documentation

- `spec/` is **immutable** — it's the external research that drove
  the design. Don't edit it. Discoveries about platform behaviour
  go to `docs/CONPTY-NOTES.md` (or sibling platform-quirks files
  as the project grows).
- `docs/CHECKPOINTS.md` is the rollback contract. `docs/RELEASE-PROCESS.md`
  has the active release flow and a "Common pitfalls" section that
  every diagnostic loop's lessons funnel into.

## Sandbox + tools caveats

These are constraints of the agent runtime that aren't obvious from
the repo and that wasted time in the previous session before being
internalised.

### `dotnet` is not on the dev sandbox

The agent edits files but cannot build / restore / test locally —
all validation happens in CI on `windows-latest`. Implications:

- Compile-time bugs surface only after a push. Iteration loops on
  CI take a few minutes per round. **Read each file twice before
  pushing**; a missed `let rec`, an unused `open`, or a typo in a
  string literal becomes a 5-minute round-trip.
- F# struct field ordering for P/Invoke can't be locally validated
  against `Marshal.SizeOf`. Cross-check carefully against the C
  header order (existing `Terminal.Pty/Native.fs` is the reference).
- Unit tests can be reasoned about but not executed; FsCheck-style
  property assertions exercise paths the author may not have
  considered.

### CI logs

- The harness blocks `productionresultssa*.blob.core.windows.net` and
  `api.github.com`, so the agent **cannot fetch CI run logs
  directly**. WebFetch and curl both 403.
- When CI fails, ask the maintainer to paste the relevant log
  lines. **The maintainer uses a screen reader**: they can't
  `Ctrl+F` through a long log easily. Either:
  - Ask for the full log paste and grep server-side via `Bash`
    against the pasted content, or
  - Tell them which exact strings to look for (e.g. `error FS`,
    `error MSB`, `Failed!`, the test name) and ask for the
    surrounding 5-10 lines.

### Tag and release pushes

- The local git proxy returns 403 on **tag pushes** (any ref under
  `refs/tags/`). Branches push fine. The only way to land a tag is
  for the maintainer to push from their workstation.
- **Releases are created via the GitHub Releases UI**, not by
  pushing a `v*` tag directly — that's how the workflow shape ended
  up triggered by `release: published`. Don't try to push a
  versioned tag and expect the release pipeline to fire.
- **Never publish a release with `Target` other than `main`.** A
  past release (v0.0.1-preview.14) accidentally targeted a stale
  branch and ran an outdated `release.yml`. The release workflow
  now fail-fasts in this case but the cure is still to delete the
  release.

### GitHub MCP

- When the GitHub MCP server is connected, the agent can read PRs,
  list checks, merge PRs, post comments, etc.
- The server can disconnect mid-session and **does not always
  reconnect**. When that happens:
  - Webhook-style events (subscribe_pr_activity) often keep
    flowing — they're a separate channel.
  - The agent falls back to "ask the maintainer" for every state
    query and merge.
  - This was the trigger for ending the previous session and
    writing this handoff.

### Stream idle timeouts

- Large file writes (~600+ lines) sometimes trigger
  "Stream idle timeout - partial response received". The model is
  fine — the transport isn't.
- Mitigation: write files in 100-200 line chunks via Write + Edit,
  or via shell heredocs that append in pieces. The maintainer is
  aware of this and will say "try smaller actions" if a long write
  hangs.

## Stage 4 implementation sketch

`spec/tech-plan.md` §4 has the full spec. This is a pre-digested
plan so the next session doesn't have to re-derive scope.

**Goal**: NVDA's review cursor (Caps Lock + Numpad 7/8/9) reads the
terminal content character / word / line at a time. No streaming
announcements yet — just static text exposure. Validation tools:
Inspect.exe, Accessibility Insights, FlaUI for unit tests, NVDA for
manual sign-off.

**Where it lives**: `src/Terminal.Accessibility/` — currently just
`Placeholder.fs`. Replace with:

- `TerminalAutomationPeer.fs` — `FrameworkElementAutomationPeer`
  subclass. Override `GetAutomationControlTypeCore` (Document),
  `GetClassNameCore` (`"TerminalView"`), `GetNameCore` (`"Terminal"`),
  `IsControlElementCore = true`, `IsContentElementCore = true`,
  `GetPatternCore(PatternInterface.Text)` returning `ITextProvider`.
- `TerminalTextProvider.fs` — `ITextProvider` + `ITextRangeProvider`
  implementations. `DocumentRange` covers all rows; ranges hold an
  immutable snapshot of affected rows; `GetText`, `Move`,
  `MoveEndpointByUnit` for Character/Word/Line/Paragraph/Document
  units; `Compare`, `Clone`, `ExpandToEnclosingUnit` etc. Stub
  `GetAttributeValue` to return `NotSupported` initially — Stage 5
  fills in the SGR-attribute exposure (foreground colour,
  font-weight, etc.) which is the project's biggest accessibility
  differentiator.

**Wiring**: `TerminalView` (in `src/Views/`) overrides
`OnCreateAutomationPeer` to return the new peer. The peer needs a
reference to the `Screen` — pass it via `TerminalView.SetScreen`
(already exists) and have the peer read snapshots from there.

**Snapshot rule**: UIA calls into the provider on a different thread
from the WPF Dispatcher. Mutating the buffer while UIA reads = crash
(see `Terminal.Accessibility` design notes in `spec/overview.md`).
For Stage 4 the simplest approach is: every `ITextRangeProvider`
holds an immutable copy of the rows it spans, taken at construction.
A buffer-modify counter (sequence number) lets the peer invalidate
stale ranges.

**Testing without NVDA**:
- **FlaUI** integration tests in `tests/Tests.Ui/` (currently
  placeholder). Launch `Terminal.App.exe` from the publish output,
  attach via `UIA3Automation`, find the element by ClassName, assert
  ControlType=Document and `Text` pattern is supported, call
  `DocumentRange.GetText(int.MaxValue)` and compare to the input
  fed to ConPTY.
- **Inspect.exe / Accessibility Insights for Windows**: visual
  verification only; no automation.

**Manual NVDA validation**: documented in
`docs/ACCESSIBILITY-TESTING.md`. Caps Lock + Numpad 7 (prev line),
Numpad 8 (current line), Numpad 9 (next line), Numpad 4/5/6 (word),
Numpad 1/2/3 (character). Should hear the visible terminal text.
"Broken" sounds like NVDA saying "TerminalView" then nothing
(no pattern) or "blank" (empty range) or repeating a line forever
(Move not advancing).

**What Stage 4 deliberately does NOT do** — guard against scope
creep:

- **No streaming announcements** — that's Stage 5
  (`UiaRaiseNotificationEvent`).
- **No SGR attribute exposure on text ranges** — `GetAttributeValue`
  returns `NotSupported` for everything in Stage 4. Stage 5 wires
  up `UIA_ForegroundColorAttributeId` etc.
- **No selection lists / list provider** — Stage 8.
- **No keyboard input → PTY** — Stage 6.

**Risk areas worth flagging in the PR body**:
- Thread affinity: any `RaiseNotificationEvent` call (none in
  Stage 4 but eventually) must run on the Dispatcher thread.
  `TermControlAutomationPeer.cpp` in microsoft/terminal is the
  reference; F# has the same constraint.
- `IsContentElement = true` on all rows for now; will likely need
  to be `false` on rows that are part of a List subtree once
  Stage 8 lands.

## Recommended reading order for a new session

1. **This file.**
2. [`spec/tech-plan.md`](../spec/tech-plan.md) §1–§4 (the stages
   already shipped + Stage 4) — establishes the architectural
   grain.
3. [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — what's stable, what
   tags need pushing, how rollback works.
4. [`docs/CONPTY-NOTES.md`](CONPTY-NOTES.md) — observed platform
   quirks. Render-cadence finding is the one most likely to bite
   again.
5. [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) "Common pitfalls"
   section — every diagnostic loop's lessons end up here.
6. Skim [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` for
   in-flight work and the most recent shipped section for the
   last release narrative shape.
7. Browse `src/` top-down: `Terminal.Core` (data) → `Terminal.Pty`
   (ConPTY) → `Terminal.Parser` (VT500) → `Views` (WPF) →
   `Terminal.App/Program.fs` (composition).

Tests are in `tests/Tests.Unit/` (xUnit + FsCheck) — `SmokeTests`,
`ConPtyHostTests` (Windows-only, runtime-skipped elsewhere),
`VtParserTests`, `ScreenTests`. `tests/Tests.Ui/` is a placeholder
that Stage 4 will populate with FlaUI.

## Closing notes

The previous session pushed through Stages 0 → 3b plus the CI
hygiene PR (#34) over a long arc of iteration. Most of the time
loss came from (a) the Stage-0 release-pipeline diagnostic loop
(`v0.0.1-preview.{1..14}` — root cause: PowerShell heredoc
indentation breaking workflow YAML, now caught by `actionlint`),
and (b) a few rounds of F# / WPF interop quirks at stage
boundaries.

The maintainer is patient and engaged but uses a screen reader and
has limited time for back-and-forth on individual log lines —
prefer asking for whole-log paste over "search for X then paste a
snippet". Small focused PRs with detailed test plans are
appreciated; sprawling PRs with multiple moving parts are not.

Thanks for picking it up. The accessibility differentiation
(`UIA_ForegroundColorAttributeId`, semantic prompts, listbox
exposure) is what actually makes this project worth shipping —
Stages 4–10 are where that work happens.
