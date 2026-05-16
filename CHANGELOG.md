# Changelog

All notable changes to `pty-speak` will be documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project follows [Semantic Versioning](https://semver.org/).

Release tags follow the pattern `vMAJOR.MINOR.PATCH` (e.g. `v0.1.0`),
or `vMAJOR.MINOR.PATCH-preview.N` / `-rc.N` for prereleases.
Releases are produced by **publishing a release** in the GitHub
Releases UI (which creates the tag). The `release: published` event
triggers the Velopack release workflow described in
[`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md), which builds the
artifacts and updates the just-published release with the proper
title, body, and Velopack `Setup.exe` + nupkg + `RELEASES` files.

## [Unreleased]

### Post-Cycle-49 PR-R (2026-05-14): Include cursor row in sub-prompt preamble count

Maintainer post-PR-Q dogfood: after typing the sub-prompt
response + Enter, NVDA narrates `Enter your name:love`
followed by the script's post-Enter response, instead of
JUST the post-Enter response. The user has already engaged
with the prompt+response line (heard the prompt
pre-Enter, typed the response themselves) and shouldn't
hear it again.

Root cause: PR-K's `endRow = cursorRow - 1` in
`capturePreambleForSubPromptResponse`. PR-K added the
`-1` in 2026-05-14 to compensate for an over-count that
was actually a wrap-row miscount (PR-N has since fixed
that properly via the `wrapRows` skip at `startRow`). Now
the `-1` excludes the cursor row, which IS the row
carrying the sub-prompt prompt + user's typed response —
the line that should be counted as preamble.

PR-R reverts to `endRow = cursorRow`. With PR-N's
`wrapRows` skip in `startRow`, the count comes out to 3
for the canonical test-02 case (START + intro +
prompt:response), which drops 3 of the 5 OutputText
lines, leaving the post-Enter response + END marker —
exactly what the user wants to hear.

### Post-Cycle-49 PR-Q (2026-05-14): ContentHistory source captured at first byte + cursor-capture gated to top-level Enter

Maintainer's post-PR-P bundle (preview.128) localised two
distinct bugs:

1. **`Enter your name:` mis-tagged as `UserInputEcho`.**
   `Source = resolveSource state` ran at TextSpan SEAL time
   inside `sealActiveSpan`. The script's `set /p` prompt
   (no trailing newline) accumulated bytes into the active
   TextSpan during `Executing`, but the TextSpan didn't
   seal until cmd went idle — by which point
   `SubPromptIdle` had already flipped `ShellInteraction`
   to `Composing`. The seal-time resolver returned
   `EntrySource.UserInputEcho`. SpeechCursor's
   `renderEntryForManualNav` filter correctly skipped the
   entry per the tag — but the tag itself was wrong.
   PR-Q captures the source at the FIRST byte of the
   active TextSpan (alongside `ActiveSpanStartedAt`),
   stores it in a new `ActiveSpanSource` field, and uses
   it at seal. Falls back to `resolveSource` if the field
   is unset (defensive — shouldn't happen since
   first-byte initialises both fields atomically). Net:
   `Enter your name:` now tags `CmdOutput` correctly and
   becomes navigable in SpeechCursor.

2. **Tuple-final no-trim regression from PR-O.** PR-O's
   cursor-row capture in the byte-write wrapper fired on
   EVERY Enter, including the sub-prompt-response Enter.
   For test-02's "love" + Enter, that overwrote
   `lastSubmittedCommandEndCursorRow` with the sub-prompt
   input row (7) instead of the script-invocation end
   row (4). `computePromptCommandWrapRows` then returned
   `wrapRows = 7 - 3 + 1 = 5`, `capturePreambleForSubPromptResponse`
   computed `startRow = 8`, range `[8, 6]` was empty,
   `nonEmptyCount` stayed 0, no preamble-captured log
   fired, and tuple-final fell through to no-trim —
   narrating the ENTIRE script output from the start
   instead of just the response delta.
   PR-Q gates the cursor capture on `not
   awaitingSubPromptEnter` so only top-level Enter
   updates the saved cursor signal. Sub-prompt-response
   Enter leaves it alone — the previously-captured
   script-invocation cursor row stays valid for the
   wrap-rows computation.

### Post-Cycle-49 PR-P (2026-05-14): Per-entry ContentHistory dump in diagnostic bundle

Maintainer's Test D (SpeechCursor missing the `Enter your
name:` chunk) couldn't be localised from the existing
bundle: the aggregate `Stats:` line gives totals but the
per-entry view — Seq / Kind / Source / Text — was only in
the sibling `content-history-<ts>.txt` reconstruction
file, and even there the SpeechCursor manual-nav verdict
wasn't computed alongside.

PR-P adds a new `--- ENTRIES (last 200 with SpeechCursor
verdict) ---` subsection inside the existing `--- CONTENT
HISTORY (last 64KB) ---` bundle section. One line per
entry:

```
[Seq=N Source=X Kind=Y ... NavRender=Some(Len=N Activity=A Text="...")/None]
```

Control characters are escape-printed (`\n`, `\r`, `\t`,
`\x1B`, `\xNN`) so a wrapped row stays on one bundle line.
Text payloads are truncated to 80 chars for paste-
friendliness; the full reconstruction stays in the sibling
file. Capped at the last 200 entries; pre-cap entries
elide with a leading `... (M earlier entries omitted)` line.

The new dump answers "is this entry CmdOutput?", "does
SpeechCursor's `renderEntryForManualNav` filter return
None for this entry?", "where exactly does the entry
chain break for the missing chunk?" — without requiring a
separate `Ctrl+Shift+Y` SessionModel dump or a debug-level
log walk.

### Post-Cycle-49 PR-O (2026-05-14): Cursor-row-based wrap detection for history-recalled commands

Maintainer Test B dogfood 2026-05-14: first run of test-02
narrated cleanly; SECOND run (after Up-arrow recall)
narrated the wrapped command-path tail before the script
intro. Root cause: PR-N's `computePromptCommandWrapRows`
relied on `UserInputBuffer.Capture().Length` to estimate the
on-screen wrap, but `UserInputBuffer` watches the user's
outgoing keystrokes. When the user presses Up arrow, the
byte stream is `\x1B[A` — `[` and `A` are printable, so
`Capture()` returns `[A` (2 chars). Cmd's doskey paints the
full recalled command (e.g. 138 chars) onto the screen, but
PR-N saw `cmdLen=2`, computed `wrapRows=1`, and started
the sub-prompt screen-read at `promptRow + 1` — including
the wrap-continuation row that PR-N was supposed to skip.

PR-O captures the SCREEN CURSOR ROW at EnterPressed
(`lastSubmittedCommandEndCursorRow`) plus the matching
`PromptRowIndex` (`lastSubmittedCommandPromptRow`). The
cursor at EnterPressed sits at the end of the on-screen
content regardless of whether it got there via typing or
history recall. `computePromptCommandWrapRows` now prefers
the cursor-row signal: `wrapRows = cursorRow - promptRow +
1`. Falls back to PR-N's length-based estimate when the
cursor signal is unavailable (defensive — shouldn't happen
in practice).

### Post-Cycle-49 PR-N (2026-05-14): Command-wrap-aware sub-prompt screen-read

Maintainer dogfood 2026-05-14: when a command (e.g. the
`test-02-text-input.cmd` invocation, ~91 chars) is long
enough that `prompt path + typed command` exceeds
`screen.Cols`, the typed command's continuation wraps onto
the row right after the prompt row. PR-K's sub-prompt
screen-read started at `promptRow + 1` unconditionally, so
the wrapped continuation row was misclassified as script
preamble and NVDA narrated the tail of the file path
before the script's intro and prompt text.

PR-N captures `UserInputBuffer.Capture()` result's length
at EnterPressed (`lastSubmittedCommandLength`) and uses it
to compute `wrapRows = ceil((PromptText.Length +
cmdLen) / screen.Cols)`. Both PR-K sub-prompt screen-read
sites — `subPromptScreenReader` (the announce body) and
`capturePreambleForSubPromptResponse` (the tuple-final
line-count) — now start at `promptRow + wrapRows` instead
of `promptRow + 1`. The wrap-continuation row(s) skip
correctly regardless of how many visual rows the command
spans.

Diagnostic logs per the PR-J CLAUDE.md convention:
`PR-N sub-prompt screen-read range. PromptRow=N
WrapRows=N StartRow=N CursorRow=N LineCount=N` at Debug
and the existing `PR-N sub-prompt preamble captured`
Information log gains `WrapRows={WrapRows} StartRow={StartRow}`
fields.

Note: the same wrap issue can in principle affect the
`extractContent` row-walk fallback in `SessionModel.fs`,
but in practice the `extractContentFromContentHistory`
linear path is used for cmd (splits on the first `\n` in
the byte stream — invariant under visual wrap) so
tuple-final extraction is already wrap-correct. The
row-walk fallback only fires on shell-switch /
finalize-incomplete edges where extraction quality is
less critical; left as a follow-up if a real reproducer
surfaces.

### Post-Cycle-49 PR-L (2026-05-14): History-recall settle gate

Maintainer dogfood 2026-05-14 reproduced two related desyncs
under rapid Up/Down arrow tapping:

1. **Spoken text ≠ visually displayed command**: PR-I's 100 ms
   debounced timer fires 100 ms after the last keystroke, but
   under rapid tap cmd's response bytes (the line-rewrite for
   each arrow press) are still arriving from the PTY when the
   timer fires. The screen-read sees an intermediate state and
   the announce reflects an earlier state than what's now on
   screen. PR-L adds a settle gate: on tick, also check
   `lastReadUtc`; if the reader has emitted bytes within the
   last 100 ms, restart the timer instead of announcing. Only
   when keystrokes AND incoming bytes have both been quiet for
   100 ms does the announce fire.

2. **Visually displayed command ≠ what cmd executes on Enter**:
   This one is a user-perception race against ConPTY round-
   trip latency — cmd processes Up/Down bytes in order and
   atomically updates its history pointer, but the screen
   reflects each step only after the response byte makes it
   back through the reader. If Enter is pressed while a
   line-rewrite is in transit, the visible frame is stale.
   Not pty-speak's bug to fix, but PR-L's settle gate ensures
   the SPOKEN text matches what cmd will run if Enter is
   pressed at the moment the user hears the announce.

Diagnostic log added per the PR-J CLAUDE.md convention: when
the gate defers, `PR-L history-recall settle-gate: deferring
(LastReadAgoMs=N)` lands at Debug.

### Post-Cycle-49 PR-K (2026-05-14): Sub-prompt screen-read + capturePreamble endRow fix

Two coupled bug fixes from preview.125 dogfood of the
Cycle-49 sub-prompt narration:

1. **Sub-prompt announce now reads the screen, not the raw
   accumulator.** PR-F's "last non-empty line of the
   accumulator" approach failed on the FIRST run of a `.cmd`
   script because cmd emits an OSC title-set sequence
   (`\x1B]0;cmd.exe - "..."\x07`) AFTER the script's prompt
   text in the byte stream; that escape sequence ends up on
   a line of its own in the accumulator and the
   `Array.rev |> tryFind` reverse-scan picked it as the
   "last" line, narrating
   `]0;C:\WINDOWS\SYSTEM32\cmd.exe - ...` — the maintainer's
   "show CMD"-style mishearing on first-run-only sub-prompts.
   PR-K reads the screen rows `[Active.PromptRowIndex + 1,
   cursorRow]` at `SubPromptIdle` instead. `Screen` already
   absorbs OSC sequences as state changes (window-title,
   palette, etc.) and `CanonicalState.renderRow` returns
   only printable cell content. Side-effect win: the
   announce now includes the full preamble — start marker,
   intro text, prompt line — restoring the script context
   PR-F had stripped down to just the prompt.

2. **`capturePreambleForSubPromptResponse` uses
   `endRow = cursorRow - 1`** to exclude the cursor row
   from the non-empty-line count. The cursor row at
   `EnterPressed` is racy — cmd can start echoing back
   content before the recordTransition callback fires —
   so counting it produces a too-high `LineCount` that
   drops the user's actual post-Enter response from the
   tuple-final announce. Maintainer dogfood log
   2026-05-14: `LineCount=4`, dropped 4 of 5 lines,
   leaving only the `PTYSPEAK-TEST-END` marker. With the
   endRow fix the count is 3 and the `Hello, NAME!`
   response line narrates correctly.

Fallback: when `Active.PromptRowIndex` is `ValueNone` (e.g.
PowerShell — the heuristic prompt detector doesn't match PS
prompts today), the sub-prompt announce path falls back to
the PR-F accumulator-last-line approach. PowerShell
support stays as a separate backlog item.

### Post-Cycle-49 PR-J (2026-05-14): History-recall diagnostic logging + new-feature diagnostic convention

Maintainer reported a non-deterministic "show CMD" mis-announce
after a few cmd evaluations in the post-Cycle-49 release build.
The leading hypothesis is that PR-I's prompt-prefix strip
occasionally fails to match (so the full prompt-path row gets
announced, and NVDA's TTS of `C:\path\to\current>recalledCmd`
produces the phonemes the maintainer is hearing as "show CMD").
PR-J adds the diagnostic signal needed to confirm or refute the
hypothesis from a `Ctrl+Shift+D` bundle without requiring an
on-demand repro:

- Information-level log gains `Stripped={Stripped}` so an
  always-on bundle confirms whether the prefix-strip matched.
- New Debug-level "PR-I history-recall details" log captures
  `PromptRow`, `Row` (full prompt row content), `PromptText`
  (the `Composing.PromptText` the strip checked against),
  `Stripped`, and `Announce` (the text NVDA spoke). Debug
  level matches the existing `UserInputBuffer captured on
  Enter: ... Text={Text}` precedent — both go to NVDA out
  loud so no new sensitivity boundary is crossed.
- Empty-recall branch also gets the same details log so a
  silent recall is distinguishable from a missed code path.

Also adds a new CLAUDE.md project convention: **new features
ship with their diagnostic triggers in the same PR**, not as
a follow-up. The convention names the minimum signal an
announce site / state transition / screen-read should emit
so the maintainer can triage from a bundle.

### Cycle 49 PR-I (2026-05-14): History-recall draft announce

When the user presses Up / Down arrow during `Composing`, the
shell (cmd's doskey, PowerShell's PSReadLine, bash readline)
rewrites the on-screen input line to display the previous /
next command from its history. Before PR-I, pty-speak's
`UserInputBuffer` only tracked OUTGOING keystrokes — the
shell-side rewrite bypassed it silently and NVDA never heard
what was now sitting in the input line.

PR-I detects the Up / Down arrow byte sequences in the byte-
write wrapper (`\x1B [ A/B` normal mode, `\x1B O A/B`
application / DECCKM mode), starts a 100 ms debounced
`DispatcherTimer`, and on tick reads the current prompt row
from `screen.SnapshotRows`. The row content is stripped of
the `Composing.PromptText` prefix where present (so the
announce is just the recalled draft, not the path) and
narrated through a new `pty-speak.input-assistant` activity
ID — separate from `pty-speak.output` so users can mute /
per-tag-configure draft announces independently of command
output.

Rapid Up / Down keypresses (the user scrolling through
history) coalesce: the timer restart drops the prior pending
announce, so only the FINAL state narrates.

CORE-ABSTRACTION-BOUNDARY.md §"input assistant" reserved
peer pane is the conceptual home for this channel; PR-I is
the first concrete user of it.

### Cycle 49 PR-H (2026-05-14): Reshape test-01-echo for explicit line counting

`scripts/cmd-tests/test-01-echo.cmd` reshaped per maintainer
feedback 2026-05-14: pre-Cycle-49 the script printed lines 2-7
with `Line N of 8` labels but Line 1 (the implicit intro
`This is a simple echo test.`) and Line 8 (the implicit
`Last line. ...` final message) carried no numeric label,
making "did I hear Line 1 of 8?" hard to verify by review
cursor. PR-H drops to three explicitly-numbered lines
(`Line 1 of 3.` → `Line 3 of 3.`) with the intro and final
messages explicitly framed as such ("Echo test follows: three
numbered lines then a final message." / "If you heard the
intro, all three numbered lines, and this final message,
output narration is healthy.").

Closes the test-01-line-1-missing follow-up from preview.121
dogfood — root cause was script-fixture labelling, not
substrate.

Updated references: `tests/Tests.Unit/DiagnosticExtractsTests.fs`
synthetic-content test, `docs/ACCESSIBILITY-TESTING.md` 47-2 +
47-17 expectations, `docs/adr/0003-shell-interaction-state-machine.md`
test-table summary.

### Cycle 49 PR-G (2026-05-14): Remove tuple-final prefix-trim against lastAnnouncedText

Maintainer dogfood of preview.122 (post-PR-F) surfaced a
"speech is unreliable and unpredictable" regression and a
specific reproducer: running the same command twice in a row
(e.g. `echo hi` then `echo hi`) silenced the second tuple-final
announce. Root cause: the tuple-final code still carried a
prefix-trim against `lastAnnouncedText` from the pre-PR-B
sub-prompt cleanup era. Once PR-F replaced that mechanism with
line-count slicing, the prefix-trim was a relic that ONLY
suppressed duplicate-command output (because the new
`tuple.OutputText` happened to start with the previous
announce body, so the trim emptied the announce). The
"unpredictable" pattern of silences was the same logic firing
non-deterministically as session output accumulated matching
prefixes.

PR-G deletes the prefix-trim branch entirely; the only
trim path for tuple-final is now PR-F's sub-prompt line-count
slice.

(An unresolved menu-right-arrow narration silence reported in
the same dogfood is NOT addressed by PR-G — pty-speak doesn't
touch WPF menu accessibility; that issue stays open pending
re-test on the post-PR-G build with a log if it persists.)

### Cycle 49 PR-F (2026-05-14): sub-prompt last-line announce + line-count post-Enter slicing

Two connected refinements from preview.121 dogfood:

1. **Sub-prompt announce: just the last line.** Pre-PR-F, the
   sub-prompt accumulator was narrated wholesale after a
   leading-echo strip. For `test-02-text-input.cmd` the
   accumulator carried three lines —
   `=== PTYSPEAK-TEST-START: test-02-text-input ===`,
   `This test prompts for a text string and echoes it back.`,
   and `Enter your name:` — and NVDA's word-by-word read of the
   embedded `02` ("test-**zero two**-text-input") confused the
   listener. PR-F narrows the announce to the LAST non-empty
   line, where the cursor is parked waiting for input. The
   preceding preamble lines remain reachable via SpeechCursor
   manual review.
2. **Post-Enter delta via line-count slicing.** PR-B's text
   prefix-trim approach (overwrite `lastAnnouncedText` with the
   screen-rendered preamble; `tuple.OutputText.StartsWith` trim)
   diverged in dogfood — the cursor row's content at
   EnterPressed time differed from the same row's content at
   tuple-finalise 175 ms later (PTY echo arriving in between),
   so the prefix match returned false and the whole tuple
   re-narrated. PR-F switches to a line-count signal: the
   EnterPressed handler counts non-empty rendered rows in the
   active tuple's output region; tuple-finalise splits
   `tuple.OutputText` on `\n` and drops that many leading
   lines. Robust to per-row drift because it relies on the
   empty-row filter both sides apply uniformly.

Cycle 49 plan: [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md).

### Cycle 49 PR-D (2026-05-14): Prompts visible in SpeechCursor manual navigation

`Ctrl+Shift+Up/Down/End` now surface `PromptStart` markers with
payload as standalone navigable entries — regardless of the
per-shell `PromptPath` setting that gates auto-drive narration.
A new `SpeechCursor.renderEntryForManualNav` function decouples
the navigation rendering from the auto-drive rendering:
manual nav uses `FinalDirOnly`-trimmed prompt text (e.g.
"Local>") for every `PromptStart`-with-payload, while
`onAppend`'s streaming narration continues to honor the
per-shell `PromptPath` so the live cmd / PowerShell experience
doesn't become chattier.

Maintainer feedback 2026-05-14: "the speech cursor only
includes the output of echo and I think it should include the
prompt as well in the history as a separate item." Cycle 49
PR-D is the navigation-side response.

Cycle 49 plan: [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md).

### Cycle 49 PR-C (2026-05-14): Review cursor refresh via UIA TextChanged

The NVDA review cursor now reflects the latest `ContentHistory`
tail immediately, instead of needing the user to run an extra
command to nudge the UIA cache.

Mechanism: `TerminalAutomationPeer` gains a `RaiseTextChanged()`
method that fires `AutomationEvents.TextPatternOnTextChanged`
— the canonical UIA signal that the document's underlying text
has been replaced. Where the prior
`TextPatternOnTextSelectionChanged` event is silently dropped by
NVDA when `GetSelection()` is empty (which ours is, per ADR 0002
Status notes 2026-05-13), `TextChanged` invalidates NVDA's
cached `DocumentRange` and triggers a re-pull on the next
review-cursor query.

Wired from every Announce site: SpeechCursor auto-drive narration,
sub-prompt announce in `recordTransitionImpl`, tuple-finalise
announce in `boundaryAction`. Each fires a paired
`raiseTextChanged()` after the existing `Announce(...)` call.

Cycle 49 plan: [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md).

### Cycle 49 PR-B (2026-05-14): post-Enter delta announce for sub-prompts

When the user responds to a sub-prompt (`set /p`, `pause`,
`choice`, etc.) and presses Enter, the tuple-finalise announce
now narrates **only the post-Enter content** instead of replaying
the entire `OutputText` from the top of the command. The
sub-prompt's prompt text and the user's typed response remain
reachable via `Ctrl+Shift+Up/Down` SpeechCursor manual review.

Mechanism: at `EnterPressed` after a `SubPromptIdle`, the
composition root snapshots the screen and renders the active
tuple's output rows from the prompt row through the cursor row
into `lastAnnouncedText`. The existing tuple-finalise prefix-trim
(introduced in the Cycle 48 post-PR-F batch) then slices that
preamble off `tuple.OutputText` so only the bytes the script
produced **after** the user submitted their response get
announced. Test 02 (`02-input.cmd`) used to re-narrate "This is
the input test." through "Hello, John!" verbatim each time the
user hit Enter; now it speaks just the post-Enter delta.

Cycle 49 plan: [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md).

### Cycle 49 PR-A (2026-05-14): SpeechCursor manual nav collapses blanks

`Ctrl+Shift+Up/Down/End` now skip entries that produce no audible
announcement (Newline, Overwrite, empty TextSpan, boundary markers
without payload, `UserInputEcho`-sourced entries). Manual review
of a `dir`-shaped output (8 lines = 16 entries with interleaved
Newlines) now requires 8 Up presses instead of 16; half the prior
presses produced no narration. `toMarker` deliberately retains
the unfiltered jump — a marker jump is the user explicitly
asking for a marker even if its `renderEntry` returns `None`.

Cycle 49 plan: [`docs/CYCLE-49-PLAN.md`](docs/CYCLE-49-PLAN.md).

### Cycle 48 post-PR-F (2026-05-13): SpeechCursor hotkeys + menu reorg + sub-prompt echo strip

Three connected fixes from preview.118 dogfood:

1. **`Ctrl+Shift+Up/Down/End` bound to SpeechCursor**
   Previous / Next / JumpToLatest. Previously menu-only; the
   HotkeyRegistry has had a comment anticipating this since
   Cycle 45. The mirror row in `TerminalView.AppReservedHotkeys`
   ships alongside per the F#/C# parity tests.
2. **Menu reorg.** Top-level "Display" renamed to
   "Interface". The old top-level "Data" menu broken up:
   "Open Data Folder" and "Edit Config" lifted to Interface
   direct level (they're infrastructure paths, not output
   review); the remaining items grouped under "Output
   History" submenu (Last Output → text editor / re-announce,
   Copy Session History, Announce Session Log Path).
   Diagnostics's flat ~10-item list grouped into three
   submenus: **Probe** (Health Check, Incident Marker, Run
   Diagnostic Battery), **Test Scripts** (Open Manual Tests,
   Process Cleanup Test, CMD Interaction Tests), **Inspect**
   (Copy Latest Bundle, Grep Diagnostics, Extract). Logging
   Level stays at the top of Diagnostics as a frequent
   toggle. All mnemonics audited for uniqueness within each
   submenu.
3. **Sub-prompt announce strips command echo.** Maintainer
   reported test 02 (`set/p`) producing garbled announce of
   `"set /p name=Enter your name:Enter your name:"` after
   pressing Enter — the SubPromptIdle accumulator included
   cmd's echo of the typed command line. `recordTransition`
   now drops everything up to and including the first `\n`
   in the accumulator before announcing, leaving just the
   sub-prompt text (e.g., `"Enter your name:"`).

Known issues not addressed in this PR (deferred to follow-up):
- The review-cursor "needs an extra command to update"
  behaviour after running a single command — UIA polling /
  caching issue, requires NVDA-side reasoning.
- Test 01 "Line 1 of 8" missing from announce (matrix-row
  validation needed first to confirm whether bug or expected).
- SpeechCursor menu/hotkey may still land on a sequence of
  Composing-state entries that all return None — needs
  diagnostic to confirm.

### Cycle 48 closure (2026-05-13): six PRs (#306–this) shipped

Six-PR sequence implementing ADR 0003's `ShellInteraction`
semantic state machine on top of the byte-level substrate.
ADR status flipped to **Accepted / Implemented** with a
per-PR deviation log in its Status notes block.

- **#306 PR-A** — ADR drafted + open questions §9.1 → §9.6
  resolved.
- **#307 PR-B** — `ShellInteraction.State` + types + pure
  `tryTransition` + sub-prompt detector, wired observe-only.
- **#308 PR-C** — `ContentHistory.Entry.Source : EntrySource`
  substrate-schema change. Per-source counts in the
  diagnostic bundle's `Stats:` line.
- **#309 PR-D** — `UserInputBuffer` byte-stream wiring via
  `writePtyBytes` wrapper. `EnterPressed` transition
  carries the captured command text (replacing PR-B's empty
  placeholder).
- **#310 PR-E** — sub-prompt announce via state machine +
  SpeechCursor filters `UserInputEcho` entries (both
  AutoDrive AND Manual) + idle-flush announce body retired.
  Per-character chatter from `idle-flush` is gone.
- **This PR-F** — docs sweep (SESSION-HANDOFF, CLAUDE.md
  sequencing, project plan change log, ADR status notes).

Cycle 47 dead code (`tupleFinaliseAnnounce` prefix-trim
machinery from #301; PR #300 UIA-materialiser typing-window
gate) preserved as defence-in-depth pending preview.118
dogfood. A follow-up PR can delete after the audible
behaviour holds.

Validation: NVDA matrix rows Cycle 48-B1 → 48-E8 walk the
CMD test corpus against the new audible behaviour. The
listening criteria for the headline issues from the
preview.117 dogfood:

- **Per-char chatter gone** — typing `echo hi` slowly
  produces zero per-char pty-speak announces (matrix 48-E1).
- **Tuple-final still works** — `hi` announces after Enter
  via the unchanged SessionModel path (matrix 48-E2).
- **set/p announces correctly** — sub-prompt
  `Enter your name:` reads aloud after the ~350 ms idle
  (matrix 48-E3).
- **`pause` announces correctly** — same path, with
  SinglekeySubmit flagged (matrix 48-E4).
- **SpeechCursor skips echo** — manual `Ctrl+Shift+Up/Down`
  navigation lands only on output + markers, never on
  echoed-input bytes (matrix 48-E5 + 48-E6).

### Cycle 48 PR-E (2026-05-13): announce routing — sub-prompt via state machine; SpeechCursor filters echo

**Narrow scope** to keep the audible-UX risk small:

- **Sub-prompt announce moves to the state machine.** When
  `ShellInteraction` fires `SubPromptIdle` (transition [c]),
  `recordTransition` reads `outcome.AccumulatedOutput`,
  sanitises + trims + caps to `OutputAnnounceCapChars`, and
  speaks it. Earcon `ReadyForInput` plays via
  `OutputDispatcher`. This replaces the per-character
  idle-flush chatter the maintainer reported in preview.117 —
  the idle-flush announce body is now silent; only the
  sub-prompt-idle transition fires speech for the
  set/p / pause / choice case.
- **Regular tuple-final announce unchanged.** The
  `PromptDetected` transition (transition [b]) does NOT drive
  the announce — `SessionModel.applyAndCapture` →
  `tuple.OutputText` → boundaryAction's existing
  Cycle-22b-onwards path keeps that responsibility. Reason:
  the SessionModel extracts clean OutputText from screen rows
  (no command echo, no next-prompt path leakage); the
  state-machine accumulator includes those messy bytes and
  wouldn't be a drop-in replacement.
- **SpeechCursor filters `UserInputEcho` entries** in both
  AutoDrive AND Manual navigation per ADR §9.6 resolution.
  `renderEntryWithPolicy` returns `None` whenever
  `entry.Source = UserInputEcho`. The user never hears their
  own typed echo via SpeechCursor even when navigating
  manually.
- **Idle-flush announce body retired.** The
  `ContentHistory.tick` call stays (seals stale active spans
  for the diagnostic-bundle tail) and the watermark advances,
  but no Announce fires from idle-flush anymore. The legacy
  PR #305 typing-window gate is also gone (made moot by the
  body removal).

Deferred to PR-F (closure audit) or a follow-up:
- Removing the now-unused `tupleFinaliseAnnounce` prefix-trim
  machinery (left in source as a defence-in-depth path; if
  PR-E proves robust under dogfood, PR-F deletes it).
- UIA materialiser substituting `UserInputBuffer` for the
  active TextSpan during Composing (the screen ↔ review-
  cursor parity refinement).
- Removing the PR #300 materialiser typing-window gate.

Matrix rows Cycle 48-E1 → 48-E8 added.

### Cycle 48 PR-D (2026-05-13): `UserInputBuffer` byte-stream wiring

Implements ADR 0003 §5.1's `UserInputBuffer` as a self-locked
class on `ShellInteraction.State.UserInputBuffer`. Updated at
byte-write time in the `writePtyBytes` wrapper:

- printable ASCII (0x20–0x7E) → `AppendChar`
- BS (0x08) → `Backspace`
- CR (0x0D) → `Capture` + clear; the captured text becomes the
  `EnterPressed` transition's `submittedCommand` (replacing
  PR-B's empty placeholder)

Multi-byte sequences (arrow keys, Tab, Unicode chars outside
ASCII) are not tracked in PR-D's MVP — refining to true key-
level tracking is deferred (would require routing the `KeyCode`
through to the buffer alongside the encoded bytes; see ADR
§5.5). Plain typed input (the dominant case) is captured
correctly; the `EnterPressed` transition log will show the
exact submitted command for the maintainer to verify.

`State.Reset()` extends to also reset the buffer on shell
hot-switch.

11 new unit tests in `ShellInteractionTests` cover buffer
operations: append, backspace at start (no-op), backspace mid-
buffer, MoveCursor + insert at non-end, Delete, MoveCursor
clamping, JumpTo Home/End, Capture clears, Reset clears, State-
level instance shared, State.Reset clears buffer.

PR-E switches announce routing onto the state-machine
transitions and adds the SpeechCursor filter for `UserInputEcho`
entries. PR-F is the closure audit.

Matrix rows Cycle 48-D1 → 48-D3 added.

### Cycle 48 PR-C (2026-05-13): `ContentHistory.Entry.Source : EntrySource`

Implements ADR 0003 §9.5 — every `ContentHistory.Entry` now
carries a `Source : EntrySource` tag. `EntrySource` is a DU
with six values: `UserInputEcho`, `CmdOutput`, `CmdSubPrompt`,
`ShellPrompt`, `BoundaryMarker`, `Unknown`. (`BoundaryMarker`
rather than `Marker` because the latter would shadow
`ContentHistory.Entry.Marker` at qualified-access under the
`[<RequireQualifiedAccess>]` attribute.)

The tag is set at append time. `ContentHistory.setSourceResolver`
takes a `unit -> EntrySource` delegate; the composition root
wires it to `ShellInteraction.entrySourceFor shellInteraction.Current`,
mapping `Composing` → `UserInputEcho` and `Executing` →
`CmdOutput`. Marker entries always get
`EntrySource.BoundaryMarker` regardless of the resolver. Pre-
state-machine entries (or exceptions in the resolver) get
`Unknown`.

Also moves the `EntrySource` DU itself out of
`ShellInteraction.fs` (where PR-B drafted it) to
`ContentHistory.fs` so the substrate can carry the field
directly. ShellInteraction re-exports a type alias for
backwards-compat with any future caller still reaching via
`ShellInteraction.EntrySource`.

The diagnostic bundle's `Stats:` line in
`--- CONTENT HISTORY ---` extends with per-source counts
(`| Sources: UserInputEcho=N CmdOutput=N CmdSubPrompt=N
ShellPrompt=N Marker=N Unknown=N`) so paste-back triage can
answer "did the substrate classify each byte correctly?"
without scrolling the 64 KB tail.

PR-D adds the keyboard-handler-driven `UserInputBuffer`. PR-E
switches announce routing onto the state-machine transitions
and adds the SpeechCursor filter for `UserInputEcho` entries.
PR-F is the closure audit.

### Cycle 48 PR-B (2026-05-13): ShellInteraction state machine — observe-only

Implements `Terminal.Core.ShellInteraction` per ADR 0003: a
two-state machine (`Composing` / `Executing`) classifying each
transition in the shell session. New file
`src/Terminal.Core/ShellInteraction.fs` with the pure types,
the `tryTransition` function, the `State` container, and the
sub-prompt detector. New test file with 24 tests covering the
transition table, sub-prompt detector, and SinglekeySubmit
pattern matcher.

Wired into the composition root in **observe-only** mode: the
state machine receives the same signals as today's announce
paths (Enter via the `writePtyBytes` wrapper detecting `\r`
bytes, PromptStart via the boundary handler, sub-prompt-idle
via the existing `idleFlushTimer` tick, byte-arrival via a
new `onByteFromPty` callback in `startReaderLoop`) and logs
each transition at Information level. Today's tuple-final +
idle-flush + UIA-materialiser gates remain in place driving
audible behaviour — PR-B does NOT change announce routing.

PR-C will add `Source : EntrySource` to `ContentHistory.Entry`.
PR-D adds the `UserInputBuffer`. PR-E switches announce
routing onto the state-machine transitions. PR-F is the
cycle closure audit.

Validation: NVDA matrix Cycle 48-B1 → 48-B8 walk the CMD test
corpus with Debug logging on; the transition log should match
the ADR §4 mapping for each test. Audible behaviour unchanged.

### Cycle 47 follow-up (2026-05-13, post-preview.116): cursor-row synthetic newline + idle-flush typing gate + per-char earcon revert

preview.116 dogfood surfaced three connected regressions:

1. **Per-character `ReadyForInput` earcon**: each typed character
   tripped idle-flush, which dispatched a `ReadyForInput` event
   (PR #302) and beeped. The earcon was right semantically for
   the `set/p` cue case but wrong for the per-char echo case the
   gate didn't cover.
2. **Per-character NVDA announce**: idle-flush sliced single-byte
   echo chunks (`"e"`, `"ec"`, `"ech"`, ...) and Announced each
   one — independent of NVDA's keyboard-hook char-speech. The
   UIA typing-window gate from PR #300 only suppressed
   `ITextProvider` polling; idle-flush is a separate announce
   path that didn't share the gate.
3. **Banner + output runs into next prompt**: rendered tail
   showed `"(c) Microsoft Corporation. All rights reserved.C:\Users\..."`
   and `"hiC:\Users\..."` with no newlines. cmd's conpty
   translator uses CSI cursor-positioning sequences instead of
   CRLF for some visual-row transitions; ContentHistory's
   `appendFromEvent` ignored `CsiDispatch` events, so the active
   span accumulated straight across rows.

Fixes (one PR):

- **`ContentHistory.appendFromEvent` tracks `LogicalCursorRow`**
  across `CsiDispatch` `H`/`f`/`A`/`B`/`E`/`F` and
  `EscDispatch` `D`/`E`/`M`. When the computed row changes AND
  it isn't an LF (LF has its own seal path) AND the active span
  is non-empty, seal the span and emit a synthetic `Newline`.
  Renders cmd's CSI-positioned banner rows as separate lines.
- **`TerminalView.LastKeystrokeAtUtc`** exposes the existing
  `_lastKeystrokeAtUtc` field publicly. Idle-flush timer reads
  it; if a keystroke is within the same 350 ms window the UIA
  materialiser uses (PR #300), the entire idle-flush body
  early-returns — no Announce, no earcon, no watermark
  advance. The next tick re-evaluates.
- **`ReadyForInput` earcon dispatch removed from idle-flush**
  (the block PR #302 added). The earcon is scoped to the
  tuple-final boundary only. Mid-evaluation `set/p` / `pause`
  cases lose the cue; a distinct-from-tuple-final earcon
  scoped to "cmd emitted a prompt-like line but no shell-
  prompt fired" is deferred to a future revision.

Matrix rows Cycle 47-24 (mid-eval input earcon) is removed.
New rows Cycle 47-26 (per-char silence under typing window) +
47-27 (CSI-row-change synthetic newlines) cover the new
behaviour.

### Cycle 47 follow-up batch (2026-05-13, post-preview.114): closure

Five PRs (#299–#303) shipped this session in response to the
maintainer's preview.114 `echo hi` / `set /p` dogfood walk:

- **#299** — diagnostic bundle preserves newlines via
  `sanitiseForBundle`; marker labels relabelled to the parallel
  `begin/end prompt/output` form; `CommandFinished` synthesised
  on the cmd-shell `PromptStart-while-AwaitingCommandStart`
  transition.
- **#300** — typing-window UIA suppression: the UIA Text-pattern
  view excludes the active TextSpan for 350 ms after each
  non-modifier keypress so NVDA's `ITextProvider` polling
  doesn't surface mid-keystroke deltas as inserted text.
  Announce content logged at Debug (`MsgHead=`).
  `ACCESSIBILITY-TESTING.md` gained an NVDA settings
  recommendation block.
- **#301** — tuple-final announce trims any prefix matching
  `lastAnnouncedText` so `set/p` doesn't replay the idle-flush
  prompt at tuple-finalise time.
- **#302** — `ReadyForInput` earcon dispatched on idle-flush so
  `set/p` / `pause` produce the same "you can type now" click
  the tuple-final path uses.
- **#303** — top-level menu mnemonic conflicts fixed:
  `_Display` vs `_Data` (renamed to `D_ata`); Diagnostics
  `Test Process _Cleanup` vs `_CMD Interaction Tests` (renamed
  to `C_MD Interaction Tests`).

Matrix rows Cycle 47-18 through 47-25 cover the batch.

### Cycle 47 follow-up (2026-05-13, post-preview.114): menu mnemonic conflict fixes

Maintainer reported menu access keys ambiguous in the preview.114
build. Two collisions found + fixed:

- **Top-level `_Display` vs `_Data`**: both used `D`. Renamed
  `_Data` → `D_ata` (mnemonic on `a`). Alt + D → Display;
  Alt + A → Data.
- **Diagnostics `Test Process _Cleanup` vs `_CMD Interaction Tests`**:
  both used `C`. Renamed the parent submenu mnemonic from `_C`
  to `_M` (`C_MD Interaction Tests`). Reachable via Alt → G → M.

The Cycle 47 post-preview.113 comment above the CMD Interaction
Tests block (which documented the prior `_C` choice) updated to
reflect the new `_M` parent. No code-side handlers changed —
the access-key letters are purely XAML.

### Cycle 47 follow-up (2026-05-13, post-preview.114): mid-evaluation input earcon on idle-flush

**Symptom**: cmd's `set /p var=Enter your text:`, `pause`, and
similar interactive prompts print a single line and then block
waiting for input. No PromptStart boundary fires (the heuristic
detector matches shell-style prompt rows, not arbitrary
mid-command questions), so the tuple-final
`SemanticCategory.ReadyForInput` earcon never plays. The user
hears the announce text (via idle-flush) but gets no audible
"you can type now" cue to distinguish "shell is paused asking
me something" from "command is still running".

**Fix**: when idle-flush fires an announce (cmd printed something
+ paused for ≥ 350 ms), dispatch a `ReadyForInput` OutputEvent
alongside the announce. The existing EarconProfile mapping
plays the same 3000Hz × 15ms click the tuple-final path uses —
semantically equivalent ("you can type now") + no new palette
entry needed. Plays on a separate WASAPI stream so it doesn't
interrupt NVDA's narration of the just-announced prompt text.
Honours View → Earcons → Muted.

Matrix row Cycle 47-24 covers the `set/p` audible cue.

### Cycle 47 follow-up (2026-05-13, post-preview.114): tuple-final prefix-trim against last-announced text

**Symptom (test-02 dogfood)**: with the test corpus's `set /p var=Enter your text:` step, the maintainer heard:

1. Idle-flush announces `"Enter your text:"` (cmd printed the
   set/p prompt then paused waiting for input).
2. Maintainer types `"foo"` + Enter.
3. Tuple-finalise announces `"Enter your text: foo"` — the
   already-spoken prefix gets read aloud again, then the
   echoed input the maintainer just typed gets re-spoken.

**Root cause**: idle-flush updated a substrate-seq watermark
(`lastAnnouncedSeq`) to track "what's been said". But
`tuple.OutputText` is curated from screen rows by SessionModel,
not from ContentHistory seqs — there's no seq-level mapping back
to "where in OutputText did we already speak up to?" An earlier
attempt to seq-slice the tuple-final text picked up the next
prompt's path bytes (Cycle 47-post-preview.113 revert), so the
last-shipped state announces the full `OutputText` every time.

**Fix**: add a companion text watermark
(`lastAnnouncedText`) updated alongside `lastAnnouncedSeq` on
every Announce. At tuple-finalise time, if
`tuple.OutputText.StartsWith(lastAnnouncedText)` exactly, trim
the prefix and announce only the suffix; if the suffix is
whitespace-only, suppress the announce entirely. Reset to `""`
on shell hot-switch alongside the seq reset.

Exact `StartsWith` (rather than longest-common-prefix or
whitespace-normalised matching) keeps the trim conservative —
fires only when the overlap is unambiguous. False-positive cost:
an idle-flush whose text happens to be a prefix of the tuple's
output loses its second narration (acceptable, already heard).
False-negative cost: cmd reformats the line slightly (extra
space, CR/LF normalisation difference) and the trim doesn't
fire — user hears the prefix twice, no worse than pre-fix.

Matrix row Cycle 47-23 covers the test-02 dogfood replay
suppression.

### Cycle 47 follow-up (2026-05-13, post-preview.114): typing-window UIA suppression + Announce content logging

**Symptom**: maintainer reported NVDA reading aloud each accreting
prefix as letters are typed at the cmd prompt
(`"e"`, `"ec"`, `"ech"`, `"echo"`, ...), distinct from NVDA's
keyboard-hook `Speak typed characters` behaviour and therefore not
silenceable via the user's NVDA settings.

**Root cause**: NVDA's `ITextProvider` polling re-reads
`DocumentRange` periodically on a focused UIA Edit. As cmd echoes
each typed character back into ContentHistory, the active
(unsealed) TextSpan grows char-by-char; the materialised tail
changes between polls; NVDA detects the diff and announces it as
inserted text.

**Fix**: `ContentHistoryMaterialiser.materialise` takes a
`lastKeystrokeAtUtc` argument. While a keystroke is within the
350 ms typing window, the materialiser calls a new
`ContentHistory.tailTextWithMarkersSealedOnly` variant that
excludes the active span — NVDA polling sees a stable tail across
successive captures. After the window elapses, the active span
re-enters the view so the review cursor can navigate to it.

`TerminalView.OnPreviewKeyDown` stamps `_lastKeystrokeAtUtc` on
every non-modifier key. `ContentHistoryTextProvider` exposes the
stamp via a `Func<DateTime>` delegate so the materialiser reads
it lazily on each UIA call.

**Observability**: `TerminalView.Announce`'s existing INFO log
entry (`RaiseNotificationEvent firing. ActivityId=... MsgLen=...`)
now has a paired Debug entry carrying the first 60 chars of the
announce text (`MsgHead="hi"`). Grep the diagnostic-bundle's
FileLogger section for `MsgHead=` to audit "what did NVDA hear?"
without live audio capture.

**NVDA settings recommendation** added to
`docs/ACCESSIBILITY-TESTING.md` § Test environment:
`Speak typed characters` is user preference;
`Speak typed words` should be `Off`;
`Speech interruption for typed character` should be `On`.

Matrix rows Cycle 47-21 (typing-window suppression) + 47-22
(announce-trail audit) added.

### Cycle 47 follow-up (2026-05-13, post-preview.114): preserve newlines in diagnostic bundle + relabel markers + synthesise end-output

Three connected fixes from the maintainer's preview.114 review walk
of a simple `echo hi` session.

#### 1. Diagnostic bundle preserves the newline structure

**Symptom**: the `Ctrl+Shift+D` snapshot's
`--- CONTENT HISTORY (last 64KB) ---` section pasted back as one
long squashed blob:
`echo hihiC:\Users\Kyle\AppData\Local\pty-speak\current>echo PtySpeakDiagPlain...`
with no row structure between the command echo, the output, and
the next prompt's path.

**Root cause**: three sites piped `ContentHistory.tailText` through
`AnnounceSanitiser.sanitise` (`Diagnostics.fs:1619`,
`Program.fs:3021` lightweight bundle, `Program.fs:3289`
test-bracketed extractor). `sanitise` is the one-line NVDA-announce
chokepoint — it strips every C0 control byte (`< 0x20`) INCLUDING
`\n` (`0x0A`), `\r` (`0x0D`), and `\t` (`0x09`). Correct for
announce; wrong for bundle-paste triage where row structure is the
point.

**Fix**: new `AnnounceSanitiser.sanitiseForBundle` variant that
preserves the three "useful whitespace" controls but still strips
BEL / ESC / DEL / C1. All three bundle paths switched to it;
announce paths continue to use the strip-all `sanitise`.

#### 2. Marker labels relabelled to a parallel begin/end form

**User request**: replace the single `--- prompt ---` marker with
four parallel boundary labels so the review cursor can answer
"where am I in the prompt / command / output structure" by line.

**Fix**: `ContentHistory.renderMarkerLine` relabels:
- `PromptStart` → `--- begin prompt ---`
- `CommandStart` → `--- end prompt ---`
- `OutputStart` → `--- begin output ---`
- `CommandFinished` → `--- end output ---`

Other markers (`BellRang`, `SelectionShown`, etc.) unchanged.

#### 3. Synthesise `CommandFinished` at the SessionModel
"PromptStart while AwaitingCommandStart" transition

**User request**: a detectable boundary between commands so the
review cursor sees `--- end output ---` before the next prompt's
content.

**Root cause**: cmd doesn't emit OSC 133, so
`HeuristicPromptDetector` only fires `PromptStart`. No
`CommandFinished` boundary signal naturally exists for cmd —
SessionModel synthesises a "finalising prior as incomplete" tuple
but ContentHistory had no matching marker.

**Fix**: in `Program.fs`'s boundary handler, before appending the
incoming `PromptStart` marker to ContentHistory, append a
`CommandFinished` marker first iff SessionModel finalised a prior
tuple (`finalisedOpt.IsSome`) AND the incoming kind is
`PromptStart`. The marker lands AFTER the prior tuple's output
TextSpan + Newline entries that already sealed via cmd's CRLF
output, giving the review cursor walk:

```
...prior command's output bytes...
--- end output ---
next prompt's path bytes
--- begin prompt ---
...
```

Note: the marker placement remains heuristic — `appendMarker`
seals the active span at insertion time, which may already contain
the new prompt's path bytes (cmd writes them before the heuristic
detector fires). The marker therefore appears AFTER the new
prompt's path in the rendered tail, not before it. A cleaner
ordering would require either OSC 133 emissions (which cmd doesn't
provide) or backtrack-insert semantics in ContentHistory
(deferred).

#### Verification

Matrix rows in `docs/ACCESSIBILITY-TESTING.md`:
- **Cycle 47-18** Diagnostic bundle preserves newlines for cmd `echo hi`.
- **Cycle 47-19** Marker labels read in the begin/end form.
- **Cycle 47-20** `--- end output ---` synthesised between commands.

### Cycle 47 follow-up (2026-05-13, post-preview.113): tuple-final revert + `tick`-on-idle + caret-event drop + test-bracketed extractors

Four fixes triggered by the maintainer's preview.113 walk of a
simple `echo hi` session through cmd. The Cycle 47-and-prior
post-audit churn left three correctness regressions and one
new feature requested in the same triage round.

#### 1. Tuple-final: announce `tuple.OutputText`, not a `ContentHistory` slice

**Symptom**: at command-end, NVDA spoke the input line echo +
the actual output + the path bytes of the next prompt — three
distinct regions joined into one utterance. For an `echo hi`
that should narrate "hi", the maintainer heard
"echo hi[newline]hi[newline]C:\\Users\\dev>".

**Root cause**: the post-PR-#296 audit re-wired the boundary
handler's `Announce` payload from `tuple.OutputText` (the
curated output region SessionModel's tuple-extractor computes)
to a `ContentHistory.sliceText` between two `latestSeq`
watermarks. The slice spans the entire tail between the
watermark and the current latest — including the input echo
emitted before `OutputStart` fired and the next prompt's path
emitted after `OutputEnd` sealed. The narrative size matched
the log line: `SliceFrom=3 SliceTo=7 Length=56` vs
`CmdLen=7 OutLen=2` — 54 bytes more than the curated output.

**Fix**: `src/Terminal.App/Program.fs` boundary handler tuple-
finalise match block reverts to `tupleFinaliseAnnounce`'s
`Some text` payload (which IS `tuple.OutputText`), capped at
`OutputAnnounceCapChars` (800), with `lastAnnouncedSeq`
advanced to the current `ContentHistory.latestSeq` so the
next idle-flush doesn't re-announce the same span.

#### 2. Idle-flush: call `ContentHistory.tick` first

**Symptom** (`test-02-text-input.cmd`): the script read the
"Welcome to ..." block, fell silent through the `set /p`
prompt, and only spoke "Enter your name:" AFTER the user
typed input and pressed Enter — at which point NVDA
narrated the prompt, the typed value, and the rest of the
evaluation all jumbled together.

**Root cause**: `ContentHistory.latestSeq` returns
`NextSeq - 1`, which only advances when an `Entry` seals.
`set /p` emits "Enter your name: " WITHOUT a trailing
newline, so the active span never seals; idle-flush's
`latest > lastAnnouncedSeq` returned false and the timer
went quiet.

**Fix**: call `ContentHistory.tick contentHistory
DateTime.UtcNow |> ignore` at the top of the idle-flush
tick body. `tick` seals stale active spans whose last
append is older than the substrate's
`IdleSpanSealMs` threshold, which then bumps `latestSeq`
so the slice path picks up the unfinished `set /p`
prompt. The 350 ms idle threshold and the staleness
window are compatible — once idle-flush fires
(≥ 350 ms quiet) the active span is by definition stale.

#### 3. Drop `raiseCaretMovedToTail` from auto-narrate paths

**Symptom**: during `echo hi` auto-narrate, NVDA spoke
"--- prompt ---" mid-utterance. The marker was supposed to
appear only in the review-cursor document, not the audible
narration.

**Root cause**: the boundary handler's `raiseCaretMovedToTail()`
call (and the matching one inside `speechCursorAnnounce`)
fires `AutomationEvents.TextPatternOnTextSelectionChanged` on
the `TerminalAutomationPeer`. NVDA reads the
DocumentRange in response, and DocumentRange is materialised
via `tailTextWithMarkers` (the Cycle 47-13 fix gave the
review cursor labelled boundary lines). The `Announce` text
itself is correct (no markers), but NVDA layers the
DocumentRange read on top.

**Fix**: drop the `raiseCaretMovedToTail ()` call from
(a) the boundary-handler tuple-finalise path, (b) the
idle-flush tick, and (c) `speechCursorAnnounce`. The helper
definition stays in place (defensive infrastructure for a
future selection-aware ITextProvider). The audible path is
now `Announce` alone; marker labels remain in the
DocumentRange for explicit Speech Cursor navigation when
the user wants to inspect boundaries.

#### 4. Test-bracketed diagnostic extractors

**New feature**: eight menu items under Diagnostics →
Extract → _Test Run. One per CMD interaction test in
`scripts/cmd-tests/`. Each scans the ContentHistory tail
(256 KB cap) for `=== PTYSPEAK-TEST-START: <id> === ...
=== PTYSPEAK-TEST-END: <id> ===` bracket pairs and writes
the bracketed body (all instances, divider-separated when
multiple) to clipboard + an extract file under
`%LOCALAPPDATA%\PtySpeak\extracts\`.

Pairs naturally with Diagnostics → _CMD Interaction Tests:
fire the matching `CmdTest*` item to insert the script
invocation into the PTY input cursor, review + press Enter
to run, then fire the matching `ExtractTest*` item to copy
the bracketed slice for paste-back to triage chat — no
surrounding session noise.

Pure helper `DiagnosticExtracts.extractByTest testId
content` is the substrate; seven new xUnit cases cover the
happy path, multi-run dividers, missing END markers
("still in flight" tail case), other-test-id rejection,
empty / no-matches placeholder, and CRLF tolerance.

#### Verification

Matrix rows `Cycle 47-14` through `Cycle 47-17` in
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
cover the new behaviours.

### Cycle 47 follow-up (2026-05-13): semantic-boundary markers in the materialised tail

Issue 4 from the maintainer's post-Cycle-47 walk: the NVDA
review cursor on `TerminalView` had no clear command-to-command
segmentation — navigation jumped between output blocks without
landing on the prompts / input lines between them. The
maintainer asked for "lines of text in that document that
aren't drawn on the screen visually" demarcating canonical
input / output boundaries.

**Fix**: new public `ContentHistory.tailTextWithMarkers`
function (parallel to `tailText`) that renders `Marker`
entries as labelled boundary lines instead of skipping them.
`ContentHistoryMaterialiser.materialise` (UIA Text-pattern
view) calls the markers-aware variant; the diagnostic-bundle
path still uses plain `tailText` so paste-back triage stays
readable.

Marker labels:
- `PromptStart` → `--- prompt ---`
- `CommandStart` → `--- input begins ---`
- `OutputStart` → `--- output begins ---`
- `CommandFinished` → `--- output ends ---`
- `BellRang` → `--- bell ---`
- `SelectionShown` → `--- selection prompt ---`
- `AltScreenEnter` → `--- entered alt-screen ---`

Each marker is bracketed by `\n` so NVDA's `Move(Line, ±1)`
lands on it as a standalone line. The screen rendering path
(`Screen.SnapshotRows` → WPF `OnRender`) doesn't touch
`ContentHistory.tailText*` so the boundary lines are
invisible visually — they exist only in the UIA Text-pattern
view that NVDA's review cursor walks.

For cmd (no OSC 133): `PromptStart` is the only marker
`HeuristicPromptDetector` emits, so the review cursor will
land on `--- prompt ---` between each command. For shells
with OSC 133 (or when CommandStart / OutputStart / etc. are
emitted explicitly), all four "input / output" boundaries
appear.

Tests:
- `ContentHistoryTests`: new section covering `tailText` (no
  markers, regression pin), `tailTextWithMarkers`
  (PromptStart / every MarkerKind / content interleave order
  / agreement-when-no-markers).
- `ContentHistoryTextRangeTests`: new test asserting
  `ContentHistoryMaterialiser.materialise` includes the
  marker labels in its output (the channel-side analogue of
  the substrate-side test).

Matrix row Cycle 47-13 in
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
covers the new behaviour: walk a test script, position the
NVDA review cursor at the start of the document, press
Down-Arrow line-by-line, verify that `--- prompt ---` (and
any other markers the active shell emits) read as
standalone lines.

### Cycle 47 follow-up (2026-05-13): idle-flush + menu fixes + Esc-clear

Three connected fixes the maintainer surfaced after walking the
Cycle 47 corpus on the post-Cycle-46-audit preview build:

#### 1. Idle-flush for intra-script prompts

**Symptom**: `test-02-text-input.cmd` (and any `set /p` /
`pause` / `choice` script) didn't speak the prompt until
after the user had typed input and pressed Enter. The user
had to guess what cmd was asking for.

**Root cause**: under `TupleFinalOnly` streaming, the
boundary-handler `Announce` fires only at `PromptStart`
(shell-prompt boundary). An intra-script `set /p` prompt
doesn't emit `PromptStart` — cmd just stops writing bytes
and waits for keystroke input. The auto-narrate had no
trigger; the prompt sat silently in `ContentHistory` until
the script completed and a fresh shell prompt appeared.

**Fix**: new `ShellPolicy.IdleFlushMs : int option` field +
WPF `DispatcherTimer` ticking every 100 ms. When the parser
has been idle for ≥ N ms AND `ContentHistory.latestSeq` is
past the new `lastAnnouncedSeq` watermark, slice the gap,
cap at `OutputAnnounceCapChars` (800), fire
`Announce(text, ActivityIds.output)`, advance the watermark.

The tuple-finalise path now also slices from the watermark
rather than announcing `tuple.OutputText` whole, so
idle-flush + tuple-finalise share one source of truth and
don't double-narrate. If idle-flushes covered the whole
script, tuple-finalise's slice is empty and is silent — the
ready-prompt earcon click is the only "command complete"
cue (intentional; user already heard the content).

`ShellPolicy.IdleFlushMs` defaults:
- `cmd`, `powershell`: `Some 350` (responsive during a
  `set /p` pause; tolerant of `dir`-style line emission
  rates).
- `claude`: `None` (Claude's per-token streaming hits the
  threshold too rarely AND would overlap a future
  `LineByLine` policy; idle-flush stays opt-in for Claude
  pending the streaming-experience cycle).

Watermark resets to `-1L` on `ContentHistory.reset` (shell
hot-switch) so post-switch narration starts from a clean
state.

#### 2. CMD Interaction Tests menu: `C` accelerator + numbering fix

- Parent menu header `"CMD Interaction T_ests"` →
  `"_CMD Interaction Tests"`. Reachable via `Alt → G → C`
  from the menu bar.
- Sub-item headers dropped the leading `_N:` digit prefix
  that caused NVDA to read "1, item 1 of 8" as "11 of 8"
  (the mnemonic-digit + position-N collision). Each sub-
  item now has a unique letter mnemonic:
  `_Simple Echo` / `_Text Input` / `_Numeric Input + Calculation`
  / `_Yes / No Choice` / `_Multi-Option Choice` /
  `_Pause / Continue` / `Pro_gress Loop` / `Stderr _Output`.

#### 3. `runCmdTest` Esc-prefix to clear the input buffer

`runCmdTest` now prepends `0x1B` (Esc) to the script
invocation bytes. cmd.exe's cooked-mode line editor treats
Esc as "clear the current input buffer", so anything the
user had partially typed before clicking the menu item
gets wiped before our quoted script invocation lands.
Avoids the "we appended to whatever was already there and
broke the parse" failure mode.

Files: `src/Terminal.Core/ShellPolicy.fs` (new field +
defaults), `src/Terminal.App/Program.fs` (compose-level
`lastAnnouncedSeq` + `DispatcherTimer` + boundary-handler
rewrite + shell-switch reset + `runCmdTest` Esc prefix),
`src/Views/MainWindow.xaml` (menu headers),
`tests/Tests.Unit/ShellPolicyTests.fs` (idle-flush
defaults assertion), `CHANGELOG.md`,
`docs/ACCESSIBILITY-TESTING.md` (Cycle 47 matrix rows).

### Cycle 47 (2026-05-13): CMD interaction test corpus

Adds eight tiny `.cmd` scripts under
[`scripts/cmd-tests/`](scripts/cmd-tests/README.md) each
exercising one cmd interaction primitive, plus a
`Diagnostics → CMD Interaction Tests` submenu that writes the
quoted script invocation to the PTY input cursor (no Enter)
so the maintainer can review + press Enter to run. The
corpus is the foundation for the post-Cycle-46 "evaluate how
basic interaction works beyond `echo` / `dir`" pass.

- **The eight scripts**:
  - `test-01-echo.cmd` — multi-line `echo` output (basic
    output narration + cap behaviour).
  - `test-02-text-input.cmd` — `set /p name=...` text input
    (line-edited prompt + echo back).
  - `test-03-numeric-input.cmd` — `set /p num=...` +
    `set /a` arithmetic (the maintainer's specific example
    in the feature ask).
  - `test-04-yes-no.cmd` — `choice /c YN` single-keystroke
    prompt.
  - `test-05-multi-choice.cmd` — `choice /c 1234 /n`
    multi-option (the closest cmd analogue to Claude's
    tool-use selection lists).
  - `test-06-pause.cmd` — `pause` between output sections.
  - `test-07-progress.cmd` — loop with `timeout` (tests
    mid-stream narration under `TupleFinalOnly`).
  - `test-08-stderr.cmd` — `>&2` redirected stderr output.

- **Test-marker discipline**: every script wraps its body
  in `=== PTYSPEAK-TEST-START: <id> ===` / `=== PTYSPEAK-
  TEST-END: <id> ===` markers so the boundaries appear in
  `Ctrl+Shift+D` snapshots. The `Program.fs` handler emits
  an INFO log line (`CmdTest invoked. TestId=<id>`) on menu
  click; combined with the in-script markers, timing
  correlation between the click and the resulting
  `ContentHistory` entries is one `grep` away.

- **Wiring**: eight `HotkeyRegistry.CmdTest*` AppCommands
  (menu-only — no keyboard accelerators); one shared
  `runCmdTest testId` helper in `Program.fs` that resolves
  `AppContext.BaseDirectory/scripts/cmd-tests/<id>.cmd`,
  writes the quoted invocation via
  `TerminalView.WritePtyBytes`, and announces "Test command
  inserted: `<id>`. Press Enter to run." through
  `ActivityIds.diagnostic`. Eight thin wrappers bind one
  AppCommand each. The 8 corresponding `MenuItem_CmdTest*`
  entries in `MainWindow.xaml` are picked up by the
  reflective menu-binding loop.

- **`Terminal.App.fsproj`** copies the scripts (and the
  README) flat into `<install-dir>/scripts/cmd-tests/` via a
  `<Content Include="..\..\scripts\cmd-tests\*.cmd">` glob
  with `<Link>scripts\cmd-tests\%(Filename)%(Extension)`.

- **`HotkeyRegistryTests`** documented-commands set extended
  to pin all eight new AppCommands. The Cycle 26b mirror
  test (`AppReservedHotkeysMirrorTests`) is unaffected since
  the new commands are menu-only (no `Key`/`Modifiers` →
  no `AppReservedHotkeys` row required).

### Cycle 46 post-audit follow-up (2026-05-13): ready-prompt earcon to a click + Data → Last Output submenu

Two small follow-ups to the audit PR per maintainer feedback:

- **`ready-prompt` earcon re-tuned from a tone to a click.**
  Was 1200Hz × 60ms (with 5ms attack); now 3000Hz × 15ms
  (no attack envelope). The 60ms tone overlapped the auto-
  narrate of the just-finished command in a way the
  maintainer described as disruptive. The shorter, higher,
  attack-free form perceptually separates from spoken
  output and reads as a brief tick rather than a tone.
  Implementation: parameter-only change in
  [`src/Terminal.Audio/EarconPalette.fs`](src/Terminal.Audio/EarconPalette.fs);
  the synthesis path
  (`EarconWaveform.synthSineEnvelope`) is unchanged. The
  existing Display → Earcons → Muted toggle silences this
  + every other earcon globally if the click is still
  unwanted; per-earcon mute is a follow-up (the maintainer
  explicitly asked not to over-invest in per-sound config
  yet).
- **Menu reorganization: Data → Last Output submenu.**
  The two `Ctrl+Shift+O` / `Ctrl+Shift+A` affordances added
  in the audit PR landed loose under the Data menu. They're
  now grouped under a `Last Output` submenu with two
  children: `Open in Text Editor`
  (`MenuItem_OpenLastOutput`) and `Announce Again`
  (`MenuItem_AnnounceLastOutput`). The reflective
  menu-binding loop (`Program.fs` ~line 3877) finds the
  named MenuItems via `window.GetType().GetField(...)`
  regardless of XAML nesting, so no Program.fs wiring
  changes are required.

No code paths changed beyond the palette numbers and the
XAML hierarchy. Hotkeys (Ctrl+Shift+O, Ctrl+Shift+A) are
unchanged. ACCESSIBILITY-TESTING matrix rows 46-11 → 46-16
continue to apply.

### Cycle 46 post-audit (2026-05-13): cap tuple-final output Announce + `Ctrl+Shift+O` open-last-output

Resolves the "DIR freezes all speech for ~5 minutes" symptom
the maintainer reported on the post-audit preview build
(version 0.0.1-preview.109, commit `bb0b239`).

**Root cause (confirmed via `Ctrl+Shift+D` log slice 2026-05-13
06:30:58 UTC)**: `SessionModel finalize extraction CmdLen=5
OutLen=18953` → `RaiseNotificationEvent firing.
ActivityId=pty-speak.output MsgLen=18953 Processing=MostRecent`.
A single 19 KB notification hits NVDA's queue; NVDA hands to
SAPI as one ~5–10 minute utterance which neither
`MostRecent` (different `activityId` → no supersede) nor the
Edit-control typed-character interrupt (NVDA's interrupt
runs at the keyboard layer; once SAPI has begun a render it
runs to completion) can cancel mid-utterance.

**Fix**:

- **`Program.fs` `OutputAnnounceCapChars = 800`**. The
  boundary handler caps `tupleFinaliseAnnounce` to the last
  800 characters of the tuple's `OutputText` before passing
  to `TerminalView.Announce`. Worst-case SAPI utterance is
  now ~30–60 seconds rather than 5–10 minutes; user can
  interrupt by typing (Edit-control flow), pressing Alt, or
  running another command that triggers its own announce.
  No prefix or suffix is added to the audible text — the
  truncation is silent at the channel; a
  `Tuple-final announce truncated.` log line at INFO
  records `OriginalLen` / `CappedLen` for diagnosis.
- **`Ctrl+Shift+O` — open last output in default text
  editor** (mnemonic: O for *O*pen Output). Writes the most
  recent tuple's full `OutputText` to a fresh timestamped
  file under `%LOCALAPPDATA%\PtySpeak\extracts\last-output-<ts>.txt`
  and launches it via `Process.Start` with
  `UseShellExecute=true` (registered `.txt` handler — Notepad
  by default, or whatever the user configured). The
  announce-then-700ms-delay pattern matches `Ctrl+Shift+E`
  (Edit config). Announces "Opening last output." on
  success, "No prior output." when `SessionModel.History`
  is empty.
- **Hotkey wiring**: new `HotkeyRegistry.OpenLastOutput`
  AppCommand (Letter 'O' + ctrlShift); registered in
  `AppReservedHotkeys` (`src/Views/TerminalView.cs`); menu
  item `MenuItem_OpenLastOutput` under "Data" in
  `MainWindow.xaml`. The reflective menu-binding loop
  (`Program.fs` ~line 3877) picks it up automatically.
- **`Ctrl+Shift+A` — re-narrate last command output**
  (mnemonic: *A*nnounce). Spoken counterpart to
  `Ctrl+Shift+O` for the user who missed the auto-narrate
  (was speaking, typing, switched window). Re-speaks the
  most recent tuple's `OutputText` via the same
  `TerminalView.Announce` channel + `ActivityIds.output`
  activity ID, capped at the same `OutputAnnounceCapChars`
  (800) the auto-narrate uses. NVDA's `MostRecent`
  processing means re-pressing supersedes the prior
  in-flight `pty-speak.output` notification, so the user can
  use it as a "say it again" / "say it louder" lever.
  Announces "No prior output." when `History` is empty;
  "Last command produced no output." when the latest
  tuple's `OutputText` is whitespace-only.
- **Hotkey wiring (`Ctrl+Shift+A`)**: new
  `HotkeyRegistry.AnnounceLastOutput` AppCommand
  (Letter 'A' + ctrlShift); registered in
  `AppReservedHotkeys`; menu item
  `MenuItem_AnnounceLastOutput` under "Data".
- **CLAUDE.md** §"Currently shipped" hotkey list gains a
  `Ctrl+Shift+O` entry alongside the existing `Ctrl+Shift+S` /
  `Ctrl+Shift+Y` companions.
- **`docs/ACCESSIBILITY-TESTING.md`** Cycle 46 matrix gains
  rows 46-11 (cap behaviour on `dir`), 46-12 (`Ctrl+Shift+O`
  happy path), and 46-13 (`Ctrl+Shift+O` empty-history
  guard) + a diagnostic-decoder paragraph for cap / hotkey
  failure modes.

**Claude shell unaffected**. `Claude`'s
`ShellPolicy.Streaming` is `LineByLine` (not `TupleFinalOnly`),
so the boundary handler's `tupleFinaliseAnnounce` is `None`
on every prompt and the cap path is never entered. Each
streaming chunk arrives via `SpeechCursor.onAppend` →
`Announce` independently and `MostRecent` does the right
thing per-chunk.

### Cycle 46 closure audit + Option ★★ pivot (no-spoken-output fix)

Post-PR-D audit PR sweeping for drift between the now-
shipped Cycle 46 state and the supporting docs, **plus** a
fix for the no-spoken-output regression maintainer testing
surfaced on the post-PR-D preview build.

**Code fix (the regression resolution).** Maintainer report:
"I'm no longer hearing any spoken output after command. I
can still hear menus and other speech." Root cause: NVDA
doesn't react to a bare
`AutomationEvents.TextPatternOnTextSelectionChanged` raised
by `RaiseCaretMovedToTail` when
`ITextProvider.GetSelection()` returns an empty array.
NVDA queries `GetSelection`, gets nothing, reads nothing.
This was the failure mode flagged in
[`docs/CYCLE-46-NEXT-STEPS.md`](docs/CYCLE-46-NEXT-STEPS.md)
§2's risk register.

Pivoted ADR §4 resolution from **Option ★ Replace** to
**Option ★★ Augment**:

- `speechCursorAnnounce` callback in
  [`src/Terminal.App/Program.fs`](src/Terminal.App/Program.fs)
  now calls `window.TerminalSurface.Announce(text, activityId)`
  **and** `raiseCaretMovedToTail ()`.
- The boundary handler's `tupleFinaliseAnnounce` branch
  similarly fires `Announce` alongside the caret-move event.
- `Announce` is what NVDA actually reads; the caret-move
  event stays as a defensive signal; the `ControlType=Edit`
  flip from PR-B is the load-bearing change for
  typing-interrupts-speech (NVDA's "Speech interrupt for
  typed character" setting fires on any key press in an
  Edit regardless of how the speech was initiated).
- Cycle 46's user-visible payoff is preserved: spoken
  output is back, AND typing interrupts speech, AND the
  architectural cleanup (legacy screen-grid types deleted,
  new ContentHistory-backed substrate, ControlType=Edit)
  stays in place.

The "long-term fix" — implement `GetSelection()` to point
at the tail on caret-move so we can drop the redundant
`Announce` again — is deferred to a future cycle. Recorded
in the ADR Status notes (2026-05-13 post-PR-D audit entry).

**Doc audit (the closure sweep).** Aligns the repo with the
post-Cycle-46 reality:

- **[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)** —
  "Current state" flips from "PR-A + PR-B merged; PR-C / PR-D
  pending" to "Cycle 46 fully shipped"; per-PR table; pivot
  note for the Option ★★ revision.
- **[`docs/PROJECT-PLAN-2026-05-12.md`](docs/PROJECT-PLAN-2026-05-12.md)**
  — §1 "What changed" collapses PR-A → PR-D into one Cycle 46
  entry; §3 validation gate consolidates from 46-PRB-1 to
  46-1; §4 "Primary track" removed (Cycle 46 done); §7
  change log appended.
- **[`CLAUDE.md`](CLAUDE.md) §"Current sequencing"** —
  per-PR breakdown collapsed under a single Cycle 46 heading;
  drop CYCLE-46-NEXT-STEPS.md from the canonical-docs list
  (now historical).
- **[`docs/CYCLE-46-NEXT-STEPS.md`](docs/CYCLE-46-NEXT-STEPS.md)**
  — banner at the top marks it historical with a "things this
  doc says that are now untrue" list. Kept verbatim as a
  retrospective artifact.
- **[`docs/adr/0002-uia-textedit-caret-output.md`](docs/adr/0002-uia-textedit-caret-output.md)**
  — Status flipped from "Accepted" to "Accepted / Implemented";
  Decision §4 + §"Open Questions" §4 record the Option ★ →
  ★★ pivot; "Staged implementation plan" gets a historical
  banner; per-PR Status notes for PR-C, PR-D, and the post-
  PR-D audit appended.
- **[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)**
  — gains a Cycle 46 matrix section (rows 46-1 through 46-10
  covering focus-announce, typing-interrupt, Alt-interrupt,
  read-all, line-nav, manual SpeechCursor, PowerShell, Claude,
  non-output notifications, errors) + a diagnostic decoder
  for likely failure modes.
- **[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)** — module
  map row for `Terminal.Accessibility` rewritten: drops the
  deleted `TerminalTextProvider` / `TerminalTextRange`;
  names the new `ContentHistoryTextProvider` /
  `ContentHistoryTextRange` + `RaiseCaretMovedToTail`;
  marks `ControlType=Edit`.
- **[`docs/USER-SETTINGS.md`](docs/USER-SETTINGS.md)** —
  word-boundary section updated to reference
  `ContentHistoryTextRange.IsWordSep` (post-Cycle-46) instead
  of the deleted `TerminalTextRange.IsWordSeparator`. Sample
  signatures + test-construction notes updated accordingly.

**CLAUDE.md addition (per maintainer ask).** Added a new
§"Cycle closure audit" subsection to CLAUDE.md "Project
conventions" so every future multi-PR cycle plans for the
closure-audit step from PR-A. Includes a checklist of doc
surfaces to sweep + `grep` patterns that catch the most
common drift.

### SpeechCursor delegates to caret + screen-grid cleanup (Cycle 46 PR-D)

Implements PR-D of the four-PR Cycle 46 sequence per
[`docs/adr/0002-uia-textedit-caret-output.md`](docs/adr/0002-uia-textedit-caret-output.md)
+ [`docs/CYCLE-46-NEXT-STEPS.md`](docs/CYCLE-46-NEXT-STEPS.md) §3.
Auto-drive narration of streaming output now goes through the
same caret-move path PR-C introduced, and the legacy
screen-grid types are gone.

- **Refactored** `Program.fs`'s `speechCursorAnnounce` callback
  to delegate to a new `raiseCaretMovedToTail` helper. Both the
  PR-C boundary handler (tuple finalise) and the per-entry
  `SpeechCursor.onAppend` invocations (reader-loop streaming +
  boundary-handler marker emit) now route through one shared
  call site. The `text` / `activityId` arguments to
  `speechCursorAnnounce` are preserved in the signature but
  unused — NVDA queries `DocumentRange` to get current content.
- **Kept** manual review-cursor hotkeys
  (`Ctrl+Shift+Up/Down/End` →
  `runSpeechCursorNext/Previous/JumpToLatest`) on the
  notification path. They emit UI-navigation feedback like
  "Already at the first entry" / "(no announcement for this
  entry)" which is non-terminal-content per ADR §"Decision"
  clause 5 (terminal command I/O uses caret;
  menus/errors/navigation/diagnostic use notifications).
- **Deleted** the legacy screen-grid types from
  `src/Terminal.Accessibility/TerminalAutomationPeer.fs`:
  `module internal SnapshotText`, `type internal TerminalTextRange`
  (with its `IsWordSeparator` / `NextWordStart` /
  `PrevWordStart` / `WordEndFrom` helpers and full
  `ITextRangeProvider` implementation), and `type internal
  TerminalTextProvider`. ~680 LOC removed.
- **Deleted** `tests/Tests.Unit/WordBoundaryTests.fs` (~30
  tests pinning the now-deleted helpers; equivalent
  word-boundary semantics are covered by
  `ContentHistoryTextRangeTests`).
- **Updated** `TerminalView.cs` and `Tests.Unit.fsproj`
  comments to reflect the deletion.
- **NVDA matrix gate Cycle 46-PRD-1** required before merge:
  `Ctrl+Shift+Up/Down/End` parity — net audible behaviour
  should match PR-C (NVDA reads via caret pacing, not an
  independent SAPI utterance) for the streaming auto-drive
  case; navigation-feedback announces still fire for the
  manual hotkeys.

### Caret-move replaces output notification (Cycle 46 PR-C): `TextSelectionChangedEvent` on tuple finalise

Implements PR-C of the four-PR Cycle 46 sequence per
[`docs/adr/0002-uia-textedit-caret-output.md`](docs/adr/0002-uia-textedit-caret-output.md)
+ [`docs/CYCLE-46-NEXT-STEPS.md`](docs/CYCLE-46-NEXT-STEPS.md) §2.
The notification-based output read
(`Announce(text, ActivityIds.output, AutomationNotificationProcessing.MostRecent)`)
fired from `Program.fs`'s `handlePromptBoundary` is replaced by a
caret-move event on the existing `TerminalAutomationPeer`.

**User-visible payoff** (the original motivating issue for
Cycle 46): NVDA's native "read from caret" path now handles
pacing for long output, **and typing into the prompt
interrupts the read** via NVDA's "Speech interrupt for typed
character" setting. The failure modes recorded in ADR 0002
Context — PR #282 `MostRecent` default, PR #284 empty-string
flush, PR #285 single-space flush ("blank" verbalisation),
PR #286 revert — are resolved.

- **Added** `TerminalAutomationPeer.RaiseCaretMovedToTail()`
  (in
  [`src/Terminal.Accessibility/TerminalAutomationPeer.fs`](src/Terminal.Accessibility/TerminalAutomationPeer.fs)).
  Raises `AutomationEvents.TextSelectionChanged` on the peer;
  NVDA queries `DocumentRange` and re-reads the new
  `ContentHistory` tail with user-tunable pacing.
- **Added** `InternalsVisibleTo("Terminal.App")` to
  Terminal.Accessibility's assembly attributes so the
  composition root in `Program.fs` can call the new helper
  without widening the public API. The peer type stays
  `internal`.
- **Modified**
  [`src/Terminal.App/Program.fs`](src/Terminal.App/Program.fs)
  to call the helper from the boundary handler
  (`handlePromptBoundary` → `boundaryAction`, dispatched on
  the WPF dispatcher). The `tupleFinaliseAnnounce` gate
  (`ShellPolicy.Streaming = TupleFinalOnly` with non-blank
  output) stays so `LineByLine` / `Off` policies still
  produce the right number of announces.
- **Kept** the `SpeechCursor.onAppend` invocation in the
  boundary handler and the reader loop. PR-D collapses the
  remaining `SpeechCursor → Announce` paths into the same
  caret-move helper.
- **Kept** `ActivityIds.output` — still used by
  `SpeechCursor`'s `renderEntry` and `NvdaChannel`'s
  `semanticToActivityId` map. Deletion (if anywhere) is a
  PR-D concern.
- **NVDA matrix gate Cycle 46-PRC-1**: cmd `dir` long output
  (caret-pacing + typing-interrupt), cmd `echo hi`
  (short-output round-trip), Claude REPL turn
  (streaming-via-caret), `Alt` interrupts in-progress read
  (the maintainer's original pain point).

### Session-handoff docs (2026-05-13): Cycle 46 PR-C / PR-D scoping

Captures the in-flight Cycle 46 state for the next session:

- **New**:
  [`docs/CYCLE-46-NEXT-STEPS.md`](docs/CYCLE-46-NEXT-STEPS.md) —
  file-level edits, threading concerns, test plans, and NVDA
  matrix gate definitions for PR-C (wire output to caret) and
  PR-D (`SpeechCursor` delegation + screen-grid cleanup).
  Self-contained; the next session can pick up without prior
  chat context.
- **Updated**:
  [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md) — Current
  state moves from "Cycle 45c cleanup complete" to "Cycle 46
  PR-A + PR-B merged". Next-stage section reframes the primary
  track around PR-C / PR-D while preserving the parallel-track
  candidates (45g / 45d / semantic labels / spinner-red-tone /
  Coalescer rename). Operational-gotcha row added for the GitHub
  MCP outage observed 2026-05-13.
- **Updated**:
  [`docs/PROJECT-PLAN-2026-05-12.md`](docs/PROJECT-PLAN-2026-05-12.md) —
  §1 "What changed" extends to cover Cycle 46 PR-A + PR-B. §3
  validation gate renamed from 45c-1→6 to 46-PRB-1. §4
  candidate cycles add a "Primary track" subsection with PR-C
  / PR-D linked to CYCLE-46-NEXT-STEPS.md. §7 change log
  appended.
- **Updated**: [`CLAUDE.md`](CLAUDE.md) §"Current sequencing"
  — adds Cycle 46 PR-A + PR-B + PR-C + PR-D headlines, points
  at CYCLE-46-NEXT-STEPS.md.

No code changes; pure docs.

### Substrate-swap (Cycle 46 PR-B): ContentHistory backs the UIA Text pattern

Implements PR-B of the four-PR Cycle 46 sequence per
[`docs/adr/0002-uia-textedit-caret-output.md`](docs/adr/0002-uia-textedit-caret-output.md)
(Accepted 2026-05-13). The existing screen-grid `TerminalTextProvider`
is replaced as the runtime `ITextProvider` by a new
`ContentHistoryTextProvider` + `ContentHistoryTextRange`
(`src/Terminal.Accessibility/ContentHistoryTextRange.fs`)
backed by `ContentHistory.tailText` (256 KB cap). The peer's
`AutomationControlType` flips from `Document` to `Edit` so
NVDA treats `TerminalView` as a text-edit surface — read-all,
read-line, character / word / line navigation, jump-to-end all
operate against the substrate's linear-text tail instead of
the screen-grid snapshot.

- **No call-site changes yet.** `TerminalView.Announce` keeps
  firing `RaiseNotificationEvent` on `ActivityIds.output`
  exactly as today; the caret-move wiring lands in PR-C.
- **NVDA matrix gate (Cycle 46-PRB-1) required before merge.**
  User-visible changes: NVDA announces "edit" on focus
  instead of "document"; "read all" reads the
  `ContentHistory` tail (last 256 KB) instead of the screen
  grid.
- **Open Questions §1–§5 resolved** (2026-05-13 inline in
  ADR Status notes): full `ITextRangeProvider` interface
  (§1); `TerminalView` itself becomes `Edit` (§2 Option B);
  `SpeechCursor` delegates to the caret in PR-D (§3
  Option β); notification path replaced for terminal I/O
  (§4 Option ★); `SessionModel` calls peer directly without
  substrate change (§5 Option ◇◇).
- **Files**:
  `src/Terminal.Accessibility/ContentHistoryTextRange.fs`
  (new); `src/Terminal.Accessibility/Terminal.Accessibility.fsproj`,
  `src/Terminal.Accessibility/TerminalAutomationPeer.fs`,
  `src/Views/TerminalView.cs`, `src/Terminal.App/Program.fs`,
  `docs/adr/0002-uia-textedit-caret-output.md`,
  `tests/Tests.Unit/ContentHistoryTextRangeTests.fs` (new),
  `tests/Tests.Unit/Tests.Unit.fsproj`.

### Proposed (Cycle 46 PR-A): ADR 0002 — UIA TextEdit caret as the output channel

Drafts [`docs/adr/0002-uia-textedit-caret-output.md`](docs/adr/0002-uia-textedit-caret-output.md)
naming **Cycle 46** as the umbrella for replacing the UIA
`RaiseNotificationEvent` model for command output with a UIA
TextEdit caret on a sibling automation peer (or on
`TerminalView` itself — see ADR Open Question §2). The ADR
records the failure sequence (PR #282 `MostRecent` default →
PR #284 empty-string flush → PR #285 single-space flush →
PR #286 revert), names the architectural mismatch
(notification is a status-message channel, not a streaming-
content channel), and outlines a four-PR staged plan:

- **PR-A** (this PR): ADR + CHANGELOG entry.
- **PR-B**: Add `TerminalOutputAutomationPeer` implementing
  `ITextProvider` + minimum `ITextRangeProvider` subset, with
  unit tests against `ContentHistory` fixtures. Not yet
  wired.
- **PR-C**: Wire output to the caret; tuple-final emit
  moves the caret + raises `TextSelectionChangedEvent`
  instead of (or in addition to — see Open Question §4)
  the existing `Announce` call. NVDA matrix gate.
- **PR-D**: Polish — typing-interrupts-speech via NVDA's
  native setting, `SpeechCursor` integration / future, dead-
  code trim.

Five Open Questions need maintainer resolution before PR-B
ships (minimum `ITextRangeProvider` subset, focus model,
`SpeechCursor` future, replace-vs-augment, `ContentHistory`
change-notification mechanism).

Docs-only; merges under the docs-only fast lane after the
link checker passes.

### Reverted (Cycle 45c follow-up): keystroke flush of pending NVDA output speech

PR #284 added a `MostRecent` notification on every keystroke,
intended to clear NVDA's pending speech queue when the user
typed or pressed Alt during a long output read. PR #285 changed
the payload from `""` to `" "` after NVDA was found to drop
empty `displayString` events before reaching its
`cancelSpeech()` path.

Both versions were wrong. Empty-payload version was silently
filtered by NVDA and did nothing. Single-space version made
NVDA verbalise every keystroke as **"blank"** without
interrupting the in-progress long read — the worst of both
worlds.

Reverting the keystroke flush entirely. The underlying problem
— a single ~19 KB output notification on a busy `dir` taking
~5–10 minutes to read with no interruption path short of
restart — is **architectural**: the UIA `RaiseNotificationEvent`
primitive isn't designed for streaming long content, and
trying to interrupt it post-hoc with another notification fights
NVDA's design. A real fix lives at the announce-side (cap
length / summary + review-cursor / replace notification model
with a UIA TextEdit caret), not in the keyboard handler.

### Changed (Cycle 45c follow-up): silence idle FileLogger spam in `HeuristicPromptDetector`

Maintainer dogfood 2026-05-12: every line of a 50-line
`Diagnostics → Extract → Last 50 Log Lines` extract was
`HeuristicPromptDetector SUPPRESSED PromptStart … rowDirtyAccumulated=False`.
At idle the detector ticks every 50 ms and the steady-state
prompt always passes the "stable match" gate but fails the
"row went dirty since last emit" gate, so the suppression
trace fired ~20 times per second — ~5 KB/sec of pure-noise log
output and ~18 MB/hour of FileLogger writes.

Gated the trace on `rowDirtyAccumulated=true`. The diagnostic
value (the post-screen-fill silence regression hunt this line
was originally added for) lives entirely in the `true` branch
— that's the "row went dirty then came back to the same
prompt" case. The `false` branch is steady-state suppression
with nothing to learn from.

### Changed (Cycle 45c follow-up): announce queueing now uses `MostRecent` for every activity-id

Maintainer dogfood 2026-05-12 named the symptom: after running
`dir` in cmd the NVDA queue could spend ~2 minutes reading the
~3 KB tuple-final output. During that read, **nothing else**
got airtime — the next command's tuple-final, `Alt`-opened
menus, hotkey announces all sat behind it. The "silent third
echo" wasn't silence; it was speech queued behind a wall.

`TerminalView.Announce(msg, activityId)` previously routed
`ActivityIds.output` to `AutomationNotificationProcessing.ImportantAll`
("queue every announce, read them all"). The 2-arg overload
now uses `MostRecent` uniformly — a later announce displaces
the still-queued prior one, so NVDA always reads the most
recent thing the user did rather than completing a back-log.
The 3-arg overload remains available for callers that need
explicit control.

Trade-off: long output reads can be interrupted by a
subsequent command's announce. Users who want every byte read
to completion can pivot to manual speech-cursor navigation
(`Ctrl+Shift+Up` / `Down` / `End`).

### Changed (Cycle 45c follow-up): diagnostic battery + ContentHistory observability

The `Ctrl+Shift+D` self-test battery's per-shell tests still
asserted on `SemanticCategory.StreamChunk` / `ErrorLine` /
`WarningLine` — events that the deleted `StreamPathway` produced
pre-Cycle-45c. Post-Cycle-45c the announce path runs through
ContentHistory + SessionModel tuple-finalise, so those tests
reported **FAIL** even on a healthy build. Replaced the
PowerShell colour-detection cases (T1–T5) with a single
`T1.Plain` case asserting that a `ReadyForInput` chime fires
(tuple sealed). Added a `T2.SecondPlain` case to cmd as a
regression pin for the silent-after-second-echo bug fixed by
PR #280.

Added the **`Stats:` header** to the `--- CONTENT HISTORY ---`
section of every diagnostic bundle (full + lightweight). The
header surfaces total entry count, latest assigned Seq, and
per-kind marker tallies (`PromptStart`, `CommandStart`,
`OutputStart`, `CommandFinished`, `BellRang`, `SelectionShown`,
`AltScreenEnter`). The silent-third-echo bug hid behind an
inline tail that *looked* populated; the stats header would
have shown `OutputStart=0` immediately.

Added a `Debug`-level **extraction-path log line** in
`SessionModel.finalizeAndEnqueue`. Each tuple seal records
`Path=content-history | row-walk-after-content-history-miss |
row-walk-only` plus `CmdLen / OutLen / OldRow / NewRow`. A
paste of `--- FILELOGGER ACTIVE LOG ---` now answers "which
extraction path produced the empty CommandText?" without the
maintainer needing a follow-up dogfood round to reproduce.

### Fixed (Cycle 45c fixup): NVDA silent after second command following scrolled output

Maintainer reproducer 2026-05-12: run `echo hi` → `dir` → `echo hi`.
NVDA announces the first two but goes silent on the third. The
diagnostic snapshot showed the just-finalised `SessionTuple` had
empty `CommandText` AND empty `OutputText`, so the
tuple-finalise-announce path (gated on non-whitespace `OutputText`)
produced nothing for NVDA to read.

**Root cause** (pre-existing, not introduced by Cycle 45c): cmd
doesn't emit OSC 133 markers, so
`extractContentFromContentHistory` returned `None` (it required
both `PromptStart` AND `OutputStart` markers). SessionModel then
fell back to `extractContent`'s screen-row walk against the
snapshot — which fails when the output has scrolled enough that
the prior prompt and the new prompt share a `rowIdx` (both at
the bottom of the screen at row 29). The row-walk can't
distinguish "same row" from "no content between prompts" and
returns empty.

ContentHistory had the data (sequenced typed-entry log, no
same-row ambiguity) — we just weren't slicing it for the
heuristic-only path.

**Fix**: extend `extractContentFromContentHistory` with a
PromptStart-only fallback. When the OSC 133 `OutputStart` marker
is absent but a prior `PromptStart` marker is present, slice
the blob from that marker to MaxValue — that's the just-
finalising tuple's combined typed-input + output text. The
trailing portion (the new prompt's rendered text, which landed
in ContentHistory before the boundary fired) is trimmed via
the boundary's `MatchedRowText`. The remainder is split on the
first newline (cmd's wire format is "echo hi\nhi\n…") into
`(CommandText, OutputText)`.

OSC 133-emitting shells (Claude, future ones) keep the clean
two-marker split. Shells without any markers (initial-startup
case) keep falling back to `extractContent`.

Tests: `tests/Tests.Unit/SessionModelTests.fs` gains 3 new cases
pinning the PromptStart-only path (with + without
`newPromptText` trim) and the OSC 133 regression pin (both
markers → OSC 133 split wins).

### Changed (Cycle 45c follow-up): post-cleanup docs + onboarding refresh

Post-Cycle-45c audit + documentation sweep ensuring the repo's
docs reflect the new (much simpler) ContentHistory + SpeechCursor
paradigm. The maintainer flagged that `docs/SESSION-HANDOFF.md`
had grown to ~2,200 lines of accumulated decision history,
diluting its role as a new-session onboarding brief. This sweep
rebalances:

**Onboarding TLDR**:
- `docs/SESSION-HANDOFF.md` rewritten as a slim ~100-line TLDR
  (current state, where we left off, next-stage candidates,
  sandbox gotchas, pointers out). The previous multi-thousand-line
  version moved to
  [`docs/archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md`](docs/archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md)
  where it serves the decision-trail role this file used to
  overload.
- `docs/PROJECT-PLAN-2026-05-12.md` added as the new active
  strategic plan (succeeds `2026-05-09` per Track E E5
  dated-snapshot discipline). Captures post-Cycle-45c state +
  candidate next cycles (45g `ShellPolicy` consolidation, 45d
  review-cursor focus, semantic labels, spinner / red-tone
  refinements, Coalescer rename, UIA caret). Predecessor moved
  to
  [`docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md).

**Cross-reference fixes**:
- All live links to `PROJECT-PLAN-2026-05-09.md` retargeted to
  the new `PROJECT-PLAN-2026-05-12.md` across `CLAUDE.md`,
  `README.md`, `docs/CORE-ABSTRACTION-BOUNDARY.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`,
  `docs/USER-SETTINGS.md`, `docs/DOC-MAP.md`,
  `docs/ACCESSIBILITY-TESTING.md`, `docs/ROADMAP.md`.
  CHANGELOG historical refs retargeted to the archive path.
- `CLAUDE.md` reading-order item 4 + "Current sequencing" section
  refreshed to point at the new plan + describe the post-Cycle-45c
  cycle state.
- `docs/DOC-MAP.md` row for SESSION-HANDOFF updated to reflect
  the new "TLDR brief" role.

**Docs body pruning**:
- `docs/USER-SETTINGS.md` — replaced ~210-line "Pathway / substrate
  selection" + "Substrate mode" sections with a ~25-line
  retirement note pointing at `ShellPolicy` (Cycle 45f) + the
  archive for predecessor detail. Replaced ~618-line
  "Suffix-diff parameters (PR #166 follow-up)" atlas section
  with a ~25-line retirement note + the current ContentHistory
  parameter surface (`IdleSpanSealMs`, `MaxEntriesPerTuple`).
  Net: ~800 lines removed from USER-SETTINGS.md.
- `docs/ACCESSIBILITY-TESTING.md` — Cycle 36 substrate-inversion-arc
  matrix section gained a Cycle 45c retirement banner pointing
  at rows 45c-1..45c-6 for current validation. Bundle-section
  reference `--- LINEAR STREAM ---` corrected to
  `--- CONTENT HISTORY ---` in the Cycle 43a Copy Latest Bundle
  test.
- `docs/ARCHITECTURE.md` — currency note rewritten to reflect
  post-Cycle-45c state; "PathwayPump thread" row in the
  threading table renamed to "Notification-consumer thread";
  `Terminal.Core/StreamPathway.fs` file inventory entry replaced
  with `ContentHistory.fs` + `SpeechCursor.fs` entries.
- `docs/CORE-ABSTRACTION-BOUNDARY.md`, `docs/SESSION-MODEL.md`,
  `docs/INTERACTION-MODEL.md`, `docs/CUSTOMIZATION-MODEL.md`,
  `docs/PANE-MODEL.md`, `docs/CHANNEL-ARCHITECTURE.md` —
  each gained a Cycle 45c retirement banner at the top
  explaining which named pieces survive vs. retired.
  Deep section-by-section rewrites are deferred to a future
  doc-cleanup cycle per
  [`docs/PROJECT-PLAN-2026-05-12.md`](docs/PROJECT-PLAN-2026-05-12.md)
  § "Open follow-ups".

**Code-comment cleanup**:
- 6 highest-visibility stale comments updated:
  `src/Terminal.App/Program.fs` (Cycle 17 actor-model docstring,
  PumpInput.Tick docstring),
  `src/Terminal.Core/SessionModel.fs` (Cycle 35b substrate-aware
  block header),
  `src/Terminal.Core/CanonicalState.fs`,
  `src/Terminal.Core/EarconProfile.fs`,
  `src/Terminal.Core/PassThroughProfile.fs`,
  `tests/Tests.Unit/EarconProfileTests.fs`. Remaining inline
  references to deleted modules in comments are now historical
  in framing (DEAD-CODE-MARKER); they survive as git-blame
  archaeology.

User-visible behaviour: **none**. Pure docs + comments.

### Removed (Cycle 45c cleanup PR-3c): pathway-pipeline source modules + LinearTextStream substrate

Final step in the Cycle 45c cleanup sequence. The pre-Cycle-45
substrate pipeline is now fully retired in code.

Deleted source files (~3,584 LOC):
- `src/Terminal.Core/StreamPathway.fs` (1,256 LOC)
- `src/Terminal.Core/LinearTextStream.fs` (1,023 LOC)
- `src/Terminal.Core/DisplayPathway.fs` (111 LOC)
- `src/Terminal.Core/TuiPathway.fs` (105 LOC)
- `src/Terminal.Core/PathwaySelector.fs` (99 LOC)

Removed from `src/Terminal.Core/Terminal.Core.fsproj`:
- 5 `<Compile Include>` entries for the source files above

Removed from `src/Terminal.Core/Config.fs`:
- `StreamParameterOverrides` record type
- `StreamOverrides` field on the `Config` record
- `resolveStreamParameters` function (stub from PR-2)
- Loader branch that populated `StreamOverrides`

Removed from `src/Terminal.Core/SessionModel.fs`:
- `extractContentFromLinearStream` (private)
- `applyAndCaptureWithSubstrate` (public)
- `applyWithSubstrate` (public)

Removed from `tests/Tests.Unit/SessionModelTests.fs`:
- `freshLinear` + `feedLinear` fixture helpers
- 5 Cycle 35b OSC 133 LinearTextStream test cases (the Cycle
  45c ContentHistory equivalents added in PR-3a cover the same
  contract).

Removed from `tests/Tests.Unit/ConfigTests.fs`:
- 4 "defaultConfig has X" smoke tests for the stripped
  `resolveStreamParameters` API (the bridge tests retained in
  PR-2 are now dead).

Removed from `src/Terminal.App/Program.fs`:
- `linearStream` mutable creation in `compose ()`
- `LinearTextStream.append` call in the reader loop
- `linearStream` parameter from `startReaderLoop` signature
- All `LinearTextStream.getLastBytes` diagnostic calls

ContentHistory grows one helper (`ContentHistory.tailText`) to
back the diagnostic bundle's `--- CONTENT HISTORY (last 64KB) ---`
section. Walks `Entries` from tail, accumulating until the byte
cap is hit, then reverses to chronological order.

Renamed in `src/Terminal.App/Diagnostics.fs`:
- `--- LINEAR STREAM (last 64KB) ---` bundle section →
  `--- CONTENT HISTORY (last 64KB) ---`
- Sibling file `linear-stream-<ts>.txt` →
  `content-history-<ts>.txt`
- `resolveLinearStreamPath` → `resolveContentHistoryPath`
- `runFullBattery`'s `resolveLinearStream` parameter →
  `resolveContentHistory`
- `linearStreamSection` parameter on `formatDiagnosticBundle` +
  `formatLightweightBundle` → `contentHistorySection`

Updated docs:
- `docs/CORE-ABSTRACTION-BOUNDARY.md` — Cycle 45c header note
  explaining the pathway pipeline retirement.
- `docs/ARCHITECTURE.md` — data-flow ASCII diagram + module
  map updated to reflect ContentHistory + SpeechCursor as the
  aural substrate. Pathway modules removed from the inventory.
- `docs/ACCESSIBILITY-TESTING.md` — new Cycle 45c NVDA matrix
  with 6 rows (45c-1 through 45c-6) covering cmd narration,
  long output through NVDA chunking, alt-screen TUI apps,
  Claude streaming, the renamed diagnostic bundle section, and
  OSC 133 boundary detection.

**Diagnostic bundle compatibility**: old bundles in
`%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\` carry the
`--- LINEAR STREAM ---` header; triage scripts grepping for that
string will break. Acceptable — bundles are session-local triage
artefacts, not interchange format.

**User-visible behaviour**: unchanged in the happy path. Cycle 45
NVDA dogfood confirmed the pathway calls weren't on the announce
path. **NVDA matrix walk required** (rows 45c-1 through 45c-6 in
`docs/ACCESSIBILITY-TESTING.md`) before tagging a release; this
PR cannot fast-merge.

### Removed (Cycle 45c cleanup PR-3b): pathway-pipeline wiring from Program.fs

Strips the dead `StreamPathway` / `LinearTextStream` /
`DisplayPathway` / `TuiPathway` / `PathwaySelector` call sites
from `src/Terminal.App/Program.fs`. Cycle 45's ContentHistory +
SpeechCursor pipeline has been the live announce path since
PRs #263–#270; the pathway calls were parallel-but-uncalled,
dispatching OutputEvents with empty Payloads that produced no
NVDA effect.

Deleted in Program.fs:

- `activePathway` mutable + initial `StreamPathway.create`
- `selectPathwayForShell` helper
- `swapPathwayForAltScreen` helper
- `dispatchPathwayEvents` helper (no callers)
- `activePathway.OnPromptBoundary` call in `handlePromptBoundary`
- `activePathway.Consume` call in `handleRowsChanged`
- `activePathway.OnModeBarrier` + `PathwaySelector.decideAltScreenAction`
  match + `swapPathwayForAltScreen` swap in `handleModeChanged`
- `activePathway.Tick` call in `handleTick`
- Startup `activePathway.Reset` + `selectPathwayForShell` swap
- `switchToShell`'s pathway Reset / reassignment / SetBaseline block

`handleModeChanged` simplified to a direct `if screen.Modes.AltScreen`
branch that toggles `SessionModel.enterAltScreen` / `exitAltScreen`
+ resets the prompt + selection detectors. The `barrier` OutputEvent
dispatch is retained so the FileLogger still captures mode transitions.

Cycle 35b's `SubstrateMode` enum dispatch (Linear / ScreenDiff /
Auto) is collapsed to `useContentHistory = true`. ContentHistory
is now the universal substrate; the fallback to `extractContent`'s
row-walk happens organically inside
`extractContentFromContentHistory` when no OSC 133 markers are
present, and `SessionModel.IsAltScreenActive` separately gates
boundary processing during alt-screen TUI apps. The `substrate_mode`
TOML key is effectively dead (already stripped by PR-2).

User-visible behaviour: unchanged. Cycle 45 NVDA dogfood confirmed
the pathway calls weren't on the announce path; PR-3b validates
that by deleting them with zero user-facing impact. Alt-screen
TUI apps (vim, less, htop, etc.) still get their entry/exit
acknowledged via the SessionModel state-machine call retained in
`handleModeChanged`.

Diagnostic plumbing retained:
- `LinearTextStream` reader-loop feed + `getLastBytes` for the
  bundle's `--- LINEAR STREAM ---` section (PR-3c migrates this
  to ContentHistory + renames the section).
- `Diagnostics.captureSessionModel`'s `activePathwayId` parameter
  receives a constant `"content-history"` (PR-3c renames the
  parameter).

### Added (Cycle 45c cleanup PR-3a): ContentHistory-driven OSC 133 substrate

Migration step toward deleting the `LinearTextStream` substrate.
SessionModel can now extract `(CommandText, OutputText)` for each
SessionTuple from ContentHistory directly. The LinearTextStream
path stays alive in this PR (parallel-but-uncalled); PR-3c deletes
it.

`src/Terminal.Core/ContentHistory.fs` grows two helpers:
- `tryLatestMarker` — locates the most recent Marker entry of a
  given Kind in the current tuple's history (used to find
  PromptStart / OutputStart at tuple-seal time).
- `sliceText` — reconstructs user-visible text from entries
  whose Seq is strictly between two boundary seqs. TextSpan +
  Overwrite + Spinner contribute their text; Newline contributes
  `\n`; Markers contribute nothing. The unsealed active TextSpan
  is included when its implicit Seq is in-region (important at
  tuple-seal time when the closing marker hasn't been appended
  yet).

`src/Terminal.Core/SessionModel.fs` grows three companions:
- `extractContentFromContentHistory` (private) — the
  ContentHistory analogue of `extractContentFromLinearStream`.
- `applyAndCaptureWithContentHistory` — public substrate-aware
  finalize taking a `ContentHistory.T` + `useContentHistory: bool`.
- `applyWithContentHistory` — state-only wrapper.

`src/Terminal.App/Program.fs` (`handlePromptBoundary`) switches
to call `applyAndCaptureWithContentHistory`; the old
`applyAndCaptureWithSubstrate(... linearStream)` call site is
gone. The `StreamPathway.SubstrateMode` enum dispatch shape is
preserved verbatim — collapses to a single bool in PR-3b once
StreamPathway gets deleted.

Compile order: `Terminal.Core.fsproj` reordered to put
`ContentHistory.fs` above `SessionModel.fs` (it depends on
`Types.fs` only, so it slots in next to `LinearTextStream.fs`).
`SpeechCursor.fs` follows `ShellPolicy.fs` since it depends on
both `ContentHistory` and `ShellPolicy`.

Tests:
- `tests/Tests.Unit/ContentHistoryTests.fs` gains 9 new cases
  pinning `tryLatestMarker` + `sliceText`: empty / present /
  repeated / wrong-kind / multi-entry slice / active-span
  inclusion / empty region / marker-text exclusion.
- `tests/Tests.Unit/SessionModelTests.fs` gains a parallel
  block of 5 Cycle 45c cases mirroring the existing Cycle 35b
  block; the Cycle 35b LinearTextStream cases stay until PR-3c
  retires the substrate.

### Removed (Cycle 45c cleanup PR-2): pathway-pipeline test files + `[pathway.stream]` TOML parsing

Test-side prune of the pathway pipeline Cycle 45 replaced. Deleted
four dedicated test files (~2,750 LOC):
- `tests/Tests.Unit/StreamPathwayTests.fs`
- `tests/Tests.Unit/LinearTextStreamTests.fs`
- `tests/Tests.Unit/TuiPathwayTests.fs`
- `tests/Tests.Unit/PathwaySelectorTests.fs`

Removed the corresponding `<Compile Include>` entries from
`tests/Tests.Unit/Tests.Unit.fsproj`.

Stripped the `[pathway.stream]` TOML parser from
`src/Terminal.Core/Config.fs` (the `knownStreamKeys` set, the
~100-line `parseStreamOverrides` function, and the loader branch
that invoked it). `config.toml` files that still carry a
`[pathway.stream]` section parse silently — the section is ignored
and the loader uses `defaultConfig.StreamOverrides`.
`Config.resolveStreamParameters` still exists and continues to
return `StreamPathway.defaultParameters` (it reads from the now
always-default `StreamOverrides` field). It will be deleted with
the `StreamPathway` type itself in PR-3.

Removed ~22 cases from `tests/Tests.Unit/ConfigTests.fs` that
exercised `[pathway.stream]` TOML parsing (Per-pathway parameter
override, color_detection, PR #168 Tier 1 backspace_policy /
mode_barrier_flush_policy / bulk_change_threshold, Cycle 35a
substrate_mode, plus the round-trip and negative/zero edge cases).
The "defaultConfig has X" smoke tests (debounce, color, bulk,
backspace, mode-barrier defaults) stay — they pass against the
stub and will be removed alongside the `StreamPathway` type in
PR-3.

### Changed (Cycle 45c cleanup PR-1): docs archived to `docs/archive/pre-cycle-45/`

The narrative layer that described the pre-Cycle-45 substrate
pipeline (replaced by `ContentHistory` + `SpeechCursor` in
PRs #263–#270) moved to `docs/archive/pre-cycle-45/`:

- Predecessor strategic plans: `PROJECT-PLAN-2026-05.md`
  (2026-05-03), `PROJECT-PLAN-2026-05-revision.md` (2026-05-07).
  Current plan `PROJECT-PLAN-2026-05-09.md` stays live.
- Six May-2026 audit deliverables (`AUDIT-CODE-CONSISTENCY`,
  `AUDIT-TEST-INVENTORY`, `AUDIT-SPEC-ALIGNMENT`,
  `AUDIT-ATLAS-ALIGNMENT`, `AUDIT-DOC-CURRENCY`,
  `AUDIT-BACKLOG-VALIDATION`).
- `PIPELINE-NARRATIVE.md` (12-stage byte-to-announce vocabulary;
  superseded by `spec/event-and-output-framework.md`).
- `STAGE-7-ISSUES.md` (Stage 7 gap inventory).
- `HISTORICAL-CONTEXT-2026-05.md`.
- `docs/rfc/0001-linear-text-substrate.md` (formalised the
  LinearTextStream substrate that's being removed by the
  follow-up code-prune cycle).
- All three `docs/research/` seeds (`MAY-4.md`,
  `Output-paradigms.md`, `emission-paradigms.md`).

ADR 0001 (`docs/adr/0001-substrate-channel-dichotomy.md`) and
the entire `spec/` directory stay live as architectural substrate.

Outgoing cross-references in surviving docs retargeted to the
new archive paths or to live successors (current PROJECT-PLAN,
spec). `docs/CORE-ABSTRACTION-BOUNDARY.md` §7 gained a Cycle 45
update note explaining the substrate shift.

### Added (Cycle 45f follow-up): diagnostic build provenance + detector trace

Diagnostic bundles now emit a `Build:` line in the header
carrying the full `AssemblyInformationalVersion` (including
the `+sha` suffix the release pipeline stamps). The existing
`Version:` line keeps the bare semver for human-readable
contexts; the new `Build:` line gives a paste-back the exact
commit identity so maintainer + Claude can verify which fixes
are in the build being dogfooded. Skipped when the SHA suffix
isn't present (avoids a redundant duplicate line).

`HeuristicPromptDetector` gained two Debug-level trace lines
to make the dirty-flag fix (PR #272) observable in dogfood
logs:
- `dirty-flag set for shell ...` — fires on the
  `false → true` transition (when the previously-emitted
  prompt row stops matching, i.e. the user just typed on the
  prompt). Single log per dirty episode, no 20Hz noise.
- `SUPPRESSED PromptStart for shell ... rowDirtyAccumulated=...`
  — fires when the gate sees a stable prompt match but the
  identity check (`(text, row, dirty)`) decides "not a new
  prompt." Captures the case where a legitimate new prompt is
  being incorrectly dedupe-suppressed; the `rowDirtyAccumulated`
  value reveals whether the dirty bit fired as expected.

Together these tell us, on the next post-screen-fill silence
regression, exactly whether the detector saw the prompt at
all and what the gate decided.

### Fixed (Cycle 45f follow-up): silence after screen-filling output

Maintainer NVDA dogfood on the Cycle 45f build surfaced a real
regression: after running any command that scrolls the entire
screen (`dir`, `tree`, anything large), the NEXT command's
output goes unannounced. Sequence to reproduce:

1. `echo hello` → narrates "hello" ✓
2. `dir` → narrates the output (long) ✓
3. `echo hello` again → input echoes visually, command runs,
   "hello" prints on screen, but NVDA stays silent ✗

Root cause: `HeuristicPromptDetector`'s `(text, rowIdx)` dedup
gate. After a screen-filling output, the prompt lands at the
last row (rowIdx = 29 on a 30-row screen). The detector emits
`PromptStart(text="C:\>", rowIdx=29)`. When the next command
runs, cmd outputs the result, scrolls the screen by one row,
and redraws the prompt on the **same bottom row with the same
text**. The dedup gate compares `(text, rowIdx)` to the last
emission, sees equality, and suppresses the emission —
SessionModel never seals the tuple, the tuple-finalise
announce never fires.

Fix: add a `RowDirtyAfterEmit: bool` flag to the detector
state. Track whether the previously-emitted row went
non-matching between emissions (i.e., the user typed something
on the prompt row, making it dirty). When the next clean
prompt match arrives, emit even if `(text, rowIdx)` matches
the previous emission. The flag clears on each emission so the
no-spurious-re-emit behaviour for idle cursor-blinks is
preserved.

Pre-existing detector tests (cursor-blink suppression,
different-text emit, different-row emit, post-reset re-emit)
all keep passing. Two new tests pin the regression and the
companion "no spurious re-emit on stable idle prompt" case.

### Added (Cycle 45f): per-shell verbosity modes

Two new menu items under `Display`:

- **`Output Verbosity`** (Tuple Final / Line By Line / Off):
  governs how streaming output narrates per shell. `Tuple
  Final` (cmd / PowerShell default) keeps PR #268's behaviour
  — `SessionTuple.OutputText` announces on each tuple seal,
  individual TextSpans stay quiet during the streaming
  window. `Line By Line` announces each `TextSpan` as it seals
  — intended for Claude / `ping -t` / other streaming-heavy
  shells where the user wants mid-stream cues. `Off` suppresses
  every streaming AND tuple-finalise announce; SpeechCursor's
  manual navigation (Next / Previous / Jump To Latest) is the
  only way to hear output.
- **`Prompt Path`** (Suppress / Final Directory Only / Full):
  governs how `PromptStart` markers narrate their payload.
  `Suppress` (default for every shell) returns nothing.
  `Final Directory Only` trims path-like prompts to the last
  directory segment + delimiter (e.g. `"Local>"` from
  `"C:\Users\Kyle\AppData\Local\>"`). `Full` narrates verbatim.

### Three-layer settings model (Cycle 45f)

A new `src/Terminal.Core/ShellPolicy.fs` module carries the
per-shell policy record. The effective policy at any moment
is resolved through three overlay layers:

1. **Compiled defaults** — `ShellPolicy.defaults`; hardcoded
   baseline per shell.
2. **TOML user config** — `[shell.<id>] verbosity = "…"` and
   `prompt_path = "…"` in `config.toml`. Persists across
   restarts. Loaded once at startup; overlayed on Layer 1 via
   `Config.resolveShellPolicy`.
3. **Runtime overrides** — menu picks. Per-shell, ephemeral
   (lost on app restart). Hot-switching cmd → Claude → cmd
   preserves the cmd override; switching to Claude reads
   Claude's own (separate) override / TOML / default.

Defaults match today's behaviour exactly — cmd / PowerShell /
Claude all start at `Tuple Final` + `Suppress`. No regression
on the post-#270 baseline.

The `ShellPolicy` record also reserves seats for the Cycle
45g consolidation refactor (PromptRegex, PromptStabilityMs,
SelectionEnabled mirroring `HeuristicPromptDetector.fs:184`
and `SelectionDetector.fs` shell gate). 45g migrates those
inline match arms to read from the table; 45f keeps them
inert so this PR stays small.

### Added (Cycle 45 follow-up): ready-for-input earcon

A brief 1200Hz × 60ms chime now plays when the shell finishes a
command and is ready for the next input. The chime routes
through the existing `EarconChannel` (WASAPI on a separate
audio stream from NVDA's TTS), so it plays **concurrently with**
— and does **not interrupt** — NVDA's read-back of the command
output. Honours the existing `View → Earcons → Enabled / Muted`
toggle.

Trigger: `Program.fs handlePromptBoundary` emits a new
`SemanticCategory.ReadyForInput` OutputEvent when
`SessionModel.applyAndCapture` returns `finalisedOpt = Some
tuple` (i.e. a real command-finished boundary, not just a
prompt redraw). `EarconProfile` maps the new semantic to a new
`"ready-prompt"` palette entry. Empty payload means
`NvdaChannel` skips its decision, so the chime never produces
double speech.

The earcon is audibly distinct from the existing `"bell-ping"`
(800Hz × 100ms) so users hearing both within a session can
distinguish "shell BEL'd at me" from "shell finished a command."

### Fixed (Cycle 45 follow-up): edit-conflated narration on tuple finalise

Maintainer dogfood (2026-05-12): typing `echo hi`, editing the
command line with arrows / backspace / delete, restoring it to
`echo hi`, then pressing Enter produced a "very complex and
strange result" — inflated 23-character and 48-character
announces that did not match the actual command (`echo hi`,
7 chars) or output (`hi`, 2 chars).

Root cause: cmd's command-line editing reprints suffix bytes
each time the cursor moves over them. Every reprint becomes a
`Print` VtEvent that accumulates into the active `TextSpan` in
`ContentHistory`. When the span seals (Enter → newline),
SpeechCursor's AutoDrive auto-announces the inflated span.
SessionModel's `SessionTuple.CommandText` / `OutputText` were
already correct — they capture from the screen grid, which is
the canonical view of "what cmd thinks is on the line" — but
the SpeechCursor auto-announce path was unaware of them.

Fix:

1. Added `SkipTextSpansInAutoDrive: bool` to
   `SpeechCursor.Parameters` (defaults `true`). When set,
   `onAppend` advances the cursor's `Position` and
   `LastSpokenSeq` for TextSpan entries without firing an
   announce; Manual navigation can still revisit them.
2. `Program.fs handlePromptBoundary` announces
   `SessionTuple.OutputText` on tuple finalise. Authoritative
   source: screen-grid-derived capture at the
   PromptStart-after-completion transition.

Trade-off: streaming output no longer auto-announces line-by-
line until the next tuple seals. That's the right shape for
cmd / PowerShell short commands (which always seal on Enter)
but a regression for long-running streaming workloads
(Claude's thinking text, `ping -t`, etc.). Cycle 45f will
introduce per-shell verbosity modes that toggle streaming
auto-announce back on for streaming-heavy shells.

### Fixed (Cycle 45 follow-up): Right-arrow + Delete nav-echo direction

Maintainer NVDA dogfood on the post-#265 build confirmed that
Backspace announces the deleted character correctly (matches
NVDA's text-editor behaviour), but called out two refinements:

1. **Right arrow** previously announced the cell being moved
   PAST (`Col`). NVDA's text-editor convention is to announce
   the cell now to the right of the cursor after the move
   (`Col + 1`). Fixed.

2. **Delete key** was deliberately skipped in #265 pending
   semantic clarity. The maintainer confirmed the desired
   behaviour matches NVDA's text-editor convention: announce
   the char that will shift left into the cursor's position
   after the delete (`Col + 1`, read BEFORE forwarding to PTY).
   Added.

Backspace, Left, and Home are unchanged; their existing
behaviour already matches NVDA's text-editor convention.

### Deferred — Up / Down arrow recall announce + verbosity defaults

Both concerns share a design space: when does typed-echo
announce, when does cmd-driven content announce, and how does
that interact with shells (cmd's typed echo + history recall,
PowerShell's PSReadLine, Claude's streaming output)?

The Up arrow case is concrete: cmd's response writes the
recalled command line without a trailing newline, so
ContentHistory's active TextSpan never seals and SpeechCursor
never announces. The fix is wiring `ContentHistory.tick`
(idle-seal) into the pump's `handleTick`, but the threshold
choice is a trade-off — aggressive (200ms) double-announces
typed text with NVDA's keyboard echo; slow (1000ms+) delays
the Up arrow announce.

Scoped as a follow-up cycle (likely "Cycle 45f — verbosity
modes") that also addresses the maintainer's
"echo hi → hi → C:\\Users" verbosity concern (suppress
typed-command-echo and next-prompt-line by default with
per-shell config knobs).

### Added (Cycle 45 follow-up): Navigation-key echo for backspace + arrows

Maintainer NVDA dogfood surfaced that Backspace / Left arrow /
Right arrow / Home keys produce NO audible echo in pty-speak,
even though the same keys announce the relevant character via
NVDA's keyboard echo in Notepad and other text controls.

Root cause: in any text-input control (Notepad, web forms,
IDEs), the control fires a UIA `TextSelectionChangedEvent`
when the caret moves, and NVDA reads the character at the new
caret position. pty-speak's `TerminalAutomationPeer` exposes
a Text pattern for review-cursor navigation but does NOT fire
caret-change events when cmd's cursor moves in response to
these keys — so NVDA's keyboard echo has nothing to react to.

Fix: in `TerminalView.OnPreviewKeyDown`, before encoding the
keystroke for the PTY, read the screen cell at the relevant
position and call `Announce` directly with the new
`ActivityIds.navigation` activity id (MostRecent processing
so rapid keystrokes supersede rather than queue).

Per-key mapping:
- **Backspace** — char that will be deleted (cell at
  `(Cursor.Row, Cursor.Col - 1)`). Skipped at column 0.
- **Left arrow** — char the cursor will move onto (same
  cell as Backspace). Skipped at column 0.
- **Right arrow** — char the cursor will move past
  (cell at `(Cursor.Row, Cursor.Col)`). Skipped at the last
  column.
- **Home** — char at `(Cursor.Row, 0)`.

Not handled in this commit (semantics need design):
- **Up / Down** — cmd recalls command history; the screen
  rewrites after cmd responds, and SpeechCursor picks the
  recalled line up via the normal append path. No
  pre-emptive announce.
- **End** — requires scanning for the last non-blank cell;
  unclear semantics for cmd's prompt-line edit buffer.
- **Delete** — behaviour varies by shell.

Read-only helper; doesn't suppress the keystroke. PTY-side
processing continues unaffected.

### Fixed (Cycle 45 Commit 2 follow-up): SpeechCursor history persists across tuple boundaries

Maintainer NVDA dogfood on preview.92 (the Commit 2 build)
surfaced that `Display → Speech Cursor → Next Entry` and
`Previous Entry` always reported "Already at the latest entry"
/ "Already at the first entry" even after several commands.
Root cause: `handlePromptBoundary` reset both `ContentHistory`
and `SpeechCursor` every time a SessionModel tuple finalised
— on the (wrong) assumption that the cursor's scope is "the
active tuple." With that scope, every completed command
emptied the history, leaving the cursor with nothing to
navigate.

Corrected scope: **the shell session.** ContentHistory entries
now accumulate across tuple boundaries; the user can Speech-
Cursor backwards through completed-command output. Reset on
shell-switch (`switchToShell`) still fires — that's a
legitimate fresh-slate boundary. Tuple-archive into
`SessionTuple` is deferred to Cycle 45e (the visual-surface
cycle that needs structured per-tuple history anyway).

One-file change (`src/Terminal.App/Program.fs` — the
`boundaryAction` closure inside `handlePromptBoundary`). The
removed `if tupleFinalised then …` block + accompanying
comment updates.

### Added (Cycle 45 Commit 2): ContentHistory + SpeechCursor wired into the live pipeline

Second commit of the Cycle 45 architectural reset (Commit 1
shipped in PR #262 as additive substrate). This commit wires
the new aural substrate (`ContentHistory`) and its announce-and-
navigate primitive (`SpeechCursor`) into the live reader-loop +
detector pipeline. The existing `StreamPathway` / `LinearTextStream`
machinery is **still present** and runs in parallel; Commit 3
deletes it after the NVDA validation gate.

**What's new**:

- `Terminal.App/Program.fs:startReaderLoop` now feeds
  `ContentHistory.appendFromEvent` for every parser event
  alongside the existing `LinearTextStream.append` and
  `Screen.Apply` calls.
- After each chunk produces new ContentHistory entries, the
  reader's dispatcher action invokes `SpeechCursor.onAppend`
  to drive AutoDrive announces.
- `HeuristicPromptDetector` boundary events (PromptStart /
  CommandStart / OutputStart / CommandFinished) emit
  `ContentHistory.appendMarker` calls of the matching
  `MarkerKind`. SessionModel tuple-finalize triggers a
  `ContentHistory.reset` + `SpeechCursor.reset` so the
  next tuple starts with a clean substrate.
- `SelectionDetector` `SelectionShown` and `SelectionDismissed`
  events emit corresponding ContentHistory markers. The
  `SelectionShown` marker carries the item-list text as
  payload so SpeechCursor's `renderEntry` can announce
  "Selection prompt: Edit, Yes, Always, No." then suspend
  AutoDrive for the duration of the list interaction.
- Shell-switch (`Ctrl+Shift+1/2/3`) resets ContentHistory +
  SpeechCursor alongside the existing `promptDetector` +
  `selectionDetector` resets.
- 4 new menu-only AppCommands under `Display → Speech Cursor →`:
  `Next Entry`, `Previous Entry`, `Jump to Latest`,
  `Toggle Mode (AutoDrive / Manual)`. No keyboard
  accelerators in this cycle.

**Architectural refinement in this commit**:
`SpeechCursor.onAppend`'s signature changed — it no longer
takes an entries list. The caller's invocation is now a "wake
up, history may have changed" signal; SpeechCursor reads
directly from `ContentHistory` and announces every entry with
`Seq > LastSpokenSeq`. This makes the function idempotent w.r.t.
multiple concurrent appenders (reader thread + pump thread)
and eliminates an out-of-order-delivery race where a marker
emitted on the pump thread could land before reader-thread
TextSpans, causing the TextSpans to be skipped via the
LastSpokenSeq gate.

The `SelectionShown` marker's announce-then-suspend ordering
was also fixed: previously the marker silently failed to
announce (suspend was set BEFORE the announce decision).
The fix splits modulation into pre-suspend (SelectionDismissed
clears the bit before announce) and post-suspend
(SelectionShown sets the bit after announce).

**Version-in-log fix**:
`Program.fs:startup`, `Diagnostics.fs:formatDiagnosticBundle`,
`Diagnostics.fs:formatLightweightBundle`, and
`Program.fs:runExtractVersionHeader` now use
`AssemblyInformationalVersionAttribute` (matching
`MainWindow.xaml.cs:29-42`) instead of
`Assembly.GetName().Version`. Future diagnostic bundles
unambiguously report the prerelease identifier (e.g.
"0.0.1-preview.92") rather than the System.Version-truncated
"0.0.1.0" that misled triage on Cycle 41 / 44.

**Tests**:
`tests/Tests.Unit/SpeechCursorTests.fs` rewritten to match the
new `onAppend` signature (entries list removed). The Spinner-
skip test was deleted (will be re-pinned in the cycle that
wires the Spinner-detection coalescer; no public API to inject
a synthetic Spinner entry through the substrate without it).
`HotkeyRegistryTests.fs` `allCommands contains exactly` updated
with the 4 new SpeechCursor commands.

**Out of scope** (Commit 3, after NVDA validation gate):
- DELETE `StreamPathway.fs`, `LinearTextStream.fs`,
  `DisplayPathway.fs`, `TuiPathway.fs`, `PathwaySelector.fs`,
  `Coalescer.fs`, `Channels/IDisplayBuffer.fs`
- REMOVE `[pathway.stream]` config section
- REMOVE corresponding tests
- UPDATE docs (`CORE-ABSTRACTION-BOUNDARY.md`,
  `rfc/0001-linear-text-substrate.md`, `ARCHITECTURE.md`,
  `PANE-MODEL.md`)

### Added (Cycle 43a): Diagnostic chunk extractors + Grep dialog

Closes the long-standing iOS-paste-crash failure mode for triage
workflows. The `Ctrl+Shift+D` diagnostic bundle is comprehensive
but routinely exceeds the paste-back limits of chat clients used
in triage (the Cycle 29b NVDA test produced a multi-megabyte
bundle that crashed the maintainer's iOS chat app on paste).
CLAUDE.md previously codified "request chunks, not full bundles"
but only as instructions to Claude; the app gave the maintainer
no tools to actually produce chunks — every triage required
shell-quoting `findstr` from cmd or `Select-String` from PowerShell,
both of which are friction for keyboard-only / screen-reader
usage. Cycle 43a converts that workflow from "unusable" to "the
primary triage path."

**Surface** — six new menu-only commands under `Diagnostics`:

- `Copy Latest Bundle to Clipboard` — fast lightweight bundle
  (~100 ms; FileLogger active log + config.toml + session log
  summary + linear stream tail + redacted environment;
  no diagnostic-battery run).
- `Grep Diagnostics...` — modal dialog (pattern + case-sensitive
  flag + regex flag + context-lines spinner); regenerates the
  lightweight bundle in-memory and produces a formatted match
  list; clipboard + extract file.
- `Extract → By Recency → Last 50 Log Lines` — tail of the active
  FileLogger log.
- `Extract → By Event Type → Errors and Warnings` — log entries
  with `Semantic=ErrorLine`, `WarningLine`, or `ParserError`.
- `Extract → By Bundle Section → Active Config` — current
  `config.toml` content.
- `Extract → Snapshot → Version Header` — version + OS + .NET +
  PID + active shell (small status snapshot).

All extractors:

- Copy the result to the Windows clipboard via the
  STA-thread + 3 s-timeout pattern (mirrors
  `runCopyHistoryToClipboard`).
- Cap the clipboard payload at **60 KB** (the Cycle 29b
  iOS-paste-crash safety ceiling) and append a
  `[... truncated at <N> bytes; full results in extract file ...]`
  footer if larger.
- Write the **full untruncated** extract to
  `%LOCALAPPDATA%\PtySpeak\extracts\<extractor>-<timestamp>.txt`
  as paste-fallback for clipboard-unfriendly chat clients.
- Announce result size + extract file path via NVDA
  (`ActivityIds.diagnostic`).

**Architecture** — two new pure F# modules in `Terminal.Core`:

- [`src/Terminal.Core/DiagnosticExtracts.fs`](src/Terminal.Core/DiagnosticExtracts.fs):
  `tailLogLines`, `filterLogLinesSince`, `filterLogBySemantic`,
  `slugifyForFilename`, `extractFilePath`, `truncateForClipboard`,
  `formatExtractHeader`, `formatBytesForAnnounce`. No WPF /
  clipboard / logger dependencies.
- [`src/Terminal.Core/DiagnosticGrep.fs`](src/Terminal.Core/DiagnosticGrep.fs):
  `GrepOptions` record + `formatGrep` + `countMatches`. Pure
  function over a string; orchestrator owns side effects.

Orchestration in `Terminal.App/Program.fs` adds a shared
`runExtractorClipboard` helper that every menu item delegates to;
per-command logic reduces to "compute body string, hand to
helper." `Diagnostics.fs` gains a new internal
`formatLightweightBundle` that mirrors `formatDiagnosticBundle`'s
section structure minus the battery + corpus sections; this is
the source `Copy Latest Bundle` copies and `Grep Diagnostics`
searches over.

**Dialog** — new [`src/Views/GrepDialog.xaml`](src/Views/GrepDialog.xaml)
+ code-behind. UIA-labeled controls (`AutomationProperties.Name`
on every focusable element); explicit tab order; Enter activates
OK, Escape activates Cancel; LiveSetting=Assertive validation
message for context-lines range errors.

**Tests** — 22 new facts across
[`tests/Tests.Unit/DiagnosticExtractsTests.fs`](tests/Tests.Unit/DiagnosticExtractsTests.fs)
(slug shape, timestamp parsing, tail behaviour, time-filter,
Semantic-filter, truncation budget, header format) and
[`tests/Tests.Unit/DiagnosticGrepTests.fs`](tests/Tests.Unit/DiagnosticGrepTests.fs)
(empty pattern, regex compile errors, case sensitivity, context
clamping at file edges, summary footer, match marker).
`HotkeyRegistryTests.allCommands contains exactly the documented
commands (PR-O)` updated with the 6 new AppCommand cases.

**Docs** — `CLAUDE.md` "Diagnostic logs — request chunks, not full
bundles" section refreshed: shell-recipe `findstr` /
`Select-String` examples replaced with "ask the maintainer to use
`Diagnostics → Grep diagnostics...`" or
"`Diagnostics → Extract → X`" as the new primary triage path.
Shell recipes retained as fallback for the few cases where the
extractor catalog doesn't cover the specific slice. New NVDA
matrix rows in [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
for the grep dialog + each proof-of-concept extractor.

**Deferred to Cycle 43b** — the remaining ~16 extractors across
the four sub-submenus per the
[`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md) catalog.
Cycle 43a ships one proof-of-concept extractor per sub-submenu
to validate the pattern end-to-end before fan-out.

### Reverted (Cycle 39): Cycle 38c echo-suppression

Cycle 38c (`EchoCorrelator` + `EchoSuppressorProfile`) was solving a
problem the maintainer never reported. On 2026-05-10 the maintainer
said "NVDA reads the entire new chunk including hello along with the
next input line" — I parsed "next input line" as "the line cmd
echoes when you type" (per-character byte echo). The maintainer's
actual report: after `echo hello` produces the "hello" output, cmd
writes the next prompt `C:\path>`, and NVDA reads ALL of it
(output + next prompt) as one blob. EchoSuppressor doesn't address
this — the prompt isn't an echo of typed input.

Cycle 39 removes the misnamed fix:

- DELETED:
  [`src/Terminal.Core/EchoCorrelator.fs`](src/Terminal.Core/EchoCorrelator.fs),
  [`src/Terminal.Core/EchoSuppressorProfile.fs`](src/Terminal.Core/EchoSuppressorProfile.fs),
  [`tests/Tests.Unit/EchoCorrelatorTests.fs`](tests/Tests.Unit/EchoCorrelatorTests.fs),
  [`tests/Tests.Unit/EchoSuppressorProfileTests.fs`](tests/Tests.Unit/EchoSuppressorProfileTests.fs).
- `Terminal.Core.fsproj` + `Tests.Unit.fsproj`: dropped the
  `<Compile Include>` entries.
- `Program.fs`: dropped `EchoCorrelator.create` + profile
  registration; reverted the WriteBytes wrap to a direct
  `host.WriteBytes` call; removed `EchoCorrelator.reset` from
  the shell-switch handler.
- `Program.fs`: `resolveProfilesForShell` defaults all shells
  to `["passthrough", "earcon", "selection"]` (was: cmd /
  PowerShell got `["echo-suppressor", "earcon", "selection"]`).
- `Config.fs`: `defaultsTemplate` per-shell-profiles example
  block updated; "echo-suppressor" mention removed.
- `tests/fixtures/canonical-interactions.toml`: `cmd.echo.plain`
  row's `notes` field flips: "Fixed in Cycle 38c" → "Bug
  2026-05-11; output and next prompt bleed; Cycle 40
  three-panel routing fixes."

**What stays from Cycle 38b**: the per-shell route table itself
(`[shell.<key>] profiles = [...]` TOML parsing,
`Config.resolveShellProfiles`, `resolveProfilesForShell` in
`Program.fs`, `setActiveProfileSet` re-call on shell-switch).
This infrastructure is still useful — future shell-specific
profiles plug in via TOML without re-introducing 38b's surface.

### Added (Cycle 38b + 38c): Per-shell route table + cmd/PowerShell echo-suppression

Closes the maintainer's 2026-05-10 dogfood regression: `echo hello`
in cmd no longer causes NVDA to read the typed-input echo prefix.
Two related changes ship as one PR per maintainer's "stable
functional position" directive.

**Cycle 38b — Per-shell profile-set route table** (~150 LOC):

- [`src/Terminal.Core/Config.fs`](src/Terminal.Core/Config.fs)
  extends `ShellPathwayConfig` with `Profiles: string[] option`
  and grows `parseShellOverrides` to read
  `[shell.<key>] profiles = [...]` alongside the existing
  `pathway` field. New `tryGetStringArray` helper. New
  `resolveShellProfiles` resolver.
- [`src/Terminal.App/Program.fs`](src/Terminal.App/Program.fs)
  adds `resolveProfilesForShell : ShellId -> Profile list`
  (defined after `chosenShell` is known) and calls
  `OutputDispatcher.ProfileRegistry.setActiveProfileSet` at
  startup AND on every shell-switch (alongside the existing
  detector resets). The setActiveProfileSet surface was planned
  at `OutputDispatcher.fs:80-83` ("Stage 8f wires this") but
  wasn't wired until now.
- Built-in defaults when TOML doesn't override:
  cmd / powershell → `["echo-suppressor", "earcon", "selection"]`
  claude → `["passthrough", "earcon", "selection"]`
- Unknown profile IDs in TOML are logged + dropped (warning, not
  fatal) so a typo doesn't crash startup.
- `defaultsTemplate` in Config.fs gains a commented example block
  documenting the override syntax.

**Cycle 38c — Echo-suppression for cmd / PowerShell** (~300 LOC):

- New module
  [`src/Terminal.Core/EchoCorrelator.fs`](src/Terminal.Core/EchoCorrelator.fs)
  holds a bounded, time-bounded buffer of bytes written to
  `ConPtyHost.WriteBytes`. `matchAndConsumeEchoPrefix` returns
  how many leading payload bytes match the recent input, with
  CR→CRLF normalisation for cmd's echo behaviour. Atomic
  match-then-consume so an input byte echoes exactly once.
  Lock-per-instance for thread safety (WriteBytes runs on UI
  thread; matching runs on dispatcher pump thread).
- New module
  [`src/Terminal.Core/EchoSuppressorProfile.fs`](src/Terminal.Core/EchoSuppressorProfile.fs)
  registers as `ProfileId = "echo-suppressor"`. For StreamChunk
  events: consults the shared correlator; strips matched prefix
  from the NVDA payload; emits FileLogger with the FULL original
  (or `(suppressed echo: ...)` annotation when fully echoed).
  For non-StreamChunk events: behaves identically to
  PassThroughProfile.
- `Program.fs` wires:
  - One `EchoCorrelator` instance at composition root.
  - One `EchoSuppressorProfile` instance, registered with
    `ProfileRegistry`.
  - `ConPtyHost.WriteBytes` is wrapped to feed the correlator
    BEFORE the PTY write (so a fast shell can't echo before we
    tracked the typed bytes).
  - `EchoCorrelator.reset` is called on shell-switch alongside
    the existing prompt / selection detector resets.

**Tests** (~250 LOC):

- 9 facts in
  [`tests/Tests.Unit/EchoCorrelatorTests.fs`](tests/Tests.Unit/EchoCorrelatorTests.fs)
  pin: empty-buffer baseline; exact-match prefix; payload
  divergence; CR-LF normalisation; match-then-consume invariant;
  partial-match leaves remainder; age-based expiry; buffer
  overflow eviction; reset.
- 7 facts in
  [`tests/Tests.Unit/EchoSuppressorProfileTests.fs`](tests/Tests.Unit/EchoSuppressorProfileTests.fs)
  pin: full-echo NVDA-drop; partial-echo strip; no-match
  pass-through; payload divergence pass-through; non-StreamChunk
  pass-through; module identity.
- 5 new facts in
  [`tests/Tests.Unit/ConfigTests.fs`](tests/Tests.Unit/ConfigTests.fs)
  pin: profiles array parse; absent profiles returns None;
  pathway + profiles together; profiles without pathway; absent
  shell section.

**Corpus update**:
[`tests/fixtures/canonical-interactions.toml`](tests/fixtures/canonical-interactions.toml)
`cmd.echo.plain` row's `notes` field flips FAIL→PASS: bug-tag
becomes "Fixed in Cycle 38c." `expected_payload_regex` field
populated (parsed today but enforced from Cycle 38d).

**What this PR does NOT do**:
- No three-pane channel routing (input vs current_output vs
  history pane). Echo is SUPPRESSED in 38c, not ROUTED. Deferred
  to Cycle 38d.
- No `expected_payload_regex` enforcement in `runCorpus`. Still
  parsed-only; 38d wires it.
- No PowerShell / Claude scenarios in the corpus. 38e adds those.
- No NVDA verbatim-text matching. Same as above.
- No prompt-stripping (cmd's next prompt `C:\>` still flows
  through). Separate concern (heuristic prompt detector).
- No timing-parameter exposure via TOML. EchoCorrelator
  parameters are hardcoded; future cycle exposes via
  `[profile.echo-suppressor]` if dogfood reveals timing issues.

### Changed (Cycle 38a-followup): Post-dogfood UX refresh

Five UX/ergonomics fixes from the 2026-05-10 dogfood session of
Cycle 38a:

1. **Truncation suffix references an existing shortcut.**
   [`src/Terminal.Core/StreamPathway.fs:346`](src/Terminal.Core/StreamPathway.fs)
   previously told the user to press `Ctrl+Shift+;` to copy the
   full log — but that hotkey was retired in Cycle 25b-1a.
   Replaced with `Ctrl+Shift+D` (the diagnostic bundle carries
   the untruncated payload in its `--- FILELOGGER ACTIVE LOG ---`
   section).

2. **Spoken diagnostic summary now includes corpus results.**
   The `Ctrl+Shift+D` end-of-battery announce previously said
   only `"Diagnostic complete. {pass} of {total} passed.{substrate fragment}"`.
   Corpus results (Cycle 38a) were in the bundle but not spoken.
   Now the announce includes `"Corpus: {p} of {t} passed; failing: {ids}."`
   when scenarios fail, or `"Corpus: all {N} passed."` when none
   do, or no addition when no scenarios match the active shell.
   The maintainer hears which canonical-interaction rows
   misbehaved without opening the file.

3. **New `Diagnostics → Open Manual Tests` menu item.**
   Filters
   [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
   to sections marked with an `<!-- DOGFOOD -->` HTML comment,
   renders Markdig HTML wrapped in an HTML5 document with a
   `<main>` landmark, writes to
   `%LOCALAPPDATA%\PtySpeak\manual-tests.html`, and opens it in
   the default browser via `ShellExecute`. NVDA browse-mode H
   key jumps section headings; D key jumps the `<main>`
   landmark. Maintainer can grow / prune the quickref by adding
   / removing markers in the source markdown — no code change
   required. Initial markers cover Cycle 38a / 37 / 36 / 29b /
   28 sections plus the always-run "Artifact integrity" and
   "Launch and process hygiene" matrices.

   - New module:
     [`src/Terminal.Core/ManualTestsHtml.fs`](src/Terminal.Core/ManualTestsHtml.fs)
     (pure `filterAndConvert : markdown -> html`).
   - New dependency: Markdig 0.38.0 (BSD 2-Clause).
   - New menu-only AppCommand:
     `HotkeyRegistry.OpenManualTests` (mirrors
     `RunProcessCleanupScript`'s Cycle 26c pattern: `Key = None,
     Modifiers = None`).
   - Handler: `openManualTests` in
     [`src/Terminal.App/Program.fs`](src/Terminal.App/Program.fs).
   - Tests: 8 new facts in
     [`tests/Tests.Unit/ManualTestsHtmlTests.fs`](tests/Tests.Unit/ManualTestsHtmlTests.fs)
     pinning the filter + Markdig conversion + HTML wrapper
     shape; 1 new fact in
     [`tests/Tests.Unit/HotkeyRegistryTests.fs`](tests/Tests.Unit/HotkeyRegistryTests.fs)
     pinning the menu-only shape.

4. **Top-level "View" menu renamed to "Display".** Matches
   maintainer's mental model;
   [`src/Views/MainWindow.xaml`](src/Views/MainWindow.xaml)
   line 66.

5. **"Logging Level" multi-state item moved to Diagnostics.**
   It's a diagnostic verbosity control, not a display setting.
   Now first in the Diagnostics menu (so it's reachable without
   arrow-scrolling); the `MultiStateRegistry` registration
   itself is unchanged. Display now hosts only the Earcons
   multi-state.

### Added (Cycle 38a): Canonical interaction-pair corpus + diagnostic battery integration

Regression scaffolding for the cmd / PowerShell / Claude per-shell
parser-route refactors landing in Cycles 38b-e. The 35b regression
went undetected for ~3 cycles (PR #249 hotfix); this cycle introduces
a curated `(bytes-in, expected-NVDA-out)` corpus that the
`Ctrl+Shift+D` battery loads and runs against the active shell so
future substrate flips can't silently break announcement.

**New module**:
[`src/Terminal.Core/CanonicalCorpus.fs`](src/Terminal.Core/CanonicalCorpus.fs)
defines the `Scenario` record + TOML parser. The schema is
**tiered**: required v1 fields (id / shell / description / command /
must_include / must_not_include / quiescence_ms / timeout_ms)
match `Diagnostics.DiagnosticTest`'s shape exactly so the runner
reuses `runOneTest`'s capture pipeline. Optional v2 extension
fields (`setup_command`, `expected_payload_regex`,
`expected_session_tuple`, `expected_pane_routing`, `notes`) are
parsed but not yet enforced — subsequent sub-cycles promote each
to load-bearing.

**Seed corpus**:
[`tests/fixtures/canonical-interactions.toml`](tests/fixtures/canonical-interactions.toml)
ships 12 cmd scenarios drawn from real workflows + the existing
Cycle 36 matrix:

- `cmd.echo.plain`, `cmd.dir.simple`, `cmd.dir.large`
- `cmd.exit.success`, `cmd.exit.failure`
- `cmd.set.userprompt` (Read-Host equivalent)
- `cmd.choice.yn` (selection prompt — currently fails because
  `SelectionDetector` is shellKey-gated to claude; tagged for 38e)
- `cmd.cls.alt`, `cmd.echo.color.benign`, `cmd.echo.color.error`
- `cmd.multiline.continuation`, `cmd.tab.completion`

Scenarios where current behaviour is wrong carry
`notes = "Bug 2026-05-10: …; fixed in <sub-cycle>"`; the assertion
lists what NVDA CURRENTLY hears so the corpus stays green at HEAD
and the bug-fix sub-cycle has to update both code and assertion
atomically.

**Diagnostic battery integration**:
[`src/Terminal.App/Diagnostics.fs`](src/Terminal.App/Diagnostics.fs)
gains `runOneScenario` (mirrors `runOneTest` but with per-scenario
`QuiescenceMs` / `TimeoutMs` rather than the hard-coded 200/1500),
`runCorpus` (filters scenarios for active shell, runs each), and
extends `formatDiagnosticBundle` with a new
`--- CANONICAL CORPUS RESULTS ---` section. The corpus TOML is
deployed next to `Terminal.App.exe` via Content + CopyToOutput in
[`src/Terminal.App/Terminal.App.fsproj`](src/Terminal.App/Terminal.App.fsproj);
runtime resolves it via `AppContext.BaseDirectory`. Failure to
load the corpus (missing file, parse error) is non-fatal — the
bundle just renders an explanatory section.

**Tests**:
[`tests/Tests.Unit/CanonicalCorpusTests.fs`](tests/Tests.Unit/CanonicalCorpusTests.fs)
adds 12 facts pinning required-field round-trip, optional-field
round-trip, missing-required-field error, unknown
`SemanticCategory` error, unknown `expected_pane_routing` error,
empty document, multiple-scenarios ordering, default
`quiescence_ms` / `timeout_ms`, `formatScenarioResult` shape,
`formatCorpusResultsForBundle` summary, empty-results summary.

**Docs**:
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
gains a `### Cycle 38a — Canonical interaction corpus baseline`
section with schema reference, the sub-cycle promotion table, a
5-row manual matrix for the dogfood loop, and the stopping gate.

**What 38a does NOT do**: per-shell route table (38b);
echo-suppression for cmd (38c); three-pane channel routing (38d);
PowerShell or Claude scenarios (38b/e); NVDA verbatim-text matching
(parsed but not yet evaluated by `runCorpus`); CI gating on corpus
results (post-38e once the corpus is comprehensive enough that
random regressions are noise rather than design).

### Fixed (Hotfix): LinearTextStream bare-CR vs CR-LF disambiguation regression

Pre-existing 34a-era bug surfaced after Cycle 35b's `substrate_mode = "auto"`
default flip routed Linear-substrate output for non-alt-screen frames.
[`src/Terminal.Core/LinearTextStream.fs`](src/Terminal.Core/LinearTextStream.fs)
`classifyLiveRegion` returned `EnterTailMask` for `\r` unconditionally,
but the deferred resolution promised by the `previousEvent` parameter
was never implemented in `append`. Result: every cmd / PowerShell output
line (which all use `\r\n`) had its content moved to `TailMask` on the
`\r` and never flushed (cmd doesn't emit OSC 133 sealed seams). The
linear stream's `Committed` buffer only ever held bare `\n` bytes;
`assembleSuffixFromStream` decoded these to whitespace the sanitiser
stripped; NVDA heard nothing on cmd / PowerShell / Claude across all
output. Confirmed via `Ctrl+Shift+D` diagnostic battery: `T1.Plain`
echo test reported `EVENTS (0)` + `FAIL — missing=StreamChunk`; the
linear-stream sibling file in the bundle was empty.

Fix: defer the tail-mask transition on `\r`. New `PendingCRDeferred`
flag on the producer's State; set when CR arrives. The next event
resolves the ambiguity:

- **CR followed by LF** → CRLF newline, no tail-mask. Clear the flag;
  the LF handler runs normally (adds to Pending, marks
  `encounteredLF`, advances `CurrentRow`).
- **CR followed by anything else** (Print, CSI, etc.) → bare CR,
  overwrite-in-place. Move `Pending` → `TailMask[currentRow]` BEFORE
  processing the new event.

The flag persists across chunks: a chunk ending with `\r` keeps the
flag set so the FIRST event of the next chunk resolves correctly.
`tick` also resolves a deferred CR after `IdleQuantumMs` elapses
(the spinner-pause case) so bare-CR live-region updates still fire
when the producer is idle.

After the fix, cmd / PowerShell output flows through Linear correctly
and NVDA announces resume across all shells without the
`substrate_mode = "screen-diff"` workaround.

Tests: 6 new facts in
[`tests/Tests.Unit/LinearTextStreamTests.fs`](tests/Tests.Unit/LinearTextStreamTests.fs)
covering CRLF in same chunk, bare CR + tail-mask transition,
chunk-spanning CR + LF (CRLF resolves), chunk-spanning CR + Print
(bare CR resolves), multiple CRLF lines (the cmd output regression
case), and empty chunk after deferred CR (flag persists).

### Changed (Cycle 37b): UIA listbox peer ships; NVDA reads Claude tool-use prompts as ControlType.List

Closes the Stage 8e-B canonical-display arc (interactive-list
exemplar). Adds `TerminalListAutomationPeer` and
`TerminalListItemAutomationPeer` virtual UIA peers in
[`Terminal.Accessibility`](src/Terminal.Accessibility/TerminalAutomationPeer.fs);
both derive directly from `System.Windows.Automation.Peers.AutomationPeer`
(no FrameworkElement backing — virtual peers materialized only
while a selection prompt is active). `TerminalListAutomationPeer`
implements `ISelectionProvider` (CanSelectMultiple=false,
IsSelectionRequired=true); `TerminalListItemAutomationPeer`
implements `ISelectionItemProvider` + `IInvokeProvider` per
`docs/CANONICAL-DISPLAY-CATALOG.md` §2.14 (ConfirmationPrompt
hybrid). NVDA in focus mode hears "list with 4 items, Edit, 1 of
4"; arrow-down announces "Yes, 2 of 4" via
`SelectionItemPatternIdentifiers.ElementSelectedEvent`;
`IInvokeProvider.Invoke()` writes `\r` (0x0D) to the PTY for
single-key activation.

`TerminalAutomationPeer` (the document peer) is extended to:

- Take a third constructor parameter `writePtyBytes:
  Action<byte[]>` (passed by the View's `OnCreateAutomationPeer`)
  that threads the PTY-write callback to `TerminalListItemAutomationPeer`'s
  Invoke handler.
- Override `IsContentElementCore` to return `false` while a list
  peer is active — the §8.5 dedup mechanic that prevents NVDA
  from reading the list rows as both document text and listbox
  items. Per the 2026-05-10 design choice: full-document form
  (simpler; trade-off accepted: NVDA reading-cursor history is
  suspended while a prompt is active).
- Override `GetChildrenCore` to expose the active list peer as
  the sole child while one is materialized.
- Add `UpdateSelectionState(payload: SelectionRawPayload)` —
  called from the View's `AnnounceRawPayload` cutover (replacing
  the 37a log-only stub) on the WPF UI thread; transitions
  through "shown" → "item" → "dismissed" with appropriate
  `RaiseAutomationEvent(StructureChanged)` calls.

[`SelectionProfile`](src/Terminal.Core/SelectionProfile.fs) drops
the duplicate NVDA `RenderText` decision retained for the 37a
bridge — keeping it would cause double-announce (text + listbox).
The FileLogger `RenderText` decision stays as the audit trail.
Selection events now emit 2 ChannelDecisions: FileLogger
RenderText + NVDA RenderRaw.

[`TerminalView.AnnounceRawPayload`](src/Views/TerminalView.cs)
cutover: replaces the 37a stub with peer-state delegation.
Adds public `WritePtyBytes(byte[])` that bridges the peer's
IInvokeProvider callback to the View's private `_writeBytes`
field.

Tests: 14 new `TerminalListAutomationPeerTests` facts (direct
F# instantiation + interface inspection; no FlaUI / WPF runtime
dependency — uses a minimal `TestStubAutomationPeer` for the
parent peer). 4 updated `SelectionProfileTests` facts (3-decision
shape → 2-decision shape; index shifts 0↔1↔2).

**Manual NVDA validation gate**: the 7-row matrix in
[`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
`### Cycle 37` is the load-bearing acceptance gate per
[`CONTRIBUTING.md`](CONTRIBUTING.md) "non-negotiable accessibility
rules". PR cannot squash-merge until maintainer confirms all
rows pass on real NVDA.

After 37b ships, the canonical-display exemplar count goes from
1 → 2 (raw text + interactive list). Cycle 38 picks up the
third (form-with-text-input).

### Changed (Cycle 37a): SelectionProfile emits RenderRaw substrate alongside RenderText

Opens the Stage 8e-B canonical-display arc (interactive-list UIA
listbox peer). `SelectionProfile.Apply` now emits a third
`ChannelDecision` per Selection event: an NVDA-channel
`RenderRaw` decision carrying a new
[`SelectionRawPayload`](src/Terminal.Core/OutputEventTypes.fs)
record (UIA-free snapshot of the selection state — discriminator,
item count, indices, item text array). The pre-existing NVDA
`RenderText` decision is kept for the bridge interval between
37a and 37b — NVDA continues to read the rendered text as it has
since Cycle 29b. Cycle 37b promotes the substrate to a
`Terminal.Accessibility` UIA peer (drops the duplicate NVDA
`RenderText` decision; the FileLogger `RenderText` decision
stays as the audit trail).

[`Terminal.Core.NvdaChannel`](src/Terminal.Core/NvdaChannel.fs)
gains a second `marshalRawPayload` callback parameter alongside
the existing `marshalAnnounce`. The composition root in
[`Program.fs`](src/Terminal.App/Program.fs) wires it via the WPF
dispatcher to a new
[`TerminalView.AnnounceRawPayload`](src/Views/TerminalView.cs)
method (Cycle 37a stub: log-only; Cycle 37b: peer-state update +
UIA event raise).

No behaviour change visible to NVDA users in 37a — the
`RenderRaw` decisions feed the View stub which logs and
no-ops. Sets up Cycle 37b's peer types
(`TerminalListAutomationPeer` + `TerminalListItemAutomationPeer`)
to consume the substrate without further substrate churn.

Tests: 6 new `SelectionProfileTests` facts (3 cover the
3-decision shape per event; 3 cover `SelectionRawPayload`
field invariants for `Kind` ∈ {"shown", "item", "dismissed"});
1 updated `NvdaChannelTests` fact (`RenderRaw` now routes to
the second marshal callback rather than skipping silently);
14 mechanical `NvdaChannel.create` call-site updates to thread
the new parameter (no behaviour change to those tests).

### Documented (Cycle 36): substrate-inversion arc validation matrix backfilled into ACCESSIBILITY-TESTING.md

Closes the substrate-inversion arc's documentation work. Adds a
new `### Cycle 36 — Substrate-inversion arc matrix backfill`
section to [`docs/ACCESSIBILITY-TESTING.md`](docs/ACCESSIBILITY-TESTING.md)
codifying the 8-row advanced-CMD validation matrix the maintainer
ran on 2026-05-10 against Cycle 35a's parallel-path-behind-flag
PR. The section captures per-row procedures + expected NVDA
shapes, plus FileLogger grep recipes for post-merge dogfood
verification of the Cycle 35b SessionModel hybrid fallback and
the Cycle 35c spinner-gate-bypass contract.

The matrix is the Cycle 39 stopping gate — all 8 rows green at
default `substrate_mode = auto` plus the FileLogger telemetry
recipes showing the expected absence/presence patterns is the
precondition for safely removing the screen-diff legacy code
(per Section 13 of the strategic plan +
[`docs/STAGE-7-ISSUES.md`](docs/STAGE-7-ISSUES.md) substrate-
cleanup section).

No code changes; no behaviour change. Pure docs-only PR.

### Changed (Cycle 35c): Path B Levenshtein spinner gate scoped to ScreenDiff path only

Closes the substrate-inversion arc (Cycles 33-35). The
`isSpinnerSuppressed` gate (Cycle 11+ Levenshtein heuristic
on row-hash recurrence) now only fires when `resolveSubstrateMode`
returns `ScreenDiff`. On the Linear path, the gate is bypassed
entirely — RFC 0001 §5.3 #3 tail-mask classifier already
collapses spinner-class output (bare `\r`, `ESC[K` transition
the current row to LATEST semantics), making the gate redundant.

**Why scope rather than delete:** the gate stays compiled in
because alt-screen TUIs and explicit `substrate_mode = "screen-diff"`
opt-in still use the screen-diff substrate. Full deletion lives
in the Cycle 39 cleanup once the screen-diff path itself is
removable (per Section 13 of the strategic plan).

**Why this matters:** the previous gating cost was small in
the typical case but masked a real bug surface. A user's
command that genuinely produces N rapid identical announces
(e.g., a tool intentionally printing a heartbeat line every
200ms) would get suppressed by the gate on the Linear path
even though the user wants to hear each beat. Linear's
tail-mask only suppresses overwrite-class bytes (`\r`-without-
`\n`); append-class content flows through.

- **`src/Terminal.Core/StreamPathway.fs:processCanonicalState`**
  — hoist `resolveSubstrateMode` call BEFORE the spinner-suppress
  check. Gate the `isSpinnerSuppressed` invocation on
  `resolvedMode`: `if resolvedMode = Linear then false else
  isSpinnerSuppressed parameters state now rowHashes contentHashes`.
  The duplicate `resolveSubstrateMode` call previously inside
  the leading-edge dispatch (line 817-820) is removed; the
  hoisted value is reused. ~10 LOC.
- **`tests/Tests.Unit/StreamPathwayTests.fs`** — 2 new facts:
  - `Cycle 35c — SubstrateMode=Linear bypasses isSpinnerSuppressed
    gate` — drives the alternating-frame sequence with
    `SubstrateMode = Linear` and asserts `state.PerRowHistory`
    + `state.HashHistory` remain empty (the gate's state
    machine was never invoked).
  - `Cycle 35c — SubstrateMode=ScreenDiff still gates spinners
    (regression)` — sentinel: same alternating-frame sequence
    with explicit `SubstrateMode = ScreenDiff`, asserts ≥1
    suppression fires AND `state.PerRowHistory` is populated.
- **`onTimerTick`**: unchanged — the trailing-edge dispatch
  has no `isSpinnerSuppressed` call (the gate is leading-edge
  only).
- Existing 4 spinner-suppression facts (per-key, cross-row,
  static-rows, GC-after-window) stay green via the
  `legacyDefaultParameters` test wrapper which forces
  `SubstrateMode = ScreenDiff`.

**Substrate-inversion arc complete** with 35c. The Cycle 33-35
sequence delivered:
- Cycle 33: RFC + canonical-display catalog (doc).
- Cycle 34a/b: `LinearTextStream` producer module + composition wiring + diagnostic bundle integration.
- Cycle 35a: parallel linear-substrate path behind default-off TOML flag.
- Cycle 35b: default flip to `Auto` + SessionModel hybrid cutover (linear path for OSC-133 shells; `extractContent` fallback for vanilla cmd / PowerShell).
- Cycle 35c: Levenshtein spinner gate scoped to ScreenDiff fallback only.

Cycle 36 (doc-only matrix backfill into ACCESSIBILITY-TESTING.md)
is the only remaining substrate-arc work; future Cycle 39
removes the screen-diff legacy when preconditions are met
(per Section 13 of `/root/.claude/plans/we-do-not-need-fluffy-simon.md`).

### Changed (Cycle 35b): default `[pathway.stream] substrate_mode` flipped to `auto` + SessionModel hybrid cutover

The headline behavioural change of the substrate-inversion arc.
After the maintainer manually validated the §3 advanced-CMD
content matrix in both Linear and Auto modes against cmd,
PowerShell, and Claude on 2026-05-10 — all 8 rows passed —
Cycle 35b flips the default `SubstrateMode` from `ScreenDiff`
to `Auto` so users get linear-substrate announces for non-alt-
screen content + screen-diff for alt-screen TUIs without any
TOML opt-in.

`SessionModel.applyAndCapture` (the SessionTuple finalize path
that feeds `Ctrl+Shift+Y` and the in-memory History queue)
also gains a substrate-aware variant
`applyAndCaptureWithSubstrate` that routes through
`LinearTextStream.finalizeHighWaterMark` when OSC 133 markers
are present, falling back to the legacy `extractContent` row-
walk for OSC-133-less shells (vanilla cmd, vanilla PowerShell)
so those users keep working.

The legacy `apply` / `applyAndCapture` API is preserved with
original signatures for the 80+ existing test callers and any
external consumer that doesn't have a `LinearTextStream` in
scope.

**Regression escape hatch.** If you hit a Linear-mode
regression, set `[pathway.stream] substrate_mode = "screen-
diff"` in `%LOCALAPPDATA%\PtySpeak\config.toml` and restart
pty-speak. See `docs/USER-SETTINGS.md` "Substrate mode" for
the full TOML schema.

- **`src/Terminal.Core/StreamPathway.fs:194`** — flipped
  `defaultParameters.SubstrateMode` from `ScreenDiff` to
  `Auto`.
- **`src/Terminal.Core/LinearTextStream.fs`** — added
  per-tuple `Osc133MarkersSetThisTuple: bool` flag set on
  every OSC 133 event (PromptStart / CommandInputStart /
  CommandOutputStart / CommandFinished) and reset in
  `finalizeHighWaterMark`. New public accessor
  `hasOsc133Markers: T -> bool` that SessionModel uses to
  pick between the linear finalize and the extractContent
  fallback.
- **`src/Terminal.Core/SessionModel.fs`** — added the new
  public surface:
  - `applyWithSubstrate` and `applyAndCaptureWithSubstrate`
    take `linearStream: LinearTextStream.T` + `useLinear:
    bool` parameters.
  - The shared body factored out as private
    `applyAndCaptureCore` that takes a `linearOverride:
    (string * string) option`. Legacy `applyAndCapture`
    passes `None`; substrate-aware `applyAndCaptureWithSubstrate`
    constructs `Some (cmd, out)` from
    `LinearTextStream.finalizeHighWaterMark` when
    `useLinear` resolves AND OSC 133 markers are present.
  - `finalizeAndEnqueue` signature gained the
    `linearOverride: (string * string) option` parameter;
    body matches on it before calling `extractContent`.
- **`src/Terminal.App/Program.fs:~1280-1340`** — composition
  cutover: `handlePromptBoundary` now resolves the
  `useLinear` boolean from the Config-resolved
  `SubstrateMode` against `screen.Modes.AltScreen` (mirror
  of `StreamPathway.resolveSubstrateMode`) and calls
  `SessionModel.applyAndCaptureWithSubstrate`. The resolved
  StreamPathway parameters are hoisted to a per-session
  `let` so the resolution doesn't re-run per boundary.
- **`tests/Tests.Unit/StreamPathwayTests.fs`** — test
  wrappers (`processCanonicalState`, `onTimerTick`,
  `create`, `createWithExposedState`) now apply a
  `legacyAware` override that forces `SubstrateMode =
  ScreenDiff` so the 80+ pre-substrate-inversion facts keep
  their screen-content-payload assertions unchanged. The
  Cycle 35a fact "SubstrateMode=ScreenDiff (default)" was
  renamed + updated to construct ScreenDiff explicitly
  (no longer the default).
- **`tests/Tests.Unit/ConfigTests.fs`** — the four "absent"
  / "unknown" / "non-string" facts updated to assert `Auto`
  as the new fallback default. The three explicit-string
  mappings (`"linear"`, `"screen-diff"`, `"auto"`) unchanged.
- **`tests/Tests.Unit/LinearTextStreamTests.fs`** — 4 new
  facts pinning `hasOsc133Markers` (false on fresh stream;
  true after PromptStart; false for plain bytes; resets to
  false after `finalizeHighWaterMark`).
- **`tests/Tests.Unit/SessionModelTests.fs`** — 6 new facts
  pin the substrate-aware path:
  - `useLinear = false` bypasses the linear stream entirely.
  - `useLinear = true` + OSC 133 markers populates
    CommandText/OutputText from the stream.
  - `useLinear = true` + no markers falls back to
    `extractContent`.
  - `useLinear = false` ignores OSC 133 markers (linear
    stream has them).
  - Non-finalize boundaries (PromptStart / CommandStart /
    OutputStart) preserve Active-state semantics.
  - Legacy `apply` / `applyAndCapture` regression check
    confirms the original API still works.
- **`docs/USER-SETTINGS.md`** — new "Substrate mode (Cycle
  35a/35b)" section after the existing `[pathway.stream]`
  parameters table. Documents the three values, the new
  default, when to override, and the hybrid finalize
  semantics.
- **`docs/STAGE-7-ISSUES.md`** — new "Open tech-debt items"
  section with the `[substrate-cleanup]` entry referencing
  Section 13 of the strategic plan and the in-code
  `TODO(Cycle 39)` comments.

**Tech-debt removal sketch (Cycle 39).** The hybrid is the
intermediate state. Section 13 of
`/root/.claude/plans/we-do-not-need-fluffy-simon.md` sketches
the eventual cleanup cycle that removes
`SessionModel.extractContent` + the `linearOverride` `None`
branch + the screen-diff `assembleSuffixPayload` legacy. The
preconditions for Cycle 39 are (a) broad OSC 133 coverage
(likely an OSC-133-injecting shim cycle for vanilla shells)
AND (b) ≥4 weeks of dogfood with the hybrid in production
without Linear-mode regressions reported. The cleanup is
deferred to preserve the regression-rollback escape hatch
during the dogfood window.

### Added (Cycle 35a): StreamPathway parallel linear-substrate path behind default-off TOML flag

Introduces a parallel announce path in `StreamPathway` that
sources its payload from the Cycle 34 `LinearTextStream`
producer instead of the existing PR #166 screen-row suffix-
diff. Selection is via the new `[pathway.stream] substrate_mode`
TOML key (`"linear" | "screen-diff" | "auto"`); default in
35a is `"screen-diff"` so existing behaviour is preserved
verbatim. Cycle 35b will flip the default to `"auto"` after
the §3 advanced-CMD content matrix passes manual NVDA
validation.

`Auto` routes alt-screen frames through the screen-diff path
(where the grid is canonical per
CORE-ABSTRACTION-BOUNDARY.md §1.4) and non-alt-screen
frames through the linear path (where the byte stream is
canonical per RFC 0001).

**No user-visible behaviour change** unless the user opts
in via `config.toml`. The screen-diff path remains
authoritative for the default install; the new linear path
runs only when explicitly selected.

- **`src/Terminal.Core/StreamPathway.fs`** — new
  `SubstrateMode` DU (`Linear | ScreenDiff | Auto`); new
  `Parameters.SubstrateMode` field defaulting to
  `ScreenDiff`; new `State.LastLinearWatermark: int64`
  tracking the last byte offset emitted via the linear path
  (zeroed in `resetState`); new private helpers
  `assembleSuffixFromStream` (decodes UTF-8 bytes from
  `LinearTextStream.suffixSince`, sanitises via
  `AnnounceSanitiser.sanitise`, applies `MaxAnnounceChars`
  cap) and `resolveSubstrateMode` (collapses `Auto` to
  `Linear` / `ScreenDiff` based on
  `canonical.IsAltScreenActive`); `processCanonicalState` +
  `onTimerTick` signatures gain `linearStream:
  LinearTextStream.T` parameter; both leading-edge and
  trailing-edge dispatch now match on `resolvedMode` to
  pick the payload assembler. Watermark advances ONLY on
  successful emit (suppressed / empty payloads don't lose
  bytes). The Path B Levenshtein gate
  (`isSpinnerSuppressed`) continues to apply to BOTH paths
  in 35a; 35c migrates it to be screen-diff-fallback-only.
- **`src/Terminal.Core/LinearTextStream.fs`** — new public
  `suffixSince: T -> int64 -> byte[]` accessor that returns
  bytes from the committed buffer starting at the supplied
  watermark. Mirrors the existing `getLastBytes` pattern
  (lock state.Gate; defensive bounds clamping). Used by
  `StreamPathway.assembleSuffixFromStream`.
- **`src/Terminal.Core/CanonicalState.fs`** — new
  `Canonical.IsAltScreenActive: bool` field captured
  atomically with the snapshot under the gate lock; new
  parameter on `CanonicalState.create`. Read at snapshot
  time from `Screen.IsAltScreenActive`. Required for
  `Auto` mode dispatch.
- **`src/Terminal.App/Program.fs`** — three
  `CanonicalState.create` call sites (in `PathwayPump`'s
  rows-changed / cursor-changed paths and in the
  composition-root baseline-seeding) updated to pass
  `screen.Modes.AltScreen`. Three `StreamPathway.create`
  call sites (initial pathway construction +
  `createPathway` factory) updated to pass `linearStream`.
- **`src/Terminal.Core/Config.fs`** — new
  `StreamParameterOverrides.SubstrateMode:
  StreamPathway.SubstrateMode option` field; new
  `readSubstrateMode` helper inside `parseStreamOverrides`
  (`"linear"` / `"screen-diff"` / `"auto"`; logs Warning +
  drops on any other value, including non-string types);
  `"substrate_mode"` added to `knownStreamKeys` so unknown-
  key warnings don't mis-fire; new field merge in
  `resolveStreamParameters`.
- **`tests/Tests.Unit/ConfigTests.fs`** — 6 new facts pin
  the parser + resolver: absent → defaults; `"linear"`,
  `"screen-diff"`, `"auto"` map correctly; unknown string
  + non-string both log Warning + fall back to default.
- **`tests/Tests.Unit/StreamPathwayTests.fs`** — 10 new
  facts cover dispatch:
  - SubstrateMode=Linear with populated stream emits
    suffix payload (and the screen-diff content does NOT
    appear).
  - SubstrateMode=Linear with empty stream emits nothing.
  - SubstrateMode=Linear advances LastLinearWatermark by
    emitted byte count.
  - SubstrateMode=Linear emits only post-watermark bytes
    on second call.
  - SubstrateMode=ScreenDiff (default) does NOT consult
    the linear stream.
  - SubstrateMode=Auto with alt-screen routes to
    ScreenDiff.
  - SubstrateMode=Auto without alt-screen routes to
    Linear.
  - Reset zeroes LastLinearWatermark.
  - Linear payload is sanitised (control chars stripped).
  - Linear payload respects `MaxAnnounceChars` cap.
- **`tests/Tests.Unit/CanonicalStateTests.fs`,
  `tests/Tests.Unit/TuiPathwayTests.fs`,
  `tests/Tests.Unit/StreamPathwayTests.fs`** — existing
  `CanonicalState.create` call sites mechanically updated
  to pass the new `isAltScreenActive: bool` parameter
  (`false` for non-alt-screen tests; the new field has
  defaulted-false semantics for the existing test suite).

**Stopping gate:** if the §3 advanced-CMD 8-row matrix
regresses against PR #166 in linear / auto mode, revert
`StreamPathway.defaultParameters.SubstrateMode` to
`ScreenDiff` (already the default in 35a — no revert
needed) and treat as a Linear-mode bug. Side-by-side
migration is the explicit insurance.

### Added (Cycle 34b): producer composition wiring + Ctrl+Shift+D bundle integration + cross-thread locking

Wires the Cycle 34a `LinearTextStream` producer at the parser-
emit edge (`Program.fs:108`), constructs the producer instance
in the composition root (`Program.fs:~731`), adds the
`--- LINEAR STREAM (last 64KB) ---` section to the
`Ctrl+Shift+D` diagnostic bundle, and adds a `lock state.Gate`
guard around the producer's `Committed` buffer access for
cross-thread safety.

**No user-visible behaviour change** for typical sessions —
the producer's emitted `CommitNotification`s are still
discarded (Cycle 35 wires the Stream profile to subscribe).
The only observable effect is the new bundle section
populating with whatever bytes the producer captured during
the session.

- **`src/Terminal.Core/LinearTextStream.fs`** — adds
  `member val internal Gate: obj = obj () with get` to the `T`
  class. Wraps `appendCommittedWithCap`, `getLastBytes`, and
  `finalizeHighWaterMark` in `lock state.Gate (fun () -> ...)`.
  Pattern mirrors `Screen.SnapshotRows` at `Screen.fs:541-553`.
  Without the gate, the diagnostic-bundle's background-task
  call to `getLastBytes` could race with PathwayPump's
  `appendCommittedWithCap` (`ResizeArray.AddRange` /
  `RemoveRange` shifts indices mid-iteration → potential
  `IndexOutOfRangeException`). The 25 producer tests
  continue to pass unchanged (single-threaded test
  assertions don't exercise the race).
- **`src/Terminal.App/Program.fs:~731`** — constructs the
  producer:
  ```fsharp
  let mutable linearStream =
      LinearTextStream.create LinearTextStream.defaultParameters
  ```
- **`src/Terminal.App/Program.fs:108`** (inside
  `startReaderLoop`) — feeds bytes + parser events to the
  producer immediately after `Parser.feedArray`:
  ```fsharp
  let _, _ =
      LinearTextStream.append
          linearStream
          DateTime.UtcNow
          chunk
          events
  ```
  Returned notifications + state are intentionally discarded
  (Cycle 35 wires the Stream profile to consume).
- **`src/Terminal.App/Program.fs:85-93`** — `startReaderLoop`
  signature gains a `linearStream: LinearTextStream.T`
  parameter (8th of 9). Composition-root call site at
  `Program.fs:~2266` passes the new argument.
- **`src/Terminal.App/Diagnostics.fs:~779-790`** — adds
  `resolveLinearStreamPath` mirroring the existing
  `resolveSnapshotPath` (lines 772-777). Sibling file lands
  at `%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\linear-
  stream-<yyyy-MM-dd-HH-mm-ss-fff>.txt` with the same
  timestamp as the bundle for mechanical cross-referencing.
- **`src/Terminal.App/Diagnostics.fs:860-867`** —
  `formatDiagnosticBundle` signature gains a 7th parameter
  `linearStreamSection: string`. The body emits a new
  `--- LINEAR STREAM (last 64KB) ---` section between
  `--- SESSION LOG ---` and `--- ENVIRONMENT ---`.
- **`src/Terminal.App/Diagnostics.fs:958-966`** —
  `runFullBattery` signature gains an 8th resolver parameter
  `resolveLinearStream: unit -> LinearTextStream.T`. Inside
  the task block (~line 1124), the resolver is called; the
  full stream bytes are written to the sibling file
  (defensively, with try/log on failure); the last 64 KB
  are UTF-8-decoded + sanitised via
  `AnnounceSanitiser.sanitise` (strips C0 controls, DEL,
  C1 controls; preserves printable Unicode); the resulting
  inline section text is passed to `formatDiagnosticBundle`.
- **`src/Terminal.App/Program.fs:~2015`** — the
  `runDiagnostic` closure gains an 8th resolver `(fun () ->
  linearStream)` that captures the producer cell at
  hotkey-press time (mirrors the existing resolver pattern
  for hot-switch resilience).

**Cycle 29b iOS-paste-crash prevention:** the bundle's inline
LinearTextStream section is hard-capped at 64 KB. Bundles
stay paste-friendly; full forensic content lives in the
sibling file referenced by the `(source: <path>)` line at
the start of the section.

**Sanitisation rationale:** raw PTY bytes include ANSI
escape sequences, bell characters, etc. Without
sanitisation the bundle would render as a mangled mess in
chat / triage tools. `AnnounceSanitiser.sanitise` (declared
at `AnnounceSanitiser.fs:53-68`) strips control characters
while preserving printable Unicode (including the U+FFFD
replacement char that `Encoding.UTF8.GetString` may produce
for partial multi-byte sequences at the 64 KB boundary).

**Cycle 34 stage complete.** With 34a (producer module +
tests) and 34b (composition wiring + diagnostic
integration), the LinearTextStream substrate is live in
the running app — capturing PTY bytes parallel-to-screen,
emitting CommitNotifications nothing yet consumes, and
exposing a sanitised tail through the diagnostic bundle.
Cycle 35 (Stream profile rebuild on linear substrate) is
the next push; it wires the first consumer for the
producer's CommitNotifications and flips
`SessionModel.applyAndCapture` from `extractContent` to
`LinearTextStream.finalizeHighWaterMark`.

### Added (Cycle 34a): `LinearTextStream` producer module + tests (substrate-only, parallel-to-screen)

First code cycle of the substrate-inversion arc that started
with Cycle 33's RFC. Ships the `LinearTextStream` producer
module per [`docs/rfc/0001-linear-text-substrate.md`](docs/rfc/0001-linear-text-substrate.md)
§3 + §5 + §6, plus 25 test facts pinning the contract.
**No behaviour change** — the producer runs parallel-to-screen
with no consumers; emitted `CommitNotification`s are
constructed and returned by `append`/`tick` calls but no
downstream code subscribes. Cycle 34b wires the producer at
`Program.fs:108` (parser-emit edge) and adds the
`Ctrl+Shift+D` diagnostic-bundle integration; Cycle 35 flips
the Stream profile to consume from the producer.

- **`src/Terminal.Core/LinearTextStream.fs`** (new, ~760 LOC)
  — the producer module. Public API:
  - `Parameters` record (6 cadence fields per RFC §5.2 verbatim)
    + `defaultParameters` value.
  - `CommitNotification` DU (`EmittedChunk` | `LiveRegionUpdate`
    | `RegimeSwitch`) with the `Sealed: bool` extension on
    `EmittedChunk` per RFC §5.4.
  - `T` opaque type wrapping internal `ResizeArray<byte>`
    buffers + tail-mask `Map<int, byte[]>` + watermark/flag
    primitives.
  - `create: Parameters → T` factory.
  - `append: T → DateTime → byte[] → VtEvent[] → CommitNotification list * T`
    — main entrypoint; consumes raw bytes for substrate
    accumulation + parser events for seam/live-region
    decisions per RFC §5.3 ranking.
  - `tick: T → DateTime → CommitNotification list * T` —
    time-driven flush for idle-quantum + max-time + tail-mask
    debounce settling.
  - `finalizeHighWaterMark: T → FinalizedChunk * T` — slices
    Command/OutputText from OSC 133 markers; runtime-unwired
    in Cycle 34a (Cycle 35 cuts over `SessionModel.applyAndCapture`).
  - `checkpointAndFreeze` / `resumeFromFreeze` — drain-checkpoint-
    swap entrypoints per RFC §6 (test-exercised in 34a;
    runtime-wired by PathwayPump in Cycle 35).
  - `getLastBytes: T → int → byte[]` — diagnostic accessor for
    the Cycle 34b `Ctrl+Shift+D` bundle's tail section.
- **`tests/Tests.Unit/LinearTextStreamTests.fs`** (new, 489
  LOC, **25 facts**) — pins the contract per RFC §10
  acceptance criteria:
  - **Seam hierarchy (Facts 01-08):** empty input no-op;
    newline-seam unsealed; OSC 133;C/D sealed + watermark
    advance; idle-quantum + max-bytes + max-time triggers;
    strongest seam pre-empts weaker.
  - **Live-region detection (Facts 09-14):** bare CR + ESC[K
    + CUU+printable + CUB+printable trigger tail-mask;
    spinner cycle collapses to LATEST; LATEST overwrites
    intermediate states.
  - **Drain-checkpoint-swap (Facts 15-17):** alt-screen
    enter/exit emits RegimeSwitch with resumeAt past drain
    settle; checkpointAndFreeze + resumeFromFreeze symmetric.
  - **4 MB cap (Facts 18-19):** small buffers report
    `Truncated=false`; cap reached evicts oldest +
    `Truncated=true` (verified with 8 KB cap to keep test
    allocation bounded).
  - **Sealed/unsealed (Facts 20-21):** OSC 133;D produces
    Sealed=true; newline boundary produces Sealed=false.
  - **State + finalize (Facts 22-23):** chained `append`
    calls accumulate state via mutable record reference;
    `finalizeHighWaterMark` slices CommandText / OutputText
    from OSC 133 offsets.
  - **Defaults + parameters (Facts 24-25):**
    `defaultParameters` matches RFC §5.2 verbatim;
    `create` with custom parameters honors overrides.
- **`src/Terminal.Core/Terminal.Core.fsproj`** — adds
  `<Compile Include="LinearTextStream.fs" />` after
  `Coalescer.fs`. The producer depends only on `VtEvent`
  (Types.fs) and primitive types; no new package references.
- **`tests/Tests.Unit/Tests.Unit.fsproj`** — adds
  `<Compile Include="LinearTextStreamTests.fs" />` after
  `MultiStateRegistryTests.fs`.

**Vocabulary used (project-canonical post-Cycle 33 RFC):**
seam hierarchy, tail mask, drain-checkpoint-swap, sealed /
unsealed events, high-water-mark commit, substrate-of-truth.
See `docs/rfc/0001-linear-text-substrate.md` §11 for full
definitions.

**Adjustments from RFC sketch:**
- The RFC §3.1 sketched `append: T -> byte[] -> CommitNotification list`
  (implying internal mutation). Phase 1 exploration of existing
  detector idioms (`HeuristicPromptDetector.tryDetect`,
  `SelectionDetector.tryDetect`) confirmed the codebase pattern
  is **immutable pass-and-return** with explicit `now: DateTime`.
  The implemented signature is `append: T -> DateTime -> byte[] -> VtEvent[] -> CommitNotification list * T`,
  threading state through call sites. The `T` is implemented
  as a class with `member` accessors holding `ResizeArray<byte>`
  buffers (mutable in-place for O(1) append) — the public API
  is functional pass-and-return, the underlying buffer is
  mutable for performance.
- The RFC §3.3 sketched the producer attaching at the
  Coalescer's input edge. Phase 1 exploration confirmed the
  Coalescer was restructured (PR-N 2026-05-04); the
  attachment point is now `Program.fs:108` (just after
  `Parser.feedArray parser chunk` in `startReaderLoop`).
  Cycle 34b makes this concrete; Cycle 34a's tests construct
  events directly via `Parser.feedArray` to feed the producer
  without composition-root involvement.
- The RFC §10.5 acceptance criterion ("`SessionModel.applyAndCapture`
  calls `LinearTextStream.finalizeHighWaterMark` instead of
  `extractContent`") was overly aggressive — the strategic plan's
  "parallel-to-screen, no consumers" framing is authoritative.
  Cycle 34a/34b preserve `extractContent` runtime-wired;
  Cycle 35 ships the cutover alongside the Stream-profile
  inversion.

**Stopping gate:** producer's seam-hierarchy implementation
must align with RFC §5.1. Test fact 08 ("strongest seam wins")
is the load-bearing assertion. If 08 fails, the priority
ordering in `append`'s event-walk is wrong; pause + revisit
RFC §5.1 before continuing to 34b.

### Docs (Cycle 33): linear-text substrate RFC + canonical-display catalog (pivot gate)

Doc-only pivot-gate cycle locking the substrate-inversion design
before any code lands. **No behaviour change.** Two new authoritative
docs + cross-reference updates in CORE-ABSTRACTION-BOUNDARY.md and
CLAUDE.md so future cycles read against a stable spec.

This cycle is the architectural lock for the next ~8 cycles
(34-38) of substrate-inversion work. Cycle 34 implements the
`LinearTextStream` producer module against the RFC's contract;
Cycles 35-36 invert the Stream profile against it; Cycles 37-38
build interactive list + form-with-text-input on the inverted
substrate. The RFC + catalog supersede the informal streaming-
incomplete protocol in CORE-ABSTRACTION-BOUNDARY.md §7 for
normative spec.

- **`docs/rfc/0001-linear-text-substrate.md`** (new, ~610 LOC) —
  the pivot-gate RFC. 12 sections: Abstract, Motivation, Current
  Extraction Path (`SessionModel.fs:338-375` `extractContent`),
  LinearTextStream Producer Design (module shape + buffer +
  Coalescer hookpoint + 4 MB cap + session-restore-explicitly-
  out-of-scope), Inversion of Cause and Effect, Streaming-
  Incomplete Emission Protocol (seam hierarchy + cadence
  parameters table + ranked live-region detection + sealed/
  unsealed extension; lifted verbatim from
  `docs/research/emission-paradigms.md` §3.A-§3.D and §4 closing
  recommendations), Drain-Checkpoint-Swap Protocol (lifted from
  emission-paradigms.md §3.E), SessionTuple Finalize Contract,
  Three Exemplar Canonical Displays (high level; full specs in
  catalog), Risks + Mitigations (11 items), Acceptance Criteria
  for Cycle 34 (10 testable invariants), Glossary (8 vocabulary
  terms adopted), Cross-references.
- **`docs/CANONICAL-DISPLAY-CATALOG.md`** (new, ~400 LOC) —
  companion catalog. Full per-primitive specs for the three
  exemplars: Raw Text (with CommandOutputTuple wrapper for the
  history sub-pane), Interactive List (with ConfirmationPrompt
  hybrid for assertive-notification cases), Form with Text Input.
  Each exemplar covers UIA control type, required pattern
  providers, ARIA role analog, NVDA reading pattern, JAWS virtual
  cursor behavior, Narrator behavior, interaction contract,
  substrate consumption, update cadence, output channel routing,
  example terminal scenarios. Extension points (SeverityAlert,
  IndeterminateProgress, CommandOutputTuple, Tier-2 deferred,
  Tier-3 deferred) listed with one-paragraph descriptions. Output
  channel routing matrix at the end. Lifted from
  `docs/research/Output-paradigms.md` §1.1, §1.2, §1.3, §1.6 with
  attribution; CORE-ABSTRACTION-BOUNDARY.md §5 cited as the
  three-exemplar framing authority.
- **`docs/CORE-ABSTRACTION-BOUNDARY.md`** §7 — streaming-incomplete
  protocol summary updated with vocabulary adopted in the RFC
  (tail mask, drain-checkpoint-swap, seam hierarchy, sealed/
  unsealed). Cross-references the RFC for normative spec; this
  section becomes a one-paragraph summary going forward.
- **`docs/CORE-ABSTRACTION-BOUNDARY.md`** §10 — cross-references
  expanded to include the new RFC + catalog + the two research
  docs (now in-repo as authoritative sources).
- **`CLAUDE.md`** "Reading order at session start" — items 6 + 7
  added for the RFC + catalog. Items 6-7 (spec/tech-plan + 
  CONTRIBUTING) renumbered to 8-9.

**Vocabulary adopted (project-canonical post-RFC):**

- **Tail mask** (replaces "live region pointer") — per-row state
  marking content as overwrite-in-place; LATEST semantics.
- **Drain-checkpoint-swap** (replaces "alt-screen freeze") —
  three-phase Stream ↔ TUI substrate transition.
- **Seam hierarchy** (replaces "idle quanta as commit points") —
  five-priority ordered emission-trigger ranking.
- **Substrate-of-truth** — formalised; the canonical source from
  which all derived projections compute.
- **Literal-language convention** — `select / mark / announce /
  present / read / focused / current`; sight metaphors
  `highlight / view / show` eliminated for accessibility-bearing
  prose.
- **Sealed / unsealed events** — formalised; `Sealed: bool`
  extension on every chunk; mirrors RFC 9112 §8.
- **High-water-mark commit** — the producer's act of finalising
  a slice of the committed buffer at a seam crossing; replaces
  `extractContent`'s row-walk (post-Cycle 34).

**Earcon frequency clarification:** the research's Brewster
guidance (≥125 Hz, ≤5 kHz) conflicts with CONTRIBUTING.md's
tighter empirical bound (<180 Hz or >1.5 kHz). The RFC defers to
CONTRIBUTING.md as the production constraint; the research's
wider lower bound is flagged as a future tuning experiment.
**No change to CONTRIBUTING.md** in this PR.

**Stopping gate (per the strategic plan §2 Cycle 33):**
maintainer reads RFC + catalog before Cycle 34 implementation
begins; signs off on (a) tail-mask vs. brief-drop-to-TuiPathway
for live-region content, (b) 4 MB per-tuple cap appropriateness,
(c) freeze-on-alt-screen handles spinner case (spinners do NOT
enter alt-screen), (d) three exemplar canonical displays
accepted as the working catalog seed, (e) earcon frequency
conflict resolved in favor of CONTRIBUTING.md's tighter bounds.

### Added (Cycle 32b): first `IDisplayBuffer` consumer — TerminalView render path

`TerminalView`'s UI render path (`OnRender`) now consumes the
`IDisplayBuffer` boundary interface (Cycle 31b) instead of
calling `Screen.SnapshotRows` directly. The composition root
constructs an inline F# object expression wrapping the existing
`Screen` instance; the C# render loop receives an injected
`IDisplayBuffer` via a new `SetDisplayBuffer` method that mirrors
the existing `SetScreen` / `SetPtyHost` post-construction-injection
pattern. **Pure refactor — visual rendering identical;** the
`System.Tuple<long, Tuple<int, int>, Cell[][]>` shape returned by
`Snapshot` is byte-identical to the prior `SnapshotRows`, so
`.Item3` access in C# is unchanged.

This is the **first consumer cutover** for the four boundary
interfaces declared in Cycle 31. The other six `Screen.SnapshotRows`
call sites (`Program.fs:1258/1345/1375/1480/1534/2574`,
`TerminalAutomationPeer.fs:742`) remain on direct `Screen`
access — future cycles can migrate as concrete value motivation
surfaces.

- **`src/Views/TerminalView.cs`** — adds `using Terminal.Core.Channels;`,
  a new `private IDisplayBuffer? _displayBuffer;` field after the
  existing `_screen` field, a new `SetDisplayBuffer(IDisplayBuffer)`
  setter mirroring `SetScreen`, and a 1-line cutover at the render
  call site (now line 1011 post-edits): `_screen.SnapshotRows(0, _screen.Rows)`
  becomes `_displayBuffer.Snapshot(0, _screen.Rows)`. The
  `_screen.Cols` access on the next line stays direct — `IDisplayBuffer`
  is a snapshot-only contract; grid sizing is host-surface metadata
  that future renderers will read from their own surface
  dimensions.
- **`src/Terminal.App/Program.fs`** — adds `open Terminal.Core.Channels`
  at the top; immediately after the existing
  `window.TerminalSurface.SetScreen(screen)` at line 731, adds
  an inline `IDisplayBuffer` object expression wrapping the
  same `screen` instance plus a `SetDisplayBuffer(displayBuffer)`
  call. ~10 LOC. No new file (YAGNI principle — extract to a
  named `DefaultDisplayBuffer` class when a second consumer
  surfaces).

**No tests changed.** No `TerminalView*Tests` exist (per
CONTRIBUTING.md "Tests" §"NVDA: manual for each release", the
render path is NVDA-validated manually). The Cycle 31a / 31b /
32a unit-test surface stays green by construction (no public
API changed; `IDisplayBuffer` was declared in Cycle 31b with
no consumers, and Cycle 32b wires the first one).

**Validation:** maintainer launches pty-speak via `dotnet run` (or
the installed Velopack build) and verifies the terminal renders
normally on a real PTY session. The cutover is a pure refactor —
if rendering looks correct, the abstraction layer is sound.

### Added (Cycle 32a): `[profile.selection]` TOML loader — closes Stage 8e-A scope

The four `SelectionDetector` tunable thresholds (`HighlightDetectionThresholdMs`,
`DismissalGraceMs`, `KeystrokeCorrelationWindowMs`, `MinConfidence`)
are now overridable via a new `[profile.selection]` TOML section.
The detector itself (`SelectionDetector.fs:128-148` `Parameters`
record + `defaultParameters`, both shipped in Cycle 29a) is
**unchanged** — Cycle 32a is pure Config-side plumbing. With
this PR, **Stage 8e-A is fully shipped** (Cycle 29a substrate +
Cycle 29b consumer-side wiring + Cycle 32a config plumbing);
Stage 8e-B (UIA listbox peer) remains open as a separate plan-
mode pass.

- **`src/Terminal.Core/Config.fs`** — adds
  `SelectionParameterOverrides` record (four `option`-typed
  fields mirroring the detector's `Parameters` shape); appends
  the field to the top-level `Config` record + `defaultConfig`;
  adds `parseProfileSelectionOverrides` (mirrors
  `parseLoggingOverrides` template) and `resolveSelectionParameters`
  (mirrors `resolveStreamParameters` template) helpers; wires
  the parser into `tryLoad`.
- **`src/Terminal.App/Program.fs:1186`** — composition cutover:
  `SelectionDetector.create SelectionDetector.defaultParameters`
  becomes
  `SelectionDetector.create (Config.resolveSelectionParameters config)`.
  Three-line change.
- **`tests/Tests.Unit/ConfigTests.fs`** — adds 6 new facts in a
  new "Cycle 32a — `[profile.selection]` TOML loader" section
  covering: section absent → `SelectionDetector.defaultParameters`;
  all four keys present override; single key overrides + others
  default; unrecognised `min_confidence` string logs Warning +
  defaults; non-integer threshold logs Warning + defaults; non-
  positive (zero / negative) threshold logs Warning + defaults.
- **`docs/USER-SETTINGS.md`** — new "Selection prompt thresholds
  and confidence modes (Cycle 32a)" subsection documenting the
  four keys, their defaults, the snake_case TOML format
  (`heuristic_sgr` / `heuristic_sgr_with_keystroke` for the enum
  values), tuning use cases, and the no-hot-reload caveat.
- **`docs/STAGE-7-ISSUES.md`** — `[output-selection]` "Status"
  line flipped from "Cycle 29c finalises the substrate" to
  "Stage 8e-A is fully shipped (Cycle 29a + 29b + 32a)".

**No behaviour change** for users who do not add a
`[profile.selection]` section to `config.toml` — `defaultConfig`'s
`SelectionOverrides` field is `None` everywhere, so
`resolveSelectionParameters` returns
`SelectionDetector.defaultParameters` byte-equivalent.

**No hot-reload.** `[profile.selection]` is loaded once at
startup; mid-session `config.toml` edits require a restart.
Matches every existing TOML section except
`[session_model.persistence]` (which gets reloaded on shell-
switch via `Program.fs:2488-2500`). Adding hot-reload would
be a future micro-cycle.

**Validation per `docs/STAGE-7-ISSUES.md` `[output-selection]`:**
manual NVDA testing requires a Claude tool-use prompt that
forces user confirmation — write a file in an untrusted
directory to defeat Claude's auto-trust mode. The Cycle 29b
NVDA matrix (in `docs/ACCESSIBILITY-TESTING.md`) is the
authoritative test surface; Cycle 32a does not add a new row.

### Added (Cycle 31b): sibling boundary interface declarations — `IClipboardProvider`, `IHotkeyTranslator<'TGesture>`, `IDisplayBuffer`

Three pure interface declarations completing the boundary set
named in `docs/CORE-ABSTRACTION-BOUNDARY.md` §1.6 (portability
invariant). All three live in `Terminal.Core` per the substrate
side of the boundary; **no consumers are cut over in this PR**.
First consumer cutover (`IDisplayBuffer` at
`TerminalView.cs:1002`) lands in Cycle 32b alongside the
selection TOML loader. Portability CI lint added in Cycle 31a
continues to gate against host-specific imports leaking into
substrate code.

- **`src/Terminal.Core/Channels/IClipboardProvider.fs`** (new)
  — abstract `SetText: string → Async<bool>` codifying the
  STA-thread + 3-second-timeout contract that
  `Program.fs:2043` (Ctrl+Shift+Y) and `Diagnostics.fs:726`
  (Ctrl+Shift+D) currently embed inline; abstract
  `TryGetText: unit → string option` for read paths
  (`TerminalView.cs:681,692,742,744`). All five existing call
  sites continue to use `System.Windows.Clipboard` directly;
  the interface formalises the seam for future cross-platform
  builds without forcing a cutover today.
- **`src/Terminal.Core/Channels/IHotkeyTranslator.fs`** (new) —
  generic over the host gesture type (`'TGesture`) so
  Terminal.Core does not import any WPF / GTK / AppKit type.
  Abstract `Translate: HotkeyKey * Set<Modifier> → 'TGesture`.
  Today's WPF translation in `Program.fs:274-333`
  (`translateHotkeyKey` + `translateHotkeyModifiers`) stays as
  direct helpers; the interface gives a future Avalonia / GTK
  host a typed seam to plug into.
- **`src/Terminal.Core/Channels/IDisplayBuffer.fs`** (new) —
  abstract `Snapshot: int * int → int64 * (int * int) * Cell[][]`
  matching today's `Screen.SnapshotRows` shape
  (`Screen.fs:541-553`) including the locking contract
  (atomic cell + cursor capture). Seven existing
  `Screen.SnapshotRows` call sites (`TerminalView.cs:1002`,
  `Program.fs:1258/1345/1375/1480/1534/2574`,
  `TerminalAutomationPeer.fs:742`) continue direct; Cycle 32b
  ships a `DefaultDisplayBuffer` adapter and migrates only
  the UI render call site.
- **`src/Terminal.Core/Terminal.Core.fsproj`** — three new
  `<Compile Include>` entries appended after `HotkeyRegistry.fs`
  (the new files depend on `HotkeyRegistry.HotkeyKey/Modifier`
  and `Cell` from `Types.fs`, both of which compile earlier).
- **`docs/CORE-ABSTRACTION-BOUNDARY.md`** §10 — cross-references
  expanded to point at the actual interface files (the prior
  doc referenced files that didn't yet exist on disk).

The "channel" name in `Channels/` subdirectory is slightly
imprecise — these three are substrate → host seams, not channel-
side surfaces (the IOutputSink that channels implement lives
in `OutputEventTypes.fs` for compile-order reasons). Naming
preserved for now to match the strategic plan's vocabulary;
may be revisited if maintenance demand surfaces.

### Docs (post-Cycle-31a): history sub-pane navigation contract via CommandOutputTuple

Doc-only refinement folding the `docs/research/Output-paradigms.md`
CommandOutputTuple primitive (Section 1.6) into the history sub-
pane's navigation contract. The framing aligns the CHI '21
Pradhan et al. CLI-accessibility finding (command-as-anchor
navigation as the single most-requested CLI feature) with our
existing three-sub-pane decomposition. No behaviour change; no
new exemplar canonical displays added — the three exemplars
(raw text / interactive list / form with text input) stand.

- **`docs/CORE-ABSTRACTION-BOUNDARY.md`** §6 — history sub-pane
  paragraph rewritten to name CommandOutputTuple as the unit of
  history navigation, with concrete quick-nav primitives (`h` /
  `Shift+h` for command boundaries, `o` / `Shift+o` for output
  blocks, `Alt+Up` / `Alt+Down` for tuple boundaries) replacing
  the prior "paragraph navigation" hand-waving. Cross-references
  the research doc; defers full UIA pattern provider mapping to
  Cycle 33 RFC.
- **`docs/PANE-MODEL.md`** — same refinement applied to the
  "Shell pane internal structure" subsection's history sub-pane
  bullet. Adds a substrate-of-truth pointer to
  `SESSION-MODEL.md` §4.

The two emission/output research docs landed independently on
main between Cycle 30 and 31a (`docs/research/Output-paradigms.md`
+ `docs/research/emission-paradigms.md`); this PR is the first
to cite them. Further integration (seam-hierarchy commit,
cadence parameters, tail-mask vocabulary) lands in Cycle 33's
linear-text-substrate RFC.

### Added (Cycle 31a): `IOutputSink` boundary interface + portability CI lint

First code cycle of the substrate / channel boundary work locked
in Cycle 30. Promotes the existing `Channel` record (a record-of-
functions used by the dispatcher) to satisfy a formally-typed
`IOutputSink` interface. Adds a CI gate that fails the build if
substrate code (`Terminal.Core` / `Terminal.Audio`) ever imports
host-specific types per ADR 0001. **No user-visible behaviour
change** — all three shipped channels (NvdaChannel, EarconChannel,
FileLoggerChannel) continue to work identically; their factory
signatures are unchanged; the composition root (`Program.fs:891-916`)
is untouched.

- **`src/Terminal.Core/OutputEventTypes.fs`** — declares the
  `IOutputSink` interface (`Id: ChannelId`, `Send: OutputEvent →
  RenderInstruction → unit`) just before the existing `Channel`
  record. The `Channel` record gains an `interface IOutputSink
  with` implementation that forwards both members to the
  record's own fields. The interface lives in the same file
  (rather than a separate `Channels/IOutputSink.fs`) for F#
  compile-order reasons — `IOutputSink` references types defined
  earlier in `OutputEventTypes.fs`, and the `Channel` record
  must reference `IOutputSink` to implement it.
- **`src/Terminal.Core/OutputDispatcher.fs:106-114`** —
  `routePair` upcasts the resolved channel to `IOutputSink`
  before invoking `Send`. Functionally identical for `Channel`-
  record sinks (which trivially satisfy the interface);
  establishes the contract for future producers (linear-text
  substrate consumers in Cycle 34, future cross-platform
  channel adapters) that implement `IOutputSink` directly
  without being `Channel` records.
- **`tests/Tests.Unit/IOutputSinkTests.fs`** (new) — 6 facts
  pinning the interface contract: each shipped channel's
  factory output coerces to `IOutputSink`; the coerced
  interface preserves the empty-payload skip (Nvda) + non-
  Earcon skip (Earcon) + structured-log emit (FileLogger);
  the `Channel` record's interface impl forwards `Id` and
  `Send` reflexively to the record's own fields.
- **`.github/workflows/ci.yml`** — new `portability-lint` job
  (ubuntu-latest, 5-minute timeout) that fails the build if
  `grep -rEn '^[[:space:]]*open[[:space:]]+(System\.Windows|
  PresentationCore|WindowsBase|Microsoft\.Win32)' src/Terminal.Core
  src/Terminal.Audio --include='*.fs'` returns matches. Anchored
  on `^[[:space:]]*open[[:space:]]+` so doc-comment mentions of
  these namespaces (e.g., `KeyEncoding.fs:11`'s comment
  explaining "Why a private DU instead of WPF's
  `System.Windows.Input.Key`?") do NOT trigger the lint.
  Verified clean on current `main`.

Cycle 31b (next) lands the sibling boundary interfaces:
`IClipboardProvider`, `IHotkeyTranslator`, `IDisplayBuffer` —
all pure interface declarations with no consumer cutovers.
First cutover (`IDisplayBuffer` at `TerminalView.cs:1002`) lands
in Cycle 32b alongside the selection TOML loader.

### Docs (Cycle 30): substrate / channel boundary doc + ADR 0001 + PANE-MODEL refinement

Doc-only foundation cycle landing the architectural framing
locked in PROJECT-PLAN-2026-05-09 §A. No behaviour change. The
boundary is a non-negotiable design constraint going forward;
all subsequent linear-text-substrate work (Cycles 33-35) builds
against it.

- **`docs/CORE-ABSTRACTION-BOUNDARY.md`** (new) — canonical
  statement of the substrate / channel dichotomy. §1 records
  the maintainer-blessed four-part assertion verbatim. §2-§3
  define what substrate code (`Terminal.Core`) and channel code
  (`Terminal.Accessibility`) may and may not do. §4 names the
  NLP-style parser pipeline layering. §5 catalogs the **three
  exemplar canonical displays** (raw text / interactive list /
  form with text input) that seed the canonical-display
  vocabulary; severity alert / progress / status / tabular /
  tree are named extension points but not specified. §6
  formalises the **three-sub-pane interaction paradigm**
  (command-input / current-output / history) for the shell
  pane plus three reserved peer panes (notification queue /
  contextual keyword info / input assistant). §7 specifies the
  streaming-incomplete protocol. §8 codifies the portability
  invariant.
- **`docs/adr/0001-substrate-channel-dichotomy.md`** (new) —
  ADR recording the four-part assertion as accepted. Documents
  context (today's screen-grid substrate breaks down on linear
  workloads — confirmed Cycle 29b spinner storms, red-tone
  misfires, `extractCommandAndOutput` fragility), decision (the
  boundary is non-negotiable), and consequences (positive +
  costs, including the multi-cycle inversion and parallel-
  substrate transition window).
- **`docs/PANE-MODEL.md`** (updated) — bumped snapshot to
  2026-05-09. Catalog table extended with the three new
  reserved peer panes (notification queue / contextual keyword
  info / input assistant). Inserted "Shell pane internal
  structure" subsection that points at
  CORE-ABSTRACTION-BOUNDARY.md §6 for the three-sub-pane
  decomposition. Three new pane sections appended after AI
  assistance — each one paragraph with content source, user
  interaction sketch, cross-pane coordination, UIA mapping,
  reserved decisions. CORE-ABSTRACTION-BOUNDARY.md is now the
  canonical source for all four refinements.
- **`CLAUDE.md`** (updated) — reading order at session start
  expanded to insert CORE-ABSTRACTION-BOUNDARY.md as item 5
  (between strategic plan and spec/tech-plan). Read before
  working on substrate or channel code.
- **`CONTRIBUTING.md`** (updated) — "Honor the spec" rule
  extended to add the boundary doc + ADR 0001 as a third
  non-negotiable: substrate code (`Terminal.Core`) may not
  import channel concerns (`Terminal.Accessibility`); channels
  may not produce new semantic events.

The Claude-research handoffs originally drafted (canonical-
display taxonomy survey + streaming partial-emit prior-art
survey) are deferred per the maintainer's 2026-05-09
redirect; the three exemplar canonical displays establish
the abstraction without requiring full taxonomy scoping.
Research can be commissioned later if extension demand arises.

### Docs (Cycle 29b NVDA-test follow-up): captured three lessons from 2026-05-09 validation pass

Doc-only follow-up to Cycle 29b's NVDA validation. Three
captured artefacts for future-session benefit; no behaviour
change.

- **`CLAUDE.md`** — new "Diagnostic logs — request chunks, not
  full bundles" guidance. The Cycle 29b NVDA test produced a
  diagnostic bundle large enough to crash the maintainer's iOS
  chat app on paste. Future Claude sessions are now told to
  prefer chunk-first requests (specific `Semantic=...`
  greps via `findstr` / `Select-String`, time-bounded
  windows, named bundle sections like `--- DIAGNOSTIC BATTERY
  LOG ---` / `--- FILELOGGER ACTIVE LOG ---` / `--- CONFIG.TOML
  ---` / `--- ENVIRONMENT ---`) over the whole snapshot.
- **`docs/STAGE-7-ISSUES.md`** — empirical confirmation notes
  on three entries from the 2026-05-09 test:
  - `[output-stream]` spinner storm: Claude's thinking-state
    spinners (`✻ Transmuting…` / glyph rotates `✻✶✽✢·` +
    incrementing token counter) defeat the existing
    identical-hash spinner gate; ~80 announces per Claude
    turn measured. Fix sketch: change suppression from
    "identical hash" to "high-frequency low-edit-distance".
    ~50-100 LOC in `StreamPathway`.
  - `[output-earcon]` red-tone misfire: same spinner glyphs
    are red-coloured, so the `StreamPathway` color-detection
    fires `ErrorLine` events for every spinner frame; ~30
    `error-tone` earcons per Claude turn. Fix sketches:
    skip ErrorLine for known-spinner glyphs OR add per-shell
    TOML toggle OR raise red-character-count threshold.
    ~30-50 LOC.
  - `[output-selection]` validation gotcha: Claude's
    auto-trust mode for known directories skips the
    tool-use confirmation prompt entirely. Cycle 29b's
    `SelectionDetector` per-shell short-circuit DID confirm
    working (zero `SelectionShown` events across a
    5-minute cmd session) but the prompt-rendering path
    didn't get exercised. To force a prompt for testing,
    ask Claude to write/edit (not just read) a file, or
    run pty-speak from a directory Claude hasn't been
    granted trust in.
- **`docs/SESSION-HANDOFF.md`** — refreshes "In-flight branch"
  + "Next stage" cells. NVDA validation status now reads:
  Cycle 27 multi-state menus validated; Cycle 28 Window menu
  validated; Cycle 29b SelectionProfile code-clean-but-not-
  exercised. Two near-term path candidates surfaced for
  decision: Path A (Cycle 29c TOML loader, scope completion)
  vs. Path B (spinner-storm + red-tone fixes, immediate UX
  unblock for Claude). Maintainer's 2026-05-09 recommendation
  was Path B first.

### Added (Cycle 29b, Stage 8e-A part 2): SelectionProfile + Program.fs wiring — NVDA starts speaking selection prompts as text

Second of three sequenced PRs (29a/29b/29c) closing spec
Stage 8e for `[output-selection]` per
`docs/STAGE-7-ISSUES.md:337` (Claude tool-use prompt reads as
flat text instead of listbox). 29a shipped the detector substrate;
29b ships the consumer side + the wiring that connects detector
output to the dispatcher. **NVDA now reads selection prompts as
text** when running Claude — the user-visible payoff of the
sub-cycle. 29c will add the `[profile.selection]` TOML loader.
8e-B introduces the UIA listbox peer (lifts text-only RenderText
to UIA listbox semantics with `1 of 4` navigation).

What ships in 29b:

- **`src/Terminal.Core/SelectionProfile.fs`** — cousin of
  `EarconProfile.fs`. Pattern-matches on
  `SemanticCategory.SelectionShown / SelectionItem /
  SelectionDismissed`; constructs user-facing text from
  structured `Extensions` data and emits NVDA + FileLogger
  ChannelDecisions. Empty-payload trick (mirrors the 8d.2
  ErrorLine / WarningLine precedent): the detector emits with
  empty `Payload` so PassThroughProfile's catch-all NVDA
  decision is skipped (NvdaChannel.fs:87 drops `RenderText ""`),
  preventing double-emission. SelectionProfile then renders
  text from Extensions and emits the ONE NVDA + FileLogger
  pair the user actually hears.
- **`src/Terminal.Core/SelectionDetector.fs`** — adjusted to
  emit empty `Payload` (was non-empty in 29a; would have
  double-emitted via PassThroughProfile catch-all). Added
  `ItemIndex` extension key on burst SelectionItem events
  (was conflated with `SelectedIndex` in 29a). Now: each
  burst SelectionItem carries `SelectedIndex` (constant
  global selected index across the burst) AND `ItemIndex`
  (THIS item's 0-based position; varies). SelectionProfile
  uses `(ItemIndex == SelectedIndex)` to prefix "selected: "
  in the rendered text.
- **`src/Terminal.Core/OutputEventTypes.fs`** — extended
  `SelectionExtensions` constants module with `ItemIndex` key
  + clarified `SelectedIndex` doc-comment.
- **`src/Terminal.App/Program.fs`** composition-root wiring:
  - Profile registration: `selectionProfile = SelectionProfile.create()`
    + `OutputDispatcher.ProfileRegistry.register selectionProfile`
    + extended `setActiveProfileSet` to include selection.
  - Detector mutable: `let mutable selectionDetector` adjacent
    to `promptDetector` (~line 1175). Same actor-model contract
    (mutated only on the PathwayPump worker thread).
  - `runDetector` extension: after the existing
    `HeuristicPromptDetector.tryDetect` invocation, call
    `SelectionDetector.tryDetect` and route each emitted
    `OutputEvent` directly to `OutputDispatcher.dispatch`.
  - Reset hooks at all 3 sites that already reset
    `promptDetector`: alt-screen entry, alt-screen exit,
    shell-switch.
- **`tests/Tests.Unit/SelectionProfileTests.fs`** — 13 facts
  pinning the profile contract: identity, SelectionShown
  rendering with full-list + selected-item prefix,
  fall-back to count-only summary when AllItems missing,
  SelectionItem rendering with "selected: " prefix when
  `ItemIndex == SelectedIndex`, plain "%s, %d of %d" when
  not, SelectionDismissed renders literal "selection
  dismissed", foreign Semantic categories return `[||]`,
  Tick + Reset are no-ops.
- **`tests/Tests.Unit/SelectionDetectorTests.fs`** — added
  `ItemIndex` schema fact + payload-empty assertions on
  SelectionShown / SelectionItem (the empty-payload trick).
- Project file registrations: `Terminal.Core.fsproj` registers
  `SelectionProfile.fs` after `EarconProfile.fs`;
  `Tests.Unit.fsproj` registers `SelectionProfileTests.fs`
  after `EarconProfileTests.fs`.

**Keystroke wiring deferred** (per maintainer's plan-mode
choice 2026-05-09). The detector emits at default
`HeuristicSGR` confidence without keystroke input — Signals
#1 (stable region) + #2 (SGR-distinct) alone meet the default
`MinConfidence` threshold so emission flows end-to-end. Signal
#3 confidence upgrade (arrow-key correlation) is purely a
debug-confidence indicator visible only as the
`selection.source` Extensions value; not user-facing in 29b.
Keystroke wiring lands in 8e-C alongside the arrow-key
round-trip (`ISelectionItemProvider.Select` → PTY arrow byte
sequence) where it belongs architecturally.

### Added (Cycle 29a, Stage 8e-A part 1): SelectionDetector substrate + SelectionExtensions schema (not yet wired to dispatcher)

First of three sequenced PRs (29a / 29b / 29c) that close the
output-framework spec's Stage 8e — `[output-selection]` per
`docs/STAGE-7-ISSUES.md:337` (Claude tool-use prompt reads as
flat text instead of listbox). 29a ships the producer-side
substrate **fully unit-tested but not yet wired into the
dispatcher**. 29b will add `SelectionProfile.fs` + `Program.fs`
wiring (NVDA starts speaking selection events as text). 29c will
add the `[profile.selection]` TOML loader. 8e-B + 8e-C continue
the sub-cycle with the UIA listbox peer + arrow-key round-trip.

- **`src/Terminal.Core/SelectionDetector.fs`** — pure functional
  detector implementing spec `tech-plan.md` §8.1 detection
  heuristic. Two-pass per-frame algorithm: PASS 1 classifies
  rows (Blank / Plain / Highlighted via SGR-distinct background
  or Inverse bit), groups runs of contiguous non-blank rows
  into candidate regions (2-6 rows; exactly one highlighted;
  reject pure-punctuation rows); PASS 2 picks the bottommost
  candidate, applies signal aggregation (stable region after
  `HighlightDetectionThresholdMs`; SGR-distinct by construction;
  arrow-key correlation upgrades confidence to
  `HeuristicSGRWithKeystroke`), and emits `OutputEvent`s with
  `SemanticCategory.SelectionShown` / `SelectionItem` /
  `SelectionDismissed`. Public surface: `Direction` /
  `SelectionSource` / `Parameters` / `defaultParameters` /
  `create` / `tryDetect` / `feedKeystroke` / `reset`. Mirrors
  `HeuristicPromptDetector.fs` shape. Per-shell short-circuit
  (claude-only in 8e-A; 8f wires TOML).
- **`src/Terminal.Core/OutputEventTypes.fs`** — new
  `SelectionExtensions` constants module declaring the
  well-known `OutputEvent.Extensions` keys (`selection.itemCount`,
  `selection.selectedIndex`, `selection.itemText`,
  `selection.allItems`, `selection.topRow`, `selection.bottomRow`,
  `selection.source`). Schema-stable across the sub-cycle —
  8e-B's UIA peer queries the same keys.
- **`tests/Tests.Unit/SelectionDetectorTests.fs`** — 19 facts
  pinning the detector contract: per-shell activation gate,
  stability window, initial-burst shape (Producer +
  Priority + Extensions), selection-index update via signature
  gate, suppression on identical snapshot, dismissal grace,
  confidence-tier upgrade on arrow-key correlation,
  `MinConfidence` gating, region-rejection (single-row /
  8-row / SGR-uniform), reset behaviour. Mirrors
  `HeuristicPromptDetectorTests.fs` fixture-builder pattern;
  adds `cellWithBg` + `highlightedRowOf` helpers for the
  SGR-distinct simulation.
- **`src/Terminal.Core/Terminal.Core.fsproj`** + **`tests/Tests.Unit/Tests.Unit.fsproj`** — register the new sources after their respective cousins (`HeuristicPromptDetector.fs` /
  `HeuristicPromptDetectorTests.fs`).

**Wiring status.** This PR does NOT route detector output into
`OutputDispatcher`. The detector compiles standalone, is
exercised entirely via tests, and exports a clean public API
ready for 29b. NVDA behaviour on the running app is **unchanged**
by this PR — there is no selection-text speech yet. That is
intentional per the maintainer-chosen 29a/29b/29c split: ship
the detection logic first so the heuristic can be code-reviewed
and CI-tested in isolation; wire it into NVDA when 29b ships.

### Added (Cycle 28): Window menu (Close Window + Exit) + Program.fs comment cleanup + PROJECT-PLAN status refresh

Foundation cleanup PR before the framework cycle ramps. Mixes
three small items: a new top-level Window menu with Close Window
+ Exit slots; a stale `Ctrl+Shift+L` comment in `Program.fs`
(removed in Cycle 25b-1a) replaced with the live `Ctrl+Shift+P`
reference; and an in-place status refresh of
`PROJECT-PLAN-2026-05-09.md` to reflect Cycle 26 + 27 closure
without spawning a new dated successor.

- **Window menu** (`MainWindow.xaml`) — new top-level `_Window`
  menu between Diagnostics and Help. Two children:
  - `Close _Window` — calls `Window.Close()` (symmetric with
    the OS-level Alt+F4 gesture WPF handles natively).
    `InputGestureText="Alt+F4"` is hardcoded in XAML so NVDA
    reads "Close Window, Alt plus F4, menu item" — the gesture
    string is purely visual since the Alt+F4 path is OS-handled,
    not via `AppReservedHotkeys`.
  - `E_xit` — calls `Application.Current.Shutdown()`. In the
    current single-window app the visible behaviour is
    identical to Close Window; the separate slot future-proofs
    multi-pane Phase 2 plans.
- **New menu-only AppCommands**: `CloseWindow` and `ExitApp`
  added to `HotkeyRegistry.AppCommand` DU + `nameOf` +
  `builtIns` (both with `Key=None, Modifiers=None`) +
  `allCommands`. Brings the menu-only AppCommand count to 3
  (after `RunProcessCleanupScript`).
- **`Program.fs` handlers**: `runCloseWindow` + `runExitApp` log
  invocation at Information level then dispatch to
  `Window.Close()` / `Application.Current.Shutdown()`. Wired
  via the existing `bind` helper so the menu and the
  (non-existent) keyboard path share a single `RoutedCommand`.
- **`HotkeyRegistryTests.fs`**: `allCommands contains exactly
  the documented commands` set extended with `CloseWindow` +
  `ExitApp`. Two new menu-only-shape fixtures
  (`CloseWindow is menu-only`, `ExitApp is menu-only`) mirror
  the existing `RunProcessCleanupScript is menu-only` pattern.
- **`Program.fs:685` comment fix**: replaced `Ctrl+Shift+L` (the
  hotkey for "open logs folder") with `Ctrl+Shift+P` (the
  current `OpenDataFolder` hotkey, which opens the parent of
  `\logs`). The Ctrl+Shift+L hotkey was removed in Cycle
  25b-1a; the stale comment had survived since.
- **`PROJECT-PLAN-2026-05-09.md` in-place refresh**: new "Status
  update 2026-05-09 (post Cycle 27 closure)" callout near the
  top of the doc + Sequencing block updated to mark Cycles
  26a/b/c/d, 27, and 28 as ✅ shipped. The next strategic stage
  is now framed as "Cycle 29+: Output framework cycle (Part
  3)" with a note pointing at `STAGE-7-ISSUES.md` +
  `spec/event-and-output-framework.md` as the design substrate
  for the framework cycle's first plan-mode RFC sub-stage.
- **Hotkey-menu coverage audit (informational)**: confirmed all
  12 remaining gesture-bearing AppCommands ARE represented in
  the menu after Cycles 26b + 27. `Ctrl+Shift+P` (Data → Open
  Data Folder), `Ctrl+Shift+U` (Help → Check for Updates),
  `Ctrl+Shift+E/D/R/H/B/Y/S/1/2/3` all surface via their
  respective menus. No hotkey is "menu-orphaned".

### Changed (Cycle 27): Multi-state menu paradigm canon + EarconsMode/LoggingLevel migration + dropped Ctrl+Shift+M / Ctrl+Shift+G

First architectural cycle after the Cycle 26 app-menu skeleton. Adds
a new menu paradigm for operations whose UX is "select one of N
discrete options" rather than "fire one action", and migrates the
two existing single-action toggles whose semantics fit that shape.

- **New `MultiStateCommand` DU + registry**
  (`src/Terminal.Core/HotkeyRegistry.fs`). Parallel concept to
  `AppCommand`; the existing `AppCommand` / `Hotkey` / `bindHotkey`
  framework is unchanged and continues to be the path for
  single-action commands. Each `MultiStateDef` declares an ordered
  list of `MultiStateOption`s with a stable `OptionId` (snake_case;
  used in XAML field names, `RoutedCommand` names, log lines, and
  future TOML keys) and a user-facing `DisplayName`.
- **`bindMultiState` helper + composition wiring**
  (`src/Terminal.App/Program.fs`). Mirrors `bindHotkey`'s shape minus
  the `KeyBinding` step (multi-state is menu-only by canon as of
  Cycle 27). Each option gets its own `RoutedCommand`; the parent
  `MenuItem`'s `SubmenuOpened` handler refreshes `IsChecked` on every
  open by querying the bound `getCurrent` closure. Each option's
  `MenuItem.IsCheckable=true` surfaces UIA TogglePattern that NVDA
  reads as "menu item, checked" / "menu item, not checked", so a
  screen-reader user can tell at a glance which option is currently
  active.
- **Migrated `EarconsMode`** (formerly `MuteEarcons` Ctrl+Shift+M).
  Now lives under View → Earcons → Enabled / Muted. The
  Ctrl+Shift+M keyboard accelerator is **dropped**; the gesture
  flows through to the shell as plain text.
- **Migrated `LoggingLevel`** (formerly `ToggleDebugLog`
  Ctrl+Shift+G). Now lives under View → Logging Level →
  Information / Debug. The Ctrl+Shift+G keyboard accelerator is
  **dropped**; the gesture flows through to the shell as plain
  text.
- **Mirror parity preserved automatically**. F#-side
  `HotkeyRegistry.builtIns` shed two entries; C#-side
  `TerminalView.AppReservedHotkeys` shed the matching `Key.G` and
  `Key.M` rows. The `AppReservedHotkeysMirrorTests` parity invariant
  catches asymmetric edits at test time.
- **`MainWindow.xaml`** — replaces the old single-action
  `MenuItem_ToggleDebugLog` and `MenuItem_MuteEarcons` items with
  parent + per-option pairs (`MenuItem_LoggingLevel` +
  `_information`/`_debug`; `MenuItem_EarconsMode` +
  `_enabled`/`_muted`).
- **New `MultiStateRegistryTests.fs`** — pins the same shape
  contracts that `HotkeyRegistryTests` pins for `AppCommand`:
  exhaustive DU/`multiStateNameOf` round-trip; one `multiStateBuiltIns`
  entry per command; ≥2 distinct options per `MultiStateDef`; OptionId
  uniqueness within a `MultiStateDef`; pinned documented OptionIds for
  both migrating commands.
- **`HotkeyRegistryTests.fs`** — updated `allCommands` documented
  set + `priorCommands` regression guard to reflect the two
  removals; new `Ctrl+Shift+G is unbound` and `Ctrl+Shift+M is
  unbound` fixtures pin the gestures' new "flows to shell" behaviour.
- **Docs**: `docs/USER-SETTINGS.md` recipe for multi-state extension;
  `docs/ACCESSIBILITY-TESTING.md` Cycle 27 NVDA matrix subsection;
  `CLAUDE.md` hotkey list refresh; `docs/SESSION-HANDOFF.md` "Where
  we left off" update; `docs/CHECKPOINTS.md` pending-tag entry for
  `baseline/cycle-27-multistate-paradigm`.

Future per-option keyboard accelerators (e.g. for a Shell-switch
multi-state migration where `Ctrl+Shift+1`/`+2`/`+3` would each
target a distinct option) are a clean extension to
`MultiStateOption` if the maintainer needs them — not in scope for
this cycle.

### Added (Cycle 26d): Cycle 26 NVDA matrix rows + How-to-add-a-menu-item recipe + Where-we-left-off refresh

Final PR of Cycle 26 (the multi-PR app-menu mini-cycle). Doc-only;
locks the cycle's user-facing contract via a new NVDA validation
matrix and a contributor-facing extension recipe.

- **`docs/ACCESSIBILITY-TESTING.md`** — new "Cycle 26 — App menu"
  subsection (sits between the Stage-7 / Cycle-24 cluster and
  Stage 8) with 6 NVDA validation rows covering: Menu reachable
  via Alt; document-role preserved on launch (26a invariant);
  `InputGestureText` reading via NVDA (26b); menu invokes same
  handler as keyboard gesture (single-source-of-truth contract);
  all 14 keyboard hotkeys still fire (regression check); menu-only
  command launches script (26c); menu-only item omits
  `InputGestureText`. Includes a 5-row diagnostic decoder for
  common failure modes and an ADR-style note documenting the
  parallel-surface decision (`TerminalView.AppReservedHotkeys`
  stays as the C#-side hot-path mirror; `AppReservedHotkeysMirrorTests`
  pins parity at test time).
- **`docs/USER-SETTINGS.md`** — new "How to add a menu item
  (Cycle 26 extension recipe)" section at the end. Documents
  the three-edit pattern (AppCommand DU + builtIns row + named
  MenuItem in XAML) for: gesture-bearing commands, menu-only
  commands, new top-level menus, accelerator rebinds, and item
  removal (the inverse of the recipe). Phase 2 evolution note
  flags the future TOML-override surface for `Hotkey.Key` /
  `Modifiers`.
- **`CLAUDE.md`** — updates the line at 462-463 (formerly "A
  future cycle's app menu will surface this script as a menu
  item.") to past tense, citing Cycle 26c's surfacing of
  `test-process-cleanup.ps1` via Diagnostics → Test Process
  Cleanup.
- **`docs/SESSION-HANDOFF.md`** — refreshes "Where we left off"
  through Cycle 26: Last merged stages cell extended with PRs
  #223-#227 (Cycle 25c, 25d, 26a, 26b, 26c) plus this PR's 26d;
  In-flight branch updated to `claude/cycle-26d-docs-and-nvda-matrix`;
  Next stage cell repointed at the candidate-cycle picker
  (Output framework Part 3 strategic-priority vs. landing one of
  the maintainer's anticipated future menus first to exercise
  the Cycle 26 extension recipe).
- **`docs/CHECKPOINTS.md`** — adds `baseline/cycle-26-app-menu`
  to the pending-tag-pushes table, with the standard "resolve
  the merge SHA after this PR lands" template the maintainer's
  workstation sweep uses.

No code changes. The Cycle 26 mini-cycle ships end-to-end with
this PR.

**Maintainer follow-ups** (per `docs/SESSION-HANDOFF.md`
"Where we left off" Open follow-ups):

- Push the new `baseline/cycle-26-app-menu` tag once this PR
  merges (along with the other pending-tag-pushes —
  `baseline/stage-7-claude-roundtrip`,
  `baseline/cycle-24-sessionmodel-persistence`).
- Run the new Cycle 26 NVDA matrix rows against the next preview
  cut to verify the menu surface end-to-end on NVDA.

### Added (Cycle 26c): RunProcessCleanupScript menu-only AppCommand + handler

Third PR of Cycle 26 (the multi-PR app-menu mini-cycle). Lands the
**first menu-only `AppCommand`** — `RunProcessCleanupScript` —
proving the option-typed `Hotkey` plumbing introduced in Cycle 26b
works end-to-end. Surfaces `scripts/test-process-cleanup.ps1` via
**Diagnostics → Test Process Cleanup**, with no default keyboard
accelerator (relieving the noted hotkey-count working-memory
ceiling).

The script needs the maintainer to physically close pty-speak via
Alt+F4 to verify Job Object cascade-kill cleanup — that's why the
autonomous in-process diagnostic battery (Ctrl+Shift+D) doesn't
cover it. PowerShell launches with `-NoExit` so the PASS/FAIL
output stays visible after the script exits.

**Architectural changes**:

- **`src/Terminal.Core/HotkeyRegistry.fs`** — new
  `RunProcessCleanupScript` case in the `AppCommand` DU
  (`nameOf` arm + `builtIns` row with `Key = None; Modifiers =
  None` + `allCommands` entry). Lines 70-104, 109-125, 146-202,
  234-249.
- **`src/Terminal.Core/Types.fs`** — new
  `ActivityIds.processCleanup = "pty-speak.process-cleanup"`.
  Distinct ActivityId so consecutive presses dedupe at the
  NVDA-channel layer.
- **`src/Terminal.App/Program.fs`** — new
  `runTestProcessCleanup` handler near `runOpenConfig`. Mirrors
  the announce-before-focus-grab pattern from
  `runOpenNewRelease` / `runOpenDataFolder` / `runOpenConfig`:
  announces "Launching process-cleanup test in a separate
  PowerShell window.", waits 700ms (so NVDA finishes speaking),
  then `ProcessStartInfo { FileName = "powershell.exe";
  Arguments = "-NoExit -ExecutionPolicy Bypass -File
  \"<path>\""; UseShellExecute = true }`. Script-path resolution
  via `Path.Combine(AppContext.BaseDirectory,
  "test-process-cleanup.ps1")`. File-not-found falls through to
  a sanitised announcement instead of throwing. Wraps in
  `try/with`; sanitises exception messages via
  `AnnounceSanitiser.sanitise`. Bind call added alongside the
  other module-level binds.
- **`src/Terminal.App/Terminal.App.fsproj`** —
  `<Content Include>` for the script was already present (PR
  #81 / Stage 4b precedent); only the inline comment is updated
  to cite the Cycle 26c menu surface instead of the obsolete
  `Ctrl+Shift+D` reference.
- **`src/Views/MainWindow.xaml`** — new
  `<MenuItem x:Name="MenuItem_RunProcessCleanupScript"
   x:FieldModifier="public" Header="Test Process _Cleanup" />`
  under the Diagnostics menu. No `InputGestureText` — compose's
  reflection-driven wiring assigns it from
  `HotkeyRegistry.gestureText`, which returns `None` for
  menu-only commands.

**Three-edit-extension recipe in action**:

This PR exercises the recipe the Cycle 26b CHANGELOG documented
verbatim:

1. ✅ AppCommand DU case + nameOf arm + allCommands row.
2. ✅ builtIns row (with `None, None` for menu-only).
3. ✅ Named `MenuItem` in XAML.

Compose's reflection-driven wiring picks up the new entry
automatically. No changes to `Program.fs compose()`, no changes
to `bindHotkey`, no changes to the menu-wiring loop. The handler
addition + bind call are the only `Program.fs` edits.

**New tests**:

- `tests/Tests.Unit/HotkeyRegistryTests.fs` — extends "allCommands
  contains exactly the documented commands" set with
  `RunProcessCleanupScript`. New fixture
  `RunProcessCleanupScript is menu-only (Cycle 26c)` pins
  `(None, None)` shape + asserts `gestureText` returns `None`
  for it (so MenuItem.InputGestureText is left blank in XAML).
- `tests/Tests.Unit/AppReservedHotkeysMirrorTests.fs` — no change
  needed; the existing `gesture-bearing` filter
  (`List.choose (fun h -> match h.Key, h.Modifiers with Some k,
  Some m -> ...)`) already excludes menu-only commands from the
  parity check.

**No NVDA validation row in this PR** (the matrix-row PR is Cycle
26d). Manual NVDA gate to verify in 26d:

- Diagnostics → Test Process _Cleanup → Enter announces
  "Launching process-cleanup test in a separate PowerShell
  window."; a new PowerShell window opens with the script
  running; closing pty-speak via Alt+F4 produces PASS/FAIL
  output in that window.
- Menu item is focusable but reads no `InputGestureText` (no
  shortcut → blank suffix).

### Added (Cycle 26b): wire 14 existing AppCommands into menu items via shared RoutedCommand

Second PR of Cycle 26 (the multi-PR app-menu mini-cycle). Replaces
the throwaway `_Help → E_xit` placeholder shipped in Cycle 26a with
the **populated-from-`HotkeyRegistry` structure**: 5 top-level menus
(Shell, View, Data, Diagnostics, Help) containing 14 named
`MenuItem`s — one per existing `AppCommand`.

Each menu item binds to the **same `RoutedCommand` instance** that
`bindHotkey` already creates for the keyboard hotkey. Pressing the
gesture and clicking the menu item invoke the same handler — single
source of truth, zero behaviour duplication. NVDA reads the gesture
text from `MenuItem.InputGestureText` (auto-set from the registry)
when the menu item is focused.

**Architectural changes**:

- **`src/Terminal.Core/HotkeyRegistry.fs`** — `Hotkey.Key` and
  `Hotkey.Modifiers` are now `option`-typed (`HotkeyKey option`,
  `Set<Modifier> option`). All 14 existing `builtIns` entries wrap
  their gesture with `Some`. Cycle 26c will add the first `None,
  None` (menu-only) entry. New `gestureText : Hotkey -> string
  option` helper formats gestures as e.g. `"Ctrl+Shift+U"` /
  `"Ctrl+Shift+1"` (modifier order Ctrl > Alt > Shift, fixed
  regardless of `Set` enumeration order). `tryFind` filters to
  `Some` entries only — menu-only commands are excluded by
  definition since they have no gesture to look up.
- **`src/Terminal.App/Program.fs`** — `bindHotkey` signature
  changed from `unit` return to `RoutedCommand` return so compose
  can capture each created command. `bindHotkey` now pattern-matches
  on `(Some k, Some m)` to install the `KeyBinding` (skipping it
  for menu-only commands) but always registers the `CommandBinding`.
  A local `bind` wrapper inside compose populates a
  `Dictionary<AppCommand, RoutedCommand>` as each binding is
  created. After all 14 binds, a reflection-driven wiring step
  walks the dictionary and assigns each XAML-named
  `MenuItem_<nameOf cmd>` element's `Command` and
  `InputGestureText` properties.
- **`src/Views/MainWindow.xaml`** — full menu structure with
  mnemonics: `_Shell` (cmd, PowerShell, Claude), `_View` (debug
  log toggle, mute earcons), `_Data` (data folder, edit config,
  copy history, announce session-log path), `Dia_gnostics` (health
  check, incident marker, run battery), `_Help` (check for updates,
  draft new release). Each `MenuItem` carries `x:Name` matching
  `MenuItem_<AppCommandName>` and `x:FieldModifier="public"` so
  compose's reflection wiring can locate it.
- **`src/Views/MainWindow.xaml.cs`** — Cycle 26a's throwaway
  `Exit_Click` handler removed (no longer needed; the populated
  Help menu replaces the placeholder).

**Extensibility-as-a-design-constraint** (per maintainer directive):

Adding a new menu item is now three single-place edits per PR:

1. Add the `AppCommand` DU case + `nameOf` arm + `allCommands` row
   in `HotkeyRegistry.fs`.
2. Add the `builtIns` row (with `Some` for gesture-bearing or
   `None, None` for menu-only).
3. Add a `<MenuItem x:Name="MenuItem_<NewCommandName>" ... />` row
   in `MainWindow.xaml`.

Compose's reflection-driven wiring picks it up automatically. No
F# composition or test changes required for routine additions.

Adding or removing a default accelerator on an existing command is
now a one-field edit (`Some` ↔ `None`) rather than a DU-shape
change. Adding a new top-level menu (Window, Display,
Preferences/Settings — anticipated by the maintainer in
[`docs/PROJECT-PLAN-2026-05-09.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md))
is just a new `<MenuItem Header="_NewMenu">` block in XAML; no F#
changes.

**New invariant test**:

`tests/Tests.Unit/AppReservedHotkeysMirrorTests.fs` (new file).
Reflectively reads `TerminalView.AppReservedHotkeys` and asserts
F# / C# parity at test time:

- Every gesture-bearing entry in `HotkeyRegistry.builtIns` has a
  matching `(Key, ModifierKeys, Description)` row in
  `AppReservedHotkeys`.
- Every `AppReservedHotkeys` row has a matching `HotkeyRegistry`
  entry (no orphans).
- Counts match (catches "off by one" in the CI log).

This pins the load-bearing `OnPreviewKeyDown` filter contract at
test time — before Cycle 26b the parity was maintainer convention
only ("update both surfaces in the same PR"); a missed update would
silently demote a hotkey to "flows through to the shell as plain
text instead of firing its handler". The test catches that
regression immediately.

**Existing tests updated**:

`tests/Tests.Unit/HotkeyRegistryTests.fs` — every direct
`hk.Key` / `hk.Modifiers` comparison wrapped with `Some (...)` for
the new option type. `tryFind` round-trip now skips menu-only
commands (no gesture to round-trip). Collision-detection filters
to `Some` entries (menu-only `(None, None)` pairs would otherwise
collide trivially with each other). 4 new fixtures pin the
option-typing invariant + `gestureText` helper formatting.

**No code changes outside the listed files**. The `OnPreviewKeyDown`
filter in `TerminalView.cs:530-614` is untouched; the C#
`AppReservedHotkeys` array at `TerminalView.cs:346-489` is
untouched. The keyboard pipeline is unaffected.

**NVDA validation gates** (all land in Cycle 26d's matrix-row PR):

- Menu items announce `<name>, <gesture>` when focused
  (e.g. "Health Check, Ctrl plus Shift plus H").
- Pressing Enter on a menu item invokes the same handler as the
  keyboard gesture (verify via Health Check's same one-line
  summary).
- All 14 keyboard gestures still fire correctly with the menu in
  place (kbd pipeline unaffected).

### Added (Cycle 26a): app menu skeleton + UIA plumbing

First PR of Cycle 26 (the multi-PR app-menu mini-cycle planned in
[`docs/PROJECT-PLAN-2026-05-09.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md)).
Goal of the cycle: surface every `AppCommand` (14 reserved hotkeys
today) as a discoverable menu item with its keyboard shortcut shown
via `MenuItem.InputGestureText`, plus add a new menu-only
`RunProcessCleanupScript` command — relieving the maintainer's
hotkey-count working-memory ceiling and creating a natural surface
for future diagnostic scripts to plug in without burning hotkey slots.

This first PR lands the **structural skeleton** only; no command
wiring, no behaviour change beyond proving the menu renders + UIA
routes correctly.

- **`src/Views/MainWindow.xaml`**: wrap the existing `<Grid>` in a
  `<DockPanel>`. Add a top-docked `<Menu>` with
  `AutomationProperties.Name="Application menu"` and
  `AutomationProperties.AutomationId="pty-speak.AppMenu"` (mirrors
  the `pty-speak.<surface>` AutomationId convention used on
  `MainWindow` itself, future-proofing for FlaUI E2E per the
  parked Cycle 25b-2 design). The terminal view stays as the
  `LastChildFill` child so it expands to fill the remaining space.
- **Single throwaway menu item**: `_Help → E_xit` wired to a
  `Click` handler in `MainWindow.xaml.cs` that calls `Close()`.
  Its only purpose is to prove that menu Click events route
  end-to-end before Cycle 26b lands the populated-from-`HotkeyRegistry`
  structure. Cycle 26b deletes both the item and the handler.
- **`src/Views/MainWindow.xaml.cs`**: add the `Exit_Click` handler
  (deleted in Cycle 26b). Add an addendum to the existing focus-routing
  comment block (lines 44-62) explicitly noting that the Cycle 26a
  Menu does NOT compete for focus on `Loaded` — WPF Menu only claims
  focus on Alt press or explicit `Focus()`, and the `DockPanel`
  layout keeps `TerminalSurface.Focus()` routing to the document-role
  peer as before. The Cycle 26a NVDA matrix row pins this.
- **No code changes outside `src/Views/`**. F# code, hotkey registry,
  `OnPreviewKeyDown` filter, and the C# `AppReservedHotkeys` mirror
  are all untouched.
- **NVDA validation gate** (lands in Cycle 26d's matrix-row PR):
  Launch reads "pty-speak terminal {version}, document"; press Alt
  to summon the menu bar; arrow keys announce menu names; Esc
  returns focus and NVDA re-announces "document".

### Added (Cycle 25d): `PROJECT-PLAN-2026-05-09.md` dated successor + cross-reference sweep

Spawns the dated successor strategic plan
[`docs/PROJECT-PLAN-2026-05-09.md`](docs/archive/pre-cycle-45/PROJECT-PLAN-2026-05-09.md)
per the Track E E5 dated-snapshot discipline. The preceding
`PROJECT-PLAN-2026-05-revision.md` (snapshot 2026-05-07)
became three implementation cycles stale once Tier 1
SessionModel substrate (Cycles 11-22b, PRs #185-#199), Tier 2
persistence (Cycles 24a-24g, PRs #203-#212+#219), and Cycle 25
operational ergonomics + diagnostic-dump bundle (PRs #220-#222)
all shipped end-to-end with NVDA-validation green between
2026-05-07 and 2026-05-09. The 2026-05-07 revision's "Next
stage" pointer (SessionModel Tier 1) was satisfied; the
SESSION-HANDOFF "Where we left off" cell flagged the gap
explicitly.

The new successor:

- Captures the post-Tier-2 + post-Cycle-25 status with
  per-PR shipped-since enumeration, audit health refresh,
  open-questions resolved/remaining tally.
- Points next stage at **Cycle 26 — app menu**
  (maintainer-chosen 2026-05-09) as the working-memory-
  pressure-relief cycle gating further hotkey-bound features.
  Includes a sketch of the menu architecture decisions to
  resolve in plan mode (hotkey-source-of-truth, NVDA
  menu-mode patterns, `OnPreviewKeyDown` filter ordering
  invariant under menu accelerators).
- Sequences Output framework Part 3 → Input framework Part 4
  → Stage 10 after Cycle 26 per the original strategic plan.
- Parks Cycle 25b-2 (FlaUI/UIA E2E runner) with the design
  sketch retained in `SESSION-HANDOFF.md`.
- Establishes a YYYY-MM-DD naming convention for further
  successors going forward.

Cross-reference sweep across 5 files where the
`-revision.md` reference was active:
[`CLAUDE.md`](CLAUDE.md) reading-order; [`README.md`](README.md)
status note; [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md)
"Authoritative plan" + "In-flight branch" + "Next stage" cells +
recommended-reading-order; [`docs/DOC-MAP.md`](docs/DOC-MAP.md)
table row + per-audience entry-point list;
[`docs/SESSION-MODEL.md`](docs/SESSION-MODEL.md) §Tier 6
vocabulary-bridge note. Two referencing files
(`AUDIT-DOC-CURRENCY.md` and CHANGELOG entries) deliberately
left untouched — those references are inside dated-snapshot
artifacts that don't update post-snapshot per the same
discipline.

Doc-only PR; no code change.

### Changed (Cycle 25b-1a): bundle gains session-log section + Ctrl+Shift+L removed

Follow-up to 25b-1 (PR #221) after the maintainer validated the
bundle format on a real Ctrl+Shift+D press:

- **New `--- SESSION LOG ---` section** in the bundle, between
  `CONFIG.TOML` and `ENVIRONMENT`. Carries the same
  `Session log mode <mode>; path <full-path>.` line that
  `Ctrl+Shift+S` announces, surfaced as a first-class
  triage line so the maintainer doesn't have to grep the
  FileLogger log slice for the active session-log path.
  Single source of truth: `Program.fs.buildSessionLogSummary`
  is consumed by both the S handler and D's
  `resolveSessionLogSummary` closure.

- **`Ctrl+Shift+L` (CopyLatestLog) removed entirely**. The D
  bundle's `--- FILELOGGER ACTIVE LOG ---` section subsumes
  L's payload — having a dedicated copy-just-the-log hotkey
  was redundant given the bundle's coverage. Removed:
  `HotkeyRegistry.AppCommand.CopyLatestLog` DU case + `nameOf`
  arm + `builtIns` row + `allCommands` entry; the
  `runCopyLatestLog` handler in `Program.fs` (~135 lines
  including the dispatcher-deadlock-fix Task wrapper); the
  `bindHotkey` line; the `SetCopyLogToClipboardHandler`
  defense-in-depth wiring; the C# `_copyActiveLogToClipboard`
  field, `SetCopyLogToClipboardHandler` setter, the
  `Key.OemSemicolon` direct handler in `OnPreviewKeyDown`,
  and the `(Key.L, ...)` row in `AppReservedHotkeys`. Tests
  and `CLAUDE.md` / `ACCESSIBILITY-TESTING.md` updated to
  point at D for log-paste flows. Per the maintainer's
  hotkey-count-pressure feedback (working-memory ceiling).

### Changed (Cycle 25b-1): Ctrl+Shift+D bundles diagnostic dump + Ctrl+Shift+T placeholder removed

`Ctrl+Shift+D` (autonomous diagnostic battery) now writes a
combined diagnostic-dump bundle into a dated snapshot file at
`%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\snapshot-<yyyy-MM-dd-HH-mm-ss-fff>.txt`
**and** copies the bundle to clipboard (instead of just the
diagnostic-battery log). The bundle includes:

- The diagnostic-battery log (existing per-shell command results,
  earcon replay outcome, T0 process snapshot, SessionModel
  substrate state).
- The active FileLogger log slice (carries Cycle 24f / 24g
  `Config:` parse messages, the runtime heartbeat trail, and
  any error-path log lines).
- The literal `config.toml` content (or "(file not present)").
- The current process environment with deny-list redaction
  (`*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`, `*_PASSWD` —
  values redacted to `<redacted by suite>`; `ANTHROPIC_API_KEY`
  exempted; names always shown verbatim).
- A header with capture timestamp, pty-speak version, OS, .NET
  runtime, process ID.

Format is plain text with `--- SECTION ---` markers between
sections so future replay tools can index content by section.
Designed for one-keystroke paste-back to triage chat: the bundle
carries everything the maintainer needs to triage a Cycle 24
matrix-row failure or a runtime regression in one block.

`Ctrl+Shift+T` (the `RunTestMatrix` placeholder shipped in 25a)
is removed entirely — the `AppCommand` DU case, the `nameOf` arm,
the `builtIns` row, the C#-side `AppReservedHotkeys` row, the
`runTestMatrix` `ActivityId`, and the `runRunTestMatrix`
placeholder handler in `Program.fs`. The diagnostic suite folds
into `Ctrl+Shift+D` rather than splitting across two hotkeys per
the maintainer's "everything that can be automated goes into D"
directive. The interactive `test-process-cleanup.ps1` (which
requires the maintainer to physically close pty-speak via Alt+F4
/ X-button) stays in the repo but is invoked manually from
PowerShell rather than hotkey-launched; a future cycle's app
menu will surface it.

The Cycle 25b plan originally bundled this with a UIA-driven
FlaUI test runner; the dump-bundle half ships first (this PR)
and the FlaUI runner lands as a separate PR (25b-2) so the
maintainer can validate the bundle format before committing to
the larger E2E test infrastructure.

### Added (Cycle 25a): hotkey reorg + open-config + reload-on-shell-switch + [logging] TOML

Operational ergonomics bundle landed in response to the friction
surfaced by the first manual NVDA-matrix walkthrough of Cycle 24:

**Hotkey reorg**:

- `Ctrl+Shift+L` — repurposed: was `OpenLogsFolder`, now
  `CopyLatestLog` (mnemonic: **L** for **L**og).
- `Ctrl+Shift+P` — NEW `OpenDataFolder` (opens
  `%LOCALAPPDATA%\PtySpeak\`, the parent of `\logs`,
  `\sessions`, and `config.toml`).
- `Ctrl+Shift+E` — NEW `OpenConfig` (auto-creates
  `config.toml` with sensible defaults if missing, then
  opens in the default app).
- `Ctrl+Shift+T` — NEW `RunTestMatrix` (placeholder; full
  implementation in Cycle 25b).
- `Ctrl+Shift+;` — vacated entirely (clean removal,
  no alias).
- `OpenLogsFolder` `AppCommand` — deleted (replaced by
  `OpenDataFolder` which lands in the parent and is one
  arrow-key step from the logs subfolder).

**Open-config auto-create**: `Ctrl+Shift+E` writes a
boilerplate `config.toml` with all four documented sections
(`[session_model.persistence]`, `[startup]`, `[logging]`,
plus `schema_version`) and inline comments explaining each
knob. UTF-8 no-BOM (`UTF8Encoding(false)`) per the
`SessionLogWriter` precedent. Idempotent: refuses to
overwrite an existing file. Solves the
"maintainer hand-types a TOML header and gets it wrong"
failure mode the Cycle 24 walkthrough surfaced (the
maintainer wrote `[sessionmodel._persistence]` instead of
`[session_model.persistence]` — character placement off
by one — and couldn't see the typo via NVDA's underscore
pronunciation).

**New `[logging]` TOML section**: `min_level` enum
(`Trace` / `Debug` / `Information` / `Warning` / `Error` /
`Critical` / `None`). Precedence mirrors the established
`[startup] default_shell` / `PTYSPEAK_SHELL` pattern:
`PTYSPEAK_LOG_LEVEL` env var > TOML `min_level` >
built-in default `Information`. Resolved at composition-root
time; the FileLogger sink's `SetMinLevel` is called
post-Config-load when the TOML value should win.
`Ctrl+Shift+G` runtime toggle remains ephemeral (doesn't
persist; next launch re-resolves the precedence).

**Reload-on-shell-switch**: every `Ctrl+Shift+1/2/3`
re-reads `config.toml` and applies `[session_model.persistence]`
changes to the new shell session's writer. Lets the
maintainer edit TOML mid-session and validate with one
keystroke instead of relaunching. `default_shell` /
`[logging]` / `[pathway.stream]` stay startup-only this
cycle (documented in `docs/SESSION-MODEL.md` "Config
reload on shell switch"). `SessionSanitiser` re-runs
`registerFromEnvironment` on every switch; idempotent by
design.

**Tests**: 5 new `[<Fact>]`s in `HotkeyRegistryTests.fs`
pinning new bindings + the vacated `Ctrl+Shift+;`. 6 new
in `ConfigTests.fs` covering `[logging]` parsing (valid /
invalid / case-insensitive / unknown-key / non-string).
5 new in `ConfigTests.fs` covering `Config.writeDefaults`
(write / idempotent / no-BOM / round-trip-no-warnings /
parent-directory-creation).

**Cycle 25b** (next): full UIA-based test runner driving
all six matrix rows + a combined diagnostic-dump file
(maintainer-requested) saved under
`%LOCALAPPDATA%\PtySpeak\diagnostic-snapshots\`
date-stamped + copied to clipboard.

### Added (Cycle 24g): TOML model snapshot logged at startup

Diagnostic surface to pin the difference between "config section
absent from the parsed Tomlyn model" and "config section present
but keyed differently than my reader expected" — the case Cycle
24f's per-branch logging surfaced but couldn't disambiguate
without further investigation.

`Config.tryLoad` now logs the **full parsed TOML model** as a
JSON-shaped hierarchical dump immediately after `Toml.ToModel()`
returns, BEFORE any per-section reader runs. A maintainer can:

- See exactly which top-level keys Tomlyn produced — pinning
  encoding (BOM), dotted-key, and typo issues that are
  otherwise invisible (the file parses cleanly, the section
  just isn't where the per-section reader looks).
- Compare the dump's structure against the source TOML to spot
  invisible characters or unexpected representations.
- Forward the log slice (via `Ctrl+Shift+;`) for triage without
  having to manually transcribe the file content.

JSON-shaped (not TOML re-emit) so subtleties like `[a.b.c]`
dotted-header → nested-table representation are explicit. Keys
quoted; embedded quotes/control chars escaped per JSON rules.
Unrecognised value types render as `"<<TypeName: value>>"` so
future Tomlyn types don't crash the dump.

The on-demand variant (hotkey-driven snapshot copy-to-clipboard
plus unknown-keys flagging) is planned for the upcoming Cycle
25 PR; this 24g surface is the no-new-hotkey unblocker that
captures the model on every launch.

10 new xUnit `[<Fact>]` tests in `ConfigTests.fs` covering empty
table, scalar types, nested dotted-headers, escaping, indent
shape, comma separation, empty sub-tables, and inline arrays.

### Fixed (Cycle 24f): SessionModel persistence config diagnostic + matrix section-name typo

Two paired fixes surfaced during the first manual NVDA-matrix
walkthrough of Cycle 24:

- **`docs/ACCESSIBILITY-TESTING.md` — section-name typo.** The
  Cycle 24 matrix (PR #211) instructed the maintainer to write
  `[session_persistence]` in `config.toml`. The actual TOML
  schema (Cycle 24a, PR #203) uses the **nested** form
  `[session_model.persistence]`. The maintainer's TOML was
  effectively invisible and the persistence silently fell
  through to `memory_only`. Matrix updated to use the canonical
  section name; the matrix's "Diagnostic decoder" entry now
  enumerates the new Cycle 24f log lines and the legacy
  `[session_persistence]` typo as the most-likely culprit.
- **`SessionPersistence.parseFromTable` — diagnostic gap.** All
  three parse-branches (no `[session_model]`, `[session_model]`
  present but no `.persistence` sub-section,
  `[session_model.persistence]` present and parsed) now emit a
  distinct **Information** log line so a maintainer can grep
  `Config:` in the FileLogger log and immediately distinguish
  "section absent → silent defaults" from "section parsed
  cleanly to memory_only" from "value rejected → fell back".
  Pre-24f all three were observationally identical (only the
  composition root's `SessionModel persistence mode: ...` line
  was emitted). New messages:
  - `Config: no [session_model] section in TOML; using
    session-persistence defaults (mode=memory_only).`
  - `Config: [session_model] present but no
    [session_model.persistence] sub-section; using
    session-persistence defaults (mode=memory_only).`
  - `Config: [session_model.persistence] section parsed;
    mode=<X>, output_dir=<Y>, format=jsonl,
    max_session_size_mb=<N>.`

`SessionPersistenceTests.fs` updated (3 existing tests extended
+ 1 new test) to pin each branch's Information message.

### Added (Cycle 24e): diagnostic hotkey + NVDA matrix for SessionModel persistence

Closes Cycle 24 with three small operational deliverables on
top of the substrate shipped in 24a-24d:

- **`Ctrl+Shift+S`** — announce the active session-log file
  path via NVDA. Verbose format: `Session log mode <mode>;
  path <full-path>.` for `session_log` / `always`;
  `Session log mode memory_only; no file.` for
  `memory_only`. Long but unambiguous — the screen-reader
  user can pause/repeat NVDA to capture the path. The
  alternative (opening Explorer to find the file) is a
  dialog-tree GUI walk and is unacceptable per the
  screen-reader contract. Mnemonic: **S** for **S**ession
  log. Companion to `Ctrl+Shift+L` (open file-logger root)
  and `Ctrl+Shift+;` (copy active log to clipboard).
- **Six new rows in `docs/ACCESSIBILITY-TESTING.md`** under
  a new "Cycle 24 — SessionModel persistence" subsection:
  mode change at startup, file creation in `session_log`
  mode, `Always` synchronous-flush perceptibility,
  diagnostic hotkey output, env-var redaction in persisted
  file, `Ctrl+Shift+Y` substrate honesty (in-memory History
  stays unsanitised; only the persistence layer redacts).
  Each row includes a `Diagnostic decoder` entry tying
  failures to likely subsystems for triage.
- **`docs/CHECKPOINTS.md`** — adds
  `baseline/cycle-24-sessionmodel-persistence` to both the
  "Current checkpoints" table (with PR links #203 / #206 /
  #208 / #209 / #210 + the 24e PR) and the
  "Pending checkpoint tags" table (sandbox blocks
  `refs/tags/`; maintainer pushes from their workstation).

Implementation notes:

- New `HotkeyRegistry.AnnounceSessionLogPath` AppCommand;
  exhaustive `nameOf` match catches forgotten cases at
  compile-time under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Parallel C# mirror in `src/Views/TerminalView.cs
  AppReservedHotkeys` keeps the hot-path keystroke filter
  in sync.
- New `ActivityIds.sessionLogPath = "pty-speak.session-log
  -path"` so consecutive presses dedupe at the NVDA-channel
  layer (per-tag dedupe behaviour mirrors `healthCheck`
  and `incidentMarker`).
- 1 new xUnit `[<Fact>]` in `HotkeyRegistryTests.fs`
  pinning the binding (`Letter 'S'` + `Ctrl+Shift`).
  The handler logic is too intertwined with the composition
  root's mutable state to unit-test directly without a
  major refactor; the NVDA matrix row is the integration
  test for the announcement text.

### Added (Cycle 24d-2): env-var value sanitisation for SessionTuple persistence

Closes the Cycle 24d sub-cycle. Adds VALUE-based redaction
of env-var-derived secrets in persisted SessionTuple text
fields, complementing Stage 7's NAME-only env-scrub at the
child-process spawn boundary.

**Threat model**: a shell expands an env var into output —
e.g. `echo $GITHUB_TOKEN` substitutes the literal token
value `ghp_abc123...` into stdout. The terminal sees the
expanded value (NOT the variable name); without sanitisation
the SessionLogWriter would persist that value to the
on-disk JSONL. NAME-only matching can't catch this case;
once the shell has expanded, the literal name is gone.

**What ships**:

- New `Terminal.Core.SessionSanitiser` module with public
  surface: `register`, `registerFromEnvironment`, `sanitise`,
  `sanitiseTuple`, `clear` (for tests), `MinValueLength`,
  `isDenied` (internal — Stage 7 deny-list parity).
- Composition root in `Program.fs` calls
  `registerFromEnvironment` at startup unconditionally
  (cheap; the registered values are only consulted by the
  sink's `writeOne`, which never runs when no sink exists).
  Per `LOGGING.md`, the registration count is logged at
  Information level — never the names or values.
- `SessionLogWriter.writeOne` calls
  `SessionSanitiser.sanitiseTuple` before
  `SessionModel.formatTupleAsJsonl`. The substrate's
  in-memory History keeps unsanitised text (user can
  recover their own commands via `Ctrl+Shift+Y`); only the
  persistence layer redacts.

**Locked design** (per the Cycle 24d-2 plan):

- **Deny-list pattern**: lifted verbatim from
  `Terminal.Pty.Native.isDenied` (Stage 7 PO-5). Suffix
  match on uppercase names: `*_TOKEN`, `*_SECRET`, `*_KEY`,
  `*_PASSWORD`. `ANTHROPIC_API_KEY` exempted (Claude Code's
  primary credential, same precedent).
- **Min-length threshold**: 16 chars. Avoids false
  positives on short common values (e.g. `BANK_API_KEY=admin`
  would NOT register).
- **Redaction marker**: `<REDACTED:UPPERCASE_NAME>`.
  Decades-stable; angle brackets are unambiguous in shell
  output; colon-delimited for regex parseability by future
  replay tools.
- **Substring-overlap safety**: registered values sorted by
  length DESC so the longer value is matched first if one
  is a substring of another.
- **Sanitised fields**: `CommandText`, `OutputText`,
  `PromptText`, and every `ExtraParams` value. Conservative.
- **Threading**: lock-guarded module-level state; safe from
  any thread.

**21 new xUnit `[<Fact>]` tests** in
`tests/Tests.Unit/SessionSanitiserTests.fs` cover:
- `MinValueLength` threshold behaviour.
- Empty / whitespace value skip.
- Marker format pinning.
- Single + multi-value redaction.
- Same-value-multiple-times handling.
- Substring-overlap safety (longer wins).
- `isDenied` parity with Stage 7 — every pattern + the
  `ANTHROPIC_API_KEY` exemption + non-match cases
  (`KEYBOARD_LAYOUT`, `KEYS_TO_THE_KINGDOM`, `PATH`).
- `sanitiseTuple` field application (CommandText,
  OutputText, PromptText, ExtraParams values, key
  pass-through, non-text-field invariance).
- `clear` test isolation.
- `registerFromEnvironment` happy path + non-match skip +
  short-value skip.

`docs/SESSION-MODEL.md` adds an "Env-var-value sanitisation
(Cycle 24d-2)" sub-section under §"Persistence modes"
documenting the threat model, registration source, marker
format stability, and known limitations (registered at
startup; not retroactively scanned).

**Limitations** (documented in SESSION-MODEL.md):
- Env vars set after launch are NOT retroactively scrubbed.
- Pattern-based detection (e.g. AWS-key regex
  `^AKIA[0-9A-Z]{16}$`) is out of scope; future cycles can
  add this if demand surfaces.

### Changed (Cycle 24d-1): SessionLog file writer — `Always` mode synchronous flush

Implements true audit-grade durability for the `Always`
persistence mode introduced as a config substrate in Cycle
24a. Cycle 24c shipped the file writer with `Always` degraded
to `SessionLog` async semantics + a Warning at startup; this
PR removes the degradation and the Warning, and adds the
synchronous-flush path.

**What ships**:

- New `SessionLogWriterSink.EnqueueSync : SessionTuple -> unit`
  — blocks the SessionModel state-machine call path until
  the tuple is durable on disk (`StreamWriter.Flush` returned
  successfully). 10-second timeout with graceful degradation:
  on timeout, log a Warning and return (the tuple is still
  queued; the state machine continues; the user gets a
  noticeable hitch instead of a hung UI).
- New internal `SessionLogWriteRequest` record (`{ Tuple;
  CompletionSignal: TaskCompletionSource<unit> option }`) is
  the channel payload. `Enqueue` (existing API for
  `SessionLog` mode) sets `CompletionSignal = None`;
  `EnqueueSync` (new API for `Always` mode) sets `Some tcs`
  and awaits it.
- Drain task accumulates per-batch `pendingSignals` and signals
  them after the single `StreamWriter.Flush` returns. On
  flush failure, faults all pending TCSs with the same
  exception so sync callers learn fast; on success, completes
  them all. Same FIFO order is preserved regardless of
  CompletionSignal presence.
- Composition root in `src/Terminal.App/Program.fs` introduces
  a `dispatchTupleToWriter` helper used by both
  `handlePromptBoundary` and the shell-switch path:
  `MemoryOnly` → no-op; `SessionLog` → `Enqueue`; `Always` →
  `EnqueueSync`. The Cycle 24c "Always not yet implemented"
  Warning is removed.
- 5 new xUnit `[<Fact>]` tests in
  `tests/Tests.Unit/SessionLogWriterTests.fs` covering:
  on-disk-after-return durability, post-Dispose graceful
  return (no hang), mixed Enqueue + EnqueueSync FIFO
  ordering, lone-surrogate skip-without-break for sync
  callers, multi-thread concurrent EnqueueSync.

**Failure modes**:

- Serializer error (lone surrogate per Cycle 24b): drain task
  faults the TCS via `TrySetException`; `EnqueueSync`
  catches via `AggregateException`, logs Warning, returns.
  Subsequent `EnqueueSync` calls unaffected.
- Disk stall: 10s timeout fires; Warning logged; method
  returns. Tuple stays queued and writes when the disk
  recovers.
- Sink `Dispose` while a sync caller is awaiting: drain's
  final-pass either writes the tuple (signalling success) or
  the channel is cancelled (caller unblocks via
  `OperationCanceledException`).

Cycle 24d-2 (still-pending) adds env-var VALUE-based
sanitisation for `commandText` / `outputText` / `promptText`
/ `extraParams` before write.

### Added (Cycle 24c): bounded-channel async file writer for SessionTuple JSONL persistence

Third sub-cycle of the Tier 2 SessionModel persistence cycle.
Wires the actual on-disk writer that consumes
`SessionModel.formatTupleAsJsonl` (Cycle 24b, PR #206) and
appends each finalised `SessionTuple` as one JSONL line to a
per-shell-session file.

**What ships**:

- New `Terminal.Core.SessionLogWriterSink` — a class that owns
  one bounded channel + one background drain task + one open
  `StreamWriter` per shell session. Mirrors the `FileLogger`
  sink pattern (`src/Terminal.Core/FileLogger.fs:120-455`).
- New `Terminal.Core.SessionLogWriterOptions` record + helper
  module with `createDefault` resolution (output-dir override
  from TOML config, fallback to
  `%LOCALAPPDATA%\PtySpeak\sessions\`).
- New `SessionModel.applyAndCapture` and
  `SessionModel.finalizeIncompleteAndCapture` —
  tuple-returning variants of `apply` and `finalizeIncomplete`
  that surface the freshly-finalised `SessionTuple` (when this
  call resulted in an Active→History transition) so the
  composition root can dispatch to the writer. The original
  `apply` and `finalizeIncomplete` stay as thin wrappers
  preserving the existing public API for 81+ existing test
  callers — no test churn.
- Composition-root wiring in `src/Terminal.App/Program.fs`:
  - Sink constructed at startup IF persistence mode is
    `SessionLog` or `Always`. `MemoryOnly` skips construction
    entirely; the sink is `None` and dispatch is a no-op.
  - Sink lifecycle: one-per-shell-session. Shell-switch
    (`Ctrl+Shift+1/2/3`) flushes the outgoing shell's
    finalize-incomplete tuple, disposes the old sink, then
    constructs a fresh sink for the new SessionId.
  - Sink disposal in `app.Exit` — flushes pending writes
    before the FileLogger provider tears down.

**Locked design** (per the Cycle 24c plan):

- `BoundedChannelFullMode.Wait`. If the channel ever fills
  (disk stall), enqueue back-pressures into the SessionModel
  state machine. Decades-stable bias: losing a tuple is worse
  than a brief UI hitch. In practice the back-pressure path
  should never trigger — `applyAndCapture` fires at most once
  per command (~1-10 Hz peak); 256-capacity channel drains at
  >>1000 lines/sec on a working disk.
- `MemoryOnly` mode: writer never opens a file.
- `Always` mode: not yet implemented this cycle. The
  composition root logs a Warning at startup and treats
  `Always` identically to `SessionLog` (async flush). True
  synchronous-flush semantics ship in Cycle 24d.
- File path: `<output_dir>/session-<SessionId>.jsonl`.
- No UTF-8 BOM (`UTF8Encoding(false)`) — preserves the
  byte-for-byte stability the wire format promises.
- Silent error handling — file I/O exceptions logged via
  `ILogger`, never thrown into the NVDA path.
- Lone-surrogate tuples (per the Cycle 24b serializer
  contract) are logged + skipped without breaking the writer
  for subsequent tuples; pinned by a test.

**13 new xUnit `[<Fact>]` tests** in
`tests/Tests.Unit/SessionLogWriterTests.fs` cover path
resolution, single + multi-tuple enqueue, LF-only line
endings, disposal flush + idempotency, output-dir creation,
lone-surrogate skip, concurrent-writer thread safety, and the
`SessionLogWriterOptions.createDefault` resolution chain.

`docs/SESSION-MODEL.md` adds a sentence under §"On-disk wire
format (Cycle 24b)" noting that Cycle 24c ships the writer
that calls the serializer on the Active→History seam.

### Added (Cycle 24b): pure JSONL serializer for SessionTuple

Second sub-cycle of the Tier 2 SessionModel persistence cycle.
Adds `SessionModel.formatTupleAsJsonl : SessionTuple -> string`
in `src/Terminal.Core/SessionModel.fs` — a pure function that
emits one JSONL line (a JSON object followed by a literal `\n`
terminator). **No I/O is wired this sub-cycle**; Cycle 24c
adds the bounded-channel async writer that calls this serializer
on the Active → History transition.

The wire format is **decades-stable**. Locked design decisions
(enforced by 39 xUnit `[<Fact>]` tests in
`tests/Tests.Unit/SessionModelJsonlTests.fs`):

- **Per-record `"schemaVersion":1`** as the first key on every
  record. Future schema changes increment the value; replay
  tools branch on it; old files always remain readable.
- **`BoundarySource` is always a tagged object**:
  `{"kind":"<case>",...payload}` — uniform shape across all
  DU cases, future-proof for new cases.
- **`sources` is an array of records, not an object**:
  `[{"boundary":"PromptStart","source":{...}},...]`. Sorted by
  an explicit `boundaryOrdinal` (PromptStart=0, ...,
  CommandFinished=3) — avoids the latent collision bug where
  `BoundaryKind.CommandFinished _` payload variants would
  alias to the same JSON key. Diverges from earlier
  illustrative SESSION-MODEL.md examples.
- **DateTime serialisation**: ISO-8601 UTC with 100ns ticks
  (`yyyy-MM-ddTHH:mm:ss.fffffffZ`). Lossless from the Windows
  clock.
- **Hand-rolled** (no JSON library). The codebase has zero JSON
  dependencies; F# `option` and DUs would require custom
  System.Text.Json converters that grow per type. Hand-rolling
  gives byte-stable output across .NET versions and full control
  over field ordering.
- **String escapes**: RFC 8259 minimum + DEL (`0x7F` →
  ``, deliberate superset).
- **Lone UTF-16 surrogates throw at emit time** — silent
  corruption of a decades-old log is the worst possible failure
  mode.
- **Trailing terminator is the literal `"\n"`**, never
  `Environment.NewLine`.

`docs/SESSION-MODEL.md` adds a comprehensive "On-disk wire
format (Cycle 24b)" sub-section under §"Persistence modes"
documenting all locked decisions for future contributors.

The test suite includes a `JsonDocument` oracle test that
parses every emitted line through the standard library's JSON
parser to catch escape bugs the per-field tests miss.

### Added (Cycle 24a): SessionModel persistence config substrate (TOML schema, no I/O)

First sub-cycle of the **Tier 2 SessionModel persistence**
implementation cycle (the maintainer's chosen Phase 4 fork
from Cycle 23). Introduces the `[session_model.persistence]`
TOML table and parses it into a typed `PersistenceConfig`
record; **no I/O is wired this sub-cycle.** Cycles 24b–24e
add the JSONL serializer, file writer, `always`-mode
synchronous flush + secrets sanitisation, and the
NVDA-validation matrix rows in turn.

**Schema** (all fields optional; absence = pre-Cycle-24a
behaviour, byte-equivalent):

```toml
[session_model.persistence]
mode = "memory_only"        # memory_only / session_log / always
output_dir = ""              # empty → default %LOCALAPPDATA%\PtySpeak\sessions\
format = "jsonl"             # jsonl (only value today)
max_session_size_mb = 64
```

**What ships**:

- New `Terminal.Core.SessionPersistence` module
  (`PersistenceMode` / `PersistenceFormat` DUs +
  `PersistenceConfig` record + `defaultConfig` +
  `parseFromTable` + `modeToString` / `formatToString`
  helpers).
- Extends `Config.Config` with a `SessionPersistence` field;
  `Config.tryLoad` invokes `parseFromTable` against the
  loaded TOML root table, mirroring the existing
  `parseStartupOverrides` warn-and-fall-back pattern.
- `Program.fs` composition root logs the resolved mode at
  `Information` level once at startup, so `Ctrl+Shift+;`
  log capture confirms the user's TOML opt-in actually took
  effect.
- 18 xUnit `[<Fact>]` tests pin every parser branch
  (defaults, each valid value, case-insensitivity, unknown
  keys, bad-typed values, empty/non-positive numerics,
  `modeToString`/`formatToString` round-trips, full-table
  composition).
- `docs/USER-SETTINGS.md` adds a "SessionModel persistence"
  section in the same shape as the existing "Default shell"
  and "Pathway selection" entries.

**Bundled doc-currency fix** (≤ 20 LOC of doc edits in the
same PR): mark the now-stale "Cycle 23 in-flight handoff"
section in `docs/SESSION-HANDOFF.md` as historical (Cycle 23
phases 1–4 all shipped 2026-05-08 via PRs #200 + #201) and
refresh the "In-flight branch" / "Next stage" cells in the
top-of-doc summary table to point at Cycle 24a.

### Changed (Cycle 24-pre): tighten CLAUDE.md CI-completion tone guidance

**Doc-only PR; no code changes.** Add a one-paragraph
"Preferred response style" rule to `CLAUDE.md` under
the existing "PR creation and webhook subscription"
working-rules subsection. When all CI checks are green,
"all three green, merging" is the preferred terseness
— no play-by-play summary needed.

### Changed (Cycle 23 Phase 3): resolve CUSTOMIZATION-MODEL open questions in research-stage doc

**Doc-only PR; no code changes.** Apply maintainer's
agreed answers from the Cycle 23 Phase 2 walk-through
(2026-05-08) to `docs/CUSTOMIZATION-MODEL.md`.
**All 7 open questions resolved.**

Small mechanical PR following the audit→fixup loop
pattern established by Track A (PR #175) and previously
applied to research-stage doc question-resolution by
PR #182 (Cycle 8).

**Resolutions**:

- **Q1 Naming** → KEEP `CUSTOMIZATION-MODEL.md`.
- **Q2 Alternatives registry shape** → both compile-time
  built-ins + runtime `.fsx` extensions
  (`%LOCALAPPDATA%\PtySpeak\extensions\`); reuses spec
  A.5 phase-2 input-side extension plumbing for outputs.
- **Q3 Trace persistence** → in-memory per SessionTuple
  by default; opt-in persistent ring buffer for forensic
  debugging.
- **Q4 Rule-context-keying scope** → full `ContextTuple`
  enumeration at substrate level; rule-authoring UX
  defaults to subset (shell + command-prefix +
  semantic-category).
- **Q5 UI surface** → Pipeline Inspector pane as new
  PANE-MODEL catalog entry; modal panel as fallback.
- **Q6 Rule precedence** → explicit user-priority field;
  default priority computed from context-specificity at
  rule-creation time.
- **Q7 Rule-authoring UX** → start with dropdowns +
  automatic context-tuple suggestion; DSL + visual
  editor later as power-user tooling.

**Edit pattern** (matches PR #182 Cycle 8):
- Section header `## Open questions` →
  `## Open questions / Resolutions`.
- Each Q1-Q7 section heading gets ` — ✅ Resolved
  2026-05-08` appended.
- **Resolution** + **Rationale** + (where applicable)
  **Cross-reference** + **Original question** blocks
  per question.
- Original question prose + alternatives preserved
  verbatim for historical context.
- Doc change-log row appended for 2026-05-08.

**Companion-doc work deferred** (per #181/#182 audit→
fixup loop precedent — companion-doc edits land in
their own follow-up cycle when downstream
implementation needs them):
- `docs/PANE-MODEL.md` Pipeline Inspector pane catalog
  entry (Q5).
- `docs/USER-SETTINGS.md` override-rule schema notes
  (Q2/Q4/Q6/Q7).
- `docs/SESSION-MODEL.md` trace-metadata persistence
  paragraph (Q3).
- `docs/AUDIT-BACKLOG-VALIDATION.md` item 31 status
  advance.

**Sequencing position**: Cycle 23 Phase 3 of 4. Phase 4
is the maintainer's choice of next coding cycle:
Tier 2 SessionModel persistence (default) or Phase 2
input framework cycle.

### Changed (Cycle 23): doc currency refresh post-Tier-1 closure

**Doc-only PR; no code changes.** Tier 1 SessionModel
implementation cycle shipped end-to-end via PRs #185-#199
(Cycles 11-22b inclusive). Six handoff / sequencing /
architecture docs accumulated currency drift relative to
the new state; this PR refreshes them in a single sweep.

**Findings refreshed**:
- 4 plan-doc routing fixes
  (`docs/SESSION-HANDOFF.md`, `CLAUDE.md`, `docs/DOC-MAP.md`,
  `README.md`) — references now point to
  `docs/PROJECT-PLAN-2026-05-revision.md` (the 2026-05-07
  revision) instead of the original
  `PROJECT-PLAN-2026-05.md`. Original plan stays as
  historical 2026-05-03 snapshot per Track E E5 dated-
  snapshot discipline.
- `docs/SESSION-HANDOFF.md` "Last merged stages" / "In-flight
  branch" / "Next stage" rows refreshed to reflect Tier 1
  closure (PR #199) + Cycle 23 doc cleanup as current work
  + Tier 2 persistence / Phase 2 input framework as the next
  maintainer-choice fork.
- `docs/ARCHITECTURE.md` currency note extended with the
  Tier 1 PR range (#185-#199) and mention of CHANNEL-
  ARCHITECTURE as the sixth research-stage doc.
- `docs/ROADMAP.md` Stage 7 row footnote extended to mention
  the post-Stage-7 substrate cycle + Tier 1 SessionModel
  implementation cycle as continuation work.
- `docs/AUDIT-BACKLOG-VALIDATION.md` per-item statuses
  refreshed: items 25, 25-F, 28 advanced to ✅ shipped;
  new rows for items 31 (CUSTOMIZATION-MODEL research stage,
  PR #181), 32 (CHANNEL-ARCHITECTURE research stage, PR #193),
  33 (Default-shell config option, PR #194). Findings summary
  recomputed (14 ✅ / 3 ✅+ / 11 📋 / 3 ⏸ / 2 🔄 / 0 ❓).
  Change log appended with a 2026-05-09 refresh row; doc
  front-matter date preserved at 2026-05-07 per snapshot
  discipline.
- `docs/PROJECT-PLAN-2026-05-revision.md` change log appended
  with a 2026-05-09 row noting Tier 1 cycle shipped + body
  preserved per snapshot discipline.

**Total**: ~80 LOC across 9 doc files. CI gates: markdown
link check + workflow lint + build (no code changes —
no-op).

**Sequencing position**: Cycle 23 Phase 1 of 4. After this
PR merges, Phase 2 (in-session maintainer Q&A walk-through
of the 7 still-open CUSTOMIZATION-MODEL questions) → Phase 3
(Q&A resolution PR mirroring PR #182 Cycle 8 shape) →
Phase 4 (maintainer chooses next coding cycle: Tier 2
SessionModel persistence default OR Phase 2 input framework
alternative).

### Added (Cycle 22b): Ctrl+Shift+Y — copy SessionModel history to clipboard

**New hotkey.** `Ctrl+Shift+Y` (mnemonic: **Y** for histor**Y**)
copies the full SessionModel — every completed tuple in
`History` plus any in-flight active tuple — to the clipboard
as structured plain text. Paste-friendly into chat / bug
reports.

**Companion to `Ctrl+Shift+D`** (diagnostic battery): the
diagnostic announces a substrate summary; `Ctrl+Shift+Y`
dumps the full content for analysis. Where the diagnostic
log truncates `CommandText` / `OutputText` to 80 chars to
keep log lines scannable, the clipboard format preserves
**full content verbatim** — 100 tuples × ~500 chars ≈ 50KB,
well within clipboard limits.

**Format**: per-tuple block with labelled fields (Id,
PromptStarted / CommandStarted / OutputStarted /
CommandFinished timestamps, ExitCode, Source provenance,
ExtraParams, Prompt / Command / Output text). Active tuple
appears at the end if present. Empty history shows
`(no entries; session has not yet captured any prompt
boundaries)` and does NOT overwrite clipboard contents.

**Files**:
- `src/Terminal.Core/SessionModel.fs` — new
  `formatHistoryForClipboard : DateTime -> T -> string`
  helper. Pure function; testable in isolation. Mirrors
  the existing `Diagnostics.captureSessionModel` pattern
  (Cycle 16) but for full history, no truncation.
- `src/Terminal.Core/HotkeyRegistry.fs` — new
  `AppCommand.CopyHistoryToClipboard` case + `nameOf` arm
  + `builtIns` row + `allCommands` entry. Bound to
  `Ctrl+Shift+Y`.
- `src/Terminal.App/Program.fs` — `runCopyHistoryToClipboard`
  closure captures `currentSession` from compose-local
  scope; resolves at hotkey-press time so a hot-switch is
  picked up correctly. Mirrors `runCopyLatestLog`'s
  STA-thread + 3s-timeout pattern (clipboard requires STA
  apartment + can hang on contention with NVDA's
  clipboard hooks).
- `src/Views/TerminalView.cs` — `AppReservedHotkeys`
  table entry for `Key.Y` + `Ctrl|Shift`.
- `tests/Tests.Unit/SessionModelTests.fs` — 10 new tests
  covering: empty session, snapshot timestamp header,
  one-finalised-tuple history, multi-line CommandText
  preservation, empty-field `(empty)` markers, source
  provenance rendering, active-only no-history, shell +
  session id + alt-screen flag rendering, full-content
  no-truncation regression guard, oldest-first ordering.
- `tests/Tests.Unit/HotkeyRegistryTests.fs` —
  `allCommands contains exactly the documented commands`
  fixture extended with the new case.
- `CLAUDE.md` — currently shipped hotkeys list extended.
- `docs/USER-SETTINGS.md` — hotkey table entry.

**Threading**: `currentSession` is read directly on the
WPF dispatcher at hotkey-press time. F# records are
immutable so field-level reads are tear-free; the worst
case is ~50ms staleness if a tick fires concurrently. Same
pattern Cycle 16's diagnostic-snapshot capture uses
(`Ctrl+Shift+D`).

### Fixed (Cycle 22a): HeuristicPromptDetector multi-match flapping → History flooded to 100/100

**Substrate hotfix.** Manual NVDA validation 2026-05-08
exposed a regression introduced by Cycle 20a (PR #195): in
cmd sessions where a prior prompt was still visible above
the current prompt (i.e. essentially every cmd session
after the first command), the `HeuristicPromptDetector`
emitted PromptStart events at ~20Hz, alternating between
two row indices (e.g. row 6 and row 13). Each emission
fired the SessionModel state machine's
"`PromptStart while Active=Some` → finalise prior as
incomplete" arm, flooding `History` to its 100-tuple cap
within ~5 seconds.

**Root cause.** Cycle 20a's emission gate was
`(text, rowIdx) != LastEmitted`, with first-match-wins
loop semantics. When two rows matched the regex with
identical text:
- Tick N: walks rows ascending, emits at row 6.
  `LastEmitted = ("C:\\Users\\admin>", 6)`.
- Tick N+1: walks again. Row 6's pair equals last-emitted →
  skip. Row 13's pair differs → emit. `LastEmitted = (..., 13)`.
- Tick N+2: row 6's pair now differs from last-emitted →
  emit. `LastEmitted = (..., 6)`.
- Forever flap at the tick rate.

**The fix** (this PR). Replace the first-match-wins loop
with two-pass detection. Pass 1 collects every
stable+matching row. Pass 2 picks the highest row index
(the newest prompt — output flows downward in every
supported shell, so the bottommost match is the active
prompt) and applies the (text, rowIdx) emission gate to
that one candidate. Earlier matches are scrollback noise
and get suppressed by construction.

**Files**:
- `src/Terminal.Core/HeuristicPromptDetector.fs` —
  refactored detection loop to two-pass.
- `tests/Tests.Unit/HeuristicPromptDetectorTests.fs` —
  updated existing `multiple rows match regex; first
  stable one emits` test (renamed to `highest-rowIdx
  stable one emits` with stronger assertion); added 4 new
  tests covering: identical-text two-row flap suppression
  (the headline regression guard); three stable rows;
  scroll-off transition (highest-row content disappears,
  lower remaining match emits); different-text two-row
  highest-wins.

**Behaviour change**: the substrate's idle behaviour for
cmd / PowerShell sessions with prior prompts visible no
longer floods History. Verifiable by maintainer: cut
release, type a few commands in cmd, wait, press
Ctrl+Shift+D — expect History grows by 1 per command (was:
fills to 100/100 within seconds at idle).

### Added (Cycle 20b): Tier 1.E2.B — CommandText + OutputText extraction (closes Tier 1)

**Second half of the Tier 1.E2 split** per maintainer
AskUserQuestion 2026-05-08. After Cycle 20a (PR #195) made
SessionModel `History` visibly grow on every cmd / PowerShell
command cycle, the tuples were still empty
(`CmdText=no OutText=no`). Cycle 20b extracts CommandText +
OutputText from screen state at finalize time, populating
the substrate end-to-end for the typical cmd / PowerShell
case.

**Closes Tier 1 substrate cycle**: the SessionModel
substrate is now structurally complete (skeleton through
state machine) AND content-complete (text fields populate
during normal use). Ready for Tier 2 persistence + Phase 2
input framework cycles.

**Behaviour change**: SessionModel tuples carry actual
command + output content. After Cycle 20b: maintainer types
`echo hi` in cmd, presses Ctrl+Shift+D, pastes log; sees
`RecentTuple[0]: ... CmdText="echo hi" OutText="hi"` (was
`CmdText=no OutText=no` pre-Cycle-20b).

**Mechanism**:

1. New `ActiveSessionTuple.PromptRowIndex: int option`
   field, recorded from `boundary.MatchedRowIndex` at
   PromptStart-arm transitions (Cycle 20a's forward-compat
   plumbing finally consumed).
2. `SessionModel.apply` signature extended:
   `T → PromptBoundaryData → Cell[][] → T`. Snapshot threads
   through `Program.fs.handlePromptBoundary` from the
   detector's existing snapshot context (heuristic path) or
   fresh capture (OSC 133 path).
3. New private `extractContent` helper computes:
   - **CommandText**: row at `oldPromptRowIndex` rendered
     via `CanonicalState.renderRow`, with `PromptText`
     prefix stripped + leading whitespace trimmed.
   - **OutputText**: rows between `oldRow + 1` and
     `newRow - 1` (inclusive) joined with newlines; empty
     rows filtered out.
4. Defensive skip when row indices are missing, out of
   bounds, or the rendered row doesn't start with the
   captured PromptText (scroll-mid-cycle).
5. `finalizeAndEnqueue` signature extended with extraction
   context params; `finalizeIncomplete` (shell-switch path)
   passes `None / None / [||]` so content stays empty for
   incomplete tuples.

**Tier 1.F diagnostic log line extension**: each
RecentTuple line now displays truncated CmdText + OutText
(cap 80 chars; longer than PromptText's 40 since outputs
typically run longer). Empty content renders as `""`.

**Files modified**:
- `src/Terminal.Core/SessionModel.fs` — `ActiveSessionTuple.PromptRowIndex`
  + `extractContent` helper + `apply` signature extension +
  `finalizeAndEnqueue` extraction params + interrupt-arm /
  CommandFinished-arm extraction-context wiring.
- `src/Terminal.App/Program.fs` — `handlePromptBoundary`
  signature extended with snapshot; `runDetector` forwards
  snapshot; OSC 133 augmentation reuses captured snapshot
  for the apply call.
- `src/Terminal.App/Diagnostics.fs` — `RecentTupleView`
  gains `CommandText` / `OutputText` fields;
  `captureSessionModel` populates from tuple; `formatTuple`
  displays truncated content (cap 80 chars).
- `tests/Tests.Unit/SessionModelTests.fs` — ~45 existing
  `apply` call sites updated mechanically with `[||]`
  snapshot arg (or `(fun s b -> apply s b [||])` lambda for
  `List.fold` callers); ~10 new tests covering extraction
  across edge cases (basic single-row / multi-row /
  empty-rows-filtered / clear-screen / scroll-mid-cycle /
  missing-row-index / out-of-bounds-row / spacing-preserved
  / shell-switch-skip-extraction / Active.PromptRowIndex
  capture).

**Out of scope** (deferred indefinitely):
- Cursor-aware command detection (Phase 2 input framework).
- Multi-line command capture (continuation, here-doc).
- Continuous OutputText accumulation during OutputStreaming
  state (current finalize-time-only extraction works for
  on-screen output; off-screen scrollback is not captured).
- ANSI-attribute preservation in extracted text.
- Spec changes (per CLAUDE.md spec-immutability).
- Spoken-announce extension to include CmdText/OutText
  (announce stays focused on count + state; log paste
  carries content).

**Stabilisation expected post-merge**: maintainer cuts
release; runs `echo hi` in cmd; presses Ctrl+Shift+D;
pastes log. Expected: `RecentTuple[0]: Prompt="C:\..." ...
CmdText="echo hi" OutText="hi"`. Substrate now produces
queryable command-cycle content for future pathway
integrations + Tier 2 persistence.

### Changed (Cycle 20a-followup): Diagnostic announce wording — "command history entries"

**Tiny accessibility-clarity follow-up** to Cycle 20a per
maintainer feedback 2026-05-08: after Cycle 20a's headline
behaviour change made the SessionModel `History.Count`
visibly grow on every command, the maintainer reported the
spoken announce wording felt opaque ("tuples" reads as
jargon; the cap wasn't audible). One-line change in
`Diagnostics.fs`:

Before:
> "Diagnostic complete. 1 of 1 passed. SessionModel: 2
> tuples, active state AwaitingCommandStart. Diagnostic log
> copied to clipboard."

After:
> "Diagnostic complete. 1 of 1 passed. SessionModel: 2 of
> 100 command history entries, active state
> AwaitingCommandStart. Diagnostic log copied to clipboard."

Adds ~3 spoken words. The "K of N" phrasing surfaces the
`MaxHistorySize` cap (default 100) so the maintainer can
hear "approaching cap" pressure without paste-into-chat
log inspection. "Command history entries" replaces "tuples"
as the user-facing term.

**Files modified**: `src/Terminal.App/Diagnostics.fs` (one
substrate-fragment template, two arms — Some-state +
None-state — updated symmetrically).

**No code-test changes**: the diagnostic battery's pure
helpers (`captureSessionModel`, `formatSessionModelSnapshot`)
are unchanged; only the announce-formatting template
inside `runFullBattery`'s body shifted. Per the Track B
audit pattern, composition-root announce strings aren't
unit-tested; verification is the maintainer's NVDA-listen.

### Changed (Cycle 20a): Tier 1.E2.A — row-index-aware detector emission

**First half of the Tier 1.E2 split** per maintainer
AskUserQuestion 2026-05-08 (Cycle 20b ships content
extraction; Cycle 20a ships the detector extension that
makes Cycle 20b's content extraction observable for stable-
prompt cmd usage). Substrate-first ordering: detector
emission gate fixed first; content extraction follows after
maintainer validation.

**Behaviour change**: SessionModel `History` now grows on
every command cycle in cmd / PowerShell, not just on
prompt-text changes. Closes the "stable-prompt cmd usage
produces zero tuples" gap surfaced by the maintainer's
2026-05-08 manual NVDA validation.

**Mechanism**: extends the heuristic detector's emission
gate from "text differs" to "(text, rowIdx) differs":

```fsharp
let isNewPrompt =
    match state.LastEmittedPromptText, state.LastEmittedPromptRowIndex with
    | Some priorText, Some priorRow ->
        priorText <> text || priorRow <> rowIdx
    | _ -> true   // first emit
```

The `(text, rowIdx)` pair captures both signals:
- **Different text** (today's case): `cd` changed the
  prompt path. Emit.
- **Same text, different row** (NEW): cmd's stable-prompt
  case where output pushed the prompt to a new row after a
  command cycle. Emit.
- **Same text, same row**: cursor blink / refresh / no real
  activity. Suppress.

**New plumbing**:

- `PromptBoundaryData.MatchedRowIndex: int option` —
  populated by `HeuristicPromptDetector` at emit time; by
  `Program.fs.handlePromptBoundary` OSC 133 augmentation
  from cursor row; left `None` by `Osc133.tryParse` (parser
  has no screen access). Forward-compat shipping in 20a (Cycle
  20b's CommandText/OutputText extraction will use this
  field).
- `HeuristicPromptDetector.T.LastEmittedPromptRowIndex: int option` —
  detector-state extension. Updated alongside
  `LastEmittedPromptText` on every emit; cleared by
  `reset` / `create`.

**Files modified**:

- `src/Terminal.Core/Types.fs` — adds `MatchedRowIndex`
  field to `PromptBoundaryData`.
- `src/Terminal.Core/Osc133.fs` — emits `MatchedRowIndex = None`.
- `src/Terminal.Core/HeuristicPromptDetector.fs` —
  `LastEmittedPromptRowIndex` field; row-index-aware emission
  gate; populates `MatchedRowIndex` on emit; `create` /
  `reset` updates.
- `src/Terminal.App/Program.fs` — OSC 133 augmentation
  populates `MatchedRowIndex` from cursor row alongside
  `MatchedRowText`.

**Tests added**: ~10 in
`tests/Tests.Unit/HeuristicPromptDetectorTests.fs`:

- Emitted boundary carries `MatchedRowIndex = Some` matching
  row (row 0 + non-zero row variants).
- Same prompt text at SAME row suppresses re-emit (regression
  guard for the cursor-blink / refresh case).
- Same prompt text at DIFFERENT row emits new PromptStart
  (Cycle 20a headline behaviour).
- Different text at SAME row emits (regression guard for
  pre-Cycle-20a behaviour).
- Different text at DIFFERENT row emits (both signals).
- `reset` clears `LastEmittedPromptRowIndex`; post-reset
  identical pair re-emits.
- Initial state has `LastEmittedPromptRowIndex = None`.
- Prompt scrolled UP between frames also emits (any row-index
  change is a valid signal of activity).

Plus 1 new test in `tests/Tests.Unit/Osc133Tests.fs` confirming
`tryParse` leaves `MatchedRowIndex = None` (mirrors the Tier
1.E `MatchedRowText = None` test).

Plus mechanical updates to ~14 existing record-literal sites
across `SessionModelTests.fs` to add the `MatchedRowIndex = None`
default (matching the Tier 1.E `MatchedRowText` rollout
pattern).

**Out of scope (deferred to Cycle 20b)**:

- CommandText extraction at finalize time.
- OutputText extraction at finalize time.
- Snapshot threading through `handlePromptBoundary` →
  `apply`.
- `ActiveSessionTuple.PromptRowIndex` field.
- Diagnostic log line extension to show truncated
  CmdText / OutText (Tier 1.F formatTuple cosmetic).

**Stabilisation expected post-merge**: maintainer cuts
release; runs `echo hi` in cmd; Ctrl+Shift+D shows
`History=1/100`. Substrate progresses through tuple cycles
even when prompt text stays stable. Cycle 20b ships content
extraction once Cycle 20a is validated.

### Added + Changed (Cycle 19): Tier 1.D-cleanup + default-shell TOML override (bundled)

**Two small named follow-ups bundled per maintainer
direction 2026-05-08.** Both items sat on the strategic
backlog after Cycle 17 / 18; combining them into a single
PR avoids two near-identical doc-+-test churn cycles.

#### Part 1 — Tier 1.D-cleanup: consolidate detector invocation sites

**Refactor only; zero behaviour change.**

Cycle 17 (PR #192) introduced the channel-driven actor
model + tick-driven `handleTick` helper to close the
detector idle-gap hole. That cycle left detector-invocation
logic duplicated across `handleRowsChanged` (frame-driven)
and `handleTick` (tick-driven) — same 4-arg shape (shell key
+ detection time + `tryDetect` + state update + dispatch on
`Some`).

Cycle 19 factors the duplication into a shared `runDetector`
helper inside the consumer task body. Both call sites
become 1-line invocations:

```fsharp
let runDetector
        (snapshot: Cell[][])
        (cursorPos: int * int)
        (now: DateTime)
        : unit
        =
    let shellKey = shellIdToConfigKey currentShellId
    let boundary, nextDetector =
        HeuristicPromptDetector.tryDetect
            snapshot cursorPos shellKey now promptDetector
    promptDetector <- nextDetector
    match boundary with
    | Some data -> handlePromptBoundary data
    | None -> ()
```

Helper closes over `currentShellId` + `promptDetector`
(composition-root mutables). Safe because all callers run
on the single notification-consumer thread per the
channel-driven actor model. Net diff: ~+25 LOC helper /
~-30 LOC removed from the two call sites.

#### Part 2 — Default-shell TOML override (item 33)

**New TOML configuration option for startup-shell selection.**

User-visible behaviour change: `[startup] default_shell`
in `%LOCALAPPDATA%\PtySpeak\config.toml` now overrides the
`PTYSPEAK_SHELL` environment variable. Use case
(maintainer's 2026-05-08 NVDA validation): after running
`setx PTYSPEAK_SHELL claude` for prior testing, every
launch defaulted to Claude Code. Clearing the env var
required a fresh shell + relaunch dance. With Cycle 19,
setting `[startup] default_shell = "cmd"` in the TOML
overrides the env var — no `setx` choreography needed.

**Precedence (highest → lowest)**:
1. **`[startup] default_shell` TOML override** (NEW).
2. `PTYSPEAK_SHELL` environment variable.
3. Built-in `cmd` default.

**Recognised values**: `cmd` / `powershell` / `pwsh` /
`claude` (case-insensitive). Validated at parse time
against `Config.knownShellKeys`; unknown values produce a
`LogWarning` and the override falls through to env var.

**Files modified**:

- `src/Terminal.Core/Config.fs` — adds
  `StartupOverrides` record + `DefaultShell: string option`
  field; `parseStartupOverrides` parser; `resolveDefaultShell`
  helper. The "Config loaded from..." log line now includes
  startup-override summary.
- `src/Terminal.App/Program.fs` — `resolveStartupShell`
  consults `Config.resolveDefaultShell config` BEFORE the
  env var. Logs at Information when the override fires
  (so post-hoc diagnosis via Ctrl+Shift+; is trivial).
- `tests/Tests.Unit/ConfigTests.fs` — 6 new tests
  covering: `defaultConfig.resolveDefaultShell = None`;
  `[startup] default_shell = "cmd"` parses + resolves;
  case-insensitive (`"PowerShell"` → `Some "powershell"`);
  unknown value (`"bash"`) logs Warning + drops; missing
  key returns None; unknown subkey logs Warning but
  doesn't corrupt parse.
- `docs/USER-SETTINGS.md` — Default-shell parameter atlas
  section gains the new TOML resolution step + use case +
  schema snippet.

**Architectural alignment**: matches the
[`docs/CHANNEL-ARCHITECTURE.md`](docs/CHANNEL-ARCHITECTURE.md)
+ [`docs/CUSTOMIZATION-MODEL.md`](docs/CUSTOMIZATION-MODEL.md)
principle of user-introspectable + user-customizable
substrate seams. The startup-shell choice is one such seam;
exposing it via TOML is the natural application of the
principle at this layer.

**Out of scope**:
- TOML hot-reload (changes still require restart).
- Default-shell preference UI (palette / menu — Phase 2).
- Per-shell override (`[shell.cmd] default = true` style)
  — single global default suffices for v1.
- Spec changes (per CLAUDE.md spec-immutability).

### Added (Cycle 18): Channel Architecture research-stage doc

**Sixth research-stage doc.** Formalises the maintainer's
2026-05-08 architectural principle — channels as the
canonical inter-thread communication primitive in pty-speak —
applied concretely in Cycle 17 / PR #192 (channel-driven
actor model that closed the Tier 1.D idle-gap hole). The
doc captures the principle in writing so future
implementation cycles (Phase 2 input framework, Tier 2
persistence, Tier 3 AI-summarisation) align with it by
construction rather than by chance.

**New doc** `docs/CHANNEL-ARCHITECTURE.md` (~750 LOC)
covers:

- **The principle stated** — channels at thread
  boundaries, single-threaded consumption, explicit
  backpressure, lifecycle via `TryComplete`.
- **Channel inventory (current state)** — 3 production
  channels: `pumpChannel : BoundedChannel<PumpInput>`
  (Cycle 17), `ConPtyHost.Stdout : ChannelReader<byte array>`
  (PTY → reader thread), `FileLogger.channel : BoundedChannel<LogEntry>`
  (any thread → drain writer). Per-channel rows: payload,
  producer, consumer, backpressure mode, lifecycle, file:line.
- **F# Events — when channels DON'T apply** — 3 events:
  `screen.ModeChanged`, `screen.Bell`,
  `screen.PromptBoundary`. The "Event → Channel bridge"
  pattern + the buffer-then-fire-after-lock idiom in
  `Screen.Apply` documented as load-bearing.
- **Decision framework** — 3-question heuristic
  (cross-thread? backpressure? fixed cardinality?) +
  4-bucket categorisation (Channel / Event / direct call /
  mutable-plus-lock).
- **5 anti-patterns** — don't channelise pure functions;
  don't use channels to avoid passing parameters; don't
  pick `Wait` blindly; don't forget `TryComplete`; don't
  bridge synchronously while holding a lock.
- **3 future channel candidates** — input-keystroke
  (Phase 2 input framework; `Wait` backpressure),
  persistence-flush (Tier 2; `Wait`), AI-summarisation
  (Tier 3; `DropOldest`). Each anchors a future cycle's
  design.
- **Pipeline Inspector pane preview** — cross-references
  PANE-MODEL.md's reserved pane; this doc establishes the
  schema the pane will query.
- **5 open questions** — `screen.PromptBoundary`
  channelise vs event; `FileLogger.channel` capacity TOML;
  input-keystroke batching vs per-keystroke; Pipeline
  Inspector subscription model; unbounded-channels policy.

**Companion doc updates** — front-matter cross-references
added to the 5 prior research-stage docs (PIPELINE-NARRATIVE,
INTERACTION-MODEL, SESSION-MODEL, PANE-MODEL,
CUSTOMIZATION-MODEL) plus a new entry in DOC-MAP.md.

**Substrate-first principle**: per the maintainer's
"moving slowly and intentionally" guidance, this doc lands
BEFORE Phase 2 / Tier 2 / Tier 3 implementation cycles
consume the principle. Constrains drift; makes the
architectural decision visible + reviewable.

**User-visible behaviour change**: zero (docs-only).

**Out of scope**:
- Channel-pool / rate-limiter patterns.
- Unbounded channels (explicitly avoided per Q5).
- Broadcast channels (multi-consumer).
- Refactoring existing channels (descriptive, not
  prescriptive).
- Spec changes (per CLAUDE.md spec-immutability).
- Channel-API tutorial content (defer to Microsoft
  `System.Threading.Channels` docs).

### Changed (Cycle 17): SessionModel Tier 1.D-fix — tick-driven detector via channel-driven actor model

**First post-Tier-1 architectural correction.** Maintainer's
manual NVDA validation 2026-05-08 against the Cycle 16 release
build exposed a Cycle 14 design hole: SessionModel diagnostic
showed `Active=none, LastEmittedPromptText=none, PerRowMatches=1`
after the maintainer typed in PowerShell + waited 6+ seconds
to press Ctrl+Shift+D. Trace analysis: Cycle 14's
frame-driven-only `tryDetect` invocation requires TWO calls
(first records the per-row match timestamp; second checks
stability + emits). At idle, no `RowsChanged` events fire,
so the second call never happens, and the detector silently
holds a recorded match without emitting.

The Cycle 14 plan explicitly named tick-driven detection as
the alternative + chose frame-driven with the note "Overkill
for Tier 1.D; the frame-based poll is sufficient." That
assumption was wrong. Cycle 17 corrects the choice.

**Per Cycle 17 plan-mode locked scope** (maintainer
AskUserQuestion 2026-05-08): **channel-driven actor model**
(option 3 of three offered). The notification-consumer task
becomes the SOLE owner of composition-root mutable state
(`currentSession`, `promptDetector`, `activePathway`); the
tick-pump no longer mutates state directly but enqueues a
synthetic `Tick` event into a unified channel. Eliminates
the race by construction; no shared mutables across threads.

**Architectural alignment** (maintainer principle 2026-05-08):
channels are the canonical inter-thread communication
primitive in pty-speak. The Pipeline Inspector pane envisioned
in `docs/PANE-MODEL.md` will eventually visualise message
flow across channel boundaries; Cycle 17 honours the
principle at the right boundary (thread-to-thread). A future
research-stage doc `docs/CHANNEL-ARCHITECTURE.md` (item 32 in
strategic backlog) will formalise where the principle applies
+ where pure functions / `Event<T>` / direct mutables remain
idiomatic.

**Mechanism**: introduces `PumpInput` discriminated union at
`Program` module scope:

```fsharp
type PumpInput =
    | Notification of ScreenNotification
    | Tick of DateTimeOffset
```

Renames `notificationChannel : BoundedChannel<ScreenNotification>`
to `pumpChannel : BoundedChannel<PumpInput>`. All existing
producers (parser reader thread; `screen.Bell` /
`screen.ModeChanged` / `screen.PromptBoundary` event
subscribers; ConPTY-spawn-failure path) wrap their writes
in `Notification (...)`. Tick-pump body simplifies to a
single line: `pumpChannel.Writer.TryWrite(Tick now)`.

**New `handleTick` helper** in the notification consumer
(`src/Terminal.App/Program.fs`) absorbs three responsibilities
formerly split across two threads:
1. Heuristic detector invocation. Captures fresh
   `screen.SnapshotRows` + cursor; calls
   `HeuristicPromptDetector.tryDetect`; dispatches any emitted
   `PromptBoundary` via the existing `handlePromptBoundary`
   helper. Closes the idle-gap hole — detector now runs every
   50ms regardless of `RowsChanged` activity.
2. `activePathway.Tick now`. Moved here from the standalone
   PathwayTickPump task body.
3. `OutputDispatcher.dispatchTick now`. Same.

Single-threaded with `handleRowsChanged` /
`handlePromptBoundary` / `handleModeChanged` because all
`PumpInput` cases are processed serially by this same
consumer task — no race on `currentSession` / `promptDetector` /
`activePathway` mutations.

**Consumer match block** extends with the `Notification`
wrapper + the new `Tick` case:

```fsharp
match peek with
| Notification (RowsChanged _) -> handleRowsChanged ()
| Notification ((ModeChanged _) as n) -> handleModeChanged n
| Notification ((ParserError _) as n) -> handleSimpleNotification n
| Notification (Bell as n) -> handleSimpleNotification n
| Notification (PromptBoundary boundary) -> handlePromptBoundary boundary
| Tick now -> handleTick now
```

**Tests**: per Track B audit pattern (composition-root
refactors aren't unit-tested). All ~673 existing tests stay
green via the mechanical channel rename + producer wrap.

**User-visible behaviour change**: SessionModel `Active`
populates correctly within 100-200ms of any prompt becoming
stable, regardless of subsequent idle. `Ctrl+Shift+D`
diagnostic now shows `Active=AwaitingCommandStart`,
`ActivePromptText="..."`, `LastEmittedPromptText="..."` for
shells with a stable matching prompt. History likely still 0
during idle (Tier 1.D's PromptStart-only emission means
History only grows when prompt text changes — e.g. `cd ..`,
or when Tier 1.E2 ships CommandFinished synthesis). Zero NVDA
behaviour change in foreground announce flow; substrate
populates correctly in the background.

**Files modified**:
- `src/Terminal.App/Program.fs` — `PumpInput` DU; channel
  rename; producer wraps; `handleTick` helper; consumer match
  extension; tick-pump simplification.
- `docs/SESSION-MODEL.md` — change-log table entry for
  Tier 1.D-fix.

**Known minor debt** (named follow-ups in strategic
backlog):
- **Tier 1.D-cleanup**: `tryDetect` is now called from
  `handleRowsChanged` AND `handleTick` with near-identical
  4-arg shape. Factor into a `runDetector` helper. Low
  priority; bundles naturally with Tier 1.E2 which extends
  the detector's outputs.
- **Channel Architecture research doc** (item 32):
  `docs/CHANNEL-ARCHITECTURE.md` formalising the where-applies
  / where-doesn't taxonomy for the channel principle. Forward-
  looking; informs Phase 2 input-framework cycle's channel
  decisions.

**Out of scope**:
- Detector invocation consolidation (Tier 1.D-cleanup).
- Channel architecture research doc (item 32).
- Persistence (Tier 2).
- CommandText / OutputText capture (Tier 1.E2).
- Spec changes (per CLAUDE.md spec-immutability).

### Added (Cycle 16): SessionModel Tier 1.F — diagnostic battery extension + cross-assembly visibility gotcha

**Sixth post-audit implementation cycle. Closes the Tier 1
substrate cycle.** The SessionModel substrate has been
operationally complete since Cycle 15 (skeleton + OSC 133 +
state machine + heuristic fallback + alt-screen wiring +
PromptText capture), but the maintainer had no easy way to
verify substrate operation without grepping FileLogger
output post-hoc. Tier 1.F closes that loop: the existing
`Ctrl+Shift+D` diagnostic battery now inspects SessionModel
state and surfaces it both via the NVDA announce (brief
fragment) and the clipboard-copied diagnostic log (full
multi-line block).

**New types in `Diagnostics.fs`**:
- `RecentTupleView` — per-tuple capture (Index, PromptText,
  CommandStartedAt, OutputStartedAt, CommandFinishedAt,
  ExitCode, plus boolean `HasCommandText` /
  `HasOutputText` indicators for Tier 1.E2 readiness).
- `SessionModelSnapshot` — captures SessionId, ShellId,
  SessionStartedAt, IsAltScreenActive, History counts,
  Active state + ActivePromptText, detector
  LastEmittedPromptText + PerRowMatches size, active
  pathway ID, and the most-recent 3 tuples
  (most-recent-first ordering).
- `truncate` helper — caps long PromptTexts at 40 chars
  with `...` suffix to keep log lines paste-friendly.

**New pure helpers**:
- `captureSessionModel session detector activePathwayId :
  SessionModelSnapshot` — pure capture function the
  composition-root closure invokes at battery-start time.
- `formatSessionModelSnapshot snap : string list` —
  renders the snapshot as a list of log lines, one per
  field group (SessionId / Active / Detector / Pathway /
  per-recent-tuple).

**Composition wiring** (`Program.fs.runDiagnostic`):
adds a fifth resolver closure capturing `currentSession`
+ `promptDetector` + `activePathway.Id` from the
`compose ()` local scope. Resolves at hotkey-press time
so a hot-switch (and the associated `currentSession`
recreation) is picked up correctly.

**Diagnostic log integration** (`runFullBattery`):
SessionModel inspection runs AFTER T0 process snapshot,
BEFORE the earcons + per-shell command battery, so
substrate state contextualises any test failures that
follow. Lines logged under the new
`[Diagnostic.SessionModel]` category mirror the existing
log format (`yyyy-MM-ddTHH:mm:ss.fffZ [INF] [Category]
message`).

**NVDA announce extension**: brief substrate fragment
appended to the existing summary line:

```
Diagnostic complete. 5 of 5 passed. SessionModel: 2
tuples, active state AwaitingCommandStart. Diagnostic
log copied to clipboard.
```

Adds ~5-10 NVDA-spoken words; keeps the announce under
typical 10-second NVDA read time.

**Tests**: per the Track B audit's
"diagnostic-battery is the irreducible manual layer"
recommendation, no new unit tests. Tests.Unit doesn't
reference `Terminal.App` (would require pulling in WPF
+ ConPty for one test file). Verification falls to
manual NVDA validation: maintainer presses Ctrl+Shift+D
after running a few commands and confirms the announce
+ pasted log contain the new substrate block.

**Bundled doc update**: CONTRIBUTING.md's "F# gotchas
learned in practice" section gains a new bullet for the
F# `let internal` cross-assembly visibility lesson
banked from Cycle 15's CI fixup (PR #190).
`CanonicalState.renderRow` was `let internal`; Tier 1.E's
`Program.fs.handlePromptBoundary` (in Terminal.App)
needed to call it for OSC 133 augmentation; CI fired
FS1094. Documented fix options ranked: (1) drop
`internal` for stable primitives, (2) add
`InternalsVisibleTo`, (3) replicate inline. Bundled into
this PR rather than shipping a separate doc-only PR.

**Out of scope** (deferred):
- `CommandText` / `OutputText` content display in
  RecentTupleView — only booleans show today; full
  content lights up when Tier 1.E2 ships.
- Persistence inspection (Tier 2).
- Pathway-internal state inspection (Phase 2).
- Diagnostic JSON output (plain-text log sufficient).
- Spec changes (per CLAUDE.md spec-immutability).

### Added (Cycle 15): SessionModel Tier 1.E — PromptText capture (text accumulation, part 1)

**Fifth post-audit implementation cycle.** Begins
populating the previously-empty
`SessionTuple.PromptText` field with the actual prompt-row
text from each shell session. Tuples in `History` now carry
meaningful content for each captured boundary.

**Per Cycle 15 plan-mode locked scope** (maintainer
AskUserQuestion 2026-05-08): **PromptText only** this
cycle; CommandText / OutputText named follow-up cycle
**Tier 1.E2** (deferred until either OSC 133 shells become
a current scenario or Phase 2's input framework provides
cursor primitives).

**Mechanism**: extends `PromptBoundaryData` with an
optional `MatchedRowText: string option` field. Both
producers populate it:

- **HeuristicPromptDetector** (Tier 1.D's detector)
  captures the matching row's text inline when the
  regex-stability check fires. Row text is rendered via
  the existing `CanonicalState.renderRow` (sanitised +
  trailing-blank-trimmed).
- **OSC 133 path** (`Program.fs.handlePromptBoundary`)
  augments parsed boundaries — the OSC 133 parser has no
  screen access, so the composition root captures a fresh
  `screen.SnapshotRows` snapshot and renders the cursor's
  row to populate `MatchedRowText` before passing to
  `SessionModel.apply`.

`SessionModel.apply`'s `newTuple` helper writes
`MatchedRowText` into `Active.Tuple.PromptText` on
PromptStart transitions (both `None, PromptStart` and
`Some active, PromptStart` interrupt-and-restart arms).
`None` falls back to `""` (matches pre-Tier-1.E
behaviour).

**No `apply` signature change**: extending the boundary
payload preserves the existing pure-function shape;
existing 30+ state-machine tests stay green via
backward-compat `MatchedRowText = None` defaults in test
builders.

**User-visible behaviour change**: zero. Pathways still
no-op `OnPromptBoundary`; no NVDA announcements depend on
SessionModel state. The substrate carries richer content
internally — observable via Tier 1.F's diagnostic battery
(future cycle) and consumable by Phase 2 pathways
(ReplPathway, ClaudeCodePathway, FormPathway).

**Files modified**:
- `src/Terminal.Core/Types.fs` — adds `MatchedRowText:
  string option` to `PromptBoundaryData`.
- `src/Terminal.Core/Osc133.fs` — emits
  `MatchedRowText = None` (parser augmentation deferred
  to composition root).
- `src/Terminal.Core/HeuristicPromptDetector.fs` —
  populates `MatchedRowText = Some text` in the emit
  branch.
- `src/Terminal.Core/SessionModel.fs` — `newTuple`
  reads `MatchedRowText` to populate `PromptText`.
- `src/Terminal.App/Program.fs` —
  `handlePromptBoundary` augments OSC 133 boundaries
  with cursor-row text via fresh snapshot capture.

**Tests**: ~7 new tests covering
`PromptBoundaryData.MatchedRowText` construction,
`SessionModel.apply`'s PromptText population (with /
without MatchedRowText, interrupt-and-restart, full
A→B→C→D progression preserves text), heuristic detector
populates inline (cmd / PowerShell / Claude row variants),
and Osc133 parser leaves field None. All ~666 existing
tests stay green via backward-compat builders.

**Out of scope** (deferred to Tier 1.E2 or later):
- CommandText capture (needs CommandStart events from OSC
  133 OR cursor-aware diff from Phase 2).
- OutputText capture (continuous frame-by-frame
  accumulation; substantial new state-machine surface).
- Multi-line prompt support (Powerline / Starship
  spanning rows captures only the cursor's row today).
- Cell-attribute preservation (defer until rendering
  consumers need attribute-aware text).
- Per-character timestamps within OutputText.

### Added (Cycle 14): SessionModel Tier 1.D — heuristic prompt-boundary fallback + alt-screen wiring

**Fourth post-audit implementation cycle.** Ships the
heuristic prompt-boundary fallback module that produces
synthetic `PromptBoundary` events for shells that don't
emit OSC 133 (cmd / PowerShell / Claude — all three by
default). Closes the Q3 alt-screen wiring deferred from
Tier 1.C: composition root now calls
`SessionModel.enterAltScreen` / `exitAltScreen` alongside
the existing pathway-swap decision.

**Per Cycle 14 plan-mode locked scope** (maintainer
AskUserQuestion responses 2026-05-08):
- **PromptStart-only emission**. Detector emits
  `BoundaryKind.PromptStart` events only. The Cycle 13
  state machine's "PromptStart while Active=Some"
  transition auto-finalises the prior tuple as incomplete
  (`ExitCode = None`,
  `CommandFinishedAt = next-prompt-time`).
- **Regex-only Claude detection**. Single regex
  `^.*│\s>.*$` per row, same shape as cmd / PowerShell.
- **Hardcoded defaults**. All per-shell parameters baked
  into `HeuristicPromptDetector.fs`. No TOML schema
  changes this cycle.

**New module: `src/Terminal.Core/HeuristicPromptDetector.fs`**
(~250 LOC). Pure-function detector with stateful record:
- `T` record: `PerRowMatches: Map<int, string * DateTime>`
  (per-row first-match timestamps) +
  `LastEmittedPromptText: string option` (duplicate-emit
  suppression).
- `create () : T` — empty initial state.
- `tryDetect snapshot cursor shellKey now state :
  PromptBoundaryData option * T` — pure detection
  function.
- `reset state : T` — clear per-row state on shell-switch
  + alt-screen entry.

**Per-shell hardcoded defaults**:
- cmd: regex `^[A-Z]:\\.*>\s*$`, stability 100ms.
- PowerShell: regex `^PS\s.*>\s*$`, stability 100ms.
- Claude: regex `^.*│\s>.*$`, stability 200ms (slower
  TUI cadence).
- Unknown shells: detection OFF (returns `None` without
  state mutation).

**Detection algorithm (per-frame)**:
1. Per row, render text via `CanonicalState.renderRow`
   (sanitised + trimmed).
2. Test against per-shell regex.
3. If matches AND `(now - first-match-time) ≥ stabilityMs`
   AND text differs from `LastEmittedPromptText` → emit
   PromptStart with
   `Source = HeuristicPromptRegex stabilityMs`.
4. If matches but window not elapsed OR text equals
   last-emitted → no emit, update first-match timestamp.
5. If row stops matching → evict its `PerRowMatches` entry
   (keeps map bounded by `screen.Rows`).

**Composition wiring** (`Program.fs`):
- `mutable promptDetector : HeuristicPromptDetector.T`
  declared alongside `currentSession`.
- `handleRowsChanged` calls
  `HeuristicPromptDetector.tryDetect` AFTER snapshot
  capture and BEFORE pathway consumption — boundary, if
  emitted, dispatches via `handlePromptBoundary` (the
  Cycle 13 helper) which advances `currentSession` +
  invokes `activePathway.OnPromptBoundary`.
- `handleModeChanged` calls
  `SessionModel.enterAltScreen` on
  `PathwaySelector.SwapToTui` and
  `SessionModel.exitAltScreen` on `SwapToShellDefault`,
  alongside the existing `swapPathwayForAltScreen` call.
  Detector reset on both transitions (stale matches
  would produce phantom boundaries on alt-screen exit).
- `switchToShell`'s `Ok newHost` branch resets the
  detector after recreating `currentSession` (fresh
  per-row state for the new shell).

**User-visible behaviour change**: zero. Stream / Tui
pathways still return `[||]` from `OnPromptBoundary`; no
NVDA announcements depend on SessionModel state. The
substrate becomes operationally meaningful (tuples
populate naturally during cmd / PowerShell / Claude use)
but remains internally observable only — Tier 2
(persistence) and Phase 2 pathways consume the tuples in
user-visible ways.

**Tests added**: ~30 in
`tests/Tests.Unit/HeuristicPromptDetectorTests.fs`
covering:
- Per-shell regex matching (8 tests): cmd / PowerShell /
  Claude prompt shapes match; non-prompt text doesn't;
  ASCII pipe `|` distinct from box-drawing `│`.
- Stability window (4 tests): first match returns None;
  same text after `stabilityMs - 1` returns None; same
  text at exactly `stabilityMs` returns Some; same text
  after window emits ONCE then suppresses.
- Duplicate suppression (3 tests): N stable frames emit
  once; cursor movement on stable prompt suppresses;
  new prompt text re-emits.
- Multi-row scenarios (4 tests): prompt at non-zero row
  detected; multiple matching rows emit one per call;
  all-blank snapshot returns None; zero-row snapshot
  returns None.
- Source provenance (3 tests): cmd → 100ms; PowerShell →
  100ms; Claude → 200ms.
- Reset behaviour (3 tests): clears `PerRowMatches`;
  clears `LastEmittedPromptText`; post-reset prompt
  needs fresh stability window.
- Unknown shell handling (3 tests): unknown / empty /
  case-mismatched keys return None.
- State hygiene (1 test): row that stops matching
  evicts its entry.

Plus ~3 tests in `SessionModelTests.fs` pinning that
heuristic-source boundaries flow through the state
machine identically to OSC 133 boundaries (same
finalisation behaviour; Sources map records the
stability ms).

**SESSION-MODEL.md updates**: Q2 + Q3 marked shipped
2026-05-08 with Tier 1.D scope details. Change-log
table entry added.

**Out of scope** (deferred):
- Heuristic synthesis of CommandStart / OutputStart /
  CommandFinished events (Phase 2 input framework /
  cursor-aware diff).
- Claude Ink-box context check (multi-row `╭─╮│╰─╯`
  scanning per SESSION-MODEL.md §275-280).
- TOML config for per-shell parameters
  (`[session_model.fallback.*]` sections + USER-SETTINGS
  atlas entries; defer until a pathway makes the params
  user-actionable).
- Cursor-position check on prompt rows (Phase 2
  cursor-aware diff prerequisite).
- Text accumulation for `PromptText` / `CommandText` /
  `OutputText` (Tier 1.E).
- Diagnostic battery extension (Tier 1.F).
- Pathway `OnPromptBoundary` non-trivial overrides
  (Phase 2 / Tier 3).
- Persistence (Tier 2).

### Added (Cycle 13): SessionModel Tier 1.C — state machine + composition wiring

**Third post-audit implementation cycle.** Replaces the
Tier 1.A no-op `SessionModel.apply` stub with the real
state-machine; wires `currentSession` into
`Program.fs`'s composition root + PathwayPump; recreates
on Ctrl+Shift+1/2/3 hot-switch with `finalizeIncomplete`
beforehand per Q5 resolution.

**Per Cycle 13 plan-mode narrowed scope**: state machine
+ composition only. Heuristic-fallback module (Tier
1.D), text accumulation (Tier 1.E), and diagnostic-
battery extension (Tier 1.F) ship in subsequent cycles.

**State machine behaviour**:
- `apply` now advances the active tuple's state through
  `AwaitingPromptStart → AwaitingCommandStart →
  EditingCommand → OutputStreaming → finalised`
  transitions per the SESSION-MODEL.md §4 specification.
- Defensive transitions (orphan boundaries, repeated
  boundaries, out-of-order boundaries) log at Warning
  level + soft-fail to preserve substrate health; never
  crash.
- `MaxHistorySize` ring-buffer eviction enforces the cap
  on `History` enqueue (FIFO; oldest-first eviction).
- Sources map records the `(BoundaryKind, BoundarySource)`
  pair for each boundary that touched the tuple.
- `CommandId` + `ExtraParams` from boundary metadata
  hoist onto the active tuple; `CommandId` conflicts log
  Warning + preserve the existing value.

**New Tier 1.C surface**:
- `SessionModel.IsAltScreenActive: bool` field on `T`
  (Q3 partial — when `true`, `apply` returns state
  unchanged; full PathwayPump-side wiring deferred to
  Tier 1.D alongside heuristic-fallback wiring).
- `SessionModel.enterAltScreen` / `exitAltScreen`
  helpers (Q3 partial).
- `SessionModel.finalizeIncomplete: T -> DateTime -> T`
  helper (Q5 — moves any in-flight active tuple to
  History with `ExitCode = None` + supplied
  `CommandFinishedAt`).

**Composition wiring** (`Program.fs`):
- `mutable currentSession : SessionModel.T` declared
  alongside `currentShellId`. Initialised with a `cmd`
  placeholder; re-created at the startup-shell alignment
  block once `chosenShell` resolves; re-created again on
  Ctrl+Shift+1/2/3 hot-switch.
- New `handlePromptBoundary` PathwayPump helper
  advances `currentSession` via `SessionModel.apply` +
  dispatches to `activePathway.OnPromptBoundary`. Stream
  / Tui pathways currently return `[||]` (no-op
  overrides from Tier 1.A); Phase 2 pathways
  (ReplPathway, ClaudeCodePathway, FormPathway) will
  override with non-trivial logic.
- The PromptBoundary arm in the PathwayPump notification
  consumer (added in Tier 1.A as `()`) now calls
  `handlePromptBoundary boundary`.
- `switchToShell`'s `Ok newHost` branch calls
  `SessionModel.finalizeIncomplete currentSession
  DateTime.UtcNow` BEFORE recreating with the new
  shell's key. Tier 1.C has no persistence so the
  finalised tuple is structurally discarded with the
  prior SessionModel; Tier 2 (persistence) will use this
  seam to flush History before recreation.

**User-visible behaviour change**: zero. Stream / Tui
pathways still return `[||]` from `OnPromptBoundary`;
no NVDA announcements depend on SessionModel state. The
substrate is structurally complete + observable via the
diagnostic battery (Tier 1.F territory) + future
pathway overrides. Internal observability arrives with
Tier 1.D (heuristic fallback produces boundaries for
cmd / PowerShell / Claude).

**Tests added**: ~30 new tests in
`tests/Tests.Unit/SessionModelTests.fs` covering the
state machine across categories:
- Happy path (4 tests): PromptStart → CommandStart →
  OutputStart → CommandFinished progression.
- Sequence pinning (5 tests): full A→B→C→D yields one
  tuple; two sequences yield two; CommandId hoists;
  ExtraParams merge with later-wins; Sources map
  records each boundary.
- Defensive transitions (10 tests): orphan boundaries
  ignored; PromptStart while Active interrupts +
  restarts; OutputStart after PromptStart tolerates
  skipped CommandStart; duplicate boundaries refresh
  timestamps; CommandFinished from EditingCommand
  finalises without OutputStartedAt; CommandId conflict
  preserves earlier value.
- Ring buffer (4 tests): bounded at MaxHistorySize;
  FIFO eviction on overflow; order preserved;
  MaxHistorySize=0 is no-op-without-crash.
- Alt-screen guard (4 tests): apply returns unchanged
  when IsAltScreenActive; enterAltScreen / exitAltScreen
  toggle the flag; apply resumes after exitAltScreen.
- finalizeIncomplete (4 tests): moves Active to History
  with CommandFinishedAt; no-op when Active=None;
  preserves accumulated metadata; sets ExitCode=None.

**SESSION-MODEL.md updates**: Q3 marked as Tier 1.C
partial (field + helpers shipped; PathwayPump wiring
deferred to Tier 1.D). Q5 marked as Tier 1.C shipped
(per-shell-session model + finalizeIncomplete helper).
Q2 (heuristic fallback) marked as deferred from Tier
1.C to Tier 1.D per the narrowed-scope split. Change-log
table entry added for 2026-05-08.

**Out of scope** (deferred to subsequent Tier 1
sub-cycles):
- Heuristic prompt-boundary fallback for shells without
  OSC 133 support (Tier 1.D).
- Text accumulation (`PromptText` / `CommandText` /
  `OutputText` populated from screen content; Tier 1.E).
- PathwayPump-side `enterAltScreen` / `exitAltScreen`
  wiring (Tier 1.D — bundled with heuristic-fallback
  composition).
- Diagnostic battery extension (Tier 1.F).
- Pathway `OnPromptBoundary` non-trivial overrides
  (Phase 2 / Tier 3).
- Persistence (Tier 2).

### Added (Cycle 12): SessionModel Tier 1.B — OSC 133 producer + cursor field

**Second post-audit implementation cycle.** Lands the OSC
133 detection producer per `docs/SESSION-MODEL.md` §3 +
the `CanonicalState.CursorPosition` field per the Q1
resolution.

**Behaviour change**: when the active shell emits an OSC
133 escape sequence (`ESC ] 133 ; <kind> [; <params>] BEL`),
pty-speak now produces a
`ScreenNotification.PromptBoundary` event flowing through
the PathwayPump's no-op consumer arm (added in Tier 1.A).
Today no shell that ships with pty-speak (cmd /
PowerShell / Claude) emits OSC 133 by default, so
**user-visible behaviour change is zero**. Tier 1.C will
wire the SessionModel state machine + heuristic fallback
+ active-pathway dispatch.

Files modified / created:

- `src/Terminal.Core/Osc133.fs` (NEW) — pure parser
  module. `tryParse: byte[][] -> DateTime ->
  PromptBoundaryData option` handles the four kind
  discriminators (A/B/C/D) + optional exit code for D
  + `aid=` hoisting + key=value `ExtraParams`. Malformed
  inputs return `None` (silent drop per the OSC
  silent-drop convention).
- `src/Terminal.Core/Terminal.Core.fsproj` — register
  `Osc133.fs` between `SessionModel.fs` and `Screen.fs`.
- `src/Terminal.Core/CanonicalState.fs` — add
  `CursorPosition: (int * int)` required field to
  `Canonical` record; `CanonicalState.create` signature
  now takes `(snapshot, cursorPosition, sequenceNumber)`
  with cursor captured atomically by the upstream
  `SnapshotRows` call.
- `src/Terminal.Core/Screen.fs`:
  - `SnapshotRows` return type extended from
    `int64 * Cell[][]` to
    `int64 * (int * int) * Cell[][]` — cursor `(row,
    col)` captured under the same gate lock as the
    snapshot.
  - New `Event<PromptBoundaryData>` + pending list +
    post-lock-release firing pattern (mirrors the Bell
    + ModeChanged pattern from Stage 5 + Stage 8d.1).
  - `Apply`'s `OscDispatch` arm now detects
    `parms.[0] = "133"B` and calls `Osc133.tryParse`;
    successful parses queue into
    `pendingPromptBoundaries` and fire post-lock as
    `PromptBoundary` events. Other OSC types continue
    to silent-drop per the existing security policy.
- `src/Terminal.App/Program.fs`:
  - 3 `SnapshotRows` call sites updated to 3-tuple
    unpacking (`let seq, cursorPos, snapshot = ...`)
    + pass `cursorPos` through `CanonicalState.create`.
  - New `screen.PromptBoundary.Add(...)` subscriber
    bridges parsed boundaries into `notificationChannel`
    (mirrors the existing Bell + ModeChanged bridges).
- `src/Terminal.Accessibility/TerminalAutomationPeer.fs`:
  - 1 `SnapshotRows` call site updated; cursor
    discarded with `_` (UIA peer's `ITextRangeProvider`
    doesn't need cursor position).
- `tests/Tests.Unit/Osc133Tests.fs` (NEW) — 17 tests
  pinning the parser. Each kind discriminator + edge
  cases (malformed exit code, missing `=` in key=value,
  empty parms, wrong type, multi-byte kind, etc.) +
  `Source = Osc133` stamping + `DetectedAt`
  passthrough.
- `tests/Tests.Unit/ScreenTests.fs` — 7 OSC 133
  integration tests pinning the producer pipeline:
  PromptStart fires; CommandFinished with exit code
  fires; `aid=` captures CommandId; malformed silent-
  drops; OSC 52 still silent-dropped (no
  misclassification); SequenceNumber advances; multiple
  back-to-back sequences fire one event each in order.
- `tests/Tests.Unit/CanonicalStateTests.fs` (16 sites),
  `tests/Tests.Unit/StreamPathwayTests.fs` (10 sites),
  `tests/Tests.Unit/TuiPathwayTests.fs` (6 sites) — 32
  `CanonicalState.create` calls updated mechanically
  (insertion of `(0, 0)` cursor argument; tests don't
  care about cursor in Tier 1.B).
- `tests/Tests.Unit/Tests.Unit.fsproj` — register
  `Osc133Tests.fs` between `SessionModelTests.fs` and
  `StreamPathwayTests.fs`.

**Q1 resolution shipped**: `Canonical.CursorPosition`
field added per the Tier 1.A plan's deferral.
SESSION-MODEL.md Q1 marked shipped.

**Sequencing**: Cycle 12 of post-audit work. Next:
Tier 1.C — SessionModel state machine + heuristic
fallback + composition root. Tier 1.D follows with
diagnostic battery extension + corpus tests.

### Added (Cycle 11): SessionModel Tier 1.A — substrate skeleton

**FIRST POST-AUDIT IMPLEMENTATION CYCLE.** Lands the
SessionModel substrate skeleton per
`docs/SESSION-MODEL.md` (item 28 design).

**Tier 1.A scope: data types only; zero behaviour
change.** No producer of `PromptBoundary` events; the
PathwayPump's notification consumer routes the new
case to a no-op arm. Tier 1.B (next cycle) ships the
OSC 133 producer + `CanonicalState.CursorPosition`
field; Tier 1.C wires the SessionModel state machine +
heuristic fallback + composition root integration;
Tier 1.D adds diagnostic battery extension + corpus
tests.

Files modified / created:

- `src/Terminal.Core/Types.fs` — adds
  `BoundaryKind` (`PromptStart` / `CommandStart` /
  `OutputStart` / `CommandFinished of int option`),
  `BoundarySource` (`Osc133` /
  `HeuristicPromptRegex of int` /
  `HeuristicClaudeInkBox`), `PromptBoundaryData`
  record, and `ScreenNotification.PromptBoundary of
  PromptBoundaryData` case.
- `src/Terminal.Core/SessionModel.fs` (NEW) — module
  with `ActiveTupleState`, `SessionTuple`,
  `ActiveSessionTuple`, `T` types + `create` /
  `createDefault` factories + `apply` no-op stub.
  String-keyed shell field per assembly-boundary
  discipline (mirrors `Config.fs`).
- `src/Terminal.Core/Terminal.Core.fsproj` — register
  `SessionModel.fs` immediately after `Types.fs`.
- `src/Terminal.Core/DisplayPathway.fs` — add
  `OnPromptBoundary: PromptBoundaryData -> OutputEvent[]`
  field to `T` record (protocol method; no-op default
  in shipping pathways).
- `src/Terminal.Core/StreamPathway.fs` — implement
  `OnPromptBoundary = fun _ -> [||]` in both `create`
  and `createWithExposedState` factories.
- `src/Terminal.Core/TuiPathway.fs` — implement
  `OnPromptBoundary = fun _ -> [||]` in `create`
  factory.
- `src/Terminal.Core/OutputEventBuilder.fs` — add
  `| PromptBoundary _ ->` arm to `fromScreenNotification`
  exhaustive match; routes to existing
  `SemanticCategory.PromptDetected` reservation with
  empty payload + Polite priority.
- `src/Terminal.App/Program.fs` — add
  `| PromptBoundary _ -> ()` no-op arm to the
  PathwayPump notification consumer loop. Tier 1.C
  replaces this with `SessionModel.apply` dispatch +
  pathway `OnPromptBoundary` invocation.
- `tests/Tests.Unit/SessionModelTests.fs` (NEW) — 18
  tests pinning the data shapes, the
  `ScreenNotification.PromptBoundary` round-trip, the
  `SessionModel.create` / `createDefault` initial
  state, the no-op `apply` contract for every
  `BoundaryKind`, and `SessionTuple`'s multi-line
  command + `int option` exit-code shape.
- `tests/Tests.Unit/Tests.Unit.fsproj` — register
  `SessionModelTests.fs` immediately after
  `CanonicalStateTests.fs`.

**SESSION-MODEL.md open-question resolutions** (per the
2026-05-07 audit walk-through; SESSION-MODEL.md
updated to mark resolved):

- **Q1** (cursor in CanonicalState — additive or
  breaking?): ADDITIVE; deferred to Tier 1.B (Tier 1.A
  doesn't touch CanonicalState).
- **Q2** (heuristic fallback default — on or off?): ON
  for known shells (cmd, PowerShell, claude); OFF for
  unknown. Implementation in Tier 1.C.
- **Q3** (SessionModel reset on alt-screen entry?):
  PAUSE; resume on exit. Implementation in Tier 1.C.
- **Q4** (multi-line commands — per line or as one?):
  ONE string with embedded newlines. **Implemented in
  this PR** via `SessionTuple.CommandText: string`.
- **Q6** (echo correlation interaction): separate
  concerns. Echo correlation (Phase 2) and SessionModel
  (this) share the active tuple but use it differently.
- **Q7** (AI summarisation — at C or D?): at
  `CommandFinished` (D); streaming summarisation
  deferred to a future Phase 3 cycle.
- **Q8** (exit-code semantics): NO. Substrate captures
  value verbatim; pathways interpret. **Implemented in
  this PR** via `SessionTuple.ExitCode: int option`.

All 8 SESSION-MODEL.md questions are now resolved (Q5
was resolved in Cycle 8, PR #182).

**Pre-emptive grep for pattern-match completeness**:
TWO existing match sites required new `PromptBoundary`
arms (per F# 9 + TreatWarningsAsErrors):

1. `src/Terminal.Core/OutputEventBuilder.fs`:
   `fromScreenNotification` — exhaustive match; added
   `| PromptBoundary _ -> OutputEvent.create
   SemanticCategory.PromptDetected Priority.Polite ...`
2. `src/Terminal.App/Program.fs`: PathwayPump consumer
   loop — added `| PromptBoundary _ -> ()` no-op arm.

`PathwaySelector.fs` has a wildcard `| _ -> Keep` so no
change needed there.

No spec changes (per CLAUDE.md spec-immutability — Track
C audit recommended that spec/ updates for SessionModel
ship when SessionModel implementation begins; the
substrate audit's recommendation is to defer spec
re-authoring to a coordinated post-Phase-2 cycle).

**Sequencing**: Cycle 11 of post-audit work. **First
post-audit implementation cycle**. Subsequent cycles
ship Tier 1.B (OSC 133 producer + cursor field), Tier
1.C (state machine + heuristic fallback + composition),
Tier 1.D (diagnostic battery + corpus). After Tier 1
ships in full, SessionModel Tier 2 (persistence) and
Tier 3 (pathway integration) follow.

### Changed + Added (Cycle 10): pre-implementation cleanup bundle

Bundles four post-audit fixup deliverables into one PR
(per the maintainer's 2026-05-07 efficiency directive —
"can you bundle all of the small ones into one PR before
moving into major code changes"). All docs-only; no code
changes; no spec changes.

**Bundle contents**:

1. **Track D atlas-side fixups** (per
   `docs/AUDIT-ATLAS-ALIGNMENT.md` Tier 1 findings) —
   `docs/USER-SETTINGS.md` updated:
   - **D2** `bulkChangeThreshold` "Current state" body:
     describe as `Parameters.BulkChangeThreshold` field
     (not the pre-PR-#168 top-level `let private`).
   - **D3** `backspacePolicy` section header + body:
     reflect `AnnounceDeletedCharacter` default (PR #168
     correction); preserve historical context.
   - **D4** `modeBarrier.flushPolicy` section header +
     body: reflect `SummaryOnly` default (PR #168 + #169);
     preserve historical context.
   - **D5** `FileLoggerOptions.ChannelCapacity = 8192`
     orphan: documented as 📋 reserved under "Platform-
     internal buffer / capacity defaults".
   - **D6** ConPtyHost buffer-size = 4096 orphan:
     documented as 📋 reserved alongside D5.
2. **Track E doc-currency fixups** (per
   `docs/AUDIT-DOC-CURRENCY.md` Tier 1 findings):
   - **E1** `docs/ROADMAP.md` Stage 7 row: marked
     **shipped** (2026-05-03) with citation to PRs A-K +
     PR-L + PR-M.
   - **E6** `docs/SESSION-HANDOFF.md` "Where we left off"
     table: rewritten to reflect post-Stage-7 substrate
     cycle (#146-#184); five research-stage docs; audit
     phase; current in-flight branch (this PR);
     "Next stage" pointer updated to SessionModel Tier 1
     implementation.
3. **ARCHITECTURE.md substantive refresh** (per Track E
   E2-E4 + Track E Tier 5):
   - Currency note updated to reflect Stages 0-7 + 11
     shipped + post-Stage-7 substrate.
   - Current-pipeline diagram redrawn through the 12-stage
     pipeline (PIPELINE-NARRATIVE vocabulary).
   - Module table refreshed: shipped projects
     (Terminal.Core / Pty / Pty.Native / Parser / Audio /
     Accessibility / Views / App) all marked ✅; reserved
     substrates (SessionModel, InputPathway, Pane,
     Customization, ClaudeCodePathway etc.) catalogued
     under "Reserved" section. Notes clarify that
     `Terminal.Semantics` / `Terminal.EventBus` from the
     original draft never materialised as separate
     projects (subsumed into `Terminal.Core`).
   - Threading-model "Today" section rewritten for
     post-Stage-7 substrate: PathwayPump thread,
     FileLogger writer thread, Earcon thread, Diagnostic
     battery thread, Heartbeat thread.
   - "Forward-looking thread additions" section added.
   - "Where the magic lives" expanded from 3 files to 5
     (adds StreamPathway + OutputDispatcher) plus the 5
     research-stage doc references.
   - "See also" section reorganised under categories
     (spec / research-stage / audit-track / operational).
4. **PROJECT-PLAN successor doc** (Track E E5 Option B
   resolution per the audit walk-through):
   - **NEW** `docs/PROJECT-PLAN-2026-05-revision.md`
     (~264 lines) — successor to the 2026-05-03 plan.
     Captures substrate-first shift; enumerates shipped
     substrate cycle + research-stage docs + audit phase
     + post-audit cleanup; points next stage at
     SessionModel Tier 1 implementation.
   - Original `PROJECT-PLAN-2026-05.md` body preserved
     verbatim per its own "Future revisions should land
     as new dated plans" discipline.

DOC-MAP.md updated with the new
`PROJECT-PLAN-2026-05-revision.md` entry.

**This PR closes the audit-fixup queue**. After merge:
- Track D atlas-side findings ✅ closed.
- Track E doc-currency findings ✅ closed.
- ARCHITECTURE.md refresh ✅ shipped.
- PROJECT-PLAN successor ✅ shipped.

**Sequencing**: Cycle 10 of post-audit work. Closes the
post-audit cleanup phase. **Next: SessionModel Tier 1
implementation = FIRST POST-AUDIT IMPLEMENTATION CYCLE**.

### Changed (spec): rename StreamProfile → PassThroughProfile (Track C D1, ADR-authorised)

`spec/event-and-output-framework.md` — apply the
`StreamProfile` → `PassThroughProfile` rename surfaced
by Track C audit finding D1 (per
`docs/AUDIT-SPEC-ALIGNMENT.md`). 7 occurrences renamed
across the spec body (module declaration at line 824,
the Coalescer-becomes-Profile narrative in §D.4, the
Stages 8a-8f retrofit list).

The module was originally drafted in spec as
`StreamProfile`; during the post-Phase-A substrate
migration the module shipped under the name
`PassThroughProfile` (better captures the pass-through
semantics; the actual streaming logic lives in the
StreamPathway substrate). Spec retroactively updated
to match shipped code.

A short historical-context note was added in §D.4
(adjacent to the original "Coalescer.fs becomes …"
line) explaining the rename + citing this audit
finding + maintainer ADR.

**ADR provenance**: maintainer authorised this spec
edit during the 2026-05-07 audit walk-through (per
`docs/AUDIT-SPEC-ALIGNMENT.md` Tier 2 / D1
recommendation). CLAUDE.md spec-immutability rule
honoured — explicit maintainer authorisation before
editing.

No code changes; no other doc changes.

**Sequencing**: Cycle 9 of post-audit work. Closes
the only ADR-required item from Track C audit. Next
cycle bundles the remaining audit-fixup queue (Track D
atlas-side + Track E doc-currency + ARCHITECTURE
refresh + PROJECT-PLAN successor) into a single
pre-implementation cleanup PR.

### Changed (Resolve Cluster 1-4 open questions in research-stage docs)

Apply maintainer's audit-walk-through decisions
(2026-05-07) to existing research-stage docs. Marks 12
open questions resolved across three docs:

- **`docs/INTERACTION-MODEL.md`** — Q1-Q6 all resolved:
  - Q1 SIM naming → KEEP "Shell Interaction Manager".
  - Q2 SIM as literal F# module → KEEP CONCEPTUAL;
    re-evaluate during Phase 2.
  - Q3 supersede other docs → NO; complementary lenses.
  - Q4 add three new SemanticCategory cases now → DEFER
    until producers ship.
  - Q5 notebook analogy → INSPIRATIONAL (not load-
    bearing).
  - Q6 per-shell vs. unified history →
    per-shell-session by default; opt-in unified
    (cross-references SESSION-MODEL.md Q5).
- **`docs/PANE-MODEL.md`** — Q1-Q5 all resolved:
  - Q1 Pane / Workspace / Pane Coordinator naming →
    KEEP.
  - Q2 single-window vs. multi-window → SINGLE-WINDOW
    multi-pane for v1.
  - Q3 floating vs. docked → DOCK with GridSplitter
    resize; skip floating for v1.
  - Q4 TOML schema scope → MODERATE (layout / size /
    visibility user-configurable; per-pane semantic
    parameters in own subsections).
  - Q5 WSL2/SSH shells: parallel panes vs. hot-switch
    → HOT-SWITCH first; multiple-pane power-user
    mode allowed later.
- **`docs/SESSION-MODEL.md`** — Q5 resolved:
  - Q5 shell-switch interaction with active tuple →
    per-shell-session SessionModel by default (option B
    from original question — persist as incomplete);
    opt-in unified history as TOML setting.

The other 7 SESSION-MODEL questions (Q1, Q2, Q3, Q4, Q6,
Q7, Q8) and the 7 CUSTOMIZATION-MODEL questions remain
open, deferred to the SessionModel Tier 1 plan-mode
cycle and the next maintainer walk-through respectively.

Edit pattern: each resolved question's section gets a
"✅ Resolved 2026-05-07" tag in its header + a
**Resolution** + **Rationale** block at the top;
original question text preserved verbatim below for
historical context. Section header updated from "Open
questions" to "Open questions / Resolutions" where
applicable.

**Sequencing**: Cycle 8 of post-audit work. Next
plan-mode cycle picks up Cycle 9 (Track C D1 spec
rename — `StreamProfile` → `PassThroughProfile` in
`spec/event-and-output-framework.md`; ADR authorized).

### Added (Customization Model research stage — item 31)

`docs/CUSTOMIZATION-MODEL.md` — first post-audit
research-stage doc. Captures the maintainer's
2026-05-07 architectural directive on user-
introspectable + user-customizable pipeline
architecture. The directive emerged during the audit-
phase open-questions walk-through; it is forward-
looking ("I'm hoping the implementation will allow for
such features down the road").

The principle, named: every pipeline operation in pty-
speak should be:

1. **Named and addressable** — has a stable identifier.
2. **Swappable** — alternative implementations exist
   (or can be authored).
3. **Inspectable per-output** — the user can see which
   alternative ran for any given output (full pipeline
   trace).
4. **User-authorable as override rules** — picking an
   alternative persists as a rule.
5. **Context-keyed** — rules attach to context tuples
   (shell, command, semantic-category, etc.) so similar
   future outputs apply the rule automatically.

Two illustrative use cases drawn from the maintainer's
examples:

- **Output-side**: command outputs a list that should
  be a navigable selection but renders as raw text.
  User opens the introspection panel for that output,
  sees the trace, picks an alternative `SelectionProfile`
  at stage 10. The choice persists as a rule keyed on
  `(shell, command-prefix)`; future similar outputs
  apply automatically.
- **Input-side**: autocomplete has a configurable
  cascade of completion sources (shell history,
  language-server, user-curated docs, LLM). User
  reorders or toggles individual sources.

Doc structure (~903 lines):

1. The customization principle (5-point definition).
2. Two illustrative use cases.
3. What the substrate must not preclude (per-doc-layer
   requirements: PIPELINE-NARRATIVE, SESSION-MODEL,
   INTERACTION-MODEL, PANE-MODEL, USER-SETTINGS, spec,
   audit baselines).
4. Pipeline-trace data model (sketch).
5. Override-rule data model (sketch with TOML schema).
6. Introspection UI sketch (Pipeline Inspector pane).
7. Composition with existing substrates
   (cross-reference matrix).
8. 6 substrate gaps (all forward-looking).
9. Versioning + maintenance.
10. 7 open questions for maintainer review (naming;
    alternatives registry shape; trace persistence;
    rule-context-keying scope; UI surface; rule
    precedence; rule-authoring UX).

**This PR does NOT include**: implementation; code
changes; spec changes; edits to existing research-
stage docs (open-question resolution + spec rename +
PROJECT-PLAN successor + audit fixup queue all ship
in subsequent sequenced cycles per single-concern
discipline).

**Sequencing**: first post-audit research-stage doc.
Cycles 8+ ship the deferred work. SessionModel Tier 1
implementation remains the FIRST POST-AUDIT
IMPLEMENTATION CYCLE.

DOC-MAP.md updated.

### Added (Audit Track F — backlog validation classification — FINAL audit-track deliverable)

`docs/AUDIT-BACKLOG-VALIDATION.md` — Track F of the
comprehensive audit phase, **closing the audit phase**.
Backlog-layer counterpart to Tracks A (code), B (test),
C (spec), D (atlas), E (doc currency). Walks every
strategic backlog item (30 numbered items + 6 audit-
track sub-items), classifies status, identifies
dependencies, surfaces ready-to-pick-up list.

Audit performed via direct read of the strategic backlog
tables + `git log --oneline main` to verify which items
shipped via which PRs. No backlog reorganization, no
GitHub issue creation, no code changes.

**Findings summary**:

- **11 ✅ shipped** (items 1, 3, 19, 24, 26, 29 + audit
  Tracks A-E sub-items).
- **5 ✅+ partially shipped** (items 5, 23, 25, 28, 30
  — research stage shipped; implementation pending).
- **11 📋 pending** (items 2, 4, 7, 8, 9, 10, 11, 12,
  13, 15-18, 20, 22 — most are Phase 2 / 3 work).
- **3 ⏸ deferred** (items 14 in-app menu, 21
  cross-platform Avalonia port, intentional historicals).
- **2 🔄 superseded** (items 6 8d.2 re-investigation
  moot after PR #163/#164; 27 scrollback reframed as
  downstream of SessionModel).
- **0 ❓ orphaned**.

**Headline**: backlog is **healthy**. ~50% of items have
shipped or are in-flight; remaining ~33% are clearly
sequenced; ~17% are deferred or superseded with explicit
rationale. No orphans.

**Ready-to-pick-up list** surfaces tiered queue:

- **Tier 1 — audit-fixup queue** (~5 small PRs):
  - Track D atlas-side fixups (~50-100 LOC)
  - Track E doc-currency fixups (~100-200 LOC)
  - ARCHITECTURE.md refresh cycle (~150-300 LOC)
  - D1 spec rename (~5 LOC; awaits maintainer ADR)
- **Tier 2 — test extensions** (per Track B
  recommendations; ~5 small PRs adding ~25-37 property
  tests + ~5 diagnostic battery cases).
- **Tier 3 — backlog refinements** (small, low-stakes;
  items 8, 15-18).
- **Tier 4 — substrate-implementation gates** (item 28
  SessionModel Tier 1 implementation = FIRST POST-AUDIT
  IMPLEMENTATION CYCLE; items 2, 4, 7 depend on it).

**4 reorganization recommendations** (R1: mark items 6 +
27 superseded; R2: promote audit-track items into main
backlog table; R3: open GitHub issues for ready-to-pick-
up Tier 1 items; R4: adopt audit→fixup loop as standing
pattern).

**Audit phase closing summary**: 6 audit-track docs +
1 audit-fixup PR = ~3,800 lines of audit content; 6 ⚠
items surfaced for Tier 1 fixup; 1 ADR candidate (D1);
7 spec holes deferred to future spec re-authoring;
~25 maintainer-pending open questions across SESSION-MODEL
/ INTERACTION-MODEL / PANE-MODEL + Track C D1 ADR + Track
E E5 PROJECT-PLAN successor; 0 ❌ structural
contradictions across all five prior tracks.

The substrate is **healthy**.

**Sequencing**: Track F audit-loop closes; **audit phase
formally closes with this PR's merge**. Next plan-mode
cycles can pick up:
- Tier 1 fixup PRs (~5 small PRs).
- Tier 2 test-extension PRs.
- Maintainer review of accumulated open questions.
- Item 28 SessionModel Tier 1 implementation — FIRST
  POST-AUDIT IMPLEMENTATION CYCLE.

DOC-MAP.md updated.

### Added (Audit Track E — doc currency classification)

`docs/AUDIT-DOC-CURRENCY.md` — Track E of the
comprehensive audit phase. Doc-currency-layer counterpart
to Tracks A (code), B (test), C (spec), D (atlas).
Inventories all 33 docs (27 in `docs/` + 5 root-level
+ 1 research seed at `docs/research/MAY-4.md`) for
currency markers, cross-reference health, and "Where we
left off" accuracy.

Audit performed via static inspection 2026-05-07; no doc
edits, no code changes.

**Findings summary**:

- **26 ✅ aligned** — doc reflects current state OR is
  intentionally historical / forward-looking and
  correctly tagged.
- **5 ⚠ stale** — developer-reference docs that
  snapshot a Stage-3b or Stage-7-just-shipped baseline
  and weren't refreshed when the substrate cycle
  (#160-#178) landed:
  - **E1**: `ROADMAP.md` line 53 — Stage 7 row marked
    "pending"; shipped 2026-05-03.
  - **E2-E4**: `ARCHITECTURE.md` lines 9-14, 18, 111 —
    Currency note + section headers reference Stage 3b
    as baseline.
  - **E6**: `SESSION-HANDOFF.md` lines 31-34 — "Where
    we left off" doesn't mention substrate cycle PRs
    #160-#178, research-stage docs, or audit phase
    Tracks A-D.
- **0 ❌ contradictions**.
- **2 📋 forward-looking openness** —
  `RFC-OUTPUT-FRAMEWORK.md` / `RFC-INPUT-FRAMEWORK.md`
  referenced in PROJECT-PLAN-2026-05.md as planned
  future deliverables; correctly absent today.

**Cross-reference health**: spot-checked 10+ markdown
links; **zero broken links**.

**DOC-MAP consistency**: 100% — every doc listed exists;
no orphans; per-audience entry-point lists current.

**Headline**: doc currency is **good**. The 5 ⚠
findings cluster on a documentation-process gap: when a
substantial substrate PR ships, the same PR (or an
immediate follow-up) should refresh SESSION-HANDOFF.md
"Where we left off" + relevant developer-reference doc
currency notes. Going forward, this becomes the discipline.

**Triage tiers**:

- **Tier 1 (doc-fix)**: 4 findings (E1, E2-E4 headers,
  E6) — small mechanical / substantive updates. Suggested
  follow-up PR title: `docs(audit): Track E — apply
  doc-currency fixups (ROADMAP / ARCHITECTURE /
  SESSION-HANDOFF)`. Estimated ~100-200 LOC.
- **Tier 2 (open question)**: 1 finding (E5: PROJECT-PLAN
  status pointer) — Option A small status-header update
  vs. Option B successor plan doc. Maintainer judgement.
- **Tier 3 (no action; intentional historicals)**:
  HISTORICAL-CONTEXT-2026-05.md, STAGE-7-ISSUES.md,
  PROJECT-PLAN body — correctly tagged as snapshots.
- **Tier 4 (forward-looking openness; no action)**: 2
  RFC-* doc references that don't yet exist (planned for
  future RFC phases).
- **Tier 5 (ARCHITECTURE.md refresh cycle, separate)**:
  the module table (lines 91-104) + threading-model
  section (lines 106-138) need substantive update
  beyond E2-E4 header fixes. Recommended as its own
  small refresh cycle (~150-300 LOC).

**Sequencing**: Track E audit-loop closes with this PR.
Next plan-mode cycles can pick up:
- Track F (backlog validation) — last in audit phase.
- Tier 1 fixup PRs from Tracks D + E.
- ARCHITECTURE.md refresh cycle.
- D1 spec rename (after maintainer ADR).

DOC-MAP.md updated.

### Added (Audit Track D — atlas alignment classification)

`docs/AUDIT-ATLAS-ALIGNMENT.md` — Track D of the
comprehensive audit phase. Atlas-layer counterpart to
Tracks A (code), B (test), C (spec). Cross-checks
`docs/USER-SETTINGS.md` (1547-line parameter atlas)
against actual code constants in
`src/Terminal.Core/Config.fs`,
`src/Terminal.Core/StreamPathway.fs`,
`src/Terminal.Core/FileLogger.fs`,
`src/Terminal.Audio/EarconPalette.fs`,
`src/Terminal.Pty/ConPtyHost.fs`, and
`src/Terminal.App/Program.fs`. No code changes; no atlas
edits in this PR (Tier 1 fixups deferred to a small
follow-up).

Audit performed via static inspection 2026-05-07.

**Findings summary**:

- **17 ✅ aligned** — atlas entry matches code default +
  status; section header current.
- **5 ⚠ stale or missing** — 3 stale-section findings (D2,
  D3, D4: atlas "Current state" describes pre-PR-#168
  world for `bulkChangeThreshold`, `backspacePolicy`,
  `modeBarrier.flushPolicy`); 2 low-priority orphans (D5:
  `FileLoggerOptions.ChannelCapacity = 8192`; D6: ConPTY
  buffer size 4096 — both platform-internal).
- **0 ❌ contradictions**.
- **11 📋 forward-looking openness** — reserved entries for
  Phase 2/3 (echo correlation, cursor announcement,
  ActivityId routing, earcon-palette tuning, keybindings
  TOML, etc.).

**Headline**: atlas alignment is **good**. The 3 stale-
section findings cluster on a common root cause: PR #167
(atlas augmentation 2026-05-06) added entries describing
the THEN-current hardcoded state + proposed configurability
shape; PR #168 immediately shipped that configurability
(`bulk_change_threshold`, `backspace_policy`,
`mode_barrier_flush_policy`) and changed the defaults to
the proposed values. Atlas's "Current state" sections were
NOT updated to reflect the implementation. Documentation-
process gap, not architectural.

**Triage tiers**:

- **Tier 1 (doc-fix)**: 5 mechanical doc-side fixes to
  USER-SETTINGS.md (~50-100 LOC). Suggested follow-up PR
  title: `docs(audit): Track D — apply atlas-side fixups
  to USER-SETTINGS`. Mirrors Track A's PR #175 pattern at
  the atlas layer.
- **Tier 2 (substrate-implementation-gated)**: 11 reserved
  atlas entries close when their substrate ships (Phase 2
  input framework / SessionModel implementation / Pane
  Model implementation).
- **Tier 3 (no action; forward-looking openness)**: none
  (Track D's findings are all closable).

**Sequencing**: Track D audit-loop closes with this PR.
Next plan-mode cycles can pick up:
- Tier 1 fixup PR — small mechanical update to
  USER-SETTINGS.md.
- Track E (doc currency) — verifies all docs reflect
  current state; cross-references work; SESSION-HANDOFF
  "where we left off" matches reality.
- Track F (backlog validation) — last in audit phase.
- D1 spec rename (Track C tier 2) — after maintainer ADR.

DOC-MAP.md updated.

### Added (Audit Track C — spec alignment classification)

`docs/AUDIT-SPEC-ALIGNMENT.md` — Track C of the
comprehensive audit phase. Cross-checks the three spec
documents (`spec/overview.md` 119 lines,
`spec/tech-plan.md` 1193 lines,
`spec/event-and-output-framework.md` 1495 lines) against
the 5 research-stage docs + current code shipped in PRs
#160-#169.

**Per CLAUDE.md spec-immutability rule, this audit
identifies but does NOT edit spec.** Findings are tiered
for follow-up cycles (doc-fix / spec-deviation requiring
ADR / spec hole / forward-looking openness).

Audit was performed via direct read 2026-05-07: read all
three spec docs in full (or with focus on relevant
sections); cross-check vocabulary against research-stage
docs; identify drift / holes / forward-looking openness.
No spec execution; no code changes.

**Findings summary**:

- **~22 ✅ aligned** — spec claims match research docs +
  current code.
- **7 ⚠ stale or holed** — all tech-plan.md holes
  covering substrate shipped in PRs #160-#169:
  suffix-diff/EditDelta, BulkChangeThreshold,
  BackspacePolicy, ModeBarrierFlushPolicy, hot-switch
  baseline-seed, per-pathway TOML selection,
  color-detection earcon path. None contradict code; all
  are temporal lag.
- **0 ❌ contradictions** — no spec claim is contradicted
  by current code or research docs.
- **5 📋 forward-looking openness** — spec deliberately
  reserves gaps for SessionModel substrate (overview.md
  line 95), Pane Model, spatial audio / ASIO, AI
  summarisation, per-content-type triggers. Not drift.

**Headline**: spec alignment is **good-to-excellent**.
Research-stage docs *extend* spec rather than diverge from
it. Tech-plan.md is the doc most "behind" current code
because it pre-dates the substrate cycle (#160-#169).
Event-and-output-framework.md (authored 2026-05-04) is
well aligned with shipped substrate naming.

**One spec-deviation candidate (D1)** flagged for
maintainer ADR review: `StreamProfile` (spec at
event-and-output-framework.md:824) → `PassThroughProfile`
(current code per Track A's PR #175 rename). 3-5 line
mechanical edit; mirrors Track A's doc-side fix at the
spec layer. Maintainer authorisation required per
CLAUDE.md.

**Three explicit supersession sections** in
tech-plan.md — §8 (interactive list detection), §9
(earcons + color), §10 (review mode + structured
navigation) — superseded by event-and-output-framework.md
§C.1 / §C.3 per its Part C. Recommended as part of a
future spec re-authoring cycle (deferred until Phase 2
input framework cycle ships, when spec re-snapshot covers
a stable baseline).

**Triage tiers**:

- **Tier 1 (doc-fix)**: NONE — Track A's PR #175 already
  closed PIPELINE-NARRATIVE drift; remaining ⚠ items are
  spec-side.
- **Tier 2 (spec-deviation, requires ADR)**: 1 item
  (D1 StreamProfile rename).
- **Tier 3 (spec hole, defer to next spec authoring
  cycle)**: 7 tech-plan.md items + 3 supersession notes.
- **Tier 4 (forward-looking openness, no action)**: 5
  items.

**Sequencing**: Track C audit-loop closes with this PR.
Next plan-mode cycles can pick up:
- Tier 2 spec-deviation (D1) — small follow-up PR after
  maintainer ADR.
- Track D (atlas alignment) and Track E (doc currency) —
  parallelisable with Track C; remaining audit tracks.
- Track F (backlog validation) — last; reflects
  post-audit state.

DOC-MAP.md updated.

### Added (Audit Track B — test inventory classification)

`docs/AUDIT-TEST-INVENTORY.md` — Track B of the
comprehensive audit phase. Test-layer counterpart to Track
A's code-consistency review (PR #172). Classifies all 531
tests across 27 meaningful test files
(`tests/Tests.Unit/` × 24 + `tests/Tests.Ui/` × 3) by
dominant pattern: algorithm-correctness, output-shape,
interaction, or mixed. Identifies fragility clusters,
substrate coverage gaps, and recommends targeted follow-up
cycles.

Audit was performed via static inspection 2026-05-07:
project enumeration → per-file test counts via
`grep -c "\[<Fact>\]\|\[<Theory>\]\|\[<Property>\]"` →
sample-classification per file → substrate cross-reference.
No code execution; no `dotnet test`.

**Findings summary**:

- **531 tests** total (527 `[<Fact>]` + 4 `[<Property>]` +
  0 `[<Theory>]`).
- **27 meaningful test files** (~7,079 LOC).
- **0 fixture corpus files** — all test data is
  code-defined.
- **4 property-based tests**, all in `VtParserTests.fs`
  (parser fuzz). Zero property tests for any other
  substrate component.

**Per-file status**: 19 ✅ healthy, 8 ⚠ fragile/stale, 0 ❌
structural concern.

The 8 ⚠ items cluster into themes:
- Output-shape fragility in StreamPathwayTests (largest
  file at 1431 LOC; payload-string assertions vulnerable
  to suffix-diff / debounce semantic changes — the kind
  that broke during PR #166).
- ChannelDecision-shape pinning in EarconProfileTests +
  FileLoggerChannelTests (assertions on decision count +
  payload).
- Record-shape pinning in OutputEventTests +
  UpdateMessagesTests.
- UIA-text-pattern shape pinning in TextPatternTests (Ui).
- Reflection-based brittleness in
  AutomationPeerReflectionTests.
- Low coverage in ConPtyHostTests (3 tests for the
  ConPTY surface).

**Substrate coverage**: shipping components
(StreamPathway, CanonicalState, OutputDispatcher, channels,
profiles, Config, FileLogger, parser, screen, key-encoding)
are well-tested. Forward-looking research-stage substrates
(SessionModel from item 28, Pane abstraction from item 30,
echo correlation + InputPathway from Phase 2) have **zero
tests** — substrate not yet implemented.

**4-tier recommendations** for follow-up cycles:

- **Tier 1 — immediate** (small, high-value): property
  tests for `longestCommonPrefixLength`,
  `computeRowSuffixDelta`, `CanonicalState.computeDiff`;
  diagnostic battery extensions for suffix-diff + bell.
- **Tier 2 — next-cycle** (medium): property tests for
  `Config.tryLoad`, `Coalescer.hashRowContent`; battery
  extensions for backspace-policy + mode-barrier;
  ConPtyHostTests expansion.
- **Tier 3 — substrate-implementation-gated**: SessionModel
  test suite (when item 28 implementation begins); Pane
  abstraction test suite (when Pane Model implementation
  begins); echo correlation tests (Phase 2); OSC 133
  detection tests (paired with SessionModel).
- **Tier 4 — fixture corpus + replay harness**: build
  `tests/Fixtures/shell-sessions/`; build a fixture loader
  module; longer-term snapshot/replay CLI binary (per
  backlog item 4).

Total recommended new property tests: ~25-37 across 5
clusters. Total recommended battery extensions: ~5 cases.

**Sequencing**: Track B audit-loop closes with this PR.
Subsequent narrower-concern cycles ship the new tests +
fixture corpus + diagnostic battery extensions one at a
time. Tracks C / D / E (spec / atlas / doc currency) and
Track F (backlog validation) continue per the audit master
plan.

DOC-MAP.md updated.

### Changed (Audit Track A — trivial doc-side fixups)

`docs/PIPELINE-NARRATIVE.md` — applies the doc-side drift
fixes that Track A of the comprehensive audit
(`docs/AUDIT-CODE-CONSISTENCY.md`, PR #172) identified.
Closes the audit→fixup loop for Track A; no code changes;
no semantic changes; pure mechanical drift correction so
PIPELINE-NARRATIVE matches the codebase as of 2026-05-07.

11 line edits across 5 distinct drift themes (the audit
identified 5 themes; cross-checking turned up additional
instances of each theme that the audit's 8 ⚠ summary
under-counted but that fall under the same fixes):

1. **`StreamProfile` → `PassThroughProfile`** rename
   propagated to all in-text references (4 sites).
   Module was renamed during the post-Phase-A substrate
   migration; only the glossary entry retains a
   historical-context note explaining the rename.
2. **`KeyEncoding` location** — corrected from
   `src/Terminal.Pty/KeyEncoding.fs` to
   `src/Terminal.Core/KeyEncoding.fs` (3 sites). The
   module was relocated; `src/Terminal.Pty/KeyEncoding.fs`
   does not exist.
3. **Stage 1 attribution** — clarified that the reader
   thread is composed in
   `Terminal.App.Program.startReaderLoop`, not "owned"
   by `ConPtyHost`. ConPtyHost provides the `readerLoop`
   helper; Program.fs starts the thread.
4. **`handleRowsChanged` line citation** — corrected
   from `Program.fs:1123-1131` to `Program.fs:965`. PRs
   #168 + #169 added ~160 LOC between audit baseline and
   now, shifting the handler ~158 lines up.
5. **Glossary entries** — KeyEncoding location +
   StreamProfile rename note (overlapping with themes
   1-2).

Verification: post-fix greps confirm zero remaining
references to the old paths / names (except the
intentional historical note in the glossary).

The audit doc (`AUDIT-CODE-CONSISTENCY.md`) is NOT
re-tagged; it's snapshot-dated 2026-05-06 and stays as
the audit-of-record. Future re-audits naturally
re-snapshot.

**Sequencing**: Track A's audit→fixup loop now closes.
Next plan-mode cycle picks up Audit Track B (test
inventory) per the substrate-first sequence.

### Added (Pane Model research stage — item 30)

`docs/PANE-MODEL.md` — snapshot-dated forward-looking
**sketch** for the multi-pane workspace framework.
Maintainer's directive 2026-05-07 framed the scope:

> "Please also include a brief sketch of how we might allow
> for additional panes to be added in the future such as a
> file tree or custom cherry picked input output pairs or
> language documentation or AI assistance. These are not
> urgent to spec out in detail now, but we want to ensure
> there's a good framework for adding more interactive panes
> in the future."

Phase 1 exploration confirmed the gap: pty-speak today is
single-surface, monolithic. `MainWindow.xaml` holds a single
`<TerminalView>` filling the window; `AppReservedHotkeys`
has no pane-navigation gestures; `TerminalAutomationPeer`
is constructed 1:1 per `TerminalView`; ActivityIds are
app-level constants. Adding a second surface is a clean
architectural break, not an incremental refactor.

The doc is **deliberately a sketch** — concrete enough that
implementation cycles have a framework to build on, but
not so detailed that it locks in decisions premature to
validate. Estimated final length: ~1100 lines (about half
INTERACTION-MODEL's 1400+, matching the maintainer's "brief
sketch" framing).

Doc structure:

1. The single-pane today — current shape; what's wired;
   what isn't (with file:line citations to MainWindow.xaml,
   TerminalView.cs, TerminalAutomationPeer, AppReservedHotkeys).
2. The multi-pane vision — workspace with multiple panes;
   shell pane is one of many.
3. Naming — Pane, Pane Coordinator, Workspace
   (definitions; relationship to SIM in INTERACTION-MODEL).
4. The pane contract — six concerns every pane must
   address (identity, content source, rendering,
   accessibility surface, input handling, lifecycle).
5. Pane catalog — table + per-pane paragraph sketch:
   - **Shell pane** ✅ (today's app, owned by SIM)
   - **File tree pane** 📋 (filesystem enumeration; OS
     file events)
   - **Cherry-picked I/O pairs pane** 📋 (consumes
     SessionModel queries)
   - **Language documentation pane** 📋 (local docset /
     web docs / LSP-sourced)
   - **AI assistance pane** 📋 (LLM API client;
     SessionModel-grounded)
6. Coordination protocols — three patterns (pane → shell
   action; shell → pane state; pane ↔ pane).
7. Accessibility — the hard problems — six named
   challenges (focus routing, NVDA review cursor,
   ActivityId scoping, pane-switch announcement, per-pane
   UIA pattern sets, compounded caret / UIA tension).
   Names but does not resolve.
8. Substrate gaps — six items missing today (Pane
   abstraction, Pane Coordinator, multi-content
   MainWindow, per-pane UIA peers, per-pane ActivityIds,
   pane-state TOML persistence).
9. Composition with existing substrate — cross-references
   to PIPELINE-NARRATIVE, SESSION-MODEL, INTERACTION-MODEL,
   ACCESSIBILITY-INTERACTION-MODEL, USER-SETTINGS, spec.
10. Versioning + maintenance — snapshot model.
11. Open questions — five for maintainer (naming;
    single-window vs. multi-window; floating vs. docked;
    TOML schema scope; WSL2/SSH shells as parallel panes
    vs. hot-switch within shell pane).

Companion-doc updates:

- `docs/INTERACTION-MODEL.md` — adds PANE-MODEL.md to
  front-matter companion list + new row in §6 cross-
  reference matrix as the UI-composition lens.
- `docs/DOC-MAP.md` — table entry.

**Sequencing**: PANE-MODEL is sister to INTERACTION-MODEL
at the UI-composition layer. After this lands, audit
Tracks B-F continue per the substrate-first sequence;
Phase 2 implementation cycles consume all five research
docs (PIPELINE-NARRATIVE, SESSION-MODEL, INTERACTION-MODEL,
PANE-MODEL, AUDIT-CODE-CONSISTENCY).

### Added (Interaction Model research stage — item 29)

`docs/INTERACTION-MODEL.md` — snapshot-dated architectural
framing doc that closes a gap surfaced by the maintainer's
reflection 2026-05-06. Names the **Shell Interaction
Manager (SIM)** abstraction; defines a **three-component
model** (Input Composition Surface / Active Output /
Historical Document); frames pty-speak's data shape as a
**structured computational document**; specifies an
**interactive element taxonomy** mapping every
`SemanticCategory` placeholder to triggers, producers,
consumers, and status.

The maintainer's question that prompted this doc:

> "Is [my model] making sense and matching what we
> currently have in the repo and is this well documented?
> I think we need to call this component of the system
> something specific so that I can reference it, how about
> the shell interaction manager …"

Phase 1 exploration confirmed the gap: PIPELINE-NARRATIVE
+ SESSION-MODEL together didn't articulate the higher-level
"what IS pty-speak" framing. Each focuses on its own lens
(operational mechanics; history substrate). INTERACTION-
MODEL sits one layer above, naming the abstraction that
makes the operational + history layers cohere.

Doc structure (~1400 lines):

1. The shell interaction reality — what's actually
   exchanged at the byte level. Honest about the
   complexity (no "command" or "result" abstraction at
   bytes; echo is implicit; structure has to be recovered).
2. The Shell Interaction Manager — five responsibilities;
   mapping to existing + future modules; rationale for
   "manager" naming; rationale for "coordinated set" not
   "single F# module".
3. The three-component model — Input Composition Surface
   (5.a; future), Active Output (5.b; partial today),
   Historical Document (5.c; future via SessionModel).
   Includes the canonical bidirectional-flow diagram.
4. Structured computational document framing — Jupyter /
   Wolfram analogy: where it transfers (linear ordering,
   per-cell state, navigation) and where it doesn't
   (streaming output, no re-execution, no inter-cell
   namespace).
5. Interactive element taxonomy — table covering every
   `SemanticCategory` case (✅ shipping vs. 📋 reserved)
   with triggers, producers, consumer interactions. Three
   NEW reservations proposed: `CommandFinished`,
   `InputCompletionMenu`, `MultiLineCommand` (deferred
   from enum until producers ship).
6. How this composes with existing substrates —
   cross-reference matrix; per-question doc-routing;
   two worked examples ("add prompt-boundary detection";
   "why does cmd typing announce twice?").
7. Substrate gaps — catalog of what's missing
   (Input Composition Surface, SessionModel, echo
   correlation, cursor-aware diff, etc.) with current
   workarounds + architectural fixes.
8. Versioning + maintenance — snapshot model; when to
   re-snapshot vs. when to substantively rewrite.
9. Open questions — six design forks awaiting maintainer
   input (SIM naming, literal-module emergence,
   doc-supersedence, enum reservations, notebook-analogy
   load-bearingness, per-shell vs. unified history).
10. Companion-doc cross-reference index.

The doc is **descriptive** for parts that ship today
(StreamPathway emit chain; mode-barrier handling; empty-
payload semantic events) and **forward-looking** for
parts the model RESERVES (Input Composition Surface as
distinct module; SessionModel-driven Active-Output /
History boundary; FormPathway for interactive elements).
Each piece is tagged so readers distinguish "this is real"
from "this is design intent".

**Sequencing**: closes the substrate-research-doc trio
(PIPELINE-NARRATIVE = operational; SESSION-MODEL = history;
INTERACTION-MODEL = framing). Subsequent work has a
shared vocabulary for "where does this live?" reasoning.
Audit Tracks B-F continue as planned; Phase 2 / Phase B
implementation cycles consume all three docs.

DOC-MAP.md updated with the new entry.

### Added (Audit Track A — code-consistency review)

`docs/AUDIT-CODE-CONSISTENCY.md` — first audit-phase
deliverable in the substrate-first sequence after
PR #170 (Pipeline Narrative) and PR #171 (SessionModel
design). Track A validates that every named entity in
`PIPELINE-NARRATIVE.md` matches what's actually in code.

The audit is performed via direct code inspection (Read +
grep). It does not execute code or run tests.

**Findings summary** (80+ entities verified across 6
categories):

- **81 ✅ matches doc** — no action needed
- **8 ⚠ minor drift** — small fixes needed, all
  doc-side (no code changes)
- **0 ❌ structural drift** — substrate's named
  structure is sound

The 8 ⚠ items cluster into 5 distinct themes:

1. `StreamProfile` was renamed to `PassThroughProfile`
   (3 doc references to update).
2. `KeyEncoding` lives in `Terminal.Core`, not
   `Terminal.Pty` (2 doc references).
3. Stage 1 attribution — reader thread is composed in
   `Program.fs:startReaderLoop`, not owned by
   ConPtyHost (clarification needed).
4. `handleRowsChanged` line citation moved from
   `Program.fs:1123-1131` to `Program.fs:965` due to
   intervening PRs.
5. Vocabulary glossary entries (KeyEncoding,
   StreamProfile) need rename / relocate.

**Recommendation**: a single small follow-up PR applies
all 5 trivial doc-side fixes to PIPELINE-NARRATIVE.md
(~30-50 LOC of edits). Not urgent; current doc is still
useful with minor drift.

**Sequencing**: Track A complete. Subsequent audit
tracks can now run:
- Track B (test inventory) — depends on Track A's "what's
  in code" baseline.
- Tracks C / D / E (spec / atlas / doc currency) —
  independent surfaces, parallelisable.
- Track F (backlog validation) — last; reflects
  post-audit state.

DOC-MAP.md updated with the new entry.

### Added (SessionModel substrate design — item 28 research stage)

`docs/SESSION-MODEL.md` — a snapshot-dated forward-looking
design doc for the SessionModel substrate. Specifies how
pty-speak captures structured (prompt, command, output,
exit-code) tuples sourced from OSC 133 escape sequences
emitted by shells, with heuristic fallback for shells that
don't emit OSC 133 (notably `claude.exe` per
`spec/overview.md:71`, and `cmd.exe` / PowerShell by
default).

The doc covers:

- **OSC 133 protocol** — the full four-boundary taxonomy
  (A/B/C/D), optional `aid=<command-id>` and key=value
  parameters, terminator handling, security
  considerations.
- **Heuristic fallback** — per-shell prompt-regex
  detection with stability checking, special-case Claude
  Code Ink-box detection, configuration via TOML.
- **Data model** — `SessionModel`, `SessionTuple`,
  `ActiveSessionTuple`, `BoundaryKind`, `BoundarySource`
  with full F# type definitions. Justifies design
  choices and explicitly documents what's deliberately
  omitted (stdout/stderr separation, per-character
  timestamps, cell-attribute preservation).
- **State machine** for the `ActiveSessionTuple` —
  PromptStart → AwaitingCommandStart → CommandStart →
  EditingCommand → OutputStart → OutputStreaming →
  CommandFinished → Complete. Recovery paths for
  out-of-order boundaries.
- **Pipeline integration** — maps SessionModel into the
  12-stage pipeline from `PIPELINE-NARRATIVE.md`. New
  notification variant `ScreenNotification.PromptBoundary`,
  new `DisplayPathway.T.OnPromptBoundary` method,
  Stage 3.5 insertion between notification emission and
  canonical-state synthesis. Threading guarantees,
  capture mechanics for prompt / command / output text.
- **Pathway integration** — pathway-by-pathway
  consumption: StreamPathway and TuiPathway stay
  history-unaware; ReplPathway / FormPathway /
  ClaudeCodePathway / AiInterpretedPathway are the
  primary SessionModel consumers; SessionConsumer base
  pathway provides shared mixin.
- **Persistence** — three policy modes (memory_only
  default; session_log; always); JSONL + msgpack
  formats; cross-session loading via item 4 (REPL CLI
  replay tool); security (env-var sanitiser applied to
  `commandText`; file permissions; opt-in only for
  non-default modes; bounded ring buffer).
- **Query API** — read methods (Active, History,
  LatestTuple, NthFromLast, TupleByCommandId,
  TuplesWhere, TuplesSince), internal write methods
  (Apply, AppendActiveOutput, AppendActiveCommand,
  PersistAndReset), diagnostic API
  (SnapshotForDiagnostic, HealthReport).
- **Implementation precedence** — 7 tiers, from
  substrate skeleton through ReplPathway → FormPathway
  / ClaudeCodePathway / history navigation →
  persistence → AI summarisation. Recommended
  ordering with rough scope estimates.
- **8 open questions** for maintainer review before
  implementation cycles start (cursor-in-canonical
  shape, fallback default on/off, alt-screen
  interaction, multi-line command capture, shell-switch
  + active tuple, echo correlation interaction,
  per-output-block AI summarisation timing,
  exit-code-meaning categorisation).

This document is the contract for the SessionModel
substrate. Implementation cycles consume it. Companion
to PIPELINE-NARRATIVE.md (vocabulary), USER-SETTINGS.md
(parameters), spec/event-and-output-framework.md (spec
absorbs this when implementation lands).

DOC-MAP.md updated with the new entry.

### Added (Pipeline Narrative research stage — substrate vocabulary doc)

`docs/PIPELINE-NARRATIVE.md` — a snapshot-dated design /
descriptive document that establishes the shared vocabulary
for how data flows through pty-speak. Captures:

- **12-stage glossary** of the output pipeline — byte
  ingestion through NVDA dispatch — with stable names,
  module locations, inputs / outputs, parameters
  (cross-referenced to USER-SETTINGS.md), and known
  fragilities per stage.
- **Event taxonomy** mapping every UI event (keypress,
  paste, screen mutation, mode change, bell, parser
  error, hotkey, shell switch, focus change, window
  resize, update, diagnostic battery, prompt boundary,
  command finished, Claude Code response boundary) to
  its producing source, data carried, pathway
  responsible, and current-vs-reserved status.
- **Substrate inventory** with explicit gap entries for
  forthcoming research-stage work: `InputPathway`
  protocol (Phase 2), `SessionModel` substrate (item 28),
  cursor-aware diff (Phase 2), echo correlation (Phase 2),
  per-input-vs-output ActivityId routing (Phase 2),
  scrollback navigation (item 27, downstream of item 28),
  Profile.Priority awareness (Phase 2), per-shell parameter
  overrides (Phase B / TOML).
- **Pathway taxonomy** — shipped pathways (StreamPathway,
  TuiPathway) and reserved-future pathways (ReplPathway,
  FormPathway, ClaudeCodePathway, AiInterpretedPathway,
  SessionConsumer) with substrate prerequisites.
- **Two end-to-end traces** — a single keystroke from
  WPF KeyDown to NVDA audible output (~17 stages); a
  single chunk of `dir` output from cmd.exe stdout to
  NVDA queue, including the bulk-change-fallback +
  scroll-fragility behaviour.
- **Seams catalogue** for future capabilities: echo
  correlation, OSC 133 / SessionModel, cursor-aware diff,
  per-input-vs-output ActivityId routing,
  scrollback / history navigation, AI-summarisation,
  Profile.Priority awareness, per-shell overrides.
- **Vocabulary glossary** — alphabetised reference for
  every named term.
- **Versioning + maintenance notes** — snapshot model,
  re-snapshot triggers, cross-doc consistency rules.

This document is the canonical authority for stage /
pathway / event-type names. Companion to USER-SETTINGS.md
(parameters), spec/event-and-output-framework.md (canonical
spec), and forthcoming docs/SESSION-MODEL.md (item 28).

DOC-MAP.md updated with the new entry.

### Fixed (shell-switch flush regression — PR #168 didn't fully resolve UX issue #5)

PR #168 changed the default `mode_barrier_flush_policy` from
verbose to `"summary_only"` to suppress the previous-shell
flush at shell-switch. Maintainer release-build validation
2026-05-06 confirmed that change worked at the
`onModeBarrier` level — but a SECOND code path was still
emitting the previous shell's screen content via
`processCanonicalState`'s bulk-change fallback. The user log
showed a 1610-character emit of the previous `dir` listing
fired immediately after the shell-switch, before the new
shell painted its startup output.

**Root cause**: `onModeBarrier` was clearing
`LastEmittedRowHashes` and `LastEmittedRowText` regardless of
policy (PR #166's behaviour, designed for the verbose-flush
case where post-barrier "Initial" emits made sense). Under
`SummaryOnly`, the cleared baselines made the next
`processCanonicalState` pass see the diff as "all 30 rows
changed" (against empty hashes) → above `BulkChangeThreshold`
(3) → bulk-change fallback engages → emits `ChangedText`
(the previous shell's content).

**Fix**: under `SummaryOnly` and `Suppressed`, preserve the
hash and text baselines at barrier time. The post-barrier
first emit's diff is then grounded against pre-barrier state
— rows still showing the previous shell's content match the
cache and produce 0-row diffs. When the new shell paints,
those rows DO change, and suffix-diff catches the new
content correctly.

Under `Verbose`, baselines are still cleared (preserves
PR #166's "user heard the flush, post-barrier emits Initial-
treat the new content" semantics).

#### Files

- `src/Terminal.Core/StreamPathway.fs` — `onModeBarrier`
  now branches on `ModeBarrierFlushPolicy`: clears baselines
  under `Verbose`, preserves under `SummaryOnly` /
  `Suppressed`. ~25 LOC change.
- `tests/Tests.Unit/StreamPathwayTests.fs` — 5 new tests:
  - "post-barrier first emit under SummaryOnly does NOT
    re-announce stale screen content" (the regression
    guard).
  - "post-barrier first emit under SummaryOnly emits new
    content when screen actually changes" (companion
    positive case).
  - "post-barrier first emit under Verbose policy does
    re-announce (Initial)" (verifies Verbose path
    unchanged).
  - "backspace with pause emits the deleted character"
    (pins the v1.1 backspace baseline for the simple
    case).
  - "backspace + retype within debounce window collapses
    to Replace" (documents the v1.1 debounce-collapsing
    limitation as expected behaviour — see USER-SETTINGS).

### Documented (v1.1 backspace debounce-collapsing limitation)

`docs/USER-SETTINGS.md`'s `stream.suffixDiff.backspacePolicy`
entry now includes a "Known v1.1 limitation: rapid backspace
+ retype is silenced (debounce-collapsing)" subsection.

When the user backspaces and re-types within the 200ms
debounce window, the cumulative leading-edge / trailing-edge
emit sees the FINAL state, computes the suffix via the
Append/Replace branch (not Shrink), and silently drops the
deleted segment. Pause-after-backspace works correctly;
rapid edit-mid-debounce gets absorbed.

The structural fix is **Phase 2's echo correlation**, which
tracks outgoing keystrokes independently of screen mutations
and can surface "Backspace was pressed" as an explicit
event. Documented here so v1.1 users have realistic
expectations and so the limitation is captured for the
Phase 2 design space.

### Changed (Tier 1 parameters from suffix-diff UX feedback)

Three parameters from `docs/USER-SETTINGS.md`'s "Suffix-diff
parameters (PR #166 follow-up)" section now ship as
configurable, with defaults updated where the old behaviour
was confirmed problematic during PR #166's release-build
validation:

- **`bulk_change_threshold`** (was a hard-coded `3` in
  `StreamPathway.fs`; now `[pathway.stream] bulk_change_threshold`,
  default 3). Above this many changed rows in a single frame,
  the suffix-diff stage bypasses per-row LCP and emits the
  full ChangedText (verbose fallback). Default unchanged;
  exposed for tuning.
- **`backspace_policy`** (was hard-coded "Silent" in PR #166;
  now `[pathway.stream] backspace_policy`, default
  `"announce_deleted_character"`). PR #166's "rely on NVDA
  keyboard echo for backspace audibility" assumption was
  confirmed wrong: NVDA's keyboard echo speaks the *key
  pressed* (Backspace), not the screen-content change. With
  the new default, backspacing the `i` from `echo hi` makes
  pty-speak announce `i` (the deleted segment of the row's
  rendered text). Legacy `"silent"` preserved as opt-in.
  Reserved `"announce_deleted_word"` treated identically to
  `"announce_deleted_character"` in v1.1 (word-boundary work
  is future).
- **`mode_barrier_flush_policy`** (was hard-coded verbose in
  PR #166; now `[pathway.stream] mode_barrier_flush_policy`,
  default `"summary_only"`). The PR #166 verbose flush at
  shell-switch / alt-screen / mode-change emitted the
  previous shell's full screen as a flush event before the
  new shell's startup — confirmed unpleasant (1200-character
  flushes of stale history). With the new default, the
  previous-shell flush is suppressed; the App-layer
  "switching to X" announce + the new shell's startup output
  carry the context. Legacy `"verbose"` preserved as opt-in.
  Subsumes strategic backlog items 23 + 24.

The internal F# DU case `BackspacePolicy.Silent` is exposed
as `SuppressShrink` to avoid collision with
`EditDelta.Silent`. The TOML value stays `"silent"`; the
rename is internal only.

What this addresses from the 2026-05-06 UX session:

- **UX issue #2** (backspace silent + NVDA also silent) —
  fixed by `backspace_policy` default change.
- **UX issue #5** (shell-switch reads previous shell's
  content) — fixed by `mode_barrier_flush_policy` default
  change.
- UX issues #1 (double-announce), #3 (cursor movement
  silent), #4 (stuttering on rapid typing) remain — they
  need the Phase 2 input framework substrate (echo
  correlation, cursor-aware diff, per-input-vs-output
  ActivityIds) and are documented in `USER-SETTINGS.md` as
  Tier 2 parameters.

Files: `src/Terminal.Core/StreamPathway.fs`,
`src/Terminal.Core/Config.fs`,
`tests/Tests.Unit/StreamPathwayTests.fs`,
`tests/Tests.Unit/ConfigTests.fs`.

### Added (sub-row suffix-diff at StreamPathway emit)

When typing `echo hi` at a shell prompt, NVDA used to read the
entire row on every keystroke (`> echo h`, then `> echo hi`).
Each character meant hearing the full prompt + command line
again. After this change, NVDA reads only the new content
since the last emit — `h`, then `i`. The verbose-readback
issue (GitHub #115/#139) regresses to "single-character
announcement on each keystroke" as intended in Phase A.

#### Behaviour

| Action | Before this PR | After this PR |
|---|---|---|
| Type `e` at a fresh prompt | Reads `> e` | Reads `e` (or `> e` on first prompt emit) |
| Type `c` after `e` | Reads `> ec` | Reads `c` |
| Type Enter on a populated line | Reads the full historical command + new prompt | Reads only the new content (next prompt + maybe output) |
| Press Backspace on `echo hi` | Reads `> echo h` | Silent (NVDA's own keyboard echo handles it) |
| Paste a multi-line script (5+ lines) | Reads the full content | Reads the full content (bulk-change fallback engages) |
| Switch shell with Ctrl+Shift+1/2/3 | Reads the new shell's startup output | Reads the new shell's startup output (mode-barrier flush stays verbose) |

#### Implementation

A new computational stage **sub-row suffix detection** sits
between the existing row-level diff (stage 7) and the
announcement payload assembly (stage 9). For each changed row
it reads the rendered text, compares against the cached
text-at-last-emit, and computes the longest-common-prefix.
The suffix beyond that prefix is what gets announced.

Three propose-defaults the maintainer approved
(redirectable on next iteration):

1. **Backspace silent.** Row shrink → no announcement; NVDA
   keyboard-echo handles the feedback at the keyboard layer.
2. **Bulk-change threshold N=3.** When more than 3 rows
   change in a single frame (scroll, screen clear, TUI
   repaint, multi-line paste), bypass suffix-diff and emit
   the full ChangedText. The maintainer benefits from full
   context at discontinuities; suffix-diff against a stale
   baseline would produce gibberish.
3. **Defer autosuggestion-aware diff to v2.** Fish-shell and
   zsh-autosuggestion-style ghost text (grey-foreground hint
   beyond cursor) over-reports under v1's pure-text LCP.
   Documented limitation; SGR-attribute-aware filtering is a
   follow-up.

#### Known v1 limitations

- **Mid-line insertion over-reports.** Cursor-left + type X
  into `> echo h` → `> echXo h` announces `Xo h` instead of
  just `X`. v2 (cursor-aware diff) fixes.
- **Powerline / Starship / oh-my-posh prompt redraws
  over-report.** Async git-status or kube-context segments
  resolve mid-keystroke and rewrite the PS1; LCP common
  prefix collapses; the whole line gets re-announced.
- **Right-aligned RPROMPT (zsh) over-reports.** Same family.
- **Autosuggestions over-report** (per Default 3 above).

#### Files

- `src/Terminal.Core/CanonicalState.fs` — extracted
  `renderRow snapshot rowIdx : string` helper from
  `renderChangedRows` so both the existing concatenated-text
  path and the new per-row suffix path share the trim +
  sanitise logic.
- `src/Terminal.Core/StreamPathway.fs` — added the
  `EditDelta` discriminated union (`Suffix of string |
  Silent`), `longestCommonPrefixLength`,
  `computeRowSuffixDelta`, `BulkChangeThreshold` (=3),
  `assembleSuffixPayload`, `renderAllRows`. Extended
  `State` with `LastEmittedRowText: string[]` and
  `PendingSnapshot: Cell[][] voption`. Wired into all five
  state-update sites: leading-edge emit (in
  `processCanonicalState`), trailing-edge tick (in
  `onTimerTick`), mode-barrier flush, `resetState`, and
  `seedBaseline`.
- `tests/Tests.Unit/StreamPathwayTests.fs` — 14 new tests
  covering the algorithm (`longestCommonPrefixLength`,
  `computeRowSuffixDelta`) and the integration behaviour
  (typing-at-prompt, multi-keystroke debounce, bulk-change
  fallback, backspace silent, first-emit-Initial, mode-
  barrier-clears-cache).

PR #164's regression tests (red row outside changed-rows scope
→ no ErrorLine emit) survive the refactor — confirms the
Phase A.2 hotfix interaction is preserved.

The diagnostic battery's existing T1-T5 PowerShell tests are
unchanged and continue to validate the colour-detection +
emit-shape regression coverage. New T6/T7 tests for suffix-
diff specifically are deferred to a follow-up PR (the v1
verification path is unit tests for the algorithm + manual
NVDA matrix for the UX).

### Added (Ctrl+Shift+D extended diagnostic battery)

Ctrl+Shift+D now runs a self-test battery against the active
shell rather than just snapshotting + replaying earcons. Goal:
collapse the manual NVDA validation loop ("open shell → run
command → listen → describe what NVDA said") into a single
"press hotkey, paste log" round-trip. The maintainer's framing
2026-05-06: every future colour-detection or pathway behaviour
change should be validatable without minutes of describe-what-
you-heard.

What runs on press:

1. Process snapshot via `enumerateShellProcesses` (kept from the
   previous behaviour) — counts of `cmd.exe`,
   `powershell.exe`, `pwsh.exe`, `claude.exe`, and
   `Terminal.App.exe`.
2. Earcon replay (kept) — plays bell-ping, error-tone,
   warning-tone with NVDA labels between each, so the audio
   path is verifiable independent of the test battery.
3. Per-shell command battery, exercising the canonical pipeline
   end-to-end. For each test case: write command bytes via
   `ConPtyHost.WriteBytes`, capture the resulting `OutputEvent`s
   via a temporary tap on `OutputDispatcher`, compare against
   expected, log PASS / FAIL with full event detail.
4. Summary line + clipboard copy of the full diagnostic log,
   announced via NVDA. Maintainer pastes the log into chat
   directly — no `Ctrl+Shift+;` step needed.

PowerShell test set (5 cases, each ~1.5s settle window):

* **T1.Plain** — `Write-Host` plain text emits `StreamChunk`
  only, no colour event.
* **T2.Red** — `Write-Host -ForegroundColor Red` emits
  `StreamChunk + ErrorLine`.
* **T3.Yellow** — `Write-Host -ForegroundColor Yellow` emits
  `StreamChunk + WarningLine`.
* **T4.PlainAfterRed** — red command, then plain. Asserts the
  second emit has NO `ErrorLine` (PR #164 hotfix regression
  guard — catches "stale red row in snapshot fires error-tone
  on every keystroke").
* **T5.YellowAfterRed** — red command, then yellow. Asserts
  the second emit has `WarningLine`, not `ErrorLine` (catches
  "red precedence shadows yellow when red is outside the diff
  scope").

Cmd test set (1 case): plain `echo`. Cmd has minimal colour
support; ANSI-escape colour testing is a separate follow-up.

Claude shell: command battery skipped (non-deterministic
interactive AI). Snapshot + earcon replay still run.

Diagnostic log file:
`%LOCALAPPDATA%\PtySpeak\logs\YYYY-MM-DD\diagnostic-YYYY-MM-DD-HH-mm-ss-fff.log`.
Same parent folder as regular `pty-speak-*.log` so
`Ctrl+Shift+L` opens the right area; `diagnostic-` prefix is
the distinguisher. Crash-safety mirrors `FileLogger`:
`StreamWriter.AutoFlush = true` + `FileShare.ReadWrite` +
`FileMode.Create`. Every line is on disk before the next is
queued, so a crash mid-battery still leaves a tail file with
everything up to the crash.

New infrastructure under the hood:

* `OutputDispatcher.installEventTap` — registers an
  `OutputEvent -> unit` observer that fires before profile
  fan-out. Returns `IDisposable` for cleanup. Tap exceptions
  are swallowed so a misbehaving tap can't break production
  routing. 6 new unit tests in
  `tests/Tests.Unit/OutputDispatcherTests.fs` cover register-
  fires, dispose-stops, multi-tap fan-out, single-dispose-
  doesn't-affect-others, throwing-tap-doesn't-break-dispatch,
  and tap-fires-with-no-profiles.
* `Terminal.App.Diagnostics` (new module) — owns the writer,
  the tap wrapper, the test definitions, the per-test loop,
  and the entry point `runFullBattery`. ~510 lines; lives
  under `src/Terminal.App/Diagnostics.fs`.

Files:

* `src/Terminal.App/Diagnostics.fs` — new module.
* `src/Terminal.App/Terminal.App.fsproj` — `Diagnostics.fs`
  ordered before `Program.fs`.
* `src/Terminal.Core/OutputDispatcher.fs` — `installEventTap` +
  `fireTaps` + tap-firing in `dispatch` and `dispatchTick`.
* `src/Terminal.App/Program.fs` — old `runDiagnostic` body +
  `enumerateShellProcesses` helper deleted (~140 lines, plus
  the dead commented-out PowerShell-script launch). The
  Ctrl+Shift+D bind moves to the closure-bind group near the
  health-check + incident-marker binds because the new
  closure captures `hostHandle` / `currentShell` /
  `screen.SequenceNumber`.
* `tests/Tests.Unit/OutputDispatcherTests.fs` — 6 new tests
  for the tap surface.

The `scripts/test-process-cleanup.ps1` script remains in the
repo and can still be invoked manually for close-and-recheck
testing — it just no longer launches from Ctrl+Shift+D (the
launch was already commented out as of 2026-05-04; this PR
removes the dead code and the surrounding stale comments).

### Fixed (Phase A.2 hotfix — colour detection scoped to diff)

The maintainer's release-build validation of Phase A.2 (PR
#163) surfaced two coupled symptoms:

1. After running `Write-Host -ForegroundColor Red "Build
   failed"` (which plays `error-tone` correctly), the next
   command `Write-Host -ForegroundColor Yellow "..."` plays
   `error-tone` AGAIN — same tone, never `warning-tone`.
2. The error-tone fires on every keystroke while typing the
   new command, even though only the prompt row is changing.

Root cause: Phase A.2's `snapshotDominantColor` walked the
ENTIRE `canonical.Snapshot` rather than just the rows in
`diff.ChangedRows`. After the red command, "Build failed"
remains rendered red on its row in the screen buffer. Every
subsequent frame sees that red row (even though it's
unchanged), `snapshotDominantColor` returns `Some "red"`,
red wins precedence over yellow, ErrorLine fires, and the
warning-tone path is starved.

Behaviour intent: the colour earcon should supplement *new*
content, not "any colour anywhere on the screen". A red error
that scrolls off shouldn't keep beeping; a yellow warning
typed into a buffer that still contains earlier red text
should fire warning-tone, not error-tone.

Fix: new `changedRowsDominantColor` helper that walks only the
rows in `diff.ChangedRows` (or the pending diff's changed rows
at trailing-edge flush time). The leading-edge emit branch and
the debounce-accumulate branch both call this scoped variant
instead of the whole-snapshot version. The original
`snapshotDominantColor` is retained for the helper-level unit
tests and for any future caller that legitimately wants the
whole-snapshot scan.

Files:
- `src/Terminal.Core/StreamPathway.fs` — new
  `changedRowsDominantColor`; `processCanonicalState` calls it
  from a `computeColor (changedRows: int[])` local closure
  that's invoked inside the leading-edge and accumulate
  branches against the diff's ChangedRows.
- `tests/Tests.Unit/StreamPathwayTests.fs` — 2 new tests:
  `changedRowsDominantColor` walks only the supplied indices;
  the integration regression — red row outside diff scope
  produces no ErrorLine emit.

The fix is small (~80 LOC + 2 tests) and the existing 14
Phase A.2 colour-detection tests all stay green because their
red/yellow snapshots have the coloured rows IN the diff (the
tests construct fresh state and emit on first call, where
every row is "changed").

### Added (Phase A.2 — colour-detection earcons re-introduced)

Re-introduces the SGR colour-detection feature originally
shipped in Stage 8d.2 (PR #156) and reverted in PR #157 due
to an unknown release-build regression. The new design avoids
the original's structural flaw + likely cause by using
**event-splitting** on the Phase A pathway substrate rather
than `Extensions["dominantColor"]` stamping on synthesized
events.

**User-visible behaviour.** When red-foreground text dominates
the streaming output (>50% of non-blank cells in any row of
the canonical snapshot), pty-speak plays a brief 400Hz tone
(`error-tone` earcon, ~150ms) alongside NVDA reading the
content. Yellow-foreground dominant text plays 600Hz
(`warning-tone`, ~120ms). Both are supplementary to NVDA — no
double-announce, no replacement of the spoken content. v1
matches the standard 16-colour ANSI palette
(`Indexed 1` / `Indexed 9` for red, `Indexed 3` / `Indexed 11`
for yellow); 256-cube reds and Truecolor RGB-distance matching
are deferred per the original 8d.2 plan.

**Configurability.** Per the C2 principle ("transparent + user
configurable + sensible defaults"), the feature is on by
default but disable-able via TOML:

```toml
# %LOCALAPPDATA%\PtySpeak\config.toml
[pathway.stream]
color_detection = false
```

`true`/`false` recognised; non-bool values log a Warning and
fall back to `true`. Schema documented in `docs/USER-SETTINGS.md`.

**What changed structurally vs original 8d.2.** The original
stamped `Extensions["dominantColor"]` on the StreamProfile-
synthesized `StreamChunk` event; EarconProfile read
`event.Extensions` to decide whether to emit an earcon. That
design had a latent bug: the dispatcher fans the SAME raw
input event to all profiles in parallel; EarconProfile only
saw the RAW translator event (with empty Extensions), not the
StreamProfile-synthesized event with the colour metadata. The
unit tests passed because they fed EarconProfile pre-populated
synthetic events with Extensions, bypassing the dispatcher. The
release-build regression where NVDA stopped reading is also
suspected (though never confirmed at runtime) to involve the
non-empty Extensions interaction with the synthesized event
flow.

The new design splits the colour signal into a SECOND
`OutputEvent` with a dedicated semantic category (`ErrorLine`
for red, `WarningLine` for yellow) and an EMPTY payload.
EarconProfile claims ErrorLine/WarningLine semantically (no
Extensions snoop). NvdaChannel skips the empty payload
(NvdaChannel.fs:87 `RenderText "" -> ()`) — no double-announce.
FileLogger records both events for the audit trail. The
dispatcher's existing per-profile fan-out works correctly: the
ErrorLine event is the ACTUAL OutputEvent dispatched, and
EarconProfile sees it directly.

**State management for trailing-edge flush.** Within the
StreamPathway's debounce window, the colour is captured
alongside the pending diff in a new `State.PendingColor:
string voption`. The trailing-edge `onTimerTick` flushes both
events together. `OnModeBarrier` clears the pending colour
WITHOUT emitting the supplementary event — mode barriers are
themselves discontinuities (alt-screen toggle, vim exiting),
and emitting an error-tone for the flushed pre-barrier diff
would be misleading.

**Library + palette.** No new packages or palette entries
needed. `EarconPalette.defaultPalette` retained `error-tone`
(400Hz × 150ms × 10ms) + `warning-tone` (600Hz × 120ms × 10ms)
across the 8d.2 revert. NvdaChannel's `semanticToActivityId`
already maps ErrorLine + WarningLine to `ActivityIds.error`.

Files:
- `src/Terminal.Core/StreamPathway.fs` — colour-detection
  helpers (`isRedFg`, `isYellowFg`, `rowDominantColor`,
  `snapshotDominantColor`); `colorOutputEvent` builder;
  `Parameters.ColorDetection: bool`; `State.PendingColor`;
  `processCanonicalState` + `onTimerTick` + `onModeBarrier`
  + `resetState` updates.
- `src/Terminal.Core/EarconProfile.fs` — `Apply` gains
  `ErrorLine -> error-tone` and `WarningLine -> warning-tone`
  cases.
- `src/Terminal.Core/Config.fs` — `tryGetBool` helper;
  `StreamParameterOverrides.ColorDetection: bool option`;
  `parseStreamOverrides` + `resolveStreamParameters` +
  `knownStreamKeys` updates.
- `tests/Tests.Unit/StreamPathwayTests.fs` — 14 new tests:
  helpers (red/yellow/threshold/blank), snapshot-level
  precedence, double-emit (red, yellow, plain),
  ColorDetection=false, trailing-edge flush, mode-barrier
  pending-colour handling.
- `tests/Tests.Unit/EarconProfileTests.fs` — replaces the
  "8d.2 will claim" pin with two new tests:
  `ErrorLine -> error-tone` and `WarningLine -> warning-tone`.
- `tests/Tests.Unit/ConfigTests.fs` — 4 new tests:
  default-true invariant, `color_detection = false` parses,
  `color_detection = true` round-trip, non-bool warning.
- `docs/USER-SETTINGS.md` — schema example + defaults table
  row for `color_detection`.

**Out of scope** (carried forward):

- 256-colour cube reds (`Indexed 196` etc.)
- Truecolor RGB-distance matching (`Rgb` ColorSpec)
- Background-colour reds (`Bg` field)
- `ParserError -> error-tone` routing — would need cross-
  profile suppression substrate (NvdaChannel announces
  ParserError on the error activityId; doubling up earcon +
  announce would be noisy)
- Per-shell colour-detection toggles
  (`[shell.cmd] color_detection = false`) — uniform pathway-
  level config is enough for v1
- TuiPathway colour detection — TuiPathway emits no
  StreamChunks; separate concern
- Earcon palette frequency/duration overrides via TOML
- Earcon dedup for sustained red errors (multiple emits across
  rapid frames produce multiple earcons; matches original
  8d.2 behaviour; refine if NVDA validation flags noisy)

### Added (Phase B subset — TOML config for pathway selection + parameters)

Closes the Phase B "TOML config" item the Phase A plan deferred
(`/root/.claude/plans/hello-i-lost-my-velvet-deer.md` decision
#5: "Per-shell pathway selection in v1 — Hardcoded mapping in
Program.fs"). With C2 landed, users can override pathway
selection and pathway parameters via a TOML file at
`%LOCALAPPDATA%\PtySpeak\config.toml` without rebuilding
pty-speak.

Closes the loop on the maintainer's guiding principle for
display configurability:

> The interpreter pathway should be transparent and user
> configurable and customizable with sensible defaults.

C2 ships the *substrate* that realises this principle for
pathway selection + pathway parameters. Future stages build on
the substrate: per-content-type triggers (regex / colour /
semantic-parser / LLM) are Phase 2/3 territory and need actual
pathways that consume the triggers; runtime "adjust on the fly,
save as template" workflows are Phase 2/3 UI work that this
substrate enables.

**Schema (v1).** `schema_version = 1` is required. Two table
families:

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

`[shell.<id>]` keys mirror the lowercase IDs `parseEnvVar`
recognises (`cmd`, `claude`, `powershell`). `pathway` values
are pathway IDs (`"stream"`, `"tui"`; future Phase 2 IDs added
without schema migration). `[pathway.<id>]` tables hold
per-pathway parameter overrides; v1 ships `[pathway.stream]`
with the four StreamPathway tunables.

**Forward-compat with input-bindings.** Spec
`event-and-output-framework.md` A.5 sketches a future schema
for hotkey overrides (`[[bindings]]` / `[[handlers]]` arrays
at top level). C2's loader silently ignores those sections —
the same `config.toml` will accumulate sections cumulatively
across Phase B sub-stages without conflict.

**Defaults preserved exactly.** `Config.defaultConfig` matches
`StreamPathway.defaultParameters` field-for-field; absence of
the file (or any key within it) is byte-equivalent to pre-C2
behaviour. Tests pin this equivalence.

**Error handling — never crashes, never opens a dialog.**
The maintainer is a screen-reader user; every error path logs
via `ILogger` (routed to FileLogger, readable via
`Ctrl+Shift+;`) and falls back to defaults:

- Missing file → Information log; defaults
- Malformed TOML → Error log (with parse detail); defaults
- Schema version newer than supported → Error log; defaults
- Unknown pathway name (e.g. `"stram"`) → Warning; that shell
  falls to default
- Unknown parameter key → Warning ("ignored"); drop value
- Negative or zero parameter → Warning ("clamped to default");
  use default for that field

A single `Information` line on startup summarises the resolved
config (e.g. `Config loaded: cmd→stream, claude→stream,
powershell→stream; stream debounce=200ms`).

**TOML library.** Tomlyn 0.18.0 (xoofx, BSD 2-Clause). Spec
already named it (`tech-plan.md:997`,
`event-and-output-framework.md:501`); this is its first
appearance.

Files:
- `Directory.Packages.props` — Tomlyn 0.18.0 PackageVersion
  added.
- `src/Terminal.Core/Terminal.Core.fsproj` — Tomlyn
  PackageReference + `Config.fs` Compile entry.
- `src/Terminal.Core/Config.fs` — new module with `Config`
  record, `defaultConfig`, `tryLoad`, `resolveShellPathway`,
  `resolveStreamParameters`, `defaultConfigFilePath`.
- `src/Terminal.App/Program.fs` — `Config.tryLoad` invoked at
  composition root; `selectPathwayForShell` rewritten as a
  closure capturing the loaded config (collapses three
  identical hardcoded match arms into one config-consulting
  body).
- `tests/Tests.Unit/ConfigTests.fs` — new file pinning the
  loader behaviour (15 tests covering parse OK / malformed /
  defaults / overrides / unknown keys / schema versioning /
  coexistence with future `[[bindings]]` sections).
- `tests/Tests.Unit/Tests.Unit.fsproj` — `ConfigTests.fs`
  Compile entry.
- `docs/USER-SETTINGS.md` — intro updated to note C2 ships
  the TOML substrate; new "Pathway selection" section with
  schema, defaults table, error semantics, out-of-scope
  list. "Process for adding a new setting" updated to tick
  off "substrate is shipped".

**Out of scope** (explicit, for reviewer + future-self
clarity):

- Hot-reload — changes require a restart.
- Runtime config write-back from a hotkey or palette ("save
  current settings as my config" workflow). Phase 2/3.
- Kill-switch substrate (`Ctrl+Shift+K` + `extensibility.killSwitch`
  per spec A.6). Phase B input-bindings owns this.
- Per-content-type triggers (regex / colour / semantic-parser
  / LLM dispatchers). Phase 2/3 — needs actual semantic /
  AI-interpretation pathways that consume the triggers.
- ClaudeCodePathway / ReplPathway / FormPathway selection —
  those pathways don't exist yet (Phase 2).
- Schema additions for input-binding overrides
  (`[[bindings]]`). Separate Phase B sub-stage; coexists
  cleanly with the current schema.
- Config validator binary or CLI.

### Added (Phase B subset — alt-screen → TuiPathway auto-detect)

`TuiPathway` shipped in Phase A as a wired-but-never-selected
option (only the hardcoded `selectPathwayForShell` mapping
chose pathways, and v1 mapped cmd / powershell / claude all to
StreamPathway). The Phase A plan deferred auto-detection of
pathway from screen state to "Phase B paired with TOML config"
because mid-session pathway swap had unsettled state semantics.

Phase A.1's `DisplayPathway.T.SetBaseline` primitive resolved
the state-semantics concern (no leaked baseline, no stale-diff
regression on swap), so alt-screen auto-detect is now
independently shippable without the TOML config.

**Behaviour.** When the active shell enters an alt-screen TUI
(`vim`, `less`, `top`, full-screen `fzf`), the screen fires
`ModeChanged(AltScreen, true)`; the `PathwayPump` swaps the
active pathway to `TuiPathway` (no streaming output — review-
cursor / browse-mode is the navigation primary). When the user
quits the TUI, `ModeChanged(AltScreen, false)` swaps the
pathway back to whatever the active shell's default is
(`StreamPathway` for cmd / powershell / claude in v1).

Without this change, the user's NVDA experience inside vim was
constant streaming announcements as the editor repainted —
unusable. The vim experience now matches the implicit Stage 5
alt-screen suppression that pre-Stage-8 pty-speak shipped, but
with the pathway substrate's state-management discipline
(flush via `OnModeBarrier`, `Reset` outgoing, seed `SetBaseline`
on incoming) so no diff state leaks across the swap.

**Implementation.** A new pure decision module
`PathwaySelector` returns one of `Keep` / `SwapToTui` /
`SwapToShellDefault` from `(currentPathwayId, ScreenNotification)`
without depending on `Terminal.Pty`'s `ShellRegistry`. The
`PathwayPump`'s `handleModeChanged` calls it after the existing
`OnModeBarrier` flush + before the existing barrier-OutputEvent
dispatch; on a non-`Keep` decision, it runs the same
flush/Reset/reassign/SetBaseline sequence used by the
`switchToShell` hot-switch path so the swap-state semantics are
identical.

The PathwayPump tracks a new `currentShellId` mutable
(initialised to `Cmd`, updated on startup-shell resolve and on
`switchToShell`'s `Ok newHost` branch) so a `SwapToShellDefault`
on alt-screen exit resolves to the active shell's default
pathway, not always to cmd's.

Files:
- `src/Terminal.Core/PathwaySelector.fs` — new pure decision
  module.
- `src/Terminal.Core/Terminal.Core.fsproj` — `<Compile Include>`
  for the new module.
- `src/Terminal.App/Program.fs` — `currentShellId` mutable +
  `swapPathwayForAltScreen` helper + updated `handleModeChanged`
  + sync at startup-resolve and `switchToShell`.
- `tests/Tests.Unit/PathwaySelectorTests.fs` — 11 tests
  exhausting the swap matrix (alt-screen entry/exit × current
  pathway × non-alt-screen flag toggles + non-ModeChanged
  notification defensives).
- `tests/Tests.Unit/Tests.Unit.fsproj` — `<Compile Include>`
  for the new test file.
- `CHANGELOG.md` — this entry.

**Out of scope.** The TOML-driven per-shell pathway selection
that was originally bundled with this change in the Phase B
sequencing — landing separately so the user-facing alt-screen
experience improves without waiting on a config-file design
cycle. Auto-detection of OSC 133 → ReplPathway is also
deferred (no ReplPathway exists yet; Phase 2 territory).

### Fixed (Phase A.1 — hot-switch baseline-seed)

The maintainer's NVDA validation pass on Phase A surfaced a
regression on shell hot-switch (`Ctrl+Shift+1/2/3`): after
switching from Claude Code to cmd, NVDA kept reading the
previous shell's screen content for the first emit of the new
shell, instead of just the new shell's first paint.

Cause: `activePathway.Reset ()` clears the pathway's
`LastEmittedRowHashes` baseline to `[||]`, which makes the
next `Consume` call treat every row as "new" and emit the
entire screen verbatim. The screen buffer itself isn't cleared
on shell-switch (existing framework-cycle deferral; see the
`switchToShell` comment block at `Program.fs`), so the entire
screen at the moment of the first emit still contains the
previous shell's content overlaid by however much the new
shell has painted so far.

Pre-Phase-A this didn't bite because every emit shipped the
full screen anyway (verbose-readback). Post-Phase-A the diff-
only contract makes the shell-switch first-emit conspicuous.

Fix: add `SetBaseline: Canonical -> unit` to `DisplayPathway.T`.
`switchToShell` calls it immediately after the new pathway is
constructed and BEFORE `wirePostSpawn` starts the new reader
loop, seeding the baseline with the screen's snapshot at the
moment of switch. The next `Consume` then emits only the rows
the new shell actually painted (typically just the new
prompt), not the residual content from the previous shell.

`StreamPathway.SetBaseline` writes
`canonical.RowHashes` into both `LastEmittedRowHashes` (the
diff baseline) and `LastRowHashes` (the spinner-suppress
"previous frame hashes" — without seeding this too, the first
post-switch Consume would mark every row as "changed" against
`ValueNone` and over-count spinner-history entries for the
seeded frame's content).

`TuiPathway.SetBaseline` is a no-op — TuiPathway is stateless
and never tracks baselines.

Files:
- `src/Terminal.Core/DisplayPathway.fs` — `T` record gains
  `SetBaseline: Canonical -> unit`.
- `src/Terminal.Core/StreamPathway.fs` — internal `seedBaseline`
  helper + wires through `create` + `createWithExposedState`.
- `src/Terminal.Core/TuiPathway.fs` — no-op `SetBaseline`.
- `src/Terminal.App/Program.fs` — `switchToShell` snapshots the
  screen, builds `Canonical`, calls
  `activePathway.SetBaseline canonical` before
  `wirePostSpawn newHost`.
- `tests/Tests.Unit/StreamPathwayTests.fs` — 4 new tests
  pinning the baseline-seed behaviour.
- `tests/Tests.Unit/TuiPathwayTests.fs` — 1 new test pinning
  the no-op contract.

### Changed (Phase A — display-pathway substrate)

Resolves the verbose-readback regression
(GitHub #115/#139): NVDA reads "h" then "i" when typing
`echo hi`, instead of the cmd banner re-announced each
keystroke. The streaming-output emission becomes a per-frame
diff rather than a full-screen render.

The change introduces a 4-layer architecture between the raw
screen and the existing OutputEvent/Profile/Channel substrate
(Stages 8a-8d), so future shells can plug in differentiated
pathways (claude-code, alt-screen TUIs, REPLs, semantic-
segmentation pathways, AI-interpretation pathways) without
rewiring the dispatcher.

**Layer 2 — canonical-state substrate.** `CanonicalState.create`
wraps a screen snapshot + per-row hashes (`Coalescer.hashRow`
position-aware + `Coalescer.hashRowContent` content-only) +
a pure `computeDiff: previousRowHashes -> CanonicalDiff`. The
substrate is mostly stateless; the pathway carries the previous
row hashes as a single `uint64[]`.

**Layer 3 — display pathways.** `DisplayPathway.T` is the
pathway interface (`Consume` / `Tick` / `OnModeBarrier` /
`Reset`). Two pathways ship in Phase A:
- **StreamPathway.** Replaces the old StreamProfile compute
  loop. Preserves the four StreamProfile algorithms verbatim
  (frame-hash dedup, per-key + cross-row spinner gates,
  leading + trailing-edge debounce, mode-barrier reset). The
  EMISSION layer changes: emits StreamChunk OutputEvents with
  diff text, not full-snapshot text.
- **TuiPathway.** Stateless alt-screen-aware pathway. Suppresses
  streaming output (NVDA review-cursor / browse-mode is the
  navigation primary for full-screen TUIs); emits a ModeBarrier
  OutputEvent on `OnModeBarrier`. Selectable but not yet
  auto-detected from alt-screen state — Phase B will introduce
  alt-screen auto-detection alongside the TOML config.

**Layer 4 — preserved.** EarconChannel, FileLoggerChannel,
NvdaChannel, EarconProfile, OutputDispatcher are unchanged. A
new **PassThroughProfile** carries the StreamProfile catch-all
fan-out (every event → NVDA + FileLogger as RenderText
decisions); the active profile set becomes
`[ passThroughProfile; earconProfile ]`.

**Per-shell pathway selection.** Hardcoded in `Program.fs`
(`selectPathwayForShell`) for Phase A: cmd / powershell /
claude → StreamPathway. Phase B will replace the hardcoded
mapping with TOML config (`[shell.X] pathway = "..."`).
Hot-switch (`Ctrl+Shift+1` / `+2` / `+3`) calls
`activePathway.Reset ()` then reassigns from the helper.

**PathwayPump replaces TranslatorPump.** Reads
ScreenNotifications and routes by case: RowsChanged builds
canonical state + dispatches `activePathway.Consume`'s output;
ModeChanged calls `activePathway.OnModeBarrier` (flushes
pending pathway state) then dispatches the barrier OutputEvent;
Bell + ParserError go through OutputEventBuilder unchanged. A
companion **PathwayTickPump** (50ms `PeriodicTimer`) drives
trailing-edge flush via `activePathway.Tick`.

`StreamProfile.fs` is deleted; its algorithm helpers live
verbatim inside `StreamPathway.fs`. Its catch-all fan-out lives
in `PassThroughProfile.fs`.

Files:
- `src/Terminal.Core/CanonicalState.fs` — new (Layer 2).
- `src/Terminal.Core/DisplayPathway.fs` — new (Layer 3
  interface).
- `src/Terminal.Core/StreamPathway.fs` — new (StreamProfile
  algorithm + diff-only emission).
- `src/Terminal.Core/TuiPathway.fs` — new (alt-screen-aware
  pathway).
- `src/Terminal.Core/PassThroughProfile.fs` — new
  (StreamProfile catch-all replacement).
- `src/Terminal.Core/StreamProfile.fs` — DELETED.
- `src/Terminal.App/Program.fs` — TranslatorPump+TickPump
  replaced by PathwayPump+PathwayTickPump; per-shell pathway
  selection wired into composition root and shell hot-switch.
- `tests/Tests.Unit/CanonicalStateTests.fs` — new
  (`computeDiff` + `create` algorithm pins).
- `tests/Tests.Unit/StreamPathwayTests.fs` — new (algorithm
  pins migrated verbatim from StreamProfileTests + Consume /
  Reset / OnModeBarrier wiring tests).
- `tests/Tests.Unit/TuiPathwayTests.fs` — new (suppress-on-
  Consume + ModeBarrier emit pins).
- `tests/Tests.Unit/StreamProfileTests.fs` — DELETED (algorithm
  tests migrated to StreamPathwayTests + CanonicalStateTests;
  profile-record-shape tests removed since StreamProfile is
  gone).

### Fixed (EarconPlayer — fresh WasapiOut per play)

The post-PR-#157 release-build logs showed the second + third
earcons in the Ctrl+Shift+D diagnostic failing with
`AUDCLNT_E_ALREADY_INITIALIZED` (HRESULT `0x88890002`):

```
[INF] WasapiOut initialised. Device=Speakers (Realtek(R) Audio)
[DBG] Earcon play started. EarconId=bell-ping ...
[INF] Earcon play failed; suppressing. EarconId=error-tone Reason=0x88890002
      System.Runtime.InteropServices.COMException (0x88890002):
        at NAudio.CoreAudioApi.AudioClient.Initialize(...)
        at NAudio.Wave.WasapiOut.Init(...)
[INF] Earcon play failed; suppressing. EarconId=warning-tone Reason=0x88890002
```

Root cause: NAudio's `WasapiOut.Init` cannot be called twice
on the same `WasapiOut` instance — the underlying
`AudioClient.Initialize` throws `AUDCLNT_E_ALREADY_INITIALIZED`
on second call. The 8d.1 release shipped with a lazy-singleton
`WasapiOut` that played the first earcon successfully then
failed silently on every subsequent play. The bug went
unnoticed because the original 8d.1 NVDA-validation row
exercised only the bell-ping path (single play); PR #157's
new diagnostic plays three earcons in sequence and exposed
the regression.

Fix: drop the singleton; construct a fresh `WasapiOut` per
play. `MMDeviceEnumerator` is still cached (cheap to share).
Each play registers a `PlaybackStopped` handler that disposes
the `WasapiOut` when the bounded sample provider exhausts;
init/play exceptions trigger explicit disposal too. The
construct-per-play overhead is acceptable for our use case
(BEL + diagnostic + future colour detection trigger plays well
under once-per-150ms).

`src/Terminal.Audio/EarconPlayer.fs` — re-architected:
- Removed `wasapiOut: WasapiOut option` cached field
- Added `MMDeviceEnumerator`-only caching via `ensureEnumerator`
- `play` now creates `new WasapiOut(...)` each call
- `PlaybackStopped` event handler disposes the instance
- Inner try/with disposes on init/play failure + rethrows

### Reverted (Stage 8d.2 — colour detection + ErrorLine/WarningLine earcons)

The maintainer cut a release build with PR #156 (Stage 8d.2)
merged and reported that NVDA stopped reading streaming output
entirely after install. Exact cause is not yet known — most
likely candidates are an exception path in
`StreamProfile.snapshotDominantColor` that crashes the
TranslatorPump task, or an interaction between the new
`Extensions["dominantColor"]` stamping and the post-8c
FileLogger / NvdaChannel dispatch. The CI test suite passes,
so the failure is something only surfaced in the live
release-build pipeline.

8d.2's commit is reverted to restore the user-visible
post-8d.1 behaviour (NVDA reads streaming output; bell-ping
plays on BEL; Ctrl+Shift+M mute hotkey works). The 8d.2 work
will land again in a fresh PR after the regression's root
cause is diagnosed, ideally with the new Ctrl+Shift+D
diagnostic (below) used to verify each step of the earcon
audio path independently.

### Changed (Ctrl+Shift+D diagnostic — earcon audio test)

The diagnostic ritual triggered by `Ctrl+Shift+D` is
restructured for the 8d sub-stage cycle:

- **PowerShell-script launch commented out.** The original
  ritual (`scripts/test-process-cleanup.ps1` running in a
  separate PowerShell window) had been consistently passing
  in NVDA validation but required closing pty-speak to
  complete its close-and-recheck flow. The commented block in
  `Program.fs runDiagnostic` preserves the code for future
  reactivation; the PS1 file still ships in the installer.
- **Earcon audio test added.** Pressing `Ctrl+Shift+D` now
  announces the shell-process snapshot (unchanged) followed
  by an earcon test that plays each earcon in sequence with
  an announce between:
  ```
  Diagnostic snapshot: 1 cmd, 0 powershell, 0 pwsh, 1 claude,
                       1 Terminal.App. Testing earcons.
  Bell ping. [bell-ping plays]
  Error tone. [error-tone plays]
  Warning tone. [warning-tone plays]
  Earcon test complete.
  ```
  The earcons play via direct `EarconPlayer.play` (bypassing
  the `EarconChannel` mute state) so the test verifies the
  WASAPI audio output path even when the user has earcons
  muted via `Ctrl+Shift+M` for normal use.

### Added (palette: error-tone + warning-tone)

`src/Terminal.Audio/EarconPalette.fs`'s `defaultPalette` gains
two new entries (forward-ported from the reverted 8d.2):

- `error-tone` (400Hz × 150ms × 10ms attack)
- `warning-tone` (600Hz × 120ms × 10ms attack)

No producer is wired to these IDs in this PR — the StreamProfile
colour-detection that would have triggered them lives in the
reverted 8d.2 commit and will return in a future PR. The
palette entries are kept so the Ctrl+Shift+D diagnostic can
exercise the full three-tone earcon path without depending on
a coloured-shell-output trigger.

### Added (Stage 8d.1 — WASAPI Earcons channel + Earcon profile + Bell)

The fourth sub-stage of the Output framework cycle. Adds WASAPI
audio playback infrastructure + a first-class Earcon channel +
an Earcon profile that maps `OutputEvent.Semantic` to earcon
sounds. v1 (8d.1) ships the substrate + the BEL → "bell-ping"
mapping; 8d.2 will add color-detection + ErrorLine /
WarningLine earcons (the full spec C.1 NVDA-validation row
mentions "Run colour-emitting commands; hear earcons"; that's
8d.2's payload).

**`Ctrl+Shift+M` mute hotkey** lands as part of 8d.1, claiming
the long-standing reservation in `CLAUDE.md`'s "Reserved (not
yet bound)" list. Each press flips the process-wide mute state
and announces "Earcons muted." / "Earcons unmuted." via NVDA
through `ActivityIds.logToggle` (same family as Ctrl+Shift+G's
debug-log toggle).

#### NAudio dependency

- `Directory.Packages.props` already pinned `NAudio` 2.2.1 — the
  umbrella package pulls NAudio.Wasapi (WasapiOut +
  MMDeviceEnumerator) and NAudio.Core (SignalGenerator +
  ISampleProvider) transitively. 8d.1 adds the
  `<PackageReference Include="NAudio" />` to
  `src/Terminal.Audio/Terminal.Audio.fsproj`.

#### Audio infrastructure (`src/Terminal.Audio/`)

The 8a placeholder shell is replaced with three new files:

- **`EarconWaveform.fs`** — pure-F# sine + envelope synthesis.
  `synthSineEnvelope` returns an `ISampleProvider` chain
  (`SignalGenerator → OffsetSampleProvider →
  FadeInOutSampleProvider`) that NAudio's `WasapiOut` consumes.
  v1 applies fade-in only (NAudio's `BeginFadeOut` triggers
  immediately rather than scheduled); a small click at the end
  of a 100ms tone is acceptable for v1; a future PR can add a
  custom per-sample-envelope `ISampleProvider` if needed.
- **`EarconPalette.fs`** — `EarconId = string` +
  `Map<EarconId, EarconWaveform.Parameters>`. Default palette
  ships `"bell-ping"` only (800Hz × 100ms × 10ms attack). 8d.2
  adds `"error-tone"` + `"warning-tone"`; Phase 2 makes the
  palette user-customisable via TOML.
- **`EarconPlayer.fs`** — WASAPI playback glue. Lazy-init
  singleton `WasapiOut` + thread-safe init under a lock. The
  `play` function is non-blocking: it stops any in-flight
  earcon, builds a fresh sample-provider chain via
  `EarconWaveform.synthSineEnvelope`, calls `WasapiOut.Init` +
  `Play`, returns. NAudio's audio thread feeds samples on its
  own schedule. **Errors are swallowed + logged at Information
  level** — earcon failures (no audio device, headless CI,
  driver permission errors) become "no sound" rather than
  crashing the dispatcher.

#### Channel + profile (`src/Terminal.Core/`)

- **`EarconChannel.fs` (new file).** Mirrors NvdaChannel /
  FileLoggerChannel: `[<Literal>] id: ChannelId = "earcon"` +
  `create (play: string -> unit) : Channel`. Send pattern-
  matches on RenderInstruction; only `RenderEarcon earconId`
  invokes `play` — the other cases (RenderText / RenderText2 /
  RenderRaw) are skipped because they target NVDA / FileLogger,
  not earcons. Process-wide mute state via `toggle ()` /
  `isMuted ()` / `clearForTests ()` (single-thread-init pattern
  matching the registries).
- **`EarconProfile.fs` (new file).** Mirrors StreamProfile:
  `[<Literal>] id: ProfileId = "earcon"` + `create () : Profile`.
  Apply maps `BellRang → RenderEarcon "bell-ping"`; all other
  Semantic categories return `[||]` (the profile is an
  additive observer, not a router for everything). Tick + Reset
  are no-ops in 8d.1.

#### BEL producer (`src/Terminal.Core/`)

- **`Types.fs`** — extends `ScreenNotification` DU with `| Bell`
  case (no payload — pure signal).
- **`Screen.fs`** — adds `bellEvent: Event<unit>` +
  `pendingBell: bool` mutable + `[<CLIEvent>] member _.Bell`
  property. `executeC0 0x07uy` sets `pendingBell`; Apply
  drains the flag after lock release and triggers `bellEvent`,
  same buffered-then-fire pattern as ModeChanged.
- **`OutputEventBuilder.fs`** — extends `fromScreenNotification`
  to map `Bell → SemanticCategory.BellRang`, `Priority =
  Assertive`, `Payload = ""`.
- **`src/Terminal.App/Program.fs`** — bridges `screen.Bell.Add`
  into the notification channel via
  `notificationChannel.Writer.TryWrite(ScreenNotification.Bell)`,
  same pattern as the existing `screen.ModeChanged.Add` bridge.

#### Mute hotkey (Ctrl+Shift+M)

- **`HotkeyRegistry.fs`** — extends `AppCommand` DU with
  `| MuteEarcons` case + matching `nameOf`, `builtIns`, and
  `allCommands` updates.
- **`Program.fs`** — `runMuteEarcons` handler calls
  `EarconChannel.toggle ()` and announces the new state via
  `ActivityIds.logToggle`. `bindHotkey` wires it.
- **`Views/TerminalView.cs`** — extends `AppReservedHotkeys`
  table with the `(Key.M, Ctrl|Shift, "Mute earcons")` entry;
  removes the matching commented-out reservation.

#### Composition root (`Program.fs`)

The active profile set grows from `[ streamProfile ]` (post-8c)
to `[ streamProfile; earconProfile ]`. For BellRang events:
StreamProfile's catch-all branch produces NVDA + FileLogger
decisions for the empty payload (NvdaChannel skips empty;
FileLogger logs the event). EarconProfile produces the earcon
decision. Total: bell ping plays + log entry; NVDA stays silent
(no double-up because empty payload).

### Tests (Stage 8d.1)

- **`tests/Tests.Unit/EarconChannelTests.fs` (new).** 13 tests:
  identity (`id = "earcon"`); RenderEarcon dispatch + multiple
  ids; RenderText / RenderText2 / RenderRaw skip; mute toggle
  state machine (initial false → toggle → true → toggle →
  false); RenderEarcon skipped when muted; play resumes after
  un-mute; clearForTests resets state.
- **`tests/Tests.Unit/EarconProfileTests.fs` (new).** 14 tests:
  identity; BellRang Apply emits one pair / one decision /
  targets EarconChannel / RenderEarcon "bell-ping" /
  effectiveEvent is the input event; empty pair array for
  StreamChunk / ParserError / ModeBarrier / AltScreenEntered /
  ErrorLine (8d.2 anchor) / Custom; Tick + Reset.
- **`tests/Tests.Unit/OutputEventTests.fs`** — 2 new tests for
  the `Bell → BellRang + Assertive` mapping.
- **`tests/Tests.Unit/HotkeyRegistryTests.fs`** — adds
  `MuteEarcons` to the `expected` Set in
  `allCommands contains exactly the documented commands`.

### Behaviour preservation (the regression bar)

All ~158 pre-existing load-bearing tests stay green. Earcon
infrastructure is purely additive: StreamProfile is unchanged;
NvdaChannel + FileLoggerChannel are unchanged; OutputDispatcher
is unchanged. The only behavioural change is the new BEL
audible cue (which Stage 7 silently swallowed; 8d.1 surfaces
it).

NVDA-validation row (manual; maintainer): "Trigger a bell
(`printf '\\a'` in cmd, or `tput bel`); hear bell ping; press
Ctrl+Shift+M; verify mute toggles + announces; verify ping
doesn't play when muted."

### Open question carried forward

Same as 8a/8b/8c: spec D.2 ParserError → Background
suppression. The Earcon profile + FileLogger channel substrate
now in place is the prerequisite for the future suppression PR
(parser errors go to FileLogger only, NVDA stays silent on them).

### Added (Stage 8c — FileLogger as first-class channel)

The third sub-stage of the Output framework cycle. Promotes
`Terminal.Core.FileLogger` to a first-class `Channel` so the
rolling log captures every `OutputEvent` the Stream profile
emits, structurally. The `Ctrl+Shift+;` clipboard-copy flow
(PR-F) now carries the full event trail for post-hoc diagnosis
— each entry includes Semantic / Priority / Verbosity / Producer
/ Shell / PayloadLen / Payload as structured-template fields.

Behaviour-identical to the post-8b release build for the
user-perceived NVDA reading: the Stream profile's emit decisions
now route to BOTH NvdaChannel and the new FileLoggerChannel, but
NVDA's behaviour (reading + activity-ID mapping + empty-payload
skip) is unchanged. The new addition is purely diagnostic
infrastructure.

**`src/Terminal.Core/FileLoggerChannel.fs` (new file).** Module
mirrors the NvdaChannel shape: `[<Literal>] id: ChannelId =
"filelogger"` + `create (logger: ILogger) : Channel`. The
channel's Send extracts a payload string from the
RenderInstruction (`RenderText` → text; `RenderText2` → Precise
register; `RenderEarcon` → `[earcon=<id>]` placeholder;
`RenderRaw` → `[raw payload]` placeholder) and writes a
structured `LogInformation` call with the OutputEvent's
metadata. **Empty-payload contract — CONTRARY to NvdaChannel:**
mode barriers + future Background-suppressed events land in the
log even if NVDA didn't read them.

**`src/Terminal.Core/StreamProfile.fs` updates.** Added a
`fileLoggerTextDecision` helper alongside `nvdaTextDecision` and
a `textDecisions` helper that returns
`[| nvdaTextDecision text; fileLoggerTextDecision text |]`. The
`coalescedToPair` helper + the inline Apply branches (ParserError,
catch-all) + the Tick closure all now use `textDecisions` instead
of single-decision `[| nvdaTextDecision ... |]` arrays. The
multi-pair `(OutputEvent * ChannelDecision[])[]` shape is
preserved; only the inner decision-array length changes from 1
to 2.

**`src/Terminal.App/Program.fs` composition root.** Three-line
addition after the NvdaChannel registration: create + register
the FileLoggerChannel passing
`Logger.get "Terminal.Core.FileLoggerChannel"` (which routes
through the configured factory at line 788 to the production
`FileLoggerSink`).

**`src/Terminal.Core/Terminal.Core.fsproj`** — new
`<Compile Include>` entry for `FileLoggerChannel.fs`, ordered
between `NvdaChannel.fs` and `StreamProfile.fs` (StreamProfile
references both channel IDs).

### Tests (Stage 8c)

- **`tests/Tests.Unit/FileLoggerChannelTests.fs` (new).** 14
  tests pinning the channel's structured-log behaviour. Uses a
  `RecordingLogger` ILogger fake that captures every Log call's
  formatted message string. Tests cover: identity (`id =
  "filelogger"`); RenderText / RenderText2 / RenderEarcon /
  RenderRaw payload extraction; the empty-payload-still-logged
  contract; ParserError events log the wrapped error message
  (anchor for the future Background-suppression PR);
  structured-template field substitution (Semantic / Priority /
  Verbosity / Producer / Shell); Source.Shell None vs Some "cmd"
  rendering; LogLevel.Information.
- **`tests/Tests.Unit/Tests.Unit.fsproj`** — new
  `<Compile Include>` entry for `FileLoggerChannelTests.fs`,
  ordered between `NvdaChannelTests.fs` and
  `OutputDispatcherTests.fs`.

### Behaviour preservation (the regression bar)

All ~144 pre-existing load-bearing tests stay green:

- 30 algorithm tests in `StreamProfileTests.fs` (incl. the 4
  PR-M #145 cross-row spinner gate pins) — untouched
- 14 tests in `NvdaChannelTests.fs` — untouched
- 17 tests in `OutputDispatcherTests.fs` — untouched (the
  synthetic `passthroughProfile` helper produces 1 decision per
  event regardless of the StreamProfile's 8c shape change)
- 17 tests in `OutputEventTests.fs` — untouched
- 15 tests in `FileLoggerTests.fs` (existing FileLogger sink
  tests) — untouched (the 8c channel uses the existing sink via
  the standard ILogger pipeline; no contract changes)
- 10 tests in `AnnounceSanitiserTests.fs` — untouched
- 12 tests in `ScreenTests.fs` — untouched
- 4 tests in `UpdateMessagesTests.fs` — untouched

NVDA-validation row (manual; maintainer): "Reproduce a session;
verify log captures match announcements." Type a few commands
in cmd, trigger an alt-screen toggle, `Ctrl+Shift+;` to copy the
log, verify the log contains `OutputEvent. Semantic=...` lines
for every announcement NVDA spoke. Verify NVDA reading is
unchanged from the post-8b release build.

### Open question carried forward

Spec D.2 maps `ParserError → Background`, where Background is
"suppressed at profile layer" per spec B.5.2. After 8c the
FileLogger channel receives every ParserError event regardless
of whether NVDA reads it; this enables the future suppression
PR's diagnostic story ("NVDA stayed silent; the parser error is
in the log via `Ctrl+Shift+;`"). Reconciliation deferred to a
focused follow-up PR.

### Changed (Stage 8b — Coalescer absorbs as the Stream profile)

The Stage-7 `Coalescer.runLoop` orchestrator and its module-level
constants (debounceWindow / spinnerWindow / spinnerThreshold)
move into the Stream profile, completing what the PR-N
substrate-cleanup contract anticipated. The Coalescer's
algorithms and `State` record are unchanged — they're now
hosted in `src/Terminal.Core/StreamProfile.fs` rather than
`Coalescer.fs`. The pipeline thread model is rewritten: the
two-channel `notificationChannel + Coalescer.runLoop +
coalescedChannel + drain` chain collapses to a one-channel
`notificationChannel + TranslatorPump + OutputDispatcher.dispatch`
+ a concurrent `TickPump` that calls
`OutputDispatcher.dispatchTick(now)` every 50ms.

Behaviour is identical to the post-8a release build: same
debounce / spinner-suppress / mode-barrier / parser-error /
500-char-cap defaults, same NVDA reading. The 8b refactor moves
constants to per-instance `StreamProfile.Parameters` fields
without changing their values; the next sub-stage (8b.2) adds a
TOML loader that overrides them per the `[profile.stream]`
section.

**Substrate API extensions** (deliberate, called out for spec
follow-up):

- **`Profile.Tick: DateTimeOffset → (OutputEvent *
  ChannelDecision[])[]`** — new field for time-driven flush.
  Profiles that don't accumulate (Selection, Earcon, the future
  Form / TUI / REPL profiles) supply
  `Tick = fun _ -> [||]` — zero-cost no-op. The Stream profile
  uses Tick to release pending stream content when the debounce
  window elapses with no new event arriving (the Stage-7
  trailing-edge flush).
- **`Profile.Apply: OutputEvent → (OutputEvent *
  ChannelDecision[])[]`** — return type changed from
  `ChannelDecision[]` to a multi-pair array. Each pair is
  `(effectiveEvent, decisionsForThatEvent)`: the effectiveEvent
  is what the channel's `Send` receives. Most profiles return a
  single pair; the Stream profile may return two pairs when an
  incoming mode-change forces a flush (one pair for the flushed
  pending stream content, one for the mode barrier itself, each
  with its own Semantic so NvdaChannel routes via the right
  ActivityId).
- **`OutputDispatcher.dispatchTick(now)`** — new dispatcher
  entry point. The composition root's TickPump task runs a
  `PeriodicTimer(50ms)` and calls dispatchTick on each tick.

The spec (`spec/event-and-output-framework.md` Part B.3) is
silent on time-driven flush + multi-pair Apply; 8b adds these
as substrate extensions. A focused doc-only PR will update the
spec after maintainer approval.

**Files modified:**

- `src/Terminal.Core/OutputEventTypes.fs` — Profile record gains
  `Tick` field; Apply return type changes to multi-pair shape.
- `src/Terminal.Core/StreamProfile.fs` — full rewrite. Adds
  `Parameters` record, `defaultParameters`, internal `State`
  record + algorithm functions (`processRowsChanged`,
  `onTimerTick`, `onModeChanged`, `onParserError`,
  `createState`), and `create` that captures parameters +
  screen + state in the Profile's Apply / Tick / Reset
  closures. The 500-char announce cap (PR-H, GitHub issue
  #139) lifts from the Stage-7 drain into the Stream profile's
  `capAnnounce` helper, parameterised by
  `Parameters.MaxAnnounceChars`.
- `src/Terminal.Core/Coalescer.fs` — gutted to ~140 lines. Keeps
  the `CoalescedNotification` DU (algorithm intermediate type)
  and the five pure helpers (`hashRowContent`, `hashRow`,
  `hashAttrs`, `hashFrame`, `renderRows`). Re-anchors the PR-N
  substrate-cleanup contract pointing at `StreamProfile.Parameters`.
- `src/Terminal.Core/OutputDispatcher.fs` — adds `dispatchTick`
  (mirrors `dispatch` for the Tick path); `dispatch` updated
  for the new Apply pair shape.
- `src/Terminal.Core/OutputEventBuilder.fs` — rewritten.
  Translates raw `ScreenNotification → OutputEvent` (the 8a
  `fromCoalescedNotification` is removed; the new
  `fromScreenNotification` runs BEFORE the Stream profile's
  Apply rather than AFTER coalescing). Producer ID changes from
  `"drain"` to `"translator"` to match the new pipeline location.
- `src/Terminal.App/Program.fs` — pipeline composition rewritten.
  `coalescedChannel` removed. `Coalescer.runLoop` call removed.
  Stage-8a drain task replaced by **TranslatorPump** (reads
  ScreenNotification, calls `OutputEventBuilder.fromScreenNotification`,
  calls `OutputDispatcher.dispatch`) and **TickPump** (50ms
  PeriodicTimer, calls `OutputDispatcher.dispatchTick`). Both
  pumps share the existing `cts.Token` for cancellation. The
  diagnostic Debug log line + post-Stage-6 drain-crash safety
  net are preserved (in adjusted form).

**Tests changed:**

- `tests/Tests.Unit/CoalescerTests.fs` → `StreamProfileTests.fs`.
  Module rename + mechanical rename of `Coalescer.X state` to
  `StreamProfile.X StreamProfile.defaultParameters state` for
  the four algorithm functions + `Coalescer.createState ()` to
  `StreamProfile.createState ()`. The 30 algorithm tests (incl.
  the 4 PR-M #145 cross-row spinner gate pins on `:198`,
  `:264`, `:316`, `:345`) preserve their assertions verbatim;
  the algorithm logic is unchanged. Three `Coalescer.runLoop`
  end-to-end tests are removed — the orchestrator is now in
  Program.fs (composition root); composition-root logic isn't
  unit-tested in this codebase. References to
  `Coalescer.OutputBatch`, `Coalescer.ErrorPassthrough`,
  `Coalescer.ModeBarrier`, and the pure helpers stay (those
  types + helpers remain in `Coalescer.fs`).
- `tests/Tests.Unit/OutputEventTests.fs` — builder tests
  updated for the new `fromScreenNotification` shape.
  ScreenNotification inputs replace CoalescedNotification
  inputs. New test pins the producer ID change (`"drain"` →
  `"translator"`) and the sanitisation of ParserError messages
  before wrapping.
- `tests/Tests.Unit/OutputDispatcherTests.fs` — replaces
  StreamProfile.create() calls (which now require parameters +
  screen) with a synthetic `passthroughProfile` test helper.
  Adds new tests for `dispatchTick` (no-op when no profiles /
  empty Tick / routes Tick decisions / fans out across
  profiles) and for the multi-pair Apply path.
- `tests/Tests.Unit/Tests.Unit.fsproj` — `<Compile Include>`
  entry renamed `CoalescerTests.fs → StreamProfileTests.fs`.

### Open question

Spec D.2 maps `ParserError → Background`, where Background is
"suppressed at profile layer" per B.5.2. The 8b Stream profile
does NOT suppress Background events (preserves the post-8a
release build's behaviour). Reconciliation deferred per the
8a CHANGELOG entry below; the maintainer can pick (A) suppress
in NVDA + log only, (B) reclassify ParserError as Assertive
in spec D.2, (C) add a fifth Diagnostic priority. Captured in
inline comments in `StreamProfile.fs` + `OutputEventBuilder.fs`.

### Added (Stage 8a — OutputEvent + Channel + NVDA-channel retrofit)

The first concrete implementation work after the post-Stage-7
substrate spec (PR #151) shipped — sub-stage 8a of the Output
framework cycle, per
[`spec/event-and-output-framework.md`](spec/event-and-output-framework.md)
Part C.1. The substrate types + dispatcher + NVDA channel land
behind the existing `ScreenNotification → Coalescer → drain →
Announce` pipeline; the user-visible NVDA reading is identical
to Stage 7. Subsequent sub-stages (8b absorbs the Coalescer as
the Stream profile, 8c promotes FileLogger to a channel, 8d
ships WASAPI Earcons, 8e the Selection profile, 8f per-shell
profile mapping) layer on top of this substrate without
re-doing the seam.

- **`src/Terminal.Core/OutputEventTypes.fs` (new file).** v1
  schema: `SemanticCategory` (14 closed cases + `Custom of string`
  escape hatch), `Priority` (Interrupt / Assertive / Polite /
  Background), `VerbosityRegister` (Approximate / Precise),
  `SourceIdentity`, `SpatialHint` / `RegionHint` /
  `StructuralRef` (reserved for v3 channels), `RenderInstruction`
  (RenderText / RenderText2 / RenderEarcon / RenderRaw),
  `ChannelDecision`, `OutputEvent` (with `Version: int` and
  `Extensions: Map<string, obj>` for forward-compat per spec
  B.2.3), `Channel`, `Profile`. The `OutputEvent.create`
  companion-module function pre-fills the v1 defaults via the
  `CompilationRepresentationFlags.ModuleSuffix` pattern F# Core
  uses for `Option` / `List` / `Map`.
- **`src/Terminal.Core/OutputEventBuilder.fs` (new file).**
  Translates `Coalescer.CoalescedNotification` to `OutputEvent`
  per spec D.2: `OutputBatch → StreamChunk + Polite`,
  `ErrorPassthrough → ParserError + Background` (with the
  Stage 7 wrapping `"Terminal parser error: %s"` preserved
  verbatim), `ModeBarrier(AltScreen, true) → AltScreenEntered
  + Assertive`, `ModeBarrier(AltScreen, false) → ModeBarrier +
  Assertive`, `ModeBarrier(other, _) → ModeBarrier + Polite`.
  The builder relies on the existing
  `AnnounceSanitiser.sanitise` chokepoint (PR-N): every Payload
  reaching the framework is already-sanitised by the upstream
  `Coalescer.renderRows` / `Coalescer.onParserError` callers.
- **`src/Terminal.Core/NvdaChannel.fs` (new file).** Channel
  implementation that takes a marshalling callback `(string *
  string) → unit` (the `(message, activityId)` pair the WPF
  dispatcher hop binds to `TerminalView.Announce`). Maps
  `SemanticCategory` to the `Types.fs:275-333` activity-ID
  vocabulary: `StreamChunk → output`, `ParserError /
  ErrorLine / WarningLine → error`, `AltScreenEntered /
  ModeBarrier → mode`, others pre-claim to `output` so an
  early producer landing before its NVDA-validation row still
  announces on the streaming channel. Empty-payload skip
  preserves the Stage-7 drain's `if msg <> "" then …`
  contract; `RenderEarcon` and `RenderRaw` skip on this
  channel (their consumers ship in 8d / 8e). **8a does NOT
  consult `OutputEvent.Priority`** — the channel calls the
  Stage-7 2-arg `Announce(msg, activityId)` overload, which
  picks `ImportantAll` for streaming output and `MostRecent`
  for everything else; a future sub-stage migrates to the
  3-arg overload + reads Priority.
- **`src/Terminal.Core/StreamProfile.fs` (new file).**
  Pass-through Stream profile in 8a: every OutputEvent
  produces exactly one `ChannelDecision` targeting the NVDA
  channel with a `RenderText` of the event's Payload. No
  debounce, no spinner-suppress, no max-announce-chars cap —
  those still live in `Coalescer.runLoop` + the Program.fs
  drain caller. 8b absorbs the Coalescer's per-instance state
  + parameters into this module per the PR-N docstring
  contract in `Coalescer.fs:82-114`.
- **`src/Terminal.Core/OutputDispatcher.fs` (new file).**
  Dispatcher + ChannelRegistry + ProfileRegistry inline. Mirrors
  the canonical extensibility shape from `HotkeyRegistry.fs`
  (PR-O) and `ShellRegistry.fs` (PR-B): module-level mutable
  `Map<,>` + tiny lock around read-modify-write, reads via
  `Map.tryFind` on the immutable Map reference. Renamed from
  `Dispatcher` to avoid shadowing
  `System.Windows.Threading.Dispatcher` (Program.fs:54 takes
  it as a parameter type). Single-thread-init pattern: all
  registration happens at composition time on the WPF main
  thread before the drain task starts. Stage 9c (TOML config
  load) converts to load-once-and-freeze.
- **`src/Terminal.App/Program.fs` (modified).** The drain task
  (lines ~891–1000) is rewritten: instead of building the
  `(message, activityId)` tuple inline and calling
  `TerminalView.Announce` via WPF dispatcher, it constructs
  an `OutputEvent` via `OutputEventBuilder` and calls
  `OutputDispatcher.dispatch event`. The 500-char announce-cap
  stopgap (PR-H, GitHub issue #139) stays in the drain in 8a;
  8b moves it into `StreamProfile.Parameters.MaxAnnounceChars`.
  The diagnostic Debug log line (`Drain → Dispatch.
  Semantic={Semantic} ...`) preserves streaming-silence triage
  continuity (`Ctrl+Shift+;` clipboard-copy + grep). The
  composition root registers the NVDA channel + the Stream
  profile + sets the active profile set to `[ streamProfile ]`
  before the drain task starts. The marshal callback uses
  synchronous `Dispatcher.Invoke` so the drain blocks per
  Announce — same effective throughput as Stage 7's
  `let! _ = InvokeAsync(...).Task` await.
- **`src/Terminal.Core/Terminal.Core.fsproj` (modified).** New
  `<Compile Include>` entries (5) for the new files, ordered
  after `Coalescer.fs` and before `KeyEncoding.fs`.

### Tests (Stage 8a)

- **`tests/Tests.Unit/OutputEventTests.fs` (new).** 17 tests.
  Schema pins (`Version = 1`, `Extensions = Map.empty`,
  optional fields default `None`, `Verbosity = Precise`,
  `Source.Producer` wired through, `Source.Shell` / `Source.CorrelationId`
  default `None`, `Payload` preserved verbatim). Builder
  pins per spec D.2 (`OutputBatch → StreamChunk + Polite`,
  `ErrorPassthrough → ParserError + Background` with the
  Stage 7 wrapping preserved, `ModeBarrier(AltScreen, true) →
  AltScreenEntered + Assertive`, `ModeBarrier(AltScreen, false)
  → ModeBarrier + Assertive`, non-AltScreen `ModeBarrier →
  Polite`, mode barrier carries empty Payload, `drain` is the
  producer ID).
- **`tests/Tests.Unit/NvdaChannelTests.fs` (new).** 14 tests.
  `Semantic → ActivityId` mapping (StreamChunk → output;
  ParserError / ErrorLine / WarningLine → error;
  AltScreenEntered / ModeBarrier → mode; others → output as
  pre-claim default; `Custom _` → output). Empty-payload skip
  (RenderText empty / RenderText2 empty Precise both skip the
  marshal callback). RenderEarcon and RenderRaw skip on this
  channel. Channel ID identity (`NvdaChannel.id = "nvda"` and
  `create`'s returned Channel.Id matches).
- **`tests/Tests.Unit/OutputDispatcherTests.fs` (new).** 13
  tests. ChannelRegistry register / lookup / idempotency.
  ProfileRegistry register / lookup / setActiveProfileSet
  round-trip. StreamProfile pass-through behaviour (one
  ChannelDecision per event, RenderText carries Payload,
  ParserError passes through without suppression, Reset is a
  no-op). Dispatch end-to-end (StreamChunk → StreamProfile →
  recording NVDA test channel; unregistered channels silently
  drop; no active profile set is a no-op; multiple profiles
  fan out).
- **`tests/Tests.Unit/Tests.Unit.fsproj` (modified).** New
  `<Compile Include>` entries (3) for the new test files,
  ordered after `CoalescerTests.fs` and before
  `KeyEncodingTests.fs`.

### Behaviour preservation (the Stage 8a regression bar)

All 52 load-bearing pre-existing tests stay green:

- 26 Coalescer tests in `tests/Tests.Unit/CoalescerTests.fs`,
  including the 4 PR-M (#145, Issue #117) load-bearing pins on
  cross-row spinner gate + change-detection.
- 10 AnnounceSanitiser tests in
  `tests/Tests.Unit/AnnounceSanitiserTests.fs`.
- 12 Screen tests in `tests/Tests.Unit/ScreenTests.fs`,
  including the load-bearing `ModeChanged subscriber can call
  SnapshotRows without deadlock`.
- 4 UpdateMessages tests.

NVDA-validation row (manual; maintainer): "Type fast in cmd;
verify identical to pre-retrofit." Reading cadence, debounce,
spinner suppression, alt-screen barriers, parser-error
announcements all sound identical. The full Stage-5 streaming
validation matrix re-runs green.

### Open question (deferred from Stage 8a, surfaces at PR review or 8b)

Spec D.2 maps `ParserError → Background`, where Background is
"suppressed at profile layer; never emitted as UIA notification"
per spec B.5.2. Stage 8a's Stream profile is a pass-through and
does NOT honour Background-suppression (so behaviour is
identical to Stage 7 — parser errors continue to reach NVDA).
The Background contract activates in 8b/8c when profiles +
channels start consulting `Priority`. Reconciliation options
(maintainer picks at the relevant stage):

- **(A)** Spec D.2 is correct; the Stage-7 behaviour was wrong;
  parser errors should stop announcing via NVDA and route to
  FileLogger only.
- **(B)** Spec D.2 is wrong; ParserError should be Assertive;
  spec gets a touch-up.
- **(C)** Add a fifth `Diagnostic` priority case (FileLogger +
  secondary channels but never NVDA by default); spec gets the
  touch-up.

Stage 8a position: deferred — neither 8a's Stream profile nor
8a's NVDA channel reads Priority, so the spec D.2 mapping
appears in OutputEventBuilder but doesn't drive observable
behaviour.

### Documentation (Event-and-output framework spec)

The post-Stage-7 substrate spec ships as a single doc-only PR.
After the maintainer-authored prior-art seed
(`docs/research/MAY-4.md`, 2026-05-04) surfaced consolidated
questions across three architectural concerns and the maintainer
authorised proceeding without per-question input ("let's move
forward the best [we can] with what we have"), this change
authors the technical specification that converts the research
into commitable engineering decisions. The spec covers two of
MAY-4.md's three concerns — universal event routing (Concern 1)
and the output framework (Concern 2) — in one document since
they are deeply connected (Concern 1's dispatcher is Concern
2's emission substrate). Concern 3 (navigable streaming
response queue) gets its own spec when Stage 10 starts and the
framework substrate is in place.

- **`spec/event-and-output-framework.md` (new file).** ~1500
  lines. Authored as the substrate spec for the post-Stage-7
  framework cycles. Structure: preamble + status block;
  "What this spec is, what it isn't"; Anchors and
  cross-references; Design principles (the cross-cutting rubric:
  failure modes / discoverability / documentation / performance
  budget / backward compat / forward compat / alignment /
  literal-language / linguistic-design rubric); Part A —
  Universal event routing (Concern 1: architecture overview /
  RawInput envelope / Intent layer / Dispatcher / Handler
  registration paths / Kill switch / Forward-looking input
  sources); Part B — Output framework (Concern 2: architecture
  overview / OutputEvent schema / Profile abstraction / Channel
  surface / Threading + priority taxonomy / Profile detection /
  Verbosity registers); Part C — Sub-stage breakdown (8a-8f for
  the output cycle, 9a-9d for the input cycle, Stage 10 reframe);
  Part D — Retrofit specifics (how the existing pipeline becomes
  the Stream profile + ScreenNotification → OutputEvent mapping
  table + HotkeyRegistry → IntentRegistry rename plan + Coalescer
  ratification); Part E — Out of scope, verification, open
  questions, closing.
- **`spec/tech-plan.md` §8 / §9 / §10 — supersession / reframe
  headers added in-place.** §8 (Interactive list detection +
  UIA List provider) and §9 (Earcons via NAudio) get
  "**Superseded by `spec/event-and-output-framework.md`**"
  one-paragraph headers; original content preserved below as
  historical reference. §10 (Review mode + structured
  navigation) gets a "**Reframed as the first non-built-in
  framework consumer**" header pointing at the new spec for the
  substrate it builds on; original §10 content stays as the
  feature plan. The May-2026 plan + chat-2026-05-03 retroactive
  ADR + the maintainer's "let's move forward" authorisation are
  the ADR-style precedent for these supersession headers.
- **`docs/PROJECT-PLAN-2026-05.md` Part 3 + Part 4
  cross-references.** Both kickoff briefs gain a 2026-05-04
  "substrate spec shipped" callout pointing at the new spec.
  Part 3's research-phase / RFC framing is preserved as
  historical reference for how the cycle was originally scoped.
  Part 4 explicitly notes that the higher-layer semantic
  interpretation work (input paradigms, tokeniser, suggestion
  engine, echo correlation, NL backend) sits ON TOP OF the
  substrate the spec defines and remains separately-scoped
  follow-on work after the substrate sub-stages (9a-9d) ship.
- **`docs/SESSION-HANDOFF.md` "Where we left off" updated.**
  In-flight branch row reflects this PR. Next-stage row points
  at sub-stage 8a (OutputEvent + Channel + NVDA-channel
  retrofit) per the new spec's Part C — Sub-stage breakdown,
  with the eleven-sub-stage roadmap listed.
- **`docs/DOC-MAP.md` audience table — new row added for the
  new spec.** Slots between the existing
  `spec/overview.md + spec/tech-plan.md` row and the
  `docs/PROJECT-PLAN-YYYY-MM.md` row. The "I'm Claude Code,
  starting a new session" entry-point list adds a step for the
  new spec when the active stage is in the framework cycles.
  The "I'm planning the next cycle" list adds a step pointing
  at the new spec as the active source for sub-stage sequencing.

This is a doc-only change. CI runs `dotnet test` on
Windows-latest (no-op), the Markdown link checker (verifies
internal links resolve), and the workflow lint. The new spec
provides the substrate that sub-stages 8a-8f and 9a-9d each
implement in their own multi-PR mini-cycle with NVDA validation.
The maintainer reviews, approves what's committed (or asks for
revisions), and the next session picks up sub-stage 8a as the
first concrete implementation work.

### Documentation (README + docs reorganisation)

The README had grown to 443 lines after PR #149 added the
"Access, dignity, and full participation" section near the top
and the long-standing "The complexities of trying to work with
technology as a blind developer" personal narrative remained
near the bottom. Reading it as a fresh-contact document showed
two competing rhythms: a tight technical orientation and an
extended philosophical / personal narrative interleaved through
it. This change separates those rhythms cleanly so the README
can serve as a clean orientation document for Claude Code
sessions and human contributors, with the wider context one
hop away in dedicated docs.

- **`docs/PROJECT-CONTEXT.md` (new file).** Receives the moved
  philosophical + author-narrative content from the README.
  Three sections: a short author bio (Dr. Kyle Keane, current
  affiliation: School of Computer Science, University of
  Bristol; previously ~10 years at MIT; full bio at
  www.kylekeane.com); the long-standing "complexities of trying
  to work with technology as a blind developer" personal
  narrative (the iOS Claude Code workaround case study + the
  literal-language convention + the Anthropic-facing report
  request) moved verbatim from the README; the
  "Access, dignity, and full participation" WHO ICF values
  frame moved verbatim from the README. Framed as a
  human-reader companion to `CLAUDE.md`'s Claude-runtime layer.
- **`docs/INSTALL.md` (new file).** End-user install path —
  centralises what was previously scattered across the README's
  120-line `## Install` section, `scripts/install-latest-preview.ps1`,
  and `scripts/README.md`. Sections: SmartScreen status warning;
  download from Releases; install + first-launch + Ctrl+Shift+U
  update flow; PowerShell-helper alternative; what to do next
  pointers; build-from-source pointer to `docs/BUILD.md`.
- **`README.md` restructured.** 443 → 246 lines. New top-level
  heading sequence:
  - `## What pty-speak is and does` (folds the previous
    "Why this exists" + "What you can do with it (when shipped)"
    into one tighter section)
  - `## Who built this and why` (NEW one-paragraph pointer to
    `docs/PROJECT-CONTEXT.md`)
  - `## Get started` (NEW short section pointing at
    `docs/INSTALL.md` for the full install steps + at
    `docs/BUILD.md` for build-from-source)
  - `## Project layout` (existing, unchanged)
  - `## Quick links` (existing per-audience structure;
    "If you're orienting on the project" extended with
    PROJECT-CONTEXT.md and INSTALL.md at the top of the list)
  - `## System requirements` (existing, unchanged)
  - `## Contributing` (existing, unchanged)
  - `## License, attribution, and citation` (existing from
    PR #149, unchanged)
  Removed and moved to PROJECT-CONTEXT.md:
  `## Access, dignity, and full participation` (~30 lines) +
  `## The complexities of trying to work with technology as a
  blind developer` (~85 lines).
  Removed and moved to docs/INSTALL.md:
  `## Install` (~120 lines including the SmartScreen warning,
  the GA flow, and the full app-reserved-hotkeys catalog).
- **`docs/DOC-MAP.md` updated.** Three new rows added to the
  audience table: `docs/PROJECT-CONTEXT.md`, `docs/INSTALL.md`,
  and `docs/ACCESSIBILITY-INTERACTION-MODEL.md`. The last is a
  bonus orphan close — the file (a 1146-line maintainer-
  requested skeleton mapping the design space for blind-developer
  terminal interaction; technical depth, not philosophical) was
  added during post-Stage-6 work but never registered in
  DOC-MAP. README entry updated to note the slimmed scope
  (defers author bio + values frame to PROJECT-CONTEXT.md and
  end-user install steps to INSTALL.md).

The README's role as the orientation document Claude Code
sessions read at session start is preserved and tightened. The
philosophical content remains discoverable for human readers
following the README's "Who built this and why" pointer or the
"If you're orienting on the project" Quick Links list. Future
Claude Code sessions get a tighter orientation; the human
context they may want is one click away in PROJECT-CONTEXT.md.

### Added (License attribution + citation + values frame)

- **`LICENSE` expanded.** Copyright line now identifies the author
  in full: `Copyright (c) 2026 Dr. Kyle Keane, School of Computer
  Science, University of Bristol, United Kingdom,
  https://www.kylekeane.com`. The MIT legal terms are unchanged.
  A new "Acknowledgment request (NOT a condition of the license
  above)" section is appended, clearly demarcated from the legal
  text by a separator and explicit "not legally enforceable
  through this license" framing. Two non-binding courtesy
  requests: cite the project in research / products / services
  that build on it, and send a brief written acknowledgment when
  the author asks — to help demonstrate the impact and adoption
  of accessibility-first developer tooling to funders, employers,
  and the broader software community. The MIT license remains
  fully OSI-compliant; the courtesy requests live alongside it
  rather than modifying it.
- **`CITATION.cff` (new file)** at the repo root, in Citation
  File Format 1.2.0. GitHub auto-renders this as a "Cite this
  repository" button on the repository page, giving downstream
  users a one-click path to the canonical citation. Includes
  full author attribution (Dr. Kyle Keane, University of Bristol
  School of Computer Science, www.kylekeane.com), abstract, MIT
  license declaration, repository URL, and accessibility-focused
  keyword set.
- **`README.md` "Access, dignity, and full participation"** — new
  section near the top of the README articulating the values
  position behind the project: access to computers as a modern
  necessity of human dignity; the WHO ICF framing of disability
  as a property of the interaction between a person and their
  environment, not of the person alone; and the operational
  consequence — that the right object to repair is the
  environment (the developer-tool surface), not the person. The
  technical sections that follow ("Why this exists", "What you
  can do", etc.) are the operational expression of those values.
- **`README.md` "License, attribution, and citation"** —
  License section retitled and rewritten to reference the
  expanded `LICENSE` file, the new `CITATION.cff`, and to
  surface the two non-binding courtesy requests at README level
  alongside the legal terms. Direct-dependency license summary
  preserved verbatim.

### Documentation (Output framework cycle research seed)

- **`docs/research/MAY-4.md` added** (maintainer-authored,
  2026-05-04, ~430 lines). Prior-art seed for the post-Stage-7
  cycles. Three architectural concerns covered: (1) universal
  event routing — whether and how to route every event through
  one named dispatch path with pre/post stages, including
  forward-looking awareness of alternate input sources (HID, OSC,
  MIDI, serial) framed through an intent-mapping layer; (2)
  output framework — typed semantic stream + switchable
  verbosity profiles, with a channel-design surface that
  includes spatial-audio engines and multi-line refreshable
  braille displays as forward-compatibility stress tests for
  the OutputEvent metadata schema; (3) navigable streaming
  response queue — typed segment forest vs. enhanced
  review-cursor for orienting inside Claude Code's streamed
  responses. Plus a section on linguistic-design properties
  (accurate / equivalent / objective / essential / contextual /
  common / appropriate / consistent / unambiguous / clear /
  concise / understandable / apt / synchronous / controllable)
  and a Discovery / Navigation / Selection / On-demand
  interaction lifecycle, both drawn from the Diagram Center
  2014 framework. Cross-cutting considerations cover failure
  modes, discoverability, documentation, performance budget,
  backward + forward compatibility, alignment with existing
  conventions, and the literal-language constraint. Consolidated
  questions list at the bottom is the natural starting point
  for proposal-phase conversation. Not prescriptive; framed as
  research for the cycle's research phase, not the proposal
  itself.
- **Cross-references added** so the seed surfaces from every
  navigation anchor:
  - `docs/DOC-MAP.md` — new `docs/research/` row in the
    audience-table; `docs/research/MAY-4.md` added to the
    "I'm planning the next cycle" entry-point list.
  - `docs/SESSION-HANDOFF.md` "Where we left off" — "In-flight
    branch" cell updated to reflect PR-N / PR-O / PR-P merged;
    "Next stage" cell now anchors the research-phase reading
    list to MAY-4.md (Concern 2 framing) + STAGE-7-ISSUES.md
    (empirical anchors).
  - `docs/STAGE-7-ISSUES.md` status block — research-phase
    inputs section maps `[output-*]` taxonomy entries to
    Concern 2 in MAY-4.md, and `[review-mode]` / `[input-*]`
    entries to Concern 3 + the Input framework cycle Part 4.
  - `docs/PROJECT-PLAN-2026-05.md` Part 3 kickoff brief — new
    "Research-phase reading list" subsection. Note that the
    research-phase deliverable, originally framed as
    `OUTPUT-FRAMEWORK-PRIOR-ART.md` (Claude-authored prior art),
    shifts to a synthesis-and-proposal document since MAY-4.md
    now provides the prior-art coverage.
  - `README.md` "If you're orienting on the project" Quick links
    section — MAY-4.md added.

### Changed (Pre-framework-cycle PR-P)

- **WPF adapter round-trip pinned by unit-test fixtures.** The
  C# adapter `TerminalView.TranslateKey` (WPF `Key` →
  `KeyCode`) plus its companion `TranslateModifiers` had no
  test coverage; a silent regression in either (e.g. a
  cursor-key case dropped during a refactor) would NOT have
  been caught by any existing test and would silently break
  the future Input framework cycle's echo-correlation logic
  (which depends on the WPF Key → encoded-bytes round-trip
  being precise per `docs/STAGE-7-ISSUES.md` `[input-buffer]`
  scope).

  Pre-framework-cycle PR-P closes the test gap:

  - **`TerminalView.TranslateKey` + `TerminalView.TranslateModifiers`
    bumped from `private` to `internal`** so unit tests can
    call them directly. `<InternalsVisibleTo
    Include="PtySpeak.Tests.Unit" />` added to
    `src/Views/Views.csproj` (modern SDK-style replacement
    for an `AssemblyInfo.cs` attribute). Tests.Unit gains a
    `ProjectReference` to PtySpeak.Views; the existing
    `<UseWPF>true</UseWPF>` declaration on Tests.Unit
    (originally added for Terminal.Accessibility's
    word-boundary tests) covers the WPF reference set.
  - **15 new fixtures in `tests/Tests.Unit/KeyEncodingTests.fs`**
    (under "Pre-framework-cycle PR-P — WPF adapter
    round-trip fixtures") cover the full WPF `Key` set
    `TranslateKey` claims to handle:
    * Round-trip pins: cursor keys (Up/Down/Left/Right),
      editing keypad (Delete/Home/End/PageUp/PageDown/Insert),
      whitespace + control (Tab/Enter/Escape/Back), function
      keys F1-F12, all letters A-Z, all digits D0-D9, all
      numpad digits NumPad0-NumPad9, Space. Each fixture
      verifies `TranslateKey` produces a non-`Unhandled`
      `KeyCode` and `KeyEncoding.encodeOrNull` produces a
      non-empty byte sequence.
    * KeyCode-output pins: specific `Key.X →
      KeyCode.Y` mappings for cursor keys, editing keypad,
      whitespace, function keys, letter / digit / numpad
      `Char` cases (including the lowercase-folding
      contract), and `KeyCode.Unhandled` for representative
      OEM keys.
    * `TranslateModifiers` pins: WPF flag → `KeyModifiers`
      flag mappings for Ctrl / Shift / Alt and combinations,
      plus the Windows-key silent-drop contract (Win+letter
      is OS-shell territory; pty-speak doesn't forward it).
  - The fixtures invoke the static `TranslateKey` /
    `TranslateModifiers` methods directly. No WPF
    dispatcher is required (both are pure static functions);
    the test runs at the same speed as the existing
    F#-side `KeyEncoding.encode` fixtures.

  Net change: ~5 lines of `private` → `internal` + docstring
  updates in `TerminalView.cs`; ~10 lines of `<InternalsVisibleTo>`
  + `ProjectReference` config in the two .csproj/.fsproj files;
  ~190 lines of new test fixtures + helper. Zero behavioural
  change to production code; CI gates the round-trip pinning.

### Changed (Pre-framework-cycle PR-O)

- **Hotkey handling unified through `HotkeyRegistry`.** The
  Stage 7 substrate had hotkey-binding boilerplate scattered
  across 8 stand-alone `setupXyzKeybinding` functions in
  `Program.fs` plus a local `bind` helper inside
  `setupShellSwitchKeybindings` (PR-J extracted that one for the
  3 shell-switch hotkeys). Each function did the same WPF
  RoutedCommand + KeyBinding + CommandBinding triple-binding;
  ~65 lines of near-identical boilerplate. The fragmentation
  meant adding a new hotkey required touching 3 places (the
  `AppReservedHotkeys` table, a new `setupXyz` function, the
  call site in `compose ()`) and the framework cycles (Output
  Part 3, Input Part 4, Stage 10) would have continued
  accumulating fragmentation if not pre-factored.
- **New `Terminal.Core.HotkeyRegistry` module** mirrors the
  `Terminal.Pty.ShellRegistry` shape: discriminated-union
  `AppCommand` (11 cases — every shipped hotkey from PR-A
  through PR-K + the 3 shell-switch slots), `Hotkey` record
  with `Key` / `Modifiers` / `Description` fields,
  `builtIns: Hotkey list` as the canonical source of truth,
  `hotkeyOf` / `tryFind` / `nameOf` / `allCommands` lookups.
  Uses its own `HotkeyKey` and `Modifier` types to keep
  Terminal.Core WPF-free (mirrors `KeyEncoding`'s
  decoupling). Phase 2 user-settings will override
  individual `Hotkey` records via TOML; the dispatch path
  stays unchanged.
- **`bindHotkey` helper in `Program.fs`** replaces all 9
  call sites (8 setup* functions + 3 shell-switch invocations)
  with single-line `bindHotkey window AppCommand.X handler`
  calls. Two small translation functions at the
  Terminal.Core ↔ WPF boundary (`translateHotkeyKey`,
  `translateHotkeyModifiers`) convert `HotkeyKey` /
  `Modifier` to WPF `Key` / `ModifierKeys`.
- **`TerminalView.cs AppReservedHotkeys` table unchanged** —
  it stays the C# hot-path filter source consulted per
  keystroke by `OnPreviewKeyDown` (avoiding C#/F# interop
  cost per key event). The two surfaces are kept in sync by
  maintainer convention; the new docstring on
  `AppReservedHotkeys` documents the contract.
- **Pinning fixtures in `tests/Tests.Unit/HotkeyRegistryTests.fs`:**
  `allCommands contains exactly the documented commands`
  (ADR-discipline mirror of `ShellRegistryTests.builtIns
  contains exactly Cmd, Claude, and PowerShell` and
  `EnvBlockTests.allowedNames contains exactly the spec-7-2
  baseline`); `every AppCommand case has a builtIns entry`;
  `builtIns has no duplicate AppCommand entries`; `no two
  hotkeys share the same (key, modifiers) gesture`
  (collision detection); `tryFind round-trips every
  builtIns gesture`; specific binding pins for the documented
  Stage 7 / PR-J hotkey set (Ctrl+Shift+U → CheckForUpdates,
  Ctrl+Shift+; → CopyLatestLog, Ctrl+Shift+1/2/3 →
  cmd/PowerShell/Claude per PR-J reordering).

What changes:

- `src/Terminal.Core/HotkeyRegistry.fs` (new, ~213 lines):
  AppCommand DU + HotkeyKey / Modifier types + Hotkey record +
  builtIns + nameOf + hotkeyOf + tryFind + allCommands.
- `src/Terminal.Core/Terminal.Core.fsproj`: register
  HotkeyRegistry.fs after KeyEncoding.fs in compile order.
- `src/Terminal.App/Program.fs`:
  * New `translateHotkeyKey` + `translateHotkeyModifiers` +
    `bindHotkey` private helpers (~95 lines).
  * Deleted 8 `setupXyzKeybinding` functions (~125 lines).
  * Replaced 9 call sites with single-line `bindHotkey`
    invocations (~12 lines vs ~40 lines previously).
  * `setupShellSwitchKeybindings` outer function inlined
    (its local `bind` helper is now superseded by the
    module-level `bindHotkey`).
- `src/Views/TerminalView.cs`:
  * `AppReservedHotkeys` docstring updated to reference
    `HotkeyRegistry` as the F#-side canonical source and
    document the two-surface convention.
- `tests/Tests.Unit/HotkeyRegistryTests.fs` (new, ~150 lines):
  10 fixtures pinning the registry contracts.
- `tests/Tests.Unit/Tests.Unit.fsproj`: register
  HotkeyRegistryTests.fs in compile order.

Net change: ~+335 lines added, ~-178 deleted (~+157 net).
The substantial deletion of duplicated WPF triple-binding
boilerplate and the addition of testable registry surface
+ pinning fixtures is the trade. Adding a new hotkey now
requires 4 touches (extend AppCommand DU, update nameOf,
append builtIns row, append AppReservedHotkeys row) which the
pinning fixtures + F# exhaustiveness force in lockstep.

Verification: existing CI (`dotnet test` on Windows-latest)
runs the new HotkeyRegistryTests fixtures alongside the
existing 100+ fixtures. Manual NVDA pass on a fresh preview
build confirms every shipped hotkey still fires
(Ctrl+Shift+U/D/R/L/G/H/B/;/1/2/3 — 11 hotkeys to walk
through).

### Documentation (Pre-framework-cycle PR-N)

- **Substrate-invariant docstring contracts.** A pre-framework-
  cycle audit (plan at `/root/.claude/plans/hello-i-lost-my-velvet-deer.md`)
  identified four implicit substrate invariants the framework
  cycles (Output Part 3, Input Part 4, Stage 10) will need to
  respect. Made them explicit so framework implementers can't
  accidentally violate:
  - **`src/Terminal.Core/Coalescer.fs` — `debounceWindow` /
    `spinnerWindow` / `spinnerThreshold`** are now documented as
    Stream-profile defaults, not universal terminal-output
    tuning knobs. The Output framework's per-profile
    presentation strategies will construct their own
    `Coalescer.State` instances with caller-supplied thresholds
    rather than reading these module globals; the Coalescer
    today IS the Stream profile.
  - **`Coalescer.onModeChanged`** docstring now warns
    profile-detection logic MUST NOT assume screen content is
    stable across mode changes; profiles must wait for the
    first post-`ModeBarrier` `RowsChanged` to inspect content.
  - **`src/Terminal.Core/Types.fs ActivityIds` module** docstring
    now documents the pairing contract with `AnnounceSanitiser`:
    every UIA Notification MUST be sanitised through
    `AnnounceSanitiser.sanitise` first AND tagged with a stable
    `ActivityIds.*` value. Skipping either breaks PTY-control-
    byte verbalisation suppression or per-class NVDA verbosity
    configuration. The drain task in `Program.fs` enforces this
    for current channels; future channels at the same seam
    inherit enforcement.
  - **`src/Terminal.App/Program.fs switchToShell`** comment now
    tags the three known-caveats (screen state not reset, parser
    state not reset, UIA peer ranges not invalidated) as
    framework-territory: the Output framework will introduce an
    `OnShellSwitched` lifecycle signal as the right seam for
    profile state-reset; pre-framework drive-by fixes here
    would create precedent the framework either has to adopt or
    break.
- **`docs/SESSION-HANDOFF.md` "Where we left off" updated.**
  "In-flight branch" cell now reflects PR-M (#145) merged at
  `b09db6e` on 2026-05-04, and identifies the pre-framework-
  cycle substrate-cleanup bundle (PR-N / PR-O / PR-P) as the
  active work. "Last merged stages" cell extended to include
  PR-L (#144) and PR-M (#145) in the Stage 7 PR list.
- **`docs/USER-SETTINGS.md` `diagnostic.heartbeatIntervalMs`
  added.** New "Diagnostic surfaces" section catalogues the
  hardcoded 5-second heartbeat interval (`runHeartbeat` timer)
  as a Phase 2 candidate setting, with rationale (CPU + log
  file size for long sessions), three plausible
  configurability levels (TOML entry / env-var override /
  runtime hotkey cycle), and implementation notes.

### Fixed (PR-M, Issue #117)

- **Coalescer cross-row spinner detection redesigned + per-key
  static-row false-positive fixed.** The Stage 5 coalescer's
  per-`(rowIdx, hash)` spinner gate uses a row-index-folded
  hash, which means a spinner whose content moves between rows
  (scrolling progress bar, Ink reconciler reflow) produces a
  different `(rowIdx, hash)` key each frame and the gate sees
  count=1 for each — never fires. The original Stage 5 design
  included a generic "any-hash-anywhere" gate to catch this,
  but its count-of-total-entries threshold was incompatible
  with the per-row scan (every event added one entry per row,
  so a 30-row screen instantly exceeded the 20-entry threshold
  and silenced the channel permanently); it was removed in the
  post-#114 fix.

  Two bugs fixed:

  1. **Cross-row gate restored**, redesigned per Issue #117.
     New `HashHistory` dictionary tracks recurrences of the
     CONTENT-only hash (no rowIdx fold) regardless of which
     row it lands at. Within-frame dedup ensures a single
     frame with N rows of identical content (e.g. blank
     padding above a prompt) only contributes one Add per
     frame, not N — the gate counts "this content appeared
     in N FRAMES", not "N rows of one frame". Catches moving
     spinners that the per-key gate misses; doesn't false-
     positive on padded screens.

  2. **Per-key static-row false-positive fixed.** The pre-PR-M
     per-key gate `Add(now)`'d on every observation, so a
     screen with N static rows added N entries per event. At
     5+ events/sec (typing cadence), static-row keys
     accumulated to threshold within ~5 events and tripped
     suppression even when no spinner existed (the typing-
     cadence false-positive flagged in the issue's
     "Note on per-key gate interaction with static rows"
     section).

  Both gates now use **change-detection**: a per-row hash is
  only `Add(now)`'d if the row's hash CHANGED since the
  previous frame. Static rows contribute zero observations.
  New `LastRowHashes: uint64[] voption` state field tracks
  the comparison baseline; `onModeChanged` resets it to None
  alongside the existing state reset (`PerRowHistory.Clear()`
  + new `HashHistory.Clear()`).

  Refactor: split `hashRow` into `hashRow` (row-index-folded;
  used for frame-hash + per-key gate + change-detection key)
  and `hashRowContent` (no fold; used for cross-row gate).
  `hashRow` is now defined as `hashRowContent cells ^^^
  (uint64 rowIdx * rowSwapMix)` so the existing semantics are
  preserved bit-exactly; only the new code path uses the
  unfolded form.

  Pinned by 4 new `CoalescerTests` fixtures: cross-row gate
  accumulates content-hash recurrence as spinner moves;
  static rows do not trip per-key gate at fast typing
  cadence; cross-row HashHistory ignores static blank rows;
  ModeChanged resets LastRowHashes and HashHistory.

  NVDA validation: maintainer runs the standard "type fast
  in cmd" pass after merge; pre-PR-M behaviour was
  intermittent typed-character silencing that was hard to
  reproduce reliably; post-PR-M behaviour should be steady
  speech for every keystroke. The new cross-row gate's
  effects only show up under genuinely-spammy spinner-
  shaped output (Claude Code progress indicators, Ink
  reflows) where the user wants suppression; routine
  typing + cmd output is unaffected.

### Documentation (Stage 7 wrap-up PR-L)

- **`docs/DOC-MAP.md` (new).** Canonical "which doc owns what" index
  with audience + when-to-read + one-line purpose per file, plus
  per-audience entry-point lists ("I'm Claude Code starting a
  session", "I'm a human contributor", "I'm the maintainer cutting
  a release", "I'm orienting on the project"). Pre-designed in
  `docs/SESSION-HANDOFF.md` "Queued before Output framework cycle
  starts"; ships now to set clean structure before the framework
  cycles spawn their RFC + research files.
- **`CLAUDE.md` slimmed.** Duplicated material (F# 9 / .NET 9
  gotchas, accessibility non-negotiables, branching + PR shape,
  test conventions, logging discipline, app-reserved-hotkey
  contract, walking-skeleton + spec-immutability rules) collapsed
  into pointers + 2-3-line summaries that point at the canonical
  owner (`CONTRIBUTING.md` for most). The Claude-runtime layer
  (sandbox quirks, MCP behaviour, ask-for-CI-logs rule, no-GUI-
  walks-for-screen-reader-users rule, diagnostic recipes) stays
  canonical here. Net change: ~150 lines shorter, zero rules lost.
- **`CONTRIBUTING.md` "F# gotchas learned in practice" expanded.**
  Absorbs the four CLAUDE.md-unique entries: F# delegate
  conversion only fires for `Func` / `Action` (`Predicate<T>`
  doesn't auto-convert; bit PR #131); record literal type
  inference fails when the record's module isn't auto-opened
  (`FS0039` at the field name; bit PR #132); NUL bytes in F#
  source (use the explicit Unicode escape, not a raw NUL);
  sequence-in-match-arm (extract a named local function rather
  than relying on the offside rule under `TreatWarningsAsErrors`).
  Same shape as the existing entries; CONTRIBUTING is now the
  single F#-gotchas source of truth.
- **`README.md` "Quick links" reorganised by audience.** Four
  sections: human contributor / Claude Code / maintainer /
  orienting. Top of section points at `docs/DOC-MAP.md` as the
  full ownership table.
- **`docs/SESSION-HANDOFF.md` "Where we left off" reset.** Stage 7
  substrate marked shipped across all 11 sequenced PRs (A → K) +
  this wrap-up PR-L; NVDA validation confirmed green by maintainer
  on 2026-05-03; next active work is the Output framework cycle
  (Part 3) starting from its research phase. Open follow-up:
  Issue #117 (coalescer cross-row spinner detection) flagged as a
  separately-mergeable small fix.
- **`docs/STAGE-7-ISSUES.md` status block updated.** All 11 Stage 7
  PRs (A → K) listed with one-line summaries + cross-references
  to the empirical NVDA finding that drove each fix; status flipped
  to "NVDA validation green (2026-05-03)".
- **`docs/CHECKPOINTS.md` `baseline/stage-7-claude-roundtrip` row
  added** to both the "Current checkpoints" table and "Pending
  checkpoint tags" (anchor at PR #143 `001ec54`; maintainer pushes
  the tag from a workstation when convenient).

### Fixed (Stage 7-followup PR-K)

- **Env-scrub allow-list was too narrow — PowerShell + claude.exe
  could not start.** The 2026-05-03 NVDA validation pass on the
  PR-J build showed both PowerShell (PID 8440) and claude.exe
  (multiple PIDs) dying within ~3 seconds of spawn while cmd.exe
  survived. Diagnosis: the original 7-name allow-list (`PATH`,
  `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOME`,
  `ANTHROPIC_API_KEY`, `CLAUDE_CODE_GIT_BASH_PATH`) stripped
  Windows runtime vars that PowerShell's .NET initialisation and
  claude.exe's Node runtime both require: `SystemRoot`, `WINDIR`,
  `TEMP`, `TMP`, `ProgramFiles`, `PATHEXT`, `PSModulePath`, etc.
  cmd.exe survived because it reads most of what it needs
  straight from the registry, but every non-trivial Windows shell
  fails without these vars.

  Fix: expand `EnvBlock.allowedNames` to a two-layer set — layer
  1 (pty-speak-specific, unchanged) + layer 2 (Windows runtime
  baseline: 24 standard machine-identity / path vars that any
  unprivileged process can already read from the registry). The
  deny-list still applies on top, so sensitive `*_TOKEN` /
  `*_SECRET` / `*_KEY` / `*_PASSWORD` vars are still stripped.

- **Misleading env-scrub log line.** The previous
  `"Env-scrub: stripped {Count} variables before child spawn"`
  line read "stripped 0" in the 2026-05-03 NVDA pass log even
  though ~50 parent vars were being silently dropped — because
  the `StrippedCount` field only counted deny-list strikes and
  silently excluded vars dropped by the allow-list filter. Future
  regressions of the same shape would have been invisible.

  Fix: replaced with
  `"Env-scrub: kept K of M parent vars; dropped D as sensitive
  (deny-list)"`. New `EnvBlock.Built` fields `ParentCount` +
  `KeptCount` plumbed through to `ConPtyHost`. Counts only —
  names and values are still NEVER captured per `SECURITY.md`
  logging discipline.

What changes:

- `src/Terminal.Pty/Native.fs`:
  * `allowedNames` set extends with 24 Windows-baseline vars.
  * `Assembled` + `Built` records gain `ParentCount` +
    `KeptCount` fields.
  * `assemble` populates them from `Map.count parent` and
    `Map.count kept`-pre-fallback-pre-always-set.
- `src/Terminal.Pty/PseudoConsole.fs` + `ConPtyHost.fs`: plumb
  the two new fields through `PtySession` into the public
  `ConPtyHost` API as `EnvScrubParentCount` +
  `EnvScrubKeptCount`.
- `src/Terminal.App/Program.fs`: log line replaced; old
  rationale comment expanded with PR-K provenance.
- `tests/Tests.Unit/EnvBlockTests.fs`:
  * `allowedNames contains exactly the spec-7-2 baseline` test
    extended with all 24 layer-2 names; same ADR-discipline pin
    so a future tightening can't silently re-break PowerShell +
    Node-based shells.
  * New test pinning Windows-baseline preservation
    (`SystemRoot`, `WINDIR`, `TEMP`, `ProgramFiles`,
    `PSModulePath`, etc. all survive).
  * New tests pinning `ParentCount` / `KeptCount` semantics
    (full filter picture, exclusion of always-set + HOME
    fallback).
- `spec/tech-plan.md` §7.2: rewritten to describe the two-layer
  allow-list explicitly and document the new log-line format.
- `SECURITY.md` PO-5 row: updated mitigation description to
  enumerate the layer-2 names and reference PR-K.

### Added (Stage 7-followup PR-J)

- **PowerShell as third built-in shell + reordered hotkeys.**
  `ShellRegistry` gains `PowerShell` (Windows PowerShell,
  always available on Windows 10+) alongside `Cmd` and
  `Claude`. The hot-switch hotkeys reorder to put PowerShell
  next to cmd as the diagnostic control shell:
  - `Ctrl+Shift+1` — switch to cmd (unchanged)
  - `Ctrl+Shift+2` — switch to PowerShell (new; was claude in PR-C)
  - `Ctrl+Shift+3` — switch to Claude (was Ctrl+Shift+2 in PR-C)

  Rationale: the maintainer's NVDA validation pass on
  2026-05-03 surfaced suspected shell-switch infrastructure
  bugs that needed a diagnostic control shell to isolate.
  PowerShell has zero auth, no terminal-capability
  detection, and produces visible banner + prompt output
  within milliseconds — making it the ideal control. Putting
  it adjacent to cmd at slot 2 means the comparison "does
  switching from cmd to PowerShell work?" is one keystroke
  away. The `PTYSPEAK_SHELL` env var also recognises
  `powershell` and `pwsh` (as aliases routing to the same
  `PowerShell` ShellId).

- **`Ctrl+Shift+H` liveness probe.** The health-check
  announce now distinguishes "child running" from "child
  exited". Each press calls
  `Process.GetProcessById(currentShell.Pid)` (which throws
  `ArgumentException` for a reaped/non-existent PID) and
  announces "alive" / "dead" alongside the rest of the
  state snapshot. Verdict heuristic gains a top-priority
  "Child shell process N has exited." arm so a screen-
  reader user gets the dispositive "the shell crashed"
  signal without reading the log file. The 5s background
  heartbeat (`runHeartbeat`) gains the same `Alive=`
  field for log forensics — post-hoc grep for the moment
  `Alive=False` first appears pinpoints the wedge
  timestamp.

- **`Ctrl+Shift+D` inline shell-process snapshot.** The
  diagnostic launcher now announces a one-line process
  enumeration BEFORE the cleanup-test PowerShell window
  opens: "Diagnostic snapshot: 1 cmd, 0 powershell, 0
  pwsh, 1 claude, 1 Terminal.App. Launching cleanup test."
  Lets a screen-reader user (or a Claude session triaging
  on their behalf) get the "what's currently running?"
  answer in one keystroke, without losing their pty-speak
  session to the close-and-recheck flow. The full
  close-and-recheck flow still launches afterwards for
  orphan-detection use cases. The bundled
  `scripts/test-process-cleanup.ps1` gains a matching
  `Get-ShellProcessSnapshot` helper that prints the same
  vocabulary at script start + before/after each pass.

- **CLAUDE.md "Diagnostic recipes" section + no-GUI rule.**
  Adds explicit guidance for Claude sessions: `tasklist |
  findstr /I` and `Get-Process` are the canonical "is the
  child alive?" recipes; never propose GUI dialog walks
  (System Properties / Task Manager / etc.) to a screen-
  reader user. CLI / keyboard-only equivalents are listed
  for the common cases (`setx`, `set`, `taskkill`, `reg
  query`).

### Fixed (Stage 7-followup PR-I)

- **Spurious "parser/reader loop: channel has been closed"
  announce on shell hot-switch.** Empirically confirmed in
  NVDA pass 2026-05-03: every `Ctrl+Shift+1` / `Ctrl+Shift+2`
  press produced an unwanted `ActivityIds.error` announce
  ("Terminal parser error: Parser/reader loop: The channel
  has been closed.", 71 characters) firing in between the
  "Switching to {target}." and "Switched to {target}." cues.
  The switch itself succeeded — the new `ConPtyHost` spawned
  and took over correctly — but the spurious error
  announcement made the switch sound broken to the user.

  Root cause: when `switchToShell` disposes the old
  `ConPtyHost`, the host's internal stdout channel completes
  (`chan.Writer.TryComplete()` runs in `Dispose`). The reader
  loop's `host.Stdout.ReadAsync(ct)` then throws
  `ChannelClosedException` — which is NOT
  `OperationCanceledException` — so the catch-all `| ex ->`
  arm in `startReaderLoop` mis-classifies it as a real
  parser/reader fault and writes a `ParserError` to the
  SHARED notification channel. The drain task announces
  it via `ActivityIds.error`.

  Fix: add silent-shutdown arms for
  `System.Threading.Channels.ChannelClosedException`,
  `System.IO.IOException`, and `ObjectDisposedException` to
  the reader-loop catch chain. All three indicate intentional
  pipe/channel teardown (channel completed; pipe handle
  closed mid-read; FileStream/SafeFileHandle disposed
  mid-read). Other exception types still surface as parser
  errors.

  `docs/STAGE-7-ISSUES.md` records this as
  `[other]` tag, **Source: NVDA pass 2026-05-03; fixed in
  PR-I**.

- **Heartbeat + health-check reported stale shell name after
  hot-switch.** Same NVDA pass log showed
  `Heartbeat. Shell=Command Prompt Pid=2596` after the user
  had switched to claude.exe. Cosmetic for the user
  (`Ctrl+Shift+H` health-check verdict said "Command Prompt
  shell" instead of "Claude Code shell"); confusing for log
  analysis (the wrong shell name in heartbeats made
  post-mortem triage harder).

  Root cause: `chosenShell` was captured at startup and never
  updated. Heartbeat and health-check closures both read
  `chosenShell.DisplayName` directly.

  Fix: new `let mutable currentShell` field in `compose ()`;
  `switchToShell` updates it after a successful spawn; both
  heartbeat and health-check read from `currentShell`
  instead of `chosenShell`.

  `docs/STAGE-7-ISSUES.md` records this as a follow-on
  `[other]` entry from the same NVDA pass.

### Changed (Stage 7-followup PR-H)

- **500-character announce-length cap on streaming output.**
  The drain task in `src/Terminal.App/Program.fs` now truncates
  any `Coalescer.OutputBatch` announcement longer than 500
  characters and appends a footer:
  `"...announcement truncated; N more characters available — press Ctrl+Shift+; to copy full log."`
  Only `OutputBatch` is affected; `ErrorPassthrough`,
  `ModeBarrier`, and the diagnostic announcements (health
  check, incident marker, log-toggle, shell-switch) pass
  through unchanged.

  **Why a cap.** The 2026-05-03 NVDA pass empirically confirmed
  the `[output-stream]` verbose-readback prediction in
  `docs/STAGE-7-ISSUES.md` with concrete numbers: trivial cmd
  interaction (`dir`, `set`, single keystrokes) produced
  1316 / 1347-character announces taking 30-45 seconds each
  for NVDA to read, making the terminal effectively unusable.
  500 characters ≈ 15-20 seconds — tolerable upper bound that
  preserves enough content for the user to follow what's
  happening while leaving the speech queue responsive enough
  to keep up with new input.

  **Why this number.** **Arbitrary.** No empirical evidence
  yet that 500 is right vs 250 / 750 / 1000. Tracked in
  [issue #139](https://github.com/KyleKeane/pty-speak/issues/139)
  for tuning based on real-feel data from subsequent NVDA
  passes, AND for surfacing as a user-configurable setting
  (likely `audio.maxAnnounceChars` in Phase 2 TOML, or a
  hotkey-cycled preset). Catalog entry in
  `docs/USER-SETTINGS.md` "Verbosity / NVDA narration" →
  "Stopgap: announce-length cap".

  **Why this is a stopgap.** The proper architectural fix is
  the Output framework cycle's Stream profile (per
  `docs/PROJECT-PLAN-2026-05.md` Part 3.2 RFC) — suffix-diff
  append-only emission eliminates the verbose-readback at its
  source rather than capping its output. When Stream profile
  ships, this cap + footer become redundant and should be
  deleted. Issue #139 tracks that deletion at the ship
  boundary.

  Inline code comment in `Program.fs` documents the
  arbitrary-threshold disclaimer + the issue reference + the
  ship-boundary-delete contract so a future contributor
  doesn't preserve the stopgap by accident.

### Fixed (Stage 7-followup PR-G)

- **`Ctrl+Shift+;` dispatcher deadlock.** Empirically confirmed in NVDA pass 2026-05-03:
  pressing `Ctrl+Shift+;` (copy log to clipboard) while NVDA
  had a long readout queued permanently wedged the WPF
  dispatcher. The 5-second background heartbeat kept firing
  (proving the runtime alive), but no further dispatcher
  events processed — typing did nothing, Alt+F4 didn't close
  the window, no other app-reserved hotkey produced an
  announcement. Force-kill via Task Manager was the only way
  out.

  Root cause: `runCopyLatestLog` called
  `sink.FlushPending(500).Result` synchronously on the
  dispatcher. `FlushPending` is implemented as
  `task { let! winner = Task.WhenAny(...) }` — the `let!`
  captures the WPF dispatcher's `SynchronizationContext`. When
  the dispatcher thread blocks on `.Result`, the task's
  continuation can never resume because the dispatcher is
  blocked. The 500ms timeout never fires because the timeout's
  continuation also needs the dispatcher. Classic
  sync-over-async deadlock. The bug has been latent since
  PR #122 (Stage 5a's FlushPending introduction); only manifests
  under sufficient dispatcher / NVDA-queue contention to expose
  it, which the post-Stage-7-PR-D NVDA validation pass
  reliably triggered.

  Fix: run the entire `runCopyLatestLog` body in a `task` off
  the dispatcher. `FlushPending` is awaited normally (no
  `.Result`). The clipboard write itself runs on a dedicated
  STA thread (`System.Windows.Clipboard.SetText` requires the
  STA apartment, so we can't use the thread pool which is MTA)
  with a 3-second timeout to bound clipboard contention with
  NVDA's clipboard hooks / clipboard managers / antivirus
  hooks. The announcement dispatches back to the WPF thread on
  completion. The hotkey handler returns immediately; the
  dispatcher never blocks regardless of clipboard or flush
  state.

  `docs/STAGE-7-ISSUES.md` records both this bug
  (`[other]` tag, **Source: NVDA pass 2026-05-03; fixed in
  PR-G**) and the empirical confirmation of the
  `[output-stream]` verbose-readback prediction (concrete
  numbers from this pass: 1316 / 1347-character announces
  per cmd command, character-by-character announce growth on
  typed input — the architectural fix remains in scope for
  the Output framework cycle Part 3.2 RFC).

### Added

- **Stage 7-followup PR-F — diagnostic surface: `Ctrl+Shift+H`
  health check, `Ctrl+Shift+B` incident marker, background
  heartbeat.** Closes the in-app debug-capture workflow alongside
  PR-E's `Ctrl+Shift+G` toggle. The maintainer's two-hour
  env-var-debug ordeal is the trigger; the goal is to make
  "capture and send a diagnostic log" achievable in three
  keystrokes (G to enable Debug logging, B to mark the incident,
  ; to copy the log to clipboard) without any out-of-app
  manipulation.

  **`Ctrl+Shift+H` — health check.** Announces a one-line state
  snapshot via NVDA: verdict ("Pty-speak healthy." / "Reader
  appears wedged. Last byte N seconds ago." / "Notification
  queue near capacity (N of 256).") + shell name + PID + log
  level + reader last-byte staleness + notification-channel
  depth + coalesced-channel depth. Lets a screen-reader user
  determine in one keystroke whether pty-speak is functioning,
  instead of inferring from "is NVDA reading anything?". Verdict
  heuristic: reader-wedge if staleness > 5000ms AND notification
  queue non-empty; queue-near-capacity if notification queue at
  ≥244/256 (DropOldest mode means past-capacity is silent data
  loss); otherwise healthy. Wired in
  `setupHealthCheckKeybinding` with the closure
  `runHealthCheck` constructed inside `compose ()` (captures
  compose-local state — `lastReadUtc`, `hostHandle`, channels,
  `chosenShell`).

  **`Ctrl+Shift+B` — incident marker.** Writes a clear
  `=== INCIDENT MARKER {timestamp} === (Ctrl+Shift+B)`
  boundary line into the active log at `Information` level via
  the standard `ILogger` path, then announces via NVDA:
  "Incident marker logged. Reproduce your issue, then press
  Ctrl+Shift+; to copy the log." The user reproduces the issue,
  then copies the full log via `Ctrl+Shift+;`; server-side grep
  for the marker extracts the relevant slice. Wired in
  `setupIncidentMarkerKeybinding` with `runIncidentMarker`
  closure inside `compose ()`.

  **Background heartbeat (no hotkey).** A
  `System.Threading.Timer` ticks every 5 seconds and logs an
  `Information`-level "Heartbeat" line with the same fields as
  the health-check announcement (shell, PID, level, last-read
  staleness, channel queues). Heartbeats stopping in the log
  are a clean wedge timestamp for post-mortem triage. Runs on
  the timer thread pool (NOT the WPF dispatcher), so heartbeats
  keep emitting even if the dispatcher is wedged — the gap
  between the last successful heartbeat and the next captured
  event localises the problem. Initialised in `compose ()`;
  disposed in `app.Exit` BEFORE the logger is disposed (so the
  timer doesn't fire one last entry into a closed channel and
  log a confusing exception).

  **Last-read timestamp threading.** `startReaderLoop` now takes
  an `onChunkRead: int -> unit` callback parameter that the
  reader fires on every non-empty chunk. `compose ()` passes
  `(fun _ -> lastReadUtc <- DateTimeOffset.UtcNow)` to share
  the timestamp across the heartbeat + health-check + every
  reader (initial spawn AND each shell hot-switch — the
  callback is captured in the closure, not the host).
  Unsynchronised reads are acceptable for a diagnostic; one
  weird-looking entry occasionally is OK.

  Two new `ActivityIds` for the announcements:
  `healthCheck = "pty-speak.health-check"`,
  `incidentMarker = "pty-speak.incident-marker"`. Distinct so
  diagnostic-config announcements stay configurable separately
  from streaming output.

  Spec deviation: bundled spec update (§6 hotkey-list amendment)
  per chat 2026-05-03 maintainer authorisation. Same ADR-style
  retroactive-formalisation pattern as PR-C and PR-E.

  Followup to the Stage 7 sequence; closes the diagnostic-
  workflow accessibility gap that surfaced when the maintainer
  attempted manual NVDA validation per
  `docs/ACCESSIBILITY-TESTING.md` Stage 7 row and could not
  reliably enable Debug-level logging or capture an in-progress
  hang.

- **Stage 7-followup PR-E — `Ctrl+Shift+G` toggle debug logging
  + announce state.** Eliminates the previous
  `PTYSPEAK_LOG_LEVEL=Debug` env-var-and-relaunch workflow that
  the maintainer (a screen-reader user) spent two hours fighting
  to enable Debug-level logging. Each `Ctrl+Shift+G` press flips
  the active `FileLoggerSink`'s min-level between `Information`
  (default) and `Debug` and announces the new state via NVDA
  ("Debug logging on." / "Debug logging off."). The toggle event
  itself logs at `Information` level so the audit trail captures
  every transition regardless of which state we just left.
  New `ActivityIds.logToggle = "pty-speak.log-toggle"` so users
  can configure NVDA's notification processing for diagnostic-
  config announcements separately from streaming output. Wired
  in `setupToggleDebugLogKeybinding`. Reserved in
  `TerminalView.AppReservedHotkeys` so Stage 6's
  `OnPreviewKeyDown` filter doesn't mark it `Handled = true`
  before WPF's `InputBindings` machinery can fire. Mnemonic: G
  for "loGging". No NVDA collision (default NVDA bindings don't
  claim `Ctrl+Shift+G`).

  Implementation: `FileLoggerSink.MinLevel` is now a runtime-
  mutable read-out + `SetMinLevel` member (previously read-only
  via the `options.MinLevel` immutable record field). Reads are
  unsynchronised — the level is a 4-byte enum, atomic on x64,
  and the hotkey-driven write happens on the WPF dispatcher
  thread while reads happen on the Channel-drain thread; a
  stale read for one entry is acceptable (worst case: one log
  line at the previous level after a toggle).

  Spec deviation: bundled spec update (§6 hotkey-list amendment)
  per chat 2026-05-03 maintainer authorisation. Same ADR-style
  retroactive-formalisation pattern as Stage 7 PR-C.

  Followup to the Stage 7 sequence; addresses the diagnostic-
  workflow accessibility gap surfaced when the maintainer
  attempted manual NVDA validation per
  `docs/ACCESSIBILITY-TESTING.md` Stage 7 row and could not
  reliably enable Debug-level logging via env-var manipulation.

- **Stage 7 PR-D — NVDA validation matrix expansion +
  `docs/STAGE-7-ISSUES.md` design-derived seed entries.**
  Closes the four-PR Stage 7 sequence per
  `docs/PROJECT-PLAN-2026-05.md` Part 2; depends on PR-A
  (#131, env-scrub PO-5), PR-B (#132, shell registry +
  `PTYSPEAK_SHELL`), and PR-C (#134, hot-switch hotkeys
  `Ctrl+Shift+1` / `Ctrl+Shift+2`).

  `docs/ACCESSIBILITY-TESTING.md` "Stage 7 — Claude Code
  roundtrip" row expanded from 4 tests to 14, covering:
  default-shell launch (PR-B), env-scrub empirical check via
  `set` enumeration confirming deny-list strips (PR-A),
  Claude-shell launch via `PTYSPEAK_SHELL=claude` (PR-B),
  welcome screen + prompt → response (Claude), review-cursor
  navigation of streaming response (`Caps Lock+Numpad 7/8/9`),
  spinner-flood guard, tool-use prompt (Edit/Yes/Always/No),
  hot-switch cmd → claude + claude → cmd (PR-C), hot-switch
  resolve failure announcement when claude.exe isn't on PATH,
  process-cleanup verification post-switch via `Ctrl+Shift+D`
  diagnostic (Job Object `KILL_ON_JOB_CLOSE` cascade), clean
  quit via `/exit` / `exit` / `Ctrl+D`, and
  `PTYSPEAK_SHELL=garbage` unrecognised-value fallback. Each
  test row specifies the exact procedure + expected NVDA
  announcement so the maintainer's manual pass is mechanically
  reproducible.

  Diagnostic-decoder section expanded with new failure-mode
  entries: env-scrub leak indicating `EnvBlock.isDenied`
  regression (security-class), allow-list missing var
  indicating spec §7.2 sync drift, hot-switch silent-announce
  indicating `AppReservedHotkeys` table or filter-ordering
  regression, hot-switch orphan-child accumulation indicating
  `ConPtyHost.Dispose` race with the new spawn (Job Object
  cascade is the safety net), hot-switch announce-but-no-shell
  indicating `wirePostSpawn` not invoked or `SetPtyHost`
  callbacks not re-bound. Each decoder names the specific
  source file or test fixture to triage against.

  `docs/STAGE-7-ISSUES.md` status header flipped from
  "stub created in PR #129; no entries yet" to "Stage 7
  substrate shipped via PRs #131 / #132 / #134; PR-D this
  PR; empirical NVDA validation maintainer-driven." The
  inventory body pre-seeded with **four design-derived
  entries** sourced from `spec/tech-plan.md` §7.4 "Known
  issues you'll hit (drives next stages)" plus the headline
  architectural finding from `docs/SESSION-HANDOFF.md` Stage
  7 sketch "Known risks":

  - `[output-stream]` Verbose readback: whole-screen
    announcement on every Ink frame redraw — the first
    foundational architecture decision the Output framework
    cycle (Part 3) addresses; framework cycle's Stream
    profile with suffix-diff append-only emission resolves.
  - `[output-selection]` Tool-use prompt
    (Edit/Yes/Always/No) reads as flat text instead of
    listbox — original Stage 8 scope, folded into the
    Output framework cycle's Selection profile.
  - `[output-earcon]` Red error text reads as plain text
    with no severity signal — original Stage 9 scope,
    folded into the Output framework cycle's Earcons
    presentation-sink layer.
  - `[review-mode]` No quick-nav after focus moves past a
    response — Stage 10 scope; depends on the
    semantic-event taxonomy landing in the parser →
    semantic-mapper layer first.

  Each entry is marked **Source: design prediction** at the
  bottom; the maintainer's empirical NVDA pass either
  confirms with concrete reproduction steps + version pins,
  refines the prediction, or marks as not-reproducible (in
  which case the substrate is more capable than the spec
  assumed and the framework cycle adjusts accordingly).
  Entries surfaced empirically use the **Source: NVDA pass
  YYYY-MM-DD** marker instead. The distinction matters for
  the Output framework cycle's research phase
  (`docs/research/OUTPUT-FRAMEWORK-PRIOR-ART.md`) which
  reads this inventory as design input.

  Per the May-2026 plan: **don't fix anything from the
  inventory in Stage 7; that's framework-cycle work.** PR-D's
  merge gate is the matrix being green for the rows that
  should be green and the inventory documenting (not solving)
  the rows that aren't.

  After PR-D ships, the doc-purpose audit queued in
  `docs/SESSION-HANDOFF.md` "Queued before Output framework
  cycle starts" runs as the transition-window work before
  the Output framework cycle's Phase 3.1 research begins.

- **Stage 7 PR-C — hot-switch hotkeys `Ctrl+Shift+1` (cmd) /
  `Ctrl+Shift+2` (claude).** Mid-session shell switching via WPF
  `KeyBinding` -> `RoutedCommand` -> orchestrator closure in
  `src/Terminal.App/Program.fs compose ()`. The orchestrator
  resolves the target via `ShellRegistry.tryFind`, announces
  "Switching to {target}." with the existing 700ms
  announce-before-launch delay (matches Stage 4b's diagnostic-
  launcher pattern), tears down the running ConPtyHost (cancels
  reader, terminates immediate child via `TerminateProcess`,
  closes pipes; `KILL_ON_JOB_CLOSE` on the Job Object cascade-
  kills any grandchildren the previous shell spawned), spawns
  a new `ConPtyHost.start` with the same grid dimensions and
  the resolved command line, re-wires keyboard / paste / focus
  / resize callbacks via the extracted `wirePostSpawn` helper,
  and announces "Switched to {target}." Resolve failure (e.g.
  Claude Code not installed) keeps the existing host running
  and announces the failure rather than dropping the user
  into a dead window.

  Both hotkeys are reserved in
  `TerminalView.AppReservedHotkeys` so Stage 6's
  `OnPreviewKeyDown` filter doesn't mark them `Handled = true`
  before WPF's `InputBindings` machinery can fire.
  Number-row digits (`Key.D1` / `Key.D2`), NOT numpad —
  numpad-with-NumLock-off carries NVDA review-cursor commands
  per the accessibility non-negotiables. NVDA collision check:
  `Ctrl+Shift+1` / `Ctrl+Shift+2` have no default NVDA
  bindings (the digit-only `1` / `Shift+1` browse-mode
  heading-quick-nav doesn't fire in focus mode, which is what
  NVDA uses for terminal applications).

  New `ActivityIds.shellSwitch = "pty-speak.shell-switch"` for
  the announce strings; tagged distinctly so users can
  configure NVDA's notification processing for shell-switch
  announcements separately from streaming output.

  `wirePostSpawn` extraction: the post-spawn wiring (env-scrub
  count log, reader-loop start, `SetPtyHost` callbacks) was
  factored out of the initial-spawn `window.Loaded.Add` block
  so the shell-switch coordinator reuses the exact same
  wiring without duplication. Both spawn paths converge on
  `wirePostSpawn host`.

  Spec deviation: bundled spec update (§6 hotkey-list
  amendment + new §7.5 "Shell registry + hot-switch UX")
  per chat 2026-05-03 maintainer authorisation. Mirrors the
  ADR-style retroactive-formalisation pattern that landed
  Stages 4a / 4b / 5a.

  Pinned by `tests/Tests.Unit/ShellSwitchTests.fs`: ShellId
  exhaustive pattern coverage (FS0025 forces lockstep updates
  when shells are added), `Ctrl+Shift+1`-to-`Cmd` /
  `Ctrl+Shift+2`-to-`Claude` mapping, registry/hotkey keyset
  parity check (every registered shell has a hotkey),
  Resolver-Result shape (no exception leak), DisplayName
  natural-language check (announce strings read aloud).

  Known limitations (deferred to follow-up PRs if NVDA
  validation flags them in PR-D):
  - Screen state is NOT reset across switches; new shell's
    first paint overlays the previous screen. cmd -> claude
    is clean (`?1049h` enters alt-screen); claude -> cmd may
    briefly show alt-screen residue.
  - Parser state is NOT reset; mid-CSI/OSC bytes from the
    dying shell may parse oddly until the new shell sends a
    complete sequence.
  - UIA peer ranges are NOT invalidated; NVDA's review cursor
    may briefly point at stale text until the new shell's
    first announce-bound output triggers a fresh
    Notification event.

  Each is acceptable for v1; the framework cycles' RFC scope
  owns the architectural fixes.

  Third of four sequenced Stage 7 PRs per
  `docs/PROJECT-PLAN-2026-05.md` Part 2; depends on PR-B
  (#132) for the `ShellRegistry` registry. PR-D (NVDA
  validation matrix + STAGE-7-ISSUES seeding) follows.

- **`CLAUDE.md` — auto-loaded standing instructions for Claude
  Code sessions.** Consolidates the working rules and project-
  specific gotchas every session needs to know upfront, indexed
  from `docs/SESSION-HANDOFF.md`'s recommended-reading-order
  (now position #1) and `README.md`'s quick-links. Covers:
  branching + PR conventions (one concern per PR, conventional
  commits, squash-merge default, fixup-commit rhythm),
  ask-for-CI-logs-don't-guess on failures (with rationale —
  the local sandbox doesn't expose `/actions/runs/.../logs` so
  speculation produces unfocused fixups), the
  `mcp__github__create_pull_request` auto-subscribe-on-create
  webhook behaviour (use the MCP tool, not `gh pr create`, to
  receive `<github-webhook-activity>` events for CI failures
  and merges), the dotnet-not-on-sandbox tooling reality, the
  full F# 9 + .NET 9 nullness gotcha catalogue (`FS3261`-prone
  APIs: `Environment.GetEnvironmentVariable`, `Process.Start
  (ProcessStartInfo)`, `Path.GetFileName`, `StreamReader.
  ReadLine`), F# delegate conversion (auto-converts to
  `Func`/`Action` only — `Predicate<T>` requires explicit
  construction), record-literal type inference (the `FS0039`
  failure mode that bit Stage 7 PR-B in CI: a record declared
  inside a non-auto-opened module needs an explicit type
  annotation on the literal), the `out SafeHandle&` byref
  brokenness, the `\u0000` source-escape convention for NUL
  literals, the sequence-in-match-arm caveat, the test
  conventions (xUnit + FsCheck.Xunit, plain ASCII source,
  InternalsVisibleTo placement), the logging discipline
  (never log secrets — env-var names like `BANK_API_KEY` are
  themselves sensitive), the AppReservedHotkey contract, and
  the current Stage 7 four-PR sequence index. Future
  CLAUDE.md edits will accumulate gotchas as they surface.

- **Stage 7 PR-B — extensible shell registry + `PTYSPEAK_SHELL`
  startup override.** New `Terminal.Pty.ShellRegistry` module
  (`src/Terminal.Pty/ShellRegistry.fs`) exposes two built-in shells
  pty-speak can spawn as the ConPTY child: `Cmd` (`cmd.exe`, the
  Stage 7 default) and `Claude` (`claude.exe`, resolved at startup
  via `where.exe claude`). Each shell carries a lazy `Resolve`
  closure returning either the command line to pass to
  `PtyConfig.CommandLine` or a human-readable failure reason. The
  shape is designed so future shells (PowerShell, WSL, Python REPL,
  others) plug in by extending the `ShellId` DU and registering an
  entry in `builtIns` — no spawn-path changes required.

  The composition root (`src/Terminal.App/Program.fs compose ()`)
  reads `PTYSPEAK_SHELL` at launch (recognised values: `cmd`,
  `claude`, case-insensitive after trim), resolves the requested
  shell, and falls back to `cmd.exe` with a `LogWarning` if either
  the env-var value is unrecognised or the resolver returns `Error`
  (e.g. Claude Code not installed — pty-speak still works as a
  generic terminal in that case rather than failing the launch).
  Chosen shell + resolved command line are logged at `Information`
  level after spawn.

  Stage 7 PR-A's env-scrub PO-5 block applies uniformly to
  whichever shell is selected — extending the registry doesn't
  broaden the env-leak surface.

  Pinned by `tests/Tests.Unit/ShellRegistryTests.fs`:
  `parseEnvVar` recognised values + case-insensitivity + trim +
  null/empty/whitespace handling + non-substring-match (`cmd.exe`
  is not recognised as `cmd`); `builtIns` keyset pin (force
  reviewer acknowledgement on every shell addition); `tryFindIn`
  synthetic-registry injection so tests don't depend on the
  production `where.exe` invocation. The `whereExe` helper itself
  is exercised manually via `docs/ACCESSIBILITY-TESTING.md`'s
  Stage 7 matrix row (extended in PR-D).

  `docs/USER-SETTINGS.md` "Default shell" section logs `PTYSPEAK_SHELL`
  as today's hardcoded knob and traces the configurability ladder
  (env var today → Ctrl+Shift+1/2 hotkeys in PR-C → Phase 2 TOML
  menu UI). Spec deviation note: the spec §7 launch story doesn't
  mention a registry — the registry is an extension within Stage
  7's scope per maintainer authorization in this session, formalised
  in PR-C's spec update alongside the hotkey contract.

  Second of four sequenced Stage 7 PRs per
  `docs/PROJECT-PLAN-2026-05.md` Part 2; depends on PR-A so the
  env-scrub applies to claude.exe as well. PR-C layers the
  Ctrl+Shift+1 / Ctrl+Shift+2 hot-switch hotkeys on top.

- **`docs/HISTORICAL-CONTEXT-2026-05.md` — supplementary backup
  reference, NOT a primary handoff source.** Curated knowledge dump
  capturing the May-2026 cleanup cycle's guiding principles
  (cleanup before architecture; surface-don't-solve at validation
  gates; spec immutability + dated plans; letter suffixes for
  retroactive stages; historical CHANGELOG entries are frozen;
  small focused PRs over bundling; maintainer-side actions cluster;
  NVDA validation gates every stage; the maintainer's working
  constraints shape the workflow), the technology specificities
  that surfaced (F# 9 nullness at .NET-API boundaries; WPF
  dispatcher / `OnRender` / routed-event ordering; NVDA
  `MostRecent` vs `ImportantAll` activityId processing;
  `AutomationPeer.GetPatternCore` reachability; ConPTY init
  prologue + Job Object lifecycle; `PeriodicTimer` reuse bug;
  AllHashHistory spinner-gate threshold; bounded-channel +
  TCS-barrier patterns; Velopack constraints; xUnit test
  conventions), the coding paradigms specific to pty-speak
  (walking-skeleton stages; two-channel composition;
  `AnnounceSanitiser` chokepoint; `ActivityIds` as NVDA
  configuration vocabulary; `AppReservedHotkeys` filter ordering;
  OSC 52 silent-drop boundary; bracketed-paste injection defence),
  and the process patterns the cycle settled into (manual PR-create
  URL fallback; three-PR chunking for spec-numbering; CHECKPOINTS
  rows in shipping order; status-as-of header on dated docs;
  mechanical merges with content-hash verification; verify diff
  stat before commit; cross-link doc references; fixup-commit
  rhythm).

  Each entry has a "lives in" cross-reference back to the primary
  source (CONTRIBUTING.md, spec sections, CHANGELOG entries,
  module-level doc-comments, etc.). The doc is **explicitly not
  authoritative** — its job is to help a future contributor find
  a curated list rather than archaeology across git history.

  Linked from `README.md` "Quick links" + "Project layout" with
  the explicit "NOT a primary handoff source" framing per
  maintainer instruction. **Read `docs/SESSION-HANDOFF.md`
  first**; this file is the backup reference.

- **Final-handoff audit closing the May-2026 cleanup cycle.** Sweeps
  the last staleness in the entry-point docs so the next session
  picks up cleanly:

  - **`docs/SESSION-HANDOFF.md` "Where we left off" cell rewritten**
    to reflect post-cleanup-cycle state. "In-flight branch" flips
    from the long-merged `claude/audit-repo-handoff-FCsnT` to
    explicit "_None._ The May-2026 cleanup cycle is complete; next
    session starts from Part 2." "Last merged stages" gains the
    spec-formalized Stages 4a / 4b / 5a + the full PR #118 → #128
    cleanup-cycle narrative. "Next stage" rewritten to point at
    Stage 7 with explicit reference to the Stage 7 implementation
    sketch in this same file. "Last shipped release" caveat clarified
    (preview.43 is the latest code-bearing preview; subsequent PRs
    are docs-only).
  - **`docs/SESSION-HANDOFF.md` "Pending action items" #1
    expanded** from "five pending baseline tags" (Stages 0-3b) to
    twelve (5 original + 7 added per PR #127 for Stages 4 / 4a /
    4b / 5 / 5a / 6 / 11). Maintainer-side action: push the tags
    from a workstation, then delete each row from the
    `docs/CHECKPOINTS.md` "Pending checkpoint tags" table per the
    existing convention.
  - **`docs/SESSION-HANDOFF.md` closing notes** gain a paragraph
    documenting the May-2026 cleanup-cycle context (PRs #118 →
    #128 closed Part 1 + the bonus context-dump work; the
    fixup-commit rhythm was exercised on PR #121; small-focused-PR
    cadence works smoothly with squash-merge).
  - **`README.md` status block** realigned: "Stage 4.5" updates to
    "Stage 4a" (matches the spec-formalization in `spec/tech-plan.md`
    §4a); explicit mentions of newly-formalized Stages 4b and 5a
    added; cleanup is marked shipped; Stage 7 explicitly called out
    as next.
  - **`docs/PROJECT-PLAN-2026-05.md`** gains a "Status as of
    2026-05-03 cleanup cycle close" note at the top so a reader
    doesn't mistake Part 1's sub-items for live to-dos. The plan
    body below the note is preserved verbatim for decision-history
    continuity per the doc's own "Future revisions should land as
    new dated plans" rule.
  - **New `docs/STAGE-7-ISSUES.md` stub.** Pre-creates the file the
    Stage 7 implementation sketch references (per
    `docs/SESSION-HANDOFF.md` §5 of the sketch). Contains the
    framework-taxonomy category tags (`[output-stream]`,
    `[output-form]`, `[output-selection]`, `[output-earcon]`,
    `[output-tui]`, `[output-repl]`, `[input-suggest]`,
    `[input-buffer]`, `[input-form]`, `[input-nl]`,
    `[review-mode]`, `[other]`), an entry template, instructions
    for use, and explicit cross-references to the spec / plan /
    sketch. Empty entries section so the next session writes into
    a structured place without making meta-decisions.

  Closes the final context-dump candidate from the May-2026
  cleanup-cycle handoff queue. **Next session starts at Part 2 —
  Stage 7. Read `docs/SESSION-HANDOFF.md` first.**

- **`CONTRIBUTING.md` gains two session-tested practice notes** —
  one F# gotcha and one PR-workflow convention captured during the
  May-2026 cleanup cycle so future contributors don't re-learn them:

  - **F# 9 nullness annotations bite at .NET-API boundaries**
    (under "F# gotchas learned in practice"). Many .NET-API
    methods are typed `string?` under `<Nullable>enable</Nullable>`
    — `Path.GetFileName`, `Path.GetDirectoryName`,
    `Environment.GetEnvironmentVariable`, `StreamReader.ReadLine`,
    etc. Passing the result to a non-null `string` parameter
    compiles to an `FS3261` warning that becomes a build error
    under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
    Two acceptable patterns documented: helper signature accepts
    `string | null` and pattern-matches the null case (matches
    the `AnnounceSanitiser.sanitise` / `KeyEncoding.encodeOrNull`
    convention), or coerce at the call site via `nonNull` /
    inline `match`. PR #121 (Issue #107 filename refinement) hit
    this and is the worked example.

  - **CI failure on an open PR → push a fixup commit to the same
    branch** (under "Branching and pull requests"). GitHub PRs
    track the branch HEAD, not a snapshot at PR-creation time;
    pushing additional commits auto-extends the PR and re-runs
    CI without disturbing the PR number, title, body, or
    `Closes #N` references. **Don't open a new PR for a fixup**
    — the squash-merge convention combines original + fixup
    into a single canonical commit on `main`. PR #121 (same
    Issue #107 work) used this rhythm and is the worked
    example.

  Both bullets cross-reference each other so a reader landing on
  either entry finds the other.

- **`docs/CHECKPOINTS.md` checkpoint rows for shipped post-Stage-3b
  stages.** The 2026-05-03 audit (in
  [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md))
  flagged a "Post-Stage-3b checkpoint rows pending" hygiene gap:
  Stages 4, 4a, 4b, 5, 5a, 6, and 11 had all merged to `main` and
  shipped in maintainer-tested previews, but no `baseline/stage-N`
  rollback-tag rows existed. This PR fills the gap with seven new
  rows in shipping order:

  - `baseline/stage-4-uia-document-text-pattern` — anchor PR #68
    (real word-navigation closing the Stage 4 follow-up).
  - `baseline/stage-11-velopack-auto-update` — anchor PR #66
    (window-title version suffix + structured update-failure
    messages).
  - `baseline/stage-4b-process-cleanup-diagnostic` — anchor PR #81
    (`Ctrl+Shift+D` diagnostic launcher).
  - `baseline/stage-4a-claude-code-substrate` — anchor PR-B #86
    (alt-screen 1049 back-buffer; Stage 4a complete with PR-A #85
    + PR-B #86).
  - `baseline/stage-5-streaming-coalescer` — anchor PR #89
    (Coalescer module + alt-screen flush barrier + Acc/9 OnRender
    lock fix bundled).
  - `baseline/stage-6-keyboard-input` — anchor PR #100 (post-Stage-6
    stability fixup completing PR-A #92 + PR-B #99).
  - `baseline/stage-5a-diagnostic-logging` — anchor PR #122
    (FlushPending; the most recent constituent that completes the
    Stage 5a scope per spec §5a).

  Each row includes a paragraph-length scope description matching
  the existing Stage 0–3b row tone (PR links + release link where
  applicable + technical narrative). Each row also has a matching
  "Pending checkpoint tags" entry with the exact `git tag -a SHA -m`
  + `git push` commands the maintainer runs from a workstation
  (the dev sandbox proxy returns 403 on tag pushes per
  `docs/SESSION-HANDOFF.md` "Sandbox + tools caveats"). The
  obsolete "Post-Stage-3b checkpoint rows pending" notice is
  removed.

  No code touched. Pure docs hygiene closing out the audit
  branch's flagged TODO.

- **Stage 7 implementation sketch in `docs/SESSION-HANDOFF.md`.**
  The next session that picks up Part 2 of the May-2026 plan
  (Claude Code roundtrip + env-scrub PO-5 — the validation gate
  before the Output / Input framework cycles) gets a
  pre-digested execution plan parallel to the existing Stage 4
  / Stage 11 sketches in the same file. ~250 lines covering:

  - **Why Stage 7 is the validation gate** — the framework
    cycles need ground-truth signal from the primary target
    workload before they can be designed coherently.
  - **Implementation outline** — `claude.exe` resolution
    (`where.exe claude` with cmd.exe fallback), configurable
    shell via `PTYSPEAK_SHELL` env var, child-process
    environment block construction via `lpEnvironment` to
    `CreateProcess` (allow-list-with-deny-list-override scheme
    for PO-5 env-scrub), NVDA validation flow, Stage 7 issues
    inventory format (`docs/STAGE-7-ISSUES.md` with
    framework-taxonomy category tags as design input for
    Parts 3 + 4).
  - **Pre-digested decisions** — cmd.exe stays default;
    `ANTHROPIC_API_KEY` in allow-list; env-scrub log line
    counts but never names/values per `SECURITY.md` logging
    discipline; no spec-§7-deltas without ADR-style
    authorization.
  - **Critical files to touch** + **existing primitives to
    reuse** + **what this stage deliberately does NOT do** +
    **known risks** (F# string-block marshalling silently
    fails; Claude Code's NVDA experience may already exceed
    the coalescer's capacity; spawned-Claude lifecycle
    differs from cmd.exe; `where.exe claude` may resolve a
    stale wrapper).
  - **Scope discipline** — one PR with the env-scrub
    potentially as a fixup; STAGE-7-ISSUES.md grows over
    multiple NVDA verification cycles but doesn't block PR
    merge.

  Reading-order item 4 in the same file updates: §1-§6 are
  fully shipped, §4a/4b/5a are retroactively-formalized
  shipped stages, §7 is the next stage with the
  implementation plan in this file's "Stage 7 implementation
  sketch (next)" section.

- **`FileLoggerSink.FlushPending(timeoutMs)` API.** New public
  member that returns a `Task<bool>` completing when the
  background drain finishes its next per-batch flush, or after
  the timeout — whichever comes first. `true` means a flush
  completed within the window; `false` means the timeout fired
  (channel was idle, or the host pegged for longer than the
  budget).

  Implementation: a TCS-barrier owned by the sink. The drain
  loop atomically swaps the current `flushTcs` for a fresh one
  after every successful `StreamWriter.Flush` and completes
  the swapped one — so a caller that captures the current TCS
  and awaits it gets signalled the next time the drain
  completes a flush. Lock-protected swap; idempotent
  `TrySetResult`; signalled once more after the dispose-time
  final flush so callers awaiting at shutdown see completion
  rather than timeout.

  **Wired into `runCopyLatestLog` (`Ctrl+Shift+;`)** with a
  500ms budget. Without this barrier, the bounded channel
  could hold ~milliseconds of recent entries that hadn't been
  written yet — the clipboard would capture a stale snapshot
  of the file. The 500ms cap is the worst-case dispatcher
  block under user-pressed-the-hotkey conditions; in practice
  the drain finishes in low ms. On timeout, the handler logs
  an `Information`-level note and proceeds with the
  not-quite-current file content (better than no copy at all).

  Caveat: if the channel is fully idle (no pending entries),
  the drain loop is parked in `WaitToReadAsync` and won't fire
  a flush until something arrives. `FlushPending` returns
  `false` (timeout) in that case — but the file already
  contains everything the writer has produced, so the
  not-drained path is benign.

  Test:
  `tests/Tests.Unit/FileLoggerTests.fs` gains
  `FlushPending makes recently-enqueued entries readable while
  the writer is active` — enqueues 5 entries with a unique
  marker, calls `FlushPending(2000)`, then reads the file
  with `FileShare.ReadWrite` (matching `runCopyLatestLog`'s
  production path) WITHOUT disposing the sink first, and
  asserts every entry made it to disk. Failure here means
  the drain's `signalFlushComplete` wiring or the TCS-swap
  path regressed.

- **Strategic plan committed to repo:
  [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md).**
  Captures the post-PR-#116 architecture review and sequences
  the next ~8-12 weeks of work as **Part 1** Cleanup → **Part 2**
  Stage 7 Claude Code roundtrip + env-scrub PO-5 (validation
  gate) → **Part 3** Output-handling framework cycle (subsumes
  original Stages 8 + 9 as Selection profile + earcons sink) →
  **Part 4** Input-interpretation framework cycle (parallel to
  Part 3; bridges to it via echo-correlation API) → **Part 5**
  Stage 10 review mode + quick-nav (first non-built-in consumer
  of the framework's semantic-event taxonomy). The plan
  supersedes `spec/tech-plan.md`'s Stage 7-10 ordering
  specifically; the spec remains immutable as architectural
  rationale. `docs/SESSION-HANDOFF.md`, `docs/ROADMAP.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`, and `README.md`
  cross-link the plan as the canonical source for the next
  several months of work, so a fresh session (Claude or human)
  can pick up the work without re-deriving the rationale.

- **Repo-wide handoff doc freshness sweep.** Bundled with the
  plan-doc commit:
  - `docs/SESSION-HANDOFF.md` — Stage 11 implementation sketch
    relabeled "shipped — retained as reference" (parallel to
    the existing Stage 4 sketch); pending-action-items 5.1
    (PO-5 env-scrub) reframed via May-2026 plan Part 2;
    item 6 (diagnostic-launcher native replacement) marked
    actionable now that Stages 5 + 6 have shipped; item 7
    (stale-branch bulk-delete) count refreshed (~100, was 77).
  - `docs/CHECKPOINTS.md` — added "Post-Stage-3b checkpoint
    rows pending" notice listing missing checkpoint rows for
    Stages 4, 4.5, 5, 6, and 11.
  - `SECURITY.md` — PO-5 row reframed from "accepted risk /
    defer" to "planned / sequenced as plan Part 2"; TC-5 row
    marked **shipped** since Stage 5's `Coalescer` confirmed
    routes per-row announcements through `AnnounceSanitiser`
    (verified at `src/Terminal.Core/Coalescer.fs:178`); the
    PRs that add log calls clause cross-links the plan.
  - `docs/USER-SETTINGS.md` — verbosity section rewritten
    around the May-2026 plan's per-profile output-framework
    taxonomy (Off / Smart / Verbose preserved as a power-user
    override per profile, not the primary surface); Stage 9
    audio section flagged as subsumed into Part 3 Stage G.

- **`Ctrl+Shift+;` copies the active session's log file content
  to the clipboard.** Bundled with the logging-restructure
  work. Pressing the hotkey reads the active log file (the one
  `FileLoggerSink.ActiveLogPath` points to), sets the OS
  clipboard, and announces the byte count via NVDA ("Log
  copied to clipboard. N bytes; ready to paste."). Fastest
  path to send a session log to a maintainer for bug-report
  diagnosis — no File Explorer navigation required.

  Hotkey-choice rationale. The semicolon / colon key sits
  immediately to the right of `L` on a US-layout keyboard,
  so it pairs by physical proximity with `Ctrl+Shift+L`
  (open logs folder) — same hand position, two adjacent keys.
  Other candidates considered and declined: `Ctrl+Alt+L` (the
  original; collides with the Windows Magnifier zoom-in
  shortcut, AND the `Alt`-modifier path through WPF's input
  pipeline required a SystemKey-aware filter that broke
  `Alt+F4`); `Ctrl+Shift+C` (the cross-terminal "copy"
  convention but reserved here for a future
  copy-latest-command-output feature, plus today it folds to
  `0x03` / SIGINT in the keyboard encoder — claiming it
  would lose the `Ctrl+Shift+C`-as-interrupt habit some
  users have); `Ctrl+Shift+M` (stays reserved for the Stage 9
  earcon mute toggle). Layout caveat: on non-US keyboards
  the `OemSemicolon` virtual-key sits in a different
  physical position; remap support is on the Phase 2
  user-settings roadmap.

  Added to `AppReservedHotkeys`; wired in
  `setupCopyLatestLogKeybinding` in `Program.fs`. The handler
  catches and announces clipboard exceptions (the OS clipboard
  can transiently throw COMException under contention; one
  failed attempt becomes an audible error rather than a silent
  no-op).

  Documentation: README, USER-SETTINGS.md, and LOGGING.md
  updated with the new hotkey, the rationale, and a refreshed
  "Sharing logs with a maintainer" section that promotes
  `Ctrl+Shift+;` as the fastest path.

- **File-based structured logging.** New
  `Terminal.Core/FileLogger.fs` implements `ILogger` /
  `ILoggerProvider` directly against
  `Microsoft.Extensions.Logging.Abstractions` (the
  first-party SDK package — no Serilog or other third-party
  dependency added). A single background task drains a bounded
  channel, formats entries, and appends to
  `%LOCALAPPDATA%\PtySpeak\logs\pty-speak-{date}.log`. Daily
  rolling, 7-day retention, off-thread writes so the WPF
  dispatcher never blocks on disk.

  New `Ctrl+Shift+L` hotkey opens the logs folder in File
  Explorer for one-keypress retrieval when reporting bugs.
  Added to `AppReservedHotkeys`; wired in
  `setupOpenLogsKeybinding` in `Program.fs`. The
  `runOpenLogs` handler uses the same announce-before-launch
  pattern as the other window-spawning hotkeys so NVDA's
  speech queue gets ~700ms before File Explorer steals focus.

  Default log level: **Information**. Off-by-default trace
  levels (Trace, Debug) reserved for verbose troubleshooting;
  Phase 2 user-settings will surface a toggle.

  Initial log calls land at the diagnosis-critical points:

  - App startup (version, OS, log directory).
  - `compose ()` lifecycle.
  - ConPTY child spawn (success with PID, failure with full
    error variant).
  - Coalescer.runLoop entry, clean-cancel exit, and exception
    path (this is the path that will catch the post-Stage-6
    intermittent "Coalescer crashed" we still haven't pinned
    down).
  - Drain task crash path (alongside the existing
    `Announce(..., pty-speak.error)` user-facing notice).
  - App exit.

  **Security posture:** new "Logging chokepoint" entry in
  `SECURITY.md`. The call-site discipline NEVER logs typed
  user input, paste content, full screen contents, or
  environment variables. Same first-class status as the
  `AnnounceSanitiser` chokepoint from audit-cycle SR-2.

  **Documentation:** new `docs/LOGGING.md` covers location,
  format, retention, what's logged, what isn't, and how to
  share log slices with a maintainer.

  **Tests:** 8 new `FileLoggerTests` pinning the contract:
  Information entries land in today's file with the
  documented format; minimum-level filtering drops below-min
  entries; exception details land in the file; retention
  sweep deletes >7-day-old files on startup; log directory
  is created on demand; `LogDirectory` member exposes the
  path; `Logger.get` returns a `NullLogger` before
  `Logger.configure` runs; the configured factory's logger
  produces correctly-categorised output.

- **Stage 6 PR-B: keyboard input, paste, focus reporting, dynamic
  resize, and Job Object child-process lifecycle.** Second and
  final half of Stage 6 — pty-speak becomes interactive. Typed
  keys reach the cmd.exe child via the new pure-F# `KeyEncoding`
  module; paste via Ctrl+V / right-click / Edit menu wraps in
  bracketed-paste markers when DECSET ?2004 is set; window resize
  flows through to `ResizePseudoConsole` after a 200ms debounce;
  the spawned child plus any process it later spawns are
  contained in a Job Object so the entire tree dies via
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` even on a hard parent
  crash.

  Component pieces:

  - **New `Terminal.Core.KeyEncoding` module** — pure F# encoder
    `KeyCode * KeyModifiers * TerminalModes -> byte[] option`.
    Decoupled from `System.Windows.Input.Key` via its own
    `KeyCode` discriminated union and `KeyModifiers` flags type
    so a future Linux / macOS port (Avalonia, MAUI, web) reuses
    this module unchanged — only the WPF→KeyCode adapter changes.
    Encoding tables follow the xterm "PC-Style" + "VT220-Style"
    function-key conventions: arrows are DECCKM-aware (`\x1b[A`
    normal vs `\x1bOA` application), modified cursor keys use
    the SGR-modifier protocol (`\x1b[1;<mod>A`), F1-F4 use SS3
    form (`\x1bO<P/Q/R/S>`), F5-F12 use CSI form
    (`\x1b[<n>~`), Ctrl-letter folds Shift, Alt-letter
    ESC-prefixes, Backspace sends DEL (`0x7f`, modern xterm
    default that bash / zsh / PowerShell / Claude Code all
    expect). The `KeyCode.Unhandled` case is the
    future-proofing escape hatch — any unknown key produces
    `None` rather than a crash; new WPF Key values can ship
    without breaking us.

  - **Bracketed-paste handler** bound to
    `ApplicationCommands.Paste` so Ctrl+V, right-click → Paste,
    and Edit menu → Paste all flow through one site.
    `KeyEncoding.encodePaste` strips embedded `\x1b[201~` from
    clipboard content **before** wrapping — paste-injection
    defence diverging from xterm's permissive default. NVDA
    users can't easily inspect their clipboard before pasting,
    so an attacker-crafted paste containing `\x1b[201~`
    followed by a malicious command would otherwise close the
    bracket-paste frame early and execute the post-paste
    portion as if typed. SECURITY.md tracks this as a
    deliberate accessibility-first posture divergence.

  - **Focus reporting** via `OnGotKeyboardFocus` /
    `OnLostKeyboardFocus`. Emits `\x1b[I` / `\x1b[O` to the
    child only when DECSET ?1004 is set. Editors like nano /
    vim / Emacs and Claude Code use these to suspend cursor
    blink, save unsaved buffers on focus loss, etc.

  - **Dynamic resize** via `OnRenderSizeChanged` →
    `DispatcherTimer` (200ms trailing-edge debounce) →
    `ConPtyHost.Resize` → `Win32.ResizePseudoConsole`. WPF
    SizeChanged fires per pixel during a window drag (60Hz);
    debouncing prevents the child shell from re-laying-out on
    every tick and flooding Stage 5's output coalescer.
    Hardcoded `// TODO Phase 2: TOML-configurable` constant.
    Note: Stage 6 resizes the **PTY** (so the child shell sees
    the new column count); the in-process `Cell[,]` Screen
    grid stays at construction-time 30×120, so oversize
    windows have empty padding and undersize windows clip.
    Full grid runtime resize is logged as a Phase 2 stage in
    `docs/SESSION-HANDOFF.md`.

  - **Job Object child-process lifecycle.** `Native.fs` adds
    P/Invokes for `CreateJobObjectW`,
    `SetInformationJobObject`, `AssignProcessToJobObject` plus
    the `JOBOBJECT_BASIC_LIMIT_INFORMATION` /
    `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` / `IO_COUNTERS`
    structs and a `SafeJobHandle`. `PseudoConsole.create`
    creates a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
    set, assigns the immediate cmd.exe to it, and stores the
    handle on `PtySession.JobHandle`. **Layered on top of**
    the existing `TerminateProcess` cleanup rather than
    replacing it: `TerminateProcess` is fast targeted cleanup
    for the immediate cmd.exe (so its pipe drains promptly);
    the Job Object is the kernel-enforced safety net for
    grandchildren (e.g. a `node` process Stage 7's Claude
    Code launches inside pty-speak). On any setup-step
    failure the orphan child is terminated before returning
    Error so we never leak.

  - **`OnPreviewKeyDown` filter ordering** is load-bearing
    and pinned by inline doc-comment + the test suite:
    1. `AppReservedHotkeys` check first (Ctrl+Shift+U /
       Ctrl+Shift+D / Ctrl+Shift+R short-circuit and let
       the parent Window's `InputBindings` see them).
    2. NVDA / screen-reader modifier filter second (bare
       Insert / CapsLock and Numpad-with-NumLock-off
       return without `Handled`).
    3. WPF Key + ModifierKeys → `KeyCode` + `KeyModifiers`
       translate.
    4. Plain printable typing (letters / digits / space
       without Ctrl or Alt) defers to `OnPreviewTextInput`
       so WPF's text-composition pipeline (IME, AltGr,
       dead keys) handles it correctly.
    5. Encode + write via `KeyEncoding.encodeOrNull`.

  - **NVDA / screen-reader compatibility.** The bare
    Insert + CapsLock filter covers NVDA, JAWS, and Narrator
    modifier keys uniformly. The Numpad-with-NumLock-off
    filter covers NVDA's review-cursor numpad layout.
    Conservative on purpose — the cost of a few key presses
    not reaching the shell is tiny compared to the cost of
    breaking screen-reader navigation.

  - **`AppReservedHotkeys` refresh** in
    `src/Views/TerminalView.cs` — was stale (only listed
    Ctrl+Shift+U); now lists all three currently-shipped
    hotkeys (Ctrl+Shift+U, Ctrl+Shift+D, Ctrl+Shift+R) plus
    the future-reserved Ctrl+Shift+M (Stage 9) and
    Alt+Shift+R (Stage 10) as comments.

  - **`Program.fs compose ()`** — wires the new host into
    `TerminalView.SetPtyHost` after spawn. The view takes
    `Action<byte[]>` + `Action<int,int>` callbacks rather
    than a direct `ConPtyHost` reference so `Views/`
    intentionally doesn't take a project ref on
    `Terminal.Pty` (preserves the F#-first / WPF-only-at-
    the-edge boundary). All callbacks invoke on the WPF
    dispatcher thread, which is also the only thread that
    touches the ConPTY stdin pipe — single-writer
    discipline by construction.

  Tests:

  - **New `tests/Tests.Unit/KeyEncodingTests.fs`** (~35
    facts) pinning the entire encoding table: cursor keys
    (DECCKM normal vs application vs modified); editing
    keypad (Insert / Delete / Home / End / PageUp /
    PageDown); F1-F12 (SS3 vs CSI form, modified
    variants); Tab / Enter / Esc / Backspace; Ctrl-letter
    folding Shift; Alt-letter ESC-prefix; Ctrl+`@`/`[`/
    `?` mapping to NUL/ESC/DEL; non-ASCII char returns
    None; Unhandled returns None; bracketed paste
    wrapping + injection defence; Unicode UTF-8
    survival; focus-reporting bytes; SGR-modifier
    parameter encoding; `encodeOrNull` C#-friendly
    wrapper.

  - **`tests/Tests.Unit/ConPtyHostTests.fs`** extended
    with two new Stage 6 facts: `ConPtyHost.Resize`
    accepts new dimensions without erroring; the
    `JobHandle` is non-null and not-invalid after
    spawn (proves the Job Object setup path works).

- **Stage 6 PR-A: parser arms for DECCKM, bracketed paste, and
  focus reporting.** The first half of Stage 6 lands the
  parser-side mode-flag plumbing for the three remaining
  `TerminalModes` flags that were declared-but-inert in
  Stage 4.5: `DECCKM` (`?1`, application vs normal cursor
  mode), `BracketedPaste` (`?2004`), and `FocusReporting`
  (`?1004`). Each new arm in
  `src/Terminal.Core/Screen.fs csiPrivateDispatch` follows
  the Stage 4.5 alt-screen template exactly: idempotence
  guard (a no-op flip silently returns) + flag mutation +
  `pendingModeChanges.Add` for the post-lock-release
  `ModeChanged` event fire that Stage 5's coalescer
  subscribes to. The flags are now toggleable from
  child-shell escape sequences but the consumer-side
  behaviour (key encoder reading `Modes.DECCKM`, paste
  handler reading `Modes.BracketedPaste`, focus events
  reading `Modes.FocusReporting`) lands in Stage 6 PR-B
  alongside the WPF input wiring + `ResizePseudoConsole` +
  Job Object lifecycle. PR-A is pure F#, no WPF, no Win32
  — splitting the stage along that line lowers review cost
  and keeps the bisect surface small if NVDA validation
  catches something later.

  Tests: 15 new `ScreenTests` (3 modes × 4 cases:
  set-fires, reset-fires, idempotent-set-no-fire,
  idempotent-reset-no-fire — plus 3 cross-flag
  independence tests defending against future
  shared-backing-field refactors). Templates from the
  Stage 5 alt-screen `ModeChanged` test triplet.

- **Stage 5: streaming-output coalescer.** First stage where
  PTY output narrates itself — before Stage 5, the only NVDA
  flow was review-cursor exploration (the user navigated to
  new content); after Stage 5, NVDA reads streaming output
  line-by-line at conversational pace as the PTY produces it.
  Per `spec/tech-plan.md` §5: "When PTY output arrives, NVDA
  reads it aloud at conversational pace. Spinner doesn't
  flood. Multi-line output is announced line by line."

  Implementation:

  - New `Terminal.Core.Coalescer` module
    (`src/Terminal.Core/Coalescer.fs`) sits between the
    parser-side `notificationChannel` (256, DropOldest) and
    a new `coalescedChannel` (16, Wait). Reads every
    `ScreenNotification` the parser publishes, applies
    debounce + dedup + spinner suppression, and emits at
    most one `CoalescedNotification` per ~200ms window.

  - **Per-row + frame hash** via FNV-1a 64-bit. Per-row hash
    folds the row index in so a row swap can't alias to the
    same frame hash; frame hash XORs per-row hashes.
    Two consecutive `RowsChanged` events with identical
    screen content produce identical frame hashes and the
    second is suppressed entirely (composes with Claude
    Ink's full-frame redraws — without this, every row
    hash changes per redraw and per-row dedup never fires).

  - **Spinner heuristic**: sliding window keyed by
    `(rowIdx, hash)` with a 1s window and threshold of 5
    same-key hits, plus a generic "any-hash high-frequency
    anywhere" gate at 4× threshold. Suppresses repeated
    frames at high rate (`|/-\` spinners, etc.).

  - **Debounce**: leading-edge + trailing-edge. First event
    in an idle period (no flush in last 200ms) emits
    immediately for fast single-event UX (`echo hello`);
    subsequent events accumulate and drain on the next
    200ms timer tick.

  - **Alt-screen flush barrier**: new
    `ScreenNotification.ModeChanged(flag, value)` case
    + `TerminalModeFlag` discriminator added to
    `Terminal.Core.Types`. `Screen.enterAltScreen` /
    `exitAltScreen` now queue `(AltScreen, true/false)` into
    a `pendingModeChanges` buffer under the gate; `Apply`
    drains the buffer AFTER releasing the lock and fires
    a new `[<CLIEvent>] ModeChanged` event. The coalescer
    subscribes (via `Program.fs compose ()`) and on
    `ModeChanged` flushes any pending accumulator first,
    resets frame-hash + spinner state, then passes the
    barrier through. Stage 6 will reuse the same shape for
    DECCKM, bracketed paste, and focus reporting.

  - **Sanitisation**: every emit text passes through the
    audit-cycle SR-2 `AnnounceSanitiser.sanitise`
    chokepoint per row, then rows are joined with `\n` so
    NVDA's per-line speech pause survives (the bug-prone
    naive "sanitise the whole joined string" path would
    have stripped `\n` as a C0 control and collapsed
    multi-line output into a single line).

  - **Activity IDs**: new `Terminal.Core.ActivityIds`
    module providing the stable
    `pty-speak.{output,update,error,diagnostic,releases,mode}`
    vocabulary so NVDA users can configure per-tag
    handling (e.g. quieter speech for the install flow vs.
    streaming text). The new
    `TerminalView.Announce(message, activityId)` overload
    lets the drain pass the right tag per
    `CoalescedNotification` shape.

  - **Two-channel composition**: `Program.fs compose ()`
    now starts the coalescer as a `Task.Run` with the
    SHARED `cts.Token` (single CTS, unified
    cancellation across reader, coalescer, and drain).
    Production passes `TimeProvider.System`; tests
    inject `FakeTimeProvider` for deterministic
    debounce assertions.

  - **`TimeProvider` injection**: `Coalescer.runLoop`
    accepts `TimeProvider`; new
    `Microsoft.Extensions.TimeProvider.Testing` package
    pinned at 9.0.0 in `Directory.Packages.props` so
    `CoalescerTests` can advance time without
    `Thread.Sleep`.

  - **`Acc/9` OnRender lock fix bundled** (per the
    SESSION-HANDOFF item 5.3 commitment).
    `src/Views/TerminalView.cs`'s `OnRender` previously
    called `_screen.GetCell(row, c)` per cell, which
    re-entered the screen gate up to `Rows*Cols` times
    per render frame and could race with the parser
    thread between cells. Refactored to take ONE
    `_screen.SnapshotRows(0, _screen.Rows)` snapshot at
    the start of the frame and walk that immutable copy
    in `RenderRow` / `DrawRun`. Single gate acquisition
    per render; no measurable perf cost. SESSION-HANDOFF
    item 5.3 flips from "deferred — Stage 5 will revisit"
    to "✓ resolved by Stage 5".

  Tests:

  - New `tests/Tests.Unit/CoalescerTests.fs` (24 facts)
    pinning every algorithm independently
    (hash equality / row-swap defence; frame dedup;
    leading- / trailing-edge debounce; per-key spinner
    gate firing + GC release; mode barrier flush +
    state reset; `ParserError` pass-through with
    sanitisation; `renderRows` per-row sanitise +
    `\n` preservation + trailing-blank trimming;
    activity-ID vocabulary pinning; `runLoop`
    cancellation cleanup; `runLoop` end-to-end with
    real `Screen` + `FakeTimeProvider`).

  - `tests/Tests.Unit/ScreenTests.fs` extended with
    five new `ModeChanged` event tests (fires on
    enter; fires on exit; idempotent enter / exit
    do NOT fire; subscriber can call `SnapshotRows`
    without deadlock — pins the post-lock fire
    contract Stage 5's coalescer relies on).

  Out of scope (deferred per the approved Stage 5 plan):

  - **Verbosity profiles** (off / smart / verbose) —
    Phase 2 TOML config. Stage 5 ships hardcoded
    200ms debounce + 1s spinner-window with
    `// TODO Phase 2` comments at each constant.

  - **`ITextProvider2` / `TextChangedEvent`** —
    explicitly forbidden per spec §5.6 (NVDA disables
    TextChanged for terminals to prevent
    double-announce).

  - **`TermControl2` className** in
    `TerminalAutomationPeer.GetClassNameCore` — could
    signal NVDA's terminal-app heuristics; not
    validation-required; flag for any later stage that
    touches the peer.

  - **Per-event-class `activityId`s for the diagnostic
    / releases hotkeys** — today they pass through the
    default `pty-speak.update` tag. Stage 5 adds the
    vocabulary; whoever next touches those hotkeys can
    flip them.

  - "Command output complete" prompt-redraw signal —
    strategic review §G assigned this to Stage 8.

  - Stage 6 keyboard input + Stage 7 Claude Code
    roundtrip + Stage 8/9/10 features — separate
    stages.

- **Stage 4.5 PR-B: alt-screen 1049 back-buffer.** Closes the
  last latent gap in the Claude Code rendering substrate.
  Claude's Ink reconciler — and many other modern TUIs (`less`,
  `vim`, `fzf`, `git log` pager, npm install's progress bars)
  — sends `\x1b[?1049h` on startup to enter the alternate
  screen and `\x1b[?1049l` on exit. Without alt-screen support,
  the primary buffer's scrollback would get corrupted on every
  alt-screen TUI launch; with it, the primary content is
  preserved untouched and the screen reader can navigate
  whichever buffer is active at the moment.

  Implementation:

  - `Screen` now holds two `Cell[,]` buffers (`primaryBuffer`,
    `altBuffer`) and a `mutable activeBuffer` field that
    points at one of them. Every cell read / write site
    (`printRune`, `executeC0` for BS/HT/LF/CR, `eraseDisplay`,
    `eraseLine`, `csiDispatch` cursor moves, `SnapshotRows`,
    `GetCell`) was migrated from `cells.[r, c]` to
    `activeBuffer.[r, c]` in one mechanical rename.

  - `csiPrivateDispatch` (added by PR-A) now handles
    `?1049h` → `enterAltScreen ()` and `?1049l` →
    `exitAltScreen ()`. Both functions are idempotent: a
    repeated `?1049h` while already in alt-screen is a
    no-op; a repeated `?1049l` while already on primary is
    a no-op.

  - **Save/restore semantics match xterm `?1049`**: on enter,
    the cursor row / col / SGR attrs are captured into a
    `savedPrimary: (int * int * SgrAttrs) option` field;
    `activeBuffer` is repointed at `altBuffer`; the alt
    buffer is cleared (xterm convention — alt-screen always
    starts blank); cursor moves to (0, 0) with default
    attrs. On exit, the saved state is restored and
    `activeBuffer` is repointed at `primaryBuffer`. Primary
    cells are *never copied* — they sit unchanged in
    `primaryBuffer` because nothing wrote to them during
    the alt session.

  - **`Modes.AltScreen` flag** flips with the swap so future
    consumers (UIA peer announcing buffer changes, Stage 5
    coalescer needing flush barriers, etc.) can read it.

  - **`SequenceNumber` bumps on `?1049h/l`** as a side
    effect of every `Apply` call. Stage 5's coalescer
    should treat alt-screen toggles as a hard
    invalidation barrier — flush the debounce window,
    then resume — because the row content can change
    wholesale between buffers and a debounce window
    straddling a swap would mis-attribute rows. The PR-B
    test `SequenceNumber bumps on ?1049h and ?1049l` pins
    the contract for Stage 5's author to read.

  9 new tests in `tests/Tests.Unit/ScreenTests.fs` exercise:
  the AltScreen flag toggle; primary content preservation
  across alt-screen entry/exit; alt-buffer reset on every
  entry; cursor + attrs reset on enter and restore on
  exit; idempotency of double-enter and double-exit;
  `SnapshotRows` returning alt content during alt mode and
  primary content after exit; and the `SequenceNumber`
  bump on toggle.

  **Stage 4.5 cycle complete with this PR**: the substrate
  is in place for Stage 7 to actually run Claude Code. Next
  is Stage 5 (streaming output notifications) — the
  coalescer plugs into the `ScreenNotification` channel
  seam shipped in audit-cycle PR-B and uses the `Modes`
  bits + `SequenceNumber` bumps that Stage 4.5 PR-A and
  PR-B established.

- **Stage 4.5 PR-A: VT mode coverage + SGR table fills.**
  Closes the latent gap where the parser correctly emits
  events that `Screen.fs` silently dropped at its `_ -> ()`
  catch-all arms. Without these, Stage 7's Claude Code
  roundtrip fails because Claude's Ink reconciler sends
  `?25l` (hide cursor) + truecolor SGR + DECSC/DECRC on
  every state change. PR-B will follow with alt-screen
  1049 (the architectural piece — separate buffer +
  swap dispatch); PR-A is the catch-all-arm fills plus
  the substrate (`TerminalModes` record, private-CSI /
  ESC dispatch split, SGR walker refactor) that PR-B
  plugs into.

  - **`TerminalModes` record** in `Terminal.Core/Types.fs`
    centralises the mode bits Stage 5/6/7 need. Wired
    today: `CursorVisible` (DECTCEM `?25h/l`). Stubbed for
    Stage 6: `DECCKM`, `BracketedPaste`, `FocusReporting`.
    `AltScreen` is wired by PR-B.

  - **`Cursor` record refactored.** Dropped the dead
    `Visible: bool` field (single source of truth via
    `Modes.CursorVisible`). Replaced
    `SaveStack: (int * int) list` with
    `SaveStack: CursorSave list` where `CursorSave` is a
    record `{ Row; Col; Attrs }` so DECSC also saves SGR
    attrs (matches xterm convention; forward-compatible
    for Stage 6 origin mode / character-set selection).

  - **`InternalsVisibleTo("PtySpeak.Tests.Unit")`** added
    to `Terminal.Core` (top of `Types.fs`) so tests can
    introspect `TerminalModes` flags, `Cursor.SaveStack`
    depth, and the alt-screen back-buffer that PR-B will
    add. Mirrors the precedent in
    `Terminal.Accessibility/TerminalAutomationPeer.fs:22-23`.

  - **DECTCEM (`?25h` / `?25l`)** wired via a new
    `csiPrivateDispatch` helper in `Screen.fs`, dispatched
    from `Apply`'s `CsiDispatch` arm when the parser
    passes the `?` private marker. Public-marker CSI
    sequences continue to flow through the existing
    `csiDispatch`. Stage 6 will plug DECCKM (`?1`),
    bracketed paste (`?2004`), and focus reporting
    (`?1004`) into the same `csiPrivateDispatch`.

  - **DECSC (`ESC 7`) and DECRC (`ESC 8`)** wired via a
    new `escDispatch` helper in `Screen.fs`. DECSC pushes
    `{ Row; Col; Attrs }` onto `Cursor.SaveStack`. DECRC
    pops and restores; on empty stack, restores to
    (0, 0) with default attrs (xterm convention).

  - **256-colour SGR (`\x1b[38;5;n m` / `\x1b[48;5;n m`)
    and truecolor SGR (`\x1b[38;2;r;g;b m` /
    `\x1b[48;2;r;g;b m`)** wired via a refactored `applySgr`
    that walks the parameter array index-by-index,
    consuming sub-parameters when it sees a `38` or `48`
    trigger followed by `5` or `2`. Bounds-guards inline
    on each arm so a malformed `\x1b[38;5m` (missing index)
    degrades to "ignore" rather than throw — hostile-input
    parity with audit-cycle SR-1's `MAX_PARAM_VALUE`
    clamps. Colon-separated sub-params (`38:5:n`,
    `38:2:r:g:b`) require parser-side support and are
    Stage 6 territory; tracked as a `// TODO Stage 6:
    colon-separated sub-params` comment in the walker.

  - **OSC 52 defensive comment** in `Apply`'s `OscDispatch`
    arm. No behaviour change — every OSC dispatch is still
    silently dropped — but the explicit arm with a
    SECURITY-CRITICAL long-form comment (mirroring SR-1
    TC-1's style at `Screen.fs` lines 201-214) makes the
    reasoning grep-able for future audits. `SECURITY.md`
    row TC-2 cross-references the new chokepoint.

  18 new tests in `tests/Tests.Unit/ScreenTests.fs` exercise
  every arm: DECTCEM toggle, DECTCEM-vs-non-private
  isolation, DECSC/DECRC round-trip, DECSC-saves-attrs,
  DECRC-on-empty-stack, multi-level DECSC LIFO, 256-colour
  Fg/Bg, truecolor Fg/Bg, Print-carries-Indexed-into-cell,
  malformed `38;5` doesn't throw, malformed `38;2;...`
  doesn't throw, mixed SGR with truecolor in the middle,
  OSC 52 bumps SequenceNumber but doesn't mutate cells.
  The existing `fresh screen cursor is at 0,0 visible` test
  was migrated from `screen.Cursor.Visible` to
  `screen.Modes.CursorVisible`.

  Companion PR: PR-B (alt-screen 1049 back-buffer) lands
  on top of this. The full Stage 4.5 plan is in
  `/root/.claude/plans/replicated-riding-sketch.md`.

- **`Ctrl+Shift+R` release-notes browser hotkey.** Press
  `Ctrl+Shift+R` (mnemonic: **R**eleases) from inside pty-speak
  to open the GitHub Releases page for the configured
  `UpdateRepoUrl` (today
  `https://github.com/KyleKeane/pty-speak/releases`) in the
  user's default web browser. NVDA narrates "Opened release
  notes in default browser: <url>". Useful as a one-keypress
  answer to "what changed in this version?" without leaving
  pty-speak. The browser's own accessibility surface handles
  the release-notes navigation; pty-speak just hands the URL
  to the OS shell. URL is derived from `UpdateRepoUrl` so a
  fork / self-hosted variant only needs to update one
  constant (Phase 2's TOML config will make `UpdateRepoUrl`
  user-configurable per `SECURITY.md` row C-1; this hotkey
  inherits whatever the user configures).

  Note on the `Ctrl+Shift+R` vs `Alt+Shift+R` (Stage 10
  review-mode toggle, reserved) mnemonic overlap: WPF treats
  the two as distinct `KeyGesture`s (different modifier
  sets), so there is no actual keypress conflict. The R-vs-R
  mnemonic similarity is the only cost; the maintainer chose
  Ctrl+Shift+R explicitly for the "R for Releases" parallel.

  The reserved-hotkey list now reads: `Ctrl+Shift+U` (update),
  `Ctrl+Shift+D` (diagnostic), `Ctrl+Shift+R` (release notes),
  `Ctrl+Shift+M` (Stage 9 mute, reserved), `Alt+Shift+R`
  (Stage 10 review, reserved). `SECURITY.md` row A-3 +
  "What we defend against" bullet updated to reflect; the
  reserved-hotkey list in `docs/USER-SETTINGS.md` Keybindings
  section also updated.

- **`Ctrl+Shift+D` diagnostic-launcher hotkey.** Press
  `Ctrl+Shift+D` from inside pty-speak to launch the bundled
  process-cleanup diagnostic (`scripts/test-process-cleanup.ps1`)
  in a separate PowerShell window. NVDA narrates "Diagnostic
  launched in a separate PowerShell window. Switch to that
  window to follow the test." The diagnostic auto-detects when
  the user closes pty-speak (no Enter prompt), reports
  PASS/FAIL plain-text output one-fact-per-line for screen-
  reader audible follow-along, and runs both close paths
  (Alt+F4 and X button). Added because Task Manager's
  Processes-tab chevron-expand affordance is not screen-
  reader-accessible, so the long-deferred Stage 4 process-
  cleanup test could not be exercised by an NVDA-using
  maintainer via the original Task Manager walkthrough.

  The script is bundled into the Velopack install via
  `Terminal.App.fsproj`'s `Content` include; the hotkey
  resolves the script path via `AppContext.BaseDirectory`
  so no install path is hardcoded. PowerShell is launched
  with `-NoExit` so the window stays open after the test
  completes; the user reads the output and closes that
  window manually.

  Future diagnostics (UIA peer health, ConPTY child status,
  version dump) can be added as additional bundled scripts
  reached either through the same hotkey via a sub-menu or
  via additional reserved hotkeys following the
  app-reserved-hotkey contract in `spec/tech-plan.md` §6.
  The reserved-hotkey list is now: `Ctrl+Shift+U` (update),
  `Ctrl+Shift+D` (diagnostic), `Ctrl+Shift+M` (Stage 9 mute,
  reserved), `Alt+Shift+R` (Stage 10 review, reserved).

  `SECURITY.md` row A-3 (pre-Stage-6 keyboard contract)
  updated to reflect the new app-reserved hotkey.

- **Audit-cycle PR-D: deferred-test burn-down.** Closes the
  largest test-coverage gap identified by the audit
  (SESSION-HANDOFF.md item 6) and validates that PR-C's
  `InternalsVisibleTo("PtySpeak.Tests.Unit")` wiring works
  end-to-end. Two new test files in `tests/Tests.Unit/`:

  - **`UpdateMessagesTests.fs`** — six unit tests for the
    Stage 11 update-failure announcement mapping. PR-D
    extracted the exception-to-message logic from
    `runUpdateFlow`'s catch block into a pure function
    `Terminal.Core.UpdateMessages.announcementForException :
    exn -> string` so the regression class that matters
    most (the user-visible NVDA announcement per failure
    class) is testable without standing up an
    `IUpdateManager` adapter to mock Velopack's concrete
    type. Tests cover all four branches
    (`HttpRequestException` → network message,
    `TaskCanceledException` → timeout message,
    `IOException` → disk message, catch-all → generic),
    plus two defensive ordering tests that fail loudly if
    a refactor accidentally moves the catch-all above the
    specific branches.

  - **`WordBoundaryTests.fs`** — fourteen unit tests for
    `TerminalTextRange`'s word-boundary helpers
    (`IsWordSeparator`, `WordEndFrom`, `NextWordStart`,
    `PrevWordStart`). PR-D changed the four helpers from
    `static member private` to `static member internal`
    so Tests.Unit can reach them via PR-C's
    `InternalsVisibleTo` declaration. The tests pin the
    "whitespace-only word boundaries (paths read as one
    word)" policy that PR #68 shipped — anyone tightening
    `IsWordSeparator` to include punctuation will fail
    these tests and have to update them deliberately.

  Companion changes: `src/Terminal.App/Program.fs`
  `runUpdateFlow` now calls
  `UpdateMessages.announcementForException` instead of
  inlining the match (no behaviour change, just relocation
  for testability). `tests/Tests.Unit/Tests.Unit.fsproj`
  gains `UseWPF=true` and a ProjectReference to
  `Terminal.Accessibility` (needed to resolve the WPF
  reference set transitively when reaching internal
  helpers in that assembly).

- **Audit-cycle PR-B: pre-Stage-5 architectural seams +
  Stage 6 spec ADR.** Two seams Stage 5+ contributors can
  plug into without rebuilding the foundation:

  1. **Parser-thread → UIA-peer notification channel.** New
     `ScreenNotification` discriminated union in
     `src/Terminal.Core/Types.fs` (`RowsChanged of int list
     | ParserError of string`). `compose` in
     `src/Terminal.App/Program.fs` constructs a bounded
     `Channel<ScreenNotification>` (256 capacity, DropOldest)
     and starts a consumer task that drains 1:1 onto the
     existing `TerminalSurface.Announce` raise path
     (PR #63). `startReaderLoop` now takes the channel
     writer and publishes one `RowsChanged` per applied
     event batch. Stage 5 inserts the coalescer (debounce
     ~200ms, hash dedup, single notification per coalesced
     batch) between the parser publish and the consumer
     without changing the channel contract. Bonus: the
     loop's previous `with | _ -> ()` exception swallow
     becomes `ParserError` publish — closes the
     cross-cutting "parser exceptions are silently
     swallowed" gap from the audit. The "ConPTY child
     failed to start" path also publishes `ParserError`
     so users hear about it via NVDA rather than staring
     at a silent terminal.

  2. **`PreviewKeyDown` routing stub on `TerminalView`.**
     New override in `src/Views/TerminalView.cs` plus a
     public `AppReservedHotkeys` static list. The list
     seeds with `Ctrl+Shift+U` (Stage 11 self-update,
     shipped) and documents future entries as code comments
     (`Ctrl+Shift+M` Stage 9, `Alt+Shift+R` Stage 10).
     The override checks each reserved hotkey first and
     leaves `e.Handled = false` so WPF's `InputBindings`
     on the parent window can process the gesture before
     any future PTY forwarding. No PTY forwarding happens
     today — that's Stage 6 — but the seam is in place so
     the contract is enforceable at review time when Stage
     6 lands.

  3. **`spec/tech-plan.md` §6 ADR amendment** (maintainer-
     authorised; immutable-spec exception). Adds an "App-
     reserved hotkey preservation contract" clause at the
     top of Stage 6 making the contract normative: Stage 6's
     keyboard layer MUST preserve every entry in
     `TerminalView.AppReservedHotkeys` and MUST NOT mark
     them `e.Handled = true`. The list and the spec clause
     are co-equal sources of truth; new app-level hotkeys
     append to both. Failure mode if violated is captured
     in the spec text (silent loss of app-level hotkeys).

  Companion PRs in the audit cycle: PR-A (docs truth-up,
  shipped); PR-C (hygiene cleanup — MSAA delete +
  InternalsVisibleTo + Stage 11 tests; queued).

- **`docs/UPDATE-FAILURES.md` — Stage 11 NVDA failure
  announcements reference.** Standalone reference doc
  cataloguing the structured failure announcements PR #66
  introduced (HttpRequestException → "cannot reach GitHub
  Releases", TaskCanceledException → "Update check timed
  out", IOException → "Update could not be written to
  disk", catch-all → "Update failed: ...", in-flight dedup
  → "Update already in progress", IsInstalled false →
  "Auto-update only available in installed builds"). Each
  entry has cause, what to do, and what NOT to interpret
  it as. Cross-linked from README, ARCHITECTURE.md, and
  this CHANGELOG.

- **`docs/USER-SETTINGS.md` — forward-looking catalog of
  hardcoded decisions that could become user-configurable
  later.** Covers six categories with full rationale per
  section: word boundaries (the maintainer-flagged
  immediate trigger; whitespace-only today, vim / UAX #29
  / per-context-with-hotkey as plausible future modes),
  visual settings (font, size, colors, palette), audio /
  earcons (mute, volume, style, device routing,
  spec-defined defaults), keybindings (currently flat;
  remappable + collision-detection candidate), update
  behaviour (channel selection, auto-check, auto-apply),
  and verbosity / NVDA narration (off / smart / verbose
  presets). Each section follows the same four-part shape
  (current state / why hardcoded now / what configurability
  would look like / implementation notes) so a future
  contributor designing the Phase 2 TOML config substrate
  has the candidate list and the rationale, not just the
  decisions.

  Also adds a "Process for adding a new setting"
  six-bullet workflow (substrate first, per-setting PR,
  default = current behaviour, validate input, document
  in this file, smoke-test row) and a "Reminder for
  contributors" close-out summarising the meta-rule.

- **`CONTRIBUTING.md` — new "Consider configurability when
  iterating" section.** Codifies the meta-rule: every PR
  that introduces a hardcoded constant or fixed behaviour
  pauses to ask whether it's a config candidate; if yes,
  pick the right default, ship it as a constant, AND
  update the candidate catalog in
  `docs/USER-SETTINGS.md`. Explicit obligations (1-4) and
  reviewer guidance ("request changes on PRs that
  introduce a clearly-config-shaped value without
  updating the catalog or noting it"). The rule's purpose
  is captured: not to make every PR a config-design
  exercise, but to keep the rationale and candidate list
  current as the project grows so the eventual
  config-loader has a complete catalog to expose.

- **`.github/PULL_REQUEST_TEMPLATE.md` gains a
  "Configurability check" checklist item** — single bullet
  that asks contributors to either update USER-SETTINGS.md
  or note "no candidate settings introduced" below. Matches
  the "Adding new manual tests" pattern from PR #58 — the
  template enforces the rule at PR-write time, not just
  review time.

- **README docs index entry** added pointing at
  USER-SETTINGS.md so contributors browsing the docs find
  the catalog.

- **Real word-navigation in `TerminalTextRange`.** Closes the
  Stage 4 follow-up logged after `v0.0.1-preview.22`'s smoke
  pass — previously `TextUnit.Word` degraded to `TextUnit.Line`
  in `ExpandToEnclosingUnit`, `Move`, and `MoveEndpointByUnit`,
  so NVDA+Ctrl+RightArrow read a whole row instead of a single
  word. Now the three navigation methods all branch on
  `TextUnit.Word` separately:

  - **Word boundaries**: `' '` (space, U+0020) and `\t` (tab,
    U+0009) are word separators. Punctuation is NOT a separator
    so `"C:\\Users\\test>"` reads as one word — matching how
    most terminal users mentally parse paths and prompts. A
    later stage with an SGR-aware tokenizer can refine this.
  - `WordEndFrom(rows, cols, r, c)` walks forward from `(r, c)`
    until it hits a separator or the document end; returns
    one-past-end. Crosses row boundaries so a word that wraps
    is one word.
  - `NextWordStart(rows, cols, r, c)` walks forward, skipping
    the rest of the current word and any separator run, landing
    on the first non-separator cell of the next word. Returns
    `(rowCount, 0)` if no further word exists.
  - `PrevWordStart(rows, cols, r, c)` walks backward through
    any separator run, then back to the start of the word
    before the original position. Returns `(0, 0)` at origin.
  - `ExpandToEnclosingUnit(Word)` snaps the range to the word
    at `Start`; if `Start` lands on a separator it advances to
    the next word. `Document` and `Character` cases unchanged;
    `Paragraph` and `Page` still degrade to `Line` because
    terminal output doesn't have well-defined paragraph or
    page semantics.
  - `Move(Word, n)` walks `n` word boundaries (forward if
    positive, backward if negative), then expands to the word
    at the new position. Returns the number of words actually
    moved (clamped at document boundaries).
  - `MoveEndpointByUnit(endpoint, Word, n)` moves only one
    endpoint by `n` word boundaries, with the existing
    endpoint-collision rule (range collapses if endpoints
    cross).

  After this, NVDA's word-navigation commands (`NVDA+Ctrl+LeftArrow`
  / `NVDA+Ctrl+RightArrow` on laptop layout) read individual
  words from the buffer instead of jumping line-by-line.
  Verification is via the manual smoke matrix's Stage-4 word
  navigation row; the FlaUI integration test from PR #59 still
  pins Line navigation against regression but doesn't yet
  exercise Word semantics specifically — that's added when we
  have deterministic test fixtures (Stage 5+).

- **Window title and accessibility name now include the running
  version.** `MainWindow.xaml.cs` reads
  `AssemblyInformationalVersionAttribute` (which carries the
  prerelease tag like `0.0.1-preview.26` because
  `System.Version` doesn't) and sets `Title = "pty-speak {version}"`
  + `AutomationProperties.Name = "pty-speak terminal {version}"`.
  NVDA+T now reads the version, so users can audibly confirm
  which build they're running — particularly important after
  Stage 11's `Ctrl+Shift+U` self-update so the post-restart
  announcement reflects the new version. Strips any
  `+commit-sha` deterministic-build trailer from the
  announcement to keep it clean. Closes the
  "version-suffix-missing" follow-up logged in
  `docs/SESSION-HANDOFF.md`.

- **Stage 11 — Velopack auto-update via `Ctrl+Shift+U`.** The
  running app can now self-update from GitHub Releases:
  pressing the keybinding fetches the next preview's delta-
  nupkg, downloads ~KB-sized binary diff, and restarts in-
  place via Velopack's `ApplyUpdatesAndRestart`. No
  SmartScreen prompt, no UAC, no installer dialog —
  replaces the standalone
  `scripts/install-latest-preview.ps1` bridge for in-place
  updates (the script stays useful for fresh installs and
  development-environment workflows).

  Implementation:

  - `src/Views/TerminalView.cs` gains a public `Announce`
    method that raises a UIA Notification event via
    `UIElementAutomationPeer.FromElement`. Uses
    `MostRecent` processing so a fast download doesn't
    flood NVDA's speech queue with stale percentages.
  - `src/Terminal.App/Program.fs` adds `runUpdateFlow` (the
    background-task orchestrator that calls Velopack's
    `UpdateManager` against `GithubSource(repoUrl, null,
    prerelease=true)` and announces each phase: "Checking
    for updates", "Downloading X.Y.Z",
    "N percent downloaded" coalesced to 25% buckets,
    "Restarting to apply update") and
    `setupAutoUpdateKeybinding` (which wires the
    `KeyBinding` via the Window's `InputBindings`).
    `compose` calls `setupAutoUpdateKeybinding window`
    before the window is shown so the gesture is live for
    the user's first keypress.
  - The `KeyBinding` lives in `Window.InputBindings`
    rather than the future Stage 6 PTY input pipeline, so
    app-level shortcuts capture the gesture before it
    reaches any keyboard router.
  - `mgr.IsInstalled` check announces a "use the install
    script for development copies" message for `dotnet
    run` paths so the keybinding fails gracefully in dev.
  - `updateInProgress` mutable flag dedupes repeat
    keypresses while a download is in flight.
  - Failure handling is currently a single
    "Update failed: <reason>" announcement; structured
    pattern-matching on Velopack exception types
    (`NetworkUnavailable`, `SignatureMismatch`) for distinct
    announcements is a later refinement.

- **`scripts/install-latest-preview.ps1` — one-command preview
  installer for Windows.** Downloads the latest (or specified)
  preview's `Setup.exe` from the GitHub Release assets, strips
  the Mark-of-the-Web tag with `Unblock-File` so SmartScreen
  doesn't prompt the unsigned-preview line on every iteration,
  and runs the installer. Replaces the multi-step "open the
  release page → navigate the asset list → click `Setup.exe`
  → click 'More info' → click 'Run anyway'" flow that takes
  several screen-reader steps per iteration with a single
  command. Scoped to the iterative-smoke-testing workflow that
  Stage 4+ NVDA verification needs; once Stage 11 ships
  Velopack delta self-update via `Ctrl+Shift+U`, this script
  becomes unnecessary for in-place updates. New `scripts/README.md`
  documents the script and reserves the directory for future
  utilities.

- **Comprehensive manual smoke-test matrix
  (`docs/ACCESSIBILITY-TESTING.md` rewrite).** Reframes the
  accessibility-only doc as the universal manual-validation gate
  every release must pass. Three new always-run sections cover
  artifact integrity (Velopack assets present, Setup.exe
  non-zero, Authenticode status, hash matches) and launch /
  process hygiene (single window, version in title, one-deep
  process tree, clean shutdown, no orphan `cmd.exe`,
  re-launch). Every per-stage table now has a "Diagnostic
  decoder" subsection that maps each possible failure to the
  likely-responsible subsystem — file paths and PR numbers
  where applicable, so a FAIL goes from "something broke" to
  "look at file X, PR Y" without further triage. New top-level
  sections "Adding new manual tests" (criteria, required
  fields, where in the matrix), "Sunset rules" (when a row
  graduates to CI or gets deleted), and "Coverage that moved
  to CI" (audit trail of the matrix's shrinking surface area
  as automated assertions land). The Stage 4 section is
  rewritten to reflect the actual ship architecture
  (`AutomationPeer.GetPattern` override after the WM_GETOBJECT
  pivot, not the original raw-provider plan), and notes that
  PR #56's `tests/Tests.Ui/TextPatternTests.fs` now CI-pins
  the producer side of the Text pattern. `docs/RELEASE-PROCESS.md`
  step 5 was rewritten to defer to the comprehensive matrix
  rather than carry its own minimal smoke list, and
  `docs/SESSION-HANDOFF.md` item 3 (the visual install smoke
  for the maintainer) now points at the matrix and tracks
  Stage 4 NVDA verification alongside the existing single-window
  check. The PR template's accessibility checklist gains an
  "Adding new manual tests" reminder so PRs that ship
  CI-unverifiable behaviour grow the matrix in the same change.
  README's docs-index entry is updated to reflect the broader
  scope.

- **Stage 4 PR C — UIA Text pattern via `AutomationPeer.GetPattern`
  override + FlaUI verification test
  (`tests/Tests.Ui/TextPatternTests.fs`).** PR C started as a
  pure verification test for PR B's WM_GETOBJECT raw-provider
  path, but two CI iterations on `windows-latest` revealed an
  architectural finding that changed the path entirely:

  - The first iteration showed UIA3 (which NVDA / Inspect.exe
    / FlaUI.UIA3 use) dispatches `WM_GETOBJECT` with
    `UiaRootObjectId` (-25), never `OBJID_CLIENT` (-4), so
    PR B's `OBJID_CLIENT`-only match never surfaced any
    patterns to UIA3 clients. Diagnostic from a 29-line
    log dump.
  - The second iteration extended the match to also handle
    `UiaRootObjectId`. That broke the entire UIA tree: every
    UI test regressed with either an HRESULT failure on
    `UIA3Automation.FromHandle` or "TerminalView descendant
    not found in the UIA tree." The root cause is that
    `WM_GETOBJECT(UiaRootObjectId)` expects a provider
    implementing `IRawElementProviderFragmentRoot` — UIA
    needs the fragment-navigation surface to traverse from
    the returned root into descendants, and our simple
    provider can't supply that even with
    `HostRawElementProvider` wired.
  - The third iteration found the actual right path:
    `AutomationPeer.GetPattern` is `public virtual` (unlike
    the unreachable `protected virtual GetPatternCore` that
    bit PR #48), so external assemblies CAN override it. The
    override adds the Text pattern to the SAME peer that's
    already in WPF's tree, leaving WPF's fragment navigation
    untouched. No `WM_GETOBJECT` interception of UIA3
    messages needed.

  What ships in PR C:

  - `TerminalAutomationPeer` gains an `ITextProvider`
    constructor parameter and a `GetPattern` override that
    returns it for `PatternInterface.Text`, deferring to
    `base.GetPattern` for every other interface.
  - `TerminalView.OnCreateAutomationPeer` passes the
    `TextProvider` it constructed in PR B through to the
    peer.
  - `WindowSubclassNative` reverts to matching `OBJID_CLIENT`
    only — kept as a defensive MSAA fallback rather than the
    primary UIA path. The `UiaRootObjectId` constant is
    documented in the source as the discovery that drove the
    pivot.
  - `tests/Tests.Ui/TextPatternTests.fs` walks the UIA tree
    for the first element exposing the Text pattern, calls
    `DocumentRange.GetText(-1)`, and asserts the result has
    the expected minimum length (30 rows × 120 cols + 29
    row-joining newlines = 3629 chars for the
    `Program.compose` default screen size) plus at least one
    `\n`. Specific cell content from cmd.exe's banner is
    deliberately not asserted — banner wording isn't
    deterministic across Windows builds.
  - Test failure messages dump the WM_GETOBJECT log and the
    visible pattern flags so a future regression diagnoses
    itself without further iteration. That diagnostic
    machinery is what made the architectural finding
    tractable in the first place; it stays in place.

- **Stage 4 PR B — Text-pattern provider scaffolding +
  WM_GETOBJECT raw-provider path (legacy MSAA fallback).**
  `Terminal.Accessibility` gains real `TerminalTextProvider`
  and `TerminalTextRange` types whose `DocumentRange.GetText`
  returns a `\n`-joined render of the current
  `Screen.SnapshotRows` capture. `TerminalView` exposes the
  provider as a public `TextProvider` property; PR C wires it
  into the UIA peer's `GetPattern` override (the actual UIA3
  surface). The PR also ships a `Views/TerminalRawProvider.cs`
  implementing `IRawElementProviderSimple` and extends the
  `WindowSubclassNative` hook from PR A (#54) to return that
  provider for `WM_GETOBJECT(OBJID_CLIENT)` queries. PR C's
  CI iteration revealed UIA3 never queries `OBJID_CLIENT` —
  it uses `UiaRootObjectId` instead, which can't be
  intercepted with a simple provider — so the raw-provider
  path is kept as a defensive fallback for legacy MSAA
  clients only, not as the primary UIA path. Stage 4
  navigation (`Move`, `MoveEndpointByUnit`, attribute
  exposure) is still stubbed; the per-cell SGR exposure
  arrives in a later stage.

  The previously throwaway `Terminal.Accessibility/RawProviderSpike.fs`
  is removed — the foundation finding it captured (F# can
  implement `IRawElementProviderSimple`) is now demonstrated by
  the production code.

- **FlaUI integration test infrastructure
  (`tests/Tests.Ui/AutomationPeerTests.fs`).** First UIA test in
  the project: launches `Terminal.App.exe` from the build
  output, attaches via `UIA3Automation`, finds the `TerminalView`
  descendant by `ClassName`, and asserts `ControlType=Document`
  and `Name="Terminal"`. `Tests.Ui.fsproj` gains
  `FlaUI.Core` / `FlaUI.UIA3` package references and a
  `ReferenceOutputAssembly="false"` ProjectReference to
  `Terminal.App` so MSBuild builds the app before the test runs
  without linking its outputs into the test bin. The test is
  scoped to validate that PR #48's reduced-scope peer actually
  works at runtime (the `TerminalView` element is reachable via
  UIA with the expected role and identity) and to give any
  future Text-pattern attempt an automated verification harness
  before merging. This is the foundation piece that any further
  Stage 4 work — raw `IRawElementProviderSimple` provider,
  reflection-based binding, or anything else — needs in place.

- **Stage 4a (reduced scope) — UIA Document role + identity.**
  `TerminalView` now exposes a `TerminalAutomationPeer` via
  `OnCreateAutomationPeer`. UIA clients (NVDA, Inspect.exe) find
  the terminal element in the automation tree with
  `ControlType=Document`, `ClassName="TerminalView"`,
  `Name="Terminal"`, `IsControlElement=true`, and
  `IsContentElement=true`. The peer subclasses
  `FrameworkElementAutomationPeer` and overrides only the five
  parameterless `*Core` methods that the spike (PR #47) confirmed
  compile cleanly from F# under `Nullable=enable`.

  **What this PR deliberately does NOT ship:** the Text pattern
  (`ITextProvider` / `ITextRangeProvider`), navigation (`Move`,
  `MoveEndpointByUnit`), and SGR attribute exposure
  (`GetAttributeValue`). All three depended on overriding
  `AutomationPeer.GetPatternCore`, which CI iteration on this PR
  established is not reachable from any external assembly in the
  .NET 9 WPF reference assembly set. C# CS0117 fires on
  `base.GetPatternCore(...)` with "FrameworkElementAutomationPeer
  does not contain a definition for 'GetPatternCore'"; F# FS0855
  on `override _.GetPatternCore(...)` is the same finding via a
  different error code. Microsoft's documented examples that
  override `GetPatternCore` evidently compile only against
  internal Microsoft assemblies where the protected member is
  visible; the public reference assembly surfaces the type
  without the override target.

  Text-pattern exposure is therefore deferred to a follow-up
  Stage 4 PR. The likely path is implementing
  `IRawElementProviderSimple` directly on `TerminalView`,
  bypassing the `AutomationPeer` hierarchy that wraps the
  unreachable protected metadata. Investigation continues with
  focused effort rather than CI iteration; tracked in
  `docs/SESSION-HANDOFF.md` Stage 4 sketch.

- **README addition: "The complexities of trying to work with
  technology as a blind developer."** Records the maintainer's
  account of why this project exists, the iOS Claude Code
  workaround (disable VoiceOver, place finger by remembered
  pixel location, re-enable VoiceOver) used to interact with
  Claude on every message of this session, and an idiom
  (`blindly iterating`) the model produced in this session that
  is not literally accurate and casually demeans the people whose
  working conditions this project is being built to improve. The
  preferred phrasing is the literal one — `iterating without
  information`, `speculative iteration`, `guessing without
  evidence` — because it communicates more precisely and removes
  the sight-as-knowledge metaphor.

- **Stage 4 spike — F# AutomationPeer + ITextProvider /
  ITextRangeProvider interop probe.** `Terminal.Accessibility`
  gains `TerminalAutomationPeer.fs` replacing the empty
  `Placeholder.fs`. The spike subclasses
  `FrameworkElementAutomationPeer`, defines stub
  `TerminalTextProvider` and `TerminalTextRange` types
  implementing the C# UIA provider interfaces, and overrides the
  five Core methods (`GetAutomationControlTypeCore`,
  `GetClassNameCore`, `GetNameCore`, `IsControlElementCore`,
  `IsContentElementCore`). Every method is a no-op; PR 4a wires
  `GetText` to the real `Screen` snapshot and PR 4b implements
  navigation. The fsproj gains `<UseWPF>true</UseWPF>` so
  WindowsBase / PresentationCore / UIAutomationProvider /
  UIAutomationTypes are resolvable. **No source-level wiring**:
  `TerminalView.OnCreateAutomationPeer` is unchanged in this PR;
  the peer is reachable as a type but never instantiated yet, so
  there's no behavior change for the running app. The spike's
  purpose is to surface F#-meets-C# interop foot-guns (analogous
  to the `out SafeFileHandle&` byref bug from Stage 1) before
  building 250+ lines of dependent code on top.

- **Parser test coverage for SUB / OSC ST / DCS CAN / Unicode
  round-trip.** `tests/Tests.Unit/VtParserTests.fs` gains four new
  cases: SUB (0x1A) cancellation in CSI mirroring the existing CAN
  test; ST-terminated OSC asserting `bellTerminated=false` plus the
  trailing bare `EscDispatch` for the `\` byte; CAN inside DCS
  passthrough emitting `DcsHook` + `DcsPut`* + `DcsUnhook` (note the
  asymmetry with CSI — CAN there emits `Execute`, here it emits
  `DcsUnhook`); and an FsCheck property that any valid Unicode
  scalar encoded as UTF-8 round-trips through the parser as a
  single `Print` event with the same rune.
- **Velopack artifact-existence gate in `release.yml`.** A new
  PowerShell step after `vpk pack` asserts that `*Setup.exe` and
  `*-full.nupkg` exist under `releases/`. Defense-in-depth on top
  of `vpk pack`'s own exit code: a future Velopack version that
  renames an artifact would otherwise produce a green workflow
  whose release ships without the file the auto-update client
  expects (because softprops is configured with
  `fail_on_unmatched_files: false` so the delta nupkg pattern can
  legitimately match nothing on first releases).
- **Stage 4 substrate — `Screen.SequenceNumber` + `Screen.SnapshotRows`
  in `Terminal.Core`.** `Screen` now exposes a monotonic
  `SequenceNumber: int64` (incremented on every `Apply`) and a
  `SnapshotRows(startRow, count): int64 * Cell[][]` method that
  atomically captures an immutable copy of the requested rows
  paired with the sequence number at capture time. Both `Apply` and
  `SnapshotRows` serialize on a private gate object, which is the
  boundary between the WPF Dispatcher (where the parser feeds
  events) and the UIA RPC thread (where Stage 4's
  `ITextRangeProvider` will read snapshots from). This is the
  thread-safety primitive that spec §4.3's snapshot-on-construction
  rule depends on; landing it ahead of the UIA peer keeps the
  Stage 4 PR focused on the peer + provider implementation.
  `tests/Tests.Unit/ScreenTests.fs` covers fresh-screen baseline,
  per-event sequence increments, deep-copy independence, sequence-
  pairing, argument validation, the `count = 0` degenerate, and a
  concurrent producer / snapshot stress test.

### Changed

- **Spec formalization: Stage 5a — Diagnostic logging surface.**
  Per chat 2026-05-03 maintainer authorization, the post-Stage-6
  diagnostic-logging cycle is now formally documented in
  `spec/tech-plan.md` as **Stage 5a**. Same letter-suffix
  convention as Stages 4a and 4b. Sub-sections cover:

  - **5a.1** `FileLogger.fs` structured-logging substrate
    (off-thread `Channel<LogEntry>` drain, `Microsoft.Extensions.Logging.Abstractions`
    contract, retention, `PTYSPEAK_LOG_LEVEL` env var, "never log
    secrets" call-site discipline).
  - **5a.2** Per-session files in per-day folders
    (`pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log` per Issue #107;
    `FileLoggerSink.ActiveLogPath` accessor).
  - **5a.3** `Ctrl+Shift+L` open-logs hotkey + announce-before-launch
    pattern (parallel to Stage 4b).
  - **5a.4** `Ctrl+Shift+;` copy-active-log hotkey + `FileShare.ReadWrite`
    matching the writer's policy + hotkey-choice rationale
    (Magnifier collision avoidance, SystemKey-unwrap → Alt+F4
    breakage, US-layout physical proximity to `L`).
  - **5a.5** `FileLoggerSink.FlushPending(timeoutMs)` TCS-barrier
    so the copy hotkey captures up-to-the-moment state.
  - **5a.6** Validation matrix (xUnit + manual NVDA).
  - **5a.7** Post-Stage-5/6 streaming-pipeline diagnostics
    (PRs #109/#111/#114/#116) — the cycle the diagnostic
    logging surface itself enabled.

  `docs/ROADMAP.md` gains a Stage 5a row between Stage 6 and
  Stage 7 (matches shipping order — work began after Stage 6
  PR-B #99 / fixup #100 and continued through the post-Stage-6
  streaming-pipeline-fix PRs plus this session's Issue #107 +
  FlushPending refinements).

  No prose alignment needed in other docs because user-facing
  references use the hotkey names (`Ctrl+Shift+L`,
  `Ctrl+Shift+;`) and module name (`FileLogger.fs`) as pointers
  rather than a stage number.

  This completes the three-PR spec-stage-numbering chunk
  authorized in chat 2026-05-03 (Stage 4a + Stage 4b + Stage 5a
  all formally documented in the spec).

- **Spec formalization: Stage 4b — Process-cleanup diagnostic.**
  Per chat 2026-05-03 maintainer authorization, the `Ctrl+Shift+D`
  diagnostic-launcher work (PR #81) is now formally documented in
  `spec/tech-plan.md` as **Stage 4b**. Same letter-suffix
  convention as the prior Stage 4a spec edit; same Stage 3a/3b
  precedent. Sub-sections cover hotkey + script bundling,
  announce-before-launch pattern (700ms `Task.Delay` so NVDA's
  speech queue plays the cue before the spawned conhost steals
  focus), validation matrix, and the documented known limitation
  (conhost NVDA reading is unreliable; in-pty-speak rework is
  deferred per SESSION-HANDOFF item 6 and now actionable since
  Stage 6 shipped). `docs/ROADMAP.md` gains a Stage 4b row
  between Stage 11 and Stage 4a in shipping order;
  `docs/SESSION-HANDOFF.md` item 6 gains a §4b cross-link so
  future sessions land on the spec section directly.

  No other prose alignment needed because user-facing references
  to this work use the `Ctrl+Shift+D` hotkey name as the
  pointer rather than a stage number.

  Companion Stage 5a (diagnostic logging surface) ships next per
  the same chat 2026-05-03 authorization.

- **Spec formalization: Stage 4a — Claude Code rendering substrate.**
  Per chat 2026-05-03 maintainer authorization, the post-Stage-4
  rendering-substrate work (alt-screen 1049, DECTCEM cursor visibility,
  256/truecolor SGR, DECSC/DECRC, OSC 52 silent drop, `TerminalModes`
  record + private-CSI / ESC dispatch substrate) is now formally
  documented in `spec/tech-plan.md` as **Stage 4a**. Letter-suffix
  naming follows the existing Stage 3a/3b precedent and avoids
  collision with the `### 4.5` NVDA validation sub-section of
  Stage 4. `docs/ROADMAP.md` gains a Stage 4a row; forward-looking
  references in `docs/SESSION-HANDOFF.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`,
  `docs/ACCESSIBILITY-TESTING.md`,
  `docs/CHECKPOINTS.md`, and
  `docs/PROJECT-PLAN-2026-05.md` realign from "Stage 4.5" to
  "Stage 4a". Historical CHANGELOG entries from when the work
  shipped (PR-A #85, PR-B #86) keep their original "Stage 4.5"
  labels as release-notes-shaped artifacts of the moment they
  shipped.

  Companion Stage 4b (process-cleanup diagnostic) and Stage 5a
  (diagnostic logging surface) ship as separate PRs per the
  same chat 2026-05-03 authorization.

- **Per-session log filenames now use full date+time + millisecond
  tie-breaker** ([#107](https://github.com/KyleKeane/pty-speak/issues/107),
  Option A). Filename scheme moves from
  `pty-speak-HH-mm-ss.log` to
  `pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log`; day folders stay
  `yyyy-MM-dd`. Two motivating concerns:

  1. **Self-describing when extracted.** The old filename
     dropped the date because the day-folder carried it; the
     moment a user copied a single log out of its folder
     (email attachment, paste into a bug report, drag into a
     chat) it lost its date context and became hard to
     correlate with the session it described. Embedding the
     full date in the filename keeps it self-describing
     anywhere it lands.

  2. **Uniqueness when the second tier collides.** Two
     launches in the same UTC second produced identical
     filenames under the old scheme; Issue #107's three
     candidate tie-breakers (millisecond suffix, short UUID,
     incremental counter) settled on milliseconds via Option
     A — alphabetical sort still equals chronological sort,
     no UUID-readability cost, no concurrent-counter retry
     code path. Two launches inside the same millisecond
     remain a theoretical collision but are vanishingly
     unlikely for human-launched terminal sessions.

  Affected code: `src/Terminal.Core/FileLogger.fs`'s
  `pathsForLaunch ()` (one-line format-string change) plus
  the doc-comment file-layout example. Tests:
  `tests/Tests.Unit/FileLoggerTests.fs` gains a
  `assertSessionFilenameFormat` helper that parses the
  filename through `DateTime.TryParseExact` against the new
  format string; the two existing tests
  (`active log lives inside a day-folder named yyyy-MM-dd`
  and `ActiveLogPath member exposes the per-session file
  inside today's day-folder`) tighten via the helper, plus
  a new test
  (`session filename uses yyyy-MM-dd-HH-mm-ss-fff format per
  Issue #107`) pins the parsed timestamp within 5 seconds of
  `DateTime.UtcNow` so the format reflects the launch
  instant rather than random digits. `docs/LOGGING.md`
  example tree updated. `tests/Tests.Unit/FileLoggerTests.fs`
  retention-sweep test fixtures use derived filenames
  (`pty-speak-{stale-or-fresh-yyyy-MM-dd}-12-00-00-000.log`)
  so the placeholder filenames match their day-folder for
  readability.

- **Restored two strategic INFO log entries that PR #111
  over-demoted.** Coalescer "Emit OutputBatch (leading-edge
  | trailing-edge)" and `TerminalView.Announce`
  "RaiseNotificationEvent firing" are back at `Information`.
  These are bounded by the coalescer's 200ms debounce
  (~5 events/sec at typing speed; far below any I/O lag
  threshold) and constitute the primary "is the streaming
  pipeline alive?" signal at default log level — without
  them, default logs show nothing of the streaming path,
  and a streaming-silence bug requires the user to launch
  with `PTYSPEAK_LOG_LEVEL=Debug` to capture any trace at
  all. The other PR #109 entries (reader publish, suppress,
  accumulate, drain dispatch) stay at `Debug` to keep the
  steady-state volume low; flip to Debug for full-chain
  diagnosis when needed.

- **Log-copy hotkey rebound from `Ctrl+Alt+L` to
  `Ctrl+Shift+;`** (the semicolon / colon key, immediately
  to the right of `L` on a US-layout keyboard).
  Maintainer-reported regressions on the post-#109 preview
  drove the move:

  1. `Ctrl+Alt+L` collides with the **Windows Magnifier**
     zoom-in shortcut on some default Magnifier configs;
     the OS swallowed the gesture before pty-speak saw it.
  2. The original fix for `Ctrl+Alt+L` not firing (PR #108)
     introduced a `SystemKey`-aware filter at the top of
     `OnPreviewKeyDown` so that `Alt`-modified gestures (which
     WPF reports as `e.Key == Key.System` + `e.SystemKey ==
     Key.L`) were unwrapped to the underlying key. Side
     effect: `Alt+F4` was unwrapped to `Key.F4 + Alt`, the
     encoder produced bytes, `e.Handled` became `true`, and
     the OS window-close gesture stopped working.

  `Ctrl+Shift+;` is a clean Ctrl+Shift gesture: no Alt path,
  no Magnifier collision, no SystemKey unwrap needed in the
  filter chain. Removing the SystemKey unwrap restored
  `Alt+F4` because `Key.System` falls through to
  `KeyCode.Unhandled`, the encoder returns null, `e.Handled`
  stays false, and WPF's default close handler fires.

  Mnemonic: physical proximity. The semicolon / colon key
  sits right next to `L`, so `Ctrl+Shift+L` (open the logs
  folder) and `Ctrl+Shift+;` (copy the active session log)
  live under one hand position. `Ctrl+Shift+C` was
  considered as the natural "copy" mnemonic but reserved
  for a future copy-latest-command-output feature (the
  cross-terminal convention for that gesture). `Ctrl+Shift+M`
  was considered but stays reserved for the Stage 9 earcon
  mute toggle. Layout caveat: on non-US keyboards the
  `OemSemicolon` virtual-key sits in a different physical
  position; remap support is on the Phase 2 user-settings
  roadmap.

  Updated everywhere it was documented: README,
  `docs/LOGGING.md`, `docs/USER-SETTINGS.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`, the
  `AppReservedHotkeys` table in `TerminalView.cs`, the
  `setupCopyLatestLogKeybinding` wiring in `Program.fs`, and
  the `HandleAppLevelShortcut` direct-dispatch path. The
  `Ctrl+Shift+L` open-folder primary is unchanged.

- **Streaming-path instrumentation demoted from `Information`
  to `Debug`** so the production default sees no per-frame
  log I/O. The PR #109 instrumentation at typing speed
  produced ~25 entries/second across all stages, which
  manifested as visible WPF dispatcher lag during streaming
  output. Demoting the per-event entries (reader publish,
  coalescer suppress / accumulate / emit, drain dispatch,
  peer-present raise) leaves the trail intact for diagnosis
  — set `PTYSPEAK_LOG_LEVEL=Debug` before launch to capture
  the full chain — but keeps the steady-state log silent.
  The peer-NULL `WARN` stays at `WARN` (rare, and the
  smoking-gun signal that a UIA client never connected and
  notifications are silently dropping). One-time entries
  (runLoop start, cancellation, hotkey invocations) stay at
  `Information`.

- **Logging restructured to per-session files in per-day
  folders.** The previous layout kept one daily-rolled file
  per UTC day; long-running development days produced massive
  aggregated files that were painful to navigate when grabbing
  a slice for a bug report. New layout (filename refined per
  Issue #107 — see the matching Changed entry):

  ```
  %LOCALAPPDATA%\PtySpeak\logs\
  ├── 2026-05-02\
  │   ├── pty-speak-2026-05-02-13-45-23-189.log    ← session that launched at 13:45:23.189 UTC
  │   ├── pty-speak-2026-05-02-15-12-08-401.log
  │   └── pty-speak-2026-05-02-16-30-44-027.log
  ├── 2026-05-01\
  │   └── pty-speak-2026-05-01-09-15-22-318.log
  └── ... (up to 7 days)
  ```

  Each launch creates a fresh session file named with its
  full launch timestamp inside today's day-folder. Sessions
  don't split across midnight (a long-running session stays
  in its launch-day folder). Retention deletes whole
  day-folders older than 7 days; folders with non-date names
  are ignored defensively. New `FileLoggerSink.ActiveLogPath`
  member exposes this session's file path for tools that
  want to grab the active session directly.

  `Ctrl+Shift+L` still opens the logs root; the user
  navigates one click into today's day-folder and picks the
  most recent session by alphabetical sort. Bug reports are
  now one-file pastes instead of "scroll a giant log to the
  right time range".

  `docs/LOGGING.md` updated with the new layout, retention
  rules, and a one-line PowerShell snippet for grabbing the
  latest session — useful for the future
  Claude-Code-on-the-machine workflow where a script could
  pull the most recent log without prompting the user.

- **`Ctrl+Shift+R` flipped from "open releases page" to "open
  draft-a-new-release form".** The original PR #83 hotkey opened
  `UpdateRepoUrl + "/releases"` (the listing). During post-Stage-5
  manual NVDA verification on the just-cut preview, the maintainer
  realised the daily-use path during the preview line is creating
  a release (publishing in the GitHub Releases UI triggers the
  Velopack build/upload workflow per `docs/RELEASE-PROCESS.md`),
  not browsing existing releases. Flipping the URL to
  `/releases/new` makes the hotkey a one-keypress shortcut to the
  cadence step that matters every preview cut. Mnemonic stays "R
  for **R**elease".

  Renames that follow the behaviour change:

  - `Program.fs runOpenReleases` → `runOpenNewRelease`
  - `Program.fs setupReleasesKeybinding` → `setupNewReleaseKeybinding`
  - `RoutedCommand("OpenReleases", ...)` → `"OpenNewRelease"`
  - `Terminal.Core.ActivityIds.releases` (`"pty-speak.releases"`)
    → `ActivityIds.newRelease` (`"pty-speak.new-release"`).
    The activity-ID rename is a soft breaking change for any NVDA
    user who already configured per-tag handling for the old
    string, but Stage 5's tag vocabulary just shipped on the
    preceding preview and is documented to accept renames until
    v0.1.0+.
  - Announce text: "Opened release notes in default browser:
    {url}" → "Opening new release form."
  - Doc updates: `README.md`, `SECURITY.md` (A-3 row + the
    pre-Stage-6 keyboard contract paragraph), `docs/USER-SETTINGS.md`.

  No hotkey contract change from the user's perspective; same
  `Ctrl+Shift+R`, different (more useful) URL.

- **SESSION-HANDOFF item 2 step 3 closed.** Post-Stage-5
  process-cleanup re-run via `Ctrl+Shift+D` on the post-Stage-5
  preview returned PASS for both close paths (Alt+F4 and
  X-button) per the maintainer's manual NVDA verification.
  Item 2 step 3 flips from "↻ pending" to "✓ PASS"; step 4
  ("After Stage 6 ships") is now the next pending pass.

- **`docs/SESSION-HANDOFF.md` item 2 truth-up.** The
  process-cleanup baseline test (Step 1 of the recurring
  cadence) was actually run via `Ctrl+Shift+D` on
  `v0.0.1-preview.27` during the post-Stage-4.5 hygiene
  session — both close paths PASSED, no orphans. The doc
  still framed the baseline as future tense ("next
  manual session — establishes whether the shipped code
  already has issues"); updated to reflect "✓ Baseline on
  `v0.0.1-preview.27` — PASS (2026-05-01)" plus a
  cross-reference to item 6 (the screen-reader-native
  replacement work, since NVDA's coverage of the spawned
  PowerShell window is the documented limitation; the
  underlying script's PASS/FAIL output is the source of
  truth). Step 2 ("After Stage 4.5 PR-B ships") is the
  next pending pass, since `v0.0.1-preview.28+` now carry
  the alt-screen back-buffer.

  No code paths touched.

- **Repo-hygiene cleanup (post-Stage-4.5 sweep).** Two
  small documentation fixes, a future-proofing convention
  added to `CONTRIBUTING.md`, and a one-time cleanup
  script for the maintainer to run on their workstation:

  - `docs/USER-SETTINGS.md` Keybindings section: corrected
    "Four app-level keybindings shipped today" to "Three"
    (the bullet list correctly enumerates three shipped
    `Ctrl+Shift+U/D/R` plus two reserved `Ctrl+Shift+M`,
    `Alt+Shift+R`; the prose count was off by one).

  - `CONTRIBUTING.md` Branching and pull requests: new
    bullet documenting the post-merge convention to
    delete the source branch (both remote and local).
    The repo had accumulated 75+ stale post-merge
    branches over the project's history; the codified
    convention prevents recurrence.

  - `scripts/cleanup-stale-branches.sh`: bundled
    maintainer-side script that deletes the 77 accumulated
    stale post-merge branches in one go. The agent
    sandbox cannot delete remote refs (proxy returns
    HTTP 403 on `git push --delete`), so this is a
    one-time maintainer action. Idempotent
    (`git ls-remote --exit-code` check skips branches
    that have already been deleted). The script can be
    deleted from the repo after the one-time cleanup
    finishes.

  - `docs/SESSION-HANDOFF.md` "Pending action items"
    item 7 tracks the cleanup-script run as a
    maintainer-side action.

  No code paths touched.

- **Audit-cycle PR-E: cache `~/.dotnet/tools` across CI
  runs.** Both `.github/workflows/ci.yml` (Build and test
  job) and `.github/workflows/release.yml` (release-pack
  job) now cache the global dotnet tools directory before
  `dotnet tool install -g vpk`. The install step gates on
  `cache-hit != 'true'` so a cached run skips the install
  entirely. Saves ~10s per CI run. Cache key is statically
  versioned (`v1`); bump to `v2` when a new vpk version is
  wanted (the cache key change forces a fresh install,
  which pulls latest from NuGet, then re-caches).

  Two other CI optimisations from SESSION-HANDOFF item 3
  investigated and **deferred**: merging the two
  `gaurav-nelson/github-action-markdown-link-check` steps
  into one invocation (the action doesn't support both
  `folder-path` and `file-path`; combining would either
  drop the `spec/` exclusion or require enumerating 14
  files explicitly that would drift); release.yml audit
  for vpk-pack input cache (per-build artefacts have no
  cache opportunity) and gh-download 5xx retry (no flakes
  observed yet, defer until a flake happens). Both
  trade-offs are documented in `docs/SESSION-HANDOFF.md`
  item 3 so a future contributor doesn't redo the
  investigation.

- **Audit-cycle PR-D: SESSION-HANDOFF.md cleanup.** Item 2
  (Re-enable the GitHub MCP server) removed as obsolete —
  the MCP has been working reliably for the last ~14 PRs
  in this session, the original "occasionally disconnects
  mid-session" concern has not recurred. Items 3-5
  renumbered to 2-4. Item 6 (Stage 11 `runUpdateFlow`
  test coverage) removed as shipped via this PR.

- **Audit-cycle PR-C: tightened `Terminal.Accessibility` API
  surface via `internal` + `InternalsVisibleTo`.**
  `TerminalAutomationPeer`, `TerminalTextProvider`,
  `TerminalTextRange`, and the `SnapshotText` module are now
  marked `internal` (were public by F# default). Two
  `[<assembly: InternalsVisibleTo>]` declarations grant access
  to `PtySpeak.Views` (the C# WPF library that constructs
  the peer in `TerminalView.OnCreateAutomationPeer`) and
  `PtySpeak.Tests.Unit` (so future Stage-5+ unit tests can
  reach into the accessibility types without re-exposing them
  publicly). `TerminalView.TextProvider` lowered from `public`
  to `internal` to match its now-internal type.

  Net effect: Stage 5+ contributors have the freedom to
  break these signatures without an external breaking-change
  concern. If the project ever publishes `Terminal.Accessibility`
  as a NuGet for third parties, the `internal` becomes the
  stable contract and we promote a curated subset to `public`
  intentionally.

- **Audit-cycle PR-C: Stage 11 `runUpdateFlow` test coverage
  scoped out of this PR; logged in
  `docs/SESSION-HANDOFF.md` item 6 as a focused follow-up.**
  The audit identified `runUpdateFlow` (~80 lines, three
  exception branches) as the largest untested surface in
  the codebase. The cheapest test approach needs an
  `IUpdateManager` adapter wrapping Velopack's concrete
  `UpdateManager` class — adapter scaffold big enough to
  warrant its own PR. SESSION-HANDOFF item 6 captures the
  recommended approach (full adapter OR a simpler
  pure-function extraction of the exception-to-message
  mapping) so the next contributor doesn't have to
  reverse-engineer the design decision.

- **Audit-cycle PR-A: documentation truth-up after Stage 4 +
  Stage 11 verification.** Three CRITICAL doc errors fixed
  in one focused PR: `README.md`'s status block referenced
  `v0.0.1-preview.15` and described "next preview will show
  live cmd.exe output" (was Stage 3 era language); now
  reflects Stages 0-4 + 11 shipped on `v0.0.1-preview.26`
  with NVDA verification. `docs/ROADMAP.md` Stage 11 row
  marked "shipped" instead of "next"; "Stage ordering"
  subsection rewritten to past tense. `docs/ARCHITECTURE.md`
  module table: `Terminal.Accessibility` row updated from
  "placeholder" to "implemented (4)" with the actual type
  surface; the `Terminal.Update *(future)*` row replaced
  with a row pointing at the actual `runUpdateFlow` location
  in `Terminal.App/Program.fs` (per walking-skeleton
  discipline, kept in the composition root).

  Bundled MEDIUM/LOW doc fixes: `docs/SESSION-HANDOFF.md`
  "from this point forward" phrasing replaced with
  "deprecated for in-place updates"; next-stage pointer
  updated to call out the PR-B notification-channel seam
  Stage 5 will plug into. `CONTRIBUTING.md` USER-SETTINGS
  cross-reference strengthened with explicit reviewer-block
  rule. `docs/USER-SETTINGS.md` gains an "Intentionally not
  user-configurable" subsection covering parser limits
  (alacritty/vte parity rationale) and earcon
  frequency/duration defaults (evidence-based from
  accessibility research; not arbitrary).

- **`SECURITY.md` rewritten with a comprehensive auto-update
  threat model and a consolidated vulnerability inventory.**
  Stage 11's auto-update flow added a new attack surface
  (network-fetch + execute) that wasn't analysed in the
  previous SECURITY.md. The maintainer asked for "every
  single vulnerability" and the known mitigations or
  forward-mitigation paths to be documented end-to-end. The
  rewrite:

  - **New section "Auto-update threat model"** enumerating
    nine threat classes (T-1 through T-9): passive observation,
    active MITM substitution, GitHub account compromise, CI
    runner / supply-chain attack, replay / downgrade, LPE via
    the update path, time-of-check vs time-of-use during apply,
    resource exhaustion, and Velopack log info-disclosure.
    Each class has Risk / Severity / Mitigation today / Future
    mitigation columns spelled out, with explicit references to
    the protections shipped in PRs #44, #63, #64, #65, #66.
    Includes a chain-of-trust diagram showing where each link
    can fail.
  - **New section "Vulnerability inventory"** consolidating
    every threat class in the document into a single table
    (terminal core, process / OS, update path, build and
    supply chain — 24 rows total). Each row has the threat
    ID, severity, mitigation today, what closes the gap at
    `v0.1.0+`, and shipping status. Severity and status
    glossaries make the table self-contained.
  - **"How to use this inventory"** subsection describing the
    contributor workflow: PRs that touch a protection class
    must update both the affected row and the narrative
    section. Reviewers are told to request changes on PRs
    that weaken a protection without updating SECURITY.md.
  - **Cross-link** from the existing "What we defend against"
    section to the new inventory so a contributor reading
    top-down lands on both the narrative and the audit-table
    view.

  No code changes; this is the documentation pass that
  captures the security state we've actually been shipping
  through the past several PRs.

- **`docs/RELEASE-PROCESS.md` step 3 rewritten with explicit
  CLI vs UI paths and target-branch failure recovery.**
  `v0.0.1-preview.23` was burned by a UI-path publish with the
  Target dropdown still pointing at a stale feature branch
  (`fix/stage-4-text-pattern-navigation`); the workflow's
  target-branch gate caught it correctly and failed fast, but
  the docs didn't make the failure mode prominent enough to
  prevent the recurrence (`v0.0.1-preview.14` was the first time
  this happened). The rewrite:

  - Splits step 3 into "3a CLI path (recommended)" and "3b UI
    path." The CLI path is recommended for screen-reader users
    because it's a single keyboard-driven command vs the UI's
    multi-step dropdown navigation.
  - Bolds and elaborates the "`--target main` is not optional"
    warning on the CLI command, with the explicit failure mode
    (gh uses your local checkout's current branch as the target
    if you don't pass `--target`).
  - Bolds and elaborates the "confirm Target reads `main`
    before clicking Publish" warning on the UI path, with NVDA-
    specific guidance (tab to the combobox, arrow until you
    hear "main", confirm). Adds an explicit fallback to the
    CLI path when the dropdown can't be confirmed.
  - New subsection "What to do if a release was published
    targeting the wrong branch" describing both recovery
    paths: skip the burned tag (the simple option;
    preview.{16, 17, 23} were all skipped this way) or
    delete-and-republish at the same tag with `--cleanup-tag`
    (only if the version number must be preserved).
  - "Common pitfalls" section's "Releases UI Target dropdown"
    entry expanded to cover both the UI and CLI failure modes,
    name the burned previews, and link forward to the new
    recovery procedure in step 3.

- **Stage 11 (Velopack auto-update) re-prioritised to land
  immediately after Stage 4, ahead of Stages 5-10.** The original
  ordering put Stage 11 last because auto-update is feature
  completeness rather than core functionality. Stage 4's manual
  NVDA verification cycle made the recurring cost of install
  friction visible — each iterative preview is download →
  SmartScreen prompts → install, several screen-reader steps per
  loop. Stage 11 has no architectural dependency on Stages 5-10
  (`UpdateManager` is independent of streaming notifications,
  keyboard input routing, list detection, earcons, and review
  mode), so moving it forward amortises the friction across all
  remaining stages. `docs/ROADMAP.md`'s Phase 1 table now lists
  Stage 11 as "next" with a "Stage ordering" subsection capturing
  the rationale; `docs/SESSION-HANDOFF.md` "Where we left off"
  and a new "Stage 11 implementation sketch" replace the old
  Stage 4 next-pointer (Stage 4 is fully merged on `main` as of
  PR #60); `spec/tech-plan.md` §11 gains an implementation-order
  note at the top (the spec content itself is unchanged — only
  the order of execution shifts). The standalone
  `scripts/install-latest-preview.ps1` (PR #61) is the bridge
  until Stage 11 lands and is documented as deprecated for
  in-place updates once it does.

- **Stage 4 implementation plan revised: spike + three small PRs
  instead of one big PR.** After completing the pre-Stage-4
  cleanup pass and re-reading the `ITextProvider` /
  `ITextRangeProvider` interfaces, the original "single PR,
  ~250-400 lines" estimate looked low by ~2x and bundled three
  independent review concerns (F#-meets-C# interop, navigation
  semantics, integration testing). New plan:
  1. **Spike** — 30-line throwaway proving F# can subclass WPF's
     `FrameworkElementAutomationPeer` and implement
     `ITextProvider` without an interop foot-gun on the order of
     the `out SafeFileHandle&` bug from Stage 1.
  2. **PR 4a — Minimal UIA surface.** `TerminalAutomationPeer`
     + `TerminalTextProvider` with `DocumentRange` / `GetText`
     working; every other `ITextRangeProvider` method stubbed to
     compile. Wires `TerminalView.OnCreateAutomationPeer`. Manual
     smoke via Inspect.exe + NVDA "current line".
  3. **PR 4b — Navigation semantics.** `Move` /
     `MoveEndpointByUnit` for Character/Word/Line/Paragraph/Document;
     `Compare` / `Clone` / `ExpandToEnclosingUnit` go from stubs
     to real implementations.
  4. **PR 4c — FlaUI integration test.** First test in
     `tests/Tests.Ui/`; adds FlaUI package references and asserts
     `ControlType=Document`, `Text` pattern present, non-empty
     `DocumentRange.GetText`. Also the de facto check that FlaUI
     works on the `windows-latest` GitHub Actions runner.
  Updated in `docs/SESSION-HANDOFF.md` (Stage 4 sketch),
  `docs/ROADMAP.md` (Stage 4 row), `docs/ARCHITECTURE.md` (Stage 4
  pointer), `docs/ACCESSIBILITY-TESTING.md` (Stage 4 matrix
  header note about which row lands in which PR). The spec
  (`spec/tech-plan.md` §4) is unchanged per the immutable-spec
  policy — this revision is purely about implementation order.

- **`docs/SESSION-HANDOFF.md` brought up to date.** Replaced the
  out-of-date "in-flight branch" / "last shipped release" rows: the
  `chore/session-handoff-and-final-audit` audit, the `preview.18`
  CHANGELOG, and the relaxed CHANGELOG-matching gate (PRs #35-#37)
  all merged on 2026-04-28; `v0.0.1-preview.18` is now the last
  shipped preview. Recorded the maintainer-reported Stage-3b
  finding that a separate `cmd.exe` console-host window appears
  behind the WPF window on launch, and tracked the conhost
  defect under "Pending action items" as orthogonal to Stage 4.
  Updated the Stage 4 sketch to reference the new
  `Screen.SnapshotRows` / `Screen.SequenceNumber` primitives so the
  snapshot rule is implementable without further substrate work.
- **Release-time `CHANGELOG.md` matching gate relaxed.** The pre-build
  step in `.github/workflows/release.yml` that failed the workflow
  when no `## [<version>]` section existed has been removed.
  `v0.0.1-preview.{16,17}` were burned by exactly that gate (publish a
  release without remembering to rename the section first → workflow
  fails → `release: published` won't refire for the same tag, so the
  next attempt has to bump). The `Generate release notes from
  CHANGELOG.md` step now resolves the body in this order: per-version
  `## [<version>]` section if present → `## [Unreleased]` content
  with the heading rewritten to `## [<version>] — <today>` for the
  release body → generic `"Release X. See CHANGELOG.md for details."`
  fallback (warned-on, not failed). Net effect: a maintainer can
  publish a release directly off `[Unreleased]` without burning a
  tag. `docs/RELEASE-PROCESS.md` "Cutting a release" updated to
  describe both flows.

### Removed

- **Audit-cycle PR-C: deleted dead-code MSAA fallback path
  (`WindowSubclassNative.cs`, `TerminalRawProvider.cs`,
  `WindowSubclassTests.fs`).** Stage 4's architectural pivot
  to `AutomationPeer.GetPattern` override (PR #56) made the
  WM_GETOBJECT subclass hook + `IRawElementProviderSimple`
  raw provider a "kept just in case" MSAA-only fallback. The
  audit found no real consumers and the maintainer
  authorised outright deletion (vs. `[Obsolete]`-deprecation
  with a tracking issue). Removed three files plus the
  `SourceInitialized` / `Closed` handlers in
  `MainWindow.xaml.cs` that installed and uninstalled the
  hook. Updated cross-references in `TerminalView.cs`,
  `TerminalAutomationPeer.fs` (docstring), `TextPatternTests.fs`
  (diagnostic message + verification-chain doc), and
  `docs/ACCESSIBILITY-TESTING.md` (diagnostic decoder no
  longer points at the deleted file).

  Stage 4's UIA Document role + Text pattern + review-cursor
  navigation chain is unaffected — that path lives entirely
  in `TerminalAutomationPeer` (Terminal.Accessibility) and
  `TerminalView.OnCreateAutomationPeer`. UIA3 clients
  (NVDA, Inspect.exe, FlaUI) reach the Text pattern through
  the WPF peer tree as designed.

- **`SmokeTests.fs` "string concat is associative" placeholder.**
  Was a vestigial FsCheck wire-up assertion from before
  `VtParserTests.fs` and `ScreenTests.fs` had real property tests.
  The file's other smoke ("Terminal.Core assembly loads") is
  preserved as a project-reference / type-loading sanity check.

- **Unused `FluentAssertions` package dependency.** The package was
  pinned in `Directory.Packages.props` and referenced in
  `tests/Tests.Unit/Tests.Unit.fsproj` but no test file used it
  (no `open FluentAssertions` / `using FluentAssertions` anywhere
  in the codebase). The project's testing convention is xUnit +
  FsCheck.Xunit (per `CONTRIBUTING.md` § Tests); FluentAssertions
  was never adopted. Removing the dead reference shrinks the
  restore graph and removes a meaningless dependency-update
  surface for Dependabot.

### Fixed

- **Streaming output was permanently silent.** Root cause:
  the coalescer's "any-hash-anywhere" spinner gate was
  fundamentally broken. It triggered when
  `AllHashHistory.Count >= 20`, but every call to
  `processRowsChanged` iterates all 30 screen rows and
  appends to that same history — so a single user event
  added 30 entries, instantly exceeding the 20-entry
  threshold. Once tripped, every subsequent event added
  another 30 entries faster than the 1-second sliding
  window could drain them, so the gate stayed permanently
  triggered for the entire session. Net effect: the
  cmd.exe banner, every typed character, and every command
  output were all silently suppressed at the coalescer
  before the dispatcher / NVDA path ever saw them.

  Diagnosed from a `PTYSPEAK_LOG_LEVEL=Debug` capture on
  the post-#114 preview where every `Reader published
  RowsChanged` entry was followed by `Suppressed (spinner)`
  — including the very first 16-byte cmd.exe banner chunk.
  No real spinner was running; the heuristic was firing on
  legitimate output.

  Fix: remove the broken any-hash-anywhere gate entirely.
  The per-`(rowIdx, hash)` gate (the OTHER spinner check,
  which fires when the same row state recurs ≥5 times in
  1s) handles the common spinner case (`|/-\` cycling on
  one cell) correctly and stays in place. Cross-row
  spinner detection — the original motivation for the
  any-hash gate — is filed as a follow-up issue with a
  proper redesign brief: count unique-hash recurrences,
  not total entries.

  Tests unchanged: existing `CoalescerTests.fs` covers
  the per-key gate; nothing covered the broken any-hash
  gate, so removing it doesn't regress any tested
  behaviour.

- **`Ctrl+Shift+;` log-copy failed with "file is in use by
  another process".** Maintainer-reported on the post-#111
  preview. The clipboard handler used `File.ReadAllText(path)`
  which opens the file with `FileShare.Read` (the overload's
  default) — meaning "I tolerate other readers but no
  writers." Since the `FileLogger` writer holds the file
  open with `FileAccess.Write`, the OS rejected the read
  open because it couldn't honor the reader's "no writers"
  requirement when the writer was already there.

  Fix: open the file via an explicit `FileStream` with
  `FileShare.ReadWrite`, matching the writer's policy. The
  writer is happy to coexist with concurrent readers
  (Notepad, NVDA, the Ctrl+Shift+; handler), so the OS
  grants the handle.

- **Log-copy hotkey didn't fire on the post-#103 preview.**
  Maintainer reported pressing the gesture and not hearing the
  "Log copied to clipboard. N bytes" announcement; the session
  log confirmed the handler never ran. The Window-level
  `KeyBinding` for the gesture was registered, but WPF's
  `CommandManager` class-handler routing on a custom
  `FrameworkElement` (`TerminalView`) didn't reliably fire
  `runCopyLatestLog` — same family of routing flakiness that
  bit `Ctrl+V` earlier in Stage 6.

  Fix: handle the gesture directly in
  `TerminalView.HandleAppLevelShortcut`, the same path that
  handles `Ctrl+V` and `Ctrl+L`. New
  `SetCopyLogToClipboardHandler` callback wired by
  `Program.fs compose ()` invokes the existing
  `runCopyLatestLog` handler. Both the direct path AND the
  Window-level `KeyBinding` are wired; whichever fires first
  wins. Direct path is reliable; Window-level is defence in
  depth.

- **`Alt+F4` window-close gesture stopped working** under the
  PR #108 SystemKey-aware filter. WPF reports Alt-modified
  gestures with `e.Key == Key.System` + the actual key in
  `e.SystemKey`. The PR #108 filter unwrapped that to make
  the original `Ctrl+Alt+L` reach the handler — but the same
  unwrap converted `Alt+F4` into `Key.F4 + Alt`, the encoder
  produced bytes, `e.Handled` became `true`, and the OS
  window-close gesture died.

  Fix: drop the SystemKey unwrap and rebind the log-copy
  hotkey to `Ctrl+Shift+;` (a clean Ctrl+Shift gesture that
  doesn't need the unwrap). `Key.System` now falls through
  to `KeyCode.Unhandled`, the encoder returns null, `e.Handled`
  stays false, and the OS default `Alt+F4` close handler
  fires. If a future Alt-modified reserved hotkey (Stage 10's
  `Alt+Shift+R`) is added, the unwrap can come back with an
  explicit `Alt+F4` fall-through.

- **Streaming-silence root cause: `PeriodicTimer` reuse bug in
  `Coalescer.runLoop`.** Diagnosed via the maintainer's manual
  NVDA verification on the post-Stage-6 preview, where typing
  `dir` produced no streaming announcement and the cmd.exe
  banner sometimes worked while subsequent output went silent.
  The audible signal was "Coalescer crashed: Operation is not
  valid due to the state of the object" — the exact message
  `PeriodicTimer.WaitForNextTickAsync` throws when called a
  second time before the previous call completes.

  Pre-fix, every iteration of the runLoop's main `while`
  unconditionally called `timer.WaitForNextTickAsync(ct)` to
  build a `Task.WhenAny` race against the input channel. When
  the reader won the race, the previous tick wait was orphaned
  but never cancelled; the next iteration called
  `WaitForNextTickAsync` AGAIN while the previous was still
  pending → `InvalidOperationException`. The catch handler
  surfaced "Coalescer crashed" then exited the loop, stopping
  all further streaming announcements for the rest of the
  session.

  Why intermittent: the bug requires the reader to win
  `Task.WhenAny` at least once before any timer tick fires
  (i.e. input arrives faster than the 200ms debounce). The
  cmd.exe launch banner was a single big chunk that arrived
  before any timer iteration cycled, so it announced
  correctly. Subsequent typed-input echoes triggered multiple
  fast iterations where the reader kept winning, and the
  second iteration's `WaitForNextTickAsync` crashed.

  Fix: track the pending timer task across loop iterations.
  Reuse the same wait until it actually fires; only after a
  timer tick wins does the next iteration start a fresh
  `WaitForNextTickAsync`. New regression test
  `runLoop survives multiple consecutive reader-wins without
  crashing the PeriodicTimer` in `CoalescerTests.fs` pumps 20
  fast events through the reader channel and asserts the
  runLoop keeps delivering notifications without faulting.

- **UI test flakiness on the windows-2025 runner.** The
  FlaUI-driven tests in `tests/Tests.Ui/` launch the actual
  `Terminal.App.exe` and wait for the WPF main window to
  appear. The previous 10-second timeout was tight; under
  parallel xUnit-test load on a freshly-provisioned
  Windows Server 2025 runner image, Velopack initialisation
  + WPF subsystem startup + ConPTY spawn could exceed it.
  Confirmed flake (not a code regression) by observing the
  same failure mode on PR #104, a markdown-only PR with
  zero code changes. Bumped to 30 seconds in three call
  sites: `AutomationPeerTests.fs`, two locations in
  `TextPatternTests.fs`. Same diagnostic messages preserved
  with the new timeout value. No application code touched;
  no behavioural change for users.

- **Ctrl+V paste re-fix + Ctrl+L clear-screen.** The previous
  attempt (in the post-Stage-6 fix-PR) added `KeyBinding`s mapping
  Ctrl+V and Shift+Insert to `ApplicationCommands.Paste`, but
  manual NVDA verification showed Ctrl+V still emitted `^V` to the
  shell. Two compounding causes:
  1. WPF's `CommandManager` class handler doesn't auto-process
     `InputBindings` on a raw `FrameworkElement` the way it does
     for built-in `Control`s, so the gesture wasn't reliably
     reaching `OnPasteExecuted`.
  2. Even when the routing did reach `OnPasteCanExecute`, an empty
     clipboard returned `CanExecute = false`, the gesture fell
     through unhandled to my `OnPreviewKeyDown` override, the
     encoder produced `0x16`, and cmd.exe echoed `^V`.

  Re-fix: handle Ctrl+V, Shift+Insert, and Ctrl+L explicitly at
  the top of `OnPreviewKeyDown` (new `HandleAppLevelShortcut`
  helper) before the encoder runs. Empty clipboard now becomes a
  silent no-op instead of a `^V` emission. The
  `ApplicationCommands.Paste` `CommandBinding` is kept for any
  future right-click-menu / Edit-menu paste paths.

  Ctrl+L is special-cased to send `cls\r` (the cmd.exe clear-
  screen command) instead of `0x0C` (form feed). Strictly the
  literally-correct terminal-emulator behaviour is to send `0x0C`
  and let the shell decide — but cmd.exe ignores `0x0C` and
  echoes `^L`, which is bad UX. Documented trade-off: when the
  foreground process is something that DOES interpret `0x0C`
  (Claude Code's Ink, `less`, `vim`), Ctrl+L will run `cls` as
  if typed instead of triggering that program's redraw. Acceptable
  for the current cmd.exe-only scope; revisit when Stage 7+ adds
  shell flexibility.

- **Three post-Stage-6 regressions surfaced during manual NVDA
  verification on the post-Stage-6 preview**, all targeted in a
  single follow-up PR:

  - **Ctrl+V didn't paste; sent `^V` to the shell instead.**
    `TerminalView`'s constructor added a `CommandBinding` for
    `ApplicationCommands.Paste`, which tells WPF "if Paste is
    invoked on me, here's the handler" — but did NOT add an
    `InputBinding` mapping `Ctrl+V` (or `Shift+Insert`) to the
    Paste command. Without the gesture-to-command map, Ctrl+V
    flowed through `OnPreviewKeyDown` → encoder → `0x16` → and
    cmd.exe echoed `^V` per its control-character display
    convention. Adding the two `InputBinding`s
    (`Ctrl+V` and `Shift+Insert`) wires the gestures to the
    existing `OnPasteExecuted` handler so the paste-injection
    chokepoint actually fires.

  - **Window resize didn't reflow; text cut off the right
    edge.** `TerminalView.MeasureOverride` returned the FIXED
    preferred size (`Cols × Rows × cellSize`), so the view
    never tracked window resize, so `OnRenderSizeChanged`
    never fired, so the Stage 6 `SizeChanged` →
    `DispatcherTimer` debounce → `ResizePseudoConsole` chain
    was dead. Changed `MeasureOverride` to honour
    `availableSize` (fall back to preferred size only when
    availableSize is unbounded, e.g. inside a `ScrollViewer`).
    The Screen buffer stays at construction-time 30×120 cells
    internally — full grid runtime resize is a documented
    Phase 2 stage — but cmd.exe now sees and adapts to the
    window's actual dimensions via `ResizePseudoConsole`,
    fixing the visible "text cuts off" symptom.

  - **Stage 5 streaming output announcements were silent.**
    `TerminalView.Announce` was hardcoded to use
    `AutomationNotificationProcessing.MostRecent`. That's the
    right choice for hotkey-style one-shot announcements
    (Ctrl+Shift+U / D / R, Velopack progress) where each new
    notification SHOULD supersede any in-flight one. But for
    Stage 5's streaming-PTY-output path it was wrong: rapid
    chunks arrive faster than NVDA can speak them, and under
    `MostRecent` each new chunk discards the in-flight speech
    of the previous one — typed-character echoes and command
    output were silently superseded before NVDA could read
    any of them. The two-arg `Announce(message, activityId)`
    overload now selects processing per activityId:
    `pty-speak.output` uses `ImportantAll` (queue all chunks);
    everything else keeps `MostRecent`. A new three-arg
    overload (`message, activityId, processing`) is exposed
    for any future caller that needs to override.

- **Diagnostic safety net for the coalescer drain task.**
  Previously the `Program.fs compose ()` drain task swallowed
  every unexpected exception silently with `| _ -> ()`. A
  crashed drain looked identical to a working-but-silent one,
  which made post-Stage-6 streaming-silence diagnosis hard
  ("is the drain dying or is NVDA filtering?"). The catch-all
  now sanitises the exception message through SR-2's
  chokepoint and emits one final `Announce(..., pty-speak.error)`
  before the task exits, so a future drain crash announces
  itself rather than disappearing into the void.

- **`Ctrl+Shift+D` and `Ctrl+Shift+R` announcements no longer get
  cut off by the spawned window's focus-grab.** Discovered during
  Stage 5 manual NVDA verification: pressing `Ctrl+Shift+D` started
  the diagnostic announcement but NVDA was interrupted as soon as
  the new PowerShell window activated and stole focus (NVDA's
  default interrupt-on-focus-change). Same shape for
  `Ctrl+Shift+R` once the browser activated. Pre-existing since
  the diagnostic hotkey shipped in PR #81 and the releases hotkey
  shipped in PR #84; latent because no NVDA verification cycle
  before today exercised the announce path end-to-end.

  Fix in `src/Terminal.App/Program.fs runDiagnostic` and
  `runOpenNewRelease`: announce a SHORT cue ("Launching
  diagnostic.", "Opening new release form.") FIRST, then
  schedule the actual `Process.Start` on a ~700ms `Task.Delay`
  so NVDA's speech queue has time to play the cue before the
  new window's title takes over. The longer guidance ("Switch
  to that window to follow the test.") is dropped from the cue
  — once the user hears the spawned window's title, they have
  all the context the long version provided. Both announces
  are also re-tagged with the proper `ActivityIds.diagnostic` /
  `ActivityIds.newRelease` per-class tags introduced in Stage 5
  (replacing the back-compat default `pty-speak.update`).

  No new hotkey contract; same `Ctrl+Shift+D/R` behaviour from
  the user's side, just audible. Phase 2 TOML config will make
  the 700ms delay configurable alongside the Stage 5 coalescer
  constants.

- **Update-failure announcements pattern-match on common
  exception types instead of a single generic catch.**
  `runUpdateFlow`'s `with` block now branches on:
  - `HttpRequestException` → "Update check failed: cannot
    reach GitHub Releases. Check your internet connection.
    (...)" — the offline case, the most common failure for
    end users on flaky connections.
  - `TaskCanceledException` → "Update check timed out. Check
    your internet connection and try Ctrl+Shift+U again." —
    timeouts and dropped-mid-download.
  - `IOException` → "Update could not be written to disk: ...
    Free up space or check folder permissions in
    %%LocalAppData%%\\pty-speak\\." — disk-side failures
    during download or patch application.
  - Catch-all for unexpected exceptions remains as
    "Update failed: ...".
  Replaces the single generic "Update failed: <ex.Message>"
  that PR #63 shipped with a "later stage can pattern-match"
  TODO comment. The user's offline-failure question on
  preview.25 install made this concrete enough to
  implement now.

- **Release workflow walks back through burned tags when
  fetching the prior `*-full.nupkg`.** `v0.0.1-preview.24`
  failed at the "Fetch prior release nupkg" step because the
  most recent prior release (`preview.23`) was a burned tag
  whose own workflow had failed at the target-branch gate, so
  no `*-full.nupkg` was ever uploaded to it. The original step
  picked the most recent prior release by publishedAt and
  blindly tried to download the asset — exit 1 from `gh
  release download` when no matching assets existed propagated
  to the workflow as a failure. Replaced with a walk-back loop
  that iterates releases in descending order and uses
  `gh release view --json assets` to find the most recent one
  that actually has a `*-full.nupkg`. Falls through to the
  existing "no prior nupkg, ship full-only" path if no release
  in the history has the asset (legitimate first release on a
  channel). Resolves the failure mode that
  `v0.0.1-preview.{14, 23, 24}` all hit at different points;
  combined with PR #64's documentation strengthening, makes
  burned tags a recoverable rather than cascading failure.

- **`MainWindow` moves keyboard focus to `TerminalSurface` on
  `Loaded`.** `v0.0.1-preview.21` install smoke established that
  even with PR #59's working Text-pattern navigation, NVDA still
  couldn't reach the buffer: focus stayed on the WPF `Window`
  after launch, so NVDA announced "pty-speak terminal, window"
  and anchored the review cursor on the Window (which has no
  Text pattern). The `TerminalView`'s Document-role peer with
  the working Text pattern was reachable in the UIA tree but
  invisible to NVDA's review cursor because focus was on the
  wrong element. One-line fix in `MainWindow.xaml.cs`: hook
  `Loaded` and call `TerminalSurface.Focus()`. NVDA now
  announces "Terminal, document" on launch and the review
  cursor anchors to the TerminalView, where PR #59's
  navigation is reachable.

- **Stage 4 Text-pattern navigation: NVDA's review cursor can
  now read the terminal buffer.** `v0.0.1-preview.20` install
  smoke established that PR #56's Text-pattern surface was
  reachable but unusable: NVDA's "read current line" returned
  "blank" and prev/next-line did nothing. Root cause was that
  `TerminalTextRange`'s `ExpandToEnclosingUnit`, `Move`,
  `MoveEndpointByUnit`, and `MoveEndpointByRange` were all
  no-op stubs from PR #56's "navigation deferred to PR D"
  scope. Without them NVDA's review cursor couldn't delimit a
  line: `ExpandToEnclosingUnit(Line)` was silently dropped,
  leaving the range collapsed at start with empty `GetText`
  output. Implementation in this commit:

  - `TerminalTextRange` now tracks mutable `(startRow,
    startCol, endRow, endCol)` endpoints (the UIA contract
    requires the void-returning navigation methods to mutate
    in place).
  - `ExpandToEnclosingUnit` handles `Character`, `Document`,
    and `Line` (other unit types degrade to `Line` until a
    terminal-output tokenizer arrives).
  - `Move`, `MoveEndpointByUnit`, `MoveEndpointByRange`
    implement UIA's contract including endpoint-collision
    handling (range collapses to the moved point if endpoints
    cross).
  - `CompareEndpoints` returns the lexicographic ordering
    over `(row, col)` positions.
  - `GetText` uses the range endpoints (was returning the
    entire snapshot regardless of range).
  - `DocumentRange` constructs a half-open `[(0,0), (rows, 0))`
    range matching UIA's standard endpoint convention.

  `tests/Tests.Ui/TextPatternTests.fs` gains a navigation
  regression test that asserts `ExpandToEnclosingUnit(Line)`
  bounds the range length below the full-document size, and
  `Move(Line, 1)` preserves the Line shape — the two
  invariants whose violation produced the preview.20
  failure mode.

- **Removed `MainWindow.xaml`'s
  `AutomationProperties.HelpText`.** Preview.20 NVDA smoke
  heard "Screen-reader-native Windows terminal. Stage 3b:
  bytes from a child shell are parsed and rendered; UIA
  exposure lands in Stage 4." read after the role
  announcement on every focus. That string was useful as
  developer documentation while the project was bootstrapping
  but is verbose chatter for the user. The window's name and
  Document role are sufficient.

- **Parser preserves the in-flight digit param across the
  Param → Intermediate transition (closes
  [Issue #42](https://github.com/KyleKeane/pty-speak/issues/42)).**
  `StateMachine.fs`'s `CsiParam → CsiIntermediate` and
  `DcsParam → DcsIntermediate` edges previously called
  `collectIntermediate` without first calling `pushParam`, so
  inputs like `\x1b[1$q` (CSI param + intermediate) or
  `\x1bP1$q...` (DECRQSS-shape DCS) emitted dispatch events
  with `parms = [||]` instead of `[|1|]`. Both edges now push
  the in-flight digit before transitioning, matching Williams'
  canonical `param;collect` action and alacritty/vte. The
  `CAN inside DCS passthrough emits DcsUnhook` test was
  re-augmented with the `$` byte (which used to be deliberately
  removed in #41 to dodge this bug); a new
  `CSI with param + intermediate preserves the in-flight digit`
  test pins the parallel CSI invariant so a future regression
  in either edge fails loudly.
- **CI release workflow now fetches the prior release `*-full.nupkg`
  before `vpk pack`, so deltas are produced for every non-first
  release.** `v0.0.1-preview.18` and `v0.0.1-preview.19` both
  shipped full-only — Velopack only generates a `*-delta.nupkg`
  when a prior `*-full.nupkg` exists in `--outputDir` at pack
  time, and CI starts from a fresh runner each release. New
  step uses `gh release list` + `gh release download
  --pattern '*-full.nupkg'` to drop the previous release's full
  package into `releases/` before `vpk pack`. A subsequent
  cleanup step removes the prior nupkg before the softprops
  upload so it doesn't get re-attached to the current release as
  a duplicate. First release on a channel (no prior to diff
  against) is handled silently — `gh release list` returns
  empty and the step logs and skips. Auto-update clients on
  the next release will fetch ~KB-sized delta packages instead
  of ~66 MB full nupkgs. `docs/RELEASE-PROCESS.md` updated to
  describe both new steps and the renumbered downstream steps.
- **`Terminal.App.exe` no longer allocates a console window at
  startup.** `Terminal.App.fsproj` previously set
  `OutputType=Exe` + `DisableWinExeOutputInference=true`, which
  forced the produced executable into the Windows console
  subsystem; Windows allocated a conhost for the parent process
  before any of our code ran, and that empty console window
  appeared behind the WPF window on every launch
  ([Issue #39](https://github.com/KyleKeane/pty-speak/issues/39)).
  Investigation ruled out the four ConPTY-side hypotheses
  originally listed (`STARTUPINFOEX.cb`, attribute attachment,
  `STARTF_USESTDHANDLES`, `CREATE_NEW_CONSOLE`) — the ConPTY
  setup matches Microsoft's canonical sample exactly. Switched
  the executable to `OutputType=WinExe` (Windows GUI subsystem,
  matching what the WPF SDK would auto-infer) and dropped the
  `DisableWinExeOutputInference` opt-out. No source changes
  needed; `grep` confirms zero `Console.WriteLine`/`printfn`
  calls in `src/`, so nothing was relying on an attached
  console. Visual smoke verification needs a Windows install of
  the next preview release.
- **`release.yml` now uploads Velopack's channel-suffixed manifest
  files.** `v0.0.1-preview.18` shipped with only three release
  assets (`*-full.nupkg`, `*-Setup.exe`, `RELEASES`) instead of the
  five Velopack produces. Root cause: the `softprops/action-gh-release`
  upload pattern was the literal `releases/releases.json`, but
  Velopack outputs `releases.<channel>.json` (we get `releases.win.json`
  for win-x64 packs since we don't pass `--channel`). With
  `fail_on_unmatched_files: false` the literal pattern matched
  nothing and the manifest was silently skipped — auto-update flows
  would have broken for any user installing from the release.
  Patterns updated to channel-agnostic globs:
  `releases/releases.*.json` and `releases/assets.*.json`. The
  artifact-existence gate added in PR #41 now also asserts both
  manifests are present, so the next release fails loudly if
  Velopack's naming changes again. `docs/RELEASE-PROCESS.md`
  refreshed with the actual `vpk pack` output set per
  Velopack's [packaging docs](https://docs.velopack.io/packaging/overview).

- **Yield in concurrent snapshot stress test.** The producer/snapshot
  test added in #38 now calls `Thread.Yield()` once per snapshot
  iteration. .NET's `Monitor` already yields on contended
  Apply/SnapshotRows, but the explicit hint keeps the test thread
  from starving the producer if the lock briefly goes uncontested
  on a slow CI scheduler.

### Security

- **Stage 7 PR-A — `SECURITY.md` row PO-5 closed (env-scrub for
  ConPTY child).** New `Terminal.Pty.Native.EnvBlock` module builds
  an explicit UTF-16LE `lpEnvironment` block before every
  `CreateProcess` call instead of inheriting the parent's full
  environment via `lpEnvironment=IntPtr.Zero`. Allow-list preserves
  `PATH`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOME` (with
  `%USERPROFILE%` fallback when absent), `ANTHROPIC_API_KEY`,
  `CLAUDE_CODE_GIT_BASH_PATH` per `spec/tech-plan.md` §7.2;
  always-set `TERM=xterm-256color` + `COLORTERM=truecolor` overrides
  any parent value (Stage 4a's truecolor SGR substrate already
  handles the produced sequences). Deny-list overrides allow-list
  for variables matching `*_TOKEN`, `*_SECRET`, `*_KEY` (with
  explicit `ANTHROPIC_API_KEY` exemption — Claude Code is the
  primary target workload), `*_PASSWORD` — suffix match on
  uppercase, so `KEYBOARD_LAYOUT` is preserved. Sorted by uppercase
  name per Win32 convention. Pinned by
  `tests/Tests.Unit/EnvBlockTests.fs` covering allow-list
  preservation, deny-list pattern matching (case-insensitive),
  the `ANTHROPIC_API_KEY` exemption, the HOME=%USERPROFILE%
  fallback, the always-set TERM/COLORTERM override, and a
  byte-level UTF-16LE marshalling round-trip — the silent-failure
  canary the `docs/SESSION-HANDOFF.md` Stage 7 sketch flagged as
  the highest-risk failure mode for this PR (get the marshalling
  wrong and the child sees no environment at all). Stripped count
  is logged at `Information` level after spawn ("Env-scrub:
  stripped {Count} variables before child spawn.") — count only,
  never names or values, per the `SECURITY.md` logging-discipline
  contract (env-var names like `BANK_API_KEY` are themselves
  sensitive). First of four sequenced Stage 7 PRs per
  `docs/PROJECT-PLAN-2026-05.md` Part 2 — env-scrub lands first
  because it's independent of the shell-registry / hot-switch
  hotkey work in PR-B / PR-C / PR-D. Stage 7 issues inventory
  (`docs/STAGE-7-ISSUES.md`) and ACCESSIBILITY-TESTING.md row
  expansion follow in PR-D after PR-C ships.

- **Security audit cycle SR-3: SECURITY.md audit response.**
  Brings the vulnerability inventory and narrative into sync
  with the shipped code from SR-1 and SR-2, plus closes the
  documentation gaps the comprehensive audit identified.
  Companion to SR-1 (#76, parser hardening) and SR-2 (#77,
  accessibility hardening). The audit cycle is complete with
  this PR.

  - **6 new inventory rows.** `A-1`/`A-2`/`A-3` cover
    application-surface findings (jagged-snapshot bounds in
    word-boundary helpers, `Move(Character)` int32 underflow,
    pre-Stage-6 keyboard contract). `D-1`/`D-2` cover
    developer-tooling and operational mitigations
    (`install-latest-preview.ps1` Mark-of-the-Web strip,
    burned-tag visibility in public release history). `C-1`
    covers the deferred-to-Phase-2 hardcoded `UpdateRepoUrl`
    configuration item.

  - **3 inventory rows updated.** `TC-1` (response-generating
    sequences) annotated with the SR-1 catch-all-drop
    documentation. `TC-5` (control characters in NVDA
    `displayString`) flipped from `planned` to `partial`,
    citing SR-2's `AnnounceSanitiser` for the
    exception-message interpolation chokepoint. `TC-6`
    (output-rate ANSI-bomb DoS) updated to credit SR-1's
    parser-state caps (`MAX_PARAM_VALUE`, `MAX_DCS_RAW`,
    `OscIgnore`) and clarify that the Stage 5 ingestion-rate
    cap is still the remaining work.

  - **2 new narrative items.** `T-10` paragraph in the
    auto-update threat model elaborates the Mark-of-the-Web
    strip rationale (cross-references T-3); `D-2` bullet
    appears under "Out of scope for the update path" for
    burned-tag visibility.

  - **New `PO-5` row** documents the ConPTY environment
    inheritance accepted-risk: parent's full env block
    reaches the child via `lpEnvironment=IntPtr.Zero`,
    leaking sensitive vars (`GITHUB_TOKEN`,
    `OPENAI_API_KEY`, etc.) to the child shell. Significant
    change to close; tracked in `docs/SESSION-HANDOFF.md`
    item 5 alongside two other deferred follow-ups.

  - **New "Application surfaces" inventory section** between
    Process / OS and Update path, plus a new "Configuration"
    mini-section for the C-prefix.

  - **Lead-paragraph legend** explains the row-prefix
    naming (`TC-`, `PO-`, `A-`, `T-`, `B-`, `D-`, `C-`)
    so audit-grep queries stay consistent across surfaces.

  - **Doc-drift fix.** Tense agreement on the Ed25519
    public-key publication sentence (`is published as ...
    (it will be added)` -> `will be published as ... (it
    will be added)`).

  - **3 deferred-follow-up rows added to
    `docs/SESSION-HANDOFF.md`** (item 5) tracking the
    findings the audit identified but didn't close inline:
    PO-5 ConPTY env scrub, D-1 install-script TOCTOU
    between `Unblock-File` and `Start-Process`, Acc/9
    `TerminalView.OnRender` lock decision (deferred to
    Stage 5's parser-off-dispatcher rework).

  Vulnerability inventory now has 31 rows: TC-1..TC-6,
  PO-1..PO-5, A-1..A-3, T-1..T-10, B-1..B-4, D-1..D-2,
  C-1. All HIGH-severity findings from the November-December
  2025 audit are CLOSED in code (SR-1 + SR-2); all MEDIUM
  findings are either CLOSED or have an inventory row
  pointing at the deferred work.

- **Security audit cycle SR-2: accessibility hardening against
  malformed snapshots and untrusted exception messages.**
  Closes three HIGH/MEDIUM findings from the comprehensive
  code-level security audit. Companion to SR-1's parser
  hardening; together they close every HIGH-severity finding
  the audit identified.

  - **Jagged-snapshot bounds in word-boundary helpers.**
    `TerminalTextRange`'s `WordEndFrom`, `NextWordStart`, and
    `PrevWordStart` walked `rows.[r].[c]` assuming uniform
    row lengths (`c < cols`). `Screen.SnapshotRows` returns
    uniform rows today, but the `TerminalTextRange`
    constructor doesn't enforce uniformity, so a future
    refactor (e.g. ragged scrollback) or adversarial test
    construction could trigger `IndexOutOfRangeException`.
    Each `rows.[r].[c]` access in
    `src/Terminal.Accessibility/TerminalAutomationPeer.fs` is
    now guarded against `c >= rows.[r].Length`; the helpers
    advance to the next row when a short row is encountered.

  - **Control-character `AnnounceSanitiser`.** New
    `Terminal.Core.AnnounceSanitiser.sanitise : string ->
    string` strips C0 (0x00..0x1F), DEL (0x7F), and C1
    (0x80..0x9F) controls before any string reaches NVDA via
    UIA's `RaiseNotificationEvent`. Applied at the two call
    sites that interpolate exception messages: the
    `ParserError` construction in
    `src/Terminal.App/Program.fs` and all four interpolations
    in `Terminal.Core.UpdateMessages.announcementForException`.
    Closes the path where an exception message containing a
    BiDi override (U+202E), BEL (0x07), or ANSI escape
    sequence (0x1B) could confuse NVDA's notification handler
    or spoof announcement direction. Stage 5's streaming
    coalescer is the future second consumer; the sanitiser
    is the central chokepoint.

  - **`Move(Character, count)` int64 widening.** Both `Move`
    and `MoveEndpointByUnit` previously did `curIdx + count`
    in unchecked int32; `count = int.MinValue` underflowed to
    a positive value due to wrap, slipping past the `max 0`
    clamp and returning a wrong-direction result. Both sites
    now widen to int64 before the add, then narrow back to
    int after the bounds clamp. Same observed clamping
    behaviour for legitimate inputs; the underflow class
    disappears.

  Three new tests in `tests/Tests.Unit/WordBoundaryTests.fs`
  pin the jagged-snapshot contract (no
  `IndexOutOfRangeException` from any of the three helpers
  on a deliberately-jagged `Cell[][]`). Three new tests in
  `tests/Tests.Unit/UpdateMessagesTests.fs` pin the
  control-char strip contract end-to-end (BiDi override
  printable-Unicode preserved; BEL stripped from `IOException`
  message; clipboard-OSC `\x1b]52;c;...\x07` stripped from
  catch-all message). New `tests/Tests.Unit/AnnounceSanitiserTests.fs`
  exercises the sanitiser directly: empty / null tolerance,
  pure-ASCII identity, each control class stripped, BiDi /
  multi-byte UTF-8 / combining-mark printable Unicode
  preserved, long control-byte runs handled.

  Companion PRs: SR-1 (parser hardening, merged via #76);
  SR-3 (`SECURITY.md` audit response, queued). The full
  plan is in `/root/.claude/plans/replicated-riding-sketch.md`.

- **Security audit cycle SR-1: parser bounds against malicious
  input.** Closes three HIGH/MEDIUM findings from the
  comprehensive code-level security audit. All three are
  ANSI-bomb-class DoS protections — they don't change
  behaviour for legitimate input, just cap the parser-state
  accumulators so an adversarially-shaped byte stream
  can't allocate without bound or wrap into negative
  values.

  - **`currentParam` int32 clamp at 65535** (alacritty / vte
    parity). Input like `\x1b[999999999999999999m` previously
    overflowed int32 to a negative SGR param; now it clamps.
    Applied at both CSI and DCS digit-accumulation sites in
    `src/Terminal.Parser/StateMachine.fs`.

  - **`MAX_DCS_RAW = 4096` cap** on DCS payload emission.
    `DcsPassthrough` now tracks `dcsTotalLen` and stops
    emitting `DcsPut` events past the cap (DCS Hook + Unhook
    pair still fires, so the framing stays intact). Matches
    the ANSI-bomb resistance pattern Sixel / ReGIS terminal
    emulators use.

  - **OSC overflow transitions to `OscIgnore`.** Previously
    the parser silently truncated OSC payloads at
    `MAX_OSC_RAW = 1024` but stayed in `OscString`, where an
    embedded `\x1B` in dropped bytes could be misread as ST
    and desynchronise the state machine. New `OscIgnore`
    sub-state mirrors the existing `DcsIgnore` pattern:
    consumes bytes until ST/BEL terminator, then dispatches
    an empty `OscDispatch`.

  - **`Screen.csiDispatch` catch-all comment** documents that
    response-generating sequences (DSR, DA1/2/3, DECRQM,
    DECRQSS, CPR, title/font reports) are deliberately
    dropped per `SECURITY.md` row TC-1. Reviewers are
    instructed to block any PR that adds a handler in this
    match without a matching `SECURITY.md` update.

  Four new tests in `tests/Tests.Unit/VtParserTests.fs` pin
  each contract: SGR param clamp returns non-negative;
  8 KiB DCS payload produces ≤ 4096 `DcsPut` events with
  Hook+Unhook intact; 8 KiB OSC payload dispatches once
  with empty params; parser returns to `Ground` after OSC
  overflow + terminator.

  Companion PRs in this audit cycle (queued):
  SR-2 (accessibility hardening: jagged-array bounds,
  control-char `AnnounceSanitiser`, `Move` overflow guard);
  SR-3 (`SECURITY.md` audit response: 6 new inventory rows
  + cross-references). The full plan is in
  `/root/.claude/plans/replicated-riding-sketch.md`.

### Post-Cycle-49 PR-S+T (2026-05-14): CI-testing future-improvements doc + CLAUDE.md PR-workflow conventions

Two coupled docs/process changes (combined into one PR
since both are about "how we ship PRs going forward"):

1. **`docs/CI-TESTING-FUTURE.md`** — scoping document
   capturing the gap between what CI catches today vs.
   what dogfood catches, plus two practical wins
   (orchestration unit tests; ConPTY + Announce-capture
   integration). Pick up when "Cycle N closes with no
   dogfood-only regressions". Not a plan to execute now.

2. **`CLAUDE.md` §"Branching and PRs"** — two new rules:
   *(a)* `git checkout main && git pull origin main`
   BEFORE creating each new branch — Cycle 50 surfaced
   the cost of skipping this (every PR after the first
   in a multi-PR session got a `CHANGELOG.md` merge
   conflict because the branch was cut from a stale
   `main`); *(b)* CHANGELOG `[Unreleased]` entries
   append at the BOTTOM, not the top — eliminates the
   "every PR conflicts with every other PR's anchor"
   pattern. This entry IS the first append-bottom
   demonstrator.

Note for future readers: the existing top-newest entry
order in `[Unreleased]` is preserved as-is — re-ordering
historical entries is more disruption than benefit.
Going forward, new entries land at the bottom; the
per-entry `### Cycle N PR-X (date):` header gives the
chronological order regardless of file position.

### Post-Cycle-49 PR-U (2026-05-14): Seed sub-prompt preamble line count at SubPromptIdle for single-key sub-prompts

Maintainer test-04 (yes/no via `choice`) dogfood 2026-05-14:
after pressing `Y`, NVDA narrated the entire 184-char script
output (START + intro + `Continue? [Y,N]?Y` + `You chose Yes.`
+ END) instead of just `You chose Yes.` + END.

Root cause: `choice` is a single-key sub-prompt — cmd
consumes the `Y` byte directly without requiring `Enter`.
The byte-write wrapper never sees `0x0D`, so no EnterPressed
transition fires, so `capturePreambleForSubPromptResponse`
never runs, so `subPromptPreambleLineCount` stays at 0, so
tuple-final's line-count strategy falls through to "no
trim". The state-machine log confirms:
`Composing(prompt=..., single-key=true)` — the state-
machine knows it's single-key but the line-count signal
isn't wired into that path.

PR-U also seeds `subPromptPreambleLineCount` at SubPromptIdle
time using the count of non-empty lines in the screen-read
announce body. For typed-input sub-prompts (test-02),
`capturePreambleForSubPromptResponse` continues to OVERRIDE
this seed on EnterPressed with the cursor-row count (finer
grained, accounts for typed response on the prompt row).
For single-key sub-prompts where no EnterPressed fires, the
SubPromptIdle seed drives tuple-final correctly. Only
seeds when `source = "screen"`; the accumulator-fallback
path's line layout may not match `tuple.OutputText`'s.

Diagnostic log per the PR-J convention: new Information-
level `PR-U sub-prompt preamble line count seeded at
SubPromptIdle. LineCount=N Source=screen` confirms the seed
fired.

(Adjacent maintainer complaint about `Continue? [Y,N]?` not
being heard — bundle disagrees: the sub-prompt announce
body at 13:17:52 includes "Continue? [Y,N]?" in the
124-char announce. Likely NVDA's TTS dropped or cut off
mid-sentence since `AnnounceSanitiser.sanitise` strips `\n`
from the announce body. Worth a follow-up if it persists
post-PR-U.)

### Cycle 51 PR-V (2026-05-14): ADR 0004 — IOCell model for shell interaction

Lock the architectural pivot from "screen-row-based
extraction on top of ContentHistory" to "IOCell as the
unit of shell interaction; ContentHistory as the sole
extraction substrate; OutputDispatcher as the sole non-
emergency channel". Triggered by the maintainer's
2026-05-14 dogfood at preview.134 (commit `ae33bc9`)
where running `test-04-yes-no.cmd` four times in sequence
produced catastrophic narration failure ("things go
completely haywire... not reading the beginning half...
not reading the end... reading a bunch of stuff well
after I've moved on"). Root cause is structural — Cycle
49's PR-K through PR-U all bolt screen-row coordinates on
top of a Seq-based substrate that doesn't need them.

ADR 0004 locks four decisions:

1. **IOCell** is the unit of shell interaction (rename of
   `SessionModel.SessionTuple`). The active IOCell is the
   in-flight cell; the IOCell history is the bounded
   queue of sealed past cells.
2. **Sub-prompts** are inline state inside the parent
   IOCell in v1, not nested cells. (Future cycle may
   promote per ADR 0006 if dogfood demands.)
3. **ContentHistory** is the sole extraction substrate.
   Screen-row coordinates become display-only post-
   pivot; `tryLatestMarker(PromptStart) = None` →
   drop-the-cell ("loud silence beats stale-scrollback
   garbage announce").
4. **OutputDispatcher** is the sole non-emergency
   channel. Tier 1 (substrate-driven announces) flows
   through `OutputDispatcher.dispatch`; Tier 2 (~50
   app-affordance call sites) uses a narrow named bypass
   `AnnounceEmergency` on a dedicated
   `pty-speak.app-affordance` activity ID.

Also locks the v1 IOCell canonical data structure: F#
records + DUs in memory, hand-rolled JSONL on disk with
`schemaVersion=2` (bumping from Cycle 24b's
`schemaVersion=1` SessionTuple format), `IOCell.parseFromJsonl`
round-trip reader shipped maintainer-only in PR-W2,
`Id`/`CellSequence` assigned at cell creation (not at seal).

Cycle 51 migration sequence (each PR independently CI-
gated): PR-V (this PR; docs-only) → PR-W (IOCell type +
extraction + formatter) → PR-W2 (round-trip reader) →
PR-X (Seq-based sub-prompt narration) → PR-Y (Tier 1
channel routing) → PR-Z (closure audit).

Phase 0 spike branch
`spike/cycle51-iocell-substrate-exploration` (commit
`de8bf81`, throwaway) shipped a diagnostic-only parallel
`extractIOCell` + `ContentHistory.entriesAfter` helper +
side-by-side comparison log. The maintainer's preview.135
dogfood bundle was then analysed **instead of** building
the spike, and it falsified the
classification-by-`EntrySource` thesis: in heuristic-only
cmd there is no CommandStart marker, so `ShellInteraction`
stays `Composing` and cmd's own output is tagged
`UserInputEcho` (`CmdSubPrompt=0` across an entire 4-run
test-04 session). The classifier is rejected; the ADR was
revised before PR-V opened — PR-W now promotes the
existing heuristic-only marker-slice + first-newline
split to the sole extractor (no `EntrySource`
classification, no row-walk fallback), and PR-X becomes
the load-bearing PR that removes the screen-row
dependence from sub-prompt narration (the actual cause of
the run-4 haywire: wrapped command echo → screen-read
fallback → `Source=screen`-gated preamble seed never
fired → full output announced).

Updates: new file
`docs/adr/0004-iocell-model-for-shell-interaction.md`,
`CLAUDE.md` reading-order index, `docs/SESSION-HANDOFF.md`
current-state + next-stage sections.

### Cycle 51 PR-W (2026-05-15): IOCell type + sole-substrate extraction + schemaVersion 2

Execute the first code PR of the Cycle 51 pivot (ADR 0004).
`SessionModel.SessionTuple` → `SessionModel.IOCell`
(`ActiveSessionTuple` → `ActiveIOCell`) across the codebase;
add the `IOCellPhase` DU (`Composing` / `Executing` /
`AwaitingSubPromptResponse` / `Sealed`) and two record fields
`CellSequence: int64` + `Phase: IOCellPhase`. Identity
contract: `Id` and `CellSequence` are assigned at cell
creation (the PromptStart transition), not at seal;
`CellSequence` is monotonic per shell session and resets
implicitly on shell hot-switch (a fresh `SessionModel.T` is
constructed per session). v1 populates `Composing` (active)
and `Sealed` (history); `Executing` /
`AwaitingSubPromptResponse` are reserved.

ContentHistory is now the **sole** extraction substrate
(ADR 0004 Decision 3): the screen-row-walk `extractContent`
fallback is deleted, `extractContentFromContentHistory` is
promoted to the sole extractor and renamed `extractIOCell`,
and `finalizeAndEnqueue` enforces the **drop-on-None**
contract — when ContentHistory has no authoritative slice
(no PromptStart Seq, or a legacy no-ContentHistory caller)
the cell does NOT finalize; it is dropped and logged at
Information (`IOCell dropped: no PromptStart Seq in
ContentHistory.`). Loud silence beats a stale-scrollback
garbage announce. Consequence: the legacy
`apply` / `applyAndCapture` / `finalizeIncomplete` surface
(no ContentHistory) no longer enqueues a cell — the
production path is `applyAndCaptureWithContentHistory`.

On-disk wire format bumped `schemaVersion 1 → 2`:
`formatTupleAsJsonl` → `formatIOCellAsJsonl`, serialising
`cellSequence` (int64 JSON number) and `phase` (tagged DU
object, same rule as `BoundarySource`). The migration is
one-way (v1/v2 mutually unreadable); the hand-rolled
byte-stable discipline is preserved. New canonical doc
`docs/IOCELL-SCHEMA.md`; `docs/SESSION-MODEL.md` reduced to
a pointer stub (pre-pivot research design preserved in git
history); `docs/DOC-MAP.md` updated. `SessionModelTests.fs`
rewritten for the drop-on-None contract + ContentHistory-path
finalize coverage (metadata accumulation, ring-buffer
eviction, `CellSequence` monotonicity + shell-switch reset,
`Phase`), plus `IOCellPhase` + schemaVersion-2 serialization
unit tests. Round-trip reader is PR-W2.

### Cycle 51 PR-X (2026-05-15): Seq-watermark narration (the load-bearing fix)

Replace the entire screen-row / line-count sub-prompt
machinery (the screen-reader callback, the wrap-rows helper,
the preamble-capture callback, the preamble line-count, and
the submitted-command screen captures — ~230 lines deleted
from `Program.fs`) with two monotonic `ContentHistory` Seq
watermarks (ADR 0004). Fixes BOTH dogfood symptoms from the
post-PR-W2 regression run:

- **History-scroll garbage** — pressing Enter after Up/Down
  history recall announced (and persisted) the entire scrolled
  list. cmd reprints the prompt line in place with CR on each
  recall; `ContentHistory` accumulates every redraw linearly,
  and the old `PromptStart→tail` + first-newline split dumped
  the whole pile into `OutputText`. `SessionModel.extractIOCell`
  now takes a `commandEnterSeq` watermark (captured at the
  byte-level command Enter) and splits there: command = last
  non-empty line up to the watermark, output = everything
  after — immune to the accumulation, in BOTH the audible
  announce and the persisted session-log JSONL.
- **test-04 §4b haywire** — single-key Y/N sub-prompts
  announced the whole command output instead of just the
  post-keypress delta. The audible tuple-final announce now
  slices `ContentHistory` from `lastEnterSeq` (advanced at
  every command Enter, every sub-prompt-response Enter, AND
  every SubPromptIdle — the last covers single-key prompts
  that have no response Enter) to the tail, minus the trailing
  next-prompt; the user never re-hears the command echo /
  preamble / question.

Sub-prompt question announce keeps the proven accumulator
last-line text (the screen-row reader is deleted). New PR-X
diagnostics at Information (`PR-X preamble watermark`, `PR-X
sub-prompt announce`, `PR-X tuple-final delta`). New
`SessionModelTests` watermark coverage (history-scroll
exclusion, multi-line output, `-1L` legacy fallback); all
pre-PR-X CH-path tests thread `-1L` (legacy first-newline
path preserved). Acceptance gate: the maintainer's 4-run
test-04 + history-scroll dogfood.

### Cycle 51 PR-Y (2026-05-15): single-key sub-prompt no longer re-announces the question

Post-PR-X dogfood (preview.137) found single-key Y/N
sub-prompts (test-04) re-announce the question in the
post-response tuple-final delta: NVDA spoke "Continue?
[Y,N]?" at SubPromptIdle, then again as the lead of "Continue?
[Y,N]?Y / You chose Yes. / === END ===". Root cause: a
single-key prompt's question row has no terminating newline
when SubPromptIdle fires, so ContentHistory hasn't sealed it
into an entry yet — `lastEnterSeq` captured then lands on the
newline *before* the question and the slice re-includes it
(typed-Enter sub-prompts like test-02 escape this because the
response Enter re-captures the watermark past the sealed
question). Fix: remember the exact question text spoken at
SubPromptIdle and, if the tuple-final delta begins with it,
drop it through the first newline after it (also strips the
inline single-key response char). Exact-match against the
literally-spoken string — a no-op for test-02 and plain
commands; cleared on every top-level command Enter +
shell hot-switch. New `PR-Y stripped already-spoken
sub-prompt question` Information diagnostic.

### Cycle 51 PR-Z (2026-05-15): history-recall announce reads wrapped commands whole

Post-PR-Y dogfood: Up/Down command-recall occasionally spoke
the wrapped tail of a long recalled command (`cho.cmd"`,
`es-no.cmd"`) instead of the command. The recall announce read
a single screen row at `Active.PromptRowIndex`; when a recalled
command (a long script path) soft-wraps past `screen.Cols` the
prompt+command spans two rows and cmd scrolls the viewport, so
the fixed row index lands on the wrapped continuation. Fix:
resolve the prompt row by scanning up from the cursor for the
row that currently starts with the prompt text (robust to
scroll), then join prompt-row..cursor-row so the wrapped
command is read whole. Non-wrapping commands are unaffected
(cursor row == prompt row → single row); with no prompt text
(e.g. PowerShell) it stays on the single prompt row as before.

### Cycle 51 PR-AA (2026-05-15): speak the full sub-prompt preamble + restore the startup banner

Post-PR-Y/Z dogfood found two "first part not spoken" gaps:
(1) two-part tests (user-input, numeric, yes/no, pause) spoke
only the question, not the intro ("This test asks…", "Enter a
number:"); (2) switching to cmd no longer read the startup
banner ("Microsoft Windows [Version …]").

(1) PR-X took only the accumulator's last non-empty line
because the raw byte accumulator carries an OSC window-title
sequence whose printable body (`]0;…\foo.cmd"`) survives the
lone-ESC `AnnounceSanitiser.sanitise` as spoken garbage. New
`AnnounceSanitiser.stripSequences` strips whole CSI / OSC
(BEL- or ST-terminated) / two-char-ESC sequences (full xUnit
coverage); the sub-prompt announce now strips sequences, splits
into visible lines, and speaks the whole preamble + question.
Only the question line is stored for PR-Y's post-response strip
(the tuple-final slice begins at the question, never the
preamble, so PR-Y still matches).

(2) PR-X's `when lastEnterSeq >= 0L` tuple-final guard
suppressed every announce before the first command Enter —
including the shell startup / post-switch banner. The slice
watermark now defaults to `0L` when no command Enter has
happened yet, so the banner is spoken; normal commands use the
command-Enter watermark unchanged. New `PR-AA sub-prompt
announce` Information diagnostic (replaces `PR-X sub-prompt
announce`).

### Cycle 51 PR-AB (2026-05-15): strip fast-type command echo from the tuple-final delta

Post-PR-AA dogfood: typing a command (e.g. `echo hi`) fast and
hitting Enter without a pause occasionally narrated extra
content — fragments of the command word or a repeated argument
— with no corresponding record in the review/speech cursor.
Root cause: `lastEnterSeq` is captured synchronously at the CR
keystroke, but a fast-typed command's echo round-trips through
the PTY and lands in ContentHistory at Seq > lastEnterSeq, so
the tuple-final slice `[lastEnterSeq, tail]` begins with the
command-echo line instead of the output (typing slowly lets the
echo settle below the watermark first, so it never reproduced
before). Fix: capture the submitted command text
(`UserInputBuffer.Capture()`) at the CR keystroke and, at
tuple-final, strip that exact already-on-screen command line off
the front of the delta through the first newline after it — the
same shape as PR-Y's sub-prompt-question strip, applied after
it. No-op when the user typed slowly, for sub-prompt deltas, and
for the startup banner. New `PR-AB stripped fast-type command
echo` Information diagnostic. Cleared on every top-level command
Enter and shell hot-switch.

### Cycle 51 PR-AC (2026-05-15): speak the new shell's banner on hot-switch

Post-PR-AA dogfood: the startup banner is heard on first app
launch but NOT after a Ctrl+Shift+1/2/3 shell switch (cmd ↔
PowerShell ↔ cmd). Root cause (corrected from PR-AA's premise):
on first launch the banner is read because NVDA reads the
terminal document when the control first gains focus — pty-speak
never announced it via the tuple-final path (the first
`PromptStart` finds `Active=None`, so no cell finalises and
`tupleFinaliseAnnounce` is `None`). On a switch the control
already has focus, so NVDA doesn't re-read, and the same
`Active=None` first-prompt means no announce either → silence.
Fix: `switchToShell` sets `announceBannerOnNextPrompt`; the
first post-switch `PromptStart` slices the freshly-reset
ContentHistory from 0 to the new prompt, trims the trailing
prompt path, cleans escape sequences (same as PR-AA), and speaks
it once. The flag is consumed on that first prompt even when the
slice is empty (a banner-less shell won't retry on the next
prompt). Switch-only — first launch keeps NVDA's focus-read, so
no double-announce. New `PR-AC shell-switch banner` Information
diagnostic.

### Cycle 51 PR-AD (2026-05-15): SpeechCursor manual navigation fed from sealed IOCells

Post-PR-AC dogfood found the Speech Cursor (Ctrl+Shift+Up/Down/
End manual review) had no record of (3) the input command line,
nor (1) the output produced after a single-key sub-prompt
response — both confirmed via the review cursor, which showed
them. Root cause (ADR 0004 §4a): SpeechCursor navigated raw
ContentHistory entries through `renderEntryWithPolicy`, which
unconditionally drops every `UserInputEcho`-tagged entry; the
command echo is `UserInputEcho`, and post-single-key-response
output is mis-tagged `UserInputEcho` because the state machine
is `Composing` after `SubPromptIdle` with no `EnterPressed`.

Per the maintainer's decision, Manual navigation is now fed from
**sealed IOCells**: at tuple-finalize, the cell's authoritative
`CommandText` + `OutputText` (from `SessionModel.extractIOCell`
— post-PR-X this already excludes history-scroll redraws and
includes post-response output) are appended to a new
SpeechCursor cell-transcript as separate navigable items (the
command line is its own item, per the maintainer's standing
request). `Ctrl+Shift+Up/Down/End` now walk this transcript
(`cellPrevious`/`cellNext`/`cellToLatest`); AutoDrive follows
the latest, Manual mode stays put on append. The legacy
ContentHistory-Seq engine (`Position`/`onAppend`/`next`/
`previous`/`toLatest`/`toMarker`/`renderEntryForManualNav`) is
**retained unchanged** for AutoDrive bookkeeping, selection-
suspend, and the Diagnostics navigability dump — only the
user-facing Manual gestures moved. Cleared on shell hot-switch
alongside the ContentHistory reset. New `SpeechCursor` cell-API
unit tests (additive; the existing 25 Seq-engine tests are
untouched).

### Cycle 51 PR-AE (2026-05-15): cursor-anchored prompt detection (root-cause fix)

The maintainer's 2026-05-15 dogfood showed garbled spoken output
on fast typing / history recall and a progress loop that dumped
everything at the end — and, correctly, flagged the
fix-one-break-another pattern as a codebase-health smell. Root
cause (single, upstream): `HeuristicPromptDetector` ignored the
cursor and PASS 2 picked the **bottommost regex match anywhere
on screen**. The instant the real prompt row was edited (history
recall / fast typing) or changed by an in-place progress redraw,
it no longer matched the bare-prompt regex, so the detector
re-locked onto a **stale scrollback `C:\…>` row** from a prior
command cycle, saw a different `rowIdx`, and fired a phantom
`PromptStart` → bogus tuple finalize → garbage announce sliced
from a stale watermark. Every recent announce-path patch
(PR-X/Y/AB) was a downstream compensation for this unstable
signal.

Fix: PASS 2 is now **cursor-anchored** — the active prompt is the
stable regex match on the cursor's row; a match on any other row
is stale scrollback and is ignored (no emission when the cursor
isn't on a clean prompt, i.e. the user is composing / output is
streaming). When the cursor is the `(-1,-1)` unit-test sentinel
the legacy bottommost-match selection is retained, so the
existing detector test corpus is unaffected (zero churn); four
additive tests pin the new behaviour. The emission gate,
dirty-flag, logging, and state are otherwise unchanged. A
follow-up audit should retire the now-redundant
announce-path compensations (PR-AB; parts of PR-X/Y) once this is
dogfood-validated.

### Cycle 52 R2 (2026-05-16): cmd OSC-133 prompt integration (Option B)

The cmd transport adapter now injects an OSC-133 shell-integration
`prompt` template at spawn (startup **and** switch-to-cmd), so cmd
emits PromptStart (`;A`) before the prompt path and CommandStart
(`;B`) after it. Replaces the brittle byte-stream
`lastEnterSeq`/first-newline heuristic with an authoritative
shell-emitted boundary for the command/output split.

Mechanism (ADR 0005/0006, **Option B** — command-line `prompt`
injection, adapter-owned): `cmd.exe /K prompt
$e]133;A$e\$p$g$e]133;B$e\` (unquoted — the value is space-free
with no cmd metacharacters, sidestepping cmd's outer-quote
stripping). Injection is gated on the resolved/target `ShellId =
Cmd`, so claude / PowerShell spawns are byte-identical.

cmd's `prompt` has no hook to emit OutputStart (`;C`) between Enter
and command output, so per the maintainer's 2026-05-16 decision the
consumer realises ADR 0005 §3's "implicit C":
`SessionModel.extractIOCell` gains a third arm — PromptStart +
CommandStart, no OutputStart — that anchors the proven PR-X
watermark split at the shell-emitted `;B` marker (the `[A,B)`
region is the prompt path and is excluded by construction).
Provenance is OSC 133, not the heuristic fallback. The pre-R2
PromptStart-only arm is byte-identical (no `CommandStart` marker
existed pre-R2 — the heuristic detector emits only `PromptStart`),
so no shell without the injection regresses. Exit-code (`;D`) is
deferred (its live `%errorlevel%` can't defer through cmd's
command-line `%`-expansion without fragile escaping; A/B is
sufficient for the split).

Diagnostics ship with the feature: an `Arm={CleanOscAC|CmdOscAB|
Heuristic}` line per extraction and `R2 cmd OSC-133 prompt
injection applied … Base=… Integrated=…` at both spawn seams. The
exact command-line string is locally unverifiable (no cmd in the
dev sandbox) — pinned by `CmdAdapterTests` and validated
end-to-end by the cmd dogfood (ADR 0005 §4 Stage B). If the
template needs adjustment it is a contained one-line change in
`CmdAdapter.Osc133PromptValue`; nothing downstream depends on *how*
cmd was told to emit OSC 133, only *that* it does.

### Cycle 52 R3a (2026-05-16): OSC-133 precedence — mute heuristic once seen

First half of R3 (ADR 0005 Stage C / ADR 0006 R3) — the
**precedence** rule. Once an `BoundarySource.Osc133` boundary is
observed in a shell session, the `HeuristicPromptDetector`'s
synthetic boundaries are no longer dispatched (`handlePromptBoundary`
sets a per-session `oscSeenThisSession` latch; `runDetector` mutes
the heuristic when set). This stops the regex detector from
competing with the authoritative shell-emitted markers — the root
cause of the announce-path compensation pile and a contributor to
KI-R2-1.

Per-shell-session and **reset only on shell-switch** (the
`switchToShell` `Ok newHost` block), *not* on alt-screen: a
cmd→claude switch must keep the heuristic (claude emits no OSC); a
cmd→cmd switch re-mutes on the new session's first OSC boundary;
alt-screen toggles don't end the session so the latch persists. The
heuristic detector itself still ticks (state stays warm) — only its
*dispatch* is gated, so the change is a pure suppression with no
detector-state divergence.

Diagnostics ship with it: a one-time Information transition log
(`R3a precedence: first OSC-133 boundary observed …`) marks the
regime change in any always-on bundle; per-occurrence Debug logs
(`R3a: muted heuristic boundary …`) detail suppressed boundaries on
demand. No unit seam (Program.fs composition-root mutable, like the
shell-switch coordinator) — dogfood-gated, the diagnostic logs are
the triage surface. The PR-X/Y/AA/AB announce-path compensation
*deletion* is the separate R3b PR.

### Cycle 52 R3b (2026-05-16): retire announce-path compensations — marker-driven tuple-final announce

Second half of R3 (ADR 0005 Stage C / ADR 0006 R3) — the
**scaffolding deletion**. The tuple-final announce now speaks the
R2-sealed IOCell's `OutputText` directly. Because `extractIOCell`'s
`;B`-anchored arm already produces a clean command/output split
(prompt path + command echo excluded by construction via the
shell-emitted CommandStart marker), the entire announce-side
compensation pile is retired:

- **PR-X** `lastEnterSeq` watermark + the `[lastEnterSeq, tail]`
  slice and its `MatchedRowText` `EndsWith` next-prompt trim — the
  exact mechanism behind **KI-R2-1** (the volume-triggered
  path-leak). Fixed by construction: a Seq-sliced IOCell field has
  no `EndsWith` heuristic to misfire.
- **PR-Y** `lastSubPromptAnnounce` already-spoken-question strip.
- **PR-AB** `lastSubmittedCommand` fast-type command-echo strip.

Net-subtractive (≈ −200 / +60 LOC in `Program.fs`) — the brittle
parts leave the core, per ADR 0006's stated R3 shape. `commandEnterSeq`
and `awaitingSubPromptEnter` are **kept** (load-bearing: they feed
`extractIOCell`'s split and gate it against sub-prompt-response
Enters — not announce compensations). The 800-char audible cap is
**kept** (a channel concern, not a compensation). The sub-prompt
*question* real-time announce is **kept** (an affordance, not the
PR-Y *strip*).

**Scope refinement (flagged):** ADR 0006 R3 names only **PR-AB/X/Y**
for deletion. **PR-AA/AC (the pre-first-prompt shell banner) is
deliberately preserved** — the banner is not a finalised cell, so
sealed-`OutputText` can't carry it; deleting it would silently
re-introduce the maintainer-flagged "switch no longer reads the
banner" regression, and it is orthogonal to the KI-R2-1
command-delta fragility.

Per the maintainer's 2026-05-16 decision (full deletion in one PR):
sub-prompt cells now announce their `OutputText`, which may
re-include the just-spoken sub-prompt question — the accepted
single-step re-derivation risk, **dogfood-gated** (test-02 / test-04
double-speech is the thing to listen for). Diagnostics: `R3b
tuple-final announce (sealed IOCell OutputText). Len=…` (Info),
body at Debug, plus `R3b command-Enter watermark …`. No unit seam
(Program.fs composition-root path).

### Cycle 52 R4a (2026-05-16): HeuristicPromptDetector namespace purify (Terminal.Core → Terminal.Shell)

First half of R4 (ADR 0006 R4-purify, folding the deferred R1.2 /
R3c tail). R1.2 physically moved `HeuristicPromptDetector.fs` into
the `Terminal.Shell` project but kept its `namespace Terminal.Core`
+ call sites ("deferred to R3 when call sites change anyway"). R4a
completes that: the file now declares `namespace Terminal.Shell`
with an explicit `open Terminal.Core` for the substrate types it
consumes (Logger / PromptBoundaryData / BoundaryKind / Cell /
CanonicalState — previously visible implicitly via the namespace),
mirroring `CmdAdapter.fs`. The three code consumers
(`Program.fs`, `Diagnostics.fs`, `HeuristicPromptDetectorTests.fs`)
gain `open Terminal.Shell`. Pure behaviour-preserving relocation
(15 +/2 −, no logic change); the Terminal.Core `///` doc-comment
mentions of the detector are unaffected.

**Maintainer-facing:** the FileLogger category for the detector
changes `Terminal.Core.HeuristicPromptDetector` →
`Terminal.Shell.HeuristicPromptDetector` — diagnostic-bundle greps
that targeted the old category string must use the new one. This
removes the last namespace-level Terminal.Core/shell-leak, setting
up R4b (extend the portability-lint CI to *enforce* the boundary).

### Cycle 52 R4b (2026-05-16): portability-lint enforces the Terminal.Core boundary

Second half of R4 — **the structural enforcement gate** ADR 0006
calls "the answer to why 51 cycles stayed brittle". The three-layer
boundary (transport / pure core / channel) is only real if CI
keeps it real. The existing `portability-lint` job (ADR 0001:
substrate must not import WPF / `Microsoft.Win32`) gains two
anchored checks for ADR 0006 R4:

- **No P/Invoke in `Terminal.Core`** — `[<DllImport …>]` /
  `extern` (the pure session core does no native interop; that
  belongs in the transport layer). Scoped to `Terminal.Core` only
  (Terminal.Audio may legitimately need native audio interop).
- **`Terminal.Core` must not depend on `Terminal.Shell`** — no
  `open Terminal.Shell` and no `<ProjectReference …Terminal.Shell…>`
  in `Terminal.Core.fsproj` (dependency direction is Shell → Core,
  never Core → Shell — the exact 51-cycle leak class).

"No shell strings" (ADR 0006 R4's third clause) is enforced
**structurally rather than via a literal grep**: with no
Terminal.Shell dependency and no P/Invoke, shell-specific
executable code/strings cannot live in `Terminal.Core` by
construction. A brittle shell-string-literal grep is deliberately
omitted — `Terminal.Core` carries many legitimate doc-comments
discussing cmd / PowerShell behaviour, and a literal grep would
false-positive on correct documentation (the too-aggressive-lint
failure mode CLAUDE.md warns against). All greps stay anchored
(`^…open`, `^…[<DllImport`, `<ProjectReference`) so doc-comment
mentions never trigger; verified zero violations on current `main`
(the boundary already holds post-R1–R4a — this just locks it).

**This completes R4.** With R1 (extract the seam) + R4 (enforce
it), the architectural boundary is structural, not disciplinary.

### Cycle 52 R4-followup (2026-05-16): health-check reports the informational version

`Ctrl+Shift+H` now announces (and logs to the bundle) the
`AssemblyInformationalVersion` alongside the existing shell / PID /
liveness / log-level / queue line — e.g. *"Pty-speak healthy.
Version 0.0.1-preview.NN+sha. cmd shell, PID …"*. Surfaced on
maintainer request from the R1–R4 foundation dogfood: a local
`git pull` + build has no release-tag in the window title (unlike
an installed preview), so there was no keyboard-only way to
sanity-check that the running build is the intended version. Reuses
the existing startup-log version resolver; no GUI walk (a
screen-reader-friendly hotkey, not a Help-menu dialog). One-line
`sprintf` extension to the existing `runHealthCheck` summary.

### Cycle 52 R3c (2026-05-16): principled spoken-watermark announce

The R1–R4 foundation dogfood surfaced two announce regressions
(#1 banner silent on fresh launch + switch; #2 interactive
commands re-read the just-spoken sub-prompt question). Root cause:
R3b correctly deleted the brittle byte-stream pile but substituted
*"tuple-final = `cell.OutputText` verbatim"* — which **ignores the
spoken-watermark**, so anything already spoken incrementally (the
sub-prompt question now; per-line progress in R6; PowerShell's
richer interaction in R5) gets re-spoken; and the banner (un-spoken
pre-prompt output, no finalised cell) had no home. This is
architectural, not a patch: R5/R6 build directly on this path.

R3c re-wires the announce to the **spoken-watermark** primitive the
codebase already had (`SpeechCursor.LastSpokenSeq` doc;
`lastAnnouncedSeq`, advanced after *every* announce incl. the
real-time sub-prompt one):

- **Tuple-final** now speaks `ContentHistory.sliceText` from
  `lastAnnouncedSeq` (the un-spoken Seq gap), not `cell.OutputText`.
  #2 fixed *by construction* — the sub-prompt question is
  `Seq ≤ watermark` and excluded; **no string strip, no PR-Y
  resurrection.** This is **not** KI-R2-1's `lastEnterSeq` (a racy
  byte-stream CR capture); `lastAnnouncedSeq` is the *settled
  post-announce* watermark on the immutable history. The trailing
  next-prompt trim reuses the same bounded `MatchedRowText` strip
  `extractIOCell` already uses for the persisted record — reliable
  here precisely *because* the lower bound is settled (KI-R2-1 was
  the racy lower bound, not the trim). `IOCell.OutputText` is
  unchanged — persistence (full) and announce (un-spoken gap)
  intentionally diverge.
- **Banner** is the same Seq-gap rule: `bannerAnnounce` slices from
  `lastAnnouncedSeq` (was a hard `0L`), and
  `announceBannerOnNextPrompt` now **defaults `true`** so fresh
  launch announces the pre-prompt banner (R3b's deletion of the old
  PR-AA `lastEnterSeq<0` path had removed the only fresh-launch
  mechanism — the prior "NVDA reads the document on focus" comment
  was disproved by the dogfood: fresh launch only said "Terminal,
  edit, blank"). Switch path unchanged structurally
  (`switchToShell` still re-arms + resets the watermark).

Net: incremental + tuple-final announces now **compose** (every
announce advances the watermark; the next speaks only the
remainder) — exactly what R5/R6 require, with **zero new
machinery** for R6's per-line streaming. Diagnostics: `R3c
tuple-final announce (un-spoken Seq gap). FromSeq=… Len=…` and
`R3c banner announce (un-spoken pre-prompt gap). FromSeq=…`.
Dogfood-gated (own NVDA matrix row); the full single-rule
unification (deleting the separate `bannerAnnounce` path entirely)
is a proven follow-on once this model is validated — walking-
skeleton: smallest correct principled step first, not bundled with
a boundary-gating restructure.

### Cycle 52 R4-followup (2026-05-16): automatic build identity (git short SHA)

A local `dotnet build`/`publish` never passes `/p:Version` (only
the release workflow does), so it always reported the
`Directory.Build.props` default `0.0.1-preview.1` — every local
build looked identical, so a dogfood could not confirm whether it
ran the commit under test or a stale build (it surfaced when a
"banner still broken" finding could not be trusted). Fixed
**automatically, no per-PR manual discipline**:

- `Directory.Build.props` gains a `SetGitShortShaSourceRevision`
  target: `git rev-parse --short HEAD` → `SourceRevisionId`; the
  .NET SDK appends it to `AssemblyInformationalVersion`. Degrades
  gracefully when git is unavailable (ContinueOnError); only fills
  when empty so SourceLink / explicit values still win on CI.
- `resolveInformationalVersion` (`Program.fs`) no longer strips
  the `+<sha>` trailer (it was discarded). `Ctrl+Shift+H` and the
  startup log now show `0.0.1-preview.1+<sha>` locally
  (`<tag>+<sha>` for releases) — match `<sha>` to `git rev-parse
  --short HEAD`.
- Canonical rule documented in `CONTRIBUTING.md` (Development
  environment) + `CLAUDE.md` (dogfood-triage: read the
  `Ctrl+Shift+H` Version line first to rule out a stale build
  before chasing a "regression"). Recommended over a manual
  PR-number convention: the SHA is automatic, exists at commit
  time, and maps directly to `git`.

### Cycle 52 R3c-followup (2026-05-16): multi-interrupt watermark-composition test

New CMD-corpus test `test-09-multi-interrupt` (maintainer-requested
from the R3c dogfood). Three distinctly-labelled output sections
separated by **two** `choice /c YN` interruptions — the
composition case the R3c spoken-watermark must hold: each section
+ each question announced exactly once, in sequence, no re-read of
an earlier segment when the watermark advances past the *second*
incremental announce. R3c was validated against a single
sub-prompt (test 02/04); this pins the multi-incremental case that
R5 (PowerShell) and R6 (per-line streaming) depend on. Wired the
full corpus pattern: `HotkeyRegistry` DU/`nameOf`/`builtIns`/
`allCommands` + `HotkeyRegistryTests` mirror + `runCmdTest` bind +
`Diagnostics → CMD Interaction Tests → Multi-Interrupt (R3c)`
menu item. NVDA matrix row `52-R3c-multi`.

### Cycle 52 R4-followup-2 (2026-05-16): SHA spoken char-by-char in Ctrl+Shift+H

Build-identity dogfood follow-up. The `+<sha>` trailer added by
the build-identity feature is a 7-char hex commit id; tokens like
`09321e7` match the `<digits>e<digits>` shape, so NVDA's number
reader voiced them as scientific notation ("9321 times ten to the
7"). `runHealthCheck` now spells the SHA character-by-character
(space-separated) in the **spoken** summary only — NVDA reads each
character. The startup log and the `Ctrl+Shift+D` bundle keep the
raw `+sha` (unchanged `resolveInformationalVersion`) for
grep/triage; this only reshapes what is voiced.

Also recorded in `docs/SESSION-HANDOFF.md` the consolidated
post-dogfood state: **#2 sub-prompt double-speech FIXED &
maintainer-validated** (R3c watermark); **KI-R2-1 / backspace /
Alt+F4 RESOLVED**; **#1 banner STILL OPEN** (silent post-R3c —
needs a SHA-confirmed bundle, no re-speculation); **output-path
either/or** and **cold-start-keyboard** tracked + deferred per
maintainer lean (the latter explicitly *not* build-identity-caused
— a separate window-activation/focus race).

### Cycle 52 R3d (2026-05-16): announce intersects watermark with the clean cell output

The SHA-confirmed dogfood bundle (build `09321e7`) proved a real
R3c regression: **every plain command re-spoke its own typed
command** — `echo X` announced as `"echo X⏎X"` instead of `"X"`.
Root cause (bundle-definitive, not speculation): R3c raw-sliced
`ContentHistory` from `lastAnnouncedSeq`, but the **idle-flush**
(`runHeartbeat`) silently bumps `lastAnnouncedSeq` to `latestSeq`
between commands — landing it on the *next* cell's CommandStart
marker, *before* that cell's command echo. The slice then
re-included the echo. (`extractIOCell` itself was correct —
`Arm=CmdOscAB CmdLen=22 OutLen=17` cleanly split command vs
output; R3c just ignored that split.)

R3d lower-bounds the announce slice at **`max commandEnterSeq
lastAnnouncedSeq`**. `commandEnterSeq` is `extractIOCell`'s
CmdOscAB output-start (the top-level command's Enter watermark,
not moved by a sub-prompt response) and sits *after* the typed
command echo → plain commands announce **output only**. For a
sub-prompt, `lastAnnouncedSeq` has advanced past the
already-spoken question (printed after `commandEnterSeq`) so it
wins → only the post-response output is announced (**dogfood #2
stays fixed** — maintainer-validated). The `max` picks whichever
correctly excludes already-heard bytes. Not KI-R2-1's racy
`lastEnterSeq`; trailing next-prompt trim and persisted
`IOCell.OutputText` unchanged. Diagnostics now log
`CommandEnterSeq` / `LastAnnouncedSeq` / `FromSeq`. (Synthetic
diagnostic-battery writes don't set `commandEnterSeq` — a
harness-only staleness, not a real-UX path.)

**#1 banner** root-caused from the same bundle: cmd emits **no
startup banner** in the maintainer's environment (first content
is the prompt path); `bannerAnnounce` correctly yields `None`, so
fresh launch is genuinely silent — not a broken announce. The
fresh-launch behaviour decision (announce the prompt vs. accept
silence) is **deferred** until after R3d lands per maintainer
direction.

### Cycle 52 R3e (2026-05-16): single-key sub-prompt re-arms Executing — fixes test-09 multi-sub-prompt

The SHA-confirmed dogfood bundle (build `62fdc2d`) validated R3d
but exposed a structural defect on the new multi-interrupt test
(`test-09-multi-interrupt.cmd`: SECTION ONE → `choice` Q1 →
SECTION TWO → `choice` Q2 → SECTION THREE). After the **first**
single-key answer the 2nd sub-prompt was never announced and the
resumed output (Seq 145–163 in the bundle) was mis-tagged
`UserInputEcho`. Root cause (bundle-definitive): a `choice`-style
sub-prompt is answered by a **single keystroke with no `\r`**, so
the `\r`→`EnterPressed` byte-path never fires. The state stayed
stuck `Composing(SinglekeySubmit=true)`; only `EnterPressed`
transitioned `Composing → Executing`, so (a) `entrySourceFor`
kept tagging cmd's resumed output `UserInputEcho`, and (b) the 2nd
`SubPromptIdle` was the blocked `Composing,SubPromptIdle → None`
arm so INTERRUPTION TWO was never surfaced.

R3e adds a `SingleKeySubmitted` `Transition`: the byte-write path
emits it on the first keystroke while
`Composing(SinglekeySubmit=true)`, driving `Composing → Executing`
(empty `SubmittedCommand`). Post-answer output is then tagged
`CmdOutput` and a subsequent `Executing,SubPromptIdle → Composing`
re-detects the 2nd sub-prompt. No-op for normal command typing
(`Composing`, not single-key) and for `Executing` (so multi-byte
writes after the transition don't re-fire). `awaitingSubPromptEnter`
self-heals at the next `PromptDetected` (no Enter to clear it,
matching the existing script-bailed-mid-sub-prompt path). Unit
tests cover all three `SingleKeySubmitted` arms +
`describeTrigger`.

### Cycle 52 R4c (2026-05-16): cmd CommandFinished completion — boundary-only deferred `;D` (pre-R5)

The pre-R5 stage the maintainer chose (2026-05-16) to make the
cmd OSC-133 event stream *complete*, so R5 (PowerShell) and R6
(per-line streaming) are genuinely shell-agnostic in the core
rather than building on a half-instrumented cmd transport. R2
shipped cmd `;A`/`;B` only and deferred `;D`. R4c prepends a
**boundary-only** deferred CommandFinished — `$e]133;D$e\`
ahead of `;A` in `CmdAdapter.Osc133PromptValue` — so cmd emits
`BoundaryKind.CommandFinished None` at the head of every prompt
(the standard Windows-Terminal cmd technique; the prior-command
"finished" marker rides the next prompt because cmd has no
post-exec hook).

**No exit code on cmd, by OS-level necessity (not a shortcut).**
Reading the shipped R2 code surfaced that the `%`-expansion
hazard the old deferral note named was never the root blocker:
cmd's `prompt`/`PROMPT` only expands `$`-metacodes, so there is
no native cmd mechanism to render the just-finished
`%errorlevel%` per prompt at all — it would need clink (a
third-party native Lua cmd enhancer) or a per-command doskey
wrapper, both rejected (dependency weight / the "patch" class
being avoided). The *boundary* is what R6 per-line streaming
needs (output region `;B` → `;D`); the exit code is a
documented cmd limitation. `ShellEvent.CommandFinished of int
option` already modelled the asymmetry — `None` for cmd,
`Some <code>` for PowerShell (R5, via `$LASTEXITCODE`). The new
template contains no `%`-expansion anywhere, so the R2
command-line-`%`-hazard is sidestepped by construction.

Net-corrective as well as net-additive: the real `;D` finalises
the cell via the clean `Some active, CommandFinished`
SessionModel arm, so the misplaced Cycle-47 synthetic
`CommandFinished`-before-`;A` compensation no longer trips for
cmd (it now fires only for the residual heuristic shell,
claude) — the correctly-placed real "end output" marker
replaces it. Consumer-side changes (both in
`Program.fs handlePromptBoundary`): (1) the `;D` boundary is
MatchedRowText-augmented like `;A`, so
`extractIOCell.stripNextPrompt` + the tuple-final announce trim
the trailing next-prompt — keeping `OutputText` and the spoken
output clean, equivalent to the pre-R4c PromptStart-interrupt
finalise; (2) the natural `CommandFinished` ContentHistory
marker is gated on a real finalise, so cmd's leading `;D` (no
prior command) and any drop-on-None cell don't inject a stray
silent "end output" SpeechCursor stop at session start (logged
once at Information: `R4c cmd CommandFinished suppressed …`).
User-visible: none on the golden path (output narration
unchanged); removes a potential stray launch-time SpeechCursor
stop. Pinned by `CmdAdapterTests` (the locally-unverifiable
string) + a new `SessionModelTests` R4c arm; end-to-end gated
by the cmd dogfood (NVDA matrix `52-R4c`, folded into the
R1–R4 foundation dogfood). ADR 0005 §3 R4c status note + ADR
0006 R4c stage.

### Cycle 52 R5a (2026-05-16): adapter-selection seam (behaviour-identical; foundation sign-off received)

First step after the maintainer's R1–R4+R4c foundation
dogfood sign-off. Recon corrected the playbook's R5a premise:
the transport is already shell-agnostic except the OSC-133
injection (already `ShellId`-gated at two sites);
`CmdAdapter.Translate` is a verbatim VT-parser wrapper; the
full `IShellAdapter` (Spawn/WriteInput) is a large
**non**-behaviour-identical refactor (spawn is
`ConPtyHost.start`-direct, input `host.WriteBytes`-direct) —
deferred to R6. Maintainer chose "literal R5a seam first".

R5a is therefore the **thin selection layer**, not full
`IShellAdapter` adoption: a single
`SessionHost.Osc133IntegratorFor : ShellRegistry.ShellId ->
(string -> string)` selector (cmd → `CmdAdapter.Integrate-
Osc133`; every other shell → identity) replacing the two
inline `if = Cmd` OSC-133 gates (`SessionHost.Resolve-
StartupShell` startup + `Program.fs` `switchToShell`).
**Byte-identical including logs:** the cmd-only "R2 cmd
OSC-133 … applied" line still fires exactly when cmd; the
single shared VT parser is still not reset across shell
switches; spawn/input paths untouched. Centralises the
per-`ShellId` dispatch so **R5b adds PowerShell in exactly
one arm**. Pinned by two `CmdAdapterTests` facts
(`Osc133IntegratorFor` Cmd-wraps / non-cmd-identity — the
non-cmd assertion flips deliberately when R5b lands). No
user-visible change. Playbook §4 R5a updated to the realized
scope + the recon correction (full `IShellAdapter` = R6).

### Cycle 52 R5b (2026-05-16): PowerShell adapter — OSC-133 emission

The PowerShell transport adapter. New
`src/Terminal.Shell/PowerShellAdapter.fs` (statics only —
Translate stays the shared shell-agnostic path per R5a;
spawn/input ConPtyHost-direct; R6 broadens). `Osc133InitScript`
is a Windows-PowerShell-5.1 `prompt` function (F# **triple-
quoted** → zero escaping risk) emitting `;D;$LASTEXITCODE`
(a **real exit code** — the asymmetry cmd structurally lacks:
cmd is `CommandFinished None`, PowerShell `Some <code>`) →
`;A` → `$($PWD.Path)>` → `;B`. Same `;A`/`;B` framing as cmd,
so PowerShell routes through the **same `extractIOCell`
`CmdOscAB` arm with zero consumer change** (`Osc133.tryParse`
already decodes `;D;<int>`); the leading `;D;0` is absorbed by
the existing `None,CommandFinished` ignore + the R4c stray-`;D`
gate exactly as cmd's leading `;D`. `IntegrateOsc133` wraps
`powershell.exe` as `-NoExit -EncodedCommand <base64-UTF16LE
of the script>` — chosen over `-Command "…"` so the produced
command line is quoting-safe by construction (the base64
alphabet is space/quote/metacharacter-free — the same
robustness property cmd's space-free `prompt` value has; the
script can't be space-free, so encode it rather than fight
CreateProcess+PowerShell quoting).

Wired through the **R5a selector**: a single `PowerShell` arm
in `SessionHost.Osc133IntegratorFor` (R5a centralised the
dispatch — no second gate site) + a distinct
`R5b PowerShell OSC-133 prompt injection applied
(startup|shell-switch)` log at the two log sites (cmd's lines
unchanged + independently greppable). `ShellRegistry`
PowerShell entry already existed (PR-J) — no registry work.

**Screen-reader posture:** the `prompt` function is a core
host hook independent of PSReadLine, so `;A`/`;B`/`;D;<code>`
emit even though PowerShell auto-disables PSReadLine under a
screen reader. We deliberately do **not** attempt `;C`
(OutputStart — would need the disabled PSReadLine hook); the
playbook's #1 R5 risk. PowerShell is therefore the screen-
reader-safe baseline on the same arm as cmd, plus an exit
code. Whether `;C` is ever reachable is the dogfood question.

Pinned by a new `PowerShellAdapterTests` (the script string +
the base64 round-trip + quoting-safety) and the flipped
`CmdAdapterTests` selector pin (PowerShell now injects — the
deliberate test-visible change R5a's pin anticipated).
**Locally unverifiable** (no PowerShell in the dev sandbox —
the cmd/R2 precedent): the script is a dogfood-tunable knob;
end-to-end gated by the maintainer's NVDA dogfood (matrix
`52-R5b`). No change to cmd/claude behaviour. Playbook §4 R5b
marked done; ADR 0005 §3 PowerShell bullet realised.

### Cycle 52 R5 closure (2026-05-16): R5b dogfood-validated; #1 risk resolved; `;C` conclusion recorded

Docs-only closure of R5 after the maintainer's R5b NVDA
dogfood. **Outcome — the playbook's #1 R5 risk resolved
positive:** switching to PowerShell works under NVDA; the
"PSReadLine … disabled … screen reader" warning reads
(expected, not a fault); `echo hi` ⏎ → NVDA speaks `hi`
(clean command/output split, the same `CmdOscAB` arm as
cmd). The `prompt`-function path emits OSC-133 correctly
even though PowerShell auto-disables PSReadLine — the
`prompt`-only baseline holds; no alternative mechanism
needed.

**R5 is functionally complete & validated** (A/B/D +
real `$LASTEXITCODE`, screen-reader-safe). The `;C`
(OutputStart) sub-goal is **answered, not deferred**:
PSReadLine is the only `;C` hook and it is disabled under
a screen reader (the only pty-speak use case), so `;C` is
unreachable **by design** — the A/B/D baseline is the
final R5 conclusion, not a stopgap; the clean `CleanOscAC`
reference waits for a future shell with a real OutputStart
hook (ADR 0006 §"Deferred to R6+" item 6). Recorded across
the playbook (status flipped to "R5 COMPLETE & VALIDATED";
R5c/R5d closed; #1-risk section flipped to RESOLVED),
SESSION-HANDOFF (current state + next = R6/P1–P5), ADR
0005 §3 (PowerShell R5 status note), ADR 0006 (R5 stage
Implemented + deferred items 6 & 7), and the `52-R5b`
matrix row (✅ PASSED). Captured the maintainer-requested
future note: a `Diagnostics → PowerShell Interaction
Tests` submenu, deferred to R6 (ADR 0006 §"Deferred to
R6+" item 7). No code; no user-visible change.

### Cycle 52 P1 (2026-05-16): heuristic-detector detection-time gate (behaviour-identical)

First of the P1–P5 pre-R6 prunings. `HeuristicPromptDetector`
was running its per-chunk regex/stability scan for
cmd/PowerShell even after OSC-133 became authoritative for the
session — its boundary was only muted at *emit* time, so the
scan was pure waste + log spam (the 2026-05-16 bundle showed
hundreds of `HeuristicPromptDetector SUPPRESSED` / `R3a: muted
heuristic boundary` lines). The prompt-detector logic is
extracted verbatim into a `runHeuristicPromptDetector` helper;
`runDetector` now invokes it only `if not oscSeenThisSession`.

**Behaviour-identical**, not a feature change: the single
notification-consumer thread means `oscSeenThisSession` cannot
flip mid-call, so "skip the scan" is exactly equivalent to the
prior "run the scan then mute/discard the boundary";
`promptDetector` is read nowhere else except the `Ctrl+Shift+D`
snapshot (which now shows its as-of-OSC state); the flag resets
on shell-switch so it stays correct across switches. **Claude
is unchanged** — it emits no OSC-133, so `oscSeenThisSession`
stays false and its detector runs every chunk exactly as
before (load-bearing). The claude-only `SelectionDetector`
(separate, own `shouldDetect` gate) is not affected. Net
effect: the one-time `R3a precedence …` Information marker
still fires; the per-chunk muted/SUPPRESSED spam and the wasted
post-OSC scan disappear. No user-visible change; CI-gated
(locally unverifiable — no `dotnet`); regression-swept by the
next NVDA dogfood. Playbook §5 P1 marked done.

### Cycle 52 P2 (2026-05-16): explicit heuristic-only guard on the Cycle-47 synthetic CommandFinished

Second of the P1–P5 pre-R6 prunings. The Cycle-47
synthetic-`CommandFinished`-before-`;A` compensation in
`Program.fs handlePromptBoundary` was already correct *by
construction* post-R4c/R5b: cmd and PowerShell finalise their
cell on the real `;D` `CommandFinished` boundary, so the
subsequent `;A` PromptStart call has `finalisedOpt = None` and
the `if finalisedOpt.IsSome && k = PromptStart` guard never
tripped for them — only genuinely-heuristic claude (a
PromptStart-while-Active interrupt finalises on the same `;A`
call) reached it. P2 makes that intent **explicit and
regression-hardened** by adding `not oscSeenThisSession` to the
guard — the **same predicate P1 uses**, the single source of
truth for "OSC-133 authoritative this session".

Golden path: **identical**. claude (`oscSeenThisSession` always
false — emits no OSC 133): **unchanged**, the synthetic marker
still fires (load-bearing). The only behaviour delta is the
intended hardening: in the defensive missed/garbled-`;D` edge,
an OSC-authoritative shell (cmd/PowerShell) no longer falls
back to the cmd-heuristic-era synthetic marker — which is
exactly the established R3a precedence principle ("once OSC
seen, mute heuristic"), not a new decision. One comment +
one `&&`-condition change; no user-visible change on the
golden path; locally unverifiable (CI is the build signal).
Playbook §5 P2 marked done.

### Cycle 52 P3 (2026-05-16): investigated; safe half is a no-op, risky half re-scoped as deferred P3b

Docs-only. P3 (audit-classified "safe-to-delete dead pathway
config keys") investigated and found mis-scoped: (a) the
**generated `config.toml` template is already clean** — it has
no `#[pathway.stream]` / `#substrate_mode` lines (the
maintainer's on-disk file showing them is a stale-build
artifact; `Ctrl+Shift+E` only creates-if-missing, never
rewrites), so that half is a no-op; (b)
`ShellPathwayConfig.PathwayId` + `resolvePathwayForShell` +
`[pathway.<id>]` parameter parsing are confirmed dead (no live
`Terminal.App` consumer — grep-verified) **but entangled in
the live `ShellPathwayConfig` record** which also carries the
live `Verbosity` → `currentShellPolicy.Streaming/PromptPath`
config consumed throughout `Program.fs`. Removing the dead bits
is a surgical, wide, locally-unverifiable record/parser
refactor of a ~1000-line core config module — deliberately not
bundled into the P1–P5 proceed-pass (no-risky-locally-
unverifiable-rip discipline). Re-scoped as deferred **P3b**
(ADR 0006 §"Deferred to R6+" item 8), low priority (inert; not
gating R6). No code change; finding + deferral recorded so a
fresh session doesn't re-attempt the "safe delete".

### Cycle 52 P4 (2026-05-16): canonical-corpus ESC-byte bug fixed (silently-dead test coverage restored)

Fourth of the P1–P5 pre-R6 prunings, and a real bug fix. The
`cmd.echo.color.error` scenario in
`tests/fixtures/canonical-interactions.toml` (the lone ANSI-
colour scenario) contained two **raw ESC (0x1B) bytes** in its
`command` value. TOML 1.0.0 forbids raw control characters in
basic strings, so Tomlyn (`CanonicalCorpus.parseFromString` →
`Toml.ToModel`) rejected the **entire document** with
`Invalid control character found \u1B` at (146,11) — meaning
the **whole** canonical-interaction corpus silently failed to
load, and every `Ctrl+Shift+D` since reported
`corpus skipped: TOML parse error` with only a `[WRN]` instead
of running the regression net. (Visible in the maintainer's
2026-05-16 bundle.)

Verified end-to-end that the consumer needs a *real* ESC at
runtime (`Scenario.Command` → Diagnostics `commandBytes` =
`Encoding.UTF8.GetBytes` → `ConPtyHost.WriteBytes`), then
re-encoded the two bytes as the TOML-1.0 standard Unicode
escape (Tomlyn decodes `\uXXXX` → U+001B → UTF-8 0x1B byte —
byte-identical to the prior intent). Applied via `sed` because
a raw 0x1B can't be round-tripped through the Edit tool — this
is exactly the CLAUDE.md test-fixture foot-gun ("use the
explicit Unicode escape, not a raw byte"). Post-fix
`grep -c $'\x1b'` on the fixture is 0; line 146 is the only
changed line; the loader is **unchanged**; the whole corpus
load is restored (not just this one scenario — the parse
failure was document-wide). No code change; CI-gated;
maintainer's next `Ctrl+Shift+D` will show a populated
`--- CANONICAL CORPUS RESULTS ---` section (diagnostic,
behaviour-neutral). Playbook §5 P4 marked done.

### Cycle 52 P5 (2026-05-16): comment-rot sweep — one genuine fix; broad rewrite (rightly) not done

Last of the P1–P5 pre-R6 prunings; right-sized like P3.
Audited with the CLAUDE.md cycle-closure rot patterns
(`stays in source`, `deletion in PR`, `until PR-`, stale
`TODO`/`FIXME`, `SessionTuple`-as-if-current, present-tense-
as-if-live pathway/`LinearTextStream`) — **all grep-empty**
except **one** genuine offender: the `PassThroughProfile.fs`
header comment asserted "the pathway owns the algorithm" in
present tense while the *same* comment correctly stated the
semantics moved to ContentHistory post-Cycle-45c (internally
contradictory and misleading to a fresh reader). Fixed
surgically — comment-only, reworded to retired-aware tense
keeping the accurate ContentHistory mapping + the Layer 3/4
framing.

The remaining `StreamPathway`/`LinearTextStream` mentions are
**accurate retired-machinery history** (the cycle-opening
audit itself classified them "historical, keep"); a broad
rewrite across ~8 core files would be negative-value churn
(destroys archaeological context, high locally-unverifiable
surface for zero correctness gain) and is **deliberately not
done**. The dangerous transitional rot the discipline targets
was already kept clean by the prior cycle-closure audits + the
in-place comment corrections shipped with R4c/P1/P2. No
behaviour change; `pathwayPump` naming unchanged (load-bearing
routing, not rot). **P1–P5 pre-R6 pruning sequence complete**
(P1/P2/P4/P5 shipped; P3 → no-op + deferred P3b). Playbook §5
P5 marked done.

### Cycle 52 R6a (2026-05-16): hybrid progress streaming — long-running commands no longer silent until they seal

First R6 feature-unlock change (ADR 0006 R6). Pre-R6a a
long-running command (`ping -n 8 …`, a multi-second `dir /s`)
was **completely silent under cmd / PowerShell until it sealed
at `;D`** — the Cycle-48 PR-E retirement of the legacy
idle-flush *body* removed the only mid-execution speech without
a principled replacement. R6a re-wires the existing idle-flush
quiescence point (the timer already runs to seal stale active
spans for the diagnostic bundle) to fire the **same clean
watermark slice the tuple-final uses**, but *during* the
`Executing` window, so output is announced progressively as it
trickles in. The seal then speaks only the un-spoken remainder
— **no double-talk**, because the R3c/R3e spoken-watermark
primitive (`52-R3c-multi`-validated) composes: each flush
advances `lastAnnouncedSeq`; the `;D` seal slices from
`max(commandEnterSeq, lastAnnouncedSeq)`.

Tightly gated, three conjuncts: (1) **`Executing` only** —
never while `Composing` at the prompt (the sub-prompt + banner
paths own those windows; mutually exclusive — R6a fires
mid-`Executing`, they fire at the boundary). (2) **`Streaming =
TupleFinalOnly` only** — the one policy with a final read to
compose against; `LineByLine` / `Off` semantics untouched. (3)
`fromSeq = max commandEnterSeq lastAnnouncedSeq` — the R3d
watermark; excludes the typed-command echo (slicing from
`lastAnnouncedSeq` alone would re-introduce the "echo hi⏎hi"
regression R3d fixed) with **no** next-prompt strip (the next
prompt does not exist yet mid-`Executing` — that is the seal's
job). claude unaffected: its `IdleFlushMs = None` ⇒ the whole
arm never runs. `ActivityIds.output` + `MostRecent`: a later
progress chunk correctly supersedes an unfinished earlier one
(identical to the tuple-final).

Diagnostic trigger (CLAUDE.md "new features ship with their
triggers"): each fired flush logs `R6a progress announce
(Executing idle-flush). CommandEnterSeq={…} LastAnnouncedSeq={…}
FromSeq={…} Len={…}` at Information — strictly-increasing
`FromSeq` across a multi-chunk command confirms the watermark
composed; absence on `echo hi` / claude confirms the gate.
Locally unverifiable (no sandbox `dotnet`); structure
re-read twice before push. NVDA matrix row `52-R6a` added.
**Dogfood-validated 2026-05-16** (matrix `52-R6a` ✅ PASSED):
the progress loop announces each output line as it appears,
echo/claude unaffected; maintainer cleared the R6b gate. Two
non-blocking observations recorded on the matrix row — a
transient first-few-echoes prompt-path-after-output that did
not reproduce even on restart (watch-item), and progress
chunks interrupting in-flight speech (expected v1 `MostRecent`
supersede; smoother queueing parked as ADR 0006 deferred-R6+
item 9).

### Cycle 52 R6b (2026-05-16): prompt-path verbosity — new context-aware "Full On Directory Change" mode

The R6b feature-unlock (ADR 0006 R6). Pre-OSC-133 the prompt
text fed to the prompt-path narrator was byte-stream
reconstruction garbage, so cmd/PowerShell were forced to the
silent `Suppress` default. OSC-133 (R2–R5) now delivers a
**clean** `;A`…`;B`-delimited prompt string, so that constraint
is gone. Maintainer decision 2026-05-16: **keep `Suppress` as
the per-shell default** (no change to the daily audio flow —
auto-drive stays silent at prompts; the prompt remains
navigable in review via `Ctrl+Shift+Up/Down`, unchanged), and
**add a new selectable mode** rather than flip the default.

New `PromptPathMode.FullOnChangeElseFinal` (`prompt_path =
"full_on_change"`; `View → Prompt Path → Full On Change`):
narrates the **full path** when the prompt differs from the
previously-narrated prompt (a `cd` / dir-changing command, or
the first prompt after a shell-switch) and **final-dir-only**
when the prompt is unchanged (several commands in the same
directory). Directory orientation on a change without repeating
the whole path every command. The existing `Full` (always
verbatim) and `FinalDirOnly` (always last-segment) modes are
unchanged.

Implementation: the "changed?" decision is stateful (it needs
the prior prompt) so it is resolved by `SpeechCursor`
(`effectivePromptPath`/`resolveOnChange`, using a new
per-cursor `LastPromptStartPayload`) *before* the pure
`ShellPolicy.trimPromptPath` call; `trimPromptPath`'s own arm
for the new case is a context-free `Full` fallback for any
direct caller. `LastPromptStartPayload` is cleared by
`SpeechCursor.reset` — which post-Cycle-45c fires on
**shell-switch only** (ContentHistory is continuous, not
per-command), so the watermark survives across commands within
a shell (making "unchanged ⇒ terse" work) and resets on a
switch (first prompt in the new shell ⇒ full path). Manual-nav
is untouched (it already forces `FinalDirOnly` for PromptStart
regardless of policy — the new mode is purely an auto-drive
verbosity). Wired through `Config` (`full_on_change`),
`HotkeyRegistry` (4th `PromptPathVerbosity` option),
`Program.fs` (menu reader/setter/cue) and the XAML menu
(`MenuItem_PromptPathVerbosity_full_on_change`).

Diagnostic trigger (CLAUDE.md "new features ship with their
triggers"): each on-change resolution logs `R6b prompt-path
on-change resolve. Changed={…} Resolved={Full|FinalDirOnly}
KeyLen={…}` at Information so a `Ctrl+Shift+D` bundle confirms
the path fired and which effective mode was chosen. Tests:
`ShellPolicyTests` (context-free fallback + all-mode coverage),
`SpeechCursorTests` (first=full, unchanged=final-dir,
changed=full, reset=full), `ConfigTests` (`full_on_change`
resolves). Locally unverifiable (no sandbox `dotnet`);
F# structure re-read twice before push. NVDA matrix row
`52-R6b` added — **dogfood-pending; gates R6c** per
walking-skeleton.

## [0.0.1-preview.18] — 2026-04-28

First preview cut from the Stage-3b state of `main`. The window now
shows live `cmd.exe` output (parser → screen → WPF rendering); the
documentation set, spec, and working conventions all reflect the
shipped-stage reality. **Unsigned preview build** — Authenticode +
Ed25519 manifest signing return before `v0.1.0`; SmartScreen will
warn on first run. See [`SECURITY.md`](SECURITY.md).

### Changed

- **Documentation audit (post-Stage-3b).** Brought README,
  `docs/ARCHITECTURE.md`, `docs/BUILD.md`, `docs/RELEASE-PROCESS.md`,
  `docs/ROADMAP.md`, `docs/ACCESSIBILITY-TESTING.md`,
  `CONTRIBUTING.md`, `SECURITY.md`, `docs/CONPTY-NOTES.md`, and
  `docs/SESSION-HANDOFF.md` in line with the actual state of `main`
  at Stage 3b. Highlights:
  - README status now distinguishes "last shipped preview" (Stage 0)
    from "on `main`" (Stages 1–3b); license dependency list reflects
    current vs future direct dependencies.
  - ARCHITECTURE adds a "current pipeline" diagram alongside the
    target one, an implementation-status column on the modules
    table, and a today/target split on the threading model.
  - CONTRIBUTING captures the F# / WPF gotchas hit during Stages 1–3b
    (`out SafeFileHandle` byref interop, `let rec` for self-referential
    class-body bindings, `internal` vs `private` constructors, F# DU
    C# interop via `IsXxx` / `.Item`, `FrameworkElement` lacking
    `Background`, `MC1002` / `NETSDK1022` / `NETSDK1047`); Tests
    section now reflects xUnit + FsCheck.Xunit (the actual frameworks)
    and the real test-project paths.
  - SECURITY annotates each "What we defend against" bullet with its
    current implementation status (most are still planned); Job Object
    deferral now consistent with `docs/CONPTY-NOTES.md`.
  - RELEASE-PROCESS workflow-step list now reflects the 12 actual
    steps in `release.yml`, including the two fail-fast gates added
    after `v0.0.1-preview.14`.
  - ROADMAP gains a Status column on the Phase 1 stage table and a
    cross-link to `docs/CHECKPOINTS.md`.
  - ACCESSIBILITY-TESTING gains rows for the only two stages with
    actual user-visible behaviour today (Stage 0 and Stage 3b); test
    fixtures section flagged as planned tooling.
- **Working conventions extracted from SESSION-HANDOFF into
  CONTRIBUTING.** SESSION-HANDOFF was carrying policy that binds every
  contributor (PR shape, CHANGELOG discipline, "`spec/` is immutable",
  "platform quirks go in `docs/CONPTY-NOTES.md`"), not just an
  inter-thread handoff. Moved those into CONTRIBUTING's "Branching and
  pull requests" section and a new "Documentation policy" section;
  SESSION-HANDOFF now points at them and keeps only the
  session-specific content (sandbox caveats, pending action items,
  Stage 4 sketch, reading order). Reading order updated so a new
  session reads SESSION-HANDOFF first, then CONTRIBUTING, then the
  rest.
- **Spec rewrite (post-Stage-3b).** Per the user's authorisation,
  applied an in-place ADR-style update to `spec/overview.md` and
  `spec/tech-plan.md` so the design contract reads true to what
  actually shipped, rather than retaining superseded choices that
  could mislead future contributors. Highlights:
  - **Elmish.WPF removed throughout.** Investigated and dropped (no
    stable .NET 9 build on nuget at the time we needed it). Replaced
    with the actual two-project F# / C# split: F# `Terminal.App`
    owns `[<EntryPoint>][<STAThread>] main`, C# `Views` library
    hosts `MainWindow.xaml` + `App.cs : Application` +
    `TerminalView.cs`.
  - **Module layout in overview.md** rewritten to match the real
    `src/` tree (`Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility`, `Views`,
    `Terminal.App`); future modules clearly marked as reserved
    names.
  - **Test framework** corrected: Expecto + FsCheck → xUnit +
    FsCheck.Xunit; per-module test projects → single `Tests.Unit/`.
  - **Stage 1 P/Invoke surface** rewritten with `nativeint&` for
    out-handle parameters; the silent `out SafeFileHandle&` byref
    bug now documented inline as a comment in the spec.
  - **Stage 1 validation criteria** rewritten around the ≥16-byte
    ConPTY init prologue (the "see directory listing" assertion the
    spec previously called for is unreliable due to the
    render-cadence finding in `docs/CONPTY-NOTES.md`).
  - **Stage 2 vs Stage 3a deferral split** clarified — the parser
    emits dispatches for everything, but `Screen.Apply` handles only
    basic-16 SGR + cursor + erase today; 256-color, truecolor, and
    DECSET are deferred with their owner-stages noted.
  - **Stage 3** annotated as having shipped split into 3a (screen
    model) + 3b (WPF rendering), with validation criteria trimmed
    to what Stage 3 alone demonstrates (cmd.exe startup banner
    renders); 256-color, resize, and typing pushed to their owner
    stages. Reference Code Map cleaned (Elmish.WPF link dropped, the
    bare FsCheck entry merged into a combined xUnit + FsCheck.Xunit
    entry).

### Added

- [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md): handoff
  document for picking up between Claude Code sessions on this
  repo. Captures the things that aren't already in other docs:
  the working conventions observed in practice (small focused
  PRs, Conventional Commits, CHANGELOG-first releases), the
  sandbox / tools caveats (no `dotnet` locally, blocked Azure
  Blob URLs, tag-push 403, GitHub MCP disconnects), the
  pre-digested Stage 4 implementation sketch, and a recommended
  reading order for new sessions. Linked from `README.md`'s
  Quick links.
- New rows in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-2-vt-parser`, `baseline/stage-3a-screen-model`,
  and `baseline/stage-3b-wpf-rendering`, each with a corresponding
  entry in the "Pending checkpoint tags" section so the maintainer
  can sweep all four pending tags in one batch from a workstation.

- **CI hygiene gates** preventing two specific bug classes that bit
  us during Stage 0 ↔ Stage 3 iteration:
  - **`actionlint` job** in `ci.yml` lints `.github/workflows/*.yml`
    on every PR. Catches the YAML / shell / expression mistakes
    that produced the silent workflow startup_failures during the
    `v0.0.1-preview.{1..5}` diagnostic loop (PowerShell heredoc body
    lines at column 0 inside an indented `run: |` block, etc.).
  - **Release-time gates in `release.yml`** running before any
    build:
    - **Target-branch gate**: fails the workflow with a clear error
      if the release was published with `target_commitish` other
      than `main`. Prevents `v0.0.1-preview.14`'s failure mode where
      the release picked up an old branch's `release.yml`.
    - **CHANGELOG gate**: fails the workflow if `CHANGELOG.md` has
      no `## [<version>]` section matching the release tag. The
      release-notes step further down silently falls back to
      `"Release X. See CHANGELOG.md for details."` otherwise; we
      almost shipped that fallback on `.2..5`.
- **Stage 3b — WPF rendering + end-to-end wiring.** First visible
  terminal surface. New `TerminalView : FrameworkElement` in
  `src/Views/TerminalView.cs` overrides `OnRender(DrawingContext)`
  per spec §3.3: contiguous cells with identical SGR attrs coalesce
  into a single `FormattedText` run; backgrounds drawn first, text
  on top; manual underline at baseline; bold/italic via
  `FormattedText.SetFontWeight` / `SetFontStyle`. Default monospaced
  font is "Cascadia Mono" with Consolas / Courier New fallbacks at
  14pt; cell metrics computed once at construction. ANSI 16-colour
  palette mapped to WPF brushes; truecolor brushes constructed
  per-call.
- `MainWindow.xaml` now hosts a `<views:TerminalView />` with
  `x:FieldModifier="public"` so F# composition code in
  `Terminal.App` can reach it across the assembly boundary.
- `Program.fs` (Terminal.App) now wires the full Stage 3 pipeline:
  on `Window.Loaded` it spawns a 120×30 `ConPtyHost` running
  `cmd.exe`, then a background `Task` reads stdout chunks, feeds
  them through the Stage 2 `Parser`, and dispatches the resulting
  `VtEvent`s back to the UI thread via `Dispatcher.InvokeAsync`
  to apply them to a single `Screen` and invalidate the
  `TerminalView`. `Application.Exit` cancels the reader and
  disposes the `ConPtyHost`.
- `src/Views/Views.csproj` gains a `ProjectReference` to
  `Terminal.Core` so the C# control can use `Screen` / `Cell` /
  `SgrAttrs` / `ColorSpec` directly.

- **Stage 3a — screen model.** `Terminal.Core` gains the data types
  per spec §3.1 (`ColorSpec` DU, `SgrAttrs` struct, `Cell` struct,
  `Cursor` mutable record) and a `Screen` class consuming `VtEvent`s
  via `Apply`. Stage 3a coverage:
  - **Print**: writes a cell at the cursor with the current SGR
    attributes, advances Col, auto-wraps to the next row at
    end-of-line, scrolls when wrapping past the bottom row.
  - **C0 controls**: BS (cursor left, clamped), HT (next 8-column
    boundary), LF (cursor down + scroll), CR (cursor to col 0).
  - **CSI cursor movement**: A/B/C/D (relative, clamped at edges),
    H/f (CUP/HVP, 1-indexed → 0-indexed).
  - **CSI erase**: J (display, modes 0/1/2), K (line, modes 0/1/2).
  - **CSI SGR**: reset (0), bold (1/22), italic (3/23), underline
    (4/24), inverse (7/27); foreground colours 30-37 + bright
    90-97 + default 39; background colours 40-47 + bright 100-107
    + default 49. Empty-param CSI m equivalent to CSI 0m. 256-colour
    and truecolor sub-parameter forms are deferred (the parser would
    need to split on `:` vs `;` first).
  - **DECSET / DECSC / OSC / DCS**: silently ignored at this stage;
    Stage 4+ adds them as their owners need them.
- Stage 3a tests in `tests/Tests.Unit/ScreenTests.fs` covering each
  of the supported behaviours plus boundary clamping (cursor at
  edges, oversize CSI A movements) and the auto-wrap → scroll
  invariant.
- **Stage 2 — VT500 parser.** `Terminal.Parser` now contains a
  pure-F# implementation of Paul Williams' DEC ANSI parser
  ([vt100.net/emu/dec_ansi_parser.html](https://vt100.net/emu/dec_ansi_parser.html)),
  matching alacritty/vte's table-driven structure. `StateMachine`
  is a stateful single-byte feeder over fourteen `VtState` cases
  (Ground, Escape, EscapeIntermediate, CsiEntry/Param/Intermediate/Ignore,
  DcsEntry/Param/Intermediate/Passthrough/Ignore, OscString,
  SosPmApcString) with the canonical alacritty caps applied
  (`MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`,
  `MAX_OSC_RAW = 1024`, `MAX_PARAMS = 16`). `Parser` exposes
  `create`/`feed`/`feedBytes`/`feedArray` for downstream consumers.
  A small UTF-8 decoder buffers continuation bytes and emits a
  single `Print of Rune` per scalar; malformed UTF-8 emits
  U+FFFD. See [`spec/tech-plan.md`](spec/tech-plan.md) Stage 2.
- The placeholder `VtEvent` discriminated union in
  `Terminal.Core/Types.fs` is replaced with the real DU per spec
  §2.2 (`Print | Execute | CsiDispatch | EscDispatch | OscDispatch
  | DcsHook | DcsPut | DcsUnhook`). Other DUs in `Types.fs` remain
  placeholders pending their owning stages.
- Stage 2 tests in `tests/Tests.Unit/VtParserTests.fs`:
  - **Fixture tests** for every byte-string example called out in
    spec §2.4 (`"Hello\r\n"`, `"\x1b[31mRed\x1b[0m"`, `"\x1b[2J"`,
    `"\x1b]0;Title\x07"`, `"\x1b[?1049h"`) plus multi-param
    SGR, default-parameter CSI, ESC dispatch (DECKPAM), CAN
    cancellation, and UTF-8 multi-byte assembly.
  - **FsCheck property tests** verifying the spec's robustness
    contract: parser never throws on arbitrary bytes; chunked feed
    equals whole-array feed; CAN (0x18) at any point returns the
    parser to `Ground`.
- [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md): rollback guide
  documenting stable development checkpoints. Defines the three
  durable references for each checkpoint (git tag in `baseline/`
  namespace, PR label `stable-baseline`, optional GitHub Release),
  the rollback procedures (read-only inspection, branch-from-baseline,
  destructive `main` reset), and the procedure for marking new
  checkpoints. Linked from `README.md`'s Quick links.
- **Stage 1 — ConPTY host.** `Terminal.Pty` library now contains the
  `Terminal.Pty.Native` P/Invoke surface (`COORD`, `STARTUPINFOEX`,
  `PROCESS_INFORMATION`, etc., and the kernel32 externs for
  `CreatePseudoConsole` / `CreatePipe` / `InitializeProcThreadAttributeList`
  / `UpdateProcThreadAttribute` / `CreateProcess`), a typed
  `PseudoConsole.create` lifecycle wrapper enforcing the strict 9-step
  Microsoft order (close ConPTY-owned handles in parent; correct
  `STARTUPINFOEX.cb`; no `CREATE_NEW_PROCESS_GROUP`), and a
  `ConPtyHost` high-level API exposing a stdin `FileStream` plus a
  `ChannelReader<byte array>` over stdout backed by a dedicated reader
  task. `SafePseudoConsoleHandle` (a `SafeHandleZeroOrMinusOneIsInvalid`
  subclass) ensures `ClosePseudoConsole` runs on disposal. See
  [`spec/tech-plan.md`](spec/tech-plan.md) Stage 1.
- Stage 1 acceptance test in `tests/Tests.Unit/ConPtyHostTests.fs`
  spawns `cmd.exe` under ConPTY and asserts the reader pipeline
  delivered at least the 16-byte ConPTY init prologue
  (`\x1b[?9001h\x1b[?1004h`). Validates the
  `CreatePipe → CreatePseudoConsole → CreateProcess → reader thread
  → channel → collectStdout` chain end-to-end. Stronger assertions
  on cmd's actual command output land in Stage 6 once a proper
  input pipeline lets us drive cmd deterministically. Windows-only;
  trivially passes on non-Windows so the suite runs unchanged on
  dev workstations.
- [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md): platform-quirks
  document for ConPTY behaviour observed in practice. First entry
  documents the **render-cadence** finding (fast-exit
  `cmd.exe /c <command>` loses its rendered output because conhost
  flushes on a timer-driven cadence and tears down before the next
  tick) plus forward-look items from `spec/overview.md` that haven't
  been hit in code yet. Linked from `README.md` and
  `docs/ARCHITECTURE.md`.
- New row in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-1-conpty-hello-world` covering the `Terminal.Pty`
  library shape and its acceptance test.

### Removed

- `.github/workflows/diagnose.yml`. Was added during the Stage 0
  release-pipeline diagnostic loop to isolate `release.yml` from
  workflow-level config issues. Its lessons live in the "Common
  pitfalls" section of [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md);
  the workflow itself is no longer needed.
- Unused `write` helper in `tests/Tests.Unit/ConPtyHostTests.fs`.
  Leftover from an earlier iteration that drove cmd via stdin; the
  working Stage 1 test asserts only on ConPTY-pipeline output
  capture, so the helper was dead code. The pattern can be re-added
  when Stage 6 lands the keyboard-to-PTY input pipeline.

## [0.0.1-preview.15] — 2026-04-27

First Stage 0 preview to ship installable artifacts. **Unsigned
preview build** — Authenticode + Ed25519 manifest signing are
deferred until before `v0.1.0`; SmartScreen will warn on first run.
See [`SECURITY.md`](SECURITY.md).

This version's binary footprint is intentionally trivial: an empty
WPF window titled "pty-speak" with `AutomationProperties.Name` set so
NVDA announces it. It exists so the deployment pipe is end-to-end
green before any terminal logic lands; future stages add the actual
ConPTY / parser / UIA work on top.

### Added

- Stage 0 shipping skeleton: F# / C# / WPF solution structure under
  [`src/`](src/) and [`tests/`](tests/) with a buildable empty-window
  app, central package management, and `TreatWarningsAsErrors=true`
  from day one.
  - F# class libraries `Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility` (placeholders for
    Stages 1–9).
  - C# WPF library `Views` hosting `MainWindow.xaml` with
    `AutomationProperties` set on the outer window. App is a plain C#
    `Application` subclass (no `App.xaml`); a Stage 0 window has no
    application-level resources.
  - F# EXE `Terminal.App` owning the `[<EntryPoint>][<STAThread>]`
    `main` that invokes `VelopackApp.Build().Run()` before any WPF
    type loads (Velopack issue #195).
  - `Tests.Unit` (xUnit + FsCheck.Xunit smoke tests) and `Tests.Ui`
    (placeholder; FlaUI work begins in Stage 4).
- CI now restores, builds, tests, publishes the app, and runs a
  Velopack `vpk pack` smoke on every PR; the resulting installer is
  uploaded as a `velopack-smoke-<run>` artifact (7-day retention).
- Release workflow keyed on `release: published` events. Maintainer
  publishes a release via the GitHub Releases UI (Target = `main`,
  prerelease checkbox set); workflow then builds, packs with
  Velopack, generates release notes from the matching CHANGELOG
  section, and updates the just-created release with the body and
  installer artifacts via `softprops/action-gh-release@v3`.

### Changed

- Release workflow simplified: SignPath Authenticode submission,
  Ed25519 release-manifest signing, and Authenticode verification
  steps are removed for the unsigned preview line. They will be
  reintroduced before `v0.1.0`; the "Re-enabling signing (deferred)"
  appendix in [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)
  keeps the procedure on file.
- CI no longer guards Restore/Build/Test on `hashFiles(...) != ''` —
  a typo in a project file now fails CI loudly instead of silently
  no-op'ing.

### Notes

- `v0.0.1-preview.{1..14}` were tagged in succession but never shipped
  installable artifacts; each was a diagnostic step in unwinding a
  silent workflow startup_failure on this repo. Root cause was a
  PowerShell `@"..."@` heredoc whose body lines were at column 0 in
  the YAML source while the surrounding `run: |` block was indented
  ten spaces — YAML literal blocks require all content lines to be
  indented at least as much as the block's first line, and the
  column-0 lines silently terminated the block, producing a malformed
  workflow file that GitHub Actions rejected at load time with no
  visible error. Fix: replace the heredoc with a properly-indented
  PowerShell array joined by newline. Documented in
  [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) so it isn't
  re-discovered the hard way.

### Project documentation (carried over from the initial scaffold)

- Specifications [`spec/overview.md`](spec/overview.md) and
  [`spec/tech-plan.md`](spec/tech-plan.md).
- Documentation scaffolding: README, [`CONTRIBUTING.md`](CONTRIBUTING.md),
  [`SECURITY.md`](SECURITY.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md),
  and supporting docs in [`docs/`](docs/).
- Issue templates for bug reports, feature requests, and accessibility
  regressions; pull request template and Dependabot configuration.
