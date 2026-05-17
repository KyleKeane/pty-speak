# Boundary diagnostic capture — Cycle 52 cell-seal track

Ground-truth raw-byte traces for the **cmd cell-seal / boundary
computational-accuracy** investigation. The active defect: the cmd
`set /p` input-test seals **no command cell** (extraction drops the
cell per [ADR 0004](../adr/0004-iocell-model-for-shell-interaction.md)
drop-on-`None`; boundary detection lives in the transport
`ShellAdapter` per [ADR 0006](../adr/0006-three-layer-refoundation.md)).
This pass captures real byte timelines *before* any fix, so the
seal/boundary correction is driven by data, not inference.

Instrument: **B1** — `RawShellRecorder`
([`src/Terminal.Core/RawShellRecorder.fs`](../../src/Terminal.Core/RawShellRecorder.fs)),
toggled by `Ctrl+Shift+T` (PR #417, merged 2026-05-17). Writes
`%LOCALAPPDATA%\PtySpeak\extracts\rawtrace-<ts>.txt`, copies to
clipboard, announces the path.

## Safety rule — what may be committed here

The recorder captures **raw PTY bytes in both directions** at the
`Terminal.Core` layer — every byte the shell emits and every byte
typed, including anything sensitive. Per
[`SECURITY.md`](../../SECURITY.md) PO-5 and
[`docs/LOGGING.md`](../LOGGING.md):

- **Only commit traces from the deterministic controlled scenarios
  catalogued below** (`echo hi`, the `Diagnostics → CMD Interaction
  Tests` fixtures). These are non-secret by construction.
- **Never commit a free-form interactive session trace.** It may
  contain credentials, tokens, paths, or env values. If a trace
  strays outside a catalogued scenario, it does not belong in the
  repo — analyse it from the maintainer's paste in chat and discard.

## Capture protocol

1. `Ctrl+Shift+T` — hear the start-recording warning.
2. Run **exactly one** catalogued scenario, start to finish.
3. `Ctrl+Shift+T` — recorder writes the trace file + announces path.
4. One scenario ⇒ one `rawtrace-<ts>.txt`. Do **not** chain
   scenarios in a single recording window — separable files diff
   cleanly; a combined trace is far harder to read.
5. Toggle on immediately before launching the scenario and off
   immediately after it completes — keep idle noise around the
   load-bearing bytes minimal.

## Scenario catalogue

| ID | Shell | Scenario | Why | Expected | Status | Trace |
|----|-------|----------|-----|----------|--------|-------|
| C1 | cmd | `echo hi`, slow, deliberate pauses, wait for output | clean single IOCell, generous timing — the reference "correct" trace | seals 1 cell | _pending_ | — |
| C2 | cmd | `echo hi`, typed as fast as possible | same logical cell, timing-stressed — isolates timing-sensitivity vs C1 | seals 1 cell | _pending_ | — |
| C3 | cmd | `Diagnostics → CMD Interaction Tests → Text Input` (`set /p`) | **the defect** — this scenario seals no cell | currently seals 0 (bug) | _pending_ | — |
| C4 | cmd | multi-line echo / `dir` interaction test (optional) | a cell that *does* seal but with output volume — brackets C3 | seals 1 cell | _pending_ | — |

PowerShell capture (P-series) is **deferred** until the cmd
boundary mechanics are understood — R5 PowerShell already shipped
and dogfood-passed; cmd `set /p` is the open bug. See
[`docs/SESSION-HANDOFF.md`](../SESSION-HANDOFF.md) § Next stage.

Raw traces are stored under `docs/boundary-capture/cmd/<ID>-<slug>.txt`
(committed only because the catalogued scenarios are deterministic
non-secret fixtures — see the safety rule).

## Per-scenario analysis

### C1 — `echo hi` slow (reference)

_Awaiting trace._ Will record: prompt-emit bytes, the
`PromptStart`/`PromptEnd` boundary markers (if any), command-echo
bytes, output bytes, the next-prompt boundary that should seal the
cell.

### C2 — `echo hi` fast (timing stress)

_Awaiting trace._ Diff against C1: does faster keystroke delivery
change byte interleaving or coalesce reads such that the boundary
detector sees a different stream?

### C3 — `set /p` input-test (the defect)

_Awaiting trace._ The key question: where in the byte timeline
does the boundary that *should* seal the cell go missing — is the
`PromptStart` marker absent, emitted in a position the detector
doesn't recognise, or present-but-dropped downstream?

### C4 — multi-line / `dir` (volume bracket, optional)

_Awaiting trace._ Confirms volume alone doesn't break sealing,
isolating C3's failure to the `set /p` sub-prompt shape.
