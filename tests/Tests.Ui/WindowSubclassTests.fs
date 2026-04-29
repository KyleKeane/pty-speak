module PtySpeak.Tests.Ui.WindowSubclassTests

open System
open System.IO
open Xunit
open FlaUI.Core
open FlaUI.UIA3

/// Same exe-locator the existing AutomationPeerTests uses. The
/// duplication is small enough to live in two places for the
/// spike; if we add more FlaUI tests a shared helper module
/// becomes worth its own file.
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

/// Stage 4 follow-up spike (after the foundation arc #51/#52/#53)
/// — confirms the WM_GETOBJECT subclass hook installed by
/// `MainWindow.xaml.cs` actually receives the message under WPF's
/// message pump on `windows-latest` CI runners. This is the
/// pre-condition for Issue #49 option 1's
/// `IRawElementProviderSimple` exposure path: if the hook fires
/// at all, we have a place to install our own raw provider in
/// the next PR.
///
/// Verification mechanism: the hook writes a side-channel entry
/// to `%TEMP%/ptyspeak-wm-getobject-<pid>.log` whenever
/// `WM_GETOBJECT` fires. The test launches the app, attaches via
/// UIA (which forces UIA to query the window via WM_GETOBJECT),
/// then reads the log file. The hook returns DefSubclassProc for
/// every message, so WPF's existing UIA tree continues to work
/// — the `AutomationPeerTests` integration test from PR #51 is
/// the regression check for that.
[<Fact>]
let ``WM_GETOBJECT subclass hook fires when a UIA client attaches`` () =
    let exePath = locateTerminalAppExe ()
    use app = Application.Launch(exePath)

    // Compute the expected log path BEFORE attaching UIA so we
    // can fail-fast if the path is somehow malformed.
    let logPath =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "ptyspeak-wm-getobject-%d.log" app.ProcessId)

    use automation = new UIA3Automation()
    let mainWindow =
        match app.GetMainWindow(automation, TimeSpan.FromSeconds(10.0)) with
        | null ->
            failwith "Main window did not appear within 10 seconds. The app may have crashed during the WM_GETOBJECT subclass installation in MainWindow's SourceInitialized handler."
        | mw -> mw

    // Force a deeper UIA query so WM_GETOBJECT definitely fires
    // beyond just the initial window-discovery query.
    let cf = automation.ConditionFactory
    let _terminalView =
        match mainWindow.FindFirstDescendant(cf.ByClassName("TerminalView")) with
        | null ->
            failwith "TerminalView descendant not found in the UIA tree. The PR #48 peer should still work alongside the subclass hook because the hook returns DefSubclassProc for every message — if this fails, the hook is over-eagerly intercepting the message."
        | tv -> tv

    // The hook should have written at least one entry by now.
    if not (File.Exists logPath) then
        failwithf
            "Expected hook log at %s but the file does not exist. The subclass hook either wasn't installed (check SourceInitialized handler in MainWindow.xaml.cs) or didn't receive WM_GETOBJECT under WPF's message pump."
            logPath

    let content = File.ReadAllText(logPath)
    if content.Length = 0 then
        failwithf
            "Log file at %s is empty. The hook installed but didn't observe any WM_GETOBJECT messages during the UIA attach + descendant query."
            logPath

    // Best-effort cleanup so the next test run starts clean if
    // the process id is reused. Failure to delete is not fatal.
    try File.Delete(logPath) with _ -> ()
