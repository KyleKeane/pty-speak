namespace Terminal.Core

/// Cycle 45f — per-shell policy table.
///
/// Single typed record per shell carrying the knobs that govern
/// how pty-speak narrates output, prompts, and (in future cycles)
/// detection thresholds for that shell. Three Cycle 45f knobs are
/// active today (`Streaming`, `PromptPath`), plus three seats
/// reserved for the Cycle 45g consolidation refactor (PromptRegex,
/// PromptStabilityMs, SelectionEnabled — these mirror
/// `HeuristicPromptDetector.fs:184` and `SelectionDetector.fs`
/// inline match arms that Cycle 45g will collapse into a single
/// table lookup).
///
/// The policy is resolved at shell-switch time in
/// `Terminal.App.Program.switchToShell` through a three-layer
/// model:
///
///   1. Compiled defaults (this module's `defaults` map)
///   2. TOML user config (`Config.toml [shell.<id>]` keys)
///   3. Runtime overrides (a `mutable Map<string, T>` populated
///      by the `View → Output Verbosity` / `Prompt Path` menus)
///
/// Later layers overlay earlier ones. Restarting the app drops
/// Layer 3 and re-reads Layer 2 → effective per-shell defaults.
///
/// See `docs/USER-SETTINGS.md` "Verbosity" section + the Cycle
/// 45f plan in `/root/.claude/plans/the-repo-linked-to-ticklish-whisper.md`
/// for the full architectural framing.
module ShellPolicy =

    /// How streaming output reaches NVDA. Per-shell.
    ///
    /// `TupleFinalOnly` (default for cmd / PowerShell) suppresses
    /// per-`TextSpan` auto-announce during streaming and emits a
    /// single `IOCell.OutputText` announce when the tuple
    /// seals. Matches the Cycle 45 fixup (PR #268) behaviour that
    /// solved cmd's edit-conflation regression — the screen-grid-
    /// derived OutputText is authoritative.
    ///
    /// `LineByLine` (intended for streaming-heavy shells like
    /// Claude or `ping -t`) announces each `TextSpan` as it seals.
    /// Best paired with shells whose output is naturally newline-
    /// delimited and whose command line doesn't reflow on edit.
    /// Opting cmd in to `LineByLine` re-introduces the
    /// edit-conflation regression — the value is exposed but
    /// docstring + TOML comments carry the warning.
    ///
    /// `Off` suppresses every TextSpan auto-announce AND the
    /// tuple-finalise announce. SpeechCursor's manual navigation
    /// (Next / Previous / JumpToLatest) is the only way to hear
    /// output. Useful for users who want maximum quiet + full
    /// control over what's spoken.
    type StreamingMode =
        | TupleFinalOnly
        | LineByLine
        | Off

    /// How the shell's prompt text is narrated when a PromptStart
    /// marker fires. Per-shell.
    ///
    /// `Suppress` (default for every shell) returns `None` from
    /// `SpeechCursor.renderEntry` on PromptStart — silence on
    /// every prompt boundary. The user knows where they are.
    ///
    /// `FinalDirOnly` trims a path-like prompt to the last
    /// directory segment + trailing delimiter (e.g.
    /// `"C:\Users\Kyle\AppData\Local\>"` becomes `"Local>"`).
    /// Cuts the verbose path noise without losing context.
    ///
    /// `Full` narrates the prompt verbatim. For shells with short
    /// prompts (`claude>`, `>>>`) or users who genuinely want the
    /// full path.
    ///
    /// `FullOnChangeElseFinal` (Cycle 52 R6b) — context-aware:
    /// when the prompt text *differs* from the previously-narrated
    /// prompt (a `cd` / dir-changing command, or the first prompt
    /// after a shell-switch) it narrates the **full** path; when
    /// the prompt is **unchanged** (running several commands in the
    /// same directory) it narrates only the **final dir** segment.
    /// Gives directory orientation on a change without repeating
    /// the whole path on every command. The "changed?" decision is
    /// stateful (it needs the prior prompt) so it is resolved by
    /// `SpeechCursor` before the pure `trimPromptPath` call — see
    /// `SpeechCursor.effectivePromptPath`. `trimPromptPath`'s own
    /// arm for this case is a context-free fallback (= `Full`) for
    /// any direct caller that does not do change-resolution.
    type PromptPathMode =
        | Suppress
        | FinalDirOnly
        | Full
        | FullOnChangeElseFinal

    /// Per-shell policy record. Future-extension seats included
    /// from day one so Cycle 45g's consolidation refactor doesn't
    /// need to extend the type.
    type T =
        { ShellKey: string
          Streaming: StreamingMode
          PromptPath: PromptPathMode
          /// Cycle 47 follow-up (2026-05-13) — idle-flush
          /// threshold. When `Some N`, an idle-flush timer
          /// fires `Announce(text, ActivityIds.output)` if the
          /// parser has been idle for at least N ms AND
          /// `ContentHistory` has unannounced content past the
          /// last-announced watermark. Solves the
          /// "intra-script `set /p` prompt doesn't speak until
          /// after the user has typed" problem: the boundary
          /// handler only fires `Announce` at `PromptStart`
          /// (shell-prompt boundary), but a `set /p` prompt
          /// inside a script doesn't emit `PromptStart` — cmd
          /// just stops writing and waits. The idle-flush fills
          /// that gap.
          ///
          /// `None` disables idle-flush — useful for shells like
          /// Claude that already stream per-token frequently
          /// enough that the threshold rarely triggers AND any
          /// flush would partially overlap a per-token
          /// `LineByLine` announce. 350 ms is the default for
          /// cmd / PowerShell — short enough to feel responsive
          /// during a `set /p` pause, long enough to avoid
          /// firing between rapid output chunks of a normal
          /// command (`dir`'s line emission rate is well under
          /// 350 ms per line).
          IdleFlushMs: int option
          // -- Cycle 45g consolidation seats; unread in 45f --
          /// Regex string for `HeuristicPromptDetector` to match
          /// against the cursor row when detecting prompt
          /// boundaries. `None` = use the detector's hardcoded
          /// default.
          PromptRegex: string option
          /// Milliseconds of unchanged cursor-row content before
          /// the detector considers a prompt "stable" and fires
          /// PromptStart. `None` = use the detector's hardcoded
          /// default.
          PromptStabilityMs: int option
          /// When true, `SelectionDetector` runs against this
          /// shell's output (currently only `claude`). When false,
          /// the detector short-circuits.
          SelectionEnabled: bool }

    /// Compiled default policy table. Each row matches today's
    /// hardcoded behaviour exactly — no regression on `cmd` /
    /// `powershell` / `claude` when the table replaces the
    /// inline shell matches in Cycle 45g.
    let defaults : Map<string, T> =
        Map.ofList
            [ "cmd",
              { ShellKey = "cmd"
                Streaming = TupleFinalOnly
                PromptPath = Suppress
                IdleFlushMs = Some 350
                PromptRegex = Some @"^[A-Za-z]:\\.*>\s?$"
                PromptStabilityMs = Some 150
                SelectionEnabled = false }
              "powershell",
              { ShellKey = "powershell"
                Streaming = TupleFinalOnly
                PromptPath = Suppress
                IdleFlushMs = Some 350
                PromptRegex = Some @"^PS [A-Za-z]:\\.*>\s?$"
                PromptStabilityMs = Some 200
                SelectionEnabled = false }
              "claude",
              { ShellKey = "claude"
                Streaming = TupleFinalOnly
                PromptPath = Suppress
                IdleFlushMs = None
                PromptRegex = None
                PromptStabilityMs = None
                SelectionEnabled = true } ]

    /// Look up the policy for a shell key. Unknown shells fall
    /// back to `cmd` so a typo or future-shell-not-yet-tabled
    /// never returns a degenerate record.
    let forShell (shellKey: string) : T =
        match Map.tryFind shellKey defaults with
        | Some policy -> policy
        | None ->
            // Synthesise a policy keyed to the requested shell
            // so consumers see the actual `ShellKey` even though
            // every field defaults to the cmd profile.
            { defaults.["cmd"] with ShellKey = shellKey }

    /// Trim a prompt-text payload to the format dictated by the
    /// `PromptPathMode`. Returns `None` to suppress the announce
    /// entirely; returns `Some text` to announce that text.
    ///
    /// Empty / whitespace input always returns `None` regardless
    /// of mode (the heuristic detector occasionally captures a
    /// blank cursor row).
    ///
    /// `FinalDirOnly` walks the input right-to-left:
    ///   1. Strip a trailing whitespace
    ///   2. Identify the trailing delimiter (`>`, `$`, `#`, `:`)
    ///      if any — preserved on the output
    ///   3. Strip trailing path separators (`\`, `/`) before the
    ///      directory name
    ///   4. Walk back to the previous path separator
    ///   5. Return the substring after that separator + the
    ///      delimiter
    ///
    /// If no path separator is found, returns the input verbatim
    /// (already short).
    let trimPromptPath
            (mode: PromptPathMode)
            (text: string)
            : string option
            =
        if System.String.IsNullOrWhiteSpace text then None
        else
            match mode with
            | Suppress -> None
            // Context-free fallback: the change-aware resolution
            // (Full when the prompt changed, FinalDirOnly when
            // unchanged) is `SpeechCursor.effectivePromptPath`'s
            // job — it resolves this case to `Full`/`FinalDirOnly`
            // *before* calling here. A direct caller (a test, a
            // future non-SpeechCursor path) that passes this case
            // through gets `Full` — verbatim, never garbage.
            | Full
            | FullOnChangeElseFinal -> Some text
            | FinalDirOnly ->
                let trimmed = text.TrimEnd()
                // Identify trailing delimiter character (if any).
                let delim =
                    let last = trimmed.[trimmed.Length - 1]
                    if last = '>' || last = '$' || last = '#' || last = ':' then
                        Some last
                    else
                        None
                // Strip the delimiter (if matched) from the path
                // body so the path-walk operates on directory text.
                let body =
                    match delim with
                    | Some _ -> trimmed.Substring(0, trimmed.Length - 1)
                    | None -> trimmed
                // Strip trailing path separators so we don't land
                // on an empty final segment.
                let bodyTrimmed = body.TrimEnd([| '\\'; '/' |])
                if bodyTrimmed.Length = 0 then
                    // Pure-root prompt (`C:\>` → body was `C:\` →
                    // bodyTrimmed empty). Just return the input
                    // verbatim; nothing to trim.
                    Some text
                else
                    // Find the last path separator. Either `\`
                    // (Windows) or `/` (POSIX) — whichever sits
                    // later in the string wins.
                    let lastBackslash = bodyTrimmed.LastIndexOf '\\'
                    let lastSlash = bodyTrimmed.LastIndexOf '/'
                    let sepIdx = max lastBackslash lastSlash
                    if sepIdx < 0 then
                        // No separator found — body is itself the
                        // final segment (e.g. `claude>` → body
                        // `claude`, no `\` or `/`). Return verbatim.
                        Some text
                    else
                        let lastSegment =
                            bodyTrimmed.Substring(sepIdx + 1)
                        let withDelim =
                            match delim with
                            | Some c -> sprintf "%s%c" lastSegment c
                            | None -> lastSegment
                        Some withDelim
