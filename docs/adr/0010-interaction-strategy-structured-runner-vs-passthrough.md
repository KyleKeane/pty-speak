# ADR 0010 — Interaction strategy: structured command-runner vs raw-terminal passthrough

- **Status**: **Proposed** (2026-05-18; the maintainer requested
  this decision record *before* any further implementation —
  "Strategy ADR first". No code changes accompany it. The
  maintainer ratifies one of the options below; until then the
  status-quo path (Option B) is **paused** so the decision is
  not pre-empted.)
- **Date**: 2026-05-18
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 52, session-closure reconciliation.
  After the boundary-diagnostic-capture track characterised the
  cell-seal defect from real byte data
  ([`docs/boundary-capture/README.md`](../boundary-capture/README.md)),
  the maintainer raised a first-principles ROI doubt: does
  "connect directly to real shells + key pass-through +
  semantic segmentation of output" earn its keep versus a
  simpler structured interface — given the goal is *a clean
  programming interface on Windows for blind developers*, not a
  perfect terminal emulator.
- **Companion docs**:
  [ADR 0005](0005-osc133-shell-integration.md) (OSC-133
  mechanism), [ADR 0006](0006-three-layer-refoundation.md) (the
  transport/core/channel seam this reframing reuses),
  [ADR 0004](0004-iocell-model-for-shell-interaction.md) (the
  IOCell unit both modes produce),
  [ADR 0007](0007-canonical-iocell-history-navigation.md) (the
  navigable history both modes feed),
  [ADR 0009](0009-canonical-cell-metadata-and-typed-outcome.md)
  (the `CellOutcome` a structured runner can fill *for real*),
  [`docs/boundary-capture/README.md`](../boundary-capture/README.md)
  (the data this decision is grounded in).

## Context

51+ cycles have iterated on one architecture: spawn a real
shell (cmd / PowerShell / claude) over ConPTY, pass keystrokes
through, and **reconstruct command/output boundaries by
semantically segmenting the byte stream** for screen-reader
announcement + a navigable IOCell history. Boundary detection
has been the recurring brittle core (heuristic scraping →
OSC-133 injection → three-layer refoundation → IOCell →
SpeechCursor history → the boundary-diagnostic-capture track).

The boundary-diagnostic-capture track (#417–#429, **complete,
on main**) now lets us reason from **data, not inference**. Its
cross-scenario synthesis
([`docs/boundary-capture/README.md`](../boundary-capture/README.md))
is decisive:

- **Non-interactive command → output**: the OSC-133 markers
  needed to fence a cell **are present and well-formed** in
  the byte stream (C1 proves a clean `… ;D ;A <prompt> ;B`
  tail). The remaining defect is **one bounded, specified,
  replay-oracle-guarded extractor fix** (seal on the top-level
  `;D`; fence the trailing next-prompt out; tolerate mixed
  OSC terminators). This is *not* an unbounded problem.
- **Interactive sub-prompts** (`set /p`, `choice`, `pause`,
  in-script reads, TUIs, REPLs): shell integration **by
  construction cannot mark them** — OSC-133 only wraps the
  top-level command loop (C3 documented this: `set /p`
  carries zero OSC-133 markers, the cell drops on `None`,
  *no cell seals*). These are *permanently* heuristic, with
  an unbounded long tail. The yes/no `choice` regression
  chased this whole session (#438) is exactly this class.

So the difficulty is **not uniform**: bounded for the ~80%
non-interactive coding path, intrinsically unbounded for the
interactive tail. The strategic question is whether the
*product's primary surface* should depend on segmenting a raw
terminal at all.

## The ROI lens

The stated goal is **a clean, reliable programming interface on
Windows for blind developers** — to make the maintainer "a
happy functional coder." Most of a coding workflow (build,
test, lint, git, run a script, file ops) is *non-interactive*:
issue a command, it runs to completion, read the output. That
shape does **not require a terminal emulator** — it requires
exact command/output boundaries and clean accessible I/O. A
raw PTY is needed only for the genuinely-interactive minority
(TUIs, REPLs, interactive prompts, SSH) — precisely where
segmentation is unbounded.

## Decision (proposed) — options

**Option A — Two-mode reframe (recommended).** Make a
**structured command-runner** the *primary* surface: the user
composes a command in a clean input; the runner invokes it
(`CreateProcess` / PowerShell API / `cmd /c`) and reads
stdout/stderr/exit-code **directly**; output returns as **one
IOCell with exact boundaries, for free** — the runner
*controls* invocation, so it knows precisely when the command
started and exited (no scraping, no OSC-133 for the common
case; the exit code is *real*, not `Indeterminate`). The
existing raw-PTY passthrough becomes an explicit,
user-selected *secondary "interactive terminal" mode* for when
a live PTY is genuinely required, where imperfect segmentation
is an **accepted, scoped** tradeoff. Both modes emit the same
`IOCell` / `CellEventBus` / navigable history / channel stack
(ADR 0004 / 0007 reused unchanged).

