# CI testing — future improvements

A scoping document, not a plan to execute now. Captures the
gap between what CI catches today and what dogfood catches,
and the two practical wins that would close it. Written
2026-05-14 after a Cycle 50 series of post-PR-Q dogfood
regressions (sub-prompt narration, line-count slicing,
`EntrySource` mis-tagging, wrap-row miscount) that all
escaped CI and surfaced only when the maintainer ran
release builds.

Pick this up when the cmd-path narration is stable and the
session has bandwidth for test infrastructure work rather
than user-visible fixes.

---

## What CI runs today

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml):

- `dotnet build --configuration Release --no-restore`
- `dotnet test --configuration Release --no-restore --no-build`
- `actionlint` on workflow files.
- Markdown link check.
- Portability lint (substrate / channel boundary).

The xUnit tests in `tests/Tests.Unit/` cover **pure F# logic**:
substrate primitives (`ContentHistory` append/seal,
`SpeechCursor` filter rules), state machines
(`ShellInteraction` transitions), parsers (`VtParser`,
`KeyEncoding`), pure helpers (`AnnounceSanitiser`,
`ShellPolicy`).

## What CI does NOT cover

- **No real shell**. Nothing spawns `cmd.exe` or any other
  shell via `ConPtyHost`. The byte-exchange between
  pty-speak and a live shell is exercised only by the
  maintainer's dogfood.
- **No `Announce()` capture**. The orchestration code in
  `Program.fs` (`recordTransitionImpl`, the sub-prompt
  screen-read path, `capturePreambleForSubPromptResponse`,
  `computePromptCommandWrapRows`, the history-recall path)
  calls `window.TerminalSurface.Announce(...)` directly.
  Nothing intercepts those calls in a unit test, so
  "what NVDA was asked to say" is unobservable from CI.
- **No NVDA assertions**. NVDA isn't installed on
  windows-latest runners, and even if it were, asserting
  on TTS output is fragile (timing, voice config, pitch
  modulation).
- **No release-build smoke test**. The release workflow
  (`.github/workflows/release.yml`) packages the build
  but doesn't run the packaged exe. The diagnostic
  battery (`Ctrl+Shift+D`) and the canonical-interactions
  corpus (`canonical-interactions.toml`) are the closest
  things to smoke tests, and both run interactively from
  inside a live pty-speak.

## What's technically possible in CI

windows-latest runners are full Windows 10 desktop VMs.
The capability is there; the wiring isn't:

- **ConPTY + cmd.exe** — ✅ `ConPtyHost` would work.
  Runner image has `cmd.exe` and a desktop session
  available. `Terminal.Pty` already runs against ConPTY
  in unit tests where applicable, but no test currently
  spawns a real `cmd.exe` end-to-end.
- **ConPTY + PowerShell / pwsh** — ✅ both shells are
  pre-installed on the runner image.
- **ConPTY + bash** — ❌ requires WSL which isn't in the
  default runner image. Would need an explicit setup
  step (~minutes added to CI runtime) or a separate
  Linux job that uses a different PTY layer entirely.
- **Capturing `Announce()` calls** — ✅ requires routing
  the announce path through a test-controllable
  interface instead of WPF's `RaiseNotificationEvent`
  directly. Today it's a direct method call on
  `TerminalView`; introducing a small seam would unblock
  this without changing the production behaviour.
- **NVDA TTS assertions** — ❌. Don't go here. Assert on
  the text passed to `Announce()`, not on what NVDA
  produces.

## Same gaps in release builds

The release workflow:

1. Builds Release.
2. Packages with Velopack (`scripts/release-velopack.ps1`).
3. Uploads installer artifacts.

There is no automated launch-and-smoke-test of the packaged
installer. The maintainer's manual "open and try things"
pass after each preview build is the only smoke test. This
is a meaningful gap: bugs that depend on installed-vs-
development context (Velopack DLL replacement, signed-
binary behaviour, single-instance handling, etc.) only
surface after the maintainer dogfoods.

