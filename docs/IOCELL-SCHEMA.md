# IOCell schema — in-memory shape + on-disk wire format

> **Status**: Live (Cycle 51 PR-W, 2026-05-15). `schemaVersion 2`.
> Supersedes the "On-disk wire format (Cycle 24b)" section of the
> now-stubbed [`SESSION-MODEL.md`](SESSION-MODEL.md). The
> decision record is
> [`adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md);
> the F# gotchas governing the hand-rolled serializer are in
> [`../CONTRIBUTING.md`](../CONTRIBUTING.md).

This document is the canonical, decades-stable reference for the
`IOCell` type — the unit of shell interaction (ADR 0004
Decision 1) — both in memory and on disk. The serializer in
`src/Terminal.Core/SessionModel.fs` (`formatIOCellAsJsonl`) and
the round-trip reader (`IOCell.parseFromJsonl`, shipped in
PR-W2) are the implementations this doc pins.

## What changed at the Cycle 51 pivot

The pre-pivot cell type was renamed to `SessionModel.IOCell`
and gained two fields. The on-disk `schemaVersion` bumped
`1 → 2`. **The migration is one-way**: a v2 reader rejects
`schemaVersion=1` payloads with a clear error and vice versa
(ADR 0004 — no dual-path serializer; that is the Cycle-25
SubstrateMode-collapse precedent). The hand-rolled byte-stable
discipline from Cycle 24b survives the bump unchanged.

## In-memory representation

F# records + discriminated unions. Immutable, structurally
equal, algebraic — the on-disk JSON is the portable surface;
in memory the structure is the Wolfram-shaped cell the
maintainer's computational-notebook framing targets.

```fsharp
/// Lifecycle phase of an IOCell. The active cell carries one
/// of {Composing, Executing, AwaitingSubPromptResponse}; every
/// cell in History carries Sealed.
[<RequireQualifiedAccess>]
type IOCellPhase =
    | Composing
    | Executing
    | AwaitingSubPromptResponse of subPromptText: string
    | Sealed

type IOCell =
    { Id: Guid                                 // assigned at CREATION
      CellSequence: int64                      // NEW v1 — monotonic / shell-session
      CommandId: string option
      Phase: IOCellPhase                       // NEW v1
      ShellId: string
      PromptStartedAt: DateTime
      CommandStartedAt: DateTime option
      OutputStartedAt: DateTime option
      CommandFinishedAt: DateTime option
      PromptText: string
      CommandText: string
      OutputText: string
      ExitCode: int option
      Sources: Map<BoundaryKind, BoundarySource>
      ExtraParams: Map<string, string> }
```

**Identity contract.** `Id` and `CellSequence` are assigned at
**cell creation** — the PromptStart-driven `apply` transition
that opens the cell — never at seal. The active cell IS the
same identity it will have once sealed. `CellSequence` is
monotonic from `0` per shell session; it resets implicitly on
shell hot-switch because a fresh `SessionModel.T` is
constructed per shell session (Q5 resolution 2026-05-07).

**Phase population in v1 (Cycle 51 PR-W).** Only `Composing`
(the active cell) and `Sealed` (cells in `History`) are driven
by the state machine today. `Executing` and
`AwaitingSubPromptResponse` are **reserved**: the DU shape is
locked now so the wire format is forward-stable, and a later
PR drives the intermediate transitions. The serializer and
round-trip reader handle all four cases.

## On-disk wire format (v2)

One JSONL line per `IOCell` — a JSON object followed by a
single literal `\n` (never `\r\n`; byte stability is
cross-platform). One file per shell session
(`session-<SessionId>.jsonl`), written by
`SessionLogWriter.fs`.

```jsonl
{"schemaVersion":2,"id":"f2c8b1a4-…","cellSequence":42,"commandId":null,"shellId":"cmd","phase":{"kind":"sealed"},"promptStartedAt":"2026-05-14T13:18:00.0000000Z","commandStartedAt":"2026-05-14T13:18:02.1230000Z","outputStartedAt":"2026-05-14T13:18:02.1240000Z","commandFinishedAt":"2026-05-14T13:18:14.5000000Z","promptText":"C:\\Users\\Kyle\\current>","commandText":"\"…test-04-yes-no.cmd\"","outputText":"=== …START === … You chose Yes. === …END ===","exitCode":null,"sources":[{"boundary":"PromptStart","source":{"kind":"HeuristicPromptRegex","stabilityMs":100}}],"extraParams":{}}
```

### Locked decisions (decades-stable)

1. **`"schemaVersion":2` is the first key.** Increment on any
   on-disk shape change; replay tools branch on it.
   `schemaVersion=1` and `=2` are mutually unreadable
   (one-way migration per ADR 0004).
2. **`BoundarySource` serialises as a tagged object**
   `{"kind":"<case>", …payload}` — uniform across all DU
   cases; future cases land cleanly.
3. **`Sources` serialises as a JSON array** of
   `{"boundary":"…","source":{…}}` records, sorted by an
   explicit `boundaryOrdinal` (NOT F# DU compare semantics).
   Avoids the latent collision where
   `BoundaryKind.CommandFinished _` payload variants would
   alias to the same JSON key.
4. **`phase` serialises as a tagged DU object** (same rule as
   decision 2):
   - `{"kind":"composing"}`
   - `{"kind":"executing"}`
   - `{"kind":"awaitingSubPromptResponse","subPromptText":"…"}`
   - `{"kind":"sealed"}`

   **`cellSequence` is a JSON number** (int64, invariant
   formatting — no thousands separators).
5. **Hand-rolled (no JSON library).** Zero JSON dependencies;
   byte-stable across .NET versions; full control over field
   ordering. Mirrors the Tomlyn-uses-only-what-we-need
   pattern from Cycle 24a.

### Key order (v2, canonical)

`schemaVersion`, `id`, `cellSequence`, `commandId`, `shellId`,
`phase`, `promptStartedAt`, `commandStartedAt`,
`outputStartedAt`, `commandFinishedAt`, `promptText`,
`commandText`, `outputText`, `exitCode`, `sources`,
`extraParams`.

### String-escape policy (unchanged from Cycle 24b)

RFC 8259 minimum (named escapes for `"`, `\`, `\b`, `\f`,
`\n`, `\r`, `\t`; `\u00XX` for any other byte in
`0x00–0x1F`) **plus** DEL (`0x7F`, deliberate superset; many
parsers and viewers mis-handle bare DEL). C1 controls
(`0x80–0x9F`), forward slash, U+2028 / U+2029 pass through
unescaped. **Lone UTF-16 surrogates throw** — loud failure
beats silent corruption in a ten-year-old log. `subPromptText`
inside the `phase` object is escaped by the same policy.

### Timestamps

ISO-8601 UTC with 100 ns ticks
(`yyyy-MM-ddTHH:mm:ss.fffffffZ`) — lossless from the Windows
clock. `option` timestamps serialise as `null` when `None`.

## Round-trip discipline

`IOCell.parseFromJsonl : string -> Result<IOCell, ParseError>`
ships in **PR-W2** (maintainer-only; no UI surface). It is a
hand-rolled parser that branches on `schemaVersion` (only `2`
supported in v1; `Result.Error` for `1` with a clear message)
and is exercised by FsCheck round-trip property tests
(`forall c, parseFromJsonl (formatIOCellAsJsonl c) = Ok c`).
Any future schema change that is not round-trip-faithful
fails CI immediately — the confidence canary.

## See also

- [`adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md)
  — the decision record (IOCell / ContentHistory-sole-substrate
  / OutputDispatcher-sole-channel).
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — stubbed pointer; the
  pre-pivot substrate research doc lives in git history.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — F# 9 / .NET 9
  serializer gotchas (nullness, NUL bytes, escape policy).
