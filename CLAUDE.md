# CLAUDE.md

Standing instructions for Claude Code sessions working on `pty-speak`.
This is the auto-loaded entry point — Claude reads it at session start
without being asked. It indexes the deeper material in
[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md),
[`CONTRIBUTING.md`](CONTRIBUTING.md), and the spec, and pins the
working rules and project-specific gotchas that keep showing up
session over session.

If you're a human reading this: it's plain Markdown, you can edit it
directly. If you're Claude: treat each rule as durable context. None
of these are negotiable defaults — surface ambiguities to the user
before deviating.

## Reading order at session start

1. **This file** — Claude-runtime-specific rules (sandbox, MCP,
   ask-for-CI-logs, no-GUI-walks-for-screen-reader-users, the
   diagnostic recipes).
2. **[`README.md`](README.md)** — what the project is, status,
   shipped stages.
3. **[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)** — short
   brief (~150 lines): current state, where we left off, next-stage
   candidates, operational gotchas. Designed to be the TLDR a new
   session reads first; deeper history lives in the archive.
3a. **[`docs/CYCLE-52-R5-PLAYBOOK.md`](docs/CYCLE-52-R5-PLAYBOOK.md)**
   — **the current start-here for active work (R5 PowerShell
   adapter + the pre-R5 pruning sequence).** Self-contained
   recovery brief: where we are, the standing FREEZE decision,
   the meticulously-scoped R5 stages (R5a wire-the-seam → R5b
   PowerShellAdapter → R5c exit-code/`;C` → R5d closure), the
   P1–P5 old-code pruning sequence, and the battle-tested
   operational playbook. Read after SESSION-HANDOFF, before any
   R5 code.
