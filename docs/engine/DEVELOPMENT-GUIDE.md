# Engine development guide — how to extend, fix, and operate

> For any developer (or future session) maintaining the
> interaction engine. Each section is a **worked recipe**: the
> files you touch, the order, the tests that must accompany
> the change, and the traps. The architectural *why* lives in
> the [textbook](TEXTBOOK-00-OVERVIEW.md); ADRs 0011–0014 are
> the decision record. House rules (one concern per PR,
> CI-gated, CHANGELOG bottom-append) are in
> `CONTRIBUTING.md` + `CLAUDE.md`.

## 0. The invariants you must never break

1. **`Engine.Core` is platform-free** — plain `net9.0`, no
   WPF/Win32 opens, no P/Invoke, no `Terminal.*` references.
   CI (`portability-lint`) fails the PR if you slip.
2. **Only sealed chunks are published or navigable** (the
   §5.2 streaming rule). Nothing may expose a mutating chunk.
3. **Capture is append-only; the notebook holds references,
   never copies.** No notebook operation writes the tree.
4. **Foreground speech is never preempted by ambient**; only
   user action interrupts.
5. **Honesty over silence**: unknown input shapes become typed
   `Unknown`/`ParseError`/warnings — never dropped, never
   guessed at (ADR 0008).
6. **No engine path may throw on user input** — editing,
   navigation, config, and file restore are total
   (option/Result/no-op), and audio failure is always
   swallowed.

## 1. Add a keyboard verb

Files, in order:

1. `Engine.Core/KeyMap.fs` — add the `Verb` case, its
   `verbName` arm (the compiler forces this), and one row in
   `defaults` (pick a free key; `KeyMapTests` fails the build
   on a conflict).
2. `Engine.Host/Program.fs` — one arm in the verb dispatch.
   Speak through `speakNow`/`enqueue` only; never call the
   sink directly.
3. Tests — `KeyMapTests` covers the table automatically; add
   behaviour tests for whatever the verb does (in the module
   the behaviour lives in — verbs should be thin).
4. Docs — `KEYBOARD-REFERENCE.md` row, `USER-GUIDE.md` if
   user-facing, CHANGELOG.

Trap: don't bind into the other mode's keyspace without
checking `validate` locally — shared-mode verbs (`Modes =
both`) collide with *either* table.

## 2. Add a chunk kind

1. `Engine.Core/Chunk.fs` — the `ChunkKind` case. The compiler
   now walks you through every consumer:
2. `ChunkNarration.describe` (how it sounds), `trailLabel` +
   `selfLabel` (breadcrumbs), `SpatialCue.pitchForKind` (its
   unique pitch — pick an unused frequency; the uniqueness
   test fails otherwise), `Notebook.toMarkdown` (how it
   exports), `ChunkSerde.kindFields` + `parseKind` (its wire
   tag — bump nothing; new tags are additive within a schema
   version), and whatever produces it (chunker or ingest).
3. Tests: narration shape, serde round-trip (add the kind to
   `ChunkSerdeTests.kinds`), cue uniqueness updates itself.

## 3. Extend the stream parser (CLI format drift)

Symptom in the field: spoken ambient notes "Unrecognized
stream event type: X" (the tolerant path working as designed).

1. Get the raw line (diagnostics dump or session event log).
2. Add a fixture to `ClaudeStreamJsonTests` reproducing it.
3. Add the arm in `ClaudeStreamJson.parseLine` (and a new
   `AgentEvent` case only if it is genuinely new semantics —
   prefer mapping into the existing vocabulary).
4. If a new `AgentEvent` case: `Ingest.applyAgentEvent` must
   handle it (the compiler enforces), decide seal-vs-ambient,
   and add `IngestTests`.

## 4. Add a participant (a new AI/tool backend)

`Engine.Participants` is the seam (spec §12: one seam, N
tools). Mirror `ClaudeCli.fs`: a pure `buildArguments`, a pure
line/translation layer that normalizes the tool's native
output **into the existing `AgentEvent` vocabulary**, and a
thin `runTurn`. The engine (ingest, navigation, narration,
notebook) needs zero changes — that is the test of a correct
participant: only the host's spawn choice knows it exists.

## 5. Add a universal-event-bus consumer (a new output channel)

Subscribe in the host: `bus.Subscribe(fun ev -> …)`. Rules:
never block (the bus fires inline), never throw (you'll be
swallowed, but don't rely on it), never *re-derive* meaning
from another channel's rendering — consume the typed event
(ADR 0008). A renderer for a new modality (braille, haptics)
should consume `EngineEvent` or `SpatialCue.Cue`, mirroring
`Engine.Audio.SpatialPlayer`.

## 6. Add a config key

1. `Engine.Core/EngineConfig.fs` — field + default + parse arm
   **with the warn-and-default discipline** (wrong type warns;
   out-of-range clamps with both numbers in the warning).
2. `EngineConfigTests` — valid, wrong-typed, out-of-range.
3. Host: consume the field at startup.
4. `CONFIGURATION.md` row + CHANGELOG.

Never add a key that can crash, and never reject silently. New
keys within the same shape are NOT a schema bump; bump
`CurrentSchemaVersion` only when an old build would
*misinterpret* a new file.

## 7. Change narration or cue mapping

Narration: `ChunkNarration` (content) and `Attention.route`
(what gets spoken at all). Keep structure-before-content (the
cap relies on it). Cues: `SpatialCue` — keep family signatures
pairwise distinct (tested) and gains ≤ 0.5 (tested); the stage
layout's meaning (center = narrative, right = content/progress,
left = lifecycle/diagnostics) is ADR 0012 canon — don't move
families across the stage without an ADR.

## 8. Persistence changes

Wire formats live in `ChunkSerde` (session) and follow the
IOCELL-SCHEMA discipline: locked key order, explicit
`schemaVersion`, tolerant typed-error reader, one-way
migration. Additive fields: same version, reader defaults.
Breaking shape: bump the version, keep the old reader path.
Always extend the round-trip property test.

## 9. Debugging a field report

You will receive: a diagnostics dump, a session `.jsonl`, an
event `.log`. In that order:

1. Dump header = uptime + lifetime counts + last error —
   usually names the failing layer outright.
2. Event log = the exact bus sequence the user heard; diff it
   against expectations (a missing `completed` = the turn
   never finished; `error` entries carry the participant's
   exit code + stderr tail).
3. Session file replays into a real tree:
   `ChunkSerde.parseJsonl` + `ChunkTree.restore` in a test
   reproduces the user's exact navigable state — write the
   regression test directly against it.
4. No local Windows? CI is the compiler; follow the
   read-twice-before-push discipline in `CLAUDE.md`.

## 10. Release / launch checklist

1. CI green on main (build + tests + portability lint).
2. The dogfood walk (`docs/ENGINE-PHASE0.md`, all steps) on a
   real machine with headphones — narration reliability and
   the stage cannot be validated remotely.
3. Docs sweep: KEYBOARD-REFERENCE matches `KeyMap.defaults`;
   CONFIGURATION matches `EngineConfig`; CHANGELOG promoted.
4. Stage the checkpoint tag in `docs/CHECKPOINTS.md` (tag
   pushes need a workstation).
5. Cycle closure audit per `CLAUDE.md` (status flips,
   SESSION-HANDOFF, no "next" language pointing backward).
