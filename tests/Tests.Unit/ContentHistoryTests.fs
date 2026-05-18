module PtySpeak.Tests.Unit.ContentHistoryTests

open System
open System.Text
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 45 Commit 1 — ContentHistory pure-function tests.
// ---------------------------------------------------------------------
//
// Pin the public contract of the new aural substrate. The data
// model is the foundation Commit 2 wires into the reader loop +
// detectors + SpeechCursor; a regression here silently breaks the
// entire aural pipeline without surfacing in any other test until
// NVDA validation time. Cover:
//
//   * Empty / latestSeq / count contracts
//   * Print accumulation into the active span (no entry until seal)
//   * Newline seal + Newline entry
//   * BEL seal + Marker entry
//   * Explicit appendMarker seals the active span first
//   * Deferred CR resolution (CRLF → newline; bare CR → overwrite)
//   * Deferred CUB resolution (CUB+Print → overwrite; CUB+other → no-op)
//   * Idle tick seals the active span
//   * Reset clears all state
//
// Helpers mirror the existing test conventions (`t0`, `after`,
// `ascii`) for paste-friendliness.

let private t0 = DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

let private printRune (c: char) : VtEvent =
    Print (Rune c)

let private execute (b: byte) : VtEvent = Execute b
let private lf = execute 0x0Auy
let private cr = execute 0x0Duy
let private bel = execute 0x07uy
let private bs = execute 0x08uy

let private cub (n: int) : VtEvent =
    CsiDispatch ([| n |], [||], 'D', None)

let private cup (row: int) (col: int) : VtEvent =
    CsiDispatch ([| row; col |], [||], 'H', None)

let private cuu (n: int) : VtEvent =
    CsiDispatch ([| n |], [||], 'A', None)

let private cud (n: int) : VtEvent =
    CsiDispatch ([| n |], [||], 'B', None)

let private freshHistory () : ContentHistory.T =
    ContentHistory.create ContentHistory.defaultParameters

let private feed
        (state: ContentHistory.T)
        (now: DateTime)
        (events: VtEvent list)
        : ContentHistory.Entry list =
    events
    |> List.collect (fun e -> ContentHistory.appendFromEvent state now e)

// ---------------------------------------------------------------------
// Initial state contracts
// ---------------------------------------------------------------------

[<Fact>]
let ``empty history reports count 0 and latestSeq -1`` () =
    let state = freshHistory ()
    Assert.Equal(0, ContentHistory.count state)
    Assert.Equal(-1L, ContentHistory.latestSeq state)
    Assert.Equal<ContentHistory.Entry[]>([||], ContentHistory.snapshot state)

[<Fact>]
let ``entryBySeq returns None on empty history`` () =
    let state = freshHistory ()
    Assert.Equal(None, ContentHistory.entryBySeq state 0L)
    Assert.Equal(None, ContentHistory.entryBySeq state 99L)

// ---------------------------------------------------------------------
// Print accumulates without sealing
// ---------------------------------------------------------------------

[<Fact>]
let ``Print events accumulate into active span without producing entries`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ printRune 'h'; printRune 'i' ]
    Assert.Empty(emitted)
    Assert.Equal(0, ContentHistory.count state)

// ---------------------------------------------------------------------
// LF seals the active span and emits Newline
// ---------------------------------------------------------------------

[<Fact>]
let ``LF seals the active span and appends a Newline entry`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ printRune 'h'; printRune 'i'; lf ]
    // Expect: the sealed TextSpan + the Newline.
    Assert.Equal(2, emitted.Length)
    match emitted with
    | [ ContentHistory.TextSpan span; ContentHistory.Newline nl ] ->
        Assert.Equal("hi", span.Text)
        Assert.Equal(0L, span.Seq)
        Assert.Equal(1L, nl.Seq)
    | _ -> Assert.Fail(sprintf "unexpected emit shape: %A" emitted)
    Assert.Equal(2, ContentHistory.count state)
    Assert.Equal(1L, ContentHistory.latestSeq state)

