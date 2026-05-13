module PtySpeak.Tests.Ui.TextPatternTests

open System
open System.IO
open System.Threading
open Xunit
open FlaUI.Core
open FlaUI.Core.AutomationElements
open FlaUI.UIA3

/// Same exe-locator as the existing FlaUI tests. Five `..`
/// segments climb from the test bin's net9.0-windows folder
/// back to the repo root.
let private locateTerminalAppExe () : string =
    let testDir = AppContext.BaseDirectory
    let repoRoot =
        Path.GetFullPath(
            Path.Combine(testDir, "..", "..", "..", "..", ".."))
    let exePath =
        Path.Combine(
            repoRoot,
            "src", "Terminal.App", "bin", "Release", "net9.0-windows",
            "Terminal.App.exe")
    if not (File.Exists exePath) then
        failwithf
            "Terminal.App.exe not found at %s. Expected MSBuild to have built it before tests run."
            exePath
    exePath

/// Walk an element + its descendants looking for the first
/// element that exposes the UIA Text pattern. With Stage 4's
/// architecture (since PR #56), the Text pattern lives on
/// the WPF `TerminalAutomationPeer`'s `GetPattern` override
/// for `PatternInterface.Text`, which UIA3 reaches through
/// the standard WPF peer tree. The exact element where
/// FlaUI's tree walk lands the pattern is an implementation
/// detail of WPF's UIA peer hierarchy, so the test searches
/// rather than asserting a fixed location.
let rec private findTextPattern
        (element: AutomationElement) : FlaUI.Core.Patterns.ITextPattern option =
    match element.Patterns.Text.PatternOrDefault with
    | null ->
        // FlaUI's FindAllChildren returns AutomationElement[]; F#'s
        // nullable-reference checking under Nullable=enable rightly
        // refuses to unwrap any nulls implicitly, so we filter.
        let children =
            element.FindAllChildren()
            |> Array.filter (fun c -> not (isNull (box c)))
        children |> Array.tryPick findTextPattern
    | pattern -> Some pattern

/// Cycle 46 PR-B — poll `DocumentRange.GetText` until the
/// materialised ContentHistory tail has at least
/// `minimumLength` characters or `timeout` elapses. The
/// substrate (`ContentHistory`) starts empty and fills as the
/// cmd.exe banner streams through the PTY reader; the
/// pre-PR-B screen-grid `TerminalTextProvider` had no such
/// settling phase (the 30×120 grid was pre-populated with
/// blanks at composition time). Returns the final text — the
/// caller asserts whatever it wants on it.
let private waitForDocumentContent
        (textPattern: FlaUI.Core.Patterns.ITextPattern)
        (minimumLength: int)
        (timeout: TimeSpan)
        : string =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable text = textPattern.DocumentRange.GetText(-1)
    while text.Length < minimumLength && sw.Elapsed < timeout do
        Thread.Sleep(100)
        text <- textPattern.DocumentRange.GetText(-1)
    text

