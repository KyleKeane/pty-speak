# Session handoff

This document is the bridge between Claude Code coding sessions on
this repo. It captures the things a new session needs that **aren't**
already in the existing docs — the sandbox-specific gotchas, the
"where we left off" pointer, and a pre-digested sketch of the next
stage. The general working conventions (PR shape, branching,
CHANGELOG discipline, documentation policy, F# / WPF gotchas) live in
[`CONTRIBUTING.md`](../CONTRIBUTING.md).

A new session should read this **first**, then
[`CONTRIBUTING.md`](../CONTRIBUTING.md),
[`spec/tech-plan.md`](../spec/tech-plan.md),
[`docs/CHECKPOINTS.md`](CHECKPOINTS.md), and
[`CHANGELOG.md`](../CHANGELOG.md) in that order — see the full
"Recommended reading order" at the bottom.

## Where we left off

| | |
|---|---|
| **Last merged stage** | Stage 3b (WPF `TerminalView` + end-to-end `ConPtyHost → Parser → Screen → TerminalView`) |
| **Last shipped release** | [`v0.0.1-preview.18`](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.18) — first preview cut from Stage 3b state. Maintainer has installed the build and confirmed `cmd.exe` runs under ConPTY end-to-end. (`preview.16` and `.17` were burned by the `## [<version>]` CHANGELOG-matching gate before #37 relaxed it; both retag the same `db44d6d` commit and were never shipped artifacts.) |
| **Stage-3b smoke status** | Resolved on `main` pending visual reverification on the next preview. The "second window" the maintainer saw on `v0.0.1-preview.18` was the parent `Terminal.App.exe` itself (the `OutputType=Exe` setting put it in the Windows console subsystem, so Windows allocated a conhost at startup before any of our code ran). The ConPTY child setup checked out clean against Microsoft's canonical sample — none of the four hypotheses originally listed in [Issue #39](https://github.com/KyleKeane/pty-speak/issues/39) was the actual cause. Fix landed in PR #44 (`OutputType=WinExe`); next preview release is the smoke target. |
| **In-flight branch** | None. Pre-Stage-4 fixes (#41 test/CI hygiene, #43 Velopack manifest globs, #44 WPF subsystem) and docs (#40 ESC-byte convention) all merged. |
| **Next stage** | **Stage 4** — UIA exposure (first NVDA milestone). Substrate (`Screen.SequenceNumber` + `Screen.SnapshotRows`) shipped in PR #38; threading boundary is in place; `tests/Tests.Ui/` is reserved for FlaUI integration tests that this stage adds. See sketch below. |

The end-to-end pipeline is wired: launching the app spawns `cmd.exe`
under ConPTY, parses its output, applies VtEvents to a 30×120
`Screen`, and renders the buffer in a custom WPF `FrameworkElement`.
The maintainer has installed `v0.0.1-preview.18` and confirmed text
appears in the WPF window. The `OutputType=Exe` defect that produced
the empty parent-process console is fixed on `main`; visual smoke
on a fresh preview will confirm a single window at launch.

## Pending action items (maintainer)

These can only be done from a workstation with normal git +
GitHub-website access. They've accumulated because the development
sandbox can't push tags and the GitHub MCP server occasionally
disconnects mid-session.

1. **Push the five pending baseline tags.** Exact commands are in
   [`docs/CHECKPOINTS.md`](CHECKPOINTS.md#pending-checkpoint-tags):
   `baseline/stage-{0-ci-release, 1-conpty-hello-world, 2-vt-parser,
   3a-screen-model, 3b-wpf-rendering}`. Tags don't trigger any
   workflow; they're rollback handles only.

2. **Re-enable the GitHub MCP server** in the next Claude Code thread
   if it's not auto-reconnected. Without it the agent can't
   programmatically check PR status, merge PRs, or post issue
   comments — falls back to asking the maintainer.

3. **Visual install smoke of `v0.0.1-preview.19` on Windows.**
   `v0.0.1-preview.19` was published 2026-04-28 and shipped all
   five Velopack assets correctly (full nupkg, Setup.exe,
   `releases.win.json`, `assets.win.json`, `RELEASES`) plus the
   two GitHub-auto source archives — 7 visible in the release
   UI, validating the manifest fix from PR #43. The
   single-window fix from PR #44 still needs an actual Windows
   install to confirm. Steps:
   1. Download `pty-speak-win-Setup.exe` from
      <https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.19>.
   2. Run the installer (SmartScreen will warn — expected for
      unsigned previews).
   3. Launch from Start menu.
   4. **Pass condition**: only one window appears (the WPF
      surface). No empty console window behind it.
   If a second window does appear, reopen [Issue #39](https://github.com/KyleKeane/pty-speak/issues/39)
   with details — the WinExe fix may not have taken effect, or
   there's a different cause we haven't surfaced yet.

4. **Enable NuGet lock files (deferred from PR #41).** The
   investigation in PR #41 settled on enabling
   `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
   in `Directory.Build.props` so `dotnet restore` writes a
   `packages.lock.json` next to each project; CI then switches to
   `dotnet restore --locked-mode` (or `RestoreLockedMode=true`)
   to reject restores whenever the lock would change, surfacing
   transitive-dep drift as explicit lock-file regenerations.
   Deferred because the agent sandbox has no `dotnet`, so it
   can't generate the initial `packages.lock.json` files itself.
   Maintainer steps to land it in a future session:
   1. Add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
      to `Directory.Build.props`.
   2. Run `dotnet restore` from the repo root locally.
   3. Commit the generated `packages.lock.json` files (one per
      project under `src/` and `tests/`).
   4. In `.github/workflows/ci.yml`, change `dotnet restore` to
      `dotnet restore --locked-mode` so future runs assert the
      lock files are consistent.
   The `**/packages.lock.json` glob already in `ci.yml`'s cache
   key picks up the lock files automatically once they exist —
   no separate cache-key change needed.

## Working conventions on this repo

The written rules live in [`CONTRIBUTING.md`](../CONTRIBUTING.md):

- **PR shape, body template, branch naming, squash-merge,
  Conventional Commits** — see CONTRIBUTING.md → "Branching and pull
  requests".
- **CHANGELOG `[Unreleased]` discipline + per-release rewrite** —
  same section.
- **Checkpoint + rollback contract** — see
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md). Sandbox-specific
  workaround when the agent can't push tags is captured under
  "Sandbox + tools caveats" below.
- **Documentation policy** — `spec/` is immutable; observed platform
  quirks go in `docs/CONPTY-NOTES.md`; CONTRIBUTING.md → "Documentation
  policy" has the full rule set.

The handoff-specific addition: the user explicitly asked for **smaller
PRs** after the Stage 0 diagnostic loop. When in doubt, split.

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
  for the maintainer to push from their workstation. Hence the
  "Pending checkpoint tags" table in
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — the agent stages the
  exact push commands for the maintainer.
- General release-flow rules (Releases UI not `git push --tags`,
  `Target = main`, the two fail-fast gates) live in
  [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md).

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

`spec/tech-plan.md` §4 has the full spec; the spec is immutable per
the documentation policy. This sketch is the pre-digested
implementation plan that captures decisions made *during execution*
without contradicting the spec.

**Goal**: NVDA's review cursor (Caps Lock + Numpad 7/8/9) reads the
terminal content character / word / line at a time. No streaming
announcements yet — just static text exposure. Validation tools:
Inspect.exe, Accessibility Insights, FlaUI for unit tests, NVDA for
manual sign-off.

### Why split Stage 4 into a spike plus three PRs

The original sketch (pre-this-revision) called for one PR delivering
the peer + provider + FlaUI test together at "~250-400 lines." On
re-reading the `ITextProvider` / `ITextRangeProvider` interfaces and
weighing what this session learned about the codebase's CI iteration
cost, that's roughly half the realistic line count and bundles
three review concerns (interop, navigation, integration tests) into
one PR. The split below keeps each PR small, reviewable, and
independently revertible.

### Spike: F# WPF AutomationPeer + C# interface implementation

**Risk being settled.** The codebase has a documented F#-interop
foot-gun (`out SafeFileHandle&` byref silently produces a runtime
`NullReferenceException`; see `Terminal.Pty/Native.fs`). Stage 4
introduces two more F#-meets-C# boundaries the project has never
exercised:

1. F# class subclassing `System.Windows.Automation.Peers.FrameworkElementAutomationPeer`
   (a C# class with `protected virtual` overrides).
2. F# class implementing `System.Windows.Automation.Provider.ITextProvider`
   (a C# interface with `[Variant]` / `[CLSCompliant]` attributes).

A 30-line throwaway PR proves the interop compiles before we build
250+ lines on top. Discard or absorb into PR 4a depending on outcome.

**Scope**: replace `Terminal.Accessibility/Placeholder.fs` with a
minimal `TerminalAutomationPeer.fs` that subclasses
`FrameworkElementAutomationPeer`, overrides
`GetAutomationControlTypeCore` returning `Document`, implements
`ITextProvider` with all methods returning `null` / no-ops. Push,
verify CI is green. **Don't wire it into `TerminalView` yet.**

**Pass condition**: build green, no F# interop errors, no
`TreatWarningsAsErrors` failures.

**Outcome (PR #47, merged 2026-04-29).** The spike validated that
F# can:
- Subclass `FrameworkElementAutomationPeer` and override the five
  parameterless `*Core` methods (`GetAutomationControlTypeCore`,
  `GetClassNameCore`, `GetNameCore`, `IsControlElementCore`,
  `IsContentElementCore`) with no nullability or interop friction.
- Implement `ITextProvider` (6 members) and `ITextRangeProvider`
  (17 members) cleanly via `interface ... with` syntax. Empty
  arrays via `Array.empty<_>` and null returns via
  `Unchecked.defaultof<_>` both work for these interface
  implementations.
- Add `<UseWPF>true</UseWPF>` to `Terminal.Accessibility.fsproj`
  to bring in `WindowsBase` / `PresentationCore` /
  `UIAutomationProvider` / `UIAutomationTypes`.

**Resolved during PR #48 (Stage 4a) — `GetPatternCore` is not
reachable from external assemblies.** What looked like an F#-only
nullability puzzle in the spike turned out to be a more
fundamental visibility problem affecting every external caller,
F# and C# alike.

The decisive evidence came from a diagnostic in PR #48 that
removed the override entirely and replaced it with a non-override
method calling `base.GetPatternCore(...)` from within a subclass
of `FrameworkElementAutomationPeer`. CI failed with C# **CS0117**
("'FrameworkElementAutomationPeer' does not contain a definition
for 'GetPatternCore'"). C#'s view of the type via the public
.NET 9 reference assembly does not expose the protected
`GetPatternCore` member. The earlier CS0115 / FS0855 errors were
both surface expressions of the same underlying fact: there is
no method on `FrameworkElementAutomationPeer` (as visible from
this build environment) for any subclass to override.

Microsoft's documented examples that override `GetPatternCore`
appear to compile against internal Microsoft assemblies where
the protected metadata is visible. The public reference assembly
ships the type without the override target.

Implication: any path to exposing the Text pattern from this
project has to bypass the `AutomationPeer.GetPatternCore`
extension point. The `Terminal.Accessibility.Interop` C# shim
project that PR 4a originally built was deleted as part of the
reduced-scope cleanup — it doesn't help, because the same
visibility limit applies regardless of which language hosts the
override attempt.

### PR 4a (reduced scope) — UIA Document role + identity

Shipped a peer that exposes the terminal element with the right
role and name, without the Text pattern.

- `TerminalAutomationPeer.fs` extends `FrameworkElementAutomationPeer`
  and overrides only the five parameterless `*Core` methods:
  - `GetAutomationControlTypeCore` returns `Document`
  - `GetClassNameCore` returns `"TerminalView"`
  - `GetNameCore` returns `"Terminal"`
  - `IsControlElementCore` returns `true`
  - `IsContentElementCore` returns `true`
- `TerminalView.OnCreateAutomationPeer` returns the peer.
- No `ITextProvider` / `ITextRangeProvider` implementation
  (unreachable without `GetPatternCore`).
- No `Screen` reference passed to the peer (no `GetText` to
  feed yet).

**What this gives NVDA**: the element appears in the UIA tree,
NVDA announces "Terminal, document" when focus reaches the
window, and Inspect.exe / FlaUI can find the element by
`ClassName="TerminalView"`. NVDA review-cursor reads on the
element will produce no text — the Text pattern isn't there.

### Stage 4 follow-up — Text-pattern exposure (deferred)

The Text pattern is the actual user-visible win — without it
NVDA can't read the buffer contents. Investigation paths to
explore with focused effort (not CI iteration):

1. **TerminalView implements `IRawElementProviderSimple`
   directly.** The COM-style raw UIA provider interface lives in
   `System.Windows.Automation.Provider` and IS visible to
   external assemblies (the spike's interface implementations
   compiled cleanly). TerminalView would expose patterns via the
   `IRawElementProviderSimple.GetPatternProvider(int)` method,
   which takes a UIA pattern ID instead of the F#-unfriendly
   `PatternInterface` enum. Routes around the protected-member
   visibility limit entirely. Likely the right answer; the cost
   is hand-wiring some plumbing that `AutomationPeer` provides
   automatically.
2. **Subclass a more specialized AutomationPeer that already
   exposes Text.** `TextBlockAutomationPeer` is the WPF built-in
   that handles Text. If its visibility surface differs from
   `FrameworkElementAutomationPeer`'s, subclassing it (even on
   a non-`TextBlock` element) might give us the Text pattern
   without re-overriding `GetPatternCore`. Worth a small spike.
3. **Reflection-based override registration.** If the runtime
   metadata has `GetPatternCore` (which it must, since
   Microsoft's own peer types use it), reflection could install
   our override at startup. Last-resort hack; brittle.

Track the investigation in a follow-up issue. The reduced 4a
peer that ships in PR #48 is a foundation that any of these
paths can build on without conflict — the Document role and
identity stay; the path adds Text on top.

### PR 4b — Navigation semantics

Implements the `ITextRangeProvider` methods that Stage 4 actually
needs for review-cursor navigation, against the contract NVDA tests
in practice.

- `Move(unit: TextUnit, count: int)` — moves both endpoints by N
  units of the requested kind.
- `MoveEndpointByUnit(endpoint, unit, count)` — moves one endpoint.
- Units to support: `Character`, `Word`, `Line`, `Paragraph`,
  `Document`. `Format` falls through to `Character`.
- Word boundaries follow the simple convention: a "word" is a
  contiguous run of non-whitespace cells. Whitespace boundaries are
  not announced; that matches NVDA's expectations for terminals.
- `Paragraph` and `Document` are functionally equivalent at this
  stage (no scrollback yet); both expand to the full grid.
- `Compare`, `CompareEndpoints`, `Clone`, `ExpandToEnclosingUnit`
  go from no-op stubs to real implementations.
- Manual smoke: NVDA Numpad 1/2/3 (char), 4/5/6 (word), 7/8/9
  (line) all announce the right thing. No infinite loops.

**Pass condition**: NVDA review-cursor navigation on the cmd.exe
startup banner reads each row's visible text, word-by-word and
character-by-character, without repeating or skipping.

### PR 4c — FlaUI integration test

First UIA test in `tests/Tests.Ui/`. Validates the producer-side
contract (what we expose) without depending on a real screen reader.

- Add `FlaUI.Core` and `FlaUI.UIA3` PackageReferences to
  `tests/Tests.Ui/Tests.Ui.fsproj`. Both are already pinned in
  `Directory.Packages.props`.
- New test: `Application.Launch` against
  `src/Terminal.App/bin/Release/net9.0-windows/Terminal.App.exe`
  (the framework-dependent build output, *not* the published
  self-contained binary — `dotnet test` runs before the publish
  step in `ci.yml`). Wait for the main window. Attach via
  `UIA3Automation`. Find the descendant by `ClassName=TerminalView`.
- Assertions:
  - `ControlType = Document`.
  - `Patterns.Text.IsSupported = true`.
  - `Patterns.Text.Pattern.DocumentRange.GetText(int.MaxValue)` is
    non-empty (cmd.exe will have produced its prologue by the
    time the test polls).
- Tear down: kill the spawned process; FlaUI handles UIA cleanup.
- Risk: FlaUI on `windows-latest` requires the interactive desktop
  session. GitHub Actions runs the build job on an interactive
  session by default but this PR is the first time the project
  actually uses it. If 4c's CI fails for desktop-session reasons,
  the fallback is to run the FlaUI test only via
  `workflow_dispatch` until we can validate it under
  `actions/runner` configurations.

**Pass condition**: green CI on `windows-latest`; the test runs
the actual app, attaches via UIA, and reads non-empty document
text.

### Snapshot rule (already in place)

UIA calls into the provider on a different thread from the WPF
Dispatcher. Mutating the buffer while UIA reads = crash (see
`Terminal.Accessibility` design notes in `spec/overview.md`). The
substrate landed in PR #38: `Screen.SnapshotRows(startRow, count)`
returns `(int64 * Cell[][])` under the same lock as `Screen.Apply`,
and `Screen.SequenceNumber` exposes the monotonic counter. Each
`ITextRangeProvider` constructor takes a `(sequence, snapshot)`
tuple and compares against `screen.SequenceNumber` later to detect
staleness. No additional locking is required in
`Terminal.Accessibility`.

### Manual NVDA validation (Stage 4 acceptance)

Documented in `docs/ACCESSIBILITY-TESTING.md`. NVDA+Numpad 7 (prev
line), Numpad 8 (current line), Numpad 9 (next line), Numpad 4/5/6
(word), Numpad 1/2/3 (character). The "NVDA" modifier is Caps Lock
or Insert depending on the user's NVDA layout setting; the canonical
notation is "NVDA+Numpad N". Should hear the visible terminal text.
"Broken" sounds like NVDA saying "TerminalView" then nothing
(no pattern) or "blank" (empty range) or repeating a line forever
(Move not advancing).

### What Stage 4 deliberately does NOT do

Guard against scope creep:

- **No streaming announcements** — that's Stage 5
  (`UiaRaiseNotificationEvent`).
- **No SGR attribute exposure on text ranges** — `GetAttributeValue`
  returns `NotSupported` for everything in Stage 4. Stage 5 wires
  up `UIA_ForegroundColorAttributeId` etc.
- **No selection lists / list provider** — Stage 8.
- **No keyboard input → PTY** — Stage 6.
- **No `RaiseNotificationEvent` calls** — Stage 5. (Threading
  reminder for then: any `RaiseNotificationEvent` call must run on
  the Dispatcher thread. `TermControlAutomationPeer.cpp` in
  microsoft/terminal is the reference; F# has the same constraint.)
- **`IsContentElement = true` on all rows for now** — will likely
  need to be `false` on rows that are part of a List subtree once
  Stage 8 lands.

## Recommended reading order for a new session

1. **This file.**
2. [`CONTRIBUTING.md`](../CONTRIBUTING.md) — PR shape, branching,
   CHANGELOG discipline, F# / WPF gotchas, documentation policy.
   Working conventions all live here.
3. [`spec/tech-plan.md`](../spec/tech-plan.md) §1–§4 (the stages
   already shipped + Stage 4) — establishes the architectural
   grain.
4. [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — what's stable, what
   tags need pushing, how rollback works.
5. [`docs/CONPTY-NOTES.md`](CONPTY-NOTES.md) — observed platform
   quirks. Render-cadence finding is the one most likely to bite
   again.
6. [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) "Common pitfalls"
   section — every diagnostic loop's lessons end up here.
7. Skim [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` for
   in-flight work and the most recent shipped section for the
   last release narrative shape.
8. Browse `src/` top-down: `Terminal.Core` (data) → `Terminal.Pty`
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