**Option B — Stay the course.** No reframe. Implement the
already-specified one boundary/extraction fix + the independent
C2 channel fix, oracle-guarded; continue treating the raw
terminal as the single primary surface and absorb the
interactive long tail as ongoing heuristic work.

**Option C — Hybrid auto-detect.** One surface that
*auto-switches*: run non-interactive commands through the
structured path, transparently fall back to PTY when
interactivity is detected. Best UX in principle, but the
detection ("will this command be interactive?") is itself an
unbounded prediction problem — risks recreating the
segmentation difficulty one level up.

### Recommendation

**Option A.** It is the only option that removes the unbounded
problem from the *critical path* while **preserving the entire
validated investment**: the three-layer boundary (ADR 0006),
IOCell (ADR 0004), CellEventBus + navigable history (ADR 0007),
the channel stack, and the replay-oracle all carry over — the
structured runner is a *new transport adapter behind the
existing `ShellAdapter` seam*, not a rewrite. It delivers the
stated goal **now** (exact boundaries, real exit codes, zero
scraping for the 80% path) instead of gating "can I be a happy
functional coder" on perfecting raw-terminal segmentation.
Option B keeps the product's primary value permanently coupled
to the unbounded tail. Option C is A's benefit minus A's main
de-risking property (it keeps an unbounded predictor on the
hot path).

This ADR records the analysis; **the maintainer ratifies the
option.** Status stays Proposed until then.

## Consequences

If **A** is ratified (walking-skeleton; each its own PR +
dogfood; none discards shipped work):

- **A1** — `StructuredRunnerAdapter` behind the existing
  `ShellAdapter` / `SessionHost` seam (ADR 0006): invoke +
  capture stdout/stderr/exit; emit one sealed `IOCell` with
  exact `CommandStartedAt` / `CommandFinishedAt` / a **real**
  `ExitCode` (turns ADR 0009's `Indeterminate`-for-cmd into a
  genuine `Succeeded`/`Failed` outcome on this path). No
  OSC-133, no `HeuristicPromptDetector` on this path.
- **A2** — a clean command-input surface as the default; the
  IOCell history (ADR 0007) is already the right output model
  — it simply receives *exact* cells.
- **A3** — an explicit mode switch to the existing raw-PTY
  passthrough (relabelled "Interactive terminal"); its known
  segmentation imperfections become *documented, scoped*
  limitations of an opt-in mode, not product-critical bugs.
- **A4** — in-flight status-quo items are **reprioritised, not
  deleted**: the specified boundary/extraction fix and the C2
  channel fix still apply to the secondary PTY mode; the
  replay-oracle still guards it; #437 / #438 become
  secondary-mode issues.
- No capability is lost — REPLs / TUIs / SSH remain fully
  available via the secondary mode; what changes is which
  surface is *primary / default*.

If **B**: implement the specified boundary fix + C2 fix; the
interactive tail (#438-class) remains a primary-path risk.

If **C**: A1 + an interactivity predictor accepted as a new
heuristic surface.

## Alternatives considered

- **Full rip-up / restart on a non-terminal architecture.**
  Rejected: discards the validated three-layer / IOCell /
  history / oracle investment; A reuses all of it via the
  adapter seam.
- **Drop interactive support entirely.** Rejected:
  REPLs / TUIs / SSH are real coding needs; A *keeps* them
  (secondary mode), it just stops letting them gate the
  primary experience.
- **Keep segmenting but add exit-code/structure heuristics.**
  Rejected: violates the ADR 0008 / cmd-FREEZE discipline (no
  heuristic outcome invention); the structured runner gets a
  real exit code *by construction*.

## Relationship to other ADRs

- **Reuses ADR 0006**: the structured runner is a transport
  adapter behind the same `ShellAdapter` / `SessionHost`
  seam; the core stays pure and shell-agnostic.
- **Reuses ADR 0004 / 0007**: identical `IOCell` /
  `CellEventBus` / navigable-history model; the runner just
  produces *exact* cells.
- **Resolves an ADR 0009 gap on the primary path**: structured
  invocation transports a *real* exit code, so `CellOutcome`
  is `Succeeded` / `Failed`, not `Indeterminate`, for the
  common case.
- **Scopes ADR 0005 honestly**: OSC-133 stays the right
  mechanism *for the secondary PTY mode*; it was never going
  to cover unmarked sub-prompts, and A stops requiring it to.

## Status / next

**Proposed 2026-05-18.** No implementation. The maintainer
selects A, B, or C; the chosen option's consequence list
becomes the next cycle's plan and `docs/SESSION-HANDOFF.md`
§ Next stage is set to it. Until ratified, status-quo
implementation is **paused** — the specified boundary fix is
*ready* but deliberately not started so this decision is not
pre-empted.
