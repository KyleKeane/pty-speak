# Project plan — 2026-05-09 (successor revision)

> **Snapshot**: 2026-05-09
> **Status**: successor to [`PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md) (the 2026-05-07 revision); supersedes its "Next stage" pointer; companion successor under the original plan's "Future revisions should land as new dated plans" discipline (Track E E5 resolution: Option B continued)
> **Companion docs**:
> - [`PROJECT-PLAN-2026-05-revision.md`](PROJECT-PLAN-2026-05-revision.md) — preceding 2026-05-07 revision; preserved verbatim per dated-snapshot discipline for decision-history continuity
> - [`PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) — original 2026-05-03 plan; preserved verbatim
> - [`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md) — Track F audit; canonical "ready-to-pick-up" list
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md), [`SESSION-MODEL.md`](SESSION-MODEL.md), [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md), [`PANE-MODEL.md`](PANE-MODEL.md), [`CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md), [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) — substrate research-stage docs (6)

## What this document is

A successor to `PROJECT-PLAN-2026-05-revision.md` capturing
the **post-Tier-2 + post-Cycle-25 state** as of 2026-05-09.
The 2026-05-07 revision's "Next stage" pointer ("SessionModel
Tier 1 implementation") has been satisfied end-to-end — Tier 1
shipped via Cycles 11-22b (PRs #185-#199), Tier 2 persistence
shipped via Cycles 24a-24g (PRs #203-#212+#219), and the
operational ergonomics + diagnostic-dump bundle shipped via
Cycle 25 (PRs #220-#222). The 2026-05-09 NVDA-matrix
walkthrough validated all of it.

Per the 2026-05-07 revision's own discipline ("When the next
substantial shift happens... a further successor lands; this
revision gets preserved in the same way"), this is a separate
dated doc. The preceding revision is preserved verbatim.

## Status update 2026-05-09 (post Cycle 27 closure)

**This document was written at Cycle 25d closure** (initial spawn
of the dated successor) and pointed "Cycle 26: App menu (NEXT)"
in the Sequencing section. As of 2026-05-09 evening, **Cycles
26 (app menu) and 27 (multi-state menu paradigm) have both
shipped end-to-end on `main`** (PRs #225–#229). Cycle 28 (the
Window menu + foundation cleanup PR carrying this update) ships
alongside.

**What Cycles 26 + 27 actually delivered vs. the sketch in
"Next stage" below:**

- ✅ Cycle 26 shipped as a 4-PR mini-cycle (26a/b/c/d) rather
  than the 3-5 sketched. The hotkey-source-of-truth question
  was resolved as "parallel surface" — `TerminalView.AppReservedHotkeys`
  stays as the C#-side hot-path mirror, and a new
  `AppReservedHotkeysMirrorTests.fs` pins the F#/C# parity
  invariant at test time. ADR-style note in
  `docs/ACCESSIBILITY-TESTING.md` Cycle 26 section.
- ✅ Cycle 26b option-typed `Hotkey.Key`/`Modifiers` so a
  command can be `(None, None)` → menu-only. Cycle 26c shipped
  the first menu-only command (`RunProcessCleanupScript`, the
  interactive `test-process-cleanup.ps1` launcher). Cycle 28
  added `CloseWindow` + `ExitApp`, the second + third
  menu-only commands.
- ✅ Cycle 27 introduced the **multi-state menu paradigm** (a
  parallel `MultiStateCommand` DU + `bindMultiState` helper)
  and migrated `MuteEarcons`/`ToggleDebugLog` to it. Both
  keyboard accelerators (`Ctrl+Shift+M`, `Ctrl+Shift+G`) were
  dropped as part of the migration — further reducing the
  hotkey-count working-memory ceiling that motivated Cycle 26.

**Next strategic stage** is the **Output framework cycle (Part 3)**
per the original sequencing — see updated "Sequencing" section
below. The framework cycle's first move should be a plan-mode
RFC sub-stage that consults `docs/STAGE-7-ISSUES.md` and
`spec/event-and-output-framework.md` with framework-design
intent. The Cycle 26 + 27 menu surface gives the framework
cycle a natural place to land per-output-class user toggles
(e.g. earcon-volume, suppression-rules) without burning hotkey
slots.

## Why this revision exists

The preceding revision was authored 2026-05-07 with status
header pointing next stage at "SessionModel Tier 1
implementation". Two days later, that pointer is stale by
three implementation cycles:

1. **Cycle 11-22b** — Tier 1 SessionModel substrate
   (PRs #185-#199, 2026-05-07/08) — shipped end-to-end with
   maintainer NVDA-validation green throughout.
2. **Cycle 24a-24g** — Tier 2 SessionModel persistence
   (PRs #203-#212+#219, 2026-05-09) — TOML schema, JSONL
   serializer, bounded-channel async file writer,
   Always-mode synchronous flush, env-var value sanitisation,
   `Ctrl+Shift+S` diagnostic hotkey + NVDA matrix,
   persistence-config diagnostic, TOML model snapshot logged
   at startup.
3. **Cycle 25 / 25a / 25b-1 / 25b-1a** — operational
   ergonomics + diagnostic-dump bundle (PRs #220-#222,
   2026-05-09) — hotkey reorg (`Ctrl+Shift+L/P/E` rebound),
   open-config auto-create, reload-on-shell-switch,
   `[logging]` TOML section, `Ctrl+Shift+D` bundles
   diagnostic dump (FileLogger log + config + redacted env +
   session-log summary into dated snapshot file +
   clipboard), `Ctrl+Shift+T` placeholder removed,
   `Ctrl+Shift+L` removed (subsumed by D's bundle).

The SESSION-HANDOFF "Where we left off" note flagged this
gap explicitly: "New dated revision of
`PROJECT-PLAN-2026-05-revision.md` overdue per E5 discipline
(current revision is the 2026-05-07 snapshot; multiple cycles
have shipped since)." This revision closes that follow-up.

## Status as of 2026-05-09

### Shipped since the preceding revision

**SessionModel Tier 1 substrate (Cycles 11-22b, PRs #185-#199)**:

- **#185** — substrate skeleton (Cycle 11; CanonicalState extension,
  `SessionModel.T` record, in-memory tuple list, per-shell-session
  model, `Memory` persistence-mode default).
- **#186** — OSC 133 producer + cursor field (Cycle 12).
- **#187** — state machine + composition (Cycle 13;
  Idle / Active / WaitingForOutput transitions).
- **#189** — heuristic fallback + alt-screen wiring (Cycle 14;
  cmd / PowerShell / Claude per-shell heuristics for shells that
  don't emit OSC 133).
- **#190** — `PromptText` capture (Cycle 15).
- **#191** — diagnostic battery extension (Cycle 16; SessionModel
  state surfaced into `Ctrl+Shift+D`).
- **#192** — tick-driven detector via channel-driven actor model
  (Cycle 17; the canonical channel-architecture pattern).
- **#193** — `CHANNEL-ARCHITECTURE.md` doc (Cycle 18; sister to
  the other research-stage docs).
- **#194** — default-shell config + detector consolidation
  (Cycle 19).
- **#195** — row-index-aware emission (Cycle 20).
- **#196** — announce-wording followup (Cycle 21).
- **#197** — `CommandText` + `OutputText` extraction (Cycle 22).
- **#198** — multi-match flap fix (Cycle 22a).
- **#199** — `Ctrl+Shift+Y` clipboard hotkey (Cycle 22b).

**SessionModel Tier 2 persistence (Cycles 24a-24g, PRs #203-#212+#219)**:

- **#203** — TOML schema (Cycle 24a; `[session_model.persistence]`
  table, `PersistenceMode` DU, no I/O yet).
- **#206** — JSONL serializer (Cycle 24b; pure
  `formatTupleAsJsonl`).
- **#208** — bounded-channel async file writer (Cycle 24c;
  mirrors `FileLogger`).
- **#209** — Always-mode synchronous flush (Cycle 24d-1).
- **#210** — env-var value sanitisation for SessionTuple
  persistence (Cycle 24d-2).
- **#211** — `Ctrl+Shift+S` diagnostic hotkey + NVDA matrix
  (Cycle 24e; announces active session-log file path).
- **#212** — persistence-config diagnostic + matrix typo fix
  (Cycle 24f).
- **#219** — TOML model snapshot logged at startup (Cycle 24g).

**Cycle 25 operational ergonomics + diagnostic-dump bundle (PRs #220-#222)**:

- **#220** — Cycle 25a; hotkey reorg (`Ctrl+Shift+L/P/E`),
  open-config auto-create, reload-on-shell-switch, `[logging]`
  TOML section.
- **#221** — Cycle 25b-1; `Ctrl+Shift+D` bundles diagnostic dump
  (FileLogger log + config + redacted env into dated snapshot
  file at `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\` +
  clipboard); `Ctrl+Shift+T` placeholder removed.
- **#222** — Cycle 25b-1a; bundle gains `--- SESSION LOG ---`
  section; `Ctrl+Shift+L` removed (subsumed by D's bundle).

**Cycle 25c — doc-only handoff refresh (PR #223)**:

- **#223** — `docs/SESSION-HANDOFF.md` "Where we left off"
  refreshed through Cycle 25b-1a; "Next stage" cell repointed
  at the candidate-cycle picker.

**Cycle 25d plan refresh (this PR)**:

- This PR; spawns the dated successor doc (`PROJECT-PLAN-2026-05-09.md`),
  sweeps cross-references in 7 files, adds CHANGELOG entry. No code
  change; pure planning-doc hygiene per E5 dated-snapshot discipline.

### Open questions resolved since the preceding revision

The 2026-05-07 revision noted ~14 maintainer-pending questions
remaining (7 SESSION-MODEL Q1-Q4 + Q6-Q8; 7 CUSTOMIZATION-MODEL
Q1-Q7). Status update:

- **7 SESSION-MODEL questions (Q1-Q4 + Q6-Q8)** — all resolved
  during the Tier 1 plan-mode cycle (Cycles 11-22b); answers
  baked into shipped substrate. SESSION-MODEL.md §"Open
  questions" section reflects resolutions.
- **7 CUSTOMIZATION-MODEL questions (Q1-Q7)** — all resolved
  via Cycle 23 walk-through (PR #201, 2026-05-08).
- **5 CHANNEL-ARCHITECTURE questions (Q1-Q5)** — flagged as
  tentative since the doc shipped; not formally walked through
  yet. Listed below as remaining.

### Open questions remaining

- **5 CHANNEL-ARCHITECTURE.md questions (Q1-Q5)** — still
  tentative; will firm up as Phase 2 / Tier 3 substrate cycles
  approach (input-keystroke channel, AI-summarisation channel,
  etc.). Not blocking Cycle 26.
- **7 spec E.5.1-E.5.7 questions** in `spec/event-and-output-framework.md`
  — explicitly marked "do not block the spec from landing now";
  pin at Stages 9b/9c/9d as the substrate cycles approach.
- **CUSTOMIZATION-MODEL implementation** — design resolved (PR #201);
  implementation deferred to a multi-cycle phase paired with
  Phase 2 framework work per the original sequencing.

### Audit health

Per `AUDIT-BACKLOG-VALIDATION.md` snapshot 2026-05-07
(refreshed 2026-05-09 inline for Tier 1 closure):
- **0 ❌ structural contradictions** across all 5 prior audit
  tracks.
- **14 ✅ shipped** items (up from 11 at original snapshot).
- **3 ✅+ partially shipped** (down from 5).
- **11 📋 pending**, **3 ⏸ deferred**, **2 🔄 superseded**, **0 ❓
  orphaned**.
- Substrate is **healthier post-Tier-2**. New items 32
  (CHANNEL-ARCHITECTURE), 33 (default-shell config), and the
  Cycle 24 + Cycle 25 sub-items shipped post-snapshot.

### NVDA validation status

- **Tier 1 SessionModel** — maintainer NVDA-validated row-by-row
  during Cycles 11-22b plus the Cycle 22b `Ctrl+Shift+Y`
  clipboard hotkey on 2026-05-08.
- **Tier 2 persistence** — maintainer ran the Cycle 24e NVDA
  matrix on 2026-05-09; surfaced operational friction that drove
  Cycle 25 (hotkey reorg + diagnostic-dump bundle).
- **Cycle 25 bundle** — maintainer paste-back-validated the
  bundle format on a real `Ctrl+Shift+D` press during the
  Cycle 24e walkthrough; format is bedded in.
- **Cycle 25c plan refresh (this PR)** — doc-only; no NVDA
  matrix row required.

## What changed in the strategic frame since 2026-05-07

The 2026-05-07 revision's frame was **substrate-first** — name +
design + audit before implementation. That shift produced a
healthy substrate by 2026-05-07 (six research-stage docs +
six-track audit). The 2026-05-09 frame is the **post-substrate
implementation acceleration**: three implementation cycles
shipped end-to-end in two calendar days, validated by NVDA, with
the substrate vocabulary intact throughout. The discipline held
under load.

The next strategic question is **how to reduce
working-memory pressure as feature surface grows** without
stalling the framework cycles. The maintainer flagged
hotkey-count pressure during Cycle 25 (~14 reserved
`Ctrl+Shift+letter` gestures, plus three numeric shell-switch
hotkeys, plus future-reserved `Ctrl+Shift+M` and `Alt+Shift+R`).
That ceiling is the gating factor for adding more
diagnostic-script entries (release-notes drafter,
process-cleanup script, future scripts) and for adding more
built-in commands.

**The Cycle 26 app menu directly addresses this ceiling.** It
absorbs the existing hotkey-gesture surface into a discoverable
menu framework with associated keyboard shortcuts, freeing the
"add another hotkey" axis from working-memory ceiling pressure
and creating a natural surface for future scripts to plug in
without a per-script hotkey.

## Next stage: Cycle 26 — app menu

**Chosen by maintainer 2026-05-09** (over Cycle 25b-2 FlaUI E2E
runner and over jumping straight to framework cycles).

### Cycle 26 scope (sketch — full plan-mode pass before code)

- **WPF Menu surface**: top-of-window menu with discoverable
  command catalogue. Plays well with NVDA's menu-mode reading
  patterns (alt-key activation, arrow nav, item announcement).
- **Hotkey ↔ menu integration**: existing `AppCommand` DU +
  `AppReservedHotkeys` table become the source of truth feeding
  both the `OnPreviewKeyDown` filter (existing) AND the new
  menu surface (new). The `MenuItem.InputGestureText` decoration
  reads from the same table so menu and hotkey labels stay in
  sync.
- **Diagnostic-script surfacing**: `scripts/test-process-cleanup.ps1`
  becomes a menu item under e.g. "Diagnostics → Process
  cleanup test (interactive)…"; future diagnostic scripts plug
  into the same submenu without burning hotkey slots.
- **Accessibility hard problems** to resolve in plan mode:
  - NVDA menu-announcement pattern (focus routing, alt-letter
    access keys, arrow-nav, accelerator announcement).
  - `AutomationProperties` plumbing on the menu surface so
    review-cursor + browse mode work correctly.
  - The `OnPreviewKeyDown` filter ordering contract — load-bearing
    per spec §6 + `CLAUDE.md` "App-reserved hotkey contract" —
    must continue to fire for reserved gestures BEFORE menu
    accelerators (or merge with them coherently).
  - Whether the menu becomes the source of truth for the
    gesture table (single source) or stays as a parallel
    surface (decorative `InputGestureText` only).

### Cycle 26 estimated scope

- Multi-PR mini-cycle, likely **3-5 sequenced PRs**:
  1. Menu skeleton + WPF surface + AutomationProperties
     plumbing; no command wiring.
  2. Wire existing `AppCommand` DU entries into menu items;
     verify hotkey-filter ordering invariant with tests.
  3. Add diagnostic-scripts submenu; surface
     `test-process-cleanup.ps1`.
  4. NVDA matrix row for menu-mode navigation +
     accessibility validation.
  5. (Optional) absorb the hotkey table into the menu as
     single source of truth, or document the parallel-surface
     decision explicitly.
- Estimated total: ~600-1200 LOC across the mini-cycle.
- Plan-mode cycle resolves the hotkey-source-of-truth question
  + the menu-mode accessibility patterns BEFORE implementation.

## Sequencing post-Cycle-25

```
✅ Cycle 11-22b: SessionModel Tier 1 implementation (PRs #185-#199)
✅ Cycle 23:     Doc cleanup + CUSTOMIZATION-MODEL Q&A (PRs #200-#201)
✅ Cycle 24a-24g: SessionModel Tier 2 persistence (PRs #203-#212+#219)
✅ Cycle 25a-25b-1a: Operational ergonomics + diagnostic-dump bundle (PRs #220-#222)
✅ Cycle 25c:    SESSION-HANDOFF "Where we left off" refresh (PR #223; doc-only)
✅ Cycle 25d:    Plan refresh + link sweep (PR #224; doc-only)
✅ Cycle 26a:    App menu skeleton + UIA plumbing (PR #225)
✅ Cycle 26b:    14 AppCommands wired into menu items + Hotkey.Key/Modifiers option-typing
                 + AppReservedHotkeysMirrorTests F#/C# parity invariant (PR #226)
✅ Cycle 26c:    First menu-only AppCommand RunProcessCleanupScript surfaced via
                 Diagnostics → Test Process Cleanup (PR #227)
✅ Cycle 26d:    Cycle 26 NVDA matrix rows + How-to-add-a-menu-item recipe in
                 USER-SETTINGS.md (PR #228)
✅ Cycle 27:     Multi-state menu paradigm canon + EarconsMode/LoggingLevel migration
                 + dropped Ctrl+Shift+G/M (PR #229; single-PR architectural cycle)
✅ Cycle 28:     Window menu (Close Window + Exit) + Program.fs Ctrl+Shift+L stale-
                 comment cleanup + this plan refresh (THIS PR)
─────────────────────────────────────────────────────────────────────
Cycle 29+:       Output framework cycle (Part 3 per the original strategic plan)
                 - Subsumes the original spec/tech-plan.md Stages 8 + 9.
                 - Designed in spec/event-and-output-framework.md.
                 - First sub-stage: plan-mode RFC consulting STAGE-7-ISSUES.md +
                   event-and-output-framework.md with framework-design intent.
                 - Multi-cycle work; plan-mode cycle per sub-stage.
                 - Per-output-class user toggles can land in the menu surface
                   established by Cycles 26 + 27 (e.g. earcon-volume,
                   suppression-rules) without burning hotkey slots.
Cycle X+:        Input framework cycle (Part 4 per the original strategic plan)
                 - Echo correlation; cursor-aware editing; InputPathway protocol;
                   ReplPathway.
                 - Unblocks Up/Down history navigation + history-paste workflows.
                 - Multi-cycle work; plan-mode cycle per sub-stage.
Cycle Y+:        Stage 10 — review-mode + quick-nav
                 - First non-built-in framework consumer per Part 5 of the original plan.
Cycle Z+:        CustomizationModel implementation (likely paired with Phase 2 to
                 share substrate).
Cycle AA+:       Phase 3 (AI summarisation; semantic segmentation; spatial audio).
```

### Deferred / parked

- **Cycle 25b-2** — FlaUI/UIA-driven E2E test runner. Original
  Cycle 25b plan called for a single PR combining the dump
  bundle + the FlaUI runner; the bundle shipped first (#221 +
  #222) and the FlaUI half is parked. Trigger to revisit:
  maintainer wants the autonomous-validation discipline OR a
  regression slips through manual NVDA validation. Until then,
  the bundle's manual paste-back loop is the validation pathway.
  Design sketch preserved in `docs/SESSION-HANDOFF.md` "Cycle
  25 in-flight handoff" section.

## What this revision does NOT change

- **Preceding revision body** preserved verbatim per its own
  discipline. Read it for the 2026-05-07 snapshot of the
  substrate-first shift + audit-phase sequencing decisions.
- **Original 2026-05-03 plan** preserved verbatim per the same
  discipline.
- **Spec immutability** — per `CLAUDE.md`, `spec/` edits still
  require ADR-style authorisation.
- **Walking-skeleton discipline** — every cycle ships +
  validates against NVDA before the next begins.
- **Single-concern PR discipline** — each cycle is one PR (or a
  sequenced multi-PR mini-cycle for larger work; Cycle 26 is
  expected to be the latter).
- **Cycle-number naming convention** chosen 2026-05-08 — `Cycle
  N` / `Cycle Na/b/c` for PR titles, branch names, commit
  messages, CHANGELOG entries; Tier-N labels remain in design
  docs as historical decision-context only.

## Open follow-ups (forward-looking)

These pre-existed Cycle 25 + survive into the post-Cycle-25
window; tracked through the strategic backlog and `SESSION-HANDOFF`:

- **Screen-buffer runtime resize** (Phase 2 stage).
- **Stable-baseline tag pushes** including
  `baseline/stage-7-claude-roundtrip` and the Cycle-24e
  candidate per `docs/CHECKPOINTS.md`. Local sandbox can't push
  tags; awaiting maintainer sweep.
- **MAY-4.md Concern 3** (navigable streaming response queue) —
  naturally subsumed by SessionModel + ReplPathway (Phase 2);
  no separate cycle required.
- **Velopack release cadence** — maintainer cuts previews from
  `main` whenever convenient. The last release predating Tier 1
  was `v0.0.1-preview.43`; subsequent previews have shipped
  through the substrate + Tier 1 + Tier 2 + Cycle 25 cycles.
- **Cross-platform port** (item 21) — deferred indefinitely;
  Avalonia (NOT MAUI) is the recommended target if/when this
  happens.
- **`pty-speak-replay` CLI** (item 4) — depends on canonical-state
  serialization (item 2); deferred to a future cycle that pairs
  the two.

## Where to read more

For the **canonical post-audit ready-to-pick-up list**:
[`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md)
§ "Ready-to-pick-up list".

For **substrate-research vocabulary** (12-stage pipeline,
SIM, three-component model, Pane abstraction, channel-decision
framework, customization principle): the six research-stage
docs cited in the front matter (PIPELINE-NARRATIVE,
SESSION-MODEL, INTERACTION-MODEL, PANE-MODEL,
CUSTOMIZATION-MODEL, CHANNEL-ARCHITECTURE).

For **historical decision context** (why the substrate-first
shift took the shape it did): `PROJECT-PLAN-2026-05-revision.md`
+ the original `PROJECT-PLAN-2026-05.md`.

For **the active "Where we left off"** between sessions:
[`SESSION-HANDOFF.md`](SESSION-HANDOFF.md). Mutable; refreshed
per cycle.

## Versioning + maintenance

This is a **dated successor** doc, the second in the
`PROJECT-PLAN-2026-05-*` chain (after `-revision.md`). When the
next substantial shift happens (e.g. Cycle 26 ships + framework
cycles begin in earnest), a further successor lands; this
revision gets preserved in the same way. Naming convention going
forward: dated YYYY-MM-DD suffix
(e.g. `PROJECT-PLAN-2026-MM-DD.md`) for unambiguous chronological
ordering. The original `PROJECT-PLAN-2026-05.md` (date-only) and
the first successor `PROJECT-PLAN-2026-05-revision.md` (ad-hoc
suffix) are preserved with their original names per
"no edits in place" discipline.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-09 | Cycle 25d (this PR; E5 dated-snapshot discipline successor) | Initial successor doc. Captures post-Tier-2 + post-Cycle-25 state: Tier 1 SessionModel substrate (Cycles 11-22b, PRs #185-#199), Tier 2 persistence (Cycles 24a-24g, PRs #203-#212+#219), Cycle 25 operational ergonomics + diagnostic-dump bundle (PRs #220-#222) all shipped end-to-end with NVDA-validation green. SESSION-MODEL Q1-Q4 + Q6-Q8 + CUSTOMIZATION-MODEL Q1-Q7 resolved during Tier 1 plan-mode + Cycle 23 walk-through respectively. Next stage points at **Cycle 26 — app menu** (maintainer-chosen 2026-05-09) as the working-memory-pressure-relief cycle gating further hotkey-bound features. Cycle 25b-2 FlaUI/UIA E2E runner parked; design sketch preserved in SESSION-HANDOFF. Output framework Part 3 + Input framework Part 4 + Stage 10 sequenced after Cycle 26 per the original strategic plan. |
