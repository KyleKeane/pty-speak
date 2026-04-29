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
/// element that exposes the UIA Text pattern. The raw provider
/// installed in `WindowSubclassNative` returns `TerminalTextProvider`
/// for `UIA_TextPatternId` on the OBJID_CLIENT fragment, which
/// UIA fuses with the WPF host provider for the same HWND — that
/// fusion's exact placement in the FlaUI tree (root window vs.
/// some descendant) is dependent on UIA's internal merge rules,
/// so the test searches rather than asserting a fixed location.
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

/// Stage 4 PR C — end-to-end verification that the
/// `WM_GETOBJECT` → `TerminalRawProvider` → `TerminalTextProvider`
/// chain installed by PRs #54 (hook) and #55 (raw provider)
/// actually surfaces the UIA Text pattern to a real UIA client.
///
/// Verification chain:
///   1. Launch `Terminal.App.exe`. `Program.compose` calls
///      `TerminalSurface.SetScreen(Screen(30, 120))` synchronously
///      during composition, so by the time the main window is
///      visible to UIA the snapshot source is wired up.
///   2. Attach via UIA3. FlaUI queries the window via
///      `WM_GETOBJECT(OBJID_CLIENT)`, which our subclass hook
///      intercepts; `UiaReturnRawElementProvider` hands UIA our
///      `TerminalRawProvider`, which advertises Text pattern
///      support through `GetPatternProvider(10024)`.
///   3. Walk the tree searching for any element that exposes the
///      Text pattern. The Text pattern lands at whichever node
///      UIA chooses to attach our raw provider's patterns to —
///      typically the OBJID_CLIENT fragment root, but UIA's merge
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
            failwith "Main window did not appear within 10 seconds. The app may have crashed during MainWindow's SourceInitialized handler — check the WindowSubclassNative.InstallHook call site for a regression."
        | mw -> mw

    let textPattern =
        match findTextPattern mainWindow with
        | None ->
            failwith "Text pattern not found anywhere in the UIA tree. The TerminalRawProvider's GetPatternProvider should return a TerminalTextProvider for UIA_TextPatternId (10024); if this fails, either the WM_GETOBJECT hook isn't installing the provider, or UIA isn't fusing the raw provider's patterns with the WPF host fragment."
        | Some tp -> tp

    let docRange =
        match textPattern.DocumentRange with
        | null ->
            failwith "TextPattern.DocumentRange returned null. The TerminalTextProvider.DocumentRange property should always return a TerminalTextRange — even when _screen is null it returns a zero-row range, never null."
        | r -> r

    // GetText(-1) returns the whole document; FlaUI / UIA pass
    // through the maxLength as-is, so the F# side's
    // "negative length means everything" branch handles this.
    let text =
        match docRange.GetText(-1) with
        | null ->
            failwith "DocumentRange.GetText(-1) returned null. SnapshotText.render should always return a string (possibly empty); a null return indicates a marshalling regression."
        | s -> s

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
