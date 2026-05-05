# User settings (current and planned)

This document is a forward-looking catalog of decisions the
project has made that are **currently hardcoded** but could
plausibly become **user-configurable settings** in the future.
It also captures the rationale for each current choice so a
future contributor designing the configuration substrate has
the context, not just the decision.

The companion working-rule for contributors is in
[`CONTRIBUTING.md`](../CONTRIBUTING.md) under "Consider
configurability when iterating": when a PR introduces a
hardcoded constant or a fixed behaviour that a user might
plausibly want to control, log it here in an appropriate
section so the catalog stays current as the project grows.

## Current state of configuration

`pty-speak` ships a **minimal TOML config substrate** introduced
in the Phase B "C2" sub-stage. The config file lives at
`%LOCALAPPDATA%\PtySpeak\config.toml` (alongside the rolling
log directory) and is parsed via
[Tomlyn](https://github.com/xoofx/Tomlyn). v1 of the schema
exposes:

- **Per-shell pathway override** — which display pathway runs
  for `cmd` / `claude` / `powershell` (see "Pathway selection"
  below).
- **Per-pathway parameter override** — debounce window,
  spinner-window, spinner threshold, and max-announce-chars for
  `StreamPathway`.

Everything else in this catalog remains a "candidate" — known
and tracked, not yet exposed. The Phase B sub-stages will keep
adding to the schema (`[[bindings]]` / `[[handlers]]` for
input-binding overrides per `spec/event-and-output-framework.md`
A.5; per-content-type triggers for Phase 2/3 semantic +
AI-interpretation pathways).

Sensible-defaults principle: the absence of `config.toml` (or
any TOML key within it) is **byte-equivalent** to the
hardcoded behaviour. A user can delete the file at any time
to revert to defaults; this catalog continues to document the
defaults so deviations are intentional.

Why incremental: adding the full configuration surface for
every candidate setting before validating which ones users
actually want to change would build a half-finished system
around the wrong choices. C2 ships only the substrate the
display-pathway architecture needs today; later sub-stages
extend the schema as user-facing demand accumulates.

## How this document is organised

Each setting category gets its own section with four parts:

1. **Current state** — what the code does today, where it
   lives.
2. **Why hardcoded now** — the rationale that made the current
   choice acceptable for the current stage of the project.
3. **What configurability would look like** — the design sketch
   for the eventual user-facing setting, including the
   contextual / per-section nuances if any.
4. **Implementation notes** — what the change touches, what
   substrate is needed, what tests would pin it.

The sections that follow are not exhaustive — they're the
ones the project has accumulated through Stage 4 + 11. New
sections get added by PRs that introduce new hardcoded
behaviour; reviewers should request a section update when
appropriate (see the "Consider configurability when iterating"
rule in CONTRIBUTING.md).

## Word boundaries

### Current state

`TerminalTextRange.IsWordSeparator` (in
`src/Terminal.Accessibility/TerminalAutomationPeer.fs`) treats
**only space (U+0020) and tab (U+0009)** as word separators.
Punctuation and symbols stay inside words. Shipped in PR #68.

Concrete consequences:

- `"C:\Users\test>"` reads as one word — fast path navigation.
- `"don't"` reads as one word — apostrophe is not a separator.
- `"--target main"` reads as `--target` and `main` — two words.
- Chinese / Japanese / Korean text without spaces would read
  as one giant word — no UAX #29 segmentation today.

### Why hardcoded now

Whitespace-only matches `bash`'s readline word motion, Windows
Terminal's default, and xterm's word selection. It's simple,
predictable, and doesn't pull in ICU. The full discussion of
alternative algorithms (vim word-chars, UAX #29, configurable
delimiters) and the rationale for picking whitespace-only as
the default lives in PR #68's description and in the source
docstring on `IsWordSeparator`.

### What configurability would look like

Three plausible levels of configurability, increasing in cost:

1. **A setting that picks a preset.** Choices: `whitespace`
   (current), `vim` (whitespace + punctuation), `uax29`
   (Unicode-aware). One-line config; `IsWordSeparator` becomes
   a function-typed mutable picked at startup.

2. **Custom delimiter list.** Like Windows Terminal's
   `wordDelimiters` setting. The user lists characters that
   count as separators. Offers per-user tuning without
   committing to a predefined preset.

3. **Per-context word boundaries with a hotkey to switch.**
   The interesting suggestion from the maintainer: different
   parts of the UI may benefit from different boundary rules.
   When reading cmd.exe paths, "treat path-as-word" is best;
   when reading code in a vim-in-terminal session,
   "split-on-punctuation" is best; when reading prose in a
   Claude Code response, UAX #29 is best. A keybinding (e.g.
   `Ctrl+Shift+W`) cycles through preset modes; NVDA announces
   "Word boundaries: vim mode" or similar; the running app
   tracks the active mode in process state and persists it
   across restart in the config file.

   Future Stage 7+ could autodetect context (Claude Code
   prompt = prose mode, vim = code mode) so the user doesn't
   have to switch manually for the common cases — but the
   manual-switch hotkey is the floor.

