namespace Terminal.Parser

open System
open Terminal.Core

/// Public parser facade. Wraps `StateMachine` with a slightly more
/// ergonomic interface for downstream consumers and tests.
///
/// Usage:
///
/// ```fsharp
/// let parser = Parser.create ()
/// let events = Parser.feedBytes parser (System.Text.Encoding.UTF8.GetBytes "Hello\r\n")
/// // events : VtEvent[] = [| Print 'H'; Print 'e'; ...; Execute 13uy; Execute 10uy |]
/// ```
type Parser internal (machine: StateMachine) =
    member _.State : VtState = machine.State

    /// Feed a single byte. Returns the event emitted by this byte, if
    /// any (most state-machine transitions emit nothing).
    member _.Feed(b: byte) : VtEvent option = machine.Feed(b)

module Parser =
    /// Create a fresh parser starting in the Ground state.
    let create () : Parser = Parser(StateMachine())

    /// Feed a single byte. Returns Some event if one was emitted, None
    /// otherwise.
    let feed (parser: Parser) (b: byte) : VtEvent option = parser.Feed(b)

    /// Feed a span of bytes and collect every emitted event in order.
    /// Allocation: a single ResizeArray<VtEvent> sized to the input
    /// chunk length; ToArray at the end. For tight inner loops in
    /// later stages we may add a callback-based variant.
    let feedBytes (parser: Parser) (bytes: ReadOnlySpan<byte>) : VtEvent[] =
        let events = ResizeArray<VtEvent>(bytes.Length)
        for i in 0 .. bytes.Length - 1 do
            match parser.Feed(bytes.[i]) with
            | Some e -> events.Add(e)
            | None -> ()
        events.ToArray()

    /// Convenience overload for byte arrays — equivalent to feeding
    /// the whole array as a span.
    let feedArray (parser: Parser) (bytes: byte[]) : VtEvent[] =
        feedBytes parser (ReadOnlySpan<byte>(bytes))
