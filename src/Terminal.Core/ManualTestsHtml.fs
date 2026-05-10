namespace Terminal.Core

open System
open System.Text
open Markdig

/// Cycle 38a-followup — runtime markdown→HTML conversion for the
/// `Diagnostics → Open Manual Tests` menu item.
///
/// **Source of truth.** [`docs/ACCESSIBILITY-TESTING.md`](../../docs/ACCESSIBILITY-TESTING.md)
/// is the canonical manual-test matrix. It's deployed next to
/// `Terminal.App.exe` via Content + CopyToOutput in
/// `Terminal.App.fsproj`; the menu handler reads it via
/// `AppContext.BaseDirectory + ACCESSIBILITY-TESTING.md`.
///
/// **DOGFOOD filter.** Many sections in the matrix are historical
/// (shipped-stage validation done long ago) and would clutter the
/// quickref. The filter convention: sub-sections preceded by an
/// `<!-- DOGFOOD -->` HTML comment are included in the quickref;
/// unmarked sub-sections are dropped. Maintainer can grow / prune
/// the quickref by adding / removing markers — no code change
/// required.
///
/// **Output HTML.** Standalone HTML5 with screen-reader-friendly
/// markup: `<main>` landmark for NVDA D-key jump; semantic
/// heading hierarchy from Markdig's `UseAutoIdentifiers` for H-key
/// jumps; `<table>` rendered from Markdown pipe tables; CSS kept
/// minimal so the page renders cleanly with NVDA browse mode.
module ManualTestsHtml =

    /// HTML-comment marker indicating a sub-section should appear
    /// in the dogfood quickref. Maintained as a constant so any
    /// future renaming touches exactly one file.
    [<Literal>]
    let DogfoodMarker = "<!-- DOGFOOD -->"

    /// Split the markdown into chunks at `### ` heading
    /// boundaries. Returns an array of `(markerFound, sectionText)`
    /// tuples. `sectionText` is the heading line + body up to
    /// (but not including) the next `### ` heading.
    /// `markerFound` is true if the `<!-- DOGFOOD -->` marker
    /// appeared in the lines BEFORE the heading (either in the
    /// previous section's body or in the file's top-matter
    /// immediately preceding the heading).
    ///
    /// Internal to allow unit-tests to pin the filter behaviour
    /// independently of Markdig conversion.
    let internal splitAndMark
            (markdown: string)
            : (bool * string)[] =
        let lines = markdown.Split([| '\n' |])
        let chunks = ResizeArray<bool * string>()
        let currentLines = ResizeArray<string>()
        let mutable markerForCurrent = false
        let mutable markerPendingForNext = false
        let mutable insideSection = false
        let flushCurrent () =
            if currentLines.Count > 0 then
                let text =
                    String.concat "\n" (currentLines |> Seq.toList)
                chunks.Add((markerForCurrent, text))
            currentLines.Clear()
        for line in lines do
            let trimmed = line.TrimStart()
            if trimmed.StartsWith("### ") then
                if insideSection then
                    flushCurrent ()
                else
                    // Pre-matter (intro before first heading) is
                    // discarded — it's not a test instruction
                    // even if the maintainer placed a marker
                    // there by accident.
                    currentLines.Clear()
                    insideSection <- true
                markerForCurrent <- markerPendingForNext
                markerPendingForNext <- false
                currentLines.Add(line)
            else
                if line.Contains(DogfoodMarker) then
                    markerPendingForNext <- true
                currentLines.Add(line)
        if insideSection then flushCurrent ()
        chunks.ToArray()

    /// HTML5 document head + body wrapper applied around the
    /// Markdig-rendered body. Minimal CSS keeps the page readable
    /// in any browser while staying out of NVDA's way.
    let private renderDocument (bodyHtml: string) : string =
        let sb = StringBuilder()
        sb.AppendLine("<!DOCTYPE html>") |> ignore
        sb.AppendLine("<html lang=\"en\">") |> ignore
        sb.AppendLine("<head>") |> ignore
        sb.AppendLine("  <meta charset=\"utf-8\">") |> ignore
        sb.AppendLine("  <title>pty-speak Manual Tests Quickref</title>") |> ignore
        sb.AppendLine("  <style>") |> ignore
        sb.AppendLine("    body { font-family: -apple-system, Segoe UI, sans-serif; max-width: 80ch; margin: 2em auto; padding: 0 1em; line-height: 1.5; }") |> ignore
        sb.AppendLine("    h1, h2, h3 { line-height: 1.2; }") |> ignore
        sb.AppendLine("    table { border-collapse: collapse; margin: 0.5em 0; }") |> ignore
        sb.AppendLine("    th, td { border: 1px solid #ccc; padding: 0.4em 0.6em; text-align: left; vertical-align: top; }") |> ignore
        sb.AppendLine("    code { background: #f4f4f4; padding: 0.1em 0.3em; }") |> ignore
        sb.AppendLine("    pre { background: #f4f4f4; padding: 0.5em; overflow-x: auto; }") |> ignore
        sb.AppendLine("    pre code { background: transparent; padding: 0; }") |> ignore
        sb.AppendLine("  </style>") |> ignore
        sb.AppendLine("</head>") |> ignore
        sb.AppendLine("<body>") |> ignore
        sb.AppendLine("  <h1>pty-speak Manual Tests Quickref</h1>") |> ignore
        sb.AppendLine("  <p><em>Generated from docs/ACCESSIBILITY-TESTING.md; filtered to sections marked <code>&lt;!-- DOGFOOD --&gt;</code>. Use NVDA browse mode H key to jump between test sections.</em></p>") |> ignore
        sb.AppendLine("  <main>") |> ignore
        sb.AppendLine(bodyHtml) |> ignore
        sb.AppendLine("  </main>") |> ignore
        sb.AppendLine("</body>") |> ignore
        sb.AppendLine("</html>") |> ignore
        sb.ToString()

    /// Build the Markdig pipeline used by `filterAndConvert`.
    /// `UseAutoIdentifiers` adds `id` attributes to headings so
    /// in-page anchors work; `UsePipeTables` handles the matrix
    /// rows formatted as `| col | col |` markdown tables.
    let private buildPipeline () : MarkdownPipeline =
        MarkdownPipelineBuilder()
            .UseAutoIdentifiers()
            .UseAutoLinks()
            .UsePipeTables()
            .Build()

    /// Filter the markdown to sections marked with the DOGFOOD
    /// comment and render the result as a standalone HTML5
    /// document. Empty input or input with no marked sections
    /// returns a valid HTML5 document with an empty `<main>`.
    let filterAndConvert (markdown: string) : string =
        let chunks = splitAndMark markdown
        let keptMarkdown =
            chunks
            |> Array.filter fst
            |> Array.map snd
            |> String.concat "\n\n"
        let pipeline = buildPipeline ()
        let bodyHtml = Markdown.ToHtml(keptMarkdown, pipeline)
        renderDocument bodyHtml
