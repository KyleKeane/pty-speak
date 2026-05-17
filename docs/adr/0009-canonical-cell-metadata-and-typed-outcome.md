# ADR 0009 — Canonical cell metadata & typed outcome (schemaVersion 3)

- **Status**: **Accepted** (2026-05-17; maintainer ratified
  — "I am happy with the ADR additions and you can mark those
  resolved". Design is locked; **still NOT implemented** — no
  code or schema change yet. P-A is the start (independently
  landable; does not block or depend on ADR 0007 Phase 4).
  Not in the autonomous-sprint scope.)
- **Date**: 2026-05-17
- **Deciders**: maintainer (KyleKeane) — ratified 2026-05-17
- **Authoring item**: Cycle 52, during the ADR 0007 phase
  sprint. The maintainer asked whether the canonical cell
  history includes "a mechanism for arbitrary tags and
  metadata as well as the nautical IOCell data (type, body,
  date, string, UUID) so that you can record things like
  whether there was an error … and search against that, and
  … use the UUID to move the system focus to that particular
  cell once we are rendering it in the interface." This ADR
  is the design answer drafted for review.
- **Companion docs**:
  [ADR 0004](0004-iocell-model-for-shell-interaction.md)
  (the IOCell data model + `schemaVersion` discipline this
  ADR extends to `3`),
  [ADR 0007](0007-canonical-iocell-history-navigation.md)
  (the navigable model + Phase 6b kind-filtered jumps this
  generalises to outcome/tag-filtered, + the D9
  `pty-speak.cell.*` events that carry the UUID-focus
  contract),
  [ADR 0008](0008-maximal-semantic-surfacing.md) (the typed
  `CellOutcome` is this principle applied: recover maximal
  *unambiguous* semantics, never relay ambiguous content),
  [`docs/IOCELL-SCHEMA.md`](../IOCELL-SCHEMA.md) (the wire
  format that takes the `schemaVersion 2 → 3` bump).

## Context

The canonical cell (`SessionModel.IOCell`, ADR 0004) already
carries: a stable **UUID** (`Id: Guid`, assigned at cell
creation), `CellSequence: int64`, **type** (`CellKind` DU),
**body** (`CommandText` / `OutputText`), four **lifecycle
timestamps** (`PromptStartedAt` + optional
`CommandStartedAt` / `OutputStartedAt` / `CommandFinishedAt`),
a correlation id (`CommandId: string option`, OSC `aid=`),
and a raw `ExitCode: int option`. The navigable projection
(`SpeechCursor.CellView`) carries a subset (`CellId`,
`CellSequence`, `Kind`, `Text`, `ActivityId`, `ExitCode`).

Two gaps motivate this ADR:

1. **No semantic outcome.** "Was this an error?" is answerable
   today *only* via the raw `ExitCode`. The `52-ADR7-P2c`
   dogfood (2026-05-17) proved that signal is
   transport-narrow: **cmd emits no exit code at all**
   (`CmdAdapter.fs:52-65`, documented limitation); PowerShell
   emits one **only for external processes** (a failing
   cmdlet such as `dir`/`type` never sets `$LASTEXITCODE`).
   So an error-search keyed on `ExitCode` silently misses
   most real failures.

2. **No metadata / tag facility.** There is no general way to
   attach searchable attributes (semantic outcome, user
   bookmarks/labels, future classifications) to a cell. ADR
   0004 *deliberately* froze a **fixed** `schemaVersion 2`
   hand-rolled JSONL schema specifically to avoid
   open-ended-blob drift, so this gap is by design — closing
   it is a conscious schema decision, not an oversight to
   patch silently.

