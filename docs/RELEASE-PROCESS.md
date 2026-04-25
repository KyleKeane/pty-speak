# Release process

This document describes how to cut a public release of `pty-speak` and
ship it to GitHub Releases. The pipeline uses Velopack for packaging
and delta-update generation, GitHub Releases as the distribution
endpoint, and SignPath Foundation OSS for Authenticode signing.

The steps below are also encoded in
[`.github/workflows/release.yml`](../.github/workflows/release.yml);
this document is the human-readable counterpart so a maintainer (or
Claude Code) can rerun the process by hand if CI is unavailable.

## Release principles

1. **One tag, one release.** Tags follow `vMAJOR.MINOR.PATCH`. Every
   tag triggers exactly one release run; reuploads happen via a new
   patch tag, never by editing an existing release.
2. **Stage gates before tags.** Don't tag a release until the relevant
   stage's validation matrix from
   [`spec/tech-plan.md`](../spec/tech-plan.md) passes against NVDA on
   a clean Windows VM.
3. **Authenticode + Ed25519, both required.** A release that is missing
   either signature must not be promoted from prerelease to stable.
4. **Changelogs are part of the release.** The `## [X.Y.Z]` section of
   [`CHANGELOG.md`](../CHANGELOG.md) becomes the GitHub release body.

## Versioning

| Tag pattern               | Meaning                                         | Released to            |
|---------------------------|-------------------------------------------------|------------------------|
| `vX.Y.Z-preview.N`        | Internal CI build for a stage in progress       | GitHub prerelease      |
| `vX.Y.Z-rc.N`             | Release candidate; NVDA validation in progress  | GitHub prerelease      |
| `vX.Y.Z`                  | Validated stage release                         | GitHub release (latest)|

Velopack uses these directly as `--packVersion`. The Ed25519 manifest
signature covers the version string, so a re-tag is impossible without
re-signing.

## One-time setup

Before the first release ever ships, a maintainer must do the
following — this is bookkeeping, not code, but it must be done in
order:

1. **Apply to SignPath Foundation OSS** at
   [signpath.org/apply](https://signpath.org/apply/). Provide the GitHub
   repository URL (`https://github.com/KyleKeane/pty-speak`) and the MIT
   license. SignPath assigns an organization and project ID.
2. **Generate the Ed25519 release-signing keypair** offline:
   ```powershell
   dotnet tool install -g minisign  # or any Ed25519 CLI
   minisign -G -p docs/release-pubkey.txt -s ~/.config/pty-speak/release.key
   ```
   Commit `docs/release-pubkey.txt` (public key only). Store the
   private key offline; never check it in.
3. **Create the GitHub repository secrets** under
   *Settings → Secrets and variables → Actions*:
   - `SIGNPATH_API_TOKEN` — from SignPath after onboarding.
   - `SIGNPATH_ORGANIZATION_ID` — from SignPath.
   - `SIGNPATH_PROJECT_SLUG` — `pty-speak`.
   - `SIGNPATH_SIGNING_POLICY_SLUG` — `release-signing` (we configure this in SignPath).
   - `MINISIGN_SECRET_KEY` — base64-encoded private key for the
     manifest signature. **Use a separate organization secret with
     manual-approval environment protection.**
4. **Create the `release` GitHub Environment** with required reviewers
   set to the maintainer team. The release workflow targets this
   environment, so SignPath signing and Ed25519 manifest signing both
   require human approval.

## Cutting a release

### 1. Verify the stage gate

Run the relevant validation matrix from
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) on a clean
Windows 10 or 11 VM with NVDA installed. Record the result in the PR
that updates `CHANGELOG.md`.

### 2. Update the changelog and version

Move the `## [Unreleased]` items into a new `## [X.Y.Z]` section in
[`CHANGELOG.md`](../CHANGELOG.md) with today's date. Bump the
`<Version>` property in `Directory.Build.props` (or the equivalent in
each `.fsproj`) to match. Open a PR titled `release: vX.Y.Z`. Merge
when CI is green.

