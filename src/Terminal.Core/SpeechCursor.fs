namespace Terminal.Core

open System

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
///   * **Manual**: the user has explicitly navigated backwards
///     (Up arrow, marker jump, etc.) and is now controlling the
///     pace. New appends do NOT advance the cursor automatically.
///     The user moves the cursor forward via Down arrow / "jump
///     to latest" / etc.
///
/// Interaction-mode mode transitions: when a `SelectionShown`
/// marker arrives, AutoDrive temporarily suspends — the user has
/// to navigate the list explicitly with arrow keys. On
/// `SelectionDismissed`, AutoDrive resumes (provided the user
/// hadn't separately switched to Manual mode).
///
/// **Commit 1 (this file) is purely additive.** No production
/// callsite wires to SpeechCursor yet; Commit 2 connects it.
/// The `announce` callback parameter keeps the module pure and
/// trivially testable.
module SpeechCursor =

    /// Auto-drive vs Manual mode. AutoDrive is the default; the
    /// user toggles to Manual when they want paced review.
    type Mode =
        | AutoDrive
        | Manual

    /// Navigation direction for `toMarker` / `jumpRelative`.
    type Direction =
        | Forward
        | Backward

    /// Configuration knobs. All defaults chosen to match the
    /// architectural intent in `docs/CORE-ABSTRACTION-BOUNDARY.md`.
    type Parameters =
        { /// Initial mode. AutoDrive in production; tests use
          /// Manual to assert non-advancement.
          InitialMode: Mode
          /// When true, AutoDrive skips `Spinner` entries
          /// silently. The user can still navigate to them via
          /// explicit `next`/`previous`. Defaults true; spinner
          /// frames are rarely what a user wants narrated.
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
          /// what did the shell print" is `SessionTuple` (see
          /// `SessionModel.SessionTuple.{CommandText,OutputText}`);
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
        /// The highest Seq we have ever announced. Used to drive
        /// `speakSince` and to gate "have we said this?" checks.
        /// -1 means "nothing has been spoken yet."
        member val internal LastSpokenSeq: int64 = -1L with get, set
        member val internal Mode: Mode = parameters.InitialMode with get, set
        /// Bookkeeping: while true, AutoDrive is suspended due to
        /// a `SelectionShown` marker; mode is logically AutoDrive
        /// but won't advance on append until `SelectionDismissed`
        /// arrives. Distinct from `Mode = Manual` (which is a
        /// user-driven switch and survives the suspension).
        member val internal SelectionSuspend: bool = false with get, set

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

    /// Look up the entry the cursor is currently parked on, if
    /// any. Returns `None` if position is -1 (before any entry)
    /// or out of range.
    let current (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        if state.Position < 0L then None
        else ContentHistory.entryBySeq history state.Position

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

    /// Move the cursor to the next/previous Marker of the given
    /// kind. Returns the marker entry, or `None` if no match.
    let toMarker
            (state: T)
            (history: ContentHistory.T)
            (kind: ContentHistory.MarkerKind)
            (direction: Direction)
            : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let matchesKind (e: ContentHistory.Entry) =
            match e with
            | ContentHistory.Marker m -> m.Kind = kind
            | _ -> false
        let candidates =
            match direction with
            | Forward ->
                entries
                |> Array.filter (fun e ->
                    matchesKind e
                    && ContentHistory.entrySeq e > state.Position)
                |> Array.tryHead
            | Backward ->
                entries
                |> Array.filter (fun e ->
                    matchesKind e
                    && ContentHistory.entrySeq e < state.Position)
                |> Array.tryLast
        match candidates with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

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
    /// `onAppend`, not here. Manual navigation (`speakCurrent`)
    /// still renders TextSpans so the user can review them
    /// explicitly.
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

    /// Cycle 49 PR-A — does this entry actually render to an
    /// audible announcement under the cursor's current policy?
    /// Newline / Overwrite / empty-TextSpan / boundary-Marker /
    /// `UserInputEcho`-sourced entries all return `None` from
    /// `renderEntryWithPolicy`; manual navigation should skip
    /// them rather than parking on Seqs the user can't hear.
    ///
    /// Manual nav pre-PR-A landed on every entry by Seq, so a
    /// `dir`-shaped output (8 lines = 8 TextSpans + 8 Newlines)
    /// required 16 Ctrl+Shift+Up presses to step backwards, half
    /// of which announced nothing. PR-A collapses that to 8
    /// presses, one per audible chunk.
    ///
    /// `toMarker` deliberately does NOT use this filter — a
    /// marker jump is the user explicitly asking for a marker,
    /// even if its `renderEntry` returns `None` (e.g.
    /// `CommandStart` boundary markers).
    ///
    /// Lives below `renderEntryWithPolicy` so F#'s single-pass
    /// type resolution sees the dependency in declaration order;
    /// `next` / `previous` / `toLatest` follow for the same
    /// reason.
    let private renderable (state: T) (entry: ContentHistory.Entry) : bool =
        (renderEntryWithPolicy state.Parameters.PromptPath entry).IsSome

    /// Move the cursor to the next renderable entry in the
    /// supplied history (Seq > Position). Returns the entry, or
    /// `None` if no later renderable entry exists.
    let next (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let target =
            entries
            |> Array.tryFind (fun e ->
                ContentHistory.entrySeq e > state.Position
                && renderable state e)
        match target with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

    /// Move the cursor to the previous renderable entry. Returns
    /// the entry, or `None` if no earlier renderable entry exists.
    let previous (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let target =
            entries
            |> Array.filter (fun e ->
                ContentHistory.entrySeq e < state.Position
                && renderable state e)
            |> Array.tryLast
        match target with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

    /// Jump the cursor to the latest renderable entry. Returns
    /// the entry, or `None` if history contains no renderable
    /// entries (only Newlines / Overwrites / etc.).
    let toLatest (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let target =
            entries
            |> Array.filter (renderable state)
            |> Array.tryLast
        match target with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

    /// Announce the entry at the cursor's current position via
    /// the supplied callback. Updates `LastSpokenSeq` to the
    /// entry's Seq if the entry rendered to a non-empty
    /// announcement. Returns `true` iff something was announced.
    let speakCurrent
            (state: T)
            (history: ContentHistory.T)
            (announce: string * string -> unit)
            : bool =
        match current state history with
        | None -> false
        | Some entry ->
            match renderEntryWithPolicy state.Parameters.PromptPath entry with
            | None ->
                // The entry exists but has no audible
                // announcement (e.g. Newline, Overwrite). Still
                // advance LastSpokenSeq so we don't redundantly
                // try again.
                state.LastSpokenSeq <-
                    max state.LastSpokenSeq (ContentHistory.entrySeq entry)
                false
            | Some (text, activityId) ->
                announce (text, activityId)
                state.LastSpokenSeq <-
                    max state.LastSpokenSeq (ContentHistory.entrySeq entry)
                true

    /// Announce every entry with Seq in `(LastSpokenSeq, throughSeq]`
    /// in ascending Seq order. Used by `onAppend` to catch up the
    /// cursor + speak any entries the user may have skipped (e.g.
    /// they were in Manual mode and just jumped to latest).
    let speakSince
            (state: T)
            (history: ContentHistory.T)
            (announce: string * string -> unit)
            (throughSeq: int64)
            : int =
        let entries = ContentHistory.snapshot history
        let lower = state.LastSpokenSeq
        let mutable spokenCount = 0
        for e in entries do
            let s = ContentHistory.entrySeq e
            if s > lower && s <= throughSeq then
                match renderEntryWithPolicy state.Parameters.PromptPath e with
                | Some (text, activityId) ->
                    announce (text, activityId)
                    spokenCount <- spokenCount + 1
                | None -> ()
                state.LastSpokenSeq <- s
        spokenCount

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
                                state.Parameters.PromptPath
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

    /// Reset cursor state. Called when the tuple seals and
    /// ContentHistory itself resets.
    let reset (state: T) : unit =
        state.Position <- -1L
        state.LastSpokenSeq <- -1L
        state.SelectionSuspend <- false
        // Mode is preserved across resets — the user's choice of
        // AutoDrive vs Manual survives tuple boundaries.
