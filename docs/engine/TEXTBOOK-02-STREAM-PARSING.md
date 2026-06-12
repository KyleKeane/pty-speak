# Engine textbook, chapter 2 — stream parsing and the participant seam

> Files: `src/Engine.Core/AgentEvent.fs`, `ClaudeStreamJson.fs`;
> `src/Engine.Participants/ClaudeCli.fs`. Tests:
> `ClaudeStreamJsonTests.fs`, `ClaudeCliTests.fs`. Decision:
> ADR 0011 E4; spec §4.3 (the primary surface is the agent's
> native structured stream), §12 (one seam, N tools).

## AgentEvent — the normalized vocabulary

Every participant's native output normalizes into ONE typed
vocabulary before the engine sees it: `SessionInit` (carries
the id continuity rides on), `AssistantMessage` of content
blocks (`Text`, `ToolUse` with verbatim input JSON,
`UnknownBlock`), `ToolResults`, `TurnResult`, and the two
honesty cases — `Unknown of eventType * rawJson` and
`ParseError of message * rawLine`. The honesty cases are the
design's load-bearing wall: the 52-cycle lesson of the
terminal era was that *guessing at unrecognized input* is the
unbounded cost; here, anything outside the vocabulary becomes
a typed value that flows to the user as a spoken ambient note
and to the maintainer as a diagnostics entry with the raw
line. Format drift is a feature request, not a crash.

## The parser

`ClaudeStreamJson.parseLine : string -> AgentEvent option` —
one JSON line in, at most one typed event out (`None` only for
blank lines). Wire shape: the Claude Code CLI's
`-p --output-format stream-json --verbose` emits one object
per line: `system/init`, `assistant` envelopes with content
arrays, `user` envelopes carrying `tool_result` blocks (string
or text-block-array content, both flattened), and a final
`result`. The fixture corpus in the tests IS the contract;
when an installed CLI drifts, the recipe in the development
guide (§3) starts from a captured raw line.

Implementation notes worth knowing: `System.Text.Json` is
inbox on `net9.0` (no package); `JsonDocument` is disposed
inside the function and every extracted string is copied
first; all property reads go through total `tryXxx` helpers
that fold JSON nulls and wrong kinds into `None` — under the
repo's F# 9 nullness checking (`FS3261` is a build error),
`GetString()`'s nullability is handled once, in one place.

## The pump and the process

`Engine.Participants.ClaudeCli` deliberately contains almost
nothing: `buildArguments` (pure — `-p <prompt> --output-format
stream-json --verbose [--resume sid]`; the prompt passes
verbatim through `ArgumentList`, so flag-looking prompts are
safe — tested), `pumpLines` (pure fold over a
`unit -> string option` reader: every line through the parser,
every event to the callback — tested with list-backed
readers), and `runTurn`, the only impure dozen lines:
`ProcessStartInfo` with redirected stdio, **stderr drained
concurrently** via `ErrorDataReceived` (a full stderr pipe
would deadlock the stdout pump — the classic trap),
null-checked `Process.Start`/`ReadLine`, and a typed
`TurnOutcome { ExitCode; StdErr }`.

## Why per-turn invocation

A long-lived bidirectional process would save spawn time but
adds lifecycle supervision (hang detection, restart policy,
partial-line buffering) for zero semantic gain — the CLI
persists conversation state itself, so `--resume <sid>` makes
each turn stateless from the engine's side. ADR 0011 E4 keeps
the persistent-process option open as a later optimization
behind the same `onEvent` shape.

## The seam test

The definition of a correctly-built participant: **only the
host's spawn choice knows it exists.** Ingest, navigation,
narration, the notebook, persistence — all consume
`AgentEvent`/chunks and need zero changes for participant N+1
(spec §12: build the seam honestly for N, implement one). A
second participant is `buildArguments` + a translation layer
into `AgentEvent` + `runTurn`, nothing else.
