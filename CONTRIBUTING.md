# Contributing to pty-speak

Thanks for your interest. This project exists to make Windows terminals
work for blind developers; that goal sets the bar for every change. The
notes below are the rules we already learned the hard way and would like
to not relearn.

## Ground rules

1. **Accessibility outcomes are the acceptance criteria.** A feature
   that compiles, passes unit tests, and looks correct on screen is not
   done until it has been validated against NVDA per
   [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md).
2. **Honor the spec.** [`spec/overview.md`](spec/overview.md) is the
   architectural rationale; [`spec/tech-plan.md`](spec/tech-plan.md) is
   the stage-by-stage implementation plan. Changes that contradict
   either need an issue and discussion first.
3. **Walking-skeleton discipline.** We add Stage N only when Stage N-1
   ships and is validated end-to-end. Don't merge Stage 5 streaming
   notifications before Stage 4 text exposure works in Inspect.exe.
4. **Be conservative with abstractions.** F# discriminated unions, the
   `IAudioSink` interface, and the `SemanticEvent` type are locked in
   from day one because they unlock the future roadmap with no refactor.
   Everything else should be added when the third concrete need appears.

## Development environment

- Windows 10 1809+ (ConPTY) or Windows 11.
- .NET 9 SDK (`winget install Microsoft.DotNet.SDK.9`).
- F# tooling — Visual Studio 2022 or JetBrains Rider, or `dotnet` CLI +
  Ionide for VS Code.
- NVDA installed (free, nvaccess.org) plus the **Event Tracker** add-on
  for confirming Notification events.
- Optional: Inspect.exe, AccEvent.exe, and Accessibility Insights for
  Windows for UIA verification.

See [`docs/BUILD.md`](docs/BUILD.md) for build instructions.

## Branching and pull requests

- Default branch: `main`.
- Feature branches: `feature/<short-slug>`; bugfix branches:
  `fix/<short-slug>`; documentation-only: `docs/<short-slug>`.
- Open a draft PR early. Mark ready for review when CI is green and you
  have run the manual NVDA test for the stage your change touches.
- Squash-merge by default. Keep the PR title in
  [Conventional Commits](https://www.conventionalcommits.org/) form
  (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`).
- Every PR must update [`CHANGELOG.md`](CHANGELOG.md) under
  `## [Unreleased]` if it changes behavior, configuration, key bindings,
  the UIA surface, or anything user-visible.

## The non-negotiable accessibility rules

These are the failure modes we have to actively prevent. Reviewers will
block PRs that violate them.

1. **Do not raise both `TextChangedEvent` and `RaiseNotificationEvent`
   for the same content.** NVDA double-announces. Phase 1 is
   Notification-only; if you need TextChanged, justify it in the PR.
2. **Do not swallow Insert, CapsLock, or numpad-with-NumLock-off
   keys.** Those belong to the screen reader. In `PreviewKeyDown` you
   must `return` (not set `e.Handled = true`) for those keys.
3. **All UIA events must be raised on the WPF Dispatcher thread.**
   `RaiseNotificationEvent` silently no-ops off-thread. Marshal via
   `Dispatcher.BeginInvoke` or `Async.SwitchToContext`.
4. **Spinners must be deduplicated.** A row whose content hash equals
   the previous flush's hash is dropped. Same-row updates ≥5/sec for
   ≥1s are classified as a spinner and suppressed.
5. **Strip control characters from `displayString`** before passing it
   to `UiaRaiseNotificationEvent`. Otherwise NVDA verbalises "escape
   bracket one A".
6. **Earcons stay out of the speech band.** Frequencies must be either
   below 180 Hz or above 1.5 kHz. Duration ≤ 200 ms. Volume ≤ -12 dBFS
   by default.
7. **Run in WASAPI shared mode** (`AudioClientShareMode.Shared`).
   Exclusive mode silences NVDA's TTS and is an immediate revert.

## F# / P/Invoke conventions

- `<Nullable>enable</Nullable>` is set in `Directory.Build.props`,
  which propagates to the F# projects. F# code that assigns `null`
  into a non-nullable type will fail under
  `TreatWarningsAsErrors=true`. Either use option types or the
  appropriate nullable-reference annotations (`string | null` in F#
  9+).
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is also
  project-wide. Fix warnings rather than suppressing them; if
  suppression is genuinely warranted, prefer per-file
  `<NoWarn>FSXXXX</NoWarn>` over project-wide.
- `Terminal.Pty.Native` is the only module allowed to declare
  `[<DllImport>]` signatures. Everything else consumes a `Result<_,_>`
  facade.
- Structs that cross the P/Invoke boundary must be
  `[<StructLayout(LayoutKind.Sequential)>]` with all fields `mutable`
  and ordered exactly as in the Win32 header.
- Use `SafeFileHandle` (or a `SafeHandleZeroOrMinusOneIsInvalid`
  subclass) for every kernel handle. `IntPtr` is reserved for opaque
  pointers we deliberately don't track.
- Set `CharSet = CharSet.Unicode` on every string-bearing API.
- The external API never throws from interop. Errors become DU cases.

## Tests

- **Parser:** Expecto + FsCheck property tests
  (`tests/Terminal.VtParser.Tests`). The minimum bar is "never throws on
  arbitrary bytes" and "feeding bytes one at a time produces the same
  events as feeding them in chunks".
- **Semantic mapper:** Replay golden Claude Code session captures and
  assert the produced `SemanticEvent` sequence
  (`tests/Terminal.Semantics.Tests`).
- **UIA:** FlaUI integration tests
  (`tests/Terminal.Uia.Tests`). They run on `windows-latest` GitHub
  runners which expose a real interactive desktop.
- **NVDA:** **manual** for each release. We deliberately avoid
  scripted NVDA testing — assert at the UIA producer level so the
  product stays screen-reader-agnostic, then confirm with a real screen
  reader.

CI runs `dotnet test` and `dotnet build -c Release` on `windows-latest`.
Local pre-PR checklist:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
```

## Reporting bugs

- Use the [Accessibility issue](.github/ISSUE_TEMPLATE/accessibility_issue.yml)
  template for screen-reader regressions; the
  [Bug report](.github/ISSUE_TEMPLATE/bug_report.yml) template
  otherwise.
- Include screen-reader name and version, Windows build, and the
  pty-speak version (Help → About, or check the title bar).
- Attach an NVDA Event Tracker log if relevant; it confirms which
  Notification events fired with which `displayString`.

## Security

Report security issues privately per [`SECURITY.md`](SECURITY.md). Do
not file public issues for unpatched vulnerabilities, especially those
involving ANSI injection or OSC sequences.
