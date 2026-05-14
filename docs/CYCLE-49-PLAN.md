# Cycle 49 — Plan (HISTORICAL)

> **HISTORICAL — Cycle 49 shipped 2026-05-14.** Eight feature
> PRs (A → I, no E1/E2/E3 — replaced in scope by PR-I) plus a
> closure-audit PR (this doc's final edit). Kept for the
> decision trail: scope re-cuts (D, E1/E2/E3 → PR-I, F's
> supersession of B's text-prefix-trim), open-follow-ups
> resolved in-cycle, deferred items handed off to future
> cycles. The live forward-looking documents are
> [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) and
> [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md).
>
> Things this doc says that are now untrue or stale:
>
> - The "drafting" status column entries are stale; all
>   feature PRs merged 2026-05-14. Final commits on `main`:
>   PR-A `8242e56`, PR-B `36acad6`, PR-C `1579ff7`, PR-D
>   `4b315ae`, PR-F `2f8a05a`, PR-G `335dbb9` (added
>   mid-cycle to fix duplicate-command silence), PR-H
>   `b9e250f` (added mid-cycle for test-fixture rename),
>   PR-I `904051c` (replaced E1/E2/E3 with the simpler
>   "byte-detect + screen-read" approach).
> - "Decisions already made" section refers to E3
>   (`EntrySource.DraftInputRecall`) as a decided next step.
>   PR-I rendered E1/E2/E3 unnecessary for audible behaviour;
>   E3's substrate-tagging role is now a deferred follow-up
>   (`docs/PROJECT-PLAN-2026-05-12.md` §4 short-term).
> - "Open follow-ups (need diagnostic data)" — all three
>   diagnosed from the 2026-05-14 bundle: #1 (test-01 Line 1
>   missing) resolved by PR-H fixture rename; #2 (sub-prompt
>   "zero c windows system") and #3 (post-Enter full-read)
>   resolved by PR-F.
> - "Validation gates" section refers to per-PR gates;
>   consolidated post-cycle to a single Cycle 49 matrix row
>   in `ACCESSIBILITY-TESTING.md`.

# Cycle 49 — Plan (in-flight scoping)

