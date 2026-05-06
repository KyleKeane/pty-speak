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
  color_detection = true

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
  | `[pathway.stream] color_detection` | `true` | Emit error-tone (red text) and warning-tone (yellow text) earcons alongside NVDA reading. `false` disables both earcons. |

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

## Suffix-diff parameters (PR #166 follow-up)

PR #166 (sub-row suffix-diff at StreamPathway emit) shipped
2026-05-06. Maintainer release-build validation that day
surfaced five UX observations — none of which are bugs in the
strict sense, but all of which are sensitive to: NVDA
configuration (e.g. "speak typed characters" on/off determines
whether pty-speak's per-keystroke announce is redundant),
maintainer preference (e.g. backspace silent vs. deleted-char-
announced), or voice synthesizer behaviour (e.g. interrupt-on-
new-notification cadence varies by voice).

This section captures each observation as a future parameter
following the catalogue's standard "Current state / Why
hardcoded now / What configurability would look like /
Implementation notes" pattern. The maintainer's directive
2026-05-06: don't quick-fix any of these — each one becomes a
parameter; design the substrate before any individual
behaviour gets locked in by an "expedient" implementation.

### Pipeline-stage glossary

The 12-stage pipeline glossary proposed in PR #166's
StreamPathway plan (and informing the future
[`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md), item 19 in
the strategic backlog). Used below to anchor each parameter
to a specific stage.

| # | Stage | Module | Role |
|---|---|---|---|
| 1 | Byte ingestion | `Terminal.Pty.ConPtyHost` | Bytes arrive from child shell's stdout |
| 2 | Parser application | `Terminal.Parser.VtParser` + `Terminal.Core.Screen` | Bytes → SGR/CSI/text → cell-grid mutations |
| 3 | Notification emission | `Terminal.Core.Screen` | RowsChanged, ModeChanged, Bell etc. |
| 4 | Canonical-state synthesis | `Terminal.Core.CanonicalState` | Snapshot + sequence + row hashes |
| 5 | Frame-dedup | `StreamPathway` (`LastFrameHash`) | Identical frames are no-ops |
| 6 | Spinner suppression | `StreamPathway` (`isSpinnerSuppressed`) | Repeated rotating-character patterns suppressed |
| 7 | Row-level diff | `CanonicalState.computeDiff` | Which rows changed + their concatenated text |
| 8 | Sub-row suffix detection | `StreamPathway` (`computeRowSuffixDelta`, PR #166) | Per row, what's new beyond previous emit's text |
| 8b | Bulk-change fallback | `StreamPathway` (heuristic, PR #166) | When too many rows changed at once, bypass suffix detection |
| 9 | Announcement payload assembly | `StreamPathway` (`assembleSuffixPayload`, `capAnnounce`) | Combine per-row suffixes into single string; cap length |
| 10 | Profile claim | `OutputDispatcher` | Profiles inspect events, emit ChannelDecisions |
| 11 | Channel rendering | `OutputDispatcher` → channels | Each channel's `Send` receives event + render |
| 12 | NVDA dispatch | `PtySpeak.Views.TerminalView.Announce` | UIA Notification fires; NVDA reads it |

Auxiliary stages: **Mode-barrier handling**
(`StreamPathway.onModeBarrier`, fired between stage 4 and 5
on shell-switch / alt-screen transitions) and **Earcon
synthesis** (`Terminal.Audio.EarconPlayer`, consumed at
stage 11).

### `stream.suffixDiff.bulkChangeThreshold` (currently 3)

#### Current state

`src/Terminal.Core/StreamPathway.fs:364` defines
`BulkChangeThreshold` as a top-level `let private = 3`
binding. Stage 8b applies it: when the row-level diff reports
more than 3 changed rows in a single frame, the suffix-diff
stage bypasses per-row LCP and emits the full
`ChangedText` (the pre-PR-#166 verbose path).

#### Why hardcoded now

3 is conservative: typing produces 1 changed row; Enter
usually produces 1-2; small pastes 2-3 — they all stay on the
suffix-diff path. Scrolls affect ~30 rows; screen clears all
of them — both engage the fallback. The 3 was chosen during
PR #166 design without dogfood evidence; the maintainer may
prefer a different value once they have lived experience.

#### What configurability would look like

```toml
[stream.suffix_diff]
bulk_change_threshold = 3
```

Lower values make verbose-fallback engage sooner (more
announces on small bursts, less risk on shell-side prompt
redraws); higher values keep more cases on the suffix path
(cheaper announcements but riskier under fish autosuggestions
or Powerline-style async prompt updates).

Implementation tier: **ready to implement** (existing TOML
loader pattern). Single field on
`StreamPathway.Parameters` + key in `StreamParameterOverrides`
+ loader in `Config.resolveStreamParameters`. ~30 LOC + tests.

#### Implementation notes

- Add to `StreamPathway.Parameters` record alongside
  `DebounceWindowMs` etc.
- Schema validation: `1 ≤ x ≤ 30` (must fit within total
  rows; below 1 makes no sense).
- Default stays 3; document the trade-off in
  `config.toml.sample`.
- Tests: a small bulk-change PR could include a property
  test — `assembleSuffixPayload` falls back to `ChangedText`
  iff `diff.ChangedRows.Length > threshold`.

### `stream.suffixDiff.backspacePolicy` (currently silent)

#### Current state

`src/Terminal.Core/StreamPathway.fs:412-413`
(`computeRowSuffixDelta`) returns `Silent` when the row
shrinks (`currentText.Length < previousText.Length`). Captures
backspace + line-clear cases. The PR-#166 plan's "Default 1"
chose this on the assumption that NVDA's "speak typed
characters" setting would handle backspace audibility.

**This assumption was wrong.** Maintainer release-build
validation 2026-05-06 confirmed: NVDA's keyboard echo speaks
the *key pressed* (not the screen content change), so
"Backspace" by default is silent. Even with NVDA's speak-
typed-characters enabled, backspace produces no audible
feedback from either NVDA or pty-speak. **UX issue #2 from
2026-05-06**.

#### Why hardcoded now

Hardcoding the behaviour was a Day-1-of-suffix-diff choice;
the parameter substrate didn't exist. Now that the
correctness gap is confirmed, the right shape is to expose
the policy and change the default rather than continue
relying on the broken assumption.

#### What configurability would look like

```toml
[stream.suffix_diff]
backspace_policy = "announce_deleted_character"
```

Values:

- `"silent"` — current behaviour. Useful only if NVDA gains
  a "speak deletions" setting in the future.
- `"announce_deleted_character"` — the proposed new default.
  Row shrink emits the character that disappeared (computed
  by comparing the new row's tail to the cached previous-emit
  text). User hears `i` after backspacing the `i` from
  `echo hi`.
- `"announce_deleted_word"` — coarser granularity for users
  who use Ctrl+Backspace / equivalent. Implementation needs
  word-boundary detection at the `previousText` tail.

Implementation tier: **ready to implement**. Small enum +
branch in `computeRowSuffixDelta`'s Shrink case. The deleted
content is `previousText.Substring(longestCommonPrefixLength
currentText previousText)` — already computed adjacently.

#### Implementation notes

- Add `BackspacePolicy = Silent | AnnounceDeletedCharacter |
  AnnounceDeletedWord` discriminated union.
- The `computeRowSuffixDelta` returns
  `Suffix deletedSuffix` rather than `Silent` when policy
  is `AnnounceDeletedCharacter`. Note this changes the
  semantic of `Suffix` slightly: it can now mean "deleted
  content was X" rather than "new content is X". Consider a
  future shape `EditDelta = AppendSuffix of string |
  DeletedSuffix of string | Silent` if telemetry / debug
  logging needs the distinction.
- Default ships as `AnnounceDeletedCharacter`. Default
  `Silent` mode preserved as opt-in.
- Tests: extend the `computeRowSuffixDelta` unit tests to
  pin both default and silent-mode behaviour.

#### Known v1.1 limitation: rapid backspace + retype is silenced (debounce-collapsing)

**Status as of PR #169 (2026-05-06)**: this limitation is
inherent to the LCP-based suffix-diff design when combined
with the debounce window. Documented here so future PRs
don't regress without authorisation.

**The scenario**: user types `echo hi`, then quickly
backspaces twice and types ` X` — final state is `echo X`.
All within the 200ms debounce window. The leading-edge
emit fires once per debounce window; the trailing-edge
processes accumulated state.

**Why backspace is silenced under `AnnounceDeletedCharacter`**:
the diff at trailing-edge time compares `current` ("echo X",
6 chars) against `previousText` ("echo hi", 7 chars). Since
`current.Length < previousText.Length`, the function would
enter the Shrink branch and emit the deleted segment. So
single backspace (without retype) DOES work.

But: **rapid backspace + retype** like the example above
produces a final `current` length that may equal or exceed
`previousText` length. In the example, "echo X" is only one
char shorter — but with 2 backspaces + 2 retyped chars, the
final lengths could be equal. When `current.Length >=
previousText.Length`, the function falls through to the
Append/Replace branch:

- LCP("echo X", "echo hi") = 5 ("echo " — the trailing space
  matches)
- Suffix = "X"

The deleted "hi" is **silenced** because the LCP-based diff
doesn't surface it. The user hears "X" (the retyped content)
but no audible signal that "hi" was deleted.

**This is the v1.1 debounce-collapsing limitation**: any
backspace event that gets followed within 200ms by enough
retyping to bring `current.Length >= previousText.Length` is
absorbed by the cumulative leading-edge / trailing-edge
emit.

**Workarounds**:

1. **Pause after backspace before retyping** — if the user
   pauses for more than 200ms after backspace, the trailing-
   edge emits the Shrink case (deleted segment) before the
   retyping arrives. The retyping then becomes its own
   emit. Both events surface audibly.
2. **`backspace_policy = "silent"`** for users who only ever
   backspace + retype quickly — the silent mode is honest
   about not announcing the operation, rather than silently
   dropping the deletion's audible signal in favour of the
   retyping content.

**Real fix**: the **Phase 2 input framework's echo correlation**
solves this structurally. With keystroke tracking,
pty-speak can detect "the user pressed Backspace at time T"
as an explicit event, independent of the screen mutation
that follows. The deletion event surfaces audibly even when
collapsed into the same debounce window as a retype. See
the `stream.echoCorrelation.policy` entry below for the
substrate.

### `stream.echoCorrelation.policy` (not yet implemented)

#### Current state

Not implemented. Today, every screen mutation that produces a
suffix is announced — including the per-keystroke echo of the
user's own typing. **UX issue #1 from 2026-05-06**: when NVDA
has "speak typed characters" enabled, the user hears each
character twice (once from NVDA's keypress echo, once from
pty-speak's screen-mutation echo). Fast typing masks this via
NVDA's interrupt-on-new-notification behaviour, but slow
typing produces audible doubles.

The right substrate doesn't exist yet: pty-speak doesn't
track outgoing keystrokes (`KeyEncoding` produces the bytes,
sends them to PTY stdin, then forgets). To correlate "this
screen mutation is an echo of a keystroke I just sent" we
need a small in-memory ring of recent keystrokes that the
StreamPathway can consult.

#### Why hardcoded now

Echo correlation is one of the headline features of the
forthcoming **Phase 2 input framework cycle** (per the May-
2026 plan, Part 4). Implementing it as a one-off without the
input framework would lock in a narrow design (single-char
correlation only, no cursor awareness, no per-shell echo
policies); the framework cycle is the right venue.

#### What configurability would look like

```toml
[stream.echo_correlation]
policy = "track_keystrokes"
keystroke_window_ms = 500
```

Values:

- `"none"` — current behaviour. Recommended only when NVDA
  keyboard echo is OFF.
- `"suppress_single_char_match"` — stopgap variant: when the
  suffix is exactly one character and matches the most-recent
  keystroke, suppress. Simple to implement (no correlation
  ring needed; just compare suffix length 1 + last byte).
  Doesn't handle multi-char (paste, autocomplete prefix).
- `"track_keystrokes"` — full correlation. Track the last
  N keystrokes (within `keystroke_window_ms`); if the
  cumulative new content matches a prefix of recent
  keystrokes, suppress the emit. Handles
  single-char + multi-char + shell-side echo modifications
  (uppercase / lowercase / autocompletion).

`keystroke_window_ms = 500` is the time horizon for "recent"
keystrokes; longer windows catch more correlations but risk
suppressing genuine shell-output that happens to match.

Implementation tier: **Phase 2 input framework prerequisite**.

#### Implementation notes

- New module `Terminal.Core.KeystrokeTracker` (or live in
  the input framework substrate when it ships) maintains a
  ring buffer of `(byte[], DateTimeOffset)` pairs.
- `KeyEncoding` (or its successor) calls
  `KeystrokeTracker.record` on every encoded keystroke.
- `StreamPathway.computeRowSuffixDelta` consumes the
  tracker (passed via Parameters) when policy is
  `TrackKeystrokes`. Suppress when match found.
- Per-shell consideration: some shells modify echo (e.g.
  passwords echo as `*`, fish autosuggestions paint grey
  text the user didn't type). The correlation needs to
  handle these edge cases — likely as separate per-pathway
  policies once ReplPathway / shell-aware pathways ship.

### `stream.cursorAnnouncement.enabled` (not yet implemented)

#### Current state

Not implemented. Cursor-only mutations (arrow keys, no row
text change) produce no `RowsChanged` notification, so the
current pathway emits nothing. **UX issue #3 from 2026-05-06**:
arrow-key cursor movement is silent, so the user has no
audible feedback when navigating within a typed command.

The right substrate is **cursor-aware diff**: track cursor
position; emit on cursor-only changes (typically "the
character at the new cursor position" — matches NVDA's
caret-tracking model in document content). PR #166
deliberately deferred this to v2.

#### Why hardcoded now

Cursor-aware diff requires threading cursor position
(`Screen.Cursor`) through the pathway. Stream pathway today
operates on row text only — cursor is a separate mutable
property of `Screen` that's not part of `CanonicalState`.
The Phase 2 ReplPathway (which is the natural consumer of
cursor awareness for prompt-buffer editing) is the venue.

#### What configurability would look like

```toml
[stream.cursor_announcement]
enabled = false  # default false for stream pathway
mode = "character_at_cursor"
```

Values for `mode`:

- `"character_at_cursor"` — announce the character now at
  the cursor's column. Arrow-right past `e` announces `c`
  (the new under-cursor char).
- `"column_position"` — announce the new column number.
  Useful for layout-sensitive editing.
- `"word_at_cursor"` — announce the word containing the
  cursor — closer to NVDA's caret-tracking model.

Default `enabled = false` for StreamPathway because
shell-style typing usually doesn't need cursor announcements
(the shell echoes back the resulting state, which the user
hears via the suffix-diff pipeline). When ReplPathway ships,
its default would be `enabled = true` — REPL prompts ARE
navigable documents.

Implementation tier: **Phase 2 ReplPathway prerequisite**.

#### Implementation notes

- Cursor data lives on `Screen.Cursor` (mutable property,
  not in `CanonicalState`). Propagating to pathways needs
  either: (a) including cursor in `Canonical` record, or
  (b) per-pathway resolver function (similar to PR #165's
  `resolveSeq` pattern in the diagnostic battery).
- `computeRowSuffixDelta` becomes cursor-aware: when cursor
  position changed but row content didn't, decide based on
  `mode` what to emit.
- Per-pathway default rather than global default (Stream
  vs Repl have different needs).

### `notification.activityIds.inputEcho` and `notification.processing.inputEcho` (not yet implemented)

#### Current state

All `StreamChunk` events route through a single ActivityId:
`pty-speak.output` (defined at
`src/Terminal.Core/Types.fs:299`,
`src/Terminal.Core/NvdaChannel.fs:semanticToActivityId`).
NVDA processes this ActivityId with `NotificationProcessing.ImportantAll`
— meaning new announcements interrupt the current speech
mid-word.

**UX issue #4 from 2026-05-06**: after running `dir`, the
DIR output is queued through `pty-speak.output`. As the
maintainer types, each keystroke fires a new
`pty-speak.output` event with `ImportantAll` processing,
interrupting NVDA mid-word. The result: stuttering speech as
typing interrupts the DIR output's read-back.

#### Why hardcoded now

The current single-ActivityId routing was the simplest design
in Stage 8a — uniform processing for all stream output.
Splitting input echo into a separate ActivityId requires
**classifying** each emit as input-echo vs not, which depends
on the Phase 2 echo-correlation substrate.

#### What configurability would look like

```toml
[notification.activity_ids]
output = "pty-speak.output"
input_echo = "pty-speak.input-echo"

[notification.processing]
output = "important_all"
input_echo = "most_recent"
```

`NotificationProcessing` values map to UIA's enum:

- `"important_all"` — process all without dropping; higher
  priority; interrupts current speech.
- `"all"` — process all without dropping; lower priority;
  queues behind current.
- `"most_recent"` — drop older same-activity announcements;
  only the latest survives.
- `"current_then_most_recent"` — keep current playing; then
  drop old + queue most recent.

For input-echo specifically, `"most_recent"` means: while
NVDA is busy reading something else (e.g. DIR output), per-
keystroke echoes get superseded by the cumulative latest
character. Smoother cadence; loses fine-grained per-keystroke
fidelity.

Implementation tier: **Phase 2 input framework prerequisite**
(needs the input-vs-output classifier).

#### Implementation notes

- StreamPathway emits classify each `OutputEvent` with a
  routing hint (e.g. `RoutingHint.InputEcho` /
  `RoutingHint.Output`) based on echo-correlation result.
- `NvdaChannel.semanticToActivityId` becomes
  `eventToActivityId` (consumes routing hint).
- WPF-side `TerminalView.Announce` reads the per-ActivityId
  `NotificationProcessing` from a settings record (currently
  hardcoded to `ImportantAll`).
- This work pairs naturally with echo correlation — both
  ship in the same framework cycle PRs.

### `modeBarrier.flushPolicy` (currently verbose)

#### Current state

`src/Terminal.Core/StreamPathway.fs:741-742`
(`onModeBarrier`) emits `diff.ChangedText` (the previous
shell's full screen) on every mode barrier — shell-switch,
alt-screen toggle, vim exit, etc. **UX issue #5 from
2026-05-06**: switching shells with Ctrl+Shift+1/2/3 reads
the previous shell's content as a flush before the new
shell's startup. The maintainer's logs show ~1200-character
flushes (entire PowerShell history including diagnostic
commands).

This is item 23 + item 24 in the strategic backlog.

#### Why hardcoded now

PR #166's plan deliberately preserved the verbose flush at
mode barriers because barriers are discontinuities — full
context felt safer than per-row suffix-diff against a
soon-to-be-stale baseline. Confirmed unpleasant in dogfood.

#### What configurability would look like

```toml
[mode_barrier]
flush_policy = "summary_only"
shell_switch_announce_pre = "Switching to {shell}"
shell_switch_announce_post = "Switched to {shell}"
```

Values for `flush_policy`:

- `"verbose"` — current behaviour. Emit
  `diff.ChangedText` for the previous shell's screen on
  barrier.
- `"summary_only"` — proposed default. Suppress the
  previous-shell flush; rely on the "switching to X"
  announce + the new shell's startup output.
- `"suppressed"` — silent on barrier; the new shell's
  startup output is the user's only signal that anything
  changed.

`shell_switch_announce_pre/post` are the announcement
templates with `{shell}` substituting the new shell's
display name. Today these are hardcoded in the
`Program.fs` shell-switch handler.

Implementation tier: **ready to implement**. Small branch
in `onModeBarrier`. Templates live in a new
`ModeBarrierParameters` record.

#### Implementation notes

- The proposed `"summary_only"` default subsumes both items
  23 and 24 from the strategic backlog (stale-red-on-flush
  + 1200-char-flush both go away).
- The barrier handler still clears `LastEmittedRowText`
  and resets the row-hash baseline (existing semantics).
- Test: extend
  `tests/Tests.Unit/StreamPathwayTests.fs:onModeBarrier`
  fixture to pin the `summary_only` policy emits no
  StreamChunk.

### NVDA-collaboration matrix

For each of the parameters above, the NVDA setting it
interacts with and the recommended value at each pty-speak
default. Used to design sensible defaults and to flag user-
config combinations that produce surprising behaviour.

| pty-speak parameter | NVDA setting | Interaction | Recommended pty-speak value |
|---|---|---|---|
| `stream.echoCorrelation.policy` | "Speak typed characters" (Preferences → Speech → Echo) | If NVDA echoes typed chars, pty-speak's per-keystroke emit is redundant. | `"track_keystrokes"` once shipped (smart suppression), `"none"` if NVDA echo is off |
| `stream.suffixDiff.backspacePolicy` | "Speak typed characters" (does NOT cover backspace by default) | NVDA's typed-character echo speaks the key pressed, not screen content changes. Backspace produces no NVDA announcement. | `"announce_deleted_character"` regardless of NVDA setting |
| `notification.processing.output` | NVDA's per-ActivityId rules + voice rate | Higher voice rates handle `"important_all"` interruptions; slower voices benefit from `"all"` (queued). | `"important_all"` for fast voices; `"all"` for slow voices |
| `notification.processing.inputEcho` | "Speak typed characters" + voice rate | When NVDA echo is on, this layer is suppressed by echo correlation; this parameter mainly matters when correlation is off. | `"most_recent"` |
| `modeBarrier.flushPolicy` | None directly | Independent of NVDA settings. | `"summary_only"` regardless |
| `stream.cursorAnnouncement.enabled` | NVDA's caret-tracking model | NVDA tracks the caret in document content but not in terminal content. pty-speak fills the gap when this is on. | `false` for shell typing; `true` for REPL/editor pathways |
| `stream.suffixDiff.bulkChangeThreshold` | None directly | Indirect via cumulative-payload pacing under `notification.processing`. | Default 3; tune higher for users who prefer suffix-diff over verbose-fallback on small bursts |

**Detection of NVDA settings (future)**: pty-speak does not
currently read NVDA's config file. A future enhancement could
detect NVDA's "speak typed characters" setting on launch and
adjust echo-correlation defaults automatically. NVDA's
`%APPDATA%\nvda\nvda.ini` is INI-format and pure-read; no
NVDA-side changes needed.

### Pipeline-stage cross-reference

For each pipeline stage, the parameters that affect it.
Helps a reader find "all the knobs on stage X" in one place.

| Stage | Parameters affecting this stage |
|---|---|
| 5 (Frame-dedup) | `stream.debounceWindowMs` (existing) |
| 6 (Spinner suppression) | `stream.spinnerWindowMs`, `stream.spinnerThreshold` (existing) |
| 7 (Row-level diff) | (none — pure) |
| 8 (Sub-row suffix detection) | `stream.suffixDiff.backspacePolicy`, `stream.suffixDiff.attributeOnlyPolicy`, `stream.echoCorrelation.*`, `stream.cursorAnnouncement.*` |
| 8b (Bulk-change fallback) | `stream.suffixDiff.bulkChangeThreshold` |
| 9 (Announcement payload assembly) | `stream.maxAnnounceChars` (existing, with stopgap status) |
| 10 (Profile claim) | `stream.colorDetection` (existing), `parserError.priority` (today's hard-coded `Background`) |
| 11 (Channel rendering) | `notification.activityIds.*`, `earcon.palette.*`, `earcon.muted` |
| 12 (NVDA dispatch) | `notification.processing.*` |
| Mode-barrier handler | `modeBarrier.flushPolicy`, `modeBarrier.shellSwitchAnnounce*` |
| Diagnostic battery | `diagnostic.heartbeatIntervalMs` (existing), `diagnostic.pollMs`, `diagnostic.quiescenceMs`, `diagnostic.timeoutMs` |

### Implementation precedence

Not every parameter ships at once. Tiers below sort by
substrate readiness so the maintainer can pick where to spend
engineering effort.

#### Tier 1 — ready to implement (existing TOML loader pattern)

Need only: new field on `Parameters` record + TOML key in
`StreamParameterOverrides` + loader logic. No new substrate.
~30-50 LOC per parameter + tests.

| Parameter | Effort |
|---|---|
| `stream.suffixDiff.bulkChangeThreshold` | Trivial — already a `let private` constant |
| `stream.suffixDiff.backspacePolicy` | Small — one extra branch in `computeRowSuffixDelta` |
| `stream.suffixDiff.attributeOnlyPolicy` | Small — bundle with backspace policy PR |
| `modeBarrier.flushPolicy` | Small — branch in `onModeBarrier` |
| `modeBarrier.shellSwitchAnnounce*` (templates) | Small — bundle with mode-barrier PR |
| `notification.processing.*` (per-ActivityId enum) | Medium — needs WPF-side wire-up + UIA `NotificationProcessing` enum mapping |
| `earcon.palette.*` overrides | Small — extends EarconPalette load path |
| `earcon.muted` | Already planned for Stage 9 (Ctrl+Shift+M) — make persistent |

#### Tier 2 — Phase 2 input framework prerequisites

Need new substrate (input-byte tracking, cursor-aware diff,
input/output event taxonomy). Phase 2 is the venue.

| Parameter | Substrate needed |
|---|---|
| `stream.echoCorrelation.policy` | Keystroke tracking from `KeyEncoding` to StreamPathway |
| `stream.echoCorrelation.keystrokeWindowMs` | Same |
| `stream.cursorAnnouncement.enabled` | Cursor-aware diff (cursor-position threading) |
| `stream.cursorAnnouncement.mode` | Same |
| `notification.activityIds.inputEcho` | Echo classification (depends on echo correlation) |
| `notification.processing.inputEcho` | Same |

#### Tier 3 — refinement / future

Lower priority; implement when need surfaces.

| Parameter | Reason |
|---|---|
| `parserError.priority` (currently set but unused) | Depends on Profile.Priority awareness landing in Phase 2 or beyond |
| `diagnostic.*` | Diagnostic is a tool, not user-facing; tunable via code if needed |
| Per-shell parameter overrides (e.g. different `bulkChangeThreshold` for cmd vs PowerShell vs claude) | Atlas-only design space; not implemented in v1 era |
| TOML hot-reload (today loader runs once at composition; future could watch the file) | Nice-to-have |
| Per-voice notification-processing profiles (reads voice name from NVDA, picks profile) | Speculation; depends on user demand |
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

