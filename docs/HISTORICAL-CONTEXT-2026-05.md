# Historical context — May 2026 cleanup cycle

> **This document is supplementary historical context, not a primary
> handoff source.** Read [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md)
> first; this file exists in case interesting details about how the
> May-2026 cleanup cycle was executed are useful as reference. Most
> of what's here is also implicitly captured in CHANGELOG entries,
> commit messages, [`CONTRIBUTING.md`](../CONTRIBUTING.md), or
> [`spec/tech-plan.md`](../spec/tech-plan.md) — this doc surfaces
> it explicitly so a future contributor can find a single curated
> list rather than archaeology across git history.

## What this doc is NOT

- **Not the project plan** — see [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md).
- **Not the working contract** — see [`CONTRIBUTING.md`](../CONTRIBUTING.md).
- **Not the entry point** — see [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md).
- **Not authoritative** — every item below has a primary source elsewhere; this doc cross-references but doesn't supersede.
- **Not exhaustive** — only the non-obvious, unusual, or "this took debugging to find" items. Mainstream conventions live in CONTRIBUTING.md.

## Guiding principles that emerged

These are the meta-rules the May-2026 cleanup cycle settled into. They generally also apply to future cycles unless the maintainer specifically says otherwise.

### 1. Repo is the continuity layer

Every meaningful piece of context — strategic plans, implementation sketches, gotchas, decision rationale — goes into the repo so it survives session boundaries. The plan-mode workspace is ephemeral; the repo is the canonical source. Future revisions should land as new dated artifacts (e.g. `PROJECT-PLAN-YYYY-MM.md`) rather than edits in place — preserves decision history.

### 2. Cleanup before architecture

Don't shortcut foundational architecture decisions. Close the deck (clean up loose ends, freshen handoff docs, take inventory) before starting big multi-week cycles. The May-2026 plan's Part 1 (cleanup) shipped before Stage 7 (validation gate) before Parts 3 + 4 (framework cycles) for exactly this reason.

### 3. Surface, don't solve, at validation gates

Stage 7's job (per spec §7 + the implementation sketch) is to surface every NVDA-validation gap, not to solve them inline. The inventory becomes the explicit design input for Parts 3 + 4 framework cycles. Bundling fixes into Stage 7 would conflate "what's broken" with "what shape the framework takes." This pattern is reusable: validation gates produce inventories, not patches.

### 4. Spec immutability + dated plans

`spec/` is immutable for planners but maintainer-editable. Architectural changes that contradict the spec need an explicit ADR-style PR with maintainer authorization. The May-2026 spec stage-numbering (Stages 4a / 4b / 5a) was this kind of edit; commit messages explicitly cite "per chat 2026-05-03 maintainer authorization" so the trail is in git history.

### 5. Letter suffixes for retroactive stages

Use lowercase letter suffixes (Stage 4a, Stage 4b, Stage 5a) rather than decimal notation (Stage 4.5, Stage 5.5) to avoid collision with sub-section numbering inside stages (`### 4.5 NVDA validation`). Matches the existing Stage 3a / 3b precedent. Forward-looking references update; historical CHANGELOG entries from when work shipped (with the original informal label) stay verbatim as release-notes-shaped artifacts.

### 6. Historical CHANGELOG entries are frozen

CHANGELOG entries from when work shipped describe the work as it was labeled at that time. Don't retrofit historical entries with new conventions; they're release-notes-shaped artifacts of a moment in time. Forward-looking references in living docs (SESSION-HANDOFF, README, plan docs, security inventories) DO update for consistency.

### 7. Small focused PRs over bundling

The squash-merge convention combines branch commits into one canonical commit on `main`. Push fixup commits to the same branch when CI fails — don't open a new PR. The standing rule: one PR per concern, multiple commits on the branch as needed. PR #121 (Issue #107 + nullness fixup) is the worked example documented in CONTRIBUTING.md.

### 8. Maintainer-side actions cluster

The dev sandbox can't push tags (`refs/tags/` returns 403) and the GitHub MCP server occasionally disconnects mid-session. Tag pushes, release cuts, and stale-branch deletion live in a "Pending action items" + "Pending checkpoint tags" pattern in SESSION-HANDOFF.md and CHECKPOINTS.md so the maintainer can sweep them from a workstation when convenient.

### 9. NVDA validation gates every stage

