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
- **R5 — PowerShell adapter** (= ADR 0005 Stage D). Full
  A/B/C/D + exit code.
- **R6 — feature unlock** (= ADR 0005 Stage E). Per-line
  progress streaming, prompt-path-verbosity fix, clean
  SpeechCursor command items — trivially correct on the clean
  event stream.
- **R7 — claude/other-shell adapter decision + Cycle closure
  audit** (= ADR 0005 Stage F).

### Deferred to R6+ — the canonical-IOCell navigation / operations layer

Strategic referrals captured 2026-05-16 (maintainer
direction, on the R4c-merge discussion). All are
**deliberately deferred until the canonical IOCell is
solid** (post-R5 foundation), not punted — an explicitly-
tracked, rationale-bearing deferral per the cycle-closure
discipline. R5 (PowerShell's real `A/B/C/D`) is what makes
several of these solvable in a *whole-system* way rather
than against cmd's quirks alone; designing them pre-R5
would be premature.

1. **Retire `stripNextPrompt` → one shell-agnostic
   output-region cut.** Today the trailing-next-prompt edge
   of an IOCell's output is reconstructed by string-suffix
   subtraction (`SessionModel.extractIOCell` slices
   `[…, Int64.MaxValue)` then strips the known prompt text)
   — a residual reconstruction heuristic, exactly the
   ADR-0006 anti-pattern. The clean form is a positional
   region-cut: cmd via deferred-finalise / backtrack-insert
   at the next `;A` Seq (the marker that isn't in
   ContentHistory yet at `;D`-finalise time — see the R4c
   consumer note + ADR 0005 §3 R4c); PowerShell via its
   native `;C`/`;D`. R5 surfaces this cross-shell (the
   `CleanOscAC` arm slices `[;C, MaxValue)` with **no**
   strip — R5's dogfood reveals the general shape), so the
   one model that is correct for both shells is designed
   *then*. Until then `stripNextPrompt` stays (it works on
   the golden path; the failure mode is output that
   legitimately ends with text equal to the prompt path).
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
