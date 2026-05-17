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
| C1 | cmd | `echo hi`, slow, deliberate pauses, wait for output | clean single IOCell, generous timing — the reference "correct" trace | seals 1 cell | **captured 2026-05-17** | [`cmd/C1-echo-hi-slow.txt`](cmd/C1-echo-hi-slow.txt) |
| C2 | cmd | `echo hi`, typed as fast as possible | same logical cell, timing-stressed — isolates timing-sensitivity vs C1 | seals 1 cell; user heard repeated speech at start | **captured 2026-05-17** | [`cmd/C2-echo-hi-fast.txt`](cmd/C2-echo-hi-fast.txt) |
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

### C1 — `echo hi` slow (reference) — captured 2026-05-17 14:02 UTC

Trace: [`cmd/C1-echo-hi-slow.txt`](cmd/C1-echo-hi-slow.txt) — 17
events, IN 8 B, OUT 101 B. Slow typing (1.4–7.5 s between chars),
strict per-keystroke `IN <char>` → `OUT <char>` local echo, then
on Enter `IN \r` → `OUT \r\n` → a single 92-byte OUT flush.

Decoded 92-byte flush:

```
ESC[?25l  HI  ESC[5;1H  ESC[?25h  OSC 133;D ST  OSC 133;A ST  C:\Users\Kyle\git\pty-speak\src\Terminal.App>  OSC 133;B ST
```

Findings:

1. **cmd OSC-133 injection (ADR 0006 R2) is live and
   well-formed.** Correct introducer, **ST-terminated**
   (`ESC \`, i.e. `\e\`), not BEL. Prior-command close + next
   prompt open both present in one flush.
2. **Marker set is A, B, D — `OSC 133;C` is absent.** There is
   no command-start / output-start marker between Enter and the
   `HI` output: the flush goes `[?25l` → `HI` → cursor-move →
   `;D` → `;A` → prompt → `;B`. cmd's injected integration does
   not emit `;C` at all (at least for `echo`).
3. **`OSC 133;D` carries no exit code** — literal `133;D`, not
   `133;D;0`. Per ADR 0008 / ADR 0009 this is the honest
   `CellOutcome = Indeterminate` case: no exit status is
   transported, so none must be invented.
4. Recording joined mid-prompt (first event is `IN E` at an
   already-ready prompt), so this command's *own* opening
   `;A`/`;B` predate the trace. The `echo hi` cell is bounded by
   `[pre-trace ;B] … [;D in the 92-byte flush]`; the next
   prompt's `;A … ;B` immediately follow the seal.

Implication for the seal bug: with no `;C`, the extractor cannot
bound the output region by `;C → ;D`. Output-start is implicit
(immediately after the echoed `\r\n`); the **seal is the `;D`**,
with the next prompt's `;A` arriving in the same flush. This
makes the C3 (`set /p`) question precise: does the interactive
read **defer or suppress the `;D`** (the cell never sees its
closing boundary → dropped on `None` per
[ADR 0004](../adr/0004-iocell-model-for-shell-interaction.md)),
or does it interleave a sub-prompt between `;B` and `;D` that the
single-IOCell extractor mishandles? C1 is the clean A/B/(no
C)/D shape to diff C3 against.

Outstanding for the record (non-blocking): the C1 in-app result
— did NVDA announce the sealed `echo hi` output, and the
`Ctrl+Shift+H` Version line (confirm the dogfood ran `de74a3f`
or later, not a stale build).

### C2 — `echo hi` fast (timing stress) — captured 2026-05-17 14:05 UTC

Trace: [`cmd/C2-echo-hi-fast.txt`](cmd/C2-echo-hi-fast.txt).
Maintainer-observed symptom: **repeated speech at the beginning**
when typing fast.

**C2 is byte-identical to the C1 baseline.** Same 17 events,
same IN 8 B / OUT 101 B, same strict per-keystroke 1:1 local
echo, same `IN \r` → `OUT \r\n`, same 92-byte OSC-133 flush
(`ESC[?25l HI ESC[5;1H ESC[?25h ;D ;A <prompt> ;B`). The only
difference is the `+elapsed_us` timestamps:

| | inter-keystroke gap | pre-Enter gap |
|---|---|---|
| C1 (slow) | ~1.4–1.9 s | ~7.5 s |
| C2 (fast) | ~0.13–0.24 s | ~0.24 s |

~6–30× faster, **zero byte-stream difference**. No coalescing,
no merged reads, no reordering — cmd's local echo emits one
discrete `IN`/`OUT` pair per keystroke irrespective of speed,
and the post-Enter OSC-133 flush is unchanged.

Findings:

1. **The repeated-speech artifact is not in the transport / byte
   layer.** `RawShellRecorder` taps the `Terminal.Core` PTY
   seam; identical bytes enter, so the duplication is produced
   *above* the tap — in the accessibility channel / announce
   path. Fast-arriving per-keystroke `UserInputEcho` bytes drive
   the SpeechCursor / review-cursor to re-read early content
   before the `Composing → Executing` classification settles
   (ADR 0003 / ADR 0008 territory). A timing-sensitive
   re-announce, not a boundary parse.
2. **Distinct bug from the C3 cell-seal defect.** C2 = channel
   re-announce under fast input (fix lives in the accessibility
   channel); C3 = boundary never seals the cell (fix lives at
   the transport boundary). They must not be conflated.
3. Consistent with the Cycle-52 "substrate sound" posture: the
   bytes are deterministic and the OSC-133 mechanism (C1-proven
   well-formed) is byte-identical here. The regression is
   downstream channel behaviour, not the boundary mechanism.

Outstanding for the record (non-blocking): the exact NVDA
utterance heard — which words, how many repeats — to localise
the announce site (prompt re-read vs per-char echo re-read), and
the `Ctrl+Shift+H` Version line.

### C3 — `set /p` input-test (the defect)

_Awaiting trace._ The key question: where in the byte timeline
does the boundary that *should* seal the cell go missing — is the
`PromptStart` marker absent, emitted in a position the detector
doesn't recognise, or present-but-dropped downstream?

### C4 — multi-line / `dir` (volume bracket, optional)

_Awaiting trace._ Confirms volume alone doesn't break sealing,
isolating C3's failure to the `set /p` sub-prompt shape.
