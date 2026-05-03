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

- **Design and rationale:** [`spec/overview.md`](spec/overview.md)
- **Implementation plan (Stages 0–11):** [`spec/tech-plan.md`](spec/tech-plan.md)
- **Strategic plan (May 2026 — supersedes Stages 7-10 sequencing):** [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md)
- **Roadmap:** [`docs/ROADMAP.md`](docs/ROADMAP.md)
- **Architecture map:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- **Build from source:** [`docs/BUILD.md`](docs/BUILD.md)
- **Release process:** [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)
- **Stable checkpoints (rollback guide):** [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md)
- **ConPTY platform notes:** [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md)
- **Session handoff (for picking up between Claude Code sessions):** [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)
- **Historical context (May-2026 cleanup cycle, supplementary reference):** [`docs/HISTORICAL-CONTEXT-2026-05.md`](docs/HISTORICAL-CONTEXT-2026-05.md) — _not_ a primary handoff source; backup curated list of guiding principles + technology specificities + paradigms that emerged during the May-2026 cycle, in case interesting details aren't captured in CHANGELOG / commit messages.
- **Manual smoke-test matrix (every release):** [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
- **User settings catalog (current and planned):** [`docs/USER-SETTINGS.md`](docs/USER-SETTINGS.md)
- **Stage 11 update-failure announcements (NVDA reference):** [`docs/UPDATE-FAILURES.md`](docs/UPDATE-FAILURES.md)
- **Install latest preview (PowerShell helper):** [`scripts/install-latest-preview.ps1`](scripts/install-latest-preview.ps1) — see [`scripts/README.md`](scripts/README.md)
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

### App-reserved hotkeys (currently shipped)

These are captured by pty-speak at the window level before any future
Stage-6 keyboard layer routes input to the child shell. Stage 6 will
preserve this list per the app-reserved-hotkey contract in
[`spec/tech-plan.md`](spec/tech-plan.md) §6.

- **`Ctrl+Shift+U`** — self-update via Velopack (Stage 11). Checks
  GitHub Releases and applies the delta in ~2 seconds.
- **`Ctrl+Shift+D`** — launch the bundled process-cleanup diagnostic
  in a separate PowerShell window. Verifies that closing pty-speak
  via Alt+F4 or the X button leaves no orphan `Terminal.App.exe` /
  ConPTY child processes. Output is plain text NVDA reads aloud
  naturally.
- **`Ctrl+Shift+R`** — open the GitHub "draft a new release" form in
  your default browser. Maintainer shortcut — the normal release flow
  is to publish a release in the Releases UI (which creates the tag
  and triggers the Velopack workflow), and this hotkey skips the
  navigation step.
- **`Ctrl+Shift+L`** — open the logs folder
  (`%LOCALAPPDATA%\PtySpeak\logs\`) in File Explorer. Useful for
  grabbing a previous session's log when reporting a bug.
- **`Ctrl+Shift+;`** — copy the active session's log file content
  to the clipboard. The semicolon / colon key sits right next
  to `L` on a US-layout keyboard, so it pairs by physical
  proximity with the `Ctrl+Shift+L` open-folder primary. NVDA
  announces the byte count; switch to the chat / email / issue
  and paste. The fastest way to send a session log to a
  maintainer. See [`docs/LOGGING.md`](docs/LOGGING.md) for what
  is and isn't logged.

Reserved but not yet implemented: `Ctrl+Shift+M` (Stage 9 mute
toggle), `Alt+Shift+R` (Stage 10 review-mode toggle).

The historical Stage 0 preview installer opens an empty window. Once
the next preview is cut from the current `main`, the installer launches
`cmd.exe` under ConPTY and renders its output in the window — input
to the PTY arrives in Stage 6. Until Stage 11 (auto-update) ships,
follow [`docs/BUILD.md`](docs/BUILD.md) to build locally.

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

## The complexities of trying to work with technology as a blind developer

This project exists because mainstream developer tools — including
the ones built by AI labs whose mission statements include making
their products useful to everyone — keep shipping interfaces that
cannot be operated by a person using a screen reader. `pty-speak`
is one blind developer's attempt to build a usable surface around
one of those tools (Anthropic's Claude Code) so that the
capabilities it offers everyone else are actually reachable from
an assistive-technology stack.

The path to writing this code is itself a small case study in why
this work is needed.

The Claude Code desktop application is not screen-reader-accessible.
There is no usable alternative on a workstation. The iOS app is
the only viable channel, and the iOS app has its own gap: the
"Add feedback" input field — the field a user types into to send
the next message to Claude — is not activatable from VoiceOver.
The standard touch-explore gesture finds the field, but the
double-tap-to-activate gesture does not focus it for input. The
workaround, which I have used for every message in this session,
is:

1. Disable VoiceOver entirely.
2. Place a finger on the screen at the remembered pixel location
   of the input field.
3. Activate the field by sighted-touch contract.
4. Re-enable VoiceOver to hear Claude's response.
5. Repeat for the next message.

That cycle runs hundreds of times across a working session.
Disabling the screen reader temporarily strips off the access
infrastructure that makes the rest of the device navigable, on a
touchscreen designed under the assumption that sight is available
for any unrecovered fall-back. It is a workaround in the strict
sense: it gets the work done, but the cost is borne entirely on
one side.

While using this workaround to build a terminal that exposes
Claude Code to NVDA, Claude itself produced an idiom in this
session that is worth recording, because it reveals how deep the
problem runs.

The idiom was the phrase "blindly iterating" — used by the model
to mean "iterating without information," "guessing without
evidence," or "speculative iteration." That is not what the words
literally say. The words equate "blind" with "ignorant," and they
do so casually, in a position where the speaker (the model) had
many specific, accurate words available and chose the metaphor
anyway. The accurate words are concrete:

- *uninformed iteration*
- *iterating without diagnostic data*
- *speculative iteration*
- *guessing without evidence*

Each describes the behaviour. None requires a sight-based metaphor.
The reason "blindly" is offensive is straightforward: it embeds
the assumption that the absence of sight is the canonical example
of the absence of information. Blind people are not less informed
than sighted people; we use different channels. A sighted person
who runs CI three times without reading the build log is operating
without information. A blind developer reading that log via a
screen reader is operating with full information. The metaphor
inverts the actual relationship between sight and knowledge, and
it does so in casual technical writing where it costs the writer
nothing to say what they actually mean.

This is not a request to police every word. It is a request to
prefer the literal phrasing when the literal phrasing is
available, because the literal phrasing communicates more
precisely *and* it does not casually demean the people whose
working conditions this project is being built to improve.

If you are reading this from inside Anthropic and would like a
report from a customer using your product through the iOS
workaround above to ship accessibility infrastructure: please
fix the iOS "Add feedback" field's VoiceOver activation, ship a
screen-reader-accessible desktop application, and review training
data for sight-based idioms used as synonyms for cognitive
deficit. The work that goes into that review is the same work
that produces a more precise model output for everyone — there is
no trade-off to mitigate.

## License

MIT — see [`LICENSE`](LICENSE).

Current direct dependencies: Velopack (MIT) and FSharp.Core (MIT).
Test dependencies: xUnit (Apache-2), FsCheck.Xunit (BSD-3), and
Microsoft.NET.Test.Sdk (MIT). Future stages add: NAudio (MIT) for
earcons (Stage 9), FlaUI (MIT) for UIA integration tests (Stage 4),
and Tomlyn (BSD-2) for verbosity-profile config (Phase 2). Optional
runtime dependencies (Piper TTS GPL-3 fork, SuperCollider GPL-3) are
invoked as separate subprocesses and are not statically linked.
