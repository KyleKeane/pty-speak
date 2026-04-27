namespace Terminal.Parser

open System.Text
open Terminal.Core

/// Internal state of Paul Williams' DEC ANSI parser. State transitions
/// follow https://vt100.net/emu/dec_ansi_parser.html. We expose the
/// states publicly so tests can assert on transitions; consumers of
/// `Parser.advance` don't need to look at this directly.
type VtState =
    | Ground
    | Escape
    | EscapeIntermediate
    | CsiEntry
    | CsiParam
    | CsiIntermediate
    | CsiIgnore
    | DcsEntry
    | DcsParam
    | DcsIntermediate
    | DcsPassthrough
    | DcsIgnore
    | OscString
    | SosPmApcString

/// alacritty/vte caps copied verbatim. Anything beyond these limits is
/// silently dropped — the alternative (allocating without bound for an
/// adversarial input stream) is a memory-DoS surface.
module internal Limits =
    let MAX_INTERMEDIATES = 2
    let MAX_OSC_PARAMS = 16
    let MAX_OSC_RAW = 1024
    let MAX_PARAMS = 16

/// Stateful single-byte-at-a-time parser. Encapsulates Williams'
/// state machine plus a small UTF-8 decoder for `Print` events.
///
/// Emit semantics: `feed` returns 0 or 1 events per byte (most bytes
/// are state transitions only). Multi-byte UTF-8 sequences accumulate
/// silently until the final continuation byte, at which point a
/// single `Print of Rune` is emitted.
///
/// Robustness contract (verified by FsCheck property tests):
///   1. `feed` never throws on any byte sequence.
///   2. The parser returns to `Ground` after at most N bytes following
///      a CAN (0x18), SUB (0x1A), ST (`ESC \`), or BEL (0x07) — the
///      cancellation/terminator bytes.
///   3. Feeding the same byte stream in two arbitrary chunkings
///      produces identical event sequences.
type StateMachine() =
    let mutable state = Ground
    let parameters = ResizeArray<int>()
    let mutable currentParam = -1  // -1 = no digits seen yet (default)
    let intermediates = ResizeArray<byte>()
    let oscParams = ResizeArray<ResizeArray<byte>>()
    let mutable oscTotalLen = 0
    let mutable privateMarker: char option = None

    // Small UTF-8 decoder. We intentionally avoid System.Text.Encoding
    // because we need byte-at-a-time streaming with replacement on
    // truncation.
    let utf8Buffer = Array.zeroCreate<byte> 4
    let mutable utf8Have = 0
    let mutable utf8Need = 0

    let resetSequence () =
        parameters.Clear()
        currentParam <- -1
        intermediates.Clear()
        privateMarker <- None

    let resetOsc () =
        oscParams.Clear()
        oscParams.Add(ResizeArray<byte>())
        oscTotalLen <- 0

    let resetUtf8 () =
        utf8Have <- 0
        utf8Need <- 0

    let pushParam () =
        if parameters.Count < Limits.MAX_PARAMS then
            parameters.Add(if currentParam < 0 then 0 else currentParam)
        currentParam <- -1

    let collectIntermediate (b: byte) =
        if intermediates.Count < Limits.MAX_INTERMEDIATES then
            intermediates.Add(b)

    let oscAppend (b: byte) =
        if oscTotalLen < Limits.MAX_OSC_RAW && oscParams.Count > 0 then
            oscParams.[oscParams.Count - 1].Add(b)
            oscTotalLen <- oscTotalLen + 1

    let oscNextParam () =
        if oscParams.Count < Limits.MAX_OSC_PARAMS then
            oscParams.Add(ResizeArray<byte>())

    let snapshotParams () =
        parameters.ToArray()

    let snapshotIntermediates () =
        intermediates.ToArray()

    let snapshotOscParams () =
        let arr = Array.zeroCreate<byte[]> oscParams.Count
        for i in 0 .. oscParams.Count - 1 do
            arr.[i] <- oscParams.[i].ToArray()
        arr

    /// Print a single Unicode scalar value via the small UTF-8
    /// decoder. ASCII (0x00..0x7F) goes through the trivial path;
    /// multi-byte sequences accumulate in `utf8Buffer` and emit when
    /// complete. Malformed continuations emit U+FFFD and reset.
    let emitPrint (b: byte) : VtEvent option =
        if utf8Need = 0 then
            // First byte of a (possibly multi-byte) scalar.
            if b < 0x80uy then
                // ASCII fast path.
                Some(Print(Rune(int b)))
            elif b &&& 0xE0uy = 0xC0uy then
                utf8Buffer.[0] <- b
                utf8Have <- 1
                utf8Need <- 2
                None
            elif b &&& 0xF0uy = 0xE0uy then
                utf8Buffer.[0] <- b
                utf8Have <- 1
                utf8Need <- 3
                None
            elif b &&& 0xF8uy = 0xF0uy then
                utf8Buffer.[0] <- b
                utf8Have <- 1
                utf8Need <- 4
                None
            else
                // Stray continuation byte (0x80..0xBF) or invalid 5-6
                // byte UTF-8 lead. Emit replacement.
                Some(Print(Rune(0xFFFD)))
        else
            // Continuation byte expected.
            if b &&& 0xC0uy <> 0x80uy then
                // Not a valid continuation — emit replacement and
                // re-process the current byte as a fresh start.
                resetUtf8 ()
                let replacement = Print(Rune(0xFFFD))
                // Re-process b as new lead. Recursion is bounded by
                // utf8Need going to 0 above.
                match emitPrint b with
                | Some _ as also -> ignore also; Some replacement
                | None -> Some replacement
            else
                utf8Buffer.[utf8Have] <- b
                utf8Have <- utf8Have + 1
                if utf8Have = utf8Need then
                    let span = System.ReadOnlySpan<byte>(utf8Buffer, 0, utf8Have)
                    let success, rune, _ = Rune.DecodeFromUtf8(span)
                    resetUtf8 ()
                    if success = System.Buffers.OperationStatus.Done then
                        Some(Print rune)
                    else
                        Some(Print(Rune(0xFFFD)))
                else
                    None

    /// Push one byte through the state machine. Returns the event
    /// emitted (if any). Callers feed bytes one-at-a-time; for chunk
    /// processing see `Parser.feedBytes`.
    member _.Feed(b: byte) : VtEvent option =
        match state with
        | Ground ->
            if b <= 0x1Fuy then
                if b = 0x1Buy then
                    // ESC: enter Escape state, drop any UTF-8 partial.
                    resetUtf8 ()
                    state <- Escape
                    resetSequence ()
                    None
                else
                    resetUtf8 ()
                    Some(Execute b)
            elif b = 0x7Fuy then
                None  // DEL is silently dropped per Williams
            else
                emitPrint b
        | Escape ->
            if b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                state <- EscapeIntermediate
                None
            elif b >= 0x30uy && b <= 0x7Euy then
                let final = char b
                let intermediates = snapshotIntermediates ()
                state <-
                    match final with
                    | '[' -> resetSequence (); CsiEntry
                    | ']' -> resetOsc (); OscString
                    | 'P' -> resetSequence (); DcsEntry
                    | 'X' | '^' | '_' -> SosPmApcString
                    | _ ->
                        // Bare ESC dispatch (e.g. ESC =, ESC 7).
                        // We'll emit and return to Ground.
                        Ground
                if state = Ground then
                    Some(EscDispatch(intermediates, final))
                else
                    None
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            elif b = 0x1Buy then
                resetSequence ()
                None
            elif b <= 0x17uy || b = 0x19uy || (b >= 0x1Cuy && b <= 0x1Fuy) then
                Some(Execute b)
            else
                state <- Ground
                None
        | EscapeIntermediate ->
            if b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                None
            elif b >= 0x30uy && b <= 0x7Euy then
                let inter = snapshotIntermediates ()
                state <- Ground
                Some(EscDispatch(inter, char b))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                None
        | CsiEntry ->
            if b >= 0x30uy && b <= 0x39uy then
                currentParam <- int (b - 0x30uy)
                state <- CsiParam
                None
            elif b = 0x3Buy then
                pushParam ()
                state <- CsiParam
                None
            elif b >= 0x3Cuy && b <= 0x3Fuy then
                privateMarker <- Some(char b)
                state <- CsiParam
                None
            elif b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                state <- CsiIntermediate
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                pushParam ()
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                let pm = privateMarker
                state <- Ground
                Some(CsiDispatch(p, i, char b, pm))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                None
        | CsiParam ->
            if b >= 0x30uy && b <= 0x39uy then
                if currentParam < 0 then currentParam <- 0
                currentParam <- currentParam * 10 + int (b - 0x30uy)
                None
            elif b = 0x3Buy then
                pushParam ()
                None
            elif b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                state <- CsiIntermediate
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                pushParam ()
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                let pm = privateMarker
                state <- Ground
                Some(CsiDispatch(p, i, char b, pm))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            elif b = 0x3Auy || (b >= 0x3Cuy && b <= 0x3Fuy) then
                state <- CsiIgnore
                None
            else
                None
        | CsiIntermediate ->
            if b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                let pm = privateMarker
                state <- Ground
                Some(CsiDispatch(p, i, char b, pm))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                None
        | CsiIgnore ->
            if b >= 0x40uy && b <= 0x7Euy then
                state <- Ground
                None
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                None
        | OscString ->
            if b = 0x07uy then
                // BEL terminator
                let parms = snapshotOscParams ()
                state <- Ground
                Some(OscDispatch(parms, true))
            elif b = 0x1Buy then
                // Start of ST (ESC \). We could go to a sub-state, but
                // the simpler approach: on ESC, re-enter Escape and
                // let it route us back to Ground via the next byte.
                let parms = snapshotOscParams ()
                state <- Escape
                resetSequence ()
                Some(OscDispatch(parms, false))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            elif b = 0x3Buy then
                oscNextParam ()
                None
            else
                oscAppend b
                None
        | DcsEntry ->
            if b >= 0x30uy && b <= 0x39uy then
                currentParam <- int (b - 0x30uy)
                state <- DcsParam
                None
            elif b = 0x3Buy then
                pushParam ()
                state <- DcsParam
                None
            elif b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                state <- DcsIntermediate
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                pushParam ()
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                state <- DcsPassthrough
                Some(DcsHook(p, i, char b))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                state <- DcsIgnore
                None
        | DcsParam ->
            if b >= 0x30uy && b <= 0x39uy then
                if currentParam < 0 then currentParam <- 0
                currentParam <- currentParam * 10 + int (b - 0x30uy)
                None
            elif b = 0x3Buy then
                pushParam ()
                None
            elif b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                state <- DcsIntermediate
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                pushParam ()
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                state <- DcsPassthrough
                Some(DcsHook(p, i, char b))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                state <- DcsIgnore
                None
        | DcsIntermediate ->
            if b >= 0x20uy && b <= 0x2Fuy then
                collectIntermediate b
                None
            elif b >= 0x40uy && b <= 0x7Euy then
                let p = snapshotParams ()
                let i = snapshotIntermediates ()
                state <- DcsPassthrough
                Some(DcsHook(p, i, char b))
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some(Execute b)
            else
                state <- DcsIgnore
                None
        | DcsPassthrough ->
            if b = 0x1Buy then
                state <- Escape
                resetSequence ()
                Some DcsUnhook
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                Some DcsUnhook
            elif b = 0x7Fuy then
                None
            else
                Some(DcsPut b)
        | DcsIgnore ->
            if b = 0x1Buy then
                state <- Escape
                resetSequence ()
                None
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                None
            else
                None
        | SosPmApcString ->
            if b = 0x1Buy then
                state <- Escape
                resetSequence ()
                None
            elif b = 0x18uy || b = 0x1Auy then
                state <- Ground
                None
            else
                None

    /// Current state, exposed for tests / debugging only.
    member _.State : VtState = state
