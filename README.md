# pty-speak

A screen-reader-native Windows terminal, built in F# / .NET 9 / WPF, designed
from the ground up for blind developers using NVDA, JAWS, or Narrator —
with Anthropic's Claude Code (and other Ink/React TUIs) as the primary
target workload.

> Status: **pre-alpha, Stage 0 shipped.** The first preview build,
> `v0.0.1-preview.15`, is an empty WPF window with the deployment
> pipe (build → Velopack pack → GitHub Releases) validated end to
> end. NVDA announces the window title. Follow
> [Releases](../../releases) for new builds.

## Why this exists

Existing Windows terminals — conhost, Windows Terminal, VS Code's integrated
terminal, Alacritty, WezTerm — all render ANSI visually and expose almost
nothing to UI Automation beyond a cell grid. NVDA falls back to diffing the
visible text, which produces three failure modes that every blind user of
Claude Code has felt:

1. **Spinner storms.** Ink redraws the "Thinking…" line ~10 Hz; the screen
   reader announces every diff and freezes.
2. **Selection lists read as flat text.** "Edit / Yes / Always / No" arrives
   as a paragraph instead of a listbox, so arrow-key navigation breaks.
3. **ANSI styling is invisible.** Bold, italic, color, and OSC 8 hyperlinks
   never reach the screen reader, so braille routing and "report font
   attributes" do nothing.

`pty-speak` fixes this at the architectural layer rather than by post-hoc
filtering: it parses ANSI into a typed event stream, derives semantic
events (prompts, selection lists, errors, spinners) from those, and exposes
them through a real UIA `ITextRangeProvider` plus `UiaRaiseNotificationEvent`
calls — the same surface Windows Terminal uses, with the attribute
identifiers (`UIA_ForegroundColorAttributeId`,
`UIA_FontWeightAttributeId`, …) actually populated.

For the full design rationale, prior-art survey, and architectural
tradeoffs, see [`spec/overview.md`](spec/overview.md). For the
stage-by-stage implementation plan (ConPTY → parser → buffer → UIA →
streaming → input → Claude Code → list provider → earcons → review mode →
Velopack), see [`spec/tech-plan.md`](spec/tech-plan.md).

## What you can do with it (when shipped)

- Run Claude Code, PowerShell, cmd.exe, or any other Windows console
  program and hear all output via NVDA at conversational pace.
- Navigate the scrollback as a structured document with NVDA's review
  cursor (line, word, character) and quick-nav letters (`e` next error,
  `w` next warning, `c` next command, `o` next output block).
- Interact with Claude Code's selection prompts as a real listbox: arrow
  keys move, NVDA announces "Yes, 2 of 4," Enter confirms.
- Hear earcons for ANSI color and style transitions on a separate WASAPI
  shared session that does not duck NVDA.
- Auto-update from GitHub Releases via Velopack with no UAC prompt and
  audible progress announcements.

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
  ACCESSIBILITY-TESTING.md   NVDA / Narrator / JAWS validation matrix
.github/
  workflows/             CI and release automation
  ISSUE_TEMPLATE/        Bug, feature, accessibility-issue templates
src/                     F# / C# / WPF source (Stage 0 skeleton merged)
tests/                   xUnit + FsCheck.Xunit + FlaUI placeholder
```

## Quick links

- **Design and rationale:** [`spec/overview.md`](spec/overview.md)
- **Implementation plan (Stages 0–11):** [`spec/tech-plan.md`](spec/tech-plan.md)
- **Roadmap:** [`docs/ROADMAP.md`](docs/ROADMAP.md)
- **Architecture map:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- **Build from source:** [`docs/BUILD.md`](docs/BUILD.md)
- **Release process:** [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)
- **Stable checkpoints (rollback guide):** [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md)
- **ConPTY platform notes:** [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md)
- **Session handoff (for picking up between Claude Code sessions):** [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)
- **Accessibility test matrix:** [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
- **Security and trust model:** [`SECURITY.md`](SECURITY.md)
- **Contributing:** [`CONTRIBUTING.md`](CONTRIBUTING.md)
- **Changelog:** [`CHANGELOG.md`](CHANGELOG.md)

## Install

> [!WARNING]
> **Preview builds are unsigned.** `v0.0.x-preview.N` releases carry
> no Authenticode signature and no Ed25519 manifest signature.
> SmartScreen will warn on first install. Do not install preview
> builds on machines that handle sensitive data. Authenticode + Ed25519
> signing return before `v0.1.0` — see [`SECURITY.md`](SECURITY.md).

Once Stage 11 (Velopack auto-update) lands, the GA flow is:

1. Download `pty-speak-Setup.exe` from the latest release.
2. Run it. Velopack installs to `%LocalAppData%\pty-speak\current\` and
   creates a Start menu entry. No admin / UAC prompt.
3. Launch from Start. NVDA should announce the window and a help line on
   first run; press `Alt+F1` for keyboard help at any time.
4. Self-update from inside the app with `Ctrl+Shift+U` (this checks GitHub
   Releases and applies the delta in ~2 seconds).

For the Stage 0 preview the installer opens an empty window — there is
no terminal surface yet. Until Stage 11 ships, follow
[`docs/BUILD.md`](docs/BUILD.md) to build locally.

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

## License

MIT — see [`LICENSE`](LICENSE).

This project depends on Velopack (MIT), NAudio (MIT), Elmish.WPF
(MIT), Tomlyn (BSD-2), and FsCheck / Expecto / FlaUI (MIT). Optional
runtime dependencies (Piper TTS GPL-3 fork, SuperCollider GPL-3) are
invoked as separate subprocesses and are not statically linked.
