module PtySpeak.Tests.Unit.SmokeTests

open Xunit

// Smoke check: the Terminal.Core assembly is reachable from the test
// project. Keeps the test runner honest about project references and
// loads succeeding before any module-specific test starts touching
// types from Terminal.Core. The earlier "string concat is associative"
// FsCheck property was a placeholder during initial wire-up of
// FsCheck.Xunit and has been removed; real property tests live in
// VtParserTests.fs and ScreenTests.fs.

[<Fact>]
let ``Terminal.Core assembly loads`` () =
    let asm = typeof<Terminal.Core.Marker>.Assembly
    Assert.Contains("Terminal.Core", asm.FullName)
