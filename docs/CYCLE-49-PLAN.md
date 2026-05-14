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
| A  | `claude/cycle49-speech-cursor-blank-collapse`                    | drafting | SpeechCursor manual nav skips entries whose `renderEntryWithPolicy` returns `None` (Newline, Overwrite, empty TextSpan, `UserInputEcho`-sourced, boundary markers without payload). `toMarker` deliberately preserves the unfiltered jump (marker jumps are explicit). Lands this plan doc. |
| B  | `claude/cycle49-post-enter-delta-announce`                       | pending  | `ShellInteraction` records `ContentHistory.Length` as a per-tuple watermark on `Composing→Executing` transition that follows a sub-prompt announce. `handlePromptBoundary` tuple-final announce slices `OutputText` from the watermark (or 0 if no sub-prompt occurred). Silently skips when the post-Enter body is empty. |
| C  | `claude/cycle49-review-cursor-refresh`                           | pending  | Investigate why `TerminalAutomationPeer.RaiseCaretMovedToTail` (Cycle 46 PR-C) does not invalidate the UIA `TextEdit` cache so the review cursor needs an extra command to pick up the latest content. Likely missing `TextChanged` / `AutomationEvents.StructureChanged` raise. Scope sizing to follow investigation. |
| D  | `claude/cycle49-line-1-of-8`                                     | pending  | Verify whether test 01's "Line 1 of 8 missing" reproduces after PR-A + PR-B (which may resolve it — the missing "Line 1" is likely a leading blank entry being numbered, or a watermark issue). If still reproducing, separate fix. |
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
