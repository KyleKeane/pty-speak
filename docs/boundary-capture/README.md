# Boundary diagnostic capture — Cycle 52 cell-seal track

Ground-truth raw-byte traces for the **cmd cell-seal / boundary
computational-accuracy** investigation. **Post-capture finding
(2026-05-17): the defect is general, not `set /p`-specific.** It
*degrades with interaction complexity* — even synchronous
`echo hi` (C1/C2) bleeds the trailing next-prompt path into the
sealed cell's announced output; the `set /p` input-test (C3)
escalates that to sealing **no command cell** at all (extraction
drops it per
[ADR 0004](../adr/0004-iocell-model-for-shell-interaction.md)
drop-on-`None`; boundary detection lives in the transport
`ShellAdapter` per
[ADR 0006](../adr/0006-three-layer-refoundation.md)). This pass
captures real byte timelines *before* any fix, so the
seal/boundary correction is driven by data, not inference. See
**Cross-scenario synthesis** at the foot of this file.

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
| C1 | cmd | `echo hi`, slow, deliberate pauses, wait for output | intended-clean control (turned out **not** clean — see analysis) | in-app: spoke `HI` **+ next-prompt path** — boundary bleed | **captured 2026-05-17** | [`cmd/C1-echo-hi-slow.txt`](cmd/C1-echo-hi-slow.txt) |
| C2 | cmd | `echo hi`, typed as fast as possible | same logical cell, timing-stressed — isolates timing-sensitivity vs C1 | C1's prompt-path bleed **+ repeated speech at start** | **captured 2026-05-17** | [`cmd/C2-echo-hi-fast.txt`](cmd/C2-echo-hi-fast.txt) |
| C3 | cmd | `Diagnostics → CMD Interaction Tests → Text Input` (`set /p`) | **the defect** — this scenario seals no cell | **no cell sealed** (drop-on-`None`) | **captured 2026-05-17** | [`cmd/C3-set-p-text-input.txt`](cmd/C3-set-p-text-input.txt) |
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

> **Correction (2026-05-17, post in-app result).** This section
> originally framed C1 as the *clean* reference. The maintainer
> confirmed the in-app behaviour: NVDA spoke **`HI` *and* the
> next-prompt path** (`C:\…\Terminal.App>`). So C1 does **not**
> seal a clean cell — it announces the output *plus* the trailing
> `;A <prompt> ;B`. The byte-level findings below stand; the
> "seal is the `;D` / C1 is the clean shape" conclusion does
> not — superseded by **Cross-scenario synthesis** at the foot.

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

Implication for the seal bug (revised): with no `;C`, the
extractor cannot bound the output region by `;C → ;D`.
Output-start is implicit (immediately after the echoed `\r\n`).
The byte stream *does* carry a clean `;D` followed by the next
prompt's `;A <prompt> ;B` in one flush — so the markers needed
to fence the cell are **present**. But the in-app result shows
the announce **includes the post-`;D` `;A <prompt> ;B`** (the
spoken "HI + prompt path"). Therefore the extractor is **not
using `;D` as a hard output terminator** and is **not fencing
the trailing next-prompt `;A…;B` out** of the just-sealed
cell's body. The defect is present even in this simplest
synchronous case; it is not introduced by `set /p`. See
**Cross-scenario synthesis**.

In-app result (maintainer-confirmed 2026-05-17): NVDA spoke
`HI` followed by the prompt path — output announced but the
trailing prompt bled in. (`Ctrl+Shift+H` Version line still
wanted for the record to pin the dogfood build ≥ `de74a3f`.)

### C2 — `echo hi` fast (timing stress) — captured 2026-05-17 14:05 UTC

Trace: [`cmd/C2-echo-hi-fast.txt`](cmd/C2-echo-hi-fast.txt).
Maintainer-observed symptom: **repeated speech at the beginning**
when typing fast. (Note: C1 itself was later confirmed to bleed
the trailing prompt path — see the C1 correction — so C2's
in-app behaviour is *that same bleed* **plus** the fast-typing
repeated speech. "C1 baseline" below means the byte-stream
baseline, not a clean-announce baseline.)

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

### C3 — `set /p` input-test (the defect) — captured 2026-05-17 14:13 UTC

Trace: [`cmd/C3-set-p-text-input.txt`](cmd/C3-set-p-text-input.txt)
— 14 events, IN 6 B, OUT 481 B. In-app result: **no command
cell sealed** (the open defect from SESSION-HANDOFF § Next).

Decoded timeline:

1. `IN \r` → `OUT \r\n` — launches `test-02-text-input.cmd`.
   **No `OSC 133;C`** (consistent with C1/C2 — cmd's injection
   never emits command-start).
2. `OUT 254B` — `=== PTYSPEAK-TEST-START… ===\r\n` ·
   `This test prompts…\r\n` · **`Enter your name:`** ·
   `ESC]0;…cmd.exe - "…test-02…cmd"… BEL`
   (an **OSC 0 window-title**, **BEL-terminated** `\a`).
