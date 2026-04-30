# Pull request

## Summary

<!-- One or two sentences. What changed and why. -->

## Stage / phase

<!-- Which stage of spec/tech-plan.md does this touch? "Stage 5", "Stage 8", "docs only", "infra", etc. -->

## Type of change

- [ ] feat — new functionality
- [ ] fix — bug fix
- [ ] docs — documentation only
- [ ] refactor — internal restructuring, no behavior change
- [ ] test — tests added or updated
- [ ] build / ci — tooling, packaging, GitHub Actions
- [ ] chore — dependency bumps, formatting, etc.

## Accessibility checklist

For any PR that touches the parser, semantics, UI, UIA, audio, or
keyboard layers, all of these must be true. If a row does not apply,
mark it N/A.

- [ ] No new `TextChangedEvent` raised on the streaming path.
- [ ] All `RaiseNotificationEvent` / `RaiseAutomationEvent` calls are
      marshalled to the WPF Dispatcher thread.
- [ ] No NVDA modifier keys (`Insert`, `CapsLock`, numpad with NumLock
      off) are swallowed by `PreviewKeyDown`.
- [ ] Spinner-class redraws are deduplicated by row hash.
- [ ] Control characters are stripped from any string passed to
      `UiaRaiseNotificationEvent`.
- [ ] Earcons are below 180 Hz or above 1.5 kHz, ≤ 200 ms, ≤ -12 dBFS.
- [ ] WASAPI shared mode (`AudioClientShareMode.Shared`) is used; no
      exclusive-mode acquisition added.
- [ ] [`docs/ACCESSIBILITY-TESTING.md`](../docs/ACCESSIBILITY-TESTING.md)
      manual smoke-test rows for the touched stage were run against
      NVDA on a clean Windows VM, or a follow-up issue is filed if
      validation is deferred. **If this PR ships new user-visible
      behaviour that CI cannot fully verify**, also add a row to the
      matrix per the document's "Adding new manual tests" section
      (procedure, expected outcome, diagnostic decoder bullet).

## Screenshots and screen-reader output

<!-- Where relevant, paste NVDA Event Tracker output or describe the
     speech the user hears. Screenshots are welcome but not sufficient
     by themselves; include alt text. -->

## Changelog

- [ ] [`CHANGELOG.md`](../CHANGELOG.md) updated under `## [Unreleased]`
      (or this PR is documentation-only and changelog is N/A).

## Related issues

<!-- "Fixes #123", "Refs #456", or "n/a". -->
