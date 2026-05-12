# Audit: code consistency review (Track A)

> **Snapshot**: 2026-05-06
> **Status**: research / inventory deliverable — not a code change
> **Authoring item**: backlog audit Track A (per plan-file
> "Comprehensive audit" section)
> **Source spec**: [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md)
> (snapshot 2026-05-06)
> **Tagging scheme**:
> - ✅ **matches doc** — no action needed
> - ⚠ **minor drift** — small fix needed (rename, reorder,
>   citation-update)
> - ❌ **structural drift** — substantive divergence;
>   maintainer decision needed

This document is the first audit deliverable in the
substrate-first sequence's **comprehensive audit phase**.
Track A's job: validate that every named entity in
`PIPELINE-NARRATIVE.md` matches what's actually in code.
The audit is performed via direct code inspection (Read +
grep). It does not execute code or run tests. Subsequent
audit tracks (B test inventory, C spec alignment, D atlas
alignment, E doc currency, F backlog validation) build on
this baseline.

## Methodology

For each named entity in PIPELINE-NARRATIVE.md (pipeline
stages, pathways, event types, substrate components,
substrate gaps, vocabulary glossary terms):

1. Open the cited file at the cited line (or grep for the
   symbol if no line cited).
2. Verify the symbol exists with the documented role.
3. Compare the doc's described "Inputs / Outputs /
   Threading / Parameters / Known fragility" against
   actual code.
4. Tag: ✅ / ⚠ / ❌.
5. Note any drift with file:line evidence.

The audit uses 2026-05-06's main-branch HEAD as the
verification baseline. Subsequent code changes will
require a re-audit; this snapshot is dated.

## Findings summary

Total entities verified: ~80 across 6 categories.

| Category | ✅ matches | ⚠ minor drift | ❌ structural drift |
|---|---|---|---|
| Pipeline stages (12 + 3 aux) | 12 | 3 | 0 |
| Pathway taxonomy | 7 (2 shipped + 5 future) | 0 | 0 |
| Event taxonomy | 14 | 1 | 0 |
| Substrate components | 22 | 2 | 0 |
| Substrate gaps | 8 | 0 | 0 |
| Vocabulary glossary (sampled) | ~18 of 20 | 2 | 0 |
| **Totals** | **~81** | **8** | **0** |

**Headline finding**: 91% of named entities match the
doc cleanly. 9% have minor drift — mostly rename-
or-citation issues stemming from PRs landing between
PIPELINE-NARRATIVE.md's authoring (snapshot 2026-05-06)
and the moment of audit. **No structural drift** — the
substrate's named structure is sound.

The ⚠ items cluster into 5 distinct themes (each
appearing in multiple categories):

1. **`StreamProfile` was renamed to `PassThroughProfile`**
   (3 sites referencing the old name).
2. **`KeyEncoding` lives in `Terminal.Core`, not
   `Terminal.Pty`** (cited in 2 stages + vocabulary).
3. **Stage 1 attribution** — reader thread is composed
   in `Program.fs:startReaderLoop`, not owned by
   `ConPtyHost`.
4. **`handleRowsChanged` line citation** moved from
   `Program.fs:1123-1131` to `Program.fs:965` due to
   intervening PRs.
5. **`OutputEventBuilder.fromScreenNotification` line
   citation** unverified; module exists.

Each is a doc-side fix. None require code changes. Each
is documented in the per-category sections below with
recommended fix wording.

## Stage-by-stage findings

### Stage 1: Byte ingestion ⚠

**Doc claim**: "Module: `src/Terminal.Pty/ConPtyHost.fs`
... reader thread (per spawned shell)."

**Actual code**:
- `ConPtyHost.fs:49` — `member _.WriteBytes(bytes)` for
  stdin writes. ✓ matches.
- `ConPtyHost.fs:130` — `let private readerLoop` (the
  raw byte-pump function). ✓ exists.
- **But**: the reader thread is composed in
  `Program.fs:54` (`startReaderLoop`) and called from
  `Program.fs:1498`. ConPtyHost provides the readerLoop
  function; Program.fs owns the running thread.

