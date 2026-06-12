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