---

## Two practical wins

### Win 1 — orchestration unit tests (cheap, ~50–100 LOC)

**Scope**: unit-test the orchestration helpers that have
been most regression-prone over the post-Cycle-49 series:

- `computePromptCommandWrapRows` (PR-N / PR-O / PR-Q):
  given fake `screen.Cols`, fake `currentSession.Active`,
  and fake `lastSubmittedCommandEndCursorRow` /
  `lastSubmittedCommandPromptRow`, assert the returned
  wrapRows for representative cases (no wrap, exactly
  fits, one wrap, two wraps, history-recall path with
  `cmdLen=0`, defensive fallback).
- `subPromptScreenReader` body (PR-K / PR-N): given a
  fake snapshot + `currentSession.Active`, assert the
  returned screen-read text. Cover wrap-skip,
  `PromptRowIndex=None` fallback, no-rows-after-prompt.
- `capturePreambleForSubPromptResponse` body (PR-K /
  PR-N / PR-Q / PR-R): given fake snapshot + cursor row,
  assert `subPromptPreambleLineCount`. Cover the test-02
  canonical case, no-typed-response case, mid-row scroll
  drift case.
- The byte-write wrapper's CR branch (PR-O / PR-Q
  cursor-capture gate): assert `lastSubmittedCommandLength`
  and `lastSubmittedCommandEndCursorRow` are updated only
  for top-level Enter, not sub-prompt-response Enter.

**Why this is the highest-ROI work**: every PR-K / PR-N /
PR-O / PR-Q / PR-R bug had a clean unit-test reproducer.
The orchestration helpers don't depend on WPF, ConPTY,
NVDA, or any external state — just F# values. A
test fixture that sets up the mutables to a known shape
and calls the helper is a few lines per case. If the
tests existed, every regression in this series would have
failed at CI rather than at maintainer dogfood.

**Constraints to think about when scoping**:

- `recordTransitionImpl` and the byte-write wrapper are
  closures over compose-local state in `Program.fs`. Need
  to either factor the testable bits into named functions
  (preferred) or use a test-only constructor that wires
  fakes for the mutables.
- The screen-read paths reference `screen.SnapshotRows`
  which is a real Screen instance method. Either inject a
  `screenSnapshotProvider: unit -> int * (int * int) * Cell[][]`
  or drive against a real Screen with hand-crafted byte
  inputs.
- `currentSession.Active` is a `SessionModel.T` field.
  Constructing a synthetic SessionModel state for the
  test's preconditions is straightforward (the type is
  internal but `[<assembly: InternalsVisibleTo>]` is
  already wired).

### Win 2 — end-to-end ConPTY + Announce-capture integration tests (~300–500 LOC)

**Scope**: integration test harness that:

1. Spawns a real `ConPtyHost` against `cmd.exe` (or
   PowerShell / pwsh).
2. Wires a fake `TerminalSurface` that captures every
   `Announce(text, activityId)` call into an
   `ImmutableList<AnnounceCall>`.
3. Replays scenarios from `canonical-interactions.toml`
   (the user-side test corpus that runs interactively
   today via `Ctrl+Shift+D`), driving keystrokes via
   `host.WriteBytes`.
4. After each scenario, asserts the captured announce
   sequence matches the expected list.

**What this catches that Win 1 doesn't**:

- ContentHistory append timing under real cmd byte
  arrival cadences (PR-Q's `ActiveSpanSource` race was
  triggered by cmd's set/p prompt with no trailing `\n`,
  exactly the kind of timing Win 1 unit tests would
  approximate but not exactly reproduce).
- Heuristic prompt-detector behaviour against real cmd
  prompt patterns and PowerShell variations.
- Sub-prompt idle-detection against real PTY byte gaps
  (the 350 ms gate fires differently when the bytes
  actually take a non-deterministic time to arrive
  vs. a synthetic test that posts them all at once).
