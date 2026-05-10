module PtySpeak.Tests.Unit.EchoCorrelatorTests

open System
open Xunit
open Terminal.Core

/// Cycle 38c — pins the EchoCorrelator contract per
/// `fluffy-simon.md` Section 20.5.

let private t0 = DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

let private fresh () : EchoCorrelator.T =
    EchoCorrelator.create EchoCorrelator.defaultParameters

let private bytes (s: string) : byte[] =
    System.Text.Encoding.UTF8.GetBytes(s)

// =====================================================================
// Baseline + exact-match
// =====================================================================

[<Fact>]
let ``matchAndConsumeEchoPrefix returns 0 when buffer is empty`` () =
    let c = fresh ()
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c t0 "anything"
    Assert.Equal(0, n)

[<Fact>]
let ``matchAndConsumeEchoPrefix returns N when payload starts with recent input`` () =
    let c = fresh ()
    EchoCorrelator.recordWrite c t0 (bytes "echo")
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "echo hello"
    Assert.Equal(4, n)

[<Fact>]
let ``matchAndConsumeEchoPrefix returns 0 when payload diverges from input`` () =
    let c = fresh ()
    EchoCorrelator.recordWrite c t0 (bytes "echo")
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "hello world"
    Assert.Equal(0, n)

// =====================================================================
// CR-LF normalisation (load-bearing for cmd)
// =====================================================================

[<Fact>]
let ``CR in correlator matches CR-LF in payload (cmd echo behaviour)`` () =
    let c = fresh ()
    // User types "echo" + Enter. KeyEncoding sends "echo\r".
    EchoCorrelator.recordWrite c t0 (bytes "echo\r")
    // Cmd echoes "echo\r\n" before running the command.
    let n =
        EchoCorrelator.matchAndConsumeEchoPrefix
            c (after 5) "echo\r\nhello\r\n"
    // 5 input bytes consumed (e,c,h,o,\r); 6 payload bytes
    // matched (echo\r\n) because the \n piggybacks on the \r.
    Assert.Equal(6, n)

// =====================================================================
// Consumption invariant
// =====================================================================

[<Fact>]
let ``matchAndConsumeEchoPrefix consumes matched bytes so a second call returns 0`` () =
    let c = fresh ()
    EchoCorrelator.recordWrite c t0 (bytes "abc")
    let first =
        EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "abc"
    Assert.Equal(3, first)
    let second =
        EchoCorrelator.matchAndConsumeEchoPrefix c (after 10) "abc"
    Assert.Equal(0, second)

[<Fact>]
let ``partial match leaves remaining bytes available for next call`` () =
    let c = fresh ()
    EchoCorrelator.recordWrite c t0 (bytes "abcdef")
    let first =
        EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "abcXYZ"
    Assert.Equal(3, first)
    let second =
        EchoCorrelator.matchAndConsumeEchoPrefix c (after 10) "def"
    Assert.Equal(3, second)

// =====================================================================
// Time bounds + buffer bounds
// =====================================================================

[<Fact>]
let ``entries older than MaxAgeMs are expired`` () =
    let parameters : EchoCorrelator.Parameters =
        { MaxBufferSize = 1024
          MaxAgeMs = 100 }
    let c = EchoCorrelator.create parameters
    EchoCorrelator.recordWrite c t0 (bytes "echo")
    // 200ms later, bytes are too old to match.
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c (after 200) "echo"
    Assert.Equal(0, n)

[<Fact>]
let ``recordWrite drops oldest entries when buffer exceeds MaxBufferSize`` () =
    let parameters : EchoCorrelator.Parameters =
        { MaxBufferSize = 4
          MaxAgeMs = 10000 }
    let c = EchoCorrelator.create parameters
    EchoCorrelator.recordWrite c t0 (bytes "abcdef")
    // Buffer should hold only the last 4 bytes: "cdef".
    Assert.Equal(4, EchoCorrelator.pendingCount c)
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "cdef"
    Assert.Equal(4, n)

[<Fact>]
let ``reset clears all pending bytes`` () =
    let c = fresh ()
    EchoCorrelator.recordWrite c t0 (bytes "abc")
    EchoCorrelator.reset c
    Assert.Equal(0, EchoCorrelator.pendingCount c)
    let n = EchoCorrelator.matchAndConsumeEchoPrefix c (after 5) "abc"
    Assert.Equal(0, n)
