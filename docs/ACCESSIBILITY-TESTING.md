# Manual smoke-test matrix

This is the comprehensive manual validation gate every release must
pass. CI verifies behaviour at the producer level (UIA peer
construction, parser correctness, screen-buffer mutation, COM
marshalling); this document verifies behaviour at the consumer level
(what NVDA actually says, what the installer actually does, what
happens to the user's process tree). The two are different enough
that we cannot replace one with the other — see "Why this is manual"
at the bottom for the longer rationale.

The filename retains the historical
`ACCESSIBILITY-TESTING.md` because accessibility testing is the bulk
of the matrix, but the scope is **all manual checks every release
must pass**: artifact integrity, process-launch hygiene, UIA, NVDA,
earcons, auto-update.

## DOGFOOD markers

Sub-sections preceded by an `<!-- DOGFOOD -->` HTML comment are
surfaced in the runtime `Diagnostics → Open Manual Tests` quickref
(Cycle 38a-followup). The menu item reads this file from the
deployed location next to `Terminal.App.exe`, filters to marked
`### `-level sub-sections via
[`src/Terminal.Core/ManualTestsHtml.fs`](../src/Terminal.Core/ManualTestsHtml.fs),
renders Markdig HTML, and opens it in the default browser for
NVDA browse-mode heading navigation. Add or remove the marker on a
per-section basis to grow or prune the quickref — no code change
required. The marker is a normal HTML comment so it stays invisible
in any rendered markdown view.

The initial set marks the most-recently-relevant sections: Cycle
38a (canonical interaction corpus baseline), Cycle 37 (UIA listbox
peer), Cycle 36 (substrate-inversion arc matrix backfill), Cycle
29b (SelectionProfile), Cycle 28 (Window menu), plus the standing
"Artifact integrity" and "Launch and process hygiene" sections that
run on every release.

## How to use this document

1. Cut the release per
   [`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md). Once the workflow
   has produced the artifacts on a GitHub Release, walk this matrix
   from top to bottom on a clean Windows VM.
2. Run every section whose stage has shipped, plus every "always run"
   section (artifact integrity, launch / process). Mark each row
   PASS / FAIL / N/A.
3. **If a row fails, consult the "Diagnostic decoder" paragraph that
   follows that section.** Each decoder maps an observed failure to
   the subsystem most likely to be responsible, so the next debugging
   step is unambiguous.
4. Open an issue for any FAIL with the row name in the title; attach
   NVDA Event Tracker output for any speech-related failure.
5. The release isn't promoted from prerelease to "latest" until every
   required-stage row is PASS or N/A.

## Test environment

- **Clean Windows 10 22H2 VM** and **Clean Windows 11 23H2+ VM**, both
  on `x64`. We test on both because UIA implementations differ between
  Windows versions in subtle ways.
- **NVDA 2024.x or newer**, installed with default settings, plus the
  [Event Tracker add-on](https://addons.nvda-project.org/addons/evtTracker.en.html).
- **Narrator** as shipped with Windows.
- *Optional:* JAWS most recent stable. JAWS regressions are tracked
  but do not block releases.
- A second audio output device available (USB headset is fine) so that
  earcons and TTS can be routed separately.

### NVDA settings recommendation

NVDA's keyboard hook controls char- and word-level typing speech
independently of pty-speak. The recommended settings for
`pty-speak` use under NVDA Settings → Keyboard:

- **Speak typed characters**: user preference. If `Off` is desired
  for pty-speak but `On` (e.g. "Edit controls") in other apps, the
  setting is global; there's no per-app override without a custom
  NVDA add-on.
- **Speak typed words**: `Off`. Words assemble from typed chars
  via NVDA's keyboard hook, independent of any UIA event pty-speak
  raises. Leaving it `On` produces a word announce on each space /
  punctuation as the user types — typically undesirable inside a
  shell where command tokens vary line-by-line.
- **Speech interruption for typed character**: `On`. Pairs with
  the ControlType=Edit + UIA caret-event surface (Cycle 46 PR-C)
  so typing always interrupts in-flight speech, even with the two
  settings above turned off.

Cycle 47 follow-up post-preview.114 also added an app-side
typing-window gate on the UIA Text-pattern materialiser
(`ContentHistoryMaterialiser.materialise` → `tailTextWithMarkersSealedOnly`
when keystrokes are fresh) so NVDA's `ITextProvider` polling
doesn't surface mid-keystroke active-span deltas as inserted text.
The gate runs independent of the user's NVDA settings — see
matrix row Cycle 47-21.

## Test data

> **Status (Stage 3b on `main`):** the `tests/fixtures/` directory and
> the `Terminal.App --replay` flag below are **planned tooling** — they
> do not yet exist. They land alongside the stage that first needs
> them (the spinner fixture lands with Stage 5, the selection-list
> fixture with Stage 8, etc.). Listed here so the canonical filenames
> are reserved.

Planned reusable fixtures under `tests/fixtures/`:

- `dir-output.txt` — raw byte capture of `cmd.exe /c dir`.
- `claude-thinking.txt` — captured Ink spinner sequence for the
  spinner-suppression test.
- `claude-selection-list.txt` — captured "Edit / Yes / Always / No"
  prompt.
- `truecolor-rainbow.txt` — sweep of `\x1b[38;2;r;g;bm` colors.
- `python-traceback.txt` — multi-line red traceback for review-mode `e`.

Once the developer-mode replay tool ships, replay these by piping
into the terminal's stdin: `Terminal.App --replay tests/fixtures/...`.

## Always-run sections (every release)

These two sections run on every release regardless of stage, because
they catch regressions in things that don't have a "stage" but break
loudly when wrong.

<!-- DOGFOOD -->
### Artifact integrity (run before installing)

| Test                              | Procedure                                       | Expected                                                          |
|-----------------------------------|-------------------------------------------------|-------------------------------------------------------------------|
| All Velopack assets present       | Inspect the GitHub Release page                 | `*-Setup.exe`, `*-full.nupkg`, `releases.win.json`, `assets.win.json`, `RELEASES`. Delta-nupkg present iff this is not the first release on the channel. |
| Setup.exe non-zero                | `(Get-Item .\pty-speak-Setup.exe).Length`       | `> 50000000` (≈ 50 MB) — a self-contained .NET 9 publish is around 60-70 MB. A truncated upload typically lands at a few KB. |
| Authenticode signature status     | `Get-AuthenticodeSignature .\*Setup.exe \| Select Status, StatusMessage` | **For unsigned previews** (current `0.0.x-preview.N` line): `Status = NotSigned`. **For signed releases** (post-`v0.1.0`, once SignPath is re-enabled per `docs/RELEASE-PROCESS.md`): `Status = Valid`. |
| Hash matches release attachment   | `Get-FileHash .\*Setup.exe -Algorithm SHA256`   | Hash matches the value in the GitHub Release body if one is published; this gate exists primarily for forensic-trace purposes (proving which build was used during smoke testing). |

**Diagnostic decoder for artifact integrity:**

- **Asset missing** → `release.yml` step 12 (the "Verify required
  Velopack artifacts" gate added in PR #43) didn't fail when it
  should have, OR the softprops upload silently dropped the file.
  Check the workflow log for the run that produced this release;
  inspect the `Velopack pack smoke artifact` upload step's file list.
- **Setup.exe < 1 MB** → Velopack pack failed silently and produced
  an empty installer. The `dotnet publish` step's `--self-contained`
  flag may have been dropped.
- **Authenticode `Valid` on a preview line** → Unexpected; the
  unsigned-preview cutover may have regressed. Diff `release.yml`
  against the documented unsigned config in `docs/RELEASE-PROCESS.md`.
- **Authenticode `NotSigned` on a `v0.1.0+` release** → SignPath
  integration regressed; the workflow needs `signpath/github-action-submit-signing-request@v1`
  back per `docs/RELEASE-PROCESS.md` "Workflow changes when signing
  returns".

<!-- DOGFOOD -->
### Launch and process hygiene (run on every install)

| Test                              | Procedure                                       | Expected                                                          |
|-----------------------------------|-------------------------------------------------|-------------------------------------------------------------------|
| Single window appears             | Run `Setup.exe`, then launch from Start menu    | **Exactly one** window (the WPF terminal surface). No empty parent console window behind it. |
| Window title shows version        | Look at the window title bar                    | "pty-speak" plus the version number (matches the release tag).    |
| Process tree is one-deep          | Open Task Manager → Details, sort by Image Name | Exactly one `Terminal.App.exe` AND one child `cmd.exe` (the ConPTY child). No leftover `conhost.exe` parent. |
| Window closes cleanly             | Close the window via the X button               | Both `Terminal.App.exe` and the child `cmd.exe` exit within ~2 s. Task Manager shows neither image afterwards. |
| Quit via Alt+F4 closes cleanly    | Press Alt+F4                                    | Same as above — clean exit, no orphans.                           |
| Re-launch after exit              | Launch from Start menu again                    | New window opens cleanly. (Catches state-file corruption / single-instance regressions if those ship later.) |

**Lifecycle inflection points — when to re-run this whole
section, not just the "every install" cadence:**

The "Window closes cleanly" + "Quit via Alt+F4" rows are
the long-deferred "Test 8 / process cleanup on close" check
flagged throughout `docs/SESSION-HANDOFF.md`. The strategic
review on 2026-05-01 made these a **recurring acceptance
check** rather than a single v0.1.0 gate. Re-run this entire
section against:

1. **Current `v0.0.1-preview.26` baseline.** Establishes
   whether the shipped code already has any orphan-process
   issues independent of upcoming work. Run before Stage 4a
   starts so any failure here gets diagnosed against
   pre-Stage-4.5 code, not bundled into 4.5's diffs.
2. **After Stage 4a ships.** Confirms the alt-screen
   back-buffer rework didn't introduce process leaks (the
   buffer-switching code itself is in F# and shouldn't
   touch ConPTY lifetime, but verifying is cheap).
3. **After Stage 6 ships.** Stage 6 is the natural home for
   Job Object lifecycle (per `spec/tech-plan.md` §160's
   note); this is the most important pass — if Job Object
   lands here, this section verifies it works.
4. **After Stage 7 ships.** Stage 7 lets Claude Code
   spawn its own subprocesses (npm install, etc.); verify
   the cascade cleanup path works for grandchild
   processes, not just the immediate ConPTY child.
5. **Final firm gate before any v0.1.0+ tag.** Per
   `SECURITY.md` row PO-2 (Job Object lifecycle); a v0.1.0
   release with orphan-process accumulation is a
   regression we can't ship.

Record each pass in the "Recording results" section below
with a per-stage note (e.g. "preview.26 baseline: PASS",
"post-Stage-4.5 v0.0.1-preview.27: PASS"). If a pass
fails, file it as a regression issue against the most
recent stage that touched lifecycle.

**Diagnostic decoder for launch and process hygiene:**

- **Two windows appear, second is an empty console** → `OutputType`
  regression in `src/Terminal.App/Terminal.App.fsproj`. Confirm it's
  set to `WinExe`, not `Exe`. PR #44 fixed this; if it's recurred,
  someone reverted the property or added `<DisableWinExeOutputInference>`.
- **Two windows appear, second is also a WPF surface** → New
  Application code path is creating a second window. Check
  `src/Terminal.App/Program.fs` for added `MainWindow()` constructions.
- **Title bar shows wrong version** → `Directory.Build.props`
  `<Version>` not bumped, OR Velopack `--packVersion` mismatch.
  Check the workflow run's "Resolve version" step output.
- **No `cmd.exe` child** → ConPTY child failed to spawn. The Stage 3b
  fallback path swallows this silently — check `src/Terminal.App/Program.fs`'s
  `ConPtyHost.start` error branch and Event Viewer for child-process
  errors.
- **Orphan `cmd.exe` after window close** → ConPTY shutdown not
  signalling the child. Check `App.Exit` cleanup in `Program.fs` and
  `ConPtyHost.Dispose` / `ClosePseudoConsole` ordering.
- **Window doesn't open at all** → WPF runtime crash on startup, OR
  `MainWindow.SourceInitialized` handler crashed (PRs #54/55/56
  added handlers there). Check Event Viewer → Application for a
  .NET runtime exception trace.

## Stage validation matrix

For every release, run the rows that correspond to stages already
shipped. A row is **PASS** only if the screen reader / observed
behaviour matches the expected column verbatim.

### Stage 0 — empty window (currently shipped: `v0.0.1-preview.15`)

| Test                              | Procedure                                       | Expected NVDA behaviour                                          |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Window opens                      | Launch app                                      | "pty-speak terminal, window" (from `AutomationProperties.Name`)  |
| Window title                      | Press Insert+T                                  | NVDA announces "pty-speak"                                       |
| Inspect.exe shows correct surface | Open Inspect.exe, hover over the window         | `AutomationId="pty-speak.MainWindow"`, `ControlType=Window`      |

**Diagnostic decoder for Stage 0:**

- **NVDA silent on focus** → `AutomationProperties.Name` removed
  from `MainWindow.xaml`, OR the WPF UIA tree isn't being created
  (check Inspect.exe — if Inspect can't see the window either, the
  problem is below WPF).
- **Wrong AutomationId in Inspect.exe** → `MainWindow.xaml`'s
  `AutomationProperties.AutomationId` was changed; this is a
  documented identity that downstream tooling (FlaUI tests) keys on.

### Stage 3b — first visible terminal surface (next preview)

This row applies to any preview cut from `main` between Stage 3b and
Stage 4. UIA exposure of the buffer is not yet present; NVDA reads
the WPF `TextBlock`-equivalent output via its fallback diff path.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| `cmd.exe` startup is visible      | Launch app                                      | The window shows live `cmd.exe` startup output (banner + prompt) |
| Output renders without flicker    | Watch the buffer for ~5 seconds                 | Text remains stable; no obvious tear/blink                       |
| ANSI 16 colours render            | Run `dir /a` (cmd colourises filenames)         | Different file types render in different foreground colours      |
| Window closes cleanly             | Close the window                                | Process exits; no orphan `cmd.exe` (verify via Task Manager)     |

**Diagnostic decoder for Stage 3b:**

- **No cmd.exe banner appears** → ConPTY spawn silently failed
  (`Program.fs`'s `Ok host`/`Error _` branch swallows errors at
  Stage 3b). Run the app from a console with `--debug` (once that
  flag exists) or check Event Viewer for `CreatePseudoConsole`
  errors.
- **Banner appears but updates freeze** → Reader-loop task crashed.
  The `try/with :? OperationCanceledException -> () | _ -> ()` in
  `startReaderLoop` swallows everything; check Event Viewer for
  unhandled task-scheduler exceptions.
- **Visible flicker / tearing** → `Screen.Apply` mutating during
  `OnRender`. Stage 4 substrate (`SnapshotRows`) was meant to
  prevent this; verify `TerminalView.OnRender` reads `_screen`
  inside the WPF dispatcher (Stage 5+ moves the parser off the
  dispatcher and tearing becomes a real risk).
- **No colour on `dir /a`** → SGR parser regression. Check the
  `CSI m` branch in `Screen.fs`; `tests/Tests.Unit` covers most
  SGR shapes — if those pass but visual output is monochrome, the
  brush mapping in `TerminalView.cs` is suspect.

### Stage 4 — UIA Document role + Text pattern (shipped through PR #56)

The Stage 4 architecture settled across four primary PRs and
later cleanup. The shipping architecture is the
`AutomationPeer.GetPattern` override path — the WM_GETOBJECT
subclass hook + raw provider that Stages 4 PR A / PR B added
were kept as MSAA-only fallback after the PR #56 pivot, then
deleted in audit-cycle PR-C as dead-code that no real consumer
exercised. If you're spelunking commit history for the
WM_GETOBJECT path, look at the merge commits between
`v0.0.1-preview.20` and PR-C's merge (May 2026).

- **PR #48 (Stage 4a)** — `TerminalAutomationPeer` exposes Document
  role, ClassName, Name, IsControlElement, IsContentElement.
- **PR #54 / #55** — exploratory WM_GETOBJECT subclass hook +
  `IRawElementProviderSimple` raw provider; **deleted in
  audit-cycle PR-C**.
- **PR #56** — pivot to `AutomationPeer.GetPattern` override
  (the public-virtual entry point that's reachable from
  external assemblies, vs. `GetPatternCore` which isn't),
  plus a FlaUI integration test pinning `DocumentRange.GetText`.
  This is the shipping path.
- **PR #59 / #60 / #66 / #68** — navigation (Line, Character,
  Word), focus-into-TerminalSurface, version-suffix in
  Title / AutomationProperties.Name, real word-navigation.

The architectural finding from PR #56 is that UIA3 clients
(NVDA, Inspect.exe, FlaUI.UIA3) dispatch `WM_GETOBJECT` with
`UiaRootObjectId` (-25), not `OBJID_CLIENT` (-4); answering
`UiaRootObjectId` with a simple provider breaks the UIA tree
because UIA needs a fragment-root provider for navigation. The
ship architecture overrides `GetPattern` on the existing peer
instead, leaving WPF's UIA tree untouched and adding the Text
pattern at the right element.

| Test                              | Procedure                                       | Expected NVDA / UIA behaviour                                    |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Window opens                      | Launch app                                      | "pty-speak, document"                                            |
| Inspect.exe shows correct surface | Open Inspect.exe, hover over the terminal       | `ControlType=Document`, `ClassName="TerminalView"`, `Name="Terminal"` |
| Inspect.exe shows Text pattern    | Inspect.exe → Patterns panel on the terminal    | Text pattern present; `DocumentRange.GetText(-1)` returns the screen contents |
| Review cursor reads current line  | NVDA+Numpad8 over the terminal                  | NVDA reads the prompt line                                       |
| Review cursor moves by line       | NVDA+Numpad7 / NVDA+Numpad9                     | NVDA reads previous / next line of the buffer                    |
| Review cursor moves by word       | NVDA+Numpad4 / NVDA+Numpad6                     | NVDA reads previous / next word                                  |
| Review cursor moves by character  | NVDA+Numpad1 / NVDA+Numpad3                     | NVDA reads previous / next character                             |
| Empty line is announced           | Move review cursor to a blank row               | NVDA says "blank" — *not* the previous row, *not* silence        |

**Diagnostic decoder for Stage 4:**

- **Inspect.exe shows Document but NO Text pattern** → `GetPattern`
  override regression on `TerminalAutomationPeer` (PR #56). Confirm
  `src/Terminal.Accessibility/TerminalAutomationPeer.fs` still has
  the `override _.GetPattern(patternInterface: PatternInterface)`
  member returning the `textProvider` for `PatternInterface.Text`.
- **Inspect.exe shows Document, Text pattern present, but
  `DocumentRange.GetText(-1)` returns empty string** → Either
  `TerminalView._screen` is null at query time (Stage 3b's
  `compose` did not run before UIA queried, or the screen got
  detached) OR `Screen.SnapshotRows` is returning an empty array.
  CI's `tests/Tests.Ui/TextPatternTests.fs` asserts a length floor
  of `30 × 120 + 29 = 3629`; if that test passes but manual smoke
  fails, the runtime composition order is the suspect.
- **NVDA silent on review-cursor commands** → Two possibilities:
  (1) Text pattern is missing per the previous decoder; verify
  with Inspect.exe first. (2) Text pattern is present and returns
  text, but NVDA isn't binding to it — usually means the peer is
  attached to the wrong WPF element (check `TerminalView.OnCreateAutomationPeer`),
  or NVDA is in browse mode (press NVDA+Space to toggle).
- **Inspect.exe crashes when hovering the terminal** → A
  WPF UIA peer is returning a bad provider. Most likely
  `TerminalAutomationPeer.GetPattern` is throwing, or
  `TerminalTextProvider`'s `DocumentRange` access is throwing
  on a null `Screen`. Check the F# source first; the WPF
  exception will be surfaced in Inspect.exe's status bar.
  (Audit-cycle PR-C deleted the WM_GETOBJECT raw-provider
  fallback path that used to live here; if you somehow
  reintroduce it, the failure mode PR #56 found around
  `UiaRootObjectId == -25` is the one to watch.)
- **CI's `TextPatternTests` passes but manual NVDA reads stale
  text** → `TerminalTextRange` is currently a snapshot at
  capture time; future stages add stale-detection via
  `Screen.SequenceNumber`. For now, this is expected behaviour,
  not a regression — record it as N/A unless content is wildly
  out of date.

### Stage 5 — streaming output

| Test                              | Procedure                                       | Expected NVDA behaviour                                          |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| `echo hello`                      | Run inside the terminal                         | NVDA says "hello" exactly once                                   |
| `dir`                             | Run inside the terminal                         | NVDA reads each line in order with brief pauses                  |
| Busy loop printing dots for 5 s   | Run a script that prints `.` 100×/s             | NVDA does not get stuck; speech queue drains within ~1 s of end  |
| Cursor blink                      | Idle terminal with blinking cursor              | No announcements; only the cursor visibility flips               |
| Notification event tracking       | Event Tracker add-on enabled                    | One Notification event per coalesced flush, with non-empty `displayString` |

**Diagnostic decoder for Stage 5:**

- **NVDA says "hello" multiple times** → Coalescing window in the
  semantics layer is too short or absent; check Stage 5's
  flush-coalescing logic against `spec/tech-plan.md` §5.
- **NVDA frozen during busy-loop test** → Notification flooding;
  the dedup-by-row-hash rule in the PR template's accessibility
  checklist has been violated. Check Event Tracker for the
  notification volume; if it's > 50/s the producer is unbounded.
- **Cursor blink announces** → Cursor-visibility toggle is being
  routed through a notification path. Should be a pure render
  invalidation; check that Stage 5's blink handler doesn't call
  `RaiseNotificationEvent`.
- **Empty `displayString` in Event Tracker** → Notification text
  not assembled correctly, or control characters slipped in
  (PR-template rule "Control characters are stripped" violated).

### Stage 6 — keyboard input

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Type into PowerShell              | Type `ls` and Enter                             | Listing read by NVDA                                             |
| Up/Down arrow history             | After running `ls`, press Up                    | PowerShell echoes the previous command; NVDA reads new prompt   |
| Tab completion                    | Type `ls C:\W` then Tab                         | Completion appears; NVDA reads the completed path                |
| Ctrl+C interrupts                 | Run `ping localhost -t`, press Ctrl+C           | Process interrupts; NVDA reads the new prompt                    |
| **NVDA shortcut still works**     | Press Insert+T                                  | NVDA announces the window title (proves we did not swallow Insert) |
| **Caps Lock review still works**  | Press CapsLock+Numpad7                          | NVDA reads previous review line                                  |

**Diagnostic decoder for Stage 6:**

- **Insert+T does nothing** → `PreviewKeyDown` swallowed the
  Insert key. The PR-template rule "No NVDA modifier keys are
  swallowed" has been violated; the keyboard handler is intercepting
  too broadly. Filter modifier keys explicitly.
- **CapsLock+Numpad7 does nothing** → Same root cause as above
  (CapsLock is the alternate NVDA modifier).
- **Ctrl+C doesn't interrupt** → SIGINT not propagated to the
  ConPTY child. Verify Ctrl+C handling in the keyboard layer
  routes to the PTY's input stream rather than the WPF dispatcher.
- **Tab completion shows no UIA notification** → Stage 6's
  completion-detection wasn't shipped, or its OSC-133 / heuristic
  detector regressed. Acceptable to mark N/A until Stage 6 ships
  the heuristic.

### Stage 7 — Claude Code roundtrip

Stage 7 ships across four sequenced PRs (per
[`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05-12.md) Part 2):
PR-A (env-scrub PO-5; #131), PR-B (shell registry +
`PTYSPEAK_SHELL`; #132), PR-C (hot-switch hotkeys
`Ctrl+Shift+1`/`Ctrl+Shift+2`; #134), and PR-D (this matrix
expansion + `docs/STAGE-7-ISSUES.md` seeding). The matrix below
exercises the full surface end-to-end. Per
[`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) "Stage 7
implementation sketch (next)" §5, **gaps surfaced during this
matrix go into [`docs/STAGE-7-ISSUES.md`](archive/pre-cycle-45/STAGE-7-ISSUES.md)
with framework-taxonomy category tags — Stage 7's job is to
surface, not solve.**

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| **Default-shell launch (PR-B)**     | Launch pty-speak with `PTYSPEAK_SHELL` unset    | cmd.exe spawns; NVDA reads the cmd prompt; log line "Startup shell: Command Prompt (command line: cmd.exe)" appears in the active session log (press `Ctrl+Shift+D` to bundle the log into clipboard + a dated snapshot file, or `Ctrl+Shift+P` to open the data folder and navigate into `\logs`) |
| **Env-scrub empirical check (PR-A)** | At the cmd prompt, type `set` and press Enter | Output enumerates the child's env block. Confirm sensitive vars from the deny-list are ABSENT: `GITHUB_TOKEN`, `OPENAI_API_KEY` (any `*_TOKEN`/`*_SECRET`/`*_PASSWORD`/non-Anthropic `*_KEY`). Confirm allow-list vars are PRESENT: `PATH`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOME`, `TERM=xterm-256color`, `COLORTERM=truecolor`. Active session log includes "Env-scrub: stripped {N} variables before child spawn." (count only; never names or values) |
| **Claude-shell launch (PR-B)**      | Quit pty-speak; relaunch with `PTYSPEAK_SHELL=claude` (or with claude.exe on PATH and PR-C's hotkey approach below) | claude.exe spawns; NVDA reads the welcome screen; log line "Startup shell: Claude Code (command line: <resolved path>)" |
| **Welcome screen (Claude)**         | After Claude spawns, listen                     | NVDA reads the welcome screen                                    |
| **Prompt → response (Claude)**      | Type "Say hi", Enter                            | Claude responds; NVDA reads the streaming response               |
| **Review-cursor navigation**        | After Claude's response completes, press `Caps Lock + Numpad 7` / `Numpad 8` / `Numpad 9` (or `Insert + Up/Down/Left/Right` depending on NVDA layout) | NVDA's review cursor moves through the response by line / current-line / next-line; NVDA reads the line under the review cursor |
| **Spinner does not flood**          | Trigger a long-thinking response                | Spinner is announced at most once or as a periodic earcon; NVDA never freezes |
| **Tool-use prompt (Claude)**        | Ask Claude to edit a file in the current dir; when "Edit / Yes / Always / No" prompt appears, accept a choice | NVDA reads the prompt text. (Reads as flat text in PR-D; Stage 8 / Output framework's Selection profile fixes this — log the failure mode in `STAGE-7-ISSUES.md` with `[output-selection]` tag.) Pressing Enter accepts; Claude proceeds |
| **Hot-switch cmd → claude (PR-C)**  | At the cmd prompt, press `Ctrl+Shift+2`         | NVDA announces "Switching to Claude Code." → ~700ms pause → "Switched to Claude Code." → claude welcome reads. Active session log includes "Shell-switch: spawning Claude Code. CommandLine=<path>" |
| **Hot-switch claude → cmd (PR-C)**  | After claude is running, press `Ctrl+Shift+1`   | NVDA announces "Switching to Command Prompt." → ~700ms pause → "Switched to Command Prompt." → cmd prompt reads. Brief alt-screen residue is acceptable for v1 — log severity to `STAGE-7-ISSUES.md` with `[output-tui]` tag if it confuses the read |
| **Hot-switch resolve failure (PR-C)** | On a machine WITHOUT claude.exe on PATH, press `Ctrl+Shift+2` | NVDA announces "Cannot switch to Claude Code: not found on PATH." (sanitised). The existing cmd shell continues to work; the user is NOT dropped into a dead window |
| **Process-cleanup after switch (PR-C)** | After at least one Ctrl+Shift+1 / Ctrl+Shift+2 cycle, press `Ctrl+Shift+D` (autonomous diagnostic battery) | The battery's snapshot section in the bundled dump (clipboard + dated snapshot file) confirms no orphan `claude.exe` / cmd.exe / Terminal.App processes from the previous host. (Job Object's `KILL_ON_JOB_CLOSE` is the kernel-enforced cleanup.) Cycle 25b — Ctrl+Shift+D no longer auto-launches `test-process-cleanup.ps1`; that interactive close-and-recheck flow is now invoked manually from PowerShell when reproducing Job-cascade-kill regressions. |
| **Quitting cleanly**                | `/exit` (Claude), `exit` (cmd), or Ctrl+D       | Process exits; NVDA announces it; terminal returns to host shell or window closes |
| **PTYSPEAK_SHELL unrecognised value** | Launch pty-speak with `PTYSPEAK_SHELL=garbage` | cmd.exe still spawns (fallback); active session log includes "PTYSPEAK_SHELL=\"garbage\" not recognised; falling back to cmd.exe. Recognised values: cmd, claude." NVDA-bound behaviour is identical to default-shell launch |

**Diagnostic decoder for Stage 7:**

- **NVDA frozen during spinner** → Spinner-row dedup is broken.
  Check `tests/fixtures/claude-thinking.txt` replay through
  Stage 5's coalescing path; if the unit tests pass, the
  semantic-layer hashing is fine and the regression is in how
  Stage 7's spinner detector classifies the row. **This is also
  the headline finding the Output framework cycle's research
  phase consumes from `STAGE-7-ISSUES.md`** — log it with
  `[output-stream]` even if you also fix the regression.
- **Welcome screen silent** → Claude's TUI emits its first paint
  in a way that bypasses our coalescing entry point. Compare against
  Stage 5's `dir` test — if `dir` works but `claude` doesn't, the
  difference is Claude's use of OSC sequences that our parser may
  not be handling yet.
- **`/exit` leaves orphaned `claude` process** → Same class as
  Stage 3b's "orphan cmd.exe" — the ConPTY shutdown isn't
  signalling the child correctly.
- **Env-scrub empirical check shows a `*_TOKEN`/`*_SECRET` leak**
  → `EnvBlock.isDenied` regression. Re-run
  `tests/Tests.Unit/EnvBlockTests.fs` first; if the suffix-match
  fixture passes, the leak is in the parent-env collection step
  (the env var name didn't reach the deny-list). Symptom is
  PO-5-class — file as a security regression, not an inventory
  entry.
- **Allow-list var missing from `set` output** → `EnvBlock.allowedNames`
  edit landed without the spec-§7.2 sync. Cross-check
  `EnvBlockTests.allowedNames contains exactly the spec-7-2 baseline`
  fixture; if that passes, the parent process didn't have the
  expected var set in the first place (sandbox / launcher
  environment issue, not an env-scrub regression).
- **Hot-switch announce is silent** → `Ctrl+Shift+1`/`Ctrl+Shift+2`
  not in `TerminalView.AppReservedHotkeys`, OR `OnPreviewKeyDown`
  marked `Handled = true` before `InputBindings` fired. Same
  diagnosis path as the Stage 6 `Ctrl+V` paste regression: check
  the `AppReservedHotkeys` table first, then the filter ordering.
- **Hot-switch produces orphan child after several switches** →
  `ConPtyHost.Dispose` race with the new spawn. Job Object's
  `KILL_ON_JOB_CLOSE` should still cascade-kill on the next
  switch (each switch creates a new Job, closing the previous
  Job's last handle), but if `Ctrl+Shift+D` shows accumulating
  orphans across many switches, file as `[other]` with the
  exact sequence to reproduce.
- **Hot-switch announce reads but new shell never appears** →
  `wirePostSpawn` not invoked OR `SetPtyHost` callbacks not
  re-bound. Check the active session log for "ConPTY child
  spawned. Pid=<N>" + "Env-scrub: stripped {N}..." — if both
  are present but no shell output reaches NVDA, the reader
  loop didn't restart. If neither is present,
  `ConPtyHost.start` failed.

### Cycle 24 — SessionModel persistence

Cycle 24 ships the SessionModel persistence substrate end-to-end:
TOML config schema (24a), pure JSONL serializer (24b),
bounded-channel async file writer (24c), `Always`-mode
synchronous flush (24d-1), env-var VALUE sanitisation (24d-2),
and a diagnostic hotkey + this matrix (24e). The rows below
verify each layer's user-facing contract.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Mode change at startup            | Press `Ctrl+Shift+E` to open `config.toml` (auto-creates with defaults if missing); set `[session_model.persistence] mode = "session_log"`; save; relaunch (or press `Ctrl+Shift+1` to reload persistence config without restart per Cycle 25a's reload-on-switch) | App starts cleanly; active session log (press `Ctrl+Shift+D` and inspect the bundle's `--- FILELOGGER ACTIVE LOG ---` section in clipboard) contains `Config: [session_model.persistence] section parsed; mode=session_log, ...` immediately followed by `SessionModel persistence mode: session_log (output_dir=..., format=jsonl, max_session_size_mb=...)`; no Warning about "not yet implemented" |
| Open-config auto-create (Cycle 25a) | Delete `%LOCALAPPDATA%\PtySpeak\config.toml` if present; press `Ctrl+Shift+E` | NVDA announces "Created config file with defaults; opening." Default text editor opens to a fresh `config.toml` containing all four documented sections (`[session_model.persistence]`, `[startup]`, `[logging]`, plus `schema_version = 1`) with inline comments describing each value. Re-pressing `Ctrl+Shift+E` re-opens the existing file with the announcement "Opening config file." (no overwrite) |
| Open-data-folder hotkey (Cycle 25a) | Press `Ctrl+Shift+P` | Explorer opens at `%LOCALAPPDATA%\PtySpeak\` showing `\logs`, `\sessions`, and `config.toml` — single jumping-off point for any of them. NVDA announces "Opening data folder." beforehand |
| File creation in `session_log` mode | With mode set as above, run `echo hello`        | File `%LOCALAPPDATA%\PtySpeak\sessions\session-<UUID>.jsonl` exists; first line is one valid JSON object containing `"commandText":"echo hello"` and `"schemaVersion":1` |
| `Always` mode synchronous flush   | Set `mode = "always"`; relaunch; run `echo one` | The JSONL file contains the `echo one` tuple BEFORE the next prompt is announced (perceptually durable; verify by inspecting the file immediately after the prompt comes back via Notepad → File → Open) |
| Diagnostic hotkey announces session-log path | Press `Ctrl+Shift+S` in any of the three modes | NVDA announces `Session log mode <mode>; path <full-path>.` for `session_log`/`always`; `Session log mode memory_only; no file.` for `memory_only`. Repeated presses dedupe via the `pty-speak.session-log-path` ActivityId |
| Env-var values redacted in persisted file | From cmd: `set BANK_API_KEY=test_value_long_enough_to_register` then launch pty-speak from that same cmd window (process-local — does NOT use `setx`, which would persist to the user environment); run `echo %BANK_API_KEY%` | Session-log file contains `<REDACTED:BANK_API_KEY>` rather than the literal value. On-screen output still shows the literal value (only the persisted artefact redacts) |
| `Ctrl+Shift+Y` substrate honesty  | With the same env var active, after the redacted command lands, press `Ctrl+Shift+Y`; paste into Notepad | Clipboard contains the UNSANITISED literal value `test_value_long_enough_to_register` (the in-memory History stays honest; only the persistence layer redacts — substrate/persistence boundary contract) |

**Diagnostic decoder for Cycle 24:**

- **Mode change ignored at startup** → grep the FileLogger
  log (press `Ctrl+Shift+D` and inspect the bundle's
  `--- FILELOGGER ACTIVE LOG ---` section in clipboard) for
  `Config: ` lines. Cycle
  24f added explicit per-branch diagnostic logging:
  - `Config: no [session_model] section in TOML; using
    session-persistence defaults.` — the section header was
    typo'd (the canonical name is **nested**:
    `[session_model.persistence]`, NOT `[session_persistence]`)
    or the parent table is missing.
  - `Config: [session_model] present but no
    [session_model.persistence] sub-section; using
    session-persistence defaults.` — you have `[session_model]`
    on its own but didn't add the `.persistence` sub-table.
  - `Config: [session_model.persistence] section parsed;
    mode=<X>, ...` — the section was found and read; if
    `<X>` isn't what you set, the value itself was rejected
    (look for an immediately preceding Warning naming the
    unrecognised value).
  - `Config: [session_model.persistence] mode = '<value>' is
    not one of 'memory_only' / 'session_log' / 'always';
    using default 'memory_only'.` — typo'd value. Note
    parsing is lower-cased, so `Session_Log` is fine but
    `sessionlog` (no underscore) is rejected.
  - If the FileLogger shows `Config: parse error in <path>`
    instead of any of the above, the TOML file itself has a
    syntax error (likely an unquoted string value or a
    duplicate-table conflict). The line with the error is
    surfaced in the same log entry.
- **File NOT created in `session_log` mode** → composition
  root never instantiated the sink. Grep the log for
  `SessionLogWriter` lines; absence of any such line means
  `sessionLogWriterFactory` was bypassed (likely a config
  parse error fell through to `MemoryOnly`). Presence of
  `SessionLogWriter: failed to create output directory` is
  a permission-denied / path-too-long fault — try a
  shorter `output_dir`.
- **`Always` mode hitches but file not durable** → the 10s
  timeout fired. Grep the active log for
  `EnqueueSync timed out (10s)`. Likely cause: disk stall
  or anti-virus scanning the file. The state machine
  continued (the maintainer can keep using pty-speak); the
  tuple lands eventually.
- **Diagnostic hotkey says "no file" in `session_log` mode**
  → the sink construction failed silently. Same root cause
  as the second bullet above; grep the log for
  `SessionLogWriter`.
- **Redaction marker missing from the file** → the env var
  was either too short (< 16 chars) or not on the deny-list
  pattern (`*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`;
  `ANTHROPIC_API_KEY` exempted). Check the active session
  log for the `SessionSanitiser: registered N env-var-value
  redaction patterns from process environment` line; `N`
  should be ≥ 1 if your test env var matched.
- **`Ctrl+Shift+Y` clipboard ALSO redacts** → bug; the
  substrate/persistence boundary leaked. Substrate must
  stay honest. File an issue with the clipboard contents
  + the active session log.

### Stage 8 — selection lists

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| List announced as listbox         | Trigger a Claude prompt that asks "Edit / Yes / Always / No" | NVDA says "list, Edit, 1 of 4" or equivalent          |
| Down arrow advances               | Press Down                                      | NVDA says "Yes, 2 of 4"                                          |
| Up arrow goes back                | Press Up                                        | NVDA says "Edit, 1 of 4"                                         |
| Enter confirms                    | Press Enter on selected item                    | List disappears; NVDA reads Claude's next output                 |
| Inspect.exe shows tree            | Open Inspect.exe while the list is shown        | List + ListItem children with `IsSelected` set on the highlighted item |

**Diagnostic decoder for Stage 8:**

- **NVDA reads the visible text but not as a list** → Stage 8's
  selection-list detector didn't recognise Claude's pattern. The
  detector matches against `tests/fixtures/claude-selection-list.txt`;
  if Claude updated its prompt rendering, the heuristic needs
  retraining.
- **`IsSelected` not set on highlighted item** → The detector
  fired but the WPF UIA peer for the list isn't propagating the
  selection. Check the `SelectionItemPattern` provider on the
  list element's peer.
- **Down arrow doesn't advance NVDA's announcement** → Selection
  changed in Claude but the selection-changed UIA event wasn't
  raised. Check `RaiseAutomationEvent(SelectionItemPatternIdentifiers.IsSelectedProperty)`.

### Stage 9 — earcons

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Red text earcon                   | Echo `\x1b[31mError\x1b[0m`                     | Alarm earcon plays before/with NVDA's "Error"                    |
| Green text earcon                 | Echo `\x1b[32mDone\x1b[0m`                      | Confirm earcon plays                                             |
| Mute toggle                       | Press Ctrl+Shift+M, then echo a red string      | No earcon, NVDA still reads the text                             |
| No NVDA TTS interference          | Earcon and NVDA speech overlap                  | Both audible; neither cuts the other                             |
| Separate audio routing            | Set earcon device to second output              | Earcons play on chosen device, NVDA continues on default          |

**Diagnostic decoder for Stage 9:**

- **No earcon on red text** → Either Stage 9 isn't shipped, or
  the SGR-to-earcon mapping regressed. Confirm the SGR red branch
  is detected (Stage 5 visual smoke renders red — if THAT works
  but the earcon doesn't, the binding from semantics → earcon is
  the suspect).
- **Earcon clips NVDA speech** → WASAPI exclusive mode acquired
  somewhere (PR-template rule "no exclusive-mode acquisition").
  Verify all WASAPI callers use `AudioClientShareMode.Shared`.
- **Earcon outside specified frequency / duration band** → PR-template
  rule "below 180 Hz or above 1.5 kHz, ≤ 200 ms, ≤ -12 dBFS" violated;
  the earcon-generation code regressed.
- **Mute toggle works once but not on next earcon** → Mute state
  is transient (not persisted). Verify Stage 9's mute is a
  process-global flag, not a per-event suppression.

### Stage 10 — review mode

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Toggle review mode                | Press Alt+Shift+R                               | NVDA announces "Review mode"                                     |
| Quick-nav `e` to next error       | After `python -c "raise ValueError('boom')"`, press Alt+Shift+R then `e` | NVDA reads the red traceback line                |
| Quick-nav `c` to next command     | After running several commands, press `c`       | NVDA reads the next command line                                 |
| Exit review mode                  | Press Alt+Shift+R again                         | NVDA announces "Interactive mode"; keys go to PTY                |

**Diagnostic decoder for Stage 10:**

- **Alt+Shift+R does nothing** → Either the binding isn't
  registered (Stage 10 not shipped, mark N/A) or the keyboard
  layer is consuming it before review-mode sees it.
- **`e` quick-nav lands on wrong line** → Error-line detector
  uses red SGR; if `python -c "raise"` doesn't produce red on
  the runner's terminal config, the test won't have a target.
  Use a terminal-banner ANSI-coloured string explicitly to be
  sure.
- **Review mode doesn't release keyboard focus** → Mode toggle
  doesn't restore the input router. Check Stage 10's mode-state
  machine for a missing transition.

### Stage 11 — auto-update

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Initial install                   | Run `pty-speak-Setup.exe`                       | NVDA reads install progress; app launches; title shows version   |
| Manual update check               | In running older version, press Ctrl+Shift+U    | NVDA reads "Update X.Y.Z available, downloading..."              |
| Progress announcement             | During download                                 | Periodic percent announcements via Notification(MostRecent)      |
| Apply and restart                 | After download completes                        | App restarts within ~2 s, no UAC prompt, new version in title    |
| Manifest signature verification   | Tamper with `releases.json` on a local mirror   | App refuses the update and announces the verification failure   |

**Diagnostic decoder for Stage 11:**

- **No update detected when one is published** → `releases.win.json`
  glob mismatch (the failure mode PR #43 fixed). Confirm Velopack
  CLI version matches CI's; check the channel suffix in the URL
  the client is requesting.
- **Update detected, download fails** → Ed25519 manifest
  signature mismatch (once signing is on). For unsigned previews,
  expected pass; for signed releases, the `MINISIGN_SECRET_KEY`
  may have been rotated without re-signing the live release.
- **UAC prompt on update** → Velopack is trying to write to a
  location outside `%LocalAppData%`. Confirm the install path is
  per-user; per-machine installs need elevated update flows.
- **Restart loops or fails** → State file in `%LocalAppData%\pty-speak\`
  may be corrupted across the version boundary. Stage 11 ships a
  backwards-compat shim; if it regresses, the delta-update path
  is the suspect.

### App-reserved hotkey launchers (run on every release that bundles them)

Two diagnostic / convenience hotkeys ride alongside the auto-update
keybinding. They aren't tied to a specific stage but were added in
the post-Stage-11 audit-cycle work to make manual NVDA-side testing
easier. Stage 6's keyboard layer must continue to honour these per
the app-reserved-hotkey contract in `spec/tech-plan.md` §6.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Diagnostic battery (Cycle 25b)    | From running pty-speak, press `Ctrl+Shift+D`    | NVDA reads "Starting diagnostic on <Shell>..." → ~10s autonomous battery → "Diagnostic complete. K of N passed. SessionModel: ... Diagnostic snapshot bundle copied to clipboard. Snapshot saved to %LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<stamp>.txt." Bundle includes: per-shell command battery results + SessionModel substrate state + active FileLogger log slice + config.toml + redacted environment. Paste back from clipboard for triage. |
| Process-cleanup interactive test  | From a PowerShell prompt (not pty-speak's hotkey since Cycle 25b), run `& "$env:LOCALAPPDATA\pty-speak\current\test-process-cleanup.ps1"` | Pass 1 (Alt+F4 close) and Pass 2 (X-button close, with `Alt+Space`/`Enter` keyboard-equivalent) both report PASS; final summary line `OVERALL: PASS`. A future cycle's app menu will surface this script as a menu item. |
| Release-notes launcher            | From running pty-speak, press `Ctrl+Shift+R`    | NVDA reads "Opened release notes in default browser: <url>"; default browser opens to the GitHub Releases page |
| Release-notes URL is correct      | Inspect the browser's address bar               | URL is `https://github.com/KyleKeane/pty-speak/releases` (or whatever fork's `UpdateRepoUrl` was configured to) |

**Known limitation (logged to `docs/SESSION-HANDOFF.md`
item 6 for post-Stage-5/6 follow-up):** the spawned
PowerShell window's stdout is **unreliably read by NVDA**.
The script's plain-text output is correct, but conhost's
UIA exposure isn't on par with pty-speak's own peer.
Treat the on-screen output as the source of truth (review
cursor in PowerShell works; sighted helper or
copy-paste-into-NVDA-Speech-Viewer also works). The right
fix is to run the diagnostic inside pty-speak itself once
Stage 5 (streaming announcements) and Stage 6 (keyboard
input) ship; see SESSION-HANDOFF item 6 for options.

**Diagnostic decoder for the launcher hotkeys:**

- **`Ctrl+Shift+D` silent, no announcement** → The
  `bindHotkey window HotkeyRegistry.RunDiagnostic runDiagnostic`
  wiring regressed, OR `runFullBattery`'s outer `try`-`with`
  swallowed an exception (check the active FileLogger log for an
  ERR line tagged `Terminal.App.Diagnostics.runFullBattery`).
  Cycle 25b: D no longer launches a PowerShell window; the
  battery runs in-process. If you expected a PS window, you're
  probably thinking of `test-process-cleanup.ps1` which is now
  invoked manually.
- **`Ctrl+Shift+D` runs the battery but the snapshot file is
  missing** → `writeSnapshotFile` failed (e.g. the user lacks
  write access to `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\`).
  The clipboard payload still contains the bundle; the announce
  notes "Snapshot file write failed; clipboard still has the
  bundle." Investigate via the FileLogger log's
  `Diagnostic snapshot file write failed` warning line.
- **`Ctrl+Shift+R` silent, no browser** → Either the
  `setupReleasesKeybinding` wiring regressed, OR the user has no
  default browser handler registered for `https:` URLs (rare on
  Windows). NVDA should announce a sanitised exception message in
  the second case.
- **`Ctrl+Shift+R` opens the wrong URL** → `UpdateRepoUrl` constant
  in `Program.fs` was changed without updating the release-notes
  flow. The two share the same constant by design (single source
  of truth for the repo URL).

### Cycle 26 — App menu

Cycle 26 ships the app-menu mini-cycle end-to-end: menu skeleton
+ UIA plumbing (26a), 14 existing AppCommands wired into menu
items via shared `RoutedCommand` instances (26b), and the first
menu-only AppCommand `RunProcessCleanupScript` for diagnostic
scripts that don't deserve a hotkey slot (26c). The menu rides
on top of WPF's standard `Menu`/`MenuItem` UIA exposure so NVDA
reads it correctly without custom-announce layering. The
`OnPreviewKeyDown` filter ordering invariant + `AppReservedHotkeys`
hot-path mirror are unchanged — keyboard pipeline behaviour is
identical to pre-Cycle-26.

The rows below verify each layer's user-facing contract.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Menu reachable via Alt; document role preserved (26a) | Launch pty-speak. NVDA should announce "pty-speak terminal {version}, document". Press Alt | NVDA announces the menu bar (typically "menu bar" or the first menu's name with role "menu"). Press Esc; NVDA returns focus and re-announces "document". The Cycle 26a `Loaded → TerminalSurface.Focus()` invariant is preserved (Menu does not steal focus on launch) |
| InputGestureText reading (26b)    | Press Alt; arrow into Diagnostics; arrow to Health Check (or any hotkey-bearing item) | NVDA reads `<item name>, <gesture>` (e.g. "Health Check, Ctrl plus Shift plus H"). The `MenuItem.InputGestureText` is set from `HotkeyRegistry.gestureText` at compose time — gesture string matches the keyboard binding |
| Menu invokes same handler as keyboard gesture (26b) | Press Alt; arrow to Diagnostics → Health Check; press Enter. Compare with pressing `Ctrl+Shift+H` directly | Both paths announce the same one-line health-check summary (shell + PID + alive + queue depth). Single-source-of-truth contract: menu and keyboard fire the same `RoutedCommand` |
| All keyboard hotkeys still fire (26b regression; updated for Cycle 27) | Without using the menu, press each of the 12 remaining reserved hotkeys: `Ctrl+Shift+U/D/R/P/E/H/B/Y/S/1/2/3` (Cycle 27 dropped `+G` and `+M` — see Cycle 27 row "dropped hotkeys flow through" below) | Each remaining hotkey announces / acts as before. The `OnPreviewKeyDown` filter ordering invariant + `AppReservedHotkeys` mirror are unchanged; `AppReservedHotkeysMirrorTests.fs` pins this at test time |
| Menu-only command launches script (26c) | Press Alt; arrow to Diagnostics → Test Process Cleanup; press Enter | NVDA announces "Launching process-cleanup test in a separate PowerShell window." A separate PowerShell window opens with `test-process-cleanup.ps1` running. Close pty-speak via Alt+F4 to see the script's PASS/FAIL output (the script verifies Job Object cascade-kill cleanup) |
| Menu-only item omits InputGestureText (26c) | Focus Diagnostics → Test Process Cleanup via arrow keys | NVDA reads only the item name "Test Process Cleanup" — no gesture suffix. `gestureText` returns `None` for menu-only commands so `InputGestureText` is left blank |

**Diagnostic decoder for Cycle 26:**

- **Menu invisible / not in UIA tree** → check that
  `MainWindow.xaml`'s `<DockPanel>` wraps both the `<Menu>`
  (with `DockPanel.Dock="Top"`) and the `<views:TerminalView>`
  child. A regression that removed the DockPanel layout would
  drop the menu from the visual tree.
- **NVDA announces "menu" instead of "document" on launch** →
  the `Loaded → TerminalSurface.Focus()` handler in
  `MainWindow.xaml.cs` was removed or misordered. The handler
  must run after `InitializeComponent()` so the visual tree
  exists when `Focus()` is called.
- **Menu item shows no `InputGestureText` for a hotkey-bearing
  command** → compose's reflection-driven wiring didn't find
  the named `MenuItem`. Check that the XAML `x:Name` matches
  `MenuItem_<HotkeyRegistry.nameOf cmd>` exactly. The wiring
  loop logs nothing on miss; missing wiring shows up as a
  blank `InputGestureText` (also: command becomes inert because
  `MenuItem.Command` was never set).
- **Menu item invokes a different handler than its hotkey** →
  the `bind` wrapper's `menuCommands` dictionary captured the
  wrong `RoutedCommand`. Press `Ctrl+Shift+D`; the bundle's
  `--- FILELOGGER ACTIVE LOG ---` section will not show this
  directly (no logging on bind), but `Diagnostics` battery
  output will surface inconsistent state.
- **F# / C# mirror parity broken (build green, NVDA reports
  hotkey doesn't fire)** → `AppReservedHotkeysMirrorTests`
  should have caught this at test time. If it didn't, check
  whether the test fixture skipped its build (e.g.
  `<Compile Include>` missing in `Tests.Unit.fsproj`).

**ADR-style note: parallel-surface decision (2026-05-09).**

Cycle 26 keeps the C# `TerminalView.AppReservedHotkeys` static
array (`TerminalView.cs:346-489`) as a parallel surface to the
F# `HotkeyRegistry.builtIns` registry. The keyboard hot-path
filter (`OnPreviewKeyDown`, fired per keystroke) consults the
C# array directly for O(1) hot-path performance; the F#
registry is consulted at compose-time only. The new
`AppReservedHotkeysMirrorTests.fs` test pins the parity
invariant at test time so "single source of truth" is maintained
in spirit (a missed update on either side fails CI immediately
rather than at NVDA-test time). A future cycle may collapse the
two surfaces into a single one if the maintainer wants — but
that's a focused refactor, not Cycle 26's scope.

### Cycle 27 — Multi-state menu items

Cycle 27 establishes the multi-state menu paradigm and migrates
`MuteEarcons` (Ctrl+Shift+M) → `EarconsMode` (View → Earcons →
Enabled / Muted) and `ToggleDebugLog` (Ctrl+Shift+G) →
`LoggingLevel` (View → Logging Level → Information / Debug).
Both keyboard accelerators are dropped; both operations are now
menu-only. Active option is indicated via WPF
`MenuItem.IsCheckable=true` + `IsChecked` (refreshed on
`SubmenuOpened` from each binding's `GetCurrent` closure),
which surfaces UIA TogglePattern that NVDA reads as "menu
item, checked" / "menu item, not checked".

The rows below verify that NVDA correctly announces the
checked-state on focus + that activating an option produces the
expected behavioural side-effect.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Logging Level checked-state on focus (Cycle 27) | Launch pty-speak (FileLogger defaults to Information). Press Alt; arrow to View → Logging Level; arrow into the submenu | NVDA announces the FIRST sub-item as "Information, menu item, checked" (the active level). Arrow to Debug; NVDA announces "Debug, menu item, not checked". The checked-state matches the active level |
| Logging Level activate Debug (Cycle 27) | Continue from above. With Debug focused, press Enter | NVDA announces "Debug logging on." (via `ActivityIds.logToggle`). Menu closes. Reopen Diagnostics → Run Diagnostic Battery and inspect the bundled FileLogger log section in the clipboard or `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\…` — the next entries are at level `[DBG]` (debug) |
| Logging Level state persists across submenu reopens (Cycle 27) | After activating Debug, press Alt; re-navigate to View → Logging Level → arrow into submenu | NVDA now announces "Information, menu item, not checked" then "Debug, menu item, checked" — the previously-activated option is the one with the checkmark. The `SubmenuOpened` handler refreshed `IsChecked` from `GetCurrent` |
| Logging Level idempotent re-activate (Cycle 27) | With Debug already active and checked, navigate to Debug and press Enter again | No announcement, no log spam. The `setByName` closure no-ops when `current ≡ target`. (Conceptually: re-selecting the active option is a no-op; flapping the menu shouldn't flap the log) |
| Earcons checked-state on focus (Cycle 27) | Launch pty-speak (earcons default to Enabled). Press Alt; arrow to View → Earcons; arrow into the submenu | NVDA announces the FIRST sub-item as "Enabled, menu item, checked". Arrow to Muted; NVDA announces "Muted, menu item, not checked" |
| Earcons activate Muted silences earcons (Cycle 27) | Continue from above. With Muted focused, press Enter. Then trigger an earcon-emitting action (e.g. press `Ctrl+Shift+H` for the health-check earcon) | NVDA announces "Earcons muted." Menu closes. The subsequent health-check announcement plays via NVDA but the audio earcon does NOT play |
| Earcons activate Enabled restores earcons (Cycle 27) | After muting, navigate to View → Earcons → Enabled; press Enter; trigger the earcon-emitting action again | NVDA announces "Earcons unmuted." The audio earcon plays |
| Dropped hotkeys flow through to shell (Cycle 27 regression) | Without opening any menu, press `Ctrl+Shift+G` then `Ctrl+Shift+M` | Both gestures flow through as plain `g` / `m` characters to the spawned shell (visible at the cmd / PowerShell prompt). No app-level announcement, no state change. The `AppReservedHotkeys` filter no longer reserves these slots |

**Diagnostic decoder for Cycle 27:**

- **Sub-item NVDA-reads as "menu item" without "checked" /
  "not checked"** → the per-option `MenuItem.IsCheckable` was
  not set to `true`. Check the multi-state wiring loop in
  `Program.fs compose ()` — the `childItem.IsCheckable <- true`
  assignment must run before the `Command` assignment (the loop
  does both inside the same match arm, so a regression here
  would drop both).
- **`IsChecked` stuck on the wrong option after activating
  another** → the `SubmenuOpened` handler isn't firing or
  `GetCurrent` is returning a stale value. Verify by adding a
  temporary `log.LogDebug("SubmenuOpened: %s -> %s", cmdName,
  binding.GetCurrent())` line at the top of the handler and
  re-running the matrix; the log should land in the FileLogger
  active log on every menu open.
- **Menu activates the option but no announcement / no
  side-effect** → the `setByName` closure was never wired
  (per-option `RoutedCommand` not assigned to
  `MenuItem.Command`). Check the multi-state wiring loop's
  `binding.PerOptionCommand.TryGetValue(opt.OptionId)` branch —
  if the OptionId in XAML doesn't match the OptionId in
  `multiStateBuiltIns`, the command stays unassigned.
- **Activating an unchecked option doesn't change `IsChecked`
  immediately** → expected behaviour. `IsChecked` is refreshed
  on the NEXT `SubmenuOpened`, not at the moment of activation.
  The active-option visual state lags by one menu open. (Adding
  immediate-update logic is possible but adds complexity for no
  user-visible benefit since the menu has already closed.)

<!-- DOGFOOD -->
### Cycle 28 — Window menu

Cycle 28 adds the top-level `Window` menu with two children:
`Close Window` and `Exit`. Both are menu-only AppCommands;
neither has a keyboard accelerator registered with
`AppReservedHotkeys`. `Close Window`'s
`InputGestureText="Alt+F4"` is hardcoded in XAML for visual
display since the OS-level Alt+F4 is handled by WPF Window
natively.

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Window menu reachable via Alt (Cycle 28) | Press Alt; arrow to Window | NVDA announces the Window menu (sits between Diagnostics and Help) |
| Close Window reads Alt+F4 gesture (Cycle 28) | Press Alt; arrow to Window → Close Window | NVDA announces "Close Window, Alt plus F4, menu item". The gesture string is hardcoded in XAML; pressing Enter calls `Window.Close()` |
| Close Window from menu closes app (Cycle 28) | With Close Window focused, press Enter | The window closes; the `app.Exit` cleanup pipeline runs (logger flush, sessionLogWriter dispose, hostHandle dispose, etc.). FileLogger active log shows `[INF] [Terminal.App.Program.runCloseWindow] Window menu Close Window invoked.` and `[INF] [Terminal.App.Program] pty-speak exiting.` |
| Alt+F4 still closes the window directly (Cycle 28 regression) | Without using the menu, press Alt+F4 | The window closes via the OS gesture handled by WPF Window natively. Same `app.Exit` chain runs. Verify no `runCloseWindow` log entry (the OS gesture bypasses the menu handler) |
| Exit from menu shuts down app (Cycle 28) | Press Alt; arrow to Window → Exit; press Enter | The window closes; `app.Exit` chain runs identical to Close Window. FileLogger shows `[INF] [Terminal.App.Program.runExitApp] Window menu Exit invoked.` |

**Diagnostic decoder for Cycle 28:**

- **Window menu missing from the menu bar** → check that the
  XAML `<MenuItem Header="_Window">` was preserved in
  `MainWindow.xaml`; the reflection-driven menu wiring loop is
  forgiving (skips missing fields silently), so a missing parent
  shows as no menu rather than a runtime error.
- **Close Window's Alt+F4 gesture text not announced** →
  `InputGestureText="Alt+F4"` in XAML was overwritten or removed.
  The Cycle 26b wiring loop only writes `InputGestureText` when
  `gestureText` returns `Some`; for menu-only commands (`None`)
  the hardcoded XAML value survives.
- **Close Window menu item fires but the Alt+F4 keyboard
  gesture doesn't** → the OS gesture is handled by WPF Window
  natively, NOT via `AppReservedHotkeys`. If Alt+F4 stops
  working, check whether something in `OnPreviewKeyDown`
  intercepted it. The Cycle 28 PR added no new
  `AppReservedHotkeys` rows; existing behaviour should be
  unchanged.

<!-- DOGFOOD -->
### Cycle 29b — SelectionProfile (NVDA reads selection prompts as text)

Cycle 29b wires `SelectionDetector` (shipped in 29a) into the
output dispatcher via the new `SelectionProfile`. NVDA now reads
Claude's tool-use selection prompts as text — the detector
recognises the highlighted-row pattern and emits structured
events; the profile constructs user-facing text from the
event's Extensions data and routes it to the NVDA + FileLogger
channels. UIA listbox semantics arrive in Stage 8e-B; arrow-key
round-trip in 8e-C.

**Pre-requisites.** Set `PTYSPEAK_SHELL=claude` (or
`Ctrl+Shift+3` to switch to Claude in-session). Selection
detection is claude-only in 29b — cmd / PowerShell sessions
short-circuit the detector entirely.

| Test                                                                      | Procedure                                                                       | Expected behaviour                                                                                                                                                                                              |
|---------------------------------------------------------------------------|---------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 29b.1 First detection (Cycle 29b)                                         | In Claude, ask it to edit a file. When the "Edit / Yes / Always / No" prompt appears, wait ~250ms.   | NVDA speaks "selection prompt: Edit, Yes, Always, No (selected: Yes)" once the heuristic-stability window elapses (~100ms after first paint). Each line of `Ctrl+Shift+D`'s log bundle shows `Semantic=SelectionShown` + a single `selection-detector` Producer entry. |
| 29b.2 Selection navigation (Cycle 29b)                                    | Press the down-arrow key on the prompt.                                         | NVDA speaks "selected: Always, 3 of 4" within ~200ms (one paint + tick cadence). Subsequent down-arrows announce each new selection. The prefix "selected: " applies because the SelectionItem update event has `ItemIndex == SelectedIndex`. |
| 29b.3 Selection dismissal (Cycle 29b)                                     | Press `Esc`, or accept the selection with `Enter` and watch the prompt vanish. | NVDA speaks "selection dismissed" within `DismissalGraceMs` (~150ms) of the highlighted-region disappearing. FileLogger shows a `Semantic=SelectionDismissed` entry with empty Payload + the rendered text on the SelectionProfile-emitted entry. |
| 29b.4 Non-Claude shell short-circuit (Cycle 29b)                          | Switch to cmd (`Ctrl+Shift+1`) or PowerShell (`Ctrl+Shift+2`); type for 5 minutes. | FileLogger shows ZERO `Semantic=SelectionShown` rows during the cmd / PowerShell session. The detector returns `([||], state)` immediately for non-claude shellKey, so no detection cost is paid.                |
| 29b.5 No PassThroughProfile double-emission (Cycle 29b)                  | Trigger any selection event in 29b.1 / 29b.2 / 29b.3.                            | NVDA speaks the user-facing text exactly ONCE per event. The empty-payload trick (detector emits with `Payload=""`) makes `NvdaChannel` skip PassThroughProfile's catch-all `RenderText ""` decision; only the SelectionProfile-rendered text reaches NVDA. |

**Diagnostic decoders for Cycle 29b:**

- **NVDA stays silent on selection prompts** → detector
  may be short-circuiting because `currentShellId` isn't
  Claude. Verify with `Ctrl+Shift+H` (announces the active
  shell). If shell is Claude but no announce: check
  `Ctrl+Shift+D` log for `Semantic=SelectionShown` entries
  — if absent, the detection heuristic isn't firing
  (highlight contrast may be too low; check `selection.source`
  Extensions value if present).
- **NVDA speaks selection text TWICE** → empty-payload trick
  not working. The detector may be emitting with non-empty
  Payload (regression vs. Cycle 29b's contract); check the
  `Payload` field on the FileLogger structured-log entry
  for SelectionShown events. Should be empty.
- **Selection text reads but with wrong "selected: " prefix
  on non-selected items** → `ItemIndex == SelectedIndex`
  comparison may be failing because of an Extensions
  schema-key drift between detector + profile. Check both
  files reference `SelectionExtensions.ItemIndex` (Cycle 29b
  added) and `SelectionExtensions.SelectedIndex` (Cycle 29a).
- **Cycle 29c will replace** the hardcoded
  `SelectionDetector.defaultParameters` in `Program.fs:1184`
  with TOML-loaded values from `[profile.selection]`. The
  Cycle 29b NVDA matrix exercises the detector at default
  thresholds; 29c lets the maintainer tune them via
  `Ctrl+Shift+E` (open config.toml).

<!-- DOGFOOD -->
### Cycle 36 — Substrate-inversion arc matrix backfill (historical; retired Cycle 45c)

> **Cycle 45c update (2026-05-12)**: this section validated the
> `LinearTextStream` substrate that Cycle 45c deleted. The
> `[pathway.stream] substrate_mode`, `applyAndCaptureWithSubstrate`,
> and `--- LINEAR STREAM ---` references in the procedures + grep
> recipes below no longer exist. The matrix is preserved for
> archaeology; **for current validation, use rows 45c-1 through
> 45c-6 in the "Cycle 45c — Pathway-pipeline cleanup" section
> further down**, which exercise the same dogfood outcomes
> (cmd narration, long-output chunking, alt-screen TUIs,
> Claude streaming, OSC 133 boundary detection) against the
> live ContentHistory substrate.

Cycles 33-35c shipped the substrate-inversion arc: the canonical
representation of "what the shell produced" pivoted from a
screen-row diff (`Screen.Snapshot` + per-row hashing) to a linear
byte-stream substrate (`LinearTextStream` producer; RFC
[`docs/rfc/0001-linear-text-substrate.md`](archive/pre-cycle-45/0001-linear-text-substrate.md)).
Cycle 36 codifies the validation matrix the maintainer ran on
2026-05-10 against Cycle 35a's parallel-path-behind-flag PR so
future contributors have a reproducible recipe + a Cycle 39
stopping gate.

**Validation status (2026-05-10).**
- **Cycle 35a** (parallel path behind default-off TOML flag) was
  hand-validated by the maintainer 2026-05-10 in cmd +
  PowerShell + Claude across all 8 rows below in both Linear and
  Auto modes. All passed.
- **Cycle 35b** (default flip to `Auto` + SessionModel hybrid
  cutover) shipped without manual NVDA validation. Relies on the
  945+ unit-test suite (including 6 SessionModelTests + 2
  LinearTextStreamTests pinning the substrate-aware path) plus
  FileLogger telemetry for post-merge regression detection.
- **Cycle 35c** (Levenshtein spinner gate scoped to ScreenDiff
  fallback only) shipped without manual NVDA validation. Relies
  on the 2 new spinner-gate-bypass facts plus the 4 existing
  screen-diff sentinel facts.

**Pre-requisites.** Default install (no TOML overrides). Cycle
35b flipped `[pathway.stream] substrate_mode` default to `auto`,
so non-alt-screen frames flow through the linear substrate; alt-
screen frames (vim, less, top) flow through screen-diff. The
matrix below is run at the default; rows that explicitly need a
different mode call it out.

**Run each row in cmd.exe AND PowerShell** unless the row marks
otherwise. Hot-switch via `Ctrl+Shift+1` (cmd) / `Ctrl+Shift+2`
(PowerShell) / `Ctrl+Shift+3` (Claude).

| Test                                  | Procedure                                                                                                                       | Expected NVDA shape                                                                                                                                                                                                            |
|---------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 36.1 `dir` (small dir)                | `cd %USERPROFILE%\Documents` (or any dir with 10–30 entries); run `dir`.                                                        | Single announce of full listing. NVDA reads the entire output as one continuous announce (no chunking, no per-row pause).                                                                                                       |
| 36.2 `dir /s` of a large tree         | `dir /s C:\Windows\System32`. (Long-running.)                                                                                   | Sustained-output. Producer commits at idle quanta (~150 ms — RFC §5.2). No raw character counts in announce; no chunk repeats; the listing is heard as a streaming flow rather than a rapid-fire burst.                          |
| 36.3 `git status` in a busy repo      | In any local git repo with uncommitted changes, run `git status`.                                                              | One announce per terminal idle. Coloured `M` / `A` / `D` / `??` lines read in order. No double-read of any file path. Earcons fire on red lines (modified files) but not on green lines (added).                              |
| 36.4 `npm install` of a small package | In a scratch directory, run `npm install lodash`.                                                                              | Spinner / progress bar suppressed (live-region tail-mask). The "added N packages" final line announces. Warning lines announce individually. The Cycle 29b ~80-spinner-storm regression does NOT recur.                          |
| 36.5 Claude tool-use spinner (Claude only) | In Claude (`Ctrl+Shift+3`), ask a question that triggers a tool-use spinner-then-response — e.g., "what files are in the current directory?" | <5 announces during the spinner (was ~80 in Cycle 29b validation). Final response announces once at completion. **This is the load-bearing row** — Cycle 35's headline win.                                                  |
| 36.6 `dir /s | more`                  | In cmd, run `dir /s | more`. Press space to advance pages.                                                                      | First page announces in full. The `-- More --` prompt announces once. Spacebar advances + announces the next page. No re-announce of prior pages.                                                                              |
| 36.7 PowerShell `gci` with `$PSStyle` | In PowerShell 7+, run `Get-ChildItem` (or `gci`) in any dir with mixed file types.                                            | Coloured output reads as text. Benign colours (file-type indicators) do NOT trigger `error-tone` earcons. Only red error lines trigger earcons.                                                                                |
| 36.8 cmd tab completion (cmd only)    | In cmd, type `cd C:\Pro` then press `<TAB>` (let it auto-complete to `C:\Program Files`); press `<Enter>`.                      | Echoed completion (`gram Files`) announces ONCE on TAB. NOT re-announced on Enter. **Critical regression test for the input-framework deferral** — substrate inversion must NOT regress plain-cmd typed-input echo.            |

**Cycle 39 stopping gate.** All 8 rows green at default
`substrate_mode = auto` + the FileLogger grep recipes below
showing zero `Suppressed (spinner)` log lines on non-alt-screen
frames is the precondition for safely removing the screen-diff
legacy code (per Section 13 of the strategic plan +
[`docs/STAGE-7-ISSUES.md`](archive/pre-cycle-45/STAGE-7-ISSUES.md) substrate-cleanup
section). Two+ rows regressing without a clean fix → revert
default to `screen-diff` via TOML; substrate-mode stays opt-in.

**Diagnostic decoders for Cycle 36:**

- **Spinner-storm regression on row 36.4 / 36.5** → producer's
  tail-mask classifier may be missing a live-region pattern
  (RFC §5.3). Capture a `Ctrl+Shift+D` bundle; grep the
  `--- LINEAR STREAM (last 64KB) ---` section for repeated
  spinner glyphs (`⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏`). If the bundle shows the
  spinner frames, the tail-mask isn't transitioning the row to
  LATEST semantics for the byte pattern the spinner uses.
  Workaround: revert TOML default to `substrate_mode =
  "screen-diff"` (the Levenshtein gate fires on the screen-
  diff path).
- **Non-OSC-133 shell loses Ctrl+Shift+Y CommandText/OutputText**
  → vanilla cmd / PowerShell don't emit OSC 133 markers. The
  hybrid fallback in `SessionModel.applyAndCaptureWithSubstrate`
  routes to `extractContent` row-walk for those shells. If
  Ctrl+Shift+Y captures empty CommandText/OutputText, check
  the bundle's `--- SESSION LOG ---` section for the active
  tuple's content. If empty: extractContent fallback isn't
  firing (regression vs. Cycle 35b's hybrid contract).
  Workaround: same TOML revert.
- **Earcon false-positive on row 36.7** → SGR-color detection
  in `EarconProfile.fs` may be misclassifying $PSStyle's blue
  / cyan / magenta as red. EarconProfile is screen-substrate
  by design (color is a grid concept per CORE-ABSTRACTION-
  BOUNDARY.md §1.1); the substrate inversion does NOT touch
  it. Regression here is a separate issue against EarconProfile,
  not the substrate arc.

**FileLogger grep recipes (post-merge dogfood verification).**

The matrix above is run by-ear; the recipes below complement it
with grep-against-bundle verification of the implementation
contracts. Run after a typical session, capture
`Ctrl+Shift+D`'s bundle, and grep:

```cmd
:: cmd — verify Linear path bypasses spinner-suppress (Cycle 35c)
findstr /C:"Suppressed (spinner)" "%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<latest>.txt"
:: Expected post-Cycle-35c: zero matches in non-alt-screen frames.
:: Matches OK if the session entered alt-screen (vim, less, top).
```

```powershell
# PowerShell — same check
Select-String -Pattern "Suppressed \(spinner\)" -Path "$env:LOCALAPPDATA\PtySpeak\diagnostic-snapshots\snapshot-*.txt" -Context 1,1
```

```cmd
:: cmd — verify SessionModel hybrid fallback (Cycle 35b) is firing for cmd / PS sessions
findstr /C:"applyAndCaptureWithSubstrate" "<bundle-path>" | findstr /V /C:"useLinear=true"
:: Expected: lines from cmd / PowerShell sessions show useLinear=true was passed but the
:: linear stream returned None (no OSC 133 markers); fallback to extractContent fired.
:: For Claude sessions, useLinear=true should yield linear-path success.
```

```cmd
:: cmd — verify the linear stream is capturing bytes
findstr /C:"--- LINEAR STREAM" "<bundle-path>"
:: Should return a header line; the section that follows shows the last 64KB of
:: producer-captured bytes. Empty section = producer isn't accumulating (bug).
```

These recipes lean on the existing diagnostic-bundle infrastructure
shipped in Cycle 34b; no new logging is required.

<!-- DOGFOOD -->
### Cycle 37 — UIA listbox peer (interactive-list canonical-display)

Cycle 37 (37a + 37b) replaces SelectionProfile's Cycle 29b
text-only NVDA announcements with full UIA listbox semantics.
NVDA hears Claude tool-use selection prompts as a real
`ControlType.List` with `1 of N` semantics, per
`spec/tech-plan.md` §8.2-§8.5 and
[`docs/CANONICAL-DISPLAY-CATALOG.md`](CANONICAL-DISPLAY-CATALOG.md)
§2 (interactive list exemplar) + §2.14 (ConfirmationPrompt
hybrid: items also implement `IInvokeProvider` for single-key
activation).

**This is the load-bearing acceptance gate for PR 37b.** Per
[`CONTRIBUTING.md`](../CONTRIBUTING.md) "non-negotiable
accessibility rules", PR 37b cannot squash-merge until the
maintainer confirms all rows below pass on real NVDA.

**Pre-requisites.** Set `PTYSPEAK_SHELL=claude` (or
`Ctrl+Shift+3` to switch to Claude in-session). SelectionDetector
is shellKey-gated to `"claude"` only — cmd / PowerShell /
non-Claude shells short-circuit detection and the list peer
never materializes for those.

| Test                                                     | Procedure                                                                                                            | Expected NVDA shape                                                                                                                                                                                                                                                              |
|----------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 37.1 List appearance (Cycle 37b)                         | In Claude (`Ctrl+Shift+3`), ask "edit a file" to trigger the Edit/Yes/Always/No prompt. Wait for the highlight.       | NVDA in focus mode: announces "list with 4 items, Edit, 1 of 4". NVDA in browse mode: announces "list" on cursor entry. The `StructureChanged` event fires; the new `TerminalList` child appears in the peer tree (verifiable via Inspect.exe).                                  |
| 37.2 Browse-mode item navigation (Cycle 37b)             | NVDA+Down or right-arrow to navigate the list cells in browse mode.                                                  | Each item announces with its number/total: "Yes, 2 of 4", "Always, 3 of 4", "No, 4 of 4". UIA `PositionInSet` / `SizeOfSet` populated correctly per `TerminalListItemAutomationPeer.GetPositionInSetCore`/`GetSizeOfSetCore`.                                                     |
| 37.3 Highlight movement (terminal-side, Cycle 37b)       | With NVDA in focus mode, press the keyboard's Down arrow (PTY echoes; Claude shifts highlight).                      | NVDA announces the new selection: "Yes, 2 of 4". The `SelectionItemPatternIdentifiers.ElementSelectedEvent` fires from `TerminalListAutomationPeer.UpdateSelection`; NVDA refocuses to the new item.                                                                              |
| 37.4 Invoke (Enter on selected item, Cycle 37b)          | With NVDA in focus mode on the selected item, press Enter (NVDA's Enter key in focus mode invokes the focused item). | The PTY receives `\r` (0x0D byte) via `IInvokeProvider.Invoke()` → `TerminalView.WritePtyBytes`. The prompt dismisses; "selection dismissed" structural event fires (StructureChanged); list peer drops from `GetChildren`. Claude proceeds with the chosen action.               |
| 37.5 Dismissal cleanup (Cycle 37b)                       | After the prompt disappears (via Enter or terminal-side Esc), navigate document with NVDA reading cursor.            | The list peer is gone (verified via Inspect.exe — `TerminalView` child count returns to 0). Document `IsContentElementCore` returns to `true`; reading cursor works again on document text.                                                                                      |
| 37.6 Document dedup (full-document IsContentElement, Cycle 37b) | While the list is showing, attempt to read terminal text with NVDA reading cursor (Insert+Up/Down).                  | Reading cursor cannot enter the document (full-document `IsContentElement = false` while list active per the 2026-05-10 design choice). Documented limitation; iterate to per-range exclusion in a follow-up if the trade-off bites the maintainer's reading flow.               |
| 37.7 cmd `choice` non-claude shell (Cycle 37b)           | In cmd (`Ctrl+Shift+1`), run `choice /c YN /m "Continue?"`. Type `Y`.                                                | `cmd choice` is prompt-line, not a multi-row interactive list. SelectionDetector is shellKey-gated to `"claude"` only. The list peer never materializes. Verify via FileLogger: `Ctrl+Shift+D` bundle's `--- FILELOGGER ACTIVE LOG ---` shows ZERO `Semantic=SelectionShown` lines during the cmd session. |

**Diagnostic decoders for Cycle 37b:**

- **NVDA reads selection text but no listbox structure** →
  the cutover may not be in effect. Capture
  `Ctrl+Shift+D` bundle and grep
  `--- FILELOGGER ACTIVE LOG ---` for `Semantic=SelectionShown`
  rows. The text payload should be present (FileLogger keeps
  the audit trail), but no `RawPayload received` warning lines
  (those would indicate the View's `AnnounceRawPayload` is
  receiving an unexpected payload type).
- **NVDA hears the list announcement TWICE** ("selection
  prompt: Edit, Yes, Always, No (selected: Yes)" then
  "list with 4 items, Edit, 1 of 4") → Cycle 37b's
  `SelectionProfile.fs` cleanup may have regressed; the
  duplicate NVDA `RenderText` decision is still being emitted.
  Pin-test failure on `SelectionProfile`'s `decisions.Length =
  2` invariant catches this at CI.
- **List peer doesn't appear in NVDA's UIA tree at all** →
  use Inspect.exe to walk the tree. If the `TerminalView`
  Document peer has no `TerminalList` child while a Claude
  prompt is showing, `TerminalAutomationPeer.UpdateSelectionState`
  is not firing. Capture FileLogger; grep
  `Semantic=SelectionShown` to confirm the substrate is
  emitting the event.
- **`IInvokeProvider.Invoke()` fires but PTY doesn't
  receive `\r`** → `TerminalView.WritePtyBytes` may be
  short-circuiting (`_writeBytes` is null because `SetPtyHost`
  wasn't called yet). Reproduce with the app already in steady
  state (post-startup); the wiring race only happens during
  the very first second after launch.
- **Cycle 37c arrives** with NVDA list-mode arrow-key
  translation (NVDA in focus mode pressing Down sends Down to
  the peer, peer translates to PTY arrow byte). 37b ships
  read-only UIA semantics: NVDA hears the list correctly but
  can't drive selection from focus mode without falling
  through to the input layer (which works today via PTY echo).

**FileLogger grep recipes (post-merge dogfood verification).**

```cmd
:: cmd — verify the substrate is emitting SelectionShown for Claude sessions
findstr /C:"Semantic=SelectionShown" "%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<latest>.txt"
:: Expected: one or more rows during a Claude session that triggered a tool-use prompt.
```

```cmd
:: cmd — verify NO unexpected RawPayload type errors
findstr /C:"AnnounceRawPayload received unexpected" "%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<latest>.txt"
:: Expected: zero rows. Any match indicates payload type drift between substrate and channel.
```

```powershell
# PowerShell — same checks
Select-String -Pattern "Semantic=SelectionShown" -Path "$env:LOCALAPPDATA\PtySpeak\diagnostic-snapshots\snapshot-*.txt"
Select-String -Pattern "AnnounceRawPayload received unexpected" -Path "$env:LOCALAPPDATA\PtySpeak\diagnostic-snapshots\snapshot-*.txt"
```

<!-- DOGFOOD -->
### Cycle 43a — Diagnostic chunk extractors + Grep dialog

Cycle 43a converts diagnostic triage from "press `Ctrl+Shift+D`,
hope the multi-megabyte bundle doesn't crash iOS chat on paste"
to "press a menu item, get a paste-safe focused chunk." The new
surface lives entirely under `Diagnostics`: two top-level items
(`Copy Latest Bundle to Clipboard`, `Grep Diagnostics...`) plus a
4-way `Extract` submenu (By Recency / By Event Type / By Bundle
Section / Snapshot) with one proof-of-concept item each in 43a.

Every extractor copies a clipboard payload (capped at **60 KB**
to dodge the Cycle 29b iOS-paste-crash failure mode), writes the
full untruncated text to
`%LOCALAPPDATA%\PtySpeak\extracts\<extractor>-<timestamp>.txt` as
paste-fallback, and announces "<extractor> copied to clipboard:
<size>. Extract file at <path>." via NVDA.

| Test | Steps | Expected |
|---|---|---|
| Top-level item: Copy Latest Bundle | Press Alt, arrow to Diagnostics → Copy Latest Bundle to Clipboard, press Enter. Wait for the NVDA announce. Paste clipboard into any text editor. | NVDA announces "CopyLatestBundle copied to clipboard: <N> kilobytes. Extract file at <path>." within ~1 second (lightweight bundle skips the diagnostic battery so it's much faster than Ctrl+Shift+D's ~10s). Pasted text starts with `pty-speak diagnostic snapshot (Cycle 43a lightweight)` and contains all five sections (`--- FILELOGGER ACTIVE LOG ---`, `--- CONFIG.TOML ---`, `--- SESSION LOG ---`, `--- CONTENT HISTORY ---`, `--- ENVIRONMENT ---`). |
| Top-level item: Grep Diagnostics dialog | Press Alt, arrow to Diagnostics → Grep Diagnostics..., press Enter. NVDA reads the dialog title. Tab through controls — pattern textbox, case-sensitive checkbox, regex checkbox, context-lines textbox, OK button, Cancel button. | Dialog opens centred on the main window. Initial focus is on the pattern textbox (NVDA reads "Search pattern, edit"). Tabbing reads each label: "Case sensitive, checkbox, not checked"; "Treat pattern as regex, checkbox, not checked"; "Lines of context around each match, edit, 5"; "OK, button"; "Cancel, button". |
| Grep dialog default-OK round trip | Type `Heartbeat` in the pattern textbox, press Enter (default OK). | Dialog closes. NVDA announces "grep-Heartbeat copied to clipboard: <N> bytes. Extract file at <path>." Pasted clipboard begins with `pty-speak grep — pattern: Heartbeat (regex=false, case=false, context=5)` and contains one `--- Match N of M (line L) ---` block per matched line. |
| Grep dialog Cancel / Escape | Open the dialog, press Escape. | Dialog closes silently. No NVDA announce. No clipboard write. |
| Grep dialog invalid context lines | Open dialog, type `abc` into the context-lines textbox, press Enter. | Dialog stays open. Inline error message reads "Context lines must be a whole number between 0 and 20." Focus moves back to the context-lines textbox. NVDA reads the error via the `LiveSetting=Assertive` automation property. |
| Grep clipboard truncation | Open dialog, type `Heartbeat` with context = 20, press Enter. The lightweight bundle has many heartbeat lines so result exceeds 60 KB. | NVDA announce contains "(clipboard truncated)" suffix. Pasted clipboard ends with `[... truncated at 60-something bytes; full results in extract file ...]`. Extract file at the announced path contains the full untruncated result. |
| Extract → By Recency → Last 50 Log Lines | Press Alt → Diagnostics → Extract → By Recency → Last 50 Log Lines → Enter. | NVDA announces "ExtractLast50LogLines copied to clipboard: <N> kilobytes." Pasted text contains 50 log lines (or fewer if the active log is shorter), each prefixed by an ISO-8601 timestamp like `2026-05-11T14:32:17.456Z`. |
| Extract → By Event Type → Errors and Warnings | Open the menu chain, select Errors and Warnings. | NVDA announces successfully. Pasted text contains only lines with `Semantic=ErrorLine`, `Semantic=WarningLine`, or `Semantic=ParserError`. If the current session has no such events, the body reads as empty (just the header). |
| Extract → By Bundle Section → Active Config | Open the menu chain, select Active Config. | NVDA announces successfully. Pasted text is the verbatim content of `%LOCALAPPDATA%\PtySpeak\config.toml`. If the file is missing, the body reads `(file not present: <path>)`. |
| Extract → Snapshot → Version Header | Open the menu chain, select Version Header. | NVDA announces successfully. Pasted text contains 5 short lines: `Version: 0.0.x.y`, `OS: <OS string>`, `.NET: <runtime version>`, `Process ID: <pid>`, `Active shell: cmd` (or whichever shell is current). Total size under 1 KB. |
| Extract file fallback for clipboard contention | With pty-speak open, open another app that holds the clipboard (e.g., a clipboard-manager utility). Press Diagnostics → Extract → Snapshot → Version Header. | NVDA may announce "ExtractVersionHeader clipboard copy timed out. Extract file at <path>." Even on clipboard timeout, the extract file is written with the full content — the user can navigate to `%LOCALAPPDATA%\PtySpeak\extracts\` and open the latest file. |
| Menu item announces with no InputGestureText | Focus any of the 6 new menu items via arrow keys. | NVDA reads only the item name (e.g., "Grep Diagnostics, ellipsis"). No keyboard shortcut is announced — all 6 commands are menu-only by canon (`Key = None`, `Modifiers = None` in `HotkeyRegistry.builtIns`). |

**Diagnostic decoder.** If a clipboard copy fails repeatedly with
"clipboard copy timed out": likely NVDA's clipboard hook
contention. Pull the extract file directly from
`%LOCALAPPDATA%\PtySpeak\extracts\`. If the extract file write
fails: check `%LOCALAPPDATA%\PtySpeak\` disk space / permissions
— the error path will report "Extract file write failed" but
clipboard copy still works.

### Cycle 45c — Pathway-pipeline cleanup (PR-3a → PR-3c)

The Cycle 5-9 screen-grid-diff pathway pipeline
(`StreamPathway` / `LinearTextStream` / `DisplayPathway` /
`TuiPathway` / `PathwaySelector`) was retired across three PRs.
The aural substrate is now `ContentHistory` + `SpeechCursor`.
The Cycle 45 NVDA dogfood confirmed the pathway calls weren't
on the announce path, so user-visible behaviour should be
unchanged; this matrix confirms.

| Row | Test | Pass criterion |
|---|---|---|
| **45c-1** | cmd + `echo hi` + mid-command edit | Narrates "hi" cleanly. Regression pin for the cmd suffix-reprint conflation fix (PR #268). |
| **45c-2** | cmd + `dir` long output | Reads end-to-end through NVDA chunking. No spinner-storm. |
| **45c-3** | cmd + alt-screen-using TUI app (`vim`, or `more <largefile>`) | Entry + exit announce as expected. NVDA mode behaviour matches pre-Cycle-45c (alt-screen suppresses boundary processing in `SessionModel`). |
| **45c-4** | Claude shell + long response | Mid-stream narration intact (depends on shell verbosity policy; `LineByLine` opt-in via `View → Output Verbosity`). |
| **45c-5** | `Ctrl+Shift+D` diagnostic bundle | Bundle contains `--- CONTENT HISTORY (last 64KB) ---` section (renamed from `--- LINEAR STREAM ---`). Sibling `content-history-<ts>.txt` file is created under `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\`. |
| **45c-6** | OSC 133-emitting tool (`pwsh` with PSReadLine, or `cmd` with the `set prompt=` OSC 133 prefix) | Tuple boundaries detected correctly; SessionTuple `CommandText` / `OutputText` are populated via `ContentHistory.sliceText` (regression pin for the OSC 133 migration from PR-3a). Verify by pressing `Ctrl+Shift+Y` after a command finishes — the clipboard payload should contain the command text + output, not row-walk fragments. |

### Cycle 46 — UIA TextEdit caret as the output channel (PRs #287–#291)

ADR 0002 flipped the **channel** for terminal output from
UIA `RaiseNotificationEvent` to a UIA TextEdit caret on
`TerminalView`. The four shipped PRs are described in the
ADR; this matrix row-set is the user-visible validation
gate. Run on the post-PR-D `main` build (commit `9bfdd48`
or later); a fresh preview from the release pipeline is the
easiest fixture. The original motivating issue ("I can't
interrupt a long `dir` read by typing or pressing Alt"
from the PR #282 → #286 failure trail) is the centerpiece
of rows **46-1** and **46-2**; if either row fails, escalate
to a post-Cycle-46 fixup PR rather than tagging a release.

| Row | Test | Pass criterion |
|---|---|---|
| **46-1** | Focus the terminal surface (Alt+Tab into pty-speak; NVDA already running). | NVDA announces "Terminal — edit" (or equivalent for the user's NVDA verbosity). Pre-PR-B this announced "Terminal — document". |
| **46-2** | cmd + `dir` long output. **Before NVDA finishes reading, type any letter into the prompt.** | NVDA stops mid-sentence the moment the typed character emits (NVDA's "Speech interrupt for typed character" setting, default on). This is the original Cycle 46 pain point being fixed. |
| **46-3** | cmd + `dir` long output. **Before NVDA finishes reading, press `Alt` to open the menu.** | NVDA stops the read and announces "View menu" (or the focused menu's UIA name). The interrupt is via the menu's UIA focus-change event, not via a notification dance. |
| **46-4** | cmd + `dir`. Press `Insert+Down` (NVDA read-all). | NVDA reads through the ContentHistory tail (last 256 KB) end-to-end with native NVDA pacing. Pre-PR-B "read all" read the screen grid (30×120 cells of mostly U+0020), which sounded like silence punctuated by content; post-Cycle-46 the read is contiguous content. |
| **46-5** | After cmd `dir`, press `Up Arrow` repeatedly. | NVDA walks back line-by-line through the ContentHistory tail. `Ctrl+End` jumps to the end; `Ctrl+Home` jumps to the start. |
| **46-6** | Press `Ctrl+Shift+Up` (the SpeechCursor manual review hotkey). | NVDA announces the entry the cursor moved to (using `ActivityIds.diagnostic`). When at the boundary, NVDA announces "Already at the first entry" / "Already at the latest entry". This row pins that PR-D's delegation didn't break the manual review surface (PR-D only delegates the auto-drive path; manual hotkeys stay on the notification channel). |
| **46-7** | PowerShell + `Get-Process`. | Same as 46-2 / 46-4 but with PowerShell's longer prompt + paginated output. Confirms the caret pacing works regardless of shell. |
| **46-8** | Claude REPL + a streaming response. | Mid-stream chunks flow through `ContentHistory.appendFromEvent`; the caret-move event fires per tuple finalisation. Streaming should feel like NVDA is reading along with the model, not a delayed block-read at the end. |
| **46-9** | Non-output notifications: press `Ctrl+Shift+H` (health check), `Ctrl+Shift+S` (session-log path), `Ctrl+Shift+D` (diagnostic snapshot). | Each still produces a notification-style announce. ADR §"Decision" clause 5 keeps notifications for non-terminal-content events; the caret-only path is for terminal command I/O. |
| **46-10** | Trigger an error (e.g. a malformed escape sequence; `printf '\033[?garbage'` in cmd). | `ActivityIds.error` notification fires as today. Error path was not touched by Cycle 46. |
| **46-11** | cmd + `dir` of a moderately-large directory (the install dir works; produces ~19 KB of output). Listen for the audible read after the command completes. | NVDA reads the last ~800 chars of output and stops cleanly. Pre-cap behaviour was a single 5–10 minute SAPI utterance that NVDA could not interrupt; post-cap the utterance is bounded to ~30–60 seconds. Subsequent commands' announces are no longer queued behind the giant read. |
| **46-12** | After 46-11, press `Ctrl+Shift+O`. | A new text editor window opens (whichever app handles `.txt` files) with the full `OutputText` of the most recent tuple. NVDA announces "Opening last output." then transitions focus to the editor. The file lives under `%LOCALAPPDATA%\PtySpeak\extracts\last-output-<timestamp>.txt`. |
| **46-13** | Restart pty-speak (so `History` is empty), focus the terminal, press `Ctrl+Shift+O` before running any command. | NVDA announces "No prior output." No editor opens. No file is written. |
| **46-14** | After 46-11, press `Ctrl+Shift+A`. | NVDA re-narrates the last ~800 chars of the most recent output via `ActivityIds.output`. Useful when the auto-narrate was missed (user was speaking, typing, switched window). Same cap as the auto-narrate. |
| **46-15** | Fresh app (no commands run), press `Ctrl+Shift+A`. | NVDA announces "No prior output." No re-narration. |
| **46-16** | Run `echo` with no argument (cmd shell will print a single empty-line response), then press `Ctrl+Shift+A`. | NVDA announces "Last command produced no output." (the latest tuple's `OutputText` is whitespace-only; covered by the `IsNullOrWhiteSpace` guard). |

**Diagnostic decoder.** If 46-1 fails (focus still announces
"document"): `TerminalAutomationPeer.GetAutomationControlTypeCore`
regressed — re-check
`src/Terminal.Accessibility/TerminalAutomationPeer.fs`. If
46-2 fails (no typing interrupt during read): NVDA's "Speech
interrupt for typed character" setting may be off in the
user's NVDA profile — confirm in NVDA's Keyboard settings
panel before assuming a code regression. If 46-4 returns 0
chars: `_contentHistory` likely null at peer-query time —
check that `Program.fs`'s `SetContentHistory` runs in
compose() before the window becomes visible (was at
Program.fs ~line 925 at PR-B merge). If 46-2 typing
interrupts NVDA but speech doesn't resume on the next chunk:
`RaiseCaretMovedToTail` may need to fire
`AutomationEvents.TextPatternOnTextChanged` in addition to
`TextPatternOnTextSelectionChanged` (the helper is in
`TerminalAutomationPeer.fs`; extend behind the one call
site). If 46-6 fails: the
`runSpeechCursorNext/Previous/JumpToLatest` handlers in
`Program.fs` may have accidentally been routed through the
caret helper — those should stay on `window.TerminalSurface.Announce`
per the PR-D scope refinement. If 46-11 NVDA still hangs on
the `dir` read for minutes: the `OutputAnnounceCapChars` cap
in `Program.fs` either isn't being applied (check for
`Tuple-final announce truncated.` log line at
INFO level in the FileLogger active log) or the cap value is
too large for SAPI's chunking — drop from 800 to 400 and
retest. If 46-12 the editor opens but is empty: the tuple
finalised with an empty `OutputText` (cmd / PowerShell can
do this for commands that don't emit visible output — try a
clearly-visible command like `dir` instead).

### Cycle 47 — CMD interaction test corpus + idle-flush

Cycle 47 shipped a `Diagnostics → CMD Interaction Tests`
submenu corpus exercising each cmd interaction primitive
(echo / `set /p` text + numeric / `choice` yes-no + multi
/ `pause` / progress loop / stderr) and an idle-flush
mechanism that fires `Announce` after a configurable parser-
idle period so intra-script prompts (`set /p`, `pause`,
`choice`) speak before the user has to guess what's being
asked. Each matrix row walks one script through the submenu
+ verifies the new behaviour.

Pre-walk setup: bind a fresh preview build with the post-
Cycle-47-follow-up `main` and a default config. Default
shell = `cmd`. NVDA running. No active typing in the
prompt before each test (so the Esc-clear can't surprise
you mid-keystroke).

| Row | Test | Pass criterion |
|---|---|---|
| **47-1** | Open Alt menu → arrow to `Diagnostics` → arrow to `CMD Interaction Tests` (`C`) → submenu opens. | NVDA announces "CMD Interaction Tests, submenu". Each sub-item reads as e.g. "Simple Echo, item 1 of 8" — no `"11 of 8"` garbling from the old digit-prefixed mnemonics. |
| **47-2** | Click `Simple Echo` (test 01). | NVDA announces "Test command inserted: test-01-echo. Press Enter to run." The script path is now in the cmd input buffer. Press Enter; all eight numbered lines audible; ready-prompt click at the end. |
| **47-3** | Click `Text Input` (test 02). Listen **before** typing anything. | NVDA announces "Test command inserted: test-02-text-input. Press Enter to run." Press Enter. Wait ~½ second. Idle-flush fires: NVDA narrates "This test prompts for a text string and echoes it back. Enter your name:" **before** you've typed anything. Type a name + Enter; NVDA narrates the greeting line. |
| **47-4** | Click `Numeric Input + Calculation` (test 03). | Same pattern as 47-3 but for `set /p num=` + `set /a`. Prompt audible before typing; result audible after Enter. |
| **47-5** | Click `Yes / No Choice` (test 04). | Idle-flush narrates the test description + `"Continue? [Y, N]?"` prompt **before** the user presses Y or N. Single-key press; branch result narrates. |
| **47-6** | Click `Multi-Option Choice` (test 05). | Idle-flush narrates the four options + the `"Pick 1-4:"` prompt before the user picks. |
| **47-7** | Click `Pause / Continue` (test 06). | First section audible; idle-flush narrates the cmd `"Press any key to continue . . ."` prompt; press any key; second section audible. |
| **47-8** | Click `Progress Loop` (test 07). | The five `"Step N of 5"` lines arrive across ~5 seconds. Idle-flush fires every ~350 ms during the gaps between steps, so each `"Step N of 5"` is narrated separately, not all batched at tuple finalise as it would have been pre-Cycle-47. |
| **47-9** | Click `Stderr Output` (test 08). | All six lines audible (no audible distinction between stdout and stderr; pty-speak unified streams). |
| **47-10** | **Esc-clear behaviour**: type `garbage` into the cmd prompt without Enter. Open the submenu, click any test (e.g. `Simple Echo`). | The `garbage` text is wiped; the inserted invocation is `"...test-01-echo.cmd"` alone, ready for Enter. Without the Esc prefix, the typed `garbage` would prepend to the invocation and break the parse. |
| **47-11** | **`Ctrl+Shift+A` after a test ran**: walk test 01, then press `Ctrl+Shift+A`. | NVDA re-narrates the last 800 chars of the test's output — the same content the auto-narrate produced when the test finalised. |
| **47-12** | **`Ctrl+Shift+O` after a test ran**: walk test 07 (progress loop), then press `Ctrl+Shift+O`. | The default text editor opens with the full `OutputText` of the just-finished progress-loop tuple. All five `"Step N of 5"` lines visible alongside the start / end markers. |
| **47-13** | **Semantic-boundary markers in the document**: run two or three commands (e.g. `echo hi`, `dir`, `echo bye`). Press `NVDA+Numpad7` to move the review cursor to the top of the document. Press Down-Arrow line-by-line through the buffer. | The review cursor lands on `--- begin prompt ---` lines between commands. For shells emitting OSC 133 (claude), `--- end prompt ---` / `--- begin output ---` / `--- end output ---` are also navigable. For cmd, `--- begin prompt ---` appears at every prompt boundary AND `--- end output ---` is synthesised between cmd commands (see 47-20). The marker lines are NOT visible on screen — only in the NVDA review-cursor document. |
| **47-14** | **Tuple-final narrates only the curated output**: in a fresh session, type `echo hi` and press Enter. Listen carefully to what NVDA reads at command-end. | NVDA narrates `"hi"` — NOT `"echo hi[newline]hi[newline]C:\\Users\\dev>"`. The auto-narrate carries only the curated `tuple.OutputText`, not the surrounding input echo or next-prompt path bytes. The FileLogger log shows `Tuple-final announce. Length=<N>` matching `OutLen` from the SessionModel finalisation line (within ~5 chars for trailing newline normalisation). |
| **47-15** | **No marker labels audible during auto-narrate**: in a fresh session, run `echo hi`. Listen for the verbatim string `"begin prompt"` or `"begin output"` in the spoken text. | NVDA narrates only `"hi"`. The audible path does NOT include `"--- begin prompt ---"`, `"--- begin output ---"`, etc. Those marker lines remain in the review-cursor document (47-13 still passes) but the auto-narrate path doesn't trigger NVDA to read DocumentRange. |
| **47-16** | **Idle-flush narrates a `set /p` prompt before the user types**: run `test-02-text-input.cmd` via `Diagnostics → CMD Interaction Tests → Text Input`. Press Enter and listen WITHOUT typing. | Within ~½ second of the script reaching `set /p`, NVDA narrates the "Enter your name:" prompt. After typing + Enter, the post-prompt body narrates separately. Was the regression: pre-fix the prompt only narrated AFTER the user typed and pressed Enter, mixed with the typed value. FileLogger shows `Idle-flush announce. SliceFrom=… SliceTo=…` lines bumping past `latestSeq`. |
| **47-17** | **Test-bracketed extractor — single run**: run test 01 (`Simple Echo`) once. Open Alt → Diagnostics → Extract → Test Run → Test 01 — Echo. | NVDA announces "ExtractTestEcho copied to clipboard: <N> bytes. Extract file at %LOCALAPPDATA%\\PtySpeak\\extracts\\extracttestecho-<ts>.txt." Pasting yields the eight `"Line N of 8"` lines + the surrounding two echo lines, bracketed by the extract-file header. No surrounding session prompts. Run test 01 again, fire the same extractor: clipboard now shows both runs with `--- test-01-echo run 1 of 2 ---` / `--- test-01-echo run 2 of 2 ---` dividers. Fire the extractor in a fresh session before running test 01: clipboard reads `"(no runs of test-01-echo found in the bundle)"`. |
| **47-18** | **Diagnostic bundle preserves newlines**: run `echo hi` once. Press `Ctrl+Shift+D`. Paste the bundle into any monospace text view. Locate the `--- CONTENT HISTORY (last 64KB) ---` section. | The content shows the cmd banner on multiple lines, `echo hi` on its own line, `hi` on its own line, and the next prompt's path on its own line — not the pre-fix squashed `"echo hihiC:\\Users\\..."` single-line blob. The newlines ContentHistory's CRLF events recorded are preserved by `sanitiseForBundle`. |
| **47-19** | **Marker labels in begin/end form**: run two commands (e.g. `echo first` then `echo second`). Press `NVDA + Numpad7` to move the review cursor to the top of the document; press Down-Arrow line-by-line through the buffer. | Marker lines read as `"--- begin prompt ---"`, `"--- end prompt ---"`, `"--- begin output ---"`, `"--- end output ---"` (the four-marker model) rather than the previous singleton `"--- prompt ---"` / `"--- input begins ---"` etc. Cmd only emits `PromptStart` directly — see 47-20 for `--- end output ---` synthesis between cmd commands. |
| **47-20** | **Synthesised `--- end output ---` between cmd commands**: run two commands (e.g. `echo first` then `echo second`). Press `NVDA + Numpad7`, then Down-Arrow through the buffer between `first`'s output and `second`'s prompt. | The review cursor surfaces `--- end output ---` between `first`'s output bytes and `second`'s prompt (specifically: it appears AFTER `second`'s prompt-path TextSpan in the rendered tail because the active span seals at marker-insertion time and the new prompt's path arrived just before — heuristic placement). Without the synthesis, the cmd shell would only show `--- begin prompt ---` markers and no "end" boundary. Shells with OSC 133 (claude) get all four markers naturally. |
| **47-21** | **Typing-window suppression silences mid-keystroke word announces**: with NVDA's `Speak typed characters` set per preference but `Speak typed words` `Off`, type `echo hi` at the cmd prompt slowly enough to hear NVDA's per-keystroke read. | NVDA announces each typed character (from its keyboard hook) but does NOT additionally announce the accreting prefix as if it were inserted text (`"e"`, `"ec"`, `"ech"`, `"echo"`, ...). The UIA Text-pattern view stays stable across NVDA's `ITextProvider` polls while a keystroke is within the 350 ms typing window. After ~½ second of no typing, press `NVDA + Numpad7` and Down-Arrow: the review cursor navigates the just-typed text (it re-enters the materialised view once the window elapses). |
| **47-22** | **Announce-trail audit via Debug log**: enable Debug logging (`View → Logging Level → Debug`), then run `echo hi`. Press `Ctrl+Shift+D` and paste the bundle. Grep the `--- FILELOGGER ACTIVE LOG ---` section for `MsgHead=`. | Each `RaiseNotificationEvent firing` line at INFO now has a paired Debug entry containing the first 60 chars of the announce text (`MsgHead="hi"` for the tuple-final auto-narrate, marker labels for menu announces, etc.). Gives forensics access to "what did NVDA hear?" without needing live audio capture. |
| **47-23** | **`set/p` replay suppression**: run `set /p var=Enter your text:` at the cmd prompt. Wait for NVDA to announce "Enter your text:" (idle-flush, ~350 ms after cmd's prompt). Type `foo` then Enter. | Pre-fix: NVDA announced "Enter your text:" once (idle-flush), then "Enter your text: foo" again at tuple-final after Enter. Post-fix: idle-flush announce of "Enter your text:" still fires; the tuple-final announce trims that prefix and either says only the new suffix (e.g. " foo" plus whatever the command output) or — if the suffix is whitespace-only — is suppressed entirely. Grep the bundle's FileLogger for `Tuple-final prefix trim.` to confirm the trim path fired. |
| **47-25** | **Menu mnemonic conflicts resolved**: open the menu via `Alt`. Then press `D`. | The Display submenu opens (not Data). Press `Alt` again to close, then `Alt` + `A`: the Data submenu opens. Press `Alt` → `G` → `M`: the CMD Interaction Tests submenu opens (formerly required `Alt` → `G` → `C` which also matched "Test Process Cleanup"). |
| **47-26** | **Idle-flush typing-window silence**: with earcons enabled and Debug logging on, type `echo hi` slowly at the cmd prompt (one char per second). | NVDA does NOT speak each typed character via pty-speak's announce path (it may still speak via its own keyboard-hook char-speech, which is independent — see test environment NVDA settings). The `ReadyForInput` 3000 Hz click does NOT play on each keystroke. After you stop typing for ≥ 350 ms, the next idle-flush tick fires normally and announces any accumulated cmd output. Grep the bundle's FileLogger for `Idle-flush announce` lines — pre-fix the diagnostic ran through `MsgHead=e`, `MsgHead=c`, `MsgHead=h`, ...; post-fix only the post-Enter `MsgHead=hi` line appears for an `echo hi` run. |
| **47-27** | **CSI-row-change synthetic newlines**: open a fresh cmd session. Inspect the diagnostic bundle's `--- CONTENT HISTORY ---` section, or use the review cursor's line navigation. | The banner reads as separate lines: `"Microsoft Windows [Version ...]"`, then `"(c) Microsoft Corporation. All rights reserved."`, then the prompt path. After `echo hi`, the `"hi"` output and the next prompt are on different lines, not concatenated. cmd's conpty translator emits CSI cursor-positioning sequences (rather than CRLF) for some row transitions; ContentHistory now treats those as logical-row breaks and inserts a synthetic `Newline` into the substrate. |
| **48-B1** | **Cycle 48 PR-B observe-only walk**: enable Debug logging (`View → Logging Level → Debug`). Run test 01 (`Simple Echo`). Press `Ctrl+Shift+D`; in the bundle's `--- FILELOGGER ACTIVE LOG ---` section, grep `ShellInteraction transition`. | The transition log shows: `Composing → Executing` on the user's Enter, `Executing → Composing` on the next PromptStart, with the prompt path included in the trigger. Audible behaviour unchanged from preview.117 (PR-B is observe-only). |
| **48-B2** | Run test 02 (`Text Input`). Grep `ShellInteraction transition` in the log. | Sequence: `Composing → Executing` on Enter; **`Executing → Composing` via `SubPromptIdle("Enter your name:")`** when cmd prints the prompt and pauses (this is the new sub-prompt detection); `Composing → Executing` on the user's answer Enter; `Executing → Composing` on the next shell PromptStart. |
| **48-B3** | Run test 04 (`Yes / No Choice`). | Sequence includes a `SubPromptIdle` whose triggered Composing state has `single-key=true` (the `[Y, N]?` regex matched). |
| **48-B4** | Run test 05 (`Multi-Option Choice`). | Same as 48-B3 but for `[Y,N,A,E]?`-style prompt — `single-key=true`. |
| **48-B5** | Run test 06 (`Pause / Continue`). | Same as 48-B3 but the SinglekeySubmit detection is via the literal `"Press any key to continue"` substring match. |
| **48-B6** | Run test 07 (`Progress Loop`). Grep `SubPromptIdle` in the log. | NO `SubPromptIdle` transitions fire during the loop — each `"Step N of 5"` line ends with `\n` so the sub-prompt detector's "last byte not `\n`" guard correctly suppresses. The loop ends with one `Executing → Composing via PromptDetected` when cmd prints the next shell prompt. |
| **48-B7** | Run test 08 (`Stderr Output`). | Same single-shot pattern as test 01 — the stderr lines end with `\n`, so no spurious `SubPromptIdle`. |
| **48-B8** | Type `garbage` at the cmd prompt without pressing Enter. Wait 5 seconds. Grep `ShellInteraction` in the log. | NO transitions fire while you're typing (no `\r` byte yet; no PromptStart; sub-prompt detector ignores Composing-state input). The state machine stays in `Composing` for the entire typing burst. Press Enter after the wait — the single `Composing → Executing(cmd=)` transition fires. (The empty `cmd=` placeholder is expected in PR-B; PR-D wires the UserInputBuffer that captures the typed text.) |
| **48-C1** | **Cycle 48 PR-C source-tag walk**: with Cycle 48 PR-B's wiring still in place, run test 02 (`Text Input` / `set /p`). Press `Ctrl+Shift+D`; in the bundle's `--- CONTENT HISTORY ---` section, read the `Stats:` line. | The line ends with `\| Sources: UserInputEcho=N CmdOutput=N CmdSubPrompt=N ShellPrompt=N BoundaryMarker=N Unknown=N`. For test 02: `UserInputEcho` should be > 0 (the user's name + the cmd echo of the `set /p` command line); `CmdOutput` > 0 (the `Enter your name:` sub-prompt + `Hello <name>!` output). `BoundaryMarker` ≥ 1 (PromptStart). `Unknown` should be 0 once the resolver is wired (any non-zero value means entries landed before `setSourceResolver` ran — only expected on the first few startup bytes). |
| **48-C2** | Same diagnostic-bundle session. Inspect the `Source` distribution per cycle of typing → cmd-output. | Each typed character (echo) tags as `UserInputEcho`. Each cmd-emitted line of output tags as `CmdOutput` or `CmdSubPrompt`. Markers tag as `BoundaryMarker`. Distinguishing `CmdSubPrompt` from `CmdOutput` is a refinement deferred to PR-D / PR-E (they get the same `CmdOutput` tag in PR-C; the distinction is set when `entrySourceFor` is enriched in PR-E). |
| **48-D1** | **Cycle 48 PR-D buffer-capture walk**: with Debug logging on, run test 01 (`Simple Echo`). Type `echo Line 1 of 8 ...` then Enter. Grep `UserInputBuffer captured` in the bundle's FileLogger section. | One log line per Enter, showing `Length=N Text=echo Line 1 of 8 ...`. The captured text matches what was typed (no edit-history garbage). For test 02 (set/p), TWO Enter captures: the `set /p` command line + the typed answer. |
| **48-D2** | Type `echi` at the prompt, press BACKSPACE, type `o`, press Enter. | The captured text on Enter is `ech` + `o` = `echo` (NOT `echi` + `o` = `echio`). Backspace removed the `i` from the buffer before `o` was appended. |
| **48-D3** | Press an arrow key while typing. | The buffer doesn't track the cursor move (PR-D's byte-stream MVP only handles printable ASCII + BS + CR). Subsequent characters insert at the wrong logical position relative to cmd's view. Acceptable for PR-D; refining to key-level tracking is deferred. The `EnterPressed` capture text will be in source-typing order, not in cmd-rendered order. |
| **48-E1** | **Per-character chatter gone**: type `echo hi` slowly at the cmd prompt (one char per second). Listen. | Pty-speak does NOT announce each typed character. NVDA's keyboard-hook char-speech (if enabled in NVDA settings) still fires per-char — that's the screen reader's responsibility, not pty-speak's. The bundle's FileLogger should contain NO `Idle-flush announce` lines during typing (the idle-flush body is retired in PR-E). |
| **48-E2** | **Tuple-final announce still works**: complete `echo hi` + Enter. | NVDA narrates `hi` as before (SessionModel-driven path unchanged). Earcon plays. The `Tuple-final announce.` log line still appears in the bundle. |
| **48-E3** | **Sub-prompt announce via state machine**: run `set /p name=Enter your name:`. | NVDA narrates `Enter your name:` ~350 ms after cmd's pause. The bundle's FileLogger contains `ShellInteraction transition: ... --[SubPromptIdle(Enter your name:)]--> Composing(...)` followed by `PR-E sub-prompt announce. Length=17` (or similar). Earcon plays. |
| **48-E4** | **Sub-prompt announce on pause**: run `pause`. | NVDA narrates `Press any key to continue . . .` via the SubPromptIdle path. SinglekeySubmit=true in the transition log. Pressing any key triggers `Composing → Executing` and the test continues. |
| **48-E5** | **SpeechCursor skips echo in AutoDrive**: type `echo hi` + Enter. With Speech Cursor in AutoDrive mode, listen during typing. | No per-entry announce for the echoed characters (UserInputEcho entries are skipped per ADR §9.6). The output `hi` announces normally. |
| **48-E6** | **SpeechCursor skips echo in Manual nav**: after running `echo hi`, press `Ctrl+Shift+Up` repeatedly to walk backwards. | The cursor jumps over the echoed `echo hi` chars (UserInputEcho entries) — manual navigation lands only on output + markers. Stop pressing Up; press `Ctrl+Shift+End` to return to live. |
| **48-E7** | **No-op transition is silent**: type a command, then press Enter at an empty prompt. | NVDA narrates the first command's output normally. The empty-Enter does not produce a tuple-final announce (SessionModel may extract an empty OutputText → tupleFinaliseAnnounce = None) but the SubPromptIdle path would similarly not fire (no Executing window output). Earcon may or may not play depending on whether SessionModel finalises a tuple. |
| **48-E8** | **Streaming policy honoured**: switch `View → Output Verbosity → Off`. Run `echo hi`. | NVDA does NOT narrate `hi` (Off suppresses tuple-final). SpeechCursor is the only path to hear output. Earcon still fires. Switch back to `Tuple Final` to restore default behaviour. |

**Diagnostic decoder.** If a test's prompt isn't audible
before user input (47-3 / 47-4 / 47-5 / 47-6 / 47-7 fail):
the idle-flush isn't firing. Check the FileLogger log for
`Idle-flush announce.` lines at INFO; if absent, the
`ShellPolicy.IdleFlushMs` value is `None` for the active
shell (check `currentShellPolicy` via a fresh
`Ctrl+Shift+D` snapshot — the diagnostic battery doesn't
include it today; could be added later) or the
`DispatcherTimer` failed to start. If 47-2 fails with
"NVDA announces nothing on menu click": the
`MenuItem_CmdTest*` reflective wiring (`Program.fs` line
~3877) didn't find a matching named MenuItem — check
`MainWindow.xaml`'s `x:Name` matches the AppCommand
`nameOf` result. If 47-8 batches all five steps instead of
narrating each: the idle-flush threshold is too high
(should be ≤ the inter-step gap; tune `IdleFlushMs` down
from 350 if needed). If 47-10 the `garbage` text still
prepends the invocation: the Esc byte didn't reach cmd —
verify `runCmdTest` is prepending `0x1Buy` to the bytes.

## Recording results

For each release tag:

1. Copy this matrix into the PR that bumps the version (or into the
   GitHub Release's "Smoke test results" comment thread if the
   version-bump PR has already merged).
2. Mark each row PASS / FAIL / N/A. Use FAIL only when the observed
   behaviour disagrees with the "Expected" column; use N/A only when
   the row's stage hasn't shipped yet OR the test fixture isn't
   available for this run.
3. Attach the NVDA Event Tracker log for Stage 5 / 7 / 8 / 11 rows
   (or note "Event Tracker not installed" if running an abbreviated
   smoke pass).
4. **For each FAIL, paste the relevant decoder bullet** from the
   stage's "Diagnostic decoder" subsection, noting which subsystem
   you suspect. This turns every failure into a triaged issue
   rather than a free-form "it didn't work" report.
5. The release workflow will not promote a draft to "latest" unless
   every required-stage row is PASS or N/A.

## Adding new manual tests

When new functionality ships and CI cannot fully verify the user-
visible behaviour, that functionality needs a row in this matrix. A
test belongs here when **at least one** of the following is true:

- The behaviour is observable only through a screen reader (NVDA /
  Narrator / JAWS) and the FlaUI integration tests can't simulate
  what the screen reader does. (Most accessibility rows.)
- The behaviour is observable only at the user's audio output
  (earcons, TTS interaction). (Stage 9 rows.)
- The behaviour involves the OS package layer (installer, signature,
  process tree, auto-update). (Always-run sections + Stage 11.)
- The behaviour requires a clean Windows VM and isn't reproducible
  in the dev sandbox or CI runner (e.g. SmartScreen prompts,
  per-user install paths).

When a test does **not** belong here:

- It can be expressed as an xUnit / FsCheck assertion against the
  parser, the screen buffer, or the F# semantics types. (Those go
  in `tests/Tests.Unit`.)
- It can be expressed as a FlaUI assertion against the UIA tree.
  (Those go in `tests/Tests.Ui`.)
- It's covered by an existing row at a different stage that already
  exercises the same code path.

### Where in the matrix

| Behaviour involves                                | Section                              |
|---------------------------------------------------|--------------------------------------|
| Installer / signature / file layout               | Always-run → Artifact integrity      |
| Process tree, window lifecycle, OS visibility     | Always-run → Launch and process hygiene |
| What NVDA / Narrator says or doesn't say          | Stage matrix → corresponding stage   |
| Audio output (earcons, separate device)           | Stage matrix → Stage 9               |
| Update download / apply                           | Stage matrix → Stage 11              |

If a behaviour spans stages (e.g. Stage 4's Text pattern is exercised
by every later stage's NVDA test), put the *primary* row in the stage
that ships it, and reference it from later stages' decoders as a
"first suspect" pointer.

### Required fields per row

Every row needs all four fields:

| Field            | Purpose                                                                 |
|------------------|-------------------------------------------------------------------------|
| **Test**         | Short noun phrase identifying what's being checked. Stable across releases — issues will reference this name. |
| **Procedure**    | Concrete keystrokes / commands. Reproducible in 30 seconds. If a fixture is needed, link to its location under `tests/fixtures/`. |
| **Expected**     | Verbatim screen-reader output, observable file-system state, or visible UI behaviour. Avoid hedges like "approximately"; pin the wording. |
| **Diagnostic**   | One bullet in the stage's "Diagnostic decoder" subsection mapping the failure to a likely subsystem. Without this, a FAIL is a triage burden; with it, a FAIL is a focused investigation. |

The diagnostic should answer "if this fails, what file / commit /
subsystem do I look at first?" — naming files and properties where
practical, citing PR numbers when the row exists because of a
specific past bug.

### Sunset rules

A row leaves this document when **either**:

- The behaviour becomes verifiable via CI (FlaUI integration test,
  semantic-layer unit test, or workflow assertion). Replace the row
  with a bullet under "Coverage that moved to CI" at the bottom of
  the relevant stage section, naming the test that now pins it.
- The stage that introduced the row is removed from the roadmap.
  Delete the row outright — the matrix is the *current* gate, not
  a historical record. (`CHANGELOG.md` keeps the history.)

Don't leave dead rows in place "just in case". They produce noise
during smoke runs and erode the matrix's authority. If you're not
sure whether to remove or keep a row, keep it for one release with
a `(deprecation candidate)` suffix, then make the call at the next
release based on whether it caught anything during that window.

## Coverage that moved to CI

This list documents tests that *used to* be in the manual matrix and
are now pinned by an automated check. It's the audit trail for the
matrix's shrinking surface area over time.

- **Stage 4 → Inspect.exe shows Text pattern + non-empty
  `DocumentRange.GetText`** is now pinned by
  `tests/Tests.Ui/TextPatternTests.fs` (PR #56). The manual row
  remains in the Stage 4 section because NVDA's review-cursor
  behaviour over the same Text pattern requires manual NVDA, but
  the producer side (pattern present + GetText returns a snapshot
  with the expected length floor) is now CI-pinned.

<!-- DOGFOOD -->
### Cycle 38b + 38c — Per-shell route table + cmd/PowerShell echo-suppression

Cycle 38b introduces a per-shell profile-set TOML override
(`[shell.<key>] profiles = [...]` in `config.toml`); Cycle 38c
ships the first profile that uses it — `EchoSuppressorProfile`,
which strips the user's typed-input echo from NVDA announcements
on cmd / PowerShell.

**Built-in default active sets** (when TOML doesn't override):
- cmd, powershell → `["echo-suppressor", "earcon", "selection"]`
- claude → `["passthrough", "earcon", "selection"]`

**EchoSuppressorProfile behaviour**:
- For `StreamChunk` events with non-empty payload, consult
  `EchoCorrelator` (which has been recording bytes written via
  `ConPtyHost.WriteBytes`). If a leading prefix of the payload
  matches recent input (with CR→CRLF normalisation for cmd's
  echo-translation behaviour), strip that prefix from the NVDA
  announce.
- If the entire payload was echo: NVDA decision is DROPPED; the
  FileLogger gets a `(suppressed echo: ...)` annotation in its
  audit trail.
- If a partial match: NVDA gets the stripped payload; FileLogger
  gets the full original.
- Non-StreamChunk events behave identically to PassThrough.

**Manual matrix rows**:

| Test | Procedure | Expected |
|---|---|---|
| 38b.1 — Active profile set per shell | Switch between cmd / PowerShell / Claude via Ctrl+Shift+1/2/3. After each switch, type a short command. | Cmd / PowerShell: no echoed-input bleed in NVDA announce. Claude: behaves as today (passthrough). |
| 38c.1 — `echo` suppression on cmd | In cmd, type `echo hello` + Enter. | NVDA announces "hello" (or near-equivalent). The typed `echo hello` echo is NOT in the announce. |
| 38c.2 — Suppression audit trail | After 38c.1, press Ctrl+Shift+D. Open the bundle's `--- FILELOGGER ACTIVE LOG ---` section. Search for `(suppressed echo:`. | The annotation appears with the echo-prefix payload. |
| 38c.3 — Partial-echo case | In cmd, type `set` + Enter (cmd lists env vars with `set` as the first echoed word). | NVDA announces the env-var list. The leading `set\r\n` echo is stripped; the rest is announced. |
| 38c.4 — Claude unaffected | Switch to Claude. Send a short message. | Claude behaviour is unchanged (still uses passthrough; selection-list peer still fires for tool-use prompts). |
| 38c.5 — Per-shell override via TOML | Edit `config.toml`. Add `[shell.cmd] profiles = ["passthrough", "earcon"]` (no echo-suppressor). Restart pty-speak (or hot-switch to a different shell and back to cmd). Type `echo hello` + Enter. | Echo is NO LONGER suppressed (because TOML overrode the default and dropped echo-suppressor). |

**Stopping gate for 38b+c**: NVDA hears only the output of
`echo hello` on cmd (no input echo). FileLogger preserves the
suppression annotation for forensics. Per-shell TOML override
respected.

<!-- DOGFOOD -->
### Cycle 38a — Canonical interaction corpus baseline

Cycle 38a is the **regression scaffolding** for the cmd / PowerShell
/ Claude per-shell parser-route work landing in 38b-e. It introduces
a curated catalogue of `(bytes-in, expected-NVDA-out)` scenarios in
[`tests/fixtures/canonical-interactions.toml`](../tests/fixtures/canonical-interactions.toml)
that the `Ctrl+Shift+D` battery loads and runs against the active
shell, reporting per-scenario PASS / FAIL in a new
`--- CANONICAL CORPUS RESULTS ---` section in the bundle.

**No behaviour change in 38a.** The corpus captures CURRENT
behaviour as the baseline. Subsequent sub-cycles flip individual
scenarios from FAIL → PASS as they fix the relevant pipeline
behaviour:

| Sub-cycle | Adds | Scenarios it makes load-bearing |
|---|---|---|
| 38b | Per-shell route table + PowerShell scenarios | `expected_session_tuple` (exit-code capture) |
| 38c | Cmd input-echo suppression | `cmd.echo.plain`, `cmd.set.userprompt`, `expected_payload_regex` |
| 38d | Three-sub-pane channel routing | `expected_pane_routing` (input / current_output / history) |
| 38e | Claude scenarios + per-shell hardening | `cmd.choice.yn` (cross-shell SelectionShown) |

**Corpus schema reference.** Each `[[scenario]]` table requires
`id`, `shell`, `description`, `command`, `must_include`,
`must_not_include`. Optional defaults: `quiescence_ms` (200),
`timeout_ms` (1500). Optional v2 extensions parsed but not yet
enforced in 38a: `setup_command`, `expected_payload_regex`,
`expected_session_tuple`, `expected_pane_routing`, `notes`. Valid
`SemanticCategory` values come from
[`src/Terminal.Core/OutputEventTypes.fs`](../src/Terminal.Core/OutputEventTypes.fs)
`type SemanticCategory` (lines 42-111).

**Adding a scenario.** Edit the TOML file, rebuild, press
`Ctrl+Shift+D`, look at the new section in the bundle. If the
scenario reports PASS but the actual NVDA experience is wrong,
the assertion is wrong — adjust `must_include` /
`must_not_include` to reflect reality and add a `notes` field
describing the gap.

**Bug-tagged scenarios.** Scenarios where current behaviour is
known-wrong carry `notes = "Bug 2026-05-XX: <gap>; fixed in
<sub-cycle>"`. The assertion lists what NVDA CURRENTLY hears so
the corpus stays green; the bug-fix sub-cycle has to update both
the code and the assertion atomically.

**Reading the bundle section.** The header `CORPUS: 9 PASS / 3
FAIL / 12 total` summarises counts. Each scenario block shows id,
elapsed ms, must_include / must_not_include sets, observed
semantic categories, and notes (if present). FAIL scenarios get a
reason after the elapsed time (`missing=…`, `unexpected=…`, or
`no quiescence within Nms`).

**Manual matrix row (Cycle 38a):**

| Test | Procedure | Expected |
|---|---|---|
| 38a.1 — Corpus runs from Ctrl+Shift+D | Open cmd in pty-speak, press Ctrl+Shift+D, wait ~10 seconds | Bundle file in `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\` contains `--- CANONICAL CORPUS RESULTS ---` section. Section starts with `CORPUS: <P> PASS / <F> FAIL / 12 total`. |
| 38a.2 — Per-scenario reporting | Read each of the 12 scenario blocks in the bundle | Each scenario block shows `[PASS]` or `[FAIL]` with elapsed ms; observed semantics line; notes line for `Bug:`-tagged scenarios. |
| 38a.3 — PowerShell skip | Switch to PowerShell with Ctrl+Shift+2; press Ctrl+Shift+D | Bundle reports `CORPUS: 0 PASS / 0 FAIL / 0 total` (no PowerShell scenarios in 38a; these arrive in 38b). |
| 38a.4 — Claude skip | Switch to Claude with Ctrl+Shift+3; press Ctrl+Shift+D | Same as 38a.3 — no Claude scenarios in 38a; these arrive in 38e. |
| 38a.5 — Behavioural calibration | For each `cmd.*` scenario in the bundle, run the same command manually in cmd and confirm the reported PASS / FAIL matches what NVDA actually does | Discrepancies → fixup commit on the PR adjusting the scenario's assertions until baseline matches reality. |

**Stopping gate for 38a.** Maintainer-acknowledged baseline + bundle
integration working. Subsequent sub-cycles use this baseline as
their regression net.

## Why this is manual

CI tests run on a Windows runner with no logged-in interactive
session, no audio hardware, no screen reader, no SmartScreen, no
per-user install path that survives across runs. They can verify
that producers emit the right UIA events, that the parser produces
the right tokens, that the screen buffer mutates correctly, that
the .NET assembly contains the right type with the right override.
They cannot verify what the user actually hears or what the
installer actually shows.

The right model is: **CI catches regressions in the producers;
manual smoke catches regressions in the consumer's experience.**
The two surfaces don't overlap fully, and the parts that overlap
(e.g. the Text pattern's existence on the UIA element) are the
parts we *do* migrate to CI as soon as a stable assertion is
available.

A row that *could* be CI-tested but isn't yet is a follow-up issue,
not a manual-matrix entry. The matrix is for the genuinely-manual
checks: things that need a human, a screen reader, an audio output,
or a real Windows shell.
