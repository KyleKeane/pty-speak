module PtySpeak.Tests.Unit.VtParserTests

open System.Text
open Xunit
open FsCheck.Xunit
open Terminal.Core
open Terminal.Parser

// ---------------------------------------------------------------------
// Fixture-based tests (spec/tech-plan.md §2.4)
//
// These cover the canonical byte-string fixtures called out in the
// spec. Each test feeds a small fragment and asserts an exact
// VtEvent[] result.
// ---------------------------------------------------------------------

let private parse (bytes: byte[]) : VtEvent[] =
    let parser = Parser.create ()
    Parser.feedArray parser bytes

let private ascii (s: string) = Encoding.ASCII.GetBytes s

let private rune (c: char) = Rune(int c)

[<Fact>]
let ``Print: 'Hello\r\n' decodes to five Prints + CR + LF`` () =
    let events = parse (ascii "Hello\r\n")
    let expected =
        [| Print(rune 'H')
           Print(rune 'e')
           Print(rune 'l')
           Print(rune 'l')
           Print(rune 'o')
           Execute 0x0Duy
           Execute 0x0Auy |]
    Assert.Equal<VtEvent[]>(expected, events)

[<Fact>]
let ``CSI: '\x1b[31mRed\x1b[0m' produces SGR(31), three Prints, SGR(0)`` () =
    let events = parse (ascii "[31mRed[0m")
    Assert.Equal(5, events.Length)
    match events.[0] with
    | CsiDispatch(parms, intermediates, finalByte, priv) ->
        Assert.Equal<int[]>([| 31 |], parms)
        Assert.Equal<byte[]>([||], intermediates)
        Assert.Equal('m', finalByte)
        Assert.Equal<char option>(None, priv)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)
    Assert.Equal(Print(rune 'R'), events.[1])
    Assert.Equal(Print(rune 'e'), events.[2])
    Assert.Equal(Print(rune 'd'), events.[3])
    match events.[4] with
    | CsiDispatch(parms, _, finalByte, priv) ->
        Assert.Equal<int[]>([| 0 |], parms)
        Assert.Equal('m', finalByte)
        Assert.Equal<char option>(None, priv)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

[<Fact>]
let ``CSI: '\x1b[2J' is CsiDispatch parms=[2] final='J'`` () =
    let events = parse (ascii "[2J")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | CsiDispatch(parms, intermediates, finalByte, priv) ->
        Assert.Equal<int[]>([| 2 |], parms)
        Assert.Equal<byte[]>([||], intermediates)
        Assert.Equal('J', finalByte)
        Assert.Equal<char option>(None, priv)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

[<Fact>]
let ``CSI private mode: '\x1b[?1049h' has priv='?' parms=[1049] final='h'`` () =
    let events = parse (ascii "[?1049h")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | CsiDispatch(parms, _, finalByte, priv) ->
        Assert.Equal<int[]>([| 1049 |], parms)
        Assert.Equal('h', finalByte)
        Assert.Equal<char option>(Some '?', priv)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

[<Fact>]
let ``OSC: '\x1b]0;Title\x07' is bell-terminated OscDispatch with one param 'Title'`` () =
    let events = parse (ascii "]0;Title")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | OscDispatch(parms, bellTerminated) ->
        Assert.True(bellTerminated)
        Assert.Equal(2, parms.Length)
        Assert.Equal<byte[]>(ascii "0", parms.[0])
        Assert.Equal<byte[]>(ascii "Title", parms.[1])
    | other -> Assert.Fail(sprintf "Expected OscDispatch, got %A" other)

[<Fact>]
let ``ESC dispatch: '\x1b=' (DECKPAM) is bare EscDispatch final='='`` () =
    let events = parse (ascii "=")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | EscDispatch(intermediates, finalByte) ->
        Assert.Equal<byte[]>([||], intermediates)
        Assert.Equal('=', finalByte)
    | other -> Assert.Fail(sprintf "Expected EscDispatch, got %A" other)

[<Fact>]
let ``CSI multi-param: '\x1b[1;2;3m' has parms=[1;2;3]`` () =
    let events = parse (ascii "[1;2;3m")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | CsiDispatch(parms, _, finalByte, _) ->
        Assert.Equal<int[]>([| 1; 2; 3 |], parms)
        Assert.Equal('m', finalByte)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

[<Fact>]
let ``CSI default param: '\x1b[H' has parms=[0]`` () =
    // CSI with no digits before the final byte — defaults to a single
    // 0 parameter per Williams.
    let events = parse (ascii "[H")
    Assert.Equal(1, events.Length)
    match events.[0] with
    | CsiDispatch(parms, _, finalByte, _) ->
        Assert.Equal<int[]>([| 0 |], parms)
        Assert.Equal('H', finalByte)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

[<Fact>]
let ``CAN cancels in-flight CSI`` () =
    // CAN (0x18) inside a CSI sequence should return to Ground and
    // be emitted as Execute, with no CsiDispatch.
    let events = parse [| 0x1Buy; byte '['; byte '3'; 0x18uy; byte 'X' |]
    Assert.Contains(Execute 0x18uy, events)
    let hasCsi = events |> Array.exists (fun e -> match e with CsiDispatch _ -> true | _ -> false)
    Assert.False(hasCsi, "CSI sequence should have been cancelled by CAN")

[<Fact>]
let ``UTF-8: multi-byte scalar emits a single Print`` () =
    // U+00E9 'é' in UTF-8 is 0xC3 0xA9 — should emit one Print, not
    // two stray bytes.
    let events = parse [| 0xC3uy; 0xA9uy |]
    Assert.Equal(1, events.Length)
    match events.[0] with
    | Print r -> Assert.Equal(0x00E9, r.Value)
    | other -> Assert.Fail(sprintf "Expected Print, got %A" other)

// ---------------------------------------------------------------------
// FsCheck property tests
//
// Spec §2.4: "for any sequence of random bytes the parser never throws
// and always returns to Ground after at most N bytes following an
// ST/BEL/CAN/SUB". Plus the chunk-equivalence invariant.
// ---------------------------------------------------------------------

[<Property>]
let ``parser never throws on arbitrary bytes`` (bytes: byte[]) =
    // If parse returns at all, the property holds. FsCheck will catch
    // exceptions automatically.
    let _ = parse bytes
    true

[<Property>]
let ``feeding bytes one at a time matches feeding the whole array``
        (bytes: byte[]) =
    let whole = parse bytes
    let pieceMeal =
        let parser = Parser.create ()
        let events = ResizeArray<VtEvent>()
        for b in bytes do
            match Parser.feed parser b with
            | Some e -> events.Add(e)
            | None -> ()
        events.ToArray()
    whole = pieceMeal

[<Property>]
let ``CAN (0x18) anywhere returns parser to Ground`` (prefix: byte[]) =
    let parser = Parser.create ()
    let _ = Parser.feedArray parser prefix
    let _ = Parser.feedArray parser [| 0x18uy |]
    parser.State = Ground
