# RFC 0001 — LinearTextStream Substrate + Streaming-Incomplete Emission Protocol

**Status:** Draft (Cycle 33; pivot gate before Cycle 34 implementation)
**Author:** Claude Code via maintainer-directed plan-mode
**Drafted:** 2026-05-09
**Snapshot:** 2026-05-09
**Supersedes:** [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) §7 (informal streaming-incomplete protocol)
**Implements:** [`docs/adr/0001-substrate-channel-dichotomy.md`](../adr/0001-substrate-channel-dichotomy.md) substrate-side concrete

## Abstract

`LinearTextStream` is the new canonical substrate-of-truth for linear and streaming workloads in pty-speak. It replaces the screen-grid-based extraction path (`SessionModel.fs:338-375` `extractContent`) with an append-only byte-buffered representation of the parser-emitted output, committed at well-defined seams. The screen grid demotes to its rightful role: the substrate-of-truth for alt-screen TUI workloads only, per [ADR 0001](../adr/0001-substrate-channel-dichotomy.md)'s substrate / channel dichotomy.

This RFC specifies:

1. **The producer module** — `src/Terminal.Core/LinearTextStream.fs`, an append-only buffer with **tail mask** semantics for overwrite-class bytes, sized to a 4 MB per-tuple cap with truncation as a safety valve.
2. **The streaming-incomplete emission protocol** — a five-tier seam hierarchy (semantic prompt seam > newline > idle quantum > max-bytes > max-time), six concrete cadence parameters, a seven-rank live-region detection classifier driven from VT500 parser events, and a sealed/unsealed event extension that mirrors RFC 9112 §8's incomplete-message contract.
3. **The drain-checkpoint-swap protocol** — a three-phase regime transition for Stream ↔ TUI substrate swaps, anchored at `ESC[?1049h` / `ESC[?1049l` boundaries.
4. **The three exemplar canonical displays** — high-level UIA / ARIA / NVDA contracts for raw text, interactive list, and form with text input. Full per-primitive specs live in [`docs/CANONICAL-DISPLAY-CATALOG.md`](../CANONICAL-DISPLAY-CATALOG.md); this RFC names them and locates the catalog as authoritative.

This is the **pivot gate** of the substrate-first framework plan ([Strategic plan](../PROJECT-PLAN-2026-05-09.md), strategic codename `we-do-not-need-fluffy-simon`). Cycle 34 implements the producer; Cycles 35–36 invert the Stream profile against it; the rest of the framework follows.

## 1. Motivation

### 1.1 The four-part architectural assertion

Per [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) §1 (locked architectural authority), the maintainer-blessed framing is verbatim:

