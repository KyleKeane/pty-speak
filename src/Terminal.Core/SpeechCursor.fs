namespace Terminal.Core

open System
open Microsoft.Extensions.Logging

/// Cycle 45 — SpeechCursor: the announce + navigate primitive
/// over `ContentHistory`.
///
/// The cursor holds a position (Seq) into a ContentHistory plus a
/// `LastSpokenSeq` watermark — the highest Seq we've ever spoken.
/// "Have we already spoken this?" is the integer comparison
/// `entry.Seq <= LastSpokenSeq`. There is no string match against
/// rendered row text, no "screen state" comparison, no implicit
/// state: just an explicit watermark on an immutable history.
///
/// Two modes:
///
///   * **AutoDrive**: when new entries are appended to the
///     ContentHistory (via `onAppend`), the cursor advances to
///     each in turn and announces. This is the everyday live
///     streaming experience.
///
///   * **Manual**: the user has explicitly navigated and is now
///     controlling the pace. New appends do NOT advance the
///     cursor automatically. Post-Cycle-51 PR-AD the user-facing
///     Manual gestures (`Ctrl+Shift+Up/Down/End`) navigate the
///     sealed-IOCell `CellTranscript`, not a raw Seq walk;
///     ADR 0007 Phase 0 removed the dead Seq-nav wrappers so
///     there is a single navigation model.
///
/// Interaction-mode transitions: when a `SelectionShown`
/// marker arrives, AutoDrive temporarily suspends — the user has
/// to navigate the list explicitly with arrow keys. On
/// `SelectionDismissed`, AutoDrive resumes (provided the user
/// hadn't separately switched to Manual mode).
///
/// The `announce` callback parameter keeps the module pure and
/// trivially testable.
module SpeechCursor =

    /// Cycle 52 R6b — diagnostic trigger for the
    /// `FullOnChangeElseFinal` prompt-path mode. One Information
    /// line per PromptStart resolution so a `Ctrl+Shift+D` bundle
    /// can confirm the path fired and which effective mode
    /// (`Full` on change / `FinalDirOnly` unchanged) was chosen.
    let private r6bLog =
        Logger.get "Terminal.Core.SpeechCursor.PromptPathOnChange"

    /// Auto-drive vs Manual mode. AutoDrive is the default; the
    /// user toggles to Manual when they want paced review.
    type Mode =
        | AutoDrive
        | Manual

    /// ADR 0007 D1 — the kind of a navigable cell-history item.
    /// v1 (Phase 1) emits only `Input` (the command line) and
    /// `Output` (the command's output). `SubPromptExchange`
    /// (Phase 5) and `ProgressSegment` (Phase 4) are reserved
    /// so the discriminator is shape-stable now; later phases
    /// drive them.
    [<RequireQualifiedAccess>]
    type CellKind =
        | Input
        | Output
        | SubPromptExchange
        | ProgressSegment

    /// ADR 0007 D1 — a typed view of one navigable cell-history
    /// item. Carries the source `IOCell` identity (`CellId` /
    /// `CellSequence`) and `Kind` so navigation and (Phase 2+)
    /// per-cell operations bind to a stable cell identity, not
    /// a flattened `(string * string)` pair. `Text` /
    /// `ActivityId` are exactly what AutoDrive / Manual narrate
    /// (the navigation accessors project `(Text, ActivityId)`
    /// so narration is byte-identical to pre-Phase-1).
    type CellView =
        { CellId: Guid
          CellSequence: int64
          Kind: CellKind
          Text: string
          ActivityId: string
          /// ADR 0007 Phase 2c — the source IOCell's exit code
          /// (`None` while in-flight / when the shell emitted
          /// none). Carried on every item of the cell (both
          /// share the cell's outcome, like `CellId`); the
          /// `jump-to-last-error` accessor scans on it.
          ExitCode: int option }

    /// ADR 0007 Phase 2 — the focused cell resolved to its
    /// command + output. The transcript stores a cell's
    /// command and output as two `CellView` items sharing one
    /// `CellId`; this gathers both for the focused item's cell
    /// so per-cell operations act on the whole cell, not just
    /// the single item the cursor is parked on. `Command` /
    /// `Output` are `None` when that side was whitespace-only
    /// (it was never added to the transcript — same skip rule
    /// as `appendCell`).
    type FocusedCell =
        { CellId: Guid
          CellSequence: int64
          Command: string option
          Output: string option }

    /// Configuration knobs. All defaults chosen to match the
    /// architectural intent in `docs/CORE-ABSTRACTION-BOUNDARY.md`.
    type Parameters =
        { /// Initial mode. AutoDrive in production; tests use
          /// Manual to assert non-advancement.
          InitialMode: Mode
          /// When true, AutoDrive skips `Spinner` entries
          /// silently (they are not announced live). Defaults
          /// true; spinner frames are rarely what a user wants
          /// narrated.
          SkipSpinnersInAutoDrive: bool
          /// When true, AutoDrive suspends on `SelectionShown`
          /// (and resumes on `SelectionDismissed`). Defaults
          /// true; the standard interaction-mode handoff.
          SuspendAutoDriveOnSelection: bool
          /// When true, AutoDrive does NOT announce `TextSpan`
          /// entries; the cursor still advances (Position +
          /// LastSpokenSeq) so Manual navigation can revisit
          /// them, but no live announce fires.
          ///
          /// Cycle 45 fixup (2026-05-12): cmd's command-line
          /// editing (arrows / backspace / delete reflowing the
          /// suffix) reprints suffix bytes whose `Print` events
          /// accumulate into the active TextSpan. Auto-
          /// announcing that span on seal produces inflated /
          /// edit-history-conflated narrations that do not
          /// match the actual `CommandText` / `OutputText`
          /// SessionModel captures from the screen grid. The
          /// authoritative source for "what did the user run +
          /// what did the shell print" is `IOCell` (see
          /// `SessionModel.IOCell.{CommandText,OutputText}`);
          /// `Program.fs handlePromptBoundary` announces that
          /// on tuple finalise.
          ///
          /// Cycle 45f (2026-05-12): this flag now carries the
          /// `ShellPolicy.StreamingMode = TupleFinalOnly`
          /// semantics. The per-shell policy resolved at shell-
          /// switch time flips it on/off — `TupleFinalOnly` /
          /// `Off` map to `true` (suppress TextSpan announce);
          /// `LineByLine` maps to `false` (announce on each
          /// TextSpan seal). The `PromptPath` field below
          /// carries the orthogonal prompt-verbosity dimension.
          SkipTextSpansInAutoDrive: bool
          /// Cycle 45f — how `PromptStart` markers narrate
          /// their `Payload` text. Default `Suppress` matches
          /// today's behaviour (no prompt announce at all).
          /// `FinalDirOnly` trims path-like prompts to the last
          /// directory segment; `Full` narrates verbatim. See
          /// `ShellPolicy.PromptPathMode` for full semantics.
          PromptPath: ShellPolicy.PromptPathMode }

    let defaultParameters : Parameters =
        { InitialMode = AutoDrive
          SkipSpinnersInAutoDrive = true
          SuspendAutoDriveOnSelection = true
          SkipTextSpansInAutoDrive = true
          PromptPath = ShellPolicy.Suppress }

    /// Cursor state. Mutated in-place by the navigation /
    /// announce APIs. Single-threaded by convention (dispatcher
    /// thread); no internal gate needed.
    ///
    /// `Parameters` is mutable on the instance (Cycle 45f) so
    /// shell-switch + the `View → Output Verbosity` menu can
    /// flip per-shell verbosity without reconstructing the
    /// cursor (which would lose `Position` + `LastSpokenSeq`).
    /// Use `setParameters` to update — replays / rewinds are
    /// not in scope; the new policy applies to subsequent
    /// appends only.
    type T internal (parameters: Parameters) =
        member val internal Parameters: Parameters = parameters with get, set
        /// The Seq of the entry the cursor is currently parked
        /// on. -1 means "before the first entry" (initial state
        /// or after `reset`).
        member val internal Position: int64 = -1L with get, set
        /// The highest Seq we have ever announced. `onAppend`
        /// advances it and gates "have we said this?" checks.
        /// -1 means "nothing has been spoken yet."
        member val internal LastSpokenSeq: int64 = -1L with get, set
        /// Cycle 52 R6b — the trimmed text of the last PromptStart
        /// payload the auto-drive walk has seen, used solely by
        /// `PromptPathMode.FullOnChangeElseFinal` to decide
        /// full-path (changed) vs final-dir-only (unchanged).
        /// `None` = no prompt seen yet ⇒ the next prompt counts as
        /// "changed" (full path). Cleared by `reset` (shell-switch
        /// only — post-Cycle-45c ContentHistory is continuous, so
        /// this survives across commands within a shell, which is
        /// exactly what makes "unchanged ⇒ terse" work).
        member val internal LastPromptStartPayload: string option =
            None with get, set
        member val internal Mode: Mode = parameters.InitialMode with get, set
        /// Bookkeeping: while true, AutoDrive is suspended due to
        /// a `SelectionShown` marker; mode is logically AutoDrive
        /// but won't advance on append until `SelectionDismissed`
        /// arrives. Distinct from `Mode = Manual` (which is a
        /// user-driven switch and survives the suspension).
        member val internal SelectionSuspend: bool = false with get, set
        /// Cycle 51 PR-AD (ADR 0004) — the sealed-IOCell
        /// transcript that **Manual** navigation
        /// (Ctrl+Shift+Up/Down/End) walks. Each finalised cell
        /// contributes its authoritative `CommandText` and
        /// `OutputText` (from `SessionModel.extractIOCell`, which
        /// post-PR-X already excludes history-scroll redraws and
        /// includes post-single-key-response output) as separate
        /// navigable `(text, activityId)` items — fixing the
        /// "speech cursor has no record of the command, nor of
        /// the output after a single-key sub-prompt response"
        /// reports (the raw ContentHistory entries are tagged
        /// `UserInputEcho` and filtered by `renderEntryWithPolicy`;
        /// ADR 0004 §4a). The ContentHistory-Seq engine
        /// (`Position`/`LastSpokenSeq`/`onAppend`) is retained
        /// for AutoDrive bookkeeping and selection-suspend; the
        /// user-facing Manual gestures use this cell model. The
        /// dead Seq-navigation wrappers (`next`/`previous`/
        /// `toLatest`/`toMarker`/`current`/`speakCurrent`/
        /// `speakSince`) had no production callers post-PR-AD and
        /// were removed in ADR 0007 Phase 0 (2026-05-16), leaving
        /// a single navigation model.
        member val internal CellTranscript
            : ResizeArray<CellView> = ResizeArray() with get
        /// Index into `CellTranscript` the Manual cursor is parked
        /// on. -1 = before the first item (fresh / after
        /// `cellReset`).
        member val internal CellPos: int = -1 with get, set

    /// Construct a fresh cursor.
    let create (parameters: Parameters) : T =
        T(parameters)

    /// Read the cursor's current `Parameters`. Cycle 45f —
    /// public accessor so cross-assembly callers (Program.fs,
    /// hosted in Terminal.App) can construct a `{ existing with
    /// ... }` update before handing it back to `setParameters`.
    /// The underlying `member val Parameters` is `internal` so
    /// direct field access from Terminal.App fails;
    /// `getParameters` is the module-level lens.
    let getParameters (state: T) : Parameters =
        state.Parameters

    /// Replace the cursor's `Parameters` (Cycle 45f). Subsequent
    /// `onAppend` invocations observe the new values; entries
    /// already announced under the previous policy stay
    /// announced — `setParameters` does NOT replay or rewind.
    /// `Position`, `LastSpokenSeq`, `Mode`, and `SelectionSuspend`
    /// are preserved across the update.
    let setParameters (state: T) (parameters: Parameters) : unit =
        state.Parameters <- parameters

    /// Current cursor mode (Manual / AutoDrive). The
    /// SelectionSuspend bit doesn't change the reported mode —
    /// it's an internal AutoDrive sub-state.
    let mode (state: T) : Mode = state.Mode

    /// Current effective drive: AutoDrive AND not selection-
    /// suspended. `onAppend` consults this to decide whether to
    /// advance.
    let private autoDriveActive (state: T) : bool =
        state.Mode = AutoDrive && not state.SelectionSuspend

    /// Toggle to the other mode. Returns the new mode.
    let toggleMode (state: T) : Mode =
        state.Mode <-
            match state.Mode with
            | AutoDrive -> Manual
            | Manual -> AutoDrive
        state.Mode

    /// Force a specific mode.
    let setMode (state: T) (m: Mode) : unit =
        state.Mode <- m

    /// Helper: predicate for "is this entry advanceable?" in
    /// AutoDrive mode. Spinner entries are skipped when the
    /// configuration says to.
    let private autoDriveAdvanceable
            (state: T)
            (entry: ContentHistory.Entry)
            : bool =
        if state.Parameters.SkipSpinnersInAutoDrive then
            match entry with
            | ContentHistory.Spinner _ -> false
            | _ -> true
        else
            true

    /// Cycle 45f — render an entry under a specific
    /// `PromptPathMode`. PromptStart markers consult the policy
    /// to decide whether (and how) to narrate their payload.
    /// Every other entry kind is unaffected.
    ///
    /// Activity-id choices mirror `NvdaChannel.semanticToActivityId`:
    /// streaming text + selection / prompt boundaries route on
    /// `pty-speak.output`; alt-screen toggles on
    /// `pty-speak.mode`; bell on `pty-speak.output` (no
    /// dedicated id for bell-via-announce; the earcon channel
    /// handles the audible cue separately).
    ///
    /// Note: AutoDrive's TextSpan-skip behaviour
    /// (`Parameters.SkipTextSpansInAutoDrive`) is enforced in
    /// `onAppend`, not here. The Cell-transcript Manual surface
    /// (Cycle 51 PR-AD) renders its own authoritative cell text,
    /// so the skip does not gate explicit review.
    let renderEntryWithPolicy
            (promptPath: ShellPolicy.PromptPathMode)
            (entry: ContentHistory.Entry)
            : (string * string) option =
        // Cycle 48 PR-E (ADR 0003 §9.6) — SpeechCursor filters
        // entries tagged `UserInputEcho` in BOTH AutoDrive AND
        // Manual navigation. Bytes appended during Composing
        // (the user typing into the prompt) are echo of what
        // the screen reader's keyboard hook already covered;
        // re-narrating them via the speech cursor would
        // duplicate.
        if ContentHistory.entrySource entry
           = ContentHistory.EntrySource.UserInputEcho then
            None
        else
        match entry with
        | ContentHistory.TextSpan d ->
            if String.IsNullOrEmpty d.Text then None
            else Some (d.Text, ActivityIds.output)
        | ContentHistory.Newline _ ->
            // Newlines don't get a separate announce — they're
            // baked into the natural pause between TextSpans.
            None
        | ContentHistory.Overwrite _ ->
            // Overwrite is a structural marker; the replacement
            // content is the NEXT TextSpan. No announce on the
            // Overwrite itself.
            None
        | ContentHistory.Marker m ->
            match m.Kind with
            | ContentHistory.MarkerKind.SelectionShown ->
                let suffix =
                    match m.Payload with
                    | Some p when not (String.IsNullOrEmpty p) ->
                        sprintf ": %s" p
                    | _ -> ""
                Some
                    (sprintf "Selection prompt%s." suffix,
                     ActivityIds.output)
            | ContentHistory.MarkerKind.SelectionDismissed ->
                Some ("Selection dismissed.", ActivityIds.output)
            | ContentHistory.MarkerKind.AltScreenEnter ->
                Some
                    ("Full-screen application active.",
                     ActivityIds.mode)
            | ContentHistory.MarkerKind.AltScreenExit ->
                Some
                    ("Full-screen application exited.",
                     ActivityIds.mode)
            | ContentHistory.MarkerKind.BellRang ->
                // Bell's audible cue is the earcon; no separate
                // announce by default. Future: a config knob can
                // flip this to announce "Bell." for users who
                // have earcons muted.
                None
            | ContentHistory.MarkerKind.PromptStart ->
                // Cycle 45f — narrate the prompt under the
                // active shell's PromptPathMode.
                //
                // - `Suppress` (default for every shell):
                //   `trimPromptPath` returns `None`; we emit
                //   nothing.
                // - `FinalDirOnly`: trims path-like prompts to
                //   the last directory + delimiter (e.g.
                //   `"Local>"` from
                //   `"C:\Users\Kyle\AppData\Local\>"`).
                // - `Full`: narrates the payload verbatim.
                //
                // Empty / missing payload returns `None` under
                // every mode (the heuristic detector
                // occasionally captures a blank cursor row).
                match m.Payload with
                | None -> None
                | Some text ->
                    ShellPolicy.trimPromptPath promptPath text
                    |> Option.map (fun rendered ->
                        (rendered, ActivityIds.output))
            | ContentHistory.MarkerKind.CommandStart
            | ContentHistory.MarkerKind.OutputStart
            | ContentHistory.MarkerKind.CommandFinished ->
                // Tuple-internal boundary markers don't announce
                // by themselves. The TextSpans they bracket are
                // what the user hears.
                None
            | ContentHistory.MarkerKind.Custom _ ->
                // Future modes will define their own announce
                // text via the marker payload.
                m.Payload
                |> Option.map (fun p -> (p, ActivityIds.output))
        | ContentHistory.Spinner _ ->
            // Spinner entries don't announce in AutoDrive; the
            // user can navigate to them manually for inspection
            // but they're not narrated by default.
            None

    /// Backwards-compatible wrapper — render an entry under the
    /// default `PromptPathMode.Suppress`. Existing tests that
    /// don't care about prompt-path verbosity use this form;
    /// PromptStart-marker rendering is unaffected (Suppress
    /// returns `None`, matching pre-Cycle-45f behaviour).
    let renderEntry (entry: ContentHistory.Entry) : (string * string) option =
        renderEntryWithPolicy ShellPolicy.Suppress entry

    /// Cycle 49 PR-D — render an entry for **manual navigation**
    /// (Ctrl+Shift+Up/Down/End). Decouples navigation from
    /// auto-drive narration: `PromptStart` markers with payload
    /// always render to a `FinalDirOnly`-trimmed announce here
    /// regardless of the cursor's `PromptPath` policy, so the
    /// user can navigate prompt-to-prompt in review even when
    /// auto-drive is silent on prompts (the per-shell default).
    ///
    /// Other entry kinds delegate to the policy-aware
    /// `renderEntryWithPolicy` so navigation and auto-drive
    /// agree on TextSpans, Newlines, Overwrites, selection
    /// markers, etc.
    ///
    /// Maintainer feedback 2026-05-14: "the speech cursor only
    /// includes the output of echo and I think it should include
    /// the prompt as well in the history as a separate item."
    /// This function is the navigation-side response; the
    /// auto-drive side keeps the existing PromptPath gating so
    /// streaming output doesn't become chattier than before.
    let renderEntryForManualNav
            (promptPath: ShellPolicy.PromptPathMode)
            (entry: ContentHistory.Entry)
            : (string * string) option =
        match entry with
        | ContentHistory.Marker m
            when m.Kind = ContentHistory.MarkerKind.PromptStart ->
            match m.Payload with
            | Some text when not (System.String.IsNullOrEmpty text) ->
                // FinalDirOnly trim keeps the manual-nav announce
                // short for path-style prompts ("Local>" rather
                // than the full `C:\Users\Kyle\AppData\Local>`).
                // If the trim returns None (rare — payload that
                // doesn't end in `>` and has no path segments),
                // fall back to the raw payload so the boundary
                // is still navigable.
                match
                    ShellPolicy.trimPromptPath
                        ShellPolicy.FinalDirOnly text
                with
                | Some rendered ->
                    Some (rendered, ActivityIds.output)
                | None ->
                    Some (text, ActivityIds.output)
            | _ -> None
        | _ -> renderEntryWithPolicy promptPath entry

    /// Cycle 52 R6b / R6b-followup — resolve an *on-change*
    /// `PromptPathMode` (`FullOnChangeElseFinal` /
    /// `FinalOnChangeElseFull` / `SilentOnUnchangedFullOnChange` /
    /// `SilentOnUnchangedFinalOnChange`) to a concrete
    /// `Full` / `FinalDirOnly` / `Suppress` for THIS entry,
    /// branching on whether the prompt changed since the last one
    /// (first prompt = "changed"), using the per-cursor
    /// `LastPromptStartPayload`. Any non-on-change mode passes
    /// through untouched. Side-effect: on a PromptStart with
    /// payload it updates `LastPromptStartPayload` so the *next*
    /// prompt's change-test is against this one. Non-PromptStart
    /// entries and a payload-less PromptStart never reach
    /// `trimPromptPath` (the other `renderEntryWithPolicy` arms
    /// ignore the mode; the PromptStart arm maps `Payload = None →
    /// None` before the trim), so returning `mode` unchanged there
    /// is inert.
    let private resolveOnChange
            (state: T)
            (mode: ShellPolicy.PromptPathMode)
            (entry: ContentHistory.Entry)
            : ShellPolicy.PromptPathMode =
        // The trimmed PromptStart payload, or None for any entry
        // that never reaches trimPromptPath (non-PromptStart, or a
        // payload-less PromptStart — `renderEntryWithPolicy` maps
        // those to None before the trim regardless of mode).
        let promptKey =
            match entry with
            | ContentHistory.Marker m
                when m.Kind = ContentHistory.MarkerKind.PromptStart ->
                m.Payload |> Option.map (fun t -> t.Trim())
            | _ -> None
        match promptKey with
        | None -> mode
        | Some key ->
            let changed =
                match state.LastPromptStartPayload with
                | Some prev -> prev <> key
                | None -> true
            state.LastPromptStartPayload <- Some key
            let resolved =
                match mode with
                | ShellPolicy.FullOnChangeElseFinal ->
                    if changed then ShellPolicy.Full
                    else ShellPolicy.FinalDirOnly
                | ShellPolicy.FinalOnChangeElseFull ->
                    if changed then ShellPolicy.FinalDirOnly
                    else ShellPolicy.Full
                | ShellPolicy.SilentOnUnchangedFullOnChange ->
                    if changed then ShellPolicy.Full
                    else ShellPolicy.Suppress
                | ShellPolicy.SilentOnUnchangedFinalOnChange ->
                    if changed then ShellPolicy.FinalDirOnly
                    else ShellPolicy.Suppress
                | other -> other
            r6bLog.LogInformation(
                "R6b prompt-path on-change resolve. Mode={Mode} Changed={Changed} Resolved={Resolved} KeyLen={KeyLen}",
                (sprintf "%A" mode),
                changed,
                (sprintf "%A" resolved),
                key.Length)
            resolved

    let private effectivePromptPath
            (state: T)
            (entry: ContentHistory.Entry)
            : ShellPolicy.PromptPathMode =
        match state.Parameters.PromptPath with
        | ShellPolicy.FullOnChangeElseFinal
        | ShellPolicy.FinalOnChangeElseFull
        | ShellPolicy.SilentOnUnchangedFullOnChange
        | ShellPolicy.SilentOnUnchangedFinalOnChange ->
            resolveOnChange state state.Parameters.PromptPath entry
        | other -> other

    /// Event hook: invoked when ContentHistory has appended one
    /// or more new entries. The caller's invocation is a "wake
    /// up, something changed" signal; the actual content to
    /// announce is read from `history` directly (specifically,
    /// every entry with `Seq > LastSpokenSeq`).
    ///
    /// **Why read from history rather than take an entries list**:
    /// concurrent appenders (the reader thread feeding parser
    /// output + the pump thread firing marker insertions on
    /// boundary events) can schedule dispatcher actions in
    /// either order. Reading from history makes onAppend
    /// idempotent: whichever invocation runs first processes
    /// everything new; the second invocation finds nothing new
    /// to do. No race; no dropped announces.
    ///
    /// **Selection-suspend ordering** matters: the `SelectionShown`
    /// marker ITSELF should announce ("selection prompt: …") and
    /// then suspend AutoDrive for entries that follow. Symmetric
    /// for `SelectionDismissed`: clear suspend FIRST so the
    /// "Selection dismissed." announce fires, then continue with
    /// any trailing entries (now in AutoDrive). The two
    /// modulation points sandwich the announce decision below.
    ///
    /// **Threading contract**: caller must serialise calls —
    /// all invocations must come from the same logical thread
    /// (the WPF dispatcher per Cycle 45 Commit 2's wiring). The
    /// single-threaded discipline is what keeps `LastSpokenSeq`
    /// monotonic without an explicit lock.
    let onAppend
            (state: T)
            (history: ContentHistory.T)
            (announce: string * string -> unit)
            : unit =
        let snapshot = ContentHistory.snapshot history
        let lower = state.LastSpokenSeq
        for entry in snapshot do
            let s = ContentHistory.entrySeq entry
            if s > lower then
                // (1) Pre-suspend modulation: SelectionDismissed
                //     clears the suspend bit BEFORE the announce
                //     decision so the dismissal announce fires.
                match entry with
                | ContentHistory.Marker m
                    when m.Kind = ContentHistory.MarkerKind.SelectionDismissed ->
                    state.SelectionSuspend <- false
                | _ -> ()

                // (2) Announce decision uses the current effective
                //     drive. TextSpan entries advance the cursor
                //     silently when SkipTextSpansInAutoDrive is on
                //     (the default, per the Cycle 45 fixup note on
                //     Parameters): the live announce is suppressed
                //     but Position + LastSpokenSeq still update so
                //     Manual navigation finds them and the
                //     "already spoken?" gate stays monotonic.
                if autoDriveActive state && autoDriveAdvanceable state entry then
                    if s > state.Position then
                        state.Position <- s
                    let suppressTextSpan =
                        match entry with
                        | ContentHistory.TextSpan _ ->
                            state.Parameters.SkipTextSpansInAutoDrive
                        | _ -> false
                    if not suppressTextSpan then
                        match
                            renderEntryWithPolicy
                                (effectivePromptPath state entry)
                                entry
                        with
                        | Some (text, activityId) ->
                            announce (text, activityId)
                        | None -> ()
                    state.LastSpokenSeq <- s

                // (3) Post-suspend modulation: SelectionShown sets
                //     suspend AFTER the announce decision so the
                //     marker itself ("selection prompt: ...") is
                //     spoken, then subsequent entries in the batch
                //     skip auto-announce.
                match entry with
                | ContentHistory.Marker m
                    when m.Kind = ContentHistory.MarkerKind.SelectionShown ->
                    if state.Parameters.SuspendAutoDriveOnSelection then
                        state.SelectionSuspend <- true
                | _ -> ()

    /// Reset cursor state. Post-Cycle-45c `ContentHistory` is
    /// continuous (it does NOT reset per command), so the only
    /// `ContentHistory.reset` + `SpeechCursor.reset` call site is
    /// `switchToShell` — i.e. this fires on a **shell-switch**,
    /// not on every tuple seal. That is exactly what makes the
    /// R6b `FullOnChangeElseFinal` mode work: `LastPromptStartPayload`
    /// survives across commands within a shell (so an unchanged
    /// directory stays terse) and is cleared here on a switch (so
    /// the first prompt in the new shell narrates the full path).
    let reset (state: T) : unit =
        state.Position <- -1L
        state.LastSpokenSeq <- -1L
        state.SelectionSuspend <- false
        // R6b — clear the on-change watermark so the first prompt
        // after a shell-switch counts as "changed" (full path).
        state.LastPromptStartPayload <- None
        // Mode is preserved across resets — the user's choice of
        // AutoDrive vs Manual survives tuple boundaries.

    // -----------------------------------------------------------------
    // Cycle 51 PR-AD (ADR 0004) — sealed-IOCell transcript: the
    // model the user-facing Manual gestures navigate. The
    // ContentHistory-Seq engine above retains only AutoDrive
    // bookkeeping (`onAppend`/`Position`/`LastSpokenSeq`) +
    // selection-suspend; the dead Seq-nav wrappers were removed
    // in ADR 0007 Phase 0.
    // -----------------------------------------------------------------

    /// Append a finalised cell's authoritative command + output as
    /// separate navigable items. Whitespace-only parts are
    /// skipped (an empty-command Enter, or a command that printed
    /// nothing). In AutoDrive the cursor follows the latest item;
    /// in Manual it stays put so an in-progress review isn't
    /// disturbed by new output (mirrors the legacy AutoDrive /
    /// Manual append semantics).
    /// ADR 0007 D7 — `cellId` / `cellSequence` are sourced from
    /// the finalized `IOCell` at the seal site (Program.fs), not
    /// re-derived from a ContentHistory slice. v1 (Phase 1)
    /// classifies the command line as `Input` and the output as
    /// `Output`; both share the source cell's identity. The
    /// whitespace-skip + `Trim` + `ActivityIds.output` are
    /// unchanged from pre-Phase-1, so what the navigation
    /// accessors return — and therefore what is narrated — is
    /// byte-identical.
    let appendCell
            (state: T)
            (cellId: Guid)
            (cellSequence: int64)
            (commandText: string)
            (outputText: string)
            (exitCode: int option)
            : unit =
        if not (String.IsNullOrWhiteSpace commandText) then
            state.CellTranscript.Add(
                { CellId = cellId
                  CellSequence = cellSequence
                  Kind = CellKind.Input
                  Text = commandText.Trim()
                  ActivityId = ActivityIds.output
                  ExitCode = exitCode })
        if not (String.IsNullOrWhiteSpace outputText) then
            state.CellTranscript.Add(
                { CellId = cellId
                  CellSequence = cellSequence
                  Kind = CellKind.Output
                  Text = outputText
                  ActivityId = ActivityIds.output
                  ExitCode = exitCode })
        if state.Mode = AutoDrive then
            state.CellPos <- state.CellTranscript.Count - 1

    /// Project a stored `CellView` to the `(text, activityId)`
    /// pair the narration path consumes. Keeps `cellCurrent` /
    /// `cellPrevious` / `cellNext` / `cellToLatest` returning
    /// exactly what they returned pre-Phase-1 (byte-identical
    /// narration); the typed view is reachable via
    /// `cellCurrentView` for the Phase 2+ operations layer.
    let private cellPair (v: CellView) : string * string =
        (v.Text, v.ActivityId)

    /// The typed cell the Manual cursor is parked on, if any.
    /// ADR 0007 D2 enabler — per-cell operations bind to this
    /// (identity + kind), not the projected pair.
    let cellCurrentView (state: T) : CellView option =
        if state.CellPos >= 0
           && state.CellPos < state.CellTranscript.Count then
            Some state.CellTranscript.[state.CellPos]
        else
            None

    /// The transcript item the Manual cursor is parked on, if any.
    let cellCurrent (state: T) : (string * string) option =
        cellCurrentView state |> Option.map cellPair

    /// Step the Manual cursor to the previous (older) transcript
    /// item. From the unparked state (-1) the first press lands on
    /// the latest item. Returns the item, or `None` if already at
    /// the first item / the transcript is empty.
    let cellPrevious (state: T) : (string * string) option =
        let count = state.CellTranscript.Count
        if count = 0 then None
        else
            let target =
                if state.CellPos < 0 then count - 1
                else state.CellPos - 1
            if target < 0 then None
            else
                state.CellPos <- target
                Some (cellPair state.CellTranscript.[target])

    /// Step the Manual cursor to the next (newer) transcript item.
    /// From the unparked state (-1) the first press lands on the
    /// latest item. Returns the item, or `None` if already at the
    /// latest item / the transcript is empty.
    let cellNext (state: T) : (string * string) option =
        let count = state.CellTranscript.Count
        if count = 0 then None
        else
            let target =
                if state.CellPos < 0 then count - 1
                else state.CellPos + 1
            if target >= count then None
            else
                state.CellPos <- target
                Some (cellPair state.CellTranscript.[target])

    /// Jump the Manual cursor to the latest transcript item.
    /// Returns it, or `None` if the transcript is empty.
    let cellToLatest (state: T) : (string * string) option =
        let count = state.CellTranscript.Count
        if count = 0 then None
        else
            state.CellPos <- count - 1
            Some (cellPair state.CellTranscript.[count - 1])

    /// ADR 0007 Phase 2c — jump the Manual cursor to the most
    /// recent transcript item whose source cell exited non-zero.
    /// Scans newest-first; the first non-zero-`ExitCode` item is
    /// the failed cell's Output item when it has one (Output is
    /// appended after Input, so it has the higher index), else
    /// its Input item. Returns the item, or `None` when no cell
    /// in the transcript has a non-zero exit code (no failure to
    /// jump to, or every cell is still in-flight / exit-less).
    /// Mode is intentionally left untouched, matching
    /// `cellPrevious` / `cellToLatest` (navigation does not flip
    /// AutoDrive↔Manual; that focus-vs-live policy is a Phase 6
    /// D5a open decision, not Phase 2c's to make).
    let jumpToLastError (state: T) : (string * string) option =
        let count = state.CellTranscript.Count
        let mutable idx = count - 1
        let mutable found = -1
        while found < 0 && idx >= 0 do
            match state.CellTranscript.[idx].ExitCode with
            | Some code when code <> 0 -> found <- idx
            | _ -> idx <- idx - 1
        if found < 0 then None
        else
            state.CellPos <- found
            Some (cellPair state.CellTranscript.[found])

    /// Clear the cell transcript. Called on shell hot-switch
    /// alongside `reset` (the previous shell's transcript is no
    /// longer relevant).
    let cellReset (state: T) : unit =
        state.CellTranscript.Clear()
        state.CellPos <- -1

    /// ADR 0007 Phase 2 — resolve the focused item to its whole
    /// cell (command + output), keyed by the shared `CellId`.
    /// `None` when nothing is focused (cursor unparked / empty
    /// transcript). The D2 per-cell-operations primitive: a
    /// copy/rerun acts on the cell, not on whichever of its two
    /// items the cursor happens to be parked on.
    let focusedCell (state: T) : FocusedCell option =
        match cellCurrentView state with
        | None -> None
        | Some v ->
            let textOf (k: CellKind) =
                state.CellTranscript
                |> Seq.tryFind (fun c ->
                    c.CellId = v.CellId && c.Kind = k)
                |> Option.map (fun c -> c.Text)
            Some
                { CellId = v.CellId
                  CellSequence = v.CellSequence
                  Command = textOf CellKind.Input
                  Output = textOf CellKind.Output }