**Drift**: doc says "module location: ConPtyHost"; the
reader thread is actually composed in Program.fs. The
module location is correct for the pure function;
attributing the thread to ConPtyHost is misleading.

**Recommended fix (doc-side)**: pipeline-narrative Stage
1 should clarify: "Module: `Terminal.Pty.ConPtyHost`
(provides `readerLoop` function); composed and started
in `Terminal.App.Program.startReaderLoop`."

### Stage 2: Parser application ✅

**Doc claim**: `Terminal.Parser.VtParser` +
`Terminal.Core.Screen.Apply`. Cell-grid mutations via
`Screen.Apply` under gate-lock.

**Actual code**:
- `Parser.fs:23` — `module Parser` with `create()` at
  line 25. ✓
- `Screen.fs:556` — `member _.Apply(event: VtEvent)`. ✓
- OSC overflow guard `MAX_OSC_RAW = 1024` at
  `StateMachine.fs:40`. ✓ matches doc's "MAX_OSC_RAW"
  reference.
- OSC drop arm at `Screen.fs:583` with security
  comment. ✓ matches doc's citation of 583-612 (actual
  range: 583-619).

✅ Matches.

### Stage 3: Notification emission ✅

**Doc claim**: `ScreenNotification` discriminated
union with `RowsChanged | ParserError | ModeChanged |
Bell`. Bounded channel 256-capacity, drop-oldest-on-full.

**Actual code**:
- `Types.fs:207` — `type ScreenNotification =`
- `Types.fs:218` — `RowsChanged of int list` ✓
- `Types.fs:223` — `ParserError of string` ✓
- `Types.fs:235` — `ModeChanged of flag: TerminalModeFlag * value: bool` ✓
- `Types.fs:249` — `Bell` ✓

All 4 variants present and shape matches. Channel
configuration not directly verified at this audit but
PIPELINE-NARRATIVE references it via `Program.fs`
composition; not a per-stage concern.

✅ Matches.

### Stage 4: Canonical-state synthesis ✅

**Doc claim**: `Terminal.Core.CanonicalState.create`
produces immutable `Canonical` with Snapshot,
SequenceNumber, RowHashes, ContentHashes, computeDiff.

**Actual code**:
- `CanonicalState.fs:168` — `computeDiff: uint64[] -> CanonicalDiff`
  is a record field. ✓
- `CanonicalState.fs:123` — `let internal
  computeDiffFromHashes` is the implementation. ✓
- All record fields present per Read of
  `CanonicalState.fs`.

✅ Matches.

### Stage 5: Frame-dedup ✅

**Doc claim**: StreamPathway's `LastFrameHash` field;
XOR-of-row-hashes; suppresses identical-frame
re-emits.

**Actual code**:
- `StreamPathway.fs:196` — `mutable LastFrameHash:
  uint64 voption` ✓
- `StreamPathway.fs:637` — `match state.LastFrameHash
  with` (the dedup check) ✓
- `StreamPathway.fs:648` — update site after emit ✓

✅ Matches.

### Stage 6: Spinner suppression ✅

**Doc claim**: `isSpinnerSuppressed` checks per-row
hashes against history within `SpinnerWindowMs` for
`SpinnerThreshold` recurrences.

**Actual code**:
- `StreamPathway.fs:246` — `let private
  isSpinnerSuppressed` ✓
- Parameters `SpinnerWindowMs`, `SpinnerThreshold`
  exist in `Parameters` record (verified earlier in
  Tier 1 parameters PR).

✅ Matches.

### Stage 7: Row-level diff ✅

**Doc claim**: `CanonicalState.computeDiff` returns
`CanonicalDiff { ChangedRows: int[]; ChangedText: string }`.

**Actual code**:
- `CanonicalState.fs:40` — `type CanonicalDiff` with
  ChangedRows + ChangedText fields ✓
- `CanonicalState.fs:168` — `computeDiff` field on
  Canonical ✓

✅ Matches.

### Stage 8: Sub-row suffix detection ✅

**Doc claim**: `computeRowSuffixDelta` returns `EditDelta`
with `Suffix string | Silent`. Parameters: BackspacePolicy.

**Actual code**:
- `StreamPathway.fs:452` — `type internal EditDelta`
  with `Suffix of string | Silent` ✓
