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

## Status

- **R-A (this):** harness + the **C3** (`set /p`) scenario
  asserted inline — deterministically retro-validates P2′
  (#423) on the real defect bytes. `tests/Tests.Unit/BoundaryReplayOracle.fs`.
- **R-B:** externalise per-trace `*.expected` files; add C1/C2
  + new capture dimensions — **backspace/retype** (deleted chars
  must not reappear in `CommandText`) and **long idle**
  mid-compose; the CMD-test scripts; a dedicated
  "Boundary replay oracle" CI job.
- **R-C onward:** P3 (inline sub-prompt) and all further
  boundary work gated by this oracle, never manual dogfood.

Generalises ADR 0007 D6 (on-send test-oracle).
