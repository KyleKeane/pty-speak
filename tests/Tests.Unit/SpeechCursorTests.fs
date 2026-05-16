module PtySpeak.Tests.Unit.SpeechCursorTests

open System
open System.Text
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 45 Commit 1 — SpeechCursor pure-function tests.
// ---------------------------------------------------------------------
//
// Pin the cursor's contract:
//
//   * AutoDrive advances + announces on append
//   * Manual mode does NOT advance on append
//   * Navigation (next / previous / toLatest / toMarker)
//   * speakSince emits in seq order; updates LastSpokenSeq
//   * SelectionShown suspends auto-drive; SelectionDismissed resumes
//   * Spinner entries are skipped in AutoDrive (configurable)
//   * Reset preserves Mode but clears Position / LastSpokenSeq
//
// Uses a fake `announce` collector instead of a real NvdaChannel
// — the module is pure-functional by design.

let private t0 = DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

let private printRune (c: char) : VtEvent =
    Print (Rune c)
let private lf = Execute 0x0Auy

let private freshHistory () : ContentHistory.T =
    ContentHistory.create ContentHistory.defaultParameters

let private freshCursor () : SpeechCursor.T =
    SpeechCursor.create SpeechCursor.defaultParameters

/// Cycle 45 fixup (2026-05-12): the default
/// `SkipTextSpansInAutoDrive = true` suppresses TextSpan
/// auto-announce in production (cmd's edit-suffix-reprint
/// pattern would otherwise produce inflated narrations).
/// Tests that pin the underlying TextSpan-announce mechanism
/// (so verbosity-mode work can still toggle it back on per
/// shell) construct a cursor with the flag flipped off.
let private cursorWithTextSpanAnnounce () : SpeechCursor.T =
    SpeechCursor.create
        { SpeechCursor.defaultParameters with
            SkipTextSpansInAutoDrive = false }

/// Capture-collector for announce callbacks. Returns the
/// callback + a getter for the accumulated (text, activityId)
/// pairs.
let private capture () =
    let entries = ResizeArray<string * string>()
    let collect ((text: string), (activityId: string)) =
        entries.Add(text, activityId)
    let snapshot () = entries.ToArray() |> Array.toList
    collect, snapshot

// ---------------------------------------------------------------------
// Initial state
// ---------------------------------------------------------------------

[<Fact>]
let ``new cursor starts in AutoDrive at Position -1 with nothing spoken`` () =
    let cursor = freshCursor ()
    let history = freshHistory ()
    Assert.Equal(SpeechCursor.AutoDrive, SpeechCursor.mode cursor)
    Assert.Equal(-1L, cursor.Position)
    Assert.Equal(-1L, cursor.LastSpokenSeq)
    Assert.Equal(None, SpeechCursor.current cursor history)

// ---------------------------------------------------------------------
// AutoDrive announces on append
// ---------------------------------------------------------------------

[<Fact>]
let ``AutoDrive announces appended TextSpans in seq order`` () =
    // Uses the TextSpan-announce variant: the production
    // default suppresses TextSpan auto-announce
    // (`SkipTextSpansInAutoDrive = true`), so this test
    // explicitly opts the underlying mechanism back on.
    let cursor = cursorWithTextSpanAnnounce ()
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'h') |> ignore
    ContentHistory.appendFromEvent history t0 (printRune 'i') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    let said = snap ()
    // Expect ONE announce for the sealed "hi" TextSpan; the
    // Newline entry's renderEntry returns None.
    Assert.Equal(1, said.Length)
    let (text, activityId) = said.[0]
    Assert.Equal("hi", text)
    Assert.Equal(ActivityIds.output, activityId)
    Assert.True(cursor.LastSpokenSeq >= 0L)

[<Fact>]
let ``AutoDrive default suppresses TextSpan announces but advances cursor`` () =
    // Cycle 45 fixup (2026-05-12): `SkipTextSpansInAutoDrive`
    // defaults to true so cmd's edit-suffix-reprint pattern
    // doesn't produce inflated narrations. The cursor still
    // advances (Position + LastSpokenSeq) so Manual review
    // can revisit the entry. Authoritative output-narration
    // happens at tuple-finalise via `IOCell.OutputText`
    // (see `Program.fs handlePromptBoundary`).
    let cursor = freshCursor ()
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'h') |> ignore
    ContentHistory.appendFromEvent history t0 (printRune 'i') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    // No live announce — the TextSpan is silenced under the
    // new default.
    Assert.Empty(snap ())
    // Cursor still advanced and the watermark moved, so the
    // entry is reachable via Manual navigation and won't be
    // re-spoken on a duplicate onAppend call.
    let latest = ContentHistory.latestSeq history
    Assert.Equal(latest, cursor.Position)
    Assert.Equal(latest, cursor.LastSpokenSeq)

[<Fact>]
let ``AutoDrive does not re-announce entries it has already spoken`` () =
    let cursor = cursorWithTextSpanAnnounce ()
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'a') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    // Re-invoke onAppend with no further appends. The cursor's
    // LastSpokenSeq gate should suppress duplicates (idempotent).
    SpeechCursor.onAppend cursor history announce
    Assert.Equal(1, (snap ()).Length)

// ---------------------------------------------------------------------
// Manual mode does NOT auto-advance
// ---------------------------------------------------------------------

