module PtySpeak.Tests.Unit.Osc133Tests

open System
open System.Text
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// SessionModel Tier 1.B — OSC 133 parser pinning
// ---------------------------------------------------------------------
//
// Tier 1.B's `Osc133.tryParse` converts a parser-emitted
// `byte[][]` (semicolon-split OSC parameters) into a
// `PromptBoundaryData` event. These tests pin:
//   * The four discriminator letters (A/B/C/D) map to the
//     correct BoundaryKind.
//   * Kind D's optional exit code parses correctly + is
//     None for malformed.
//   * `aid=<id>` parameter hoists to CommandId.
//   * Other key=value parameters land in ExtraParams.
//   * Malformed inputs (empty parms, wrong type,
//     non-A/B/C/D letter) return None silently.
//   * BoundarySource is always Osc133 + DetectedAt is the
//     supplied parameter.
//
// State-machine tests live in SessionModelTests (Tier 1.A
// pinned data shapes; Tier 1.C will add transition tests).

let private ascii (s: string) : byte[] =
    Encoding.ASCII.GetBytes s

/// Construct a parms array from string parts. Mirrors the
/// upstream parser's semicolon-split behaviour: each part
/// becomes one byte[] entry.
let private parmsOf (parts: string list) : byte[][] =
    parts |> List.map ascii |> List.toArray

let private fixedTime = DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc)

// ---------------------------------------------------------------------
// Kind discriminator — happy path
// ---------------------------------------------------------------------

[<Fact>]
let ``tryParse handles ESC]133;A as PromptStart`` () =
    let parms = parmsOf [ "133"; "A" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
        Assert.Equal(BoundarySource.Osc133, data.Source)
        Assert.Equal(fixedTime, data.DetectedAt)
        Assert.Equal(None, data.CommandId)
        Assert.True(Map.isEmpty data.ExtraParams)
    | None -> Assert.Fail("Expected PromptStart")

[<Fact>]
let ``tryParse handles ESC]133;B as CommandStart`` () =
    let parms = parmsOf [ "133"; "B" ]
    match Osc133.tryParse parms fixedTime with
    | Some data -> Assert.Equal(BoundaryKind.CommandStart, data.Kind)
    | None -> Assert.Fail("Expected CommandStart")

[<Fact>]
let ``tryParse handles ESC]133;C as OutputStart`` () =
    let parms = parmsOf [ "133"; "C" ]
    match Osc133.tryParse parms fixedTime with
    | Some data -> Assert.Equal(BoundaryKind.OutputStart, data.Kind)
    | None -> Assert.Fail("Expected OutputStart")

[<Fact>]
let ``tryParse handles ESC]133;D without exit code`` () =
    let parms = parmsOf [ "133"; "D" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.CommandFinished None, data.Kind)
    | None -> Assert.Fail("Expected CommandFinished None")

[<Fact>]
let ``tryParse handles ESC]133;D;0 as CommandFinished Some 0`` () =
    let parms = parmsOf [ "133"; "D"; "0" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.CommandFinished (Some 0), data.Kind)
    | None -> Assert.Fail("Expected CommandFinished (Some 0)")

[<Fact>]
let ``tryParse handles ESC]133;D;127 as CommandFinished Some 127`` () =
    let parms = parmsOf [ "133"; "D"; "127" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.CommandFinished (Some 127), data.Kind)
    | None -> Assert.Fail("Expected CommandFinished (Some 127)")

[<Fact>]
let ``tryParse handles ESC]133;D with malformed exit code as CommandFinished None`` () =
    // Malformed exit-code value (not parseable as Int32);
    // boundary still emits, exit code dropped.
    let parms = parmsOf [ "133"; "D"; "garbage" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.CommandFinished None, data.Kind)
    | None -> Assert.Fail("Expected CommandFinished None for malformed exit")

