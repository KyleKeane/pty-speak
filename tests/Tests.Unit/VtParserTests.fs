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
let ``SUB cancels in-flight CSI like CAN`` () =
    // SUB (0x1A) is the CAN counterpart per Williams; the StateMachine
    // treats them identically inside CSI/DCS/OSC. Mirror the CAN test
    // so a future change that special-cases one without the other
    // breaks loudly.
    let events = parse [| 0x1Buy; byte '['; byte '3'; 0x1Auy; byte 'X' |]
    Assert.Contains(Execute 0x1Auy, events)
    let hasCsi = events |> Array.exists (fun e -> match e with CsiDispatch _ -> true | _ -> false)
    Assert.False(hasCsi, "CSI sequence should have been cancelled by SUB")

[<Fact>]
let ``OSC: ST-terminated sequence emits OscDispatch with bellTerminated=false`` () =
    // ST is ESC \ (0x1B 0x5C). OscString on ESC emits OscDispatch with
    // bellTerminated=false and transitions to Escape; the trailing \
    // (0x5C) then dispatches as a bare EscDispatch with finalByte='\'.
    // Verify both events to lock the contract.
    let events = parse [|
        0x1Buy; byte ']'; byte '0'; byte ';'
        byte 'T'; byte 'i'; byte 't'; byte 'l'; byte 'e'
        0x1Buy; byte '\\'
    |]
    Assert.Equal(2, events.Length)
    match events.[0] with
    | OscDispatch(_, bellTerminated) ->
        Assert.False(bellTerminated, "ST-terminated OSC must report bellTerminated=false")
    | other -> Assert.Fail(sprintf "Expected OscDispatch, got %A" other)
    match events.[1] with
    | EscDispatch(intermediates, finalByte) ->
        Assert.Equal<byte[]>([||], intermediates)
        Assert.Equal('\\', finalByte)
    | other -> Assert.Fail(sprintf "Expected EscDispatch, got %A" other)

[<Fact>]
let ``CAN inside DCS passthrough emits DcsUnhook and returns to Ground`` () =
    // ESC P 1 $ q (DECRQSS-shape: one-param DCS with intermediate),
    // then payload bytes 'Z' 'Z' 'Z', then CAN. Two invariants
    // exercised at once:
    //   1. DcsParam → DcsIntermediate transition pushes the in-flight
    //      digit (was Issue #42; fixed in this PR by adding pushParam
    //      to that edge in StateMachine.fs). parms must be [|1|], not
    //      [||] as it was pre-fix.
    //   2. DcsPassthrough on CAN emits DcsUnhook (NOT Execute —
    //      asymmetry with CSI's CAN-during-CSI is deliberate; see
    //      StateMachine.fs).
    let parser = Parser.create ()
    let events = Parser.feedArray parser [|
        0x1Buy; byte 'P'; byte '1'; byte '$'; byte 'q'
        byte 'Z'; byte 'Z'; byte 'Z'
        0x18uy
    |]
    Assert.Equal(5, events.Length)
    match events.[0] with
    | DcsHook(parms, intermediates, finalByte) ->
        Assert.Equal<int[]>([| 1 |], parms)
        Assert.Equal<byte[]>([| 0x24uy |], intermediates)
        Assert.Equal('q', finalByte)
    | other -> Assert.Fail(sprintf "Expected DcsHook, got %A" other)
    Assert.Equal(DcsPut 0x5Auy, events.[1])
    Assert.Equal(DcsPut 0x5Auy, events.[2])
    Assert.Equal(DcsPut 0x5Auy, events.[3])
    Assert.Equal(DcsUnhook, events.[4])
    Assert.Equal(Ground, parser.State)

[<Fact>]
let ``CSI with param + intermediate preserves the in-flight digit`` () =
    // CSI 1 $ q is the DECRQSS counterpart for SGR queries. The
    // parallel transition CsiParam → CsiIntermediate had the same
    // missing-pushParam bug as DcsParam → DcsIntermediate (Issue #42).
    // Pinning both with this test means a regression in either edge
    // breaks loudly.
    let events = parse [|
        0x1Buy; byte '['; byte '1'; byte '$'; byte 'q'
    |]
    Assert.Equal(1, events.Length)
    match events.[0] with
    | CsiDispatch(parms, intermediates, finalByte, priv) ->
        Assert.Equal<int[]>([| 1 |], parms)
        Assert.Equal<byte[]>([| 0x24uy |], intermediates)
        Assert.Equal('q', finalByte)
        Assert.Equal<char option>(None, priv)
    | other -> Assert.Fail(sprintf "Expected CsiDispatch, got %A" other)

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