4. **[`docs/PROJECT-PLAN-2026-05-12.md`](docs/PROJECT-PLAN-2026-05-12.md)**
   — current strategic plan (2026-05-12; post-Cycle-45c).
   Predecessor revisions archived under
   [`docs/archive/pre-cycle-45/`](docs/archive/pre-cycle-45/)
   per Track E E5 dated-snapshot discipline
   ([`PROJECT-PLAN-2026-05-09.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md),
   [`PROJECT-PLAN-2026-05-revision.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-revision.md),
   [`PROJECT-PLAN-2026-05.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05.md)).
5. **[`docs/CORE-ABSTRACTION-BOUNDARY.md`](docs/CORE-ABSTRACTION-BOUNDARY.md)**
   — architectural framing that locks the substrate / channel
   dichotomy; recorded as
   [`docs/adr/0001-substrate-channel-dichotomy.md`](docs/adr/0001-substrate-channel-dichotomy.md).
   **Non-negotiable design constraint going forward.** Names the
   three exemplar canonical displays (raw text / interactive
   list / form with text input), the three-sub-pane interaction
   paradigm (input / current-output / history), and the three
   reserved peer panes (notification queue, contextual keyword
   info, input assistant). Read before working on substrate or
   channel code.
6. **[`docs/adr/0003-shell-interaction-state-machine.md`](docs/adr/0003-shell-interaction-state-machine.md)**
   — Cycle 48 architectural pivot (Proposed 2026-05-13). Adds a
   third framing on top of substrate / channel: **interaction**
   as a semantic layer that classifies each byte as user-input
   echo vs cmd output via a two-state machine
   (`Composing` / `Executing`). Replaces the Cycle 47 idle-flush +
   tuple-final + prefix-trim + typing-window gate pile-up
   (PRs #299–#305) with a single transition table. Read before
   working on the announce / NVDA-channel routing code or the
   UIA Text-pattern materialiser.
7. **[`docs/adr/0004-iocell-model-for-shell-interaction.md`](docs/adr/0004-iocell-model-for-shell-interaction.md)**
   — Cycle 51 architectural pivot (Proposed 2026-05-14). Locks
   four decisions: (1) **IOCell** is the unit of shell
   interaction (renamed from `SessionTuple`); (2) sub-prompts are
   inline state inside the parent IOCell in v1, not nested cells;
   (3) **ContentHistory** is the sole extraction substrate —
   screen-row coordinates are display-only post-pivot, and
   extraction drops the cell when no PromptStart Seq exists
   ("loud silence beats stale-scrollback garbage announce"); (4)
   **OutputDispatcher** is the sole non-emergency channel —
   substrate-driven announces flow through Tier 1, app-affordance
   announces use a narrow named bypass (`AnnounceEmergency`) on a
   dedicated `pty-speak.app-affordance` activity ID. Also locks
   the v1 IOCell data structure (F# records + DUs in memory,
   hand-rolled JSONL on disk with `schemaVersion=2`, round-trip
   reader shipped maintainer-only in PR-W2). Read before working
   on any extraction / announce / channel-routing code.
8. **[`docs/adr/0005-osc133-shell-integration.md`](docs/adr/0005-osc133-shell-integration.md)**
   — Cycle 52 strategic pivot (Proposed 2026-05-15). After the
   Cycle 51 PR-X…PR-AE heuristic-patch cycle hit its ceiling,
   the maintainer chose to **exit heuristic screen-scraping**:
   inject shell integration so cmd / PowerShell emit OSC 133,
   making `BoundarySource.Osc133` + the existing (already
   shipped & tested) `Osc133.tryParse` → `Screen.Apply` →
   `extractIOCell` clean arm the **primary** path;
   `HeuristicPromptDetector` demoted to fallback with a
   "once Osc133 seen, mute heuristic" precedence rule. Net-
   subtractive — retires the PR-AB / PR-X/Y announce-path
   scaffolding. Read before working on prompt-boundary
   detection, the shell-spawn seam, or the announce path.
   **Accepted 2026-05-15 (maintainer approved 0005+0006
   together).** Its A–F stage list is
   superseded by ADR 0006's R0–R7; 0005 stays the canonical
   *mechanism* spec.
9. **[`docs/adr/0006-three-layer-refoundation.md`](docs/adr/0006-three-layer-refoundation.md)**
   — Cycle 52 architectural re-foundation (Proposed
   2026-05-15). The strategic-review outcome: 51 cycles were
   brittle because shell-specific reconstruction
   (`HeuristicPromptDetector`) leaked into `Terminal.Core`
   and there was no single orchestration point. Locks a
   **three-layer boundary** — transport (`ShellAdapter` per
   shell, OSC-133 injection lives here) / pure session core
   (`Terminal.Core`, no WPF/P-Invoke/shell strings) /
   accessibility channel (WPF+UIA, swappable sink) — plus a
   one-file `SessionHost` orchestration point. **Supersedes
   ADR 0005's stage list** with R0–R7 (folds OSC-133 in).
   F# kept, WPF kept, **MAUI explicitly out of scope**
   (maintainer decision 2026-05-15). Read before any
   transport / core / channel / spawn work. **Accepted
   2026-05-15; R1–R4 Implemented & CI-green (#347–#357,
   2026-05-16) — the full structural re-foundation is
   landed (R1 seam · R2 cmd OSC-133 · R3 precedence +
   announce-compensation deletion, KI-R2-1 fixed · R4
   namespace purify + portability-lint enforcement).
   Checkpoint at R1–R4 (maintainer 2026-05-16): a
   consolidated foundation dogfood gates R5 (PowerShell
   adapter). Each R-stage stays independently CI- +
   dogfood-gated.** **ADR 0007
   ([`docs/adr/0007-canonical-iocell-history-navigation.md`](docs/adr/0007-canonical-iocell-history-navigation.md),
   **Accepted 2026-05-16**) replaces R6c:** SpeechCursor as
   the canonical navigable IOCell history (D1 typed cells /
   D2 per-cell ops / D3 live-trickle / D4 one model / D5a
   focusable standard control + `Ctrl+Shift+Left/Right` pane
   switch / D6 on-send test-oracle / D7 fenced off from the
   legacy review-cursor patchwork / D8 standard UIA control
   `Tree`-recommended-`List`-fallback, ratified by the Phase
   6a dogfood / D9 cell events on the canonical pipeline).
   Unifies ADR 0006 §"Deferred to R6+" items 2–5; **full
   Phase 0…7 in force**, each its own PR + dogfood. Phase 0
   (delete the dead Seq engine — net-subtractive) is the
   start. D9's project-wide principle is elevated to **ADR
   0008** ([`docs/adr/0008-maximal-semantic-surfacing.md`](docs/adr/0008-maximal-semantic-surfacing.md),
   Accepted): recover maximal unambiguous semantics, emit
   typed canonical events, never relay ambiguous content —
   read it as the *why* behind ADR 0001/0004/0007. Read both
   before any SpeechCursor / IOCell-history / channel /
   substrate work.
10. **RFC 0001 (archived)** — Cycle 33 pivot-gate RFC, formalised
   the `LinearTextStream` substrate + streaming-emission protocol.
   The substrate it specified was replaced by `ContentHistory` +
   `SpeechCursor` in Cycle 45 (PRs #263–#270, 2026-05-12); the RFC
   is now archived at
   [`docs/archive/pre-cycle-45/0001-linear-text-substrate.md`](docs/archive/pre-cycle-45/0001-linear-text-substrate.md)
   alongside the code it described. Read only for historical
   context — the live substrate lives in `src/Terminal.Core/`
   (`ContentHistory.fs`, `SpeechCursor.fs`) and is described
   informally in CORE-ABSTRACTION-BOUNDARY.md §7.
11. **[`docs/CANONICAL-DISPLAY-CATALOG.md`](docs/CANONICAL-DISPLAY-CATALOG.md)**
   — Cycle 33 pivot-gate companion. Full per-primitive UIA /
   ARIA / NVDA / JAWS / Narrator / interaction-contract /
   channel-routing specs for the three exemplars (raw text +
   CommandOutputTuple wrapper, interactive list +
   ConfirmationPrompt hybrid, form with text input) plus named
   extension points (severity alert, indeterminate progress,
   Tier-2 deferred, Tier-3 deferred). Read before working on
   any output-side framework or channel implementation.
12. **[`spec/tech-plan.md`](spec/tech-plan.md)** §N for the canonical
    spec of whatever stage you're working on.
13. **[`CONTRIBUTING.md`](CONTRIBUTING.md)** — canonical PR shape,
    branch naming, F# / .NET 9 gotchas, accessibility
    non-negotiables, P/Invoke conventions, test conventions.

Doc ownership and audience-by-doc routing live in
[`docs/DOC-MAP.md`](docs/DOC-MAP.md). When in doubt about which
file a topic belongs to, check that map first.

This file is **the Claude-runtime layer**: rules that apply only
because Claude Code is running, not because you're working on
`pty-speak` per se. Project-wide conventions (PR shape, F#
gotchas, accessibility rules, tests) live in `CONTRIBUTING.md`
and are referenced — not duplicated — below.

## Working rules

### Branching and PRs

Canonical: [`CONTRIBUTING.md`](CONTRIBUTING.md) "Branching and pull
requests". Quick reminders:

- Develop on the branch assigned in the system instructions; create
  it from `main` if it doesn't exist.
- **`git checkout main && git pull origin main` BEFORE creating
  the new branch.** Cycle 50 surfaced the cost of skipping this:
  every PR after the first in a multi-PR session got a
  `CHANGELOG.md` merge conflict because the branch was cut
  from a stale `main`. The conflict-resolution + force-push
  cycle wastes a full CI round-trip per occurrence (~3 min).
  Always rebase the local main first.
- **One concern per PR.** Multi-PR sequences are preferred over
  bundles (Stage 7 ran 11 sequenced PRs A → K).
- Conventional Commits for branch names (`claude/<slug>`,
  `feat/<slug>`, `fix/<slug>`, ...) and PR titles.
- Squash-merge default. Delete the source branch (local + remote) after.
- Update [`CHANGELOG.md`](CHANGELOG.md) `[Unreleased]` for any
  user-visible change. **Append at the BOTTOM of the
  `[Unreleased]` section, not the top.** Reverse-chronological
  reading order is given by the per-entry `### Cycle N PR-X
  (date):` headers, not by file position. Bottom-append means
  two simultaneous PRs only conflict when they both touch the
  exact same trailing line — much rarer than top-insert, where
  every PR conflicts with every other PR's anchor by
  construction.

### CI failures — ask for the log, don't guess

When a CI check fails on a PR, **ask the user to paste the relevant
log slice** rather than guessing at the failure cause. **This
request MUST be made via the `AskUserQuestion` tool, never plain
text** — `AskUserQuestion` pushes a phone notification so the
maintainer is alerted even away from the desk; a plain-text request
sits silently in the transcript and stalls the loop (maintainer
instruction 2026-05-16, after several CI-log asks were sent as plain
text). This is **not a judgment call about whether the maintainer
seems present** — always use `AskUserQuestion` for a CI-failure log
request. Frame the question with the failing-job URL + the exact
strings to search (screen-reader-friendly, see below) as the
question/options text. The local sandbox blocks
`productionresultssa*.blob.core.windows.net` and `api.github.com`,
so you cannot fetch CI run logs directly via `WebFetch` or `curl` —
both 403. The GitHub MCP tools don't return job logs either.
Without the user's paste, any fix is inferred from the diff alone —
which produces unfocused fixup commits that may miss the real issue.
PR #132 demonstrated the failure mode: two speculative fixes
(`Process.Start` nullness + sequence-in-match-arm refactor) were
both wrong; the actual cause was an `FS0039` record-label
resolution issue only visible in the build log.

**Request format — the maintainer uses a screen reader.** Long
logs aren't `Ctrl+F`-searchable on a screen reader. In the
`AskUserQuestion` body, either:

- Ask for the full log paste and grep server-side via `Bash`
  against the pasted content, OR
- Tell the maintainer **exactly which strings to look for**
  (`error FS`, `error MSB`, `Failed!`, `Build FAILED`, the test
  name) and request the surrounding 5–10 lines.

The second option is usually faster for the maintainer. **Do not
push speculative fixups** — the cost of one wrong fix is multiple
minutes of wasted CI cycle plus a noisier git history. (A flaky
run is the exception to "investigate the diff": if the failure is
unrelated to the change — infra/runner/network flake — a re-run
clears it; the maintainer may just say "flaky, green now", in
which case proceed to merge.)

### Diagnostic logs — request chunks via the in-app menu

`Ctrl+Shift+D`'s full diagnostic snapshot is comprehensive but
can be multi-megabyte after a Claude session. Cycle 29b NVDA
validation 2026-05-09 produced a bundle that crashed the
maintainer's iOS chat app on paste. Cycle 43a (2026-05-11)
shipped in-app chunking infrastructure so the maintainer can
produce paste-safe focused slices without shell-quoting `findstr`
or `Select-String` — both of which are friction for keyboard-only
/ screen-reader usage. **Default to the menu items**:

- **`Diagnostics → Grep diagnostics...`** — opens a dialog
  (pattern, case-sensitive checkbox, regex checkbox,
  context-lines spinner). Greps the lightweight diagnostic
  bundle in-memory; clipboard payload is capped at 60 KB with
  full untruncated text written to
  `%LOCALAPPDATA%\PtySpeak\extracts\grep-<slug>-<timestamp>.txt`.
  Use this whenever you want a pattern match — replaces every
  `findstr` recipe.
- **`Diagnostics → Copy Latest Bundle to Clipboard`** — fast
  current-state snapshot (~100 ms; skips the diagnostic battery,
  so much faster than `Ctrl+Shift+D`'s ~10 s). Use when you want
  the broad picture without running test commands.
- **`Diagnostics → Extract → By Recency → Last 50 Log Lines`** —
  tail of the active FileLogger log.
- **`Diagnostics → Extract → By Event Type → Errors and Warnings`** —
  log entries with `Semantic=ErrorLine`, `WarningLine`, or
  `ParserError`.
- **`Diagnostics → Extract → By Bundle Section → Active Config`** —
  current `config.toml` content.
- **`Diagnostics → Extract → Snapshot → Version Header`** —
  version + OS + .NET + PID + active shell (< 1 KB).

**Asking the maintainer for an extract.** Replace
"please paste me the FileLogger log filtered for Semantic=ErrorLine"
with "press `Alt`, arrow to Diagnostics → Extract → By Event Type
→ Errors and Warnings, press Enter, paste me the clipboard." The
NVDA announce confirms size + extract-file path, so if iOS chat
chokes on the paste the maintainer can navigate to the extract
file path the announce mentioned and share that instead.

**Asking for a custom pattern.** Replace any `findstr`/
`Select-String` recipe with: "press `Alt`, arrow to Diagnostics →
Grep Diagnostics, press Enter, type `<pattern>` and press Enter,
paste me the result." If the pattern needs regex semantics, tell
the maintainer to tick the "Treat pattern as regex" checkbox
before pressing Enter.

**Shell-script fallback (deprecated; use only if the menu items
are unavailable, e.g. on a build before Cycle 43a):**

```
:: cmd
findstr /C:"Semantic=ErrorLine" "%LOCALAPPDATA%\PtySpeak\logs\<file>.log" > excerpt.txt
:: then have the maintainer paste excerpt.txt (typically much smaller)
```

```
# PowerShell
Select-String -Pattern "Color=red" -Path "$env:LOCALAPPDATA\PtySpeak\logs\<file>.log" -Context 3,3 |
    Out-File excerpt.txt -Encoding utf8
```

If the bundle is genuinely needed in full and even the extract
file is too large for the chat channel, ask whether the
maintainer can share via gist / file attachment / paste service
— paste-into-chat is the long-standing failure mode the Cycle 43a
extractors are designed to sidestep but cannot eliminate for
truly enormous artifacts.

### PR creation and webhook subscription

- **Create PRs via `mcp__github__create_pull_request`**, not
  `gh pr create` or git push alone. The MCP call auto-subscribes the
  session to PR activity (`<github-webhook-activity>` events for
  CI failures, comments, and merges land in the conversation
  automatically). Other PR-creation channels do not trigger the
  subscription.
- **Webhook events are unreliable in this maintainer's environment —
  poll CI every 60 seconds.** Confirmed across many sessions: PR
  activity webhooks frequently fail to arrive when CI completes,
  leaving the agent stuck in "waiting for events that won't come".
  The workaround: after pushing a PR (or a fixup commit), start a
  poll loop that calls `mcp__github__pull_request_read` with
  `method: get_check_runs` every 60 seconds until every check's
  `status` is `completed`. Implementation: `Bash` with
  `run_in_background: true` running `sleep 60`; the harness
  notifies on completion; check status; repeat. **Don't poll faster
  than 60s** — it wastes API calls, clutters the chat, and the
  actual CI feedback cadence is multi-minute. **Don't sleep longer
  between polls** — the maintainer's working rhythm depends on
  prompt CI follow-up. Webhook events still arrive sometimes;
  treat them as a free early-exit signal but don't depend on them.
- **Docs-only PRs may merge after only the markdown link check
  passes** — skip the slow Windows build/test. The Windows build
  cannot fail on pure-markdown changes; the link checker catches
  the real risk (broken internal / external links silently rotting
  the docs over time). "Docs-only" is **strictly** defined:
  - **Allowed**: `*.md`, `CHANGELOG.md`, `README.md`, `LICENSE`,
    `SECURITY.md` (any file extension `.md` or top-level
    `LICENSE`).
  - **Disallowed** (any change to these forfeits the fast-merge):
    `.github/workflows/**`, `src/**`, `tests/**`, `spec/**`,
    `scripts/**`, `*.fs` / `*.fsproj` / `*.cs` / `*.csproj` /
    `*.xaml` / `*.toml` / `*.yml` / `*.yaml`.
  - **Verify scope** by checking the PR's file list before applying
    this rule (`mcp__github__pull_request_read` with
    `method: get_files`). A "docs-only" PR that secretly touches a
    workflow file, a sample consumed by a test, or a CHANGELOG
    when a changelog gate is enabled must wait for full CI. When
    in doubt, wait for everything.
  - Still wait for the link check itself — don't merge a docs PR
    while the link check is `in_progress`. It's the one signal
    that matters.
- **Standing merge rule:** once CI is green on a PR (or, for
  docs-only PRs, the link check is green per the rule above),
  merge it without re-asking (per maintainer authorization). Use
  squash-merge. Update local main + delete the local branch after.
- **Preferred response style:** simple and succinct, especially on
  CI-completion announcements. When all checks are green,
  "all three green, merging" is sufficient — no play-by-play
  summary needed.
- **Fixup-commit rhythm for CI failures:** a `git push` to the open
  PR's branch auto-extends the PR with the new commit and re-runs
  CI. The squash-merge convention combines original + fixup into
  a single canonical commit on `main`. **Don't open a new PR for a
  fixup.**

### Tooling

- **No `dotnet` in the dev sandbox.** You can write F# code but
  cannot `dotnet build` or `dotnet test` locally. Validation falls
  to CI on Windows-latest. Compile-time bugs surface only after a
  push and each CI round-trip is a few minutes. **Read each file
  twice before pushing** — a missed `let rec`, an unused `open`,
  a typo in a string literal, or a missing record-type annotation
  becomes a multi-minute round-trip otherwise.
- **F# struct field ordering for P/Invoke** can't be locally
  validated against `Marshal.SizeOf`. Cross-check carefully against
  the C header order; existing
  [`src/Terminal.Pty/Native.fs`](src/Terminal.Pty/Native.fs) is the
  reference.
- **No `gh` CLI** — use `mcp__github__*` tools.
- **Use `Read`/`Edit`/`Write` for file operations**, not `cat`/
  `sed`/`echo` via Bash. The dedicated tools render better in the
  UI and avoid shell-quoting hazards.
- **Use the GitHub MCP tools** (`mcp__github__*`) for all PR / issue
  / branch / commit work. Repository scope is restricted to
  `kylekeane/pty-speak`.

### Sandbox / runtime constraints

- **Tag pushes 403.** The local git proxy returns 403 on any ref
  under `refs/tags/`. Branches push fine. The only way to land a
  tag is for the maintainer to push from their workstation —
  hence the "Pending checkpoint tags" table in
  [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md). Stage the exact
  push commands there for the maintainer to sweep.
- **GitHub MCP can disconnect mid-session and may not reconnect.**
  When that happens:
  - **Webhook events** (`<github-webhook-activity>`) often keep
    flowing — they're a separate channel and survive MCP
    disconnects.
  - For state queries / merges, fall back to "ask the maintainer".
  - **Manual PR-create fallback:** push the branch and send the
    maintainer
    `https://github.com/KyleKeane/pty-speak/compare/main...<branch>?expand=1`
    plus a suggested title and body. Maintainer opens the PR
    manually. Faster than waiting for MCP to reconnect for one PR.
- **Stream idle timeouts on large writes.** Files over ~600 lines
  occasionally trigger "Stream idle timeout — partial response
  received" during a single `Write` call. Mitigation: write large
  files in 100–200 line chunks (initial `Write` for the skeleton,
  then `Edit` to append sections), or via shell heredocs that
  append in pieces.
- **Verify diff stat before commit.** `git diff --stat` shows scope
  at a glance. If the stat matches the expected scope (~5 files,
  ~150 lines for a typical PR in this codebase), commit. If it
  surprises you, investigate before committing — caught a couple
  of accidentally-staged files in the May-2026 cycle.

## F# 9 + .NET 9 gotchas

**Canonical:**
[`CONTRIBUTING.md`](CONTRIBUTING.md) section 'F# gotchas learned in
practice'. Read it before writing F# in this codebase.

Index of what is there (one line each so you know what to look for):

- **F# 9 nullness annotations** at .NET API boundaries
  (`FS3261` is the canonical CI failure). `string | null`
  signatures vs `nonNull` coercion. APIs known to return
  nullable: `Environment.GetEnvironmentVariable`,
  `Process.Start(ProcessStartInfo)`, `Path.GetFileName`,
  `Path.GetDirectoryName`, `StreamReader.ReadLine`.
- **`out SafeHandle&` byref interop is silently broken** —
  use `nativeint&` and wrap manually. See `Native.fs`.
- **`let rec` for self-referencing class-body bindings** —
  `FS0039` is misleading.
- **`internal` (not `private`) for companion-module access** —
  factory pattern needs internal.
- **Discriminated-union access from C#** — `IsXxx` predicates +
  `.Item` / `.Item1` / `.Item2` payload accessors.
- **F# delegate conversion only fires for `Func` / `Action`** —
  `Predicate<T>` doesn auto-convert (bit PR #131).
- **Record literal type inference fails when the record module
  is not auto-opened** — annotate the binding (`FS0039` at the
  field; bit PR #132).
- **NUL bytes in F# source** — use the explicit Unicode escape,
  not a raw NUL byte.
- **Sequence-in-match-arm** — extract a named local function
  rather than relying on the offside rule under
  `TreatWarningsAsErrors=true`.
- **WPF gotchas** — `FrameworkElement` has no `Background`;
  `App.xaml` auto-classification; `<UseWPF>true</UseWPF>` SDK
  globbing.

**Test-fixture foot-gun (also in CONTRIBUTING):** literal
`0x1B` (ESC) bytes in test source files survive Edit-tool
round-trips but can silently be stripped by other tooling.
`VtParserTests.fs` and `ScreenTests.fs` use them. Verify with
`grep -c $'' <file>` after any edit.

## Project conventions

Most rules in this section are pointers into the canonical doc.
Material that is NOT canonical here continues to be authoritative
in CONTRIBUTING.md / spec/tech-plan.md / docs/PROJECT-PLAN-2026-05-09.md
per docs/DOC-MAP.md.

### Accessibility outcomes are the acceptance criteria

Canonical: [`CONTRIBUTING.md`](CONTRIBUTING.md) section
'The non-negotiable accessibility rules' +
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md).

Quick rule: a feature that compiles + passes unit tests is **not
done** until the maintainer has run the NVDA matrix row in
ACCESSIBILITY-TESTING.md. When shipping a stage, ensure the matrix
row is in place + the manual procedure is concrete enough that
someone other than the author can run it.

### Walking-skeleton discipline

Stage N ships only after Stage N-1 is validated end-to-end. Don't
bundle stages or merge ahead of validation. The 11-PR Stage 7
sequence (PR-A through PR-K) is the discipline scaled down — each
PR independently CI-gated and NVDA-validated where applicable.

### Cycle closure audit

**After the final PR of a multi-PR cycle merges, before
opening a new cycle, ship a "cycle closure audit" PR.** Plan
for it from the first PR of the cycle — every multi-PR cycle
plan should explicitly list "closure audit" as the final
item, alongside the NVDA matrix walk. The audit sweeps for
drift between the now-shipped state and the docs / supporting
artifacts so the next session reads a coherent repo, not a
mid-cycle snapshot.

Without it, transitional language ("PR-C is next", "stays in
source for deletion in PR-D", "validation gate is N-PRA-1")
accumulates as confusing history. Cycle 46 retro
(`docs/CYCLE-46-NEXT-STEPS.md`) is the cautionary tale: PR-D
shipped, but until the closure audit, four docs still said
"PR-D is next" and a fifth still said "the legacy types
stay in source for deletion in PR-D." Future sessions would
have had to reverse-engineer the truth.

The audit covers, at minimum:

- **`docs/SESSION-HANDOFF.md`** — "Current state" reflects
  completion; "Next stage" drops the cycle-internal PRs.
- **`docs/PROJECT-PLAN-YYYY-MM-DD.md`** — shipped items
  removed from "Primary track"; validation-gate section
  consolidates per-PR gates to a single cycle-level gate;
  §"Change log" gains a closure entry.
- **`CLAUDE.md` §"Current sequencing"** — per-PR breakdown
  collapses to a single cycle-level line (or short PR-A→D
  bullet block under one cycle heading).
- **The cycle's in-flight scoping doc** (if one exists, e.g.
  `CYCLE-46-NEXT-STEPS.md`) — mark historical with a banner
  at the top + a "things this doc says that are now untrue"
  list. Don't delete; the delta between the planning and
  the shipped diff is itself a useful artifact.
- **ADRs** — flip "Accepted" → "Accepted / Implemented" (or
  add per-PR Status-notes entries) so the ADR records the
  ship outcome.
- **`docs/ACCESSIBILITY-TESTING.md`** — gains a Cycle N
  matrix section; per-PR gates (e.g. Cycle 46-PRB-1,
  Cycle 46-PRC-1) consolidate to a single Cycle 46-1 row
  (or row-set) once the cycle ships as a whole.
- **`docs/ARCHITECTURE.md` / `docs/DOC-MAP.md` /
  `README.md`** — module map / doc routing / feature-list
  entries reflect the new surface, not the old one.
- **Code comments** — `grep -rn '<DeletedTypeName>' src/
  tests/` catches lingering type references. Comments that
  say "X stays in source until PR-D" should now say "PR-D
  deleted X" (or be dropped entirely if they no longer add
  value).
- **`CHANGELOG.md`** — optional closure note if the cycle
  was substantive enough that the `[Unreleased]` section
  reads as a single coherent ship; usually the per-PR
  entries are enough.

Search patterns that catch most drift:

```
grep -rn '<DeletedTypeName>' src/ tests/ docs/
grep -rn 'next)\|after PR\|stays in source\|deletion in PR-' docs/
grep -rn '<CycleN>-PR' docs/ tests/
```

The audit PR is typically docs-only (eligible for the
fast-merge lane via the markdown link checker) unless code
comments need editing. Squash-merge as usual; the closure
PR's commit on `main` is a useful "Cycle N done" bookmark
and the natural snapshot point for the maintainer's
release-build smoke test.

### Spec immutability

[`spec/`](spec/) is the design substrate. Don't edit it without
explicit ADR-style maintainer authorisation. Precedent: chat
2026-05-03 retroactive Stage 4a/4b/5a authorisation; PR-K (env-scrub
allow-list expansion) ran the same flow before touching §7.2.

### Tests

Canonical: [`CONTRIBUTING.md`](CONTRIBUTING.md) section 'Tests'
+ 'Test fixtures: CSI / OSC / DCS sequences'.

Quick reminders:
- xUnit 2.9.x + FsCheck.Xunit 3.x. Live in `tests/Tests.Unit/`.
- Backtick test names (```should do X when Y```).
- New test fixture files require a `<Compile Include=...>` entry
  in `tests/Tests.Unit/Tests.Unit.fsproj`.
- Cross-assembly `internal` access via
  `[<assembly: InternalsVisibleTo("PtySpeak.Tests.Unit")>]`.
- Literal `0x1B` / NUL bytes in source are foot-guns; verify with
  `grep -c $'' <file>` after edits.

### Logging discipline

Canonical: [`docs/LOGGING.md`](docs/LOGGING.md) +
[`SECURITY.md`](SECURITY.md) PO-5 row.

Quick rule: **never log secrets**, even names — env-var names like
`BANK_API_KEY` are themselves sensitive. Log counts. Sanitise
announcement-bound exception messages through
`Terminal.Core.AnnounceSanitiser.sanitise`.

### New features ship with their diagnostic triggers

Established 2026-05-14 after PR-I (history-recall announce)
shipped with a thin `RowLen={RowLen} DraftLen={DraftLen}` log
and the maintainer reported a non-deterministic mis-announce
that the lean log couldn't localise. Required a follow-up
PR-J just to add the diagnostic args.

**Rule**: when a feature introduces a new announce site,
state-machine transition, screen-read, or any other path
whose behaviour the maintainer would need to triage from a
`Ctrl+Shift+D` bundle, ship the diagnostic args **in the
same PR**, not as a follow-up.

Minimum signal for an announce site:

- The **announce body** (logged at Debug if it contains user
  data; at Information if structural). PR-I logs the
  recalled draft text at Debug, matching the existing
  `UserInputBuffer captured on Enter: ... Text=...` log
  precedent — both go to NVDA out loud so no new
  sensitivity boundary is crossed.
- The **decision inputs** that determined the announce —
  for PR-I the prompt row, `PromptText`, and whether the
  prefix-strip matched. For sub-prompt announces, the
  accumulator length + last-line vs full-body choice.
- Counts (lengths, indices, line counts) at Information so
  the always-on bundle has enough to confirm the path fired
  even when Debug logging is off.

Bundle-friendly format:

- Use templated args (`{Foo}={Foo} {Bar}={Bar}`), not
  interpolated strings, so the FileLogger can index them.
- Prefix with the PR ID (`PR-I history-recall details.`)
  so a grep on a noisy bundle quickly finds your log lines.
- Keep templates **single-line** — multi-line bodies break
  the grep tooling on the bundle.

Test fixtures (the canonical-interactions corpus + the
`Diagnostics → Test ...` menu items) also count as
diagnostic triggers; if a new feature has a deterministic
shell-side reproducer, add a test-corpus entry so the
maintainer doesn't have to script it ad-hoc.

### App-reserved hotkey contract

Canonical: [`spec/tech-plan.md`](spec/tech-plan.md) section 6 +
[`src/Views/TerminalView.cs`](src/Views/TerminalView.cs)
`AppReservedHotkeys` table.

Quick rule: `OnPreviewKeyDown` MUST NOT mark `e.Handled = true`
for any reserved gesture — WPF's `InputBindings` machinery on the
parent window must run first. Stage 6's keyboard filter ordering
is load-bearing and pinned by xUnit + behavioural tests.

Currently shipped (orientation reference; spec section 6 is canonical):

- `Ctrl+Shift+U` — Velopack auto-update (Stage 11)
- `Ctrl+Shift+D` — full automated diagnostic battery (Cycle 25b
  bundles its diagnostic-battery log + active FileLogger log +
  config.toml + redacted environment into a single dated
  snapshot file at `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\
  snapshot-<yyyy-MM-dd-HH-mm-ss-fff>.txt` and copies the bundle
  to clipboard, so a paste-back to triage chat carries the full
  triage context in one block)
- `Ctrl+Shift+R` — draft-a-new-release form launcher
- `Ctrl+Shift+P` — open the pty-speak data folder
  (`%LOCALAPPDATA%\PtySpeak\`, parent of `\logs`,
  `\sessions`, and `config.toml`) (Cycle 25a; replaces the
  old `Ctrl+Shift+L`)
- `Ctrl+Shift+E` — edit `config.toml` in the default app;
  auto-creates with sensible defaults if missing (Cycle 25a)
- `Ctrl+Shift+1` / `+2` / `+3` — hot-switch the spawned shell
  (`+1`=cmd / `+2`=PowerShell / `+3`=Claude; PR-J reordered to
  put PowerShell next to cmd as the diagnostic control shell)
- `Ctrl+Shift+H` — health-check announce: **informational
  version incl. `+<git-short-sha>` build identity** (Cycle 52
  R4-followup — a local build always reports
  `0.0.1-preview.1`, so the `+sha` is the only thing that
  confirms which commit a dogfood is running; auto-embedded by
  the `SetGitShortShaSourceRevision` target in
  `Directory.Build.props`, match it to `git rev-parse --short
  HEAD`). When triaging a dogfood, **always have the maintainer
  read the `Ctrl+Shift+H` Version line first** to rule out a
  stale local build before chasing a "regression". Then shell +
  PID + alive, log level, reader staleness, queue depths (PR-F +
  PR-J liveness probe)
- `Ctrl+Shift+B` — incident marker boundary line in the log (PR-F)
- `Ctrl+Shift+Y` — copy SessionModel history to clipboard (Cycle 22b);
  paste-friendly structured plain-text dump of all completed
  tuples + any in-flight active tuple. Companion to
  `Ctrl+Shift+D` (which copies the broader diagnostic snapshot
  bundle); `Ctrl+Shift+Y` is the SessionModel-only narrow dump
  for substrate-specific analysis.
- `Ctrl+Shift+S` — announce the active session-log file path
  (Cycle 24e); verbose format `Session log mode <mode>; path
  <full-path>.` for `session_log` / `always`;
  `Session log mode memory_only; no file.` for `memory_only`.
  Companion to `Ctrl+Shift+P` (open the data-folder root)
  and `Ctrl+Shift+D` (which folds the same summary into the
  bundle's `--- SESSION LOG ---` section so a paste-back
  carries it without a separate keystroke).
- `Ctrl+Shift+O` — open the last command's full `OutputText`
  in the default text editor (Cycle 46 post-audit, 2026-05-13).
  Companion to the 800-char tuple-final `Announce` cap in
  `Program.fs` (`OutputAnnounceCapChars`): the cap keeps the
  audible read interruptible for a `dir`-shaped output; this
  hotkey is the escape hatch for hearing / reading the full
  body. Writes a fresh timestamped file under
  `%LOCALAPPDATA%\PtySpeak\extracts\last-output-<ts>.txt` and
  launches it via `Process.Start` with `UseShellExecute=true`
  (the registered `.txt` handler — Notepad by default, or
  whatever the user has configured). Announces "Opening last
  output." on success; "No prior output." when
  `SessionModel.History` is empty.
- `Ctrl+Shift+A` — re-narrate the last command's `OutputText`
  via NVDA, capped at the same `OutputAnnounceCapChars` (800)
  the auto-narrate uses (Cycle 46 post-audit, 2026-05-13). For
  the user who missed the auto-Announce (was speaking, typing,
  switched window). Spoken counterpart to `Ctrl+Shift+O`'s
  text-editor surface. Uses the same `ActivityIds.output`
  activity ID, so NVDA's `MostRecent` processing supersedes
  any other in-flight `pty-speak.output` notification.
  Announces "No prior output." when `SessionModel.History`
  is empty; "Last command produced no output." when the
  latest tuple's `OutputText` is whitespace-only.

Multi-state menu items (Cycle 27 paradigm; menu-only, no
keyboard accelerator):

- View → Logging Level → Information / Debug — flips
  FileLogger min-level (migrated from `Ctrl+Shift+G`).
- View → Earcons → Enabled / Muted — toggles WASAPI earcons
  (migrated from `Ctrl+Shift+M`).

Window-management menu items (Cycle 28; menu-only):

- Window → Close Window — `Window.Close()`. Visual
  `InputGestureText="Alt+F4"` since the OS-level Alt+F4 is
  handled by WPF Window natively, not via
  `AppReservedHotkeys`.
- Window → Exit — `Application.Current.Shutdown()`. Identical
  visible behaviour to Close Window in today's single-window
  app; separate slot future-proofs multi-pane Phase 2 plans.

Reserved (not yet bound):

- `Alt+Shift+R` — Stage 10 review-mode toggle
- `Ctrl+Shift+4` / `+5` / `+6` — additional shells (WSL, Python
  REPL, etc.) per Phase 2 plans


### `AskUserQuestion` is the maintainer's phone-notification surface

The maintainer often steps away from the desk while a Cycle's PR
sequence is in flight. **A plain text reply does not generate a
phone notification — `AskUserQuestion` does.** The harness routes
`AskUserQuestion` interactions through the maintainer's phone, so a
question posed via that tool reaches them away from the desk
whereas regular text output sits silently in the transcript until
they next look.

Use `AskUserQuestion` when:

- A genuine design decision needs to be made before Claude can
  proceed (e.g. "preserve vs drop history-recall entries in
  ContentHistory" — answered 2026-05-14 via this surface).
- A CI failure log slice is needed (per "CI failures — ask for
  the log" rule above). **Always** — not conditioned on whether
  the maintainer seems present. Plain-text CI-log requests
  stalled the loop several times on 2026-05-16; the maintainer
  explicitly asked that these always come through
  `AskUserQuestion` so the phone buzzes.
- The MCP server has disconnected and the manual PR-create
  fallback needs the maintainer to open a compare URL.
- Anything ambiguous in the user's instructions that genuinely
  blocks forward progress.

**Don't** use `AskUserQuestion` for:

- Status updates ("PR-A pushed, waiting for CI") — those are
  plain text.
- Confirming things the autonomy contract in the current
  cycle plan already authorises (writing code, pushing fixup
  commits, merging green PRs, moving to the next PR).
- Closed-form choices where the autonomy contract or
  CLAUDE.md picks an obvious default — go with the default
  and note the choice in the PR body.
- "Ready to start?" / "should I proceed?" preambles — just
  proceed.

The rule of thumb: would the maintainer be annoyed if their
phone buzzed for this? If yes, plain text. If genuinely
blocked, `AskUserQuestion`.

### Literal language — never use "blind" (or "see", "look", "view") as a metaphor

**Non-negotiable.** The maintainer is blind. Do **not** use
"blind" to mean *without information / unverified / guessing*
("flying blind", "a blind fix", "blindly pushing"). "Blind"
describes a person's sensory reality, not a lack of data. Using
it as a pejorative metaphor — especially in this project, to
this maintainer — is offensive. This rule recurred across
sessions purely because it was never written down here; it is
now, so there is no excuse.

State the **literal** condition instead:

- "no local compiler / `dotnet` in the sandbox — CI is the
  only build signal"
- "locally unverifiable; the CI round-trip is the check"
- "without the diagnostic bundle I'd be guessing — please
  paste the log slice"
- "unverified assumption" / "speculative" / "untested"

Apply the same literalness to incidental "see / look at / view"
when precision is easy ("the log shows", "per `file:line`",
"the bundle reports") — not a hard ban on common verbs, but
prefer the concrete reference. The point: be literal about the
actual issue; never reach for sight/blindness as shorthand.

### The maintainer is a screen-reader user — no GUI dialog walks

The maintainer uses NVDA. **Never** propose a fix or workflow that
requires walking through a GUI dialog tree (System Properties →
Advanced → Environment Variables → New…, Task Manager →
Processes-tab chevron-expand, "right-click → Properties → Details
tab", etc.). Every such suggestion is a multi-minute frustration
sink because dialog trees are not always cleanly screen-reader-
navigable, and the maintainer has explicitly called this out as
unacceptable.

Instead, surface keyboard- or shell-only equivalents:

- Setting a user env var → `setx VAR value` from cmd. (Persists
  across sessions; needs a fresh process to pick up.)
- Setting a process-scoped env var → `set VAR=value` from cmd
  before launching the child.
- Inspecting running processes → `tasklist | findstr /I name`
  from cmd, or
  `Get-Process -Name name -ErrorAction SilentlyContinue` from
  PowerShell. **Use these yourself first** before asking the
  maintainer to check anything (see the diagnostic-recipes section
  below).
- Killing a stuck process → `taskkill /PID 1234 /F` from cmd, or
  `Stop-Process -Id 1234 -Force` from PowerShell.
- Inspecting / setting registry keys → `reg query` / `reg add`
  from cmd, or `Get-ItemProperty` / `Set-ItemProperty` from
  PowerShell.

If a CLI / keyboard equivalent doesn't exist, say so explicitly
and ask the maintainer how they want to proceed — don't paper
over the gap with a dialog walk and hope.

### Diagnostic recipes — triage without bothering the maintainer

A standing problem in this codebase is "is the child shell
actually running, or did it exit silently?". Before asking the
maintainer to verify by hand, run these yourself via the user's
existing `Ctrl+Shift+D` flow OR via direct CLI commands. Same
recipes Ctrl+Shift+D uses internally.

**Inline child-process check (the everyday triage tool):**

`Ctrl+Shift+D` runs the autonomous diagnostic battery (which
internally calls F# `enumerateShellProcesses` —
`src/Terminal.App/Diagnostics.fs`) and writes the per-process
counts into both the diagnostic-battery log file AND the
combined snapshot bundle that lands in clipboard +
`%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\`. For Claude
sessions reasoning about what the user might be seeing, the
equivalent CLI commands are:

```
:: Enumerate shell processes by name (run in cmd)
tasklist | findstr /I "cmd.exe powershell.exe pwsh.exe claude.exe Terminal.App.exe"

:: Or in PowerShell:
Get-Process -Name cmd, powershell, pwsh, claude, Terminal.App -ErrorAction SilentlyContinue |
    Group-Object Name |
    Select-Object @{Name='Name'; Expression='Name'}, Count
```

**Liveness probe for a specific PID:**

`Ctrl+Shift+H` (PR-F + PR-J) announces the current child's PID
and an `alive`/`dead` flag computed via
`Process.GetProcessById(pid)`. The corresponding CLI form:

```
:: cmd
tasklist /FI "PID eq 1234"
:: PowerShell
Get-Process -Id 1234 -ErrorAction SilentlyContinue
```

**Heartbeat trail:**

The 5s background heartbeat
(`runHeartbeat` in `Program.fs`) writes a line per tick to the
active log including `Pid={Pid} Alive={Alive}`. When triaging
"why did NVDA stop reading?" post-hoc, ask for the log slice
covering that time window (`Ctrl+Shift+D` bundles the active
FileLogger log into clipboard + a dated snapshot file) and
grep `Heartbeat` in the bundle's `--- FILELOGGER ACTIVE LOG ---`
section to find the moment `Alive=False` first appears. That's
the precise wedge timestamp.

**Process tree diagnostic:**

`scripts/test-process-cleanup.ps1` is the interactive complement
to `Ctrl+Shift+D`'s autonomous battery (Cycle 25b decoupled it
from the hotkey because it requires the maintainer to physically
close pty-speak via Alt+F4 / X-button). The script enumerates
Terminal.App.exe + its parent-PID children + sibling shell counts
(`Get-ShellProcessSnapshot`), prompts the maintainer to close
pty-speak, and reports whether anything was orphaned. Run it
directly from PowerShell when diagnosing Job Object cascade-kill
regressions, NOT for "is the child alive right now?" — that's the
inline check above. **Cycle 26c also surfaces this script via the
app menu** (Diagnostics → Test Process Cleanup; no keyboard
accelerator) — the menu item invokes `runTestProcessCleanup` in
`Program.fs`, which spawns the same script in a separate
PowerShell window via `ProcessStartInfo`.

## Current sequencing (May 2026)

Canonical:
[`docs/PROJECT-PLAN-2026-05-12.md`](docs/PROJECT-PLAN-2026-05-12.md) +
[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md). The plan
catalogues candidate next cycles + sequencing rationale; the
handoff names the current state in ~150 lines. This index just
points at the cycle headline.

- **Cycle 45** (PRs #263–#270, 2026-05-12) shipped the
  `ContentHistory` + `SpeechCursor` aural substrate.
- **Cycle 45c** (PRs #274–#278, 2026-05-12) deleted the
  pre-Cycle-45 pathway pipeline (`StreamPathway` /
  `LinearTextStream` / `DisplayPathway` / `TuiPathway` /
  `PathwaySelector`). ~5,000+ LOC removed.
- **Cycle 46** (PRs #287–#291, 2026-05-12 → 2026-05-13)
  flipped the **channel** for terminal output from UIA
  `RaiseNotificationEvent` to a UIA TextEdit caret on
  `TerminalView`. See
  [ADR 0002](docs/adr/0002-uia-textedit-caret-output.md).
- **Cycle 47 dogfood batch** (PRs #295–#305, 2026-05-13,
  post-preview.114 → post-preview.117) layered byte-stream
  patches on top of Cycle 46's substrate flip — typing-window
  gates, prefix-trim, mid-eval earcon, marker-label
  parallelism. preview.117 dogfood confirmed the patches
  don't compose; root cause is that announce routing is
  byte-stream driven, not semantic.
- **Cycle 48** (PRs #306–#311, 2026-05-13) added the
  semantic state machine on top of the substrate per
  [ADR 0003](docs/adr/0003-shell-interaction-state-machine.md):
  `ShellInteraction` with `Composing` / `Executing` states,
  `EntrySource` provenance on every `ContentHistory.Entry`,
  `UserInputBuffer` byte-stream tracking, sub-prompt-announce
  via state-machine transition, SpeechCursor filter for
  `UserInputEcho`. Idle-flush announce body retired.
- **Cycle 49** (PRs #313–#320 + this-PR-Z, 2026-05-14)
  refined speech narration on top of Cycle 48's state
  machine. PR-A SpeechCursor blank-collapse; PR-B post-
  Enter delta announce (text-prefix-trim, superseded by
  PR-F); PR-C UIA `TextChanged` review-cursor refresh;
  PR-D prompts navigable in manual SpeechCursor; PR-F
  sub-prompt last-line announce + line-count post-Enter
  slicing; PR-G removed the duplicate-command-silencing
  prefix-trim relic; PR-H reshaped `test-01-echo.cmd` with
  explicit `Line 1 of 3` labelling; PR-I Up/Down arrow
  history-recall announce via new `pty-speak.input-assistant`
  activity ID. Deferred: `EntrySource.DraftInputRecall`
  ContentHistory tagging (Cycle 49 E3) — substrate
  refinement, no audible user-visible bug behind it after
  PR-I's screen-read approach. Cycle plan archived at
  [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md) (now
  historical).
- **Cycle 51** (PRs #337–#344, 2026-05-14) — IOCell pivot
  ([ADR 0004](docs/adr/0004-iocell-model-for-shell-interaction.md)):
  `IOCell` + schemaVersion-2 JSONL + round-trip reader, then
  the PR-X/Y/AA/AB/AC/AD/AE dogfood-driven announce-patch
  sequence. SHIPPED; its compensations are what Cycle 52 R3b
  retired. `CYCLE-51-PLAYBOOK.md` HISTORICAL.
- **Cycle 52** (PRs #345–#372, 2026-05-15 → 2026-05-16) —
  three-layer re-foundation
  ([ADR 0005](docs/adr/0005-osc133-shell-integration.md) +
  [ADR 0006](docs/adr/0006-three-layer-refoundation.md)).
  **R1–R4 + R4c complete & CI-green** (R1 seam · R2 cmd
  OSC-133 · R3a/b precedence + KI-R2-1 fix · R3c/d/e
  watermark · R4a/b purify+enforce · R4c cmd boundary-only
  deferred `;D`). **Standing decision (2026-05-16): cmd
  announce-heuristic FREEZE** — substrate sound; do not
  patch cmd announce heuristics before R5 (ADR 0006
  §"Deferred to R6+" decision note). Per-PR detail:
  CHANGELOG Cycle 52 + ADR 0006 R-stage list.
- **Validation gate**: one consolidated **R1–R4 + R4c
  foundation dogfood** (installed preview of post-`b14667f`
  `main`; matrix rows `52-1`/`R3c`/`R3c-multi`/`R3d`/`R3e`/
  `R4c` in
  [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)).
  Blocks R5 start; maintainer's court.
- **Next** = **R5 PowerShell adapter** → R6 feature unlock
  → R7 claude + closure. **Start-here for active work:
  [`docs/CYCLE-52-R5-PLAYBOOK.md`](docs/CYCLE-52-R5-PLAYBOOK.md)**
  — the self-contained R5 brief (R5a wire-the-seam → R5b
  PowerShellAdapter → R5c exit-code/`;C` → R5d closure) +
  the P1–P5 pre-R5 old-code pruning sequence + ops. Other
  backlog (independent): 45g `ShellPolicy` consolidation;
  full deferral list in ADR 0006 §"Deferred to R6+".

## When in doubt

- **Spec deviation** — flag and ask. The chat-2026-05-03 Stage
  4a/4b/5a authorization is the precedent for retroactive
  formalisation; do not deviate silently.
- **Architecture decisions** that span multiple stages — defer to
  the maintainer; don't decide unilaterally.
- **Risky operations** (force-push, rebase, branch deletion on
  main, mass file rewrites) — confirm before proceeding.
- **Anything ambiguous in the user's instructions** — surface it
  via `AskUserQuestion` or a clarifying message, don't assume.
