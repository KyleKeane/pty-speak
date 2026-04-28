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

### Fixed

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
