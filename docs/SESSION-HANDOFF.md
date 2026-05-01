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
| **Last merged stages** | **Stage 4** (UIA Document + Text pattern + Line/Character/Word navigation + focus-into-TerminalSurface fix, PRs #54-#56, #59, #60, #68) and **Stage 11** (Velopack auto-update via `Ctrl+Shift+U`, PRs #63, #66). Both **NVDA-verified working** on a clean Windows 11 install — Stage 4 on `v0.0.1-preview.22` and `.26`, Stage 11 via a successful `preview.25 → preview.26` self-update. |
| **Last shipped release** | `v0.0.1-preview.26` (or whichever preview the maintainer last cut — release cadence is ad-hoc during the unsigned-preview line). The auto-update path replaces the `scripts/install-latest-preview.ps1` bridge for in-place updates; the script is now **deprecated for in-place updates** but remains useful for fresh installs and dev-environment workflows. |
| **In-flight branch** | None. Stages 4 and 11 are both fully merged and verified. Word-navigation (the only Stage 4 follow-up that needed code) shipped in PR #68. Process-cleanup smoke (the only Stage 4 row not yet exercised in NVDA verification) is logged as a maintainer pending item. |
| **Next stage** | **Stage 5 — Streaming output notifications.** First stage where the user gets passive narration as terminal output streams in, rather than active review-cursor exploration. Validation: NVDA reads `dir` line-by-line; spinner-class redraws don't flood NVDA's speech queue; busy loops printing dots don't get NVDA stuck. Substrate: the `Screen.SequenceNumber` + `Screen.SnapshotRows` primitives (PR #38), the parser→UIA notification channel seam shipped in the audit-cycle PR-B, and Stage 11's existing `TerminalView.Announce` raise path (PR #63). Stage 5 plugs the coalescer (Channel<int> per-row tickets + 200ms debounce + hash dedup) between the parser and that channel — the seams are in place; only the coalescing logic is new. |

The end-to-end pipeline now reaches the auto-update boundary:
launching the app spawns `cmd.exe` under ConPTY, parses its
output, applies VtEvents to a 30×120 `Screen`, renders the buffer
in a custom WPF `FrameworkElement`, exposes that buffer to NVDA
via UIA Document role + Text pattern with working Line / Word /
Character / Document review-cursor navigation, and self-updates
to subsequent previews via `Ctrl+Shift+U` with NVDA progress
narration plus an audible version-flip on restart. Stage 5 is
the first stage where output starts narrating itself instead of
waiting for the user to navigate to it.

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

2. **Stage 4 + Stage 11 NVDA verification — complete except
   for one process-cleanup row.** The maintainer ran the
   relevant rows of
   [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
   on `v0.0.1-preview.22` (Stage 4 first pass), `.26` (Stage
   4 word-navigation re-verification + Stage 11 self-update
   verification) on Windows 11 + NVDA. Outcome:

   **Stage 4 (UIA Document + Text pattern + navigation):**

   - ✓ Document role announced on focus.
   - ✓ Review cursor reads current line, prev / next line,
     prev / next character, prev / next word.
   - ✓ Window title accessibility name (`NVDA+T`) reads
     "pty-speak terminal {version}" (version suffix shipped
     in PR #66).
   - ✓ Re-launch from Start menu works cleanly.
   - ↻ **Still deferred — Test 8 "Process cleanup on close":**
     Alt+F4 the running app, wait ~3 s, open Task Manager →
     Details, confirm neither `Terminal.App.exe` nor orphan
     `cmd.exe` remains. Pass condition is in the
     "Launch and process hygiene" section of the matrix.
     Low priority because no orphan accumulation has been
     observed in repeated launch / close cycles during
     Stage 4 + 11 verification, but should be checked once
     before any non-prerelease (`v0.1.0`+) tag.

   **Stage 11 (Velopack auto-update):**

   - ✓ `Ctrl+Shift+U` from inside `preview.25` triggered the
     update flow.
   - ✓ NVDA narrated "Checking for updates" → "Downloading"
     → bucketed percent updates → "Restarting to apply
     update".
   - ✓ App restarted automatically at preview.26.
   - ✓ Post-restart, NVDA+T reads "pty-speak terminal
     0.0.1-preview.26" — audible confirmation the version
     actually flipped.
   - Negative paths (offline, network failure, repeat
     keypresses) covered by PR #66's structured error
     announcements; not yet manually exercised in
     verification, but failure-mode logic is exercised by
     the in-progress dedup test path.

   Both Stage 4 follow-ups discovered during verification
   have shipped:

   - ✓ **Word-navigation real implementation** — PR #68.
     `IsWordSeparator` is whitespace-only (space + tab);
     punctuation stays inside words so paths like
     `C:\Users\test>` read as one token. UAX #29 / vim-
     style is a future refinement only if user feedback
     warrants; see "Word boundaries" rationale at the top
     of `TerminalTextRange` in
     `src/Terminal.Accessibility/TerminalAutomationPeer.fs`.
   - ✓ **Window title version suffix** — PR #66.
     `MainWindow.xaml.cs` reads
     `AssemblyInformationalVersionAttribute` and sets both
     `Title` and `AutomationProperties.Name` to include
     the version. Strips any `+commit-sha` deterministic-
     build trailer.

   For Stage 4 / 11 NVDA failures going forward, file a
   fresh issue — a Stage 4 regression is most likely a
   `GetPattern`
   override regression on `TerminalAutomationPeer` per
   the Stage-4 diagnostic decoder.

3. **CI / release timing optimisation — partial completion.**
   Audit-cycle PR-E shipped the highest-leverage optimisation:
   `~/.dotnet/tools` is now cached across CI runs in both
   `ci.yml` and `release.yml`, so `dotnet tool install -g vpk`
   no longer re-downloads (~10s saved per run). Cache key is
   statically versioned; bump `v1 → v2` in the cache key
   when a new vpk version is wanted.

   Two candidate trims investigated and DEFERRED (low-value
   relative to restructuring cost):

   - **Merge two `gaurav-nelson/github-action-markdown-link-check`
     steps into one invocation.** The action takes either
     `folder-path` OR `file-path` (not both), so combining
     would require enumerating all 14 markdown files
     explicitly OR scanning the whole repo (which would
     break the deliberate `spec/` exclusion documented in
     the workflow comments). Savings: ~15-20s. Cost:
     either ugly file enumeration that drifts from reality
     as docs are added, or losing the `spec/`-immutable
     URLs exclusion. Not worth it; revisit if the action
     gains a multi-folder option.

   - **Audit `release.yml` for similar wins** (vpk pack
     input cache, gh release-download retry on transient
     5xx). The vpk-pack input is per-build artefacts (no
     cache opportunity). The gh-fetch retry would help
     on transient flakes but hasn't actually flaked in any
     of our release runs to date — defer until a flake
     happens. PR-E added a doc note in this file rather
     than guess-coding for a non-issue.

   Constraint preserved: `continue-on-error: true` on
   link checks (advisory by design), Velopack pack smoke
   step (catches packaging regressions before release
   day), `--locked-mode` once NuGet lock files land
   (item 4) all unchanged.

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

5. **Audit-cycle SR-3 deferred follow-ups (not coded, tracked).**
   The security-audit cycle (SR-1..SR-3, November-December 2025)
   identified three items genuinely worth deferring rather than
   shipping inline. Each is tracked in `SECURITY.md`'s inventory;
   the items below are the action handles a future maintainer
   needs to actually close them.

   1. **ConPTY environment scrub (PO-5).** Parent's full env
      block inherits to the child via `lpEnvironment=IntPtr.Zero`
      in `CreateProcess`, so sensitive vars (`GITHUB_TOKEN`,
      `OPENAI_API_KEY`, etc.) reach the child shell. To close:
      build an allow/deny-list inside `Terminal.Pty/Native.fs`,
      construct an env block from filtered entries, pass to
      `CreateProcess` via the `lpEnvironment` parameter. Risks:
      breaking developer workflows that depend on inherited
      `PATH` / locale variables; getting the F# string-block
      marshalling exactly right (must be double-NUL terminated,
      sorted order matters for some tools).
   2. **`install-latest-preview.ps1` TOCTOU (D-1, T-10).**
      A local attacker can swap the `Setup.exe` in `%TEMP%`
      between `Unblock-File` and `Start-Process`. Mitigation
      options: download to a path the attacker process can't
      write to (e.g. via per-user `%LocalAppData%` with strict
      ACLs); compute SHA-256 of the downloaded bytes and verify
      before running; or sign the file before run (which is the
      same v0.1.0+ work that makes the script unnecessary
      altogether). The script is dev-iteration tooling per
      `scripts/README.md`; defer until it's used outside that
      context.
   3. **`TerminalView.OnRender` `_screen` lock (Acc/9).**
      `OnRender` reads `_screen` without holding any lock;
      Stage 3b is safe because the parser runs on the
      dispatcher and the read happens on the same thread.
      Stage 5 will move the parser off the dispatcher and
      rework snapshot-on-render; the lock decision belongs in
      that work, not in a one-liner now. A one-line forward-
      reference comment in `OnRender` deferring to Stage 5 is
      the lightweight handle.

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

## Stage 11 implementation sketch (next)

Stage 11 was re-prioritised to land next — see "Where we left off"
and `docs/ROADMAP.md` "Stage ordering" for the rationale.
`spec/tech-plan.md` §11 has the full spec; this sketch is the
pre-digested implementation plan.

**Goal:** `Ctrl+Shift+U` from inside the running app downloads the
next preview's delta nupkg and restarts within ~2 seconds, with
NVDA progress announcements throughout. Replaces the standalone
`scripts/install-latest-preview.ps1` for in-place updates.

**Implementation outline:**

1. Add `Velopack` NuGet reference to `Terminal.App.fsproj`
   (already in scope per `spec/tech-plan.md` §0; the reference may
   already be transitive through the Velopack tooling, verify
   first).

2. In `Terminal.App/Program.fs`, replace the existing bare
   `VelopackApp.Build().Run()` with the full update protocol:
   - `VelopackApp.Build().Run()` stays first (must run before any
     WPF type loads — Velopack issue #195).
   - After WPF startup, construct `UpdateManager` pointing at the
     GitHub Releases source for our channel.
   - On `Ctrl+Shift+U`, kick a background task that calls
     `CheckForUpdatesAsync` → `DownloadUpdatesAsync` (with progress
     callback) → `ApplyUpdatesAndRestart`.

3. Wire the keybinding via `MainWindow.xaml`'s `InputBindings`
   (a `KeyBinding` with `Modifiers="Control+Shift"` and `Key="U"`).
   Or in code-behind on `MainWindow.xaml.cs`. The keybinding must
   not pass through to the PTY (Stage 6's input pipeline isn't
   shipped yet but design for forward compat).

4. NVDA progress announcements: each phase ("Checking for
   updates", "Downloading X.Y.Z", "X% downloaded", "Restarting")
   raises a UIA Notification event with `displayString` set. The
   Stage 4 `TerminalAutomationPeer` is the natural raise point —
   it's already focused per PR #60.

5. Failure surfaces (no update available, network failure,
   signature mismatch once signing returns): each becomes a
   distinct NVDA announcement, never a silent failure. Use
   structured error matching on Velopack's exceptions, not
   string contains.

6. FlaUI integration test in `tests/Tests.Ui/`: launch the app,
   send Ctrl+Shift+U via UIA `IInvokeProvider` or keyboard
   simulation, assert the Notification event fires. Don't
   assert actual download/restart in CI (that needs a real
   GitHub release to exist) — file as a manual smoke row.

7. Manual smoke matrix row in `docs/ACCESSIBILITY-TESTING.md`
   Stage 11 section already exists; the diagnostic decoder I
   added in PR #58 covers the failure modes.

**Pitfalls already captured in `spec/tech-plan.md` §11.5:**
- Don't request elevation (Velopack discussion #8 — manifest must
  be `asInvoker`).
- `VelopackApp.Build().Run()` MUST be first, or you get the
  endless restart loop from Velopack issue #195.
- Test installer flow on a clean VM (the Stage-4 manual matrix
  installer pass already does this).

**Scope discipline:** Stage 11 is one PR. If during implementation
we discover Velopack's API needs more glue than expected, that's
a sign to spike first (the same Stage-4 spike-then-PR pattern
that worked for PRs #51-#56). Don't combine Stage 11 with Stage 5
or any other stage.

## Stage 4 implementation sketch (shipped — retained as reference)

> **Status:** all of Stage 4 has merged on `main` as of PR #60.
> The sketch below is the pre-digested execution plan that drove
> PRs #54-#56 + #59 + #60; it's preserved as reference for the
> architectural reasoning (especially the WM_GETOBJECT vs
> AutomationPeer.GetPattern pivot in PR #56) but is no longer the
> active plan.

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

### Stage 4 follow-up — Text-pattern exposure (in flight)

The Text pattern is the actual user-visible win — without it
NVDA can't read the buffer contents. Investigation status:

- **Option 2 ruled out (PR #50, FS0855).** `TextBlockAutomationPeer`
  has the same `GetPatternCore` visibility limit as
  `FrameworkElementAutomationPeer`. The AutomationPeer extension
  point for `GetPatternCore` is closed across all subclasses in
  the .NET 9 WPF reference assembly set, not just one specific
  parent class. There is no specialized `*AutomationPeer` to
  subclass that opens up the override.

- **Option 1 (current path): `TerminalView` implements
  `IRawElementProviderSimple` directly.** The COM-style raw UIA
  provider interface lives in `System.Windows.Automation.Provider`
  and IS visible to external assemblies (the PR #47 spike's
  interface implementations compiled cleanly there). The
  interface exposes patterns via
  `IRawElementProviderSimple.GetPatternProvider(int)` — taking a
  UIA pattern ID (an int constant) instead of the
  `PatternInterface` enum that the AutomationPeer override path
  required. Routes around the protected-member visibility limit
  entirely.

  Implementation shape for the next PR:
    * `TerminalView : FrameworkElement` (C#) implements
      `IRawElementProviderSimple` directly: the four interface
      members are `ProviderOptions`, `GetPatternProvider(int)`,
      `GetPropertyValue(int)`, and `HostRawElementProvider`.
    * `GetPatternProvider(UIA_TextPatternId /* 10014 */)` returns
      a `TerminalTextProvider` instance from F#.
    * `GetPropertyValue(int)` returns Document role / ClassName /
      Name properties — the same identity the
      `TerminalAutomationPeer` already reports, but on a
      different code path.
    * `HostRawElementProvider` returns the AutomationPeer's host
      provider so WPF's standard tree integration still works.
    * The F# `TerminalTextProvider` and `TerminalTextRange` types
      from the deleted PR #48 attempt can be revived; they
      compiled cleanly.
  Tracked in [Issue #49](https://github.com/KyleKeane/pty-speak/issues/49).

- **Option 3 ruled out (PR #52 reflection probe).** The runtime
  metadata strips `GetPatternCore` the same way the public
  reference assembly does — a reflection probe via
  `BindingFlags.Instance | NonPublic | Public | FlattenHierarchy`
  on `FrameworkElementAutomationPeer` finds zero matches for
  `GetPatternCore`. Sanity baseline (`GetClassNameCore` IS
  findable via the same probe) confirms the test infrastructure
  isn't the cause. Reflection-based binding is therefore not a
  viable architectural path. The probe is preserved as a
  regression sentinel in
  `tests/Tests.Ui/AutomationPeerReflectionTests.fs`; if a future
  .NET update exposes `GetPatternCore` in runtime metadata,
  the sentinel test fails and we know to re-evaluate.

The reduced PR #48 peer that ships the Document role + identity
stays; the IRawElementProviderSimple path adds Text on top
without removing what's already working.

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
