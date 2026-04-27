# Stable checkpoints

This document tracks **stable development checkpoints** in the
project's history — known-good states deliberately marked for easy
rollback when a later change breaks something fundamental. Each
checkpoint is anchored by three durable references:

- A **git tag** in the `baseline/` namespace
  (the primary handle for `git checkout` / `git reset`)
- A **GitHub PR label** `stable-baseline` on the PR that landed the
  checkpoint state (searchable via `is:pr label:stable-baseline`)
- A **GitHub Release** when the checkpoint matches a shipped preview

The `baseline/` tag namespace deliberately avoids the `v*` patterns,
so creating, fetching, or deleting a checkpoint tag never interacts
with the release workflow.

## Current checkpoints

| Tag | PR | Release | Description |
|---|---|---|---|
| `baseline/stage-0-ci-release` | [#26](https://github.com/KyleKeane/pty-speak/pull/26) | [v0.0.1-preview.15](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.15) | Stage 0 ship: F# / .NET 9 / WPF skeleton, working CI (build + test + Velopack pack smoke), working release pipeline (`release: published` → build → vpk pack → softprops upload). NVDA-validated unsigned installer. First state where the end-to-end deployment pipe is demonstrably green. |

## Rolling back to a checkpoint

### Read-only inspection (browse the tree)

```bash
git fetch origin --tags
git checkout baseline/<checkpoint-name>
```

This puts you in detached-HEAD state on the checkpoint commit. Use it
to read code, run tests, copy snippets out, or build the artifacts of
that point in time.

### Restart a feature branch from a checkpoint

```bash
git fetch origin --tags
git checkout -b feature/<short-slug> baseline/<checkpoint-name>
```

Use this when a later stage's work has gone sideways and you want to
start over from the last known-good state. Push the new branch and
open a PR as usual.

### Reset `main` (last resort, destructive)

If a series of bad merges has polluted `main` and individual reverts
aren't tractable:

```bash
git fetch origin --tags
git checkout main
git reset --hard baseline/<checkpoint-name>
git push --force-with-lease origin main
```

This **rewrites public history** on the default branch. Coordinate
with all maintainers first. Prefer per-merge `git revert` PRs unless
the pollution is too tangled.

## Adding a new checkpoint

When a stage's work ships and its validation matrix passes (per
[`spec/tech-plan.md`](../spec/tech-plan.md) and
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)), mark
the merge commit as a checkpoint so you can return to it later:

1. **Push an annotated tag** at the merge SHA:
   ```bash
   git fetch origin main
   git tag -a baseline/<stage-or-purpose> <merge-sha> \
     -m "<one-paragraph description>"
   git push origin baseline/<stage-or-purpose>
   ```
   Tag pushes don't trigger the release workflow (it's keyed on
   `release: published`), so this is safe.

2. **Apply the `stable-baseline` label** to the PR that landed the
   checkpoint. PR sidebar → *Labels* → `stable-baseline`. The label
   is auto-created if it doesn't exist.

3. **Add a row** to the "Current checkpoints" table above with the
   tag name, PR link, release link (if applicable), and a one-line
   description of what makes this state stable.

4. **If the checkpoint corresponds to a shipped release**, link the
   release in the table column. Otherwise leave that column empty —
   not every checkpoint is a published release (Stage 0 was, but a
   mid-stage refactor checkpoint may not be).

## Why checkpoints matter

`pty-speak` follows a walking-skeleton plan with twelve stages
([`spec/tech-plan.md`](../spec/tech-plan.md)). Each stage adds a
narrow vertical slice on top of the previous skeleton. When a later
stage's experimental work breaks something fundamental, returning to
the most recent known-good checkpoint is faster than unpicking a
stack of partial commits.

The first checkpoint, `baseline/stage-0-ci-release`, was deliberately
placed *after* the deployment-pipe diagnostic loop that produced
`v0.0.1-preview.{1..14}`. Restarting from this point means starting
Stage 1 from a state where the build, test, and release workflows
all demonstrably work end-to-end. The lessons from that diagnostic
loop are captured in
[`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) under "Common
pitfalls" so they don't have to be re-learned.
