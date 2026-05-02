module PtySpeak.Tests.Ui.AutomationPeerTests

open System
open System.IO
open System.Threading
open Xunit
open FlaUI.Core
open FlaUI.UIA3

/// Locate the Terminal.App.exe build output. CI's `dotnet build`
/// before `dotnet test` builds every project in the solution, so by
/// the time this test runs the exe is present at the expected path.
/// `<ProjectReference ReferenceOutputAssembly="false">` in the
/// fsproj guarantees the build-order dependency without linking the
/// app's outputs into the test bin directory.
///
/// Directory layout:
///   <repo>/tests/Tests.Ui/bin/Release/net9.0-windows/   ← AppContext.BaseDirectory
///   <repo>/src/Terminal.App/bin/Release/net9.0-windows/Terminal.App.exe
let private locateTerminalAppExe () : string =
    let testDir = AppContext.BaseDirectory
    // Five `..` segments to get from net9.0-windows back up to <repo>.
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

/// Stage 4 FlaUI infrastructure spike — the minimum-viable
/// integration test that:
///   1. launches Terminal.App.exe
///   2. attaches via UIA3
///   3. finds the TerminalView element by the ClassName the
///      TerminalAutomationPeer (PR #48) sets explicitly
///   4. asserts the element's ControlType is Document and Name is
///      "Terminal"
/// If this passes on CI, the FlaUI test infrastructure is real
/// and we have a verification harness any future Text-pattern
/// implementation can build on. If it fails — for desktop-session
/// reasons, P/Invoke quirks, or anything else — the failure mode
/// itself is the diagnostic.
[<Fact>]
let ``TerminalView is exposed as a UIA Document with the correct ClassName and Name`` () =
    let exePath = locateTerminalAppExe ()
    use app = Application.Launch(exePath)

    use automation = new UIA3Automation()
    let mainWindow =
        // FlaUI declares GetMainWindow's return as
        // `AutomationElement | null`; F# 9's Nullable=enable rightly
        // refuses to let us call methods on it without an explicit
        // null check. Pattern-match for null AND give a useful
        // failure message in one step — beats Assert.NotNull both
        // for nullability narrowing and for diagnostic value.
        //
        // Timeout bumped from 10s to 30s after observed flakiness on
        // the windows-2025 runner image: under parallel xUnit-test
        // load, Velopack initialisation + WPF subsystem startup +
        // ConPTY spawn can exceed 10s on a freshly-provisioned VM.
        // PRs that touched only docs (#104) hit this same timeout
        // failure mode, ruling out code regressions; the fix is
        // runner-tolerance, not changing application behaviour.
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(30.0)) with
        | null ->
            failwith "Main window did not appear within 30 seconds. Possible causes: Velopack startup hang, WPF subsystem misconfiguration, or runner desktop session not available."
        | mw -> mw

    let cf = automation.ConditionFactory
    let terminalView =
        match mainWindow.FindFirstDescendant(cf.ByClassName("TerminalView")) with
        | null ->
            failwith "TerminalView descendant not found in the UIA tree. PR #48's TerminalAutomationPeer should set ClassName=TerminalView via GetClassNameCore; check that OnCreateAutomationPeer is still wired and the peer is reaching the tree."
        | tv -> tv

    Assert.Equal(
        FlaUI.Core.Definitions.ControlType.Document,
        terminalView.ControlType)
    Assert.Equal("Terminal", terminalView.Name)

    app.Close() |> ignore
