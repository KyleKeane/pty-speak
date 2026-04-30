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

### Launch and process hygiene (run on every install)

| Test                              | Procedure                                       | Expected                                                          |
|-----------------------------------|-------------------------------------------------|-------------------------------------------------------------------|
| Single window appears             | Run `Setup.exe`, then launch from Start menu    | **Exactly one** window (the WPF terminal surface). No empty parent console window behind it. |
| Window title shows version        | Look at the window title bar                    | "pty-speak" plus the version number (matches the release tag).    |
| Process tree is one-deep          | Open Task Manager → Details, sort by Image Name | Exactly one `Terminal.App.exe` AND one child `cmd.exe` (the ConPTY child). No leftover `conhost.exe` parent. |
| Window closes cleanly             | Close the window via the X button               | Both `Terminal.App.exe` and the child `cmd.exe` exit within ~2 s. Task Manager shows neither image afterwards. |
| Quit via Alt+F4 closes cleanly    | Press Alt+F4                                    | Same as above — clean exit, no orphans.                           |
| Re-launch after exit              | Launch from Start menu again                    | New window opens cleanly. (Catches state-file corruption / single-instance regressions if those ship later.) |

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

The Stage 4 architecture settled across four PRs:

- **PR #48 (Stage 4a)** — `TerminalAutomationPeer` exposes Document
  role, ClassName, Name, IsControlElement, IsContentElement.
- **PR #54 (PR A)** — `WM_GETOBJECT` subclass hook (kept as a
  legacy MSAA fallback path, not the primary route).
- **PR #55 (PR B)** — `IRawElementProviderSimple` /
  `TerminalRawProvider`, F# `TerminalTextProvider` /
  `TerminalTextRange` over `Screen.SnapshotRows`.
- **PR #56 (PR C)** — pivot to `AutomationPeer.GetPattern` override
  (the public-virtual entry point that's reachable from external
  assemblies, vs. `GetPatternCore` which isn't), plus a FlaUI
  integration test pinning `DocumentRange.GetText`.

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
  WM_GETOBJECT handler is misbehaving or returning a bad provider.
  Most likely the raw-provider hook in `WindowSubclassNative.cs`
  was re-enabled for `UiaRootObjectId` (-25) which is the failure
  mode PR #56 found and reverted; confirm `OBJID_CLIENT` (-4) is
  the only id matched.
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

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Welcome screen                    | Launch `claude`                                 | NVDA reads the welcome screen                                    |
| Prompt → response                 | Type "Say hi", Enter                            | Claude responds; NVDA reads the response                         |
| Spinner does not flood            | Trigger a long-thinking response                | Spinner is announced at most once or as a periodic earcon; NVDA never freezes |
| Quitting cleanly                  | `/exit` or Ctrl+D                               | Process exits; NVDA announces it; terminal returns to host shell |

**Diagnostic decoder for Stage 7:**

- **NVDA frozen during spinner** → Spinner-row dedup is broken.
  Check `tests/fixtures/claude-thinking.txt` replay through
  Stage 5's coalescing path; if the unit tests pass, the
  semantic-layer hashing is fine and the regression is in how
  Stage 7's spinner detector classifies the row.
- **Welcome screen silent** → Claude's TUI emits its first paint
  in a way that bypasses our coalescing entry point. Compare against
  Stage 5's `dir` test — if `dir` works but `claude` doesn't, the
  difference is Claude's use of OSC sequences that our parser may
  not be handling yet.
- **`/exit` leaves orphaned `claude` process** → Same class as
  Stage 3b's "orphan cmd.exe" — the ConPTY shutdown isn't
  signalling the child correctly.

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
