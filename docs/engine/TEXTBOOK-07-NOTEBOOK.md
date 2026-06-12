# Engine textbook, chapter 7 — the notebook

> File: `src/Engine.Core/Notebook.fs`. Tests: `NotebookTests`.
> Decision: ADR 0013; spec §5.3 (capture vs authored), §6.3
> (editor verbs), §3 (the Wolfram notebook as reference).

## Two layers, never collapsed

The spec's sharpest design sentence: *the chat-log is what
flows in; the notebook is what the maintainer thinks in.* The
capture tree (chapters 1–3) is the immutable record of what
happened; the notebook is the **authored** sequence the user
curates out of it. Spec §14.5 forbids collapsing them, and the
model enforces that structurally: a notebook cell is one of —

- `PinnedChunk of ChunkId` — a **reference** into the capture
  tree. Never a copy: narration and export render through the
  live tree (the tested proof: pin a heading, append a child
  to it afterwards, and the pinned cell announces the new
  count). No notebook operation writes the tree.
- `Narrative of text` — the user's own words, the connective
  tissue an agent never wrote.
- `SectionHeader of title` — the higher-order structure of the
  computational narrative.

## Editing by ear is total

`pin`, `addNarrative`, `addSection`, `moveUp`, `moveDown`,
`removeAt` — all pure, all **total**. Out-of-range indices are
no-ops that *report* (`Notebook * bool`), because the host
must cue an edge exactly like tree navigation: a blind editor
discovers boundaries by bumping them, and a bump must be a
sound, never an exception.

## Export: markdown as the interchange form

`toMarkdown` renders the sequence: sections become `##`,
narrative becomes prose, and pinned chunks render by kind —
headings demote to `###`, code re-fences with its language
tag, **lists re-render from the live tree's items**, requests
and tool calls/results are labelled and fenced. Two properties
make this the Wolfram lesson honestly adapted:

1. **Publishable**: the export reads top-to-bottom as a
   document — the deliverable of an exploration session.
2. **Re-ingestable** (tested): feeding the export through the
   engine's own `MarkdownChunker` reproduces typed structure.
   Where Wolfram has one language for interface and data, this
   engine's interchange form is typed markdown — the one
   structure every participant already speaks — so a narrative
   composed today literally seeds tomorrow's conversation
   ("flows into new domains").

## What is deliberately v2

Inline editing of narrative text, split/merge/re-segment,
anchored re-issue *with modifications*, multiple notebooks.
The model needs no migration for any of them (cells have ids;
the sequence is ordered), which is the ADR 0013 test of having
modelled the v1 correctly.
