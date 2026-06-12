module PtySpeak.Tests.Unit.ChunkNarrationTests

open Xunit
open Engine.Core
open Engine.Core.Chunk

// ---------------------------------------------------------------------
// RELAUNCH-SPEC §4.6 / §14.11 — canonical narration rendering.
// ---------------------------------------------------------------------
//
// Structure announced before content; container kinds announce
// child counts; tool calls render the §6.2 "what was run" info.

let private ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "fixture build failed: %s" e

let private describeSolo (kind: ChunkKind) (text: string) : string =
    let chunk, tree = ok (ChunkTree.append None kind text ChunkTree.empty)
    ChunkNarration.describe tree chunk

[<Fact>]
let ``paragraph narrates its text verbatim`` () =
    Assert.Equal("Plain words.", describeSolo Paragraph "Plain words.")

[<Fact>]
let ``heading announces its level first`` () =
    Assert.Equal(
        "Heading level 2: Setup",
        describeSolo (Heading 2) "Setup")

[<Fact>]
let ``a heading with children announces its section size`` () =
    let h, tree = ok (ChunkTree.append None (Heading 1) "Plan" ChunkTree.empty)
    let _, tree = ok (ChunkTree.append (Some h.Id) Paragraph "a" tree)
    let _, tree = ok (ChunkTree.append (Some h.Id) Paragraph "b" tree)
    Assert.Equal(
        "Heading level 1: Plan. 2 items inside.",
        ChunkNarration.describe tree h)

[<Fact>]
let ``list announces order and item count`` () =
    let list, tree =
        ok (ChunkTree.append None (ListBlock true) "" ChunkTree.empty)
    let _, tree = ok (ChunkTree.append (Some list.Id) ListItem "one" tree)
    let _, tree = ok (ChunkTree.append (Some list.Id) ListItem "two" tree)
    Assert.Equal(
        "Numbered list, 2 items.",
        ChunkNarration.describe tree list)

[<Fact>]
let ``list item with nested content says so`` () =
    let item, tree =
        ok (ChunkTree.append None ListItem "outer" ChunkTree.empty)
    let _, tree =
        ok (ChunkTree.append (Some item.Id) (ListBlock false) "" tree)
    Assert.Equal(
        "outer. Has nested content.",
        ChunkNarration.describe tree item)

[<Fact>]
let ``code block announces language and line count before the body`` () =
    Assert.Equal(
        "Code block, fsharp, 2 lines. let x = 1\nlet y = 2",
        describeSolo (CodeBlock (Some "fsharp")) "let x = 1\nlet y = 2")

[<Fact>]
let ``tool call renders name then input`` () =
    Assert.Equal(
        """Tool call, Bash. Input: {"command":"dir"}""",
        describeSolo (ToolUse "Bash") """{"command":"dir"}""")

[<Fact>]
let ``tool error result is announced as an error`` () =
    Assert.Equal(
        "Tool result, error. boom",
        describeSolo (ToolResult true) "boom")

[<Fact>]
let ``user request is labelled`` () =
    Assert.Equal(
        "Your request: fix the bug",
        describeSolo UserRequest "fix the bug")

[<Fact>]
let ``separator has a fixed word`` () =
    Assert.Equal("Separator.", describeSolo ThematicBreak "")

// --- ADR 0012 S5 — capped narration ---------------------------------

let private describeCappedSolo maxChars (kind: ChunkKind) (text: string) =
    let chunk, tree = ok (ChunkTree.append None kind text ChunkTree.empty)
    ChunkNarration.describeCapped maxChars tree chunk

[<Fact>]
let ``short bodies are not truncated`` () =
    Assert.Equal(
        "Plain words.",
        describeCappedSolo 600 Paragraph "Plain words.")

[<Fact>]
let ``long bodies cut with an honest marker and remaining count`` () =
    let longText = String.replicate 100 "abcdefghij" // 1000 chars
    let rendered = describeCappedSolo 100 Paragraph longText
    Assert.StartsWith(longText.Substring(0, 100), rendered)
    Assert.Contains("Truncated; 900 more characters", rendered)
    Assert.Contains("press r to hear all", rendered)

[<Fact>]
let ``the structure prefix survives the cut`` () =
    let longCode = String.replicate 200 "let x = 1\n"
    let rendered =
        describeCappedSolo 80 (CodeBlock (Some "fsharp")) longCode
    Assert.StartsWith("Code block, fsharp, ", rendered)

// --- ADR 0012 S2 — positional orientation ---------------------------

/// req ── [p1; heading ── [inner]; p2]
let private orientationFixture () =
    let req, tree = ok (ChunkTree.append None UserRequest "explain the plan" ChunkTree.empty)
    let p1, tree = ok (ChunkTree.append (Some req.Id) Paragraph "first" tree)
    let h, tree = ok (ChunkTree.append (Some req.Id) (Heading 2) "Setup" tree)
    let inner, tree = ok (ChunkTree.append (Some h.Id) Paragraph "inside text" tree)
    let p2, tree = ok (ChunkTree.append (Some req.Id) Paragraph "last" tree)
    tree, req, p1, h, inner, p2

[<Fact>]
let ``positionOf is one-based among siblings`` () =
    let tree, _, p1, h, inner, p2 = orientationFixture ()
    Assert.Equal((1, 3), ChunkNarration.positionOf tree p1)
    Assert.Equal((2, 3), ChunkNarration.positionOf tree h)
    Assert.Equal((3, 3), ChunkNarration.positionOf tree p2)
    Assert.Equal((1, 1), ChunkNarration.positionOf tree inner)

[<Fact>]
let ``describeAt appends the position to the bounded read`` () =
    let tree, _, p1, _, _, _ = orientationFixture ()
    Assert.Equal(
        "first — 1 of 3.",
        ChunkNarration.describeAt 600 tree p1)

[<Fact>]
let ``locate speaks kind position trail and depth`` () =
    let tree, _, _, _, inner, _ = orientationFixture ()
    Assert.Equal(
        "Paragraph, 1 of 1, inside section Setup, "
        + "inside your request: explain the plan. Depth 3.",
        ChunkNarration.locate tree inner)

[<Fact>]
let ``locate at top level has no trail and depth 1`` () =
    let req, tree =
        ok (ChunkTree.append None UserRequest "solo" ChunkTree.empty)
    Assert.Equal(
        "Your request, 1 of 1. Depth 1.",
        ChunkNarration.locate tree req)

[<Fact>]
let ``locate clips long ancestor labels`` () =
    let longReq = String.replicate 30 "abc" // 90 chars
    let req, tree = ok (ChunkTree.append None UserRequest longReq ChunkTree.empty)
    let kid, tree = ok (ChunkTree.append (Some req.Id) Paragraph "x" tree)
    let located = ChunkNarration.locate tree kid
    Assert.Contains("your request: " + longReq.Substring(0, 40) + "…", located)
