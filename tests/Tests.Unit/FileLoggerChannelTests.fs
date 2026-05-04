module PtySpeak.Tests.Unit.FileLoggerChannelTests

open System
open Microsoft.Extensions.Logging
open Xunit
open Terminal.Core

/// Stage 8c — pins the FileLogger channel's structured-log
/// behaviour. The channel consumes `OutputEvent` +
/// `RenderInstruction` and writes a structured log line via the
/// supplied `ILogger` so the rolling FileLogger sink captures
/// every event the Stream profile emits.
///
/// The channel implementation lives at
/// `src/Terminal.Core/FileLoggerChannel.fs`. Production
/// composition (`src/Terminal.App/Program.fs`) passes
/// `Logger.get "Terminal.Core.FileLoggerChannel"`; these tests
/// pass a `RecordingLogger` fake that captures
/// `Log<TState>(...)` calls (which is what `LogInformation`
/// extension methods funnel through) and exposes the formatted
/// message string for assertions.
///
/// **Empty-payload contract — CONTRARY to NvdaChannel.**
/// NvdaChannel skips empty payloads; FileLoggerChannel logs
/// them. Mode barriers + future Background-suppressed events
/// must appear in the log even if NVDA didn't read them.

/// Recording ILogger fake: captures every `Log<TState>` call's
/// formatted message string. F# `interface ... with` syntax
/// implements the full ILogger contract (BeginScope returns a
/// no-op IDisposable to avoid the F# 9 nullness `FS3261` that
/// fires when assigning `null` to a non-null reference type;
/// IsEnabled returns true so every Log call lands).
type private NoopScope() =
    interface System.IDisposable with
        member _.Dispose() = ()