### Implementation notes

- `IsWordSeparator(cell: Cell): bool` is currently a static
  method. Making it a member (or a function-typed property)
  on `TerminalTextRange` lets it vary per-instance.
- The `TerminalTextProvider` constructor would take an
  additional `wordSeparator: Cell -> bool` parameter, which
  the WPF view holds and can swap when the hotkey fires.
- NVDA needs to be informed of the change: raise a
  Notification with `displayString = "Word boundaries: vim
  mode"`. The existing `TerminalView.Announce` from PR #63
  is the channel.
- Persistence: requires the Phase 2 TOML substrate. Until
  then, "preset cycle on hotkey" works in-session but doesn't
  survive restart.
- Tests: F#-side unit tests can construct `TerminalTextRange`
  with synthetic `Cell[][]` content and verify each preset's
  word boundaries against expected positions. No FlaUI
  needed.

## Visual settings

### Current state

All visual properties are hardcoded in
`src/Views/TerminalView.cs`:

- **Font family**: `"Cascadia Mono, Consolas, Courier New"`
  (constant `FontFamilyName`).
- **Font size**: `14.0` (constant `FontSize`).
- **Background**: `Brushes.Black` (field `_background`).
- **Default foreground**: `Brushes.White` (in `ResolveBrush`
  fallback).
- **ANSI 16-colour palette**: hardcoded in `Ansi16ToBrush`
  using WPF's named brushes (Red, Green, Olive, Blue, etc.).
- **Truecolor brushes**: constructed per-call from `ColorSpec.Rgb`.

`MainWindow.xaml` sets `Background="Black"` on the Window
itself; that's separate from `TerminalView`'s rendering and
not currently exposed as a setting.

### Why hardcoded now

Stage 3b's first goal was a visible terminal surface; making
it pretty / configurable was scope-creep. Cascadia Mono is the
modern Windows Terminal default and ships with Windows 11; the
Consolas / Courier New fallbacks cover Windows 10. 14pt is
readable on a 1080p display; would feel small on 4K.

### What configurability would look like

- **Font family** — string config; common need, low risk.
- **Font size** — number config; users with low vision would
  benefit greatly from per-user sizing. Should also expose a
  zoom in / zoom out hotkey (`Ctrl++` / `Ctrl+-` are standard
  in browsers and editors) that adjusts at runtime without
  needing config-file editing.
- **Colour scheme** — full palette overrides. Standard terminal
  colour schemes (Solarized, Gruvbox, One Dark, Tomorrow Night)
  are well-known sets users will expect. Should support
  per-scheme files, e.g. a `themes/solarized-dark.toml` that
  the main config references.
- **Background colour** — usually flows from the colour scheme,
  but worth a dedicated setting for users who want
  high-contrast (e.g. a hardcoded `#000000` background even
  when the scheme says otherwise).
- **Cursor style** — Block / underline / vertical bar. Not yet
  even rendered today; will become relevant once cursor blink
  semantics ship in a later stage.

For a blind user the visual settings matter less for content
consumption, but they matter for sighted collaborators
(pair-programming, presenting), and for users with low vision
who can use the terminal but need larger fonts. Worth
designing for.

### Implementation notes

- All visual settings can be applied via WPF property
  bindings; `TerminalView` would expose dependency properties
  bound to a settings object.
- Runtime change (zoom hotkey) needs `_cellWidth` / `_cellHeight`
  to be recomputed and an `InvalidateVisual()` to redraw.
- Tests: visual properties don't have automated tests beyond
  "doesn't crash on render"; manual verification per the
  matrix's Stage-3b row + new Stage-3b row for the chosen
  font / size.

## Audio (earcons + interface sounds)

### Current state

**No audio shipped yet** — Stage 9 (earcons via NAudio) is
**subsumed** into Part 3 Stage G of the
[May-2026 plan](PROJECT-PLAN-2026-05.md): earcons land as a
presentation sink the Output framework drives, with
colour-to-earcon mapping as a profile-tunable strategy. The
current state is "all the candidate settings are
deciding-now-shippable-later." Logging them here lets the
design think through configurability before shipping the
substrate so we don't bake in choices that users want to
flip.

The spec (`spec/tech-plan.md` §9) calls out specific audio
constraints already:

- Earcons are below 180 Hz or above 1.5 kHz (so they don't
  interfere with the speech band).
- ≤ 200 ms duration.
- ≤ -12 dBFS amplitude (so they're audibly subordinate to
  TTS).