[<Fact>]
let ``LF with no prior Print emits only the Newline`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ lf ]
    Assert.Equal(1, emitted.Length)
    match emitted with
    | [ ContentHistory.Newline nl ] -> Assert.Equal(0L, nl.Seq)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)

// ---------------------------------------------------------------------
// BEL emits a BellRang marker (and seals active span first)
// ---------------------------------------------------------------------

[<Fact>]
let ``BEL byte seals the active span and emits a BellRang marker`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ printRune 'h'; bel ]
    Assert.Equal(2, emitted.Length)
    match emitted with
    | [ ContentHistory.TextSpan span; ContentHistory.Marker m ] ->
        Assert.Equal("h", span.Text)
        Assert.Equal(ContentHistory.MarkerKind.BellRang, m.Kind)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)

// ---------------------------------------------------------------------
// appendMarker explicit insertion
// ---------------------------------------------------------------------

[<Fact>]
let ``appendMarker seals any active span before inserting the marker`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'h'; printRune 'i' ]
    let emitted =
        ContentHistory.appendMarker
            state
            ContentHistory.MarkerKind.PromptStart
            t0
            None
    Assert.Equal(2, emitted.Length)
    match emitted with
    | [ ContentHistory.TextSpan span; ContentHistory.Marker m ] ->
        Assert.Equal("hi", span.Text)
        Assert.Equal(ContentHistory.MarkerKind.PromptStart, m.Kind)
        Assert.Equal(None, m.Payload)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)

[<Fact>]
let ``appendMarker with no active span emits only the marker`` () =
    let state = freshHistory ()
    let emitted =
        ContentHistory.appendMarker
            state
            ContentHistory.MarkerKind.AltScreenEnter
            t0
            (Some "claude")
    Assert.Equal(1, emitted.Length)
    match emitted with
    | [ ContentHistory.Marker m ] ->
        Assert.Equal(ContentHistory.MarkerKind.AltScreenEnter, m.Kind)
        Assert.Equal(Some "claude", m.Payload)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)

// ---------------------------------------------------------------------
// CR deferred resolution
// ---------------------------------------------------------------------

[<Fact>]
let ``CR followed by LF is a CRLF newline, no overwrite emitted`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ printRune 'h'; printRune 'i'; cr; lf ]
    // Expect: sealed TextSpan + Newline; CR did NOT emit an
    // Overwrite because LF resolved the deferral as CRLF.
    Assert.Equal(2, emitted.Length)
    let hasOverwrite =
        emitted
        |> List.exists (fun e ->
            match e with
            | ContentHistory.Overwrite _ -> true
            | _ -> false)
    Assert.False(hasOverwrite)
    let hasNewline =
        emitted
        |> List.exists (fun e ->
            match e with
            | ContentHistory.Newline _ -> true
            | _ -> false)
    Assert.True(hasNewline)

[<Fact>]
let ``bare CR followed by Print resolves to an Overwrite over the sealed span`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'a'; printRune 'b'; printRune 'c'; cr ]
    // Now feed a Print; the CR's deferred resolution should
    // seal "abc" and emit an Overwrite.
    let emitted = feed state (after 5) [ printRune 'X' ]
    let hasSealed =
        emitted
        |> List.exists (fun e ->
            match e with
            | ContentHistory.TextSpan s when s.Text = "abc" -> true
            | _ -> false)
    let hasOverwrite =
        emitted
        |> List.exists (fun e ->
            match e with
            | ContentHistory.Overwrite _ -> true
            | _ -> false)
    Assert.True(hasSealed, sprintf "expected sealed 'abc' span: %A" emitted)
    Assert.True(hasOverwrite, sprintf "expected Overwrite: %A" emitted)

// ---------------------------------------------------------------------
// CUB deferred resolution
// ---------------------------------------------------------------------

[<Fact>]
let ``CUB followed by Print resolves to an Overwrite (Fact 12 equivalent)`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'a'; printRune 'b'; printRune 'c'; cub 3 ]
    // CUB defers; Print resolves to in-place overwrite.
    let emitted = feed state (after 5) [ printRune 'X' ]
    let hasOverwrite =
        emitted
        |> List.exists (fun e ->
            match e with
            | ContentHistory.Overwrite _ -> true
            | _ -> false)
    Assert.True(hasOverwrite, sprintf "expected Overwrite: %A" emitted)

