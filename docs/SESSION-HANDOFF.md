# Session handoff

This document is the bridge between Claude Code coding sessions on
this repo. It captures the things a new session needs that **aren't**
already in the existing docs — the sandbox-specific gotchas, the
"where we left off" pointer, and a pre-digested sketch of the next
stage. The general working conventions (PR shape, branching,
CHANGELOG discipline, documentation policy, F# / WPF gotchas) live in
[`CONTRIBUTING.md`](../CONTRIBUTING.md).

A new session should read this **first**, then
[`docs/PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md) (the
current strategic plan revision; supersedes the original
`PROJECT-PLAN-2026-05.md` per Track E E5 dated-snapshot
discipline),
[`CONTRIBUTING.md`](../CONTRIBUTING.md),
[`spec/tech-plan.md`](../spec/tech-plan.md),
[`docs/CHECKPOINTS.md`](CHECKPOINTS.md), and
[`CHANGELOG.md`](../CHANGELOG.md) in that order — see the full
"Recommended reading order" at the bottom.

## Where we left off

> **Authoritative plan**:
> [`docs/PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md). The
> "Where we left off" table below captures snapshot state; the plan
> document captures the multi-week sequencing decisions
> (cleanup → Stage 7 validation gate → output framework cycle →
> input framework cycle → Stage 10) that drive the work.

| | |
|---|---|
| **Last merged stages** | **Stages 0 → 7 + 11** all merged to `main`, plus the retroactively-formalized Stages 4a / 4b / 5a. Stage 7 shipped 2026-05-03 across 11 sequenced PRs (A → K, #131-#143) + PR-L #144 (doc-purpose audit + Stage 7 wrap-up) + PR-M #145 (coalescer cross-row spinner gate fix). **Post-Stage-7 substrate cycle 2026-05-04 → 2026-05-07** (PRs #146-#184) shipped: event-and-output framework spec (#151); Stages 8a/8b/8c/8d.1/8d.2 (#152-#157, #155); display-pathway substrate Phase A + Phase A.1 + Phase B subset + Phase A.2 + #166 suffix-diff + #167 atlas + #168 Tier 1 parameters + #169 shell-switch flush fix (#159-#169); five research-stage docs: PIPELINE-NARRATIVE (#170), SESSION-MODEL (#171), INTERACTION-MODEL (#173), PANE-MODEL (#174), CUSTOMIZATION-MODEL (#181); six-track audit phase (#172, #175-#180); post-audit cleanup (#181-#184). **Tier 1 SessionModel implementation cycle 2026-05-07 → 2026-05-08** (PRs #185-#199) shipped substrate end-to-end: skeleton (#185), OSC 133 producer + cursor field (#186), state machine + composition (#187), heuristic fallback + alt-screen wiring (#189), PromptText capture (#190), diagnostic battery extension (#191), tick-driven detector via channel-driven actor model (#192), CHANNEL-ARCHITECTURE doc (#193), default-shell config + detector consolidation (#194), row-index-aware emission (#195) + announce-wording followup (#196), CommandText + OutputText extraction (#197), multi-match flap fix (#198), Ctrl+Shift+Y clipboard hotkey (#199). Maintainer NVDA-validation green throughout. **Cycle 24 SessionModel persistence cycle 2026-05-09** (PRs #203, #206-#212, #219) shipped Tier 2 end-to-end: 24a TOML schema (#203), 24b JSONL serializer (#206), 24c bounded-channel async file writer (#208), 24d-1 Always-mode synchronous flush (#209), 24d-2 env-var value sanitisation (#210), 24e Ctrl+Shift+S diagnostic hotkey + NVDA matrix (#211), 24f persistence-config diagnostic + matrix typo fix (#212), 24g TOML model snapshot logged at startup (#219). **Cycle 25 operational-ergonomics + diagnostic-dump bundle 2026-05-09** (PRs #220-#222): 25a hotkey reorg (Ctrl+Shift+L/P/E rebound) + open-config auto-create + reload-on-shell-switch + `[logging]` TOML (#220); 25b-1 Ctrl+Shift+D bundles diagnostic dump (FileLogger log + config + redacted env into dated snapshot file + clipboard) + Ctrl+Shift+T placeholder removed (#221); 25b-1a bundle gains `--- SESSION LOG ---` section + Ctrl+Shift+L removed since D's bundle subsumes it (#222). |
| **Last shipped release** | Maintainer cuts release builds from `main` whenever convenient. The last release predating Tier 1 was `v0.0.1-preview.43`; subsequent previews have shipped through the substrate + Tier 1 cycles. |
| **In-flight branch** | (none) — Cycle 25b-1a shipped 2026-05-09 via PR #222 (the most recent merge; commit `75ae04e`). |
| **Next stage** | **Decide next cycle direction.** Three candidates on deck: **(a) Cycle 25b-2** — the deferred FlaUI/UIA-driven E2E test runner from the original Cycle 25b plan; new `tests/Tests.E2E/` project with 7 tests covering the Cycle 24e NVDA matrix rows; spawns pty-speak via UIA, drives input via UIA `Keyboard.Type` / `Keyboard.Press`, asserts on JSONL file content + clipboard text + an internal `LastAnnouncement` hook on `TerminalView`. Risks: FlaUI flakiness; UIA needs an interactive desktop session so CI can't run it (project would build in CI but tests skipped). See "Cycle 25 in-flight handoff" below for the fuller deferred-design sketch. **(b) Cycle 26** — app menu, would surface `scripts/test-process-cleanup.ps1` + future diagnostic-script entries (release-notes drafter, etc.) as menu items; reduces hotkey-count working-memory pressure for additional features per the maintainer's noted ceiling. **(c) Framework cycles** — Output framework Part 3 + Input framework Part 4 per [`docs/PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md); Stage 10 review-mode + quick-nav lands as the first non-built-in framework consumer. Maintainer to pick on next planning round. Open follow-ups (cross-cutting; not blocking the next cycle): (a) Screen-buffer runtime resize (Phase 2). (b) Stable-baseline tag pushes including `baseline/stage-7-claude-roundtrip` per [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — local sandbox can't push tags, awaiting maintainer sweep. (c) MAY-4.md Concern 3 (navigable streaming response queue) — naturally subsumed by SessionModel + ReplPathway (Phase 2). (d) New dated revision of `PROJECT-PLAN-2026-05-revision.md` overdue per E5 discipline (current revision is the 2026-05-07 snapshot; multiple cycles have shipped since). |

## Cycle 23 in-flight handoff (✅ shipped 2026-05-08; retained for historical reference)

> **Status (2026-05-09):** all four Cycle 23 phases shipped.
> Phase 1 = PR #200 (doc-currency refresh); Phase 3 = PR #201
> (CUSTOMIZATION-MODEL Q1–Q7 resolutions). Phase 2 (in-session
> walk-through) ran 2026-05-08; the resolutions it produced are
> already merged into `docs/CUSTOMIZATION-MODEL.md`. Phase 4
> resolved with the maintainer's pick of **Tier 2 SessionModel
> persistence** — now tracked as **Cycle 24** (sub-cycles
> 24a–24e). Cycles 24a (TOML schema, PR #203) and 24b (JSONL
> serializer, PR #206) shipped 2026-05-09; Cycle 24c (file
> writer) is in flight. See the Cycle 24 in-flight handoff
> section below for the original sub-cycle planning sketch
> (now partly historical — 24a + 24b are shipped; 24c–24e
> remain).
>
> The original cross-session-bridge content for Cycle 23 is
> preserved verbatim below for the audit trail (Q1–Q7 verbatim
> prompts + the tentative recommendations the maintainer
> reviewed). New agents picking up SessionModel persistence
> work should read the Cycle 24 in-flight handoff section, not
> this one.

### Cycle 23 phase status

- **Phase 1** — doc cleanup PR (this PR; awaiting CI green +
  merge as of 2026-05-09).
- **Phase 2** — in-session maintainer Q&A walk-through of the
  7 CUSTOMIZATION-MODEL questions (Q1-Q7). **PENDING.**
- **Phase 3** — Q&A resolution PR. Mirrors PR #182 (Cycle 8)
  shape: edit `docs/CUSTOMIZATION-MODEL.md` Q1-Q7 sections to
  append `— ✅ Resolved 2026-05-09` (or current date) to each
  heading + a "**Resolution**: ..." block + brief rationale +
  preserved-for-history "Original question / alternatives"
  block. ~150-250 LOC; single-concern PR.
- **Phase 4** — maintainer chooses next coding cycle: **Tier 2
  SessionModel persistence** (default, per
  `PROJECT-PLAN-2026-05-revision.md` sequencing block) OR
  **Phase 2 input framework cycle** (alternative; unblocks
  history navigation + echo correlation + ReplPathway).

### Phase 2 walk-through prep — 7 CUSTOMIZATION-MODEL questions

Reference: `docs/CUSTOMIZATION-MODEL.md` lines 762-880. Each
question's full prompt + tentative recommendation is captured
here so the maintainer Q&A can happen with any agent + any
session length. Maintainer can answer inline or redirect; a
new agent should walk through Q1-Q7 in order, capturing
answers, then ship Phase 3 PR.

**Q1 — Naming** (line 762)
- Should the doc be named `CUSTOMIZATION-MODEL.md`?
- Alternatives: `INTROSPECTION-MODEL.md`,
  `USER-AUTHORSHIP-MODEL.md`, `PIPELINE-CUSTOMIZATION.md`,
  `EDITABLE-PIPELINE.md`.
- **Tentative**: keep `CUSTOMIZATION-MODEL.md` — parallels
  PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL /
  PANE-MODEL / CHANNEL-ARCHITECTURE; names the user's
  relationship to the pipeline.

**Q2 — Alternatives registry shape** (line 777)
- How do alternative implementations register? Compile-time
  only (user enables/disables built-ins)? Runtime via TOML
  extension? Both?
- **Tentative**: BOTH. Built-in alternatives compiled in;
  user-authored alternatives load from
  `%LOCALAPPDATA%\PtySpeak\extensions\` as `.fsx` scripts (per
  spec A.5 phase 2 plan).

**Q3 — Trace persistence** (line 796)
- Where do per-output traces live? In-memory per
  SessionTuple? Persisted with SessionTuple to disk? Separate
  trace ring buffer (opt-in, bounded)?
- **Tentative**: in-memory per SessionTuple by default;
  separate persistent ring buffer as opt-in power-user
  feature. Aligns with SessionModel's memory-only / session-
  log / always design.

**Q4 — Rule-context-keying scope** (line 812)
- Which context fields qualify rules as "similar"? Minimal
  (shell only)? Recommended (shell + command-prefix +
  semantic-category)? Full (all fields)? User-extensible?
- **Tentative**: full enumeration at substrate level (all
  fields available); rule authoring UX defaults to recommended
  subset (shell + command-prefix + semantic-category).

**Q5 — UI surface for introspection** (line 830)
- Where does introspection UI live? Pipeline Inspector pane
  (new, per PANE-MODEL catalog)? Modal panel? Companion app?
  Extension to Cherry-picked I/O pane?
- **Tentative**: Pipeline Inspector pane as new entry in
  PANE-MODEL pane catalog. Modal-panel fallback for ephemeral
  inspection.

**Q6 — Rule precedence when multiple match** (line 845)
- When multiple rules match the same context + target same
  stage, which wins? Most-specific context? Most-recent
  authored? Explicit user-priority field? Hybrid?
- **Tentative**: explicit user-priority field (simplest; user
  has agency); compute sensible default priority based on
  context-specificity at creation time.

**Q7 — Rule-authoring UX** (line 865)
- How does the user author rules? Dropdown at each step
  (inline)? Recipe DSL (text-based for power users)?
  Conditional-logic editor (visual)? All three?
- **Tentative**: start with dropdowns + automatic
  context-tuple suggestion (matches the maintainer's
  2026-05-07 example framing); add DSL + visual editor later
  as power-user tooling.

### CHANNEL-ARCHITECTURE.md tentative-resolutions (Q1-Q5; optional Phase 2 add-on)

Reference: `docs/CHANNEL-ARCHITECTURE.md` (PR #193, Cycle 18).
The doc shipped with 5 forward-looking design questions; all
flagged "tentative" already. Worth confirming or redirecting
since they shape Phase 2 / Tier 2 / Tier 3 substrate
decisions. Maintainer can opt to lock these in during the
Cycle 23 Phase 2 walk-through, or leave as tentative.

- Q1: PromptBoundary — keep as F# Event vs. dedicated Channel?
  Tentative: leave as Event.
- Q2: FileLogger capacity TOML-configurable? Tentative: defer
  until demand surfaces.
- Q3: Input-keystroke channel — batch or per-keystroke?
  Tentative: per-keystroke (Phase 2 will firm up).
- Q4: Pipeline Inspector subscription mechanism? Tentative:
  design alongside the pane in a future cycle.
- Q5: Unbounded channels ever? Tentative: NO; BoundedChannel
  only.

### Spec E.5.1-E.5.7 (informational only — NOT in Cycle 23 Q&A)

`spec/event-and-output-framework.md` §E.5 carries 7 open
questions explicitly marked "do not block the spec from
landing now". They pin at Stages 9b/9c/9d as those substrate
cycles approach. Surfaced here only so a new agent knows they
exist; not addressed this cycle.

### Phase 4 next-cycle pre-digest

When maintainer chooses Phase 4 direction, the new agent picks
up from one of:

**Default — Tier 2 SessionModel persistence**:
- Persistence model per Q4 (already resolved 2026-05-07 →
  "Memory" default; TOML opt-in adds SessionLog / Always
  modes).
- Serialise `SessionModel.T` to disk (JSON or compact binary).
- Restore across launches per shell session.
- Designed in `docs/SESSION-MODEL.md` §"Persistence modes".
- Estimated: 4-6 PRs over 2-3 weeks.

**Alternative — Phase 2 input framework cycle**:
- Echo correlation; cursor-aware editing; InputPathway
  protocol; ReplPathway.
- Unblocks Up/Down history navigation (replay past commands)
  + history-paste workflows.
- Substantial multi-cycle work.

Maintainer chooses; new agent plans the chosen direction in a
fresh plan-mode cycle.

The end-to-end pipeline now reaches the auto-update boundary:
launching the app spawns `cmd.exe` under ConPTY, parses its
output, applies VtEvents to a 30×120 `Screen`, renders the buffer
in a custom WPF `FrameworkElement`, exposes that buffer to NVDA
via UIA Document role + Text pattern with working Line / Word /
Character / Document review-cursor navigation, and self-updates
to subsequent previews via `Ctrl+Shift+U` with NVDA progress
narration plus an audible version-flip on restart. **Stage 4a**
(Claude Code rendering substrate, formalized in `spec/tech-plan.md`
§4a per chat 2026-05-03; previously informally referred to as
"Stage 4.5") shipped in two PRs (mode coverage + alt-screen
back-buffer); the Screen layer now applies DECTCEM, DECSC/DECRC,
256/truecolor SGR, alt-screen 1049, and the OSC 52
SECURITY-CRITICAL silent drop. Stage 5 closes the loop on output narrating itself: the
new `Coalescer` module (FNV-1a per-row + frame hash dedup,
sliding-window spinner suppression, leading- + trailing-edge
200ms debounce, alt-screen flush barrier, per-row
`AnnounceSanitiser` chokepoint) sits between the parser-side
`notificationChannel` (256, DropOldest) and a new
`coalescedChannel` (16, Wait), with the existing UIA drain
fanning out to `TerminalView.Announce(message, activityId)`
using the new `ActivityIds` vocabulary so NVDA users can
configure per-tag handling. The Stage 5 PR also bundled the
Acc/9 OnRender lock fix so the WPF render path takes ONE
locked snapshot per frame instead of re-entering the screen
gate per cell. Stage 6 makes pty-speak interactive: the new
pure-F# `KeyEncoding` module (decoupled from
`System.Windows.Input.Key` so it survives a future Linux /
macOS port unchanged) translates keystrokes into xterm-style
VT byte sequences honouring DECCKM application-cursor mode,
the SGR-modifier protocol, F1-F12 SS3/CSI conventions, and
Ctrl-letter / Alt-prefix encoding; an `ApplicationCommands.Paste`
handler wraps clipboard text in bracketed-paste markers when
`?2004` is set (and strips embedded `\x1b[201~` for paste-
injection defence, an accessibility-first divergence from
xterm); `OnGotKeyboardFocus` / `OnLostKeyboardFocus` emit
`\x1b[I` / `\x1b[O` when `?1004` is set; window resize debounces
through a 200ms `DispatcherTimer` to `ResizePseudoConsole`;
and a kernel Job Object with `KILL_ON_JOB_CLOSE` semantics
contains the entire child-process tree so even a hard parent
crash leaves no orphans. The reserved-hotkey contract
(Ctrl+Shift+U / D / R shipped, Ctrl+Shift+M and Alt+Shift+R
future-reserved) takes priority over PTY input via the
load-bearing `OnPreviewKeyDown` filter ordering pinned in
the inline doc-comment + behavioural tests.

**Post-Stage-6 streaming-fix cycle.** After Stage 6 shipped,
manual NVDA verification surfaced that streaming announcements
weren't actually reaching NVDA in practice — a series of
diagnostics + fixes followed: PR #109 instrumented the
streaming path with INFO-level logging, PR #111 added the
`Ctrl+Shift+;` log-copy hotkey to make those logs trivially
shareable, PR #114 fixed a `FileShare` race that prevented
the log-copy from reading its own writer's file, and **PR
#116** removed the broken `AllHashHistory` spinner gate that
was the root cause: the gate counted total entries rather
than unique-hash recurrences, so every emit added 30 row
entries and the threshold-of-20 was instantly exceeded,
silencing the channel permanently. As of PR #116 the
streaming pipeline is provably alive end-to-end (cmd.exe
banner reads on launch, typed characters and command output
both trigger speech). The remaining verbose-readback issue
— Stage 5's `renderRows` design announces the whole
rendered screen on every emit — is the **first foundational
architecture decision** that the new May-2026 plan addresses.

**The May-2026 strategic plan.** A 2026-05-03 review identified
two foundational architecture decisions that remain unsolved
(output-handling profiles and input-interpretation profiles)
AND four original `spec/tech-plan.md` Stages still unshipped
(Stages 7, 8, 9, 10). The result is
[`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md), which
sequences the work as: **Part 1** Cleanup → **Part 2** Stage 7
(validation gate) → **Part 3** Output framework cycle (subsumes
Stages 8 + 9) → **Part 4** Input framework cycle → **Part 5**
Stage 10 (first framework consumer). Read that document next
for the full sequencing rationale; the per-stage detail in this
file's "Stage N implementation sketch" sections remains useful
historical reference but is no longer the active plan.

## Cycle 24 in-flight handoff (✅ Cycles 24a–24g all shipped 2026-05-09; retained for historical reference)

> **Status (2026-05-09):** all eight Cycle 24 sub-cycles
> shipped. PR map: 24a TOML schema (#203); 24b JSONL
> serializer (#206); 24c bounded-channel async file writer
> (#208); 24d-1 Always-mode synchronous flush (#209); 24d-2
> env-var value sanitisation (#210); 24e Ctrl+Shift+S
> diagnostic hotkey + NVDA matrix (#211); 24f
> persistence-config diagnostic + matrix typo fix (#212);
> 24g TOML model snapshot logged at startup (#219). Tier 2
> SessionModel persistence is end-to-end functional in
> `main`; the maintainer's first manual NVDA-matrix
> walkthrough on 2026-05-09 surfaced operational friction
> that drove the subsequent Cycle 25 ergonomics + bundle
> work (see the Cycle 25 in-flight handoff section below).
>
> The original Cycle 24 sub-cycle planning sketch is preserved
> below verbatim for the audit trail. New agents picking up
> SessionModel-persistence-adjacent work should treat the
> sub-cycle table below as historical roadmap; the canonical
> behaviour-of-record is `main` itself, the CHANGELOG
> entries for 24c–24g, and `docs/SESSION-MODEL.md`.

### Cycle 24 status

- **Cycle 23** (doc-currency refresh + CUSTOMIZATION-MODEL Q&A +
  Q&A-resolution PR) — ✅ closed 2026-05-08 via PRs #200 + #201.
- **Phase 4 fork resolved by maintainer 2026-05-08**: chose
  **Tier 2 SessionModel persistence** (the default), not Phase 2
  input framework cycle.
- **Cycle 24-pre** (CLAUDE.md tone-note) — ✅ shipped via PR #202
  (resolved manual MCP-reconnect path).
- **Cycle 24a** (TOML plumbing) — ✅ shipped via PR #203
  (2026-05-09).
- **Cycle 24b** (JSONL serializer) — ✅ shipped via PR #206
  (2026-05-09).
- **Cycle 24c** (file writer) — in flight; design locked, see
  "Next stage" cell at the top of this doc.
- **Cycles 24d-24e** — sketched (see sub-cycle table below);
  detailed planning at execute-time per sub-cycle.

### Cycle naming convention (chosen 2026-05-08)

Use **cycle-number naming** (`Cycle 24`, `Cycle 24a/b/c/d/e`,
`Cycle 24-pre`) for PR titles, branch names, commit messages, and
CHANGELOG entries. Avoid "Tier 2" / "Tier 6" framings in user-
facing artifacts because the docs collide on that vocabulary
(see next subsection).

### Vocabulary note: SESSION-MODEL Tier 6 vs planning-doc Tier 2 (✅ resolved 2026-05-09)

**Resolution**: persistence work is now tracked under
**cycle-number naming** as `Cycle 24a–24e`. Both the
SESSION-MODEL.md §6 "Tier 6" framing and the
planning-doc / SESSION-HANDOFF "Tier 2 persistence" framing
are preserved as historical decision-context, with
in-place vocabulary notes pointing at the active labels.

The original collision (preserved for context):

- `docs/SESSION-MODEL.md` §6 calls Tier 2 the **heuristic-fallback**
  stage (already shipped via Cycles 11-22b) and lists
  **persistence as Tier 6** (now annotated with a vocabulary
  note pointing at Cycle 24).
- `docs/PROJECT-PLAN-2026-05-revision.md`,
  `docs/SESSION-HANDOFF.md`,
  `docs/AUDIT-BACKLOG-VALIDATION.md` (item 28) called the next
  implementation cycle **"Tier 2 SessionModel persistence"**.

The cycle-number convention wins for user-facing artifacts
(PR titles, branch names, CHANGELOG entries, body text).
Tier-N labels remain in design docs as historical
decision-context only.

### Cycle 24a scope (the immediate next PR)

**Maintainer-approved scope (chosen 2026-05-08)**: TOML plumbing
only — **no I/O, no JSONL serializer**. Mirrors PR #185 (Tier 1
substrate skeleton) shape. ~150-250 LOC across ~6-8 files.

**Files to touch**:

- **NEW** `src/Terminal.Core/SessionPersistence.fs` —
  `PersistenceMode` DU (`MemoryOnly` / `SessionLog` / `Always`),
  `PersistenceFormat` DU (`Jsonl`), `PersistenceConfig` record,
  `defaultConfig` factory, `parseFromTable` parser. Mirror
  `Config.fs:395-429` whitelist + log-and-drop pattern.
- **EDIT** `src/Terminal.Core/Config.fs` — extend root-config
  parser to recognise `[session_model.persistence]` table.
- **EDIT** `src/Terminal.Core/Terminal.Core.fsproj` — add
  `<Compile Include="SessionPersistence.fs" />` **before**
  `Config.fs`.
- **EDIT** `src/Terminal.App/Program.fs` — read new config field
  at composition root + emit one Information-level log line
  (`"SessionModel persistence mode: {Mode} (output_dir={OutputDir})"`).
  No behaviour change.
- **NEW** `tests/Tests.Unit/SessionPersistenceTests.fs` —
  10-12 `[<Fact>]` parsing tests (xUnit only, no FsCheck).
- **EDIT** `tests/Tests.Unit/Tests.Unit.fsproj` — slot new test
  file after `SessionModelTests.fs` (line 30).
- **EDIT** `docs/USER-SETTINGS.md` — document the
  `[session_model.persistence]` schema (mirror existing
  `[startup]` section format).
- **EDIT** `CHANGELOG.md` — `### Added (Cycle 24a): SessionModel
  persistence config substrate (TOML schema, no I/O yet)` block
  at top of `[Unreleased]`.

### Cycle 24 sub-cycle sketch

| Sub-cycle | What | LOC est. | I/O? | NVDA row? |
|---|---|---|---|---|
| **24a** *(next)* | TOML schema + Config.fs extension + composition-root wire (config read, behavior unchanged) | 150-250 | none | no |
| 24b | JSONL serializer — pure `formatTupleAsJsonl : SessionTuple -> string` mirroring `formatHistoryForClipboard` | 150-250 | none | no |
| 24c | File writer (bounded-channel async, mirrors `FileLogger.fs`) + `memory_only` / `session_log` modes wired | 300-500 | yes | yes |
| 24d | `always` mode (synchronous flush on Active→History) + secrets sanitisation via env-var deny-list | 200-300 | yes | yes |
| 24e | NVDA matrix rows + diagnostic helpers + CHECKPOINTS tag candidate | 100-200 | yes | yes |

Subsequent cycles (Cycle 25+) cover the read-back / query API
specified in `docs/SESSION-MODEL.md` §7 (`LatestTuple`,
`NthFromLast`, `TupleByCommandId`, etc.) and the
`pty-speak-replay` CLI deferred to that doc's Tier 6.

### Pre-decided design (per `docs/SESSION-MODEL.md` §5)

- **Modes**: `memory_only` (default — privacy-by-default) /
  `session_log` (per-tuple flush on Active→History) /
  `always` (synchronous flush, audit-grade durability).
- **Format**: JSONL (newline-delimited JSON, one tuple per line).
- **Path**:
  `%LOCALAPPDATA%\PtySpeak\sessions\session-<SessionId>.jsonl`.
  One file per shell session per launch (shell-switch creates a
  new file).
- **TOML schema**: `[session_model.persistence]` with `mode`
  (string) / `output_dir` (string) / `format` (string) /
  `max_session_size_mb` (int).
- **Secrets handling**: env-var deny-list sanitiser applied to
  `commandText` before write (same policy as Stage 7's
  env-scrub PO-5). Lands in Cycle 24d.
- **Permissions**: inherit `FileLogger`'s user-only NTFS ACLs.
- **Ring-buffer cap**: in-memory History bounded to 100 tuples
  (already enforced by Tier 1); beyond cap, oldest evicted
  silently (or written to disk first if persistence enabled).

### Persistence hookpoint already commented in code

`src/Terminal.App/Program.fs` lines 2142-2147 carry an
explicit `// Tier 1.C has no persistence so the finalised tuple
is structurally discarded with the prior SessionModel; Tier 2's
persistence will use this seam to flush History before
recreation.` comment marking the shell-switch flush injection
point. Cycle 24c is the PR that takes this seam.

### Patterns to mirror (file:line)

- **TOML loading**: `src/Terminal.Core/Config.fs:173-181`
  (`defaultConfigFilePath()` — `%LOCALAPPDATA%\PtySpeak\` path
  resolution with `%TEMP%` fallback if `LOCALAPPDATA` unset).
- **TOML parsing + whitelist + log-and-drop + fallback**:
  `src/Terminal.Core/Config.fs:395-429` (`parseStartupOverrides`,
  PR #194 precedent).
- **JSONL serializer template** (Cycle 24b, NOT Cycle 24a):
  `src/Terminal.Core/SessionModel.fs:formatHistoryForClipboard`
  (Cycle 22b, pure function, StringBuilder pattern).
- **File writer** (Cycle 24c, NOT Cycle 24a):
  `src/Terminal.Core/FileLogger.fs:11-12, 84-88, 104-119,
  174-225` (day-folder + per-session-file + bounded-channel
  async writer + 7-day retention + defensive `%TEMP%` fallback).
- **Test pattern**: `tests/Tests.Unit/SessionModelTests.fs`
  (xUnit `[<Fact>]`, immutable record fixtures, no FsCheck).

### In-flight branch state (2026-05-08)

- Branch: `claude/continue-project-setup-2LepM`.
- HEAD: `40aaf60` (`docs(claude): prefer succinct CI-completion
  responses`) — pushed to origin; **no PR opened yet**.
- A second commit will land on top of this with the very handoff
  section you're reading now.
- Both commits are independent in scope. New agent options:
  - Open ONE PR with both commits (multi-concern body covering
    tone-note + Cycle 24 handoff doc).
  - Open TWO separate PRs (`docs(claude): prefer succinct
    CI-completion responses` + `docs(handoff): Cycle 24
    in-flight handoff`).
- Local `main` is divergent (51/53 commits) — leave alone;
  always branch off `origin/main` for new work.

### Open clarifications for the new agent (none load-bearing)

- Whether `docs/config.toml.sample` exists and needs updating
  (investigate at execute-time; bundle into smallest of Cycle
  24a-c PRs that touches user-facing schema, OR ship as a
  follow-up).
- Whether `Ctrl+Shift+S`-style "dump current session-log path"
  hotkey lands in Cycle 24e or a later sub-cycle (defer
  decision until 24c ships and the maintainer's NVDA experience
  surfaces the need).

## Cycle 25 in-flight handoff (✅ 25 + 25a + 25b-1 + 25b-1a all shipped 2026-05-09; 25b-2 deferred; retained for reference)

> **Status (2026-05-09):** Cycle 25 closed the operational
> ergonomics + diagnostic-dump bundle work that the Cycle
> 24e NVDA-matrix walkthrough surfaced. Three PRs landed:
> 25a (#220) shipped the hotkey reorg + open-config
> auto-create + reload-on-shell-switch + `[logging]` TOML;
> 25b-1 (#221) shipped the `Ctrl+Shift+D` diagnostic-dump
> bundle + removed the `Ctrl+Shift+T` placeholder; 25b-1a
> (#222) added the `--- SESSION LOG ---` section to the
> bundle and removed `Ctrl+Shift+L` (subsumed by D).
> Maintainer NVDA-validation green throughout — the bundle
> format was paste-back-validated on a real D press
> mid-session (the validation surfaced the env-var typo
> `PTYS[EAK_SHELL=CLAUDE` that was harmless and intentionally
> left in place).
>
> The deferred half — **Cycle 25b-2, the FlaUI/UIA-driven
> E2E test runner** — is still on the table. Original 25b
> plan called for a single PR combining the dump bundle +
> the FlaUI runner; the maintainer chose to split when
> the dump bundle's value was higher and the FlaUI
> infrastructure carried more risk (UIA flakiness + CI
> can't run it without an interactive desktop). Pick up
> 25b-2 when the autonomous-validation discipline is
> wanted; the bundle has bedded in by then so the runner
> ships against a stable target.

### Cycle 25 status

- **Cycle 25a** — operational ergonomics (PR #220, shipped):
  - Hotkey reorg: `Ctrl+Shift+L` rebound from "open logs
    folder" to "copy active log to clipboard" (since
    superseded by 25b-1a's removal); `Ctrl+Shift+P` rebound
    to "open data folder" (the parent of `\logs`,
    `\sessions`, and `config.toml` — one-keystroke access
    to all three first-tier directories); `Ctrl+Shift+E`
    NEW for "edit config.toml".
  - Open-config auto-creates `config.toml` with sensible
    defaults if missing, then opens in default app. Targets
    the friction the maintainer surfaced typing TOML
    section headers by hand (the `[sessionmodel._persistence]`
    typo in the Cycle 24e walkthrough).
  - Reload-on-shell-switch refreshes `persistenceConfig`
    + `loggingConfig` from disk on each `Ctrl+Shift+1/2/3`
    so config edits don't require a full restart for
    persistence-mode toggles.
  - New `[logging]` TOML section consolidates per-shell
    log overrides under one schema branch.

- **Cycle 25b-1** — Ctrl+Shift+D diagnostic-dump bundle
  (PR #221, shipped):
  - `Diagnostics.runFullBattery` extended to bundle the
    diagnostic-battery log + active FileLogger log
    (`loggerSink.ActiveLogPath`) + `config.toml` + redacted
    environment into a single dated snapshot file at
    `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<yyyy-MM-dd-HH-mm-ss-fff>.txt`
    (paired stamp with the diagnostic-log file from the
    same press). Bundle content also copied to clipboard.
  - Plain-text format with `--- SECTION ---` markers
    between sections (DIAGNOSTIC BATTERY LOG, FILELOGGER
    ACTIVE LOG, CONFIG.TOML, ENVIRONMENT, END OF SNAPSHOT;
    plus a header carrying timestamp + version + OS + .NET
    + PID). Format intentionally stable so future replay
    tools can index by section header.
  - Env-var deny-list mirrors `SessionSanitiser`'s pattern
    (`*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`,
    `*_PASSWD`); `ANTHROPIC_API_KEY` exempted (maintainer
    needs to verify it's set when triaging Claude shell
    startup). Names always shown verbatim; values redacted
    to `<redacted by suite>` for matches.
  - `Ctrl+Shift+T` placeholder shipped in 25a removed
    entirely (DU case + `nameOf` arm + `builtIns` row +
    `allCommands` entry + C# `AppReservedHotkeys` row +
    `runRunTestMatrix` placeholder handler + `bindHotkey`
    line + `runTestMatrix` ActivityId). The "everything
    that can be automated goes into D" directive folded
    the diagnostic suite into D rather than splitting
    across two hotkeys.
  - One CI fixup: F# 9 nullness error at
    `Diagnostics.fs:924,43` (`Path.GetDirectoryName` returns
    `string | null`; pattern-match instead of
    `if not (String.IsNullOrEmpty dir)` form which doesn't
    propagate through F# 9 flow analysis).

- **Cycle 25b-1a** — bundle gains `--- SESSION LOG ---`
  section + Ctrl+Shift+L removed (PR #222, shipped):
  - New `--- SESSION LOG ---` section between
    `--- CONFIG.TOML ---` and `--- ENVIRONMENT ---`. Carries
    the same `Session log mode <mode>; path <full-path>.`
    line that `Ctrl+Shift+S` announces — surfaced as a
    first-class triage line so the active session-log path
    doesn't have to be grepped out of the FileLogger log
    slice.
  - Single source of truth via
    `Program.fs.buildSessionLogSummary` (defined before
    `runDiagnostic` so D's `resolveSessionLogSummary`
    closure can capture it). Both the S handler and D's
    bundle read this helper.
  - **`Ctrl+Shift+L` (CopyLatestLog) removed entirely.**
    The D bundle's `--- FILELOGGER ACTIVE LOG ---` section
    subsumes L's payload, so a dedicated copy-just-the-log
    hotkey was redundant. Removed: AppCommand DU case +
    `nameOf` arm + `builtIns` row + `allCommands` entry;
    `runCopyLatestLog` handler in Program.fs (~135 lines
    including the dispatcher-deadlock-fix Task wrapper);
    the `bindHotkey` line; the
    `SetCopyLogToClipboardHandler` defense-in-depth
    wiring; the C# `_copyActiveLogToClipboard` field,
    `SetCopyLogToClipboardHandler` setter, the
    `Key.OemSemicolon` direct handler in
    `OnPreviewKeyDown`, and the `(Key.L, ...)` row in
    `AppReservedHotkeys`. Per maintainer's
    hotkey-count-pressure feedback (working-memory ceiling).

### Cycle 25b-2 (deferred — design sketch retained)

The deferred FlaUI/UIA-driven E2E test runner. Original
plan locked these decisions before the split:

- **New `tests/Tests.E2E/` xUnit project** depending on
  `FlaUI.UIA3` 4.x. NOT added to the .sln so CI's
  solution-level `dotnet test` doesn't pick it up
  (UIA needs interactive desktop session; GitHub-hosted
  Windows runners don't have one).
- **7 `[<Fact>]`s** mirroring the Cycle 24e NVDA matrix
  rows: (1) mode change at startup, (2) file creation in
  session_log, (3) Always-mode synchronous flush,
  (4) Ctrl+Shift+S announces path, (5) env-var redaction,
  (6) Ctrl+Shift+Y substrate honesty, (7) inline
  shell-process snapshot.
- Each test backs up `config.toml`, writes a fixture,
  spawns `Terminal.App.exe`, attaches via
  `FlaUI.UIA3.Application.Attach(processId)`, drives
  input via UIA `Keyboard.Type` / `Keyboard.Press`,
  asserts on JSONL file content + clipboard text + an
  internal `LastAnnouncement` hook on `TerminalView`,
  then tears down + restores config.
- **Internal `LastAnnouncement` hook** in
  `src/Views/TerminalView.cs`: `internal static
  (string Text, string ActivityId, DateTime At)
  LastAnnouncement` updated under the announce-lock; tests
  poll it via `[<assembly:
  InternalsVisibleTo("PtySpeak.Tests.E2E")>]`.
- **PowerShell harness script** at
  `scripts/run-diagnostic-suite.ps1`: backs up config,
  resolves the bundled `Tests.E2E.dll` path, runs
  `dotnet test`, parses trx, builds the diagnostic dump
  (would extend the existing 5-section bundle with a
  `--- SUITE RESULTS ---` section and per-test session-log
  files), restores config. Estimated 200-300 lines.
- **Bundling**: `Tests.E2E.dll` + FlaUI deps via
  `<Content Include>` in `Terminal.App.fsproj` so they
  land next to `Terminal.App.exe` in the Velopack pack.
- Estimated ~1200 lines total.
- **Risk**: FlaUI flakiness on the maintainer's NVDA
  machine; CI integration is a separate cycle (autologon
  + scheduled task on a self-hosted runner).
- **Trigger**: maintainer wants the autonomous-validation
  discipline OR a regression slips through manual NVDA
  validation. Until then, the bundle's manual-paste-back
  loop is the validation pathway.

## Pending action items (maintainer)

These can only be done from a workstation with normal git +
GitHub-website access. They've accumulated because the development
sandbox can't push tags and the GitHub MCP server occasionally
disconnects mid-session.

1. **Push the twelve pending baseline tags.** Exact commands are in
   [`docs/CHECKPOINTS.md`](CHECKPOINTS.md#pending-checkpoint-tags):
   - **Original five (Stages 0-3b):** `baseline/stage-{0-ci-release,
     1-conpty-hello-world, 2-vt-parser, 3a-screen-model,
     3b-wpf-rendering}`.
   - **Seven added per PR #127 (post-Stage-3b shipped stages):**
     `baseline/stage-{4-uia-document-text-pattern,
     11-velopack-auto-update, 4b-process-cleanup-diagnostic,
     4a-claude-code-substrate, 5-streaming-coalescer,
     6-keyboard-input, 5a-diagnostic-logging}`.

   Tags don't trigger any workflow; they're rollback handles only.
   The dev sandbox can't push tags (proxy returns 403 on `refs/tags/`),
   so this is a maintainer-side action from a workstation. After
   pushing each tag, **delete the matching row from the "Pending
   checkpoint tags" table** in `docs/CHECKPOINTS.md` per the
   existing convention (avoids orphan rows in the table claiming
   tags exist that don't).

2. **Stage 4 + Stage 11 NVDA verification — complete except
   for one process-cleanup row.** The maintainer ran the
   relevant rows of
   [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
   on `v0.0.1-preview.22` (Stage 4 first pass), `.26` (Stage
   4 word-navigation re-verification + Stage 11 self-update
   verification) on Windows 11 + NVDA. Outcome:

   **Stage 4 (UIA Document + Text pattern + navigation):**

   - ✓ Document role announced on focus.
   - ✓ Review cursor reads current line, prev / next line,
     prev / next character, prev / next word.
   - ✓ Window title accessibility name (`NVDA+T`) reads
     "pty-speak terminal {version}" (version suffix shipped
     in PR #66).
   - ✓ Re-launch from Start menu works cleanly.
   - ↻ **Process-cleanup acceptance check — recurring cadence
     (per 2026-05-01 strategic review).** Alt+F4 / X-button
     close, then confirm neither `Terminal.App.exe` nor orphan
     `cmd.exe` remains. The bundled `Ctrl+Shift+D` hotkey runs
     the diagnostic via `scripts/test-process-cleanup.ps1`
     automatically; full procedure + lifecycle inflection
     points are in the "Launch and process hygiene" section of
     [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).
     Run order:
     1. ✓ **Baseline on `v0.0.1-preview.27` — PASS**
        (2026-05-01, via `Ctrl+Shift+D` diagnostic). Both
        close paths (Alt+F4 and X-button) reported no
        orphans. Confirms the shipped code through Stage
        4.5 PR-A had no pre-existing leak. (Note: NVDA
        reading of the spawned PowerShell window is
        unreliable in practice — `docs/SESSION-HANDOFF.md`
        item 6 tracks the screen-reader-native replacement
        path; the underlying script's PASS/FAIL output is
        the source of truth and was confirmed by the
        maintainer.)
     2. ↻ **After Stage 4a PR-B ships (`v0.0.1-preview.28`+
        carry the alt-screen back-buffer)** — re-run via
        `Ctrl+Shift+D` to confirm the alt-screen rework
        didn't introduce a process-lifecycle regression.
        Pending the next manual session on a preview that
        carries PR-B (already shipped to `main` via PR #86;
        cut whichever preview corresponds and run).
     3. ✓ **Post-Stage-5 preview — PASS** (2026-05-02, via
        `Ctrl+Shift+D` diagnostic on the preview that carries
        Stage 5 + the announce-before-launch fix). Both close
        paths (Alt+F4 and X-button) returned no orphans.
        Confirms the new coalescer task + Acc/9 OnRender
        refactor introduced no process-lifecycle regression
        (the coalescer's `cts.Cancel()` +
        `coalescedChannel.Writer.TryComplete()` ordering in
        `Program.fs compose ()`'s `app.Exit.Add` is the
        intended shutdown path and works correctly).
     4. After Stage 6 ships (most important pass — Stage 6
        is where Job Object lifecycle lands per
        `spec/tech-plan.md` §160).
     5. After Stage 7 ships (Claude Code spawns subprocesses;
        verify cascade cleanup).
     6. **Firm gate before any v0.1.0+ tag** (per
        `SECURITY.md` row PO-2). Until v0.1.0, prior passes
        accumulate evidence; if any regression appears, file
        against the most recent stage that touched lifecycle.

   **Stage 11 (Velopack auto-update):**

   - ✓ `Ctrl+Shift+U` from inside `preview.25` triggered the
     update flow.
   - ✓ NVDA narrated "Checking for updates" → "Downloading"
     → bucketed percent updates → "Restarting to apply
     update".
   - ✓ App restarted automatically at preview.26.
   - ✓ Post-restart, NVDA+T reads "pty-speak terminal
     0.0.1-preview.26" — audible confirmation the version
     actually flipped.
   - Negative paths (offline, network failure, repeat
     keypresses) covered by PR #66's structured error
     announcements; not yet manually exercised in
     verification, but failure-mode logic is exercised by
     the in-progress dedup test path.

   Both Stage 4 follow-ups discovered during verification
   have shipped:

   - ✓ **Word-navigation real implementation** — PR #68.
     `IsWordSeparator` is whitespace-only (space + tab);
     punctuation stays inside words so paths like
     `C:\Users\test>` read as one token. UAX #29 / vim-
     style is a future refinement only if user feedback
     warrants; see "Word boundaries" rationale at the top
     of `TerminalTextRange` in
     `src/Terminal.Accessibility/TerminalAutomationPeer.fs`.
   - ✓ **Window title version suffix** — PR #66.
     `MainWindow.xaml.cs` reads
     `AssemblyInformationalVersionAttribute` and sets both
     `Title` and `AutomationProperties.Name` to include
     the version. Strips any `+commit-sha` deterministic-
     build trailer.

   For Stage 4 / 11 NVDA failures going forward, file a
   fresh issue — a Stage 4 regression is most likely a
   `GetPattern`
   override regression on `TerminalAutomationPeer` per
   the Stage-4 diagnostic decoder.

3. **CI / release timing optimisation — partial completion.**
   Audit-cycle PR-E shipped the highest-leverage optimisation:
   `~/.dotnet/tools` is now cached across CI runs in both
   `ci.yml` and `release.yml`, so `dotnet tool install -g vpk`
   no longer re-downloads (~10s saved per run). Cache key is
   statically versioned; bump `v1 → v2` in the cache key
   when a new vpk version is wanted.

   Two candidate trims investigated and DEFERRED (low-value
   relative to restructuring cost):

   - **Merge two `gaurav-nelson/github-action-markdown-link-check`
     steps into one invocation.** The action takes either
     `folder-path` OR `file-path` (not both), so combining
     would require enumerating all 14 markdown files
     explicitly OR scanning the whole repo (which would
     break the deliberate `spec/` exclusion documented in
     the workflow comments). Savings: ~15-20s. Cost:
     either ugly file enumeration that drifts from reality
     as docs are added, or losing the `spec/`-immutable
     URLs exclusion. Not worth it; revisit if the action
     gains a multi-folder option.

   - **Audit `release.yml` for similar wins** (vpk pack
     input cache, gh release-download retry on transient
     5xx). The vpk-pack input is per-build artefacts (no
     cache opportunity). The gh-fetch retry would help
     on transient flakes but hasn't actually flaked in any
     of our release runs to date — defer until a flake
     happens. PR-E added a doc note in this file rather
     than guess-coding for a non-issue.

   Constraint preserved: `continue-on-error: true` on
   link checks (advisory by design), Velopack pack smoke
   step (catches packaging regressions before release
   day), `--locked-mode` once NuGet lock files land
   (item 4) all unchanged.

4. **Enable NuGet lock files (deferred from PR #41).** The
   investigation in PR #41 settled on enabling
   `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
   in `Directory.Build.props` so `dotnet restore` writes a
   `packages.lock.json` next to each project; CI then switches to
   `dotnet restore --locked-mode` (or `RestoreLockedMode=true`)
   to reject restores whenever the lock would change, surfacing
   transitive-dep drift as explicit lock-file regenerations.
   Deferred because the agent sandbox has no `dotnet`, so it
   can't generate the initial `packages.lock.json` files itself.
   Maintainer steps to land it in a future session:
   1. Add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
      to `Directory.Build.props`.
   2. Run `dotnet restore` from the repo root locally.
   3. Commit the generated `packages.lock.json` files (one per
      project under `src/` and `tests/`).
   4. In `.github/workflows/ci.yml`, change `dotnet restore` to
      `dotnet restore --locked-mode` so future runs assert the
      lock files are consistent.
   The `**/packages.lock.json` glob already in `ci.yml`'s cache
   key picks up the lock files automatically once they exist —
   no separate cache-key change needed.

5. **Audit-cycle SR-3 deferred follow-ups (not coded, tracked).**
   The security-audit cycle (SR-1..SR-3, November-December 2025)
   identified three items genuinely worth deferring rather than
   shipping inline. Each is tracked in `SECURITY.md`'s inventory;
   the items below are the action handles a future maintainer
   needs to actually close them. The 2026-05-03 strategic
   review (canonical:
   [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md))
   sequences PO-5 as the security half of Part 2 (Stage 7
   Claude Code roundtrip + env-scrub PO-5 — the validation
   gate before the framework cycles).

   1. **ConPTY environment scrub (PO-5) — sequenced as
      May-2026 plan Part 2.** Parent's full env block inherits
      to the child via `lpEnvironment=IntPtr.Zero` in
      `CreateProcess`, so sensitive vars (`GITHUB_TOKEN`,
      `OPENAI_API_KEY`, etc.) reach the child shell. To close:
      build an allow/deny-list inside `Terminal.Pty/Native.fs`,
      construct an env block from filtered entries, pass to
      `CreateProcess` via the `lpEnvironment` parameter. Risks:
      breaking developer workflows that depend on inherited
      `PATH` / locale variables; getting the F# string-block
      marshalling exactly right (must be double-NUL terminated,
      sorted order matters for some tools). Lands together
      with the Claude-Code-as-spawned-child wiring per the
      May-2026 plan; both pieces of Stage 7 ship as a single
      cycle so the env-scrub coverage is exercised against
      Claude Code's actual environment expectations during
      NVDA validation.
   2. **`install-latest-preview.ps1` TOCTOU (D-1, T-10).**
      A local attacker can swap the `Setup.exe` in `%TEMP%`
      between `Unblock-File` and `Start-Process`. Mitigation
      options: download to a path the attacker process can't
      write to (e.g. via per-user `%LocalAppData%` with strict
      ACLs); compute SHA-256 of the downloaded bytes and verify
      before running; or sign the file before run (which is the
      same v0.1.0+ work that makes the script unnecessary
      altogether). The script is dev-iteration tooling per
      `scripts/README.md`; defer until it's used outside that
      context.
   3. ✓ **`TerminalView.OnRender` `_screen` lock (Acc/9) —
      RESOLVED by Stage 5.** Bundled into the Stage 5 PR per
      the prior commitment ("Stage 5 will revisit"). `OnRender`
      now takes ONE `_screen.SnapshotRows(0, _screen.Rows)`
      snapshot at the start of the frame and walks that
      immutable copy in `RenderRow` / `DrawRun`, instead of
      calling `_screen.GetCell(row, c)` per cell (which
      re-entered the screen gate up to `Rows*Cols` times per
      render frame). Single gate acquisition per render; no
      measurable perf cost. The Stage 5 coalescer reads via
      the same `SnapshotRows` primitive, so the WPF render
      path and the coalescer agree on the snapshot-on-read
      contract.

6. **Diagnostic-launcher UX needs a screen-reader-native
   replacement — now actionable (Stages 5 + 6 shipped, PR
   #116 functional end-to-end).** Original ship is **Stage 4b**
   (`spec/tech-plan.md` §4b); the in-pty-speak rework path
   below is documented in §4b.4 as the deferred
   screen-reader-native replacement. PR #81 shipped
   `Ctrl+Shift+D` to launch
   `scripts/test-process-cleanup.ps1` in a separate
   PowerShell window, with the working hypothesis that the
   spawned conhost window would route the script's stdout
   through Windows' default screen-reader path. Manual
   verification on `v0.0.1-preview.27` found that **NVDA's
   reading of the spawned PowerShell window is unreliable**
   in practice — line-by-line stdout is the script's design
   but conhost's UIA exposure isn't on par with pty-speak's
   own `Document` + `Text` pattern peer.

   The right replacement is to run diagnostics **inside
   pty-speak itself** rather than spawning a separate
   process. As of PR #116 (streaming pipeline functional
   end-to-end) the foundations are all in place:

   - **Stage 6 keyboard-input-to-PTY (shipped).** The user
     can type `pwsh ./scripts/test-process-cleanup.ps1`
     directly into pty-speak's child shell.
   - **Stage 5 streaming announcements (functional via PR
     #116).** Output is actively narrated as it streams in
     via the well-tested UIA peer + `TerminalView.Announce`
     chokepoint, not just available via review cursor.
   - **Optional architectural alternative** if multi-instance
     launch becomes a desired feature: `Ctrl+Shift+D`
     could launch a second pty-speak instance whose child
     shell is the diagnostic, side-by-side with the
     primary instance. Requires multi-instance plumbing
     that isn't on the roadmap today.
   - **Or:** rewrite the diagnostic as F# in
     `Terminal.App.exe`, emitting announcements via the
     existing `TerminalSurface.Announce` chokepoint. Most
     accessible, but duplicates the logic.

   `Ctrl+Shift+D` stays in place as a usable-but-imperfect
   diagnostic until a screen-reader-native replacement
   lands; the `docs/ACCESSIBILITY-TESTING.md` "Diagnostic
   decoder for the launcher hotkeys" subsection notes the
   limitation so future runs aren't surprised. Filing this
   as a tracked issue (or folding into Part 1 spec hygiene
   via a "type the diagnostic into pty-speak directly"
   note in `ACCESSIBILITY-TESTING.md`) is a reasonable
   next move; deferred from the May-2026 plan because the
   diagnostic's verbose-readback experience will benefit
   from the Output framework cycle's per-profile tuning
   (Part 3) anyway.

7. **One-time bulk-delete of stale post-merge branches
   on `origin/`.** The post-Stage-4.5 hygiene audit
   identified 77 remote branches whose work has been
   squash-merged into `main`; the count has since grown
   to ~100 as the post-Stage-6 streaming-fix cycle added
   more merged-but-not-deleted branches. Every branch's
   PR has either `merged_at` or a `closed` state per the
   GitHub API. They've accumulated because the
   delete-branch-after-merge convention wasn't codified
   until PR #87 added it to CONTRIBUTING.md, and the rule
   has been observed inconsistently for sandbox-pushed
   branches the agent can't delete from origin.

   The agent sandbox cannot delete remote branches
   (proxy returns HTTP 403 on `git push --delete`), so
   this is a maintainer-side action. The full deletion
   list is bundled as
   `scripts/cleanup-stale-branches.sh` (shipped via
   PR #87). To execute:

   ```bash
   bash scripts/cleanup-stale-branches.sh
   ```

   Runs from any workstation with normal git push
   permissions. ~30 sec end to end. Idempotent (skips
   branches that have already been deleted via
   `ls-remote --exit-code` check). After it completes,
   `origin/` should have ~3 branches: `main`, the
   active hygiene PR's branch (if not yet merged), and
   any in-flight new work.

   The script can be deleted from the repo after the
   one-time cleanup finishes; future branch hygiene is
   covered by the per-PR convention in CONTRIBUTING.md.

## Working conventions on this repo

The written rules live in [`CONTRIBUTING.md`](../CONTRIBUTING.md):

- **PR shape, body template, branch naming, squash-merge,
  Conventional Commits** — see CONTRIBUTING.md → "Branching and pull
  requests".
- **CHANGELOG `[Unreleased]` discipline + per-release rewrite** —
  same section.
- **Checkpoint + rollback contract** — see
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md). Sandbox-specific
  workaround when the agent can't push tags is captured under
  "Sandbox + tools caveats" below.
- **Documentation policy** — `spec/` is immutable; observed platform
  quirks go in `docs/CONPTY-NOTES.md`; CONTRIBUTING.md → "Documentation
  policy" has the full rule set.

The handoff-specific addition: the user explicitly asked for **smaller
PRs** after the Stage 0 diagnostic loop. When in doubt, split.

## Sandbox + tools caveats

These are constraints of the agent runtime that aren't obvious from
the repo and that wasted time in the previous session before being
internalised.

### `dotnet` is not on the dev sandbox

The agent edits files but cannot build / restore / test locally —
all validation happens in CI on `windows-latest`. Implications:

- Compile-time bugs surface only after a push. Iteration loops on
  CI take a few minutes per round. **Read each file twice before
  pushing**; a missed `let rec`, an unused `open`, or a typo in a
  string literal becomes a 5-minute round-trip.
- F# struct field ordering for P/Invoke can't be locally validated
  against `Marshal.SizeOf`. Cross-check carefully against the C
  header order (existing `Terminal.Pty/Native.fs` is the reference).
- Unit tests can be reasoned about but not executed; FsCheck-style
  property assertions exercise paths the author may not have
  considered.

### CI logs

- The harness blocks `productionresultssa*.blob.core.windows.net` and
  `api.github.com`, so the agent **cannot fetch CI run logs
  directly**. WebFetch and curl both 403.
- When CI fails, ask the maintainer to paste the relevant log
  lines. **The maintainer uses a screen reader**: they can't
  `Ctrl+F` through a long log easily. Either:
  - Ask for the full log paste and grep server-side via `Bash`
    against the pasted content, or
  - Tell them which exact strings to look for (e.g. `error FS`,
    `error MSB`, `Failed!`, the test name) and ask for the
    surrounding 5-10 lines.

### Tag and release pushes

- The local git proxy returns 403 on **tag pushes** (any ref under
  `refs/tags/`). Branches push fine. The only way to land a tag is
  for the maintainer to push from their workstation. Hence the
  "Pending checkpoint tags" table in
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — the agent stages the
  exact push commands for the maintainer.
- General release-flow rules (Releases UI not `git push --tags`,
  `Target = main`, the two fail-fast gates) live in
  [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md).

### GitHub MCP

- When the GitHub MCP server is connected, the agent can read PRs,
  list checks, merge PRs, post comments, etc.
- The server can disconnect mid-session and **does not always
  reconnect**. When that happens:
  - Webhook-style events (subscribe_pr_activity) often keep
    flowing — they're a separate channel.
  - The agent falls back to "ask the maintainer" for every state
    query and merge.
  - This was the trigger for ending the previous session and
    writing this handoff.

### Stream idle timeouts

- Large file writes (~600+ lines) sometimes trigger
  "Stream idle timeout - partial response received". The model is
  fine — the transport isn't.
- Mitigation: write files in 100-200 line chunks via Write + Edit,
  or via shell heredocs that append in pieces. The maintainer is
  aware of this and will say "try smaller actions" if a long write
  hangs.

## Stage 7 implementation sketch (next)

> **Status:** Stage 7 is **next** per
> [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) Part 2 —
> sequenced as the **validation gate** before the Output / Input
> framework cycles begin. `spec/tech-plan.md` §7 has the canonical
> specification (launch resolution, environment baseline, expected
> Claude Code startup behaviour, validation criteria); this sketch is
> the pre-digested implementation plan layered on top per the
> May-2026 plan's framing (env-scrub PO-5 bundled in; "Stage 7
> issues inventory" feeds Parts 3 + 4). Mirrors the existing
> Stage 4 / Stage 11 sketch pattern (shipped — retained as
> reference) so a future session can promote this section to
> "shipped — retained as reference" once Stage 7 ships.

### Why Stage 7 is the validation gate

Stage 7 ships the maintainer's primary target workload — Claude
Code running inside pty-speak end-to-end. Without that validation,
the Output / Input framework cycles (Parts 3 + 4 of the May-2026
plan) would optimise for theoretical paradigms (Stream / REPL /
TUI / Form / Selection) without ground-truth signal from the
workload they're supposed to serve best. Concretely: Claude Code
is **Ink-rendered** (full-frame redraws on every state change),
uses **alt-screen + cursor visibility tricks** (Stage 4a substrate
validates this works), **renders Markdown** with code blocks +
lists + headings, presents **interactive prompts** (multi-choice,
text input, file selection — Stage 8 territory), produces
**structured streaming output** (assistant turns + tool calls +
tool results — Stage 5 / Output framework territory). Each
characteristic maps to a framework-design decision. Watching Claude
Code break (or not break) under the current Stage 5 generic
coalescer + Stage 6 pass-through input tells us what the
frameworks MUST handle correctly — far better than enumerating
paradigms in the abstract.

### Goal

Spawn Claude Code as the ConPTY child shell, complete a roundtrip
prompt → response, hear the streaming response via NVDA, navigate
the response with the review cursor, accept a tool-use
confirmation, and hear the result. Strip sensitive env-vars (PO-5)
before child spawn so secrets that reach pty-speak don't reach
Claude. Produce a "Stage 7 issues inventory" enumerating every
gap surfaced during NVDA validation — the inventory becomes
design input for the framework cycles.

### Implementation outline

1. **Resolve `claude.exe` per `spec/tech-plan.md` §7.1.**
   `where.exe claude` from the spawn entrypoint; if missing,
   spawn cmd.exe instead and announce a one-time "Claude Code
   not found on PATH; install it first" notice (don't fail the
   launch — pty-speak should work as a generic terminal
   regardless of whether Claude Code is installed).

2. **Configurable shell.** Add a single environment variable
   `PTYSPEAK_SHELL` (defaults to `cmd.exe`; Stage 7 sets it to
   `claude.exe` when found). Phase 2 user-settings TOML
   eventually exposes this via the menu/palette (Issue #112
   territory). Stage 7 ships the env var only — no UI yet.
   Logged in `docs/USER-SETTINGS.md` as a candidate setting.

3. **Build a child-process environment block**
   (`lpEnvironment` parameter to `CreateProcess` in
   `Terminal.Pty/Native.fs`) instead of inheriting the parent's
   full block via `lpEnvironment=IntPtr.Zero`:

   - **Allow-list** preserves the env-vars Claude Code needs
     per spec §7.2: `PATH`, `USERPROFILE`, `APPDATA`,
     `LOCALAPPDATA`, `HOME` (set to `%USERPROFILE%` if absent),
     plus the Claude Code knobs (`ANTHROPIC_API_KEY`,
     `CLAUDE_CODE_GIT_BASH_PATH`).
   - **Always set:** `TERM=xterm-256color`,
     `COLORTERM=truecolor`.
   - **Deny-list overrides allow-list** for the
     security-sensitive vars surfaced in `SECURITY.md` PO-5:
     any variable name matching `*_TOKEN`, `*_SECRET`, `*_KEY`
     (except the explicit `ANTHROPIC_API_KEY` allow),
     `*_PASSWORD` is dropped. Logged at `Information` level:
     "Env-scrub: stripped N variables before child spawn" —
     count only, never names or values (per the `SECURITY.md`
     logging-discipline contract; env-var names like
     `BANK_API_KEY` are themselves sensitive).
   - **F# string-block marshalling:** UTF-16, double-NUL
     terminated, sorted by name (Win32 convention). The
     `Terminal.Pty/Native.fs` env-block constructor needs
     careful unit tests because getting the marshalling wrong
     silently fails (child sees no env vars at all — confusing
     failure mode).

4. **NVDA-validate the end-to-end flow.** Manual matrix row in
   `docs/ACCESSIBILITY-TESTING.md` "Stage 7 — Claude Code
   roundtrip":

   - Launch pty-speak; Claude Code spawns; NVDA reads the
     welcome screen.
   - Type a prompt ("Say hi"); press Enter; Claude responds;
     NVDA reads the streaming response.
   - Use `Caps Lock+Numpad 7/8/9` (review cursor up / current /
     down) to navigate the response after it's complete.
   - Trigger an interactive prompt (e.g. ask Claude to edit a
     file → "Edit / Yes / Always / No" listbox); accept a
     choice; NVDA reads the result.
   - Inside Claude, type `Get-ChildItem env:` (PowerShell) or
     `set` (cmd) to enumerate the child's env block;
     **confirm sensitive vars from the deny-list are absent**
     (`GITHUB_TOKEN`, `OPENAI_API_KEY`, `AWS_*`, etc.).

5. **Capture a Stage 7 issues inventory.** As the maintainer
   NVDA-validates, every gap (broken / awkward / verbose /
   silent / "this should be a list but reads as text") goes
   into `docs/STAGE-7-ISSUES.md` (new file) with a brief
   category tag matching the framework taxonomy:
   `[output-stream]`, `[output-form]`, `[output-selection]`,
   `[output-earcon]`, `[input-suggest]`, `[input-buffer]`,
   `[review-mode]`, `[other]`. The inventory is the explicit
   design input for Parts 3 + 4 of the May-2026 plan — each
   framework-cycle Stage starts by reading this file.
   **Don't try to fix anything from the inventory in Stage 7;
   that's framework-cycle work. Stage 7's job is to surface,
   not solve.**

### Pre-digested decisions

- **Cmd.exe stays default.** Stage 7 makes Claude Code reachable,
  doesn't make it the default. Reasoning: a fresh-install user
  without Claude Code installed should still see a working
  terminal. The `PTYSPEAK_SHELL` env var (or future menu setting)
  flips it.
- **Env-scrub is allow-list-with-deny-list-override.** Pure
  deny-list would strip env-vars Claude Code might depend on
  that we haven't enumerated; pure allow-list would over-strip
  and break workflows. Allow-list-then-deny gives us conservative
  defaults plus a safety override for the patterns we know are
  sensitive (`*_TOKEN`, etc.).
- **`ANTHROPIC_API_KEY` is in the allow-list.** Claude Code is
  the primary target workload; stripping its auth would defeat
  the purpose. A future "guest mode" setting could deny it for
  sandboxed sessions — Phase 2 territory.
- **The env-scrub log line counts but never names/values.** Per
  `SECURITY.md`'s logging-discipline contract.
- **No spec-§7-deltas without explicit authorization.** Spec §7.2's
  environment baseline (TERM, COLORTERM, allow-list of
  PATH/USERPROFILE/etc.) is the authoritative source. Stage 7's
  implementation matches it; any deviation needs an ADR-style
  spec PR with maintainer authorization (parallel to the
  Stage 4a / 4b / 5a chunk in chat 2026-05-03).

### Critical files to touch

| File | Change |
|---|---|
| `src/Terminal.Pty/Native.fs` | New env-block constructor: allow-list filter + deny-list override + Win32 marshalling (UTF-16, double-NUL, sorted). Pass to `CreateProcess` via `lpEnvironment`. |
| `src/Terminal.Pty/PseudoConsole.fs` | Resolve `claude.exe` via `where.exe claude`; fall back to `cmd.exe` with one-time announcement. Read `PTYSPEAK_SHELL` env var to override. |
| `src/Terminal.App/Program.fs compose ()` | Wire the env-block constructor into the spawn path; log the env-scrub count at `Information` level. |
| `tests/Tests.Unit/Tests.Unit.fsproj` | Env-scrub fixture tests (allow-list preservation, deny-list pattern matching, marshalling round-trip). |
| `SECURITY.md` | PO-5 row flips from "planned" to "shipped" with the allow-list-with-deny-override scheme documented. |
| `docs/ACCESSIBILITY-TESTING.md` | New "Stage 7 — Claude Code roundtrip" matrix row. |
| `docs/STAGE-7-ISSUES.md` | New file; inventory grows as NVDA validation surfaces gaps. |
| `docs/USER-SETTINGS.md` | New "Default shell" candidate setting noting `PTYSPEAK_SHELL` as today's hardcoded knob. |

### Existing primitives to reuse

- **`PseudoConsole.create`** (`src/Terminal.Pty/PseudoConsole.fs`) —
  the 9-step ConPTY lifecycle wrapper from Stage 1. Just need
  to pass a different command line + env block.
- **`AnnounceSanitiser.sanitise`** (audit-cycle SR-2) — for any
  error-message interpolation surfacing during launch failure.
- **`Logger.get`** (`src/Terminal.Core/FileLogger.fs`) — for the
  Information-level env-scrub-count log call.
- **`ActivityIds`** (Stage 5) — `pty-speak.error` for the
  "Claude Code not found" announcement.
- **Stage 4a's parser substrate** — Claude Code's alt-screen +
  DECTCEM + truecolor SGR + DECSC/DECRC are already handled.
  Stage 7 just exercises the substrate, doesn't extend it.
- **Stage 6's keyboard input pipeline** — Claude Code's
  interactive prompts (DECCKM application-cursor mode,
  bracketed paste) are already encoded correctly.

### What this stage deliberately does NOT do

(Per [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md)
Part 2 "Out of scope":)

- **Fixing the gaps surfaced during NVDA validation.** Those
  become framework requirements, not Stage 7 hotfixes. Surface,
  don't solve.
- **Configurable shell beyond the `PTYSPEAK_SHELL` toggle.**
  Phase 2 user-settings work owns the menu/palette UI.
- **Distributing pty-speak with Claude Code bundled.**
  Packaging concern; Stage 7 expects Claude Code already
  installed on the user's `PATH`.
- **The "command output complete" prompt-redraw signal.**
  Strategic review §G assigned this to Stage 8; can land in
  Stage 7's tail if Claude's redraw rhythm benefits, but not
  required.

### Known risks

- **F# string-block marshalling silently fails.** Get the
  UTF-16 + double-NUL + sorted-order wrong and the child shell
  sees no environment at all — Claude Code probably crashes
  immediately or behaves bizarrely. The `Tests.Unit` round-trip
  fixture is the canary. Test the byte-level layout against a
  known input/output pair before any integration test.
- **Claude Code's NVDA experience may already exceed our
  coalescer's capacity.** Stage 5's `renderRows` design
  announces the whole rendered screen on every emit. Claude's
  Ink does whole-frame redraws ~10 Hz (per spec §7.3). The
  combination may produce so much speech that NVDA queues fall
  behind and the user can't keep up. **This is the headline
  finding the Stage 7 issues inventory will document** — it's
  the first foundational architecture decision the Output
  framework cycle (Part 3) addresses.
- **Spawned Claude Code's lifecycle may differ from cmd.exe.**
  Stage 6's Job Object cleanup with `KILL_ON_JOB_CLOSE` should
  still work, but Claude Code spawns subprocesses (npm, git,
  the file tools). The recurring acceptance check
  ("Pending action items" item 2 in this file) needs to run
  after Stage 7 ships specifically to verify cascade cleanup.
- **`where.exe claude` may resolve a stale wrapper.** Older
  Claude Code distributions (npm-installed, WSL) live in
  different paths. The npm version may not work under ConPTY.
  Document in `docs/STAGE-7-ISSUES.md` if encountered; redirect
  users to the official native installer per spec §7.1.

### Scope discipline

Stage 7 is **one PR** (with the env-scrub potentially as a fixup
commit if the F# marshalling needs iteration). The Stage 7 issues
inventory file grows over multiple manual-NVDA verification
cycles but doesn't block PR merge — first cut documents what's
broken at land time, subsequent passes refine.

If during implementation we discover that Claude Code's launch
needs more glue than expected (e.g. Git Bash discovery,
working-directory handling, native-installer path resolution
edge cases), spike the gluing first as a small isolated PR
before bundling into Stage 7. Same Stage-4-spike-then-PR
pattern that worked for Stages 4 and 11.

After Stage 7 ships and NVDA-validates, **the Output framework
cycle (May-2026 plan Part 3) starts** — its research phase
(`docs/research/OUTPUT-FRAMEWORK-PRIOR-ART.md`) reads the
Stage 7 issues inventory as design input.

## Queued before Output framework cycle starts

Items that don't belong to a specific Stage 7 PR but **must
land between Stage 7 closing (PR-D merge) and the Output
framework cycle's Phase 3.1 research starting**. Each is
small relative to a stage; the bundle exists because they're
the natural transition-window work — the doc surface needs
to be tidy before the framework cycles spawn their own RFC
+ research files into it.

### Doc-purpose audit + reorganisation

**Status: shipped via PR-L (Stage 7 wrap-up bundle).** Captured here
as historical reference for the design rationale; the audit's
deliverables are now live in [`docs/DOC-MAP.md`](DOC-MAP.md), the
slimmed [`CLAUDE.md`](../CLAUDE.md), the audience-organised
[`README.md`](../README.md) Quick links section, and the expanded
[`CONTRIBUTING.md`](../CONTRIBUTING.md) "F# gotchas learned in
practice" (which absorbed the four CLAUDE.md-unique entries —
delegate conversion, record literal type inference, NUL bytes,
sequence-in-match-arm).

The original design notes that drove the audit:

**Why now (and not earlier or later):** the doc set has
organic-grown overlap. Adding `CLAUDE.md` (PR #133) clarified
one boundary (Claude Code session rules vs. human-contributor
PR conventions) but exposed others — content about sandbox /
tooling constraints lives in both `CLAUDE.md` and
`docs/SESSION-HANDOFF.md` "Sandbox + tools caveats"; F# /
.NET 9 gotchas live in both `CLAUDE.md` and `CONTRIBUTING.md`
"F# gotchas learned in practice"; accessibility non-negotiables
live in both `CLAUDE.md` and `CONTRIBUTING.md` "The non-
negotiable accessibility rules". The duplication is harmless
today (~3 files) but compounds as the framework cycles spawn
new docs (`docs/research/OUTPUT-FRAMEWORK-PRIOR-ART.md`,
`docs/RFC-OUTPUT-FRAMEWORK.md`, parallel for Input). Cleaning
up the principles before those cycles land sets cleaner
structure for them to slot into.

**Why not before PR-D ships:** Stage 7 is the validation
gate. The May-2026 plan's explicit sequencing rule is
"validation before architecture before code." Doc-set
re-architecture is meta-architecture and shouldn't preempt
the validation work in flight. PR-C (hot-switch hotkeys)
is also architecturally heavy (mid-session ConPtyHost
teardown + respawn + UIA continuity decisions); context-
switching to doc reorg right before tackling that is bad
timing.

**Guiding principle the audit will codify:** **each doc has
exactly one audience and one stage of contribution; cross-
references go between them rather than content duplicating
into them.**

**Audience + stage-of-contribution table** (captured here
for review; the actual reorg PR will commit this into
`README.md` or a dedicated `docs/DOC-MAP.md`):

| Doc | Audience | When read |
|---|---|---|
| `README.md` | Anyone hitting the GitHub repo | First contact; routes to the right next doc per audience |
| `CLAUDE.md` | Claude Code agents | Every session start (auto-loaded) |
| `CONTRIBUTING.md` | Human contributors opening PRs | When opening a PR |
| `docs/SESSION-HANDOFF.md` | Next Claude Code session | Session-to-session continuity (mutable, ephemeral) |
| `spec/*.md` | Architecture review | When changing design (immutable; ADR for edits) |
| `docs/PROJECT-PLAN-YYYY-MM.md` | Strategic planning | When planning a cycle (dated; status-as-of when stale) |
| `docs/HISTORICAL-CONTEXT-*.md` | Debugging archived patterns | Backup reference only — NOT primary handoff |
| `docs/ARCHITECTURE.md` | First-time code navigator | Code orientation |
| `docs/ROADMAP.md` | Quick "what's next" scan | High-level glance |
| `docs/CHECKPOINTS.md` | Maintainer at release / rollback | Release cut or stable-baseline rollback |
| `docs/CONPTY-NOTES.md` | Platform-specific debug | ConPTY-issue triage |
| `docs/USER-SETTINGS.md` | Contributor adding a hardcoded constant | When introducing config-shaped values |
| `docs/ACCESSIBILITY-TESTING.md` | Maintainer cutting a release | Manual NVDA pass |
| `docs/STAGE-N-ISSUES.md` | Framework-cycle research phases | Design input for the cycle |
| `CHANGELOG.md` | Release history | Every PR + release cut |
| `SECURITY.md` | Threat-model review | Security questions |

**Concrete deliverables of the audit PR:**

1. New `docs/DOC-MAP.md` (or expanded section in `README.md`)
   with the audience + stage-of-contribution table above as
   the canonical "which file should I open?" index, with
   one-line guiding principles per file.
2. Cross-reference cleanup: each doc references the others
   it depends on rather than duplicating their content.
   Specifically: `CLAUDE.md`'s F#-gotcha section becomes a
   pointer-with-summary into `CONTRIBUTING.md`; `CLAUDE.md`'s
   sandbox-constraints section becomes a pointer-with-summary
   into `docs/SESSION-HANDOFF.md`; `CLAUDE.md`'s accessibility
   non-negotiables become a pointer-with-summary into
   `CONTRIBUTING.md`. The Claude-runtime-specific layer
   (sandbox quirks, MCP behaviour, ask-for-CI-logs rule,
   webhook auto-subscribe) stays in `CLAUDE.md` because it's
   genuinely Claude-only.
3. `README.md` "Quick links" section reorganised by audience
   (per-audience entry-point lists) rather than the current
   flat list.
4. Deduplication pass: any rule that appears in N>1 files
   gets canonicalised into the single owning doc; the others
   get a short pointer + 2-3-line summary. Owning docs by
   topic:
   - **Working rules** (PR shape, branch naming, fixup-commit
     rhythm, accessibility-as-acceptance, walking-skeleton
     discipline) → `CONTRIBUTING.md` is canonical.
   - **F# / WPF / .NET 9 gotchas** → `CONTRIBUTING.md`
     "F# gotchas learned in practice" is canonical;
     `CLAUDE.md` indexes the canonical entries with one-line
     summaries pointing at line numbers.
   - **Sandbox / tooling constraints + Claude-runtime
     specifics** → `CLAUDE.md` is canonical (purely Claude-
     audience; no human-contributor relevance).
   - **Strategic sequencing + cycle plans** →
     `docs/PROJECT-PLAN-YYYY-MM.md` is canonical.
   - **Stage-by-stage spec** → `spec/tech-plan.md` is
     canonical (immutable).
5. `docs/SESSION-HANDOFF.md` adjusted to focus on its core
   audience: the next Claude Code session continuing from
   where the previous left off. Generic Claude-runtime rules
   that don't change session-to-session move to `CLAUDE.md`.
   "Where we left off" + "Pending action items" + the
   active-stage implementation sketch stay here (mutable,
   session-state).

**Estimated scope:** one PR, ~150 lines of edits across 4–6
docs, no code changes. Mechanical-merge approach (similar to
PR #120's CHANGELOG `[Unreleased]` consolidation) with
content-hash verification before/after to prove no rule was
lost in the dedup.

**Trigger:** PR-D (`docs/STAGE-7-ISSUES.md` seeding) merges
to main, at which point Stage 7 is closed. Maintainer signs
off "Stage 7 substrate validated"; this audit PR is the
first thing on the docket before Phase 3.1 research begins.

## Stage 11 implementation sketch (shipped — retained as reference)

> **Status:** Stage 11 has shipped. The sketch below is the
> pre-digested execution plan that drove PRs #63 and #66; it's
> preserved as reference for the architectural reasoning
> (especially the Velopack `VelopackApp.Build().Run()` ordering
> requirement and the structured-error-announcement contract)
> but is no longer the active plan.

Stage 11 was re-prioritised to land next — see "Where we left off"
and `docs/ROADMAP.md` "Stage ordering" for the rationale.
`spec/tech-plan.md` §11 has the full spec; this sketch is the
pre-digested implementation plan.

**Goal:** `Ctrl+Shift+U` from inside the running app downloads the
next preview's delta nupkg and restarts within ~2 seconds, with
NVDA progress announcements throughout. Replaces the standalone
`scripts/install-latest-preview.ps1` for in-place updates.

**Implementation outline:**

1. Add `Velopack` NuGet reference to `Terminal.App.fsproj`
   (already in scope per `spec/tech-plan.md` §0; the reference may
   already be transitive through the Velopack tooling, verify
   first).

2. In `Terminal.App/Program.fs`, replace the existing bare
   `VelopackApp.Build().Run()` with the full update protocol:
   - `VelopackApp.Build().Run()` stays first (must run before any
     WPF type loads — Velopack issue #195).
   - After WPF startup, construct `UpdateManager` pointing at the
     GitHub Releases source for our channel.
   - On `Ctrl+Shift+U`, kick a background task that calls
     `CheckForUpdatesAsync` → `DownloadUpdatesAsync` (with progress
     callback) → `ApplyUpdatesAndRestart`.

3. Wire the keybinding via `MainWindow.xaml`'s `InputBindings`
   (a `KeyBinding` with `Modifiers="Control+Shift"` and `Key="U"`).
   Or in code-behind on `MainWindow.xaml.cs`. The keybinding must
   not pass through to the PTY (Stage 6's input pipeline isn't
   shipped yet but design for forward compat).

4. NVDA progress announcements: each phase ("Checking for
   updates", "Downloading X.Y.Z", "X% downloaded", "Restarting")
   raises a UIA Notification event with `displayString` set. The
   Stage 4 `TerminalAutomationPeer` is the natural raise point —
   it's already focused per PR #60.

5. Failure surfaces (no update available, network failure,
   signature mismatch once signing returns): each becomes a
   distinct NVDA announcement, never a silent failure. Use
   structured error matching on Velopack's exceptions, not
   string contains.

6. FlaUI integration test in `tests/Tests.Ui/`: launch the app,
   send Ctrl+Shift+U via UIA `IInvokeProvider` or keyboard
   simulation, assert the Notification event fires. Don't
   assert actual download/restart in CI (that needs a real
   GitHub release to exist) — file as a manual smoke row.

7. Manual smoke matrix row in `docs/ACCESSIBILITY-TESTING.md`
   Stage 11 section already exists; the diagnostic decoder I
   added in PR #58 covers the failure modes.

**Pitfalls already captured in `spec/tech-plan.md` §11.5:**
- Don't request elevation (Velopack discussion #8 — manifest must
  be `asInvoker`).
- `VelopackApp.Build().Run()` MUST be first, or you get the
  endless restart loop from Velopack issue #195.
- Test installer flow on a clean VM (the Stage-4 manual matrix
  installer pass already does this).

**Scope discipline:** Stage 11 is one PR. If during implementation
we discover Velopack's API needs more glue than expected, that's
a sign to spike first (the same Stage-4 spike-then-PR pattern
that worked for PRs #51-#56). Don't combine Stage 11 with Stage 5
or any other stage.

## Stage 4 implementation sketch (shipped — retained as reference)

> **Status:** all of Stage 4 has merged on `main` as of PR #60.
> The sketch below is the pre-digested execution plan that drove
> PRs #54-#56 + #59 + #60; it's preserved as reference for the
> architectural reasoning (especially the WM_GETOBJECT vs
> AutomationPeer.GetPattern pivot in PR #56) but is no longer the
> active plan.

`spec/tech-plan.md` §4 has the full spec; the spec is immutable per
the documentation policy. This sketch is the pre-digested
implementation plan that captures decisions made *during execution*
without contradicting the spec.

**Goal**: NVDA's review cursor (Caps Lock + Numpad 7/8/9) reads the
terminal content character / word / line at a time. No streaming
announcements yet — just static text exposure. Validation tools:
Inspect.exe, Accessibility Insights, FlaUI for unit tests, NVDA for
manual sign-off.

### Why split Stage 4 into a spike plus three PRs

The original sketch (pre-this-revision) called for one PR delivering
the peer + provider + FlaUI test together at "~250-400 lines." On
re-reading the `ITextProvider` / `ITextRangeProvider` interfaces and
weighing what this session learned about the codebase's CI iteration
cost, that's roughly half the realistic line count and bundles
three review concerns (interop, navigation, integration tests) into
one PR. The split below keeps each PR small, reviewable, and
independently revertible.

### Spike: F# WPF AutomationPeer + C# interface implementation

**Risk being settled.** The codebase has a documented F#-interop
foot-gun (`out SafeFileHandle&` byref silently produces a runtime
`NullReferenceException`; see `Terminal.Pty/Native.fs`). Stage 4
introduces two more F#-meets-C# boundaries the project has never
exercised:

1. F# class subclassing `System.Windows.Automation.Peers.FrameworkElementAutomationPeer`
   (a C# class with `protected virtual` overrides).
2. F# class implementing `System.Windows.Automation.Provider.ITextProvider`
   (a C# interface with `[Variant]` / `[CLSCompliant]` attributes).

A 30-line throwaway PR proves the interop compiles before we build
250+ lines on top. Discard or absorb into PR 4a depending on outcome.

**Scope**: replace `Terminal.Accessibility/Placeholder.fs` with a
minimal `TerminalAutomationPeer.fs` that subclasses
`FrameworkElementAutomationPeer`, overrides
`GetAutomationControlTypeCore` returning `Document`, implements
`ITextProvider` with all methods returning `null` / no-ops. Push,
verify CI is green. **Don't wire it into `TerminalView` yet.**

**Pass condition**: build green, no F# interop errors, no
`TreatWarningsAsErrors` failures.

**Outcome (PR #47, merged 2026-04-29).** The spike validated that
F# can:
- Subclass `FrameworkElementAutomationPeer` and override the five
  parameterless `*Core` methods (`GetAutomationControlTypeCore`,
  `GetClassNameCore`, `GetNameCore`, `IsControlElementCore`,
  `IsContentElementCore`) with no nullability or interop friction.
- Implement `ITextProvider` (6 members) and `ITextRangeProvider`
  (17 members) cleanly via `interface ... with` syntax. Empty
  arrays via `Array.empty<_>` and null returns via
  `Unchecked.defaultof<_>` both work for these interface
  implementations.
- Add `<UseWPF>true</UseWPF>` to `Terminal.Accessibility.fsproj`
  to bring in `WindowsBase` / `PresentationCore` /
  `UIAutomationProvider` / `UIAutomationTypes`.

**Resolved during PR #48 (Stage 4a) — `GetPatternCore` is not
reachable from external assemblies.** What looked like an F#-only
nullability puzzle in the spike turned out to be a more
fundamental visibility problem affecting every external caller,
F# and C# alike.

The decisive evidence came from a diagnostic in PR #48 that
removed the override entirely and replaced it with a non-override
method calling `base.GetPatternCore(...)` from within a subclass
of `FrameworkElementAutomationPeer`. CI failed with C# **CS0117**
("'FrameworkElementAutomationPeer' does not contain a definition
for 'GetPatternCore'"). C#'s view of the type via the public
.NET 9 reference assembly does not expose the protected
`GetPatternCore` member. The earlier CS0115 / FS0855 errors were
both surface expressions of the same underlying fact: there is
no method on `FrameworkElementAutomationPeer` (as visible from
this build environment) for any subclass to override.

Microsoft's documented examples that override `GetPatternCore`
appear to compile against internal Microsoft assemblies where
the protected metadata is visible. The public reference assembly
ships the type without the override target.

Implication: any path to exposing the Text pattern from this
project has to bypass the `AutomationPeer.GetPatternCore`
extension point. The `Terminal.Accessibility.Interop` C# shim
project that PR 4a originally built was deleted as part of the
reduced-scope cleanup — it doesn't help, because the same
visibility limit applies regardless of which language hosts the
override attempt.

### PR 4a (reduced scope) — UIA Document role + identity

Shipped a peer that exposes the terminal element with the right
role and name, without the Text pattern.

- `TerminalAutomationPeer.fs` extends `FrameworkElementAutomationPeer`
  and overrides only the five parameterless `*Core` methods:
  - `GetAutomationControlTypeCore` returns `Document`
  - `GetClassNameCore` returns `"TerminalView"`
  - `GetNameCore` returns `"Terminal"`
  - `IsControlElementCore` returns `true`
  - `IsContentElementCore` returns `true`
- `TerminalView.OnCreateAutomationPeer` returns the peer.
- No `ITextProvider` / `ITextRangeProvider` implementation
  (unreachable without `GetPatternCore`).
- No `Screen` reference passed to the peer (no `GetText` to
  feed yet).

**What this gives NVDA**: the element appears in the UIA tree,
NVDA announces "Terminal, document" when focus reaches the
window, and Inspect.exe / FlaUI can find the element by
`ClassName="TerminalView"`. NVDA review-cursor reads on the
element will produce no text — the Text pattern isn't there.

### Stage 4 follow-up — Text-pattern exposure (in flight)

The Text pattern is the actual user-visible win — without it
NVDA can't read the buffer contents. Investigation status:

- **Option 2 ruled out (PR #50, FS0855).** `TextBlockAutomationPeer`
  has the same `GetPatternCore` visibility limit as
  `FrameworkElementAutomationPeer`. The AutomationPeer extension
  point for `GetPatternCore` is closed across all subclasses in
  the .NET 9 WPF reference assembly set, not just one specific
  parent class. There is no specialized `*AutomationPeer` to
  subclass that opens up the override.

- **Option 1 (current path): `TerminalView` implements
  `IRawElementProviderSimple` directly.** The COM-style raw UIA
  provider interface lives in `System.Windows.Automation.Provider`
  and IS visible to external assemblies (the PR #47 spike's
  interface implementations compiled cleanly there). The
  interface exposes patterns via
  `IRawElementProviderSimple.GetPatternProvider(int)` — taking a
  UIA pattern ID (an int constant) instead of the
  `PatternInterface` enum that the AutomationPeer override path
  required. Routes around the protected-member visibility limit
  entirely.

  Implementation shape for the next PR:
    * `TerminalView : FrameworkElement` (C#) implements
      `IRawElementProviderSimple` directly: the four interface
      members are `ProviderOptions`, `GetPatternProvider(int)`,
      `GetPropertyValue(int)`, and `HostRawElementProvider`.
    * `GetPatternProvider(UIA_TextPatternId /* 10014 */)` returns
      a `TerminalTextProvider` instance from F#.
    * `GetPropertyValue(int)` returns Document role / ClassName /
      Name properties — the same identity the
      `TerminalAutomationPeer` already reports, but on a
      different code path.
    * `HostRawElementProvider` returns the AutomationPeer's host
      provider so WPF's standard tree integration still works.
    * The F# `TerminalTextProvider` and `TerminalTextRange` types
      from the deleted PR #48 attempt can be revived; they
      compiled cleanly.
  Tracked in [Issue #49](https://github.com/KyleKeane/pty-speak/issues/49).

- **Option 3 ruled out (PR #52 reflection probe).** The runtime
  metadata strips `GetPatternCore` the same way the public
  reference assembly does — a reflection probe via
  `BindingFlags.Instance | NonPublic | Public | FlattenHierarchy`
  on `FrameworkElementAutomationPeer` finds zero matches for
  `GetPatternCore`. Sanity baseline (`GetClassNameCore` IS
  findable via the same probe) confirms the test infrastructure
  isn't the cause. Reflection-based binding is therefore not a
  viable architectural path. The probe is preserved as a
  regression sentinel in
  `tests/Tests.Ui/AutomationPeerReflectionTests.fs`; if a future
  .NET update exposes `GetPatternCore` in runtime metadata,
  the sentinel test fails and we know to re-evaluate.

The reduced PR #48 peer that ships the Document role + identity
stays; the IRawElementProviderSimple path adds Text on top
without removing what's already working.

### PR 4b — Navigation semantics

Implements the `ITextRangeProvider` methods that Stage 4 actually
needs for review-cursor navigation, against the contract NVDA tests
in practice.

- `Move(unit: TextUnit, count: int)` — moves both endpoints by N
  units of the requested kind.
- `MoveEndpointByUnit(endpoint, unit, count)` — moves one endpoint.
- Units to support: `Character`, `Word`, `Line`, `Paragraph`,
  `Document`. `Format` falls through to `Character`.
- Word boundaries follow the simple convention: a "word" is a
  contiguous run of non-whitespace cells. Whitespace boundaries are
  not announced; that matches NVDA's expectations for terminals.
- `Paragraph` and `Document` are functionally equivalent at this
  stage (no scrollback yet); both expand to the full grid.
- `Compare`, `CompareEndpoints`, `Clone`, `ExpandToEnclosingUnit`
  go from no-op stubs to real implementations.
- Manual smoke: NVDA Numpad 1/2/3 (char), 4/5/6 (word), 7/8/9
  (line) all announce the right thing. No infinite loops.

**Pass condition**: NVDA review-cursor navigation on the cmd.exe
startup banner reads each row's visible text, word-by-word and
character-by-character, without repeating or skipping.

### PR 4c — FlaUI integration test

First UIA test in `tests/Tests.Ui/`. Validates the producer-side
contract (what we expose) without depending on a real screen reader.

- Add `FlaUI.Core` and `FlaUI.UIA3` PackageReferences to
  `tests/Tests.Ui/Tests.Ui.fsproj`. Both are already pinned in
  `Directory.Packages.props`.
- New test: `Application.Launch` against
  `src/Terminal.App/bin/Release/net9.0-windows/Terminal.App.exe`
  (the framework-dependent build output, *not* the published
  self-contained binary — `dotnet test` runs before the publish
  step in `ci.yml`). Wait for the main window. Attach via
  `UIA3Automation`. Find the descendant by `ClassName=TerminalView`.
- Assertions:
  - `ControlType = Document`.
  - `Patterns.Text.IsSupported = true`.
  - `Patterns.Text.Pattern.DocumentRange.GetText(int.MaxValue)` is
    non-empty (cmd.exe will have produced its prologue by the
    time the test polls).
- Tear down: kill the spawned process; FlaUI handles UIA cleanup.
- Risk: FlaUI on `windows-latest` requires the interactive desktop
  session. GitHub Actions runs the build job on an interactive
  session by default but this PR is the first time the project
  actually uses it. If 4c's CI fails for desktop-session reasons,
  the fallback is to run the FlaUI test only via
  `workflow_dispatch` until we can validate it under
  `actions/runner` configurations.

**Pass condition**: green CI on `windows-latest`; the test runs
the actual app, attaches via UIA, and reads non-empty document
text.

### Snapshot rule (already in place)

UIA calls into the provider on a different thread from the WPF
Dispatcher. Mutating the buffer while UIA reads = crash (see
`Terminal.Accessibility` design notes in `spec/overview.md`). The
substrate landed in PR #38: `Screen.SnapshotRows(startRow, count)`
returns `(int64 * Cell[][])` under the same lock as `Screen.Apply`,
and `Screen.SequenceNumber` exposes the monotonic counter. Each
`ITextRangeProvider` constructor takes a `(sequence, snapshot)`
tuple and compares against `screen.SequenceNumber` later to detect
staleness. No additional locking is required in
`Terminal.Accessibility`.

### Manual NVDA validation (Stage 4 acceptance)

Documented in `docs/ACCESSIBILITY-TESTING.md`. NVDA+Numpad 7 (prev
line), Numpad 8 (current line), Numpad 9 (next line), Numpad 4/5/6
(word), Numpad 1/2/3 (character). The "NVDA" modifier is Caps Lock
or Insert depending on the user's NVDA layout setting; the canonical
notation is "NVDA+Numpad N". Should hear the visible terminal text.
"Broken" sounds like NVDA saying "TerminalView" then nothing
(no pattern) or "blank" (empty range) or repeating a line forever
(Move not advancing).

### What Stage 4 deliberately does NOT do

Guard against scope creep:

- **No streaming announcements** — that's Stage 5
  (`UiaRaiseNotificationEvent`).
- **No SGR attribute exposure on text ranges** — `GetAttributeValue`
  returns `NotSupported` for everything in Stage 4. Stage 5 wires
  up `UIA_ForegroundColorAttributeId` etc.
- **No selection lists / list provider** — Stage 8.
- **No keyboard input → PTY** — Stage 6.
- **No `RaiseNotificationEvent` calls** — Stage 5. (Threading
  reminder for then: any `RaiseNotificationEvent` call must run on
  the Dispatcher thread. `TermControlAutomationPeer.cpp` in
  microsoft/terminal is the reference; F# has the same constraint.)
- **`IsContentElement = true` on all rows for now** — will likely
  need to be `false` on rows that are part of a List subtree once
  Stage 8 lands.

## Recommended reading order for a new session

1. **[`CLAUDE.md`](../CLAUDE.md)** — auto-loaded standing
   instructions for every Claude Code session. Working rules
   (one concern per PR, ask-for-CI-logs-don't-guess, squash-merge
   default, `mcp__github__create_pull_request` for the
   auto-subscribe webhook, etc.), F# 9 + .NET 9 gotchas
   (nullness at API boundaries, record-literal type inference,
   `out SafeHandle&` byref interop, escape literals for NUL bytes
   in source, F# delegate conversion), the app-reserved hotkey
   contract, the accessibility non-negotiables, and the current
   stage sequencing index. Read this first; it indexes
   everything below.
2. **This file** (SESSION-HANDOFF.md).
3. **[`docs/PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md)** —
   the current strategic plan revision (snapshot 2026-05-07);
   supersedes the original `PROJECT-PLAN-2026-05.md` per
   Track E E5 dated-snapshot discipline. Captures the
   substrate-first shift, audit phase outcome, and forward
   sequencing through Tier 2 SessionModel persistence + Phase 2
   input framework. Supersedes the per-stage ordering of
   `spec/tech-plan.md` for Stages 7-10 specifically; the spec
   remains immutable as architectural rationale. The original
   `PROJECT-PLAN-2026-05.md` stays preserved as the 2026-05-03
   historical snapshot.
4. [`CONTRIBUTING.md`](../CONTRIBUTING.md) — PR shape, branching,
   CHANGELOG discipline, F# / WPF gotchas, documentation policy.
   Working conventions all live here.
5. [`spec/tech-plan.md`](../spec/tech-plan.md) §1–§6 plus the
   retroactively-formalized Stages **4a** (Claude Code rendering
   substrate), **4b** (process-cleanup diagnostic), and **5a**
   (diagnostic logging surface) — establishes the architectural
   grain. §7 is the **next stage**; the in-flight implementation
   plan lives in this file's "Stage 7 implementation sketch
   (next)" section. §8–§10 are the original-plan stages that
   the May-2026 plan reshapes (see plan doc for sequencing —
   Stages 8 and 9 fold into the Output framework cycle; Stage
   10 ships post-frameworks as their first consumer).
6. [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) — what's stable, what
   tags need pushing, how rollback works.
7. [`docs/CONPTY-NOTES.md`](CONPTY-NOTES.md) — observed platform
   quirks. Render-cadence finding is the one most likely to bite
   again.
8. [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) "Common pitfalls"
   section — every diagnostic loop's lessons end up here.
9. Skim [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` for
   in-flight work and the most recent shipped section for the
   last release narrative shape.
10. Browse `src/` top-down: `Terminal.Core` (data) → `Terminal.Pty`
    (ConPTY) → `Terminal.Parser` (VT500) → `Views` (WPF) →
    `Terminal.App/Program.fs` (composition).

Tests are in `tests/Tests.Unit/` (xUnit + FsCheck) — `SmokeTests`,
`ConPtyHostTests` (Windows-only, runtime-skipped elsewhere),
`VtParserTests`, `ScreenTests`. `tests/Tests.Ui/` is a placeholder
that Stage 4 will populate with FlaUI.

## Closing notes

The previous session pushed through Stages 0 → 3b plus the CI
hygiene PR (#34) over a long arc of iteration. Most of the time
loss came from (a) the Stage-0 release-pipeline diagnostic loop
(`v0.0.1-preview.{1..14}` — root cause: PowerShell heredoc
indentation breaking workflow YAML, now caught by `actionlint`),
and (b) a few rounds of F# / WPF interop quirks at stage
boundaries.

The maintainer is patient and engaged but uses a screen reader and
has limited time for back-and-forth on individual log lines —
prefer asking for whole-log paste over "search for X then paste a
snippet". Small focused PRs with detailed test plans are
appreciated; sprawling PRs with multiple moving parts are not.

The May-2026 cleanup cycle (PRs #118 → #128, all merged) closed
out Part 1 of [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md)
and produced the handoff infrastructure that makes this file
useful as a starting point: the canonical plan doc itself, the
Stage 7 implementation sketch, the spec stage-numbering hygiene
(Stages 4a/4b/5a in spec), the rollback-tag rows for all shipped
post-Stage-3b stages, and the F# 9 nullness + fixup-commit-rhythm
gotchas in `CONTRIBUTING.md`. The cycle also exercised the
fixup-commit-on-open-PR rhythm (PR #121 hit FS3261 in CI; fixup
commit on the same branch closed it) and confirmed that the
small-focused-PR cadence the maintainer prefers works smoothly
when paired with the squash-merge convention. The next session
starts at Part 2 — Stage 7.

Thanks for picking it up. The accessibility differentiation
(`UIA_ForegroundColorAttributeId`, semantic prompts, listbox
exposure) is what actually makes this project worth shipping —
Stages 4–10 are where that work happens; Stage 7 is next.
