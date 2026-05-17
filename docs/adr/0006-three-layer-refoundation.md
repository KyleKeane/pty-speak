# ADR 0006 — Three-layer re-foundation: ShellAdapter / Session core / Accessibility channel

- **Status:** Accepted (2026-05-15; maintainer approved) /
  **R1–R4 Implemented** (2026-05-16, #347–#357). The
  architectural re-foundation. Builds on and **supersedes the
  stage list of [ADR 0005](0005-osc133-shell-integration.md)**
  (0005's OSC-133 mechanism is folded into this ADR's
  R-stages). **The full structural re-foundation is landed
  and CI-green:** R1 (seam extraction, maintainer-dogfood-
  validated) · R2 (cmd OSC-133, Option B; "implicit C"
  realised consumer-side) · R3a (OSC precedence latch) · R3b
  (announce-compensation deletion — KI-R2-1 structurally
  fixed) · R4a (heuristic namespace purify) · R4b
  (portability-lint *enforces* the boundary). Per-stage
  status in the R-stage list below. **Checkpoint (maintainer
  decision 2026-05-16):** R1–R4 is the milestone; a single
  consolidated R1–R4 *foundation dogfood* gates R5 (the
  PowerShell adapter — net-new, must not build on an
  unvalidated foundation). R5–R7 pending that dogfood. Each
  R-stage remains independently CI- and dogfood-gated.
- **R4c added (2026-05-16):** a pre-R5 stage — complete
  the cmd transport with a *boundary-only* deferred `;D`
  (`CommandFinished None`; no exit code — cmd has no
  native `%errorlevel%`-in-prompt mechanism, an OS-level
  limitation). Makes the cmd OSC-133 event stream
  *complete* so R5/R6 are genuinely shell-agnostic in the
  core. Net-corrective (the real `;D` replaces the
  misplaced Cycle-47 synthetic compensation for cmd).
  See the R4c entry in the R-stage list + ADR 0005 §3 R4c
  status note. Folded into the R1–R4 foundation dogfood.
- **Deciders:** maintainer — chose "Re-foundation ADR now" +
  "Drop MAUI consideration for now" (2026-05-15), after the
  strategic review of why 51 cycles produced brittle
  extension.
- **Keeps unchanged:** ADR 0001 (substrate/channel
  dichotomy), ADR 0003 (interaction state machine), ADR 0004
  (IOCell model). This ADR does **not** change the IOCell
  data model. It relocates *boundaries* so shell-specific
  logic cannot leak into the core, and names the single
  orchestration point.
- **Out of scope (maintainer decision 2026-05-15):** WPF→MAUI
  migration. The accessibility channel is defined as an
  interface because that is good design, **not** because a
  MAUI port is planned. No MAUI-shaped abstraction beyond
  "the channel is a swappable sink" is to be introduced.

## Context

Across ~51 cycles, every attempt to extend behaviour (a new
shell, history recall, progress loops, sub-prompts) was
brittle. The strategic review (2026-05-15) traced this to a
single structural defect, not a conceptual one:

- **Shell-specific reconstruction leaked into the core.**
  `HeuristicPromptDetector` lives in `Terminal.Core` and
  reaches into `cmd.exe`'s screen layout. The headless model
  therefore depends on the quirks of one shell.
- **No single orchestration point.** Shell resolution + spawn
  is buried in `Program.fs` (~2797–2890, in a multi-thousand-
  line file); parsing is in `Terminal.Core`; there is no
  enforced contract between "the thing that talks to a shell"
  and "the headless model".
- **The substrate/channel dichotomy (ADR 0001) is sound but
  under-specified at the transport edge.** It names *what the
  model is* and *how it is rendered*, but not *what is allowed
  to produce model input*. That gap is where every shell
  quirk re-entered the core.

[ADR 0005](0005-osc133-shell-integration.md) supplies a clean
structured-event *source* (make shells emit OSC 133). It does
not, by itself, stop the next shell from re-leaking. This ADR
makes the layering **explicit and enforced** so the ADR 0005
gain is permanent.

The conceptual model is *not* being discarded. The VT parser,
IOCell/state-machine modeling, substrate/channel framing, and
the already-shipped-and-tested OSC-133 decoder are correct
assets and are reused. F# is retained — exhaustive sum types
and pattern matching are the right tool for "classify every
byte / model every interaction state"; the difficulty was the
heuristic problem and the CI-only dev loop, not the language.

## Decision — three layers and one orchestration point

1. **Transport layer — `Terminal.Shell` (new assembly).**
   The `ShellAdapter` contract. One adapter per shell family
   (`CmdAdapter`, `PowerShellAdapter`, `ClaudeAdapter`). Each
   adapter owns spawn (command line, environment, OSC-133
   injection per ADR 0005), the PTY handle, and a stateful
   `byte[] -> ShellEvent list` translation. **No layer above
   this knows which shell is running.** The
   `HeuristicPromptDetector` becomes a *fallback translation
   strategy inside `CmdAdapter`*, not a core module.

2. **Session core — `Terminal.Core` (existing, purified).**
   Consumes `ShellEvent`. Owns IOCell / ContentHistory /
   SpeechCursor / input-composition state machine (ADR
   0003/0004). Pure, deterministic, **no WPF, no P/Invoke,
   no shell strings**. `HeuristicPromptDetector` *leaves*
   this assembly.

3. **Accessibility channel — `Terminal.App` / Views
   (existing, narrowed).** Subscribes to core state-change
   notifications, renders to the AT (UIA caret + NVDA routing
   per ADR 0002). The **only** layer that references
   WPF/UIA. A swappable sink — but WPF is the sole
   implementation; MAUI is out of scope.

4. **Orchestration point — `SessionHost` (new, ~one file).**
   The single place that (a) selects a `ShellAdapter` from
   config, (b) wires the adapter's `ShellEvent` stream into
   the session core, (c) wires core state-change
   notifications to the active channel. Replaces the
   shell-resolution / spawn tangle in `Program.fs`.
   Dependency-injected; testable with a fake adapter + fake
   channel and no PTY.

### The load-bearing contracts

- **`ShellEvent`** — the transport↔core boundary type:
  `PromptStart | CommandStart | OutputChunk of bytes |
  CommandFinished of int option | Raw of bytes`. The core
  depends only on this — never on raw bytes, screen
  coordinates, or shell identity.
- **`ShellAdapter`** — `Spawn : SpawnConfig ->
  Result<RunningShell, SpawnError>`; a stateful
  `Translate : byte[] -> ShellEvent list` (per-session
  mutable, so an object/closure, not a pure function — stated
  honestly); `WriteInput : bytes -> unit`; `Dispose`.
  OSC-133 injection is an adapter implementation detail.
- **Channel interface** — the core emits semantic announce
  intents; the channel renders them. Largely already present
  as ADR 0004's `OutputDispatcher`; this ADR formalises it as
  the core→channel contract rather than introducing a new
  one.

### What moves (concrete, verified this session)

- `HeuristicPromptDetector` : `Terminal.Core` →
  `Terminal.Shell` (becomes `CmdAdapter`'s fallback
  translator). This is the specific leak the review found.
- Shell resolution + spawn (`Program.fs` ~2797–2890),
  `ShellRegistry`, `Terminal.Pty.PseudoConsole` wiring →
  `Terminal.Shell` adapters + `SessionHost`.
- `Osc133.fs` stays a **pure** decoder in `Terminal.Core`
  (no shell coupling); adapters *call* it. `Screen.Apply`'s
  OSC arm is driven from inside the adapter's translate path.
  (Exact placement finalised in R2.)

## Relationship to ADR 0005

ADR 0005 = the *mechanism* (make shells emit OSC 133, the
existing clean decoder/`extractIOCell` arm becomes primary).
ADR 0006 = *where that mechanism lives and what it may
touch*. ADR 0005's Stage C ("delete heuristic scaffolding")
is realised here as "relocate heuristic to a fallback adapter
strategy + delete the announce-path compensation patches from
the now-pure core". The two ADRs are not sequential
alternatives; **0006's R-stage list supersedes 0005's A–F
list** and folds 0005's mechanism into it.

## Combined walking-skeleton stages (supersedes ADR 0005 A–F)

- **R0 — this ADR.** Docs-only. Maintainer sign-off gate.
- **R1 — extract `ShellAdapter` + `SessionHost`, zero
  behaviour change.** Wrap the *existing* spawn/parse/
  heuristic path verbatim behind the new interface and host.
  First implementation task: produce the rigorous current
  module/dependency map as the refactor's evidence base.
  Gate: full test suite + a mandatory **behaviour-identical**
  regression dogfood (echo / test-01 narrate exactly as
  before). This proves the boundary holds before any
  semantic change.
- **R2 — cmd OSC-133 adapter** (= ADR 0005 Stage B, now
  inside `CmdAdapter`). Heuristic remains the adapter's
  fallback. Gate: clean command/output incl. history recall,
  `BoundarySource=Osc133`.
- **R3 — precedence + scaffolding deletion** (= ADR 0005
  Stage C). "Once OSC133 seen, mute heuristic" *inside the
  adapter*; delete the PR-AB / PR-X / PR-Y announce-path
  compensations from the core. **Sequenced R3a → R3b**
  (one-concern-per-PR / walking-skeleton): **R3a — precedence
  (in progress, 2026-05-16):** per-shell-session
  `oscSeenThisSession` latch mutes the heuristic dispatch once
  an `Osc133` boundary is seen (reset on shell-switch, not
  alt-screen). Implemented at the current Program.fs detector
  call site; relocating the gate physically *inside*
  `CmdAdapter` (with the deferred R1.2 heuristic-namespace
  move) is a structural follow-on (R3c / R4-purify), behaviour
  first per the R1 precedent. **R3b — scaffolding deletion
  (implemented 2026-05-16, dogfood-pending):** the
  tuple-final announce now speaks the R2-sealed IOCell
  `OutputText` (extractIOCell's `;B`-anchored split);
  PR-X (`lastEnterSeq`) / PR-Y / PR-AB deleted (≈ −200/+60
  LOC, net-subtractive per this ADR's stated R3 shape) —
  the structural fix for KI-R2-1. `commandEnterSeq` +
  `awaitingSubPromptEnter` kept (load-bearing for
  extractIOCell's split, not announce compensations).
  **Scope refinement:** ADR R3 names only PR-AB/X/Y;
  **PR-AA/AC banner deliberately preserved** (the
  pre-first-prompt banner is not a finalised cell, so
  sealed-`OutputText` can't carry it — deleting it
  re-introduces the maintainer-flagged banner-silence
  regression; orthogonal to the KI-R2-1 command-delta
  fragility). Maintainer chose full deletion in one PR
  (2026-05-16) accepting the sub-prompt re-derivation
  risk; dogfood is the gate.
  **R3c — spoken-watermark (done 2026-05-16, dogfood-pending):**
  the R1–R4 foundation dogfood surfaced exactly the accepted
  risk (#2 sub-prompt question re-read) plus a banner
  regression (#1: R3b's deletion of the old PR-AA
  `lastEnterSeq<0` path left fresh-launch with no banner
  mechanism). Root cause = R3b substituted "tuple-final =
  `cell.OutputText` verbatim", ignoring the spoken-watermark.
  R3c re-wires the announce to the pre-existing
  `SpeechCursor.LastSpokenSeq` / `lastAnnouncedSeq`
  primitive: tuple-final + banner speak the un-spoken
  `ContentHistory` Seq gap (`> lastAnnouncedSeq`), trailing
  trim reusing `extractIOCell`'s bounded `MatchedRowText`
  strip. #2 fixed *by construction* (no string strip — NOT
  PR-Y/PR-X resurrection; the *settled* announce watermark
  ≠ KI-R2-1's racy `lastEnterSeq`). Banner default-armed for
  fresh launch. Incremental + tuple-final announces now
  compose — the model R5/R6 require, zero new machinery for
  R6 per-line streaming. The full single-rule unification
  (delete the separate `bannerAnnounce` path) is a proven
  follow-on once R3c is dogfood-validated. The
  heuristic→adapter *physical* relocation (R1.2 namespace
  move shipped in R4a; only the in-`CmdAdapter` placement
  remains) is a separate deferred structural cleanup, not
  blocking.
- **R4 — purify + enforce.** Sequenced R4a → R4b. **R4a —
  purify (done 2026-05-16):** `HeuristicPromptDetector`
  namespace `Terminal.Core` → `Terminal.Shell` (+ explicit
  `open Terminal.Core`; consumers `open Terminal.Shell`;
  logger category restrung) — the deferred R1.2 / R3c tail,
  removing the last namespace-level shell-leak. Behaviour-
  preserving (15 +/2 −). **R4b — enforce (done 2026-05-16):**
  the `portability-lint` CI job gained two anchored checks —
  no P/Invoke in `Terminal.Core`, and no `Terminal.Shell`
  dependency in `Terminal.Core` (`open` + `<ProjectReference>`).
  WPF was already covered (ADR 0001 step). "No shell strings"
  is enforced **structurally** (no shell dependency + no
  P/Invoke ⇒ shell code/strings can't live in core) rather
  than via a brittle literal grep that would false-positive
  on core's legitimate shell-discussing doc-comments —
  rationale recorded in the job comment + CHANGELOG. **This
  is the enforcement that structurally prevents re-leak** —
  the answer to "why did 51 cycles stay brittle". **R4
  complete**: with R1 (extract the seam) + R4 (enforce it)
  the boundary is structural, not disciplinary.
- **R4c — cmd CommandFinished completion (pre-R5; the
  extra stage the maintainer chose 2026-05-16).** R2
  shipped cmd OSC-133 deliberately half: `;A`/`;B` only,
  `;D` deferred. R5 (PowerShell, full A/B/C/D) and R6
  (per-line streaming) are only genuinely shell-agnostic
  in the core if the cmd event stream is *complete* — i.e.
  cmd also delivers a real `CommandFinished` *boundary*.
  R4c prepends a **boundary-only deferred `;D`** to the cmd
  `prompt` template (`$e]133;D$e\` ahead of `;A`), so cmd
  emits `BoundaryKind.CommandFinished None` at the head of
  every prompt — the standard Windows-Terminal cmd
  technique. **No exit code on cmd:** reading the shipped
  R2 code surfaced that cmd's `prompt` cannot render
  `%errorlevel%` natively at all (needs clink / a doskey
  wrapper — rejected: dependency + patch class); the
  *boundary* is what R6 needs (output region `;B`→`;D`),
  the code is a documented OS-level cmd limitation.
  `ShellEvent.CommandFinished of int option` already
  modelled the asymmetry (`None` cmd / `Some` PowerShell).
  Net-additive + net-corrective: the real `;D` replaces
  the misplaced Cycle-47 synthetic-CommandFinished-before-
  `;A` compensation for cmd (it now fires only for the
  residual heuristic shell, claude); consumer changes are
  one augmentation-arm extension (`;D` gets
  MatchedRowText like `;A`, keeping OutputText + the
  tuple-final announce clean — equivalent to the pre-R4c
  PromptStart-interrupt finalise) and one gate (suppress
  the natural `CommandFinished` marker when no cell
  finalised, so cmd's leading/drop-on-None `;D` doesn't
  inject a stray "end output" SpeechCursor stop).
  Mechanism spec: ADR 0005 §3 R4c status note. Pinned by
  `CmdAdapterTests` + the R4c `SessionModelTests` arm;
  dogfood-gated (matrix `52-R4c`) **alongside / folded
  into the R1–R4 foundation dogfood** — same installed
  preview, one NVDA pass.
- **R5 — PowerShell adapter** (= ADR 0005 Stage D).
  **Implemented & dogfood-validated 2026-05-16** (#374
  R5a selection seam · #375 R5b PowerShell adapter).
  Shipped as **A/B/D + `$LASTEXITCODE`, no C** — *not* the
  "Full A/B/C/D" originally written here: PowerShell
  auto-disables PSReadLine under a screen reader (always,
  for this app's user), so the `;C`/`PSConsoleHostReadLine`
  hook can't run. The maintainer R5b dogfood confirmed the
  `prompt`-function path emits OSC-133 correctly under NVDA
  regardless (the #1 R5 risk resolved positive). PowerShell
  rides the **same `CmdOscAB` consumer arm as cmd**, plus a
  real exit code. **The `;C` clean-reference is not an R5
  gap — it is deferred to a future shell with a real
  OutputStart hook** (see §"Deferred to R6+"). Mechanism +
  status note: ADR 0005 §3 PowerShell R5 status note.
- **R6 — feature unlock** (= ADR 0005 Stage E). Per-line
  progress streaming, prompt-path-verbosity fix, clean
  SpeechCursor command items — trivially correct on the clean
  event stream. Sequenced R6a→R6d, one PR + dogfood each
  (walking-skeleton). **R6a — hybrid per-line progress
  streaming: SHIPPED, CI-green & DOGFOOD-VALIDATED
  2026-05-16 (#383, matrix `52-R6a` ✅ PASSED)** — progress
  loop announces each output line as it appears, echo/claude
  unaffected; maintainer cleared the gate ("good to progress
  to the next stage"). Two non-blocking observations: a
  transient first-few-echoes prompt-path-after-output that
  did not reproduce even on restart (watch-item, not chased);
  and progress chunks *interrupt* in-flight speech — expected
  v1 (`MostRecent` supersede by design), smoother queueing
  deferred (see new item 9 below). Maintainer chose the
  *hybrid* model: the authoritative `TupleFinalOnly` seal
  read is unchanged (no edit-conflation risk — it stays the
  screen-authoritative `IOCell.OutputText`); R6a additionally
  fires the **same clean watermark slice** the tuple-final
  uses, on output-quiescence during the `Executing` window
  (the retired idle-flush timer re-wired to the validated
  R3c/R3e watermark — `fromSeq = max commandEnterSeq
  lastAnnouncedSeq`, no command-echo, no next-prompt-strip
  needed mid-execution). Watermark-composed so the seal
  speaks only the remainder (`52-R3c-multi` proved the
  composition). Gated Executing ∧ `TupleFinalOnly`; claude
  unaffected (`IdleFlushMs = None`). This is why the ADR
  predicted "trivially correct on the clean event stream" —
  R6a is purely the *trigger*, the slice + composition were
  already validated. **R6b — prompt-path verbosity: SHIPPED
  & dogfood-PASSED 2026-05-16 (#385, matrix `52-R6b` ✅;
  maintainer "this works"). R6b-followup (this-PR) added the
  three additional on-change modes the maintainer requested
  in the same dogfood — `FinalOnChangeElseFull` (the mirror)
  + `SilentOnUnchangedFullOnChange` /
  `SilentOnUnchangedFinalOnChange` (silent when unchanged) —
  completing the {full,final} × {change,unchange,silent}
  family; the announce-interrupts-output limitation the
  maintainer also flagged is parked as deferred item 10
  (explicitly NOT addressed now, maintainer's call).** The
  OSC-133 clean `;A`…`;B` prompt string (R2–R5)
  removed the reason cmd/PowerShell were forced to the silent
  `Suppress` default. Maintainer decision: **keep `Suppress`
  as the per-shell default** (no daily-flow change — auto-drive
  stays silent at prompts, the prompt stays navigable in
  review; this is exactly the ADR's "trivially correct on the
  clean event stream" — the mechanism + clean text were already
  in place) and **add** a new selectable
  `PromptPathMode.FullOnChangeElseFinal` (full path when the
  prompt differs from the previously-narrated one — a `cd` or
  the first prompt after a shell-switch — final-dir-only when
  unchanged), alongside the unchanged always-`Full` /
  always-`FinalDirOnly`. The stateful "changed?" decision is
  resolved by `SpeechCursor` (`effectivePromptPath`/
  `resolveOnChange` + a per-cursor `LastPromptStartPayload`
  cleared by the shell-switch-only `reset`) *before* the pure
  `ShellPolicy.trimPromptPath`, which keeps a context-free
  `Full` fallback for the new case. Manual-nav untouched (it
  already forces `FinalDirOnly` for PromptStart). **R6c clean
  SpeechCursor command items → R6d PS-diagnostics submenu**
  (deferred item 7) remain; R6b's `52-R6b` dogfood is **PASSED
  → R6c unblocked** (R6b-followup rides the same `52-R6b`
  matrix as a followup row, not a new gate).
- **R7 — claude/other-shell adapter decision + Cycle closure
  audit** (= ADR 0005 Stage F).

### Deferred to R6+ — the canonical-IOCell navigation / operations layer

> **Items 2, 3, 4, 5 below (the canonical-IOCell
> navigation / per-cell-operations / review-cursor-document
> cluster) are unified and sequenced in
> [ADR 0007](0007-canonical-iocell-history-navigation.md)
> (Proposed 2026-05-16) — the comprehensive design review
> the maintainer requested in place of the R6c "quick
> patch". This list is retained as the originating record;
> ADR 0007 is the live design + phased plan once accepted.**

**Decision (maintainer, 2026-05-16, post-`5518f5c`
foundation dogfood + bundle analysis): freeze cmd
announce-heuristic work until the PowerShell clean
reference exists.** The R1–R4 + R4c bundle proved the
IOCell substrate is sound — `extractIOCell` produced a
correct `CmdLen`/`OutLen` split on every command (plain,
`dir`, test-02, test-09's seven segments); the
`RecentTuple` dump and ContentHistory markers are clean.
**Every remaining cmd defect lives in the announce-
reconstruction layer on top**, not the substrate:
- the R3d tuple-final announce slices
  `[max(commandEnterSeq, lastAnnouncedSeq), end)` then
  string-strips the trailing prompt using a *racy screen-
  cursor-row snapshot*; under history-recall churn (the
  bundle showed ~60 `PR-I history-recall` arrow-spam
  events and a Seq-86 `"echo1 echo hidir echo hi…"`
  accumulation) that snapshot mismatches → path/echo
  bleed. Same byte-stream-reconstruction root cause as
  deferral 1 below; **not a new R4c regression** (R4c only
  made the strip reachable on the `;D` path; the racy
  mechanism is R3c/R3d, unchanged).
- cmd emits **no pre-prompt banner at all** (bundle: zero
  `R3c banner announce` for cmd; PowerShell's fires with
  the full text). "Terminal edit Blank" on fresh cmd
  launch is the empty-document focus fallback, **not a
  bug** — architectural for cmd. The banner *mechanism*
  works (PowerShell proves it).

Consequently: **do not push further cmd announce-heuristic
patches pre-R5.** PowerShell's native `A/B/C/D` removes the
reconstruction entirely and becomes the clean reference;
cmd's announce path is then rebuilt to that shape
(deferral 1). Continuing to patch cmd announce heuristics
now is the documented tail-chasing loop. cmd substrate
work (R4c-class) remains fine; only the announce-heuristic
layer is frozen.

Strategic referrals captured 2026-05-16 (maintainer
direction, on the R4c-merge discussion). All are
**deliberately deferred until the canonical IOCell is
solid** (post-R5 foundation), not punted — an explicitly-
tracked, rationale-bearing deferral per the cycle-closure
discipline. R5 (PowerShell's real `A/B/C/D`) is what makes
several of these solvable in a *whole-system* way rather
than against cmd's quirks alone; designing them pre-R5
would be premature.

1. **Retire byte-stream reconstruction on BOTH IOCell-output
   edges → real marker boundaries.** Today *both* edges of an
   IOCell's output region are reconstructed, not cut at a
   marker:
   - **Trailing edge (output / next-prompt).**
     `SessionModel.extractIOCell` slices `[…, Int64.MaxValue)`
     then string-strips the known prompt text
     (`stripNextPrompt`). Failure mode: output that
     legitimately ends with text equal to the prompt path.
   - **Leading edge (command / output split).** cmd emits no
     real `;C` OutputStart, so the command-vs-output split is
     the **`commandEnterSeq` byte-level Enter watermark**, not
     a marker. Failure mode: **fast-typing a command and
     hitting Enter quickly races the watermark capture**, so
     the echoed command leaks into the announce — observable
     as "`echo hi` spoken, then `hi` spoken" instead of just
     "`hi`" (the `52-R3d` fail signal). R3d's
     `max(commandEnterSeq, lastAnnouncedSeq)` fixed
     *normal-paced* typing; the **fast-type window is
     residual** and is *not* an R4c regression (R4c changed
     only which boundary finalises + the trailing-edge
     augmentation; it did not touch `commandEnterSeq` or the
     NVDA-caret path). A second contributor may be NVDA's own
     caret-read of the echoed input line (ADR 0002 channel) —
     a `Ctrl+Shift+D` bundle (`R3d tuple-final announce …
     CommandEnterSeq=… FromSeq=…`) disambiguates; deferred,
     not needed pre-R5. **Classification rule:** if a
     *pre-R4c* preview does not show the fast-type leak but a
     post-R4c one does, it is a regression and blocks —
     otherwise pre-existing (the byte-watermark lineage of
     KI-R2-1).

   Both edges are the same root cause — byte-stream
   reconstruction instead of real markers — and both have the
   same clean form: a positional region-cut. cmd via
   deferred-finalise / backtrack-insert at the next `;A` Seq
   (the marker not in ContentHistory yet at `;D`-finalise
   time — see the R4c consumer note + ADR 0005 §3 R4c) plus a
   real command/output boundary (cmd has no `;C` hook, so
   this is the harder half); PowerShell via its native
   `;C`/`;D`. R5 surfaces this cross-shell (the `CleanOscAC`
   arm slices `[;C, MaxValue)` with **no** strip — R5's
   dogfood reveals the general shape), so the one model
   correct for both shells is designed *then*. Until then the
   `stripNextPrompt` + `commandEnterSeq` reconstruction stays
   (golden path works; the two failure modes above are the
   known residue).

   **Tracked variant — command-line-edit input/output desync
   (maintainer dogfood 2026-05-17, `52-ADR7-P2a`).** Editing
   the command line before Enter (e.g. typing, **backspace**,
   retyping, Enter) makes cmd reprint/reflow the line, so
   `ContentHistory` accumulates `TextSpan → Overwrite →
   TextSpan`. `ContentHistory.sliceText` treats an `Overwrite`
   as *appended* text rather than "replaces the prior visual
   region", and `commandEnterSeq` was not captured at a clean
   boundary, so `SessionModel.extractIOCell`'s heuristic split
   attributes the *pre-edit* fragment as one cell's command
   while the *real* command's output binds to it — shifting
   command/output pairing by one for every subsequent cell (a
   phantom input cell). **Same root cause as the leading-edge
   residue above** (byte-stream reconstruction, no real cmd
   `;C`; plus the `Overwrite`-semantics gap in `sliceText`);
   **not an ADR 0007 Phase 0/1/2a regression** — Phase 2a's
   copy faithfully copied an already-wrong cell. ADR 0007's
   navigable history made this *deterministically diagnosable*
   instead of "sounds haywire" — the ADR 0008 / ADR 0007 D6
   premise validating itself. **Decision (maintainer
   2026-05-17): record as this pre-existing residual and
   continue the ADR 0007 phases; do NOT speculative-patch the
   heuristic.** The clean fix is this item's
   marker/`Overwrite`-aware reconstruction; ADR 0007 Phase 7's
   oracle is designed to pin exactly this off-by-one once the
   harness exists.

   **Tracked variant 2 — command argument truncation on a
   clean command (maintainer dogfood 2026-05-17,
   `52-ADR7-P2b`).** A *simple, un-edited* `echo hi` (no
   backspace, no reflow) captures only `echo` as the IOCell
   command — the whitespace-delimited argument(s) are lost.
   Distinct *symptom* from variant 1 (no editing involved;
   the loss is within a single clean command line, not a
   cross-cell pairing shift) but the **same root-cause
   family**: the cmd command text is reconstructed from a
   `ContentHistory` slice bounded by the `commandEnterSeq`
   byte-watermark rather than cut at a real `;B`/`;C`
   marker, so a sub-line boundary (here, the first space)
   can terminate the captured command early. **Not an ADR
   0007 Phase 2b regression** — Phase 2b's copy faithfully
   copied whatever `extractIOCell` produced (output copy +
   focus-hold both confirmed working in the same dogfood);
   the truncation is upstream and pre-existing. **Decision
   (maintainer 2026-05-17): record alongside variant 1 and
   continue the phases; do NOT speculative-patch (cmd
   announce-heuristic FREEZE).** Fixed by the same
   marker-aware command/output reconstruction this item
   specifies; ADR 0007 Phase 7's oracle pins it once the
   harness exists.
2. **Replace SpeechCursor manual history-navigation with
   canonical IOCell-history navigation + per-cell
   operations.** The SpeechCursor's navigable history and
   the underlying review-cursor document have been
   strategically under-invested while the substrate
   stabilised. The target: navigate the **IOCell history**
   itself, with reliable, typed per-cell operations —
   copy-output-to-clipboard on output cells, run-again on
   input cells (CORE-ABSTRACTION-BOUNDARY's history sub-pane
   + reserved peer panes). Gated on the canonical IOCell so
   navigation/operations bind to a stable cell identity, not
   a Seq-walk over raw ContentHistory.
3. **Open decision — is IOCell history written into the
   review-cursor document?** NVDA's review cursor navigates
   a UIA text document (ADR 0002 channel). Whether the
   canonical IOCell history is materialised into that
   document (so review-cursor nav == cell-history nav) or
   kept as a parallel structure with its own nav surface is
   an **unresolved architectural decision**, not yet taken.
   Record it; decide post-canonical-IOCell.
4. **Live current-line / system focus → NVDA "read current
   line".** The system focus / caret is not currently moved
   to the live current line in a way that propagates to
   NVDA's read-current-line keyboard shortcut. Tracked gap;
   better served once the canonical IOCell + its document
   representation (item 3) are settled, since the answer
   depends on what "current line" means against the cell
   model.
5. **Multi-interrupt IOCell internal structure.** See ADR
   0004 Decision 2's future-direction note (annotated
   2026-05-16): the IOCell produced by multi-interruption /
   sub-prompt-flow tests should expose its internals as a
   collection of navigable chunks within a container — or at
   minimum a sequence of unambiguous output segments
   connected to the most-recent input cell — rather than one
   opaque inline blob. The boundary substrate already
   supports the bracketing; this is the navigation/structure
   promotion, gated on the canonical IOCell.
6. **Clean `;C` / `CleanOscAC` reference shell.** R5
   confirmed (dogfood 2026-05-16) that PowerShell cannot
   emit `;C` for pty-speak's screen-reader user (PSReadLine
   auto-disabled). cmd has no `;C` hook either. So the
   clean `CleanOscAC` extraction arm
   (`SessionModel.extractIOCell` `Some promptStart, Some
   outputStart, _` — no watermark, no `stripNextPrompt`)
   has **no shell exercising it today**; both shipped
   shells use `CmdOscAB`. The clean reference (which would
   also unblock retiring `stripNextPrompt`, deferral 1)
   waits for a future shell with a real Enter→output hook
   (a claude TUI mode, or a custom/instrumented shell).
   Not an R5 gap — a recorded architectural conclusion.
7. **`Diagnostics → PowerShell Interaction Tests` submenu**
   (maintainer-requested 2026-05-16, R5b dogfood). Mirror
   the existing `CMD Interaction Tests` menu (`Program.fs`
   + `publish/scripts/cmd-tests/*.cmd`) with PowerShell
   equivalents (`.ps1`) of the test-01/02/04/09 scenarios,
   so PS manual coverage is one keystroke like cmd's.
   **Deferred until R6** — adding empty scaffolding before
   PS feature behaviour (per-line progress, prompt
   verbosity) defines the concrete scenario list has no
   signal; wire it when R6 needs PS-specific manual tests.
8. **P3b — remove dead pathway config plumbing**
   (investigated 2026-05-16, #380). `ShellPathwayConfig.
   PathwayId` + `resolvePathwayForShell` + the
   `[pathway.<id>]` parameter-table parsing in
   `Terminal.Core/Config.fs` are confirmed dead (no live
   `Terminal.App` consumer since Cycle 45c deleted the
   pathway pipeline) **but entangled in the live
   `ShellPathwayConfig` record**, which also carries the
   live `Verbosity` → `currentShellPolicy.Streaming/
   PromptPath` config consumed throughout `Program.fs`.
   Removal is a surgical, wide-ish, locally-unverifiable
   record/parser refactor of a ~1000-line core config
   module — deliberately NOT bundled into the P1–P5
   proceed-pass (the audit's "safe-to-delete" was
   optimistic; the cmd announce FREEZE does not apply but
   the no-risky-locally-unverifiable-rip discipline does).
   Its own careful PR: extract/rename the record so the
   live `Verbosity`/prompt-path config is preserved
   byte-for-byte while `PathwayId`/resolver/`[pathway.*]`
   parsing are dropped + a deprecation note added. Low
   priority (inert; pure cleanliness); not gating R6.

9. **Smoother progress-chunk delivery (R6a follow-on).**
   Surfaced in the `52-R6a` dogfood 2026-05-16: an R6a
   progress chunk uses `ActivityIds.output` + `MostRecent`,
   so it **interrupts** whatever NVDA is currently speaking
   (a prior chunk, an unrelated announce). The maintainer
   confirmed this is **acceptable v1 behaviour** and the
   correct supersede semantics for *output* (a later chunk
   should win over a stale one) — but flagged that a more
   sophisticated model (queue-and-coalesce rather than
   hard-interrupt, or a chunk-cadence floor so closely-spaced
   flushes don't stutter) is worth exploring "in the far
   future". NOT a v1 defect; no behaviour change owed. Park
   here so the design conversation has a home; revisit only
   if real-use friction makes it a priority over the R6b–R7
   primary track. Any future work here must preserve the
   tuple-final seal as the screen-authoritative read and the
   R3c/R3e watermark composition (the load-bearing invariants
   R6a is built on).

10. **Prompt-path announce interrupts the output read
    (R6b follow-on).** Surfaced in the `52-R6b` dogfood
    2026-05-16: when a non-`Suppress` prompt-path mode
    narrates the prompt, that announce uses
    `ActivityIds.output` + `MostRecent`, so it **interrupts**
    a still-in-progress output read of the *previous*
    command (the same supersede semantics as item 9, here
    biting the prompt-vs-output boundary rather than
    chunk-vs-chunk). The maintainer confirmed this is a
    **known limitation, explicitly NOT to be addressed now**
    ("better just noted as a future improvement … so we can
    move forward") — it does not block R6b or its followup
    (the modes themselves work; dogfood-passed). Likely
    shares a fix with item 9 (queue-and-coalesce / an
    activity-ID separation for prompt vs output so a prompt
    announce defers behind an unfinished output read instead
    of cutting it off). Park here; revisit with item 9 if
    real-use friction prioritises it over the R6c–R7 primary
    track. Any future work must preserve the `Suppress`
    per-shell default (the daily flow is unchanged — this
    only bites users who opt into an announcing prompt-path
    mode).

Each stage independently CI-gated and dogfood-validated. R1
and R4 are the new structural gates that make the boundary
real and keep it real.

## Consequences

- **R1 is a sizeable behaviour-preserving refactor — the
  risky kind.** Subtle regressions are easy and there is no
  local `dotnet` (CI-only validation; multi-minute round
  trips). Mitigation: R1 is explicitly behaviour-identical,
  so its dogfood is a strict equality check, gated before any
  semantic stage; ship in reviewable slices, read twice
  before push (per CLAUDE.md).
- **Net-subtractive at the core, small-additive at the
  edges.** The brittle parts (heuristic + compensation
  patches) leave the core; the additions (explicit adapters +
  one host) are small and are exactly the seam the project
  has lacked.
- **F# kept; WPF kept; MAUI dropped from scope.** The channel
  stays an interface for design hygiene only.
- **Every future shell/interaction is an adapter, not a core
  patch.** The 51-cycle failure mode is structurally
  prevented by R4's lint, not by discipline alone.

## Alternatives considered

- **Plan from scratch (F# 10 + MAUI)** — rejected by the
  maintainer 2026-05-15; would re-derive this same model and
  re-learn the OSC-133 lesson at the cost of months and the
  repo's correct assets.
- **Keep two layers (substrate/channel) and just land
  OSC 133** — rejected: leaves the transport↔core boundary
  implicit, so the next non-cmd shell re-leaks; does not
  address the 51-cycle failure mode.
- **Adopt MAUI now** — rejected by the maintainer
  2026-05-15; couples a platform migration to an
  architecture re-foundation (two large risks at once), and
  MAUI's Windows screen-reader parity vs direct WPF+UIA is an
  unquantified risk that would gate, not ride along with,
  such a move.

## Validation gate

Structural acceptance: **R1 behaviour-identical regression
dogfood** + **R4 portability-lint enforcement** green.
First semantic proof: **R2 cmd OSC-133 dogfood** (the
`echo hi` / history-recall / progress-loop scenarios that
defeated the heuristic model produce exact boundaries with no
announce-path watermark or strip). Per-stage NVDA matrix rows
in [`../ACCESSIBILITY-TESTING.md`](../ACCESSIBILITY-TESTING.md).
