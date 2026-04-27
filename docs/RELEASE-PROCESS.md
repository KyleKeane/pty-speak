# Release process

This document describes how to cut a public release of `pty-speak` and
ship it to GitHub Releases. The pipeline uses Velopack for packaging
and delta-update generation and GitHub Releases as the distribution
endpoint.

> **Signing status.** The current `0.0.x-preview.N` line ships
> **unsigned** while the project gets the deployment pipe working
> end-to-end. Authenticode (via SignPath Foundation OSS) and Ed25519
> release-manifest pinning return before `v0.1.0`. The detailed
> procedure for re-enabling signing is preserved at the bottom of this
> document — see [Re-enabling signing (deferred)](#re-enabling-signing-deferred).

The steps below are also encoded in
[`.github/workflows/release.yml`](../.github/workflows/release.yml);
this document is the human-readable counterpart so a maintainer (or
Claude Code) can rerun the process by hand if CI is unavailable.

## Release principles

1. **One release, one tag.** A maintainer **publishes a release** in
   the GitHub Releases UI; that creates the tag (`vMAJOR.MINOR.PATCH`
   or `-preview.N` / `-rc.N`) and fires `release: published` which
   runs the workflow. Reuploads happen by publishing a new release at
   a new tag, never by editing an existing release.
2. **Stage gates before tags.** Don't tag a stable release until the
   relevant stage's validation matrix from
   [`spec/tech-plan.md`](../spec/tech-plan.md) passes against NVDA on
   a clean Windows VM. Preview tags are exempt (their job is to
   validate the pipe).
3. **Once signing is re-enabled, both layers are required.** A
   `vX.Y.Z` (non-preview) release that is missing either Authenticode
   or Ed25519 must not be promoted to stable.
4. **Changelogs are part of the release.** The `## [X.Y.Z]` section of
   [`CHANGELOG.md`](../CHANGELOG.md) becomes the GitHub release body.

## Versioning

| Tag pattern               | Meaning                                         | Released to            |
|---------------------------|-------------------------------------------------|------------------------|
| `vX.Y.Z-preview.N`        | Internal CI build for a stage in progress       | GitHub prerelease      |
| `vX.Y.Z-rc.N`             | Release candidate; NVDA validation in progress  | GitHub prerelease      |
| `vX.Y.Z`                  | Validated stage release                         | GitHub release (latest)|

Velopack uses these directly as `--packVersion`. Once Ed25519 manifest
pinning is re-enabled, the manifest signature will cover the version
string, making a re-tag impossible without re-signing.

## One-time setup (current, unsigned line)

None. The unsigned-preview pipeline targets no GitHub Environment,
reads no secrets, and uses only stable public actions
(`actions/checkout@v4`, `actions/setup-dotnet@v5`,
`softprops/action-gh-release@v3`). The repo's default permissions
("Read and write" under *Settings → Actions → General*) are
sufficient.

The earlier draft of this document required a `release` Environment
to gate SignPath approval; that's preserved under
[Re-enabling signing (deferred)](#re-enabling-signing-deferred) for
when signing returns.

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

### 3. Publish the release

In the GitHub UI:

1. Go to *Releases → Draft a new release*
   (`https://github.com/KyleKeane/pty-speak/releases/new`).
2. **Choose a tag** → type `vX.Y.Z` (or `-preview.N` / `-rc.N`) →
   click "Create new tag: `vX.Y.Z` on publish".
3. **Target**: `main`. **Verify this — the dropdown defaults to main
   but at least one preview accidentally targeted a stale branch and
   ran the wrong workflow file.**
4. Leave title and description blank — the workflow overwrites both
   from the matching `CHANGELOG.md` section + unsigned-preview banner.
5. Check **"Set as a pre-release"** for `-preview.N` / `-rc.N` tags.
6. Click **Publish release**.

The `release: published` event fires
[`.github/workflows/release.yml`](../.github/workflows/release.yml).

### 4. Wait for the release workflow

Triggered by the `release: published` event you just fired. Runs on
`windows-latest`, in order:

1. **Confirm workflow started** — echo step that prints the release's
   tag, ID, and event name. Belt-and-suspenders so the run shows
   meaningful output even if a later step crashes.
2. **Checkout** with default `ref` — release events set `GITHUB_REF`
   to `refs/tags/<tag_name>` so the source at the tag is checked out.
3. **Setup .NET 9** via `actions/setup-dotnet@v5`.
4. **Resolve version** — strips the leading `v` from
   `github.event.release.tag_name`, detects prerelease from the
   `-preview.N` / `-rc.N` suffix.
5. `dotnet restore` / `dotnet build -c Release` / `dotnet test`.
6. `dotnet publish src/Terminal.App/Terminal.App.fsproj -c Release -r win-x64 --self-contained -o publish` (with `/p:Version=...`).
7. **Install Velopack CLI** (`dotnet tool install -g vpk`).
8. **`vpk pack`** — wraps `publish/` into a Velopack release at
   `releases/`: `*Setup.exe`, full nupkg, `RELEASES`. (No
   `*-delta.nupkg` on the first release; `releases.json` is not
   produced by our `vpk pack` config.)
9. **Generate release notes from `CHANGELOG.md`** — writes
   `release-body.md` with the unsigned-preview banner prepended to the
   matching `## [<version>]` section.
10. **Update GitHub Release** via `softprops/action-gh-release@v3`:
    sets title to `pty-speak <version>`, replaces the auto-generated
    body with `release-body.md`, sets `prerelease` from step 4, and
    attaches the artifact files. `fail_on_unmatched_files: false` so
    the optional `*-delta.nupkg` and `releases.json` patterns don't
    fail the upload when they're absent.

### 5. Smoke-test the release

On a clean VM:

1. Download `pty-speak-Setup.exe` from the new release.
2. Confirm the build is **unsigned** (this is currently expected):
   ```powershell
   Get-AuthenticodeSignature .\pty-speak-Setup.exe |
     Select-Object Status, StatusMessage
   ```
   `Status` is `NotSigned`; `StatusMessage` confirms no signature.
   SmartScreen will prompt; choose "More info → Run anyway" or use
   `Unblock-File` first.
3. Run the installer; for the Stage 0 preview, confirm an empty
   window titled "pty-speak" opens with no errors.
4. Launch the app from the Start menu; confirm the same window opens
   from the installed location (`%LocalAppData%\pty-speak\current\`).
5. Once Stage 11 lands, trigger the auto-update path from a previously
   installed older version (`Ctrl+Shift+U`); confirm the delta
   downloads and the relaunch happens within ~2 seconds.

### 6. Announce

Update the pinned issue or discussions thread with the release notes
and the highlights. If the release closes any GitHub Security
Advisory, flip the advisory's status to *Published* and link the fix
commit.

## What to do if a release goes wrong

- **Workflow failed mid-release.** Fix the underlying issue on a PR
  to `main`. Then publish a new release at the next preview/patch
  version (e.g. `vX.Y.Z-preview.<N+1>`). Don't try to retry the same
  tag — `release: published` doesn't refire for a tag GitHub has
  already seen, even if the underlying release is deleted and
  republished. Bump and try again.
- **The artifact misbehaves on first install.** Publish a `vX.Y.Z+1`
  patch release with the fix; do not edit the existing release.
- **(Once signing is re-enabled) Authenticode passed but Ed25519
  verification fails on user machines.** Treat as a security
  advisory. Yank the release (mark it as a prerelease and add a
  banner in the release notes). Investigate whether the manifest
  signing key is intact; if compromised, follow the key-rotation
  playbook below.

## Reproducibility

CI release builds are deterministic with respect to:

- The committed source at the tag.
- The `dotnet --version` recorded in the workflow log.
- The `vpk --version` recorded in the workflow log.

`Directory.Build.props` sets `Deterministic=true` and (under
`GITHUB_ACTIONS`) `ContinuousIntegrationBuild=true`. Anyone can
rebuild the artifacts locally per [`docs/BUILD.md`](BUILD.md) and
confirm hash equality with the released nupkg.

## Common pitfalls

Lessons learned the hard way bringing the pipeline up; documented so
they aren't re-discovered.

- **PowerShell `@"..."@` heredocs inside a YAML `run: |` block.** The
  heredoc body lines must be indented at least as much as the run
  block's first content line. Column-0 lines silently terminate the
  YAML literal block, producing a malformed workflow file that GitHub
  Actions rejects at load time **with no banner, no error, no jobs
  spawned, 0-second "failed" runs**. If you need a multi-line string,
  build it from a properly-indented PowerShell array joined by
  `` `n `` instead — see the `Generate release notes from CHANGELOG.md`
  step in
  [`.github/workflows/release.yml`](../.github/workflows/release.yml)
  for the canonical pattern.
- **Releases UI Target dropdown.** It defaults to the default branch,
  but at least one preview accidentally targeted a stale branch
  (`claude/create-project-docs-xVA6n`) which still had the old broken
  workflow file. Always **confirm Target = `main`** before clicking
  Publish.
- **`fail_on_unmatched_files: true` on a glob that's only sometimes
  populated.** `vpk pack` doesn't produce `*-delta.nupkg` until there's
  a previous release to diff against, so a strict files list fails on
  the first release. We use `fail_on_unmatched_files: false`; required
  artifacts are gated upstream by `vpk pack` itself failing if any
  expected output is missing.
- **Don't pass `--no-restore` to `dotnet publish` after a
  platform-default `dotnet restore`.** A self-contained `--runtime
  win-x64` publish needs RID-specific assets that the earlier restore
  didn't produce; NETSDK1047. Either restore once with the RID, or
  drop `--no-restore` from the publish step (we drop it).
- **Velopack `--packVersion` minimum.** `vpk pack` rejects versions
  below `0.0.1`. Our CI smoke uses `0.0.1-ci.<run_number>`; SemVer
  prerelease ordering keeps it strictly less than any real preview.

---

## Re-enabling signing (deferred)

The procedure below was the design before signing was deferred. It is
preserved verbatim so that re-enabling signing is a matter of restoring
a few workflow steps and executing the one-time setup, not redoing the
research.

### One-time signing setup

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
4. The `release` GitHub Environment with required reviewers (already
   set up for the unsigned line) gates SignPath signing and Ed25519
   manifest signing on human approval.

### Workflow changes when signing returns

Add these steps back to `.github/workflows/release.yml` (they were
removed in the Stage 0 unsigned-preview cutover and the file's git
history shows the exact previous content):

1. **Upload `publish/` as a workflow artifact** (so SignPath can pull
   it). Use `actions/upload-artifact@v4` and capture
   `outputs.artifact-id`.
2. **`signpath/github-action-submit-signing-request@v1`** between
   `dotnet publish` and `vpk pack`. It must consume the artifact ID
   from step (1) and write to `publish-signed/`. Switch the `vpk pack`
   step's `--packDir` to `publish-signed`.
3. **Ed25519 manifest signature.** After `vpk pack`, install
   `Minisign.Tool`, write `MINISIGN_SECRET_KEY` to a temp file using
   `Set-Content -AsByteStream` (not `Out-File -Encoding ASCII`,
   which corrupts base64), then sign `releases/releases.json` to
   produce `releases/releases.json.minisig`.
4. **Authenticode verification gate.** Run
   `Get-AuthenticodeSignature releases/*Setup.exe` and fail the job
   if `Status -ne 'Valid'`. This catches a SignPath misconfiguration
   before users see it.
5. Add `releases/releases.json.minisig` to the
   `softprops/action-gh-release` files list.
6. Drop the unsigned-preview banner from the `Generate release notes`
   step.
7. Update `SECURITY.md` to remove the "Releases tagged before v0.1.0
   are unsigned previews" warning, and update the "Install" section
   of `README.md` similarly.

### Key rotation

If the Ed25519 release-signing key is suspected compromised:

1. Issue an out-of-band advisory (GitHub Security Advisory + pinned
   discussion).
2. Generate a new keypair offline.
3. Update `docs/release-pubkey.txt` and the `MINISIGN_SECRET_KEY`
   secret in a single PR.
4. Cut a `vX.Y.(Z+1)` release using the new key. The application
   will accept either old or new keys for one transitional release,
   then drop the old key in `vX.(Y+1).0`.