Automated tests verify code; manual NVDA validation verifies the feature is actually accessible. Per CONTRIBUTING.md ground rule #1: "Accessibility outcomes are the acceptance criteria. A feature that compiles, passes unit tests, and looks correct on screen is not done until it has been validated against NVDA." Stages aren't declared shipped until the manual matrix row in `docs/ACCESSIBILITY-TESTING.md` passes.

### 10. The maintainer's working constraints shape the workflow

The maintainer uses a screen reader and has limited bandwidth for back-and-forth on individual log lines. Workflow patterns that work well: paste the whole log (let the assistant grep it server-side); cut small focused PRs (review faster, bisect cleaner); use the announce-before-launch ~700ms `Task.Delay` pattern when spawning windows that grab focus (NVDA's speech queue plays the cue first); document every diagnostic-loop lesson in `docs/RELEASE-PROCESS.md` "Common pitfalls" so it's not re-learned.

### 11. Sandbox + tools caveats are first-class concerns

`docs/SESSION-HANDOFF.md` "Sandbox + tools caveats" enumerates the constraints (no `dotnet` locally, can't fetch CI logs directly because of HTTP 403 on `productionresultssa*.blob.core.windows.net` and `api.github.com`, tag-push 403, GitHub MCP disconnects, large-file-write stream-idle timeouts). These shape every decision. A future session should re-read that section before assuming it can do something the sandbox can't.

## Technology specificities that bit us (or could bite next time)

Curated list of "this took debugging to find" items. CONTRIBUTING.md has the canonical "F# / WPF gotchas learned in practice" list — this section adds context and cross-references; the canonical list is in CONTRIBUTING.md.

### F# / .NET 9

- **F# 9 nullness annotations bite at .NET-API boundaries** (CONTRIBUTING.md F# gotchas; PR #121). `Path.GetFileName`, `Path.GetDirectoryName`, `Environment.GetEnvironmentVariable`, `StreamReader.ReadLine` all return `string?`. Passing to a non-null `string` parameter triggers `FS3261` which becomes a build error under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Helper-accepts-`string | null` pattern matches the codebase convention (`AnnounceSanitiser.sanitise`, `KeyEncoding.encodeOrNull`).
- **F# 9 `nonNull` operator** from `FSharp.Core` is the cleaner alternative when you know a value is non-null but the compiler doesn't. Used in `AnnounceSanitiser.fs:56`.
- **`out SafeFileHandle` byref interop is silently broken** (CONTRIBUTING.md). Declaring a P/Invoke `out` parameter as `SafeFileHandle&` produces a `NullReferenceException` even when the kernel writes the handle correctly. Use `nativeint&` and wrap manually with `new SafeFileHandle(p, ownsHandle = true)`. See `Terminal.Pty/Native.fs`.
- **`let rec` for self-referencing class-body bindings** (CONTRIBUTING.md). A `let` inside a class body that calls itself produces `error FS0039: 'X' is not defined`. Add `rec`. Compiler doesn't suggest this fix.
- **F# `internal` (not `private`) for companion modules** (CONTRIBUTING.md). A companion `module` is in a different IL scope from its `type`'s `private` members.
- **F# DU access from C# uses `IsXxx` predicates and `.Item`/`.Item1`/`.Item2`** (CONTRIBUTING.md). Stage 3b's `Views/TerminalView.cs` reads `Cell.Attrs.Fg.IsDefaultFg` and `Cell.Attrs.Fg.Item`.
- **`let mutable` in class body becomes a field**, not a captured local. Closures inside members can read/write it without the "unsafe captured-mutable" warning. The `flushTcs` field in `FileLogger.fs` (PR #122) is a sink-level field accessed via a `lock`-protected swap.
- **`let` bindings in class body are private by default**. Promoting to `member` exposes them. This is fine for internal-only helpers like `signalFlushComplete` (PR #122).

### WPF + Win32

- **`FrameworkElement` does NOT have a `Background` property** (CONTRIBUTING.md). Only `Control` and `Panel` do. A custom-render `FrameworkElement` needs its own private brush field.
- **`MeasureOverride` controls layout**. Returning a fixed size means the element doesn't track parent resize — Stage 6's resize-cuts-off-text bug; fix was to honour `availableSize` (fall back to preferred size only when `availableSize.Width.IsInfinity`).
- **WPF dispatcher is single-threaded STA**. UI work, UIA peer events, clipboard access all serialize on the dispatcher thread. Long operations on the dispatcher freeze the window. Background work has to marshal back via `Dispatcher.InvokeAsync` for any UI-touching call.
- **WPF `OnRender` runs on the dispatcher** (Acc/9 fix bundled in Stage 5). Long renders block input. Use `Screen.SnapshotRows(0, _screen.Rows)` to take ONE locked snapshot per render frame instead of repeated `GetCell` calls (which would re-enter the screen gate up to `Rows*Cols` times per frame).
- **Routed-event ordering**: `PreviewKeyDown` (tunneling) → `KeyDown` (bubbling) → `InputBindings` → `CommandBindings`. Marking `e.Handled = true` in any handler stops the rest. We exploit this for the `AppReservedHotkeys` short-circuit at the top of `OnPreviewKeyDown`.
- **`InputBindings` on a custom `FrameworkElement` are NOT auto-routed** by `CommandManager`. `KeyBinding` → `CommandBinding` only fires reliably on built-in `Control` subclasses. We learned this in Stage 6 (Ctrl+V paste fix); the fix was direct gesture handling at the top of `OnPreviewKeyDown` via `HandleAppLevelShortcut`.
- **`Clipboard.SetText` requires STA + can throw `COMException`** when the OS clipboard is contended. Single-attempt is acceptable; the user retries.
- **WPF SDK auto-classifies `App.xaml` as `ApplicationDefinition`** based on filename. Invalid in `OutputType=Library` projects (build error `MC1002`). Either remove `App.xaml` and use a plain `App.cs : Application`, or move the WPF entry to the executable project. We do the former.

### NVDA / UI Automation

- **NVDA disables `LiveRegion` and `TextChanged` events for terminals**. Forbidden by `spec/tech-plan.md` §5.6 to prevent double-announce. Don't re-add them.
- **`AutomationNotificationProcessing.MostRecent` is per-`activityId`**. Rapid bursts of the same activityId supersede each other in NVDA's queue; chunks earlier in a burst can be dropped before NVDA reads them. Use `ImportantAll` for streaming output (Stage 6 post-fix bundled in PR #100) and `MostRecent` for hotkey-style one-shot announcements.
- **`ITextProvider2` adds caret-aware methods** (`GetCaretRange`). Newer NVDA versions prefer the `2` variant when present. We currently implement `ITextProvider` only; Stage 7+ may need to add `2` for the caret-sync fix tracked in `docs/ACCESSIBILITY-INTERACTION-MODEL.md`.
- **`ITextProvider.GetSelection()` is how NVDA learns the caret position**. Returning a zero-width range at the PTY cursor lets NVDA's "current line" command read the right line WITHOUT moving the system caret. Not yet implemented — the disconnect is documented as Pattern B in `ACCESSIBILITY-INTERACTION-MODEL.md`.
- **`AutomationPeer.GetPatternCore` is unreachable from external assemblies** (Stage 4 deep lesson, multi-PR investigation). The .NET 9 reference assembly strips the protected member; reflection probes confirm runtime metadata strips it too. Hence the `IRawElementProviderSimple` raw-provider path that the Stage 4 PRs settled on. Issue #49 has the full investigation; the regression-sentinel test in `tests/Tests.Ui/AutomationPeerReflectionTests.fs` would fire if a future .NET update exposes the override.
- **NVDA modifier keys (bare Insert, CapsLock, Numpad-with-NumLock-off) must NOT be swallowed** by the input encoder. The Stage 6 `OnPreviewKeyDown` filter explicitly returns without `Handled` for these so NVDA's hooks see them. Conservative on purpose — the cost of a few key presses not reaching the shell is tiny compared to the cost of breaking screen-reader navigation.
- **`UIElementAutomationPeer.FromElement` returns `null` until UIA queries the element**. Peer creation is lazy. `Announce` early-skips when peer is null; defensive default, not a bug. The peer-NULL `WARN` log entry is the smoking-gun signal that a UIA client never connected.

### ConPTY (Windows pseudo-console)

- **No OVERLAPPED I/O on ConPTY pipes**. All reads and writes are synchronous. The reader runs in its own background task; the writer (`ConPtyHost.WriteBytes`) blocks the caller until the kernel accepts the bytes.
- **Pipe handles must be released by the parent immediately after `CreatePseudoConsole`** or the child's pipes never signal EOF. Single missed close causes hangs on shutdown. Documented inline in `PseudoConsole.fs:create`.
- **The ConPTY init prologue includes `\x1b[?9001h\x1b[?1004h`** emitted by ConPTY itself before the child runs. Stage 4a's parser must not choke on these; Stage 6 PR-A's FocusReporting arm now handles `?1004h` explicitly.
- **`ResizePseudoConsole` is documented thread-safe** but the child shell does layout work on every resize. WPF's per-pixel `SizeChanged` during a window drag would hammer the child without debouncing — Stage 6's 200ms `DispatcherTimer` debounce protects against this.
- **`Job Object` containment requires the child to be assigned BEFORE its first instruction** for strict guarantees. We don't pass `CREATE_SUSPENDED` (microsecond race window accepted; cmd.exe doesn't fork that fast). `KILL_ON_JOB_CLOSE` ensures the descendant tree dies on parent exit even on hard parent crash.
- **Environment variables inherit from parent by default** (`lpEnvironment = NULL` in `CreateProcess`). Sensitive vars (`GITHUB_TOKEN`, `OPENAI_API_KEY`) reach the child unless filtered. Stage 7 ships the env-scrub (PO-5) — see `docs/SESSION-HANDOFF.md` "Stage 7 implementation sketch (next)" §3 for the allow-list-with-deny-list-override scheme.

### Coalescer / streaming

- **`PeriodicTimer.WaitForNextTickAsync` cannot be called twice without awaiting** (PR-cycle bug; CHANGELOG `[Unreleased]` Fixed entry). Calling it a second time before the previous call completes throws `InvalidOperationException`. The runLoop must track the pending timer task across iterations and reuse the same wait until it actually fires.
- **AllHashHistory spinner gate threshold is per-emit, not per-row** (PR #116 root cause). Counting total entries in a 1-second window when each emit adds 30 entries (one per row) instantly exceeds any reasonable threshold. The fix removed the broken any-hash-anywhere gate; the per-`(rowIdx, hash)` gate (the OTHER spinner check, which fires when the same row state recurs ≥5 times in 1s) handles the common spinner case correctly. Cross-row spinner detection is filed as Issue #117 with a redesign brief: count unique-hash recurrences.
- **Alt-screen toggle is a hard invalidation barrier**. The Stage 5 coalescer flushes the pending debounce window first, resets frame-hash + spinner state, then passes the barrier through. `SequenceNumber` bumps on every `?1049h/l` so the coalescer can detect it. Stage 4a PR-B ships the back-buffer; Stage 5 ships the barrier consumer.
- **`AnnounceSanitiser` strips C0 / DEL / C1 controls** before any string reaches NVDA via `RaiseNotificationEvent`. Defense in depth: a BiDi override (U+202E), BEL (0x07), or ANSI escape sequence (0x1B) in an exception message could otherwise confuse NVDA's notification handler or spoof announcement direction. Stage 5's coalescer routes per-row announcements through the same chokepoint (`Coalescer.fs:178`).

### Logging (Stage 5a)

- **Bounded `Channel<T>` with `BoundedChannelFullMode.Wait` + `SingleReader=true`** is the backpressure pattern. Used in parser-side `ScreenNotification` channel, Stage 5 coalescer, and Stage 5a FileLogger. `SingleReader=true` lets the channel use a more efficient single-consumer path.
- **TCS-barrier pattern for "wait for next flush"** (PR #122 / `FileLogger.fs FlushPending`). The drain loop atomically swaps `flushTcs` for a fresh one after every successful `StreamWriter.Flush` and completes the swapped one. A caller capturing the current TCS gets signalled the next time the drain finishes a flush. Lock-protected swap; idempotent `TrySetResult`; signalled once more after the dispose-time final flush so callers awaiting at shutdown see completion rather than timeout.
- **`Task.Delay(-1)` is `Timeout.Infinite`**. Lets a single `Task.WhenAny` line handle both bounded and unbounded waits without an extra `if`-branch.
- **`File.ReadAllText` opens with `FileShare.Read`** (the overload's default) — meaning "I tolerate other readers but no writers." Since the `FileLogger` writer holds the file open with `FileAccess.Write`, the OS rejects the read open. Use an explicit `FileStream` with `FileShare.ReadWrite` to match the writer's policy (PR #114 fix; verified by the Stage 5a FlushPending test that reads while the writer is active).
- **Filename `pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log`** (Issue #107). Full date+time keeps the file self-describing when extracted from its day-folder context; millisecond suffix is the uniqueness tie-breaker so two launches in the same UTC second produce distinct filenames; alphabetical sort equals chronological sort.

### Velopack (auto-update)

- **`VelopackApp.Build().Run()` MUST be first**, before any WPF type loads. Otherwise you get the endless restart loop from Velopack issue #195. Documented in `spec/tech-plan.md` §11.5.
- **Don't request elevation**. Velopack discussion #8: manifest must be `asInvoker`. Stage 11 ships with the `asInvoker` manifest; `SECURITY.md` row PO-4 documents this as shipped.
- **Structured error matching**, not string contains. Each Velopack exception class maps to a distinct NVDA announcement per `docs/UPDATE-FAILURES.md`. The `UpdateMessages.announcementForException` pure function is testable without standing up an `IUpdateManager` adapter.

### Test conventions

- **xUnit + FsCheck.Xunit 3.x** pinned because the `[<Property>]` attribute integrates cleanly with `xunit.runner.visualstudio` (CONTRIBUTING.md). Expecto was considered but never adopted.
- **Backtick-named test functions** (`let \`\`name with spaces\`\` () = ...`). Standard F# / xUnit pattern across the codebase. Lets test names read as English sentences.
- **`TimeProvider` injection** for deterministic time in tests. Production passes `TimeProvider.System`; tests inject `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` so debounce / spinner-window assertions don't rely on `Thread.Sleep`.
- **Literal ESC byte in test fixtures is a foot-gun** (CONTRIBUTING.md). Existing `VtParserTests.fs` / `ScreenTests.fs` embed a literal 0x1B byte (ESC) directly in the F# source. Invisible in most editors. Plain-text edits silently strip the byte. PR #38 burned one CI cycle on this; new fixtures should use the explicit `` Unicode escape.

## Coding paradigms specific to pty-speak

- **Walking-skeleton stages**. Each stage is a narrow vertical slice through the entire pipeline (parser → screen → view → UIA peer → audio sink). Add Stage N only when Stage N-1 ships and is validated end-to-end. Don't merge Stage 5 streaming notifications before Stage 4 text exposure works in Inspect.exe (CONTRIBUTING.md ground rule #3).
- **Two-channel composition** (Stage 5). Parser-side `notificationChannel` (256 capacity, `DropOldest`) → coalescer → `coalescedChannel` (16, `Wait`) → drain → UIA peer. Composed in `Program.fs compose ()` with shared `cts.Token` for unified cancellation.
- **`AnnounceSanitiser` as security chokepoint**. Every announce-bound string passes through `Terminal.Core.AnnounceSanitiser.sanitise` before `RaiseNotificationEvent`. Single grep-able call site for security audits.
- **`ActivityIds` as NVDA configuration vocabulary**. `pty-speak.output`, `pty-speak.update`, `pty-speak.error`, `pty-speak.diagnostic`, `pty-speak.new-release`, `pty-speak.mode`. Lets NVDA users configure per-tag handling in their NVDA settings.
- **`AppReservedHotkeys` + load-bearing `OnPreviewKeyDown` filter ordering**. The filter ordering is pinned by inline doc-comment + behavioural tests because re-ordering it silently breaks the screen-reader-modifier-key contract. Per `spec/tech-plan.md` §6 (Stage 6 ADR amendment): Stage 6's keyboard layer MUST preserve every entry in `TerminalView.AppReservedHotkeys` and MUST NOT mark them `e.Handled = true`.
- **Dispatcher.InvokeAsync for thread marshalling**. Every UI-touching call from a background task marshals back via `Dispatcher.InvokeAsync`. The render path uses `Screen.SnapshotRows` to take ONE snapshot per frame so the WPF dispatcher doesn't compete with the parser thread cell-by-cell.
- **OSC 52 silent drop is a security boundary** (Stage 4a PR-A). The OSC dispatch arm in `Screen.csiDispatch` includes an explicit no-op for OSC 52 (clipboard manipulation) with a SECURITY-CRITICAL inline comment. A future maintainer adding clipboard support has to remove the arm deliberately, not by accident. Cross-referenced from `SECURITY.md` row TC-2.
- **Bracketed-paste injection defence** (Stage 6 PR-B). `KeyEncoding.encodePaste` strips embedded `\x1b[201~` from clipboard content BEFORE wrapping. NVDA users can't easily inspect their clipboard before pasting, so an attacker-crafted paste containing `\x1b[201~` followed by a malicious command would otherwise close the bracket-paste frame early and execute the post-paste portion as if typed. Accessibility-first divergence from xterm's permissive default.

## Process patterns from the May-2026 cleanup cycle

These are session-mechanic patterns that worked smoothly. Worth knowing for the next cycle.

### Manual PR-create URL fallback when GitHub MCP disconnects

The GitHub MCP server occasionally disconnects mid-session. When it does, push the branch and send the maintainer a `https://github.com/KyleKeane/pty-speak/compare/main...<branch>?expand=1` URL plus suggested title and body. Maintainer opens the PR manually. Faster than reconnecting MCP for one PR; equivalent outcome.

### Three-PR chunking for spec stage-numbering

The maintainer's "small focused PRs" preference + the spec-immutability rule led to splitting the Stage 4a / 4b / 5a formalization into three separate PRs (#123, #124, #125) rather than one big spec edit. Each PR's commit message cites "per chat 2026-05-03 maintainer authorization" so the trail is in git history.

### CHECKPOINTS rows in shipping order

The ROADMAP table and CHECKPOINTS table both order stages by shipping order, not stage number. Stage 11 ships between Stage 4 and Stage 4b (chronologically); the table reflects that even though `11 < 4b` numerically. PR #127 added the seven new rows in shipping order: Stage 11 → 4b → 4a → 5 → 6 → 5a.

### Status-as-of header on dated docs

When a dated doc (like `PROJECT-PLAN-2026-05.md`) ages and its body becomes stale, add a "Status as of YYYY-MM-DD" header note explaining what's changed. Preserves the body verbatim for decision-history continuity (no edits in place per the doc's own rule). The next reader sees the updated status before reading the now-historical plan body.

### Mechanical merges with content-hash verification

PR #120 (CHANGELOG `[Unreleased]` consolidation) used a Python script to merge 38 sub-section headers into 5 mechanically. The script computed a SHA-256 over the sorted set of non-header non-blank content lines before and after the merge. Hash match proved no content was lost (2153 lines preserved). Useful pattern when restructuring large docs without prose rewrites.

### Verify diff stat before commit

`git diff --stat` shows scope at a glance. If the stat matches the expected scope (~5 files, ~150 lines for a typical PR in this cycle), commit. If it surprises you, investigate before committing. Caught a couple of accidentally-staged files in this cycle.

### Cross-link doc references

When a doc references work that lives in another doc (e.g. Stage 4b's known-limitation references SESSION-HANDOFF item 6; Stage 7 sketch references STAGE-7-ISSUES.md; CHECKPOINTS rows reference spec sections), make the cross-link bidirectional. Each end-point references the other so a reader landing on either side finds the full context.

### Fixup-commit rhythm on open PRs

CI failure on an open PR → push a fixup commit to the same branch. GitHub PRs track branch HEAD; the PR auto-extends and CI re-runs. Don't open a new PR. PR #121 (Issue #107 + nullness fixup) is the worked example. Documented in CONTRIBUTING.md "Branching and pull requests."

## What's NOT here

- **Stage-specific implementation details** — they live in `spec/tech-plan.md` (designed) + CHANGELOG entries (as shipped) + commit messages (as built).
- **PR rationale** — each PR body has the "Summary / Why / What changed / Test plan" structure per CONTRIBUTING.md.
- **Architectural reasoning for each design decision** — captured in spec section authorship notes (e.g. spec §4a / §4b / §5a) + the May-2026 plan doc's strategic rationale.
- **The full audit chronology of the May-2026 cleanup cycle** — recoverable from `git log origin/main --oneline` filtered to the PR #118 → #129 range.
- **Generic Claude-Code-tool patterns** (parallel tool calls, `--first-parent` git log usage, F# helper-function nullness pattern) — these belong in Claude-tool docs, not this project. The project-specific ones ARE in CONTRIBUTING.md.

## How to use this doc

- **As reference** when CHANGELOG or commit messages don't quite explain a non-obvious decision.
- **As checklist** when starting a new architecture cycle, to remember the "guiding principles that emerged."
- **As warning** when about to add a new helper / abstraction / convention — check whether it conflicts with an existing pattern documented above.
- **NOT as primary handoff** — `docs/SESSION-HANDOFF.md` is the entry point. This file is the supplementary context.

If items here become widely-known enough to belong in `CONTRIBUTING.md` or `docs/ACCESSIBILITY-INTERACTION-MODEL.md` or another primary doc, **promote them and remove from here**. This doc is a backup, not a primary reference.
