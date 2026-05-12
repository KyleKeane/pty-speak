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
          SuspendAutoDriveOnSelection: bool }

    let defaultParameters : Parameters =
        { InitialMode = AutoDrive
          SkipSpinnersInAutoDrive = true
          SuspendAutoDriveOnSelection = true }

    /// Cursor state. Mutated in-place by the navigation /
    /// announce APIs. Single-threaded by convention (dispatcher
    /// thread); no internal gate needed.
    type T internal (parameters: Parameters) =
        member val internal Parameters: Parameters = parameters with get
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

    /// Move the cursor to the next entry in the supplied
    /// history (Seq > Position). Returns the entry, or `None` if
    /// already at the latest.
    let next (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let target =
            entries
            |> Array.tryFind (fun e ->
                ContentHistory.entrySeq e > state.Position)
        match target with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

    /// Move the cursor to the previous entry. Returns the entry,
    /// or `None` if already at the start.
    let previous (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        let target =
            entries
            |> Array.filter (fun e ->
                ContentHistory.entrySeq e < state.Position)
            |> Array.tryLast
        match target with
        | Some e ->
            state.Position <- ContentHistory.entrySeq e
            Some e
        | None -> None

    /// Jump the cursor to the most recently appended entry.
    /// Returns the entry, or `None` if history is empty.
    let toLatest (state: T) (history: ContentHistory.T) : ContentHistory.Entry option =
        let entries = ContentHistory.snapshot history
        if entries.Length = 0 then None
        else
            let last = entries.[entries.Length - 1]
            state.Position <- ContentHistory.entrySeq last
            Some last

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

    /// Render an entry to the (text, activityId) pair the
    /// announce callback will receive. Pure; tests use this
    /// directly to assert format.
    ///
    /// Activity-id choices mirror `NvdaChannel.semanticToActivityId`:
    /// streaming text + selection / prompt boundaries route on
    /// `pty-speak.output`; alt-screen toggles on `pty-speak.mode`;
    /// bell on `pty-speak.output` (no dedicated id for bell-via-
    /// announce; the earcon channel handles the audible cue
    /// separately).
    let renderEntry (entry: ContentHistory.Entry) : (string * string) option =
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
            | ContentHistory.MarkerKind.PromptStart
            | ContentHistory.MarkerKind.CommandStart
            | ContentHistory.MarkerKind.OutputStart
            | ContentHistory.MarkerKind.CommandFinished ->
                // Tuple-internal boundary markers don't announce
                // by themselves. The TextSpans they bracket are
                // what the user hears.
                //
                // Cycle 45 backlog (docs/USER-SETTINGS.md
                // "Prompt-path verbosity"): future user setting
                // could announce a shortened form of the prompt
                // here (final-directory-only, or fully
                // suppressed) so a sighted-style "I'm in
                // C:\Users\Kyle\...>" cue is replaced with a
                // briefer "Kyle>" or with silence. Pull the
                // prompt text from m.Payload or from
                // SessionModel.ActivePromptText.
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
            match renderEntry entry with
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
                match renderEntry e with
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
                //     drive.
                if autoDriveActive state && autoDriveAdvanceable state entry then
                    if s > state.Position then
                        state.Position <- s
                    match renderEntry entry with
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
