#!/bin/bash
# One-time bulk-delete of stale post-merge branches on `origin/`.
#
# The post-Stage-4.5 hygiene audit (2026-05-02) identified 77 remote
# branches whose work has been squash-merged into `main` over the
# project's history. They accumulated because the
# delete-branch-after-merge convention wasn't codified until PR #87
# added it to `CONTRIBUTING.md`.
#
# Each branch listed below has a corresponding closed-or-merged PR;
# the squashed commit on `main` is the canonical reference. The
# `git ls-remote --exit-code` check makes this idempotent — branches
# that have already been deleted are silently skipped, so re-running
# the script after a partial run is safe.
#
# Skipped intentionally:
#   - `main` (obvious)
#   - `chore/hygiene-keybinding-count-and-branch-convention` — the
#     active PR #87 branch that ships THIS script. Once #87 merges,
#     it can be added to a follow-up cleanup if desired (or simply
#     deleted via the GitHub UI's "Delete branch" button on the
#     merged PR page).
#
# Run from any workstation with normal git push permissions:
#
#     bash scripts/cleanup-stale-branches.sh
#
# After it completes, this script file itself can be deleted (it's a
# one-time cleanup). Future branch hygiene is covered by the
# per-PR convention in CONTRIBUTING.md.

set -e
cd "$(git rev-parse --show-toplevel)"
git fetch --prune origin

BRANCHES=(
    chore/add-diagnostic-workflow
    chore/audit-hygiene
    chore/bump-action-gh-release-v3
    chore/bump-changelog-to-preview-2
    chore/bump-changelog-to-preview-3
    chore/bump-upload-artifact-v7
    chore/changelog-preview-18
    chore/checkpoints-pending-tags
    chore/ci-hygiene-actionlint-and-release-gates
    chore/ci-timing-optimisation
    chore/document-checkpoints
    chore/drop-release-env-and-bump-changelog
    chore/finalize-release-pipeline-docs
    chore/install-latest-preview-script
    chore/process-cleanup-test-script
    chore/relax-changelog-gate
    chore/release-bisect-add-notes-step
    chore/release-bisect-pack-only
    chore/release-conditional-delta-pattern
    chore/release-drop-optional-delta-pattern
    chore/release-fix-heredoc-indentation
    chore/release-restore-build-pipeline
    chore/release-restore-publish-pack-upload
    chore/release-restore-softprops-upload
    chore/release-restore-windows-runner
    chore/release-trigger-on-release-event
    chore/remove-diagnose-workflow
    chore/session-handoff-and-final-audit
    chore/stage-1-audit-and-conpty-notes
    chore/stage-4-prep-snapshot-and-handoff
    chore/strip-release-to-minimum
    chore/test-and-ci-cleanup-pre-stage-4
    ci/fix-velopack-manifest-globs
    claude/create-project-docs-xVA6n
    docs/audit-truth-up
    docs/comprehensive-manual-smoke-tests
    docs/contributing-esc-byte-convention
    docs/diagnostic-ux-deferred-followup
    docs/move-stage-11-forward
    docs/post-audit-handoff-update
    docs/release-process-target-branch-guidance
    docs/security-comprehensive-threat-model
    docs/session-handoff-ci-timing-reminder
    docs/session-handoff-stage-4-and-11-verified
    docs/stage-4-three-pr-plan
    docs/user-settings-catalog-and-configurability-principle
    feat/diagnostic-hotkey-ctrl-shift-d
    feat/pre-stage-5-seams
    feat/release-notes-hotkey-ctrl-shift-n
    feat/stage-11-velopack-self-update
    feat/stage-4-text-flaui-test
    feat/stage-4-text-raw-provider
    feat/stage-4.5a-mode-coverage
    feat/stage-4.5b-alt-screen
    feat/stage-4a-minimal-uia-surface
    feat/text-range-word-navigation
    feat/version-suffix-and-update-error-clarity
    feature/stage-1-conpty-hello-world
    feature/stage-2-vt-parser
    feature/stage-3a-screen-model
    feature/stage-3b-wpf-rendering
    fix/main-window-focuses-terminal-view
    fix/parser-param-push-and-delta-nupkg
    fix/release-fetch-prior-walks-back-burned-tags
    fix/stage-4-text-pattern-navigation
    fix/test-script-already-running-mode
    fix/wpf-subsystem-no-console
    sec/sr1-parser-hardening
    sec/sr2-accessibility-hardening
    sec/sr3-security-md-audit-response
    spike/flaui-integration-test
    spike/fsharp-rawelementprovider-compile
    spike/getpatterncore-reflection-probe
    spike/stage-4-fsharp-uia-interop
    spike/textblock-peer-visibility
    spike/wm-getobject-window-subclass
    test/burn-down-deferred-tests
)

deleted_count=0
skipped_count=0
failed_count=0

for branch in "${BRANCHES[@]}"; do
    if git ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
        if git push origin --delete "$branch"; then
            echo "deleted: $branch"
            deleted_count=$((deleted_count + 1))
        else
            echo "FAILED: $branch"
            failed_count=$((failed_count + 1))
        fi
    else
        echo "skipped (already gone): $branch"
        skipped_count=$((skipped_count + 1))
    fi
done

echo ""
echo "=== Summary ==="
echo "Deleted:  $deleted_count"
echo "Skipped:  $skipped_count"
echo "Failed:   $failed_count"
echo ""
echo "Run 'git fetch --prune origin' afterwards to refresh local"
echo "remote-tracking branches."
