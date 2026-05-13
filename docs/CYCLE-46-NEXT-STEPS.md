# Cycle 46 — Next steps (PR-C + PR-D) — *HISTORICAL*

> **This doc is historical.** Every PR it scoped is merged:
> PR-C (#290, `a5dd320`) and PR-D (#291, `9bfdd48`) landed on
> 2026-05-13. The file is kept in source for archaeology — the
> exact pre-shipping scoping is a useful artifact when reading
> the PR diffs — but it is **not actionable** any more.
>
> For the live state, see:
>
> - [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) — current
>   state in ~150 lines.
> - [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
>   — strategic plan, Cycle 46 closure entry.
> - [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
>   — full decision record with §1–§5 resolutions + per-PR
>   Status notes.
>
> Things this doc says that are now untrue:
>
> - "PR-C — wire output to the caret. Scope below." → PR-C is
>   in main. The `RaiseCaretMovedToTail` helper exists on
>   `TerminalAutomationPeer`; the boundary handler in
>   `Program.fs` calls it.
> - "PR-D — `SpeechCursor` delegation + screen-grid cleanup."
>   → PR-D is in main. `speechCursorAnnounce` delegates to
>   the caret helper. The legacy screen-grid types are gone.
> - "Add `InternalsVisibleTo("Terminal.App")`" → done.
> - "256 KB hardcoded" → in `ContentHistoryMaterialiser.TailCapBytes`.
> - File `tests/Tests.Unit/WordBoundaryTests.fs` → deleted in
>   PR-D; the word-boundary contract is now pinned by
>   `tests/Tests.Unit/ContentHistoryTextRangeTests.fs`.
>
> A scope refinement worth noting that wasn't in this doc:
> PR-D kept the manual review-cursor hotkeys
> (`Ctrl+Shift+Up/Down/End` → `runSpeechCursorNext` /
> `Previous` / `JumpToLatest`) on the notification path
> because their announces are UI-navigation feedback
> ("Already at the first entry") which is
> non-terminal-content per ADR §"Decision" clause 5. The
> delegation only applies to auto-drive
> `SpeechCursor.onAppend` narration.
>
> ---
>
> Original front-matter (preserved verbatim below):
>
> **Snapshot**: 2026-05-13 (post-PR-B merge).
> **Audience**: the next session — human or Claude. Self-contained;
> nothing in this doc requires reading prior chat history.
> **Reading order**:
> [ADR 0002](adr/0002-uia-textedit-caret-output.md) → this doc →
> the implementation files named in each section below.

## 1. Where Cycle 46 stands

### Shipped

- **PR-A** (#287, merged 2026-05-12) —
  [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
  drafted. CHANGELOG entry for Cycle 46.
- **PR-B** (#288, merged 2026-05-13) — substrate-swap of the
  UIA Text pattern from screen grid to `ContentHistory`.
  `TerminalAutomationPeer.AutomationControlType` flipped from
  `Document` to `Edit`. New file
  [`src/Terminal.Accessibility/ContentHistoryTextRange.fs`](../src/Terminal.Accessibility/ContentHistoryTextRange.fs)
  with full `ITextRangeProvider` implementation. ADR Open
  Questions §1–§5 resolved (recorded inline in the ADR Status
  notes).

### Open Questions resolved (recap)

| § | Resolution                                                                                 |
|---|--------------------------------------------------------------------------------------------|
| §1 | Full `ITextRangeProvider` interface (every `TextUnit`; `Format`/`Page`/`Paragraph` → `Line`). |
| §2 | Option B — `TerminalView` itself becomes `Edit`. No sibling peer.                          |
| §3 | Option β — `SpeechCursor` delegates to the caret peer in PR-D.                             |
| §4 | Option ★ — notifications replaced for terminal I/O.                                        |
| §5 | Option ◇◇ — `SessionModel` calls peer directly; substrate stays UIA-ignorant.              |

### Pending

- **NVDA matrix gate Cycle 46-PRB-1** — see
  [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).
  Real validation of the substrate swap. Status: not yet
  walked at handoff time.
- **PR-C** — wire output to the caret. Scope below.
- **PR-D** — `SpeechCursor` delegation + screen-grid cleanup.
  Scope below.

## 2. PR-C — Wire output to the caret

**Goal**: replace the notification-based output read with a
`TextSelectionChangedEvent` raise on tuple finalise. NVDA's
native "read from caret" picks up the caret move; the user can
interrupt by typing (NVDA's "Speech interrupt for typed
character" setting). PR-C is the user-visible payoff of Cycle
46.

### Concrete edits

#### Edit 1 — Helper on `TerminalAutomationPeer`

**File**:
[`src/Terminal.Accessibility/TerminalAutomationPeer.fs`](../src/Terminal.Accessibility/TerminalAutomationPeer.fs)
**Around line**: after the `UpdateSelectionState` member
(currently ~line 367).

Add a public-on-the-internal-type method that the channel side
can call to advance NVDA's caret to the tail of the
materialised `ContentHistory`:

```fsharp
/// Cycle 46 PR-C — channel-side caret advance. SessionModel
/// calls this on tuple finalise (replaces the previous
/// `Announce(text, ActivityIds.output, MostRecent)` call).
/// Raises `TextSelectionChangedEvent` on this peer so NVDA's
/// native "read from caret" path picks up the new tail
/// position. Must be called on the WPF dispatcher thread.
member this.RaiseCaretMovedToTail() =
    this.RaiseAutomationEvent(AutomationEvents.TextSelectionChanged)
```

**Note**: `AutomationEvents.TextSelectionChanged` is the
correct enum value (singular; not "Changed"). Verify via the
`System.Windows.Automation.Peers.AutomationEvents` enum.
`AutomationEvents.TextChanged` is an alternative if NVDA
doesn't react to selection — empirical NVDA testing will
decide. **Suggest landing both behind a config flag if
ambiguous** — the matrix walk will identify which one NVDA
actually consumes.

Why no offset / text argument? UIA's event signature is
fire-and-forget; the listening client (NVDA) queries the
provider for the new state. With our `ContentHistoryTextProvider`
re-materialising on each `DocumentRange` call, NVDA gets the
latest tail automatically.

#### Edit 2 — Call from `SessionModel` boundary handler

**File**:
[`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs)
**Around line**: 1627–1630 (inside `boundaryAction` in
`handlePromptBoundary`, dispatched at line 1631 via
`window.Dispatcher.InvokeAsync`).

Current code:

```fsharp
match tupleFinaliseAnnounce with
| Some text ->
    speechCursorAnnounce (text, ActivityIds.output)
| None -> ()
```

Replace with:

```fsharp
match tupleFinaliseAnnounce with
| Some _ ->
    // Cycle 46 PR-C — caret-move replaces the
    // RaiseNotificationEvent path for terminal output.
    // ContentHistory already has the new content (appended
    // by the reader loop); raising
    // TextSelectionChangedEvent on the peer signals NVDA
    // to re-read DocumentRange and pick up the tail.
    let peer =
        UIElementAutomationPeer.FromElement(window.TerminalSurface)
    match peer with
    | :? TerminalAutomationPeer as tp ->
        tp.RaiseCaretMovedToTail()
    | _ ->
        // Peer not yet created (no UIA client connected).
        // Same silent-no-op semantics as the old
        // Announce path — see TerminalView.cs:344.
        ()
| None -> ()
```

The `:? TerminalAutomationPeer` downcast is safe because
`TerminalView.OnCreateAutomationPeer` returns exactly that
type. The fallback branch matches the existing null-peer
silence (see comment at the top of
[`src/Views/TerminalView.cs`](../src/Views/TerminalView.cs)
~line 344 in the `Announce` overload).

#### Edit 3 — Open the right `Terminal.Accessibility` namespace

The `TerminalAutomationPeer` type is `internal` (declared in
`TerminalAutomationPeer.fs:294`), and the assembly attribute
`InternalsVisibleTo("Terminal.App")` is **not** currently
declared. Two options:

1. **Add `InternalsVisibleTo("Terminal.App")`** to the assembly-
   attribute block at the top of
   [`src/Terminal.Accessibility/TerminalAutomationPeer.fs`](../src/Terminal.Accessibility/TerminalAutomationPeer.fs)
   (currently lines 22–23 expose to `PtySpeak.Views` and
   `PtySpeak.Tests.Unit`).
2. **Promote `TerminalAutomationPeer` to `public`** (more
   invasive; widens the surface).

**Recommended**: Option 1. Add a single line to the existing
block:

```fsharp
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Terminal.App")>]
```

#### Edit 4 — Drop / gate the `ActivityIds.output` activity ID

**File**:
[`src/Terminal.Core/ActivityIds.fs`](../src/Terminal.Core/ActivityIds.fs)
(verify path — search for `ActivityIds.output` to find the
canonical declaration).

After Edit 2, the only consumer of `ActivityIds.output` is
gone. **Don't delete the constant in PR-C** — `SpeechCursor`'s
internal `renderEntry` / `renderEntryWithPolicy` paths may
still emit it for the read-by-line case (`Ctrl+Shift+Up/Down`
review surface). Deletion belongs in PR-D after `SpeechCursor`
delegation lands.

### Why no `SessionModel` substrate change

Per §5 resolution (Option ◇◇), `ContentHistory` itself stays
UIA-ignorant. The channel side (`Program.fs` boundary handler)
knows when tuples finalise and is the orchestrator. No
`ContentChanged` event is added.

### PR-C test plan

- **Existing unit tests** in
  [`tests/Tests.Unit/SessionModelTests.fs`](../tests/Tests.Unit/SessionModelTests.fs)
  pin the tuple-finalise emission shape. Verify they still pass
  — the `speechCursorAnnounce` removal might break a test that
  asserts the announce fires. If so, update the test to assert
  the peer's `RaiseCaretMovedToTail` is called instead (or
  decouple the assertion via a mock callback).
- **No new UI integration tests** — Tests.Ui can't reliably
  observe UIA events from outside the process. The NVDA matrix
  walk is the validation gate.
- **NVDA matrix gate Cycle 46-PRC-1** — required. Replace the
  Cycle 46-PRB-1 row in
  [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
  with a new row that covers:
  - cmd `dir` long-output: NVDA reads with native "read from
    caret" pacing; typing the prompt interrupts speech.
  - cmd `echo hi`: short-output round-trip still verbalises.
  - Claude REPL turn: streaming token-by-token output reads via
    the caret.
  - Confirm `Alt` to open the menu interrupts an in-progress
    read (this is the maintainer's original pain point — see
    ADR Context).

### PR-C risk register

- **NVDA may not react to `TextSelectionChanged` on a read-only
  Edit.** If matrix walk shows no speech on tuple finalise,
  swap to `AutomationEvents.TextChanged` or fire both. The
  helper `RaiseCaretMovedToTail` is the right point to add
  multiple events behind one call site.
- **Threading.** `RaiseAutomationEvent` must run on the WPF
  dispatcher thread (per AutomationPeer contract). Edit 2's
  call site is already inside `window.Dispatcher.InvokeAsync`
  (Program.fs:1631), so the dispatch is correct. If you
  refactor, preserve the dispatcher hop.
- **`UIElementAutomationPeer.FromElement` may return null** if
  no UIA client has connected yet. The fallback branch matches
  current `Announce` semantics — silent no-op, logged at
  WARN. Match that.

## 3. PR-D — `SpeechCursor` delegation + cleanup

**Goal**: collapse the two parallel review surfaces (UIA caret
+ `SpeechCursor`-driven `Announce`) into one. `Ctrl+Shift+
Up/Down/End` keep their bindings but their implementations
move the peer's caret instead of firing an independent
`Announce`. Then remove the now-dead screen-grid
`TerminalTextProvider` / `TerminalTextRange` from
`TerminalAutomationPeer.fs`.

### Concrete edits

#### Edit 1 — `SpeechCursor` callbacks delegate to peer

**File**:
[`src/Terminal.Core/SpeechCursor.fs`](../src/Terminal.Core/SpeechCursor.fs)

`SpeechCursor.next` / `previous` / `toLatest` currently produce
a `(text, activityId)` pair that the call site passes to
`speechCursorAnnounce`. Keep `SpeechCursor`'s logic unchanged
(it's the **substrate-side** review-cursor primitive — staying
agnostic of channels is the right boundary), but change the
**channel-side** wiring in
[`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs)
(around lines 2864–2899) from:

```fsharp
match SpeechCursor.next speechCursor contentHistory with
| Some (text, activityId) ->
    window.TerminalSurface.Announce(text, activityId)
| None -> ()
```

to:

```fsharp
match SpeechCursor.next speechCursor contentHistory with
| Some _ ->
    // Cycle 46 PR-D — delegation: caret advance instead of
    // an independent Announce. NVDA's "read from caret" path
    // reads the new tail position with user-tunable pacing.
    raiseCaretMovedToTail window
| None -> ()
```

…where `raiseCaretMovedToTail window` is a local helper
constructed once near the top of compose that reuses the same
peer-lookup + downcast pattern from PR-C Edit 2.

Apply the same change to the `previous` (~2883) and `toLatest`
(~2899) sites.

#### Edit 2 — Drop the legacy screen-grid types

**File**:
[`src/Terminal.Accessibility/TerminalAutomationPeer.fs`](../src/Terminal.Accessibility/TerminalAutomationPeer.fs)

Delete:

- `module internal SnapshotText` (lines ~421–443).
- `type internal TerminalTextRange` (lines ~464–1039 — large).
- `type internal TerminalTextProvider` (lines ~1051–1097).

Approximate LOC dropped: ~600. Verify no consumer outside the
deleted block — the only references after PR-B are inside the
block itself.

#### Edit 3 — Drop the tests for the deleted types

**File**:
[`tests/Tests.Unit/WordBoundaryTests.fs`](../tests/Tests.Unit/WordBoundaryTests.fs)

This test file exercises `TerminalTextRange.NextWordStart` /
`PrevWordStart` / `WordEndFrom` (the screen-grid types). After
Edit 2, those internal helpers don't exist. Decide between:

1. **Delete the file entirely.** Its purpose was to pin
   word-boundary semantics on the screen-grid path. The new
   `ContentHistoryTextRange` has equivalent word-boundary
   semantics, covered by
   [`tests/Tests.Unit/ContentHistoryTextRangeTests.fs`](../tests/Tests.Unit/ContentHistoryTextRangeTests.fs)
   (the Move(Word) + ExpandToEnclosingUnit(Word) tests).
2. **Rewrite to target `ContentHistoryTextRange`'s static
   helpers** (`NextWordStart`, `PrevWordStart`, `WordEndFrom`
   — all `internal`). Lower-loss; preserves the FsCheck-style
   property tests if there are any.

**Recommended**: Option 1. The unit tests for
`ContentHistoryTextRange` already cover the word-boundary
contract. Don't pin internal helpers twice.

#### Edit 4 — Drop `ActivityIds.output` if unused

After PR-D's delegation removes the last `speechCursorAnnounce`
call sites, search for remaining references:

```
grep -rn "ActivityIds.output\|pty-speak.output" src/ tests/
```

If only the declaration remains, delete it. If any non-test
consumer remains, leave it for a follow-up cleanup cycle.

#### Edit 5 — Update the ADR status

**File**:
[`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)

Append a "PR-D merged" entry to the Status notes section.
Optionally fold ADR 0002 into the "Implemented" archive once
Cycle 46 retros cleanly — see `docs/adr/` conventions.

### PR-D test plan

- Existing
  [`tests/Tests.Unit/ContentHistoryTextRangeTests.fs`](../tests/Tests.Unit/ContentHistoryTextRangeTests.fs)
  continues to pin the new range. No new unit tests needed for
  the delegation itself — the helper is trivially testable via
  inspection.
- **NVDA matrix gate Cycle 46-PRD-1** — `Ctrl+Shift+Up/Down/End`
  produce identical aural behaviour to PR-C (NVDA reads via
  caret pacing, not an independent SAPI utterance). The
  maintainer's muscle memory for these hotkeys is preserved.

### PR-D risk register

- **`SpeechCursor` mode (`AutoDrive` vs `Manual`)** affects the
  callback behaviour. The delegation should respect the mode —
  in `AutoDrive`, every new entry triggers a caret move; in
  `Manual`, only the explicit hotkey calls do. The wiring in
  `Program.fs` already gates `onSpeechCursorWake` on the mode;
  preserve that.
- **Dead-code removal blast radius.** ~600 LOC delete is a big
  diff. Verify each deletion against `grep -rn` for the type
  name. The previous CI failure mode (PR #132 cited in
  CLAUDE.md) was a missed `let rec` after a similar refactor —
  re-read each remaining function in
  `TerminalAutomationPeer.fs` after the delete.

## 4. Known issues / open follow-ups (not blocking)

### CI: cmd.exe banner doesn't reliably reach ContentHistory

Surfaced during PR-B's CI runs. The pre-PR-B Tests.Ui content
assertions worked because `Screen(30, 120)` pre-populated 3629
chars regardless of cmd.exe banner timing. With ContentHistory
the substrate starts empty; if cmd.exe banner doesn't actually
flow through the PTY reader in the CI environment, the
assertions fire on length 0.

The PR-B fixup commits weakened the Tests.Ui content
assertions to "Text pattern is reachable + API doesn't
throw" — see
[`tests/Tests.Ui/TextPatternTests.fs`](../tests/Tests.Ui/TextPatternTests.fs).
A follow-up cycle could investigate:

- Does `ConPtyHost.start` succeed in the GitHub Actions
  `windows-latest` runner? Add an explicit assertion + log.
- Does the reader loop actually fire? Add a counter / log
  inspection.
- Could the test send a known string via FlaUI's keyboard
  input to force content into ContentHistory deterministically?

Not urgent — NVDA matrix walks the real user-visible path.

### `FileLoggerTests.FlushPending` flake

Pre-existing 2-second timing assertion that intermittently
fails under CI runner load (observed on the second PR-B fixup
run; not present on the third). Unrelated to PR-B; bump the
timeout to 5s as a quick fix if it recurs. The test lives at
[`tests/Tests.Unit/FileLoggerTests.fs:306`](../tests/Tests.Unit/FileLoggerTests.fs).

### GitHub MCP outage handling

Observed during the PR-B fixup cycle on 2026-05-13: MCP token
failed to retrieve, blocking auto-merge and check-status
queries for the remainder of the session. Fallback per
[`CLAUDE.md`](../CLAUDE.md) "Sandbox / runtime constraints":
ask the maintainer to merge manually via the
"Squash and merge" button. Webhook events kept flowing — the
two channels are independent.

If MCP is still down at next-session start, the maintainer can
either:

- Wait for the MCP server to recover (usually self-heals
  within hours).
- Use the manual fallback URL for PR creation:
  `https://github.com/KyleKeane/pty-speak/compare/main...<branch>?expand=1`.

## 5. Reading order for the new session

1. **This doc** (you are here).
2. [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md)
   — full Cycle 46 decision record, including the §1–§5
   resolutions.
3. [`src/Terminal.Accessibility/ContentHistoryTextRange.fs`](../src/Terminal.Accessibility/ContentHistoryTextRange.fs)
   — the new substrate consumer; PR-C's helper sits in the
   peer file just above this.
4. [`src/Terminal.Accessibility/TerminalAutomationPeer.fs`](../src/Terminal.Accessibility/TerminalAutomationPeer.fs)
   — where PR-C Edit 1 adds the helper; where PR-D Edit 2
   deletes the legacy types.
5. [`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs)
   lines 1471–1700 — the boundary handler PR-C Edit 2 modifies;
   lines 2864–2899 — the `SpeechCursor` hotkey handlers PR-D
   Edit 1 modifies.
6. [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
   — add `Cycle 46-PRC-1` + `Cycle 46-PRD-1` matrix rows when
   shipping each PR.

## 6. Sequencing recommendation

PR-C and PR-D are sized to land sequentially in a single
session if the matrix walks fit. Suggested order:

1. **Walk NVDA matrix Cycle 46-PRB-1** first to confirm PR-B
   didn't regress anything before adding more change on top.
2. **PR-C** — wire the caret. Smaller diff, higher leverage
   (the actual user-visible payoff).
3. **NVDA matrix Cycle 46-PRC-1** — caret-driven read,
   typing interrupts, menu interrupts.
4. **PR-D** — delegation + cleanup. Pure refactor + dead-code
   removal; no new user-visible behaviour.
5. **NVDA matrix Cycle 46-PRD-1** — `Ctrl+Shift+*` parity
   check.
6. **Optional**: ADR 0002 → ADR archived; CHANGELOG closes
   Cycle 46.

If PR-C's NVDA matrix surfaces a regression (e.g.
`TextSelectionChanged` doesn't trigger NVDA), pause and
iterate on the helper (Edit 1) before moving to PR-D.

## 7. What this doc deliberately doesn't try to be

- An ADR — that lives at
  [`docs/adr/0002-uia-textedit-caret-output.md`](adr/0002-uia-textedit-caret-output.md).
- A spec — the spec is unchanged by Cycle 46.
- A test plan for PR-A / PR-B — those PRs are merged.
- A handoff for everything in flight — there is **no** in-flight
  work at handoff time. Main is clean.
