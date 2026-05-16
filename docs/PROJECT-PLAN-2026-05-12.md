# Project plan — 2026-05-12 (post-Cycle-45c)

> **Snapshot**: 2026-05-12
> **Status**: active strategic plan. Succeeds
> [`archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md`](archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md)
> per the Track E E5 dated-snapshot discipline (Option B
> continued).
> **Companion docs**:
> - [`SESSION-HANDOFF.md`](SESSION-HANDOFF.md) — short brief for
>   the next session
> - [`CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) —
>   substrate/channel dichotomy (architectural framing)
> - [`ARCHITECTURE.md`](ARCHITECTURE.md) — current module map
> - [`CHANGELOG.md`](../CHANGELOG.md) — cycle-by-cycle trail

## 1. What changed

Two-and-a-half cycles since the predecessor plan:

**Cycle 45 (PRs #263–#270)** introduced the
`ContentHistory` + `SpeechCursor` substrate. Live announce path
since 2026-05-12.

**Cycle 45c (PRs #274–#278)** retired the pre-Cycle-45 substrate
pipeline (`StreamPathway` / `LinearTextStream` /
`DisplayPathway` / `TuiPathway` / `PathwaySelector`). Net change:
**~5,000+ LOC removed**, including the `[pathway.stream]` TOML
block, ~22 test cases, the `--- LINEAR STREAM ---` diagnostic
section (renamed to `--- CONTENT HISTORY ---`), and the entire
substrate-mode dispatch.

**Cycle 46 (PRs #287, #288, #289, #290, #291; 2026-05-12 →
2026-05-13)** — full four-PR sequence shipped:

- #287 PR-A drafted [ADR 0002](adr/0002-uia-textedit-caret-output.md).
- #288 PR-B substrate-swapped the UIA Text pattern from the
  screen grid to `ContentHistory.tailText` (256 KB cap) +
  flipped `AutomationControlType` from `Document` to `Edit`.
- #289 (handoff) shipped the next-steps + plan updates.
- #290 PR-C added
  `TerminalAutomationPeer.RaiseCaretMovedToTail()` and rewired
  the boundary handler to fire it on tuple finalise (replaces
  the `Announce(text, ActivityIds.output, MostRecent)` call).
- #291 PR-D rewired `speechCursorAnnounce` to delegate to the
  same caret helper, and deleted the legacy screen-grid types
  (`TerminalTextProvider` / `TerminalTextRange` / `SnapshotText`)
  + the `WordBoundaryTests` they backed. Net ~-800 LOC.

The Open Questions §1–§5 are resolved in the ADR Status
notes (2026-05-13).

The architectural assertion from the prior plan — substrate vs.
channel boundary, recorded as
[ADR 0001](adr/0001-substrate-channel-dichotomy.md) — survives
unchanged. **The substrate implementation flipped** in Cycle 45
(grid → ContentHistory) and **the channel for terminal output
flipped** in Cycle 46 (`RaiseNotificationEvent` → UIA caret).

## 2. Live substrate today

```
ConPtyHost ──bytes──▶ Parser ──events──▶ Screen ──snapshot──▶ CanonicalState
                                                                    │
                                                                    ▼
                                  ContentHistory ◀──events / markers
                                         │
                                         ▼
                                   SpeechCursor (auto-drive + manual nav)
                                         │
                                         ▼
                                  OutputDispatcher ──▶ Profiles ──▶ Channels
                                                                       │
                                                              ┌────────┼────────┐
                                                              ▼        ▼        ▼
                                                            NVDA   FileLogger  Earcons
```

Key primitives:

- **`ContentHistory`** (`src/Terminal.Core/ContentHistory.fs`) —
  append-only typed-entry log per shell session. Entries:
  `TextSpan`, `Newline`, `Overwrite`, `Marker`, `Spinner`.
  Three query helpers shipped in Cycle 45c:
  `tryLatestMarker`, `sliceText`, `tailText`.
- **`SpeechCursor`** (`src/Terminal.Core/SpeechCursor.fs`) —
  announce-and-navigate primitive. AutoDrive emits new entries
  to NVDA; Manual mode (`Ctrl+Shift+Up/Down` / Home / End)
  lets the user navigate the session history.
- **`SessionModel`** still owns the `(prompt, command, output,
  exit-code)` tuple model. Its `applyAndCaptureWithContentHistory`
  reads `ContentHistory` markers + `sliceText` to extract
  `(CommandText, OutputText)` at tuple-seal time.
- **`ShellPolicy`** holds per-shell verbosity / prompt-path /
  selection-detector toggles (Cycle 45f).

## 3. Immediate validation gate

**NVDA matrix Cycle 46-1** in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) —
all four Cycle 46 PRs are on the live UIA path as of
2026-05-13 (`9bfdd48`). Required walk before tagging the
next release:

- Focus the terminal surface → NVDA announces "edit" (was
  "document" pre-Cycle-46).
- `Insert+Down` (read all) → NVDA reads the `ContentHistory`
  tail (last 256 KB).
- `Up/Down` arrow navigation → moves line-by-line through the
  materialised tail.
- `Ctrl+End` / `Ctrl+Home` → jumps to end / start of the tail.
- **Typing into the prompt interrupts an in-progress read**
  (NVDA's "Speech interrupt for typed character" — the
  original Cycle 46 motivating issue).
- **`Alt` to open the menu interrupts** an in-progress read.
- Non-output notification paths (`Ctrl+Shift+D` snapshot,
  `Ctrl+Shift+S` session-log, `Ctrl+Shift+H` health,
  manual `Ctrl+Shift+Up/Down/End` review-cursor) still fire
  as `Announce`-based notifications.

**Carried over from Cycle 45c** (assumed rolled forward into
46-PRB-1 unless the matrix file says otherwise): rows 45c-1
through 45c-6 covering cmd `echo hi` mid-edit, cmd `dir` long
output, alt-screen TUI, Claude streaming, `Ctrl+Shift+D`
bundle section, OSC 133 boundaries. The substrate change
shouldn't have altered any of these (they exercise the
substrate's content semantics, not the UIA channel), but
confirm in passing.

## 4. Candidate next cycles

**Cycle 48 (ShellInteraction state machine) and Cycle 49
(speech-narration refinements) both shipped.** Cycle 48
introduced the semantic state machine per
[`docs/adr/0003-shell-interaction-state-machine.md`](adr/0003-shell-interaction-state-machine.md);
Cycle 49 (eight PRs, 2026-05-14) refined the narration on top:
SpeechCursor blank-collapse, post-Enter delta announce,
review-cursor refresh via UIA TextChanged, prompts-visible-in-
manual-nav, sub-prompt last-line announce, line-count
post-Enter delta slicing (superseded Cycle 49 PR-B's text-
prefix-trim), tuple-final prefix-trim removed (silenced
duplicate commands), test-01 reshape (Line 1 of 3 explicit
labelling), and Up/Down history-recall announce via a new
`pty-speak.input-assistant` activity ID. Maintainer dogfood
2026-05-14 verified each defect resolved; preview.123 build
is the post-cycle release-build cut.

None of the candidates below block any active cycle; they
are independent and pick-by-priority.

### Short-term

| Cycle | Scope | LOC | Depends on |
|---|---|---|---|
| **45g** | `ShellPolicy` consolidation: migrate `HeuristicPromptDetector` + `SelectionDetector`'s per-shell gates onto the `ShellPolicy` table (pure refactor) | ~200 | nothing |
| **45d** | Interactive review-cursor focus: `Enter` on a focused row dispatches per type (copy prompt, select item, etc.) | ~150 | nothing |
| **`EntrySource.DraftInputRecall`** | Tag history-recall draft rewrites in `ContentHistory` (deferred Cycle 49 E3). PR-I solved the audible problem with a simpler screen-read approach; this is a substrate refinement so the review cursor can distinguish recalled drafts from typed input | ~50 | nothing |
| **Spinner / red-tone fixes** | Rewrite the Cycle 29b spinner-storm + false-positive red-tone fixes against ContentHistory (originals lived in StreamPathway) | ~100–150 each | nothing |

### Medium-term (substrate maturity)

| Cycle | Scope | LOC | Depends on |
|---|---|---|---|
| **Semantic labels** | Add `Source: EntrySource` (`TypedInput` / `CmdOutput` / `Marker`) to every `ContentHistory.Entry`. Foundational for chunk-level nav + "inject past input" + future automation | ~150 | nothing; foundational |
| **Chunk-level nav** | `Ctrl+Shift+Up/Down` jumps between output chunks instead of individual entries. NVDA announce "Output chunk 2 of 5" | ~100 | semantic labels |
| **Coalescer rename** | `Coalescer` no longer coalesces announce events (kept only for `hashRow` / `hashRowContent` / `renderRows` helpers used by `CanonicalState`). Rename to clarify its current scope | ~25 sites | nothing; pure rename |
| **UIA caret + read-current-line** | Wire `ITextRangeProvider.GetCaretRange` + fire `TextSelectionChangedEvent` on Screen cursor moves. Highest-leverage UIA improvement per backlog — fixes nav-echo, read-current-line, AND lets NVDA's keyboard echo work natively | substantial | scope first |

### Phase 2 (deferred — input framework)

Echo correlation, cursor-aware editing, `InputPathway` protocol,
`ReplPathway`, command-start earcon. These have been on the
backlog since the original framework cycles were sequenced.
The post-Cycle-45c substrate is ready to receive them, but
none are urgent until validation lands.

### Phase 3 (deferred — cross-cutting)

Screen-buffer runtime resize. AI summarisation + semantic
segmentation + spatial audio. `pty-speak-replay` CLI
(requires canonical-state serialization). Cross-platform port
(Avalonia) if/when scoped.

## 5. Out-of-scope / non-cycles

- **NVDA matrix walk itself** — that's the validation gate, not
  a code cycle.
- **Documentation refresh** — Cycle 45c shipped doc updates; the
  next-cycle plan inherits them.
- **`[pathway.stream]` TOML migration helper** — the parser
  silently ignores unknown sections, so users with stale
  configs see no error. No migration tool needed.
- **Checkpoint tag for Cycle 45c** — cleanup cycles don't earn
  baseline tags per
  [`CHECKPOINTS.md`](CHECKPOINTS.md).

## 6. Open follow-ups (cross-cutting; not scoped as a cycle)

- **Stable-baseline tag pushes**. The sandbox can't push tags;
  see
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md)
  "Pending checkpoint tags" table.
- **Diagnostic bundle compatibility note**. Old bundles in
  `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\` carry the
  `--- LINEAR STREAM ---` header; triage scripts grepping for
  that string will break. Acceptable per the Cycle 45c plan.
- **Doc deep-rewrite for `CORE-ABSTRACTION-BOUNDARY.md`**. The
  doc has a Cycle 45c header note; the prose body still
  describes the pathway pipeline in present tense. A future
  cycle should rewrite the body in terms of
  ContentHistory/SpeechCursor.

## 7. Change log

| Date | Cycle | What |
|---|---|---|
| 2026-05-12 | Cycle 45c (post-merge) | This plan supersedes `PROJECT-PLAN-2026-05-09.md`. Captures post-Cycle-45c state: ContentHistory/SpeechCursor is sole substrate; old pathway pipeline deleted via PRs #274–#278. NVDA matrix walk 45c-1 → 45c-6 is the validation gate. Roadmap reset around the new substrate. |
| 2026-05-13 | Cycle 46 PR-A + PR-B (post-merge) | Added §"Cycle 46" to §1 + roadmap. ADR 0002 drafted (PR #287, 2026-05-12) and accepted (2026-05-13 with all five Open Questions resolved). PR #288 swapped the UIA `ITextProvider` from screen grid to `ContentHistory`, flipped `ControlType` Document→Edit. PR-C / PR-D scoped in [`CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md). Validation gate renamed from 45c-1→6 to 46-PRB-1 (subsumes the carry-over). The roadmap kept the 45g / 45d / semantic-labels / spinner / Coalescer cycles as a parallel track. |
| 2026-05-13 | Cycle 46 PR-C + PR-D (post-merge) | PRs #290 + #291 closed Cycle 46. PR-C added `RaiseCaretMovedToTail` and replaced the tuple-finalise `Announce` call. PR-D rewired `speechCursorAnnounce` to delegate to the same helper and deleted the legacy screen-grid `TerminalTextProvider` / `TerminalTextRange` / `SnapshotText` (~680 LOC) + `WordBoundaryTests.fs`. The §1 summary collapsed PR-A through PR-D into one Cycle-46 entry. The §4 "Primary track" subsection was removed (Cycle 46 done; nothing else is in primary track). Validation gate consolidated to NVDA matrix Cycle 46-1 in `ACCESSIBILITY-TESTING.md`. |
| 2026-05-13 | Cycle 47 follow-up batch (PRs #299–#305, preview.114 → preview.117) | Shipped marker-label parallelism, sanitiseForBundle newlines, CommandFinished synthesis, typing-window UIA gate, prefix-trim, mid-eval earcon, menu mnemonic fixes, cursor-row synthetic newlines, idle-flush typing gate. preview.117 dogfood found the gates don't compose: typed chars still announced, set/p replays, review cursor drifts. Diagnosis: announce path is byte-stream-driven, not semantic. |
| 2026-05-13 | Cycle 48 PR-A (ADR 0003 draft) | Active cycle. Six-PR sequence specified in [`docs/adr/0003-shell-interaction-state-machine.md`](adr/0003-shell-interaction-state-machine.md): ShellInteraction state machine (`Composing` / `Executing`) as the semantic layer above the substrate; ContentHistory.Entry gains `Source : EntrySource` per §9.5 resolution; UserInputBuffer for canonical "what the user typed"; sub-prompt detection via "idle and last-byte-not-LF"; announce routing collapsed from `tuple-final + idle-flush + prefix-trim` to one transition-driven path; SpeechCursor filters `UserInputEcho` entries in both AutoDrive AND Manual nav per §9.6 resolution. Open questions §9.1 → §9.6 resolved 2026-05-13 in-chat; resolutions recorded in the ADR itself. Validation: maintainer reads + approves ADR before PR-B writes any code. |
| 2026-05-13 | Cycle 48 PR-B → PR-F (closure) | Six-PR Cycle 48 shipped: PR-B (#307, `3c2a372`) ShellInteraction state machine observe-only; PR-C (#308, `5d34369`) `ContentHistory.Entry.Source` substrate change + per-source counts in diagnostic bundle Stats line; PR-D (#309, `96b6e56`) UserInputBuffer byte-stream wiring via writePtyBytes wrapper; PR-E (#310, `459a0b2`) sub-prompt announce via state machine + SpeechCursor filter for UserInputEcho + idle-flush announce body retired; PR-F (this PR) docs sweep. ADR 0003 status flipped to **Accepted / Implemented**. Cycle 47 dead code (tupleFinaliseAnnounce prefix-trim, PR #300 materialiser typing-window gate) preserved as defence-in-depth pending preview.118 dogfood; cleanup is staged for a follow-up PR. Validation: NVDA matrix Cycle 48-B1 → 48-E8 walks the CMD test corpus against the new audible behaviour. |
| 2026-05-14 | Cycle 49 PR-A → PR-I + PR-Z (closure) | Eight-PR Cycle 49 shipped on top of Cycle 48's state machine, refining speech narration and review-cursor behaviour from maintainer preview.118 → preview.122 dogfood. PR-A (#313, `8242e56`) SpeechCursor manual-nav blank-collapse. PR-B (#314, `36acad6`) post-Enter delta announce via screen-rendered preamble at `EnterPressed` time (text-prefix-trim approach later superseded by PR-F's line-count slice). PR-C (#315, `1579ff7`) `TerminalAutomationPeer.RaiseTextChanged` after every Announce site to invalidate NVDA's review-cursor `DocumentRange` cache. PR-D (#316, `4b315ae`) `SpeechCursor.renderEntryForManualNav` makes `PromptStart` markers with payload navigable regardless of per-shell `PromptPath` policy. PR-F (#317, `2f8a05a`) two refinements: sub-prompt announce narrates only the last non-empty line of the accumulator; post-Enter delta uses line-count slicing for robustness to per-row content drift. PR-G (#318, `335dbb9`) removed tuple-final `lastAnnouncedText` prefix-trim relic (silenced duplicate commands; "unpredictable speech" symptom). PR-H (#319, `b9e250f`) reshaped `test-01-echo.cmd` with explicit `Line 1 of 3` labelling. PR-I (#320, `904051c`) Up/Down arrow history-recall announce via new `pty-speak.input-assistant` activity ID; 100ms debounced screen-read + prompt-path strip. PR-Z (this PR) docs sweep. Cycle 49 plan archived as historical. Maintainer-reported defects all resolved in-cycle: test-01 missing line (PR-H), sub-prompt content (PR-F), post-Enter still full-read (PR-F), duplicate-command silence (PR-G), menu narration regression (downstream of PR-G), history recall (PR-I). Deferred to a future cycle: `EntrySource.DraftInputRecall` (Cycle 49 E3) — ContentHistory substrate refinement for tagging recalled drafts. Validation: post-PR-I release-build dogfood. |
| 2026-05-16 | Cycle 52 R1–R4 (closure checkpoint) | Cycle 50 (CI/branch hygiene) + Cycle 51 (IOCell pivot, ADR 0004, #337–#344) shipped between this plan's last entry and Cycle 52; per-PR detail in `CHANGELOG.md` / `git log`. **Cycle 52 = the three-layer re-foundation** (ADR 0005 OSC-133 mechanism folded into ADR 0006's R0–R7). R1–R4 complete & CI-green: **R1** (#347–#352) extracted the `Terminal.Shell` assembly + `SessionHost` + `CmdAdapter` (VtEvent seam), behaviour-identical, maintainer-dogfood-validated. **R2** (#353) cmd OSC-133 via Option B (command-line `prompt`, adapter-owned); ADR 0005 §3's "implicit C" realised consumer-side (`extractIOCell` PromptStart+CommandStart arm) per maintainer decision. **R3a** (#354) OSC-precedence latch (mute heuristic once OSC seen). **R3b** (#355) retired the PR-X/Y/AB announce-path compensations — tuple-final announce now speaks the R2-sealed IOCell `OutputText`; **KI-R2-1 structurally fixed**; PR-AA/AC banner preserved. **R4a** (#356) `HeuristicPromptDetector` namespace Core→Shell. **R4b** (#357) `portability-lint` CI now *enforces* no-P/Invoke + no-`Terminal.Shell`-dependency in `Terminal.Core` — the boundary is structural, not disciplinary. **Checkpoint (maintainer 2026-05-16):** R1–R4 is the milestone; a single consolidated **R1–R4 foundation dogfood** (NVDA matrix 52-1) gates R5 (PowerShell adapter, net-new). R5–R7 pending. Deferred backlog unchanged (`EntrySource.DraftInputRecall`; 45g `ShellPolicy`; backspace-to-empty path-announce = pre-existing R2-backlog). Validation: the 52-1 foundation dogfood on post-`cbf8d48` `main`. |
