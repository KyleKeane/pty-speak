module PtySpeak.Tests.Unit.ShellInteractionTests

open System
open Xunit
open Terminal.Core

/// Cycle 48 PR-B (ADR 0003) — unit tests for the
/// `ShellInteraction` state machine. Covers the pure
/// `tryTransition` function, the sub-prompt detector, and
/// the SinglekeySubmit pattern matcher. The composition-
/// root wiring (signal sources → `applyTransition`) is
/// validated via the Cycle 48-B1 → 48-B8 NVDA matrix walk
/// against the CMD test corpus.

let private t0 = DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

let private freshState () : ShellInteraction.State =
    let s = ShellInteraction.State()
    s.Reset()
    s.LastTransitionAt <- t0
    // Reset bumps EnteredAt to UtcNow; pin it to t0 for
    // deterministic asserts.
    match s.Current with
    | ShellInteraction.Composing data ->
        s.Current <-
            ShellInteraction.Composing
                { data with EnteredAt = t0 }
    | _ -> ()
    s

// ---------------------------------------------------------------------
// tryTransition — pure transitions
// ---------------------------------------------------------------------

[<Fact>]
let ``EnterPressed from Composing transitions to Executing`` () =
    let prior =
        ShellInteraction.Composing
            { EnteredAt = t0
              PromptText = ValueSome "C:\\Users\\Kyle>"
              SinglekeySubmit = false }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.EnterPressed "echo hi") (after 100)
    match result with
    | Some (ShellInteraction.Executing data) ->
        Assert.Equal("echo hi", data.SubmittedCommand)
        Assert.Equal(after 100, data.EnteredAt)
        Assert.False(data.OutputLastByteIsLf)
    | other -> Assert.Fail(sprintf "Expected Executing; got %A" other)

[<Fact>]
let ``EnterPressed while already Executing is a no-op`` () =
    let prior =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "ping localhost"
              OutputLastByteIsLf = false
              OutputLastByteAt = t0 }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.EnterPressed "subordinate") (after 200)
    Assert.Equal(None, result)

[<Fact>]
let ``PromptDetected from Executing transitions to Composing`` () =
    let prior =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "echo hi"
              OutputLastByteIsLf = true
              OutputLastByteAt = after 50 }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.PromptDetected "C:\\Users\\Kyle>") (after 100)
    match result with
    | Some (ShellInteraction.Composing data) ->
        Assert.Equal(ValueSome "C:\\Users\\Kyle>", data.PromptText)
        Assert.False(data.SinglekeySubmit)
    | other -> Assert.Fail(sprintf "Expected Composing; got %A" other)

[<Fact>]
let ``PromptDetected while Composing refreshes prompt text in place`` () =
    // Heuristic re-fired (screen redraw). Refresh PromptText
    // but don't bump EnteredAt.
    let prior =
        ShellInteraction.Composing
            { EnteredAt = t0
              PromptText = ValueSome "old prompt"
              SinglekeySubmit = false }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.PromptDetected "new prompt") (after 500)
    match result with
    | Some (ShellInteraction.Composing data) ->
        Assert.Equal(ValueSome "new prompt", data.PromptText)
        // EnteredAt unchanged.
        Assert.Equal(t0, data.EnteredAt)
    | other -> Assert.Fail(sprintf "Expected Composing refresh; got %A" other)

[<Fact>]
let ``SubPromptIdle from Executing transitions to Composing`` () =
    let prior =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "set /p name=Enter your name:"
              OutputLastByteIsLf = false
              OutputLastByteAt = after 50 }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.SubPromptIdle "Enter your name:") (after 500)
    match result with
    | Some (ShellInteraction.Composing data) ->
        Assert.Equal(ValueSome "Enter your name:", data.PromptText)
        // Not a single-key prompt.
        Assert.False(data.SinglekeySubmit)
    | other -> Assert.Fail(sprintf "Expected Composing; got %A" other)

[<Fact>]
let ``SubPromptIdle with choice-style prompt sets SinglekeySubmit`` () =
    let prior =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "choice /c YNAE /m Pick:"
              OutputLastByteIsLf = false
              OutputLastByteAt = after 50 }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.SubPromptIdle "Pick: [Y,N,A,E]?") (after 500)
    match result with
    | Some (ShellInteraction.Composing data) ->
        Assert.True(data.SinglekeySubmit)
    | other -> Assert.Fail(sprintf "Expected Composing single-key; got %A" other)

