# ADR 0002 — Command output is delivered via a UIA TextEdit caret, not a UIA Notification

- **Status**: Accepted / Implemented (2026-05-13)
- **Date**: 2026-05-12 (Proposed); 2026-05-13 (Accepted); 2026-05-13 (Implemented across PRs #287, #288, #290, #291)
- **Deciders**: maintainer (KyleKeane)
- **Authoring item**: Cycle 46 PR-A, in response to the
  failed-keystroke-flush sequence (PRs #282 → #284 → #285 →
  #286 revert).
- **Resolutions of Open Questions §1–§5**: see "Status notes"
  at the bottom of this document. The 2026-05-13 acceptance
  recorded the maintainer's call on each open question; the
  Decision section below has been rewritten around those
  resolutions and the original framing is preserved in the
  "Open questions" section for the historical record.
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
UIA TextEdit caret on `TerminalView`'s existing automation
peer, backed by `ContentHistory`.**

Concretely (resolutions on Open Questions §1–§5 baked in):

1. **No new sibling peer is added.** `TerminalView`'s existing
   `TerminalAutomationPeer` keeps its place in the WPF
   automation tree; the work happens entirely inside that one
   peer. The peer's `AutomationControlType` flips from
   `Document` to `Edit` so NVDA treats `TerminalView` as a
   text-edit surface (resolves Open Question §2 Option B).

2. **The peer's `ITextProvider` is backed by `ContentHistory`.**
   The implementation
   (`ContentHistoryTextProvider` over
   `Func<ContentHistory.T | null>`,
   `src/Terminal.Accessibility/ContentHistoryTextRange.fs`)
   materialises the tail of `ContentHistory` via
   `ContentHistory.tailText` capped at 256 KB and exposes it
   as a single linear string range. (Pre-Cycle-46 the
   peer's `ITextProvider` was a screen-grid
   `TerminalTextProvider`; PR-D deleted that type along with
   its `TerminalTextRange` companion.)

3. **`ContentHistoryTextRange` implements the full
   `ITextRangeProvider` interface** — `Clone`, `Compare`,
   `CompareEndpoints`, `ExpandToEnclosingUnit`,
   `FindAttribute`, `FindText`, `GetAttributeValue`,
   `GetBoundingRectangles`, `GetChildren`, `GetEnclosingElement`,
   `GetText`, `Move`, `MoveEndpointByUnit`,
   `MoveEndpointByRange`, `Select`, `AddToSelection`,
   `RemoveFromSelection`, `ScrollIntoView` — covering every
   `TextUnit` value (`Character`, `Word`, `Line`,
   `Paragraph`, `Page`, `Document`, `Format`). Selection
   mutations and read-only attribute writes are no-ops to
   match the read-only surface. The full surface insures
   against NVDA-version-skew surprises (resolves Open
   Question §1).

4. **Output events that today call `Announce(text,
   ActivityIds.output, MostRecent)` will instead update
   `ContentHistory` (already happens; the substrate is
   append-only on the reader thread) and the channel side
   will raise `AutomationEvents.TextSelectionChangedEvent` on
   the peer to move NVDA's caret to the new tail.** NVDA's
   native "read from caret" path picks up the caret move and
   reads. **`SessionModel` (channel-side) is the orchestrator
   that calls the peer**; `ContentHistory` (substrate) gains
   no `ContentChanged` event — the substrate / channel
   boundary stays clean (resolves Open Question §5
   Option ◇◇). PR-C will land the wiring; PR-B does not yet
   raise the event.

5. **Output notifications stay alongside the caret-move
   event (Option ★★ Augment).** ~~PR-C drops the
   `Announce(text, ActivityIds.output, MostRecent)` call~~ —
   the initial Option ★ Replace resolution was revised
   2026-05-13 after PR-D testing showed NVDA doesn't react
   to a bare caret-move event when
   `ITextProvider.GetSelection()` is empty. Both `Announce`
   and `RaiseCaretMovedToTail` now fire on terminal output:
   `Announce` is what NVDA reads; the caret-move event is a
   defensive signal; the `ControlType=Edit` flip from clause
   1 is the load-bearing change for typing-interrupts-speech.
   `ActivityIds.output` stays in source. **Other notification
   announces stay unchanged** — menu activations, errors,
   diagnostic battery progress (`ActivityIds.diagnostic`),
   hotkey announces (`Ctrl+Shift+H` health-check,
   `Ctrl+Shift+S` session-log path), `ActivityIds.newRelease`,
   parser errors (`ActivityIds.error`).

6. **`SpeechCursor` (Ctrl+Shift+Up/Down/End) is preserved as
   a keyboard surface; its implementation delegates to the
   peer's caret in PR-D.** `Ctrl+Shift+Up` becomes "move the
   peer's caret up one line + raise
   `TextSelectionChangedEvent`" rather than firing a separate
   `Announce(text, ActivityIds.output)`. Net behaviour
   identical for the user; muscle memory preserved; the
   announce-side code path collapses to one (resolves Open
   Question §3 Option β).

7. **The user-visible interrupt path becomes typing itself.**
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

> **Historical (2026-05-13).** All four PRs in this plan
> shipped on 2026-05-12 → 2026-05-13. The §"Status notes"
> section below records the actual ship outcome and any
> deltas from this plan. The plan is kept verbatim because
> the diff between "planned" and "shipped" is a useful
> retrospective artifact.

### PR-A — This document.

Pure docs. Ships the ADR + a CHANGELOG entry naming
**Cycle 46** as the umbrella for the work. Merges under the
docs-only fast lane after the link checker passes.

### PR-B — Substrate-swap the `ITextProvider` + flip ControlType to `Edit`.

- **Adds** `src/Terminal.Accessibility/ContentHistoryTextRange.fs`
  containing `ContentHistoryTextRange` (full
  `ITextRangeProvider` over a materialised `ContentHistory`
  tail) + `ContentHistoryTextProvider` (`ITextProvider` over
  `Func<ContentHistory.T | null>`).
- **Modifies** `src/Terminal.Accessibility/TerminalAutomationPeer.fs`
  `GetAutomationControlTypeCore` to return
  `AutomationControlType.Edit` (was `Document`).
- **Modifies** `src/Views/TerminalView.cs` to construct the
  new provider (`new ContentHistoryTextProvider(() => _contentHistory)`)
  in place of the screen-grid one, and adds a
  `SetContentHistory(ContentHistory.T)` post-construction
  injection method mirroring the existing
  `SetScreen` / `SetDisplayBuffer` pattern.
- **Modifies** `src/Terminal.App/Program.fs` to call
  `window.TerminalSurface.SetContentHistory(contentHistory)`
  after the `SetScreen` call.
- **Adds** unit tests in `tests/Tests.Unit/ContentHistoryTextRangeTests.fs`
  pinning the offset arithmetic against `ContentHistory`
  fixtures. Cover: empty history, single TextSpan,
  multi-line content, `Move(Unit.Line, ±N)`,
  `Move(Unit.Character, ±N)`, `MoveEndpointByUnit`,
  `ExpandToEnclosingUnit` for every `TextUnit`, `Clone` /
  `Compare` / `CompareEndpoints`, `GetText(maxLength)`.
- **Tail cap**: 256 KB hardcoded. Rationale: ~5–10 minutes
  of SAPI speech at normal rate; bounded materialisation
  cost. Configurable later if needed.
- **No `TextSelectionChangedEvent` raised yet.** The peer
  exposes the new ContentHistory-backed text but nothing on
  the channel side moves the caret. Existing
  `Announce(text, ActivityIds.output, MostRecent)` calls
  keep firing as today.
- **Existing `TerminalTextProvider` / `TerminalTextRange`
  remain in source** (no removal in PR-B). Cleanup to PR-D
  once PR-C has validated the new path end-to-end.
- **CI gate**: full Windows build + xUnit suite.
- **User-visible change**: NVDA focus announces "edit"
  instead of "document"; NVDA's "read all" reads the
  ContentHistory tail (up to 256 KB) instead of the current
  screen-grid content. **NVDA matrix gate required** before
  merge — minimum: cmd `dir`, PowerShell `Get-Process`,
  Claude REPL turn. New matrix row Cycle 46-PRB-1 per
  ACCESSIBILITY-TESTING.md.

### PR-C — Wire output to the caret.

- **Modifies** `SessionModel` to call the peer's
  `TextSelectionChangedEvent` raise on tuple finalisation
  (channel-side orchestrator per §5 Option ◇◇; no substrate
  change). Helper method on `TerminalAutomationPeer` such as
  `RaiseCaretMovedToTail()` so the call site doesn't have to
  reach into `AutomationPeer.RaiseAutomationEvent` directly.
- **Drops** the
  `Announce(text, ActivityIds.output, MostRecent)` call for
  terminal command I/O (§4 Option ★ replace). Status-event
  announces (errors, diagnostic, hotkey, etc.) keep firing.
- **CI gate**: full Windows build + xUnit + lints.
- **NVDA matrix gate**: a new matrix row (Cycle 46-PRC-1)
  per ACCESSIBILITY-TESTING.md.

### PR-D — Polish & follow-ups.

- **Delegates `SpeechCursor` (Ctrl+Shift+Up/Down/End) to the
  caret peer** (§3 Option β). Net behaviour identical for the
  user; announce-side code path collapses to one.
- **Removes the screen-grid `TerminalTextProvider` and
  `TerminalTextRange`** (deprecated by PR-B). Possibly
  several hundred lines of code drop.
- **Trims dead code.** `ActivityIds.output` removed if PR-C
  left it unused.
- **Wires** new keyboard gestures if needed (e.g.
  `Ctrl+Shift+End` to move caret to end-of-history if NVDA's
  default `Ctrl+End` doesn't behave usefully through our
  custom peer).

## Open questions

These are the decision points where me being wrong costs us
another cycle. **All five were resolved 2026-05-13** — the
resolutions are recorded inline below and folded into the
Decision section above. Original framing preserved for the
historical record.

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

**Resolution 2026-05-13**: implement the **full
`ITextRangeProvider` interface**, not a minimum subset.
Belt-and-suspenders against NVDA-version-skew surprises;
mirrors the surface the existing screen-grid
`TerminalTextRange` already implements. Every `TextUnit`
value is handled (`Format` / `Page` / `Paragraph` degrade
to `Line` per the UIA contract). Cost: more code in PR-B,
but the implementation is mechanical given the existing
`TerminalTextRange` to reference.

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

**Resolution 2026-05-13**: **Option B — `TerminalView`
itself becomes `Edit`**. One surface for input + output;
no new sibling peer; closer to how Notepad models its text;
NVDA's text-edit reading path applies naturally because the
focused element IS the edit. The "prompt vs. emitted output"
distinction blurring is real but acceptable — the prompt
text in cmd / PowerShell IS part of the shell's emitted
output, so treating them as one stream matches the user's
mental model.

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

**Resolution 2026-05-13**: **Option β — delegation**.
`Ctrl+Shift+Up/Down/End` keep their existing key bindings;
their implementation in PR-D becomes "move the peer's caret
+ raise `TextSelectionChangedEvent`" instead of firing an
independent announce. Net behaviour identical for the user;
muscle memory preserved; the announce-side code path
collapses to one.

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

**Resolution 2026-05-13**: ~~Option ★ — replace entirely
for terminal command I/O~~. **Revised 2026-05-13** (after
PR-D shipped + maintainer preview-build testing): **Option
★★ — augment**. Maintainer test reported "no spoken output
after command; menus + other speech still work." Root cause:
NVDA doesn't read on a bare
`TextPatternOnTextSelectionChanged` raise when
`ITextProvider.GetSelection()` returns empty. Both
`Announce` and the caret-move event now fire for terminal
output:

- `Announce` is what NVDA actually reads.
- The caret-move event remains as a defensive signal for
  review-cursor / future client integrations.
- The `ControlType=Edit` flip from PR-B is the
  load-bearing change for "typing-interrupts-speech" — NVDA's
  native setting fires on key press in an Edit regardless of
  how speech was initiated, so the Cycle 46 win persists.

The "long-term fix" mentioned earlier in §4 (implement
`GetSelection()` to point at the tail when the caret-move
event fires, then drop `Announce` again) is left for a
future cycle. See ADR Status notes (2026-05-13 post-PR-D
audit entry) for the full reasoning.

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

**Resolution 2026-05-13**: **Option ◇◇ — `SessionModel` calls
the peer directly**. No `ContentChanged` event on
`ContentHistory`; the substrate stays UIA-ignorant. Channel
side already knows when tuples finalise (the existing
boundary handler in `Program.fs`); the same hook moves the
caret in PR-C.

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
- **2026-05-13**: Accepted. Maintainer resolved all five
  Open Questions:
  - §1 → full `ITextRangeProvider` interface.
  - §2 → Option B (`TerminalView` itself becomes `Edit`).
  - §3 → Option β (`SpeechCursor` delegates to caret in
    PR-D).
  - §4 → Option ★ (replace notification announces for
    terminal I/O entirely; notifications stay for menus,
    errors, diagnostics, hotkey announces, releases).
  - §5 → Option ◇◇ (`SessionModel` calls peer directly; no
    substrate change).

  Decision section rewritten to bake in the resolutions.
  PR-B (substrate-swap of `ITextProvider` from screen grid
  to `ContentHistory` + `ControlType` flip
  `Document`→`Edit`) starts now.
- **2026-05-13**: PR-C merged (commit `a5dd320`).
  Implements §"Decision" clause 4: the tuple-finalise
  `Announce(text, ActivityIds.output, MostRecent)` call in
  `Program.fs`'s boundary handler is replaced by a
  `TerminalAutomationPeer.RaiseCaretMovedToTail()` invocation
  that raises `AutomationEvents.TextPatternOnTextSelectionChanged`.
  Adds `InternalsVisibleTo("Terminal.App")` to
  Terminal.Accessibility so the composition root can call the
  helper without widening the peer's public surface. NVDA matrix
  Cycle 46-PRC-1 is the gate.
- **2026-05-13**: PR-D merged. Implements §"Decision"
  clauses 6 (`SpeechCursor` delegation, Option β) + the cleanup
  half of clause 2 (delete legacy screen-grid types):
  - `Program.fs`'s `speechCursorAnnounce` callback now
    delegates to the same `RaiseCaretMovedToTail` helper PR-C
    introduced. Auto-drive `SpeechCursor.onAppend` narration
    goes through the caret path instead of an independent
    `Announce`. Manual review-cursor hotkeys
    (`Ctrl+Shift+Up/Down/End` `runSpeechCursorNext/Previous/JumpToLatest`)
    kept their notification calls because they emit
    UI-navigation feedback (e.g. "Already at the first
    entry") which is non-terminal-content per §"Decision"
    clause 5.
  - Deleted the legacy screen-grid `TerminalTextProvider`,
    `TerminalTextRange`, and `SnapshotText` types from
    `TerminalAutomationPeer.fs` (~680 LOC). The post-PR-B
    runtime path doesn't use them; nothing in source
    referenced them after PR-B. `TerminalView.TextProvider`'s
    type stays `ITextProvider` (the interface PR-B widened
    to).
  - Deleted `tests/Tests.Unit/WordBoundaryTests.fs`: it
    pinned the deleted helpers'
    `IsWordSeparator` / `NextWordStart` / `PrevWordStart` /
    `WordEndFrom` behaviour, which is now covered by
    `ContentHistoryTextRangeTests` (Move(Word) +
    ExpandToEnclosingUnit(Word) assertions).
  - `ActivityIds.output` kept in source — still used by
    `SpeechCursor.renderEntry` (manual navigation surface)
    and `NvdaChannel.semanticToActivityId` (streaming
    routing map). Deletion would require touching those
    consumers; deferred as a future cleanup if/when it
    becomes truly unreferenced.
- **2026-05-13** (post-PR-D audit): **§4 resolution
  revised from Option ★ Replace to Option ★★ Augment.**
  Maintainer testing of the post-PR-D preview build
  reported: "I'm no longer hearing any spoken output after
  command. I can still hear menus and other speech." The
  failure mode is the one flagged in CYCLE-46-NEXT-STEPS.md
  §2's risk register: NVDA does not react to a bare
  `AutomationEvents.TextPatternOnTextSelectionChanged`
  raised by `RaiseCaretMovedToTail` when
  `ITextProvider.GetSelection()` returns an empty array.
  NVDA queries `GetSelection`, gets nothing back, reads
  nothing. Menus + diagnostic announces continued to work
  because they're on the separate `RaiseNotificationEvent`
  channel which PR-D had stopped using for terminal output.

  The post-merge audit PR restores the
  `Announce(text, ActivityIds.output)` calls in
  `Program.fs`'s `speechCursorAnnounce` callback + the
  boundary handler. Both `Announce` and
  `RaiseCaretMovedToTail` now fire on every output event:

  - `Announce` is what NVDA reads (the channel it already
    consumes correctly).
  - `RaiseCaretMovedToTail` stays as a defensive caret-
    move signal; NVDA may consume it for review-cursor
    positioning or in a future config, and the cost is one
    extra UIA event per output.
  - The `ControlType=Edit` flip from PR-B stays in place
    and is the load-bearing change for
    "typing-interrupts-speech": NVDA's "Speech interrupt
    for typed character" setting fires on any key press in
    an Edit control regardless of how the in-flight speech
    was initiated.

  Net behaviour after the audit:

  - Spoken output is back (via `Announce`).
  - Typing interrupts speech (via Edit-control + NVDA's
    native setting). This is the Cycle 46 win, preserved.
  - Long output is still one big `Announce` under
    `TupleFinalOnly`; `MostRecent` processing means a
    follow-up announce supersedes the queue. With the Edit
    control in place the user can now interrupt by typing,
    which the pre-Cycle-46 setup couldn't deliver.

  The cleaner long-term fix (still possible) is to
  implement `ITextProvider.GetSelection()` to return a
  range at the tail when the caret-move event fires; NVDA
  would then read the new selection without needing the
  separate `Announce`. That requires choosing between
  "selection = just the new content" (delta-shaped) or
  "selection = everything from prior caret to new tail"
  (range-shaped) and validating against NVDA's actual
  behaviour — scope as its own cycle when a future
  contributor takes it on.

- **Supersession**: this ADR may be revised if further NVDA
  matrix validation surfaces NVDA behaviour the resolutions
  didn't anticipate. The most likely future revision is
  implementing `GetSelection()` to drop the redundant
  `Announce` (see the post-PR-D audit entry above).
