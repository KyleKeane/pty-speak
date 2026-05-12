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

Two cycles since the predecessor plan:

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

The architectural assertion from the prior plan — substrate vs.
channel boundary, recorded as
[ADR 0001](adr/0001-substrate-channel-dichotomy.md) — survives
unchanged. **Only the substrate implementation flipped.**

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

**NVDA matrix walk rows 45c-1 through 45c-6** in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md):

1. **45c-1** — cmd + `echo hi` mid-edit (regression pin from PR #268)
2. **45c-2** — cmd + `dir` long output through NVDA chunking
3. **45c-3** — cmd + alt-screen TUI (`vim`, `more <largefile>`)
4. **45c-4** — Claude shell + long streaming response
5. **45c-5** — `Ctrl+Shift+D` bundle contains
   `--- CONTENT HISTORY ---` section
6. **45c-6** — OSC 133 boundary detection via
   `ContentHistory.sliceText`

User-visible behaviour should be unchanged in the happy path —
Cycle 45 dogfood confirmed the pathway calls weren't on the
announce path. The matrix exists to catch any regression.

## 4. Candidate next cycles

None of these block each other. Pick by maintainer priority.

### Short-term (post-validation)

| Cycle | Scope | LOC | Depends on |
|---|---|---|---|
| **45g** | `ShellPolicy` consolidation: migrate `HeuristicPromptDetector` + `SelectionDetector`'s per-shell gates onto the `ShellPolicy` table (pure refactor) | ~200 | nothing |
| **45d** | Interactive review-cursor focus: `Enter` on a focused row dispatches per type (copy prompt, select item, etc.) | ~150 | nothing |
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