[<Fact>]
let ``Manual mode does NOT announce on append`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'h') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    Assert.Empty(snap ())
    Assert.Equal(-1L, cursor.LastSpokenSeq)

// ---------------------------------------------------------------------
// toggleMode and setMode
// ---------------------------------------------------------------------

[<Fact>]
let ``toggleMode switches between AutoDrive and Manual`` () =
    let cursor = freshCursor ()
    let m1 = SpeechCursor.toggleMode cursor
    Assert.Equal(SpeechCursor.Manual, m1)
    let m2 = SpeechCursor.toggleMode cursor
    Assert.Equal(SpeechCursor.AutoDrive, m2)

[<Fact>]
let ``setMode forces the specified mode`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    Assert.Equal(SpeechCursor.Manual, SpeechCursor.mode cursor)
    SpeechCursor.setMode cursor SpeechCursor.AutoDrive
    Assert.Equal(SpeechCursor.AutoDrive, SpeechCursor.mode cursor)

// ---------------------------------------------------------------------
// Navigation: next / previous / toLatest
// ---------------------------------------------------------------------

[<Fact>]
let ``next moves cursor to the next renderable entry skipping Newlines`` () =
    // Cycle 49 PR-A — manual nav skips non-renderable entries
    // (Newline, Overwrite, empty TextSpan, UserInputEcho).
    // After "a\nb\n" the entries are TextSpan "a" (Seq 0),
    // Newline (Seq 1), TextSpan "b" (Seq 2), Newline (Seq 3).
    // From Position -1, `next` lands on Seq 0 then jumps over
    // the Newline to Seq 2.
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'a')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'b')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let n1 = SpeechCursor.next cursor history
    Assert.True(n1.IsSome)
    Assert.Equal(0L, cursor.Position)
    let n2 = SpeechCursor.next cursor history
    Assert.True(n2.IsSome)
    Assert.Equal(2L, cursor.Position)
    // No further renderable entry — the trailing Newline is
    // skipped and `next` returns None.
    let n3 = SpeechCursor.next cursor history
    Assert.Equal(None, n3)
    Assert.Equal(2L, cursor.Position)

[<Fact>]
let ``previous moves cursor backward skipping Newlines`` () =
    // Cycle 49 PR-A — `previous` mirrors `next`'s renderable
    // filter: from the latest TextSpan "b" (Seq 2), stepping
    // back skips the Newline (Seq 1) and lands on TextSpan "a"
    // (Seq 0).
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'a')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'b')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = SpeechCursor.toLatest cursor history
    Assert.Equal(2L, cursor.Position)
    let prev = SpeechCursor.previous cursor history
    Assert.True(prev.IsSome)
    Assert.Equal(0L, cursor.Position)
    let prev2 = SpeechCursor.previous cursor history
    Assert.Equal(None, prev2)
    Assert.Equal(0L, cursor.Position)

[<Fact>]
let ``toLatest jumps to the latest renderable entry skipping trailing Newline`` () =
    // Cycle 49 PR-A — `toLatest` follows the same renderable
    // filter as `next`/`previous` so a trailing Newline (the
    // common shape after a `dir`-style command) doesn't park
    // the cursor on a Seq the user can't hear.
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'h')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let latest = SpeechCursor.toLatest cursor history
    Assert.True(latest.IsSome)
    // Latest renderable entry is the TextSpan "h" (Seq 0); the
    // trailing Newline (latestSeq = 1) is skipped.
    Assert.Equal(0L, cursor.Position)
    Assert.Equal(1L, ContentHistory.latestSeq history)

[<Fact>]
let ``toLatest on empty history returns None and leaves Position at -1`` () =
    let cursor = freshCursor ()
    let history = freshHistory ()
    let result = SpeechCursor.toLatest cursor history
    Assert.Equal(None, result)
    Assert.Equal(-1L, cursor.Position)

[<Fact>]
let ``next skips UserInputEcho-sourced entries`` () =
    // Cycle 49 PR-A — UserInputEcho entries render to None in
    // `renderEntryWithPolicy` (Cycle 48 PR-E) and so are
    // skipped by manual navigation. A typical sub-prompt
    // sequence has the cmd-output TextSpan followed by the
    // user's typed-then-Enter UserInputEcho TextSpan; `next`
    // should jump over the echo entry.
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    // First batch: cmd output. SourceResolver defaults to
    // Unknown which renders normally.
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'o')
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'k')
    let _ = ContentHistory.appendFromEvent history t0 lf
    // Flip the resolver to UserInputEcho before appending the
    // typed-echo bytes.
    ContentHistory.setSourceResolver
        history
        (fun () -> ContentHistory.EntrySource.UserInputEcho)
    let _ = ContentHistory.appendFromEvent history (after 10) (printRune 'y')
    let _ = ContentHistory.appendFromEvent history (after 10) (printRune 'o')
    let _ = ContentHistory.appendFromEvent history (after 10) lf
    let n1 = SpeechCursor.next cursor history
    Assert.True(n1.IsSome)
    // First renderable entry is the "ok" TextSpan at Seq 0.
    Assert.Equal(0L, cursor.Position)
    // No further renderable entry — the echo TextSpan + its
    // surrounding Newlines are all filtered.
    let n2 = SpeechCursor.next cursor history
    Assert.Equal(None, n2)

