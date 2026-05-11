namespace Terminal.Core

open System
open System.Text
open System.Text.RegularExpressions

/// Cycle 43a — pure F# grep over the in-memory diagnostic bundle.
/// Backs the `Diagnostics → Grep diagnostics...` top-level menu
/// command (`HotkeyRegistry.AppCommand.GrepDiagnostics`).
///
/// The orchestrator in `Terminal.App/Program.fs` opens a modal
/// `Views/GrepDialog.xaml` window, collects a pattern + options,
/// regenerates a lightweight bundle via
/// `Terminal.App.Diagnostics.formatLightweightBundle`, then calls
/// into this module to produce the formatted match list. The
/// orchestrator caps the output via
/// `DiagnosticExtracts.truncateForClipboard` and writes the full
/// untruncated content to an extract file as paste-fallback.
///
/// Substrate rule: this module is pure. No file I/O, no clipboard,
/// no logger, no clock. Caller passes the search-target string;
/// this module returns the formatted output string. The orchestrator
/// owns side effects.
module DiagnosticGrep =

    /// Match-mode options collected from the dialog.
    type GrepOptions =
        { /// Pattern to search for. Empty pattern produces a
          /// single-line error result rather than matching every
          /// line.
          Pattern: string
          /// Case sensitivity flag. Default: false (case-insensitive
          /// substring match is the friendliest default for users
          /// who think of grep as "find this word, however it's
          /// spelled").
          CaseSensitive: bool
          /// When true, `Pattern` is interpreted as a .NET regex.
          /// When false, it's a literal substring. Regex errors
          /// produce a single-line error result rather than
          /// throwing.
          TreatAsRegex: bool
          /// Number of context lines emitted before AND after each
          /// matched line. Clamped to [0, 20] by the orchestrator
          /// per the dialog's spinner range.
          ContextLines: int }

    let defaultOptions : GrepOptions =
        { Pattern = ""
          CaseSensitive = false
          TreatAsRegex = false
          ContextLines = 5 }

    /// Result of compiling a pattern. Encapsulates the per-line
    /// match predicate so the actual matching loop stays simple.
    type private CompiledMatcher =
        /// Pattern compiled successfully; carries the predicate.
        | Compiled of (string -> bool)
        /// Pattern was empty.
        | EmptyPattern
        /// Regex compile failed with the given message.
        | RegexError of string

    let private compile (opts: GrepOptions) : CompiledMatcher =
        if String.IsNullOrEmpty opts.Pattern then EmptyPattern
        elif opts.TreatAsRegex then
            try
                let regexOpts =
                    if opts.CaseSensitive then RegexOptions.None
                    else RegexOptions.IgnoreCase
                let rx =
                    Regex(
                        opts.Pattern,
                        regexOpts ||| RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1.0))
                Compiled (fun line -> rx.IsMatch(line))
            with
            | :? RegexParseException as ex -> RegexError ex.Message
            | :? ArgumentException as ex -> RegexError ex.Message
        else
            let comparison =
                if opts.CaseSensitive then StringComparison.Ordinal
                else StringComparison.OrdinalIgnoreCase
            let pattern = opts.Pattern
            Compiled (fun line -> line.IndexOf(pattern, comparison) >= 0)

    /// Count matches without producing the full output. Used by
    /// the orchestrator for the NVDA pre-announce ("Found N
    /// matches.") so the user knows the result size before the
    /// clipboard copy completes.
    let countMatches (opts: GrepOptions) (source: string) : int =
        match compile opts with
        | EmptyPattern -> 0
        | RegexError _ -> 0
        | Compiled predicate ->
            if String.IsNullOrEmpty source then 0
            else
                let lines = source.Split([| '\n' |], StringSplitOptions.None)
                let mutable count = 0
                for rawLine in lines do
                    let line = rawLine.TrimEnd('\r')
                    if predicate line then count <- count + 1
                count

    /// Format the full grep output: a per-match block carrying
    /// context lines and the matched line marked with a `>>>`
    /// prefix, followed by a one-line summary footer.
    ///
    /// Output shape (stable; future tooling can index by header
    /// format):
    /// ```
    /// pty-speak grep — pattern: <pattern> (regex=<bool>, case=<bool>)
    /// Source size: <bytes> bytes (<lines> lines)
    /// =========================================================
    ///
    /// --- Match 1 of 12 (line 42) ---
    /// line 40
    /// line 41
    /// >>> line 42 with pattern hit
    /// line 43
    /// line 44
    ///
    /// --- Match 2 of 12 (line 48) ---
    /// ...
    ///
    /// --- Summary: 12 matches; <N> bytes of output ---
    /// ```
    ///
    /// Special cases:
    /// - Empty pattern → `pty-speak grep — empty pattern` banner.
    /// - Regex error → `pty-speak grep — regex error: <msg>` banner.
    /// - Zero matches → banner + `No matches for <pattern>.` line.
    let formatGrep (opts: GrepOptions) (source: string) : string =
        let sb = StringBuilder()
        let separator = "========================================================="
        let appendLine (s: string) = sb.AppendLine(s) |> ignore

        // Banner — always present so the consumer can read the
        // search parameters back without scrolling.
        appendLine
            (sprintf
                "pty-speak grep — pattern: %s (regex=%b, case=%b, context=%d)"
                opts.Pattern
                opts.TreatAsRegex
                opts.CaseSensitive
                opts.ContextLines)

        // Compile + validate before computing source statistics so
        // an empty / invalid pattern returns quickly without
        // scanning the source.
        match compile opts with
        | EmptyPattern ->
            appendLine separator
            appendLine ""
            appendLine "(empty pattern — nothing to match)"
            sb.ToString()
        | RegexError msg ->
            appendLine separator
            appendLine ""
            appendLine (sprintf "(regex error: %s)" msg)
            sb.ToString()
        | Compiled predicate ->
            let sourceBytes = Encoding.UTF8.GetByteCount(source)
            let lines =
                if String.IsNullOrEmpty source then [||]
                else source.Split([| '\n' |], StringSplitOptions.None)
            appendLine
                (sprintf
                    "Source size: %d bytes (%d lines)"
                    sourceBytes
                    lines.Length)
            appendLine separator
            appendLine ""

            // Scan once, collect matching indexes.
            let matchIndexes = ResizeArray<int>()
            for i in 0 .. lines.Length - 1 do
                let line = lines.[i].TrimEnd('\r')
                if predicate line then matchIndexes.Add(i)

            if matchIndexes.Count = 0 then
                appendLine (sprintf "No matches for %s." opts.Pattern)
                sb.ToString()
            else
                let total = matchIndexes.Count
                let ctx = max 0 opts.ContextLines

                // Emit one block per match. Adjacent matches will
                // produce overlapping context windows — that's
                // intentional: each match is independently labelled
                // with its line number, which is the field the
                // consumer reads to navigate. Deduplicating the
                // body would obscure that.
                for matchOrdinal in 0 .. total - 1 do
                    let idx = matchIndexes.[matchOrdinal]
                    let lineNum = idx + 1
                    appendLine
                        (sprintf
                            "--- Match %d of %d (line %d) ---"
                            (matchOrdinal + 1)
                            total
                            lineNum)
                    let startIdx = max 0 (idx - ctx)
                    let endIdx = min (lines.Length - 1) (idx + ctx)
                    for k in startIdx .. endIdx do
                        let line = lines.[k].TrimEnd('\r')
                        if k = idx then
                            appendLine (sprintf ">>> %s" line)
                        else
                            appendLine line
                    appendLine ""

                appendLine
                    (sprintf
                        "--- Summary: %d matches; pattern=%s ---"
                        total
                        opts.Pattern)
                sb.ToString()