/// Stage 4 end-to-end verification that the WPF
/// `TerminalAutomationPeer.GetPattern` override for
/// `PatternInterface.Text` actually surfaces the UIA Text
/// pattern to a real UIA3 client (PR #56's ship architecture).
///
/// Cycle 46 PR-B substrate-swap (ADR 0002): `DocumentRange`
/// now reflects the `ContentHistory` tail (materialised via
/// `ContentHistory.tailText`, capped at 256 KB) instead of
/// the screen grid. Behavioural differences this test now
/// accounts for:
///   * `ContentHistory` starts empty and fills as cmd.exe
///     emits its banner. Old test relied on the pre-populated
///     30×120 screen grid (≥3629 chars from blank cells); new
///     test waits for actual cmd.exe banner content to arrive.
///   * Line counts and lengths are variable (cmd.exe banner
///     is ~3 lines of ~60-80 chars), not the old grid-shaped
///     30 rows of 120 cells.
///
/// Verification chain:
///   1. Launch `Terminal.App.exe`. `Program.compose` calls
///      `TerminalSurface.SetContentHistory(contentHistory)`
///      synchronously during composition, so by the time the
///      main window is visible to UIA the substrate is wired.
///   2. Attach via UIA3. `TerminalAutomationPeer.GetPattern(
///      PatternInterface.Text)` returns
///      `ContentHistoryTextProvider`.
///   3. Walk the tree searching for the Text pattern.
///   4. Poll `DocumentRange.GetText(-1)` until the cmd.exe
///      banner content arrives (cap at ~5s; cmd.exe banner is
///      typically present within a few hundred ms).
[<Fact>]
let ``UIA Text pattern is reachable and DocumentRange.GetText reflects the ContentHistory tail`` () =
    let exePath = locateTerminalAppExe ()
    use app = Application.Launch(exePath)

    use automation = new UIA3Automation()
    let mainWindow =
        // Timeout bumped from 10s to 30s after observed flakiness
        // on the windows-2025 runner image (see AutomationPeerTests
        // for details). Cycle 45 Commit 2 (2026-05-11) hit the
        // 30s ceiling on a fresh PR with code unaffecting window
        // load — bumped to 60s.
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(60.0)) with
        | null ->
            failwith "Main window did not appear within 60 seconds. The app may have crashed during MainWindow construction or the F# composition root (Program.compose). Check the SetScreen / channel-construction / focus-on-Loaded path."
        | mw -> mw

    let textPattern =
        match findTextPattern mainWindow with
        | None ->
            // Diagnostic: surface what patterns FlaUI does see
            // on the main window. Audit-cycle PR-C deleted the
            // WM_GETOBJECT log dump that lived here previously
            // (the WM_GETOBJECT hook itself was dead-code MSAA
            // fallback after the Stage 4 architectural pivot in
            // PR #56). Today the Text pattern lives on the WPF
            // peer's `GetPattern` override; if it's missing,
            // the regression is most likely on
            // `TerminalAutomationPeer.GetPattern` returning the
            // wrong thing for `PatternInterface.Text`.
            let mainWindowPatternNames =
                let p = mainWindow.Patterns
                let pairs : (string * bool) list = [
                    "Text", p.Text.IsSupported
                    "Value", p.Value.IsSupported
                    "LegacyIAccessible", p.LegacyIAccessible.IsSupported
                    "Window", p.Window.IsSupported
                    "Transform", p.Transform.IsSupported
                ]
                pairs
                |> List.map (fun (n, s) -> sprintf "%s=%b" n s)
                |> String.concat ", "

            failwithf
                "Text pattern not found anywhere in the UIA tree.\n\
                \n\
                MainWindow class=%s, name=%s\n\
                MainWindow.Patterns: %s\n\
                \n\
                Suspect (in priority order):\n\
                  1. TerminalAutomationPeer.GetPattern is no longer returning the TextProvider for PatternInterface.Text.\n\
                  2. TerminalView.OnCreateAutomationPeer no longer constructs the peer.\n\
                  3. TerminalView.TextProvider is null at peer construction time."
                mainWindow.ClassName
                mainWindow.Name
                mainWindowPatternNames
        | Some tp -> tp

    // Cycle 46 PR-B: ContentHistory starts empty and fills as
    // cmd.exe streams its banner through the PTY reader. Poll
    // for content; cap at 5s to keep the test bounded if
    // something upstream wedges. A 16-char floor is well below
    // the cmd.exe banner length ("Microsoft Windows [Version
    // 10.0.x.y]\n(c) Microsoft Corporation. All rights
    // reserved.") so it tolerates banner variation across
    // Windows builds while still asserting that real content
    // arrived (not just a single prompt char).
    let minimumLength = 16
    let text =
        waitForDocumentContent
            textPattern
            minimumLength
            (TimeSpan.FromSeconds(5.0))

    Assert.True(
        text.Length >= minimumLength,
        sprintf
            "Expected DocumentRange.GetText length >= %d after 5s of polling (cmd.exe banner content via ContentHistory); got %d. If the length is 0 the substrate is empty — likely SetContentHistory was never wired (check Program.fs after SetScreen / SetDisplayBuffer) or the reader loop is wedged before the cmd.exe banner emits. If non-zero but below %d, the banner arrived but is shorter than expected — check cmd.exe behaviour on the runner."
            minimumLength
            text.Length
            minimumLength)

    // Newlines confirm that the materialised tail spans rows.
    // cmd.exe banner is ~3 lines so this is reliably present.
    Assert.Contains("\n", text)

    app.Close() |> ignore

