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

let private cub (n: int) : VtEvent =
    CsiDispatch ([| n |], [||], 'D', None)

let private cuu (n: int) : VtEvent =
    CsiDispatch ([| n |], [||], 'A', None)

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
