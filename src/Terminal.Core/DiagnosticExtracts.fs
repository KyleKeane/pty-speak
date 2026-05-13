namespace Terminal.Core

open System
open System.Globalization
open System.IO
open System.Text

/// Cycle 43a — pure helpers backing the new "Diagnostics → Extract"
/// submenu and the "Diagnostics → Grep diagnostics..." dialog.
///
/// Background: pty-speak's existing `Ctrl+Shift+D` diagnostic bundle
/// is comprehensive but routinely exceeds the paste-back limits of
/// chat clients used in triage workflows (the Cycle 29b NVDA test
/// produced a multi-megabyte bundle that crashed the maintainer's
/// iOS chat app on paste). CLAUDE.md codifies "request chunks, not
/// full bundles" but until Cycle 43 the app exposed no way for a
/// screen-reader user to actually produce chunks — every triage
/// required shell-quoting `findstr` from cmd or `Select-String` from
/// PowerShell, both of which are friction for keyboard-only usage.
///
/// This module is the pure F# substrate the new menu items call
/// into. Each function is deterministic, testable in isolation, and
/// avoids any WPF / logging / clipboard dependencies — those live at
/// the orchestration layer in `Terminal.App/Program.fs`.
///
/// Outputs from these helpers are sized for the 60 KB clipboard
/// ceiling (`clipboardSafetyCeilingBytes`); orchestrators are
/// responsible for calling `truncateForClipboard` before clipboard
/// hand-off, and for falling back to an on-disk extract file when
/// truncation fires.
module DiagnosticExtracts =

    /// Conservative clipboard ceiling. The Cycle 29b iOS-paste-crash
    /// incident motivated the 64 KB cap on the bundle's
    /// `--- LINEAR STREAM ---` section; the same ceiling applies
    /// here. 60 KB leaves headroom for an `[... truncated ...]`
    /// footer plus any user-visible framing the orchestrator adds.
    let clipboardSafetyCeilingBytes : int = 60 * 1024

    /// Root folder for on-disk extract files. Created on demand by
    /// `ensureExtractsRoot`. Lives alongside `logs\` and
    /// `diagnostic-snapshots\` under `%LOCALAPPDATA%\PtySpeak\`.
    let extractsRoot () : string =
        let local =
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData)
        Path.Combine(local, "PtySpeak", "extracts")

    /// Ensure the extracts root exists. Returns the path.
    /// Best-effort — a permissions failure or disk-full surfaces as
    /// the exception from `Directory.CreateDirectory`, propagating
    /// to the caller for a single "could not write extract file"
    /// announce rather than a silent failure.
    let ensureExtractsRoot () : string =
        let root = extractsRoot ()
        Directory.CreateDirectory(root) |> ignore
        root

    /// Reduce an arbitrary user-supplied string (e.g. a grep
    /// pattern) to a filename-safe slug. Alphanumeric + dashes
    /// only; consecutive non-alnum runs collapse to a single dash;
    /// leading/trailing dashes trimmed; truncated to `maxLen`
    /// characters; falls back to `"untitled"` when the input
    /// produces an empty slug.
    let slugifyForFilename (maxLen: int) (input: string) : string =
        if String.IsNullOrEmpty input then "untitled"
        else
            let sb = StringBuilder(input.Length)
            let mutable lastWasDash = false
            for ch in input do
                if Char.IsLetterOrDigit(ch) then
                    sb.Append(Char.ToLowerInvariant(ch)) |> ignore
                    lastWasDash <- false
                elif not lastWasDash && sb.Length > 0 then
                    sb.Append('-') |> ignore
                    lastWasDash <- true
            let raw = sb.ToString().Trim('-')
            let capped =
                if raw.Length > maxLen then raw.Substring(0, maxLen)
                else raw
            if String.IsNullOrEmpty capped then "untitled" else capped

    /// Build the on-disk path for an extract file. Format:
    /// `<extractsRoot>\<extractorName>-<timestamp>.txt`.
    /// `extractorName` is slugified; `timestamp` uses the same
    /// `yyyy-MM-dd-HH-mm-ss-fff` shape as `FileLogger.pathsForLaunch`
    /// and `Diagnostics.resolveSnapshotPath`, so a sort-by-name in
    /// the extracts folder is also a sort-by-time.
    let extractFilePath
            (now: DateTimeOffset)
            (extractorName: string)
            : string =
        let slug = slugifyForFilename 40 extractorName
        let stamp = now.UtcDateTime.ToString("yyyy-MM-dd-HH-mm-ss-fff")
        let fileName = sprintf "%s-%s.txt" slug stamp
        Path.Combine(extractsRoot (), fileName)

    /// Read an entire file's content while tolerating concurrent
    /// writers (the active FileLogger log is the canonical case —
    /// pty-speak is still writing as we read). Returns `None` if
    /// the file is missing; throws on any other IO error so the
    /// orchestrator can surface a single "could not read X" message
    /// rather than silently returning empty.
    let private readFileShared (path: string) : string option =
        if not (File.Exists path) then None
        else
            use stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite)
            use reader = new StreamReader(stream, Encoding.UTF8)
            Some (reader.ReadToEnd())

    /// Split a UTF-8 text body into lines, stripping the single
    /// trailing empty element that `String.Split('\n')` produces
    /// when the source ends with a newline. Keeps interior blank
    /// lines (a blank line BETWEEN log entries is content; the
    /// trailing empty AFTER the final newline is not). Centralised
    /// here so `tailLogLines`, `filterLogLinesSince`, and
    /// `filterLogBySemantic` share the same line-count semantics.
    let private splitLinesStripTrailing (content: string) : string[] =
        let lines = content.Split([| '\n' |], StringSplitOptions.None)
        if lines.Length > 0 && lines.[lines.Length - 1] = "" then
            lines |> Array.take (lines.Length - 1)
        else
            lines

    /// Read the last `n` lines of a UTF-8 text file. Returns the
    /// joined lines (without a trailing newline) plus a header note
    /// indicating the source path and the line count actually
    /// returned. Returns a placeholder string if the file is
    /// missing rather than throwing — for diagnostic surfacing the
    /// caller should always get a renderable result.
    ///
    /// Implementation reads the entire file into memory then takes
    /// the tail. For pty-speak's bounded log sizes (a few MB per
    /// daily file) this is O(file-size) but simple and avoids
    /// reverse-stream tricks. If log files ever grow large enough
    /// to make this measurably slow, swap in a reverse-line reader;
    /// the public signature is stable.
    let tailLogLines (path: string) (n: int) : string =
        match readFileShared path with
        | None ->
            sprintf "(file not present: %s)" path
        | Some content ->
            // Strip the trailing empty BEFORE computing the tail
            // window so `lineCount` reflects real-line count, not
            // the Split-artifact count. Without this strip the
            // window includes the trailing-empty slot, eats one
            // real line, and returns N-1 lines instead of N.
            let lines = splitLinesStripTrailing content
            let lineCount = lines.Length
            let takeCount = if n < 0 then 0 else min n lineCount
            let startIdx = lineCount - takeCount
            let tail =
                if startIdx <= 0 then lines
                else lines |> Array.skip startIdx
            // Strip embedded \r so paste targets render cleanly on
            // platforms that surface CR as a visible glyph.
            let cleaned =
                tail
                |> Array.map (fun line -> line.TrimEnd('\r'))
            String.Join("\n", cleaned)

    /// Try to parse the leading ISO-8601 timestamp emitted by
    /// `FileLogger.formatEntry`: `yyyy-MM-ddTHH:mm:ss.fffZ`. Returns
    /// `None` for lines that don't carry a parseable timestamp
    /// (continuation lines from a multi-line stack trace, blank
    /// lines, etc.) — caller-policy whether to retain or drop those.
    let parseLogTimestamp (line: string) : DateTimeOffset option =
        if String.IsNullOrEmpty line || line.Length < 24 then None
        else
            let prefix = line.Substring(0, 24)
            let mutable result = DateTimeOffset.MinValue
            let ok =
                DateTimeOffset.TryParseExact(
                    prefix,
                    "yyyy-MM-ddTHH:mm:ss.fffZ",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        ||| DateTimeStyles.AdjustToUniversal,
                    &result)
            if ok then Some result else None

    /// Filter log lines whose timestamp is at or after `cutoff`.
    /// Lines without a parseable timestamp (continuation lines of
    /// multi-line entries) are retained when they immediately
    /// follow a kept timestamped line, so stack traces stay
    /// attached to their parent entry. Lines without a timestamp
    /// that appear at the start of the file are dropped (no
    /// parent to attach to).
    let filterLogLinesSince
            (path: string)
            (cutoff: DateTimeOffset)
            : string =
        match readFileShared path with
        | None ->
            sprintf "(file not present: %s)" path
        | Some content ->
            let lines = splitLinesStripTrailing content
            let kept = ResizeArray<string>()
            let mutable retainContinuations = false
            for rawLine in lines do
                let line = rawLine.TrimEnd('\r')
                match parseLogTimestamp line with
                | Some ts when ts >= cutoff ->
                    kept.Add(line)
                    retainContinuations <- true
                | Some _ ->
                    retainContinuations <- false
                | None ->
                    if retainContinuations then kept.Add(line)
            String.Join("\n", kept)

    /// Filter log lines containing any of the supplied
    /// `Semantic=<token>` substrings. Matching is case-sensitive
    /// (the producer emits the SemanticCategory case names in their
    /// canonical casing — `Semantic=ErrorLine`, not
    /// `Semantic=errorline`). Continuation lines of multi-line
    /// entries (stack traces) are kept when they immediately
    /// follow a kept timestamped line, mirroring
    /// `filterLogLinesSince`'s policy.
    let filterLogBySemantic
            (path: string)
            (semanticTokens: string list)
            : string =
        if List.isEmpty semanticTokens then ""
        else
            match readFileShared path with
            | None ->
                sprintf "(file not present: %s)" path
            | Some content ->
                let needles =
                    semanticTokens
                    |> List.map (fun t -> sprintf "Semantic=%s" t)
                let lineMatches (line: string) : bool =
                    needles
                    |> List.exists (fun n -> line.Contains(n))
                let lines = splitLinesStripTrailing content
                let kept = ResizeArray<string>()
                let mutable retainContinuations = false
                for rawLine in lines do
                    let line = rawLine.TrimEnd('\r')
                    match parseLogTimestamp line with
                    | Some _ ->
                        if lineMatches line then
                            kept.Add(line)
                            retainContinuations <- true
                        else
                            retainContinuations <- false
                    | None ->
                        if retainContinuations then kept.Add(line)
                String.Join("\n", kept)

    /// Cycle 47 follow-up (2026-05-13) — extract the body of
    /// every `=== PTYSPEAK-TEST-START: <id> ===` /
    /// `=== PTYSPEAK-TEST-END: <id> ===` bracketed region in
    /// `content`. The bracket markers are emitted by each
    /// `scripts/cmd-tests/test-*.cmd` script (Cycle 47); when
    /// those scripts run during dogfooding the markers land in
    /// the ContentHistory tail and the FileLogger log. Greppable
    /// after the fact, but the maintainer requested a one-press
    /// extractor that surfaces just the bracketed slice so a
    /// paste-back to triage chat carries only the relevant
    /// section and not the surrounding session noise.
    ///
    /// Multiple runs of the same test in one session each emit
    /// their own bracket pair; all are returned, separated by a
    /// "--- run N of M ---" divider so the paste-back consumer
    /// can tell them apart. An empty body case (start marker
    /// without matching end) is included for visibility — useful
    /// when the script is still mid-run at extract-time. A test
    /// that didn't run yet returns a one-line "no runs found"
    /// placeholder so the announce is informative.
    ///
    /// Implementation: line-by-line scan with a tiny state
    /// machine (`outsideBracket` / `insideBracket`). Each
    /// matched start marker opens an accumulator; the matching
    /// end marker closes it. Bracket markers are NOT included
    /// in the extracted body (the test slug appears in the
    /// header instead). Marker matching is exact-string against
    /// the canonical `=== PTYSPEAK-TEST-START: <id> ===` form
    /// trimmed of leading / trailing whitespace; trailing
    /// `\r` is stripped first.
    let extractByTest (testId: string) (content: string) : string =
        if String.IsNullOrEmpty content then
            sprintf "(no runs of %s found in the bundle)" testId
        else
            let startMarker =
                sprintf "=== PTYSPEAK-TEST-START: %s ===" testId
            let endMarker =
                sprintf "=== PTYSPEAK-TEST-END: %s ===" testId
            let lines = splitLinesStripTrailing content
            let runs = ResizeArray<string>()
            let mutable current : StringBuilder option = None
            for rawLine in lines do
                let line = rawLine.TrimEnd('\r')
                let trimmed = line.Trim()
                match current with
                | None ->
                    if trimmed = startMarker then
                        current <- Some (StringBuilder())
                | Some sb ->
                    if trimmed = endMarker then
                        runs.Add(sb.ToString().TrimEnd('\n'))
                        current <- None
                    elif trimmed = startMarker then
                        // Nested / unterminated previous run.
                        // Close the previous one as in-flight,
                        // open a fresh accumulator.
                        runs.Add(
                            sb.ToString().TrimEnd('\n')
                            + "\n[... run unterminated at start of next run ...]")
                        current <- Some (StringBuilder())
                    else
                        sb.AppendLine(line) |> ignore
            // Tail case: extractor pressed mid-run.
            match current with
            | Some sb ->
                runs.Add(
                    sb.ToString().TrimEnd('\n')
                    + "\n[... run still in flight at extract time ...]")
            | None -> ()
            if runs.Count = 0 then
                sprintf "(no runs of %s found in the bundle)" testId
            else
                let total = runs.Count
                let sb = StringBuilder()
                for i in 0 .. total - 1 do
                    if total > 1 then
                        sb.AppendLine(
                            sprintf "--- %s run %d of %d ---" testId (i + 1) total)
                        |> ignore
                    sb.AppendLine(runs.[i]) |> ignore
                    if i < total - 1 then sb.AppendLine("") |> ignore
                sb.ToString().TrimEnd('\n')

    /// Cap `content` at `maxBytes` UTF-8 bytes. Returns the
    /// (possibly-truncated) content and a `wasTruncated` flag the
    /// orchestrator uses to decide whether to surface a truncation
    /// notice in the NVDA announce. Truncation happens at a UTF-8
    /// byte boundary (no partial code points) and appends a single-
    /// line footer `[... truncated at <N> bytes; full results in
    /// extract file ...]` so paste-back consumers know they're
    /// looking at a head-only view.
    ///
    /// Implementation: encode the entire content to UTF-8 once,
    /// scan for the largest prefix whose byte count fits the budget
    /// minus the footer length, then decode that prefix back. This
    /// is O(content-size) but simple and correct.
    let truncateForClipboard
            (maxBytes: int)
            (content: string)
            : string * bool =
        let encoding = Encoding.UTF8
        let totalBytes = encoding.GetByteCount(content)
        if totalBytes <= maxBytes then content, false
        else
            let footer =
                sprintf
                    "\n[... truncated at %d bytes; full results in extract file ...]"
                    maxBytes
            let footerBytes = encoding.GetByteCount(footer)
            let budget = maxBytes - footerBytes
            if budget <= 0 then "", true
            else
                // Find the largest substring whose UTF-8 encoding
                // fits the budget. Char-by-char scan from the
                // start avoids the surrogate-pair-split problem
                // that a naive byte-substring would hit.
                let sb = StringBuilder(content.Length)
                let mutable running = 0
                let mutable idx = 0
                let mutable stopped = false
                while not stopped && idx < content.Length do
                    let ch = content.[idx]
                    let segLen =
                        if Char.IsHighSurrogate(ch)
                           && idx + 1 < content.Length
                           && Char.IsLowSurrogate(content.[idx + 1]) then
                            encoding.GetByteCount(content, idx, 2)
                        else
                            encoding.GetByteCount(content, idx, 1)
                    if running + segLen > budget then
                        stopped <- true
                    else
                        if Char.IsHighSurrogate(ch)
                           && idx + 1 < content.Length
                           && Char.IsLowSurrogate(content.[idx + 1]) then
                            sb.Append(ch) |> ignore
                            sb.Append(content.[idx + 1]) |> ignore
                            idx <- idx + 2
                        else
                            sb.Append(ch) |> ignore
                            idx <- idx + 1
                        running <- running + segLen
                sb.Append(footer) |> ignore
                sb.ToString(), true

    /// Compose the standard extract-file header. Format:
    /// ```
    /// pty-speak extract — <extractorName>
    /// Captured: <timestamp> UTC
    /// Source: <description>
    /// Size: <bytes> bytes (<lineCount> lines)
    /// =========================================================
    /// ```
    /// Used as the prefix for every extract file. Stable shape so
    /// future tooling can parse extract files by header (mirrors
    /// the `formatDiagnosticBundle` separator convention).
    let formatExtractHeader
            (now: DateTimeOffset)
            (extractorName: string)
            (sourceDescription: string)
            (body: string)
            : string =
        let separator = "========================================================="
        let bytes = Encoding.UTF8.GetByteCount(body)
        let lineCount =
            if String.IsNullOrEmpty body then 0
            else
                let mutable count = 1
                for ch in body do
                    if ch = '\n' then count <- count + 1
                count
        let sb = StringBuilder()
        sb.AppendLine(sprintf "pty-speak extract — %s" extractorName) |> ignore
        sb.AppendLine(
            sprintf
                "Captured: %s UTC"
                (now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))) |> ignore
        sb.AppendLine(sprintf "Source: %s" sourceDescription) |> ignore
        sb.AppendLine(
            sprintf "Size: %d bytes (%d lines)" bytes lineCount) |> ignore
        sb.AppendLine(separator) |> ignore
        sb.AppendLine("") |> ignore
        sb.Append(body) |> ignore
        sb.ToString()

    /// Write `content` to `path`, creating parent directories as
    /// needed. Best-effort — exceptions propagate so the
    /// orchestrator can surface a single "could not write extract
    /// file" announce.
    ///
    /// F# 9 nullness: `Path.GetDirectoryName` returns
    /// `string | null` (null for root paths or empty input).
    /// Pattern-match on the result rather than `if not (isNull …)`
    /// so the narrowing is explicit per `Diagnostics.writeSnapshotFile`'s
    /// convention (`Diagnostics.fs:970-974`).
    let writeExtractFile (path: string) (content: string) : unit =
        match Path.GetDirectoryName(path) with
        | null -> ()
        | "" -> ()
        | dir -> Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(path, content, Encoding.UTF8)

    /// Format a size-bytes value as a human-readable string. Used
    /// in NVDA announces ("3.4 kilobytes"). Mirrors NVDA's own
    /// pronunciation conventions: "bytes" / "kilobytes" /
    /// "megabytes"; no abbreviations (NVDA would spell "KB" as
    /// "kay bee" which is not what we want).
    let formatBytesForAnnounce (bytes: int) : string =
        if bytes < 1024 then sprintf "%d bytes" bytes
        elif bytes < 1024 * 1024 then
            sprintf "%.1f kilobytes" (float bytes / 1024.0)
        else
            sprintf "%.1f megabytes" (float bytes / 1024.0 / 1024.0)