[<Property>]
let ``valid Unicode scalars round-trip through UTF-8 as a single Print``
        (raw: int) =
    // Wrap raw into [0, 0x110000) and skip:
    //   * surrogate range [0xD800, 0xDFFF] — invalid scalars,
    //   * C0 controls [0x00, 0x1F] — emit Execute, not Print,
    //   * DEL (0x7F) — silently dropped per Williams.
    // Without the wrap, most random ints would be out of range and
    // the property would be vacuously true on almost every run.
    // FsCheck's int generator biases toward small values, so without
    // the C0 / DEL skip the property fails on raw=0 (0x00 → Execute).
    let candidate = ((raw % 0x110000) + 0x110000) % 0x110000
    if candidate >= 0xD800 && candidate <= 0xDFFF then true
    elif candidate < 0x20 then true
    elif candidate = 0x7F then true
    else
        let r = Rune(candidate)
        let bytes = Encoding.UTF8.GetBytes(r.ToString())
        let events = parse bytes
        events.Length = 1 &&
        (match events.[0] with
         | Print decoded -> decoded = r
         | _ -> false)

// ---------------------------------------------------------------------
// SR-1 (audit-cycle security hardening): bounds against malicious or
// adversarially-shaped input. Each test pins a contract enforced by
// changes in `Terminal.Parser/StateMachine.fs` and
// `Terminal.Core/Screen.fs`. See SECURITY.md row A-1 for the surface.
// ---------------------------------------------------------------------

[<Fact>]
let ``CSI param accumulator clamps int32 overflow at MAX_PARAM_VALUE`` () =
    // SR-1: `\x1b[999999999999999999m` previously overflowed int32 to a
    // negative value. Now it clamps at 65535 and an SGR dispatch
    // fires with a non-negative param. The exact value is
    // implementation detail (clamped or 0); the contract is
    // never-negative.
    let events = parse (ascii "\x1b[999999999999999999m")
    let dispatched =
        events
        |> Array.choose (function
            | CsiDispatch(parms, _, 'm', _) -> Some parms
            | _ -> None)
        |> Array.tryHead
    match dispatched with
    | None -> failwith "expected an SGR (CSI m) dispatch"
    | Some parms ->
        Assert.True(parms.Length >= 1)
        Assert.True(parms.[0] >= 0, sprintf "param[0]=%d, should not be negative" parms.[0])
        Assert.True(parms.[0] <= 65535, sprintf "param[0]=%d, should be clamped at 65535" parms.[0])

[<Fact>]
let ``DCS payload caps at MAX_DCS_RAW; DcsHook + DcsUnhook still fire`` () =
    // SR-1: 8 KiB of DCS payload should produce DcsHook then ≤ 4096
    // DcsPut events then DcsUnhook on ESC \. The cap protects
    // against ANSI-bomb DoS while preserving DCS framing.
    let header = ascii "\x1bP1$q"  // DCS hook (DECRQSS shape)
    let payload = Array.create 8192 (byte 'x')
    let terminator = ascii "\x1b\\"
    let bytes = Array.concat [ header; payload; terminator ]
    let events = parse bytes
    let hookCount = events |> Array.filter (function DcsHook _ -> true | _ -> false) |> Array.length
    let putCount = events |> Array.filter (function DcsPut _ -> true | _ -> false) |> Array.length
    let unhookCount = events |> Array.filter (function DcsUnhook -> true | _ -> false) |> Array.length
    Assert.Equal(1, hookCount)
    Assert.Equal(1, unhookCount)
    Assert.True(putCount <= 4096, sprintf "DcsPut count = %d, should be capped at 4096" putCount)
    Assert.True(putCount > 0, "expected at least some DcsPut emit")

[<Fact>]
let ``OSC overflow transitions to OscIgnore and dispatches empty payload`` () =
    // SR-1: 8 KiB of OSC payload should produce exactly one
    // OscDispatch on BEL — empty payload because the parser
    // transitions to OscIgnore once MAX_OSC_RAW (1024 bytes) is
    // hit. Parser ends in Ground (verified by feeding a Print
    // after the BEL and seeing it emit).
    let header = ascii "\x1b]0;"  // OSC start, set window title
    let payload = Array.create 8192 (byte 'x')
    let terminator = [| 0x07uy |]  // BEL
    let bytes = Array.concat [ header; payload; terminator ]
    let events = parse bytes
    let oscDispatches =
        events
        |> Array.choose (function OscDispatch(parms, bel) -> Some(parms, bel) | _ -> None)
    Assert.Equal(1, oscDispatches.Length)
    let parms, bellTerminated = oscDispatches.[0]
    Assert.True(bellTerminated)
    // Empty payload: after overflow we don't carry forward the
    // partially-collected bytes. (Pinned: NEVER carry the
    // truncated payload — that's the desync risk SR-1 closed.)
    let nonEmptyParam = parms |> Array.exists (fun p -> p.Length > 0)
    Assert.False(nonEmptyParam, "OSC overflow should dispatch with empty params")

[<Fact>]
let ``OSC overflow leaves parser in Ground after terminator`` () =
    // After overflow + BEL terminator, feeding a Print byte should
    // produce a Print event — confirms parser is back in Ground.
    let header = ascii "\x1b]0;"
    let payload = Array.create 4096 (byte 'x')
    let terminator = [| 0x07uy |]  // BEL
    let after = ascii "Z"
    let bytes = Array.concat [ header; payload; terminator; after ]
    let events = parse bytes
    let lastPrint = events |> Array.tryFindBack (function Print _ -> true | _ -> false)
    Assert.Equal(Some (Print(rune 'Z')), lastPrint)
