# Audit: Atlas Alignment

> **Snapshot**: 2026-05-07
> **Status**: audit / inventory document — no code change; no atlas edits in this PR; recommendations tiered for follow-up cycles
> **Authoring item**: Track D of the comprehensive audit phase
> **Companion docs**:
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) — Track A (code-vs-research-doc)
> - [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) — Track B (test layer)
> - [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) — Track C (spec layer)
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — the parameter atlas (audit subject)
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational substrate vocabulary
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking history substrate
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing
> - [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition substrate
> - [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching + PR conventions

## Why this exists

Track D of the substrate-first audit closes the loop in
the parameter direction: validate **`docs/USER-SETTINGS.md`
parameter atlas** against **actual code constants**.

The atlas was substantively augmented in PR #167 (post-
release-build UX feedback 2026-05-06) to capture the 5 UX
issues as future parameters with proposed defaults +
NVDA-collaboration matrix. PR #168 then shipped three of
those proposals (`bulk_change_threshold`, `backspace_policy`,
`mode_barrier_flush_policy`) into `Config.fs` and
`StreamPathway.fs`. The atlas was NOT updated to reflect
the implementation — its "Current state" sections still
describe the pre-PR-#168 world.

This audit catches that. Findings:
- 3 stale section headers / "Current state" bodies referring
  to pre-PR-#168 hardcoded states.
- 2 low-priority orphans (code constants not in atlas).
- 0 contradictions.

The atlas is otherwise comprehensive + well-aligned; the
1547-line USER-SETTINGS.md is a substantial document and
its overall structure is healthy.

## What this document is

A snapshot-dated **classification + triage report** for
atlas alignment, paralleling Tracks A / B / C. Audit-only;
no atlas edits, no code changes. Recommendations are
tiered; a small follow-up PR can apply the doc-side fixes
mechanically.

## Methodology

Audit performed via static inspection 2026-05-07. Steps:

1. **Read `docs/USER-SETTINGS.md`** in full (1547 lines)
   to inventory atlas entries.
2. **Read `src/Terminal.Core/Config.fs`** (TOML loader) +
   `src/Terminal.Core/StreamPathway.fs` (`Parameters` record
   + `defaultParameters`) to inventory shipped TOML
   parameters with their actual defaults.
3. **Cross-check** every atlas entry against its claimed
   code citation; verify status tag (✅ / 📋 reserved).
4. **Sample hard-coded constants** from
   `EarconPalette.fs`, `FileLogger.fs`, `ConPtyHost.fs`,
   `Program.fs` (heartbeat), and verify each has either an
   atlas entry or a defensible "platform-internal; no
   atlas entry needed" rationale.
5. **Cross-reference research-stage docs** —
   PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL /
   PANE-MODEL — to confirm the atlas captures all
   parameters those docs reserve.

The audit does NOT execute code or change any file. Pure
static cross-reference.

## Findings summary

**Atlas alignment with code (snapshot 2026-05-07):**

| Tag | Count | Meaning |
|---|---|---|
| ✅ aligned | 17 | Atlas entry matches current code default + status; section header current. |
| ⚠ stale or missing | 5 | 3 stale-section findings (atlas "Current state" describes pre-PR-#168 world) + 2 low-priority orphans (code constants not in atlas). |
| ❌ contradiction | 0 | No atlas claim contradicts code in a way that needs architecture-level reconciliation. |
| 📋 forward-looking openness | 11 | Reserved entries for Phase 2 / 3 (echo correlation, cursor announcement, ActivityId routing, earcon-palette tuning, keybindings TOML, etc.). |

**Headline**: atlas alignment is **good**. The 3 stale-
section findings cluster on the same root cause: PR #167
added the atlas entries describing what PR #168 was about
to ship; PR #168 shipped them but didn't update PR #167's
"Current state" prose. Doc-side fix is mechanical.

## Atlas inventory

### Shipped TOML parameters (✅ aligned with Config.fs + StreamPathway.fs)

8 parameters, all with `Config.fs` loader entries +
`StreamPathway.Parameters` defaults + atlas entries.
Verified 2026-05-07.

| TOML key | Default (code) | Code citation | Atlas section | Status |
|---|---|---|---|---|
| `[shell.<id>] pathway` | (`stream` for cmd / powershell / claude) | `Config.fs:505-508` | Pathway selection | ✅ aligned |
| `[pathway.stream] debounce_window_ms` | 200 | `StreamPathway.fs:155` | Pathway selection | ✅ aligned |
| `[pathway.stream] spinner_window_ms` | 1000 | `StreamPathway.fs:156` | Pathway selection | ✅ aligned |
| `[pathway.stream] spinner_threshold` | 5 | `StreamPathway.fs:157` | Pathway selection | ✅ aligned |
| `[pathway.stream] max_announce_chars` | 500 | `StreamPathway.fs:158` | Pathway selection | ✅ aligned |
| `[pathway.stream] color_detection` | true | `StreamPathway.fs:159` | Pathway selection | ✅ aligned |
| `[pathway.stream] bulk_change_threshold` | 3 | `StreamPathway.fs:160` | Suffix-diff parameters | ⚠ stale "Current state" body (see Drift D2) |
| `[pathway.stream] backspace_policy` | AnnounceDeletedCharacter | `StreamPathway.fs:161` | Suffix-diff parameters | ⚠ stale section header + body (see Drift D3) |
| `[pathway.stream] mode_barrier_flush_policy` | SummaryOnly | `StreamPathway.fs:162` | Suffix-diff parameters | ⚠ stale section header + body (see Drift D4) |

Note: 8 distinct parameter *keys* + per-shell `pathway`
selection (3 keys) = 11 atlas entries; the table groups by
parameter type for readability.

### Reserved atlas entries (📋 — forward-looking, no code home expected today)

11 entries in the atlas correctly marked 📋 reserved
because the substrate is forward-looking:

| Atlas entry | Substrate | Phase |
|---|---|---|
| `stream.echoCorrelation.policy` | Phase 2 input framework | future |
| `stream.cursorAnnouncement.enabled` + `.mode` | Phase 2 ReplPathway prerequisite | future |
| `notification.activityIds.inputEcho` | Phase 2 input framework | future |
| `notification.processing.inputEcho` | Phase 2 input framework | future |
| `diagnostic.heartbeatIntervalMs` | Phase 2 hotkey-tunable diagnostic | future |
| `Word boundaries` (whole section) | Phase 2/3 review-mode quick-nav | future |
| `Visual settings` (whole section) | Phase 2 theme TOML | future |
| `Audio` (earcon-palette TOML) | Phase 2 earcon-palette tuning | future |
| `Default shell` | Phase 2 pre-launch shell config | future |
| `Keybindings` (whole section) | Phase 2/3 user-binding TOML | future |
| `Update behaviour` | Phase 2 auto-update opt-out | future |

All correctly tagged. No action.

### Hard-coded constants in code, not in atlas

Sampled across `EarconPalette.fs`, `FileLogger.fs`,
`ConPtyHost.fs`, `Program.fs`. Most are correctly
documented in the atlas; two are orphans worth adding as
📋 reserved.

| Constant | Code citation | Value | Atlas status | Verdict |
|---|---|---|---|---|
| `bell-ping` frequency / duration | `EarconPalette.fs:53-55` | 800Hz × 100ms | Mentioned in §Audio + §Suffix-diff §pre-wired-palette | ✅ aligned (📋 reserved tuning is captured in §Intentionally not user-configurable line 1536) |
| `error-tone` frequency / duration | `EarconPalette.fs:57-59` | 400Hz × 150ms | Mentioned in §Audio | ✅ aligned |
| `warning-tone` frequency / duration | `EarconPalette.fs:61-63` | 600Hz × 120ms | Mentioned in §Audio | ✅ aligned |
| `FileLoggerOptions.RetentionDays` | `FileLogger.fs:86` | 7 | Mentioned in §Current state of configuration line 31 | ✅ aligned (📋 reserved as Phase 2 tuning) |
| `FileLoggerOptions.ChannelCapacity` | `FileLogger.fs:88` | 8192 | **Not in atlas** | ⚠ orphan (Drift D5) |
| `ConPtyHost` `FileStream` buffer | `ConPtyHost.fs:137,138,173` | 4096 bytes | **Not in atlas** | ⚠ orphan (Drift D6) |
| Heartbeat interval | `Program.fs:1453` | 5000ms | Documented in §Diagnostic surfaces line 774 | ✅ aligned |
| `MAX_OSC_RAW` | `StateMachine.fs:40` | 1024 | Documented in §Intentionally not user-configurable line 1513 | ✅ aligned (intentionally NOT user-tunable; security guard) |

### Research-doc cross-reference

PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL /
PANE-MODEL all align with the atlas — no parameter
documented in research docs is missing from the atlas:

- **PIPELINE-NARRATIVE stage parameters** (debounce,
  spinner-window, spinner-threshold, max-announce-chars,
  bulk-change-threshold, backspace-policy,
  mode-barrier-flush-policy): all in atlas as shipped.
- **PIPELINE-NARRATIVE auxiliary** (mode-barrier handling
  policy): in atlas (with stale "Current state"; see D4).
- **SESSION-MODEL substrate parameters** (bounded-history
  size, persistence mode, OSC 133 detection mode): NOT yet
  in atlas; correctly absent because substrate is not
  implemented (Phase 2; SESSION-MODEL.md is the design
  doc; atlas absorbs when implementation begins).
- **INTERACTION-MODEL parameters** (interactive-element
  trigger thresholds): NOT yet in atlas; correctly absent
  (forward-looking).
- **PANE-MODEL parameters** (per-pane TOML schema): NOT
  yet in atlas; correctly absent (forward-looking).

No drift surfaced.

## Drift findings

### D1: ~~mode_barrier_flush_policy default mismatch~~ — superseded

The Phase 1 exploration flagged `mode_barrier_flush_policy`
as a "default mismatch" (atlas claims `verbose`; code has
`SummaryOnly`). Closer inspection: the **atlas's "What
configurability would look like" section** correctly proposes
`SummaryOnly` as the default. The mismatch is in the
**section header** (`(currently verbose)`) and the
**"Current state" body**, which describe the pre-PR-#168
behaviour. Reframed as D4 below — same finding, more
precisely diagnosed.

### D2: `bulkChangeThreshold` "Current state" body stale

**Atlas**: USER-SETTINGS.md line 893 says:
> `src/Terminal.Core/StreamPathway.fs:364` defines
> `BulkChangeThreshold` as a top-level `let private = 3`
> binding.

**Code reality** (post-PR-#168): `BulkChangeThreshold` is
a field on `StreamPathway.Parameters` (line 142), not a
top-level `let private` binding. Default at
`StreamPathway.fs:160`. PR #168 lifted it into the
`Parameters` record; the atlas's "Current state" wasn't
updated.

**Triage**: Tier 1 (doc-fix). Atlas section header
("currently 3") is fine. Update lines 893-894 to reflect
the parameter-record location.

### D3: `backspacePolicy` section header + body stale

**Atlas**: USER-SETTINGS.md line 939 section header:
> ### `stream.suffixDiff.backspacePolicy` (currently silent)

**Atlas body** (lines 941-948): describes the silent
behaviour as the current default and frames the section as
"this assumption was wrong, here's what the parameter
should look like".

**Code reality** (post-PR-#168): default is
`AnnounceDeletedCharacter`
(`StreamPathway.fs:161` + `Config.fs` enum default).
The configurability the atlas proposed is shipped.

**Triage**: Tier 1 (doc-fix). Update section header to
`(currently announce_deleted_character)`. Update the
"Current state" body to reflect that the parameter ships
with the recommended default. Move the "this assumption
was wrong" history to a "Background" or "How this parameter
arrived" sub-section if useful.

### D4: `modeBarrier.flushPolicy` section header + body stale

**Atlas**: USER-SETTINGS.md line 1284 section header:
> ### `modeBarrier.flushPolicy` (currently verbose)

**Atlas body** (lines 1286-1306): describes the verbose-
flush behaviour as the current default and frames the
section as "UX issue #5 from 2026-05-06".

**Code reality** (post-PR-#168 + #169): default is
`SummaryOnly` (`StreamPathway.fs:162`).
`StreamPathway.fs:843-866` shows the policy actively
applied (`SummaryOnly` and `Suppressed` paths both
suppress the flush).

**Triage**: Tier 1 (doc-fix). Update section header to
`(currently summary_only)`. Update "Current state" body to
reflect shipped behaviour. Preserve the UX-issue-#5 narrative
as historical motivation if useful for future readers.

### D5: `FileLoggerOptions.ChannelCapacity` orphan

**Code**: `FileLogger.fs:88` `ChannelCapacity = 8192`.
Backpressure threshold for log enqueues; affects
crash-safety semantics under heavy logging load.

**Atlas**: not mentioned anywhere.

**Triage**: Tier 1 (doc-fix; low priority). Add a
📋 reserved entry under §Diagnostic surfaces or a new
sub-section. Document as "platform-internal default;
unlikely to be user-tunable in v1; document for
discoverability". Could ship under a future
`[diagnostic.fileLogger]` section if user demand surfaces.

### D6: ConPtyHost buffer-size orphan

**Code**: `ConPtyHost.fs:137,138,173` —
`new FileStream(handle, FileAccess.Read, 4096, false)` and
`Array.zeroCreate<byte> 4096`. ConPTY I/O buffer.

**Atlas**: not mentioned.

**Triage**: Tier 1 (doc-fix; very low priority). Same
treatment as D5 — add a 📋 reserved entry under
§Intentionally not user-configurable (likely; ConPTY
buffer size is platform-internal). Document for
discoverability.

## Triage

Findings classified by required action:

### Tier 1 — Doc-fix follow-up (small follow-up PR; mechanical)

Five doc-side fixes to USER-SETTINGS.md. No code changes;
no spec changes. Estimated PR size: ~50-100 LOC of doc
edits.

| # | Finding | Atlas line(s) | Edit type |
|---|---|---|---|
| D2 | `bulkChangeThreshold` "Current state" body | ~893-894 | Update file:line + parameter-record location |
| D3 | `backspacePolicy` section header + body | 939, 941-988 | Section-header rename + body update |
| D4 | `modeBarrier.flushPolicy` section header + body | 1284, 1286-1335 | Section-header rename + body update |
| D5 | `FileLoggerOptions.ChannelCapacity` orphan | (NEW entry) | Add 📋 reserved entry under §Diagnostic surfaces |
| D6 | ConPtyHost buffer size orphan | (NEW entry) | Add 📋 reserved entry under §Intentionally not user-configurable |

Suggested follow-up PR title:
`docs(audit): Track D — apply atlas-side fixups to USER-SETTINGS`.

### Tier 2 — Substrate-implementation-gated

11 reserved atlas entries close when their substrate
ships. None require action today. The atlas correctly
documents each as 📋 reserved with implementation sketches.

### Tier 3 — Forward-looking openness

None — Track D's findings are all closable; the atlas's
intentional gaps (Phase 2/3 substrate) are documented in
§Intentionally not user-configurable and per-section
"Implementation tier" notes.

## Cross-cutting themes

### Theme 1: PR #167 / PR #168 staleness (D2, D3, D4)

Three findings cluster on a common root cause: PR #167
added the atlas entries (describing the THEN-current
hardcoded state + the proposed configurability shape).
PR #168 implemented the proposed configurability
(`bulk_change_threshold`, `backspace_policy`,
`mode_barrier_flush_policy`) and changed the defaults to
the proposed values. The atlas's "Current state" sections
were NOT updated to reflect implementation; they still
describe the pre-PR-#168 world.

This is a **documentation-process gap**, not an
architectural gap. A small follow-up PR fixes it
mechanically. Going forward, when a Phase 2 PR ships an
atlas-reserved parameter, the same PR (or an immediate
follow-up) should update the atlas's "Current state" to
match.

### Theme 2: Low-priority orphans (D5, D6)

Two code constants (`ChannelCapacity = 8192`,
ConPTY buffer = 4096) have no atlas entries. Neither is
likely to ever be user-tunable; they're platform-internal
buffers. **Adding them to atlas is documentation hygiene,
not user-facing necessity.** Recommended atlas section:
§Intentionally not user-configurable (where MAX_OSC_RAW
already lives) — discoverable for future contributors,
explicit "not for tuning" tag.

## Recommendations

### Immediate (small, mechanical)

- **Atlas-side fixup PR** applying D2-D6 (~50-100 LOC).
  Mirrors Track A's PR #175 pattern but for the atlas
  layer.

### Next-cycle (small)

- **None.** Track D's findings close in one mechanical PR.

### Future (substrate-implementation-gated)

- When SessionModel substrate (item 28) ships
  implementation, atlas gains a `[session]` section
  documenting bounded-history size, persistence mode,
  OSC 133 detection mode.
- When Pane Model substrate (item 30) ships
  implementation, atlas gains a `[workspace]` /
  `[pane.<id>]` section documenting layout + per-pane
  parameters.
- When Phase 2 input framework ships, atlas's
  `stream.echoCorrelation` /
  `stream.cursorAnnouncement` /
  `notification.activityIds.inputEcho` entries graduate
  from 📋 reserved to ✅ shipping.

## What this PR does NOT do

- **No atlas edits** — Tier 1 doc-side fixes are deferred
  to a follow-up PR (single-concern discipline).
- **No code changes**.
- **No spec changes** (per CLAUDE.md spec-immutability).
- **No new TOML parameters**.
- **No earcon-palette tuning UI** (Phase 2 feature).

## Cross-references

- [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md)
  — Track A; this doc mirrors its structure at the atlas
  layer.
- [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) —
  Track B.
- [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) —
  Track C.
- [`USER-SETTINGS.md`](USER-SETTINGS.md) — the audit
  subject.
- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md),
  [`SESSION-MODEL.md`](SESSION-MODEL.md),
  [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md),
  [`PANE-MODEL.md`](PANE-MODEL.md) — research-stage docs
  cross-checked.
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching +
  PR conventions (single-concern PR discipline informs
  the audit→fixup loop pattern).

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Track D audit (Cycle 4) | Initial snapshot; atlas inventoried (1547 lines, 8 shipped TOML parameters + 11 reserved); 17 ✅ aligned, 5 ⚠ (3 stale-section findings + 2 low-priority orphans), 0 ❌ contradictions; tier 1 doc-side fixup follow-up PR recommended (~50-100 LOC). |

