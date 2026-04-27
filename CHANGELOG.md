# Changelog

All notable changes to `pty-speak` will be documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project follows [Semantic Versioning](https://semver.org/).

Release tags follow the pattern `vMAJOR.MINOR.PATCH` (e.g. `v0.1.0`),
or `vMAJOR.MINOR.PATCH-preview.N` / `-rc.N` for prereleases. Pushing a
matching tag triggers the Velopack release workflow described in
[`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md).

## [Unreleased]

_(empty — Stage 1 work lands here)_

## [0.0.1-preview.11] — 2026-04-27

> **Unsigned preview build.** Authenticode + Ed25519 signing are
> deferred until before `v0.1.0`; SmartScreen will warn on first run.
> See [`SECURITY.md`](SECURITY.md).

> Note: `v0.0.1-preview.{1..5}` were tagged but never shipped
> artifacts — every release-workflow run on this repo silently
> failed to spawn any job at all (workflow-level startup rejection
> with no diagnostic info), despite incrementally fixing every
> visible cause. A minimal echo-only diagnose workflow runs fine on
> `workflow_dispatch`, so the issue is specific to release.yml's
> content. release.yml has been temporarily reduced to a minimal
> shape to verify it can start at all. The full build/test/publish/
> Velopack-pack/upload steps will be re-added incrementally once
> we see a visibly-running job. `.6` is the test publish for that
> minimal shape; subsequent .7+ will incrementally restore work
> until we identify the offending construct.

### Added

- Stage 0 shipping skeleton: F# / C# / WPF solution structure under
  [`src/`](src/) and [`tests/`](tests/) with a buildable empty-window
  app, central package management, and `TreatWarningsAsErrors=true`
  from day one.
  - F# class libraries `Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility` (placeholders for
    Stages 1–9).
  - C# WPF library `Views` hosting `App.xaml` and `MainWindow.xaml`
    with `AutomationProperties` set on the outer window.
  - F# EXE `Terminal.App` owning the `[<EntryPoint>][<STAThread>]`
    `main` that invokes `VelopackApp.Build().Run()` before any WPF
    type loads (Velopack issue #195).
  - `Tests.Unit` (xUnit + FsCheck.Xunit smoke tests) and `Tests.Ui`
    (placeholder; FlaUI work begins in Stage 4).
- CI now restores, builds, tests, publishes the app, and runs a
  Velopack `vpk pack` smoke on every PR; the resulting installer is
  uploaded as a `velopack-smoke-<run>` artifact.

### Changed

- Release workflow simplified: SignPath Authenticode submission,
  Ed25519 release-manifest signing, and Authenticode verification
  steps are removed for the unsigned preview line. They will be
  reintroduced before `v0.1.0`; the “Re-enabling signing (deferred)”
  appendix in `docs/RELEASE-PROCESS.md` keeps the procedure on file.
- CI no longer guards Restore/Build/Test on `hashFiles(...) != ''` —
  a typo in a project file now fails CI loudly instead of silently
  no-op'ing.

### Project documentation (already present from the initial scaffold)

- Specifications [`spec/overview.md`](spec/overview.md) and
  [`spec/tech-plan.md`](spec/tech-plan.md).
- Documentation scaffolding: README, [`CONTRIBUTING.md`](CONTRIBUTING.md),
  [`SECURITY.md`](SECURITY.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md),
  and supporting docs in [`docs/`](docs/).
- Issue templates for bug reports, feature requests, and accessibility
  regressions; pull request template and Dependabot configuration.
