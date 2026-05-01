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

`pty-speak` does **not yet have** a configuration surface
beyond compile-time constants. The infrastructure for
user-configurable settings is reserved as a Phase 2 stage in
[`spec/tech-plan.md`](../spec/tech-plan.md) (TOML config via
[Tomlyn](https://github.com/xoofx/Tomlyn) — referenced at the
end of the plan's reference code map). Until that lands, every
setting in this document is a "candidate" — known and tracked,
not yet exposed.

Why no config yet: the project ships through Stage 4 + 11 with
flat hardcoded defaults. Adding a config substrate before we
know which settings users actually want to change would build a
half-finished setting system around the wrong choices. Tracking
the candidates here lets the choices accumulate without paying
the substrate cost prematurely.

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
still pending in the roadmap. The current state is "all the
candidate settings are deciding-now-shippable-later." Logging
them here lets the design think through configurability before
shipping the substrate so we don't bake in choices that
users want to flip.

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

## Keybindings

### Current state

Two app-level keybindings shipped today:

- **`Ctrl+Shift+U`** — Velopack auto-update (Stage 11, PR #63).
  Wired in `setupAutoUpdateKeybinding` in
  `src/Terminal.App/Program.fs`.
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

No explicit verbosity setting yet. Stage 5 (streaming output
notifications, pending) will introduce the first decisions
about what to narrate — line-by-line, only on event
boundaries, only on errors, etc. Spec calls for spinner
suppression and rate limiting.

### Why hardcoded now (for Stage 5+ once it ships)

The defaults for Stage 5 will be evidence-based: "what does
NVDA need to say so the user understands what's happening
without being overwhelmed?" The defaults aren't arbitrary;
they're the answer to that question. But "what counts as
overwhelming" varies per user and per task, so verbosity
control will be one of the first config knobs to need
exposing.

### What configurability would look like

The spec sketches three profiles (`spec/overview.md` Phase 2):

- **Off** — no streaming narration; review cursor only.
- **Smart** (default) — coalesced flushes, spinner
  suppression, error highlighting.
- **Verbose** — every line announced, no coalescing.

Plus:

- **Per-event-class enable/disable** — power-user setting,
  e.g. "narrate stderr but suppress stdout"; "announce errors
  but skip command echoes".
- **Notification rate limit override** — for the
  programmer-class user who wants every byte read aloud and
  knows the cost.

### Implementation notes

- Verbosity needs to be readable from Stage 5's notification
  emission point in real time.
- `Ctrl+Shift+V` cycle hotkey is a natural choice
  ("verbosity off / smart / verbose" cycle) similar to the
  proposed word-boundary cycle.
- Tests: `tests/Tests.Unit` should include FsCheck properties
  on the coalescer (no notification flood at any verbosity
  level, no notifications dropped silently at "verbose"
  level).

## Process for adding a new setting

When the project moves from "hardcoded" to "user-configurable"
for a given setting, the following pattern keeps the change
focused:

1. **Land the substrate first.** Phase 2 introduces TOML
   config via Tomlyn. Don't add a one-off settings file
   ahead of that — it'll need to be migrated.
2. **Per-setting PR.** Each setting becomes its own PR rather
   than a "add 12 settings" mega-PR. Easier to review,
   easier to revert if the chosen default doesn't survive
   user testing.
3. **Default = current behaviour.** The first version of any
   setting MUST default to whatever the code does today. No
   silent behaviour change for users who don't set the
   value.
4. **Validate the input.** Bad config files should produce a
   clear error announcement at startup, not a silent revert
   to defaults — silent reverts hide misspelled keys.
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