- OSC sequence handling (the `\x1B]0;TITLE\x07` issue
  PR-K caught — synthetic tests rarely include OSC).

**Constraints to think about when scoping**:

- ConPTY startup on CI runners: the runner has the
  capability but I don't know the cost (likely fast —
  it's just a Windows API call). Worth measuring before
  committing.
- WPF dependency: pty-speak compose currently depends on
  a `MainWindow`. The orchestration code needs to be
  callable without instantiating WPF UI. Either factor
  the compose root to take an interface-typed surface
  (preferred — also unblocks Win 1) or accept the cost
  of running a hidden WPF window in the test harness.
- `canonical-interactions.toml` parsing has been used for
  the `Diagnostics → Test ...` menu items but not for an
  external xUnit fixture. The corpus loader currently
  runs inside the live pty-speak process; would need a
  factored loader callable from a test.
- TerminalSurface seam: `window.TerminalSurface.Announce`
  is a direct method call on the WPF `TerminalView`
  class. To capture from a test, introduce a small
  interface (`IAnnounceSink`) and pass an implementation
  in the compose root. The WPF path uses the production
  implementation; tests use a capturing one. ~20 LOC
  refactor.
- Test scenario format: the existing
  `canonical-interactions.toml` is shell-agnostic
  scenarios with expected outcomes. Could be reused as
  the test source so the test corpus stays single-
  sourced between interactive (Ctrl+Shift+D) and
  CI use.

**Why hold this for a dedicated cycle**: requires
infrastructure work in three independent areas (compose-
root factoring, TerminalSurface seam, ConPTY-in-CI
startup). Each is small but the combination needs careful
sequencing — better to scope and ship as one coherent
change than mash into a "fix this bug" PR.

## Coverage matrix (what each win covers)

| Concern | Today | Win 1 | Win 2 |
|---|---|---|---|
| Substrate primitives (ContentHistory, SpeechCursor pure logic) | ✅ unit tests | ✅ | ✅ |
| State machine transitions (ShellInteraction) | ✅ unit tests | ✅ | ✅ |
| Orchestration helpers (computeWrap, capturePreamble, etc.) | ❌ | ✅ | ✅ |
| Byte-write wrapper logic (CR branch, arrow detect) | ❌ | ✅ | ✅ |
| ContentHistory append timing under real PTY cadences | ❌ | partial | ✅ |
| Heuristic prompt-detector against real shell prompts | ❌ | ❌ | ✅ |
| Sub-prompt idle-gate against real cmd byte gaps | ❌ | ❌ | ✅ |
| OSC sequence handling end-to-end | ❌ | ❌ | ✅ |
| Announce sequence + activity IDs per scenario | ❌ | ✅ (orchestration only) | ✅ (full) |
| NVDA TTS output assertions | ❌ | ❌ | ❌ |
| Release-build smoke test (signed installer launch) | ❌ | ❌ | ❌ |

## Out of scope even for Win 2

- **NVDA TTS assertions**: don't go there. Assert on what
  was passed to `Announce()`, not on what NVDA produced.
  TTS output is dependent on voice config, speech rate,
  punctuation rules, NVDA add-ons, etc. Fragile.
- **Release-build smoke test**: a separate concern. Would
  need a signed-installer launch-and-screenshot setup,
  probably running against the maintainer's installed
  Velopack flow. The Velopack PR-M staleness investigation
  could justify this.
- **Cross-shell coverage as a CI gate**: bash via WSL
  needs a separate runner image. PowerShell + cmd are
  enough for the first iteration; add bash later if
  needed.

## When to pick this up

After the cmd-narration path is stable for at least one
cycle without a "bug discovered in dogfood that should
have been caught earlier" report. Until then, the
maintainer's dogfood IS the test harness — and adding
test infrastructure while still iterating on the core
behaviour costs more than it saves (every behaviour
change requires synchronous test updates).

The trigger for picking this up is "Cycle N closes with
no dogfood-only regressions" — that's when the test
matrix is stable enough to be worth encoding.
