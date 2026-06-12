# Engine textbook, chapter 9 — persistence and diagnostics

> Files: `src/Engine.Core/ChunkSerde.fs`, `ChunkTree.fs`
> (`restore`), `EngineDiagnostics.fs`. Tests:
> `ChunkSerdeTests`, `EngineDiagnosticsTests`. Decisions: ADR
> 0013 N4, ADR 0014 C4; precedent: `docs/IOCELL-SCHEMA.md`.

## The session wire format (schema v1)

A session file is JSONL: one **meta line** — `schemaVersion`,
the participant session id (the thing `--resume` rides on, so
a restored session *continues the conversation*), and the two
cursor anchors — followed by **one chunk per line in capture
order**. Keys are written in a locked order by a hand-rolled
`Utf8JsonWriter` emitter (no reflection serializer: the format
is a contract, not an implementation detail), and kinds
serialize as a tag plus their payload fields (`heading` +
`level`, `code` + `language`, `toolUse` + `name`, …).

The reader is the mirror discipline of the stream parser
(chapter 2): every failure — malformed line, missing field,
unknown kind tag, newer schema, empty file — is a **typed
`Error` string**, never an exception, so the host can speak
exactly what is wrong with a file. New optional fields are
additive within a version; `schemaVersion` bumps only when an
old build would *misinterpret* a new file, and old readers
stay (one-way migration, the IOCELL-SCHEMA rule).

## restore — never a silently wrong tree

`ChunkTree.restore` lives inside the tree module (it needs
the private representation) and **re-validates every
structural invariant while rebuilding**: parents must precede
children, capture order strictly increases, ids are unique,
and each authored index equals its arrival position among its
siblings. The threat model is not malice — it is a hand-edited
file, a partial write, a future bug — and the failure posture
is the engine's universal one: a typed error the user hears,
never a crash, and **never a plausible-looking wrong tree**
(which would be worse than either).

The round-trip law is property-tested: for arbitrary
generated trees over all fourteen chunk kinds,
`serialize → parse → restore` reproduces the exact capture
order.

## EngineDiagnostics — triage by ear

The design constraint is unusual and absolute: the user who
must produce the bug report **cannot read a console**. So:

- A thread-safe bounded **ring** (default 500 entries) records
  everything: every bus event (via `describeEvent`, with
  bodies clipped to 80 chars — diagnostics traces *shape*; the
  session file holds content), turn outcomes, config
  warnings, host errors.
- Counts are **lifetime**, kept outside the ring, so eviction
  never lies about volume ("note 4,012" stays honest after
  the entries scrolled away).
- `Summary()` is the one-breath spoken triage: uptime, counts
  by category, and the last error verbatim.
- `Dump()` is the bug-report artifact: ISO-stamped,
  grep-friendly, one line per entry, written to a file whose
  path is spoken.

Beside the dump, the host appends a **session event log** —
one line per bus event as it happened — which is the replay of
what the user heard; diffing it against expectations is the
field-debugging recipe (development guide §9).