3. ~12 s blocking wait, then `IN N`/`A`/`M`/`E` each with a
   1-byte `OUT` echo, then `IN \r` → `OUT \r\n`.
4. `OUT 219B` — `ESC[?25l` · `Hello, NAME! …` ·
   `=== PTYSPEAK-TEST-END… ===` · `ESC[18;1H` ·
   `ESC]0;…cmd.exe BEL` (title reset) · `ESC[?25h` ·
   **`OSC 133;D ST`** · `OSC 133;A ST` · prompt ·
   `OSC 133;B ST`.

Findings:

1. **The `set /p` sub-prompt carries zero OSC-133 markers.**
   `Enter your name:` has no `;A`/`;B` around it and no
   `;C`/`;D`. cmd's shell-integration only wraps the top-level
   command loop; an in-script `set /p` is invisible to it.
   Across the whole test-02 interaction the *only* OSC-133
   markers are a single `;D` at the very end (closing the whole
   `.cmd`) plus the next top-level prompt's `;A`/`;B`.
2. **Mixed OSC terminators.** OSC 0 titles are **BEL**-terminated
   (`\a`); OSC 133 is **ST**-terminated (`ESC \`). They
   interleave in the same write — the parser must tolerate both.
3. **Why no cell seals.** test-02's own opening `;A`/`;B`
   predate the recording (joined mid-prompt, like C1).
   Mid-`Executing`, a burst of `IN`+echo arrives (the `set /p`
   read). ADR 0004 intends sub-prompts to be *inline state
   inside the parent IOCell*, but the boundary/state machine
   (ADR 0003 `Composing`/`Executing`) mis-segments: the echoed
   `NAME` mid-command reads as fresh prompt composition, a new
   cell is opened that has **no `PromptStart`/`;A`**, ADR 0004
   drop-on-`None` discards it, and test-02's real close (`;D`)
   lands on the wrong/dropped cell. Net: **no cell sealed.**
4. Same root mechanism as the C1/C2 bleed, escalated: the
   extractor neither treats `;D` as a hard output terminator
   nor fences the trailing `;A…;B`; once an unmarked sub-prompt
   + mid-command input is added, "bleed" becomes "total
   non-seal".

Fix territory (separate PR, after the capture pass — *not* in
this record): the seal must (a) anchor the cell close on the
top-level `;D`, (b) fence the post-`;D` `;A <prompt> ;B` out as
the **next** cell's prompt (not appended body), and (c) hold the
current cell open across an unmarked sub-prompt — treating any
`IN`+echo between command-launch and `;D` as ADR 0004 inline
sub-prompt state, never a new `Composing` cell that then
drops-on-`None`. Parser must accept BEL- and ST-terminated OSC
interleaved.

Outstanding for the record (non-blocking): the exact NVDA
utterance during C3 (silence? partial? the sub-prompt text?)
and the `Ctrl+Shift+H` Version line.

### C4 — multi-line / `dir` (volume bracket, optional)

_Awaiting trace._ Confirms volume alone doesn't break sealing,
isolating C3's failure to the `set /p` sub-prompt shape.

## Cross-scenario synthesis (2026-05-17)

After C1–C3 + the maintainer-confirmed in-app results, the
picture is **one boundary defect that degrades with interaction
complexity**, not three separate bugs (with one channel-layer
rider):

| | byte stream | in-app result | boundary verdict |
|---|---|---|---|
| **C1** slow | clean `… ;D ;A <prompt> ;B` tail | spoke `HI` **+ prompt path** | seals, but **bleeds trailing prompt** |
| **C2** fast | **byte-identical to C1** | C1's bleed **+ repeated speech at start** | same bleed + channel timing artifact |
| **C3** `set /p` | unmarked sub-prompt + mid-cmd `IN`/echo; one terminal `;D` | **no cell at all** | total failure |

Conclusions:

1. **The OSC-133 markers needed to fence a cell are present in
   the byte stream** (C1 proves a well-formed `;D` then
   `;A <prompt> ;B`). The bug is downstream of transport: the
   extractor does not use `;D` as a hard output-end nor fence
   the following `;A…;B` out of the sealed cell.
2. **C1 = C2 at the byte level** (timing only). C2's *extra*
   "repeated speech at the start" is a **separate
   channel-layer** timing artifact (fast `UserInputEcho`
   re-driving the SpeechCursor before `Composing→Executing`
   settles; ADR 0003 / 0008). It is **not** the boundary bug
   and must be fixed in the accessibility channel, not the
   boundary layer.
3. **C3 is the same boundary defect escalated** by an unmarked
   `set /p` sub-prompt + mid-`Executing` input: "bleed"
   becomes "drop-on-`None`, no cell".
4. The fix is therefore **one boundary/extraction change**
   (seal on top-level `;D`; fence the trailing next-prompt;
   hold the cell open across unmarked sub-prompts as ADR 0004
   inline state; tolerate mixed OSC terminators) **plus one
   independent channel-layer change** for C2's repeated-speech
   timing artifact. Two PRs, two layers — tracked separately,
   *after* this capture pass, per the data-first discipline.