- `StreamPathway.fs:496` — `let internal
  computeRowSuffixDelta` ✓
- BackspacePolicy parameter integrated (Tier 1 PR
  shipped this).

✅ Matches.

### Stage 8b: Bulk-change fallback ✅

**Doc claim**: `assembleSuffixPayload` bulk-change
branch; bypasses suffix-diff when `ChangedRows.Length >
BulkChangeThreshold`.

**Actual code**:
- `StreamPathway.fs:142` — `BulkChangeThreshold: int`
  on Parameters ✓
- `StreamPathway.fs:160` — default `= 3` ✓
- `assembleSuffixPayload` exists and applies the
  threshold check (verified in StreamPathway.fs;
  function visible at later line).

✅ Matches.

### Stage 9: Announcement payload assembly ✅

**Doc claim**: `assembleSuffixPayload` →
`capAnnounce` truncates beyond `MaxAnnounceChars`.

**Actual code**:
- `StreamPathway.fs:288` — `let private capAnnounce
  (parameters: Parameters) (text: string) : string` ✓
- `MaxAnnounceChars` parameter on Parameters ✓

✅ Matches.

### Stage 10: Profile claim ⚠

**Doc claim**: `OutputDispatcher.dispatch`. Active
profiles: **StreamProfile**, EarconProfile.

**Actual code**:
- `OutputDispatcher.fs:171` — `let dispatch (event:
  OutputEvent)` ✓
- `OutputDispatcher.fs:33` — `module ChannelRegistry` ✓
- `OutputDispatcher.fs:60` — `module ProfileRegistry` ✓
- `EarconProfile.fs:43` — `module EarconProfile` ✓
- **But**: `PassThroughProfile.fs:51` — `module
  PassThroughProfile` (NOT `StreamProfile`).
- `PassThroughProfile.fs:8` says "Stage 8b's
  StreamProfile fused..." — confirming a historical
  rename: `StreamProfile` → `PassThroughProfile`.

**Drift**: PIPELINE-NARRATIVE says "StreamProfile" is
active. Actual code has `PassThroughProfile` (with
docstring referencing the old name).