The UUID-focus ask ("use the UUID to move focus to that cell
when rendered") needs **no new mechanism** — `Id: Guid` is
already the stable handle and ADR 0007 D9 + Phase 6a already
specify the focusable list keyed by `CellId`. This ADR only
*ratifies that contract explicitly* (Decision 3) so it cannot
drift.

## Decision (proposed)

### D1 — Typed `CellOutcome`, distinct from raw `ExitCode`

Add a **closed** outcome type to the canonical cell:

```
type FailureSignal =
    | NonZeroExit of int            // shell transported a code
    | ShellReportedError            // typed shell-integration error signal (future)
type CellOutcome =
    | Succeeded
    | Failed of FailureSignal
    | Indeterminate                 // no reliable transported signal
```

`CellOutcome` is **derived only from transported signals**
(OSC-133 `;D;<code>`, future typed shell-integration error
markers) — never from heuristic output-text parsing. When no
reliable signal exists (cmd; PowerShell cmdlet errors) the
outcome is **`Indeterminate`**, *not* a guess. This is ADR
0008 applied verbatim ("recover maximal unambiguous
semantics; never relay computationally ambiguous content")
and is **FREEZE-safe** by construction — it reconstructs
nothing. Raw `ExitCode` is retained unchanged for diagnostics.

### D2 — Disciplined metadata, not a free blob

Add an **extensible-but-closed** metadata facility:
- A typed, enumerated **known-tag** set (DU keys, typed
  values) for system-derived attributes — *not* a free
  `Map<string,string>` (that reintroduces exactly the
  ADR 0004 anti-drift problem and a secrets-leak surface).
- A **bounded** `UserTags` set (user-applied bookmarks /
  labels — the natural backing for ADR 0007 **Phase 6c**
  bookmarks/sections): size-capped, values sanitised through
  `Terminal.Core.AnnounceSanitiser`, **never logged** (per
  the standing logging/secrets discipline — metadata can
  carry sensitive strings).

### D3 — UUID focus-addressing contract (ratify existing)

`CellId: Guid` is *the* addressable focus key. ADR 0007
Phase 6a's focusable list items and the D9 `pty-speak.cell.*`
event family MUST key on `CellId` so "a computation returned
a result → emit a cell event → move focus to that cell by
UUID" is a stable contract. No new mechanism — this ADR
records it so it cannot regress.

### D4 — Persistence: `schemaVersion 2 → 3`

The new fields bump the hand-rolled JSONL schema to **`3`**.
The serializer **and** the maintainer-only round-trip reader
are updated in the **same** PR that introduces the fields
(ADR 0004 round-trip discipline). `schemaVersion 2` files
remain readable (forward-compat: absent outcome →
`Indeterminate`, absent tags → empty). No silent blob; the
locked-key-order + tagged-DU conventions of IOCELL-SCHEMA.md
extend to the new fields.

### D5 — Surfacing generalises ADR 0007 Phase 6b

`CellView` gains `Outcome` (+ the tag view) so the
navigation/search layer queries without touching persistence.
ADR 0007 **Phase 6b** "kind-filtered jumps" generalises to
**outcome/tag-filtered jumps & search**;
`jumpToLastError` is reframed as the *first instance* of
outcome-filtered jump (`Failed _`), behaviour-preserving
where a signal exists and now also catching
`ShellReportedError` once that signal lands.

## Consequences — phased plan (walking-skeleton)

Each its own PR + dogfood; none reorders the ADR 0007 phase
sequence (this slots alongside, it does not gate Phase 4).

- **P-A** — `CellOutcome` in the model + derivation from
  transported signals + `CellView` projection +
  `jumpToLastError` reframed onto `Outcome` (net
  behaviour-preserving) + **`schemaVersion 3`** serializer &
  round-trip reader. Independently landable (no Phase-4
  dependency).
- **P-B** — known-tag facility + bounded sanitised `UserTags`;
  wires the ADR 0007 Phase 6c bookmark backing.
- **P-C** — outcome/tag-filtered search & jumps, delivered
  *within* ADR 0007 Phase 6b (depends on the Phase 6a list
  existing).

## Alternatives considered

- **Free `Map<string,string>` metadata bag.** Rejected:
  reinstates the ADR 0004 anti-drift problem and a
  secrets-leak surface; the closed/typed + bounded-UserTags
  shape gets the extensibility without the blob.
- **Infer "error" from output text.** Rejected: that is the
  cmd-heuristic FREEZE zone and violates ADR 0008 ("never
  relay computationally ambiguous content"). `Indeterminate`
  is the honest signal where none is transported.
- **Keep `ExitCode` as the only error signal.** Rejected:
  `52-ADR7-P2c` proved it transport-narrow; a typed
  `CellOutcome` is the ADR 0008-correct fix.
- **Bump no schema (in-memory only).** Rejected: the value
  of a searchable outcome/tag history is largely lost if it
  does not survive a session reload; the controlled
  `schemaVersion 3` bump is the point.

## Relationship to other ADRs

- **Extends ADR 0004**: `schemaVersion 2 → 3`; same
  round-trip discipline + locked-order JSONL conventions.
- **Realises ADR 0008**: `CellOutcome` is the typed
  unambiguous semantic; `Indeterminate` is the
  never-relay-ambiguous stance.
- **Extends ADR 0007**: D5 generalises Phase 6b; P-B backs
  Phase 6c; D3 ratifies the D9/Phase-6a UUID-focus contract.
- **Honors ADR 0001/0002**: cells stay a projection of the
  linear substrate (this adds attributes, not a new
  substrate); the UIA channel is unaffected.

## Status / next

**Accepted 2026-05-17 (maintainer ratified).** Design locked;
**not yet implemented** — no code or schema change has
landed. **P-A** is the start (typed `CellOutcome` + derivation
+ `CellView` projection + `jumpToLastError` reframed +
`schemaVersion 3` serializer & round-trip reader); it is
independently landable and does **not** block or depend on
ADR 0007 Phase 4. Sequencing of P-A vs the remaining ADR 0007
phases is the maintainer's call (raise when ready). The
autonomous sprint does **not** implement this.