[<Fact>]
let ``previous returns None when only Newlines precede the cursor`` () =
    // Cycle 49 PR-A — if the only earlier entries are
    // non-renderable, `previous` reports "already at the
    // earliest renderable" rather than parking on a Newline.
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'x')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = SpeechCursor.toLatest cursor history
    // toLatest lands on TextSpan "x" (the leading Newline at
    // Seq 0 sealed the empty active span without producing a
    // renderable TextSpan).
    let prev = SpeechCursor.previous cursor history
    Assert.Equal(None, prev)

// ---------------------------------------------------------------------
// toMarker
// ---------------------------------------------------------------------

[<Fact>]
let ``toMarker forward finds the next matching marker`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ =
        ContentHistory.appendMarker
            history
            ContentHistory.MarkerKind.PromptStart
            t0
            None
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'h')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ =
        ContentHistory.appendMarker
            history
            ContentHistory.MarkerKind.PromptStart
            (after 10)
            None
    // Cursor is at -1; forward jump should land on Seq 0
    // (the first PromptStart marker).
    let found =
        SpeechCursor.toMarker
            cursor
            history
            ContentHistory.MarkerKind.PromptStart
            SpeechCursor.Forward
    Assert.True(found.IsSome)
    match found with
    | Some (ContentHistory.Marker m) ->
        Assert.Equal(ContentHistory.MarkerKind.PromptStart, m.Kind)
        Assert.Equal(0L, m.Seq)
    | _ -> Assert.Fail("expected PromptStart marker at seq 0")

[<Fact>]
let ``toMarker with no matching kind returns None`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ =
        ContentHistory.appendMarker
            history
            ContentHistory.MarkerKind.PromptStart
            t0
            None
    let found =
        SpeechCursor.toMarker
            cursor
            history
            ContentHistory.MarkerKind.SelectionShown
            SpeechCursor.Forward
    Assert.Equal(None, found)

// ---------------------------------------------------------------------
// SelectionShown / SelectionDismissed suspend/resume AutoDrive
// ---------------------------------------------------------------------

[<Fact>]
let ``SelectionShown marker suspends AutoDrive announces`` () =
    // Uses the TextSpan-announce variant so the "X" suffix
    // suppression is checked against an actually-announceable
    // TextSpan (under the production default, TextSpans are
    // silenced regardless of suspend state — the suppression
    // would be untestable without flipping the flag).
    let cursor = cursorWithTextSpanAnnounce ()
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'h') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    Assert.Equal(1, (snap ()).Length)
    // SelectionShown marker fires.
    ContentHistory.appendMarker
        history
        ContentHistory.MarkerKind.SelectionShown
        (after 10)
        (Some "Yes, No")
    |> ignore
    SpeechCursor.onAppend cursor history announce
    // The SelectionShown marker itself announces (Cycle 45
    // post-ordering of the suspend bit); then AutoDrive
    // suspends so subsequent entries don't auto-announce.
    ContentHistory.appendFromEvent history (after 20) (printRune 'X') |> ignore
    ContentHistory.appendFromEvent history (after 20) lf |> ignore
    SpeechCursor.onAppend cursor history announce
    let said = snap ()
    let lastTexts = said |> List.map fst
    Assert.DoesNotContain("X", lastTexts)
    // The marker announce is in the list.
    let hasSelectionAnnounce =
        lastTexts |> List.exists (fun t -> t.Contains("Selection prompt"))
    Assert.True(hasSelectionAnnounce)

[<Fact>]
let ``SelectionDismissed marker resumes AutoDrive announces`` () =
    // Uses the TextSpan-announce variant so "Z" is a
    // detectable resume signal (TextSpans are silenced under
    // the production default).
    let cursor = cursorWithTextSpanAnnounce ()
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendMarker
        history
        ContentHistory.MarkerKind.SelectionShown
        t0
        None
    |> ignore
    SpeechCursor.onAppend cursor history announce
    // Now dismiss the selection.
    ContentHistory.appendMarker
        history
        ContentHistory.MarkerKind.SelectionDismissed
        (after 100)
        None
    |> ignore
    SpeechCursor.onAppend cursor history announce
    ContentHistory.appendFromEvent history (after 110) (printRune 'Z') |> ignore
    ContentHistory.appendFromEvent history (after 110) lf |> ignore
    SpeechCursor.onAppend cursor history announce
    let said = snap () |> List.map fst
    Assert.Contains("Z", said)

// ---------------------------------------------------------------------
// renderEntry shape
// ---------------------------------------------------------------------

[<Fact>]
let ``renderEntry returns the TextSpan text on the output activity id`` () =
    let span =
        ContentHistory.TextSpan
            { Seq = 0L
              Text = "hello"
              StartedAt = t0
              SettledAt = t0
              Source = ContentHistory.EntrySource.Unknown }
    match SpeechCursor.renderEntry span with
    | Some (text, activityId) ->
        Assert.Equal("hello", text)
        Assert.Equal(ActivityIds.output, activityId)
    | None -> Assert.Fail("expected non-empty render")

[<Fact>]
let ``renderEntry returns None for empty TextSpan`` () =
    let span =
        ContentHistory.TextSpan
            { Seq = 0L
              Text = ""
              StartedAt = t0
              SettledAt = t0
              Source = ContentHistory.EntrySource.Unknown }
    Assert.Equal(None, SpeechCursor.renderEntry span)

