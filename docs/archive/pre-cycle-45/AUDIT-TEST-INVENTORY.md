# Audit: Test Inventory Classification

> **Snapshot**: 2026-05-07
> **Status**: audit / inventory document — no code change
> **Authoring item**: Track B of the comprehensive audit (folds in backlog item 25)
> **Companion docs**:
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) — Track A (code-against-doc validation). This doc is the test-layer counterpart.
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational substrate; Track B references stage names.
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking history substrate; Track B reports zero current test coverage.
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing; Track B reports per-component coverage.
> - [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition substrate; Track B reports zero current test coverage (substrate not yet implemented).
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas; some recommendations cross-reference there.
> - [`CONTRIBUTING.md`](../CONTRIBUTING.md) — testing conventions (xUnit, FsCheck.Xunit, backtick-naming, fixture cautions).

## Why this exists

PR #166 (sub-row suffix-diff) surfaced a class of test
fragility: a pre-existing test asserted on a payload string
shape (`Assert.Contains "abc"`) that was correct under
verbose-emit semantics but broke when suffix-diff split the
emit across leading-edge + trailing-edge legs. The fix was
mechanical (update the assertion), but the underlying issue
is structural: **some tests are semantic-laden** — they pin
a specific output shape that's only correct under specific
substrate semantics. When substrate semantics evolve, those
tests break, often with confusing diff output.

The maintainer's framing 2026-05-06 (captured in
`CONTRIBUTING.md` "Tests with semantic-laden assertions"):

> Before jumping to another big phase, plan a pass dedicated
> to inventorying testing + validation infrastructure so we
> don't carry semantic regressions silently.

This audit is that pass. Track B walks every test file,
classifies the dominant pattern, surfaces fragility
clusters, and recommends targeted follow-up cycles.

## What this document is