type private RecordingLogger() =
    let calls = ResizeArray<LogLevel * string>()
    member _.Calls = calls
    interface ILogger with
        // F# 9 + .NET 9 nullness signatures (per the `FS0193` and
        // `FS0017` errors during 8c CI):
        //
        //   - `BeginScope<TState>` has `where TState : notnull` in
        //     the C# interface; F# requires `when 'TState : not null`.
        //   - `Log<TState>` does NOT carry the constraint in F#'s
        //     reading of the metadata; the compiler reports the
        //     interface signature as `Log<'TState>` plain.
        //   - The `exception` parameter is `Exception?` (nullable) in
        //     the C# signature; F# 9 requires `exn | null` here AND
        //     in the formatter `Func<'TState, exn | null, string>`.
        member _.BeginScope<'TState when 'TState : not null>(_state: 'TState) : System.IDisposable =
            (new NoopScope()) :> System.IDisposable
        member _.IsEnabled(_: LogLevel) : bool = true
        member _.Log<'TState>
                (level: LogLevel,
                 _eventId: EventId,
                 state: 'TState,
                 ex: exn | null,
                 formatter: System.Func<'TState, exn | null, string>) : unit =
            let message = formatter.Invoke(state, ex)
            calls.Add((level, message))

let private buildEvent
        (semantic: SemanticCategory)
        (priority: Priority)
        (producer: string)
        (shell: string option)
        (payload: string)
        : OutputEvent
        =
    let baseEvent = OutputEvent.create semantic priority producer payload
    { baseEvent with Source = { baseEvent.Source with Shell = shell } }

// ---- Channel identity ------------------------------------------

[<Fact>]
let ``FileLoggerChannel.id is filelogger`` () =
    Assert.Equal("filelogger", FileLoggerChannel.id)

[<Fact>]
let ``create returns a Channel whose Id matches the module-level id`` () =
    let channel = FileLoggerChannel.create (RecordingLogger() :> ILogger)
    Assert.Equal(FileLoggerChannel.id, channel.Id)

// ---- RenderInstruction handling --------------------------------

[<Fact>]
let ``RenderText payload reaches the logger as the Payload field`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "ls"
    channel.Send event (RenderText event.Payload)
    Assert.Equal(1, logger.Calls.Count)
    let _, message = logger.Calls.[0]
    Assert.Contains("Payload=ls", message)
    Assert.Contains("PayloadLen=2", message)

[<Fact>]
let ``RenderText2 picks the Precise register for the logger`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "approx"
    channel.Send event (RenderText2 ("approx-form", "precise-form"))
    let _, message = logger.Calls.[0]
    Assert.Contains("Payload=precise-form", message)

[<Fact>]
let ``RenderEarcon logs an earcon-id placeholder`` () =
    // Earcons go to the 8d Earcon channel for actual playback,
    // but FileLogger captures the emission event so post-hoc
    // diagnosis sees the earcon was triggered.
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.BellRang Priority.Assertive "test" None ""
    channel.Send event (RenderEarcon "bell-ping")
    let _, message = logger.Calls.[0]
    Assert.Contains("Payload=[earcon=bell-ping]", message)

[<Fact>]
let ``RenderRaw logs a raw-payload placeholder`` () =
    // Raw payloads are channel-specific opaque data (e.g. UIA
    // listbox metadata from the future Selection profile);
    // FileLogger logs a placeholder so the event's existence
    // is captured.
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.SelectionShown Priority.Polite "test" None ""
    channel.Send event (RenderRaw ("opaque" :> obj))
    let _, message = logger.Calls.[0]
    Assert.Contains("Payload=[raw payload]", message)

// ---- Empty-payload (mode barrier + the spec D.2 contract) ------

[<Fact>]
let ``empty-payload events are still logged (mode barriers)`` () =
    // Stage 8c capture-everything contract. NvdaChannel skips
    // empty; FileLogger does not.
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.ModeBarrier Priority.Polite "translator" None ""
    channel.Send event (RenderText "")
    Assert.Equal(1, logger.Calls.Count)
    let _, message = logger.Calls.[0]
    Assert.Contains("PayloadLen=0", message)
    Assert.Contains("Semantic=ModeBarrier", message)

[<Fact>]
let ``ParserError events log the wrapped error message`` () =
    // Anchor for the future spec D.2 ParserError → Background
    // suppression: even if NvdaChannel stops reading parser
    // errors, FileLogger continues to record them so
    // Ctrl+Shift+; diagnosis works.
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event =
        buildEvent
            SemanticCategory.ParserError
            Priority.Background
            "translator"
            None
            "Terminal parser error: decoder failed"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Semantic=ParserError", message)
    Assert.Contains("Priority=Background", message)
    Assert.Contains("Payload=Terminal parser error: decoder failed", message)

// ---- Structured-template field substitution --------------------

[<Fact>]
let ``OutputEvent.Semantic substitutes into the structured template`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Semantic=StreamChunk", message)

[<Fact>]
let ``OutputEvent.Priority substitutes into the structured template`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Assertive "test" None "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Priority=Assertive", message)

[<Fact>]
let ``OutputEvent.Verbosity substitutes into the structured template`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Verbosity=Precise", message)

[<Fact>]
let ``OutputEvent.Source.Producer substitutes into the structured template`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "translator" None "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Producer=translator", message)

[<Fact>]
let ``Source.Shell None renders as empty string`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    // Shell= followed by space (next field) means empty value.
    Assert.Contains("Shell= ", message)

[<Fact>]
let ``Source.Shell Some "cmd" renders as "cmd"`` () =
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" (Some "cmd") "x"
    channel.Send event (RenderText event.Payload)
    let _, message = logger.Calls.[0]
    Assert.Contains("Shell=cmd", message)

// ---- Log level -------------------------------------------------

[<Fact>]
let ``channel writes at LogLevel.Information`` () =
    // The level affects sink-side filtering (FileLoggerOptions.MinLevel).
    // Information is the production default; Debug events would
    // be filtered out unless the user toggled `Ctrl+Shift+G`. The
    // channel always emits at Information so every OutputEvent
    // lands at the default level.
    let logger = RecordingLogger()
    let channel = FileLoggerChannel.create (logger :> ILogger)
    let event = buildEvent SemanticCategory.StreamChunk Priority.Polite "test" None "x"
    channel.Send event (RenderText event.Payload)
    let level, _ = logger.Calls.[0]
    Assert.Equal(LogLevel.Information, level)