[<Fact>]
let ``CUB followed by LF does NOT emit an Overwrite (cmd output regression)`` () =
    let state = freshHistory ()
    let emitted =
        feed state t0
            [ printRune 'h'
              printRune 'i'
              cub 1
              lf ]
    // The CUB's deferred resolution should clear without
    // transition when LF (not Print) follows. The sealed
    // TextSpan "hi" + Newline emit normally.
    let overwrites =
        emitted
        |> List.filter (fun e ->
            match e with
            | ContentHistory.Overwrite _ -> true
            | _ -> false)
    Assert.Empty(overwrites)
    let textSpans =
        emitted
        |> List.choose (fun e ->
            match e with
            | ContentHistory.TextSpan s -> Some s.Text
            | _ -> None)
    Assert.Contains("hi", textSpans)

[<Fact>]
let ``CUB followed by another CSI does NOT emit an Overwrite`` () =
    let state = freshHistory ()
    let emitted =
        feed state t0
            [ printRune 'a'
              cub 1
              cuu 1
              printRune 'b'
              lf ]
    // Note: this test pins ContentHistory's CUB-deferred behavior
    // specifically. The active span "a" should NOT be sealed by
    // the CUB-then-CUU sequence; CUU has no classifier in
    // ContentHistory yet (deferred to a future cycle).
    // After the next Print 'b', the span "ab" continues to
    // accumulate; the LF seals "ab" into a TextSpan.
    let textSpans =
        emitted
        |> List.choose (fun e ->
            match e with
            | ContentHistory.TextSpan s -> Some s.Text
            | _ -> None)
    Assert.Contains("ab", textSpans)
    let overwrites =
        emitted
        |> List.filter (fun e ->
            match e with
            | ContentHistory.Overwrite _ -> true
            | _ -> false)
    Assert.Empty(overwrites)

// ---------------------------------------------------------------------
// Tick / idle seal
// ---------------------------------------------------------------------

[<Fact>]
let ``tick within the idle window does NOT seal`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'h'; printRune 'i' ]
    // Tick at t0+50 ms; defaultParameters.IdleSpanSealMs = 200.
    let emitted = ContentHistory.tick state (after 50)
    Assert.Empty(emitted)
    Assert.Equal(0, ContentHistory.count state)

[<Fact>]
let ``tick past the idle window seals the active span`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'h'; printRune 'i' ]
    // Tick at t0+250 ms; past IdleSpanSealMs (200).
    let emitted = ContentHistory.tick state (after 250)
    Assert.Equal(1, emitted.Length)
    match emitted with
    | [ ContentHistory.TextSpan span ] ->
        Assert.Equal("hi", span.Text)
        Assert.Equal(0L, span.Seq)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)
    Assert.Equal(1, ContentHistory.count state)

[<Fact>]
let ``tick with no active content is a no-op`` () =
    let state = freshHistory ()
    let emitted = ContentHistory.tick state (after 5000)
    Assert.Empty(emitted)

// ---------------------------------------------------------------------
// Reset
// ---------------------------------------------------------------------

[<Fact>]
let ``reset clears all state and restarts seq from zero`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'h'; printRune 'i'; lf ]
    Assert.True(ContentHistory.count state > 0)
    ContentHistory.reset state
    Assert.Equal(0, ContentHistory.count state)
    Assert.Equal(-1L, ContentHistory.latestSeq state)
    // After reset, the next entry gets Seq 0.
    let emitted = feed state t0 [ lf ]
    match emitted with
    | [ ContentHistory.Newline nl ] -> Assert.Equal(0L, nl.Seq)
    | _ -> Assert.Fail(sprintf "unexpected: %A" emitted)

// ---------------------------------------------------------------------
// Identity / seq uniqueness
// ---------------------------------------------------------------------

[<Fact>]
let ``repeated identical content gets distinct Seq numbers`` () =
    let state = freshHistory ()
    // Feed "hi\n" twice. Two distinct TextSpan entries with
    // distinct Seqs, even though the Text is identical. This is
    // the property that distinguishes ContentHistory from a
    // screen-grid-diff baseline.
    let _ = feed state t0 [ printRune 'h'; printRune 'i'; lf ]
    let _ = feed state (after 100) [ printRune 'h'; printRune 'i'; lf ]
    let spans =
        ContentHistory.snapshot state
        |> Array.choose (fun e ->
            match e with
            | ContentHistory.TextSpan s -> Some s
            | _ -> None)
    Assert.Equal(2, spans.Length)
    Assert.NotEqual<int64>(spans.[0].Seq, spans.[1].Seq)
    Assert.Equal(spans.[0].Text, spans.[1].Text)

[<Fact>]
let ``entryBySeq retrieves by identity, not text`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'h'; printRune 'i'; lf ]
    // Seq 0 is the sealed "hi" span; Seq 1 is the Newline.
    let s0 = ContentHistory.entryBySeq state 0L
    let s1 = ContentHistory.entryBySeq state 1L
    Assert.True(s0.IsSome)
    Assert.True(s1.IsSome)
    match s0.Value with
    | ContentHistory.TextSpan span ->
        Assert.Equal("hi", span.Text)
    | other ->
        Assert.Fail(sprintf "expected TextSpan at Seq 0, got %A" other)
    match s1.Value with
    | ContentHistory.Newline _ -> ()
    | other ->
        Assert.Fail(sprintf "expected Newline at Seq 1, got %A" other)

