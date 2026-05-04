# pty-speak

A screen-reader-native Windows terminal, built in F# / .NET 9 / WPF, designed
from the ground up for blind developers using NVDA, JAWS, or Narrator —
with Anthropic's Claude Code (and other Ink/React TUIs) as the primary
target workload.

> Status: **pre-alpha.** Latest code-bearing preview is
> [`v0.0.1-preview.43`](../../releases). Shipped on `main`
> and NVDA-verified on Windows 11:
> Stages 0-4 (skeleton + CI, ConPTY host, Williams VT500
> parser, screen model + WPF rendering, UIA Document role
> + Text pattern + Line/Word/Character navigation),
> **Stage 4a** (Claude Code rendering substrate: alt-screen
> + DECTCEM + 256/truecolor SGR + DECSC/DECRC + OSC 52 silent
> drop), **Stage 4b** (process-cleanup diagnostic via
> `Ctrl+Shift+D`), **Stage 5** (streaming output via
> `Coalescer`; functional end-to-end as of PR #116 — pipeline
> reaches NVDA, though the verbose-readback issue is the
> first foundational architecture decision the May-2026 plan
> addresses), **Stage 5a** (diagnostic logging surface:
> `FileLogger.fs` + `Ctrl+Shift+L` + `Ctrl+Shift+;` log-copy
> with `FlushPending` barrier), **Stage 6** (keyboard input
> to PTY + paste + focus reporting + dynamic resize + Job
> Object lifecycle), and **Stage 11** (Velopack auto-update
> via `Ctrl+Shift+U`). Stages 4a / 4b / 5a were formalized
> in the spec per chat 2026-05-03 maintainer authorization;
> see [`spec/tech-plan.md`](spec/tech-plan.md) §4a / §4b /
> §5a. Stages 7-10 are sequenced via
> [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md)
> as cleanup (✓ shipped May 2026) → Stage 7 (validation
> gate; **next**) → Output framework cycle (subsumes Stages
> 8 + 9) → Input framework cycle → Stage 10. Follow
> [Releases](../../releases) for new builds; once installed,
> `Ctrl+Shift+U` updates in place.

## What pty-speak is and does

`pty-speak` is a Windows terminal emulator that exposes its content
to screen readers as structured semantics rather than diffed visible
text. The target workload is Anthropic's Claude Code (and other
Ink/React TUIs), which existing Windows terminals — conhost, Windows
Terminal, VS Code's integrated terminal, Alacritty, WezTerm — all
present poorly to NVDA, JAWS, and Narrator because they render ANSI
visually and expose almost nothing to UI Automation beyond a cell
grid.

The screen-reader fallback of diffing the visible text produces
three failure modes every blind user of Claude Code has felt:

1. **Spinner storms.** Ink redraws the "Thinking…" line ~10 Hz; the
   screen reader announces every diff and freezes.
2. **Selection lists read as flat text.** "Edit / Yes / Always / No"
   arrives as a paragraph instead of a listbox, so arrow-key
   navigation breaks.
3. **ANSI styling is invisible.** Bold, italic, color, and OSC 8
   hyperlinks never reach the screen reader, so braille routing and
   "report font attributes" do nothing.

`pty-speak` fixes this at the architectural layer rather than by
post-hoc filtering. It parses ANSI into a typed event stream, derives
semantic events (prompts, selection lists, errors, spinners) from
those, and exposes them through a real UIA `ITextRangeProvider` plus
`UiaRaiseNotificationEvent` calls — the same surface Windows
Terminal uses, with the attribute identifiers
(`UIA_ForegroundColorAttributeId`, `UIA_FontWeightAttributeId`, …)
actually populated.

When fully shipped, this means a blind developer can:

- Run Claude Code, PowerShell, cmd.exe, or any other Windows console
  program and hear all output via NVDA at conversational pace.
- Navigate the scrollback as a structured document with NVDA's
  review cursor (line, word, character) and quick-nav letters
  (`e` next error, `w` next warning, `c` next command,
  `o` next output block).
- Interact with Claude Code's selection prompts as a real listbox:
  arrow keys move, NVDA announces "Yes, 2 of 4," Enter confirms.
- Hear earcons for ANSI color and style transitions on a separate
  WASAPI shared session that does not duck NVDA.
- Auto-update from GitHub Releases via Velopack with no UAC prompt
  and audible progress announcements.

