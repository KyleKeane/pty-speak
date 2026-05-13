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

- **Per-shell verbosity / prompt-path overrides** (Cycle 45f) —
  `verbosity = "tuple_final" | "line_by_line" | "off"` and
  `prompt_path = "suppress" | "final_dir_only" | "full"` under
  `[shell.<id>]`. Runtime-togglable via `View → Output Verbosity`
  / `View → Prompt Path`. Layer 3 (menu) overlays Layer 2 (TOML)
  overlays Layer 1 (compiled defaults in
  `src/Terminal.Core/ShellPolicy.fs`).
- **Per-shell profile-set override** (Cycle 38b) —
  `profiles = ["passthrough", "earcon", "selection"]` under
  `[shell.<id>]`. Composition-root default applies when absent.

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

`ContentHistoryTextRange.IsWordSep` (in
`src/Terminal.Accessibility/ContentHistoryTextRange.fs`) treats
**only space (U+0020) and tab (U+0009)** as word separators.
Newline (`\n`) is a separator too but is handled at line
boundaries, not as part of `IsWordSep`'s scalar test (it
appears in `IsWordOrNewline` which the word-boundary scanners
use). Punctuation and symbols stay inside words. Originally
shipped in PR #68 on the pre-Cycle-46 `TerminalTextRange`;
Cycle 46 PR-B reimplemented the same semantics on the new
substrate; PR-D deleted the screen-grid types.

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

- `IsWordSep(c: char): bool` is currently a static method on
  `ContentHistoryTextRange`. Making it a member (or a
  function-typed property) on the type lets it vary per-
  instance. (Cycle 46 PR-D collapsed the pre-PR-B
  cell-based `TerminalTextRange.IsWordSeparator(Cell)` into
  the char-based form; the contract is identical for ASCII
  but the new signature drops the `Cell` dependency.)
- The `ContentHistoryTextProvider` constructor would take
  an additional `wordSeparator: char -> bool` parameter,
  which the WPF view holds and can swap when the hotkey
  fires.
- NVDA needs to be informed of the change: raise a
  Notification with `displayString = "Word boundaries: vim
  mode"`. The existing `TerminalView.Announce` from PR #63
  is the channel.
- Persistence: requires the Phase 2 TOML substrate. Until
  then, "preset cycle on hotkey" works in-session but doesn't
  survive restart.
- Tests: F#-side unit tests can construct `ContentHistoryTextRange`
  with synthetic strings (the post-Cycle-46 substrate is
  string-shaped, not `Cell[][]`-shaped) and verify each
  preset's word boundaries against expected offsets.
  `tests/Tests.Unit/ContentHistoryTextRangeTests.fs` is the
  current pinning surface. No FlaUI needed.

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
[May-2026 plan](PROJECT-PLAN-2026-05-12.md): earcons land as a
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

1. **Cycle 19** — Read `[startup] default_shell` from
   `%LOCALAPPDATA%\PtySpeak\config.toml`. When set to a
   recognised shell id (`cmd` / `claude` / `powershell` /
   `pwsh`; case-insensitive; validated against
   `knownShellKeys`), this override wins over the env var.
   Unrecognised values produce a `LogWarning` at parse time
   and the override falls through.
2. Read `PTYSPEAK_SHELL` env var. Recognised values: `cmd`,
   `claude`, `powershell`, `pwsh` (after trim, case-insensitive).
   Unrecognised non-empty values produce a `LogWarning` and fall
   back to the default.
3. Default: `Cmd`.
4. Resolve via `ShellRegistry.tryFind`. If the resolver returns
   `Error` (e.g. Claude Code not installed), log a warning at
   `Information`-adjacent severity and fall back to `Cmd`.
5. Log the chosen shell + resolved command line at `Information`.

**Cycle 19 use case**: maintainer has `PTYSPEAK_SHELL=claude`
set from prior testing + wants `cmd` as the durable default
without manipulating env vars. Setting
`[startup] default_shell = "cmd"` in the TOML solves this
without changing system state.

**Schema snippet**:

```toml
[startup]
default_shell = "cmd"   # cmd / powershell / pwsh / claude
```

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

## Pathway / substrate selection (retired Cycle 45c)

The pre-Cycle-45 `[pathway.stream]` + `[shell.<id>] pathway` +
`substrate_mode` TOML knobs were retired across PRs #275 + #278
(Cycle 45c, 2026-05-12). The pathway-selection abstraction
they configured (`StreamPathway` / `TuiPathway` /
`PathwaySelector` / `LinearTextStream` substrate-mode dispatch)
is gone. `ContentHistory` + `SpeechCursor` is the sole aural
substrate; pathway choice is no longer user-configurable.

Old `config.toml` files that still carry `[pathway.stream]`,
`[shell.<id>] pathway`, or `substrate_mode` keys parse
silently — the loader's unknown-section tolerance treats them
as no-ops. No migration step is required; the entries simply
have no effect.

**Per-shell knobs that did survive Cycle 45c** live under
`[shell.<id>]`:

- `verbosity` — `tuple_final` / `line_by_line` / `off` (Cycle
  45f; runtime-togglable via `View → Output Verbosity`)
- `prompt_path` — `suppress` / `final_dir_only` / `full` (Cycle
  45f; runtime-togglable via `View → Prompt Path`)
- `profiles = [...]` — per-shell profile-set override (Cycle 38b)

Implementation pointer: `src/Terminal.Core/ShellPolicy.fs`
holds the per-shell policy record + defaults table;
`Config.resolveShellPolicy` overlays TOML on the compiled
defaults; menu picks layer a per-shell runtime override on
top.

For the pre-Cycle-45 details on what each retired key did, see
the archived `docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md`
and the prior revision of this file
(`docs/archive/pre-cycle-45/USER-SETTINGS-pre-cycle-45c.md`
if a snapshot was captured; otherwise reconstruct from git
history).