// ---------------------------------------------------------------------
// Snapshot is independent
// ---------------------------------------------------------------------

[<Fact>]
let ``snapshot returns a fresh array independent of further appends`` () =
    let state = freshHistory ()
    let _ = feed state t0 [ printRune 'a'; lf ]
    let snap1 = ContentHistory.snapshot state
    let _ = feed state (after 10) [ printRune 'b'; lf ]
    let snap2 = ContentHistory.snapshot state
    Assert.Equal(2, snap1.Length)
    Assert.Equal(4, snap2.Length)

// ---------------------------------------------------------------------
// Cycle 45c — tryLatestMarker + sliceText helpers
// ---------------------------------------------------------------------

[<Fact>]
let ``tryLatestMarker on empty history returns None`` () =
    let state = freshHistory ()
    Assert.Equal(None,
        ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart)

[<Fact>]
let ``tryLatestMarker returns Some when the kind is present`` () =
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    let result =
        ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart
    Assert.True(result.IsSome)
    Assert.Equal(ContentHistory.MarkerKind.PromptStart, result.Value.Kind)

[<Fact>]
let ``tryLatestMarker returns the most recent of repeated kinds`` () =
    // Cycle 45e re-emits PromptStart after a dirty intermission.
    // tryLatestMarker must return the LATEST so commandText reflects
    // the user's currently-typed (post-intermission) command.
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0
        (Some "first")
    |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart (after 10)
        (Some "second")
    |> ignore
    let result =
        ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart
    Assert.True(result.IsSome)
    Assert.Equal(Some "second", result.Value.Payload)

[<Fact>]
let ``tryLatestMarker ignores other kinds`` () =
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart (after 5) None
    |> ignore
    let pstart =
        ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart
    let cfin =
        ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.CommandFinished
    Assert.True(pstart.IsSome)
    Assert.Equal(None, cfin)

[<Fact>]
let ``sliceText between two markers concatenates TextSpan + Newline`` () =
    // Layout: [PromptStart, "echo hello", Newline, OutputStart]
    // Expected commandText slice = "echo hello\n"
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    let _ =
        feed state (after 5)
            [ printRune 'e'; printRune 'c'; printRune 'h'
              printRune 'o'; printRune ' '; printRune 'h'
              printRune 'e'; printRune 'l'; printRune 'l'
              printRune 'o'; lf ]
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart (after 10) None
    |> ignore
    let pstart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart).Value
    let ostart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.OutputStart).Value
    Assert.Equal(
        "echo hello\n",
        ContentHistory.sliceText state pstart.Seq ostart.Seq)

[<Fact>]
let ``sliceText with toSeqExclusive = MaxValue includes the tail`` () =
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart t0 None
    |> ignore
    let _ =
        feed state (after 5)
            [ printRune 'h'; printRune 'i'; lf ]
    let ostart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.OutputStart).Value
    Assert.Equal(
        "hi\n",
        ContentHistory.sliceText state ostart.Seq Int64.MaxValue)

[<Fact>]
let ``sliceText includes the unsealed active span when in-region`` () =
    // At tuple-finalise time the CommandFinished marker hasn't
    // been appended yet, so the trailing output text sits in the
    // unsealed active span. sliceText must include it.
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart t0 None
    |> ignore
    let _ =
        feed state (after 5)
            [ printRune 'h'; printRune 'i' ]
    // No newline + no marker → "hi" stays in the active span
    let ostart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.OutputStart).Value
    Assert.Equal(
        "hi",
        ContentHistory.sliceText state ostart.Seq Int64.MaxValue)