For the full design rationale, prior-art survey, and architectural
tradeoffs, see [`spec/overview.md`](spec/overview.md). For the
stage-by-stage implementation plan (ConPTY → parser → buffer → UIA →
streaming → input → Claude Code → list provider → earcons → review
mode → Velopack), see [`spec/tech-plan.md`](spec/tech-plan.md).

## Who built this and why

`pty-speak` is built and maintained by **Dr. Kyle Keane** (School of
Computer Science, University of Bristol; previously ~10 years at
MIT; [www.kylekeane.com](https://www.kylekeane.com)).

Kyle is a blind developer. The project is one blind developer's
attempt to build the developer-tool environment that ought to exist
— from the position of someone who needs it to work in order to do
the rest of their work. The wider context — author bio, the
workarounds Kyle uses to make this work in practice, and the values
frame (the WHO ICF position that disability is a property of the
interaction between a person and their environment, not of the
person) that drives the technical decisions in this repository —
lives in [`docs/PROJECT-CONTEXT.md`](docs/PROJECT-CONTEXT.md).

## Get started

`pty-speak` is pre-alpha. Latest preview installer is on the
[Releases](../../releases) page as `pty-speak-Setup.exe`. Step-by-step
install (with the SmartScreen workaround for unsigned previews) plus
the post-install hotkey reference is in
[`docs/INSTALL.md`](docs/INSTALL.md).

If you prefer scripted install, the repository ships
[`scripts/install-latest-preview.ps1`](scripts/install-latest-preview.ps1)
as a PowerShell helper.

To build and develop pty-speak from source instead of running a
pre-built preview, see [`docs/BUILD.md`](docs/BUILD.md).

## Project layout

```
spec/                    Design and implementation specifications
  overview.md            Architectural rationale and prior-art survey
  tech-plan.md           Stage 0–11 walking-skeleton plan
docs/                    Living developer documentation
  ARCHITECTURE.md        Module map and data flow
  ROADMAP.md             Phased roadmap derived from spec/tech-plan.md
  BUILD.md               Build from source
  RELEASE-PROCESS.md     Cutting a Velopack release to GitHub Releases
  ACCESSIBILITY-TESTING.md   Comprehensive manual smoke-test matrix (artifact integrity, process hygiene, NVDA, earcons, auto-update) with per-row diagnostic decoders
  USER-SETTINGS.md       Catalog of current hardcoded values that may become user-configurable later (word boundaries, font, colors, audio, keybindings, etc.)
  UPDATE-FAILURES.md     Reference for the NVDA announcements Stage 11's Ctrl+Shift+U self-update produces on each failure mode
  CHECKPOINTS.md         Stable-baseline tags per shipped stage
  CONPTY-NOTES.md        Observed Windows ConPTY platform quirks
  SESSION-HANDOFF.md     Bridge between Claude Code coding sessions
  PROJECT-PLAN-2026-05.md  Strategic plan supersedes Stages 7-10 sequencing
  HISTORICAL-CONTEXT-2026-05.md  Supplementary backup reference (NOT a primary handoff source)
  STAGE-7-ISSUES.md      Stub for Stage 7 NVDA-validation gap inventory (Part 2)
.github/
  workflows/             CI and release automation
  ISSUE_TEMPLATE/        Bug, feature, accessibility-issue templates
scripts/                 Repo-local utility scripts (install helpers, etc.)
src/                     F# / C# / WPF source (Stage 0 skeleton merged)
tests/                   xUnit + FsCheck.Xunit + FlaUI placeholder
```

## Quick links

The full doc-ownership table + per-audience routing lives in
[`docs/DOC-MAP.md`](docs/DOC-MAP.md). The lists below are
shortcuts for the four most common audiences.

**If you're a human contributor opening a PR:**
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — PR shape, branch naming, F# / .NET 9 gotchas, accessibility non-negotiables, P/Invoke conventions, tests
- [`docs/BUILD.md`](docs/BUILD.md) — local dev setup
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — code orientation
- [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md) — NVDA acceptance criteria

**If you're Claude Code starting a session:**
- [`CLAUDE.md`](CLAUDE.md) — Claude-runtime-only rules (auto-loaded; sandbox quirks, MCP behaviour, ask-for-CI-logs, no-GUI-walks rule, diagnostic recipes)
- [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md) — "Where we left off"
- [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md) — strategic plan
- [`spec/tech-plan.md`](spec/tech-plan.md) — stage-by-stage spec

**If you're the maintainer cutting a release:**
- [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) — Velopack release flow
- [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md) — manual NVDA pass per stage
- [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) — pending baseline-tag pushes
- [`CHANGELOG.md`](CHANGELOG.md) — promote `[Unreleased]` to the new version

**If you're orienting on the project:**
- [`docs/PROJECT-CONTEXT.md`](docs/PROJECT-CONTEXT.md) — author bio, the workarounds Kyle uses to make this work in practice, and the WHO ICF values frame that drives the technical decisions
- [`docs/INSTALL.md`](docs/INSTALL.md) — end-user install path with SmartScreen workaround for unsigned previews
- [`spec/overview.md`](spec/overview.md) — design and rationale
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — high-level stage list
- [`SECURITY.md`](SECURITY.md) — threat model + mitigation status
- [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md) — Win32 ConPTY quirks
- [`docs/USER-SETTINGS.md`](docs/USER-SETTINGS.md) — current and planned settings
- [`docs/UPDATE-FAILURES.md`](docs/UPDATE-FAILURES.md) — Stage 11 update-failure NVDA reference
- [`docs/research/MAY-4.md`](docs/research/MAY-4.md) — prior-art seed for the Output / Input framework cycles (universal event routing + output framework + navigable streaming response queue + linguistic-design rubric)
- [`docs/HISTORICAL-CONTEXT-2026-05.md`](docs/HISTORICAL-CONTEXT-2026-05.md) — May-2026 cleanup cycle archaeology (supplementary reference, not primary handoff)
- [`scripts/install-latest-preview.ps1`](scripts/install-latest-preview.ps1) — install latest preview (PowerShell helper); see [`scripts/README.md`](scripts/README.md)

## System requirements

- Windows 10 1809 (build 17763) or newer — ConPTY is the only supported
  PTY mechanism.
- .NET 9 Desktop Runtime (bundled by the installer).
- A screen reader (NVDA recommended; JAWS and Narrator targeted but only
  manually validated each release).
- Recommended: route NVDA's TTS to a separate audio endpoint from
  earcons. The terminal honours `MMDeviceEnumerator` and lets you pick.

## Contributing

This project optimises for accessibility outcomes over feature breadth.
Before opening a PR please read [`CONTRIBUTING.md`](CONTRIBUTING.md) —
in particular the rules about never raising both `TextChanged` and
`Notification` events for the same content, never swallowing
`Insert` / `CapsLock` / numpad keys, and the F# P/Invoke conventions in
`Terminal.Pty.Native`.

If you are reporting a screen-reader regression, please use the
[Accessibility issue](.github/ISSUE_TEMPLATE/accessibility_issue.yml)
template and include screen-reader name + version, NVDA Event Tracker
log if possible, and the exact steps.

## License, attribution, and citation

`pty-speak` is released under the **MIT License** — see
[`LICENSE`](LICENSE) for the full legal terms. In short: anyone may
use, modify, and redistribute the software provided the copyright
notice is preserved.

**Copyright** (c) 2026 Dr. Kyle Keane, School of Computer Science,
University of Bristol, United Kingdom (https://www.kylekeane.com).

Beyond the legal MIT floor, the author makes two non-binding
courtesy requests, separately documented in [`LICENSE`](LICENSE) and
[`CITATION.cff`](CITATION.cff):

1. **Citation.** If you incorporate this software into research,
   products, services, or accessibility work, please consider
   citing it. The CITATION.cff file is GitHub-recognised and is
   surfaced as a "Cite this repository" link on the repo page.
2. **Written acknowledgment when requested.** If the author
   contacts you in writing to ask for a brief acknowledgment of how
   you have used or adapted this software, please consider sending
   one. The aim is to demonstrate the impact and adoption of
   accessibility-first developer tooling — currently
   underrepresented in the developer-tools field — to funders,
   employers, and the broader software community.

These are courtesy requests, not license conditions. The legal MIT
terms stand on their own; a downstream user is fully within their
rights under the MIT license to ignore the courtesy requests.

**Direct dependency licenses.** Velopack (MIT) and FSharp.Core (MIT).
Test dependencies: xUnit (Apache-2), FsCheck.Xunit (BSD-3), and
Microsoft.NET.Test.Sdk (MIT). Future stages add: NAudio (MIT) for
earcons (Stage 9), FlaUI (MIT) for UIA integration tests (Stage 4),
and Tomlyn (BSD-2) for verbosity-profile config (Phase 2). Optional
runtime dependencies (Piper TTS GPL-3 fork, SuperCollider GPL-3) are
invoked as separate subprocesses and are not statically linked.