## SessionModel persistence (substrate shipped — Cycle 24a)

The SessionModel substrate (PRs #185–#199, "Cycles 11–22b")
populates `History` with completed `SessionTuple` values for
cmd / PowerShell sessions. **Cycle 24a** introduces the TOML
schema for opting `History` into disk persistence; the actual
file writer ships in **Cycle 24c**, and **Cycle 24d** adds
synchronous-flush + secrets sanitisation for the audit-grade
`always` mode.

### Current state

- Config file: `%LOCALAPPDATA%\PtySpeak\config.toml` (same file
  the pathway-selection schema reads).
- Schema:

  ```toml
  [session_model.persistence]
  mode = "memory_only"        # memory_only / session_log / always
  output_dir = ""              # empty → default %LOCALAPPDATA%\PtySpeak\sessions\
  format = "jsonl"             # jsonl (only value today)
  max_session_size_mb = 64
  ```

- Defaults table:

  | Setting | Default | What it controls |
  |---|---|---|
  | `[session_model.persistence] mode` | `"memory_only"` | When (and whether) tuples are flushed to disk. `memory_only` keeps `History` in RAM only; `session_log` flushes each tuple on Active→History; `always` flushes synchronously (audit-grade durability, blocks the transition). |
  | `[session_model.persistence] output_dir` | `""` (resolves to `%LOCALAPPDATA%\PtySpeak\sessions\`) | Directory holding `session-<SessionId>.jsonl` files. |
  | `[session_model.persistence] format` | `"jsonl"` | Wire format. Single value today; reserved as a DU so future binary / sqlite backends can land without a schema bump. |
  | `[session_model.persistence] max_session_size_mb` | `64` | Per-session-file size cap before rotation (rotation logic ships with the writer in Cycle 24c). |

- Loader: `src/Terminal.Core/SessionPersistence.fs` parses the
  table; `src/Terminal.Core/Config.fs` invokes it from the
  same `tryLoad` flow as `[startup]` and `[pathway.stream]`.
- Composition root: `src/Terminal.App/Program.fs` logs the
  resolved mode at startup at `Information` level (one line:
  `SessionModel persistence mode: memory_only (output_dir=<default>, format=jsonl, max_session_size_mb=64)`)
  so post-hoc diagnosis via `Ctrl+Shift+;` log capture confirms
  the user's TOML actually took effect.

### Why hardcoded now

`memory_only` is the privacy-by-default choice that protects a
brand-new install from accidentally writing prompt content +
command output to disk. Users who want audit-grade durability
opt in explicitly via TOML. The TOML schema lands first
(Cycle 24a) so Cycles 24b–d can wire the JSONL serializer +
file writer + sanitiser against a stable config surface.

### Error semantics

The loader **never** crashes pty-speak — it mirrors
`Config.parseStartupOverrides`'s warn-and-fall-back pattern.

| Condition | Log level | Effect |
|---|---|---|
| Missing `[session_model]` or `[session_model.persistence]` table | (silent) | Use defaults |
| Unknown key under `[session_model.persistence]` | Warning | Drop the value |
| Unknown `mode` value (e.g. `"verbose"`) | Warning | Fall back to `memory_only` |
| Non-string `mode` value | Warning | Fall back to `memory_only` |
| Unknown `format` value | Warning | Fall back to `jsonl` |
| Empty `output_dir` string | (silent) | Treated as "no override" |
| Non-positive / out-of-range `max_session_size_mb` | Warning | Fall back to `64` |

### Out of scope (deferred to later sub-cycles)

- **Cycle 24b** — pure JSONL serializer
  (`formatTupleAsJsonl : SessionTuple -> string`).
- **Cycle 24c** — bounded-channel async file writer +
  `memory_only` / `session_log` modes wired against the
  Active→History transition seam at `Program.fs`'s shell-switch
  / SessionModel-recreate site.
- **Cycle 24d** — `always` mode (synchronous flush) + secrets
  sanitisation via env-var deny-list.
- **Cycle 24e** — NVDA matrix rows for the "verify persistence
  is on" flow + diagnostic helpers.
- **Cycle 25+** — read-back / query API per
  `docs/SESSION-MODEL.md` §7 and the `pty-speak-replay` CLI per
  `SESSION-MODEL.md` Tier 6.

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
- **`Ctrl+Shift+Y`** — copy SessionModel history to clipboard
  (Cycle 22b). Dumps all completed tuples + any in-flight
  active tuple as structured plain text, suitable for paste
  into chat / bug reports. Mnemonic: Y for histor**Y**.
  Companion to `Ctrl+Shift+D` (which announces a substrate
  summary); `Ctrl+Shift+Y` dumps full content for analysis.
  Format includes per-tuple block (PromptText / CommandText /
  OutputText / timestamps / exit code / source provenance);
  no truncation (clipboard limits dwarf realistic sessions).
  NVDA announces the byte count + entry count on success
  ("History copied to clipboard. K of 100 entries, N bytes.").
  Wired via `bindHotkey` against
  `HotkeyRegistry.CopyHistoryToClipboard`.

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
[May-2026 plan](PROJECT-PLAN-2026-05-12.md)'s Output framework
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

### Selection prompt thresholds and confidence modes (Cycle 32a)

#### Current state

The `SelectionDetector` (Cycle 29a substrate; `src/Terminal.Core/SelectionDetector.fs`) watches the screen substrate for SGR-styled rows that look like interactive-list selection prompts (e.g., Claude tool-use confirmations, cmd `choice`, fzf in non-alt-screen mode). It emits `SelectionShown` / `SelectionItem` / `SelectionDismissed` semantic events that the `SelectionProfile` (Cycle 29b) translates into NVDA announcements.

Four tunable parameters live in the detector's `Parameters` record (`SelectionDetector.fs:128-148`):

- `HighlightDetectionThresholdMs` — milliseconds a row must remain visually styled before counting as a stable selection candidate. Default **100**.
- `DismissalGraceMs` — milliseconds the candidate must remain absent before emitting `SelectionDismissed`. Default **150**.
- `KeystrokeCorrelationWindowMs` — milliseconds after a user keystroke (arrow key) inside which an SGR change counts as keystroke-correlated. Default **250**.
- `MinConfidence` — minimum confidence tier required for emission. Either `HeuristicSGR` (signals 1+2 only — more permissive; surfaces gaps quickly) or `HeuristicSGRWithKeystroke` (signals 1+2+3 — strict; requires arrow-key correlation observed within the keystroke window). Default `HeuristicSGR`.

Cycle 32a externalises all four via TOML.

#### Why hardcoded before Cycle 32a

The defaults are spec-derived (`spec/tech-plan.md` §8.1; `spec/event-and-output-framework.md` §B.3.3). Cycle 29b NVDA validation 2026-05-09 surfaced acute regressions elsewhere (spinner storms, red-tone misfires) but did not surface a need to tune the selection thresholds — the gotcha that surfaced was that Claude's auto-trust mode skipped the confirmation prompt entirely (per `docs/STAGE-7-ISSUES.md` `[output-selection]` section), not that the thresholds were wrong.

Cycle 32a externalises the knobs without changing defaults. Power users (or future per-shell tuning) can override.

#### What configurability looks like

```toml
schema_version = 1

[profile.selection]
# Milliseconds a row's SGR-styled run must remain stable before
# the detector treats it as a selection candidate. Defaults to 100.
highlight_detection_threshold_ms = 100

# Milliseconds the candidate must remain absent before emitting
# SelectionDismissed. Defaults to 150.
dismissal_grace_ms = 150

# Milliseconds after a user keystroke (arrow key) inside which an
# SGR change counts as keystroke-correlated. Defaults to 250.
keystroke_correlation_window_ms = 250

# Minimum confidence required to emit SelectionShown. Either
# "heuristic_sgr" (signals 1+2 only) or
# "heuristic_sgr_with_keystroke" (signals 1+2+3 — arrow-key
# correlation observed within the keystroke window).
# Default: "heuristic_sgr".
min_confidence = "heuristic_sgr_with_keystroke"
```

All four keys are optional; absent keys fall back to detector defaults. Unknown keys log a Warning and are dropped. Non-positive integer values log a Warning and fall back to default. Unrecognised `min_confidence` strings log a Warning and fall back to `"heuristic_sgr"`.

Tuning use cases:

- **Slow synthesiser / slow user** — raise `highlight_detection_threshold_ms` to 200-300ms so transient SGR repaints don't trigger spurious "selection prompt" announcements.
- **Strict mode (Claude tool-use only)** — set `min_confidence = "heuristic_sgr_with_keystroke"` to suppress non-keystroke-correlated selections (e.g., a `git status` line that happens to be SGR-styled but isn't a real prompt).
- **Faster dismissal** — lower `dismissal_grace_ms` to 50ms if the maintainer wants `SelectionDismissed` to fire immediately when a prompt clears.

#### Implementation notes

- Cycle 32a-only scope: `src/Terminal.Core/Config.fs` adds `SelectionParameterOverrides` record + `parseProfileSelectionOverrides` parser + `resolveSelectionParameters` helper. `src/Terminal.App/Program.fs:1186` composition cutover (3-line change). `tests/Tests.Unit/ConfigTests.fs` adds 6 facts covering: section absent → defaults; all four keys present override; single key present overrides + others default; unrecognised `min_confidence` logs Warning + defaults; non-integer threshold logs Warning + defaults; non-positive threshold logs Warning + defaults.
- **No hot-reload** — the detector is constructed once at startup with the resolved parameters; mid-session config changes require a restart. This matches every other section except `[session_model.persistence]` (which gets reloaded on shell-switch via `Program.fs:2488-2500`). Hot-reload could be added in a future cycle by plumbing into the existing shell-switch reload handler.
- **No detector internal changes** — the `Parameters` record + `defaultParameters` value already exist (Cycle 29a shipped them); Cycle 32a only adds Config-side overrides + composition-root wiring.
- **Validation** — per `docs/STAGE-7-ISSUES.md` `[output-selection]`, manual NVDA testing requires a Claude tool-use prompt that requires user confirmation (force a prompt by writing a file in an untrusted directory; Claude auto-trust mode skips the prompt for trusted workspaces). The Cycle 29b NVDA matrix is the authoritative test surface; Cycle 32a does not add a new row.

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

## Suffix-diff parameters (retired Cycle 45c)

PR #166's sub-row suffix-diff at `StreamPathway` emit shipped
2026-05-06 and accumulated a parameter atlas across Cycles 35–43
(`BulkChangeThreshold`, `BackspacePolicy`, `ModeBarrierFlushPolicy`,
`ColorDetection`, plus `[pathway.stream]` debounce / spinner-window /
spinner-threshold / max-announce-chars knobs). The atlas catalogued
each parameter for eventual user-tunability via `config.toml`.

**All of these parameters were retired in Cycle 45c** (PRs #275 + #278,
2026-05-12) along with the `StreamPathway` substrate they tuned.
The post-Cycle-45c aural substrate is `ContentHistory` +
`SpeechCursor`, which has its own (much smaller) parameter
surface — currently:

| Parameter | Default | Source |
|---|---|---|
| `ContentHistory.Parameters.IdleSpanSealMs` | `200` | `src/Terminal.Core/ContentHistory.fs` |
| `ContentHistory.Parameters.MaxEntriesPerTuple` | `10_000` | same |

Neither is user-tunable today. A future cycle will rebuild the
atlas-style "configurability path" entries for these (and for
any new parameters surfaced by Cycle 45g / semantic-labels /
spinner refinements) once the substrate has bedded in.

The pre-Cycle-45 suffix-diff atlas is preserved in
[`docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md`](archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md)
and the relevant subsections of this file's predecessor revision
(reconstruct from git history at `e51c23e^` if needed; the
content was historically too detailed to copy forward to a slim
post-Cycle-45c doc).

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

### Platform-internal buffer / capacity defaults (📋 reserved)

These are platform-internal tuning constants that affect
internal queue / buffer behaviour. Listed here for
discoverability (per Track D audit findings D5 + D6).
Currently NOT user-configurable; could become 📋 reserved
TOML knobs under a future `[diagnostic]` or `[platform]`
section if user demand surfaces, but they're unlikely to
be tunable in v1 — the defaults are well-suited to the
expected workload.

- **`FileLoggerOptions.ChannelCapacity = 8192`**
  (`src/Terminal.Core/FileLogger.fs:88`). Backpressure
  threshold for the bounded log-write channel. Affects
  what happens under heavy logging load (channel-full →
  blocking enqueue). Increasing it raises memory ceiling;
  decreasing it raises blocking risk.
- **ConPtyHost FileStream buffer = 4096 bytes**
  (`src/Terminal.Pty/ConPtyHost.fs:137,138,173`).
  Read/write buffer size for ConPTY stdin / stdout
  FileStream wrappers + the byte-array reader buffer.
  Standard Win32 default; rarely a performance
  bottleneck.

Both are catalogued for discoverability rather than
configurability. If user demand surfaces (e.g. heavy-log
workload that sees backpressure), the right move is to
investigate the workload first, not to expose the knob.

## How to add a menu item (Cycle 26 extension recipe)

The Cycle 26 app menu (`src/Views/MainWindow.xaml`) is wired
via reflection from `HotkeyRegistry.builtIns`: every entry whose
`nameOf` matches a XAML `MenuItem`'s `x:Name` (in the form
`MenuItem_<AppCommandName>`) gets its `Command` and
`InputGestureText` assigned at compose time. Adding a new menu
item is therefore a **three-edit recipe** with no `Program.fs`
composition or test-fixture changes required.

### Recipe — gesture-bearing command (the typical case)

To add a new keyboard hotkey AND surface it in the menu:

1. **`src/Terminal.Core/HotkeyRegistry.fs`** — add the new
   `AppCommand` DU case, the matching `nameOf` arm
   (the F# compiler enforces exhaustive pattern match), the
   `builtIns` row with `Some Key` / `Some Modifiers`, and
   the `allCommands` row.
2. **`src/Views/TerminalView.cs`** — add the matching
   `(Key.X, ModifierKeys.Control | ModifierKeys.Shift, "Description")`
   row to the `AppReservedHotkeys` static array
   (`TerminalView.cs:346-489`). The
   `AppReservedHotkeysMirrorTests.fs` test pins this parity at
   test time — a missing C# row fails CI immediately.
3. **`src/Views/MainWindow.xaml`** — add a
   `<MenuItem x:Name="MenuItem_<NewCommand>" x:FieldModifier="public" Header="..." />`
   element inside the appropriate top-level menu (Shell, View,
   Data, Diagnostics, Help — see Cycle 26b notes for taxonomy).

Compose's reflection wiring picks up the new entry on next
launch — `Program.fs:638-647 / 1940-1950 / 2415-2417` already
calls `bind` for every `AppCommand` and the menu-wiring loop at
`Program.fs:2452-2480` walks the dictionary and assigns
`MenuItem.Command` + `InputGestureText` automatically.

The handler — the function that runs when the user presses the
hotkey or selects the menu item — does need a `bind` call
somewhere in `Program.fs`. Module-level handlers go alongside
`runOpenNewRelease` / `runOpenDataFolder` / `runOpenConfig`
(announce-before-focus-grab pattern); compose-closure handlers
go alongside `runHealthCheck` / `runDiagnostic` (when state
capture is needed).

### Recipe — menu-only command (no keyboard accelerator)

To add a new menu item with NO default keyboard hotkey
(relieving the working-memory ceiling):

1. **`src/Terminal.Core/HotkeyRegistry.fs`** — add the
   `AppCommand` DU case + `nameOf` arm + `allCommands` row, as
   above. The `builtIns` row uses `Key = None; Modifiers = None`
   (the helper `gestureText` returns `None` for menu-only
   commands so `InputGestureText` is left blank).
2. **No `TerminalView.AppReservedHotkeys` change** — menu-only
   commands have no gesture to reserve. The mirror test's filter
   excludes `None` entries automatically.
3. **`src/Views/MainWindow.xaml`** — add the
   `<MenuItem x:Name="MenuItem_<NewCommand>" ... />` element.

`bindHotkey` pattern-matches on `(Some k, Some m)` to install
the `KeyBinding` (skipping it for menu-only) but always registers
the `CommandBinding` so the menu can dispatch via the captured
`RoutedCommand`.

Cycle 26c (`RunProcessCleanupScript`) is the worked example.

### Recipe — adding a new top-level menu

To add a new menu (e.g. Window, Display, Preferences/Settings —
all anticipated in
[`docs/PROJECT-PLAN-2026-05-09.md`](PROJECT-PLAN-2026-05-12.md)):

1. **`src/Views/MainWindow.xaml`** — add a new
   `<MenuItem Header="_NewMenu">...</MenuItem>` block inside the
   `<Menu>` element. Put it in the order the maintainer wants
   relative to the existing 5 top-level menus.
2. Pick a unique mnemonic letter (the `_X` prefix); top-level
   menus must not conflict with each other. Current taxonomy
   uses S (Shell), V (View), D (Data), g (Diagnostics), H (Help).
3. Add child `<MenuItem>` items per the recipes above.

No F# changes required for the new top-level menu itself; only
the children's wiring follows the existing recipes.

### Recipe — rebinding or removing an accelerator

To rebind a hotkey (e.g. change `Ctrl+Shift+M` to `Ctrl+Alt+M`):

1. Edit the `Key` / `Modifiers` fields in the `builtIns` row in
   `HotkeyRegistry.fs`.
2. Update the matching `AppReservedHotkeys` row in
   `TerminalView.cs`.
3. Update any `HotkeyRegistryTests.fs` documented-binding fixture
   that pins the old gesture (the test name typically mentions
   the gesture explicitly).

The mirror test catches a rebind that touches only one side. The
menu's `InputGestureText` updates automatically on next launch
since it's read from `gestureText` at compose time.

To remove an accelerator while keeping the menu item, change
the `builtIns` row's `Key` and `Modifiers` to `None`. Drop the
`AppReservedHotkeys` row. Mirror test asserts the now-menu-only
command is excluded from the parity set.

To remove a menu item entirely: reverse the original recipe —
delete the `AppCommand` DU case + `nameOf` arm + `builtIns` row
+ `allCommands` row + `AppReservedHotkeys` row + `MenuItem` XAML
element + the handler function + the `bind` call. (Cycle 25b-1a
removed `Ctrl+Shift+L` via this exact shape; the commit history
is the worked example.)

### Phase 2 evolution (forward-looking)

The `Hotkey` record's `Key` / `Modifiers` fields are designed for
a future Phase 2 user-settings TOML override surface — e.g.
`[hotkeys] checkForUpdates = "Ctrl+Alt+U"`. The user-settings
substrate would (a) load TOML, (b) override fields on the
default `Hotkey` record, (c) pass the result to `bindHotkey`.
Menu-only commands have no TOML-overridable gesture today;
Phase 2 may surface the `None → Some` promotion as a separate
config knob.

### Recipe — multi-state command (Cycle 27 paradigm)

The Cycle 27 paradigm covers operations whose UX is "select one
of N discrete options" rather than "fire one action". Multi-state
commands surface as a parent `MenuItem` with one sub-item per
option; each option's `IsCheckable=true` + `IsChecked` exposes
UIA TogglePattern that NVDA reads as "menu item, checked" /
"menu item, not checked", so a screen-reader user can tell at a
glance which option is currently active.

The two existing migrations (Cycle 27) are:

- `EarconsMode` (View → Earcons → Enabled / Muted) — formerly the
  `MuteEarcons` Ctrl+Shift+M toggle.
- `LoggingLevel` (View → Logging Level → Information / Debug) —
  formerly the `ToggleDebugLog` Ctrl+Shift+G toggle.

Multi-state commands are **menu-only by canon** as of Cycle 27;
they have no keyboard accelerator. A future multi-state command
that needs per-option keyboard accelerators (e.g. a Shell-switch
migration where `Ctrl+Shift+1`/`+2`/`+3` would each target a
distinct option) is a clean extension to `MultiStateOption` —
add optional `Key`/`Modifiers` fields and extend
`bindMultiState` to install a `KeyBinding` per gesture-bearing
option. Out of scope until needed.

#### Four edits to add a multi-state command

1. **`HotkeyRegistry.fs` — extend the registry.** Add the new
   case to `MultiStateCommand`, append a `multiStateNameOf`
   match arm (the F# compiler enforces exhaustiveness under
   `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`),
   append a row to `multiStateBuiltIns` listing the `Options`
   (each with a stable snake_case `OptionId` + a user-facing
   `DisplayName`), and append the case to
   `multiStateAllCommands`.
2. **`Program.fs compose ()` — add a `bindMultiState` call.**
   Right after the existing `bindMultiState` block for
   `EarconsMode` / `LoggingLevel`, add a new block:
   ```fsharp
   let myCmdLog = Logger.get "Terminal.App.Program.bindMultiState.MyCmd"
   let myCmdDef = HotkeyRegistry.multiStateOf HotkeyRegistry.MyCmd
   let myCmdBinding =
       bindMultiState
           window
           myCmdDef
           (fun () -> /* current OptionId */)
           (fun target -> /* set state to target; idempotent */
                          /* + log + announce via ActivityIds */)
   multiStateBindings.[HotkeyRegistry.MyCmd] <- myCmdBinding
   ```
   The `setByName` closure should be **idempotent**: no-op when
   `current ≡ target`. Re-selecting the active option from the
   menu should not re-log or re-announce; the existing two
   migrations gate the work behind a `current ≠ target`
   comparison and you should follow that pattern.
3. **`MainWindow.xaml` — add the parent + per-option items.**
   Convention: `MenuItem_<MultiStateName>` for the parent and
   `MenuItem_<MultiStateName>_<OptionId>` for each child.
   Mark both with `x:FieldModifier="public"` so reflection-
   driven wiring in `compose ()` can find them.
   ```xaml
   <MenuItem x:Name="MenuItem_MyCmd"
             x:FieldModifier="public"
             Header="My _Cmd">
       <MenuItem x:Name="MenuItem_MyCmd_optionA"
                 x:FieldModifier="public"
                 Header="Option _A" />
       <MenuItem x:Name="MenuItem_MyCmd_optionB"
                 x:FieldModifier="public"
                 Header="Option _B" />
   </MenuItem>
   ```
4. **`MultiStateRegistryTests.fs` — pin the OptionIds.** Add a
   pinning fixture so a future PR can't silently rename an
   `OptionId` (which would break the XAML field-name lookup,
   the `RoutedCommand` name, and any future TOML key):
   ```fsharp
   [<Fact>]
   let ``MyCmd options are pinned to optionA, optionB`` () =
       let def = HotkeyRegistry.multiStateOf HotkeyRegistry.MyCmd
       let ids = def.Options |> List.map (fun o -> o.OptionId)
       Assert.Equal<string list>([ "optionA"; "optionB" ], ids)
   ```

The reflection-driven wiring in `compose ()` picks up the new
parent + children automatically — the per-option
`MenuItem.Command` is assigned and the parent's
`SubmenuOpened` event is hooked to refresh `IsChecked` whenever
the user opens the submenu.

#### Removing a multi-state command

Inverse of the recipe: drop the `MultiStateCommand` DU case
(F# compiler surfaces dead arms in `multiStateNameOf` and any
exhaustive match), drop the `multiStateBuiltIns` row, drop the
`multiStateAllCommands` entry, drop the `bindMultiState` call
in `compose ()`, drop the parent + children from
`MainWindow.xaml`, drop the pinning fixture from
`MultiStateRegistryTests.fs`. The reflection-driven wiring is
forgiving: missing XAML fields are skipped silently (same
behaviour as the single-action wiring loop).

#### Migrating an existing single-action toggle

If the existing single-action `AppCommand` is a binary toggle
that conceptually fits "select one of N options" (the Cycle 27
migrations are the worked examples), follow this sequence:
(1) drop the `AppCommand` DU case + `nameOf` arm + `builtIns`
row + `allCommands` entry; (2) drop the matching row from
C#-side `TerminalView.AppReservedHotkeys` (the
`AppReservedHotkeysMirrorTests` parity test catches asymmetry
automatically); (3) drop the handler function in `Program.fs`
+ the `bind HotkeyRegistry.<cmd> ...` call in `compose ()`;
(4) drop the single-action XAML `MenuItem`; (5) follow the
four-edit recipe above to add the multi-state replacement.

If the migration drops the keyboard accelerator (the Cycle 27
default per the maintainer's working-memory ceiling), also add
a fixture to `HotkeyRegistryTests.fs` pinning the gesture as
unbound (mirrors the `Ctrl+Shift+G is unbound` and
`Ctrl+Shift+M is unbound` fixtures Cycle 27 added).

## Cycle 45 aural-UX backlog (planned settings + features)

Captured 2026-05-12 from maintainer dogfood on the post-Cycle-45
build. These are user-facing preferences and small features the
maintainer flagged during validation — none are blocking, but
each is a concrete improvement worth tracking until a dedicated
follow-up cycle (likely "Cycle 45f — verbosity modes") picks
them up.

### Navigation-key announce shape

The Cycle 45 nav-key echo (PR #265 + #266) announces a single
character when the user presses Backspace / Delete / Left /
Right / Home. The current behaviour matches NVDA's text-editor
convention. Maintainer-requested alternative settings:

- **Home key**: announce the entire current line (the typed
  input AND/OR the prompt path) instead of just the char at
  column 0. Variant: announce just the typed input portion
  WITHOUT the prompt path prefix (more useful when the prompt
  is a long absolute path).
- Insertion point for the future toggle:
  `src/Views/TerminalView.cs` `AnnounceNavigationEcho`
  (the `Key.Home` arm of the switch). Surrounding doc-comment
  carries a `// Cycle 45 backlog:` marker.

### Prompt-path verbosity

For shells whose prompt is a long absolute path
(`C:\Users\Kyle\AppData\Local\...\>`), the path content
dominates the announce. Maintainer-requested settings:

- **Suppress the full path** in announces, leaving just the
  final directory name (`Local>` rather than `C:\Users\…\Local>`)
- **Suppress the path entirely** for a minimal prompt cue
- **Keep the full path** (today's default)
- Insertion point: SpeechCursor `renderEntry` for the
  PromptStart marker (currently returns `None`; future logic
  could read the prompt text from the SessionTuple's
  ActivePromptText and apply the user-selected verbosity rule)

### Speech-cursor keyboard accelerators

Speech-cursor navigation is currently menu-only (`Display →
Speech Cursor → ...`). Maintainer-requested accelerator:

- **`Ctrl+Up` / `Ctrl+Down`** for entry-by-entry navigation —
  `SpeechCursorPrevious` / `SpeechCursorNext`. Direct keyboard
  access is much faster than menu walks for review-driven use.
- **`Ctrl+Shift+Up` / `Ctrl+Shift+Down`** for chunk-level
  navigation (jump to the START of a logical input or output
  chunk, skipping over multiple TextSpans within one chunk).
  Requires the semantic-label work below.
- Implementation: change the `Key = None, Modifiers = None`
  rows in `HotkeyRegistry.builtIns` for `SpeechCursorPrevious`
  / `SpeechCursorNext` / `SpeechCursorJumpToLatest` /
  `SpeechCursorToggleMode` to bind the gesture; mirror in
  `src/Views/TerminalView.cs` `AppReservedHotkeys` (Cycle 26b
  mirror invariant). Verify no NVDA collision on Ctrl+Up/Down
  — NVDA's default review-cursor commands use `NVDA+Up/Down`
  not `Ctrl+Up/Down`, so the gesture is free in screen-reader
  mode.

### ContentHistory semantic labels (input vs output)

To support the "inject past input into current input" feature
the maintainer flagged for the future, every `ContentHistory.Entry`
needs to carry a label of whether it originated from typed
input or cmd output. The label powers:

- **"Output chunk 2 of 5"** style navigation announces that
  collapse multiple consecutive TextSpans within a single
  output chunk into one navigable unit
- **`Ctrl+Shift+Up/Down`** chunk-level jump (above)
- **Future "inject this past input"** action — when the speech
  cursor is parked on a labelled-as-input entry, the user can
  trigger an action that pastes that text back into the
  current input buffer

Implementation sketch: add a `Source: EntrySource` field to
each Entry record (DU: `TypedInput | CmdOutput | Marker`); set
it at `appendFromEvent` / `appendMarker` time based on
SessionModel's current ActiveTupleState. The post-tuple-seal
labelling pass that counts output chunks ("2 of 5") can run as
part of the tuple-finalise side effect, mutating the entries'
labels with index information.

### NVDA "Read Current Line" follows the cmd cursor

NVDA's "Read Current Line" command (default gesture
`NVDA+Up Arrow`) typically reads the line at the **system
caret** position. In a normal text edit (Notepad, web form),
the system caret follows the user's typing. In pty-speak's
`TerminalAutomationPeer`, the UIA peer exposes a Document
with a Text pattern but does NOT track a caret position;
NVDA's read-current-line consequently reads whatever line the
review cursor was last on, which can drift away from the
actual cmd input row.

The proper fix is to expose a caret via the Text pattern's
`ITextRangeProvider.GetCaretRange` (or equivalent) and fire
`AutomationEvents.TextSelectionChangedEvent` whenever cmd's
cursor moves (which we already track via Screen state changes).
That'd make NVDA read the correct line on each read-current-line
gesture, AND eliminate the need for the manual nav-echo we
shipped in #265 — NVDA's keyboard echo would Just Work.

Insertion point:
`src/Terminal.Accessibility/TerminalAutomationPeer.fs` — the
ITextProvider implementation. Likely needs a substantial
revisit; not a one-line fix.

This is arguably the highest-leverage UIA improvement in the
backlog because it fixes nav-echo, read-current-line, AND
brings pty-speak closer to "indistinguishable from a normal
text input control" from NVDA's perspective. Worth scoping
properly before tackling.

### Unified event stream — keypresses + timestamps + modifiers

Captured 2026-05-12 from a maintainer clarifying question:
ensure the project doesn't drift away from an extensible,
modular event substrate. The system should route every kind
of event through one canonical stream so future handlers can
fan out per event-type (notifications pipeline / keypress
pipeline / earcon pipeline / spatial-audio pipeline / etc.)
without each producer inventing its own dispatch surface.

**Current state**:

- `OutputEvent` (`src/Terminal.Core/OutputEventTypes.fs:246`)
  is the canonical event type. Fields today:
  `Semantic / Priority / Verbosity / Source / SpatialHint /
  RegionHint / StructuralContext / Payload / Version /
  Extensions`. `Source` carries `Producer / Shell /
  CorrelationId`; `Extensions: Map<string, obj>` is the
  forward-compat slot.
- `OutputDispatcher` fans every event through an active
  profile set; each profile decides which channel renders.
  This is the pluggable routing — it works today.
- `FileLoggerChannel.fs:83` formats each event as a structured
  log line keyed on `Semantic / Priority / Producer / Shell /
  PayloadLen / Payload`. Serilog adds a timestamp prefix
  (`[HH:mm:ss.fff INF]`) to the line; the timestamp is NOT a
  structured field on `OutputEvent` itself.

**Gaps to close**:

1. **Keypresses are not in the stream.** `TerminalView.OnPreviewKeyDown`
   handles keys and forwards them straight to the PTY (and
   emits a one-off `Announce(...)` for the Cycle 45 nav-echo).
   No audit trail of "user pressed Backspace at T=...".
   Adding a `SemanticCategory.KeyPress` arm and emitting via
   `OutputDispatcher` would close the loop — every key the
   user pressed becomes a structured event with the same
   logging + routing shape as everything else.
2. **No explicit timestamp field.** Pure consumers (an event
   sink that isn't FileLogger) can't read a Unix timestamp
   today. Adding `Timestamp: DateTimeOffset` to `OutputEvent`
   is a small additive change; producers set it at construction
   time, FileLoggerChannel's format gets a `Ts={Ts:O}` field
   alongside the existing structured fields.
3. **No modifiers field.** A keypress event needs to carry
   Shift / Ctrl / Alt state. Add `Modifiers: KeyModifiers
   option` (or similar — a flags enum) to `OutputEvent`, or
   route it via `Extensions` with a well-known key. The
   `Extensions` route is the safer-additive option since
   non-key events never set it.
4. **`Source` taxonomy is free-text.** `Source.Producer` is
   any string. Maintainer's desired taxonomy: `window /
   process / shell / runtime`. Refactor `SourceIdentity`
   to carry a `Kind: SourceKind` DU (`Window | Process | Shell
   | Runtime | Other of string`) plus the existing `Producer`
   string for the within-kind identifier.

**Why bother**: the user explicitly named the goal of making
"highly deterministic functionality optimized for a
screen-reader user" extensible. Future features — record &
replay, scriptable input, third-party plugins reacting to
events, low-vision-pair visual rendering — all assume there's
ONE event stream they can subscribe to. The closer the actual
stream is to that abstraction, the cheaper each future feature
becomes.

**Sequence sketch** (each commit independent + CI-gated):

1. Add `Timestamp` field to `OutputEvent`; producers populate
   `DateTimeOffset.UtcNow`; FileLoggerChannel renders
   `Ts={Ts:O}`. Pure refactor, no behaviour change.
2. Add `SemanticCategory.KeyPress`; emit one event per
   `OnPreviewKeyDown` (modifiers via `Extensions`). FileLogger
   captures each automatically; a future "input pipeline"
   profile can subscribe.
3. Refactor `SourceIdentity` to carry a `SourceKind` DU.
   Producers thread the right kind through. Tests + the diag
   bundle's `--- FILELOGGER ACTIVE LOG ---` section format
   pick up the new field without source-level changes.

Insertion points:

- `src/Terminal.Core/OutputEventTypes.fs` (schema)
- `src/Terminal.Core/FileLoggerChannel.fs` (rendering)
- `src/Views/TerminalView.cs` `OnPreviewKeyDown` (key producer)
- `src/Terminal.App/OutputDispatcher.fs` (no change; consumers
  pick up the new fields via the existing fan-out)

### Per-shell policy table — keep cmd-specific logic at one site

Captured 2026-05-12 from the same clarifying question: keep
cmd-specific handling separated from general handling so we
can branch any special-case logic as far down the chain as
possible when richer CLIs (Claude Code CLI, eventually WSL /
Python REPL / etc.) land.

**Current state**: shell-keyed branching IS localized to a
handful of sites. Concentration is decent today:

- `src/Terminal.Core/HeuristicPromptDetector.fs:184` —
  `match shellKey with | "cmd" -> ...` selects per-shell regex
  + stability threshold
- `src/Terminal.Core/SelectionDetector.fs` short-circuits when
  `shellKey ≠ "claude"` (`Program.fs:1678`-region comment)
- `src/Terminal.Core/Config.fs` `ShellOverrides: Map<string,
  ShellOverride>` keyed by shell-id string
- `src/Terminal.Pty/ShellRegistry.fs` is the enum of supported
  shells

**Drift risk**: Cycle 45's tuple-seal-announce (PR #268)
unconditionally announces `SessionTuple.OutputText` on each
tuple boundary. That's the right shape for cmd / PowerShell
(short commands that always seal on Enter) but wrong for
Claude (streaming dialogue, no tuple boundary per response).
Cycle 45f (verbosity modes) will add per-shell streaming
policy — and unless we factor first, will introduce ANOTHER
`match shellKey` site somewhere new. Repeated, that becomes
the spaghetti the maintainer wants to avoid.

**Proposed shape**: a single `ShellPolicy` record per shell,
collected in a `ShellPolicyTable: Map<ShellKey, ShellPolicy>`,
referenced by every dispatch site instead of re-matching the
shell key inline.

```fsharp
type ShellPolicy =
    { PromptRegex: Regex option
      PromptStabilityMs: int
      SelectionDetectorEnabled: bool
      StreamingAnnounceMode: StreamingMode
        // TupleFinalOnly | LineByLine | Hybrid
      EditEcho: EditEchoMode
        // SuppressUntilSeal | EveryKey | NavOnly
      AltScreenBehavior: AltScreenMode
        // SuppressUntilExit | StreamThrough }
```

New shells = add a row to the table. Code sites = look up
`ShellPolicyTable.[currentShellKey]` and read fields, no
matching. Future Claude Code CLI = its own row with
`StreamingAnnounceMode = LineByLine`, `EditEcho = NavOnly`,
etc.

**Migration sequence**:

1. Introduce `ShellPolicy` type + a `defaultCmdPolicy` /
   `defaultPowerShellPolicy` / `defaultClaudePolicy` set;
   wire one consumer (`HeuristicPromptDetector`) to read from
   the table. Existing inline match becomes a fallback for the
   table-miss case. Pure refactor.
2. Migrate `SelectionDetector`'s shell gate to the same table.
3. Migrate Cycle 45f's verbosity-mode logic to the table from
   day one (so the new code is the right shape, not a fresh
   inline match site).

Insertion points:

- `src/Terminal.Core/` — new module `ShellPolicy.fs` carrying
  the type + the default table
- `src/Terminal.Core/HeuristicPromptDetector.fs:184` —
  consumer #1
- `src/Terminal.Core/SelectionDetector.fs` — consumer #2
- `src/Terminal.Core/Config.fs` — `ShellOverrides` becomes
  user-supplied overrides ON TOP of the default policy table

### Semantic output rendering — navigable units instead of stream chunks

Captured 2026-05-12 from maintainer dogfood on the
PR #268 build. Validated outcomes:

- `echo hi` with arrow / backspace / delete editing now
  narrates cleanly as "hi" (the screen-grid-derived
  `SessionTuple.OutputText`) instead of the inflated edit-
  conflated TextSpan from before #268.
- Long `dir` output reads all the way to the end without
  truncation; NVDA chunks the payload internally with small
  pauses between chunks.

Maintainer observation from this dogfood: the inter-chunk
pauses on long outputs are NVDA's natural read cadence over a
single `ImportantAll` payload — we don't control the pause
from the producer side. The next-level shape, which the
maintainer explicitly raised, is to **render output in a
semantically navigable format** so the user navigates units
(rows of `dir`, individual error lines, individual `git log`
entries, individual `ping` echoes, etc.) rather than waiting
for NVDA's read-back to traverse a single multi-kilobyte
payload.

This is not a single feature — it's the natural endpoint of
work already flagged in the strategic plan. Explicit linkage:

- **Cycle 45e** (ContentHistory-driven visual surface for
  sighted-pair collaboration, per `PROJECT-PLAN-2026-05-09.md`
  and `CORE-ABSTRACTION-BOUNDARY.md`) introduces the
  ContentHistory → primary visual rendering and routes the
  UIA Document at the same data. Once that lands, NVDA's
  review cursor speaks the same per-entry units the visual
  surface highlights — semantic navigation Just Works for
  any output ContentHistory has typed entries for.
- **Cycle 45f** (verbosity modes) introduces per-shell
  streaming-vs-tuple-final policy AND the
  `ContentHistory.Entry.Source` semantic label work (see
  "ContentHistory semantic labels (input vs output)" above)
  — those labels are the input-vs-output dimension of the
  unit taxonomy. Per-line / per-section / per-chunk units
  are the orthogonal dimension and would come from per-
  shell or per-output-type detectors.
- **Per-shell policy table** (above) — the unit-detector for
  cmd's `dir` (one row per file), Claude Code's streaming
  response (one chunk per dialogue turn), `git log` (one
  entry per commit), etc. each becomes a row in the policy
  table rather than scattered inline logic. New shells / new
  output types plug in as additional rows.

The headline payoff for the screen-reader UX: SpeechCursor
Next / Previous (and the Ctrl+Up/Down accelerators above)
moves the user through SEMANTIC units regardless of how the
underlying shell happened to draw them. `dir` becomes 50
navigable rows; `git log` becomes one navigable commit at a
time; Claude's streaming response becomes one navigable
paragraph; the inter-chunk NVDA pause is replaced by the
user's own pace.

Scope: substantial — depends on Cycle 45e's visual surface
landing first AND Cycle 45f's semantic-label work. Worth
naming explicitly here so the connection between the three
cycles is documented; the actual implementation slots after
they settle.