1. The substrate of truth is the byte stream emitted by the PTY. The screen grid is a derived projection.
2. Channels are first-class peers. NVDA-via-UIA, self-voicing TTS, earcons, refreshable braille, FileLogger, and the WPF visual surface are each consumers of the substrate-with-detector-annotations; none is privileged.
3. The detector pipeline (parser → coalescer → profiles → channels) is a deterministic, push-based stream-processing graph. Consumer-specific concerns (UIA peer registration, ANSI-color-as-error-tone, prompt-detection regex) belong at the appropriate stage; the substrate must not absorb them.
4. The screen-cell grid is the substrate-of-truth for **only one** workload class: alt-screen TUI applications (`vim`, `htop`, `less`, `claude`'s alt-screen mode if it had one). For everything else — shell command output, REPL prose, `dir` listings, `git` chatter, Claude's primary streaming text — the linear byte stream is canonical and the grid is a downstream rendering convenience.

### 1.2 The current extraction path is an inversion

Today's `SessionModel.fs:338-375` `extractContent` function reconstructs linear-text content by walking the screen grid:

> Pure row-walker with two independent branches: (1) **CommandText extraction** renders `snapshot[oldPromptRowIndex]` via `CanonicalState.renderRow`, strips the captured `oldPromptText` prefix, returns the remainder; (2) **OutputText extraction** iterates rows `oldPromptRowIndex + 1` through `newPromptRowIndex - 1`, renders each, filters blank lines, joins with `\n`.

This is *cause-and-effect inverted*. The bytes existed first. The grid was derived from them by the VT500 parser applying ANSI semantics. `extractContent` reads the grid back as if it were primary, then **reconstructs the bytes** by re-rendering rows and stripping prefixes.

The inversion is the source of three concrete failure modes observed during Cycle 29b NVDA validation 2026-05-09:

- **Spinner storms** — Claude's `⠋ ⠙ ⠹ ⠸` thinking spinner mutates a single grid row at 10 Hz. `extractContent`'s row-walk treats every spinner frame as new output. NVDA receives ~80 announcements per Claude turn instead of 1.
- **Red-tone misfires** — `git status` lines styled with SGR-red trigger `EarconProfile`'s color-as-error detector. The bytes that produced the red glyphs were never errors; the grid's ANSI reverse-projection is mistaken for an error signal.
- **Unexercised SelectionProfile** — Claude's auto-trust mode skips the confirmation prompt's grid-row-mutation entirely. The substrate-side detector (`SelectionDetector.fs`) couldn't fire because no row was *visually styled* in a selection-prompt-like way. The byte stream contained the prompt regardless; the grid never saw it.

### 1.3 Why a doc-only pivot gate

Cycle 33 is **doc-only by design**. Substrate inversion is the largest architectural decision in the framework plan; locking the design before code lands is non-negotiable per the walking-skeleton discipline ([CONTRIBUTING.md](../../CONTRIBUTING.md) "Walking-skeleton discipline"). This RFC + the companion catalog are the substrate; Cycle 34 ships the producer module against it; Cycle 35 inverts the Stream profile; Cycle 36 validates against the advanced-CMD content matrix.

The two research docs that landed independently between Cycle 30 and Cycle 31a — [`emission-paradigms.md`](../research/emission-paradigms.md) and [`Output-paradigms.md`](../research/Output-paradigms.md) — are RFC-grade prior-art surveys. This RFC lifts ~5 specific items verbatim from emission-paradigms.md and uses Output-paradigms.md as authoritative source for the catalog. Attribution is preserved via inline section references.

## 2. Current Extraction Path: `SessionModel.fs:338-375`

### 2.1 The function being replaced

`extractContent` (private; F#) is the runtime call that produces a SessionTuple's `CommandText` and `OutputText` fields when a prompt boundary fires. Signature:

```fsharp
let private extractContent
        (oldPromptText: string)
        (oldPromptRowIndex: int option)
        (newPromptRowIndex: int option)
        (snapshot: Cell[][])
        : string * string
```

It is invoked by `applyAndCapture` → `finalizeAndEnqueue` (`SessionModel.fs:406-436`) at every SessionTuple finalization. Two finalization arms reach it:

- **Interrupt path** (`SessionModel.fs:569-574`) — `(Some active, PromptStart)` when a new prompt boundary arrives while a tuple is still active (Ctrl-C scenarios; mid-output prompts).
- **Normal completion path** (`SessionModel.fs:642-687`) — `(Some active, CommandFinished exitCode)` when the shell signals command completion via OSC 133 `D` or heuristic equivalent.

### 2.2 The algorithm and its brittleness

The CommandText branch (lines 345–359) reads `snapshot[oldPromptRowIndex]`, calls `CanonicalState.renderRow` to convert cells back to a string, strips the prefix that matched the prompt regex, and returns the remainder. Defensive: returns `""` on row-out-of-bounds or prefix-mismatch.

The OutputText branch (lines 360–374) iterates rows `[oldPromptRowIndex + 1 .. newPromptRowIndex - 1]`, renders each via `CanonicalState.renderRow`, filters empty rows, joins with `\n`. Defensive: returns `""` on missing indices or zero-length range.

Five concrete brittleness signals:

1. **Cursor moves invalidate row indices.** A `\r` (carriage return without newline) repositions the cursor; subsequent bytes overwrite the prior row. The row-walk sees only the post-overwrite state; the original bytes are gone.
2. **Alt-screen entry/exit blanks the grid.** A short-lived TUI invocation (e.g., `git log` with a pager) clears the grid on exit; `extractContent` after an interrupt finds no rows.
3. **Wrap and scroll lose history.** Output longer than the grid's row count scrolls; rows that scrolled off are unrecoverable from the snapshot.
4. **SGR styling is fully discarded.** `renderRow` collapses styled cells to plain text; downstream consumers that wanted the styling (`EarconProfile`'s color detection) read the wrong source.
5. **Dual cost.** Every prompt-boundary fire triggers a full row-range render — O(rows × cols) per emission, even though the bytes were already consumed once on the parser path.

### 2.3 Call sites + invocation frequency

`applyAndCapture` (`SessionModel.fs:498-690`) is invoked per `ScreenNotification.PromptBoundary`. The `PathwayPump.handleRowsChanged` (`Program.fs:~1340`) triggers on every parser-emitted row delta; the prompt detector (`HeuristicPromptDetector.fs:202-335`) gates which deltas reach `applyAndCapture` by enforcing a per-shell stability window (100ms cmd/PowerShell, 200ms Claude). On each fire, `extractContent` walks the snapshot.

In a typical Claude session with 30 tool-use turns, `extractContent` runs at minimum 60 times (PromptStart + CommandFinished per turn), each walking ~30 rows × ~120 cols = 3,600 cells. Total runtime walks: 216,000 cells per session. The proposed substrate replaces this with O(1) high-water-mark commits.

## 3. LinearTextStream Producer Design

### 3.1 Module shape

New module: `src/Terminal.Core/LinearTextStream.fs`. F# module, `internal` to Terminal.Core but with a public `T` opaque type for cross-module reference.

```fsharp
namespace Terminal.Core

/// Cycle 34 — append-only byte buffer maintaining the canonical
/// linear substrate per RFC 0001. Hooked into the parser-emit path
/// (Coalescer input edge); commits at seam-hierarchy boundaries.
module LinearTextStream =

    /// Opaque producer instance. Holds the appendable buffer + tail
    /// mask + commit-watermark state.
    type T

    /// Construction. Wired at composition root post-Cycle 34.
    val create : parameters: Parameters -> T

    /// Tunable parameters. Shipped as constants in Cycle 34;
    /// future cycle externalises to TOML `[coalescer]` section.
    type Parameters =
        { IdleQuantumMs: int          // 150 ms (default)
          MaxBytesPerEmit: int         // 4096 bytes
          MaxTimeWithoutEmitMs: int    // 2000 ms
          LiveRegionDebounceMs: int    // 250 ms
          RegimeSwitchDrainMs: int     // 500 ms
          PerTupleCapBytes: int }      // 4 * 1024 * 1024 (4 MB)

    /// Default parameters (RFC §5.2 lift from emission-paradigms.md §3.C).
    val defaultParameters : Parameters

    /// Feed bytes from the parser's emit path. Coalescer's input edge
    /// calls this synchronously per parser tick. The producer
    /// classifies bytes (printable / overwrite-class / regime-switch),
    /// appends to the buffer or tail mask, and may emit a commit
    /// notification if a seam is crossed.
    val append : T -> byte[] -> CommitNotification list

    /// Force-finalize the current high-water-mark slice into a
    /// SessionTuple-shaped chunk. Called by SessionModel.applyAndCapture
    /// on prompt-boundary fire (replaces extractContent's row-walk).
    val finalizeHighWaterMark : T -> FinalizedChunk

    /// Drain-checkpoint-swap entrypoint (RFC §6). Called by
    /// PathwayPump on alt-screen toggle.
    val checkpointAndFreeze : T -> CheckpointResult
    val resumeFromFreeze : T -> unit

    /// Commit-time notification. Multiple may emerge per append call
    /// (e.g., a prompt-boundary inside a max-bytes-flushed chunk).
    type CommitNotification =
        | EmittedChunk of EmittedChunk
        | LiveRegionUpdate of LiveRegionUpdate
        | RegimeSwitch of RegimeSwitch

    /// A finalized chunk produced when the high-water-mark advances.
    /// Sealed=true means this is the authoritative final state of
    /// this region; Sealed=false means more bytes may arrive (live-
    /// region in flight, or max-time emitted while output continues).
    type EmittedChunk =
        { Bytes: byte[]
          Sealed: bool
          Truncated: bool         // true if 4MB cap forced drop-oldest
          ProducerWaterMark: int64
          EmittedAt: DateTime }
```

The exact F# signatures are sketches; Cycle 34 may adjust the curry/uncurry shape, parameter naming, and visibility modifiers. The architectural shape — append-only buffer + tail mask + seam-driven commits + sealed/unsealed events — is non-negotiable per this RFC.

### 3.2 Buffer structure

Two buffers, one mask:

- **Committed buffer** — the substrate-of-truth. Append-only; entries flow downstream via `EmittedChunk` notifications. Sized to a soft cap of `PerTupleCapBytes` (4 MB default) per command zone. On overflow, drop-oldest semantics apply (head bytes evicted; tail preserved); the next emitted chunk carries `Truncated: true` so consumers can announce "[output truncated]" if appropriate.
- **Pending buffer** — bytes accumulated since the last seam crossing. Drained at the next seam.
- **Tail mask** — per-row state marking content as overwrite-in-place. When the producer detects an overwrite-class byte (see §5.3 Live-region detection), the affected row is marked tail-masked; subsequent printable bytes overwrite the masked region rather than appending. On seam crossing, only the most recent state of the tail mask is committed (LATEST semantics from RxJS / Project Reactor).

### 3.3 Hookpoint: Coalescer input edge

The Coalescer (`src/Terminal.Core/Coalescer.fs`) was restructured by PR-N (2026-05-04). The state machine moved to `StreamProfile.fs`; Coalescer.fs now contains only the `CoalescedNotification` DU + 5 pure helpers. The new `LinearTextStream.append` callback hooks at the **input edge** of the Coalescer's debounce — i.e., where the parser's emitted bytes first arrive, before the Coalescer's existing batching kicks in.

Concretely, Cycle 34 wires:

```fsharp
// Composition root (Program.fs:~1180), post-Coalescer construction.
let linearStream =
    LinearTextStream.create LinearTextStream.defaultParameters

// Parser tick callback: split the emit between the existing
// Coalescer path and the new linear-stream producer.
let onParserEmit (bytes: byte[]) =
    let commits = LinearTextStream.append linearStream bytes
    for commit in commits do
        match commit with
        | EmittedChunk chunk -> dispatchToStreamProfile chunk
        | LiveRegionUpdate upd -> dispatchToStreamProfileLiveRegion upd
        | RegimeSwitch switch -> dispatchToPathwaySelector switch
    // Existing Coalescer call retained until Cycle 35 inverts it.
    coalescer.Apply bytes
```

Cycle 34 runs the producer **parallel-to-screen** with no consumers — the screen grid path remains authoritative; the producer emits notifications that nothing handles yet. Cycle 35 inverts the Stream profile to consume from the producer; Cycle 36 retires the screen-grid path for non-alt-screen workloads.

### 3.4 Threading

The Coalescer's algorithms run on the **PathwayPump worker thread** (single-threaded notification consumption per `HeuristicPromptDetector.fs:79-82`). The producer's `append` runs on the same thread. No additional synchronization required for the buffer or tail mask; cross-thread reads from the substrate-of-truth happen only at seam-crossing emissions, which marshal through the existing `OutputDispatcher` channel-record path (Cycle 31a `IOutputSink`).

### 3.5 The 4 MB per-tuple cap

The cap is a **safety valve**, not a tuning knob. Pathological inputs — `cat /dev/urandom | python`, malformed shell loops, runaway output from a buggy tool — can produce gigabytes of bytes between prompt boundaries. Without a cap, the producer's committed buffer grows unbounded; the eventual SessionTuple's `OutputText` field would be too large to announce, log, or persist.

The cap operates as **drop-oldest**: when the buffer reaches 4 MB, the oldest 1 MB is evicted to free space, the `Truncated: bool` flag is set on the next emitted chunk and on the eventual SessionTuple. Consumers that care (NvdaChannel announce, FileLoggerChannel log, SessionLogWriter persist) can prefix "[output truncated]" or similar; consumers that don't care (EarconProfile's bell-ring detector) ignore the flag.

The 4 MB number is chosen as ~16 × `MaxBytesPerEmit` (4096 × 1024) — large enough to absorb a long-running `dir /s` traversal of `C:\Windows\System32` (~200K entries × ~80 bytes = ~16 MB) only in the median case; pathological cases will truncate, which is correct. The number is **revisitable in a future cycle** if real workloads surface a misfit.

### 3.6 Session-restore-from-disk: explicitly out of scope

The current JSONL session-persistence path (`SessionPersistence.fs` + `SessionLogWriter.fs`) writes SessionTuples to `%LOCALAPPDATA%\PtySpeak\sessions\session-<SessionId>.jsonl` with a stable schema (`schemaVersion: 1`). The deserialization plumbing exists but is not wired to a restore consumer; restore semantics are explicitly Phase 2 work per Phase 1 exploration findings.

This RFC therefore does **NOT** specify session-restore semantics. The `extractContent` function (`SessionModel.fs:338-375`) is **preserved verbatim post-Cycle 34** in case a future restore implementation re-uses its row-walking logic against a serialized snapshot. The runtime SessionTuple finalize path uses `LinearTextStream.finalizeHighWaterMark`; `extractContent` becomes runtime-unwired but kept for future use.

A future cycle may delete `extractContent` if the restore path is implemented differently (e.g., from a serialized linear-stream slice rather than a re-rendered grid). That decision is out of scope here.

## 4. The Inversion of Cause and Effect

A short conceptual section that crystallizes the architectural shift. May be lifted into [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) §1 as supporting prose.

### 4.1 Three layers of state

The pty-speak pipeline maintains three logical layers:

1. **Bytes** — the parser-emitted stream. Append-only, never lossy. The substrate-of-truth.
2. **Cells** — the screen grid. A derived projection of bytes via VT500 parser semantics. Lossy at every cursor move and row scroll.
3. **Annotations** — semantic events overlaid on bytes (PromptBoundary, AltScreenEntered, BellRang, SelectionShown). Produced by detectors that consume bytes (and sometimes cells) and emit boundary-shaped events.

Pre-Cycle-34, downstream consumers (NVDA, FileLogger, EarconProfile) read predominantly from layer 2 (cells) with occasional annotations from layer 3. Reconstructing bytes from cells is therefore the standard operation — and it is structurally lossy.

Post-Cycle-34, consumers read predominantly from layer 1 (bytes) with annotations from layer 3. The grid (layer 2) is computed only when a consumer specifically needs it (alt-screen TUI rendering, the WPF visual surface). Reconstructing bytes from cells becomes unnecessary; the bytes are never lost.

### 4.2 What this enables

Three immediate wins:

- **Spinner storms collapse.** A 10 Hz `\b\r ⠋` sequence is a single tail-masked row in layer 1. The producer commits one chunk per `live_region_debounce_ms` (250 ms default) with the latest spinner glyph. NVDA hears one announcement; not 80.
- **Color is preserved without re-projection.** SGR escape sequences are bytes in layer 1; downstream `EarconProfile`'s color detector reads them directly. No grid re-rendering, no false positives from styled-but-not-error cells.
- **Selection prompts surface even without grid mutation.** Claude's auto-trust skip is a layer-3 annotation gap (the prompt detector's regex fires on the byte stream regardless of grid state). The producer is layer-1-faithful; the detector consumes it; the prompt is detected.

### 4.3 What this does NOT change

- **Alt-screen TUI workloads still consume from layer 2.** `vim`, `htop`, `less`, future TUI consumers — these are by definition grid-canonical workloads. The screen grid is their substrate-of-truth. The producer freezes its layer-1 stream during alt-screen and resumes on exit (drain-checkpoint-swap; §6).
- **WPF visual rendering still consumes from layer 2.** The `IDisplayBuffer` cutover in Cycle 32b is layer-2 to layer-2 (the host renderer reads cells from the grid). No change to that path.
- **Channel architecture is layer-3-driven.** Channels consume `OutputEvent`s with semantic annotations; the producer's emitted chunks become a new annotation source, but the channel surface is unchanged.

Layer 1 was always there (the parser emitted bytes; the Coalescer consumed them); the change is making layer 1 first-class for downstream consumers rather than reading them back from a lossy projection.

## 5. The Streaming-Incomplete Emission Protocol

This section is largely a verbatim lift from [`docs/research/emission-paradigms.md`](../research/emission-paradigms.md) §3.A–§3.D, with attribution to the prior-art survey and RFC-canonical framing.

### 5.1 Seam hierarchy

Lifted verbatim from [`emission-paradigms.md` §3.A lines 76–83](../research/emission-paradigms.md):

> The primary emission strategy is a **seam-hierarchy commit** evaluated on every parser tick, with the following ordered seams (strongest first):
>
> 1. **Semantic prompt seam** (OSC 133 `D` command-finished, or `C` command-output-start when transitioning out of prompt): drain everything in the buffer immediately; mark emission `final=true`. This handles `dir`, plain commands that finish promptly.
> 2. **Newline boundary** (LF or CRLF arrived since last emit): emit at the last seen newline, retain trailing partial bytes. This handles per-line streaming during `dir /s C:\Windows\System32`.
> 3. **Idle quantum** (no bytes received for *idle_quantum_ms*): emit accumulated bytes, retain nothing. This handles Claude's text-response streaming at 200–400 chars/sec (gaps between sentences and tool-call boundaries).
> 4. **Max-bytes ceiling** (buffered bytes ≥ *max_bytes_per_emit*): emit a hard chunk regardless of seam. This is the "tight loop with no newlines" safety valve, modeled on IRC's 512-byte cap and Reactor's 256-element prefetch.
> 5. **Max-time ceiling** (now − last_emit ≥ *max_time_without_emit*): emit whatever is buffered. This is the "Claude thinking, no output, no spinner suppressed bytes either" liveness guarantee; modeled on SSE's 15 s keep-alive cadence.

Each stronger seam pre-empts weaker ones. A semantic prompt seam at the same parser tick as an idle-quantum trigger drains with `final=true` and marks the chunk `Sealed=true` regardless of the idle quantum's pending state.

The substrate accumulator is **append-only** unless live-region detection (§5.3) marks the current row as overwrite-class, in which case the row's bytes are held in the tail mask, not committed to the substrate. On emission, the tail mask's *latest* state is appended (LATEST semantics from `BufferOverflowStrategy.LATEST`), and the mask is cleared.

### 5.2 Cadence parameters

Lifted verbatim from [`emission-paradigms.md` §3.C lines 108–115](../research/emission-paradigms.md). These values become Cycle 34 constants in `LinearTextStream.fs`; a future micro-cycle (post-Cycle-34) externalises them to a `[coalescer]` TOML section following the Cycle 32a `[profile.selection]` precedent.

| Parameter                | Recommended default | Source / justification                                                                                                                                                                                                                                                                                                                                       |
|--------------------------|---------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `idle_quantum_ms`        | **150 ms**          | Below the ~200 ms threshold below which sighted users perceive UI as instant; matches HuggingFace TextStreamer's "wait for whitespace, then flush" perception target. NVDA's speech queue can absorb new chunks at ~150 ms intervals without speech jitter (extrapolated from NVDA's own SAPI5 latency observations in nvaccess/nvda#19551). Reasoned default. |
| `max_bytes_per_emit`     | **4 096 bytes**     | Larger than IRC's 512-byte safety cap (which is too tight for screen-reader chunks of `dir` output) and HTTP chunked-encoding common reads of 4–8 KB. Also approximately one screen-reader announcement-buffer worth at typical TTS rates. Reasoned default.                                                                                                  |
| `max_time_without_emit`  | **2 000 ms**        | Longer than `tail -f`'s 1 s default poll, shorter than SSE's 3 000 ms reconnect, well within the 15 s SSE keep-alive cadence. Provides a liveness guarantee during silent commands without producing spurious empty chunks. Reasoned default.                                                                                                                |
| `live_region_debounce_ms`| **250 ms**          | Spinners at 10 Hz produce a frame every 100 ms; debouncing at 250 ms collapses them while preserving one announcement per "real" tail change. Modeled on RxJS `debounceTime` patterns for keystroke-rate sources. Reasoned default.                                                                                                                          |
| `prompt_seam_priority`   | **highest**         | OSC 133 `C`/`D` always wins over time/byte triggers; matches FinalTerm/iTerm2/VS Code semantic-zone priority. Spec-derived.                                                                                                                                                                                                                                  |
| `regime_switch_drain_ms` | **500 ms**          | Time the pipeline waits after an alt-screen exit before re-emitting from the Stream pathway, to allow the shell's redrawn prompt to settle. Reasoned default.                                                                                                                                                                                                |

The defaults are starting points, not measured optima. The maintainer should profile NVDA and JAWS announcement latency on representative hardware after Cycle 34 ships; `idle_quantum_ms` in particular may need to rise to 200–300 ms for slower synthesisers or eSpeak-NG with high speech rates.

### 5.3 Live-region detection

Detection is **reactive on a ranked Williams VT500 event set**, not a per-shell heuristic — heuristics break on Windows ConPTY (per VS Code documentation) and on alt-screen apps that share row 0 with prompt output. The producer subscribes to the parser's event stream and applies the following ranked classifier to each row currently being assembled (lifted verbatim from [`emission-paradigms.md` §3.B lines 94–104](../research/emission-paradigms.md)):

> 1. **`ESC[?1049h` / `ESC[?1049l`** — alternate-screen enter/exit. This is not a row-level overwrite; it is a regime switch (§5.4). The substrate is checkpointed and the pipeline switches pathway.
> 2. **`ESC[2J` (ED 2)** or **`ESC[H ESC[2J`** sequence — full-screen erase. The current substrate is *frozen* and a new substrate begins; alternatively, treat as a regime switch into TUI pathway if it occurs without an alt-screen toggle.
> 3. **Bare `\r` (CR) without immediately following `\n`** — classic carriage return for in-line overwrite. The current row enters tail-mask state. This catches progress bars (`[####    ] 47%\r`).
> 4. **`ESC[K` (Erase in Line)** — erases from cursor to end of line. Marks the current row as tail-mask. Catches `--More--` prompts and tab-completion erase-and-redraw.
> 5. **`ESC[A` (CUU, cursor up)** followed by printable bytes on a previously committed row — retroactive rewrite. Mark the *target row* as tail-mask; if the target row was already emitted, the rewrite is announced as a separate `[updated]` annotation rather than retroactively patching the substrate.
> 6. **`ESC[<n>D` (CUB, cursor back)** followed by printable text — partial-line overwrite. Same handling as #3.
> 7. **Reverse Index (`ESC M`)** when at row 0 — indicates scroll, not overwrite, but combined with tail-mask state it should not extend the substrate.

The discriminator runs in the Williams VT500 parser's "Action" hook for ground state and CSI dispatch state; it does not require a separate parsing pass. Output is a per-row enum: `Append | TailMask | Frozen`. Only `Append` rows extend the canonical substrate. Detection is **reactive** (driven by parser events), not **proactive** (no per-shell regex like "if shell == bash and prompt regex matches…"). The single heuristic exception is recognising OSC 133 `A`/`B` markers as "prompt zone" — a row inside a prompt zone is implicitly tail-masked while the user is editing, and frozen on `B`.

### 5.4 Sealed / unsealed event extension

Every chunk emitted by the producer carries a `Sealed: bool` extension on the `OutputEvent`'s extension map. This formalises the informal mention in [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) §7 and mirrors RFC 9112 §8's incomplete-message contract:

- **`Sealed=true`** — the chunk is the authoritative final state of its byte range. Consumers may commit irreversibly (NvdaChannel may announce; FileLoggerChannel may write; SessionLogWriter may persist). Triggered by: semantic prompt seam, max-time ceiling at a logical boundary, alt-screen drain-checkpoint, explicit shell-switch finalize.
- **`Sealed=false`** — the chunk is a forward progress indicator; more bytes may amend or overwrite this region. Consumers MUST tolerate without crashing; sensible defaults: announce as a polite live-region update (NvdaChannel → `LiveRegionChanged`), buffer for batched logging (FileLoggerChannel may delay until sealed), do not persist (SessionLogWriter waits for `Sealed=true`). Triggered by: idle quantum, max-bytes ceiling mid-stream, live-region tail-mask commit.

Channels that cannot meaningfully distinguish (e.g., EarconProfile's bell-ring detector) ignore the flag.

### 5.5 Closing recommendations (RFC-actionable bullets)

Lifted from [`emission-paradigms.md` §4 lines 154–165](../research/emission-paradigms.md), the actionable summary for Cycle 34 implementation:

- **Adopt a seam-hierarchy commit model** for the Stream pathway, evaluated on every Williams VT500 parser tick: `OSC 133;C/D` > newline > idle-quantum > max-bytes > max-time. Each stronger seam pre-empts weaker ones. This mirrors SSE's blank-line dispatch, IRC's CRLF rule, and HTTP chunked encoding's length-prefix authority while degrading gracefully when no shell cooperation is available.
- **Default cadence parameters** (per §5.2 above): `idle_quantum_ms = 150`, `max_bytes_per_emit = 4096`, `max_time_without_emit = 2000`, `live_region_debounce_ms = 250`, `regime_switch_drain_ms = 500`. Externalise to TOML in a future micro-cycle.
- **Detect live regions reactively from VT500 parser events**, ranked per §5.3. No per-shell regex heuristics — that path leads to VS Code's ConPTY-misalignment failure mode.
- **Use LATEST semantics on the tail mask**: spinners, progress bars, `--More--` prompts, and tab-completion overwrites collapse to one announcement per idle quantum carrying the most recent state, drawn from Reactor's `BufferOverflowStrategy.LATEST`.
- **Treat OSC 133 / OSC 633 markers as advisory, not authoritative**: when present they act as the strongest seam; when absent or out of order (as on Windows ConPTY per VS Code docs), the time/byte seams provide forward progress. Match VS Code's `IsWindows=True` posture by not assuming sequence positions are exact.
- **Emit explicit `Sealed: bool` flag** on every chunk handed to NvdaChannel / FileLoggerChannel / UIA, mirroring RFC 9112 §8's incomplete-message contract. Consumers must tolerate `Sealed=false` chunks without crashing.
- **Drain-checkpoint-swap on regime transitions** (per §6): on `ESC[?1049h` (or 47h/1047h), flush the Stream buffer with `Sealed=true`, checkpoint, freeze the linear substrate, swap to TUI pathway. On `ESC[?1049l`, drop the TUI substrate (tmux/xterm convention), resume the linear substrate after a `regime_switch_drain_ms` settle period.
- **Never flush mid-CSI**: the Williams VT500 parser owns the "complete-sequence" boundary; the producer must not split an emission across an in-progress escape sequence, mirroring the HTML5 tokenizer's "implicit-abandonment-only-at-EOF" invariant.
- **Cap the substrate accumulator** at `PerTupleCapBytes = 4_194_304` bytes (4 MB) per command zone (§3.5). Producers exceeding this limit force a forced flush + `Truncated: true` annotation — this prevents the IRC-style "overlong-line stalls the pipeline" failure.
- **Persist the Final event on disposal**: SessionLogWriter always serialises the trailing buffer when the pipeline shuts down, mirroring HuggingFace TextStreamer's `end()` flush, avoiding the HTML5-tokenizer-style silent-abandonment failure.

## 6. Drain-Checkpoint-Swap Protocol

Lifted from [`emission-paradigms.md` §3.E lines 138–148](../research/emission-paradigms.md). This three-phase flow defines the Stream ↔ TUI substrate swap and is non-negotiable per the substrate-vs-host separation in [ADR 0001](../adr/0001-substrate-channel-dichotomy.md).

### 6.1 Three-phase protocol

> 1. On `ESC[?1049h` (or `47h`, `1047h` legacy), the Stream-pathway Coalescer flushes its pending buffer with `Sealed=true`, emits a "stream-suspended" checkpoint to NvdaChannel / FileLoggerChannel / UIA, and freezes the linear substrate.
> 2. The TUI-pathway substrate (the screen-cell grid) becomes canonical.
> 3. On `ESC[?1049l`, the TUI substrate is discarded (per tmux/xterm convention); the linear substrate resumes from its frozen state with a `regime_switch_drain_ms` settle period before any new bytes are committed.

The streaming-systems analogues (Kafka consumer-commit, Flink barrier checkpoints) confirm the principle: a checkpoint is a moment at which the in-flight state is durable and the substrate-identity changes. For pty-speak specifically:

- The in-flight buffer at the transition point is **drained** (Stream → TUI) — bytes already accumulated belong to the linear substrate and should be announced before the regime change. This matches `tail -f`'s drain-on-EOF behaviour.
- TUI → Stream does **not** drain anything inbound from the alt-screen — that content was canonically the screen, not the linear stream. The screen substrate is dropped (matching tmux/xterm), and the linear substrate continues from where it was frozen. This is the "discard scrollback on alt-screen exit" pattern.
- A failed transition (alt-screen entered without a clean exit, e.g., `vim` killed by signal) is recoverable: the next `\r\n` plus prompt emission in the Stream pathway re-establishes a seam, and the frozen prefix is re-announced with an `[interrupted]` marker.

### 6.2 Spinner case (NOT alt-screen)

Claude's thinking spinner does NOT enter alt-screen. It operates in the Stream pathway via `\r` + cursor-back overwrites of a single row. The drain-checkpoint-swap protocol therefore does not apply to spinners; live-region tail-mask handling (§5.3 #3 + #6) absorbs spinners within the Stream pathway. This is verified by the maintainer-reconfirm gate item (d) in Cycle 33's stopping criteria.

### 6.3 The `more` paginator case (also NOT alt-screen)

`dir /s | more` and similar paginators write the `--More--` prompt with `\r` + `ESC[K` (no alt-screen). The tail mask absorbs the prompt; on space-bar advance, the next chunk of bytes flushes via newline boundary. No drain-checkpoint-swap involved.

### 6.4 Failure mode: alt-screen without clean exit

A TUI killed by signal (`vim` → `kill -9`) leaves the terminal in alt-screen state. The next byte stream from the parent shell may include alt-screen-exit (`ESC[?1049l`) followed by a fresh prompt, OR it may not (depending on shell signal handling). The producer's resume logic must:

- On observing `ESC[?1049l`: clean exit; resume linear stream after `regime_switch_drain_ms` settle.
- On observing prompt detector fire WITHOUT a preceding `ESC[?1049l`: treat as "interrupted alt-screen exit"; emit a `[interrupted]` annotation on the next chunk; resume linear stream immediately.
- On observing further bytes that DON'T match either pattern: continue freezing the linear substrate; the frozen state is recoverable when a future seam arrives.

## 7. SessionTuple Finalize Contract

The SessionTuple is the primary downstream consumer of the producer's high-water-mark commits. This section specifies how `LinearTextStream.finalizeHighWaterMark` replaces the runtime call to `SessionModel.fs:338-375` `extractContent` while preserving the SessionTuple shape and the on-disk JSONL schema.

### 7.1 Current finalize path

`SessionModel.applyAndCapture` (`SessionModel.fs:498-690`) is the state machine entrypoint. On `(Some active, PromptStart)` or `(Some active, CommandFinished exitCode)`, it calls `finalizeAndEnqueue` (`SessionModel.fs:406-436`), which:

1. Calls `extractContent` to produce `(commandText, outputText)` from the snapshot.
2. Captures `now: DateTime` for `CommandFinishedAt`.
3. Constructs the SessionTuple record.
4. Returns `(state', SessionTuple option)` for the composition root to dispatch to `SessionLogWriterSink`.

### 7.2 Post-Cycle-34 finalize path

Replace step 1 with `LinearTextStream.finalizeHighWaterMark`. The function returns a `FinalizedChunk`:

```fsharp
type FinalizedChunk =
    { CommandText: string         // bytes between PromptStart and CommandStart
      OutputText: string          // bytes between CommandStart and CommandFinished
      Truncated: bool             // 4 MB cap was hit
      Sealed: bool                // always true at finalize time
      ProducerWaterMark: int64 }  // for diagnostic correlation
```

The producer maintains internal markers for prompt-start and command-start positions in its committed buffer. On `finalizeHighWaterMark`, it slices the buffer between those markers, returns the slice as `CommandText` / `OutputText`, and advances its internal "last-finalized" watermark.

Steps 2–4 of the existing finalize path are unchanged. `extractContent` remains in `SessionModel.fs` as **runtime-unwired but byte-preserved** for any future session-restore-from-disk implementation that re-uses its row-walking logic against a serialized snapshot.

### 7.3 SessionTuple record shape — unchanged

The SessionTuple record (`SessionModel.fs:84-133`) is unchanged. Field-by-field:

- `Id: Guid` — unchanged.
- `CommandId: string option` — unchanged (OSC 133 `aid=` correlation).
- `ShellId: string` — unchanged.
- `PromptStartedAt: DateTime` — unchanged.
- `CommandStartedAt: DateTime option` — unchanged.
- `OutputStartedAt: DateTime option` — unchanged.
- `CommandFinishedAt: DateTime option` — unchanged.
- `PromptText: string` — unchanged (captured at PromptStart → CommandStart transition; from `boundary.MatchedRowText`).
- `CommandText: string` — **sourced from `FinalizedChunk.CommandText`** instead of `extractContent`'s row-walk.
- `OutputText: string` — **sourced from `FinalizedChunk.OutputText`** instead of `extractContent`'s row-walk.
- `ExitCode: int option` — unchanged.
- `Sources: Map<BoundaryKind, BoundarySource>` — unchanged.
- `ExtraParams: Map<string, string>` — **gain a new key `pty-speak.linear-stream.truncated = "true"`** when `FinalizedChunk.Truncated = true`. Consumers that announce can prefix `[output truncated]`.

### 7.4 On-disk JSONL schema — unchanged

`SessionLogWriter`'s JSONL format (`SessionModel.fs:867-906`) is byte-stable:

- `schemaVersion: 1` on every record.
- Record shape exactly mirrors the in-memory SessionTuple.
- The new `pty-speak.linear-stream.truncated` key in `ExtraParams` is forward-compatible (existing consumers ignore unknown keys per the `ExtraParams: Map<string, string>` contract).

The `MaxSessionSizeMb` cap (default 64 MB per session file at `SessionPersistence.fs:29`) is unchanged. The 4 MB per-tuple cap is a **separate constraint** that operates earlier in the pipeline.

### 7.5 Shell-switch + Ctrl-C semantics

Two finalize-path corner cases preserved verbatim:

- **Shell-switch** (`Ctrl+Shift+1/2/3`) calls `SessionModel.finalizeIncomplete`, which finalises the active tuple with `CommandFinishedAt = now`, `ExitCode = None`, empty `CommandText` / `OutputText`. The producer must NOT retroactively finalize partial output after shell-switch; the tuple is sealed at that instant. The producer's `checkpointAndFreeze` is called before shell-switch finalize; subsequent bytes start a new linear stream for the new shell.
- **Ctrl-C at prompt** mid-output → the next `PromptStart` arrives; `applyAndCapture` calls `finalizeAndEnqueue` with the interrupt path. The producer's `finalizeHighWaterMark` returns whatever bytes accumulated since `CommandStartedAt`; this is now the truncated `OutputText`. Consumers see a "command interrupted" tuple with whatever output the user-visible part had.

### 7.6 Alt-screen interaction

`SessionModel.applyAndCapture` has an `IsAltScreenActive` guard (`SessionModel.fs:527`) that suppresses tuple state-machine transitions during alt-screen. The producer respects this: `finalizeHighWaterMark` is only called when alt-screen is NOT active; the producer's `checkpointAndFreeze` runs on alt-screen entry. If alt-screen is active during a SessionModel state-machine event (which the existing guard prevents), the producer's behavior is undefined — but the existing guard makes this a non-issue.

## 8. The Three Exemplar Canonical Displays

This section names the three exemplar canonical displays at high level. Full per-primitive specs (UIA control types, ARIA role analogs, NVDA reading patterns, JAWS virtual cursor behavior, Narrator behavior, interaction contracts, substrate consumption, update cadence, output channel routing) live in [`docs/CANONICAL-DISPLAY-CATALOG.md`](../CANONICAL-DISPLAY-CATALOG.md).

The framing follows [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) §5: three exemplars cover the dominant interaction archetypes; a named-but-not-specified extension-points list captures the future workload taxonomy without locking it.

### 8.1 Exemplar 1 — Raw Text

The append-mostly canonical display for assistant prose, command output, long-running tool stdout. Single highest-traffic primitive in the Claude Code workload.

- **Substrate:** Linear-text producer (this RFC's core deliverable).
- **UIA control type:** `ControlType.Document` + `ITextProvider` + `ITextProvider2` exposing `DocumentRange` and `RangeFromPoint`; `ITextRangeProvider` supporting `Move` / `MoveEndpointByUnit` for `TextUnit.Character/Word/Line/Paragraph/Document`.
- **ARIA role analog:** `role="log"` with `aria-live="polite"`, `aria-atomic="false"`, `aria-relevant="additions text"`.
- **NVDA reading pattern:** Browse mode, arrow keys read by line; `NVDA+UpArrow` / `NVDA+DownArrow` for say-all from caret; quick-nav `h` / `Shift+h` for semantic-block boundaries.
- **Update cadence:** Live polite. Coalesce appends with the producer's `live_region_debounce_ms` (250 ms) keyed on word-boundary detection. Per-token announcement is forbidden.
- **Channel routing:** NVDA UIA (native), self-voicing TTS (native), earcon (block-boundary semantics), refreshable braille (native via UIA), spatial audio (optional, block-azimuth), FileLogger (native), WPF visual (native).
- **CommandOutputTuple wrapper:** Per the post-Cycle-31a doc tweak ([PR #236](https://github.com/KyleKeane/pty-speak/pull/236)), the history sub-pane consumes raw-text exemplars wrapped as CommandOutputTuple primitives — command + output + exit-code as a single semantically-navigable region. Quick-nav: `h` / `Shift+h` (command boundaries), `o` / `Shift+o` (output blocks), `Alt+Up` / `Alt+Down` (tuple boundaries).

Full spec: [CANONICAL-DISPLAY-CATALOG.md §1 Exemplar 1](../CANONICAL-DISPLAY-CATALOG.md).

### 8.2 Exemplar 2 — Interactive List

Vertical or horizontal selection menu. Distinguished from raw text by **selection semantics** — the user's keystroke commits a choice rather than appending to a stream.

- **Substrate:** Derived semantic-event store. The `SelectionDetector` (Cycle 29a) consumes the linear-text producer's bytes (post-Cycle 35 inversion) and emits `SelectionShown` / `SelectionItem` / `SelectionDismissed` semantic events; the `SelectionProfile` (Cycle 29b) translates them.
- **UIA control type:** `ControlType.List` + `ISelectionProvider` (single-select default; multi-select for `fzf -m`); each item `ControlType.ListItem` + `ISelectionItemProvider`.
- **ARIA role analog:** `role="listbox"` with `aria-orientation="vertical"` (or `horizontal` per layout), `aria-required="true"`, single-select via `aria-selected`.
- **NVDA reading pattern:** Focus mode auto-entered; arrow keys move selection; first-letter type-ahead jumps by `Name`; `Insert+F7` Elements List shows options under "Form fields" / "Lists".
- **Update cadence:** Snapshot-on-render; one `NotificationKind.Other` + `NotificationProcessing.ImportantAll` event on appearance; selection-follows-focus fires `ElementSelectedEvent` per arrow press.
- **Channel routing:** NVDA UIA (native), self-voicing TTS (native), earcon (boundary tick + decision earcon mandatory for prompt-class), refreshable braille (native via UIA), FileLogger (native), WPF visual (native).
- **ConfirmationPrompt hybrid:** When the interactive list carries assertive-notification semantics (Claude tool-use confirmation, `apt`'s `Continue? [Y/n]`), the catalog notes the hybrid alert+selection pattern as a related primitive that combines this exemplar's selection contract with `role="alertdialog"` modality. Full implementation lands in a future cycle when both halves are stable.

Full spec: [CANONICAL-DISPLAY-CATALOG.md §2 Exemplar 2](../CANONICAL-DISPLAY-CATALOG.md).

### 8.3 Exemplar 3 — Form with Text Input

Field-with-label primitive for shell prompts that require typed input (`Read-Host`, password prompts, `set /p`, future Claude tool-use approve-with-comment forms).

- **Substrate:** Linear-text producer + future `InputPathway` (Cycle 38). The form detector (`FormProfile`, Cycle 38a) consumes linear-text bytes and emits `FormPrompt` semantic events; the UIA peer (Cycle 38b) translates.
- **UIA control type:** `ControlType.Group` containing one or more `ControlType.Edit` children with `IValueProvider` (read-only after submission, `IsReadOnly=true`).
- **ARIA role analog:** `role="form"` with descendant `role="textbox"` fields and `aria-label`.
- **NVDA reading pattern:** Focus mode auto-entered (forms-mode); Tab between fields; Enter submits.
- **Update cadence:** Live polite for typed-echo (Stage 6 typed-echo coalescer reused); snapshot at submit.
- **Channel routing:** NVDA UIA (native), self-voicing TTS (native, with redaction for password fields), earcon (field tick), refreshable braille (native via UIA), FileLogger (native, redacted for password fields), WPF visual (native).
- **Implementation timing:** Cycle 38 (input framework cycle). The exemplar is named here for completeness of the three-exemplar set; the spec lifted from [CORE-ABSTRACTION-BOUNDARY.md §5 Exemplar 3](../CORE-ABSTRACTION-BOUNDARY.md) into the catalog, not implemented in Cycle 34.

Full spec: [CANONICAL-DISPLAY-CATALOG.md §3 Exemplar 3](../CANONICAL-DISPLAY-CATALOG.md).

### 8.4 Extension points (named, not specified)

The catalog names these as future canonical displays without locking the spec:

- **SeverityAlert** — single-shot interruptive announcement of error/warning/fatal. `role="alert"`, assertive interrupt. Detector consumes ANSI-red + lexical patterns (`error:` / `panic` / `warning`).
- **IndeterminateProgress** — spinner-class ongoing activity. `role="status"` + `aria-busy="true"` + earcon-driven start/end. **No `IRangeValueProvider`, no per-frame UIA events.** This exemplar's current Cycle 29b regressions (~80 announcements per Claude turn) motivate getting this right out of the gate when the future cycle ships.
- **CommandOutputTuple** — described as a wrapper for raw-text exemplar in §8.1; named here as a discrete extension point because its UIA contract (`ControlType.Group` wrapping `Edit` + `Document` + `Text` for exit code) is distinct from raw-text's contract.
- **Tier-2 (deferred)**: DiffView, CodeBlockWithSyntaxStructure, DeterminateProgress, FormInputGroup (specific form variants beyond the generic Exemplar 3), TabularDataDisplay.
- **Tier-3 (research / defer)**: HierarchicalTreeDisclosure (`npm ls`, `git log --graph`), SpatialAudioStatusField (HRTF-spatialized ambient tones), MultiRegionFocusManager (composite multi-region focus arbitration).

The plan deliberately ships only three exemplars to avoid premature taxonomy lock-in. The extension points are named so future cycles have a coherent vocabulary; their full specs land when concrete workloads demand them.

## 9. Risks + Mitigations

This section extends the [Strategic plan](../PROJECT-PLAN-2026-05-09.md) §4 risk register with RFC-specific concerns.

| # | Risk                                                                                              | Detection                                                                                  | Mitigation                                                                                                                                                                              |
|---|---------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| A | Tail-mask false positives on benign repaints (e.g., shell prompt redraws during typed input).     | Cycle 6 typed-echo regression tests; Stage 6 unit-test suite.                              | The detector ranks (§5.3) only mark the row tail-masked when overwrite-class bytes are observed. Stage 6 suppression in the Coalescer's input pipeline still runs.                      |
| B | 4 MB per-tuple cap collides with legitimate large outputs (`dir /s` on 200K-file tree → ~16 MB).  | Cycle 36 advanced-CMD content matrix row 2.                                                | Drop-oldest semantics + `Truncated=true` annotation; consumer-side `[output truncated]` prefix. If the median legitimate workload regularly exceeds 4 MB, raise the cap in a follow-up. |
| C | Sealed/unsealed event semantics confuse channels that need atomic announce.                       | Channel-test review in Cycle 35.                                                           | NvdaChannel implementation: `Sealed=false` → `LiveRegionChanged` (polite); `Sealed=true` → assertive announce per existing semantic mapping. FileLoggerChannel: log both, mark sealed.   |
| D | Producer's tail-mask LATEST semantics drop intermediate progress info that some users want.       | Maintainer NVDA validation post-Cycle 35.                                                  | The cap configurable; user can lower `live_region_debounce_ms` to 50 ms to hear intermediate states. Future cycle could expose per-shell tuning.                                        |
| E | Drain-checkpoint-swap settle period (`regime_switch_drain_ms = 500 ms`) feels laggy.              | Maintainer NVDA validation post-Cycle 35; maintainer can tune via TOML.                    | Future micro-cycle externalises to `[coalescer]` TOML; documented in USER-SETTINGS.md as a tunable.                                                                                     |
| F | Heuristic prompt detector (`HeuristicPromptDetector.fs:202-335`) misses on a new shell.           | Stage 7-issues `[heuristic-prompt-detector]` tracking; manual report from maintainer.      | Non-RFC concern; pre-existing detector gap. The producer's `finalizeHighWaterMark` only fires when the detector fires — if the detector never fires, the tuple never finalizes.        |
| G | `extractContent` is preserved verbatim but never executed at runtime — accidentally deleted.      | Cycle-N PR review; CI lint?                                                                | Add a comment in `SessionModel.fs:338` reading `// Cycle 34: preserved for any future session-restore-from-disk implementation. NOT runtime-wired post-Cycle 34.`                       |
| H | Research doc earcon-frequency conflict (≥125 Hz vs CONTRIBUTING.md's <180 Hz floor).              | Reviewer comparing this RFC to CONTRIBUTING.md.                                            | RFC §11 glossary explicitly defers to CONTRIBUTING.md; the research's wider bound is flagged as a future tuning experiment (not a current production constraint).                       |
| I | Cycle 33 RFC text drifts as Cycle 34 implementation discovers contract gaps.                      | RFC review on every Cycle 34+ PR.                                                          | RFC versioning: snapshot at adoption time; explicit "amended in Cycle N" notes inline; major changes require ADR-style maintainer authorization.                                        |
| J | The two research docs (sources cited extensively) get edited or removed.                          | Markdown link checker on every PR; RFC's citations are anchored to file paths.             | The research docs are now in-repo as authoritative references; treat them as immutable like `spec/tech-plan.md` (per the spec-immutability discipline in CLAUDE.md).                     |
| K | `IsAltScreenActive` guard at `SessionModel.fs:527` is bypassed by a future code change.           | Cycle-N test; Cycle 35+ inversion will exercise this path.                                 | Cycle 34 adds an explicit `LinearTextStreamTests.fs` fact: "alt-screen active → finalizeHighWaterMark MUST throw or return None"; pin the contract.                                     |

## 10. Acceptance Criteria for Cycle 34

Cycle 34 implementation must satisfy these contracts to honor this RFC. Each is a concrete, testable invariant:

### 10.1 Producer module exists

- `src/Terminal.Core/LinearTextStream.fs` exists with the public surface specified in §3.1 (or a Cycle 34-justified variant).
- `Terminal.Core.fsproj` lists it in compile order before `SessionModel.fs` (so SessionModel can reference `LinearTextStream.FinalizedChunk`).
- The portability CI lint (Cycle 31a) continues to pass — no host-specific imports introduced.

### 10.2 Seam hierarchy implemented

- §5.1's five-tier seam hierarchy is implemented as `LinearTextStream.append`'s commit logic.
- Order is enforced: a parser tick that satisfies multiple seams emits ONCE, with the strongest seam's `Sealed` flag prevailing.
- Test fixtures in `tests/Tests.Unit/LinearTextStreamTests.fs` exercise each seam in isolation + each pair-overlap case.

### 10.3 Live-region detection ranked

- §5.3's seven-rank classifier is implemented and runs from VT500 parser events (not a separate parsing pass).
- Test fixtures cover each rank (alt-screen toggle, full-erase, bare CR, EL, CUU+printable, CUB+printable, RI at row 0).
- Spinner workload (10 Hz `\b\r` cycle) collapses to ≤ 4 emissions per second (`live_region_debounce_ms = 250 ms` → 4 Hz max).

### 10.4 Drain-checkpoint-swap

- §6.1's three-phase protocol is implemented at `LinearTextStream.checkpointAndFreeze` + `resumeFromFreeze`.
- Test fixtures cover: clean alt-screen entry+exit; alt-screen entry without exit; alt-screen exit without preceding entry (defensive).

### 10.5 SessionTuple finalize

- `SessionModel.applyAndCapture` calls `LinearTextStream.finalizeHighWaterMark` instead of `extractContent` on every PromptStart and CommandFinished arm.
- All existing SessionModelTests stay green (the SessionTuple shape is unchanged; only the source of `CommandText` / `OutputText` shifts).
- `extractContent` remains in `SessionModel.fs:338-375` as runtime-unwired but byte-preserved (with the comment from Risk G mitigation).

### 10.6 4 MB per-tuple cap

- Pathological input (synthetic 10 MB byte stream between two prompts) produces a SessionTuple with `Truncated: true` in `ExtraParams` and `OutputText.Length ≤ 4 MB`.
- Drop-oldest semantics: the SessionTuple's `OutputText` contains the most-recent ≤4 MB, not the first ≤4 MB.

### 10.7 Sealed flag round-trip

- Every `OutputEvent` emerging from the producer has the `Sealed: bool` extension set.
- NvdaChannel + FileLoggerChannel + EarconChannel handle `Sealed=true` and `Sealed=false` correctly per §5.4.
- `IOutputSinkTests` (Cycle 31a) gain a fact: "Sealed=false event does NOT trigger NvdaChannel announce; Sealed=true does".

### 10.8 Diagnostic bundle integration

- `Ctrl+Shift+D` adds `--- LINEAR STREAM (last 64KB) ---` section between `--- SESSION LOG ---` and `--- ENVIRONMENT ---` in the bundle.
- Sibling file: `linear-stream-<yyyy-MM-dd-HH-mm-ss-fff>.txt` in `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\`.
- 64KB inline cap respected; full stream on disk; Cycle 29b iOS-paste-crash incident does not recur.

### 10.9 NVDA regression check

- All Cycle 29b NVDA fixtures pass with identical wording (regression check; the substrate inversion must not regress NVDA reading).
- All Cycle 31a / 32a fixtures pass.

### 10.10 Test count

- `tests/Tests.Unit/LinearTextStreamTests.fs` ships ~25 facts covering: append, backspace, `\r`, alt-screen freeze, prompt finalize, live-region commit, 4 MB cap, sealed/unsealed, drain-checkpoint-swap.

## 11. Glossary (vocabulary adopted in this RFC)

These terms are project-canonical post-RFC adoption. Future docs and CHANGELOG entries should use these terms verbatim; older terms persist in pre-RFC text but are no longer preferred for new prose.

| Term                          | Definition                                                                                                                                                                                                                                              | Replaces (in pre-RFC docs)                                |
|-------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------|
| **Tail mask**                 | Per-row state marking content as overwrite-in-place (not appendable to the substrate). Spinners, progress bars, `--More--` paginators live here. LATEST semantics: most recent state wins; intermediates dropped. Source: emission-paradigms.md §3.A.   | "live region pointer" (CORE-ABSTRACTION-BOUNDARY.md §7) |
| **Drain-checkpoint-swap**     | Three-phase Stream ↔ TUI substrate transition: (1) flush pending Stream buffer with `Sealed=true`; (2) freeze linear substrate, swap to TUI; (3) on exit, drop TUI substrate, resume linear after `regime_switch_drain_ms` settle. Source: emission-paradigms.md §3.E. | "alt-screen freeze" (CORE-ABSTRACTION-BOUNDARY.md §7)    |
| **Seam hierarchy**            | Five-priority emission trigger ordering: semantic-prompt > newline > idle > max-bytes > max-time. Stronger seams pre-empt weaker. Source: emission-paradigms.md §3.A.                                                                                  | "idle quanta as commit points" (CORE-ABSTRACTION-BOUNDARY.md §7) |
| **Substrate-of-truth**        | The canonical source from which all derived projections are computed. Linear text is the substrate-of-truth for linear workloads; the screen grid is the substrate-of-truth for alt-screen TUI workloads. Source: Output-paradigms.md §5.              | informal use throughout pre-RFC docs                     |
| **Literal-language convention** | The discipline of using *select / mark / announce / present / read / focused / current* for accessibility-bearing prose; eliminating sight metaphors *highlight / view / show*. Source: Output-paradigms.md Front Matter.                              | new — adopt in CONTRIBUTING.md doc-style guidance         |
| **Sealed / Unsealed events**  | Mid-stream events carry a `Sealed: bool` extension. Unsealed events are advisory (profiles may show provisional state but must not commit irreversibly); sealed events are authoritative. Mirrors RFC 9112 §8 incomplete-message contract.             | formalises CORE-ABSTRACTION-BOUNDARY.md §7's `Sealed: bool` mention |
| **High-water-mark commit**    | The producer's act of finalising a slice of the committed buffer at a seam crossing. Replaces `extractContent`'s row-walk. Source: this RFC §3.                                                                                                        | new                                                      |
| **Producer / consumer**       | The producer is `LinearTextStream` (substrate-side, append-only). Consumers are downstream stages (Coalescer, profiles, channels) that read from the producer's emitted chunks. Source: this RFC §3.                                                  | informal use                                             |

### Earcon frequency clarification

[`docs/research/Output-paradigms.md`](../research/Output-paradigms.md) §1.1 cites Brewster guidelines (≥125 Hz, ≤5 kHz, multi-harmonic timbre, rhythmic motif rather than pitch alone). [`CONTRIBUTING.md`](../../CONTRIBUTING.md) line 200 specifies the tighter empirical constraint: "Earcons stay out of the speech band. Frequencies must be either below 180 Hz or above 1.5 kHz."

This RFC defers to CONTRIBUTING.md as the production constraint. The research's wider lower bound (125 Hz) is a future tuning experiment, not a current standard. The Cycle 11+ NVDA-validation rounds that pinned 180 Hz / 1.5 kHz remain authoritative until a future cycle re-validates with the wider range.

## 12. Cross-references

- [`docs/CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md) — architectural framing; this RFC is the first concrete substrate-side spec implementing §1's four-part assertion.
- [`docs/CANONICAL-DISPLAY-CATALOG.md`](../CANONICAL-DISPLAY-CATALOG.md) — full per-primitive specs for the three exemplars + extension points named in §8.
- [`docs/adr/0001-substrate-channel-dichotomy.md`](../adr/0001-substrate-channel-dichotomy.md) — the architectural ADR; this RFC is the first downstream implementation cycle's design doc.
- [`docs/PROJECT-PLAN-2026-05-09.md`](../PROJECT-PLAN-2026-05-09.md) — strategic plan; Cycle 33 is the pivot gate before Cycle 34 implementation.
- [`docs/research/emission-paradigms.md`](../research/emission-paradigms.md) — primary source for §5 emission protocol + §6 drain-checkpoint-swap.
- [`docs/research/Output-paradigms.md`](../research/Output-paradigms.md) — primary source for §8 three exemplars (high-level) + the catalog (full per-primitive spec).
- [`docs/STAGE-7-ISSUES.md`](../STAGE-7-ISSUES.md) — Stage 7 findings that motivated the substrate inversion; closed scope (Cycle 32a `[output-selection]`) and open scope (Stage 8e-B UIA listbox peer).
- [`docs/PANE-MODEL.md`](../PANE-MODEL.md) — three-sub-pane interaction paradigm; the linear-text producer's high-water-mark commits feed the history sub-pane's CommandOutputTuple navigation primitive.
- [`SessionModel.fs:338-375`](../../src/Terminal.Core/SessionModel.fs) — `extractContent` (the path being replaced).
- [`SessionModel.fs:498-690`](../../src/Terminal.Core/SessionModel.fs) — `applyAndCapture` (the consumer that calls `extractContent` today; will call `LinearTextStream.finalizeHighWaterMark` post-Cycle-34).
- [`Coalescer.fs`](../../src/Terminal.Core/Coalescer.fs) — restructured by PR-N 2026-05-04; producer attaches at the input edge.
- [`HeuristicPromptDetector.fs:202-335`](../../src/Terminal.Core/HeuristicPromptDetector.fs) — prompt-boundary fire that triggers `finalizeHighWaterMark`.
- [`Diagnostics.fs:860-915`](../../src/Terminal.App/Diagnostics.fs) — `formatDiagnosticBundle`; Cycle 34 inserts `--- LINEAR STREAM (last 64KB) ---` between SESSION LOG and ENVIRONMENT.

## Versioning + maintenance

Snapshot model per the established research-stage discipline. Top-of-doc front matter carries `Snapshot: YYYY-MM-DD`. Trigger conditions for re-snapshot:

- Cycle 34 ships `LinearTextStream.fs` — RFC's "Acceptance Criteria" section gets a "shipped 2026-MM-DD" notation per acceptance bullet.
- Cycle 35 inverts the Stream profile against the producer — RFC §3.3 hookpoint description updates to reflect the inverted call path.
- Cycle 36 retires the screen-grid path for non-alt-screen workloads — RFC §3.6 gets a "session-restore-from-disk: still out of scope as of Cycle 36" note.
- A maintainer-authored amendment (per spec-immutability discipline) updates a load-bearing constraint — a dated "Amended in Cycle N" note is appended to the affected section.

