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
3. **[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)** — the
   bridge between sessions; "where we left off", in-flight branches,
   pre-digested implementation sketches for the next stage.
4. **[`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md)**
   — strategic plan that supersedes Stages 7-10 sequencing.
5. **[`spec/tech-plan.md`](spec/tech-plan.md)** §N for the canonical
   spec of whatever stage you're working on.
6. **[`CONTRIBUTING.md`](CONTRIBUTING.md)** — canonical PR shape,
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
- **One concern per PR.** Multi-PR sequences are preferred over
  bundles (Stage 7 ran 11 sequenced PRs A → K).
- Conventional Commits for branch names (`claude/<slug>`,
  `feat/<slug>`, `fix/<slug>`, ...) and PR titles.
- Squash-merge default. Delete the source branch (local + remote) after.
- Update [`CHANGELOG.md`](CHANGELOG.md) `[Unreleased]` for any
  user-visible change.

### CI failures — ask for the log, don't guess

When a CI check fails on a PR, **ask the user to paste the relevant
log slice** rather than guessing at the failure cause. The local
sandbox blocks `productionresultssa*.blob.core.windows.net` and
`api.github.com`, so you cannot fetch CI run logs directly via
`WebFetch` or `curl` — both 403. The GitHub MCP tools don't return
job logs either. Without the user's paste, any fix is inferred from
the diff alone — which produces unfocused fixup commits that may
miss the real issue. PR #132 demonstrated the failure mode: two
speculative fixes (`Process.Start` nullness + sequence-in-match-arm
refactor) were both wrong; the actual cause was an `FS0039`
record-label resolution issue only visible in the build log.

**Request format — the maintainer uses a screen reader.** Long
logs aren't `Ctrl+F`-searchable on a screen reader. Either:

- Ask for the full log paste and grep server-side via `Bash`
  against the pasted content, OR
- Tell the maintainer **exactly which strings to look for**
  (`error FS`, `error MSB`, `Failed!`, `Build FAILED`, the test
  name) and request the surrounding 5–10 lines.

The second option is usually faster for the maintainer. If the
user isn't around, queue the question and wait. **Do not push
speculative fixups** — the cost of one wrong fix is multiple
minutes of wasted CI cycle plus a noisier git history.

### PR creation and webhook subscription

- **Create PRs via `mcp__github__create_pull_request`**, not
  `gh pr create` or git push alone. The MCP call auto-subscribes the
  session to PR activity (`<github-webhook-activity>` events for
  CI failures, comments, and merges land in the conversation
  automatically). Other PR-creation channels do not trigger the
  subscription.
- **Standing merge rule:** once CI is green on a PR, merge it
  without re-asking (per maintainer authorization). Use squash-merge.
  Update local main + delete the local branch after.
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
in CONTRIBUTING.md / spec/tech-plan.md / docs/PROJECT-PLAN-2026-05.md
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
- `Ctrl+Shift+D` — process-cleanup diagnostic launcher with
  inline shell-process snapshot announce (PR-J)
- `Ctrl+Shift+R` — draft-a-new-release form launcher
- `Ctrl+Shift+L` — open logs folder
- `Ctrl+Shift+;` — copy active log to clipboard
- `Ctrl+Shift+1` / `+2` / `+3` — hot-switch the spawned shell
  (`+1`=cmd / `+2`=PowerShell / `+3`=Claude; PR-J reordered to
  put PowerShell next to cmd as the diagnostic control shell)
- `Ctrl+Shift+G` — toggle FileLogger min-level Information ↔ Debug
  (PR-E)
- `Ctrl+Shift+H` — health-check announce: shell + PID + alive,
  log level, reader staleness, queue depths (PR-F + PR-J liveness
  probe)
- `Ctrl+Shift+B` — incident marker boundary line in the log (PR-F)

Reserved (not yet bound):

- `Ctrl+Shift+M` — Stage 9 earcon mute
- `Alt+Shift+R` — Stage 10 review-mode toggle
- `Ctrl+Shift+4` / `+5` / `+6` — additional shells (WSL, Python
  REPL, etc.) per Phase 2 plans


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

The user can press `Ctrl+Shift+D` in pty-speak and NVDA reads back
"Diagnostic snapshot: 1 cmd, 0 powershell, 0 pwsh, 1 claude, 1
Terminal.App. Launching cleanup test." in one announcement —
that's the inline enumeration the F# `enumerateShellProcesses`
helper produces (`src/Terminal.App/Program.fs`). For Claude
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
covering that time window (`Ctrl+Shift+;` copies the active log)
and grep `Heartbeat` to find the moment `Alive=False` first
appears. That's the precise wedge timestamp.

**Process tree diagnostic:**

`Ctrl+Shift+D` launches `scripts/test-process-cleanup.ps1` in a
new PowerShell window for the close-and-recheck flow. That
script enumerates Terminal.App.exe + its parent-PID children +
sibling shell counts (`Get-ShellProcessSnapshot`), then asks the
user to close pty-speak via Alt+F4 / X-button and reports
whether anything was orphaned. Use this when diagnosing Job
Object cascade-kill regressions, NOT for "is the child alive
right now?" — that's the inline check above.

## Current sequencing (May 2026)

Canonical:
[`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md) +
[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md) "Where we left
off". This index just tells you which cycle is active.

- **Stage 7** = validation gate. Shipped 2026-05-03 across 11
  sequenced PRs (A through K + the doc-purpose PR-L). Closes the
  Claude Code roundtrip + env-scrub PO-5; surfaces gaps for the
  framework cycles via [`docs/STAGE-7-ISSUES.md`](docs/STAGE-7-ISSUES.md).
- **Output framework cycle** (Part 3, subsumes original Stages 8+9)
  — research → RFC → eight sub-stages, each with NVDA validation.
  Reads STAGE-7-ISSUES.md as design input.
- **Input framework cycle** (Part 4) — same shape.
- **Stage 10** — review mode + quick-nav, first non-built-in
  consumer of the framework taxonomy.

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