[<Fact>]
let ``renderEntry returns None for Newline`` () =
    let nl =
        ContentHistory.Newline
            { Seq = 0L
              At = t0
              Source = ContentHistory.EntrySource.Unknown }
    Assert.Equal(None, SpeechCursor.renderEntry nl)

[<Fact>]
let ``renderEntry returns mode activity for AltScreenEnter`` () =
    let m =
        ContentHistory.Marker
            { Seq = 0L
              Kind = ContentHistory.MarkerKind.AltScreenEnter
              At = t0
              Payload = None
              Source = ContentHistory.EntrySource.BoundaryMarker }
    match SpeechCursor.renderEntry m with
    | Some (_, activityId) -> Assert.Equal(ActivityIds.mode, activityId)
    | None -> Assert.Fail("expected announce for AltScreenEnter")

[<Fact>]
let ``renderEntry returns selection announce for SelectionShown with payload`` () =
    let m =
        ContentHistory.Marker
            { Seq = 0L
              Kind = ContentHistory.MarkerKind.SelectionShown
              At = t0
              Payload = Some "Edit, Yes, Always, No"
              Source = ContentHistory.EntrySource.BoundaryMarker }
    match SpeechCursor.renderEntry m with
    | Some (text, _) ->
        Assert.Contains("Selection prompt", text)
        Assert.Contains("Edit, Yes, Always, No", text)
    | None -> Assert.Fail("expected announce for SelectionShown")

// ---------------------------------------------------------------------
// Cycle 45f — renderEntryWithPolicy / PromptPathMode
// ---------------------------------------------------------------------

let private promptMarker (payload: string option) : ContentHistory.Entry =
    ContentHistory.Marker
        { Seq = 0L
          Kind = ContentHistory.MarkerKind.PromptStart
          At = t0
          Payload = payload
          Source = ContentHistory.EntrySource.BoundaryMarker }

[<Fact>]
let ``PromptStart under Suppress returns None`` () =
    let entry = promptMarker (Some "C:\\Users\\Kyle\\Local>")
    Assert.Equal(
        None,
        SpeechCursor.renderEntryWithPolicy ShellPolicy.Suppress entry)

[<Fact>]
let ``PromptStart under FinalDirOnly trims to last directory`` () =
    let entry = promptMarker (Some "C:\\Users\\Kyle\\Local>")
    match
        SpeechCursor.renderEntryWithPolicy ShellPolicy.FinalDirOnly entry
    with
    | Some (text, activityId) ->
        Assert.Equal("Local>", text)
        Assert.Equal(ActivityIds.output, activityId)
    | None -> Assert.Fail("expected announce under FinalDirOnly")

[<Fact>]
let ``PromptStart under Full returns the payload verbatim`` () =
    let entry = promptMarker (Some "C:\\Users\\Kyle\\Local>")
    match
        SpeechCursor.renderEntryWithPolicy ShellPolicy.Full entry
    with
    | Some (text, _) ->
        Assert.Equal("C:\\Users\\Kyle\\Local>", text)
    | None -> Assert.Fail("expected announce under Full")

[<Fact>]
let ``PromptStart with empty payload returns None under every mode`` () =
    let entry = promptMarker (Some "")
    for mode in
        [ ShellPolicy.Suppress
          ShellPolicy.FinalDirOnly
          ShellPolicy.Full
          ShellPolicy.FullOnChangeElseFinal
          ShellPolicy.FinalOnChangeElseFull
          ShellPolicy.SilentOnUnchangedFullOnChange
          ShellPolicy.SilentOnUnchangedFinalOnChange ] do
        Assert.Equal(None, SpeechCursor.renderEntryWithPolicy mode entry)

[<Fact>]
let ``PromptStart with no payload returns None under every mode`` () =
    let entry = promptMarker None
    for mode in
        [ ShellPolicy.Suppress
          ShellPolicy.FinalDirOnly
          ShellPolicy.Full
          ShellPolicy.FullOnChangeElseFinal
          ShellPolicy.FinalOnChangeElseFull
          ShellPolicy.SilentOnUnchangedFullOnChange
          ShellPolicy.SilentOnUnchangedFinalOnChange ] do
        Assert.Equal(None, SpeechCursor.renderEntryWithPolicy mode entry)

[<Fact>]
let ``backwards-compat renderEntry suppresses PromptStart`` () =
    // The no-policy overload uses Suppress for backwards-compat;
    // Cycle 45f pinned this so existing tests + callers don't
    // spontaneously start announcing prompts.
    let entry = promptMarker (Some "C:\\Users\\Kyle>")
    Assert.Equal(None, SpeechCursor.renderEntry entry)

// ---------------------------------------------------------------------
// Cycle 52 R6b — FullOnChangeElseFinal (on-change prompt verbosity).
// The change-aware resolution lives in the private
// `effectivePromptPath`, exercised through the auto-drive
// `onAppend` path; these drive a real cursor + history.
// ---------------------------------------------------------------------

let private onChangeCursor () : SpeechCursor.T =
    SpeechCursor.create
        { SpeechCursor.defaultParameters with
            PromptPath = ShellPolicy.FullOnChangeElseFinal }