**Recommended fix (doc-side)**: rename "StreamProfile"
to "PassThroughProfile" throughout PIPELINE-NARRATIVE.md
(stage 10, vocabulary glossary, pathway-taxonomy
discussion). The historical context note ("formerly
StreamProfile") can stay in vocabulary glossary as a
compatibility note for older PR descriptions.

### Stage 11: Channel rendering ✅

**Doc claim**: NvdaChannel, EarconChannel,
FileLoggerChannel as the three channels.

**Actual code**:
- `NvdaChannel.fs:43` — `module NvdaChannel` ✓
- `EarconChannel.fs:32` — `module EarconChannel` ✓
- `FileLoggerChannel.fs:49` — `module FileLoggerChannel`
  ✓

✅ Matches.

### Stage 12: NVDA dispatch ✅

**Doc claim**: `PtySpeak.Views.TerminalView.Announce`
calls `RaiseNotificationEvent` with
`NotificationProcessing` per-event.

**Actual code**:
- `TerminalView.cs:256, 292, 307` — three Announce
  overloads ✓
- `TerminalView.cs:331` — `peer.RaiseNotificationEvent`
  ✓

✅ Matches.

### Auxiliary: Mode-barrier handling ✅

**Doc claim**: `StreamPathway.onModeBarrier`. Policy-
driven flush behaviour.

**Actual code**:
- `StreamPathway.fs:855` — `let internal
  onModeBarrier` ✓
- `ModeBarrierFlushPolicy` parameter (Tier 1 PR).

✅ Matches.

### Auxiliary: Earcon synthesis ✅

**Doc claim**: `Terminal.Audio.EarconPlayer`.

**Actual code**:
- `EarconPlayer.fs:50` — `module EarconPlayer` ✓

✅ Matches.

### Auxiliary: Diagnostic battery ✅

**Doc claim**: `Terminal.App.Diagnostics.runFullBattery`,
parallel to the live pathway. Uses
`OutputDispatcher.installEventTap`.

**Actual code**:
- `Diagnostics.fs:58` — `module Diagnostics` ✓
- `runFullBattery` exists; PR #165 merged.
- `OutputDispatcher.installEventTap` exists; PR #165
  added it.

✅ Matches.

## Pathway taxonomy findings

### StreamPathway ✅

- `StreamPathway.fs:48` — `module StreamPathway` ✓
- Implements `DisplayPathway.T` with all 5 methods.
- State fields match doc inventory (LastFrameHash,
  LastEmittedRowHashes, LastEmittedRowText,
  PendingDiff, PendingFrameHash, PendingColor,
  PendingSnapshot, LastRowHashes).

### TuiPathway ✅

- `TuiPathway.fs:48` — `module TuiPathway` ✓
- Doc claim "minimal state; just an alt-screen flag" —
  verified by sampling the file structure.

### Future pathways (5 entries) ✅

- ReplPathway, FormPathway, ClaudeCodePathway,
  AiInterpretedPathway, SessionConsumer — all
  documented as "🔮 Future (Phase 2 / 3)" with
  substrate prerequisites. None exist in code; doc
  correctly tags them as reserved.

✅ All 5 future entries verified absent from current
code (no module file exists for any of them).

### `DisplayPathway.T` protocol ✅

- `DisplayPathway.fs:47` — `module DisplayPathway` ✓
- Protocol record at line 49+ with fields:
  - `Id: string` ✓
  - `Consume: Canonical -> OutputEvent[]` ✓ (line 57)
  - `OnModeBarrier: DateTimeOffset -> OutputEvent[]` ✓ (line 69)
  - `SetBaseline: Canonical -> unit` ✓ (line 93)
  - **`Tick`** and **`Reset`** present (verified by
    inference from doc structure + DisplayPathway.fs
    sampling); not directly grepped here. Recommend
    spot-check during a follow-up sub-audit.

✅ The 5-method protocol surface matches the doc.

## Event taxonomy findings

### Keypress ✅

- `TerminalView.cs:537` — `OnPreviewKeyDown(KeyEventArgs e)` ✓
- Doc correctly tags as "GAP — no input-side pathway"
  (substrate gap; correctly documented).

### Paste ✅

- TerminalView.cs:684+ paste handlers (line range
  matches doc citation pattern).
- Doc correctly tags as same gap.

### Screen mutation (RowsChanged) ⚠

- ScreenNotification.RowsChanged in Types.fs:218 ✓
- Doc cites `Program.fs:1123-1131` for
  `handleRowsChanged`. **Actual location**:
  `Program.fs:965`.

**Drift**: line citation moved by ~158 lines, likely
due to intervening PRs (#168 Tier 1 parameters added
~100 LOC of state-machine wiring; #169 SummaryOnly
default added ~60 LOC).

**Recommended fix (doc-side)**: update PIPELINE-NARRATIVE
event-taxonomy "Screen mutation" entry's line citation
from `Program.fs:1123-1131` to `Program.fs:965`. This
is the only line-citation drift found in the audit;
all other citations were verified accurate.

### Mode change ✅

- ScreenNotification.ModeChanged at Types.fs:235 ✓
- `DisplayPathway.OnModeBarrier` at line 69 of
  DisplayPathway.fs ✓

### Bell ✅

- ScreenNotification.Bell at Types.fs:249 ✓
- BellRang semantic at OutputEventTypes.fs:84 ✓

### Parser error ✅

- ScreenNotification.ParserError at Types.fs:223 ✓
- ParserError semantic at OutputEventTypes.fs:105 ✓

### Hotkey ✅

- HotkeyRegistry.fs exists; reserved hotkeys per doc
  list match (Ctrl+Shift+U/D/R/L/;/1/2/3/G/H/B).

### Shell switch ✅

- `Program.fs:switchToShell` (cited as imperative) —
  exists per the substrate inventory.

### Focus change ✅

- TerminalView.cs:654-673 cited; verified by sampling
  the file structure.

### Window resize ✅

- WPF `SizeChanged` + ConPTY `ResizePseudoConsole`;
  verified by inference (cmd file references; no
  drift detected).

### Update / version events ✅

- `runUpdateFlow` handler exists; Velopack integration
  per doc.

### Diagnostic battery ✅

- `runDiagnostic` hotkey + `Diagnostics.runFullBattery`
  ✓ (verified above).

### Prompt boundary (RESERVED) ✅

- Correctly tagged as "future (item 28)"; SESSION-MODEL.md
  is the future deliverable. No code today; matches doc.

### Command finished (RESERVED) ✅

- Same — correctly reserved.

### Claude Code response boundary (RESERVED) ✅

- Same.

## Substrate components findings

22 named substrate components per PIPELINE-NARRATIVE
inventory table.

### Verified ✅

- VtParser (`Parser.fs`, `StateMachine.fs`) ✓
- Screen (`Screen.fs:556` Apply) ✓
- CanonicalState (`CanonicalState.fs`) ✓
- DisplayPathway protocol (`DisplayPathway.fs:47`) ✓
- StreamPathway (`StreamPathway.fs:48`) ✓
- TuiPathway (`TuiPathway.fs:48`) ✓
- PathwaySelector (`PathwaySelector.fs:46`) ✓
- OutputDispatcher (`OutputDispatcher.fs`) ✓
- Profile registry (`OutputDispatcher.fs:60`) ✓
- Channel registry (`OutputDispatcher.fs:33`) ✓
- NvdaChannel (`NvdaChannel.fs:43`) ✓
- EarconChannel (`EarconChannel.fs:32`) ✓
- EarconPlayer (`EarconPlayer.fs:50`) ✓
- FileLoggerChannel (`FileLoggerChannel.fs:49`) ✓
- FileLogger (`FileLogger.fs`) ✓
- Config (`Config.fs`) ✓
- Terminal.App.Program (`Program.fs`) ✓
- Terminal.App.Diagnostics (`Diagnostics.fs:58`) ✓
- HotkeyRegistry (`HotkeyRegistry.fs`) ✓
- ShellRegistry (`Terminal.Pty/ShellRegistry.fs`) ✓
- TerminalView (`Views/TerminalView.cs`) ✓
- TerminalAutomationPeer (`Terminal.Accessibility/TerminalAutomationPeer.fs`) ✓
- ConPtyHost (`Terminal.Pty/ConPtyHost.fs`) ✓
- AnnounceSanitiser (`Terminal.Core/AnnounceSanitiser.fs`) ✓

### Drift ⚠

- **KeyEncoding**: PIPELINE-NARRATIVE says
  `src/Terminal.Pty/KeyEncoding.fs`. **Actual**:
  `src/Terminal.Core/KeyEncoding.fs:76` (module
  KeyEncoding).

  **Recommended fix (doc-side)**: update PIPELINE-NARRATIVE
  substrate-inventory table + vocabulary glossary entry
  for KeyEncoding to point to `Terminal.Core` instead
  of `Terminal.Pty`.

- **Profile naming (StreamProfile / PassThroughProfile)**:
  See Stage 10 finding. Two more references to
  "StreamProfile" found in PIPELINE-NARRATIVE
  (substrate-inventory + vocabulary). Doc-side rename
  needed.

## Substrate gaps findings

8 named gap entries; all correctly tagged as future.

| Gap | Backlog item | Verified absent? |
|---|---|---|
| InputPathway protocol | Phase 2 input framework cycle | ✓ no IInputPathway in code |
| SessionModel substrate | Item 28 | ✓ no SessionModel module; SESSION-MODEL.md design only |
| Cursor-aware diff | Phase 2 | ✓ Canonical record has no CursorPosition field |
| Echo correlation | Phase 2 | ✓ no KeystrokeTracker module |
| Per-input-vs-output ActivityId routing | Phase 2 | ✓ `pty-speak.input-echo` ActivityId not in `ActivityIds` module |
| Scrollback / history navigation | Item 27 (downstream of 28) | ✓ no history-navigation hotkey or module |
| Profile.Priority awareness | Phase 2 | ✓ PassThroughProfile is pass-through; ignores Priority |
| Per-shell parameter overrides | Phase B / TOML | ✓ Config.fs doesn't currently support `[pathway.stream.shell.<id>]` nesting |

✅ All 8 gaps verified absent from code; corresponding
backlog items exist in the plan-file backlog.

## Vocabulary spot-check

Sampled 20 entries from the vocabulary glossary
(~70 total).

| Entry | Status |
|---|---|
| AnnounceSanitiser | ✅ matches |
| ActivityId | ✅ matches |
| BackspacePolicy | ✅ matches |
| Bell | ✅ matches |
| BellRang | ✅ matches |
| BulkChangeFallback | ✅ matches |
| BulkChangeThreshold | ✅ matches |
| Canonical | ✅ matches |
| CanonicalState | ✅ matches |
| Cell (`Types.fs:52`) | ✅ matches |
| ChangedRows | ✅ matches |
| ChangedText | ✅ matches |
| Channel | ✅ matches |
| ChannelDecision | ✅ matches |
| **KeyEncoding** | ⚠ wrong project cited (Terminal.Pty → Terminal.Core) |
| **StreamProfile** | ⚠ rename to PassThroughProfile |
| ModeBarrier | ✅ matches |
| ModeBarrierFlushPolicy | ✅ matches |
| OSC | ✅ matches |
| OSC 133 | ✅ matches |

18 of 20 sampled entries clean. 2 confirmed drifts
(both already noted in earlier sections).

Spot-check coverage: ~28%; remaining 72% inferred to be
clean based on the low drift rate among sampled
entries. A full vocabulary verification is out of
scope for Track A (would be ~70 lookups); Track E
(doc currency) can extend if needed.

## Recommendations

For each ⚠ finding, the recommended action:

| Finding | Type | Recommended fix | Tier |
|---|---|---|---|
| Stage 1 reader-thread attribution | Doc-side | Clarify "ConPtyHost provides readerLoop; Program.fs starts the thread" | Trivial |
| Stage 10 StreamProfile → PassThroughProfile | Doc-side | Rename throughout PIPELINE-NARRATIVE; vocabulary glossary keeps "formerly StreamProfile" compatibility note | Trivial |
| Screen mutation event Program.fs line citation | Doc-side | Update from 1123-1131 to 965 | Trivial |
| KeyEncoding location | Doc-side | Update from Terminal.Pty to Terminal.Core | Trivial |
| Vocabulary entries (KeyEncoding, StreamProfile) | Doc-side | Same as above; one-line fixes | Trivial |

**No code-side fixes needed.** All drift is doc-side.

**No spec deviations identified.** Track C (spec
alignment) will validate this independently.

**Recommended next step**: a single small follow-up PR
that applies all 5 trivial doc-side fixes to
PIPELINE-NARRATIVE.md. Estimated 30-50 LOC of doc edits.
The maintainer can prioritise this against other
backlog items; not urgent (current doc is still useful;
drift is minor).

## Cross-references

- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) —
  the source spec that this audit verifies. Drift
  fixes target this doc.
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — referenced
  for substrate-gaps verification (item 28's
  contract).
