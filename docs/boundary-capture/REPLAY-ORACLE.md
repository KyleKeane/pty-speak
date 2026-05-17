# Boundary replay oracle

Deterministic regression suite that replaces manual dogfood for
the cmd cell-seal / boundary track. Replays a committed
`RawShellRecorder` trace (`cmd/*.txt`) through the **real
Terminal.Core pipeline** and asserts the resulting
`SessionModel.IOCell` history.

## Why

The 40 hand-built `SessionModelTests` cases never caught
C1/C2/C3 because they construct ContentHistory directly and so
do **not** reproduce the production ordering that actually
breaks: the reader thread appends a whole PTY chunk's text
(command output **and** the next prompt's path) to ContentHistory
*before* any boundary is handled, and a boundary's own
ContentHistory marker is appended *after* `extractIOCell` runs.
The oracle drives that exact ordering headlessly, so a boundary
regression is caught in CI, deterministically, instead of by
non-deterministic manual NVDA dogfood.

## Design (decisions locked, R-A)

- **Replay, don't simulate.** Parser → ContentHistory → Screen
  (OSC-133 `PromptBoundary`) → SessionModel — the production
  seam. No WPF / P-Invoke / real `cmd.exe` / PTY.
- **Chunk-granular, recorded order.** Per recorded chunk: the
  whole chunk's events are appended to ContentHistory first, then
  `Screen.Apply` fires that chunk's boundaries, then each
  boundary is handled (the SessionModel subset of
  `Program.fs handlePromptBoundary`). This is the load-bearing
  fidelity — the C1/C2 lesson.
- **Virtual clock.** A clock derived from the trace's µs
  timestamps reproduces idle gaps (incl. a user idling while
  thinking) with zero real waiting → fast, non-flaky CI.
- **Seed prompt.** The C1–C3 traces were captured with
  `Ctrl+Shift+T` toggled *after* the prompt was shown, so the
  cell's opening `;A`/`;B` predate the trace. The harness seeds a
  synthetic "joined at a ready prompt P" (P = the stable cmd
  prompt the trace exhibits) so the captured closing `;D` seals
  the cell — faithful to the mid-session production state.
  **Future captures should toggle `Ctrl+Shift+T` *before* the
  prompt** for a full lifecycle (then the seed is unnecessary).
- **IOCell-only oracle.** Asserts cell count + per cell
  `{CommandText, OutputText, Phase, ExitCode}`. SpeechCursor is
  explicitly out of scope (deprecating).

## Expectation files

One trace ⇒ one `<name>.expected` beside it (hand-rolled,
schemaVersioned per ADR-0004 wire discipline). `#`/blank lines
ignored; `key=value` (value may contain `=`):

```
schemaVersion=1
seedPrompt=<the mid-prompt-join seed P>
cellCount=<int>
cellN.phase=Sealed
cellN.commandContains=<substr>      (optional)
cellN.outputContains=<substr>       (optional)
cellN.outputNotContains=<substr>    (optional)
```

Assertions are substring-based on purpose — exact
Command/Output boundary text is slice-semantics-sensitive (P3's
concern); the oracle pins the load-bearing invariants (seal
count, phase, no prompt-path bleed).

## Fixture hygiene — lone 0x20 payloads

A recorder line whose entire payload is one printable space
(`... 1B | ` + 0x20) is **all trailing whitespace** and is
silently stripped in a paste→chat→commit round-trip (C1/C2
line 12/13 — the space in `ECHO HI` — hit exactly this; the
oracle caught it as a `"ECHOHI"` cmd-text mismatch). Two
defences, both shipped R-B1:

- **Normalise** a lone 0x20 payload to `\x20` in committed
  traces (`unescape`-equivalent, immune to whitespace
  stripping). Apply to any future capture before committing.
- **Loud guard** in `parseTrace`, scoped to the strip
  signature only: a declared count ≥1 that decodes to an
  **empty** payload `failwith`s naming the file + line, so the
  corruption fails at the parser, not three layers away as a
  confusing assertion. It is deliberately *not* a general
  unescape-fidelity check — the recorder counts raw PTY bytes
  and `unescape` legitimately differs by ~1 on large OSC/ST
  chunks (extraction tolerates that).

## Status

- **R-A:** harness + the **C3** (`set /p`) scenario —
  deterministically retro-validated P2′ (#423) on the real
  defect bytes. `tests/Tests.Unit/BoundaryReplayOracle.fs`.
- **R-B1:** externalised per-trace `*.expected` files;
  **C1/C2** scenarios added (locks the prompt-path-bleed fix +
  the slow/fast determinism on the real echo traces). Runs as
  part of the standard **Build and test** job — the oracle
  facts are plain `Tests.Unit` xUnit cases (~2 s); a separate
  CI job would only duplicate the .NET runner/restore/build
  infra for no coverage gain (R-B1-followup removed it).
- **R-B2 (in progress):** **C5** backspace/retype landed —
  real capture (`ECHO HELLOXX` → 2×Backspace → `ECHO HELLO`);
  asserts the deleted `XX` never reaches `CommandText`
  (`commandNotContains=X` — only the two deleted bytes contain
  X). Added the `commandNotContains` schema key. **C6**
  long-idle mid-compose still awaiting capture.
- **R-C onward:** P3 (inline sub-prompt) and all further
  boundary work gated by this oracle, never manual dogfood.

Generalises ADR 0007 D6 (on-send test-oracle).
