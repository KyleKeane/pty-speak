# Engine textbook, chapter 0 — the whole machine on one page

> The textbook covers every facet of the interaction engine's
> code: what each module is, why it has the shape it has, and
> the laws its tests pin. Read this chapter first; the rest in
> any order. Decision history: ADRs 0011–0014 +
> [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md).

## The one-paragraph theory

The engine is a **pure functional core with a thin imperative
shell**. Everything that decides — parsing, chunking,
outlining, sealing, navigating, narrating, routing attention,
mapping cues, editing the notebook, reading config, dispatching
keys, serializing sessions — is a pure function in
`Engine.Core` over immutable data, tested without any I/O.
Everything that *touches the world* — a process, a SAPI voice,
a WASAPI device, the console, the filesystem — is one of three
small leaf assemblies (`Engine.Participants`, `Engine.Voice`,
`Engine.Audio`) plus one orchestrating host (`Engine.Host`)
that owns all mutable state behind a single lock.

## The data flow

```
 keyboard ──► Engine.Host (the only mutable state)
                │  compose / verbs (KeyMap table)
                ▼
 Engine.Participants.ClaudeCli ── spawns ──► claude CLI
                │  stdout lines
                ▼
 ClaudeStreamJson.parseLine        (pure: line → AgentEvent)
                ▼
 Ingest.applyAgentEvent            (pure fold: seal at message
                │                   boundaries via MarkdownChunker
                │                   + SemanticOutline into ChunkTree)
                ▼
 EngineEvent list ──► EngineBus.Publish
                │
   ┌────────────┼──────────────────────┐
   ▼            ▼                      ▼
 Attention   SpatialCue           EngineDiagnostics
 (route +    (event → stage       (ring: counts, dump)
  queue)      signature)
   ▼            ▼
 ISpeechSink  SpatialPlayer
 (SAPI)       (NAudio stereo pan)
```

Navigation (`Navigator`) and the notebook (`Notebook`) read
the same `ChunkTree`; narration (`ChunkNarration`) renders
either; `ChunkSerde` round-trips the tree to disk.

## The assemblies

| Assembly | TFM | Role | Chapter |
|---|---|---|---|
| `Engine.Core` | `net9.0` (platform-free, CI-enforced) | every decision | 1–9 |
| `Engine.Participants` | `net9.0` | process spawn + line pump | 2 |
| `Engine.Voice` | `net9.0-windows` | SAPI behind `ISpeechSink` | 5 |
| `Engine.Audio` | `net9.0-windows` | stereo cue renderer | 6 |
| `Engine.Host` | `net9.0-windows` exe | wiring + state + keys | 10 |

## The seven laws (each a tested invariant)

1. **Sealed-only** — a chunk is navigable/announced only once
   complete (spec §5.2). Ch. 3.
2. **Append-only capture** — the transcript never mutates;
   ids, capture order, and authored order are stable. Ch. 1.
3. **Reference-not-copy authoring** — notebook cells point
   into the tree. Ch. 7.
4. **Foreground over ambient, user over everything** —
   attention. Ch. 5.
5. **Typed honesty** — unknown shapes surface typed; config
   degrades with warnings; restore errors are values. Chs.
   2, 8, 9.
6. **Non-ejection** — navigation can never leave the content
   model. Ch. 4.
7. **Position is meaning** — every event family owns a unique
   (pan, pitch) signature. Ch. 6.

## Chapter index

- [01 — The chunk tree](TEXTBOOK-01-CHUNK-TREE.md): identity,
  two orderings, branches.
- [02 — Stream parsing & participants](TEXTBOOK-02-STREAM-PARSING.md):
  `AgentEvent`, the tolerant parser, the seam.
- [03 — Ingest](TEXTBOOK-03-INGEST.md): chunker, outline,
  sealing, the bus.
- [04 — Navigation & narration](TEXTBOOK-04-NAVIGATION-NARRATION.md):
  verbs, edges, breadcrumbs, caps.
- [05 — Attention & voice](TEXTBOOK-05-ATTENTION-VOICE.md):
  the queue, routing, SAPI.
- [06 — Spatial audio](TEXTBOOK-06-SPATIAL-AUDIO.md): the
  stage, cue math, the renderer.
- [07 — The notebook](TEXTBOOK-07-NOTEBOOK.md): authored
  layer, export, re-ingestion.
- [08 — Config & keymap](TEXTBOOK-08-CONFIG-KEYMAP.md):
  engine.toml, the verb table.
- [09 — Persistence & diagnostics](TEXTBOOK-09-PERSISTENCE-DIAGNOSTICS.md):
  serde, restore, the ring.
- [10 — The host](TEXTBOOK-10-HOST.md): threading, the lock,
  the drain, the key loop.