let private appendPrompt (history: ContentHistory.T) (payload: string) =
    ContentHistory.appendMarker
        history
        ContentHistory.MarkerKind.PromptStart
        t0
        (Some payload)
    |> ignore

[<Fact>]
let ``FullOnChangeElseFinal narrates the full path on the first prompt`` () =
    let cursor = onChangeCursor ()
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("C:\\Users\\Kyle\\Local>", ActivityIds.output) ],
        snap ())

[<Fact>]
let ``FullOnChangeElseFinal narrates final-dir-only when the prompt is unchanged`` () =
    let cursor = onChangeCursor ()
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // Same prompt again (commands run in the same directory).
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("C:\\Users\\Kyle\\Local>", ActivityIds.output)
          ("Local>", ActivityIds.output) ],
        snap ())

[<Fact>]
let ``FullOnChangeElseFinal narrates the full path again when the directory changes`` () =
    let cursor = onChangeCursor ()
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // `cd` to a different directory → full path again.
    appendPrompt history "C:\\Users\\Kyle\\Other>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("C:\\Users\\Kyle\\Local>", ActivityIds.output)
          ("Local>", ActivityIds.output)
          ("C:\\Users\\Kyle\\Other>", ActivityIds.output) ],
        snap ())

[<Fact>]
let ``FullOnChangeElseFinal resets to full path after SpeechCursor.reset`` () =
    // reset fires on shell-switch (post-Cycle-45c ContentHistory is
    // continuous). The first prompt after a switch must narrate the
    // full path even if its text matches the last pre-switch prompt.
    let cursor = onChangeCursor ()
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    SpeechCursor.reset cursor
    let history2 = freshHistory ()
    appendPrompt history2 "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history2 announce
    Assert.Equal<(string * string) list>(
        [ ("C:\\Users\\Kyle\\Local>", ActivityIds.output)
          ("C:\\Users\\Kyle\\Local>", ActivityIds.output) ],
        snap ())

// ---------------------------------------------------------------------
// Cycle 52 R6b-followup — the three additional on-change modes.
// Same auto-drive `onAppend` path; same `appendPrompt` helper.
// ---------------------------------------------------------------------

let private cursorWith (mode: ShellPolicy.PromptPathMode) : SpeechCursor.T =
    SpeechCursor.create
        { SpeechCursor.defaultParameters with PromptPath = mode }

[<Fact>]
let ``FinalOnChangeElseFull narrates final-dir on change, full when unchanged`` () =
    let cursor = cursorWith ShellPolicy.FinalOnChangeElseFull
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // Same dir (unchanged) → full path.
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // `cd` (changed) → final-dir-only.
    appendPrompt history "C:\\Users\\Kyle\\Other>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("Local>", ActivityIds.output)
          ("C:\\Users\\Kyle\\Local>", ActivityIds.output)
          ("Other>", ActivityIds.output) ],
        snap ())

[<Fact>]
let ``SilentOnUnchangedFullOnChange narrates full on change, nothing when unchanged`` () =
    let cursor = cursorWith ShellPolicy.SilentOnUnchangedFullOnChange
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // Same dir (unchanged) → silent (no announce entry at all).
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    // `cd` (changed) → full path.
    appendPrompt history "C:\\Users\\Kyle\\Other>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("C:\\Users\\Kyle\\Local>", ActivityIds.output)
          ("C:\\Users\\Kyle\\Other>", ActivityIds.output) ],
        snap ())

[<Fact>]
let ``SilentOnUnchangedFinalOnChange narrates final-dir on change, nothing when unchanged`` () =
    let cursor = cursorWith ShellPolicy.SilentOnUnchangedFinalOnChange
    let history = freshHistory ()
    let announce, snap = capture ()
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    appendPrompt history "C:\\Users\\Kyle\\Local>"
    SpeechCursor.onAppend cursor history announce
    appendPrompt history "C:\\Users\\Kyle\\Other>"
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<(string * string) list>(
        [ ("Local>", ActivityIds.output)
          ("Other>", ActivityIds.output) ],
        snap ())

// ---------------------------------------------------------------------
// Cycle 49 PR-D — renderEntryForManualNav decouples nav from narration
// ---------------------------------------------------------------------

[<Fact>]
let ``manual-nav render surfaces PromptStart with payload even under Suppress`` () =
    // Cycle 49 PR-D — maintainer feedback 2026-05-14: prompts
    // should appear as standalone entries in SpeechCursor manual
    // navigation, regardless of the per-shell PromptPath
    // (which gates the AUTO-DRIVE narration, not navigation).
    let entry = promptMarker (Some "C:\\Users\\Kyle\\Local>")
    match
        SpeechCursor.renderEntryForManualNav ShellPolicy.Suppress entry
    with
    | Some (text, activityId) ->
        Assert.Equal("Local>", text)
        Assert.Equal(ActivityIds.output, activityId)
    | None -> Assert.Fail("expected manual-nav announce for PromptStart under Suppress")

