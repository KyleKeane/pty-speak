module PtySpeak.Tests.Unit.IOutputSinkTests

open Xunit
open Terminal.Core

/// Cycle 31a — pins the IOutputSink interface contract:
///
/// 1. The `Channel` record (`OutputEventTypes.fs:339-360`)
///    trivially satisfies `IOutputSink` via the interface
///    implementation added in Cycle 31a.
/// 2. All three shipped channel factories (`NvdaChannel.create`,
///    `EarconChannel.create`, `FileLoggerChannel.create`) return
///    `Channel` records that coerce cleanly to `IOutputSink` and
///    round-trip representative `RenderInstruction`s.
/// 3. The dispatcher's call-site upcast in
///    `OutputDispatcher.routePair:106-114` is functionally
///    identical to the pre-Cycle-31a direct `channel.Send` call.
///
/// These tests are an architectural pin: future cycles that
/// introduce non-`Channel` `IOutputSink` implementations
/// (linear-text producers in Cycle 34, future cross-platform
/// channels) MUST keep these passing — the Channel-record path
/// is the canonical "satisfies the contract" reference shape.

let private buildEvent (semantic: SemanticCategory) (payload: string) : OutputEvent =
    OutputEvent.create semantic Priority.Polite "test" payload

// ---- NvdaChannel coerces to IOutputSink ------------------------

[<Fact>]
let ``NvdaChannel record coerces to IOutputSink and round-trips RenderText`` () =
    let calls = ResizeArray<string * string>()
    let recorder (msg, activityId) = calls.Add((msg, activityId))
    let channel = NvdaChannel.create recorder
    let sink = channel :> IOutputSink
    Assert.Equal(NvdaChannel.id, sink.Id)
    let event = buildEvent SemanticCategory.StreamChunk "hello"
    sink.Send event (RenderText "hello")
    Assert.Equal(1, calls.Count)
    let (msg, _activityId) = calls.[0]
    Assert.Equal("hello", msg)

[<Fact>]
let ``NvdaChannel via IOutputSink preserves empty-payload skip`` () =
    let calls = ResizeArray<string * string>()
    let recorder (msg, activityId) = calls.Add((msg, activityId))
    let channel = NvdaChannel.create recorder
    let sink = channel :> IOutputSink
    let event = buildEvent SemanticCategory.StreamChunk ""
    sink.Send event (RenderText "")
    Assert.Empty(calls)

// ---- EarconChannel coerces to IOutputSink ----------------------

[<Fact>]
let ``EarconChannel record coerces to IOutputSink and round-trips RenderEarcon`` () =
    EarconChannel.clearForTests ()
    let calls = ResizeArray<string>()
    let recorder = calls.Add
    let channel = EarconChannel.create recorder
    let sink = channel :> IOutputSink
    Assert.Equal(EarconChannel.id, sink.Id)
    let event = buildEvent SemanticCategory.ErrorLine "boom"
    sink.Send event (RenderEarcon "error-tone")
    Assert.Equal(1, calls.Count)
    Assert.Equal("error-tone", calls.[0])

[<Fact>]
let ``EarconChannel via IOutputSink preserves non-Earcon skip`` () =
    EarconChannel.clearForTests ()
    let calls = ResizeArray<string>()
    let recorder = calls.Add
    let channel = EarconChannel.create recorder
    let sink = channel :> IOutputSink
    let event = buildEvent SemanticCategory.StreamChunk "text"
    sink.Send event (RenderText "text")
    Assert.Empty(calls)

// ---- FileLoggerChannel coerces to IOutputSink ------------------

/// Tiny ILogger fake — captures `LogInformation` calls without
/// pulling in the full RecordingLogger from FileLoggerChannelTests.
type private CapturingLogger() =
    let entries = ResizeArray<string>()
    member _.Entries = entries
    interface Microsoft.Extensions.Logging.ILogger with
        member _.BeginScope<'TState>(_state: 'TState) : System.IDisposable =
            { new System.IDisposable with member _.Dispose() = () }
        member _.IsEnabled(_logLevel) = true
        member _.Log<'TState>(
                _logLevel,
                _eventId,
                state: 'TState,
                _exn: exn,
                formatter: System.Func<'TState, exn, string>) =
            entries.Add(formatter.Invoke(state, _exn))

[<Fact>]
let ``FileLoggerChannel record coerces to IOutputSink and emits a structured log entry`` () =
    let logger = CapturingLogger()
    let channel = FileLoggerChannel.create (logger :> Microsoft.Extensions.Logging.ILogger)
    let sink = channel :> IOutputSink
    Assert.Equal(FileLoggerChannel.id, sink.Id)
    let event = buildEvent SemanticCategory.StreamChunk "diag-payload"
    sink.Send event (RenderText "diag-payload")
    Assert.Equal(1, logger.Entries.Count)

// ---- Channel record's IOutputSink impl is reflexive ------------

[<Fact>]
let ``Channel record IOutputSink impl forwards Id and Send to the record's own fields`` () =
    let calls = ResizeArray<OutputEvent * RenderInstruction>()
    let channel : Channel =
        { Id = "test-sink"
          Send = fun event render -> calls.Add((event, render)) }
    let sink = channel :> IOutputSink
    Assert.Equal("test-sink", sink.Id)
    let event = buildEvent SemanticCategory.StreamChunk "x"
    let render = RenderText "x"
    sink.Send event render
    Assert.Equal(1, calls.Count)
    let (capturedEvent, capturedRender) = calls.[0]
    Assert.Equal("x", capturedEvent.Payload)
    match capturedRender with
    | RenderText s -> Assert.Equal("x", s)
    | other -> Assert.Fail(sprintf "expected RenderText \"x\"; got %A" other)
