# Changelog

All notable changes to `pty-speak` will be documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project follows [Semantic Versioning](https://semver.org/).

Release tags follow the pattern `vMAJOR.MINOR.PATCH` (e.g. `v0.1.0`),
or `vMAJOR.MINOR.PATCH-preview.N` / `-rc.N` for prereleases.
Releases are produced by **publishing a release** in the GitHub
Releases UI (which creates the tag). The `release: published` event
triggers the Velopack release workflow described in
[`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md), which builds the
artifacts and updates the just-published release with the proper
title, body, and Velopack `Setup.exe` + nupkg + `RELEASES` files.

## [Unreleased]

### Added

- **`scripts/install-latest-preview.ps1` — one-command preview
  installer for Windows.** Downloads the latest (or specified)
  preview's `Setup.exe` from the GitHub Release assets, strips
  the Mark-of-the-Web tag with `Unblock-File` so SmartScreen
  doesn't prompt the unsigned-preview line on every iteration,
  and runs the installer. Replaces the multi-step "open the
  release page → navigate the asset list → click `Setup.exe`
  → click 'More info' → click 'Run anyway'" flow that takes
  several screen-reader steps per iteration with a single
  command. Scoped to the iterative-smoke-testing workflow that
  Stage 4+ NVDA verification needs; once Stage 11 ships
  Velopack delta self-update via `Ctrl+Shift+U`, this script
  becomes unnecessary for in-place updates. New `scripts/README.md`
  documents the script and reserves the directory for future
  utilities.

### Fixed

- **`MainWindow` moves keyboard focus to `TerminalSurface` on
  `Loaded`.** `v0.0.1-preview.21` install smoke established that
  even with PR #59's working Text-pattern navigation, NVDA still
  couldn't reach the buffer: focus stayed on the WPF `Window`
  after launch, so NVDA announced "pty-speak terminal, window"
  and anchored the review cursor on the Window (which has no
  Text pattern). The `TerminalView`'s Document-role peer with
  the working Text pattern was reachable in the UIA tree but
  invisible to NVDA's review cursor because focus was on the
  wrong element. One-line fix in `MainWindow.xaml.cs`: hook
  `Loaded` and call `TerminalSurface.Focus()`. NVDA now
  announces "Terminal, document" on launch and the review
  cursor anchors to the TerminalView, where PR #59's
  navigation is reachable.

- **Stage 4 Text-pattern navigation: NVDA's review cursor can
  now read the terminal buffer.** `v0.0.1-preview.20` install
  smoke established that PR #56's Text-pattern surface was
  reachable but unusable: NVDA's "read current line" returned
  "blank" and prev/next-line did nothing. Root cause was that
  `TerminalTextRange`'s `ExpandToEnclosingUnit`, `Move`,
  `MoveEndpointByUnit`, and `MoveEndpointByRange` were all
  no-op stubs from PR #56's "navigation deferred to PR D"
  scope. Without them NVDA's review cursor couldn't delimit a
  line: `ExpandToEnclosingUnit(Line)` was silently dropped,
  leaving the range collapsed at start with empty `GetText`
  output. Implementation in this commit:

  - `TerminalTextRange` now tracks mutable `(startRow,
    startCol, endRow, endCol)` endpoints (the UIA contract
    requires the void-returning navigation methods to mutate
    in place).
  - `ExpandToEnclosingUnit` handles `Character`, `Document`,
    and `Line` (other unit types degrade to `Line` until a
    terminal-output tokenizer arrives).
  - `Move`, `MoveEndpointByUnit`, `MoveEndpointByRange`
    implement UIA's contract including endpoint-collision
    handling (range collapses to the moved point if endpoints
    cross).
  - `CompareEndpoints` returns the lexicographic ordering
    over `(row, col)` positions.
  - `GetText` uses the range endpoints (was returning the
    entire snapshot regardless of range).
  - `DocumentRange` constructs a half-open `[(0,0), (rows, 0))`
    range matching UIA's standard endpoint convention.

  `tests/Tests.Ui/TextPatternTests.fs` gains a navigation
  regression test that asserts `ExpandToEnclosingUnit(Line)`
  bounds the range length below the full-document size, and
  `Move(Line, 1)` preserves the Line shape — the two
  invariants whose violation produced the preview.20
  failure mode.

- **Removed `MainWindow.xaml`'s
  `AutomationProperties.HelpText`.** Preview.20 NVDA smoke
  heard "Screen-reader-native Windows terminal. Stage 3b:
  bytes from a child shell are parsed and rendered; UIA
  exposure lands in Stage 4." read after the role
  announcement on every focus. That string was useful as
  developer documentation while the project was bootstrapping
  but is verbose chatter for the user. The window's name and
  Document role are sufficient.

### Added

- **Comprehensive manual smoke-test matrix
  (`docs/ACCESSIBILITY-TESTING.md` rewrite).** Reframes the
  accessibility-only doc as the universal manual-validation gate
  every release must pass. Three new always-run sections cover
  artifact integrity (Velopack assets present, Setup.exe
  non-zero, Authenticode status, hash matches) and launch /
  process hygiene (single window, version in title, one-deep
  process tree, clean shutdown, no orphan `cmd.exe`,
  re-launch). Every per-stage table now has a "Diagnostic
  decoder" subsection that maps each possible failure to the
  likely-responsible subsystem — file paths and PR numbers
  where applicable, so a FAIL goes from "something broke" to
  "look at file X, PR Y" without further triage. New top-level
  sections "Adding new manual tests" (criteria, required
  fields, where in the matrix), "Sunset rules" (when a row
  graduates to CI or gets deleted), and "Coverage that moved
  to CI" (audit trail of the matrix's shrinking surface area
  as automated assertions land). The Stage 4 section is
  rewritten to reflect the actual ship architecture
  (`AutomationPeer.GetPattern` override after the WM_GETOBJECT
  pivot, not the original raw-provider plan), and notes that
  PR #56's `tests/Tests.Ui/TextPatternTests.fs` now CI-pins
  the producer side of the Text pattern. `docs/RELEASE-PROCESS.md`
  step 5 was rewritten to defer to the comprehensive matrix
  rather than carry its own minimal smoke list, and
  `docs/SESSION-HANDOFF.md` item 3 (the visual install smoke
  for the maintainer) now points at the matrix and tracks
  Stage 4 NVDA verification alongside the existing single-window
  check. The PR template's accessibility checklist gains an
  "Adding new manual tests" reminder so PRs that ship
  CI-unverifiable behaviour grow the matrix in the same change.
  README's docs-index entry is updated to reflect the broader
  scope.

- **Stage 4 PR C — UIA Text pattern via `AutomationPeer.GetPattern`
  override + FlaUI verification test
  (`tests/Tests.Ui/TextPatternTests.fs`).** PR C started as a
  pure verification test for PR B's WM_GETOBJECT raw-provider
  path, but two CI iterations on `windows-latest` revealed an
  architectural finding that changed the path entirely:

  - The first iteration showed UIA3 (which NVDA / Inspect.exe
    / FlaUI.UIA3 use) dispatches `WM_GETOBJECT` with
    `UiaRootObjectId` (-25), never `OBJID_CLIENT` (-4), so
    PR B's `OBJID_CLIENT`-only match never surfaced any
    patterns to UIA3 clients. Diagnostic from a 29-line
    log dump.
  - The second iteration extended the match to also handle
    `UiaRootObjectId`. That broke the entire UIA tree: every
    UI test regressed with either an HRESULT failure on
    `UIA3Automation.FromHandle` or "TerminalView descendant
    not found in the UIA tree." The root cause is that
    `WM_GETOBJECT(UiaRootObjectId)` expects a provider
    implementing `IRawElementProviderFragmentRoot` — UIA
    needs the fragment-navigation surface to traverse from
    the returned root into descendants, and our simple
    provider can't supply that even with
    `HostRawElementProvider` wired.
  - The third iteration found the actual right path:
    `AutomationPeer.GetPattern` is `public virtual` (unlike
    the unreachable `protected virtual GetPatternCore` that
    bit PR #48), so external assemblies CAN override it. The
    override adds the Text pattern to the SAME peer that's
    already in WPF's tree, leaving WPF's fragment navigation
    untouched. No `WM_GETOBJECT` interception of UIA3
    messages needed.

  What ships in PR C:

  - `TerminalAutomationPeer` gains an `ITextProvider`
    constructor parameter and a `GetPattern` override that
    returns it for `PatternInterface.Text`, deferring to
    `base.GetPattern` for every other interface.
  - `TerminalView.OnCreateAutomationPeer` passes the
    `TextProvider` it constructed in PR B through to the
    peer.
  - `WindowSubclassNative` reverts to matching `OBJID_CLIENT`
    only — kept as a defensive MSAA fallback rather than the
    primary UIA path. The `UiaRootObjectId` constant is
    documented in the source as the discovery that drove the
    pivot.
  - `tests/Tests.Ui/TextPatternTests.fs` walks the UIA tree
    for the first element exposing the Text pattern, calls
    `DocumentRange.GetText(-1)`, and asserts the result has
    the expected minimum length (30 rows × 120 cols + 29
    row-joining newlines = 3629 chars for the
    `Program.compose` default screen size) plus at least one
    `\n`. Specific cell content from cmd.exe's banner is
    deliberately not asserted — banner wording isn't
    deterministic across Windows builds.
  - Test failure messages dump the WM_GETOBJECT log and the
    visible pattern flags so a future regression diagnoses
    itself without further iteration. That diagnostic
    machinery is what made the architectural finding
    tractable in the first place; it stays in place.

- **Stage 4 PR B — Text-pattern provider scaffolding +
  WM_GETOBJECT raw-provider path (legacy MSAA fallback).**
  `Terminal.Accessibility` gains real `TerminalTextProvider`
  and `TerminalTextRange` types whose `DocumentRange.GetText`
  returns a `\n`-joined render of the current
  `Screen.SnapshotRows` capture. `TerminalView` exposes the
  provider as a public `TextProvider` property; PR C wires it
  into the UIA peer's `GetPattern` override (the actual UIA3
  surface). The PR also ships a `Views/TerminalRawProvider.cs`
  implementing `IRawElementProviderSimple` and extends the
  `WindowSubclassNative` hook from PR A (#54) to return that
  provider for `WM_GETOBJECT(OBJID_CLIENT)` queries. PR C's
  CI iteration revealed UIA3 never queries `OBJID_CLIENT` —
  it uses `UiaRootObjectId` instead, which can't be
  intercepted with a simple provider — so the raw-provider
  path is kept as a defensive fallback for legacy MSAA
  clients only, not as the primary UIA path. Stage 4
  navigation (`Move`, `MoveEndpointByUnit`, attribute
  exposure) is still stubbed; the per-cell SGR exposure
  arrives in a later stage.

  The previously throwaway `Terminal.Accessibility/RawProviderSpike.fs`
  is removed — the foundation finding it captured (F# can
  implement `IRawElementProviderSimple`) is now demonstrated by
  the production code.

- **FlaUI integration test infrastructure
  (`tests/Tests.Ui/AutomationPeerTests.fs`).** First UIA test in
  the project: launches `Terminal.App.exe` from the build
  output, attaches via `UIA3Automation`, finds the `TerminalView`
  descendant by `ClassName`, and asserts `ControlType=Document`
  and `Name="Terminal"`. `Tests.Ui.fsproj` gains
  `FlaUI.Core` / `FlaUI.UIA3` package references and a
  `ReferenceOutputAssembly="false"` ProjectReference to
  `Terminal.App` so MSBuild builds the app before the test runs
  without linking its outputs into the test bin. The test is
  scoped to validate that PR #48's reduced-scope peer actually
  works at runtime (the `TerminalView` element is reachable via
  UIA with the expected role and identity) and to give any
  future Text-pattern attempt an automated verification harness
  before merging. This is the foundation piece that any further
  Stage 4 work — raw `IRawElementProviderSimple` provider,
  reflection-based binding, or anything else — needs in place.

- **Stage 4a (reduced scope) — UIA Document role + identity.**
  `TerminalView` now exposes a `TerminalAutomationPeer` via
  `OnCreateAutomationPeer`. UIA clients (NVDA, Inspect.exe) find
  the terminal element in the automation tree with
  `ControlType=Document`, `ClassName="TerminalView"`,
  `Name="Terminal"`, `IsControlElement=true`, and
  `IsContentElement=true`. The peer subclasses
  `FrameworkElementAutomationPeer` and overrides only the five
  parameterless `*Core` methods that the spike (PR #47) confirmed
  compile cleanly from F# under `Nullable=enable`.

  **What this PR deliberately does NOT ship:** the Text pattern
  (`ITextProvider` / `ITextRangeProvider`), navigation (`Move`,
  `MoveEndpointByUnit`), and SGR attribute exposure
  (`GetAttributeValue`). All three depended on overriding
  `AutomationPeer.GetPatternCore`, which CI iteration on this PR
  established is not reachable from any external assembly in the
  .NET 9 WPF reference assembly set. C# CS0117 fires on
  `base.GetPatternCore(...)` with "FrameworkElementAutomationPeer
  does not contain a definition for 'GetPatternCore'"; F# FS0855
  on `override _.GetPatternCore(...)` is the same finding via a
  different error code. Microsoft's documented examples that
  override `GetPatternCore` evidently compile only against
  internal Microsoft assemblies where the protected member is
  visible; the public reference assembly surfaces the type
  without the override target.

  Text-pattern exposure is therefore deferred to a follow-up
  Stage 4 PR. The likely path is implementing
  `IRawElementProviderSimple` directly on `TerminalView`,
  bypassing the `AutomationPeer` hierarchy that wraps the
  unreachable protected metadata. Investigation continues with
  focused effort rather than CI iteration; tracked in
  `docs/SESSION-HANDOFF.md` Stage 4 sketch.

- **README addition: "The complexities of trying to work with
  technology as a blind developer."** Records the maintainer's
  account of why this project exists, the iOS Claude Code
  workaround (disable VoiceOver, place finger by remembered
  pixel location, re-enable VoiceOver) used to interact with
  Claude on every message of this session, and an idiom
  (`blindly iterating`) the model produced in this session that
  is not literally accurate and casually demeans the people whose
  working conditions this project is being built to improve. The
  preferred phrasing is the literal one — `iterating without
  information`, `speculative iteration`, `guessing without
  evidence` — because it communicates more precisely and removes
  the sight-as-knowledge metaphor.

- **Stage 4 spike — F# AutomationPeer + ITextProvider /
  ITextRangeProvider interop probe.** `Terminal.Accessibility`
  gains `TerminalAutomationPeer.fs` replacing the empty
  `Placeholder.fs`. The spike subclasses
  `FrameworkElementAutomationPeer`, defines stub
  `TerminalTextProvider` and `TerminalTextRange` types
  implementing the C# UIA provider interfaces, and overrides the
  five Core methods (`GetAutomationControlTypeCore`,
  `GetClassNameCore`, `GetNameCore`, `IsControlElementCore`,
  `IsContentElementCore`). Every method is a no-op; PR 4a wires
  `GetText` to the real `Screen` snapshot and PR 4b implements
  navigation. The fsproj gains `<UseWPF>true</UseWPF>` so
  WindowsBase / PresentationCore / UIAutomationProvider /
  UIAutomationTypes are resolvable. **No source-level wiring**:
  `TerminalView.OnCreateAutomationPeer` is unchanged in this PR;
  the peer is reachable as a type but never instantiated yet, so
  there's no behavior change for the running app. The spike's
  purpose is to surface F#-meets-C# interop foot-guns (analogous
  to the `out SafeFileHandle&` byref bug from Stage 1) before
  building 250+ lines of dependent code on top.

### Changed

- **Stage 4 implementation plan revised: spike + three small PRs
  instead of one big PR.** After completing the pre-Stage-4
  cleanup pass and re-reading the `ITextProvider` /
  `ITextRangeProvider` interfaces, the original "single PR,
  ~250-400 lines" estimate looked low by ~2x and bundled three
  independent review concerns (F#-meets-C# interop, navigation
  semantics, integration testing). New plan:
  1. **Spike** — 30-line throwaway proving F# can subclass WPF's
     `FrameworkElementAutomationPeer` and implement
     `ITextProvider` without an interop foot-gun on the order of
     the `out SafeFileHandle&` bug from Stage 1.
  2. **PR 4a — Minimal UIA surface.** `TerminalAutomationPeer`
     + `TerminalTextProvider` with `DocumentRange` / `GetText`
     working; every other `ITextRangeProvider` method stubbed to
     compile. Wires `TerminalView.OnCreateAutomationPeer`. Manual
     smoke via Inspect.exe + NVDA "current line".
  3. **PR 4b — Navigation semantics.** `Move` /
     `MoveEndpointByUnit` for Character/Word/Line/Paragraph/Document;
     `Compare` / `Clone` / `ExpandToEnclosingUnit` go from stubs
     to real implementations.
  4. **PR 4c — FlaUI integration test.** First test in
     `tests/Tests.Ui/`; adds FlaUI package references and asserts
     `ControlType=Document`, `Text` pattern present, non-empty
     `DocumentRange.GetText`. Also the de facto check that FlaUI
     works on the `windows-latest` GitHub Actions runner.
  Updated in `docs/SESSION-HANDOFF.md` (Stage 4 sketch),
  `docs/ROADMAP.md` (Stage 4 row), `docs/ARCHITECTURE.md` (Stage 4
  pointer), `docs/ACCESSIBILITY-TESTING.md` (Stage 4 matrix
  header note about which row lands in which PR). The spec
  (`spec/tech-plan.md` §4) is unchanged per the immutable-spec
  policy — this revision is purely about implementation order.

### Fixed

- **Parser preserves the in-flight digit param across the
  Param → Intermediate transition (closes
  [Issue #42](https://github.com/KyleKeane/pty-speak/issues/42)).**
  `StateMachine.fs`'s `CsiParam → CsiIntermediate` and
  `DcsParam → DcsIntermediate` edges previously called
  `collectIntermediate` without first calling `pushParam`, so
  inputs like `\x1b[1$q` (CSI param + intermediate) or
  `\x1bP1$q...` (DECRQSS-shape DCS) emitted dispatch events
  with `parms = [||]` instead of `[|1|]`. Both edges now push
  the in-flight digit before transitioning, matching Williams'
  canonical `param;collect` action and alacritty/vte. The
  `CAN inside DCS passthrough emits DcsUnhook` test was
  re-augmented with the `$` byte (which used to be deliberately
  removed in #41 to dodge this bug); a new
  `CSI with param + intermediate preserves the in-flight digit`
  test pins the parallel CSI invariant so a future regression
  in either edge fails loudly.
- **CI release workflow now fetches the prior release `*-full.nupkg`
  before `vpk pack`, so deltas are produced for every non-first
  release.** `v0.0.1-preview.18` and `v0.0.1-preview.19` both
  shipped full-only — Velopack only generates a `*-delta.nupkg`
  when a prior `*-full.nupkg` exists in `--outputDir` at pack
  time, and CI starts from a fresh runner each release. New
  step uses `gh release list` + `gh release download
  --pattern '*-full.nupkg'` to drop the previous release's full
  package into `releases/` before `vpk pack`. A subsequent
  cleanup step removes the prior nupkg before the softprops
  upload so it doesn't get re-attached to the current release as
  a duplicate. First release on a channel (no prior to diff
  against) is handled silently — `gh release list` returns
  empty and the step logs and skips. Auto-update clients on
  the next release will fetch ~KB-sized delta packages instead
  of ~66 MB full nupkgs. `docs/RELEASE-PROCESS.md` updated to
  describe both new steps and the renumbered downstream steps.
- **`Terminal.App.exe` no longer allocates a console window at
  startup.** `Terminal.App.fsproj` previously set
  `OutputType=Exe` + `DisableWinExeOutputInference=true`, which
  forced the produced executable into the Windows console
  subsystem; Windows allocated a conhost for the parent process
  before any of our code ran, and that empty console window
  appeared behind the WPF window on every launch
  ([Issue #39](https://github.com/KyleKeane/pty-speak/issues/39)).
  Investigation ruled out the four ConPTY-side hypotheses
  originally listed (`STARTUPINFOEX.cb`, attribute attachment,
  `STARTF_USESTDHANDLES`, `CREATE_NEW_CONSOLE`) — the ConPTY
  setup matches Microsoft's canonical sample exactly. Switched
  the executable to `OutputType=WinExe` (Windows GUI subsystem,
  matching what the WPF SDK would auto-infer) and dropped the
  `DisableWinExeOutputInference` opt-out. No source changes
  needed; `grep` confirms zero `Console.WriteLine`/`printfn`
  calls in `src/`, so nothing was relying on an attached
  console. Visual smoke verification needs a Windows install of
  the next preview release.
- **`release.yml` now uploads Velopack's channel-suffixed manifest
  files.** `v0.0.1-preview.18` shipped with only three release
  assets (`*-full.nupkg`, `*-Setup.exe`, `RELEASES`) instead of the
  five Velopack produces. Root cause: the `softprops/action-gh-release`
  upload pattern was the literal `releases/releases.json`, but
  Velopack outputs `releases.<channel>.json` (we get `releases.win.json`
  for win-x64 packs since we don't pass `--channel`). With
  `fail_on_unmatched_files: false` the literal pattern matched
  nothing and the manifest was silently skipped — auto-update flows
  would have broken for any user installing from the release.
  Patterns updated to channel-agnostic globs:
  `releases/releases.*.json` and `releases/assets.*.json`. The
  artifact-existence gate added in PR #41 now also asserts both
  manifests are present, so the next release fails loudly if
  Velopack's naming changes again. `docs/RELEASE-PROCESS.md`
  refreshed with the actual `vpk pack` output set per
  Velopack's [packaging docs](https://docs.velopack.io/packaging/overview).

### Added

- **Parser test coverage for SUB / OSC ST / DCS CAN / Unicode
  round-trip.** `tests/Tests.Unit/VtParserTests.fs` gains four new
  cases: SUB (0x1A) cancellation in CSI mirroring the existing CAN
  test; ST-terminated OSC asserting `bellTerminated=false` plus the
  trailing bare `EscDispatch` for the `\` byte; CAN inside DCS
  passthrough emitting `DcsHook` + `DcsPut`* + `DcsUnhook` (note the
  asymmetry with CSI — CAN there emits `Execute`, here it emits
  `DcsUnhook`); and an FsCheck property that any valid Unicode
  scalar encoded as UTF-8 round-trips through the parser as a
  single `Print` event with the same rune.
- **Velopack artifact-existence gate in `release.yml`.** A new
  PowerShell step after `vpk pack` asserts that `*Setup.exe` and
  `*-full.nupkg` exist under `releases/`. Defense-in-depth on top
  of `vpk pack`'s own exit code: a future Velopack version that
  renames an artifact would otherwise produce a green workflow
  whose release ships without the file the auto-update client
  expects (because softprops is configured with
  `fail_on_unmatched_files: false` so the delta nupkg pattern can
  legitimately match nothing on first releases).
- **Stage 4 substrate — `Screen.SequenceNumber` + `Screen.SnapshotRows`
  in `Terminal.Core`.** `Screen` now exposes a monotonic
  `SequenceNumber: int64` (incremented on every `Apply`) and a
  `SnapshotRows(startRow, count): int64 * Cell[][]` method that
  atomically captures an immutable copy of the requested rows
  paired with the sequence number at capture time. Both `Apply` and
  `SnapshotRows` serialize on a private gate object, which is the
  boundary between the WPF Dispatcher (where the parser feeds
  events) and the UIA RPC thread (where Stage 4's
  `ITextRangeProvider` will read snapshots from). This is the
  thread-safety primitive that spec §4.3's snapshot-on-construction
  rule depends on; landing it ahead of the UIA peer keeps the
  Stage 4 PR focused on the peer + provider implementation.
  `tests/Tests.Unit/ScreenTests.fs` covers fresh-screen baseline,
  per-event sequence increments, deep-copy independence, sequence-
  pairing, argument validation, the `count = 0` degenerate, and a
  concurrent producer / snapshot stress test.

### Changed

- **`docs/SESSION-HANDOFF.md` brought up to date.** Replaced the
  out-of-date "in-flight branch" / "last shipped release" rows: the
  `chore/session-handoff-and-final-audit` audit, the `preview.18`
  CHANGELOG, and the relaxed CHANGELOG-matching gate (PRs #35-#37)
  all merged on 2026-04-28; `v0.0.1-preview.18` is now the last
  shipped preview. Recorded the maintainer-reported Stage-3b
  finding that a separate `cmd.exe` console-host window appears
  behind the WPF window on launch, and tracked the conhost
  defect under "Pending action items" as orthogonal to Stage 4.
  Updated the Stage 4 sketch to reference the new
  `Screen.SnapshotRows` / `Screen.SequenceNumber` primitives so the
  snapshot rule is implementable without further substrate work.
- **Release-time `CHANGELOG.md` matching gate relaxed.** The pre-build
  step in `.github/workflows/release.yml` that failed the workflow
  when no `## [<version>]` section existed has been removed.
  `v0.0.1-preview.{16,17}` were burned by exactly that gate (publish a
  release without remembering to rename the section first → workflow
  fails → `release: published` won't refire for the same tag, so the
  next attempt has to bump). The `Generate release notes from
  CHANGELOG.md` step now resolves the body in this order: per-version
  `## [<version>]` section if present → `## [Unreleased]` content
  with the heading rewritten to `## [<version>] — <today>` for the
  release body → generic `"Release X. See CHANGELOG.md for details."`
  fallback (warned-on, not failed). Net effect: a maintainer can
  publish a release directly off `[Unreleased]` without burning a
  tag. `docs/RELEASE-PROCESS.md` "Cutting a release" updated to
  describe both flows.

### Fixed

- **Yield in concurrent snapshot stress test.** The producer/snapshot
  test added in #38 now calls `Thread.Yield()` once per snapshot
  iteration. .NET's `Monitor` already yields on contended
  Apply/SnapshotRows, but the explicit hint keeps the test thread
  from starving the producer if the lock briefly goes uncontested
  on a slow CI scheduler.

### Removed

- **`SmokeTests.fs` "string concat is associative" placeholder.**
  Was a vestigial FsCheck wire-up assertion from before
  `VtParserTests.fs` and `ScreenTests.fs` had real property tests.
  The file's other smoke ("Terminal.Core assembly loads") is
  preserved as a project-reference / type-loading sanity check.

## [0.0.1-preview.18] — 2026-04-28

First preview cut from the Stage-3b state of `main`. The window now
shows live `cmd.exe` output (parser → screen → WPF rendering); the
documentation set, spec, and working conventions all reflect the
shipped-stage reality. **Unsigned preview build** — Authenticode +
Ed25519 manifest signing return before `v0.1.0`; SmartScreen will
warn on first run. See [`SECURITY.md`](SECURITY.md).

### Changed

- **Documentation audit (post-Stage-3b).** Brought README,
  `docs/ARCHITECTURE.md`, `docs/BUILD.md`, `docs/RELEASE-PROCESS.md`,
  `docs/ROADMAP.md`, `docs/ACCESSIBILITY-TESTING.md`,
  `CONTRIBUTING.md`, `SECURITY.md`, `docs/CONPTY-NOTES.md`, and
  `docs/SESSION-HANDOFF.md` in line with the actual state of `main`
  at Stage 3b. Highlights:
  - README status now distinguishes "last shipped preview" (Stage 0)
    from "on `main`" (Stages 1–3b); license dependency list reflects
    current vs future direct dependencies.
  - ARCHITECTURE adds a "current pipeline" diagram alongside the
    target one, an implementation-status column on the modules
    table, and a today/target split on the threading model.
  - CONTRIBUTING captures the F# / WPF gotchas hit during Stages 1–3b
    (`out SafeFileHandle` byref interop, `let rec` for self-referential
    class-body bindings, `internal` vs `private` constructors, F# DU
    C# interop via `IsXxx` / `.Item`, `FrameworkElement` lacking
    `Background`, `MC1002` / `NETSDK1022` / `NETSDK1047`); Tests
    section now reflects xUnit + FsCheck.Xunit (the actual frameworks)
    and the real test-project paths.
  - SECURITY annotates each "What we defend against" bullet with its
    current implementation status (most are still planned); Job Object
    deferral now consistent with `docs/CONPTY-NOTES.md`.
  - RELEASE-PROCESS workflow-step list now reflects the 12 actual
    steps in `release.yml`, including the two fail-fast gates added
    after `v0.0.1-preview.14`.
  - ROADMAP gains a Status column on the Phase 1 stage table and a
    cross-link to `docs/CHECKPOINTS.md`.
  - ACCESSIBILITY-TESTING gains rows for the only two stages with
    actual user-visible behaviour today (Stage 0 and Stage 3b); test
    fixtures section flagged as planned tooling.
- **Working conventions extracted from SESSION-HANDOFF into
  CONTRIBUTING.** SESSION-HANDOFF was carrying policy that binds every
  contributor (PR shape, CHANGELOG discipline, "`spec/` is immutable",
  "platform quirks go in `docs/CONPTY-NOTES.md`"), not just an
  inter-thread handoff. Moved those into CONTRIBUTING's "Branching and
  pull requests" section and a new "Documentation policy" section;
  SESSION-HANDOFF now points at them and keeps only the
  session-specific content (sandbox caveats, pending action items,
  Stage 4 sketch, reading order). Reading order updated so a new
  session reads SESSION-HANDOFF first, then CONTRIBUTING, then the
  rest.
- **Spec rewrite (post-Stage-3b).** Per the user's authorisation,
  applied an in-place ADR-style update to `spec/overview.md` and
  `spec/tech-plan.md` so the design contract reads true to what
  actually shipped, rather than retaining superseded choices that
  could mislead future contributors. Highlights:
  - **Elmish.WPF removed throughout.** Investigated and dropped (no
    stable .NET 9 build on nuget at the time we needed it). Replaced
    with the actual two-project F# / C# split: F# `Terminal.App`
    owns `[<EntryPoint>][<STAThread>] main`, C# `Views` library
    hosts `MainWindow.xaml` + `App.cs : Application` +
    `TerminalView.cs`.
  - **Module layout in overview.md** rewritten to match the real
    `src/` tree (`Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility`, `Views`,
    `Terminal.App`); future modules clearly marked as reserved
    names.
  - **Test framework** corrected: Expecto + FsCheck → xUnit +
    FsCheck.Xunit; per-module test projects → single `Tests.Unit/`.
  - **Stage 1 P/Invoke surface** rewritten with `nativeint&` for
    out-handle parameters; the silent `out SafeFileHandle&` byref
    bug now documented inline as a comment in the spec.
  - **Stage 1 validation criteria** rewritten around the ≥16-byte
    ConPTY init prologue (the "see directory listing" assertion the
    spec previously called for is unreliable due to the
    render-cadence finding in `docs/CONPTY-NOTES.md`).
  - **Stage 2 vs Stage 3a deferral split** clarified — the parser
    emits dispatches for everything, but `Screen.Apply` handles only
    basic-16 SGR + cursor + erase today; 256-color, truecolor, and
    DECSET are deferred with their owner-stages noted.
  - **Stage 3** annotated as having shipped split into 3a (screen
    model) + 3b (WPF rendering), with validation criteria trimmed
    to what Stage 3 alone demonstrates (cmd.exe startup banner
    renders); 256-color, resize, and typing pushed to their owner
    stages. Reference Code Map cleaned (Elmish.WPF link dropped, the
    bare FsCheck entry merged into a combined xUnit + FsCheck.Xunit
    entry).

### Added

- [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md): handoff
  document for picking up between Claude Code sessions on this
  repo. Captures the things that aren't already in other docs:
  the working conventions observed in practice (small focused
  PRs, Conventional Commits, CHANGELOG-first releases), the
  sandbox / tools caveats (no `dotnet` locally, blocked Azure
  Blob URLs, tag-push 403, GitHub MCP disconnects), the
  pre-digested Stage 4 implementation sketch, and a recommended
  reading order for new sessions. Linked from `README.md`'s
  Quick links.
- New rows in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-2-vt-parser`, `baseline/stage-3a-screen-model`,
  and `baseline/stage-3b-wpf-rendering`, each with a corresponding
  entry in the "Pending checkpoint tags" section so the maintainer
  can sweep all four pending tags in one batch from a workstation.

- **CI hygiene gates** preventing two specific bug classes that bit
  us during Stage 0 ↔ Stage 3 iteration:
  - **`actionlint` job** in `ci.yml` lints `.github/workflows/*.yml`
    on every PR. Catches the YAML / shell / expression mistakes
    that produced the silent workflow startup_failures during the
    `v0.0.1-preview.{1..5}` diagnostic loop (PowerShell heredoc body
    lines at column 0 inside an indented `run: |` block, etc.).
  - **Release-time gates in `release.yml`** running before any
    build:
    - **Target-branch gate**: fails the workflow with a clear error
      if the release was published with `target_commitish` other
      than `main`. Prevents `v0.0.1-preview.14`'s failure mode where
      the release picked up an old branch's `release.yml`.
    - **CHANGELOG gate**: fails the workflow if `CHANGELOG.md` has
      no `## [<version>]` section matching the release tag. The
      release-notes step further down silently falls back to
      `"Release X. See CHANGELOG.md for details."` otherwise; we
      almost shipped that fallback on `.2..5`.
- **Stage 3b — WPF rendering + end-to-end wiring.** First visible
  terminal surface. New `TerminalView : FrameworkElement` in
  `src/Views/TerminalView.cs` overrides `OnRender(DrawingContext)`
  per spec §3.3: contiguous cells with identical SGR attrs coalesce
  into a single `FormattedText` run; backgrounds drawn first, text
  on top; manual underline at baseline; bold/italic via
  `FormattedText.SetFontWeight` / `SetFontStyle`. Default monospaced
  font is "Cascadia Mono" with Consolas / Courier New fallbacks at
  14pt; cell metrics computed once at construction. ANSI 16-colour
  palette mapped to WPF brushes; truecolor brushes constructed
  per-call.
- `MainWindow.xaml` now hosts a `<views:TerminalView />` with
  `x:FieldModifier="public"` so F# composition code in
  `Terminal.App` can reach it across the assembly boundary.
- `Program.fs` (Terminal.App) now wires the full Stage 3 pipeline:
  on `Window.Loaded` it spawns a 120×30 `ConPtyHost` running
  `cmd.exe`, then a background `Task` reads stdout chunks, feeds
  them through the Stage 2 `Parser`, and dispatches the resulting
  `VtEvent`s back to the UI thread via `Dispatcher.InvokeAsync`
  to apply them to a single `Screen` and invalidate the
  `TerminalView`. `Application.Exit` cancels the reader and
  disposes the `ConPtyHost`.
- `src/Views/Views.csproj` gains a `ProjectReference` to
  `Terminal.Core` so the C# control can use `Screen` / `Cell` /
  `SgrAttrs` / `ColorSpec` directly.

- **Stage 3a — screen model.** `Terminal.Core` gains the data types
  per spec §3.1 (`ColorSpec` DU, `SgrAttrs` struct, `Cell` struct,
  `Cursor` mutable record) and a `Screen` class consuming `VtEvent`s
  via `Apply`. Stage 3a coverage:
  - **Print**: writes a cell at the cursor with the current SGR
    attributes, advances Col, auto-wraps to the next row at
    end-of-line, scrolls when wrapping past the bottom row.
  - **C0 controls**: BS (cursor left, clamped), HT (next 8-column
    boundary), LF (cursor down + scroll), CR (cursor to col 0).
  - **CSI cursor movement**: A/B/C/D (relative, clamped at edges),
    H/f (CUP/HVP, 1-indexed → 0-indexed).
  - **CSI erase**: J (display, modes 0/1/2), K (line, modes 0/1/2).
  - **CSI SGR**: reset (0), bold (1/22), italic (3/23), underline
    (4/24), inverse (7/27); foreground colours 30-37 + bright
    90-97 + default 39; background colours 40-47 + bright 100-107
    + default 49. Empty-param CSI m equivalent to CSI 0m. 256-colour
    and truecolor sub-parameter forms are deferred (the parser would
    need to split on `:` vs `;` first).
  - **DECSET / DECSC / OSC / DCS**: silently ignored at this stage;
    Stage 4+ adds them as their owners need them.
- Stage 3a tests in `tests/Tests.Unit/ScreenTests.fs` covering each
  of the supported behaviours plus boundary clamping (cursor at
  edges, oversize CSI A movements) and the auto-wrap → scroll
  invariant.
- **Stage 2 — VT500 parser.** `Terminal.Parser` now contains a
  pure-F# implementation of Paul Williams' DEC ANSI parser
  ([vt100.net/emu/dec_ansi_parser.html](https://vt100.net/emu/dec_ansi_parser.html)),
  matching alacritty/vte's table-driven structure. `StateMachine`
  is a stateful single-byte feeder over fourteen `VtState` cases
  (Ground, Escape, EscapeIntermediate, CsiEntry/Param/Intermediate/Ignore,
  DcsEntry/Param/Intermediate/Passthrough/Ignore, OscString,
  SosPmApcString) with the canonical alacritty caps applied
  (`MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`,
  `MAX_OSC_RAW = 1024`, `MAX_PARAMS = 16`). `Parser` exposes
  `create`/`feed`/`feedBytes`/`feedArray` for downstream consumers.
  A small UTF-8 decoder buffers continuation bytes and emits a
  single `Print of Rune` per scalar; malformed UTF-8 emits
  U+FFFD. See [`spec/tech-plan.md`](spec/tech-plan.md) Stage 2.
- The placeholder `VtEvent` discriminated union in
  `Terminal.Core/Types.fs` is replaced with the real DU per spec
  §2.2 (`Print | Execute | CsiDispatch | EscDispatch | OscDispatch
  | DcsHook | DcsPut | DcsUnhook`). Other DUs in `Types.fs` remain
  placeholders pending their owning stages.
- Stage 2 tests in `tests/Tests.Unit/VtParserTests.fs`:
  - **Fixture tests** for every byte-string example called out in
    spec §2.4 (`"Hello\r\n"`, `"\x1b[31mRed\x1b[0m"`, `"\x1b[2J"`,
    `"\x1b]0;Title\x07"`, `"\x1b[?1049h"`) plus multi-param
    SGR, default-parameter CSI, ESC dispatch (DECKPAM), CAN
    cancellation, and UTF-8 multi-byte assembly.
  - **FsCheck property tests** verifying the spec's robustness
    contract: parser never throws on arbitrary bytes; chunked feed
    equals whole-array feed; CAN (0x18) at any point returns the
    parser to `Ground`.
- [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md): rollback guide
  documenting stable development checkpoints. Defines the three
  durable references for each checkpoint (git tag in `baseline/`
  namespace, PR label `stable-baseline`, optional GitHub Release),
  the rollback procedures (read-only inspection, branch-from-baseline,
  destructive `main` reset), and the procedure for marking new
  checkpoints. Linked from `README.md`'s Quick links.
- **Stage 1 — ConPTY host.** `Terminal.Pty` library now contains the
  `Terminal.Pty.Native` P/Invoke surface (`COORD`, `STARTUPINFOEX`,
  `PROCESS_INFORMATION`, etc., and the kernel32 externs for
  `CreatePseudoConsole` / `CreatePipe` / `InitializeProcThreadAttributeList`
  / `UpdateProcThreadAttribute` / `CreateProcess`), a typed
  `PseudoConsole.create` lifecycle wrapper enforcing the strict 9-step
  Microsoft order (close ConPTY-owned handles in parent; correct
  `STARTUPINFOEX.cb`; no `CREATE_NEW_PROCESS_GROUP`), and a
  `ConPtyHost` high-level API exposing a stdin `FileStream` plus a
  `ChannelReader<byte array>` over stdout backed by a dedicated reader
  task. `SafePseudoConsoleHandle` (a `SafeHandleZeroOrMinusOneIsInvalid`
  subclass) ensures `ClosePseudoConsole` runs on disposal. See
  [`spec/tech-plan.md`](spec/tech-plan.md) Stage 1.
- Stage 1 acceptance test in `tests/Tests.Unit/ConPtyHostTests.fs`
  spawns `cmd.exe` under ConPTY and asserts the reader pipeline
  delivered at least the 16-byte ConPTY init prologue
  (`\x1b[?9001h\x1b[?1004h`). Validates the
  `CreatePipe → CreatePseudoConsole → CreateProcess → reader thread
  → channel → collectStdout` chain end-to-end. Stronger assertions
  on cmd's actual command output land in Stage 6 once a proper
  input pipeline lets us drive cmd deterministically. Windows-only;
  trivially passes on non-Windows so the suite runs unchanged on
  dev workstations.
- [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md): platform-quirks
  document for ConPTY behaviour observed in practice. First entry
  documents the **render-cadence** finding (fast-exit
  `cmd.exe /c <command>` loses its rendered output because conhost
  flushes on a timer-driven cadence and tears down before the next
  tick) plus forward-look items from `spec/overview.md` that haven't
  been hit in code yet. Linked from `README.md` and
  `docs/ARCHITECTURE.md`.
- New row in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-1-conpty-hello-world` covering the `Terminal.Pty`
  library shape and its acceptance test.

### Removed

- `.github/workflows/diagnose.yml`. Was added during the Stage 0
  release-pipeline diagnostic loop to isolate `release.yml` from
  workflow-level config issues. Its lessons live in the "Common
  pitfalls" section of [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md);
  the workflow itself is no longer needed.
- Unused `write` helper in `tests/Tests.Unit/ConPtyHostTests.fs`.
  Leftover from an earlier iteration that drove cmd via stdin; the
  working Stage 1 test asserts only on ConPTY-pipeline output
  capture, so the helper was dead code. The pattern can be re-added
  when Stage 6 lands the keyboard-to-PTY input pipeline.

## [0.0.1-preview.15] — 2026-04-27

First Stage 0 preview to ship installable artifacts. **Unsigned
preview build** — Authenticode + Ed25519 manifest signing are
deferred until before `v0.1.0`; SmartScreen will warn on first run.
See [`SECURITY.md`](SECURITY.md).

This version's binary footprint is intentionally trivial: an empty
WPF window titled "pty-speak" with `AutomationProperties.Name` set so
NVDA announces it. It exists so the deployment pipe is end-to-end
green before any terminal logic lands; future stages add the actual
ConPTY / parser / UIA work on top.

### Added

- Stage 0 shipping skeleton: F# / C# / WPF solution structure under
  [`src/`](src/) and [`tests/`](tests/) with a buildable empty-window
  app, central package management, and `TreatWarningsAsErrors=true`
  from day one.
  - F# class libraries `Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility` (placeholders for
    Stages 1–9).
  - C# WPF library `Views` hosting `MainWindow.xaml` with
    `AutomationProperties` set on the outer window. App is a plain C#
    `Application` subclass (no `App.xaml`); a Stage 0 window has no
    application-level resources.
  - F# EXE `Terminal.App` owning the `[<EntryPoint>][<STAThread>]`
    `main` that invokes `VelopackApp.Build().Run()` before any WPF
    type loads (Velopack issue #195).
  - `Tests.Unit` (xUnit + FsCheck.Xunit smoke tests) and `Tests.Ui`
    (placeholder; FlaUI work begins in Stage 4).
- CI now restores, builds, tests, publishes the app, and runs a
  Velopack `vpk pack` smoke on every PR; the resulting installer is
  uploaded as a `velopack-smoke-<run>` artifact (7-day retention).
- Release workflow keyed on `release: published` events. Maintainer
  publishes a release via the GitHub Releases UI (Target = `main`,
  prerelease checkbox set); workflow then builds, packs with
  Velopack, generates release notes from the matching CHANGELOG
  section, and updates the just-created release with the body and
  installer artifacts via `softprops/action-gh-release@v3`.

### Changed

- Release workflow simplified: SignPath Authenticode submission,
  Ed25519 release-manifest signing, and Authenticode verification
  steps are removed for the unsigned preview line. They will be
  reintroduced before `v0.1.0`; the "Re-enabling signing (deferred)"
  appendix in [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)
  keeps the procedure on file.
- CI no longer guards Restore/Build/Test on `hashFiles(...) != ''` —
  a typo in a project file now fails CI loudly instead of silently
  no-op'ing.

### Notes

- `v0.0.1-preview.{1..14}` were tagged in succession but never shipped
  installable artifacts; each was a diagnostic step in unwinding a
  silent workflow startup_failure on this repo. Root cause was a
  PowerShell `@"..."@` heredoc whose body lines were at column 0 in
  the YAML source while the surrounding `run: |` block was indented
  ten spaces — YAML literal blocks require all content lines to be
  indented at least as much as the block's first line, and the
  column-0 lines silently terminated the block, producing a malformed
  workflow file that GitHub Actions rejected at load time with no
  visible error. Fix: replace the heredoc with a properly-indented
  PowerShell array joined by newline. Documented in
  [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) so it isn't
  re-discovered the hard way.

### Project documentation (carried over from the initial scaffold)

- Specifications [`spec/overview.md`](spec/overview.md) and
  [`spec/tech-plan.md`](spec/tech-plan.md).
- Documentation scaffolding: README, [`CONTRIBUTING.md`](CONTRIBUTING.md),
  [`SECURITY.md`](SECURITY.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md),
  and supporting docs in [`docs/`](docs/).
- Issue templates for bug reports, feature requests, and accessibility
  regressions; pull request template and Dependabot configuration.