// ---------------------------------------------------------------------
// aid= and key=value parameter parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``tryParse hoists aid= parameter into CommandId`` () =
    let parms = parmsOf [ "133"; "A"; "aid=foo-123" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(Some "foo-123", data.CommandId)
        Assert.True(Map.isEmpty data.ExtraParams)
    | None -> Assert.Fail("Expected boundary with CommandId")

[<Fact>]
let ``tryParse handles aid= alongside other key=value params`` () =
    let parms = parmsOf [ "133"; "A"; "aid=cmd-7"; "k1=v1"; "k2=v2" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(Some "cmd-7", data.CommandId)
        Assert.Equal(2, data.ExtraParams.Count)
        Assert.Equal("v1", data.ExtraParams.["k1"])
        Assert.Equal("v2", data.ExtraParams.["k2"])
    | None -> Assert.Fail("Expected boundary with CommandId + ExtraParams")

[<Fact>]
let ``tryParse handles key=value params without aid`` () =
    let parms = parmsOf [ "133"; "C"; "k1=v1"; "k2=v2" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(None, data.CommandId)
        Assert.Equal(2, data.ExtraParams.Count)
    | None -> Assert.Fail("Expected boundary with ExtraParams")

[<Fact>]
let ``tryParse silently skips parameters without =`` () =
    // "noEqualsHere" has no `=`; should be dropped silently
    // per the OSC silent-drop convention. Boundary still
    // emits.
    let parms = parmsOf [ "133"; "A"; "noEqualsHere"; "ok=yes" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
        Assert.Equal(1, data.ExtraParams.Count)
        Assert.Equal("yes", data.ExtraParams.["ok"])
    | None -> Assert.Fail("Expected boundary; malformed params should be skipped")

[<Fact>]
let ``tryParse handles aid= alongside CommandFinished with exit code`` () =
    let parms = parmsOf [ "133"; "D"; "0"; "aid=cmd-9" ]
    match Osc133.tryParse parms fixedTime with
    | Some data ->
        Assert.Equal(BoundaryKind.CommandFinished (Some 0), data.Kind)
        Assert.Equal(Some "cmd-9", data.CommandId)
    | None -> Assert.Fail("Expected CommandFinished with CommandId")

// ---------------------------------------------------------------------
// Malformed inputs → None
// ---------------------------------------------------------------------

[<Fact>]
let ``tryParse rejects empty parms array`` () =
    let parms : byte[][] = [||]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

[<Fact>]
let ``tryParse rejects single-param array`` () =
    // Just "133" without a kind discriminator.
    let parms = parmsOf [ "133" ]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

[<Fact>]
let ``tryParse rejects parms.[0] not equal to 133`` () =
    // OSC 52 (clipboard) — must NOT be treated as 133.
    let parms = parmsOf [ "52"; "A" ]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

[<Fact>]
let ``tryParse rejects unknown kind letter`` () =
    let parms = parmsOf [ "133"; "Z" ]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

[<Fact>]
let ``tryParse rejects multi-byte kind discriminator`` () =
    // Kind must be exactly one byte; "AA" is malformed.
    let parms = parmsOf [ "133"; "AA" ]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

[<Fact>]
let ``tryParse rejects empty kind bytes`` () =
    let parms = [| ascii "133"; [||] |]
    Assert.Equal(None, Osc133.tryParse parms fixedTime)

// ---------------------------------------------------------------------
// Source + DetectedAt stamping
// ---------------------------------------------------------------------

[<Fact>]
let ``tryParse always stamps Source = Osc133`` () =
    let parms = parmsOf [ "133"; "A" ]
    match Osc133.tryParse parms fixedTime with
    | Some data -> Assert.Equal(BoundarySource.Osc133, data.Source)
    | None -> Assert.Fail("Expected boundary")

[<Fact>]
let ``tryParse stamps DetectedAt from the supplied parameter`` () =
    let customTime = DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let parms = parmsOf [ "133"; "B" ]
    match Osc133.tryParse parms customTime with
    | Some data -> Assert.Equal(customTime, data.DetectedAt)
    | None -> Assert.Fail("Expected boundary")

[<Fact>]
let ``tryParse leaves MatchedRowText = None (parser has no screen access)`` () =
    // Tier 1.E: the OSC 133 parser doesn't see screen state.
    // `Program.fs.handlePromptBoundary` augments parsed
    // boundaries with cursor-row text via a fresh snapshot
    // capture before passing to SessionModel.apply.
    let parms = parmsOf [ "133"; "A" ]
    match Osc133.tryParse parms fixedTime with
    | Some data -> Assert.Equal(None, data.MatchedRowText)
    | None -> Assert.Fail("Expected boundary")
