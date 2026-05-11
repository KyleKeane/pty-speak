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
    let cursor = freshCursor ()
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
let ``AutoDrive does not re-announce entries it has already spoken`` () =
    let cursor = freshCursor ()
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
let ``next moves cursor to the next entry by Seq`` () =
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
    Assert.Equal(1L, cursor.Position)

[<Fact>]
let ``previous moves cursor backward`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'a')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'b')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let _ = SpeechCursor.toLatest cursor history
    let prev = SpeechCursor.previous cursor history
    Assert.True(prev.IsSome)
    Assert.True(cursor.Position < ContentHistory.latestSeq history)

[<Fact>]
let ``toLatest jumps to the latest entry`` () =
    let cursor = freshCursor ()
    SpeechCursor.setMode cursor SpeechCursor.Manual
    let history = freshHistory ()
    let _ = ContentHistory.appendFromEvent history t0 (printRune 'h')
    let _ = ContentHistory.appendFromEvent history t0 lf
    let latest = SpeechCursor.toLatest cursor history
    Assert.True(latest.IsSome)
    Assert.Equal(ContentHistory.latestSeq history, cursor.Position)

[<Fact>]
let ``toLatest on empty history returns None and leaves Position at -1`` () =
    let cursor = freshCursor ()
    let history = freshHistory ()
    let result = SpeechCursor.toLatest cursor history
    Assert.Equal(None, result)
    Assert.Equal(-1L, cursor.Position)

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
    let cursor = freshCursor ()
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
    let cursor = freshCursor ()
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
              SettledAt = t0 }
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
              SettledAt = t0 }
    Assert.Equal(None, SpeechCursor.renderEntry span)

[<Fact>]
let ``renderEntry returns None for Newline`` () =
    let nl = ContentHistory.Newline { Seq = 0L; At = t0 }
    Assert.Equal(None, SpeechCursor.renderEntry nl)

[<Fact>]
let ``renderEntry returns mode activity for AltScreenEnter`` () =
    let m =
        ContentHistory.Marker
            { Seq = 0L
              Kind = ContentHistory.MarkerKind.AltScreenEnter
              At = t0
              Payload = None }
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
              Payload = Some "Edit, Yes, Always, No" }
    match SpeechCursor.renderEntry m with
    | Some (text, _) ->
        Assert.Contains("Selection prompt", text)
        Assert.Contains("Edit, Yes, Always, No", text)
    | None -> Assert.Fail("expected announce for SelectionShown")

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
