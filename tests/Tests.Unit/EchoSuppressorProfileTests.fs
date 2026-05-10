module PtySpeak.Tests.Unit.EchoSuppressorProfileTests

open System
open Xunit
open Terminal.Core

/// Cycle 38c — pins the EchoSuppressorProfile contract per
/// `fluffy-simon.md` Section 20.5.2.

let private bytes (s: string) : byte[] =
    System.Text.Encoding.UTF8.GetBytes(s)

let private streamChunk (payload: string) : OutputEvent =
    OutputEvent.create
        SemanticCategory.StreamChunk
        Priority.Polite
        "echo-suppressor-tests"
        payload

let private renderText (decision: ChannelDecision) : string =
    match decision.Render with
    | RenderText t -> t
    | other ->
        Assert.Fail(sprintf "expected RenderText, got %A" other)
        ""

// =====================================================================
// Full-echo suppression
// =====================================================================

[<Fact>]
let ``Apply drops NVDA decision when entire StreamChunk payload is echo`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    EchoCorrelator.recordWrite correlator DateTime.UtcNow (bytes "hi\r")
    let profile = EchoSuppressorProfile.create correlator
    let event = streamChunk "hi\r\n"
    let pairs = profile.Apply event
    Assert.Equal(1, pairs.Length)
    let _, decisions = pairs.[0]
    // Only FileLogger decision (no NVDA) because the whole
    // payload was echo.
    Assert.Equal(1, decisions.Length)
    Assert.Equal(FileLoggerChannel.id, decisions.[0].Channel)
    let text = renderText decisions.[0]
    Assert.Contains("suppressed echo", text)
    Assert.Contains("hi", text)

// =====================================================================
// Partial-echo strip
// =====================================================================

[<Fact>]
let ``Apply emits stripped NVDA payload when partial echo`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    EchoCorrelator.recordWrite correlator DateTime.UtcNow (bytes "echo\r")
    let profile = EchoSuppressorProfile.create correlator
    let event = streamChunk "echo\r\nhello"
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    // First decision: NVDA with stripped payload.
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal("hello", renderText decisions.[0])
    // Second decision: FileLogger with full original payload.
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal("echo\r\nhello", renderText decisions.[1])

// =====================================================================
// No-match pass-through
// =====================================================================

[<Fact>]
let ``Apply behaves as PassThrough when no echo match (empty buffer)`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    let profile = EchoSuppressorProfile.create correlator
    let event = streamChunk "hello"
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal("hello", renderText decisions.[0])
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)
    Assert.Equal("hello", renderText decisions.[1])

[<Fact>]
let ``Apply behaves as PassThrough when payload diverges from buffer`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    EchoCorrelator.recordWrite correlator DateTime.UtcNow (bytes "abc")
    let profile = EchoSuppressorProfile.create correlator
    let event = streamChunk "hello"
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    // No prefix match → emit NVDA + FileLogger with the original
    // payload.
    Assert.Equal("hello", renderText decisions.[0])
    Assert.Equal("hello", renderText decisions.[1])

// =====================================================================
// Non-StreamChunk events untouched
// =====================================================================

[<Fact>]
let ``Apply behaves as PassThrough for non-StreamChunk events`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    EchoCorrelator.recordWrite correlator DateTime.UtcNow (bytes "anything")
    let profile = EchoSuppressorProfile.create correlator
    let event =
        OutputEvent.create
            SemanticCategory.BellRang
            Priority.Polite
            "test"
            ""
    let pairs = profile.Apply event
    let _, decisions = pairs.[0]
    Assert.Equal(2, decisions.Length)
    Assert.Equal(NvdaChannel.id, decisions.[0].Channel)
    Assert.Equal(FileLoggerChannel.id, decisions.[1].Channel)

// =====================================================================
// Profile identity
// =====================================================================

[<Fact>]
let ``EchoSuppressorProfile.id is "echo-suppressor"`` () =
    Assert.Equal("echo-suppressor", EchoSuppressorProfile.id)

[<Fact>]
let ``create returns a Profile whose Id matches the module id`` () =
    let correlator =
        EchoCorrelator.create EchoCorrelator.defaultParameters
    let profile = EchoSuppressorProfile.create correlator
    Assert.Equal(EchoSuppressorProfile.id, profile.Id)
