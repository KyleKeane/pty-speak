module PtySpeak.Tests.Unit.ManualTestsHtmlTests

open Xunit
open Terminal.Core

/// Cycle 38a-followup — pins the ManualTestsHtml filter +
/// conversion contract. Tests use inline markdown strings rather
/// than the real `ACCESSIBILITY-TESTING.md` fixture so they stay
/// stable as that file grows.

// =====================================================================
// splitAndMark — DOGFOOD filter behaviour
// =====================================================================

[<Fact>]
let ``splitAndMark keeps sections preceded by DOGFOOD marker`` () =
    let md =
        "# Title\n\nintro\n\n<!-- DOGFOOD -->\n### Marked Section\nbody A\n"
    let chunks = ManualTestsHtml.splitAndMark md
    Assert.Equal(1, chunks.Length)
    let (markerFound, text) = chunks.[0]
    Assert.True(markerFound)
    Assert.Contains("### Marked Section", text)
    Assert.Contains("body A", text)

[<Fact>]
let ``splitAndMark drops sections without preceding DOGFOOD marker`` () =
    let md =
        "# Title\n\nintro\n\n### Unmarked\nbody A\n\n<!-- DOGFOOD -->\n### Marked\nbody B\n"
    let chunks = ManualTestsHtml.splitAndMark md
    Assert.Equal(2, chunks.Length)
    let (firstMarker, firstText) = chunks.[0]
    let (secondMarker, secondText) = chunks.[1]
    Assert.False(firstMarker)
    Assert.Contains("### Unmarked", firstText)
    Assert.True(secondMarker)
    Assert.Contains("### Marked", secondText)

[<Fact>]
let ``splitAndMark resets the marker between sections so it does not bleed forward`` () =
    let md =
        "<!-- DOGFOOD -->\n### First\nbody A\n\n### Second\nbody B\n"
    let chunks = ManualTestsHtml.splitAndMark md
    Assert.Equal(2, chunks.Length)
    let (firstMarker, _) = chunks.[0]
    let (secondMarker, _) = chunks.[1]
    Assert.True(firstMarker)
    Assert.False(secondMarker)

[<Fact>]
let ``splitAndMark returns empty array for markdown with no level-3 headings`` () =
    let md = "# Title\n\nSome intro text with no sub-sections.\n"
    let chunks = ManualTestsHtml.splitAndMark md
    Assert.Empty(chunks)

// =====================================================================
// filterAndConvert — end-to-end (filter + Markdig conversion)
// =====================================================================

[<Fact>]
let ``filterAndConvert produces a complete HTML5 document with main landmark`` () =
    let md = "<!-- DOGFOOD -->\n### Test\nbody\n"
    let html = ManualTestsHtml.filterAndConvert md
    Assert.StartsWith("<!DOCTYPE html>", html)
    Assert.Contains("<html lang=\"en\">", html)
    Assert.Contains("<main>", html)
    Assert.Contains("</main>", html)
    Assert.Contains("</html>", html)

[<Fact>]
let ``filterAndConvert includes content from marked section but not unmarked`` () =
    let md =
        "<!-- DOGFOOD -->\n### Dogfood Section\nbody KEEP\n\n### Hidden Section\nbody SKIP\n"
    let html = ManualTestsHtml.filterAndConvert md
    Assert.Contains("Dogfood Section", html)
    Assert.Contains("KEEP", html)
    Assert.DoesNotContain("Hidden Section", html)
    Assert.DoesNotContain("SKIP", html)

[<Fact>]
let ``filterAndConvert renders pipe tables as HTML tables`` () =
    let md =
        "<!-- DOGFOOD -->\n### Table Section\n\n| col1 | col2 |\n| --- | --- |\n| a | b |\n"
    let html = ManualTestsHtml.filterAndConvert md
    Assert.Contains("<table>", html)
    Assert.Contains("<th>col1</th>", html)
    Assert.Contains("<td>a</td>", html)

[<Fact>]
let ``filterAndConvert on empty input returns a valid HTML5 shell with empty main`` () =
    let html = ManualTestsHtml.filterAndConvert ""
    Assert.StartsWith("<!DOCTYPE html>", html)
    Assert.Contains("<main>", html)
    Assert.Contains("</main>", html)
    Assert.Contains("</html>", html)
