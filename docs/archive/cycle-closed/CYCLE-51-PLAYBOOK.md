# Cycle 51 execution playbook — IOCell pivot (HISTORICAL)

> **HISTORICAL — Cycle 51 shipped 2026-05-14 (PRs #337–#344).**
> The IOCell pivot landed; its announce compensations were
> later retired by Cycle 52 R3b. This is no longer a
> start-here doc — for current work see
> [`CYCLE-52-R5-PLAYBOOK.md`](../CYCLE-52-R5-PLAYBOOK.md).
> Kept for the planning-vs-shipped delta. Original companions
> (paths are archive-relative):
> [`adr/0004-iocell-model-for-shell-interaction.md`](../adr/0004-iocell-model-for-shell-interaction.md)
> (the canonical decision record) and
> [`SESSION-HANDOFF.md`](../SESSION-HANDOFF.md).
>
> This doc was written 2026-05-15 by the session that shipped
> PR-V, deliberately as a crash-safe handoff. It is exhaustive
> on purpose. When Cycle 51 ships, PR-Z marks this doc
> HISTORICAL (banner at top + move to `docs/archive/`).

## 0. STOP — three things that will waste your time if you skip them

1. **The classification-by-`EntrySource` thesis is REJECTED.**
   The original ADR draft (and the Phase 0 spike) proposed
   walking `ContentHistory` entries and routing each to
   command-vs-output by its `EntrySource` tag
   (`UserInputEcho` → command, `CmdOutput` → output). **This
   does not work.** Section 4 has the bundle proof. Do not
   re-introduce it. The command/output split is the
   **first-newline rule on a marker-bounded slice** — the
   heuristic the existing extractor already ships.

2. **The spike branch `spike/cycle51-iocell-substrate-exploration`
   (`de8bf81`) is a GRAVEYARD, not a starting point.** It
   contains the rejected classifier (`SessionModel.extractIOCell`
   spike version), the `IOCellSpikeComparison` facade, the
   `ContentHistory.entriesAfter` helper, and a `Cycle 51 spike
   PR-V0.` diagnostic log in `Program.fs`. **Do NOT cherry-pick
   anything from it.** It is pushed only so the ADR's
   provenance reference (`de8bf81`) resolves. Work from clean
   `main`.

3. **There is no `dotnet` in the sandbox.** You cannot
   `dotnet build` / `dotnet test` locally. Every compile error
   is a multi-minute CI round-trip. Read each `.fs` edit twice.
   The F# 9 gotchas in §7 have bitten this codebase repeatedly;
   internalize them before editing F#.

## 1. Where we are exactly (2026-05-15)

- **`main` HEAD = `6b3ff6a`** — PR-V (ADR 0004) merged via
  PR #332 (docs-only fast-merge on the markdown link check).
- **Local `main` is up to date; working tree clean.**
- **Branches**: the merged PR-V branch was deleted local +
  remote. The spike branch is still on remote (graveyard;
  leave it — PR-Z dispositions it).
- **No code has changed on `main`** since `ae33bc9` except
  the ADR docs. `SessionModel.fs`, `Program.fs`,
  `ContentHistory.fs` on `main` are exactly the
  Cycle-49/50 state. All line anchors in this doc are against
  `main` at `6b3ff6a`.
- **Cycle 51 progress**: Phase 0 (spike) ✅ done (superseded
  by bundle analysis). PR-V ✅ merged. **PR-W is next.**
  Then PR-W2 → PR-X → PR-Y → PR-Z.

## 2. The north star (maintainer's verbatim intent)

Keep these in view — they are why the pivot exists, not just
what it does:

- *"the core infrastructure is not robust enough and
  extensible and modular enough to handle other shells"*
- *"we need to be just handling The Computational in and out
  in a similar way that a computational notebook such as
  Wolfram does"*
- *"I want to be able to have an abstraction layer on top of
  all of this that will allow us to route each one of these
  different events that is presently going into NVDA through
  different channels, such as spatial audio and custom speech
  synthesis"*
- *"We have a ton of wonderful infrastructure in place to do
  CI, build releases, diagnostics, and user testing scripts.
  How do we pivot and use all of this existing tooling, but
  get rid of the screen paradigm"*

The pivot's essence: **stop using screen-row coordinates as a
source of truth; the substrate (ContentHistory, Seq-keyed) is
already notebook-shaped; finish trusting it and delete the
screen-row code that fights it.** The maintainer is a
screen-reader (NVDA) user — see §7 for the no-GUI-walks rule.

## 3. The reading order before you touch code

1. This doc (§0–§10).
2. [`adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md)
   — the canonical decision record. Especially §"Context →
   What the substrate reliably provides", §"Decision 3",
   §"Status notes" (the draft→revised arc), §"Migration plan"
   (PR-W / PR-X are the load-bearing ones).
3. [`../CLAUDE.md`](../CLAUDE.md) — runtime rules (CI-log
   asking, no-GUI-walks, diagnostic recipes, docs-only
   fast-merge, poll-CI-every-60s).
4. [`../CONTRIBUTING.md`](../CONTRIBUTING.md) §"F# gotchas
   learned in practice" — before any `.fs` edit.
5. The four ADRs 0001–0004 if you don't already hold the
   substrate/channel/interaction/IOCell framing.

## 4. The bundle evidence (why the thesis was rejected)

The maintainer's preview.135 dogfood bundle (2026-05-14,
4-run test-04 sequence) carried a `--- ENTRIES ---` per-entry
ContentHistory dump. Two findings, both load-bearing for the
migration:

### 4a. `EntrySource` is too coarse for command/output split

From the dump (run 1 of test-04):

| Seq | Source | Text | Truth |
|----|--------|------|-------|
| 6  | UserInputEcho | `"…test-04-yes-no…."` (typed cmd) | command ✓ |
| 8  | CmdOutput | `=== PTYSPEAK-TEST-START …` | output ✓ |
| 12 | CmdOutput | `Continue? [Y,N]?` | output ✓ |
| 13 | **UserInputEcho** | `N` (the keypress) | ambiguous |
| 15 | **UserInputEcho** | `You chose No.` | **output** ✗ |
| 17 | **UserInputEcho** | `=== PTYSPEAK-TEST-END …` | **output** ✗ |

Root cause: heuristic-only cmd emits **no CommandStart
marker**. `ShellInteraction` therefore stays `Composing`
almost the whole session, and the `EntrySource` resolver
returns `UserInputEcho` for every byte that arrives in
`Composing` — including cmd's own command OUTPUT. The dump's
own stats line confirmed it: `Sources: UserInputEcho=70
CmdOutput=32 CmdSubPrompt=0` — the `CmdSubPrompt` tag that
was supposed to bracket sub-prompt output **never fired
once** across the entire 4-run session. Even a plain
`echo PtySpeakDiagPlain` had its echoed output (`Seq 94
"PtySpeakDiagPlain"`) tagged `UserInputEcho`.

**Conclusion**: an `EntrySource`-classifying extractor would
push most of every cell's OUTPUT into the COMMAND field.
Thesis dead. Use the first-newline split (cmd echoes the
typed command, then `\r\n`, then output) on a marker-bounded
slice — exactly what `extractContentFromContentHistory`'s
heuristic-only arm already does and ships today.

### 4b. The real haywire mechanism (what PR-X must fix)

Run 4 of test-04 (the command echo wrapped because the
script path exceeds 80 cols). Timeline from the FileLogger
slice:

```
HeuristicPromptDetector dirty-flag set … priorRow=29  (wrapped)
PR-I history-recall announce. RowLen=10 DraftLen=10   ← only "es-no.cmd"" announced
UserInputBuffer captured on Enter: … Text=y[A
PR-K sub-prompt announce. Length=16 Source=accumulator-fallback
  (NO `PR-N sub-prompt screen-read range` log — screen-read path bailed)
  (NO `PR-U sub-prompt preamble line count seeded` log)
Tuple-final announce. Length=196                       ← WHOLE output
  (NO `Tuple-final trim` log)
```

Mechanism: command echo wrapped → `subPromptScreenReader`'s
screen-row math failed → it fell back to `accumulator-fallback`
(text correct) → **but `PR-U`'s preamble-line-count seed is
gated on `Source=screen`**, so it never fired →
`subPromptPreambleLineCount` stayed `0` → the tuple-final
trim (gated on line-count > 0) never activated → the full
196-char output was announced instead of the ~14-char
post-Enter delta ("You chose Yes."). That is the "things go
completely haywire" the maintainer reported.

**PR-X kills this by construction**: no screen-read, no
`Source=screen` gate, no line-count math. A Seq watermark
captured at EnterPressed + a Seq slice at tuple-final. See
§6.

## 5. PR-W — IOCell type + extraction simplification + schemaVersion 2

**Branch**: `claude/cycle51-pr-w-iocell-type-extraction`
(cut from fresh `main`; `git checkout main && git pull`
first per CLAUDE.md).

**Scope**: type rename + 2 new fields + promote the existing
heuristic extractor arm to sole + delete the row-walk +
formatter schemaVersion bump + SessionLogWriter swap +
test rewrite + SESSION-MODEL→IOCELL-SCHEMA doc. **No
EntrySource classifier. No spike helpers.**

### 5a. Type rename + new fields — `src/Terminal.Core/SessionModel.fs`

- **`SessionModel.fs:84`** `type SessionTuple =` — rename to
  `IOCell`. Use `Edit replace_all=true` on the token
  `SessionTuple` across the whole file (~150 sites incl.
  `ActiveSessionTuple` — keep that name distinct;
  rename to `ActiveIOCell`). Also sweep `Program.fs`,
  `SessionLogWriter.fs`, `Diagnostics.fs`,
  `tests/Tests.Unit/*` for `SessionTuple` references
  (`grep -rn 'SessionTuple' src tests`).
- Add the phase DU **above** the `IOCell` record:
  ```fsharp
  [<RequireQualifiedAccess>]
  type IOCellPhase =
      | Composing
      | Executing
      | AwaitingSubPromptResponse of subPromptText: string
      | Sealed
  ```
- Add two fields to the `IOCell` record (per ADR §"In-memory
  representation"): `CellSequence: int64` and
  `Phase: IOCellPhase`. **Identity rule**: assign `Id` and
  `CellSequence` at cell CREATION (the PromptStart-driven
  `apply` transition that opens a cell), not at seal. The
  active-cell constructor and `History`-seal path both live
  in this file — find them via `grep -n 'Id = \|History\b'`.
  `CellSequence` is monotonic per shell-session starting at
  `0L`; it resets on shell-switch (same place
  `ContentHistory.reset` is called — see Program.fs
  `switchToShell`).
- Every record-construction site of the old `SessionTuple`
  must add the two fields. The compiler will flag each
  (F# requires all record fields). Expect ~10–20 sites.

### 5b. Extraction simplification — DELETE row-walk, PROMOTE heuristic arm

- **`SessionModel.fs:794`** `let private
  extractContentFromContentHistory` — this function has two
  arms:
  - **`SessionModel.fs:805`** `| Some promptStart, Some
    outputStart ->` — the OSC-133 arm. **KEEP as-is.** It's
    Seq-based and correct; cmd just doesn't exercise it.
  - **`SessionModel.fs:816`** `| Some promptStart, None ->`
    — the heuristic-only arm (slice from PromptStart Seq to
    tail, trim trailing new-prompt text via `newPromptText`,
    split on first `\n`). **This is the canonical extractor.**
    Lines 850–875 are the proven logic.
  - **`SessionModel.fs:876`** `| _ -> None` — the
    no-PromptStart-Seq case. This is now the **drop-on-None**
    contract: return `None`, the cell does NOT finalize.
  Rename the whole function to `extractIOCell` (drop the
  `private` only if a test needs it; otherwise keep private).
- **`SessionModel.fs:347`** `let private extractContent` —
  the screen-row-walk fallback. **DELETE the entire
  function.**
- **`SessionModel.fs:441`** `let private finalizeAndEnqueue`
  — at **`SessionModel.fs:452-469`** the
  `commandText, outputText, extractionPath` block does
  `match fromLinear with Some -> … | None -> extractContent …`.
  **Delete the `None -> extractContent …` fallback.** When
  the thunk returns `None`, the cell drops:
  `finalizeAndEnqueue` should return `state, None` (no tuple
  enqueued) and log at Information
  `IOCell dropped: no PromptStart Seq in ContentHistory.`
  (per CLAUDE.md "new features ship with their diagnostic
  triggers"). The `extractionPath` string local can go away
  or collapse to a constant `"content-history"`.
- **`SessionModel.fs:437`** has a `TODO(Cycle 39): remove the
  linearOverride None branch + extractContent` — this PR IS
  that removal. Delete the TODO comment too.
- The `linearOverride` thunk plumbing in
  `applyAndCaptureWithContentHistory`
  (**`SessionModel.fs:888`**) stays structurally — it just
  never has a fallback now.

### 5c. Formatter — schemaVersion 1 → 2

- **`SessionModel.fs:1357`** `let formatTupleAsJsonl` →
  rename `formatIOCellAsJsonl`. Serialize the two new fields:
  `cellSequence` (int64 JSON number) and `phase` as a tagged
  DU object per the locked rule —
  `{"kind":"composing"}` / `{"kind":"executing"}` /
  `{"kind":"awaitingSubPromptResponse","subPromptText":"…"}` /
  `{"kind":"sealed"}`.
- **`SessionModel.fs:1137-1180`** is the locked-wire-format
  docstring block (the "Hand-rolled (no JSON library)" notes
  at line 1161). Update it: bump the `schemaVersion` line
  1 → 2; document the two new fields + the `phase` tagged
  shape; keep every other locked rule (escape policy,
  Map-as-array, lone-surrogate-throws) verbatim.
- **`SessionLogWriter.fs:231`** `let line =
  SessionModel.formatTupleAsJsonl sanitised` → swap to
  `formatIOCellAsJsonl`. Also the docstring at
  `SessionLogWriter.fs:12` + `:53` mention
  `formatTupleAsJsonl` — update.

### 5d. Tests + docs

- `tests/Tests.Unit/SessionModelTests.fs` — find cases that
  exercised the row-walk (`extractContent`) path; rewrite to
  assert the drop-on-None contract (no tuple finalised when
  no PromptStart Seq). Add `IOCellPhase` serialization unit
  tests (round-trip property tests are PR-W2, not here).
  Any new fixture file needs a `<Compile Include=…>` in
  `tests/Tests.Unit/Tests.Unit.fsproj`.
- `docs/SESSION-MODEL.md` → replace with
  `docs/IOCELL-SCHEMA.md` (same decade-stable wire-format
  discipline, schemaVersion 2, the two new fields). Leave a
  1-line `docs/SESSION-MODEL.md` pointer stub
  ("Renamed to IOCELL-SCHEMA.md in Cycle 51 PR-W"). Update
  any doc cross-links (`grep -rn 'SESSION-MODEL.md' docs`).
- CHANGELOG.md `[Unreleased]` — append-BOTTOM entry
  `### Cycle 51 PR-W (…)`.
- This is NOT docs-only → full Windows CI required.

## 6. PR-X — Seq-based sub-prompt narration (THE LOAD-BEARING PR)

**Branch**: `claude/cycle51-pr-x-seq-subprompt-narration`.

**This PR fixes the actual haywire (§4b).** Everything it
touches is in `src/Terminal.App/Program.fs`. The replacement
design is small; most of the work is DELETION.

### 6a. The replacement design (what to build)

1. **One mutable Seq watermark** replacing all the
   line-count / cursor-row state:
   `let mutable subPromptPreambleSeq : int64 = -1L`.
2. **At EnterPressed** (user submits the sub-prompt
   response): `subPromptPreambleSeq <-
   ContentHistory.latestSeq contentHistory`. Unconditional
   — NO `Source=screen` gate (that gate is the bug).
3. **Sub-prompt announce** (replaces `subPromptScreenReader`):
   `ContentHistory.sliceText contentHistory promptStartSeq
   (ContentHistory.latestSeq contentHistory)` where
   `promptStartSeq` is
   `(ContentHistory.tryLatestMarker contentHistory
   MarkerKind.PromptStart).Value.Seq`. Sanitise, announce.
   The existing `accumulator-fallback` already proved this
   text is correct — you're just making it the ONLY path.
   Do **not** classify by `EntrySource` (§4a).
4. **Tuple-final delta**: at cell-seal, announce
   `ContentHistory.sliceText contentHistory
   subPromptPreambleSeq System.Int64.MaxValue` minus the
   trailing next-prompt text (reuse the existing
   `newPromptText` tail-trim logic). No `OriginalLen` /
   `TrimmedLen` / `Strategy=line-count` / `SubPromptLines`
   arithmetic.

### 6b. DELETE these (all in `Program.fs`, anchors vs `main`)

- **`1027`** `let mutable subPromptPreambleLineCount : int`
- **`1028`** `let mutable capturePreambleForSubPromptResponse`
- **`1041`** `let mutable subPromptScreenReader`
- **`1074-1076`** `lastSubmittedCommandLength` /
  `lastSubmittedCommandEndCursorRow` /
  `lastSubmittedCommandPromptRow`
- **`1981`** `let computePromptCommandWrapRows`
- **`2024`** the `subPromptScreenReader <- …` assignment
  block (through ~`2050`, the `PR-N sub-prompt screen-read
  range` log at `2046`)
- **`1734`** `let screenRowsAnnounce = subPromptScreenReader ()`
  and the `1764-1829` PR-K/PR-U announce+seed block —
  replace with the §6a design (Seq-slice announce; the PR-K
  truncation-to-`OutputAnnounceCapChars` at `1765` stays as
  a cap on the new Seq-slice body).
- **`1864`** `capturePreambleForSubPromptResponse ()` call
  → replace with the watermark assignment (§6a item 2).
- **`2141`** the second `subPromptPreambleLineCount <-
  nonEmptyCount` site.
- **`2350`** `let tupleFinaliseAnnounce` + **`2529-2605`**
  the trim block (`Tuple-final trim` log at `2587`,
  `subPromptPreambleLineCount <- 0` resets at `2602`/`2605`)
  → replace with the §6a item-4 Seq-slice delta. Keep the
  `OutputAnnounceCapChars` (800) cap at `2579-2584`.
- **`4626`** `lastSubmittedCommandLength <-` and
  **`4658-4668`** `lastSubmittedCommandEndCursorRow <-` /
  `lastSubmittedCommandPromptRow <-` in the byte-write CR
  branch — delete the captures (the values are now unused).
- The PR-N/PR-O comment blocks around `1056-1076` and
  `1974-2023`.

### 6c. Diagnostic triggers (SHIP IN THIS PR — CLAUDE.md rule)

Replace the deleted `PR-N`/`PR-U`/`Tuple-final trim` logs
with, at Information (single-line, templated args, PR-X
prefix so a bundle grep finds them):
- `PR-X sub-prompt Seq-slice. PromptStartSeq={Seq}
  LatestSeq={Seq} AnnounceLen={Len}`
- `PR-X preamble watermark. PreambleSeq={Seq}`
- `PR-X tuple-final delta. PreambleSeq={Seq} DeltaLen={Len}
  TailTrimmed={Bool}`
- the announce body at Debug (matches the existing
  `PR-K sub-prompt announce body` precedent).

### 6d. Tests

Pure-F# unit tests over a `ContentHistory` fake (easy per
PR-S's `CI-TESTING-FUTURE.md` Win 1). **Add the regression
test for §4b**: a cell whose typed-command echo spans a wrap
boundary still produces the correct short post-Enter delta
(this is the exact bug the screen-row path had; the Seq
path must pass it).

## 7. PR-W2, PR-Y, PR-Z (summaries — full detail in ADR §"Migration plan")

### PR-W2 — `IOCell.parseFromJsonl` round-trip reader

- New `IOCell.parseFromJsonl : string -> Result<IOCell,
  ParseError>` near `formatIOCellAsJsonl` in
  `SessionModel.fs`. Hand-rolled (no JSON lib). Branch on
  `schemaVersion`; `Result.Error` for `1` with a clear
  message. **No UI surface** — internal + test only.
- `tests/Tests.Unit/IOCellRoundTripTests.fs` (new; add
  `<Compile Include=…>`): FsCheck
  `forall c, parseFromJsonl (formatIOCellAsJsonl c) = Ok c`.
  Generators cover the `IOCellPhase` + `BoundarySource` DUs,
  `option` fields, edge strings (lone-surrogate must throw
  on format per existing policy; reader rejects the same
  payload as malformed).

### PR-Y — Tier 1 channel routing

Announce-site inventory (done this session, vs `main`):
- **Tier 1 (~3-5 sites; refactor to
  `OutputDispatcher.dispatch`)**: `Program.fs:1777` (PR-K
  sub-prompt announce body — note PR-X reworks this site;
  sequence PR-Y AFTER PR-X), `Program.fs:2645` (tuple-final
  announce — same note), the Cycle-49 PR-I history-recall
  announce on `ActivityIds.input-assistant` (needs a new
  `SemanticCategory.InputAssistant` / `HistoryRecall`
  variant), mode-change announces (alt-screen, selection,
  shell-policy).
- **Tier 2 (~50 sites; mechanical rename to
  `AnnounceEmergency`)**: all `Ctrl+Shift+*` hotkeys, all
  `Diagnostics →`/`View →` menu items + their result
  announces, shell-switch success/failure, exception
  fallbacks. Full list in ADR §"Decision 4".
- New `TerminalSurface.AnnounceEmergency(msg, ?activityId)`
  routing directly to `RaiseNotificationEvent` on
  `pty-speak.app-affordance` — no dispatcher lookup. Lives
  in the WPF view layer (`grep -rn 'member.*Announce'
  src/Views src/*.xaml.cs`).
- Channel-extensibility smoke test: a no-op `LoggingProfile`
  recording every dispatched event proves Tier 1 routes
  through the dispatcher.

### PR-Z — closure audit (docs-only; fast-merge)

Per CLAUDE.md §"Cycle closure audit". Checklist:
- ADR 0004 Status → Accepted / Implemented (per-PR
  Status-notes entries).
- `CORE-ABSTRACTION-BOUNDARY.md` → point at ADR 0004 as the
  cell-model lock; reframe §6 in IOCell terms.
- `INTERACTION-MODEL.md` snapshot; `CANONICAL-DISPLAY-CATALOG.md`
  cross-ref; `ARCHITECTURE.md` / `DOC-MAP.md` / `README.md`
  module-map + feature-list.
- `ACCESSIBILITY-TESTING.md` — add the Cycle 51 NVDA matrix
  row (4-run test-04 + test-01 + test-02 + history-recall +
  menu + Tier 2 emergency announce).
- `CLAUDE.md` §"Current sequencing" — collapse the per-PR
  breakdown to one Cycle-51 line.
- `SESSION-HANDOFF.md` — Current state = Cycle 51 done;
  Next stage drops the cycle-internal PRs.
- This playbook + the spike branch: banner HISTORICAL,
  move playbook to `docs/archive/`, delete the spike branch
  (local + remote).
- `grep -rn 'SessionTuple' src tests docs` must be empty
  (rename complete). `grep -rn 'extractContent\b\|
  subPromptScreenReader\|computePromptCommandWrapRows' src`
  must be empty (deletions complete).
- Tag (maintainer pushes; tag-push 403s in sandbox per
  CLAUDE.md): stage `cycle51-iocell-shipped` in
  `docs/CHECKPOINTS.md`.

## 8. Operational protocol (every PR)

- **`git checkout main && git pull origin main` BEFORE
  cutting each PR branch** (Cycle-50 CHANGELOG-conflict
  lesson). Branch name `claude/cycle51-pr-<x>-<slug>`.
- **One concern per PR.** Conventional-commit messages.
  CHANGELOG `[Unreleased]` append-BOTTOM.
- **Create PRs via `mcp__github__create_pull_request`**
  (auto-subscribes to webhook activity). Ready-for-review,
  not draft.
- **Poll CI every 60s** (webhooks unreliable here):
  `mcp__github__pull_request_read method=get_check_runs`;
  `Bash sleep 60 run_in_background:true` between polls; do
  NOT poll faster or sleep longer.
- **Docs-only PRs** (PR-Z, this playbook's PR) fast-merge
  on the markdown link check alone — verify the file list
  is strictly `*.md` first (`get_files`). PR-W / PR-W2 /
  PR-X / PR-Y touch `src/`+`tests/` → full Windows CI
  required.
- **Standing merge rule**: CI green → squash-merge without
  re-asking → `git checkout main && git pull` → delete
  local+remote branch. Succinct CI announcements
  ("all green, merging").
- **CI failure**: ASK the maintainer for the log slice
  (sandbox blocks the blob/api hosts; you cannot fetch
  job logs). Tell them the exact strings to grep
  (`error FS`, `error MSB`, `Build FAILED`, the test
  name) + ~5–10 surrounding lines. **Do not push
  speculative fixups.** Use `AskUserQuestion` if they may
  be away (it phone-notifies; plain text does not).
- **Walking-skeleton discipline**: each PR ships through
  the maintainer's release-build NVDA dogfood before the
  next. Don't bundle. After PR-X especially, dogfood the
  4-run test-04 reproducer — that's the acceptance gate.

## 9. F# 9 / .NET 9 gotchas that WILL bite (canonical: CONTRIBUTING.md)

- **Nullness annotations at .NET boundaries** (`FS3261`).
  `Environment.GetEnvironmentVariable`,
  `Process.Start(ProcessStartInfo)`, `Path.GetFileName/
  GetDirectoryName`, `StreamReader.ReadLine` return
  nullable — annotate / `nonNull`.
- **`let rec`** for self-referencing class-body bindings
  (`FS0039` is misleading).
- **`internal` not `private`** for companion-module access;
  cross-assembly test access via
  `[<assembly: InternalsVisibleTo("PtySpeak.Tests.Unit")>]`.
- **DU access from C#**: `IsXxx` predicates + `.Item`/
  `.Item1` accessors. `IOCellPhase` is consumed from F#
  only here, but `IOCell` may be touched by the WPF C#
  layer — check.
- **Record literal type inference** fails when the record
  module isn't auto-opened — annotate the binding
  (`FS0039` at the field).
- **Sequence-in-match-arm** under
  `TreatWarningsAsErrors=true` — multi-statement match arms
  generally compile (precedent exists), but if a complex
  arm trips the offside rule, extract a named local
  function. Watch this in the PR-X announce rework.
- **Literal `0x1B` / NUL bytes** in test fixtures are
  foot-guns — verify with `grep -c $'\x1b' <file>` after
  edits. Relevant if PR-X / PR-W2 tests embed ESC bytes.
- **No `dotnet` locally** — read each `.fs` twice; a missed
  `let rec`/unused `open`/typo is a multi-minute CI
  round-trip. `git diff --stat` before every commit;
  investigate surprises before committing.

## 10. The screen-reader-user constraints (NON-NEGOTIABLE)

- The maintainer uses **NVDA**. **Never** propose a fix /
  workflow that requires walking a GUI dialog tree. Give
  keyboard/shell equivalents (`setx`, `set`, `tasklist |
  findstr`, `taskkill`, `reg query`). If no CLI equivalent
  exists, say so and ask.
- **Asking for diagnostics**: don't say "open the bundle
  and Ctrl+F" (not screen-reader-searchable). Use the
  in-app **`Diagnostics → Grep Diagnostics`** dialog
  (pattern; clipboard-capped 60 KB; full text to
  `%LOCALAPPDATA%\PtySpeak\extracts\`). Tell them the exact
  pattern (e.g. `PR-X tuple-final delta`). The NVDA
  announce confirms size + extract path.
- **`AskUserQuestion` phone-notifies; plain text does
  not.** Use it for genuine design decisions / CI-log asks
  when they may be away — NOT for status updates or
  autonomy-contract-covered actions.

## 11. If you have to stop mid-cycle

Update SESSION-HANDOFF.md §"Current state" + §"Next stage"
with: exact `main` HEAD, which PR merged last, which branch
is in flight + its commit, what's left in the in-flight PR,
and any CI-failure context awaiting a maintainer log paste.
Then this playbook + the ADR carry the rest. The maintainer
explicitly values a crash-safe handoff over finishing a
turn — leave the next session a clean pickup, like this doc
was left for you.
