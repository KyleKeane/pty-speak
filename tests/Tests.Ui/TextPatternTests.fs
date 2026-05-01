module PtySpeak.Tests.Ui.TextPatternTests

open System
open System.IO
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

/// Stage 4 end-to-end verification that the WPF
/// `TerminalAutomationPeer.GetPattern` override for
/// `PatternInterface.Text` actually surfaces the UIA Text
/// pattern to a real UIA3 client (PR #56's ship architecture).
///
/// Verification chain:
///   1. Launch `Terminal.App.exe`. `Program.compose` calls
///      `TerminalSurface.SetScreen(Screen(30, 120))` synchronously
///      during composition, so by the time the main window is
///      visible to UIA the snapshot source is wired up.
///   2. Attach via UIA3. FlaUI walks the WPF UIA peer tree;
///      `TerminalAutomationPeer.GetPattern(PatternInterface.Text)`
///      returns `TerminalTextProvider`. UIA3 surfaces it on the
///      peer element directly — no WM_GETOBJECT raw-provider
///      indirection. (Audit-cycle PR-C deleted that indirection;
///      see commit history if you need the WM_GETOBJECT path
///      back for some reason.)
///   3. Walk the tree searching for any element that exposes the
///      Text pattern. The pattern lands on whichever element
///      WPF's peer tree assigns to it; UIA3's merge
///      semantics with the WPF host provider make the exact node
///      identity an implementation detail not worth pinning.
///   4. Read `DocumentRange.GetText(-1)`. The 30×120 blank screen
///      yields 30 rows of 120 spaces each joined by `\n`, so the
///      result has length `30*120 + 29 = 3629` minimum. cmd.exe's
///      banner ("Microsoft Windows [Version ...]" + prompt) may
///      fill some of those cells before the snapshot, but the
///      length floor stays the same — every cell has a rune even
///      when blank. The test only asserts the length floor; it
///      does not pin specific banner content because cmd.exe's
///      output isn't deterministic across Windows builds.
[<Fact>]
let ``UIA Text pattern is reachable and DocumentRange.GetText reflects the screen snapshot`` () =
    let exePath = locateTerminalAppExe ()
    use app = Application.Launch(exePath)

    use automation = new UIA3Automation()
    let mainWindow =
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(10.0)) with
        | null ->
            failwith "Main window did not appear within 10 seconds. The app may have crashed during MainWindow construction or the F# composition root (Program.compose). Check the SetScreen / channel-construction / focus-on-Loaded path."
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

    // FlaUI annotates `ITextPattern.DocumentRange` and
    // `ITextRange.GetText` as non-nullable, so F# 9's
    // Nullable=enable rejects defensive `match | null` patterns
    // here (FS3261). If either ever does return null at runtime
    // it'll be a `NullReferenceException` from the next call —
    // a noisy failure that points at the FlaUI surface, which
    // is the right place for the diagnostic since our F# side
    // never returns null for either value.
    let docRange = textPattern.DocumentRange

    // GetText(-1) returns the whole document; FlaUI / UIA pass
    // through the maxLength as-is, so the F# side's
    // "negative length means everything" branch handles this.
    let text = docRange.GetText(-1)

    // Stage 3b composes Screen(rows=30, cols=120) before the
    // window becomes visible to UIA, so the snapshot has every
    // cell populated (with U+0020 by default). The length floor
    // is 30*120 + 29 newlines = 3629. We assert >= rather than
    // == because cmd.exe banner / shell output may run before
    // the snapshot is captured but won't shrink it.
    let minimumLength = 30 * 120 + 29
    Assert.True(
        text.Length >= minimumLength,
        sprintf
            "Expected DocumentRange.GetText length >= %d (30 rows × 120 cols + 29 newlines from a default Screen snapshot); got %d. If the length is 0 the snapshot fell through to the empty-screen branch (screenSource returned null); if it's between 1 and %d the snapshot machinery is firing but the row dimensions don't match Program.compose's Screen(30, 120)."
            minimumLength
            text.Length
            (minimumLength - 1))

    // Newlines confirm that SnapshotText.render is joining rows
    // (the per-row character stream alone wouldn't have any).
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
/// What's asserted:
///   1. After `ExpandToEnclosingUnit(Line)` on the document
///      range, `GetText` length is bounded above by ~`cols`
///      (one row plus one row-separator newline at most).
///      This catches the no-op-stub regression — without
///      navigation the range stays at full-document length
///      (3629).
///   2. After `Move(Line, 1)` the range stays Line-shaped
///      (same length bound), confirming Move preserves the
///      unit shape rather than e.g. collapsing the range.
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
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(10.0)) with
        | null -> failwith "Main window did not appear within 10 seconds."
        | mw -> mw

    let textPattern =
        match findTextPattern mainWindow with
        | None -> failwith "Text pattern not found in the UIA tree."
        | Some tp -> tp

    let docRange = textPattern.DocumentRange
    let docLength = docRange.GetText(-1).Length

    // After Line expansion the range should cover at most
    // one row plus one row-separator. `Program.compose`
    // sets cols=120; the upper bound 200 leaves slack for
    // implementation choices around the trailing newline
    // without admitting "full document" (3629).
    let lineRange = docRange.Clone()
    lineRange.ExpandToEnclosingUnit(FlaUI.Core.Definitions.TextUnit.Line)
    let lineText = lineRange.GetText(-1)
    let firstLineLength = lineText.Length
    Assert.True(
        firstLineLength > 0 && firstLineLength <= 200,
        sprintf
            "Expected Line-expanded range length in (0, 200]; got %d. If %d == %d, ExpandToEnclosingUnit(Line) regressed to a no-op (Stage 4's preview.20 failure mode)."
            firstLineLength
            firstLineLength
            docLength)

    // Move to next line should preserve Line shape.
    let moved = lineRange.Move(FlaUI.Core.Definitions.TextUnit.Line, 1)
    Assert.True(
        moved >= 0,
        sprintf "Expected Move(Line, 1) to return non-negative count; got %d" moved)
    let secondLineLength = lineRange.GetText(-1).Length
    Assert.True(
        secondLineLength > 0 && secondLineLength <= 200,
        sprintf
            "Expected post-Move Line range length in (0, 200]; got %d. Move regressed if this matches the document length (%d)."
            secondLineLength
            docLength)

    app.Close() |> ignore