### 3. Tag and push

```powershell
git checkout main
git pull
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin vX.Y.Z
```

Pushing the tag triggers
[`.github/workflows/release.yml`](../.github/workflows/release.yml).

### 4. Wait for the release workflow

The workflow runs the following on `windows-latest`, in order:

1. `dotnet test -c Release` — same suite as CI; release fails fast if
   anything regressed since the merge.
2. `dotnet publish` — produces a self-contained `win-x64` build of
   `Terminal.App` into `publish/`.
3. **SignPath signing of the unpacked binaries.** The job uploads
   `publish/` to SignPath via the official GitHub Action and waits for
   a maintainer to approve in the SignPath dashboard. (For OSS-tier
   accounts each release is manually approved.)
4. `vpk pack` — wraps the now-signed binaries into a Velopack release
   (`Setup.exe`, full nupkg, delta nupkg, `releases.json`).
5. **Ed25519 manifest signature.** A small script signs `releases.json`
   with the offline key (loaded from `MINISIGN_SECRET_KEY`) and writes
   `releases.json.minisig`.
6. `vpk upload github` — uploads all artifacts to a draft GitHub
   Release pinned to the tag.
7. The workflow promotes the draft to a published release with
   `prerelease=false` for `vX.Y.Z` tags and `prerelease=true` for
   `-preview.N` / `-rc.N` tags.

### 5. Smoke-test the release

On a clean VM:

1. Download `pty-speak-Setup.exe` from the new release.
2. Confirm the Authenticode signature:
   ```powershell
   Get-AuthenticodeSignature .\pty-speak-Setup.exe |
     Select-Object Status, SignerCertificate
   ```
   `Status` must be `Valid`; the signer must be the SignPath OSS cert.
3. Run the installer; confirm NVDA announces the install progress.
4. Launch the app; confirm version in the title bar matches `X.Y.Z`.
5. Trigger the auto-update path from a previously-installed older
   version (`Ctrl+Shift+U`); confirm the delta downloads, the manifest
   signature verifies, and the relaunch happens within ~2 seconds.

### 6. Announce

Update the pinned issue or discussions thread with the release notes
and the highlights. If the release closes any GitHub Security
Advisory, flip the advisory's status to *Published* and link the fix
commit.

## What to do if a release goes wrong

- **CI failed mid-release.** Delete the draft GitHub release and the
  tag (`git push --delete origin vX.Y.Z` — coordinate with maintainers
  first). Fix the underlying issue. Re-tag.
- **Authenticode passed but Ed25519 verification fails on user
  machines.** Treat as a security advisory. Yank the release (mark it
  as a prerelease and add a banner in the release notes). Investigate
  whether the manifest signing key is intact; if compromised, follow
  the key-rotation playbook below.
- **The signed artifact misbehaves on first install.** Push a
  `vX.Y.Z+1` patch release with the fix; do not edit the existing
  release.

## Key rotation

If the Ed25519 release-signing key is suspected compromised:

1. Issue an out-of-band advisory (GitHub Security Advisory + pinned
   discussion).
2. Generate a new keypair offline.
3. Update `docs/release-pubkey.txt` and the `MINISIGN_SECRET_KEY`
   secret in a single PR.
4. Cut a `vX.Y.(Z+1)` release using the new key. The application
   will accept either old or new keys for one transitional release,
   then drop the old key in `vX.(Y+1).0`.

## Reproducibility

CI release builds are deterministic with respect to:

- The committed source at the tag.
- The `dotnet --version` recorded in the workflow log.
- The `vpk --version` recorded in the workflow log.

Anyone can rebuild the unsigned artifacts locally per
[`docs/BUILD.md`](BUILD.md) and confirm hash equality with the signed
release's underlying nupkg.
