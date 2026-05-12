# ADR 0002 — Command output is delivered via a UIA TextEdit caret, not a UIA Notification

- **Status**: Proposed
- **Date**: 2026-05-12
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 46 PR-A, in response to the
  failed-keystroke-flush sequence (PRs #282 → #284 → #285 →
  #286 revert).
- **Companion docs**:
  - [`0001-substrate-channel-dichotomy.md`](0001-substrate-channel-dichotomy.md)
    — substrate vs. channel framing this ADR refines.
  - [`../CORE-ABSTRACTION-BOUNDARY.md`](../CORE-ABSTRACTION-BOUNDARY.md)
    — §7 names `ContentHistory` + `SpeechCursor` as the current
    substrate; this ADR adds the new output-channel mechanism
    that consumes them.

## Context

Through Cycles 22-45 the NVDA output channel
(`Terminal.Accessibility` + `TerminalView.Announce`) has used
UIA's **`RaiseNotificationEvent`** primitive — specifically
`AutomationNotificationKind.Other` with an `activityId` of
`pty-speak.output` — to deliver command output to screen
readers. The contract is "we hand NVDA a string, NVDA reads
it." NVDA's processing hint
(`AutomationNotificationProcessing`) lets us pick between
`ImportantAll` (queue everything, read in order),
`MostRecent` (clear the queue, read this latest), and a few
intermediates.

This contract has now failed three times in production:

- **PR #282 (Cycle 45f → 45c)**: shipped `MostRecent` as the
  default for `ActivityIds.output`, intended to let a later
  announce displace a queued long-output read. Worked for the
  case where another `Announce` followed (the next tuple-final
  chime, a hotkey announce), but **typing into the prompt and
  pressing Alt** don't naturally fire `Announce` — typing goes
  straight to the PTY encoder, Alt activates WPF's menu
  through a separate UIA channel. So the maintainer's "I can't
  interrupt a long `dir` read by typing or pressing Alt"
  problem remained.

- **PR #284 (keystroke flush v1)**: added
  `OnPreviewKeyDown → FlushPendingOutputSpeech` that fired an
  **empty-string** `MostRecent` notification on every keystroke
  that wasn't a bare modifier. NVDA's UIA notification handler
  short-circuits on falsy `displayString` (returns before
  `speech.cancelSpeech()` is reached). Did nothing.

- **PR #285 (keystroke flush v2)**: changed the payload from
  `""` to `" "` to bypass NVDA's empty-string filter. SAPI was
  expected to render the lone space as silence (no phoneme).
  In practice NVDA verbalises the single-space notification as
  the word **"blank"** — every keystroke during a long output
  read produced "blank" without interrupting the in-progress
  read. Reverted in PR #286.

The root-cause framing from the 2026-05-12 maintainer
escalation: **a single ~19 KB output notification (the
diagnostic snapshot at 21:06:53 confirms `MsgLen=18953` for a
`dir` on the install directory) is ~5–10 minutes of speech
delivered to NVDA as one indivisible blob, and there is no
reliable way to interrupt it from outside.** Every salvage
attempt fights NVDA's design rather than working with it.

The architectural mismatch:

- **UIA `RaiseNotificationEvent` is a status-message channel.**
  Designed for short alerts ("Wi-Fi connected", "Build
  succeeded"). Long content sent through this channel becomes
  a single SAPI utterance the user cannot navigate, pause-and-
  resume, or interrupt with their own activity.
- **NVDA's natural interrupt path is the text-edit caret.**
  When the focused element is a real text-edit control (a
  `<textarea>`, Notepad's edit surface, VSCode's editor),
  NVDA's "Speech interrupt for typed character" setting
  (default on) interrupts speech the moment the user types a
  character. The interrupt is instantaneous, scoped, and
  doesn't require us to fire a notification.
- **NVDA's natural reading path for long content is
  `ITextProvider` / `ITextRangeProvider`.** "Read from here"
  (`Insert+Down` arrow), "read line" (`Insert+Up`), "read all"
  (`Insert+Down`) all operate against a caret in a UIA
  `TextPattern`-supporting peer. The user controls cadence;
  they can stop at any time.

PR #282's `MostRecent` change, PR #284 + #285's keystroke
flush attempts, and the maintainer's mounting frustration all
trace back to the same wrong choice: trying to make
`RaiseNotificationEvent` behave like a text-edit caret.

## Decision

**Command output stops being a UIA Notification. It becomes a
UIA TextEdit caret on a sibling automation peer.**

Concretely:

1. **A new `TerminalOutputAutomationPeer` is added** as a
   child peer of `TerminalView`'s existing
   `TerminalAutomationPeer`. The new peer implements
   `ITextProvider` and the minimum subset of
   `ITextRangeProvider` that NVDA actually uses (see Open
   Questions §1 for the minimum-subset audit). It exposes its
   `ControlType` as `Edit` and reports as a multi-line,
   read-only text control.

2. **The peer is backed by `ContentHistory`.** Content text
   for any given `(seq, kind)` event is materialised through
   `ContentHistory`'s existing accessors; the peer maintains
   a (`character offset` ↔ `(seq, intra-event character
   offset)`) mapping for `ITextRangeProvider` operations.

3. **Output events that today call `Announce(text,
   ActivityIds.output, MostRecent)` instead call
   `TerminalOutputAutomationPeer.AppendAndMoveCaret(text)`,
   followed by `RaiseAutomationEvent(TextSelectionChangedEvent)`.**
   NVDA's native "read from caret" path picks up the caret
   move and reads.

4. **Notification announces stay for short status events.**
   `ActivityIds.diagnostic` (battery progress),
   `ActivityIds.error` (parser errors, exceptions),
   `ActivityIds.newRelease`, hotkey announces
   (`Ctrl+Shift+H` health-check, `Ctrl+Shift+S` session-log
   path) — all of these are <1 KB status messages that
   `RaiseNotificationEvent` was designed for. They keep the
   notification path.

5. **The user-visible interrupt path becomes typing itself.**
   With a real text-edit surface, NVDA's "Speech interrupt
   for typed character" setting fires naturally — no
   keystroke-flush hack needed. Alt activates the menu
   through WPF's usual route; the menu's UIA focus-change
   event displaces the read.

## Consequences

### Positive

- **Long output is reviewable, not just spoken.** The user
  can use `Insert+Down` to read-all, `Up/Down` arrows to
  walk line-by-line, `Ctrl+End` / `Ctrl+Home` to jump,
  selection gestures for clipboard — all the standard
  text-edit affordances NVDA users already know.
- **Interruption is instantaneous and scoped.** NVDA stops
  speaking the moment the user types into the prompt.
  Doesn't require a flush notification, doesn't produce
  "blank" verbalisations, doesn't fight the queue
  semantics.
- **The architectural mismatch goes away.** UIA notifications
  go back to being short status messages; long content goes
  through the channel UIA was designed for. Future workloads
  (Claude streaming output, multi-screen-of-text builds, REPL
  history) get the right primitive for free.
- **NVDA-config-friendly.** "Read all" speed, punctuation
  level, sentence chunking — all of NVDA's user-tunable
  reading behaviour now applies to our output. We stop
  imposing one-size-fits-all chunking on the user.
- **Builds on existing substrate.** `ContentHistory` already
  exists (Cycle 45 PRs #263–#270) and is the canonical
  linear-text source per CORE-ABSTRACTION-BOUNDARY.md §7.
  This ADR adds a new channel-side consumer; the substrate
  doesn't change.
- **`SpeechCursor` (Ctrl+Shift+Up/Down/End) stays valid as a
  channel-managed review surface.** The caret approach and
  the SpeechCursor approach are complementary — see Open
  Questions §3 for the integration decision.

### Negative / costs

- **Multi-PR cycle.** Realistically a four-PR sequence
  (PR-A through PR-D, sized to match the Stage 7 discipline).
  Each PR is independently CI-gated and the user-visible
  flip happens at PR-C; PR-A and PR-B are non-shipping.
- **`ITextRangeProvider` is a non-trivial interface.** The
  minimum subset NVDA uses is much smaller than the full
  surface, but it's still ~10 methods with offset arithmetic
  that has to be exactly right. PR-B's unit-test density
  is high.
- **Focus-model question is genuinely open.** Today
  `TerminalView` is the focused UIElement. Whether the new
  peer should be a sibling or whether `TerminalView` itself
  should change ControlType to `Edit` is decision #1 of
  PR-B. See Open Questions §2.
- **The `Ctrl+Shift+Y` history-dump hotkey
  (Cycle 22b) and the `Ctrl+Shift+D` snapshot bundle
  (Cycle 25b) currently extract structured tuples from
  `SessionModel.History`; they're orthogonal to this ADR and
  keep working.** Mentioning here only to flag that they're
  NOT affected.
- **Local validation is impossible in the dev sandbox.**
  `dotnet build` doesn't run; UIA peer behaviour can't be
  verified outside Windows. Every PR in the cycle round-trips
  through CI + NVDA matrix walk. Same constraint as every
  other UIA-touching PR; not new.

## Staged implementation plan

### PR-A — This document.

Pure docs. Ships the ADR + a CHANGELOG entry naming
**Cycle 46** as the umbrella for the work. Merges under the
docs-only fast lane after the link checker passes.

### PR-B — Add `TerminalOutputAutomationPeer` (not yet wired).

- **Adds** `src/Terminal.Accessibility/TerminalOutputAutomationPeer.cs`
  implementing `AutomationPeer` + `ITextProvider` + minimum
  `ITextRangeProvider`.
- **Adds** unit tests in `tests/Tests.Unit/TextRangeProviderTests.fs`
  pinning the offset arithmetic against `ContentHistory`
  fixtures. Cover: empty history, single-event history,
  multi-event history, multi-line events, `Move(Unit.Line,
  +N)` / `Move(Unit.Character, ±N)` / `MoveEndpointByUnit`
  boundary cases.
- **No call sites change.** `TerminalView.Announce` keeps
  firing notifications on `ActivityIds.output` exactly as
  today. The new peer is added to the automation tree but
  has no content yet.
- **CI gate**: full Windows build + xUnit suite. NVDA matrix
  walk NOT required (no user-visible change).

### PR-C — Wire output to the caret.

- **Modifies** `TerminalOutputAutomationPeer` to subscribe to
  `ContentHistory` changes (probably via a new event raised
  from `Terminal.Core.ContentHistory` — needs a substrate-
  side hook).
- **Modifies** `SessionModel` / `NvdaChannel` so the
  tuple-final emit path moves the caret + raises
  `TextSelectionChangedEvent` instead of (or in addition to —
  see Open Questions §4) calling `Announce(text,
  ActivityIds.output, MostRecent)`.
- **CI gate**: full Windows build + xUnit + lints.
- **NVDA matrix gate**: a new matrix row (Cycle 46-1) per
  ACCESSIBILITY-TESTING.md. The matrix walks cmd / PowerShell
  / Claude through the canonical exemplars
  (CANONICAL-DISPLAY-CATALOG.md §1-§3). At minimum: `dir`,
  `echo hi`, `claude` REPL turn.

### PR-D — Polish & follow-ups.

- **Wires** typing-interrupts-speech via NVDA's native
  setting (the prompt textbox becomes the focused edit
  surface; NVDA's "Speech interrupt for typed character"
  takes over).
- **Decides** `SpeechCursor` (Ctrl+Shift+Up/Down/End) future
  — keep its own machinery, delegate to the caret, or
  deprecate. Resolution of Open Questions §3.
- **Wires** new keyboard gestures if needed (e.g.
  `Ctrl+Shift+End` to move caret to end-of-history if NVDA's
  default `Ctrl+End` doesn't behave usefully through our
  custom peer).
- **Trims dead code.** `ActivityIds.output` may end up unused
  if PR-C drops the notification path entirely; if so, remove
  it cleanly here.

## Open questions

These are the decision points where me being wrong costs us
another cycle. **Maintainer call required on each before the
relevant PR ships.**

### §1. Minimum `ITextRangeProvider` subset NVDA actually uses

NVDA's UIA `TextPattern` handler doesn't call every method
on `ITextRangeProvider`. The minimum subset that satisfies
"read-all", "read-from-caret", and "read-line" is roughly:

- `Clone`, `Compare`, `CompareEndpoints`
- `ExpandToEnclosingUnit(TextUnit)`
- `Move(TextUnit, count)`, `MoveEndpointByUnit(endpoint,
  unit, count)`
- `GetText(maxLength)`
- `GetAttributeValue(attributeId)` for at least
  `IsReadOnlyAttributeId`
- `GetEnclosingElement`
- `Select` (for selection-changed events)

`TextUnit` we'd need at minimum: `Character`, `Line`,
`Document`. `Format` / `Page` / `Paragraph` are optional;
returning "do the same as Line" for unsupported units is
acceptable per the UIA contract.

**Decision needed**: confirm this subset, or expand if the
NVDA review-cursor path needs more. Action item for PR-B
implementation: dump NVDA's UIA call sequence for a real
text-edit control (Notepad) as a reference.

### §2. Focus model — sibling peer vs. `TerminalView` becomes `Edit`

**Option A — Sibling peer.** Keep `TerminalView`'s peer as
today (`ControlType.Custom`). Add `TerminalOutputAutomationPeer`
as a child. Focus stays on `TerminalView`; the output peer
is reached via UIA tree traversal. NVDA's "read-all" needs
focus on the output peer, so we'd need to either move focus
when output arrives (intrusive — interrupts user typing) or
fire a `TextSelectionChangedEvent` on the unfocused peer
and trust NVDA to react.

**Option B — `TerminalView` itself becomes `Edit`.** The
existing `TerminalAutomationPeer` gets `ITextProvider`. No
new peer; the output and the input share one surface. Closer
to how Notepad models its text. Risk: the prompt-input area
becomes part of the same caret universe as the output, and
distinguishing "what the user typed" vs "what the shell
emitted" gets fuzzier.

**Option C — Sibling peer + explicit "output area received
focus" announce on output arrival.** Hybrid: keep sibling
peer, fire a brief notification "Output: <command>" on
arrival to direct the user's attention, and move focus only
when the user explicitly requests review (a new gesture like
`Ctrl+Shift+R`).

**Decision needed**: which model? Maintainer preference
informs PR-B's peer-tree decision and PR-C's wiring.
**My instinct**: Option B (the simpler model), but Option A
or C might be necessary if NVDA's text-edit reading path
won't work without focus.

### §3. `SpeechCursor` integration / future

`SpeechCursor.fs` (Cycle 45 PRs #263–#270) provides
`Ctrl+Shift+Up/Down/End` review-by-line gestures that emit
`Announce(text, ActivityIds.output)` calls. With the
caret-based reading approach, the same review surface is
reachable through NVDA's native `Up/Down` arrow / `Ctrl+End`
within the edit control.

**Option α — Keep `SpeechCursor` unchanged.** Two parallel
review surfaces. Users pick whichever they prefer.
Maintainer might use `SpeechCursor` (keyboard-only, no
mouse-cursor concerns); NVDA-natural users might use
`Insert+Down`. Both produce announces.

**Option β — Have `SpeechCursor` delegate to the caret peer.**
`Ctrl+Shift+Up` becomes "move the caret peer's caret up one
line + fire TextSelectionChangedEvent". Net behaviour
identical for the user but the announce-side code path
collapses to one.

**Option γ — Deprecate `SpeechCursor`.** Standardise on
NVDA's native gestures within the caret edit control.
Removes ~250 lines of channel-side review machinery.
Cost: users who like the `Ctrl+Shift+*` mnemonics lose them.

**Decision needed**: which integration? **My instinct**:
Option β (delegation) — preserves the keyboard surface the
maintainer has muscle-memory for while collapsing duplicate
logic.

### §4. Drop notification announces for output entirely, or keep as fallback?

PR-C's wiring change can either:

- **Option ★ Replace** the `Announce(text,
  ActivityIds.output, MostRecent)` call entirely with the
  caret move.
- **Option ★★ Augment** — fire BOTH the caret move AND a
  short notification ("Output: <command>, <line count>
  lines") for screen readers that don't yet support the
  text-edit reading path well.

**Decision needed**: ★ or ★★? **My instinct**: ★ for cmd /
PowerShell / Claude (the matrix shells); ★★ might make sense
later if we see a screen reader (JAWS, Narrator) that
doesn't track the caret well.

### §5. Does `Terminal.Core.ContentHistory` need a change-notification event?

Today `ContentHistory` is read by `SessionModel` /
`SpeechCursor` polling-style. PR-C needs the new peer to
react to appends. Options:

- **Option ◇ Add a `ContentChanged` event on
  `ContentHistory`.** Channel-side observers subscribe.
- **Option ◇◇ Have `SessionModel` (which already knows when
  tuples finalise) call the peer directly.** No substrate
  change; the channel side does the orchestration.

**Decision needed**: ◇ or ◇◇? **My instinct**: ◇◇ — keeps
the substrate / channel boundary clean (substrate doesn't
need to know about UIA peers).

## Alternatives considered

### Alternative A — Cap announce length + summary handoff

Cap `ActivityIds.output` announces at ~300 chars and append
a brief summary like "<N> more lines. Press Ctrl+Shift+End
to review." Full output reachable via `SpeechCursor`.

**Why rejected**: addresses the symptom (announce too long)
without fixing the root cause (using the wrong UIA
primitive). Still no way to interrupt mid-read except via
another announce. Long output still has to round-trip
through `SpeechCursor` even when the user just wants
"read it all, I'll listen." A reasonable intermediate fix —
viable as a Cycle 46.0 if PR-C blocks on Open Question §1
or §2 longer than expected. Recorded in PROJECT-PLAN-2026-
05-12 as a "fallback option if the caret approach hits
implementation friction".

### Alternative B — Streaming chunks by line on `ImportantAll`

Fire one announce per output line. Lets NVDA chunk
naturally per line. With a 269-line `dir`, that's 269
queued announces; the original "I can't interrupt" problem
returns because we'd need `MostRecent` semantics to be
honoured per-chunk. Lower confidence; not pursued.

### Alternative C — Replace the WPF `TerminalView` with a real WPF `TextBox` or `RichTextBox`

WPF's `TextBox` already implements `ITextProvider` through
its built-in `TextBoxAutomationPeer`. Putting our output
text into a hidden / overlay `TextBox` could deliver the
text-edit caret behaviour for free.

**Why not the primary path**: `TextBox` has its own input
handling that conflicts with `TerminalView`'s
encoder-and-write-to-PTY model. Wiring them together is
fragile. A custom peer that exposes `ITextProvider` against
our own backing store gives the same NVDA experience
without the input-handling conflict. We may end up
prototyping with `TextBox` in PR-B to validate the
text-edit reading model before committing to the custom
peer — but the shipped form is custom-peer, per the
decision above.

### Alternative D — Land the revert (PR #286) and stop

Acceptable but unsatisfying. Without a forward path the long-
output problem persists and the maintainer's working
experience stays painful. The maintainer's 2026-05-12
escalation explicitly named "this again needs a massive
redesign"; that maps to PR-A through PR-D, not "do nothing
new".

## Status notes

- **2026-05-12**: Proposed. Awaiting maintainer review +
  Open Questions §1-§5 resolutions before PR-B begins.
- **Supersession**: this ADR may be revised if Open
  Question resolutions invalidate the staged plan (e.g. if
  Option C from §2 wins, PR-B's peer-tree decision changes
  and the ADR §"Decision" needs a "Sibling-peer + focus-on-
  review" paragraph).
