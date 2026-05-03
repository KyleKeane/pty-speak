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

1. **This file** — the rules below.
2. **[`README.md`](README.md)** — what the project is, status,
   shipped stages.
3. **[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)** — the
   bridge between sessions; "where we left off", in-flight branches,
   pre-digested implementation sketches for the next stage.
4. **[`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md)**
   — strategic plan that supersedes Stages 7-10 sequencing.
5. **[`spec/tech-plan.md`](spec/tech-plan.md)** §N for the canonical
   spec of whatever stage you're working on.
6. **[`CONTRIBUTING.md`](CONTRIBUTING.md)** — PR shape, branch
   naming, F# gotchas learned in practice, fixup-commit rhythm.

## Working rules

### Branching and PRs

- **Develop on the branch assigned in the system instructions for
  this session.** If it doesn't exist locally, create it from `main`.
- **One concern per PR.** When tempted to bundle two improvements,
  split them. Multi-PR sequences (Stage 7 = PR-A → PR-B → PR-C →
  PR-D) are explicitly preferred — merging is cheap, CI gates each
  step independently.
- **Conventional Commits** for branch names (`feat/<slug>`,
  `fix/<slug>`, `chore/<slug>`, `docs/<slug>`, `claude/<slug>`) and
  PR titles (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`,
  `build:`, `ci:`).
- **Squash-merge default.** The merged commit subject becomes the
  canonical history line.
- **Delete the source branch after the squash-merge lands** (both
  remote and local). If GitHub auto-delete is on, only the local
  delete is your job.
- **Update [`CHANGELOG.md`](CHANGELOG.md)** under `## [Unreleased]`
  for any user-visible change.

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

CONTRIBUTING.md §"F# gotchas learned in practice" is the long
catalog. The hot list:

### F# 9 nullness annotations (`<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)

Many .NET APIs return `string?` / `T?` under .NET 9 nullness
annotations. **`FS3261` "non-nullable was expected" is the canonical
CI failure mode** for code that ignores this. Two acceptable
patterns: function signature accepts `string | null` and
pattern-matches at the boundary, OR coerce at the call site with
`nonNull` / inline match.

APIs known to return nullable today:

- `System.Environment.GetEnvironmentVariable` → `string | null`
- `System.Diagnostics.Process.Start(ProcessStartInfo)` →
  `Process | null`
- `System.IO.Path.GetFileName` / `GetDirectoryName` → `string | null`
- `System.IO.StreamReader.ReadLine` → `string | null`

Match-on-null at the call site:

```fsharp
match Process.Start(psi) with
| null -> Error "Process.Start returned null"
| p ->
    use _ = p
    // ... use p (non-null) ...
```

Or accept `string | null` in your own helper signature:

```fsharp
let parseEnvVar (value: string | null) : ShellId option =
    match value with
    | null -> None
    | v -> // v is non-null `string` here
        ...
```

### F# delegate conversion

F# auto-converts lambdas to `System.Func<...>` and `System.Action<...>`
only. Other delegate types (`Predicate<T>`, custom delegates) need an
explicit construction, OR rephrase the API call to use a `Func`-based
overload. xUnit's `Assert.DoesNotContain(IEnumerable<T>, Predicate<T>)`
in particular doesn't auto-convert from F#; use
`List.tryFind ... = None` + `Assert.Equal` instead.

### Record literal type inference

When a record `T` is declared inside a `module M` that is not auto-
opened, an unqualified record literal `{ Field = ... }` outside that
module fails to resolve the field names — F# can't infer which
record type the literal belongs to. **Add an explicit type
annotation** on the binding:

```fsharp
// Fails with FS0039 "record label not defined":
let registry =
    Map.ofList
        [ ShellRegistry.Cmd,
            { Id = ShellRegistry.Cmd
              DisplayName = "fake"
              Resolve = fun () -> Ok "x.exe" } ]

// Works:
let shell : ShellRegistry.Shell =
    { Id = ShellRegistry.Cmd
      DisplayName = "fake"
      Resolve = fun () -> Ok "x.exe" }
let registry = Map.ofList [ ShellRegistry.Cmd, shell ]
```

