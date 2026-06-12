# Engine textbook, chapter 10 — the host

> File: `src/Engine.Host/Program.fs` (the one imperative
> file). Decisions: ADR 0011 E8/E9, 0013 N2, 0014 integration.

## The shape: one lock, three threads, zero shared decisions

The host owns ALL mutable state in one record (`HostState`:
session, navigator, notebook + its cursor, mode, the attention
queue, the speaking flag, turn-in-flight, rate) behind **one
lock**. Three threads touch it:

- the **console thread** — the key loop, verbs, compose lines;
- the **turn thread** — one background thread per turn,
  folding participant events through `Ingest` and publishing;
- **audio callbacks** — `UtteranceCompleted` (the drain) and
  NAudio's playback thread (never touches state).

The discipline that keeps this trivially safe: every lock
section is a *pure-function call plus field assignment* —
decisions happen in Engine.Core on immutable values; the lock
only swaps which value the fields hold. No lock is ever held
across I/O, speech, or a publish.

## The speech drain

The host speaks at most one utterance at a time: `speakNext`
dequeues (foreground first, ambient otherwise) only when not
already speaking; `UtteranceCompleted` — which SAPI fires for
finished *and cancelled* utterances — clears the flag and
drains again. This tiny protocol is what makes the attention
queue's ordering guarantees real at the audio device.
`speakNow` (every user-initiated read) cancels current speech,
supersedes stale queued foreground, keeps ambient, then
drains.

## Turns

`startTurn` captures the request (pure), publishes, then runs
the participant on a background thread; each incoming
`AgentEvent` folds under the lock and publishes outside it.
The turn thread ends by clearing the in-flight flag,
publishing any process-level failure as an ambient note, and
**auto-saving the session**. Compose and rerun refuse while a
turn is in flight (one conversation, one turn — a v1
simplification the bus does not require).

## Files

Everything lives under `%LOCALAPPDATA%\PtySpeak\`:
`engine.toml`; `engine-sessions\` (per-run session snapshots +
`session-latest.jsonl` + per-run event logs + notebook
exports); `engine-diagnostics\` (dump files). All writes are
try/with + diagnostics-recorded — a full disk degrades to a
spoken warning, never a crash.

## The key loop

Four hardwired arrow arms (transcript safety net), then one
table lookup: `KeyMap.tryFind mode key.KeyChar bindings →
runVerb`. `runVerb` is the only mode-aware code: movement
verbs route to the navigator or the notebook cursor; everything
else is mode-shared. Unbound keys are silently ignored —
random keyboard contact must not produce noise.

## What the host deliberately does NOT do

No parsing, no chunking, no sealing rules, no narration
strings (beyond prompts/confirmations), no cue choices, no
queue policy, no serialization — each of those is a tested
core module. The integration test of the architecture is this
file's diff history: every feature cycle so far changed the
host by *wiring*, not by *logic*.