[<Fact>]
let ``sliceText returns empty string when the region is empty`` () =
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart (after 1) None
    |> ignore
    let pstart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart).Value
    let ostart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.OutputStart).Value
    Assert.Equal(
        "",
        ContentHistory.sliceText state pstart.Seq ostart.Seq)

[<Fact>]
let ``sliceText excludes the marker entries themselves`` () =
    // PromptStart.Seq = 0, "x" TextSpan after newline at Seq = 1+,
    // OutputStart.Seq = N. Markers are boundary tokens — they
    // contribute "" so even if the comparison were inclusive the
    // visible behaviour is identical. This test pins the
    // strict-comparison contract.
    let state = freshHistory ()
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0
        (Some "prompt-text")
    |> ignore
    let _ = feed state (after 5) [ printRune 'x'; lf ]
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart (after 10)
        (Some "output-marker-text")
    |> ignore
    let pstart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.PromptStart).Value
    let ostart =
        (ContentHistory.tryLatestMarker
            state ContentHistory.MarkerKind.OutputStart).Value
    let result =
        ContentHistory.sliceText state pstart.Seq ostart.Seq
    Assert.Equal("x\n", result)
    Assert.DoesNotContain("prompt-text", result)
    Assert.DoesNotContain("output-marker-text", result)

// ---------------------------------------------------------------------
// Cursor-row synthetic newline (Cycle 47 follow-up post-preview.116)
// ---------------------------------------------------------------------

[<Fact>]
let ``CSI CUP to new row seals active span and emits a Newline`` () =
    // cmd's conpty translator emits CSI [2;1H between visual
    // rows of the banner instead of CRLF; pre-fix the active
    // span accumulated straight across rows. Post-fix: row
    // change triggers seal + synthetic Newline.
    let state = freshHistory ()
    feed state t0 [ printRune 'a'; printRune 'b'; printRune 'c' ] |> ignore
    let emitted = feed state t0 [ cup 2 1 ] |> List.last
    let entries = ContentHistory.snapshot state
    // Expect: TextSpan "abc" (sealed by the row change) +
    // synthetic Newline.
    Assert.Equal(2, entries.Length)
    match entries with
    | [| ContentHistory.TextSpan span; ContentHistory.Newline _ |] ->
        Assert.Equal("abc", span.Text)
    | _ ->
        Assert.Fail(sprintf "Unexpected entries: %A" entries)

[<Fact>]
let ``CSI CUP to same row does not seal or emit a Newline`` () =
    // Targeting the same row the cursor is already on must not
    // produce a synthetic Newline — that would over-segment
    // SGR-only redraws and spinner-style mid-row updates.
    let state = freshHistory ()
    // Position cursor on row 0 explicitly, accumulate "abc",
    // then re-position to row 0.
    feed state t0 [ cup 1 1 ] |> ignore
    feed state t0 [ printRune 'a'; printRune 'b'; printRune 'c' ] |> ignore
    feed state t0 [ cup 1 5 ] |> ignore
    let entries = ContentHistory.snapshot state
    Assert.Equal(0, entries.Length)

[<Fact>]
let ``LF after CSI cursor move does not double-emit Newline`` () =
    // The row-change detector and the LF handler must not both
    // fire for an LF event. Pre-fix verification: confirm only
    // one Newline lands when CSI CUP is followed by a Print +
    // LF.
    let state = freshHistory ()
    feed state t0 [ cup 2 1; printRune 'x'; lf ] |> ignore
    let entries = ContentHistory.snapshot state
    // Expect: TextSpan "x" + Newline. CUP at row 2 from start
    // (row 0) changed row but active span was empty, so no
    // synthetic Newline. Print "x" accumulates. LF seals "x"
    // + emits its own Newline.
    Assert.Equal(2, entries.Length)
    match entries with
    | [| ContentHistory.TextSpan span; ContentHistory.Newline _ |] ->
        Assert.Equal("x", span.Text)
    | _ ->
        Assert.Fail(sprintf "Unexpected entries: %A" entries)

