# ADR 0013 — The computational notebook: authored layer, pinning, narrative export, session persistence

- **Status**: Proposed — authored 2026-06-12 under the
  maintainer's explicit instruction to push the engine toward a
  launch-ready, audio-only **computational notebook** (the
  Wolfram-notebook-inspired direction named in the directive),
  with autonomy to decide implementation questions. The
  `P0-ENGINE-1` dogfood (extended) ratifies.
- **Date**: 2026-06-12
- **Deciders**: Claude (autonomous, per maintainer grant);
  maintainer ratifies retroactively.
- **Companion docs**:
  [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md) §5.3
  (capture layer vs authored layer — this ADR builds the
  authored layer), §6.3 (editor verbs), §3 (the Wolfram
  notebook as the primary reference experience),
  [ADR 0011](0011-phase0-interaction-engine-bootstrap.md),
  [ADR 0012](0012-semantic-outline-and-spatial-event-signatures.md).

## Context

The spec's sharpest reference experience (§3) is the Wolfram
notebook: a reorderable document of typed cells that serves
both the final output **and** the act of holding contextual
awareness mid-exploration. The spec separates the **capture
layer** (the immutable transcript — shipped: the chunk tree)
from the **authored layer** (the notebook the user *thinks
in*) and warns: never collapse them (§14.5).

The maintainer's directive asks for exactly this: a minimal
interface that records an **editable final sequence** composed
into a higher-order computational narrative — memory of complex
workflows that can later be modified and flow into new domains.
The capture tree already holds everything that happened; what
is missing is the *curated* sequence built from it.

The interface must remain language-agnostic (the participant
seam supports many languages/tools); the notebook's canonical
vocabulary is the typed chunk kinds, not any one language's
syntax — code cells carry their language tag, nothing more.

## Decisions

### N1 — The notebook model: references + narrative, never copies

`Notebook` (pure, `Engine.Core`) is an ordered sequence of
cells, each one of:

- **`PinnedChunk of ChunkId`** — a *reference* into the capture
  tree (never a copy: the transcript stays the single source of
  truth; a pinned cell renders through the live tree).
- **`Narrative of text`** — the user's own authored words (the
  connective tissue of the computational narrative).
- **`SectionHeader of title`** — the higher-order structure.

Operations (the §6.3 editor verbs, v1 set): `pin`,
`addNarrative`, `addSection`, `moveUp`/`moveDown`, `removeAt`.
All pure, all total (out-of-range indices are no-ops returning
the notebook unchanged — editing by ear must never throw).
Capture-vs-authored stays uncollapsed by construction: pinning
mutates only the notebook; the tree is never written by any
notebook operation.

### N2 — Two host modes, one minimal surface

The host gains a **mode**: *Transcript* (the capture tree —
everything shipped so far) and *Notebook* (the authored
sequence). One key toggles; the same movement keys work in
both (`j`/`k`, `r`, `w`, edges cue identically). In transcript
mode `p` pins the focused chunk (appended to the notebook); in
notebook mode the editor verbs are live. Minimal by design:
no panes, no chrome — a mode is just which sequence the cursor
walks.

### N3 — Export: the notebook IS a markdown document

`Notebook.toMarkdown` renders the authored sequence to plain
markdown: sections become `##` headings, narrative becomes
paragraphs, pinned chunks render by kind (headings demoted
under their section, code blocks re-fenced with their language
tag, tool results fenced as output). The export is therefore
immediately publishable, diffable, and — because markdown is
the engine's own ingest format — **re-ingestable**: a narrative
composed today can flow into a new conversation tomorrow (the
maintainer's "flow into new domains of knowledge"). This is the
Wolfram lesson adapted honestly: where Wolfram has one language
for interface and data, the engine's interchange form is typed
markdown — the one structure every participant already speaks.

### N4 — Persistence: sessions and notebooks as JSONL, schema v1

- **Session capture**: the chunk tree serializes to JSONL (one
  chunk per line, capture order; a leading meta line carries
  `schemaVersion`, the participant session id, and the cursor
  anchors). `ChunkTree.restore` rebuilds a tree from chunk
  records, *validating* the invariants (parents precede
  children, capture order strict, authored index consistent) —
  a corrupt file degrades to a typed error, never a crash.
- **Auto-save** after every completed turn to
  `%LOCALAPPDATA%\PtySpeak\engine-sessions\`; manual save verb;
  an **open-last-session** verb restores both the tree and the
  CLI `--resume` id, so the conversation continues across app
  restarts — the "seamlessly holds the memory" requirement,
  mechanically.
- **Notebook** persists alongside the session (same JSONL
  discipline) and exports to `.md` on demand.
- Format discipline follows `docs/IOCELL-SCHEMA.md` precedent:
  locked key order, explicit `schemaVersion`, one-way
  migrations.

### N5 — Re-issue: rerun a captured request

In transcript mode, the rerun verb on a focused `UserRequest`
chunk sends that request text again as a new turn (the §6.3
"revisit a cell and re-issue" in its v1 form — re-issue
verbatim; inline editing of the text is the v2 editor). On any
other chunk kind it explains itself instead of guessing.

## Consequences

- The user's loop becomes: explore in the transcript → pin the
  results worth keeping → narrate between them → reorder into
  an argument → export a publishable markdown narrative →
  re-ingest it tomorrow as the seed of the next exploration.
- The notebook is unbounded by participant or language — pinned
  cells are typed chunks, whatever produced them.
- Deliberately deferred (v2 editor): inline text editing of
  narrative cells, split/merge/re-segment, anchored re-issue
  with modifications, multi-notebook management.

## Status notes

- 2026-06-12: authored; implementation lands in this cycle
  (serde → auto-save/restore → notebook model → host mode →
  export → rerun), each its own PR.