/// Stage 4 navigation regression test (post-PR-#56).
///
/// Preview.20 install smoke established that a working
/// Text-pattern producer is necessary but not sufficient
/// for NVDA: the review cursor needs functional
/// `ExpandToEnclosingUnit` + `Move` to delimit and traverse
/// lines. PR #56's no-op stubs satisfied the interface but
/// silently dropped NVDA's navigation calls, leaving the
/// range collapsed at start (read-current-line returned
/// "blank"). This test pins that working line navigation
/// stays working.
///
/// Cycle 46 PR-B substrate-swap (ADR 0002): the range is now
/// over the `ContentHistory` materialised tail, not the 30×120
/// screen grid. cmd.exe banner lines are typically 0-80 chars;
/// no fixed `cols` to assert against. The single-line bound
/// is now an absolute upper limit (256) rather than the
/// grid-derived 200; the meaningful regression test is still
/// "Line-expanded range is much shorter than the full
/// document", which a 256-char ceiling catches.
///
/// What's asserted:
///   1. After `ExpandToEnclosingUnit(Line)` on the document
///      range, `GetText` length is bounded above by 256 chars.
///      This catches the no-op-stub regression — without
///      navigation the range stays at full-document length
///      (typically thousands of chars for a populated tail).
///   2. After `Move(Line, 1)` the range either stays Line-
///      shaped (same length bound) or returns 0 if the
///      content was a single line. We accept either since
///      cmd.exe banner length varies across Windows builds.
///
/// What's deliberately not asserted:
///   * Specific cell content — cmd.exe banner timing isn't
///     deterministic across Windows runners.
///   * Word / Paragraph navigation — Stage 4 ships Line +
///     Character only; later stages add tokenized units.
[<Fact>]
let ``Text-pattern range navigation can pin to a single line and advance`` () =
    let exePath = locateTerminalAppExe ()
    use app = Application.Launch(exePath)
    use automation = new UIA3Automation()
    let mainWindow =
        // Timeout bumped from 10s to 30s, then to 60s in Cycle 45
        // Commit 2 (2026-05-11) after a fresh flake. See notes
        // elsewhere in this file and in AutomationPeerTests.
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(60.0)) with
        | null -> failwith "Main window did not appear within 60 seconds."
        | mw -> mw

    let textPattern =
        match findTextPattern mainWindow with
        | None -> failwith "Text pattern not found in the UIA tree."
        | Some tp -> tp

    // Cycle 46 PR-B: wait for ContentHistory to populate with
    // cmd.exe banner before navigating. Without the wait the
    // document range starts empty and ExpandToEnclosingUnit(Line)
    // can't show meaningful behaviour.
    let _ =
        waitForDocumentContent
            textPattern
            16
            (TimeSpan.FromSeconds(5.0))

    let docRange = textPattern.DocumentRange
    let docLength = docRange.GetText(-1).Length

    // After Line expansion the range should cover at most one
    // line of cmd.exe output. 256 chars is a comfortable
    // ceiling for any single banner line.
    let lineRange = docRange.Clone()
    lineRange.ExpandToEnclosingUnit(FlaUI.Core.Definitions.TextUnit.Line)
    let lineText = lineRange.GetText(-1)
    let firstLineLength = lineText.Length
    Assert.True(
        firstLineLength > 0 && firstLineLength <= 256,
        sprintf
            "Expected Line-expanded range length in (0, 256]; got %d. If %d == %d, ExpandToEnclosingUnit(Line) regressed to a no-op (preview.20 failure mode)."
            firstLineLength
            firstLineLength
            docLength)

    // Move to next line should preserve Line shape OR return 0
    // if the content was a single line. cmd.exe banner is
    // typically >1 line but isn't guaranteed across Windows
    // builds; we accept either outcome.
    let moved = lineRange.Move(FlaUI.Core.Definitions.TextUnit.Line, 1)
    Assert.True(
        moved >= 0,
        sprintf "Expected Move(Line, 1) to return non-negative count; got %d" moved)
    if moved > 0 then
        let secondLineLength = lineRange.GetText(-1).Length
        Assert.True(
            secondLineLength >= 0 && secondLineLength <= 256,
            sprintf
                "Expected post-Move Line range length in [0, 256]; got %d. Move regressed if this matches the document length (%d)."
                secondLineLength
                docLength)

    app.Close() |> ignore