[<Fact>]
let ``Banner-like CSI-positioned rows render with newlines between them`` () =
    // Reproducer for the preview.116 failure mode where cmd's
    // banner used CSI cursor-positioning between visual rows
    // and the rendered tail collapsed
    // "Microsoft Windows..." + "(c) Microsoft..." + prompt
    // into one line with no separators.
    let state = freshHistory ()
    // Row 1: "row-one"
    feed state t0 (List.append [ cup 1 1 ] [ for c in "row-one" -> printRune c ])
    |> ignore
    // CUP to row 2.
    feed state t0 [ cup 2 1 ] |> ignore
    // Row 2: "row-two"
    feed state t0 [ for c in "row-two" -> printRune c ] |> ignore
    // CUP to row 3.
    feed state t0 [ cup 3 1 ] |> ignore
    // Row 3: "row-three"
    feed state t0 [ for c in "row-three" -> printRune c ] |> ignore
    // Force a final seal so the active span lands in the
    // rendered tail too.
    ContentHistory.tick state (t0.AddSeconds(10.0)) |> ignore
    let result = ContentHistory.tailText state 4096
    Assert.Equal("row-one\nrow-two\nrow-three", result)

// ---------------------------------------------------------------------
// tailText / tailTextWithMarkers (Cycle 45c + Cycle 47 follow-up)
// ---------------------------------------------------------------------

[<Fact>]
let ``tailText skips markers; markers do not appear in output`` () =
    let state = freshHistory ()
    feed state t0 [ printRune 'h'; printRune 'i'; lf ] |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    feed state t0 [ printRune 'a'; printRune 'b'; lf ] |> ignore
    let result = ContentHistory.tailText state 4096
    Assert.Equal("hi\nab\n", result)
    Assert.DoesNotContain("begin prompt", result)
    Assert.DoesNotContain("---", result)

[<Fact>]
let ``tailTextWithMarkers renders PromptStart as a navigable line`` () =
    let state = freshHistory ()
    feed state t0 [ printRune 'h'; printRune 'i'; lf ] |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    feed state t0 [ printRune 'a'; printRune 'b'; lf ] |> ignore
    let result = ContentHistory.tailTextWithMarkers state 4096
    Assert.Contains("--- begin prompt ---", result)
    // Marker line is bracketed by newlines so NVDA's
    // Move(Line, ±1) lands on it as a standalone unit.
    Assert.Contains("\n--- begin prompt ---\n", result)

[<Fact>]
let ``tailTextWithMarkers renders each MarkerKind with the documented label`` () =
    // Cover every MarkerKind so a future rename / addition
    // surfaces here. Cycle 47 follow-up relabelled the four
    // input/output boundary markers to the parallel
    // begin/end wording per maintainer preview.114 review.
    let expectations =
        [ ContentHistory.MarkerKind.PromptStart, "begin prompt"
          ContentHistory.MarkerKind.CommandStart, "end prompt"
          ContentHistory.MarkerKind.OutputStart, "begin output"
          ContentHistory.MarkerKind.CommandFinished, "end output"
          ContentHistory.MarkerKind.BellRang, "bell"
          ContentHistory.MarkerKind.SelectionShown, "selection prompt"
          ContentHistory.MarkerKind.SelectionDismissed, "selection dismissed"
          ContentHistory.MarkerKind.AltScreenEnter, "entered alt-screen"
          ContentHistory.MarkerKind.AltScreenExit, "left alt-screen" ]
    for (kind, expectedLabel) in expectations do
        let state = freshHistory ()
        ContentHistory.appendMarker state kind t0 None |> ignore
        let result = ContentHistory.tailTextWithMarkers state 4096
        Assert.Contains(sprintf "--- %s ---" expectedLabel, result)

[<Fact>]
let ``tailTextWithMarkers renders Custom marker with the tag payload`` () =
    let state = freshHistory ()
    ContentHistory.appendMarker
        state (ContentHistory.MarkerKind.Custom "tool-call") t0 None
    |> ignore
    let result = ContentHistory.tailTextWithMarkers state 4096
    Assert.Contains("--- custom: tool-call ---", result)