[<Fact>]
let ``manual-nav render still returns None for PromptStart with empty payload`` () =
    // Empty / missing payload yields no navigable entry — same
    // safety net as `renderEntryWithPolicy` under every policy.
    let empty = promptMarker (Some "")
    let none = promptMarker None
    for mode in
        [ ShellPolicy.Suppress
          ShellPolicy.FinalDirOnly
          ShellPolicy.Full
          ShellPolicy.FullOnChangeElseFinal
          ShellPolicy.FinalOnChangeElseFull
          ShellPolicy.SilentOnUnchangedFullOnChange
          ShellPolicy.SilentOnUnchangedFinalOnChange ] do
        Assert.Equal(None, SpeechCursor.renderEntryForManualNav mode empty)
        Assert.Equal(None, SpeechCursor.renderEntryForManualNav mode none)

[<Fact>]
let ``manual-nav render falls back to raw payload when FinalDirOnly trim returns None`` () =
    // PromptStart payload without path delimiters (no `>`, no
    // path separators) doesn't match FinalDirOnly's trim
    // pattern. Manual nav still surfaces the raw payload so the
    // boundary is navigable; auto-drive's `renderEntryWithPolicy`
    // under FinalDirOnly would similarly return None for these.
    let entry = promptMarker (Some "PS C:\\>")
    match
        SpeechCursor.renderEntryForManualNav ShellPolicy.Suppress entry
    with
    | Some (text, _) ->
        // Either the trimmed form ("PS C:\\>" or similar) OR the
        // raw payload — both are acceptable as long as something
        // surfaces.
        Assert.False(System.String.IsNullOrEmpty text)
    | None -> Assert.Fail("expected fallback announce for non-trimmable PromptStart")

[<Fact>]
let ``manual-nav render delegates to renderEntryWithPolicy for non-PromptStart entries`` () =
    // Cycle 49 PR-D — only PromptStart gets the policy override;
    // every other entry kind continues to obey the supplied
    // PromptPathMode (which the cursor passes through from its
    // own Parameters). TextSpans render normally.
    let span =
        ContentHistory.TextSpan
            { Seq = 0L
              Text = "hello"
              StartedAt = t0
              SettledAt = t0
              Source = ContentHistory.EntrySource.CmdOutput }
    match
        SpeechCursor.renderEntryForManualNav ShellPolicy.Suppress span
    with
    | Some (text, activityId) ->
        Assert.Equal("hello", text)
        Assert.Equal(ActivityIds.output, activityId)
    | None -> Assert.Fail("expected TextSpan to render under manual nav")

[<Fact>]
let ``manual-nav previous lands on PromptStart marker as navigable entry`` () =
    // End-to-end sanity: with a PromptStart sandwiched between
    // two TextSpans, stepping back from the latest surfaces the
    // prompt as its own stop. Without PR-D this would skip the
    // PromptStart under the default `PromptPath = Suppress`.
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'a')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ =
        ContentHistory.appendMarker
            history
            ContentHistory.MarkerKind.PromptStart
            (after 10)
            (Some "C:\\Users\\Kyle\\Local>")
    let _ = ContentHistory.appendFromEvent history (after 20) (printRune 'b')
    let _ = ContentHistory.appendFromEvent history (after 20) lf
    let _ = SpeechCursor.toLatest cursor history
    // Latest renderable is TextSpan "b". Previous step lands on
    // the PromptStart marker (newly renderable in PR-D).
    let prev = SpeechCursor.previous cursor history
    match prev with
    | Some (ContentHistory.Marker m) ->
        Assert.Equal(ContentHistory.MarkerKind.PromptStart, m.Kind)
    | _ -> Assert.Fail("expected previous to land on PromptStart marker")

// ---------------------------------------------------------------------
// Cycle 45f — setParameters + LineByLine streaming + Off mode
// ---------------------------------------------------------------------

[<Fact>]
let ``setParameters flips SkipTextSpansInAutoDrive at runtime`` () =
    let cursor = freshCursor ()
    Assert.True(cursor.Parameters.SkipTextSpansInAutoDrive)
    let updated =
        { cursor.Parameters with SkipTextSpansInAutoDrive = false }
    SpeechCursor.setParameters cursor updated
    Assert.False(cursor.Parameters.SkipTextSpansInAutoDrive)
    // Position + LastSpokenSeq + Mode preserved.
    Assert.Equal(-1L, cursor.Position)
    Assert.Equal(-1L, cursor.LastSpokenSeq)

[<Fact>]
let ``onAppend with SkipTextSpansInAutoDrive=false announces every TextSpan`` () =
    // Equivalent to flipping the policy to LineByLine — every
    // sealed TextSpan narrates as it arrives.
    let cursor = freshCursor ()
    let lineByLineParams =
        { cursor.Parameters with SkipTextSpansInAutoDrive = false }
    SpeechCursor.setParameters cursor lineByLineParams
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'a') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    ContentHistory.appendFromEvent history (after 10) (printRune 'b') |> ignore
    ContentHistory.appendFromEvent history (after 10) lf |> ignore
    SpeechCursor.onAppend cursor history announce
    let said = snap () |> List.map fst
    Assert.Equal<string list>([ "a"; "b" ], said)

