# Roadmap

The roadmap below is the user-facing version of
[`spec/tech-plan.md`](../spec/tech-plan.md). Each stage is a
walking-skeleton slice that ships to GitHub Releases. We commit to a
release tag once the validation criteria for that stage pass against
NVDA on a clean Windows VM.

The release cadence is **stage-by-stage, not time-boxed.** Stage N
ships when its validation matrix is green; we will not bundle stages
together to make a "bigger" release.

## Versioning

| Tag           | Stage gate                              | Public artifact                            |
|---------------|-----------------------------------------|--------------------------------------------|
| `v0.0.x`      | Internal pre-alpha                      | Unsigned Velopack installer (preview only) |
| `v0.1.0`      | Stage 11 (Velopack auto-update works)   | Signed Velopack installer ¹                |
| `v0.2.0`–`v0.9.x` | Phase 2 features (see below)        | Signed installer + delta ¹                 |
| `v1.0.0`      | "Claude Code can drive its own further development inside our terminal" — the spec's Phase 1 success criterion verified by an external blind reviewer | Signed installer + delta ¹ |

¹ Signing (Authenticode + Ed25519 manifest) is currently deferred for
the `v0.0.x-preview.N` line; see
[`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md#re-enabling-signing-deferred)
for the procedure that returns it before `v0.1.0`.

## Phase 1 — MVP (toward v0.1.0)

The Phase 1 goal, copied verbatim from the spec: *a blind user can run
Claude Code (Ink-based React TUI) inside the terminal, hear all output
via NVDA, navigate output as a structured document, interact with
selection lists via NVDA-friendly listbox semantics, and get earcons
for ANSI color/style changes.*

| Stage | Title                                  | Validation                                      | Status |
|-------|----------------------------------------|-------------------------------------------------|--------|
| 0     | Solution skeleton + CI + Hello WPF     | `vpk pack` succeeds; empty window opens         | shipped (`v0.0.1-preview.15`) |
| 1     | ConPTY hello world                     | `dir` bytes returned from cmd.exe via PTY       | merged on `main` |
| 2     | VT500 parser                           | Golden tests + FsCheck never-throws property    | merged on `main` |
| 3     | Screen model + WPF rendering           | `cmd` runs visibly inside the WPF window        | merged on `main` (split as 3a + 3b — see [`docs/CHECKPOINTS.md`](CHECKPOINTS.md)) |
| 4     | First UIA provider (text exposure)     | NVDA review cursor reads the buffer             | **shipped** (`v0.0.1-preview.22`+ for first NVDA verification, `v0.0.1-preview.26` after word-nav + focus + version-suffix follow-ups; PRs #54-#56, #59, #60, #66, #68) |
| 11    | Velopack auto-update                   | `Ctrl+Shift+U` updates from GitHub Releases     | **shipped** (`v0.0.1-preview.26`, NVDA-verified end-to-end via `preview.25 → preview.26` self-update) |
| 5     | Streaming output notifications         | NVDA reads `dir` line by line; spinner doesn't flood | **functional** as of PR #116 (pipeline reaches NVDA end-to-end). The verbose-readback issue — Stage 5's `renderRows` design announces the whole rendered screen on every emit — is the **first foundational architecture decision** addressed by the [May-2026 plan](PROJECT-PLAN-2026-05.md)'s Output framework cycle (Part 3); the original Stage 5 generic coalescer remains in place as the future Stream profile. |
| 6     | Keyboard input to PTY                  | PowerShell, vim, Ctrl+C all work; NVDA keys still work | **shipped** (Stage 6 PR-A parser arms + PR-B `KeyEncoding` + WPF input wiring + Job Object lifecycle). The minimal pass-through input pipeline is the **second foundational architecture decision** addressed by the [May-2026 plan](PROJECT-PLAN-2026-05.md)'s Input framework cycle (Part 4). |
| 7     | Run Claude Code end-to-end             | Roundtrip prompt → response, NVDA reads it      | pending — sequenced as **Part 2 (validation gate)** of the [May-2026 plan](PROJECT-PLAN-2026-05.md); ships before the framework cycles to surface the gap inventory that drives their design. |
| 8     | Interactive list detection + UIA list  | Selection prompt announced as listbox           | pending — **subsumed** by the [May-2026 plan](PROJECT-PLAN-2026-05.md)'s Output framework cycle (Part 3 Stage F: Selection profile). List-detection becomes a profile-detection signal; UIA list exposure becomes a presentation-sink contract. |
| 9     | Earcons (NAudio) + color announcement  | Red plays alarm; Ctrl+Shift+M mutes             | pending — **subsumed** by the [May-2026 plan](PROJECT-PLAN-2026-05.md)'s Output framework cycle (Part 3 Stage G: earcons + colour-to-earcon mapping as a presentation sink the framework drives). Gates the `Ctrl+Shift+M` mute hotkey reservation. |
| 10    | Review mode + structured navigation    | `e` jumps to next error in scrollback           | pending — sequenced as **Part 5 (post-frameworks)** of the [May-2026 plan](PROJECT-PLAN-2026-05.md); first non-built-in consumer of the framework's semantic-event taxonomy. |

A stage in CI green and an internal test pass earns a `vX.Y.Z-preview.N`
prerelease. A stage with a successful external NVDA validation pass
earns a non-prerelease tag.

For the canonical list of stable rollback points (one per shipped
stage), see [`docs/CHECKPOINTS.md`](CHECKPOINTS.md).

### Stage ordering

The original ordering put Stage 11 (Velopack auto-update) last
because auto-update is feature-completeness rather than core
functionality. After Stage 4's manual-NVDA verification cycle
made the install friction's recurring cost visible (each
iterative preview was download → SmartScreen prompts → install,
several screen-reader steps per loop), Stage 11 was
re-prioritised to land ahead of Stages 5-10 and **has now
shipped** (`v0.0.1-preview.26`, NVDA-verified end-to-end). The
justification:

- Stage 11 has **no architectural dependency** on Stages 5-10. It's
  Velopack's `UpdateManager` API + a `KeyBinding` on the Window's
  `InputBindings`; both are independent of streaming notifications,
  keyboard input routing, list detection, earcons, or review mode.
- Every subsequent stage will need its own NVDA verification loop,
  and each loop pays the install-friction tax under the current
  flow. Shipping Stage 11 first amortises that cost across all
  remaining stages.
- The standalone `scripts/install-latest-preview.ps1` (PR #61)
  was the bridge until Stage 11 shipped; it is now **deprecated
  for in-place updates** (use `Ctrl+Shift+U` instead) but
  remains useful for fresh installs and dev-environment
  workflows.

Stage 4's verification completed in parallel with Stage 11
shipping — both are now NVDA-verified end-to-end on
`v0.0.1-preview.26`.

### Post-Stage-7 architecture cycles (May-2026 plan)

Per [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md), the
post-Stage-6 review surfaced two foundational architecture
decisions that the original walking-skeleton plan deferred to
"Stage 5 polish" but are too consequential to leave to
incremental tuning:

1. **Output handling.** Stage 5's `renderRows` design announces
   the whole rendered screen on every emit. Manageable for a
   one-shot command; untenable for typing (NVDA reads the whole
   screen per keystroke); fundamentally wrong for structured
   workloads — REPL prompts, TUI apps, Ink-rendered forms
   (Claude Code), selection lists (fzf, peco). Different content
   paradigms need different presentation strategies, not
   different verbosity tunings of one strategy. The Output
   framework cycle (plan Part 3) abstracts the Stream / REPL /
   TUI / Form / Selection profile taxonomy. **Subsumes original
   Stages 8 (Selection profile = list detection + UIA list) and
   9 (earcons + colour announcement = presentation-sink
   stage).**

2. **Input interpretation.** Today every keystroke goes straight
   from `OnPreviewKeyDown` to the encoder to the PTY, with no
   rolling buffer, no completion, no lint, no natural-language
   suggestion surface. Sighted developers see bash's
   completion; blind developers get nothing. The Input framework
   cycle (plan Part 4) introduces a per-keystroke rolling buffer
   that feeds a tokeniser → lexer → suggestion engine pipeline,
   surfaced on demand via `Ctrl+Space` / `Tab` / `Ctrl+H` so the
   speech queue isn't spammed. Includes an explicit
   echo-correlation API that lets the Output framework's Stream
   profile suppress double-spoken character echoes (closes the
   typed-input half of Issue #115).

The full sequencing is **Part 1 cleanup → Part 2 Stage 7
(validation gate) → Part 3 Output framework cycle → Part 4 Input
framework cycle → Part 5 Stage 10 (first framework consumer)**.
Total estimated effort: ~8-12 weeks of focused work to reach
v0.1.0 with all original-plan stages either shipped or formally
subsumed. See the plan document for the per-part scope and the
research → RFC → staged-implementation breakdown.

## Phase 2 — accessibility depth (v0.2.0 onward)

These items are designed for in [`spec/overview.md`](../spec/overview.md)
but deliberately not part of MVP scope.

- **Self-voicing** via Piper TTS subprocess
  (`huggingface.co/rhasspy/piper-voices`), with separate voice channels
  per semantic stream (stderr, stdout, Claude responses).
- **OSC 133 shell-integration heuristic** — we synthesise OSC 133 A/B/C/D
  events when the child is Claude Code, since upstream refuses to emit
  them.
- **Compiler diagnostic interpreter** for gcc / clang / rustc / MSBuild /
  fsc, exposed via UIA `IInvokeProvider` so the user can press Enter on
  a diagnostic and jump to the source location.
- **Pluggable `ISemanticInterpreter` API** so users can ship their own
  interpreters as F# scripts loaded at startup.
- **JAWS and Narrator parity passes.** NVDA is the primary screen
  reader; JAWS and Narrator regressions are accepted but tracked.
- **Configurable verbosity profiles** (off / smart / verbose) loaded
  from a TOML file via Tomlyn.

## Phase 3 — audio depth

- **`ISpatialAudioClient` sink** (`SpatialSink`) for 3D-positioned
  earcons over Windows Sonic / Atmos.
- **`AsioSink` for multi-channel hardware** (Dante Virtual Soundcard,
  MADI, HDX cards). Up to 64 channels natively.
- **OSC sink to a local SuperCollider `scsynth`** for arbitrary speaker
  layouts, including the long-term 256-speaker ambition. We deliberately
  do not render 256 channels from .NET.
- **Color → 3D position cascading** via the same SGR pipeline that
  drives earcons today.

## Phase 4 — beyond Claude Code

- Generic Ink/React TUI heuristics so other tools (Yarn, Vite, Bun,
  Deno) get the same listbox/spinner/error treatment.
- VT100 screen-reader output for legacy curses applications (htop, vim,
  emacs in `-nw` mode) via a screen-difference fallback.
- A "developer mode" that records every VT byte and every
  Notification event into a replayable log for bug reports.

## What we are not building

- A cross-platform terminal. macOS and Linux already have screen-reader
  ecosystems (VoiceOver Terminal scripting, Speakup, Fenrir); their
  failure modes are different and warrant their own projects.
- A new screen reader. NVDA and Narrator do their job; the gap is in
  the terminal's UIA surface, not in the assistive tech.
- A new VT parser standard. Paul Williams' VT500 spec is the spec.

## How to influence the roadmap

- Stage-blocking issues use the
  [Accessibility issue](../.github/ISSUE_TEMPLATE/accessibility_issue.yml)
  template.
- Out-of-stage requests use the
  [Feature request](../.github/ISSUE_TEMPLATE/feature_request.yml)
  template; we triage them at the start of each stage.
- Architectural changes go through a discussion before code is
  written. Design that contradicts
  [`spec/overview.md`](../spec/overview.md) needs an explicit ADR PR
  updating the spec.
