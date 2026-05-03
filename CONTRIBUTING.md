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
- Branch naming follows the Conventional Commits prefix: `feat/<slug>`
  (or `feature/<slug>`), `fix/<slug>`, `chore/<slug>`, `docs/<slug>`,
  `ci/<slug>`. Automated contributors may use `claude/<slug>`.
- Open a draft PR early. Mark ready for review when CI is green and you
  have run the manual NVDA test for the stage your change touches.
- Squash-merge by default. Keep the PR title in
  [Conventional Commits](https://www.conventionalcommits.org/) form
  (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`).
  All PRs to date have been squashed; the merge commit subject becomes
  the canonical history line.
- **Delete the source branch after the squash-merge lands** (both
  remote and local). The squashed commit on `main` is the canonical
  reference; the branch is redundant after merge and accumulates as
  visual noise in `git branch -a`. GitHub's "Delete branch" button
  on the merged PR page does the remote half in one click; locally,
  `git fetch --prune origin` + `git branch -d <name>` finishes the
  job. Without this discipline the repo accumulates dozens of stale
  refs over time (the post-Stage-4.5 hygiene sweep removed 75+ such
  branches in one go).
- **One concern per PR.** When tempted to bundle two improvements,
  split them. Past frustrations on this repo trace back to oversized
  PRs with multiple moving parts; small focused PRs review faster and
  bisect cleaner.
- **CI failure on an open PR → push a fixup commit to the same
  branch.** GitHub PRs track the branch HEAD, not a snapshot at
  PR-creation time. A `git push` to the open PR's branch
  auto-extends the PR with the new commit and re-runs CI against
  the new HEAD; the PR number, title, body, `Closes #N`
  references, and any auto-merge configuration are preserved.
  **Don't open a new PR for a fixup** — that creates a stack the
  maintainer has to merge in order. The standing rule is
  **one PR per concern, multiple commits on the branch as needed**;
  the squash-merge convention combines the original + fixup into
  a single canonical commit on `main`. PR #121 (Issue #107
  filename refinement) used this rhythm: initial commit hit a
  F# 9 nullness `FS3261` error in CI, a single fixup commit on
  the same branch updated the helper signature, CI re-ran green,
  the PR auto-extended, and the maintainer merged the
  two-commit branch as one squashed commit. See "F# gotchas
  learned in practice" — F# 9 nullness for the underlying
  technical issue.
- **PR body**: brief Summary + What changed + Test plan checklist.
  Stage-implementation PRs also include a "What this PR deliberately
  omits" section pointing at the stage(s) that own the deferred items,
  so reviewers don't ask for scope that belongs elsewhere.
- Every PR must update [`CHANGELOG.md`](CHANGELOG.md) under
  `## [Unreleased]` if it changes behavior, configuration, key bindings,
  the UIA surface, or anything user-visible. When a release is cut, the
  unreleased entries are rewritten into a clean per-release section
  (template: see the existing `[0.0.1-preview.15]` entry). The release
  workflow gates on a matching `## [<version>]` section existing
  before it builds — don't tag a release without updating CHANGELOG
  first.

## Documentation policy

- **`spec/` is immutable.** It captures the external research that
  drove the design (`overview.md`) and the stage-by-stage plan
  (`tech-plan.md`). Don't edit it. Architectural changes that
  contradict the spec need an explicit ADR-style PR updating the spec
  with an issue and discussion preceding it.
- **Observed platform quirks** (things we learn by running the code,
  not from external research) go in [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md)
  or sibling platform-quirks files as the project grows. Keep
  `spec/overview.md` for the planned design and `docs/CONPTY-NOTES.md`
  for what we actually saw.
- **[`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) is the rollback
  contract.** Every shipped stage adds a row to its "Current
  checkpoints" table; if you can't push the tag yourself, also add a
  row under "Pending checkpoint tags" with the exact push commands so
  a maintainer can sweep them later. The PR that lands a checkpoint
  gets the `stable-baseline` label.
- **[`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) "Common
  pitfalls" is the funnel** for every diagnostic-loop lesson. If you
  hit something nasty in CI or a release, the fix is half the patch
  and the entry under "Common pitfalls" is the other half.
- **[`docs/USER-SETTINGS.md`](docs/USER-SETTINGS.md) tracks
  candidate user settings** — anything currently hardcoded that
  users might plausibly want to control later. See "Consider
  configurability when iterating" below for the working rule;
  **reviewers will request changes on PRs that introduce
  config-shaped values without updating the catalog or noting
  "no candidate settings introduced" in the PR description.**

## Consider configurability when iterating

Every PR that introduces a hardcoded constant or fixed behaviour
should pause and ask: **is this a value users might want to
control later?** Common shapes that hit this:

- A magic number (font size, screen rows × cols, timeout
  duration, percentage threshold).
- A choice between alternatives where reasonable people would
  pick differently (word-boundary algorithm, default shell,
  colour palette).
- A behaviour that varies by user task (verbosity, when to
  announce things, which audio device for earcons).

If the answer is "yes, plausibly," the contributor's
obligations are:

1. **Still pick the right default and ship it as a constant.**
   Don't add a one-off settings file ahead of the Phase 2 TOML
   substrate — that's tech debt the moment proper config lands.
2. **Add or update a section in
   [`docs/USER-SETTINGS.md`](docs/USER-SETTINGS.md)** capturing
   the current state, why it's hardcoded now, what
   configurability would look like, and any per-context nuances.
3. **If the value lives in a shared module** (e.g. F# constants
   on a top-level type), name it something the future
   config-loader can find without source archaeology. Better:
   `Terminal.App.Defaults.ScreenRows = 30` than a bare `30`
   buried in `Program.fs`.
4. **The PR description** explicitly notes "future-config
   candidate logged in USER-SETTINGS.md" so reviewers know to
   check that the catalog stays current.

Reviewers should request changes on PRs that introduce a
clearly-config-shaped value without updating USER-SETTINGS.md
or the PR description's configurability note.

The purpose of this rule isn't to make every PR a config-design
exercise. The purpose is to make sure the rationale and
candidate list stays current as the project grows, so when the
Phase 2 TOML substrate lands, the catalog of what to expose is
already there — not reverse-engineered from "what was hardcoded
where" by reading every PR's diff.

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
   `Dispatcher.InvokeAsync` (preferred), `Dispatcher.BeginInvoke`, or
   `Async.SwitchToContext`.
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

### F# gotchas learned in practice

These are mistakes the project has actually made; keep them out of
new code.

- **`out SafeFileHandle` byref interop is silently broken.** Declaring
  a P/Invoke `out` parameter as `SafeFileHandle&` produces a
  `NullReferenceException` at the call site even when the kernel
  writes the handle correctly. Use `nativeint&` and wrap manually:
  `new SafeFileHandle(p, ownsHandle = true)`. See
  `src/Terminal.Pty/Native.fs` for the canonical pattern.
- **`let rec` for self-referencing class-body bindings.** A `let`
  inside a class body that calls itself produces `error FS0039: 'X'
  is not defined`. Add the `rec` keyword. The compiler does not
  suggest this fix.
- **F# `internal` (not `private`) on members companion modules need.**
  A companion `module` is in a different IL scope from its `type`'s
  `private` members, so a `private` constructor breaks the
  `Foo.create` pattern. Use `internal`.
- **Discriminated-union access from C# uses `IsXxx` predicates and
  `.Item` / `.Item1` / `.Item2` payload accessors.** Stage 3b's
  `Views/TerminalView.cs` reads `Cell.Attrs.Fg.IsDefaultFg` and
  `Cell.Attrs.Fg.Item` (the `int` payload of the `Indexed` case).
  Worth knowing before the Stage-4 UIA peer crosses the boundary.
- **F# 9 nullness annotations bite at .NET-API boundaries.** Many
  .NET-API methods are typed `string?` (nullable string) under
  `<Nullable>enable</Nullable>` — `Path.GetFileName`,
  `Path.GetDirectoryName`, `Environment.GetEnvironmentVariable`,
  `StreamReader.ReadLine`, etc. Passing the result to a non-null
  `string` parameter compiles to an `FS3261` warning that becomes
  a build error under
  `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (CI fails
  on Windows-latest with the actual error text reading
  "Nullness warning: A non-nullable 'string' was expected but
  this expression is nullable"). Two acceptable patterns:

    - **Helper / function signature accepts `string | null`** and
      pattern-matches the null case at the boundary. Matches the
      convention established by `Terminal.Core.AnnounceSanitiser.sanitise`
      and `Terminal.Core.KeyEncoding.encodeOrNull`. Useful when
      many call sites would otherwise duplicate null-coercion.
      Inside the function:

      ```fsharp
      let foo (x: string | null) : 'T =
          let x =
              match x with
              | null -> failwith "x was null"
              | s -> s
          // ... use non-null x ...
      ```
    - **Coerce at the call site** with `nonNull` (from `FSharp.Core`)
      or an inline `match ... with | null -> ... | s -> s`. Useful
      for one-off boundary crossings without changing helper
      signatures.

    Issue #107's filename-format helper hit this when test code
    passed `Path.GetFileName(...)` to a non-null parameter; PR #121
    moved the helper signature to `string | null` and added the
    pattern-match. The fix landed as a fixup commit on the same
    branch (see "Branching and pull requests" — fixup-commit
    rhythm) so the PR auto-extended without scope churn.

### WPF gotchas learned in practice

- **`FrameworkElement` does NOT have a `Background` property.** Only
  `Control` and `Panel` do. A custom-render `FrameworkElement` needs
  its own private brush field. See `Views/TerminalView.cs`.
- **WPF SDK auto-classifies `App.xaml` as `ApplicationDefinition`**
  based on filename, which is invalid in `OutputType=Library`
  projects (build error `MC1002`). Either remove `App.xaml` and use
  a plain `App.cs : Application`, or move the WPF entry to the
  executable project. We do the former.
- **Don't add explicit `<Page>` / `<ApplicationDefinition>` items to
  a `Microsoft.NET.Sdk` project with `<UseWPF>true</UseWPF>`.** The
  SDK auto-globs them and explicit items produce `NETSDK1022`.
- **`dotnet publish -r win-x64 --self-contained` after a
  platform-default `dotnet restore` fails with `NETSDK1047`** because
  the earlier restore didn't generate RID-specific assets. Drop
  `--no-restore` from the publish step (or restore with the RID).

## Tests

The project uses **xUnit + FsCheck.Xunit**, pinned to FsCheck.Xunit 3.x
because the `[<Property>]` attribute integrates cleanly with
`xunit.runner.visualstudio` (Expecto was considered but never adopted).
All current tests live in a single `tests/Tests.Unit/` project; the
empty `tests/Tests.Ui/` project reserves the path for FlaUI work.

- **Parser:** fixture tests + FsCheck property tests in
  `tests/Tests.Unit/VtParserTests.fs`. The minimum bar is "never
  throws on arbitrary bytes" and "feeding bytes one at a time produces
  the same events as feeding them in chunks".
- **Screen model:** fixture tests in
  `tests/Tests.Unit/ScreenTests.fs` covering Print + auto-wrap +
  scroll, BS/HT/LF/CR, CSI cursor moves and erases, basic-16 SGR.
- **ConPTY host:** Windows-only acceptance test in
  `tests/Tests.Unit/ConPtyHostTests.fs` (runtime-skipped elsewhere).
- **UIA (Stage 4+):** FlaUI integration tests will land in
  `tests/Tests.Ui/`. They run on `windows-latest` GitHub runners which
  expose a real interactive desktop.
- **Semantic mapper (Stage 5+):** golden Claude Code session captures
  asserting the produced `SemanticEvent` sequence will live in
  `tests/Tests.Unit/SemanticsTests.fs` (or its own project once the
  module is carved out).
- **NVDA:** **manual** for each release. We deliberately avoid
  scripted NVDA testing — assert at the UIA producer level so the
  product stays screen-reader-agnostic, then confirm with a real screen
  reader.

### Test fixtures: CSI / OSC / DCS sequences

Existing tests in `VtParserTests.fs` and `ScreenTests.fs` embed a
**literal 0x1B byte** (ESC) directly in the F# source. The byte
is invisible in most editors but appears as `\033` under `od -c`.
The test reads `feed screen (ascii "[5;3H")` to the eye, but the
real bytes between `"` and `[` are `0x1B 0x5B`, so the parser
recognizes CSI CUP. This is the pre-existing convention.

**It is also a foot-gun.** Any edit that round-trips through plain
text — including most agent edit tools, plain-text patches, and
some web-editor copy-paste — silently strips the 0x1B byte. The
test then literally reads `"[5;3H"` (no ESC), the parser emits
five Print events, and the assertion fails (or worse, passes
vacuously if it only checks something the Prints happen to
satisfy). PR #38 burned one CI cycle on exactly this; the fix
landed in `4c9c0d6`.

For new test fixtures, prefer the explicit F# Unicode escape so
the dependency on the parser's CSI handling is visible in source:

```fsharp
feed screen (ascii "\u001b[5;3H")           // CUP
feed screen (ascii "\u001b[31m")            // SGR red
feed screen (ascii "\u001b]0;Title\u0007")  // OSC 0; bell-terminated
```

Both forms compile to the same bytes at runtime; `\u001b` survives
text-only edits. When touching a test that already uses the
raw-byte form, leave it as-is rather than rewriting — but if a
diff would land on that line for any other reason, take the
opportunity to migrate it.

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