[<Fact>]
let ``tailTextWithMarkers preserves text content alongside marker lines`` () =
    let state = freshHistory ()
    feed state t0 [ printRune 'd'; printRune 'i'; printRune 'r'; lf ] |> ignore
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.OutputStart t0 None
    |> ignore
    feed state t0 [ printRune 'a'; printRune 'b'; lf ] |> ignore
    let result = ContentHistory.tailTextWithMarkers state 4096
    Assert.Contains("dir", result)
    Assert.Contains("--- begin output ---", result)
    Assert.Contains("ab", result)
    // Chronological order is preserved: content before marker
    // appears before the marker label; content after appears
    // after.
    let dirIdx = result.IndexOf("dir")
    let markerIdx = result.IndexOf("--- begin output ---")
    let abIdx = result.IndexOf("ab")
    Assert.True(
        dirIdx < markerIdx && markerIdx < abIdx,
        sprintf
            "Expected dir<marker<ab; got dir=%d marker=%d ab=%d (result=%A)"
            dirIdx markerIdx abIdx result)

[<Fact>]
let ``tailTextWithMarkersSealedOnly excludes the active span`` () =
    // Cycle 47 follow-up (2026-05-13) post-preview.114 — the
    // typing-window gate in the UIA materialiser routes through
    // this variant. The unsealed tail (here: "in_progress")
    // must NOT contribute.
    let state = freshHistory ()
    feed state t0 [ printRune 's'; printRune 'e'; printRune 'a'; printRune 'l'; printRune 'e'; printRune 'd'; lf ] |> ignore
    feed state t0 [ printRune 'i'; printRune 'n'; printRune '_'; printRune 'p'; printRune 'r'; printRune 'o'; printRune 'g'; printRune 'r'; printRune 'e'; printRune 's'; printRune 's' ] |> ignore
    let result = ContentHistory.tailTextWithMarkersSealedOnly state 4096
    Assert.Contains("sealed", result)
    Assert.DoesNotContain("in_progress", result)

[<Fact>]
let ``tailText and tailTextWithMarkers agree when no markers exist`` () =
    let state = freshHistory ()
    feed state t0 [ printRune 'a'; printRune 'b'; lf; printRune 'c'; lf ] |> ignore
    Assert.Equal(
        ContentHistory.tailText state 4096,
        ContentHistory.tailTextWithMarkers state 4096)

// ---------------------------------------------------------------------
// Cycle 48 PR-C — Source tag (EntrySource per ADR 0003 §9.5)
// ---------------------------------------------------------------------

[<Fact>]
let ``Default Source is Unknown when no resolver is set`` () =
    // Without setSourceResolver, all entries get Unknown.
    let state = freshHistory ()
    feed state t0 [ printRune 'h'; printRune 'i'; lf ] |> ignore
    let entries = ContentHistory.snapshot state
    for e in entries do
        Assert.Equal(
            ContentHistory.EntrySource.Unknown,
            ContentHistory.entrySource e)

[<Fact>]
let ``Marker entries always carry EntrySource.BoundaryMarker`` () =
    let state = freshHistory ()
    // Set a non-default resolver — Marker should still be Marker.
    ContentHistory.setSourceResolver state (fun () ->
        ContentHistory.EntrySource.CmdOutput)
    ContentHistory.appendMarker
        state ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    let entries = ContentHistory.snapshot state
    let marker =
        entries
        |> Array.find (fun e ->
            match e with
            | ContentHistory.Marker _ -> true
            | _ -> false)
    Assert.Equal(
        ContentHistory.EntrySource.BoundaryMarker,
        ContentHistory.entrySource marker)

[<Fact>]
let ``Source resolver applies to non-marker entries`` () =
    let state = freshHistory ()
    let mutable currentSource = ContentHistory.EntrySource.UserInputEcho
    ContentHistory.setSourceResolver state (fun () -> currentSource)
    // Append in UserInputEcho mode (typing).
    feed state t0 [ printRune 'e'; printRune 'c'; printRune 'h'; printRune 'o'; lf ]
    |> ignore
    // Switch to CmdOutput (executing) and append more.
    currentSource <- ContentHistory.EntrySource.CmdOutput
    feed state t0 [ printRune 'h'; printRune 'i'; lf ] |> ignore
    let entries = ContentHistory.snapshot state
    // Should have 4 entries: TextSpan + Newline + TextSpan + Newline.
    let textSpanSources =
        entries
        |> Array.choose (fun e ->
            match e with
            | ContentHistory.TextSpan d -> Some d.Source
            | _ -> None)
    Assert.Equal(2, textSpanSources.Length)
    Assert.Equal(ContentHistory.EntrySource.UserInputEcho, textSpanSources.[0])
    Assert.Equal(ContentHistory.EntrySource.CmdOutput, textSpanSources.[1])