Bit me on PR #132 (Stage 7 PR-B). The error message is unhelpful —
it points at the field, not the missing type annotation.

### `out SafeHandle&` byref interop is silently broken

Declaring a P/Invoke `out` parameter as `SafeFileHandle&` produces a
`NullReferenceException` at the call site even when the kernel writes
the handle correctly. Use `nativeint&` and wrap manually:
`new SafeFileHandle(p, ownsHandle = true)`. Canonical pattern:
[`src/Terminal.Pty/Native.fs`](src/Terminal.Pty/Native.fs).

### NUL bytes in F# source

Use the explicit `'\u0000'` escape (or `'\x00'`) for NUL char
literals, **not** a raw NUL byte in source. Raw NULs survive Edit
tool round-trips but are brittle to tooling and reviewers' editors.
Convention enforced by
[`tests/Tests.Unit/AnnounceSanitiserTests.fs`](tests/Tests.Unit/AnnounceSanitiserTests.fs)
header note.

### Discriminated-union access from C#

Use `IsXxx` predicates and `.Item` / `.Item1` / `.Item2` payload
accessors. C# can't pattern-match F# DUs natively. See
[`src/Views/TerminalView.cs`](src/Views/TerminalView.cs).

### `nonNull` is the cleaner alternative to match-on-null

When you know a value is non-null but the compiler doesn't
(commonly: just-tested via `String.IsNullOrEmpty` or guaranteed by
a contract), `FSharp.Core`'s `nonNull` operator does the coercion
without an `obj.GetType()` check. See
[`src/Terminal.Core/AnnounceSanitiser.fs:56`](src/Terminal.Core/AnnounceSanitiser.fs)
for the canonical pattern. Use match-on-null at API boundaries
(unknown nullability) and `nonNull` inside helpers (proven
non-null).

### `let rec` for self-referencing class-body bindings

A `let` inside a class body that calls itself produces
`error FS0039: 'X' is not defined`. Add the `rec` keyword. The
compiler does not suggest this fix, and `FS0039` looks identical
to "missing identifier" errors.

### `internal` (not `private`) for companion-module access

A companion `module` is in a different IL scope from its `type`'s
`private` members, so a `private` constructor breaks the
`Foo.create` factory pattern. Use `internal` instead.

### Sequence-in-match-arm

When a match-arm body needs to do a side-effect-then-return-value,
**extract a named local function** rather than relying on F#'s
offside rule to sequence two expressions. The offside rule does
work in most cases, but `TreatWarningsAsErrors=true` makes the
brittle parses fail unpredictably.

```fsharp
// Brittle:
| None ->
    match envVar with
    | null -> ()
    | v -> log.LogWarning(...)
    ShellRegistry.Cmd

// Safer:
let logIfUnrecognised () : unit =
    match envVar with
    | null -> ()
    | v -> log.LogWarning(...)
match parseEnvVar envVar with
| Some id -> id
| None ->
    logIfUnrecognised ()
    ShellRegistry.Cmd
```

## Project conventions

### Accessibility outcomes are the acceptance criteria

A feature that compiles, passes unit tests, and looks correct on
screen **is not done** until it has been validated against NVDA per
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md).
Stages aren't declared shipped until the manual matrix row passes.
The maintainer runs the NVDA pass after merge — so when shipping
a stage, ensure the matrix row is in place + the manual procedure
is described concretely enough that someone other than the author
can run it.

This is also why **Stage 7's PR-D is its own PR** in the four-PR
sequence — the validation matrix update + initial gap-inventory
seeding live separately from the code so the maintainer can run
the matrix on a clean post-merge build.

### Walking-skeleton discipline

Stage N ships only after Stage N-1 is validated end-to-end. Don't
merge Stage 5 streaming notifications before Stage 4 text exposure
works in Inspect.exe. The four-PR Stage 7 sequence is the same
discipline scaled down: PR-A's env-scrub doesn't depend on the
shell-registry, but PR-C's hot-switch hotkeys do depend on PR-B.
Don't bundle.

### Spec immutability

