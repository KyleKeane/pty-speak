namespace Terminal.Core

open System
open System.Text

/// Cycle 45 — ContentHistory: the computational substrate for the
/// aural pipeline.
///
/// The substrate of pty-speak's aural experience is NOT the screen
/// grid. The screen is a finite 30×120 projection of an unbounded
/// byte stream; using it as the announce baseline causes false
/// matches on repeated content, content loss on scroll, and
/// implicit-state bugs ("did we already say this?" answered by
/// fragile string-diff heuristics). ContentHistory replaces that
/// with an append-only typed log of entries, each carrying a
/// monotonic `Seq` identity. "Already spoken?" becomes
/// `Seq <= cursor.LastSpokenSeq` — an integer comparison.
///
/// Scope: a single ContentHistory instance covers the **active
/// tuple** — from the prompt boundary that opened it to the prompt
/// boundary that seals it. On seal, the entries are archived into
/// `SessionModel`'s `SessionTuple.History` queue, and a fresh
/// ContentHistory begins. The substrate is therefore bounded in
/// memory by tuple size, not session lifetime.
///
/// **Threading**: `Gate`-protected for cross-thread reads (the
/// diagnostic-bundle thread may read snapshots while the reader
/// thread is appending). Same pattern `LinearTextStream` used.
///
/// **Commit 1 (this file) is purely additive.** No consumer wires
/// to this module yet; Commit 2 connects it to the reader loop +
/// detectors + SpeechCursor. The module exists in isolation so
/// the data model can be iterated on without touching the live
/// pipeline.
module ContentHistory =

    /// Discrete semantic events that detectors emit into the
    /// history. The set is intentionally open-ended via `Custom`
    /// — new canonical interaction modes (per the Cycle 45 plan's
    /// "modes are extensible" principle) add new `MarkerKind`
    /// values without disturbing the substrate.
    [<RequireQualifiedAccess>]
    type MarkerKind =
        /// A shell prompt's leading byte sequence has been
        /// detected (heuristically or via OSC 133;A). Opens the
        /// "user is typing a command" phase.
        | PromptStart
        /// The user pressed Enter; the typed command boundary is
        /// known (heuristically or via OSC 133;B). Opens the
        /// "command is running, output may stream" phase.
        | CommandStart
        /// Output region of the active tuple has begun
        /// (heuristically inferred or OSC 133;C). Distinguishes
        /// the prompt+typed-command region from the actual
        /// command-output region.
        | OutputStart
        /// The active command has completed; the next prompt
        /// rendered (heuristically or OSC 133;D). Triggers
        /// tuple-seal at the SessionModel level.
        | CommandFinished
        /// An interactive selection prompt is on screen.
        /// SpeechCursor's auto-drive suspends; user navigates
        /// the list manually until `SelectionDismissed`.
        | SelectionShown
        /// The selection prompt is gone. SpeechCursor's
        /// auto-drive resumes.
        | SelectionDismissed
        /// A BEL byte (0x07) was processed. Earcons can react.
        | BellRang
        /// The alt-screen mode was entered. Visual state
        /// regime-switches; SpeechCursor may reset its baseline.
        | AltScreenEnter
        /// The alt-screen mode was exited.
        | AltScreenExit
        /// Open-ended extension point. The string is the
        /// canonical identifier for the new mode (e.g.
        /// `"claude.thinking-state"`).
        | Custom of string

    /// One sealed `TextSpan` of printable text accumulated from
    /// a contiguous run of `Print` events. Sealing triggers:
    /// newline, marker insertion, idle window, or overwrite.
    type TextSpanData =
        { Seq: int64
          Text: string
          StartedAt: DateTime
          SettledAt: DateTime }

    /// A newline boundary in the active tuple's output. Carries
    /// only the seq + timestamp; the actual `\n` byte is logically
    /// at the end of the preceding TextSpan.
    type NewlineData =
        { Seq: int64
          At: DateTime }

    /// An in-place overwrite of a prior `TextSpan`. The
    /// `ReplacesSeq` points at the TextSpan whose visible position
    /// is being overwritten. This is how genuine in-place patterns
    /// (e.g. spinner frames, progress bar updates) are represented
    /// — they DO NOT mutate the prior TextSpan. The history is
    /// strictly append-only; "what was already spoken" remains
    /// addressable by its Seq.
    type OverwriteData =
        { Seq: int64
          ReplacesSeq: int64
          Text: string
          At: DateTime }

    /// A semantic event emitted by a detector or by the substrate
    /// itself. The `Payload` is optional — most markers carry only
    /// their kind, but some (e.g. `SelectionShown` carrying the
    /// option list) need additional data.
    type MarkerData =
        { Seq: int64
          Kind: MarkerKind
          At: DateTime
          Payload: string option }

    /// A coalesced sequence of `Overwrite` entries that cycle
    /// through a small set of glyph patterns at high frequency —
    /// i.e., a spinner. Replaces the run of Overwrites with a
    /// single entry so SpeechCursor can skip them in AutoDrive
    /// mode without N redundant announces. Detection is deferred
    /// to a follow-up (Commit 2 or later); the entry shape ships
    /// in Commit 1 so the data model is complete.
    type SpinnerData =
        { Seq: int64
          LatestText: string
          FrameCount: int
          FirstAt: DateTime
          LastAt: DateTime }

    /// The complete entry taxonomy. Order of cases doesn't matter
    /// for correctness; this listing matches the canonical
    /// architectural docs.
    ///
    /// Cycle 45 backlog (docs/USER-SETTINGS.md "ContentHistory
    /// semantic labels"): every entry will eventually carry a
    /// `Source` label distinguishing typed-input from
    /// cmd-output. The label powers "Output chunk 2 of 5" style
    /// navigation announces, `Ctrl+Shift+Up/Down` chunk jumps,
    /// and the maintainer's future "inject this past input into
    /// current input" action. Cheapest implementation: add a
    /// `Source: EntrySource` field to each *Data record; set it
    /// at append-time based on SessionModel's `ActiveTupleState`
    /// (`AwaitingCommandStart` → TypedInput; `EditingCommand` /
    /// `OutputStreaming` → CmdOutput). The "chunk index" labels
    /// can be added as a post-tuple-seal pass that walks the
    /// entries grouped by Source and writes back the index.
    type Entry =
        | TextSpan of TextSpanData
        | Newline of NewlineData
        | Overwrite of OverwriteData
        | Marker of MarkerData
        | Spinner of SpinnerData

    /// Accessor: every entry has a Seq.
    let entrySeq (e: Entry) : int64 =
        match e with
        | TextSpan d -> d.Seq
        | Newline d -> d.Seq
        | Overwrite d -> d.Seq
        | Marker d -> d.Seq
        | Spinner d -> d.Seq

    /// Tuneable knobs. Defaults chosen to mirror existing
    /// behaviour (e.g. Coalescer's 200 ms debounce window).
    type Parameters =
        { /// Idle window after the last `Print` event before the
          /// active TextSpan auto-seals via tick (not append).
          /// Set in milliseconds. Default 200.
          IdleSpanSealMs: int
          /// Hard cap on entry count per tuple before older
          /// entries are pruned (deferred Commit 2+; not yet
          /// enforced). Default 10_000.
          MaxEntriesPerTuple: int }

    let defaultParameters : Parameters =
        { IdleSpanSealMs = 200
          MaxEntriesPerTuple = 10_000 }

    /// State instance. One per active tuple. Mutated in-place by
    /// `appendFromEvent` / `appendMarker` / `tick` / `reset` under
    /// the `Gate` lock.
    ///
    /// `internal` getters/setters mirror `LinearTextStream`'s
    /// pattern: tests in the same assembly inspect state directly;
    /// callers outside the assembly use the module's functions.
    type T internal (parameters: Parameters) =
        member val internal Gate: obj = obj () with get
        member val internal Parameters: Parameters = parameters with get
        member val internal Entries: ResizeArray<Entry> = ResizeArray<Entry>() with get
        member val internal NextSeq: int64 = 0L with get, set
        /// Active TextSpan accumulator. Built up by sequential
        /// `Print` events; sealed (moved into `Entries`) on
        /// newline / marker / idle / overwrite.
        member val internal ActiveSpanText: StringBuilder = StringBuilder() with get
        member val internal ActiveSpanStartedAt: DateTime voption = ValueNone with get, set
        member val internal LastEventAt: DateTime = DateTime.MinValue with get, set
        /// CUB deferred-resolution: same shape as Cycle 44's bare-
        /// CR + CUB fixes in `LinearTextStream`. When a `CSI N D`
        /// arrives, we don't yet know whether the next event is a
        /// `Print` (a real in-place overwrite) or something else
        /// (cursor positioning during normal rendering). Defer.
        /// `ValueSome n` holds the CUB count; cleared on next
        /// event resolution.
        member val internal PendingCUB: int voption = ValueNone with get, set
        /// CR deferred-resolution: parallel to PendingCUB. Bare CR
        /// → overwrite of current line; CR+LF → newline.
        member val internal PendingCRDeferred: bool = false with get, set

    /// Construct a fresh ContentHistory bound to the supplied
    /// parameters. Caller binds the result to a `mutable` cell at
    /// the composition root.
    let create (parameters: Parameters) : T =
        T(parameters)

    /// Total entry count. Includes the unsealed active TextSpan
    /// only AFTER it seals (it's not in `Entries` while active).
    let count (state: T) : int =
        lock state.Gate (fun () -> state.Entries.Count)

    /// Snapshot of the entries array. Returns a fresh array each
    /// call (safe to enumerate without holding the gate).
    let snapshot (state: T) : Entry[] =
        lock state.Gate (fun () -> state.Entries.ToArray())

    /// The highest seq number assigned. -1 if empty.
    let latestSeq (state: T) : int64 =
        lock state.Gate (fun () ->
            if state.NextSeq = 0L then -1L
            else state.NextSeq - 1L)

    /// Look up an entry by Seq. Returns `None` if outside the
    /// active history range or if the entry has been pruned (cap-
    /// driven; Commit 2+).
    let entryBySeq (state: T) (target: int64) : Entry option =
        lock state.Gate (fun () ->
            let entries = state.Entries
            let mutable found = None
            // Linear scan; entries array is bounded by
            // MaxEntriesPerTuple. Future: binary search if perf
            // becomes a concern.
            let mutable i = 0
            while found.IsNone && i < entries.Count do
                if entrySeq entries.[i] = target then
                    found <- Some entries.[i]
                i <- i + 1
            found)

    /// Cycle 45c — most recent Marker entry of the given Kind, or
    /// `None` if absent. Walks `Entries` from tail. Used by
    /// `SessionModel.extractContentFromContentHistory` at
    /// tuple-seal time to locate the PromptStart + OutputStart
    /// boundaries of the just-sealed tuple.
    let tryLatestMarker (state: T) (kind: MarkerKind) : MarkerData option =
        lock state.Gate (fun () ->
            let entries = state.Entries
            let mutable found = None
            let mutable i = entries.Count - 1
            while found.IsNone && i >= 0 do
                match entries.[i] with
                | Marker m when m.Kind = kind -> found <- Some m
                | _ -> ()
                i <- i - 1
            found)

    /// Cycle 45c — reconstruct the user-visible text payload for
    /// entries whose Seq is strictly between `fromSeqExclusive`
    /// and `toSeqExclusive`. The caller typically passes adjacent
    /// Marker seqs (e.g. PromptStart.Seq, OutputStart.Seq) to
    /// extract a single bracketed region.
    ///
    /// Contribution per entry kind:
    ///   - `TextSpan` / `Overwrite` / `Spinner` → their text
    ///   - `Newline`                            → "\n"
    ///   - `Marker`                             → ""  (boundary
    ///                                                token only)
    ///
    /// The unsealed active TextSpan also contributes if its
    /// implicit Seq (`NextSeq`) is in-region — important at
    /// tuple-seal time when bytes have arrived since the most
    /// recent seal but the closing marker hasn't been appended
    /// yet.
    ///
    /// "User-visible" semantics: Overwrites contribute the
    /// post-overwrite text (not the original); Spinners
    /// contribute their `LatestText` frame. The reconstruction
    /// reflects what the user actually saw, not the byte trail.
    let sliceText
            (state: T)
            (fromSeqExclusive: int64)
            (toSeqExclusive: int64)
            : string =
        lock state.Gate (fun () ->
            let sb = StringBuilder()
            for entry in state.Entries do
                let seq = entrySeq entry
                if seq > fromSeqExclusive && seq < toSeqExclusive then
                    match entry with
                    | TextSpan d -> sb.Append(d.Text) |> ignore
                    | Newline _ -> sb.Append('\n') |> ignore
                    | Overwrite d -> sb.Append(d.Text) |> ignore
                    | Spinner d -> sb.Append(d.LatestText) |> ignore
                    | Marker _ -> ()
            // The unsealed active span's implicit Seq is NextSeq;
            // include its content iff that lands in-region.
            if state.ActiveSpanText.Length > 0 then
                let activeSeq = state.NextSeq
                if activeSeq > fromSeqExclusive
                   && activeSeq < toSeqExclusive then
                    sb.Append(state.ActiveSpanText.ToString())
                    |> ignore
            sb.ToString())

    /// Internal: allocate the next Seq.
    let private nextSeq (state: T) : int64 =
        let s = state.NextSeq
        state.NextSeq <- s + 1L
        s

    /// Seal the active TextSpan (if any) into `Entries`. Caller
    /// holds `state.Gate`. Returns the sealed entry, or `None` if
    /// no active span.
    let private sealActiveSpan
            (state: T)
            (at: DateTime)
            : Entry option =
        if state.ActiveSpanText.Length = 0 then None
        else
            let startedAt =
                match state.ActiveSpanStartedAt with
                | ValueSome t -> t
                | ValueNone -> at
            let entry =
                TextSpan
                    { Seq = nextSeq state
                      Text = state.ActiveSpanText.ToString()
                      StartedAt = startedAt
                      SettledAt = at }
            state.ActiveSpanText.Clear() |> ignore
            state.ActiveSpanStartedAt <- ValueNone
            state.Entries.Add(entry)
            Some entry

    /// Resolve any deferred CUB now that a new event has arrived.
    /// Caller holds `state.Gate`. Returns 0..1 Entry list; an
    /// Overwrite is appended iff the next event is a Print AND
    /// there's existing content in the active span to overwrite.
    let private resolveDeferredCUB
            (state: T)
            (now: DateTime)
            (nextEvent: VtEvent)
            : Entry list =
        match state.PendingCUB with
        | ValueNone -> []
        | ValueSome cubCount ->
            state.PendingCUB <- ValueNone
            match nextEvent with
            | Print _ ->
                // CUB+Print → in-place overwrite. Seal the active
                // span (it represents the content that's about to
                // be replaced) and emit an Overwrite entry that
                // points at it. The next Print event will start a
                // fresh active span.
                match sealActiveSpan state now with
                | None ->
                    // No active content to overwrite; treat as
                    // bare cursor positioning.
                    []
                | Some sealed' ->
                    let replacesSeq = entrySeq sealed'
                    let entry =
                        Overwrite
                            { Seq = nextSeq state
                              ReplacesSeq = replacesSeq
                              Text = "" // Filled by subsequent Print events into a fresh span; the Overwrite marker just records the boundary
                              At = now }
                    state.Entries.Add(entry)
                    [ sealed'; entry ]
            | _ ->
                // CUB followed by non-Print → bare cursor
                // positioning. No overwrite; no transition. The
                // accumulated active span continues unchanged.
                ignore cubCount
                []

    /// Resolve any deferred CR now that a new event has arrived.
    /// Caller holds `state.Gate`. Mirrors `LinearTextStream`'s CR
    /// deferred-resolution: CR+LF is a newline (no overwrite);
    /// bare CR is an overwrite signal for the current line.
    let private resolveDeferredCR
            (state: T)
            (now: DateTime)
            (nextEvent: VtEvent)
            : Entry list =
        if not state.PendingCRDeferred then []
        else
            state.PendingCRDeferred <- false
            match nextEvent with
            | Execute b when b = 0x0Auy ->
                // CR + LF → newline. The LF handler below will
                // emit the Newline entry; nothing to do here.
                []
            | _ ->
                // Bare CR → overwrite of current line. Seal the
                // active span (the content being overwritten);
                // subsequent Print events start a fresh span.
                match sealActiveSpan state now with
                | None -> []
                | Some sealed' ->
                    let entry =
                        Overwrite
                            { Seq = nextSeq state
                              ReplacesSeq = entrySeq sealed'
                              Text = ""
                              At = now }
                    state.Entries.Add(entry)
                    [ sealed'; entry ]

    /// Append a single VtEvent. Returns the list of entries that
    /// became visible (sealed TextSpans, Newlines, Overwrites)
    /// as a result. Most events return `[]` because the active
    /// TextSpan is still accumulating; entries materialise on
    /// seal boundaries (newline, marker insertion, idle).
    ///
    /// **Threading**: takes the gate. Safe to call from the
    /// single reader thread; concurrent reads (snapshot,
    /// entryBySeq, latestSeq) are safe.
    let appendFromEvent
            (state: T)
            (now: DateTime)
            (event: VtEvent)
            : Entry list =
        lock state.Gate (fun () ->
            // First, resolve any deferred CR/CUB from a prior
            // event. These must run BEFORE we process the new
            // event because their resolution depends on what the
            // new event is.
            let deferredCub = resolveDeferredCUB state now event
            let deferredCr = resolveDeferredCR state now event
            let preEntries = deferredCub @ deferredCr

            state.LastEventAt <- now

            let newEntries =
                match event with
                | Print rune ->
                    // Append to the active span. No new entry
                    // emitted until the span seals.
                    if state.ActiveSpanStartedAt.IsNone then
                        state.ActiveSpanStartedAt <- ValueSome now
                    state.ActiveSpanText.Append(rune.ToString())
                    |> ignore
                    []
                | Execute b when b = 0x0Auy ->
                    // LF → seal active span (if any), then emit a
                    // Newline entry.
                    let sealedEntry = sealActiveSpan state now
                    let nlEntry =
                        Newline { Seq = nextSeq state; At = now }
                    state.Entries.Add(nlEntry)
                    match sealedEntry with
                    | Some e -> [ e; nlEntry ]
                    | None -> [ nlEntry ]
                | Execute b when b = 0x0Duy ->
                    // CR → defer to next event.
                    state.PendingCRDeferred <- true
                    []
                | Execute b when b = 0x07uy ->
                    // BEL → seal active span (it's a chunk
                    // boundary) and emit a BellRang marker.
                    let sealedEntry = sealActiveSpan state now
                    let bellEntry =
                        Marker
                            { Seq = nextSeq state
                              Kind = MarkerKind.BellRang
                              At = now
                              Payload = None }
                    state.Entries.Add(bellEntry)
                    match sealedEntry with
                    | Some e -> [ e; bellEntry ]
                    | None -> [ bellEntry ]
                | CsiDispatch (_, _, 'D', _) ->
                    // CUB → defer to next event.
                    state.PendingCUB <- ValueSome 1
                    []
                | _ ->
                    // Everything else (other CSI, OSC, DCS, etc.)
                    // is currently ignored by ContentHistory.
                    // Commit 2 hooks OSC 133 + alt-screen toggles
                    // here to emit canonical markers; for now
                    // those events flow through detectors that
                    // call `appendMarker` explicitly.
                    []

            preEntries @ newEntries)

    /// Explicit marker insertion. Detectors call this when they
    /// fire (HeuristicPromptDetector → `PromptStart`,
    /// SelectionDetector → `SelectionShown` / `SelectionDismissed`,
    /// etc.). Seals the active TextSpan first so the marker's Seq
    /// is strictly after the preceding text content.
    ///
    /// Returns the list of entries that became visible (the
    /// sealed TextSpan if any, plus the new Marker).
    let appendMarker
            (state: T)
            (kind: MarkerKind)
            (at: DateTime)
            (payload: string option)
            : Entry list =
        lock state.Gate (fun () ->
            let sealedEntry = sealActiveSpan state at
            let markerEntry =
                Marker
                    { Seq = nextSeq state
                      Kind = kind
                      At = at
                      Payload = payload }
            state.Entries.Add(markerEntry)
            match sealedEntry with
            | Some e -> [ e; markerEntry ]
            | None -> [ markerEntry ])

    /// Time-driven seal: if the active TextSpan has been idle for
    /// at least `IdleSpanSealMs`, seal it. Caller polls this on
    /// the PeriodicTimer tick. Returns the sealed entry (if any).
    /// Used to commit accumulated text when the bytes pause, so
    /// SpeechCursor's AutoDrive can announce promptly during
    /// streaming output rather than waiting for an explicit
    /// boundary.
    let tick (state: T) (now: DateTime) : Entry list =
        lock state.Gate (fun () ->
            if state.ActiveSpanText.Length = 0 then []
            else
                let elapsed =
                    (now - state.LastEventAt).TotalMilliseconds
                if elapsed >= float state.Parameters.IdleSpanSealMs then
                    match sealActiveSpan state now with
                    | Some e -> [ e ]
                    | None -> []
                else
                    [])

    /// Reset to empty. Called when SessionModel transitions out
    /// of a sealed tuple so the next tuple starts fresh.
    let reset (state: T) : unit =
        lock state.Gate (fun () ->
            state.Entries.Clear()
            state.NextSeq <- 0L
            state.ActiveSpanText.Clear() |> ignore
            state.ActiveSpanStartedAt <- ValueNone
            state.LastEventAt <- DateTime.MinValue
            state.PendingCUB <- ValueNone
            state.PendingCRDeferred <- false)