[<Fact>]
let ``Resolver exception falls back to Unknown`` () =
    let state = freshHistory ()
    ContentHistory.setSourceResolver state (fun () ->
        failwith "resolver crash")
    feed state t0 [ printRune 'x'; lf ] |> ignore
    let entries = ContentHistory.snapshot state
    for e in entries do
        Assert.Equal(
            ContentHistory.EntrySource.Unknown,
            ContentHistory.entrySource e)

// ---------------------------------------------------------------------
// #428 — BS (0x08) destructive cursor-back on the active span
// ---------------------------------------------------------------------

let private textSpans (state: ContentHistory.T) : string[] =
    ContentHistory.snapshot state
    |> Array.choose (fun e ->
        match e with
        | ContentHistory.TextSpan d -> Some d.Text
        | _ -> None)

[<Fact>]
let ``BS deletes the last char of the active span`` () =
    let state = freshHistory ()
    feed state t0 [ printRune 'a'; printRune 'b'; printRune 'c'; bs; lf ]
    |> ignore
    Assert.Equal<string[]>([| "ab" |], textSpans state)

[<Fact>]
let ``BS on an empty active span is a no-op`` () =
    let state = freshHistory ()
    let emitted = feed state t0 [ bs ]
    Assert.Empty(emitted)
    Assert.Equal(0, ContentHistory.count state)
    Assert.Equal(-1L, ContentHistory.latestSeq state)
    // Subsequent content is unaffected.
    feed state t0 [ printRune 'x'; lf ] |> ignore
    Assert.Equal<string[]>([| "x" |], textSpans state)

[<Fact>]
let ``cmd BS-SP-BS erase idiom nets to a single deletion`` () =
    let state = freshHistory ()
    // "ECHO HELLOXX" then two `BS SP BS` erase idioms → "ECHO HELLO".
    let typed = [ for c in "ECHO HELLOXX" -> printRune c ]
    let eraseIdiom = [ bs; printRune ' '; bs ]
    feed state t0 (typed @ eraseIdiom @ eraseIdiom @ [ lf ]) |> ignore
    Assert.Equal<string[]>([| "ECHO HELLO" |], textSpans state)

[<Fact>]
let ``BS does not reach across a seal boundary into a sealed span`` () =
    let state = freshHistory ()
    // "ab" + LF seals "ab"; BS now hits an EMPTY active span
    // (no-op) and must not mutate the sealed "ab".
    feed state t0
        [ printRune 'a'; printRune 'b'; lf; bs; printRune 'c'; lf ]
    |> ignore
    Assert.Equal<string[]>([| "ab"; "c" |], textSpans state)

[<Fact>]
let ``tick does NOT idle-seal a UserInputEcho active span (#428 ii)`` () =
    let state = freshHistory ()
    ContentHistory.setSourceResolver state (fun () ->
        ContentHistory.EntrySource.UserInputEcho)
    feed state t0
        [ printRune 'e'; printRune 'c'; printRune 'h'; printRune 'o' ]
    |> ignore
    // Idle well past IdleSpanSealMs (200).
    let ticked = ContentHistory.tick state (after 5000)
    Assert.Empty(ticked)
    Assert.Equal(0, ContentHistory.count state)
    // Still one unsealed span; an LF now seals the WHOLE
    // command in one TextSpan (no idle fragmentation).
    feed state (after 5001) [ lf ] |> ignore
    Assert.Equal<string[]>([| "echo" |], textSpans state)

[<Fact>]
let ``tick still idle-seals a CmdOutput active span`` () =
    let state = freshHistory ()
    ContentHistory.setSourceResolver state (fun () ->
        ContentHistory.EntrySource.CmdOutput)
    feed state t0 [ printRune 'h'; printRune 'i' ] |> ignore
    let ticked = ContentHistory.tick state (after 500)
    Assert.Equal(1, List.length ticked)
    Assert.Equal(1, ContentHistory.count state)