[<Fact>]
let ``SubPromptIdle with pause-style prompt sets SinglekeySubmit`` () =
    let prior =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "pause"
              OutputLastByteIsLf = false
              OutputLastByteAt = after 50 }
    let result =
        ShellInteraction.tryTransition
            prior
            (ShellInteraction.SubPromptIdle "Press any key to continue . . . ")
            (after 500)
    match result with
    | Some (ShellInteraction.Composing data) ->
        Assert.True(data.SinglekeySubmit)
    | other -> Assert.Fail(sprintf "Expected Composing single-key; got %A" other)

[<Fact>]
let ``SubPromptIdle while Composing is a no-op`` () =
    let prior =
        ShellInteraction.Composing
            { EnteredAt = t0
              PromptText = ValueSome "C:\\>"
              SinglekeySubmit = false }
    let result =
        ShellInteraction.tryTransition
            prior (ShellInteraction.SubPromptIdle "stale") (after 500)
    Assert.Equal(None, result)

// ---------------------------------------------------------------------
// isSingleKeySubmit
// ---------------------------------------------------------------------

[<Theory>]
[<InlineData("Pick: [Y,N,A,E]?")>]
[<InlineData("Continue [Y,N]?")>]
[<InlineData("Done? [A,B]?")>]
[<InlineData("[Y,N]?")>]
[<InlineData("Press any key to continue . . . ")>]
[<InlineData("Press any key to continue.")>]
let ``isSingleKeySubmit recognises choice + pause patterns`` (text: string) =
    Assert.True(ShellInteraction.isSingleKeySubmit text)

[<Theory>]
[<InlineData("Enter your name:")>]
[<InlineData("Type a number:")>]
[<InlineData("")>]
[<InlineData("[just brackets]")>]
[<InlineData("not even close")>]
let ``isSingleKeySubmit rejects regular sub-prompts`` (text: string) =
    Assert.False(ShellInteraction.isSingleKeySubmit text)

// ---------------------------------------------------------------------
// applyTransition — state mutation + Executing-window bookkeeping
// ---------------------------------------------------------------------

[<Fact>]
let ``applyTransition Composing -> Executing clears the executing-window state`` () =
    let state = freshState ()
    // Pre-set fake "executing-window" state to something non-empty so we
    // can verify Reset on Executing entry.
    state.HadAnyBytesThisExecuting <- true
    state.LastByteWasLf <- true
    state.SubPromptAccumulator.Append("stale") |> ignore
    let outcome =
        ShellInteraction.applyTransition
            state (ShellInteraction.EnterPressed "echo hi") (after 100)
    Assert.True(outcome.IsSome)
    match state.Current with
    | ShellInteraction.Executing _ -> ()
    | other -> Assert.Fail(sprintf "Expected Executing; got %A" other)
    Assert.False(state.HadAnyBytesThisExecuting)
    Assert.False(state.LastByteWasLf)
    Assert.Equal(0, state.SubPromptAccumulator.Length)

[<Fact>]
let ``applyTransition no-op returns None and leaves state unchanged`` () =
    let state = freshState ()
    // Composing receives SubPromptIdle (defensive no-op).
    let priorStateValue = state.Current
    let outcome =
        ShellInteraction.applyTransition
            state (ShellInteraction.SubPromptIdle "stale") (after 100)
    Assert.True(outcome.IsNone)
    Assert.Equal(priorStateValue, state.Current)

// ---------------------------------------------------------------------
// observeByte + sub-prompt detector
// ---------------------------------------------------------------------

[<Fact>]
let ``observeByte updates LastByteAt and LastByteWasLf`` () =
    let state = freshState ()
    // Move into Executing first so the Executing branch fires.
    ShellInteraction.applyTransition
        state (ShellInteraction.EnterPressed "echo hi") (after 100)
    |> ignore
    ShellInteraction.observeByte state (after 200) 0x65uy  // 'e'
    Assert.Equal(after 200, state.LastByteAt)
    Assert.False(state.LastByteWasLf)
    Assert.True(state.HadAnyBytesThisExecuting)
    ShellInteraction.observeByte state (after 250) 0x0Auy  // LF
    Assert.True(state.LastByteWasLf)