**Snapshot**: 2026-05-14 (post-Cycle-48 closure, post-PR-#312 merge).

**Audience**: the next session — human or Claude. Self-contained;
nothing in this doc requires reading prior chat history.

**Status**: HISTORICAL — see banner at top of file. Cycle
shipped 2026-05-14.

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
| D  | `claude/cycle49-prompt-in-speechcursor-nav`                      | merged 2026-05-14 (PR #316) | **Re-cut 2026-05-14** from the original "test-01 line-1" scope (which needs diagnostic-bundle data after A/B/C and stays open as the §"Open follow-ups" item below). New D scope: maintainer feedback "speech cursor should include the prompt as a separate item." Adds `SpeechCursor.renderEntryForManualNav` which decouples manual-nav rendering from `PromptPath` narration policy: `PromptStart` markers with payload always render to a `FinalDirOnly`-trimmed announce for navigation regardless of the per-shell PromptPath, so the user can navigate prompt-to-prompt in review even when auto-drive is silent on prompts. Auto-drive narration unchanged. |
| F  | `claude/cycle49-subprompt-last-line-and-line-count-delta`        | merged 2026-05-14 (PR #317) | Maintainer dogfood 2026-05-14 surfaced two sub-prompt-announce defects on top of the post-A/B/C build. (1) The sub-prompt announce narrated every preamble line the script printed before the prompt; NVDA reading "test-02-text-input" as "test zero two text input" mis-confused the listener. PR-F narrows the announce to the LAST non-empty line of the post-echo-strip accumulator. (2) PR-B's text-prefix-trim for the post-Enter delta failed to fire (the cursor row's content drifted between EnterPressed and tuple-finalise). PR-F replaces the prefix-trim with a line-count signal: count non-empty rows at EnterPressed; drop that many `\n`-delimited lines from `tuple.OutputText` at tuple-finalise. Robust to per-row drift. |
| G  | `claude/cycle49-remove-duplicate-output-suppression-prefix-trim` | merged 2026-05-14 (PR #318) | **Added mid-cycle 2026-05-14** after preview.122 dogfood showed "speech is unreliable and unpredictable" with `echo hi` twice in a row silencing the second announce. Root cause: tuple-final still carried `lastAnnouncedText` prefix-trim from pre-PR-B; PR-F made it useless for sub-prompt cleanup and actively wrong for duplicate commands. PR-G deletes the prefix-trim branch entirely. As a downstream effect, also resolved a menu-narration regression NVDA exhibited under the same announce-queue conditions. |
| H  | `claude/cycle49-test-01-script-clearer-labels`                   | merged 2026-05-14 (PR #319) | **Added mid-cycle 2026-05-14** for the "Line 1 of 8 missing" follow-up: bundle review confirmed `This is a simple echo test.` was in the substrate verbatim, so the issue was script-fixture labelling (Line 1 unlabelled, only Lines 2–7 labelled). PR-H reshapes the script with explicit `Line 1 of 3` → `Line 3 of 3` labels + framed intro/final messages so every audible line is countable. |
| I  | `claude/cycle49-history-recall-announce`                         | merged 2026-05-14 (PR #320) | **Replaces the original E1/E2/E3 sequence** with a single simpler implementation. Detects Up/Down arrow byte sequences (`\x1B [ A/B`, `\x1B O A/B`) in the byte-write wrapper, 100 ms debounced `DispatcherTimer` reads the prompt row, strips the `Composing.PromptText` prefix, and announces via the new `pty-speak.input-assistant` activity ID. The E1/E2/E3 CSI-aware-UserInputBuffer + dedicated rewrite pathway approach was deferred — PR-I's screen-read approach addresses the audible user need without substrate changes. E3 (`EntrySource.DraftInputRecall` tagging) deferred to a future cycle as a substrate refinement. |
| Z  | `claude/cycle49-closure-audit`                                   | this PR | Cycle closure audit per [`CLAUDE.md`](../CLAUDE.md) §"Cycle closure audit". Updates SESSION-HANDOFF, PROJECT-PLAN, ACCESSIBILITY-TESTING matrix, ADR Status notes, this doc's *HISTORICAL* banner. |

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

## Open follow-ups

Maintainer dogfood on the post-PR-A/B/C build (2026-05-14)
surfaced three issues. Bundle paste 2026-05-14 06:26 (preview.121)
diagnosed each; PR-F addresses #2 + #3, #1 remains for review.

1. **Test 01 "Line 1 of 8" missing — likely recognition, not
   substrate.** The bundle's `CONTENT HISTORY` section shows
   `This is a simple echo test.` IS in ContentHistory at the
   expected position; the tuple-final announce log shows it
   WAS narrated (`Length=266`,
   `MsgHead==== PTYSPEAK-TEST-START: test-01-echo ===This is a simple e...`).
   Working hypothesis: the script's "Line 1" is implicit
   (it doesn't say "Line 1 of 8" literally — only Lines 2-7
   are labelled, with Line 1 = `This is a simple echo test.`
   and Line 8 = `Last line. ...`), and NVDA reading the
   whole 266-char output as one continuous utterance makes
   the unlabelled line easy to miss. **Remediation pending
   maintainer confirmation**: either (a) rename the script
   lines to be explicit ("Line 1 of 8. This is a simple
   echo test." etc.) — purely a test-fixture change, no
   substrate code; or (b) genuine substrate bug — would
   need the user to confirm via review-cursor navigation
   that the line is in fact absent. Pending response.
2. **Test 02 sub-prompt: full preamble narrated, including
   "test-02" pronounced "zero two".** Bundle's `PR-E
   sub-prompt announce. Length=239` confirms the announce
   body was the full 239-char preamble (start-marker,
   prelude line, prompt). PR-F narrows the announce to the
   LAST non-empty line of the accumulator (the actual
   prompt text where the cursor is parked).
3. **Post-Enter still narrates the full output.** Bundle
   shows `PR-B sub-prompt preamble captured. PromptRow=16
   CursorRow=20 Length=139` followed by
   `Tuple-final announce. Length=220` with no
   "Tuple-final prefix trim" log line — confirming the
   `text.StartsWith(lastAnnouncedText)` check returned
   false (per-row content drift in the 175 ms between
   EnterPressed and tuple-finalise). PR-F replaces the
   prefix-trim with line-count slicing: capture the count
   of non-empty rows at EnterPressed; drop that many
   `\n`-delimited lines from `tuple.OutputText` at
   tuple-finalise. Robust to drift.

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