- WASAPI shared mode (so they don't block other audio).
- Optional separate audio device routing for earcons vs TTS.

### Why hardcoded (in the spec) now

These constraints come from accessibility-research literature
on auditory icons / earcons in screen-reader contexts. The
defaults are evidence-based for "doesn't compete with TTS"
not arbitrary preferences. They're worth keeping as
defaults; they shouldn't be the only options.

### What configurability would look like

- **Mute toggle** (already specced in Stage 9 — `Ctrl+Shift+M`).
- **Volume** — a 0-100 setting independent of system volume.
- **Earcon style** — preset themes ("default", "minimal",
  "classic Macintosh"). Different palettes for users who find
  the default too sparse or too busy.
- **Per-event earcon override** — power user setting; assign
  specific WAV files to specific event classes (red text,
  green text, prompt detected, error detected). Phase 2
  territory.
- **Audio device routing** (already specced) — which output
  device for earcons vs TTS. Should be selectable from a
  list of installed devices, not by typing the device name.
- **Frequency / duration / amplitude tuning** — the spec
  defaults. Users with hearing loss in specific bands may
  want different frequencies. Probably power-user-only;
  default presets cover the common case.

### Implementation notes

- Audio settings need the Phase 2 TOML substrate plus a
  device-listing API (`MMDeviceEnumerator` from
  `NAudio.CoreAudioApi`).
- Runtime change to volume / mute should not require restart;
  changes to earcon style or device may.
- Tests: hard to test audio output programmatically; the
  manual smoke matrix already covers Stage 9 with NVDA-
  audible verification.

## Default shell

### Current state

Stage 7 PR-B added an extensible `ShellRegistry` in
`src/Terminal.Pty/ShellRegistry.fs`. PR-J added PowerShell as
the third built-in (the diagnostic control shell). Current
shipped set:

- **`Cmd`** — Windows Command Prompt (`cmd.exe`). The Stage 7
  default; what new users see if they haven't configured anything.
- **`PowerShell`** — Windows PowerShell (`powershell.exe`,
  always available on Windows 10+). PR-J added; selected by
  setting `PTYSPEAK_SHELL=powershell` or `=pwsh` (the `pwsh`
  alias is for users who think of PowerShell as the
  cross-platform `pwsh.exe`; both currently route to
  `powershell.exe`).
- **`Claude`** — Claude Code (`claude.exe`, resolved via
  `where.exe claude`). The maintainer's primary target workload;
  selected by setting `PTYSPEAK_SHELL=claude` (case-insensitive)
  before launching pty-speak.

Resolution lives in `compose ()` (`src/Terminal.App/Program.fs`):

1. Read `PTYSPEAK_SHELL` env var. Recognised values: `cmd`,
   `claude`, `powershell`, `pwsh` (after trim, case-insensitive).
   Unrecognised non-empty values produce a `LogWarning` and fall
   back to the default.
2. Default: `Cmd`.
3. Resolve via `ShellRegistry.tryFind`. If the resolver returns
   `Error` (e.g. Claude Code not installed), log a warning at
   `Information`-adjacent severity and fall back to `Cmd`.
4. Log the chosen shell + resolved command line at `Information`.

The Stage 7 PR-A env-scrub block from `Terminal.Pty.Native.EnvBlock`
is applied uniformly to whichever shell is selected — adding new
shells doesn't broaden the env-leak surface.

### Why hardcoded now

Stage 7's job is to validate Claude Code under NVDA, not to ship
a configuration UI. `PTYSPEAK_SHELL` is the smallest possible
selection surface: an env var any user can set in their shell rc
or via a desktop-shortcut "Run with environment" wrapper. Stage 7
PR-C added `Ctrl+Shift+1` / `Ctrl+Shift+2` hotkeys for in-session
hot-switching; PR-J added PowerShell + reordered the slots
(`Ctrl+Shift+1` = cmd, `Ctrl+Shift+2` = PowerShell,
`Ctrl+Shift+3` = Claude). Phase 2 adds the menu / palette UI
that exposes the registry to the user without requiring shell
literacy.

### What configurability would look like

Three plausible levels of configurability, increasing in cost:

1. **`PTYSPEAK_SHELL` env-var override (today's surface).**
   Recognised names map to built-in registry entries. No file I/O,
   no parsing risk, easy to revert by unsetting.

2. **Hotkey-driven hot-switch (Stage 7 PR-C, reordered + extended in PR-J).**
   `Ctrl+Shift+1` selects cmd; `Ctrl+Shift+2` selects PowerShell;
   `Ctrl+Shift+3` selects claude. The architectural lift is
   mid-session `ConPtyHost` teardown + respawn, which PR-C
   shipped. Future shells claim higher digit slots
   (`Ctrl+Shift+4`, `+5`, ... for WSL / Python REPL / bash, etc.).
   `AppReservedHotkeys` contract + spec §6 update land in PR-C
   / PR-J.

3. **Phase 2 TOML config + menu UI.** A `shells` table in the
   user-settings file lists shell-id + display-name + executable
   path + optional argument list, and a palette UI lets the user
   pick from the resolved set. Plugin-style third-party shells
   (Python, Node, custom scripts) become possible. Requires the
   Phase 2 user-settings substrate (Issue #112).

### Implementation notes

- `ShellRegistry.builtIns: Map<ShellId, Shell>` is the single source
  of truth. Adding a shell requires (a) extending the `ShellId`
  discriminated union, (b) adding an entry to `builtIns` with a
  resolver closure, (c) updating `parseEnvVar` to recognise the
  new name, and (d) updating
  `tests/Tests.Unit/ShellRegistryTests.fs ``builtIns contains exactly Cmd, Claude, and PowerShell```
  (the assertion intentionally pins the exact set so a reviewer is
  forced to acknowledge each addition).
- `tryFindIn` exists separate from `tryFind` so tests can inject
  synthetic registries without touching `builtIns`. Production code
  uses `tryFind` (which delegates to `builtIns`).
- `whereExe` is a thin `Process.Start` wrapper with a 2-second
  timeout. Not unit-tested directly; the real path is exercised in
  `docs/ACCESSIBILITY-TESTING.md`'s Stage 7 row (PR-D).
- Persistence: `PTYSPEAK_SHELL` is per-process; the user sets it in
  their shell environment or via a launcher wrapper. Future TOML
  config persists across launches.

## Pathway selection (shipped — Phase B subset, C2)

The display-pathway substrate (Phase A, PR #159) introduced the
abstraction "for each shell, which pathway translates canonical
screen state into NVDA-bound events". The substrate ships
`StreamPathway` (per-frame diff) and `TuiPathway` (alt-screen-
aware suppression). C2 makes the choice config-driven so users
can override the hardcoded mapping without rebuilding pty-speak.

### Current state

- Config file: `%LOCALAPPDATA%\PtySpeak\config.toml` (optional;
  absent → hardcoded defaults).
- Schema:

  ```toml
  schema_version = 1

  [shell.cmd]
  pathway = "stream"

  [shell.powershell]
  pathway = "stream"

  [shell.claude]
  pathway = "stream"

  [pathway.stream]
  debounce_window_ms = 200
  spinner_window_ms = 1000
  spinner_threshold = 5
  max_announce_chars = 500

  [pathway.tui]
  # reserved; no parameters today
  ```

- Defaults table:

  | Setting | Default | What it controls |
  |---|---|---|
  | `[shell.cmd] pathway` | `"stream"` | Pathway used by `cmd.exe` |
  | `[shell.powershell] pathway` | `"stream"` | Pathway used by `powershell.exe` (and `pwsh.exe` alias) |
  | `[shell.claude] pathway` | `"stream"` | Pathway used by `claude.exe` |
  | `[pathway.stream] debounce_window_ms` | `200` | Trailing-edge flush window for diff accumulation |
  | `[pathway.stream] spinner_window_ms` | `1000` | History window for spinner-suppress detection |
  | `[pathway.stream] spinner_threshold` | `5` | Row/content-hash change count to trigger suppression |
  | `[pathway.stream] max_announce_chars` | `500` | Text truncation cap for NVDA announcements |

- Available pathway IDs: `"stream"`, `"tui"`. Future Phase 2
  IDs (`"claude-code"`, `"repl"`, `"form"`) will be added
  without schema migration.
- Loader: `src/Terminal.Core/Config.fs` parses + resolves;
  `src/Terminal.App/Program.fs`'s `selectPathwayForShell`
  consults the loaded `Config` for both pathway selection and
  parameter overrides on startup, hot-switch
  (`Ctrl+Shift+1/2/3`), and alt-screen exit.

### Error semantics

The loader **never** crashes pty-speak. Every failure mode logs
via `ILogger` (which routes to FileLogger; readable via
`Ctrl+Shift+;`) and falls back to defaults. The maintainer is a
screen-reader user; GUI dialogs are explicitly out of scope.

| Condition | Log level | Effect |
|---|---|---|
| File does not exist | Information | Use all defaults |
| Malformed TOML | Error (with parse detail) | Use all defaults |
| `schema_version` missing | Warning | Treat as 1 |
| `schema_version` newer than supported | Error | Use all defaults |
| `schema_version` older | Warning | Best-effort parse |
| Unknown `[shell.<key>]` | Warning | Skip that section |
| Unknown pathway name (e.g. `"stram"`) | Warning | That shell falls to default |
| Unknown parameter key | Warning | Drop the value |
| Negative or zero parameter value | Warning ("clamped to default") | Use default for that field |

A single `Information` line on startup summarises the resolved
config (e.g. `"Config loaded: cmd→stream, claude→stream,
powershell→stream; stream debounce=200ms"`) so post-hoc
diagnosis via log capture is trivial.

### Out of scope (deferred to later sub-stages)

- Hot-reload — changes require a restart.
- Runtime config write-back from a hotkey or palette
  ("save current settings as my config"). Phase 2/3.
- Kill-switch substrate (per spec A.6). Phase B input-bindings
  owns this.
- Per-content-type triggers (regex / colour / semantic-parser /
  LLM dispatchers). Phase 2/3 territory; needs actual pathways
  that consume the triggers.
- ClaudeCodePathway / ReplPathway / FormPathway selection —
  those pathways don't exist yet (Phase 2).
- Schema additions for input-binding overrides
  (`[[bindings]]`). Separate Phase B sub-stage; coexists
  cleanly with the current schema (the loader silently
  ignores those sections).
- Config validator binary or CLI.

## Keybindings

### Current state

Three app-level keybindings shipped today (plus two reserved
for future stages — listed inline below):

- **`Ctrl+Shift+U`** — Velopack auto-update (Stage 11, PR #63).
  Wired in `setupAutoUpdateKeybinding` in
  `src/Terminal.App/Program.fs`.
- **`Ctrl+Shift+D`** — process-cleanup diagnostic launcher
  (PR #81). Spawns `scripts/test-process-cleanup.ps1` (bundled
  next to `Terminal.App.exe`) in a separate PowerShell window
  so the screen-reader user can audibly verify ConPTY child
  cleanup on app close — added because Task Manager's
  Processes-tab chevron-expand affordance isn't NVDA-accessible.
  Wired in `setupDiagnosticKeybinding`.
- **`Ctrl+Shift+R`** — "draft a new release" form launcher
  (PR #83 originally shipped this as a release-notes browser;
  flipped to the new-release form path during the post-Stage-5
  manual NVDA verification cycle when the maintainer realised
  the create-release flow is the actual daily-use path during
  the preview line). Opens the GitHub draft-release form
  (`UpdateRepoUrl` + `/releases/new`) in the user's default web
  browser. The maintainer's release flow per
  `docs/RELEASE-PROCESS.md` is to publish a release in the
  Releases UI (which creates the tag and triggers the Velopack
  build/upload workflow); this hotkey skips the navigation
  step. Wired in `setupNewReleaseKeybinding`. Note:
  `Ctrl+Shift+R` and `Alt+Shift+R` (Stage 10 review-mode toggle,
  reserved) are distinct WPF gestures — different modifier
  sets — so there is no actual keypress conflict, only a
  mnemonic similarity.
- **`Ctrl+Shift+L`** — open the logs folder
  (`%LOCALAPPDATA%\PtySpeak\logs\`) in File Explorer (Logging-PR).
  Useful for navigating into a previous session's log file when
  reporting a bug. Logs are per-session files inside per-day
  folders with 7-day retention. See [`LOGGING.md`](LOGGING.md)
  for the full description of what's logged and what isn't
  (typed input, paste content, full screen contents,
  environment variables — never). Wired in
  `setupOpenLogsKeybinding`.
- **`Ctrl+Shift+;`** — copy the active session's log file content
  to the clipboard as a single string. The semicolon / colon
  key is physically next to `L` on a US-layout keyboard, so it
  pairs with the `Ctrl+Shift+L` open-folder primary by
  proximity. NVDA announces the byte count on success ("Log
  copied to clipboard. N bytes; ready to paste."). Fastest
  path to send a session log to a maintainer. Hotkey-choice
  history: the original binding was `Ctrl+Alt+L` (Magnifier
  collision + the SystemKey filter for the Alt path broke
  `Alt+F4`); `Ctrl+Shift+C` was considered but reserved for a
  future copy-latest-command-output feature. Layout caveat:
  on non-US keyboards the `OemSemicolon` virtual-key sits in
  a different physical position. Wired in
  `setupCopyLatestLogKeybinding`.
- (planned) **`Ctrl+Shift+M`** — earcon mute toggle (Stage 9).
- (planned) **`Alt+Shift+R`** — review-mode toggle (Stage 10).

NVDA's own keybindings (NVDA+T, NVDA+arrow keys for review
cursor, NVDA+Space for browse mode, etc.) are reserved
through ConPTY input handling — Stage 6 will need to filter
NVDA-modifier keys out of the keys forwarded to the child
shell so app-level shortcuts capture before PTY routing.

### Why hardcoded now

So few keybindings exist that a config surface would be
overhead without payoff. The two shipped + the two planned
are all in the "global app commands" class that won't be
varied by user.

### What configurability would look like

- **All keybindings remappable.** Especially important for
  international users who have different keyboard layouts;
  `Ctrl+Shift+U` lands on a different physical key on AZERTY.
- **Conflict detection.** If a user binds something to a
  combination that also passes through to the PTY (e.g.
  `Ctrl+C`), the config UI should warn — Stage 6's keyboard
  pipeline needs to know which keys are app-reserved vs
  pass-through.
- **NVDA-modifier respect.** Whatever rebinding is allowed,
  the contract that NVDA's modifiers (Insert, CapsLock with
  NumLock off, etc.) are NOT swallowed must hold. The
  `CONTRIBUTING.md` PR template's accessibility checklist
  already enforces this; the config layer must too.

### Implementation notes

- WPF's `InputBindings` collection is mutable; rebinding can
  mutate it at runtime.
- A reserved-key registry should live alongside the config
  loader so the validator knows which keys are app-only,
  PTY-only, NVDA-only, and which can be reassigned.
- Tests: a keybinding-collision test in
  `tests/Tests.Unit` would assert no two app commands share
  a gesture, and no app command shadows an NVDA modifier.

## Update behaviour

### Current state

Hardcoded in `runUpdateFlow` (Program.fs):

- **Repository URL**: `"https://github.com/KyleKeane/pty-speak"`.
- **Channel**: `prerelease=true` (constant in `GithubSource`
  construction).
- **No automatic background check**: updates only happen on
  manual `Ctrl+Shift+U`.
- **Progress announcement granularity**: 25%-buckets
  (hardcoded in the progress callback).

### Why hardcoded now

The unsigned-preview line ships on a single channel; users
don't need a chooser yet. Manual-only update keeps the
behaviour predictable — no surprise restarts.

### What configurability would look like

- **Channel selection** — `stable` / `prerelease` / `nightly`.
  Once `v0.1.0` ships and there's a stable line distinct from
  preview, users will want to opt in / out of preview-quality
  updates.
- **Auto-check on startup** — opt-in. Some users will want
  the app to check for updates passively and announce
  "Update available; press Ctrl+Shift+U to install."
- **Auto-apply** — power-user setting. Some users will trust
  the channel enough to skip the manual step entirely.
- **Progress verbosity** — every percent / bucketed / silent
  through the download. Default of 25%-bucket is reasonable
  but not universal.

### Implementation notes

- Channel switching changes the `GithubSource` parameters.
- Auto-check needs a background timer; auto-apply needs a
  user-confirmation step UNLESS the user has explicitly
  opted in.
- Tests: the existing FlaUI infrastructure can simulate the
  Ctrl+Shift+U keybinding; auto-check would need a mockable
  `IUpdateSource`.

## Terminal behaviour

### Current state

Hardcoded in `Program.fs`:

- **Screen size**: `ScreenRows = 30`, `ScreenCols = 120`.
- **Default shell**: `cmd.exe` (in `PtyConfig`).
- **Working directory**: process current directory (Velopack's
  install path on installed builds).
- **Environment variables**: inherited from parent.

### Why hardcoded now

Stage 3b's goal was a working pipeline; configurable
shell / size / cwd would have been scope creep. cmd.exe is
the universal default on Windows.

### What configurability would look like

- **Shell choice** — PowerShell, pwsh, cmd, WSL bash, custom
  command line. Especially valuable since Claude Code is
  often launched via a custom command.
- **Size** — initial rows × cols. Power users with ultrawide
  displays want more columns; users on smaller displays want
  fewer rows.
- **Working directory** — start in `~`, project root, or
  custom path. Common request from developers.
- **Environment variables** — inject custom env vars. Useful
  for setting `LANG`, `TERM`, `COLORTERM`, etc.
- **Multiple profiles** — Windows Terminal-style: the user
  defines a list of named profiles (e.g. "PowerShell as
  Admin", "WSL Ubuntu", "Claude Code session") and picks
  which to launch.

### Implementation notes

- `PtyConfig` already takes the parameters as inputs; making
  them configurable is a parameter-passing change at the
  config-load site.
- Multi-profile support is a bigger design — needs a
  profile-selector UI (or hotkey) and default-profile
  setting.
- Tests: `tests/Tests.Unit/ConPtyHostTests.fs` already
  exercises the `PtyConfig` shape; a per-profile test
  fixture would mock different config inputs.

## Verbosity / NVDA narration

### Current state

Stage 5's `Coalescer` ships a single generic strategy:
`renderRows` announces the rendered screen on every emit
(functional end-to-end as of PR #116). No verbosity setting
exposes any knob to the user; the strategy itself is the
decision. The verbose-readback experience that results — NVDA
re-reading the whole screen on every keystroke for typed
input — is the **first foundational architecture decision**
addressed by the
[May-2026 plan](PROJECT-PLAN-2026-05.md)'s Output framework
cycle (Part 3), not a tuning problem inside the existing
coalescer.

### Why hardcoded now

The defaults for Stage 5 are evidence-based: "what does
NVDA need to say so the user understands what's happening
without being overwhelmed?" The Output framework cycle
treats this as a **per-profile** question (Stream / REPL /
TUI / Form / Selection) rather than a single global verbosity
slider — different content paradigms need different
presentation strategies, not different verbosity tunings of
one strategy.

### What configurability would look like

Per the May-2026 plan Part 3, configurability lands as a
**profile taxonomy** with per-profile parameters:

- **Profile selection** — automatic detection (alt-screen,
  app-name, OSC 133); per-app override; manual switch
  hotkey (`Ctrl+Alt+1..5` style).
- **Per-profile parameters** — Stream profile (suffix-diff
  vs full-screen, OSC 133 prompt awareness); REPL profile
  (prompt detection signals, output-block boundaries); TUI
  profile (review-cursor primary, alt-screen flush barrier);
  Form profile (field detection, focus model); Selection
  profile (list enumeration verbosity).
- **Per-app overrides** — `claude` always uses Form; `fzf`
  always uses Selection; `python` always uses REPL; etc.

The original three-bucket sketch (Off / Smart / Verbose) from
`spec/overview.md` Phase 2 is preserved as a power-user
override that the framework's settings UI exposes per
profile, but it's not the primary surface — profile selection
is. Other power-user knobs:

- **Per-event-class enable/disable** — e.g. "narrate stderr
  but suppress stdout"; "announce errors but skip command
  echoes". Ships through the framework's per-profile
  configuration UI.
- **Notification rate limit override** — programmer-class
  user wants every byte read aloud and knows the cost.

### Implementation notes

- Phase 2 user-settings TOML (Tomlyn) is the persistence
  surface; the Output framework cycle's settings UI (plan
  Part 3 Stage H) provides the discoverable knob.
- Verbosity overrides are per-profile, not global — the
  framework's `IOutputProfile` interface owns its own
  parameter surface.
- Tests: `tests/Tests.Unit` should include FsCheck properties
  per profile (no notification flood at any verbosity level,
  no notifications dropped silently at "verbose" level).
- The existing `Coalescer.fs` migrates into the framework's
  Stream profile during Part 3 Stage A; nothing in the
  pipeline (parser, screen, drain, peer) needs to move.

### Stopgap: announce-length cap (Stage 7-followup PR-H)

`src/Terminal.App/Program.fs`'s drain task currently caps every
`Coalescer.OutputBatch` announcement at **500 characters**.
When the announce text exceeds the cap, the head is preserved
and a footer is appended:

> `...announcement truncated; N more characters available — press Ctrl+Shift+; to copy full log.`

**This is an arbitrary stopgap**, not the architectural fix.
500 was chosen because it's roughly 15-20 seconds of NVDA
speech at default rate — the empirical 2026-05-03 NVDA pass
showed 1316 / 1347-character announces (30-45 seconds each)
making the terminal effectively unusable. 500 trades some
information loss for survivable speech-queue latency.

**Tracked in [issue #139](https://github.com/KyleKeane/pty-speak/issues/139)**
for revisiting both the threshold and whether to surface it as
a user-configurable setting (likely `audio.maxAnnounceChars`
in the Phase 2 TOML config, or a hotkey to cycle preset
thresholds).

When the Output framework cycle's Stream profile ships
(suffix-diff append-only emission), this stopgap becomes
redundant — the new emission strategy doesn't produce
1000+-character announces in the first place. Issue #139
also tracks the deletion of this cap at that ship boundary.

## Diagnostic surfaces

### `diagnostic.heartbeatIntervalMs` (currently 5000ms)

#### Current state

`src/Terminal.App/Program.fs`'s `runHeartbeat` closure is
invoked by a `System.Threading.Timer` every **5000 ms** (5
seconds) and writes a single `Information`-level log line
capturing shell name + PID + alive/dead flag (PR-J liveness
probe) + log level + reader last-byte staleness + queue
depths. Heartbeats stopping appear as a clean wedge timestamp
in the log when troubleshooting later (per `runHealthCheck`'s
contract — `Ctrl+Shift+H` reads the same fields on demand).

The 5-second cadence was chosen as a balance between
diagnostic visibility (granular enough that a wedge timestamp
narrows the suspect window to <5 seconds) and log volume (a
24-hour session at 5 seconds = 17,280 lines from the heartbeat
alone, well under FileLogger's per-session-file capacity at
default Information level).

#### Why hardcoded now

PR-F (Stage 7-followup) introduced the heartbeat to make
post-Stage-6 wedge diagnosis tractable for a screen-reader
user; the value was set to "the first plausible default." No
maintainer or external user has reported wanting a different
cadence. The Phase 2 TOML user-settings substrate doesn't exist
yet, so promoting this constant prematurely would create the
same one-off settings file tech debt the rest of this catalogue
warns against.

#### What configurability would look like

Three plausible levels of configurability, increasing in cost:

1. **Single TOML entry** (`diagnostic.heartbeatIntervalMs = 5000`)
   in the Phase 2 user-settings file. Read once at startup;
   change requires app restart. Trivial to implement.
2. **Per-session override via env var** (`PTYSPEAK_HEARTBEAT_MS`)
   for ad-hoc debugging without persisting state. Cheap;
   matches the `PTYSPEAK_SHELL` precedent.
3. **Runtime hotkey to cycle presets** (1s / 5s / 30s / off).
   Useful for live-debugging a wedge that's mid-occurrence;
   high implementation cost (state machine for cycle).

Recommended Phase 2 surface: option 1 only. Power users who
need 1-second granularity can use the env var (option 2) as a
lightweight escape hatch; option 3 is overkill given the
expected use pattern.

#### Implementation notes

- The constant is the `dueTime` + `period` argument to the
  `System.Threading.Timer` constructor in `compose ()`. Reading
  from a settings record at compose time, then passing through
  to the Timer, is the natural integration point.
- Setting the cadence to 0 or a negative value should be
  treated as "heartbeat off"; the timer is created with
  `Timeout.Infinite` to disable. Useful for noise-sensitive
  users but defeats the wedge-timestamp diagnostic — surface a
  log warning at startup if disabled.
- The `runHealthCheck` (`Ctrl+Shift+H`) fields are independent
  of the heartbeat cadence; only the periodic background log
  emit is affected.

## Process for adding a new setting

When the project moves from "hardcoded" to "user-configurable"
for a given setting, the following pattern keeps the change
focused:

1. **Substrate is shipped (Phase B C2).** TOML config via
   Tomlyn lives at `%LOCALAPPDATA%\PtySpeak\config.toml` with
   a versioned schema (`schema_version = 1`). New settings
   extend the schema in a backwards-compat way; settings that
   need a new section (e.g. `[[bindings]]` for input-binding
   overrides) inherit the same loader.
2. **Per-setting PR.** Each setting becomes its own PR rather
   than a "add 12 settings" mega-PR. Easier to review,
   easier to revert if the chosen default doesn't survive
   user testing.
3. **Default = current behaviour.** The first version of any
   setting MUST default to whatever the code does today. No
   silent behaviour change for users who don't set the
   value. The C2 `Config.defaultConfig` is the authoritative
   source of truth; new fields default to `None` and the
   resolver merges with the existing hardcoded baseline.
4. **Validate the input.** Bad config files should log clearly
   via `ILogger` (which routes to FileLogger; readable via
   `Ctrl+Shift+;`) and fall back to defaults — never crash.
   The maintainer is a screen-reader user; GUI dialogs are
   off limits. Mis-typed keys log a Warning and continue;
   structurally-broken TOML logs an Error and uses defaults
   wholesale.
5. **Document in this file.** The candidate section here gets
   moved to a "Shipped settings" section (or a separate
   `docs/SETTINGS-REFERENCE.md` once the catalog grows large
   enough to warrant a split). The candidate stays as a
   pointer if there's still more to expose ("auto-apply
   shipped; channel selection still pending").
6. **Smoke-test row.** Manual matrix gets a row asserting the
   setting actually flips behaviour visible to NVDA / the
   user.

## Reminder for contributors

When iterating on `pty-speak`, **at every PR consider whether
the change introduces a hardcoded constant or fixed behaviour
that users might want to control later.** If yes, the
contributor's options are, in order of preference:

1. **Pick the right default and ship it as a constant** (what
   we do today for nearly everything). Add or update a
   section in this document so the candidate is tracked.
2. **Make it a constant in a clearly-named module** so the
   eventual config-loader has a single binding to override.
3. **Wire a per-instance parameter** if the value varies by
   instance even before config (e.g. `PtyConfig.Cols` already
   supports this for screen size).

The thing NOT to do is add a one-off settings file or env-var
override ahead of the Phase 2 substrate — those become tech
debt the moment the proper config system lands.

The companion entry in
[`CONTRIBUTING.md`](../CONTRIBUTING.md) ("Consider
configurability when iterating") and the PR template's
checklist enforce this at review time.

## Intentionally not user-configurable

Some hardcoded values in the codebase look like config
candidates but are deliberately not exposed because the choice
is bound to external invariants (specs, library compatibility,
proven defaults from upstream implementations). Listing them
here so future contributors don't waste effort proposing config
knobs that would only introduce footguns.

### VT500 parser limits (`src/Terminal.Parser/StateMachine.fs`)

- **`MAX_INTERMEDIATES = 2`** — Maximum number of intermediate
  bytes (0x20–0x2F) collected during CSI / DCS / ESC sequences.
- **`MAX_OSC_PARAMS = 16`** — Maximum number of
  semicolon-separated OSC fields.
- **`MAX_OSC_RAW = 1024`** — Total bytes in the OSC payload
  across all params.
- **`MAX_PARAMS = 16`** — Maximum number of CSI parameters.

These caps match the alacritty / vte parser invariants. They
are deliberately fixed to maintain bytewise-equivalent dispatch
behaviour with the canonical Williams VT500 implementation
that the rest of the terminal ecosystem relies on. Making
them configurable would let a user opt into incompatible
parser behaviour with no realistic upside.

If a real-world terminal sequence ever exceeds these limits in
a way that pty-speak should handle differently from
alacritty / vte, the right fix is a parser-level investigation
and (if warranted) a coordinated change to the canonical caps,
not a per-user override.

### Earcon frequency / amplitude defaults (Stage 9, planned)

The `spec/tech-plan.md` §9 constraints (≤ 180 Hz or
≥ 1.5 kHz, ≤ 200 ms duration, ≤ -12 dBFS amplitude) are
**evidence-based defaults from accessibility research on
auditory icons in screen-reader contexts** — not arbitrary
preferences. The defaults stay defaults; the "Audio (earcons +
interface sounds)" section above describes which knobs around
them become user-configurable. The frequency / duration /
amplitude bounds themselves are not freely overridable because
violating them is known to interfere with TTS comprehension.