[<Fact>]
let ``trySubPromptDetect fires when idle threshold elapsed and last byte not LF`` () =
    let state = freshState ()
    state.IdleThresholdMs <- 350
    ShellInteraction.applyTransition
        state (ShellInteraction.EnterPressed "set /p var=X:") (after 0)
    |> ignore
    // Feed "X:" — last byte ":" (not LF).
    ShellInteraction.observeByte state (after 100) 0x58uy  // 'X'
    ShellInteraction.observeByte state (after 110) 0x3Auy  // ':'
    // Just under threshold — no detect.
    let early = ShellInteraction.trySubPromptDetect state (after 459)
    Assert.Equal(None, early)
    // Past threshold — detect fires.
    let late = ShellInteraction.trySubPromptDetect state (after 461)
    match late with
    | Some (ShellInteraction.SubPromptIdle text) ->
        Assert.Equal("X:", text)
    | other -> Assert.Fail(sprintf "Expected SubPromptIdle; got %A" other)

[<Fact>]
let ``trySubPromptDetect does NOT fire when last byte is LF (streaming pause)`` () =
    let state = freshState ()
    state.IdleThresholdMs <- 350
    ShellInteraction.applyTransition
        state (ShellInteraction.EnterPressed "ping localhost") (after 0)
    |> ignore
    // "Step 1\n" — last byte IS LF.
    "Step 1"
    |> Seq.iteri (fun i c ->
        ShellInteraction.observeByte state (after (100 + i * 5)) (byte c))
    ShellInteraction.observeByte state (after 200) 0x0Auy
    // Past threshold — but LF blocks detection.
    let result = ShellInteraction.trySubPromptDetect state (after 1000)
    Assert.Equal(None, result)

[<Fact>]
let ``trySubPromptDetect does NOT fire while Composing`` () =
    let state = freshState ()
    state.IdleThresholdMs <- 350
    // Stay in Composing; pretend we somehow accumulated bytes
    // (defensive).
    state.HadAnyBytesThisExecuting <- true
    state.LastByteWasLf <- false
    state.LastByteAt <- after 100
    let result = ShellInteraction.trySubPromptDetect state (after 1000)
    Assert.Equal(None, result)

[<Fact>]
let ``trySubPromptDetect does NOT fire when no bytes received this Executing window`` () =
    let state = freshState ()
    state.IdleThresholdMs <- 350
    ShellInteraction.applyTransition
        state (ShellInteraction.EnterPressed "fast cmd") (after 0)
    |> ignore
    // No observeByte calls. Past threshold but no output to
    // sub-prompt-announce.
    let result = ShellInteraction.trySubPromptDetect state (after 1000)
    Assert.Equal(None, result)

// ---------------------------------------------------------------------
// Reset
// ---------------------------------------------------------------------

[<Fact>]
let ``Reset returns state to fresh Composing`` () =
    let state = freshState ()
    ShellInteraction.applyTransition
        state (ShellInteraction.EnterPressed "echo hi") (after 100)
    |> ignore
    ShellInteraction.observeByte state (after 200) 0x65uy
    state.Reset()
    match state.Current with
    | ShellInteraction.Composing data ->
        Assert.Equal(ValueNone, data.PromptText)
        Assert.False(data.SinglekeySubmit)
    | other -> Assert.Fail(sprintf "Expected Composing; got %A" other)
    Assert.False(state.HadAnyBytesThisExecuting)
    Assert.Equal(0, state.SubPromptAccumulator.Length)

// ---------------------------------------------------------------------
// describeState / describeTrigger smoke tests (logging helpers)
// ---------------------------------------------------------------------

[<Fact>]
let ``describeState produces non-empty single-line text`` () =
    let composing =
        ShellInteraction.Composing
            { EnteredAt = t0
              PromptText = ValueSome "C:\\>"
              SinglekeySubmit = false }
    let executing =
        ShellInteraction.Executing
            { EnteredAt = t0
              SubmittedCommand = "echo hi"
              OutputLastByteIsLf = false
              OutputLastByteAt = t0 }
    let cText = ShellInteraction.describeState composing
    let eText = ShellInteraction.describeState executing
    Assert.Contains("Composing", cText)
    Assert.Contains("Executing", eText)
    Assert.DoesNotContain("\n", cText)
    Assert.DoesNotContain("\n", eText)

[<Fact>]
let ``describeTrigger produces non-empty single-line text for each variant`` () =
    let triggers =
        [ ShellInteraction.EnterPressed "cmd"
          ShellInteraction.PromptDetected "C:\\>"
          ShellInteraction.SubPromptIdle "Enter:"
          ShellInteraction.AltScreenEntered
          ShellInteraction.AltScreenExited ]
    for t in triggers do
        let s = ShellInteraction.describeTrigger t
        Assert.False(String.IsNullOrEmpty s)
        Assert.DoesNotContain("\n", s)

