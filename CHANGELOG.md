# Changelog

All notable changes to `pty-speak` will be documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project follows [Semantic Versioning](https://semver.org/).

Until the first 0.1.0 tagged release, all changes land under
`[Unreleased]`. Release tags follow the pattern `vMAJOR.MINOR.PATCH`
(e.g. `v0.1.0`); pushing a tag triggers the Velopack release workflow
described in [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md).

## [Unreleased]

### Added

- Project specifications: [`spec/overview.md`](spec/overview.md)
  (architectural rationale and prior art) and
  [`spec/tech-plan.md`](spec/tech-plan.md) (Stage 0–11 walking-skeleton
  plan).
- Documentation scaffolding: README,
  [`CONTRIBUTING.md`](CONTRIBUTING.md), [`SECURITY.md`](SECURITY.md),
  [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md), and supporting docs in
  [`docs/`](docs/).
- GitHub Actions CI and release workflows targeting Velopack +
  GitHub Releases with optional SignPath signing.
- Issue templates for bug reports, feature requests, and accessibility
  regressions; pull request template and Dependabot configuration.

### Notes

- No binaries have been shipped. The first release will be `v0.1.0`
  after Stage 11 (Velopack auto-update) completes per the tech plan.