A snapshot-dated **classification + gap report** for
pty-speak's test suite. Per Track A's pattern, this doc is
**audit-only** — no new tests, no fixture corpus, no
diagnostic-battery extensions. Subsequent narrower-concern
cycles ship those follow-ups, each tackling one specific gap
(e.g. "property tests for `CanonicalState.computeDiff`
invariants"). This doc identifies what needs attention; the
fixup cycles apply.

The doc is structured to mirror Track A
(`AUDIT-CODE-CONSISTENCY.md`):
- Methodology
- Findings summary (✅ / ⚠ / ❌ counts)
- Per-file inventory (table + per-file paragraph for files
  with notable observations)
- Per-category analysis
- Substrate coverage gap analysis
- Recommendations (tiered)
- Cross-references

## Methodology

The audit was performed via direct inspection (Read + grep)
on 2026-05-07. Steps:

1. **Project enumeration**: read `tests/Tests.Unit/Tests.Unit.fsproj`
   and `tests/Tests.Ui/Tests.Ui.fsproj` to determine compile
   order and file membership.
2. **Per-file test counts**: for each test file, count
   `[<Fact>]` + `[<Theory>]` + `[<Property>]` attributes via
   `grep -c "^\s*\[<Fact>\]\|^\s*\[<Theory>\]\|^\s*\[<Property>\]"`.
3. **Per-file LOC**: `wc -l` per file.
4. **Sample classification**: sample 5-10 test names per file
   (via `grep "^let \`\`"`) to determine the dominant
   pattern: algorithm-correctness, output-shape, interaction,
   or mixed.
5. **Substrate cross-reference**: grep tests for substrate
   component names (StreamPathway, CanonicalState, SessionModel,
   EditDelta, etc.) to identify coverage gaps relative to the
   research-stage docs.
6. **Property-based test enumeration**: `grep -rn
   "\[<Property>\]" tests/` lists current FsCheck.Xunit
   property tests.
7. **Diagnostic battery review**: read
   `src/Terminal.App/Diagnostics.fs` to enumerate the
   current self-test cases.

The audit does NOT execute tests. It does NOT run
`dotnet test`. It's pure static inspection of test code.

## Findings summary

**Test-suite headline numbers (verified 2026-05-07):**

- **531 tests** total
  (527 `[<Fact>]` + 4 `[<Property>]` + 0 `[<Theory>]`).
- **27 meaningful test files** (24 in Tests.Unit + 3 in
  Tests.Ui; excludes 2 `Program.fs` xUnit harness files
  and 1 `Placeholder.fs`).
- **~7,079 LOC** of test code (Tests.Unit ~6,549 + Tests.Ui
  ~530).
- **0 fixture corpus files** — all test data is
  code-defined.
- **4 property-based tests**, all in `VtParserTests.fs`
  (parser fuzz-resilience). Zero property tests for any
  other substrate component.

**File-level findings (✅ / ⚠ / ❌):**

| Tag | Count | Meaning |
|---|---|---|
| ✅ healthy | 19 | Well-aligned with substrate; stable assertions; coverage proportional to module complexity. |
| ⚠ fragile or stale | 8 | Contains output-shape assertions that may need attention if substrate semantics change; OR has a coverage gap; OR is name-shifted (e.g. file references old module name). |
| ❌ structural concern | 0 | No structurally problematic test files; substrate is sound. |

**Substrate coverage findings:**

| Substrate component | Test status | Notes |
|---|---|---|
| `VtParser` | ✅ covered (22 `[<Fact>]` + 4 `[<Property>]`) | Property tests add real chaos-resilience guard. |
| `Screen` | ✅ covered (70 tests) | Largest non-pathway file by test count. |
| `KeyEncoding` | ✅ covered (65 tests) | Per-key encoding pinned exhaustively. |
| `CanonicalState` | ✅ covered (13 tests) | Dedicated file; matches substrate scope. |
| `StreamPathway` | ✅ covered (81 tests; ⚠ output-shape cluster) | Largest test file; mixed category. Some payload-string assertions are fragile. |
| `TuiPathway` | ✅ covered (8 tests) | Small but proportional. |
| `PathwaySelector` | ✅ covered (11 tests) | New module; well-pinned. |
| `Config` | ✅ covered (35 tests) | TOML loader well-tested; matches Tier 1 parameters scope. |
| `OutputDispatcher` | ✅ covered (21 tests) | Profile/Channel routing pinned. |
| `NvdaChannel` / `EarconChannel` / `FileLoggerChannel` | ✅ covered (14/13/15 tests) | Per-channel tests proportional to surface. |
| `EarconProfile` | ✅ covered (15 tests) | Profile claim semantics pinned. |
| `FileLogger` | ✅ covered (15 tests) | Crash-safety + level filtering pinned. |
| `Diagnostics` (Ctrl+Shift+D battery) | ⚠ no dedicated unit tests | Self-tests run live; no F# unit tests for the orchestrator. |
| `EditDelta` (StreamPathway internal) | ⚠ tested implicitly only | No dedicated tests for `computeRowSuffixDelta` algorithm. |
| `SessionModel` | ⚠ zero tests (substrate not implemented) | Forward-looking; doc-only today. |
| `PaneCoordinator` / `Pane` abstractions | ⚠ zero tests (substrate not implemented) | Forward-looking; doc-only today. |
| `TerminalAutomationPeer` (UIA peer) | ✅ covered (5 tests in Tests.Ui) | Reflection + text-pattern provider pinned. |

## Test-project inventory

### Tests.Unit

`tests/Tests.Unit/Tests.Unit.fsproj`:

- **24 meaningful test files** (excludes `Program.fs` xUnit
  runner harness).
- **530 tests** total.
- **~6,549 LOC**.
- Compile order:
  `SmokeTests` → `ConPtyHostTests` → `VtParserTests` →
  `ScreenTests` → `UpdateMessagesTests` → `WordBoundaryTests` →
  `AnnounceSanitiserTests` → `CanonicalStateTests` →
  `StreamPathwayTests` → `TuiPathwayTests` → `PathwaySelectorTests` →
  `ConfigTests` → `OutputEventTests` → `NvdaChannelTests` →
  `FileLoggerChannelTests` → `EarconChannelTests` →
  `EarconProfileTests` → `OutputDispatcherTests` →
  `KeyEncodingTests` → `FileLoggerTests` → `EnvBlockTests` →
  `ShellRegistryTests` → `ShellSwitchTests` →
  `HotkeyRegistryTests`.

### Tests.Ui

`tests/Tests.Ui/Tests.Ui.fsproj`:

- **3 meaningful test files** (excludes `Program.fs` runner +
  `Placeholder.fs`).
- **5 tests** total.
- **~500 LOC**.
- Compile order:
  `Placeholder` → `AutomationPeerTests` →
  `AutomationPeerReflectionTests` → `TextPatternTests`.
- Smaller surface — UIA peer + text-pattern provider only.
  Most accessibility behaviour validation happens via NVDA
  manual matrix per `ACCESSIBILITY-TESTING.md`, not code
  tests.

## Per-file classification table

Sorted by test count (descending). Categories:
**Alg** = algorithm-correctness; **Out** = output-shape;
**Int** = interaction; **Mix** = mixed.

| File | Tests | LOC | Category | Status |
|---|---:|---:|---|---|
| `StreamPathwayTests.fs` | 81 | 1431 | Mix | ⚠ output-shape cluster |
| `ScreenTests.fs` | 70 | 872 | Mix | ✅ healthy |
| `KeyEncodingTests.fs` | 65 | 503 | Alg | ✅ healthy |
| `ConfigTests.fs` | 35 | 426 | Mix | ✅ healthy |
| `EnvBlockTests.fs` | 24 | 503 | Alg | ✅ healthy |
| `VtParserTests.fs` | 22 + 4 props | 369 | Alg | ✅ healthy |
| `OutputEventTests.fs` | 22 | 238 | Out | ⚠ record-shape pinning |
| `OutputDispatcherTests.fs` | 21 | 392 | Int | ✅ healthy |
| `ShellRegistryTests.fs` | 18 | 212 | Alg | ✅ healthy |
| `WordBoundaryTests.fs` | 18 | 207 | Alg | ✅ healthy |
| `FileLoggerTests.fs` | 15 | 430 | Mix | ✅ healthy |
| `FileLoggerChannelTests.fs` | 15 | 241 | Out | ⚠ payload-string assertions |
| `EarconProfileTests.fs` | 15 | 154 | Out | ⚠ ChannelDecision-shape pinning |
| `NvdaChannelTests.fs` | 14 | 164 | Mix | ✅ healthy |
| `CanonicalStateTests.fs` | 13 | 184 | Alg | ✅ healthy |
| `EarconChannelTests.fs` | 13 | 141 | Int | ✅ healthy |
| `HotkeyRegistryTests.fs` | 12 | 190 | Alg | ✅ healthy |
| `PathwaySelectorTests.fs` | 11 | 126 | Alg | ✅ healthy |
| `ShellSwitchTests.fs` | 10 | 195 | Alg | ✅ healthy |
| `AnnounceSanitiserTests.fs` | 10 | 102 | Alg | ✅ healthy |
| `UpdateMessagesTests.fs` | 9 | 141 | Out | ⚠ message-template shape pinning |
| `TuiPathwayTests.fs` | 8 | 111 | Int | ✅ healthy |
| `ConPtyHostTests.fs` | 3 | 163 | Alg | ⚠ very low coverage for ConPTY surface |
| `SmokeTests.fs` | 1 | 16 | Alg | ✅ healthy |
| `TextPatternTests.fs` (Ui) | 2 | 252 | Out | ⚠ UIA-text-pattern shape pinning |
| `AutomationPeerReflectionTests.fs` (Ui) | 2 | 160 | Alg | ⚠ reflection-based; brittle to peer-internal renames |
| `AutomationPeerTests.fs` (Ui) | 1 | 88 | Int | ✅ healthy |

**Totals**: 27 files; 531 tests (527 `[<Fact>]` + 4 `[<Property>]`); 19 ✅, 8 ⚠, 0 ❌.

## Per-category analysis

### Algorithm-correctness

Tests that exercise pure functions / data transformations.
**Stable across substrate changes** because they pin
mathematical / structural invariants, not behavioural shape.

**Sample tests:**

- `VtParserTests.fs:236` — `parser never throws on arbitrary
  bytes` (FsCheck `[<Property>]`). Fuzz-resilience of the
  parser state machine. Pure invariant.
- `KeyEncodingTests.fs` — `Up in DECCKM normal mode encodes
  as ESC [ A`. Pure function: `(KeyCode, KeyModifiers,
  DECCKM) → byte[]`. 65 such tests pin the WPF-key-to-PTY
  encoding table exhaustively.
- `AnnounceSanitiserTests.fs` — `strips C0 control
  characters`, `preserves multi-byte UTF-8`. Pure
  string→string function.
- `ShellRegistryTests.fs` — `parseEnvVar maps "cmd" to Cmd`.
  Pure parsing.
- `WordBoundaryTests.fs` — boundary detection algorithm pin.

**Observations:**

- ~17 of 27 files are dominantly algorithm-correctness.
- These are the **most stable** tests in the suite — they
  rarely break on substrate changes; when they do break, it
  signals a genuine algorithmic regression worth
  investigating.
- The 4 property-based tests in `VtParserTests.fs` are the
  current high-water mark for invariant pinning. The
  recommended new property tests (see "Property-based test
  recommendations" below) extend this pattern.

### Output-shape

Tests assert on payload strings or specific OutputEvent /
ChannelDecision fields. **Fragile** to semantic changes — if
the substrate's output shape evolves (e.g. PR #166's
suffix-diff change), these break first.

**Sample tests:**

- `OutputEventTests.fs:42` — `OutputEvent.create populates
  v1 default Version`. Asserts on a specific record field's
  default. Stable because the field semantics don't drift,
  but tied to the v1 schema.
- `EarconProfileTests.fs` — `BellRang Apply emits one pair
  with one ChannelDecision`. Asserts on the **count** of
  decisions, not just the content. Fragile if EarconProfile
  ever needs to emit two decisions.
- `FileLoggerChannelTests.fs` — payload-string assertions on
  log line format. Fragile if the format changes.
- `UpdateMessagesTests.fs` — message-template shape pinning.
  Fragile if Velopack messages are reworded.
- `TextPatternTests.fs` (Ui) — UIA `ITextRangeProvider` shape
  assertions. Fragile if the peer's text-pattern semantics
  evolve.

**Observations:**

- ~5 files are dominantly output-shape (EarconProfileTests,
  FileLoggerChannelTests, OutputEventTests, UpdateMessagesTests,
  TextPatternTests).
- These are the **first to break** on semantic changes. PR
  #166's burst-debounce regression was an output-shape
  assertion (`Assert.Contains "abc"` on a trailing-edge
  payload).
- **Mitigation strategies for future cycles**:
  1. **Loosen** when the assertion pins shape that's
     coincidentally true under one mode; tighten the
     assertion to invariant content (e.g. "payload ends with
     `i`" instead of "payload equals `i`").
  2. **Split** when the assertion is correct under two
     branches but fails when behaviour changes; split into
     branch-specific tests.
  3. **Replace** when the assertion is semantically
     overspecified; replace with a contract-level check.
  4. **Keep** when the shape IS the contract (e.g.
     `OutputEvent.Version = 1`).

### Interaction

Multi-component flow tests (pathway → profile → channel;
mode-barrier handling; debounce window). Sit between
algorithm-correctness and output-shape — they pin behaviour
across module boundaries.

**Sample tests:**

- `OutputDispatcherTests.fs` — synthetic profiles + channels;
  asserts on end-to-end dispatch. ConcurrentQueue capture
  pattern (per `OutputEvent` tap) verifies the routing.
- `StreamPathwayTests.fs:85` — `two identical canonical
  frames in a row → only first emits`. Frame-dedup via
  CanonicalState → debounce window → emit. Multi-stage.
- `TuiPathwayTests.fs` — alt-screen-on suppresses streaming
  emit; alt-screen-off resumes. Mode-barrier interaction.
- `EarconChannelTests.fs` — earcon ID → frequency / duration
  via palette → audio synthesis. Producer → channel chain.

**Observations:**

- ~5 files are dominantly interaction.
- These tests are the **most informative** when they pass —
  they validate the substrate's composition. When they
  break, they often reveal genuine substrate bugs (not just
  test fragility).
- Coverage of substrate seams is good for shipping
  components (StreamPathway, OutputDispatcher) but absent
  for forward-looking ones (SessionModel: zero tests; Pane
  Coordinator: zero tests).

### Mixed

Files containing tests of multiple categories. Common in
larger files (StreamPathwayTests at 1431 LOC).

**Sample file: `StreamPathwayTests.fs`** (81 tests):
- Algorithm-correctness sub-cluster: `EditDelta` cases
  (`Suffix` / `Silent`); `longestCommonPrefixLength`
  invariants implied via assertions; `BulkChangeThreshold`
  branching.
- Output-shape sub-cluster: payload-string assertions on
  `StreamChunk.Payload`; `OutputEvent` field-shape
  assertions.
- Interaction sub-cluster: frame-dedup → debounce →
  trailing-edge flush; mode-barrier handling; `BackspacePolicy`
  enum cases tested via parameter helpers
  (`verboseFlushParameters`, `suppressShrinkParameters` at
  lines 69-80).

Mixed-category files are **not problematic by themselves** —
they're a natural consequence of a module's tests living
together. The audit's per-category analysis runs across all
files; mixed files contribute to multiple categories.

## Property-based test recommendations

Today: 4 tests, all parser-fuzz. Recommended candidates for
new property tests targeting substrate invariants:

### 1. `StreamPathway.longestCommonPrefixLength`

Invariants worth pinning:
- `longestCommonPrefixLength a b ≤ min(a.Length, b.Length)`
- Commutative: `longestCommonPrefixLength a b =
  longestCommonPrefixLength b a`
- Equality bound: `longestCommonPrefixLength a a = a.Length`
- Empty edge: `longestCommonPrefixLength "" x = 0`

Module: would land in a new `StreamPathwayPropertyTests.fs`
or extend existing `StreamPathwayTests.fs`. ~5-8 property
tests.

### 2. `StreamPathway.computeRowSuffixDelta`

Invariants worth pinning:
- `Silent ⇒ a = b` (same content; no suffix)
- `Suffix s ⇒ b ends with s` (suffix is a tail of new content)
- `Suffix s ⇒ a is a strict prefix of b` (when `Suffix` non-empty)
- Length-monotonicity: `b.Length ≥ a.Length` for `Suffix`
  case (current implementation only emits Suffix on growth).

~6-10 property tests.

### 3. `CanonicalState.computeDiff`

Invariants worth pinning:
- `ChangedRows ⊆ [0, snapshot.Length)`
- `ChangedRows` is sorted ascending.
- `ChangedRows` has no duplicates.
- Identity: `computeDiff snap snap` returns empty
  `ChangedRows`.
- Roundtrip: applying ChangedText to a row at index N
  preserves the rest of the snapshot.

~5-7 property tests.

### 4. `Coalescer.hashRowContent` / `hashFrame`

Invariants worth pinning:
- Determinism: same input → same hash.
- Content sensitivity: changing one cell changes the hash
  (with high probability).
- Position invariance for `hashRowContent`: same row content
  at different row index → same hash.

~3-5 property tests.

### 5. `Config.tryLoad`

Invariants worth pinning:
- Malformed TOML → defaultConfig (never throw).
- Valid TOML round-trip: write → read → equality.
- Unknown keys are silently dropped + logged.
- Negative parameter values are clamped to default + warned.

~5-7 property tests.

**Total recommended new property tests**: ~25-37 across 5
clusters. Each cluster could ship as a small follow-up PR.

## Fixture corpus recommendations

Today: zero fixture files. Test data is code-defined via F#
helpers (`snapshotOf`, `rowOf`, `cellOf`,
`Path.Combine(Path.GetTempPath(), ...)` for TOML).

**Recommended corpus structure (future)**:

### `tests/Fixtures/shell-sessions/`

Real shell session captures: bytes captured from `cmd` /
`pwsh` / `claude` running representative commands.

Proposed scenarios:
- `cmd/dir-output.bytes` — `dir` output capture.
- `cmd/multi-line-paste.bytes` — paste of a multi-line
  script.
- `pwsh/get-childitem.bytes` — `Get-ChildItem` directory
  listing.
- `pwsh/write-host-red.bytes` — colour-detection
  regression fixture.
- `pwsh/write-host-yellow.bytes` — colour-detection
  regression fixture.
- `claude/banner-startup.bytes` — Claude banner
  (includes red text that triggers false-positive
  ErrorLine; see backlog item 22).
- `claude/multi-line-response.bytes` — typical Claude
  response for echo-correlation tests.

### `tests/Fixtures/osc-133/`

Future SessionModel substrate corpus:
- `pwsh-with-osc133/<scenario>.bytes` — PSReadLine OSC
  133 markup.
- `bash-with-osc133/<scenario>.bytes` — for future WSL2
  shell support.

### `tests/Fixtures/colour/`

Pre-rendered cell grids for color-detection regression
tests:
- `red-row-only.cells` — single red row.
- `mixed-red-yellow.cells` — both colors present.
- `red-outside-diff.cells` — Phase A.2 hotfix regression
  guard.

### Loader pattern

A `tests/Fixtures/Loader.fs` module providing:
- `loadShellSession : string -> byte[]`
- `loadCellGrid : string -> Cell[][]`
- Path resolution from `<repo>/tests/Fixtures/...` regardless
  of where `dotnet test` runs.

## Diagnostic battery review

`src/Terminal.App/Diagnostics.fs` (Ctrl+Shift+D self-test).
Current cases (per Phase 1 exploration):

**PowerShell test set (5 cases)**:
- T1.Plain — `Write-Host PtySpeakDiagPlain` → StreamChunk only.
- T2.Red — `Write-Host -ForegroundColor Red` → StreamChunk +
  ErrorLine.
- T3.Yellow — `Write-Host -ForegroundColor Yellow` →
  StreamChunk + WarningLine.
- T4.PlainAfterRed — Setup red, then plain → StreamChunk
  only. Phase A.2 hotfix regression guard.
- T5.YellowAfterRed — Setup red, then yellow → WarningLine
  (not ErrorLine). Same regression guard family.

**Cmd test set (1 case)**:
- T1.Plain — `echo PtySpeakDiagPlain` → StreamChunk only.

**Claude test set**: empty. Non-deterministic AI output;
intentional skip.

### Gaps

The diagnostic battery is a **valuable integration-test
surface** but currently exercises a narrow slice. Gaps:

1. **No alt-screen / TUI tests** — vim entry/exit; alt-screen
   toggle behaviour; TuiPathway suppression of streaming.
2. **No bell tests** — `echo ^G` (cmd) or
   `[char]7` (pwsh) → expect BellRang event + bell-ping
   earcon.
3. **No multi-line paste tests** — paste of >3 lines should
   exercise `BulkChangeThreshold` fallback.
4. **No mode-barrier tests** — Ctrl+Shift+1/2/3 shell switch
   should emit a barrier-flush event with the configured
   policy (verbose / summary-only / suppressed).
5. **No suffix-diff tests** — PR #166 introduced suffix-diff
   but the battery doesn't exercise it. T1.Plain is a single
   StreamChunk; doesn't cover the LCP-based path.
6. **No backspace-policy tests** — `BackspacePolicy.SuppressShrink`
   vs `AnnounceDeletedCharacter` exercise the row-shrink
   path; no battery case for either today.
7. **No EditDelta tests** — `Suffix` vs `Silent` decision
   cases.
8. **No spinner suppression tests** — spinner-pattern detection
   in StreamPathway has no battery case.
9. **No diagnostic-to-log-file roundtrip** — battery output
   is captured per-test but the log-file write path isn't
   sanity-checked.

### Recommended battery extensions

Each future cycle adds one extension. Suggested order:

1. **Suffix-diff cases** (PowerShell): type a multi-character
   string; assert per-character emit shape.
2. **Bell cases** (cmd + pwsh): assert BellRang event +
   earcon.
3. **Backspace cases** (PowerShell): exercise both
   `BackspacePolicy.SuppressShrink` and
   `AnnounceDeletedCharacter`.
4. **Mode-barrier cases**: shell switch; alt-screen toggle.
5. **Multi-line paste cases**: trigger `BulkChangeThreshold`
   fallback.

Each extension is small (~50-100 LOC) and ships as its own
PR.

## Substrate coverage gap analysis

Cross-references the 5 research-stage docs against test
files. **Reports gaps**, not actual code-vs-test alignment
(that would require reading every module + checking test
correspondence; out of scope for this audit).

### PIPELINE-NARRATIVE.md stages

| Stage | Stage name | Tested via | Status |
|---|---|---|---|
| 1 | Byte ingestion | `ConPtyHostTests.fs` (3 tests) | ⚠ low coverage |
| 2 | Parser application | `VtParserTests.fs` + `ScreenTests.fs` | ✅ 92 tests + 4 props |
| 3 | Notification emission | `ScreenTests.fs` (notification-emission tests) | ✅ |
| 4 | Canonical-state synthesis | `CanonicalStateTests.fs` | ✅ 13 tests |
| 5 | Frame-dedup | `StreamPathwayTests.fs` (frame-dedup cluster) | ✅ |
| 6 | Spinner suppression | `StreamPathwayTests.fs` (spinner cluster) | ✅ |
| 7 | Row-level diff | `CanonicalStateTests.fs` + `StreamPathwayTests.fs` | ✅ |
| 8 | Sub-row suffix detection | `StreamPathwayTests.fs` (suffix-diff cluster) | ⚠ no property tests |
| 8b | Bulk-change fallback | `StreamPathwayTests.fs` (bulk-change cluster) | ✅ |
| 9 | Announcement payload assembly | `StreamPathwayTests.fs` (assembly cluster) | ⚠ output-shape fragility |
| 10 | Profile claim | `OutputDispatcherTests.fs` + `EarconProfileTests.fs` | ✅ |
| 11 | Channel rendering | `NvdaChannelTests.fs` + `EarconChannelTests.fs` + `FileLoggerChannelTests.fs` | ✅ |
| 12 | NVDA dispatch | `Tests.Ui/AutomationPeerTests.fs` + `TextPatternTests.fs` | ✅ |

### INTERACTION-MODEL.md three-component model

| Component | Test status | Notes |
|---|---|---|
| Shell pane (today's) | ✅ extensively tested | Via StreamPathway / Screen / VtParser etc. |
| Input Composition Surface (5.a) | ⚠ zero tests | Substrate not yet implemented (Phase 2). |
| Active Output (5.b) | ✅ tested via StreamPathway | Today's substrate ships. |
| Historical Document (5.c) | ⚠ zero tests | SessionModel not yet implemented. |
| Echo correlation seam | ⚠ zero tests | Not yet implemented. |
| Command-finished seam | ⚠ partial — only via mode-barrier | OSC 133 fully unimplemented. |

### INTERACTION-MODEL.md interactive element taxonomy

| Element | Test status |
|---|---|
| `StreamChunk` | ✅ tested |
| `BellRang` | ✅ tested |
| `ErrorLine` | ✅ tested (PR #163 + #164 regression guards) |
| `WarningLine` | ✅ tested |
| `SpinnerTick` | ✅ tested (suppression path) |
| `AltScreenEntered` | ✅ tested (TuiPathway) |
| `ModeBarrier` | ✅ tested |
| `ParserError` | ✅ tested (VtParserTests) |
| `Custom` | ⚠ no producers; no consumer tests |
| `PromptDetected` | ⚠ reserved; no tests |
| `CommandSubmitted` | ⚠ reserved; no tests |
| `SelectionShown` / `SelectionItem` / `SelectionDismissed` | ⚠ reserved; no tests |
| `HyperlinkOpened` | ⚠ reserved; no tests |

### SESSION-MODEL.md substrate

Status: **zero tests**. Substrate is forward-looking
research-stage design. When implementation ships, tests will
need:
- OSC 133 detection
- Heuristic prompt-boundary fallback (cmd / PowerShell /
  Claude)
- SessionTuple lifecycle
- Persistence (memory-only / session-log / always)
- Query API

### PANE-MODEL.md substrate

Status: **zero tests**. Substrate is forward-looking
research-stage design. When implementation ships, tests will
need:
- Pane abstraction
- Pane Coordinator
- Per-pane UIA peers
- Per-pane ActivityIds
- Pane-state TOML persistence
- Coordination protocols (pane → shell action; shell → pane
  state; pane ↔ pane)

## Recommendations

Tiered by priority + scope. Each item is a candidate for a
follow-up cycle.

### Tier 1 — immediate (small, high-value)

- **Property tests for `longestCommonPrefixLength`** (~5-8
  tests; one PR; ~100-150 LOC).
- **Property tests for `computeRowSuffixDelta`** (~6-10
  tests; one PR; ~150-200 LOC).
- **Property tests for `CanonicalState.computeDiff`** (~5-7
  tests; one PR; ~100-150 LOC).
- **Diagnostic battery extension: suffix-diff cases** (~3-5
  cases; one PR; ~80-120 LOC).
- **Diagnostic battery extension: bell + ParserError cases**
  (~3-5 cases; one PR).

### Tier 2 — next-cycle (medium scope)

- **Property tests for `Config.tryLoad`** (~5-7 tests).
- **Property tests for `Coalescer.hashRowContent` /
  `hashFrame`** (~3-5 tests).
- **Diagnostic battery: backspace-policy + mode-barrier
  cases** (~6-8 cases; one PR).
- **`ConPtyHostTests` expansion** — current 3 tests is low
  coverage for the ConPTY surface. Add ~10-15 more.

### Tier 3 — substrate-implementation-gated

- **SessionModel test suite** — when SessionModel substrate
  implementation begins (per item 28 implementation cycles).
- **Pane abstraction test suite** — when Pane Model
  implementation begins.
- **Echo correlation tests** — when Phase 2 input framework
  cycle ships.
- **OSC 133 detection tests** — paired with SessionModel
  implementation.

### Tier 4 — fixture corpus + replay harness

- **Build `tests/Fixtures/shell-sessions/`** — ~10-20 byte
  captures of representative shell sessions (one PR; ~500
  LOC of fixture data + ~100 LOC of loader).
- **Build `tests/Fixtures/Loader.fs`** — fixture-loading
  module.
- **Snapshot/replay harness** (per backlog item 4) —
  standalone CLI binary that loads a SessionModel snapshot
  + replays through pathway pump. Becomes the testbed for
  new pathway development. Larger work; multiple cycles.

## Cross-references

- [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) —
  Track A; this doc mirrors its structure at the test layer.
- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) —
  substrate stages cross-referenced in coverage analysis.
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking
  substrate; coverage gap noted.
- [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) —
  three-component model + interactive element taxonomy
  cross-referenced.
- [`PANE-MODEL.md`](PANE-MODEL.md) — forward-looking
  substrate; coverage gap noted.
- [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas;
  some recommendations cross-reference there.
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — testing
  conventions (xUnit 2.9.x; FsCheck.Xunit 3.x; backtick
  naming; no fixture cautions).
- [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
  — manual NVDA validation matrix; the "human" arm of the
  test discipline.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Track B audit (Cycle 2) | Initial snapshot; 27 files audited; 19 ✅ + 8 ⚠ + 0 ❌; tiered recommendations for follow-up cycles. |