// ---------------------------------------------------------------------
// UserInputBuffer (Cycle 48 PR-D)
// ---------------------------------------------------------------------

[<Fact>]
let ``UserInputBuffer starts empty`` () =
    let b = ShellInteraction.UserInputBuffer()
    Assert.Equal(0, b.Count)
    Assert.Equal(0, b.CursorIndex)
    Assert.Equal("", b.Snapshot())

[<Fact>]
let ``AppendChar inserts at cursor and advances`` () =
    let b = ShellInteraction.UserInputBuffer()
    b.AppendChar('e')
    b.AppendChar('c')
    b.AppendChar('h')
    b.AppendChar('o')
    Assert.Equal(4, b.Count)
    Assert.Equal(4, b.CursorIndex)
    Assert.Equal("echo", b.Snapshot())

[<Fact>]
let ``Backspace removes char before cursor`` () =
    let b = ShellInteraction.UserInputBuffer()
    "echi" |> Seq.iter b.AppendChar
    b.Backspace()  // remove 'i'
    b.AppendChar('o')
    Assert.Equal("echo", b.Snapshot())
    Assert.Equal(4, b.CursorIndex)

[<Fact>]
let ``Backspace at start is no-op`` () =
    let b = ShellInteraction.UserInputBuffer()
    b.Backspace()
    Assert.Equal(0, b.Count)
    Assert.Equal(0, b.CursorIndex)

[<Fact>]
let ``MoveCursor and AppendChar at non-end inserts`` () =
    let b = ShellInteraction.UserInputBuffer()
    "echo".ToCharArray() |> Array.iter b.AppendChar
    b.MoveCursor(-2)  // cursor between 'h' and 'o'
    b.AppendChar('X')
    Assert.Equal("echXo", b.Snapshot())
    Assert.Equal(4, b.CursorIndex)

[<Fact>]
let ``Delete removes char at cursor`` () =
    let b = ShellInteraction.UserInputBuffer()
    "echo".ToCharArray() |> Array.iter b.AppendChar
    b.MoveCursor(-2)  // cursor between 'h' and 'o'
    b.Delete()  // removes 'o'
    Assert.Equal("ech", b.Snapshot())
    Assert.Equal(2, b.CursorIndex)

[<Fact>]
let ``MoveCursor clamps at boundaries`` () =
    let b = ShellInteraction.UserInputBuffer()
    "abc".ToCharArray() |> Array.iter b.AppendChar
    b.MoveCursor(-100)
    Assert.Equal(0, b.CursorIndex)
    b.MoveCursor(100)
    Assert.Equal(3, b.CursorIndex)

[<Fact>]
let ``JumpTo Home and End`` () =
    let b = ShellInteraction.UserInputBuffer()
    "abc".ToCharArray() |> Array.iter b.AppendChar
    b.JumpTo(0)
    Assert.Equal(0, b.CursorIndex)
    b.JumpTo(b.Count)
    Assert.Equal(3, b.CursorIndex)

[<Fact>]
let ``Capture returns content and clears buffer`` () =
    let b = ShellInteraction.UserInputBuffer()
    "echo hi".ToCharArray() |> Array.iter b.AppendChar
    let captured = b.Capture()
    Assert.Equal("echo hi", captured)
    Assert.Equal(0, b.Count)
    Assert.Equal(0, b.CursorIndex)
    Assert.Equal("", b.Snapshot())

[<Fact>]
let ``Reset clears the buffer`` () =
    let b = ShellInteraction.UserInputBuffer()
    "abc".ToCharArray() |> Array.iter b.AppendChar
    b.Reset()
    Assert.Equal(0, b.Count)
    Assert.Equal(0, b.CursorIndex)

[<Fact>]
let ``State.UserInputBuffer is shared instance`` () =
    let s = ShellInteraction.State()
    s.UserInputBuffer.AppendChar('x')
    Assert.Equal(1, s.UserInputBuffer.Count)
    Assert.Equal("x", s.UserInputBuffer.Snapshot())

[<Fact>]
let ``State.Reset clears the user input buffer`` () =
    let s = ShellInteraction.State()
    s.UserInputBuffer.AppendChar('x')
    s.UserInputBuffer.AppendChar('y')
    s.Reset()
    Assert.Equal(0, s.UserInputBuffer.Count)