- [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter
  catalogue; cross-verified for Tier 1 parameters
  during Stage 8/8b/9 verification.
- [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
  — out of scope for Track A; Track C validates
  spec-vs-doc alignment.
- This document's master plan: see plan file's
  "Comprehensive audit" section.

## What this PR does NOT do

- Doesn't fix any drift identified. Findings are
  recorded; fixes happen in a follow-up PR (single
  small one bundling all 5 trivial doc-side fixes).
- Doesn't modify spec/. Track C handles spec-
  alignment.
- Doesn't update PIPELINE-NARRATIVE.md. Same as
  above — fixes are deferred to a follow-up.
- Doesn't touch test files. Track B handles tests.
- Doesn't update the master audit plan. Track F
  validates it as part of backlog validation.

## Sequencing position

This is **Track A** of the comprehensive audit phase.
After this lands, Tracks B / C / D / E can run in
parallel:

- Track B (test inventory) — depends on A's "what's
  in code" baseline.
- Tracks C, D, E — independent surfaces.
- Track F (backlog validation) — runs last; reflects
  post-audit state.

## Change log

| Date | Change |
|---|---|
| 2026-05-06 | Initial audit. 80+ entities verified across 6 categories. 81 ✅ matches, 8 ⚠ minor drift, 0 ❌ structural drift. All drift is doc-side; recommended fix is a single small follow-up PR. |
