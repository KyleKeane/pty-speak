# Accessibility testing

This is the manual validation matrix every release must pass. CI
verifies behaviour at the UIA producer level; this document verifies
behaviour at the screen-reader consumer level. The two are different
enough that we cannot replace one with the other.

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

## Stage validation matrix

For every release, run the rows that correspond to stages already
shipped. A row is **PASS** only if the screen reader behaviour matches
the expected column verbatim.

### Stage 0 — empty window (currently shipped: `v0.0.1-preview.15`)

| Test                              | Procedure                                       | Expected NVDA behaviour                                          |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Window opens                      | Launch app                                      | "pty-speak terminal, window" (from `AutomationProperties.Name`)  |
| Window title                      | Press Insert+T                                  | NVDA announces "pty-speak"                                       |
| Inspect.exe shows correct surface | Open Inspect.exe, hover over the window         | `AutomationId="pty-speak.MainWindow"`, `ControlType=Window`      |

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

### Stage 4 — text exposure (planned)

The matrix below describes the full Stage 4 acceptance set. Stage 4
ships in three PRs (spike + 4a + 4b + 4c, see
[`SESSION-HANDOFF.md`](SESSION-HANDOFF.md#stage-4-implementation-sketch));
the first three rows ("Window opens", "Review cursor reads current
line", "Inspect.exe shows correct surface") become testable after
PR 4a, the navigation rows after PR 4b, and the Inspect.exe row is
also the manual-smoke gate before PR 4c's FlaUI test exists.

| Test                              | Procedure                                       | Expected NVDA behaviour                                          |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Window opens                      | Launch app                                      | "pty-speak, document"                                            |
| Review cursor reads current line  | NVDA+Numpad8                                    | NVDA reads the prompt line                                       |
| Review cursor moves by line       | NVDA+Numpad7 / NVDA+Numpad9                     | NVDA reads previous / next line of the buffer                    |
| Review cursor moves by word       | NVDA+Numpad4 / NVDA+Numpad6                     | NVDA reads previous / next word                                  |
| Review cursor moves by character  | NVDA+Numpad1 / NVDA+Numpad3                     | NVDA reads previous / next character                             |
| Empty line is announced           | Move review cursor to a blank row               | NVDA says "blank" — *not* the previous row, *not* silence        |
| Inspect.exe shows correct surface | Open Inspect.exe, hover over the terminal       | ControlType=Document, ClassName=`TerminalView` (set explicitly by the Stage 4 automation peer; today's element inherits a generated WPF class name), Text pattern present |

### Stage 5 — streaming output

| Test                              | Procedure                                       | Expected NVDA behaviour                                          |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| `echo hello`                      | Run inside the terminal                         | NVDA says "hello" exactly once                                   |
| `dir`                             | Run inside the terminal                         | NVDA reads each line in order with brief pauses                  |
| Busy loop printing dots for 5 s   | Run a script that prints `.` 100×/s             | NVDA does not get stuck; speech queue drains within ~1 s of end  |
| Cursor blink                      | Idle terminal with blinking cursor              | No announcements; only the cursor visibility flips               |
| Notification event tracking       | Event Tracker add-on enabled                    | One Notification event per coalesced flush, with non-empty `displayString` |

### Stage 6 — keyboard input

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Type into PowerShell              | Type `ls` and Enter                             | Listing read by NVDA                                             |
| Up/Down arrow history             | After running `ls`, press Up                    | PowerShell echoes the previous command; NVDA reads new prompt   |
| Tab completion                    | Type `ls C:\W` then Tab                         | Completion appears; NVDA reads the completed path                |
| Ctrl+C interrupts                 | Run `ping localhost -t`, press Ctrl+C           | Process interrupts; NVDA reads the new prompt                    |
| **NVDA shortcut still works**     | Press Insert+T                                  | NVDA announces the window title (proves we did not swallow Insert) |
| **Caps Lock review still works**  | Press CapsLock+Numpad7                          | NVDA reads previous review line                                  |

### Stage 7 — Claude Code roundtrip

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Welcome screen                    | Launch `claude`                                 | NVDA reads the welcome screen                                    |
| Prompt → response                 | Type "Say hi", Enter                            | Claude responds; NVDA reads the response                         |
| Spinner does not flood            | Trigger a long-thinking response                | Spinner is announced at most once or as a periodic earcon; NVDA never freezes |
| Quitting cleanly                  | `/exit` or Ctrl+D                               | Process exits; NVDA announces it; terminal returns to host shell |

### Stage 8 — selection lists

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| List announced as listbox         | Trigger a Claude prompt that asks "Edit / Yes / Always / No" | NVDA says "list, Edit, 1 of 4" or equivalent          |
| Down arrow advances               | Press Down                                      | NVDA says "Yes, 2 of 4"                                          |
| Up arrow goes back                | Press Up                                        | NVDA says "Edit, 1 of 4"                                         |
| Enter confirms                    | Press Enter on selected item                    | List disappears; NVDA reads Claude's next output                 |
| Inspect.exe shows tree            | Open Inspect.exe while the list is shown        | List + ListItem children with `IsSelected` set on the highlighted item |

### Stage 9 — earcons

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Red text earcon                   | Echo `\x1b[31mError\x1b[0m`                     | Alarm earcon plays before/with NVDA's "Error"                    |
| Green text earcon                 | Echo `\x1b[32mDone\x1b[0m`                      | Confirm earcon plays                                             |
| Mute toggle                       | Press Ctrl+Shift+M, then echo a red string      | No earcon, NVDA still reads the text                             |
| No NVDA TTS interference          | Earcon and NVDA speech overlap                  | Both audible; neither cuts the other                             |
| Separate audio routing            | Set earcon device to second output              | Earcons play on chosen device, NVDA continues on default          |

### Stage 10 — review mode

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Toggle review mode                | Press Alt+Shift+R                               | NVDA announces "Review mode"                                     |
| Quick-nav `e` to next error       | After `python -c "raise ValueError('boom')"`, press Alt+Shift+R then `e` | NVDA reads the red traceback line                |
| Quick-nav `c` to next command     | After running several commands, press `c`       | NVDA reads the next command line                                 |
| Exit review mode                  | Press Alt+Shift+R again                         | NVDA announces "Interactive mode"; keys go to PTY                |

### Stage 11 — auto-update

| Test                              | Procedure                                       | Expected behaviour                                               |
|-----------------------------------|-------------------------------------------------|------------------------------------------------------------------|
| Initial install                   | Run `pty-speak-Setup.exe`                       | NVDA reads install progress; app launches; title shows version   |
| Manual update check               | In running older version, press Ctrl+Shift+U    | NVDA reads "Update X.Y.Z available, downloading..."              |
| Progress announcement             | During download                                 | Periodic percent announcements via Notification(MostRecent)      |
| Apply and restart                 | After download completes                        | App restarts within ~2 s, no UAC prompt, new version in title    |
| Manifest signature verification   | Tamper with `releases.json` on a local mirror   | App refuses the update and announces the verification failure   |

## Recording results

For each release tag:

1. Copy this matrix into the PR that bumps the version.
2. Mark each row PASS / FAIL / N/A.
3. Attach the NVDA Event Tracker log for Stage 5 / 7 / 8 / 11 rows.
4. The release workflow will not promote a draft to "latest" unless
   every required-stage row is PASS or N/A.

## When to add new rows

Any time we ship a stage, the rows for that stage become required and
move into the "always run" portion of the matrix. Out-of-stage features
(verbosity profiles, OSC 133 heuristics, compiler diagnostic
interpreters) get their own rows in their own section as they ship.
