module PtySpeak.Tests.Unit.SmokeTests

open Xunit
open FsCheck.Xunit

[<Fact>]
let ``Terminal.Core assembly loads`` () =
    let asm = typeof<Terminal.Core.Marker>.Assembly
    Assert.Contains("Terminal.Core", asm.FullName)

[<Property>]
let ``string concat is associative`` (a: string) (b: string) (c: string) =
    (a + b) + c = a + (b + c)