[`spec/`](spec/) captures the external research that drove the
design (`overview.md`) and the stage-by-stage plan (`tech-plan.md`).
**Don't edit it without explicit ADR-style authorization** — see
the chat-2026-05-03 retroactive Stage 4a/4b/5a authorization for
the precedent. Changes need an issue + maintainer signoff.

### Tests

- xUnit 2.9.x + FsCheck.Xunit 3.x. Live in `tests/Tests.Unit/`
  (single project; FlaUI work reserved for `tests/Tests.Ui/`).
- Backtick test names ("\`\`should do X when Y\`\`").
- Plain ASCII source — use `\u`/`\x` escapes for non-ASCII.
- New test fixture files require a `<Compile Include=…>` entry in
  [`tests/Tests.Unit/Tests.Unit.fsproj`](tests/Tests.Unit/Tests.Unit.fsproj).
- Cross-assembly `internal` access via
  `[<assembly: InternalsVisibleTo("PtySpeak.Tests.Unit")>]` at the
  top of one source file in the assembly under test.
- CI runs `dotnet test --configuration Release --no-build` on
  Windows-latest on every PR. Tests in `Tests.Unit` are picked up
  automatically.
- **Literal `0x1B` (ESC) byte in test source files is a foot-gun.**
  `VtParserTests.fs` and `ScreenTests.fs` embed raw `0x1B` bytes
  (visible as `\033` under `od -c`, invisible in most editors) so
  the parser sees CSI sequences. **Edit-tool round-trips can strip
  the byte** silently, after which `feed screen (ascii "[5;3H")`
  becomes five `Print` events instead of a CUP, and assertions
  pass vacuously or fail confusingly. Verify with
  `od -c <file> | head` or `grep -c $'\x1b' <file>` after any
  edit to those files. PR #38 burned a CI cycle on this; see
  CONTRIBUTING.md §"Test fixtures: CSI / OSC / DCS sequences" for
  the canonical convention.

### Logging discipline

- File-based via [`src/Terminal.Core/FileLogger.fs`](src/Terminal.Core/FileLogger.fs)
  / `Microsoft.Extensions.Logging`.
- **Never log secrets.** Env-var names like `BANK_API_KEY` are
  themselves sensitive — log counts, not names or values, when
  surfacing security-relevant data (see `SECURITY.md` PO-5 row +
  `EnvBlock` count-only log line).
- Sanitise announcement-bound exception messages through
  `Terminal.Core.AnnounceSanitiser.sanitise` (audit-cycle SR-2).

### App-reserved hotkey contract

The keyboard layer ([`src/Views/TerminalView.cs`](src/Views/TerminalView.cs)
`OnPreviewKeyDown`) **MUST NOT mark `e.Handled = true`** for any
gesture in the `AppReservedHotkeys` table. WPF's `InputBindings`
machinery on the parent window must run first. Stage 6's keyboard
filter ordering is load-bearing and pinned by xUnit + behavioural
tests. Spec authority: [`spec/tech-plan.md`](spec/tech-plan.md) §6.

WPF routed-event ordering for reference: `PreviewKeyDown` (tunneling)
→ `KeyDown` (bubbling) → `InputBindings` → `CommandBindings`. Marking
`e.Handled = true` in any handler stops the rest. We exploit the
ordering for the `AppReservedHotkeys` short-circuit at the top of
`OnPreviewKeyDown`. Note: `InputBindings` on a custom
`FrameworkElement` are NOT auto-routed by `CommandManager` —
`KeyBinding` → `CommandBinding` only fires reliably on built-in
`Control` subclasses. We learned this in Stage 6 (Ctrl+V paste fix);
the fix was direct gesture handling at the top of `OnPreviewKeyDown`
via `HandleAppLevelShortcut`.

Currently shipped:

- `Ctrl+Shift+U` — Velopack auto-update (Stage 11)
- `Ctrl+Shift+D` — process-cleanup diagnostic launcher (PR-J adds
  inline shell-process snapshot announce before the script
  window opens)
