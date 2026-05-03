# Stable checkpoints

This document tracks **stable development checkpoints** in the
project's history — known-good states deliberately marked for easy
rollback when a later change breaks something fundamental. Each
checkpoint is anchored by three durable references:

- A **git tag** in the `baseline/` namespace
  (the primary handle for `git checkout` / `git reset`)
- A **GitHub PR label** `stable-baseline` on the PR that landed the
  checkpoint state (searchable via `is:pr label:stable-baseline`)
- A **GitHub Release** when the checkpoint matches a shipped preview

The `baseline/` tag namespace deliberately avoids the `v*` patterns,
so creating, fetching, or deleting a checkpoint tag never interacts
with the release workflow.

## Current checkpoints

| Tag | PR | Release | Description |
|---|---|---|---|
| `baseline/stage-0-ci-release` | [#26](https://github.com/KyleKeane/pty-speak/pull/26) | [v0.0.1-preview.15](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.15) | Stage 0 ship: F# / .NET 9 / WPF skeleton, working CI (build + test + Velopack pack smoke), working release pipeline (`release: published` → build → vpk pack → softprops upload). NVDA-validated unsigned installer. First state where the end-to-end deployment pipe is demonstrably green. |
| `baseline/stage-1-conpty-hello-world` | [#28](https://github.com/KyleKeane/pty-speak/pull/28) | _(no release; library-only stage)_ | Stage 1 ship: `Terminal.Pty` library with `Native` P/Invoke surface, typed `PseudoConsole.create` lifecycle wrapper enforcing the strict 9-step Microsoft order, and `ConPtyHost` high-level API (synchronous stdin `FileStream`, dedicated reader `Task` draining the output pipe into a bounded `Channel<byte array>`). Acceptance test in `Tests.Unit/ConPtyHostTests.fs` proves the chain on `windows-2025`. ConPTY render-cadence quirk documented in [`CONPTY-NOTES.md`](CONPTY-NOTES.md). No WPF surface change yet — same empty-window installer as `v0.0.1-preview.15`. |
| `baseline/stage-2-vt-parser` | [#31](https://github.com/KyleKeane/pty-speak/pull/31) | _(no release; library-only stage)_ | Stage 2 ship: `Terminal.Parser` containing Paul Williams' DEC ANSI state machine plus a `Parser` facade. Fourteen-state DFA, alacritty/vte caps applied (`MAX_INTERMEDIATES=2`, `MAX_OSC_PARAMS=16`, `MAX_OSC_RAW=1024`, `MAX_PARAMS=16`), inline UTF-8 decoder, full FsCheck property-test coverage (never throws, chunk-feed equals whole-feed, CAN returns to Ground). Real `VtEvent` DU lands in `Terminal.Core/Types.fs` (replaces placeholder). |
| `baseline/stage-3a-screen-model` | [#32](https://github.com/KyleKeane/pty-speak/pull/32) | _(no release; library-only stage)_ | Stage 3a ship: `Terminal.Core` gains `ColorSpec`, `SgrAttrs`, `Cell`, `Cursor`, and `Screen` (mutable buffer) with `Apply(VtEvent)` covering Print + auto-wrap + scroll, BS/HT/LF/CR, CSI cursor moves (A/B/C/D, H/f), CSI erase (J/K), and basic-16 SGR (fg 30-37/90-97/39, bg 40-47/100-107/49 + bold/italic/underline/inverse). 256-colour and DECSET deferred. |
| `baseline/stage-3b-wpf-rendering` | [#33](https://github.com/KyleKeane/pty-speak/pull/33) | _(release pending; would be the first Stage 3+ preview)_ | Stage 3b ship: `Views/TerminalView.cs` custom `FrameworkElement` with `OnRender` over the `Screen`, coalescing same-attr cell runs into single `FormattedText`s. `MainWindow.xaml` hosts it; `Terminal.App/Program.fs` wires `ConPtyHost → Parser → Screen → TerminalView` end-to-end with a background reader task and `Dispatcher.InvokeAsync` marshalling. First state where text from a child shell is visible in the WPF window. |
| `baseline/stage-4-uia-document-text-pattern` | [#54](https://github.com/KyleKeane/pty-speak/pull/54)–[#56](https://github.com/KyleKeane/pty-speak/pull/56), [#59](https://github.com/KyleKeane/pty-speak/pull/59), [#60](https://github.com/KyleKeane/pty-speak/pull/60), [#66](https://github.com/KyleKeane/pty-speak/pull/66), [#68](https://github.com/KyleKeane/pty-speak/pull/68) | [v0.0.1-preview.26](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.26) | Stage 4 ship: `Terminal.Accessibility/TerminalAutomationPeer.fs` exposes the screen via UIA Document role + Text pattern with working Line / Word / Character / Document review-cursor navigation. `IRawElementProviderSimple` raw-provider path bypasses the .NET 9 reference-assembly `GetPatternCore` visibility limit (per Issue #49 investigation). Anchor commit is PR #68 (real word-navigation closing the Stage 4 follow-up). NVDA-verified end-to-end on Windows 11: review cursor reads the prompt + prev/next line + prev/next word + prev/next char; `MainWindow.OnLoaded` focuses TerminalSurface so UIA peer is reachable; window title carries the version suffix for audible version-flip on auto-update. |
| `baseline/stage-11-velopack-auto-update` | [#63](https://github.com/KyleKeane/pty-speak/pull/63), [#66](https://github.com/KyleKeane/pty-speak/pull/66) | [v0.0.1-preview.26](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.26) | Stage 11 ship: `Ctrl+Shift+U` from inside the running app downloads the next preview's delta nupkg via `UpdateManager` and restarts within ~2 seconds, with NVDA progress announcements ("Checking for updates" → "Downloading" → bucketed percent → "Restarting to apply update"). Structured error matching on Velopack exceptions per `docs/UPDATE-FAILURES.md`. Anchor commit is PR #66 (window-title version suffix + structured update-failure messages). NVDA-verified end-to-end via `preview.25 → preview.26` self-update on Windows 11. |
| `baseline/stage-4b-process-cleanup-diagnostic` | [#81](https://github.com/KyleKeane/pty-speak/pull/81) | [v0.0.1-preview.27](https://github.com/KyleKeane/pty-speak/releases/tag/v0.0.1-preview.27) | Stage 4b ship: `Ctrl+Shift+D` reserved hotkey launches bundled `scripts/test-process-cleanup.ps1` in a separate PowerShell window for screen-reader-aware verification of the Alt+F4 / X-button close paths (Task Manager's chevron-expand affordance is not screen-reader-accessible, so the original Stage 4 process-cleanup matrix row was unreachable for an NVDA user). Script bundled via `Terminal.App.fsproj` `<Content>` include with `CopyToOutputDirectory=PreserveNewest`; path resolved via `AppContext.BaseDirectory`. Hotkey uses the announce-before-launch pattern (~700ms `Task.Delay`) so NVDA's speech queue plays the cue before the spawned conhost steals focus. Spec §4b. Known limitation tracked in `docs/SESSION-HANDOFF.md` item 6: conhost NVDA reading is unreliable; in-pty-speak rework is the screen-reader-native replacement now actionable since Stage 6 shipped. |
| `baseline/stage-4a-claude-code-substrate` | [#85](https://github.com/KyleKeane/pty-speak/pull/85), [#86](https://github.com/KyleKeane/pty-speak/pull/86) | _(post-Stage-4 preview line; foundation for Stage 7)_ | Stage 4a ship: VT-mode coverage gaps Claude Code's Ink reconciler depends on. PR-A (#85): `TerminalModes` record + `csiPrivateDispatch` + `escDispatch` substrate; DECTCEM cursor visibility (`?25h/l`); DECSC/DECRC cursor save/restore with SGR attrs (xterm convention); 256-colour and truecolor SGR with malformed-input bounds-guards (degrade to "ignore" rather than throw); OSC 52 explicit silent drop with SECURITY-CRITICAL inline comment cross-referenced from `SECURITY.md` row TC-2. PR-B (#86): alt-screen 1049 back-buffer (two `Cell[,]` buffers + `mutable activeBuffer`; xterm save/restore semantics; primary cells never copied during alt session; `Modes.AltScreen` flips with the swap; `SequenceNumber` bumps on every `?1049h/l` so Stage 5's coalescer can treat the toggle as a hard invalidation barrier). 27 xUnit facts across PR-A + PR-B in `ScreenTests.fs`. Spec §4a (formalized per chat 2026-05-03 maintainer authorization). Anchor commit is PR-B (#86). |
| `baseline/stage-5-streaming-coalescer` | [#89](https://github.com/KyleKeane/pty-speak/pull/89) | _(post-Stage-4a preview line; functional end-to-end as of PR #116)_ | Stage 5 ship: `Terminal.Core.Coalescer` module sits between the parser-side `notificationChannel` (256, DropOldest) and a new `coalescedChannel` (16, Wait). FNV-1a per-row + frame hash dedup with row-index folding; sliding-window spinner suppression (per `(rowIdx, hash)`, 1s window, threshold 5); leading- + trailing-edge 200ms debounce; alt-screen flush barrier (subscribes to Stage 4a's `ModeChanged` event); per-row `AnnounceSanitiser` chokepoint per audit-cycle SR-2; `ActivityIds` vocabulary so NVDA users can configure per-tag handling (`pty-speak.output`, `pty-speak.update`, `pty-speak.error`, `pty-speak.diagnostic`, `pty-speak.new-release`, `pty-speak.mode`). Two-channel composition wired in `Program.fs compose ()` with shared `cts.Token`; production passes `TimeProvider.System`, tests inject `FakeTimeProvider`. The Stage 5 PR also bundled the audit-cycle-flagged `Acc/9` `OnRender` lock fix (single `SnapshotRows` call per render frame). **Functional end-to-end as of PR #116** (which removed the broken `AllHashHistory` spinner-gate that was suppressing every event); the verbose-readback issue is the first foundational architecture decision the May-2026 plan's Output framework cycle (Part 3) addresses. Anchor commit is PR #89. |
| `baseline/stage-6-keyboard-input` | [#92](https://github.com/KyleKeane/pty-speak/pull/92), [#99](https://github.com/KyleKeane/pty-speak/pull/99), [#100](https://github.com/KyleKeane/pty-speak/pull/100) | _(post-Stage-5 preview line; xUnit-verified)_ | Stage 6 ship: pty-speak becomes interactive. PR-A (#92): parser arms for DECCKM (`?1`), bracketed paste (`?2004`), focus reporting (`?1004`) following Stage 4a's `ModeChanged` template. PR-B (#99): pure-F# `KeyEncoding` module (decoupled from `System.Windows.Input.Key`; xterm-style VT byte sequences honouring DECCKM, SGR-modifier protocol, F1-F12 SS3/CSI conventions, Ctrl-letter / Alt-prefix encoding, Backspace sends DEL); WPF input wiring with load-bearing `OnPreviewKeyDown` filter ordering (AppReservedHotkeys first; NVDA-modifier filter second; encode + write last); `ApplicationCommands.Paste` handler wraps clipboard text in bracketed-paste markers when `?2004` is set + strips embedded `\x1b[201~` (paste-injection defence — accessibility-first divergence from xterm); `OnGotKeyboardFocus`/`OnLostKeyboardFocus` emit `\x1b[I`/`\x1b[O` when `?1004` is set; window resize debounces through 200ms `DispatcherTimer` to `ResizePseudoConsole`; kernel Job Object with `KILL_ON_JOB_CLOSE` contains the entire child-process tree so even a hard parent crash leaves no orphans. Fixup (#100): three post-Stage-6 NVDA-verification regressions fixed (Ctrl+V routing via `HandleAppLevelShortcut` direct-dispatch, `MeasureOverride` honours `availableSize` for resize reflow, `MostRecent` → `ImportantAll` activityId processing for streaming output). 35 `KeyEncodingTests` + 15 `ScreenTests` for the new mode arms + 2 `ConPtyHostTests` for resize and Job Object. Anchor commit is PR #100 (the post-Stage-6 stability point). |
| `baseline/stage-7-claude-roundtrip` | [#131](https://github.com/KyleKeane/pty-speak/pull/131), [#132](https://github.com/KyleKeane/pty-speak/pull/132), [#134](https://github.com/KyleKeane/pty-speak/pull/134), [#135](https://github.com/KyleKeane/pty-speak/pull/135), [#138](https://github.com/KyleKeane/pty-speak/pull/138), [#140](https://github.com/KyleKeane/pty-speak/pull/140), [#141](https://github.com/KyleKeane/pty-speak/pull/141), [#142](https://github.com/KyleKeane/pty-speak/pull/142), [#143](https://github.com/KyleKeane/pty-speak/pull/143) | _(post-Stage-5a preview line; NVDA-verified 2026-05-03)_ | Stage 7 ship: Claude Code roundtrip end-to-end + the validation gate before the Output / Input framework cycles. 11 sequenced PRs (A → K) plus the doc-purpose wrap-up PR-L. PR-A (#131): env-scrub PO-5 — `EnvBlock` builds an explicit `lpEnvironment` block with allow-list + deny-list-overrides. PR-B (#132): extensible `ShellRegistry` + `PTYSPEAK_SHELL` startup override. PR-C (#134): hot-switch hotkeys `Ctrl+Shift+1` / `Ctrl+Shift+2` with mid-session ConPtyHost teardown + respawn. PR-D (#135): NVDA validation matrix expansion + first STAGE-7-ISSUES seeding. PR-E: `Ctrl+Shift+G` runtime debug-log toggle. PR-F: `Ctrl+Shift+H` health-check + `Ctrl+Shift+B` incident marker + 5s background heartbeat. PR-G (#138): `Ctrl+Shift+;` dispatcher-deadlock fix (`FlushPending(500).Result` sync-over-async caught by F# `task { }` capturing the SynchronizationContext). PR-H (#140): 500-char streaming-announce stopgap (tracked by Issue #139 for Stream-profile-driven removal). PR-I (#141): silent reader-loop shutdown on shell-switch (`ChannelClosedException` mis-classified as a parser error) + `currentShell` mutable so heartbeat / health-check report the post-switch identity. PR-J (#142): PowerShell as third built-in shell; hotkeys reordered to `+1`=cmd / `+2`=PowerShell / `+3`=Claude (PowerShell is the diagnostic control shell — always installed, no auth, fast prompt — sitting next to cmd makes infrastructure-vs-claude isolation one keystroke away); `Ctrl+Shift+H` liveness probe (`Process.GetProcessById` alive/dead flag); `Ctrl+Shift+D` inline shell-process snapshot announce. PR-K (#143): env-scrub allow-list expanded with the Windows runtime baseline (`SystemRoot`, `WINDIR`, `TEMP`, `ProgramFiles`, `PATHEXT`, `PSModulePath`, `USERNAME`, `PROCESSOR_*`, etc. — 24 layer-2 names readable from registry by any unprivileged process; security posture unchanged) after the empirical 2026-05-03 NVDA pass surfaced PowerShell + claude.exe both dying on spawn because the original 7-name allow-list stripped vars they needed to initialise; misleading "Env-scrub: stripped 0 variables" log line replaced with `"kept K of M parent vars; dropped D as sensitive (deny-list)"` plus `ParentCount` + `KeptCount` plumbed through `EnvBlock.Built` → `PtySession` → `ConPtyHost`. Stage 7 substrate spec lives in `spec/tech-plan.md` §7 + §7.5 (PR-C/PR-J/PR-K-extended); PO-5 mitigation in `SECURITY.md` row PO-5 (PR-A/PR-K-extended). NVDA-verified end-to-end on the post-PR-K preview: cmd / PowerShell / Claude all spawn, stay alive, and announce correctly under hot-switch. Anchor commit is PR-K (#143) at `001ec54` — the most recent Stage 7 constituent that completes the substrate. |
| `baseline/stage-5a-diagnostic-logging` | [#102](https://github.com/KyleKeane/pty-speak/pull/102), [#103](https://github.com/KyleKeane/pty-speak/pull/103), [#109](https://github.com/KyleKeane/pty-speak/pull/109), [#111](https://github.com/KyleKeane/pty-speak/pull/111), [#114](https://github.com/KyleKeane/pty-speak/pull/114), [#116](https://github.com/KyleKeane/pty-speak/pull/116), [#121](https://github.com/KyleKeane/pty-speak/pull/121), [#122](https://github.com/KyleKeane/pty-speak/pull/122) | _(multi-preview cycle; latest in `v0.0.1-preview.43`)_ | Stage 5a ship: diagnostic logging surface that made post-Stage-5/6 manual NVDA verification efficient. `Terminal.Core/FileLogger.fs` (#102) implements `ILogger`/`ILoggerProvider` directly against `Microsoft.Extensions.Logging.Abstractions` with off-thread `Channel<LogEntry>` drain (256 capacity, `BoundedChannelFullMode.Wait`, `SingleReader=true`). Per-session-per-day-folder layout (#103) so a session is one file. Filename refinement to `pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log` (#121, [Issue #107](https://github.com/KyleKeane/pty-speak/issues/107)) — full datetime + millisecond tie-breaker so the file is self-describing when extracted from its day-folder context. `Ctrl+Shift+L` opens the logs root in File Explorer; `Ctrl+Shift+;` (#111) copies the active session's log to the clipboard for one-keystroke bug-report sharing — `FileShare.ReadWrite` (#114) matches the writer's policy so the read works while the writer is active. `FileLoggerSink.FlushPending(timeoutMs)` API (#122) — TCS-barrier so the copy hotkey waits for in-flight entries to reach disk before reading. PRs #109 (streaming-path INFO instrumentation) + #116 (`AllHashHistory` spinner-gate fix) closed the post-Stage-5 streaming-silence bug that the diagnostic logging itself enabled the maintainer to capture. Spec §5a (formalized per chat 2026-05-03 maintainer authorization). Anchor commit is PR #122 — the most recent constituent that completes the Stage 5a scope. |

## Pending checkpoint tags

Tags listed in the table above that have **not yet been pushed to
the remote** because tag pushes can't run from the development
sandbox (the harness's git proxy returns 403 on tag refs). A
maintainer should push them from a local machine when convenient;
the rows in "Current checkpoints" already reference them as if they
exist, since the tag names are deterministic.

After pushing, **delete the matching row from this section** so it
stays accurate.

| Tag | Push commands |
|---|---|
| `baseline/stage-0-ci-release` | <pre>git fetch origin main<br>git tag -a baseline/stage-0-ci-release \\<br>  8c261b75cafffa223af07464b298621d934b4f22 \\<br>  -m "Stage 0 ship: CI + release pipeline working; v0.0.1-preview.15 shipped from this state"<br>git push origin baseline/stage-0-ci-release</pre> |
| `baseline/stage-1-conpty-hello-world` | <pre>git fetch origin main<br>git tag -a baseline/stage-1-conpty-hello-world \\<br>  c245564469a4f8f2f920ab1ee212b2e2cceac0c3 \\<br>  -m "Stage 1 ship: Terminal.Pty library; ConPtyHost spawns cmd.exe under ConPTY"<br>git push origin baseline/stage-1-conpty-hello-world</pre> |
| `baseline/stage-2-vt-parser` | <pre>git fetch origin main<br>git tag -a baseline/stage-2-vt-parser \\<br>  8c5ced25346a053636beec968a72ca2fe0e61dfa \\<br>  -m "Stage 2 ship: Terminal.Parser implementing Williams' VT500 state machine"<br>git push origin baseline/stage-2-vt-parser</pre> |
| `baseline/stage-3a-screen-model` | <pre>git fetch origin main<br>git tag -a baseline/stage-3a-screen-model \\<br>  9c30fc49a25ef2de6bf0491f441d631512ed9fe7 \\<br>  -m "Stage 3a ship: ColorSpec/SgrAttrs/Cell/Cursor/Screen in Terminal.Core"<br>git push origin baseline/stage-3a-screen-model</pre> |
| `baseline/stage-3b-wpf-rendering` | <pre>git fetch origin main<br>git tag -a baseline/stage-3b-wpf-rendering \\<br>  0a5ee22fb490a70982724b614bf69249e0b512da \\<br>  -m "Stage 3b ship: WPF TerminalView + end-to-end ConPty→Parser→Screen→View"<br>git push origin baseline/stage-3b-wpf-rendering</pre> |
| `baseline/stage-4-uia-document-text-pattern` | <pre>git fetch origin main<br>git tag -a baseline/stage-4-uia-document-text-pattern \\<br>  f415802d652663490abb49db2c4ec905ddfc6e67 \\<br>  -m "Stage 4 ship: UIA Document role + Text pattern + Line/Word/Character/Document review-cursor navigation; NVDA-verified end-to-end on v0.0.1-preview.26"<br>git push origin baseline/stage-4-uia-document-text-pattern</pre> |
| `baseline/stage-11-velopack-auto-update` | <pre>git fetch origin main<br>git tag -a baseline/stage-11-velopack-auto-update \\<br>  3050786def752f0ae83a2bcca0157b0f7effe9b5 \\<br>  -m "Stage 11 ship: Velopack auto-update via Ctrl+Shift+U with NVDA progress announcements; NVDA-verified end-to-end via preview.25 → preview.26 self-update"<br>git push origin baseline/stage-11-velopack-auto-update</pre> |
| `baseline/stage-4b-process-cleanup-diagnostic` | <pre>git fetch origin main<br>git tag -a baseline/stage-4b-process-cleanup-diagnostic \\<br>  999db6986b18e9467de1918301b5c6a8d8aec144 \\<br>  -m "Stage 4b ship: Ctrl+Shift+D launches bundled process-cleanup diagnostic for screen-reader-aware verification of Alt+F4 / X-button close paths; spec §4b"<br>git push origin baseline/stage-4b-process-cleanup-diagnostic</pre> |
| `baseline/stage-4a-claude-code-substrate` | <pre>git fetch origin main<br>git tag -a baseline/stage-4a-claude-code-substrate \\<br>  42ad7c747034a891c69c6c1b7841a37308cfcbcd \\<br>  -m "Stage 4a ship: Claude Code rendering substrate — DECTCEM + DECSC/DECRC + 256/truecolor SGR + alt-screen 1049 back-buffer + OSC 52 silent drop; spec §4a"<br>git push origin baseline/stage-4a-claude-code-substrate</pre> |
| `baseline/stage-5-streaming-coalescer` | <pre>git fetch origin main<br>git tag -a baseline/stage-5-streaming-coalescer \\<br>  e2f62f98da4dc54dceffb84c6d92ebc5aa7f0008 \\<br>  -m "Stage 5 ship: streaming-output Coalescer with FNV-1a dedup + spinner suppression + leading/trailing-edge debounce + alt-screen flush barrier + AnnounceSanitiser chokepoint; functional end-to-end as of PR #116"<br>git push origin baseline/stage-5-streaming-coalescer</pre> |
| `baseline/stage-6-keyboard-input` | <pre>git fetch origin main<br>git tag -a baseline/stage-6-keyboard-input \\<br>  ae4b038419b3409296cb413ddfb57dbe09bdcb36 \\<br>  -m "Stage 6 ship: pty-speak becomes interactive — KeyEncoding + bracketed paste + focus reporting + dynamic resize + Job Object lifecycle; anchor at PR #100 post-Stage-6 stability point"<br>git push origin baseline/stage-6-keyboard-input</pre> |
| `baseline/stage-5a-diagnostic-logging` | <pre>git fetch origin main<br>git tag -a baseline/stage-5a-diagnostic-logging \\<br>  f1b95178710b5a81ba8a0577a6861322795be64d \\<br>  -m "Stage 5a ship: diagnostic logging surface — FileLogger.fs + per-session-per-day-folder layout + Ctrl+Shift+L/Ctrl+Shift+; hotkeys + FlushPending TCS-barrier; spec §5a; anchor at PR #122 (FlushPending) as the most recent Stage 5a constituent"<br>git push origin baseline/stage-5a-diagnostic-logging</pre> |
| `baseline/stage-7-claude-roundtrip` | <pre>git fetch origin main<br>git tag -a baseline/stage-7-claude-roundtrip \\<br>  001ec5484d442b8a1a7753a707aad6b2d5894cfa \\<br>  -m "Stage 7 ship: Claude Code roundtrip end-to-end + env-scrub PO-5 + shell registry (cmd / PowerShell / Claude) + hot-switch hotkeys + diagnostic surface (Ctrl+Shift+G/H/B + heartbeat) + 11 sequenced PRs A through K + doc-purpose wrap-up PR-L; NVDA-verified 2026-05-03 (all three shells spawn, stay alive, and announce); anchor at PR #143 (PR-K env-scrub allow-list expansion with Windows runtime baseline) as the most recent Stage 7 substrate constituent"<br>git push origin baseline/stage-7-claude-roundtrip</pre> |

## Rolling back to a checkpoint

### Read-only inspection (browse the tree)

```bash
git fetch origin --tags
git checkout baseline/<checkpoint-name>
```

This puts you in detached-HEAD state on the checkpoint commit. Use it
to read code, run tests, copy snippets out, or build the artifacts of
that point in time.

### Restart a feature branch from a checkpoint

```bash
git fetch origin --tags
git checkout -b feature/<short-slug> baseline/<checkpoint-name>
```

Use this when a later stage's work has gone sideways and you want to
start over from the last known-good state. Push the new branch and
open a PR as usual.

### Reset `main` (last resort, destructive)

If a series of bad merges has polluted `main` and individual reverts
aren't tractable:

```bash
git fetch origin --tags
git checkout main
git reset --hard baseline/<checkpoint-name>
git push --force-with-lease origin main
```

This **rewrites public history** on the default branch. Coordinate
with all maintainers first. Prefer per-merge `git revert` PRs unless
the pollution is too tangled.

## Adding a new checkpoint

When a stage's work ships and its validation matrix passes (per
[`spec/tech-plan.md`](../spec/tech-plan.md) and
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)), mark
the merge commit as a checkpoint so you can return to it later:

1. **Push an annotated tag** at the merge SHA. If you have local
   push access:
   ```bash
   git fetch origin main
   git tag -a baseline/<stage-or-purpose> <merge-sha> \
     -m "<one-paragraph description>"
   git push origin baseline/<stage-or-purpose>
   ```
   Tag pushes don't trigger the release workflow (it's keyed on
   `release: published`), so this is safe.

   **If you can't push the tag immediately** (e.g. an automated
   contributor in a sandbox where tag pushes aren't allowed), add
   a row to the **"Pending checkpoint tags"** section below with
   the exact commands so a human maintainer can push it later
   from their workstation. Don't skip this — orphan baseline rows
   in the table above with no actual tag are confusing for
   readers.

2. **Apply the `stable-baseline` label** to the PR that landed the
   checkpoint. PR sidebar → *Labels* → `stable-baseline`. The label
   is auto-created if it doesn't exist.

3. **Add a row** to the "Current checkpoints" table above with the
   tag name, PR link, release link (if applicable), and a one-line
   description of what makes this state stable.

4. **If the checkpoint corresponds to a shipped release**, link the
   release in the table column. Otherwise leave that column empty —
   not every checkpoint is a published release (Stage 0 was, but a
   mid-stage refactor checkpoint may not be).

## Why checkpoints matter

`pty-speak` follows a walking-skeleton plan with twelve stages
([`spec/tech-plan.md`](../spec/tech-plan.md)). Each stage adds a
narrow vertical slice on top of the previous skeleton. When a later
stage's experimental work breaks something fundamental, returning to
the most recent known-good checkpoint is faster than unpicking a
stack of partial commits.

The first checkpoint, `baseline/stage-0-ci-release`, was deliberately
placed *after* the deployment-pipe diagnostic loop that produced
`v0.0.1-preview.{1..14}`. Restarting from this point means starting
Stage 1 from a state where the build, test, and release workflows
all demonstrably work end-to-end. The lessons from that diagnostic
loop are captured in
[`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md) under "Common
pitfalls" so they don't have to be re-learned.
