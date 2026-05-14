# Cycle 49 — Plan (in-flight scoping)

**Snapshot**: 2026-05-14 (post-Cycle-48 closure, post-PR-#312 merge).

**Audience**: the next session — human or Claude. Self-contained;
nothing in this doc requires reading prior chat history.

**Status**: in flight. Update the "Status" column on each row
when a PR ships; flip this whole doc to *HISTORICAL* once the
closure-audit PR lands per the Cycle-closure-audit discipline
in [`CLAUDE.md`](../CLAUDE.md) §"Cycle closure audit".

## Cycle goal

Tighten the SpeechCursor + sub-prompt-aware announce surface
that landed in Cycles 45–48 so the existing tests
(`tests/manual/02-input.cmd` etc.) read cleanly:

1. SpeechCursor manual nav (`Ctrl+Shift+Up/Down/End`) jumps
   between audible chunks, not between every byte-stream entry.
2. Tuple-final announce after a sub-prompt only narrates the
   **post-Enter** content, not the entire `OutputText` from the
   top.
3. Review cursor (UIA `TextEdit` caret on `TerminalView`)
   reflects the latest content immediately — no "extra command
   to refresh" friction.
4. Shell history recall (Up/Down arrow at the live prompt)
   speaks the recalled command so the user knows what they're
   about to run, and the recall is recorded in `ContentHistory`
   with a distinct `EntrySource`.

## PRs

| #  | Branch                                                           | Status   | Scope |
|----|------------------------------------------------------------------|----------|-------|
| A  | `claude/cycle49-speech-cursor-blank-collapse`                    | merged 2026-05-14 (PR #313) | SpeechCursor manual nav skips entries whose `renderEntryForManualNav` returns `None` (Newline, Overwrite, empty TextSpan, `UserInputEcho`-sourced, boundary markers without payload). `toMarker` deliberately preserves the unfiltered jump (marker jumps are explicit). Lands this plan doc. |
| B  | `claude/cycle49-post-enter-delta-announce`                       | merged 2026-05-14 (PR #314) | `recordTransitionImpl` captures the on-screen preamble (active tuple's prompt row through cursor row) at `EnterPressed` time after a `SubPromptIdle` and writes it to `lastAnnouncedText`; tuple-finalise prefix-trim then slices the post-Enter delta from `tuple.OutputText`. |
| C  | `claude/cycle49-review-cursor-refresh`                           | merged 2026-05-14 (PR #315) | `TerminalAutomationPeer.RaiseTextChanged()` fires `AutomationEvents.TextPatternOnTextChanged` after every Announce site so NVDA's review cursor invalidates its cached `DocumentRange` and re-pulls the latest tail. |
| D  | `claude/cycle49-prompt-in-speechcursor-nav`                      | drafting | **Re-cut 2026-05-14** from the original "test-01 line-1" scope (which needs diagnostic-bundle data after A/B/C and stays open as the §"Open follow-ups" item below). New D scope: maintainer feedback "speech cursor should include the prompt as a separate item." Adds `SpeechCursor.renderEntryForManualNav` which decouples manual-nav rendering from `PromptPath` narration policy: `PromptStart` markers with payload always render to a `FinalDirOnly`-trimmed announce for navigation regardless of the per-shell PromptPath, so the user can navigate prompt-to-prompt in review even when auto-drive is silent on prompts. Auto-drive narration unchanged. |
| E1 | `claude/cycle49-csi-aware-userinputbuffer`                       | pending  | `UserInputBuffer` parses `CSI 2K` / `CSI nG` / bare `\r` clear-line + cursor-position sequences arriving during `Composing` so the buffer reflects the actual on-screen draft after a shell-side history recall (cmd's doskey, PSReadLine's history-search, bash readline). |
| E2 | `claude/cycle49-history-recall-announce`                         | pending  | On `UserInputBuffer` rewrite mid-`Composing`, announce the new buffer contents via a dedicated activity ID (probably new — `pty-speak.input-assistant` per CORE-ABSTRACTION-BOUNDARY.md §"input assistant" reserved peer pane). NVDA's `MostRecent` processing supersedes prior recall announces. |
| E3 | `claude/cycle49-draft-input-recall-entry-source`                 | pending  | Add `EntrySource.DraftInputRecall`. Recalled-buffer rewrites produce a `ContentHistory.Entry` tagged with this source, so manual nav can optionally include them but live announce + SpeechCursor AutoDrive filter them like `UserInputEcho`. Symmetric with how typed input is recorded today. Decided 2026-05-14 per maintainer answer in chat. |
| Z  | `claude/cycle49-closure-audit`                                   | pending  | Cycle closure audit per [`CLAUDE.md`](../CLAUDE.md) §"Cycle closure audit". Updates SESSION-HANDOFF, PROJECT-PLAN, ACCESSIBILITY-TESTING matrix, ADR Status notes, this doc's *HISTORICAL* banner. |

## Validation gates

Each PR's CI run on `windows-latest` is the build/test gate
per usual. NVDA-matrix walk is the cycle-level gate:

- After PR-A merges: matrix row Cycle-49-A — manual nav skips
  blanks; `Ctrl+Shift+Up` from latest in a `dir` output lands
  on the previous TextSpan, not on a Newline.
- After PR-B merges: matrix row Cycle-49-B — re-run test 02
  + test 03; sub-prompt response narrates only the post-Enter
  body.
- After PR-C merges: matrix row Cycle-49-C — UIA review-cursor
  reads the latest content without requiring an extra command.
- After PR-D merges (if needed): matrix row Cycle-49-D — test
  01 reports correct "Line 1 of N" sequence.
- After PR-E3 merges: matrix row Cycle-49-E — Up/Down at the
  live prompt narrates the recalled command; navigating back
  via SpeechCursor surfaces the recall as a distinct entry
  type.

Consolidate to a single Cycle-49-1 row in the closure-audit
(PR-Z) per the cycle-closure-audit discipline.

## Open follow-ups (need diagnostic data)

Maintainer dogfood on the post-PR-A/B/C build (2026-05-14) surfaced
three issues that need diagnostic data before they can be sized as
PRs. Bundle paste pending.

1. **Test 01 missing first line.** "This is a simple echo test."
   (script line 15) is absent from BOTH the speech narration AND
   the review-cursor document. Substrate-level issue —
   `ContentHistoryTextProvider.DocumentRange` materialises from
   `state.Entries` unfiltered, so if the line isn't visible to the
   review cursor, the bytes either didn't reach `ContentHistory`
   or got displaced. Need grep on the bundle for "This is a
   simple echo test" + the surrounding `ContentHistory` append
   log to identify which.
2. **Test 02 sub-prompt content wrong.** Maintainer hears
   "zero c windows system…" as the first audible chunk instead of
   "Enter your name:". The Cycle 48 post-PR-F echo-strip drops
   content up to the first `\n` in the accumulator; this symptom
   suggests the accumulator contains multiple preamble lines (or
   the cmd version banner / script path) that survive the
   single-line strip. Bundle grep "PR-E sub-prompt announce.
   Length=" reveals what was actually narrated.
3. **Post-Enter still narrates full output.** PR-B's
   `capturePreambleForSubPromptResponse` should overwrite
   `lastAnnouncedText` with the on-screen preamble at
   EnterPressed time so the tuple-final prefix-trim slices the
   delta. Either the capture isn't firing
   (`awaitingSubPromptEnter` never set), the captured preamble
   doesn't match `tuple.OutputText`'s prefix (screen-render
   divergence), or the capture throws (logged at Warning).
   Bundle greps "PR-B sub-prompt preamble captured" and
   "Tuple-final prefix trim" disambiguate.

## Decisions already made

- **EntrySource.DraftInputRecall over drop-on-floor for E3**
  (2026-05-14): preserve recalled drafts in ContentHistory
  with a distinct source. Symmetric with typed-input recording;
  future review-cursor / scrollback gets a faithful picture of
  what the user explored. SpeechCursor filters by default;
  manual nav can opt in via cursor parameter (TBD whether to
  expose a hotkey or just filter via Parameters).

## Open design questions

These are *not* blockers for PR-A / PR-B / PR-C / PR-D —
they're sized to resolve before PR-E1 starts.

1. Should the input-assistant activity ID (`pty-speak.input-assistant`)
   be its own NVDA-channel activity, or fold into
   `pty-speak.output` with `MostRecent`? Activity-ID inventory
   currently lives in `ActivityIds.fs`.
2. For shells that don't echo recall through the PTY (rare —
   PSReadLine does, doskey does, bash readline does), do we
   need a fallback that intercepts the keystroke at the
   TerminalView level? Likely no, but verify with test 06
   (multi-shell smoke test) if it exists.
3. Should `DraftInputRecall` entries also be sealed at
   tuple-finalise time (i.e. preserved across tuple boundaries
   in some future review-cursor scrollback), or scoped to the
   active tuple? Default: scoped to the active tuple —
   matches every other `ContentHistory.Entry`.

## Autonomy contract for Claude

Per the maintainer's note 2026-05-14: execute the cycle without
interaction unless absolutely necessary. Defined as:

- **Always OK to act without asking**: write code, push fixup
  commits, merge green PRs, move to the next PR in the
  sequence, write tests, edit docs in this cycle's scope.
- **Always must ask**: CI log paste request (sandbox cannot
  fetch); spec/ deviations; architecture decisions that span
  beyond this cycle's PR list; destructive operations.
- **Surface, don't ask** (write down + proceed): scope drift
  within a PR (note in PR description); unexpected test
  failures with a clear fix (push the fix); design choices
  with an obvious default (pick default + note in PR body).

## Predecessor docs

- [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
- [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md)
- [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
- [`docs/adr/0003-shell-interaction-state-machine.md`](adr/0003-shell-interaction-state-machine.md)
- [`docs/CYCLE-46-NEXT-STEPS.md`](CYCLE-46-NEXT-STEPS.md) — historical, but the doc shape this one mirrors.
