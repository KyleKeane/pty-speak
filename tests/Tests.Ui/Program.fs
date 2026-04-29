module PtySpeak.Tests.Ui.Program

// Serialize all UI tests in this assembly. xUnit's default
// runs distinct test classes (== distinct .fs files with
// `[<Fact>]` members) in parallel — fine for unit tests, but
// our FlaUI integration tests each spawn `Terminal.App.exe`
// and create a `UIA3Automation` instance, and concurrent
// COM/UIA setup across processes consistently fails one of
// the tests with an "Unexpected HRESULT" at
// `UIA3Automation.FromHandle`. Adding TextPatternTests
// (PR #56) was the third FlaUI test class to land — once
// three were in the same assembly the COM cross-talk
// surfaced reliably.
//
// `DisableTestParallelization = true` is the xUnit-documented
// assembly-level switch for serializing the whole test
// collection. Cost is negligible (UI tests dominate runtime
// regardless) and the benefit is deterministic UIA setup.
[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

[<EntryPoint>]
let main _ = 0