- `Ctrl+Shift+R` — draft-a-new-release form launcher
- `Ctrl+Shift+L` — open logs folder
- `Ctrl+Shift+;` — copy active log to clipboard
- `Ctrl+Shift+1` / `+2` / `+3` — hot-switch the spawned shell
  (`+1` = cmd, `+2` = PowerShell, `+3` = Claude Code). PR-J
  reordered: PowerShell sits in slot 2 deliberately so the
  diagnostic control shell is one keystroke from cmd.
- `Ctrl+Shift+G` — toggle FileLogger min-level between
  Information and Debug at runtime (Stage 7-followup PR-E)
- `Ctrl+Shift+H` — health-check announce: shell + PID + alive,
  log level, reader staleness, queue depths (PR-F + PR-J
  liveness probe)
- `Ctrl+Shift+B` — incident marker: writes a clear
  `=== INCIDENT MARKER {timestamp} ===` line into the active
  log so post-hoc grep extracts the relevant slice (PR-F)

Reserved (not yet bound):

- `Ctrl+Shift+M` — Stage 9 earcon mute
- `Alt+Shift+R` — Stage 10 review-mode toggle
- `Ctrl+Shift+4` / `+5` / `+6` — additional shells (WSL, Python
  REPL, etc.) per Phase 2 plans

### Accessibility (the non-negotiable rules)

CONTRIBUTING.md §"The non-negotiable accessibility rules" is the
canonical list. Highlights:

- **Never raise both `TextChanged` and `Notification` events for the
  same content** — NVDA reads it twice. Phase 1 is
  Notification-only.
- **Never swallow `Insert` / `CapsLock` / numpad-with-NumLock-off
  keys** — they're NVDA modifier keys; capturing them breaks every
  screen-reader shortcut. In `PreviewKeyDown` you must `return`
  (not set `e.Handled = true`) for those keys.
- **All UIA events on the WPF Dispatcher thread.**
  `RaiseNotificationEvent` silently no-ops off-thread. Marshal via
  `Dispatcher.InvokeAsync` (preferred), `Dispatcher.BeginInvoke`,
  or `Async.SwitchToContext`.
- **Spinners must be deduplicated.** A row whose content hash
  equals the previous flush's hash is dropped. Same-row updates
  ≥5/sec for ≥1s are classified as a spinner and suppressed.
- **Strip control characters from `displayString`** before passing
  it to `UiaRaiseNotificationEvent`. Otherwise NVDA verbalises
  "escape bracket one A". `Terminal.Core.AnnounceSanitiser.sanitise`
  is the chokepoint.
- **WASAPI shared mode** for any audio (Stage 9+). Exclusive mode
  silences NVDA's TTS — immediate revert.
- **F# P/Invoke conventions** are immutable per
  `src/Terminal.Pty/Native.fs` — see CONTRIBUTING.md §"F# / P/Invoke
  conventions" for the full list.

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

The cleanup cycle (Part 1 of the May-2026 plan) shipped 2026-05-03.
Active sequence:

- **Stage 7** = validation gate. Four sequenced PRs + followups:
  - **PR-A** — env-scrub PO-5 (shipped: PR #131)
  - **PR-B** — shell registry + `PTYSPEAK_SHELL` (shipped: PR #132)
  - **PR-C** — hot-switch hotkeys `Ctrl+Shift+1` / `Ctrl+Shift+2`
    (shipped)
  - **PR-D** — NVDA validation matrix + `docs/STAGE-7-ISSUES.md`
    seeding (shipped)
  - **PR-E…I** — diagnostic-surface + bug-fix followups from
    NVDA-pass empirical findings (shipped)
  - **PR-J** — PowerShell as third built-in shell + Ctrl+Shift+H
    liveness probe + Ctrl+Shift+D inline child-process snapshot
    (this PR; reorders hotkeys to `+1`=cmd / `+2`=PowerShell /
    `+3`=Claude so the diagnostic control shell sits next to cmd)
- **Output framework cycle** (Part 3, subsumes original Stages 8+9)
  — research → RFC → eight sub-stages, each with NVDA validation.
- **Input framework cycle** (Part 4) — same shape.
- **Stage 10** — review mode + quick-nav, first non-built-in
  consumer of the framework taxonomy.

[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md) "Where we
left off" tracks the in-flight branch + the next concrete task.

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