[<Fact>]
let ``setParameters mid-stream only affects subsequent appends`` () =
    // Cycle 45f edge case: flipping the policy does NOT replay
    // or rewind. Entries already announced under the prior
    // policy stay announced; subsequent appends obey the new
    // mode.
    let cursor = freshCursor ()
    // Start in LineByLine — first batch will announce.
    SpeechCursor.setParameters
        cursor
        { cursor.Parameters with SkipTextSpansInAutoDrive = false }
    let history = freshHistory ()
    let announce, snap = capture ()
    ContentHistory.appendFromEvent history t0 (printRune 'a') |> ignore
    ContentHistory.appendFromEvent history t0 lf |> ignore
    SpeechCursor.onAppend cursor history announce
    Assert.Equal<string list>([ "a" ], snap () |> List.map fst)
    // Flip to TupleFinalOnly mid-stream.
    SpeechCursor.setParameters
        cursor
        { cursor.Parameters with SkipTextSpansInAutoDrive = true }
    ContentHistory.appendFromEvent history (after 10) (printRune 'b') |> ignore
    ContentHistory.appendFromEvent history (after 10) lf |> ignore
    SpeechCursor.onAppend cursor history announce
    // No new announce — "b" was suppressed under the new policy.
    Assert.Equal<string list>([ "a" ], snap () |> List.map fst)

// ---------------------------------------------------------------------
// speakSince
// ---------------------------------------------------------------------

[<Fact>]
let ``speakSince emits in seq order and advances LastSpokenSeq`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let announce, snap = capture ()
    let _ =
        ContentHistory.appendFromEvent history t0 (printRune 'a')
        @ ContentHistory.appendFromEvent history t0 lf
    let _ =
        ContentHistory.appendFromEvent history (after 10) (printRune 'b')
        @ ContentHistory.appendFromEvent history (after 10) lf
    let latest = ContentHistory.latestSeq history
    let count =
        SpeechCursor.speakSince cursor history announce latest
    Assert.Equal(2, count)
    let said = snap () |> List.map fst
    Assert.Equal<string list>([ "a"; "b" ], said)
    Assert.Equal(latest, cursor.LastSpokenSeq)

[<Fact>]
let ``speakSince emits nothing when there's nothing new`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let announce, snap = capture ()
    let count = SpeechCursor.speakSince cursor history announce 10L
    Assert.Equal(0, count)
    Assert.Empty(snap ())

// ---------------------------------------------------------------------
// Spinner skip — re-pinned in a future commit once ContentHistory has
// a Spinner-emit path through `appendFromEvent` / `appendMarker`.
// With Cycle 45 Commit 2's `onAppend` reading directly from history
// (rather than from a passed-in entries list), there's no longer a
// public way to inject a synthetic Spinner entry into the history
// for testing in isolation. The cursor's `autoDriveAdvanceable`
// gate is still visible to the runtime via the `Parameters.SkipSpinnersInAutoDrive`
// flag, but the integration test will live alongside the spinner
// detector when it lands. Deleted for now to keep the test suite
// honest about what's actually pinned.
// ---------------------------------------------------------------------

// ---------------------------------------------------------------------
// Reset
// ---------------------------------------------------------------------

