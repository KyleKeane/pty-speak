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
  risk; dogfood is the gate. R3c (heuristic→adapter
  physical relocation + R1.2 namespace move) remains the
  structural follow-on.
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
- **R5 — PowerShell adapter** (= ADR 0005 Stage D). Full
  A/B/C/D + exit code.
- **R6 — feature unlock** (= ADR 0005 Stage E). Per-line
  progress streaming, prompt-path-verbosity fix, clean
  SpeechCursor command items — trivially correct on the clean
  event stream.
- **R7 — claude/other-shell adapter decision + Cycle closure
  audit** (= ADR 0005 Stage F).

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
