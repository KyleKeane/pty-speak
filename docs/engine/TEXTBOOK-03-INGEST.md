# Engine textbook, chapter 3 — ingest: from stream to sealed structure

> Files: `src/Engine.Core/MarkdownChunker.fs`,
> `SemanticOutline.fs`, `Ingest.fs`, `EngineEvent.fs`. Tests:
> `MarkdownChunkerTests`, `SemanticOutlineTests`,
> `IngestTests`, `EngineBusTests`. Decisions: ADR 0011 E2/E5,
> ADR 0012 S1; spec §5.1–§5.2.

## The pipeline

One assistant text block travels:
`text → MarkdownChunker.decompose → SemanticOutline.nest →
Ingest.appendSpecs → ChunkTree` — and only then does anyone
hear about it (`ChunkSealed` on the bus). Three pure stages,
each independently tested, composed by a fourth pure fold.

## MarkdownChunker — the grain

Markdig parses the markdown the model emitted; the chunker
walks the block AST into `ChunkSpec` records (kind + narration
text + children). The grain is **block-level** because that is
the structure the author marked: headings, paragraphs, lists
(items as children; nested lists under their item), fenced
code (language kept; body verbatim), quotes (flattened),
thematic breaks. Inline emphasis/code/links flatten into
narration text — they are *styling* at this layer, and a
voice would either over-announce or mis-announce them.
Whitespace-only leaves drop; structural kinds survive empty.

Name collision note: Markdig's `ListBlock`/`CodeBlock` syntax
types share names with `ChunkKind` cases, so the module never
opens `Engine.Core.Chunk` — every kind is `Chunk.`-qualified.

## SemanticOutline — the hierarchy

The chunker's output is flat at section level (headings are
siblings of their prose). `nest` recovers the outline with a
single recursive scope rule: **a heading absorbs every
following spec — including deeper headings — until a heading
of equal or shallower level.** Content before the first
heading stays top-level (the source put it there); list
children pass through untouched; skipped levels (H1 → H3)
nest by relative depth. This is ADR 0008 verbatim — recovery
of structure the source unambiguously provides; there is no
inference anywhere in the function, which is why it is twenty
lines and provable.

The payoff is navigational: top-level `next` is
section-to-section; `descend` enters a section; a heading
narrates "N items inside."

## Ingest — the sealing fold

`Ingest` is a pure fold: `(AgentEvent, Session) → (Session,
EngineEvent list)`. No bus, no I/O — the host publishes what
ingest returns, which is why every sealing rule is a unit
test. The session record carries the tree plus four cursors:
the CLI session id, the current request (new chunks' parent),
the latest-response start (the jump verb's target — set by
the first batch of a turn, untouched by later batches), and
the in-flight count.

The rules, each pinned by a test:

- `captureRequest` appends the typed `UserRequest` (anchored
  under a chunk for branches) and resets the turn counters.
- An `AssistantMessage` seals its text blocks through the
  chunker+outline, tool-use blocks as `ToolUse` chunks
  carrying verbatim input JSON, and surfaces unknown blocks
  as ambient notes — **never in the tree**.
- `ToolResults` seal as typed result chunks.
- `TurnResult` completes (error-aware, with the count) and
  deliberately does **not** re-append the result text — it
  duplicates the final assistant message.
- Sealing emits one `ChunkSealed` per chunk + a trailing
  ambient `ResponseProgress` count: the §5.2 streaming rule —
  the user never navigates a moving target and is never cut
  off from progress.
- `appendSafe` makes ingest total: the structurally-
  unreachable missing-parent case degrades to a top-level
  append rather than losing content.

## EngineBus — the fan-out

The universal event bus, instance-scoped (unlike the WPF
app's global `CellEventBus`, so tests and engines compose):
token-keyed subscribers under a lock, snapshot-then-fire, a
throwing sink swallowed so it can never break ingest. Three
consumers today — attention (speech), spatial cues, and
diagnostics/event-log — and the contract for every future one
(braille, haptics, a remote mirror) is identical: consume the
typed event, never re-derive meaning from another channel's
rendering.