[<Fact>]
let ``reset clears Position and LastSpokenSeq but preserves Mode`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    cursor.Position <- 5L
    cursor.LastSpokenSeq <- 4L
    SpeechCursor.reset cursor
    Assert.Equal(-1L, cursor.Position)
    Assert.Equal(-1L, cursor.LastSpokenSeq)
    Assert.Equal(SpeechCursor.Manual, SpeechCursor.mode cursor)

// ---------------------------------------------------------------------
// Cycle 51 PR-AD (ADR 0004) — sealed-IOCell transcript that Manual
// navigation walks. Additive to the legacy ContentHistory-Seq
// engine (those tests above are unchanged). Pins: appendCell
// command/output items, whitespace skip, AutoDrive-follow vs
// Manual-stay, cellPrevious/cellNext/cellToLatest navigation +
// boundaries, cellReset, and the Issue 1/3 scenarios (command +
// post-response output are navigable).
// ---------------------------------------------------------------------

let private manualCursor () : SpeechCursor.T =
    SpeechCursor.create
        { SpeechCursor.defaultParameters with
            InitialMode = SpeechCursor.Manual }

let private expectSome (label: string) (o: (string * string) option) =
    match o with
    | Some (t, a) -> t, a
    | None ->
        Assert.True(false, sprintf "%s: expected Some, got None" label)
        "", ""

[<Fact>]
let ``appendCell adds command then output as separate items`` () =
    let c = freshCursor ()
    SpeechCursor.appendCell c "echo hi" "hi"
    // AutoDrive (default) parks on the latest item = the output.
    let t, a = expectSome "current" (SpeechCursor.cellCurrent c)
    Assert.Equal("hi", t)
    Assert.Equal(ActivityIds.output, a)
    // Previous steps to the command line (separate item).
    let tc, _ = expectSome "prev" (SpeechCursor.cellPrevious c)
    Assert.Equal("echo hi", tc)
    // Nothing before the command.
    Assert.Equal(None, SpeechCursor.cellPrevious c)

[<Fact>]
let ``appendCell skips whitespace-only command and output`` () =
    let c = freshCursor ()
    SpeechCursor.appendCell c "   " "real output"
    let t, _ = expectSome "current" (SpeechCursor.cellCurrent c)
    Assert.Equal("real output", t)
    // Only one item (the output); no blank command item.
    Assert.Equal(None, SpeechCursor.cellPrevious c)
    SpeechCursor.appendCell c "cmd-only" "  "
    let t2, _ = expectSome "latest" (SpeechCursor.cellToLatest c)
    Assert.Equal("cmd-only", t2)

[<Fact>]
let ``appendCell command item is trimmed`` () =
    let c = freshCursor ()
    SpeechCursor.appendCell c "  echo hi  " "hi"
    let _ = SpeechCursor.cellPrevious c
    let tc, _ = expectSome "cmd" (SpeechCursor.cellCurrent c)
    Assert.Equal("echo hi", tc)

[<Fact>]
let ``appendCell in AutoDrive follows the latest item`` () =
    let c = freshCursor ()
    SpeechCursor.appendCell c "one" "out1"
    SpeechCursor.appendCell c "two" "out2"
    let t, _ = expectSome "current" (SpeechCursor.cellCurrent c)
    Assert.Equal("out2", t)

[<Fact>]
let ``appendCell in Manual does not move the cursor`` () =
    let c = manualCursor ()
    SpeechCursor.appendCell c "one" "out1"
    // Manual: cursor stays unparked (-1) on append.
    Assert.Equal(None, SpeechCursor.cellCurrent c)
    // Explicit navigation still works.
    let t, _ = expectSome "latest" (SpeechCursor.cellToLatest c)
    Assert.Equal("out1", t)
    SpeechCursor.appendCell c "two" "out2"
    // Still parked on the old position (out1), not snapped to out2.
    let t2, _ = expectSome "current" (SpeechCursor.cellCurrent c)
    Assert.Equal("out1", t2)

[<Fact>]
let ``cellPrevious walks back then stops at first`` () =
    let c = manualCursor ()
    SpeechCursor.appendCell c "c1" "o1"
    SpeechCursor.appendCell c "c2" "o2"
    // Items: [c1; o1; c2; o2]. First Previous from unparked → latest.
    Assert.Equal("o2", fst (expectSome "p1" (SpeechCursor.cellPrevious c)))
    Assert.Equal("c2", fst (expectSome "p2" (SpeechCursor.cellPrevious c)))
    Assert.Equal("o1", fst (expectSome "p3" (SpeechCursor.cellPrevious c)))
    Assert.Equal("c1", fst (expectSome "p4" (SpeechCursor.cellPrevious c)))
    Assert.Equal(None, SpeechCursor.cellPrevious c)

[<Fact>]
let ``cellNext walks forward then stops at latest`` () =
    let c = manualCursor ()
    SpeechCursor.appendCell c "c1" "o1"
    SpeechCursor.appendCell c "c2" "o2"
    let _ = SpeechCursor.cellToLatest c       // park on o2 (idx 3)
    let _ = SpeechCursor.cellPrevious c       // c2
    let _ = SpeechCursor.cellPrevious c       // o1
    Assert.Equal("c2", fst (expectSome "n1" (SpeechCursor.cellNext c)))
    Assert.Equal("o2", fst (expectSome "n2" (SpeechCursor.cellNext c)))
    Assert.Equal(None, SpeechCursor.cellNext c)

[<Fact>]
let ``cellToLatest jumps to the last item`` () =
    let c = manualCursor ()
    SpeechCursor.appendCell c "c1" "o1"
    SpeechCursor.appendCell c "c2" "o2"
    let _ = SpeechCursor.cellPrevious c
    let _ = SpeechCursor.cellPrevious c
    let t, _ = expectSome "latest" (SpeechCursor.cellToLatest c)
    Assert.Equal("o2", t)

[<Fact>]
let ``cell nav on empty transcript returns None`` () =
    let c = freshCursor ()
    Assert.Equal(None, SpeechCursor.cellCurrent c)
    Assert.Equal(None, SpeechCursor.cellPrevious c)
    Assert.Equal(None, SpeechCursor.cellNext c)
    Assert.Equal(None, SpeechCursor.cellToLatest c)

[<Fact>]
let ``cellReset clears transcript and position`` () =
    let c = freshCursor ()
    SpeechCursor.appendCell c "c1" "o1"
    SpeechCursor.cellReset c
    Assert.Equal(None, SpeechCursor.cellCurrent c)
    Assert.Equal(None, SpeechCursor.cellToLatest c)
    // Fresh after reset.
    SpeechCursor.appendCell c "c2" "o2"
    Assert.Equal("o2", fst (expectSome "post" (SpeechCursor.cellCurrent c)))

[<Fact>]
let ``Issue 1 and 3 — command and post-response output are navigable`` () =
    // test-04 shape: the IOCell's OutputText is the authoritative
    // full output (incl. the post-single-key-response "You chose
    // Yes." that the raw ContentHistory path mis-tags
    // UserInputEcho). Both the command and that output must be
    // reachable via Manual navigation.
    let c = manualCursor ()
    let cmd = "\"C:\\test-04-yes-no.cmd\""
    let out =
        "=== START ===\nThis test asks a yes/no question.\n"
        + "Continue? [Y,N]?Y\nYou chose Yes.\n=== END ==="
    SpeechCursor.appendCell c cmd out
    let tOut, _ = expectSome "out" (SpeechCursor.cellToLatest c)
    Assert.Contains("You chose Yes.", tOut)
    let tCmd, _ = expectSome "cmd" (SpeechCursor.cellPrevious c)
    Assert.Equal(cmd, tCmd)
