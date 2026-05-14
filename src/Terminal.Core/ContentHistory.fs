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

    /// Cycle 48 PR-C (ADR 0003 §9.5) — provenance tag on every
    /// `Entry`. Defined here (substrate-level) rather than in
    /// `ShellInteraction` because every Entry carries one and
    /// `ContentHistory` is upstream of the state machine in
    /// the compile order.
    ///
    /// The tag is set at append time by reading the current
    /// `InteractionState` via `setSourceResolver` (a delegate
    /// the composition root provides). Pre-state-machine
    /// entries get `Unknown`. Markers get `BoundaryMarker`
    /// unconditionally.
    [<RequireQualifiedAccess>]
    type EntrySource =
        /// Cmd's echo of bytes the user typed (or the
        /// diagnostic battery wrote). Suppressed from
        /// announces and SpeechCursor navigation.
        | UserInputEcho
        /// Bytes cmd produced as command output during
        /// `Executing`. Announced + navigable.
        | CmdOutput
        /// Bytes cmd produced as a sub-prompt (set/p, pause,
        /// choice). Announced; sub-prompt detector marked the
        /// transition.
        | CmdSubPrompt
        /// The shell's PS1 / prompt path bytes.
        | ShellPrompt
        /// A semantic boundary marker entry. Not text;
        /// always navigable in the review cursor. Renamed from
        /// `Marker` (PR-C initial draft) to avoid shadowing
        /// `ContentHistory.Entry.Marker` at qualified-access
        /// when `EntrySource` is `[<RequireQualifiedAccess>]`.
        | BoundaryMarker
        /// Pre-state-machine fallback. Entries that landed
        /// before the resolver was wired (or while the
        /// resolver returned an unresolvable state) get this
        /// value.
        | Unknown

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
    /// `Source` per Cycle 48 PR-C (ADR 0003 §9.5).
    type TextSpanData =
        { Seq: int64
          Text: string
          StartedAt: DateTime
          SettledAt: DateTime
          Source: EntrySource }

    /// A newline boundary in the active tuple's output. Carries
    /// only the seq + timestamp; the actual `\n` byte is logically
    /// at the end of the preceding TextSpan. `Source` per Cycle
    /// 48 PR-C.
    type NewlineData =
        { Seq: int64
          At: DateTime
          Source: EntrySource }

    /// An in-place overwrite of a prior `TextSpan`. The
    /// `ReplacesSeq` points at the TextSpan whose visible position
    /// is being overwritten. This is how genuine in-place patterns
    /// (e.g. spinner frames, progress bar updates) are represented
    /// — they DO NOT mutate the prior TextSpan. The history is
    /// strictly append-only; "what was already spoken" remains
    /// addressable by its Seq. `Source` per Cycle 48 PR-C.
    type OverwriteData =
        { Seq: int64
          ReplacesSeq: int64
          Text: string
          At: DateTime
          Source: EntrySource }

    /// A semantic event emitted by a detector or by the substrate
    /// itself. The `Payload` is optional — most markers carry only
    /// their kind, but some (e.g. `SelectionShown` carrying the
    /// option list) need additional data. Markers always have
    /// `Source = EntrySource.BoundaryMarker`; field included for shape
    /// uniformity with the other Data records.
    type MarkerData =
        { Seq: int64
          Kind: MarkerKind
          At: DateTime
          Payload: string option
          Source: EntrySource }

    /// A coalesced sequence of `Overwrite` entries that cycle
    /// through a small set of glyph patterns at high frequency —
    /// i.e., a spinner. Replaces the run of Overwrites with a
    /// single entry so SpeechCursor can skip them in AutoDrive
    /// mode without N redundant announces. Detection is deferred
    /// to a follow-up (Commit 2 or later); the entry shape ships
    /// in Commit 1 so the data model is complete. `Source` per
    /// Cycle 48 PR-C.
    type SpinnerData =
        { Seq: int64
          LatestText: string
          FrameCount: int
          FirstAt: DateTime
          LastAt: DateTime
          Source: EntrySource }

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
        /// PR-Q (2026-05-14) — `EntrySource` captured at the
        /// first byte of the active TextSpan. The seal-time
        /// resolver was racy for sub-prompts: the script's
        /// `set /p` prompt text (no trailing newline) idle-
        /// sealed AFTER `SubPromptIdle` flipped
        /// `ShellInteraction` to `Composing`, so the resolver
        /// returned `UserInputEcho` even though the bytes
        /// arrived during `Executing`. Sealing now uses this
        /// first-byte snapshot instead. Reset to `ValueNone`
        /// on seal/clear.
        member val internal ActiveSpanSource: EntrySource voption = ValueNone with get, set
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
        /// Cycle 47 follow-up (2026-05-13) post-preview.116 —
        /// logical cursor row, tracked across CSI / ESC cursor-
        /// positioning events so the renderer can synthesise a
        /// `Newline` entry between visual rows that cmd's conpty
        /// translator emits as cursor-positioning sequences
        /// instead of CRLF (the banner-collapse + output-
        /// concatenated-with-next-prompt failure mode from
        /// preview.116). Increments on `Execute 0x0A` (LF),
        /// updates on CSI `H`/`f`/`A`/`B`/`E`/`F` and ESC
        /// `D`/`E`/`M`. Pure substrate state — has no semantic
        /// meaning beyond "did the cursor change row between
        /// successive events?"
        member val internal LogicalCursorRow: int = 0 with get, set
        /// Cycle 48 PR-C — source-resolver delegate. The
        /// composition root sets this via `setSourceResolver`
        /// after the `ShellInteraction.State` is constructed.
        /// Returns the `EntrySource` to tag non-marker entries
        /// with; called once per append-from-event under the
        /// gate. Defaults to `ValueNone` → `EntrySource.Unknown`.
        member val internal SourceResolver: (unit -> EntrySource) voption =
            ValueNone with get, set

    /// Construct a fresh ContentHistory bound to the supplied
    /// parameters. Caller binds the result to a `mutable` cell at
    /// the composition root.
    let create (parameters: Parameters) : T =
        T(parameters)

    /// Cycle 48 PR-C — wire the source-resolver delegate. The
    /// composition root calls this after constructing both
    /// `ContentHistory` and `ShellInteraction.State`. The
    /// delegate reads `ShellInteraction.State.Current` and maps
    /// it to an `EntrySource`. Without this call, all entries
    /// get `EntrySource.Unknown` (acceptable for tests + early
    /// startup; live sessions wire it).
    let setSourceResolver
            (state: T) (resolver: unit -> EntrySource) : unit =
        lock state.Gate (fun () ->
            state.SourceResolver <- ValueSome resolver)

    /// Resolve the source via the delegate; default to
    /// `Unknown` if unset.
    let private resolveSource (state: T) : EntrySource =
        match state.SourceResolver with
        | ValueSome f ->
            try f () with _ -> EntrySource.Unknown
        | ValueNone -> EntrySource.Unknown

    /// Accessor: every entry has a Source.
    let entrySource (e: Entry) : EntrySource =
        match e with
        | TextSpan d -> d.Source
        | Newline d -> d.Source
        | Overwrite d -> d.Source
        | Marker d -> d.Source
        | Spinner d -> d.Source

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

    /// Cycle 51 spike (Phase 0) — sealed entries strictly above
    /// `fromSeqExclusive` in append-time (== Seq-ascending) order,
    /// plus a synthetic `TextSpan` entry for the unsealed active
    /// span if its implicit Seq is in-range. The synthetic entry
    /// uses the captured `ActiveSpanSource` (or `Unknown` if the
    /// resolver never ran). Lock-safe; returns a fresh array.
    ///
    /// Used by `SessionModel.extractIOCell` to classify per-byte
    /// provenance into command vs output regions without going
    /// through `sliceText`'s byte-stream flatten — the spike needs
    /// each entry's `EntrySource` tag, which the flat string
    /// representation strips.
    let entriesAfter
            (state: T)
            (fromSeqExclusive: int64)
            : Entry[] =
        lock state.Gate (fun () ->
            let result = ResizeArray<Entry>()
            for entry in state.Entries do
                if entrySeq entry > fromSeqExclusive then
                    result.Add(entry)
            if state.ActiveSpanText.Length > 0 then
                let activeSeq = state.NextSeq
                if activeSeq > fromSeqExclusive then
                    let src =
                        match state.ActiveSpanSource with
                        | ValueSome s -> s
                        | ValueNone -> EntrySource.Unknown
                    let startedAt =
                        match state.ActiveSpanStartedAt with
                        | ValueSome t -> t
                        | ValueNone -> DateTime.UtcNow
                    let synthetic =
                        TextSpan
                            { Seq = activeSeq
                              Text = state.ActiveSpanText.ToString()
                              StartedAt = startedAt
                              SettledAt = DateTime.UtcNow
                              Source = src }
                    result.Add(synthetic)
            result.ToArray())

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

    /// Cycle 47 follow-up (2026-05-13) — render a `Marker` entry
    /// as a labelled boundary line suitable for the UIA
    /// Text-pattern materialiser (where NVDA's review cursor
    /// navigates). `tailText` skips markers entirely (they're
    /// metadata-only for the substrate); `tailTextWithMarkers`
    /// calls this helper so each marker becomes its own
    /// navigable line in the materialised tail.
    let private renderMarkerLine (kind: MarkerKind) : string =
        // Cycle 47 follow-up (2026-05-13) — relabelled per
        // maintainer's preview.114 review feedback. The four
        // input/output boundary markers use a parallel
        // "begin X / end X" wording so the review cursor walking
        // a session can answer "where am I in the prompt /
        // command / output structure" by line. PromptStart and
        // CommandStart bracket the prompt area; OutputStart and
        // CommandFinished bracket the output area. For cmd
        // (no OSC 133), only PromptStart is heuristic-detected
        // directly; CommandFinished is synthesised at the
        // PromptStart-while-AwaitingCommandStart transition in
        // `Program.fs`'s boundary handler so the user sees
        // `--- end output ---` between commands. CommandStart
        // and OutputStart remain unsynthesised for cmd (no
        // reliable signal) and appear only under shells that
        // emit OSC 133.
        let label =
            match kind with
            | MarkerKind.PromptStart -> "begin prompt"
            | MarkerKind.CommandStart -> "end prompt"
            | MarkerKind.OutputStart -> "begin output"
            | MarkerKind.CommandFinished -> "end output"
            | MarkerKind.BellRang -> "bell"
            | MarkerKind.SelectionShown -> "selection prompt"
            | MarkerKind.SelectionDismissed -> "selection dismissed"
            | MarkerKind.AltScreenEnter -> "entered alt-screen"
            | MarkerKind.AltScreenExit -> "left alt-screen"
            | MarkerKind.Custom tag -> sprintf "custom: %s" tag
        // Leading + trailing newlines guarantee the marker
        // stands on its own line regardless of whether the
        // prior / following content ends with `\n`. Multiple
        // consecutive newlines collapse harmlessly under
        // NVDA's line-walking semantics (blank lines read as
        // "blank" but don't break Move(Line, ±1)).
        sprintf "\n--- %s ---\n" label

    /// Cycle 47 follow-up (2026-05-13) — shared implementation
    /// for `tailText` (markers stripped, used by the diagnostic
    /// bundle) and `tailTextWithMarkers` (markers rendered as
    /// labelled lines, used by the UIA Text-pattern
    /// materialiser). Pre-this-cycle this body lived inline in
    /// `tailText`; refactored to a private helper + two public
    /// wrappers so the bundle and UIA paths can diverge on
    /// marker rendering without diverging on the byte-cap +
    /// tail-walk machinery.
    let private tailTextInternal
            (state: T)
            (maxBytes: int)
            (includeMarkers: bool)
            (includeActiveSpan: bool)
            : string =
        lock state.Gate (fun () ->
            let frames = ResizeArray<string>()
            let mutable bytes = 0
            // Active span first (it's the most recent content).
            // Cycle 47 follow-up (2026-05-13) post-preview.114 —
            // `includeActiveSpan` gates whether the unsealed
            // active TextSpan participates in the rendered tail.
            // The UIA Text-pattern materialiser sets this to
            // `false` during the user's typing window so NVDA's
            // periodic `ITextProvider` polling doesn't see the
            // mid-keystroke deltas + announce them as inserted
            // text (the preview.114 "words being read aloud while
            // I type" failure mode). Substrate consumers
            // (diagnostic bundle, SpeechCursor manual nav) keep
            // `true` so paste-back triage + review-cursor
            // navigation still surface the in-progress span.
            if includeActiveSpan && state.ActiveSpanText.Length > 0 then
                let text = state.ActiveSpanText.ToString()
                frames.Add(text)
                bytes <- bytes + System.Text.Encoding.UTF8.GetByteCount(text)
            let entries = state.Entries
            let mutable i = entries.Count - 1
            while bytes < maxBytes && i >= 0 do
                let contribution =
                    match entries.[i] with
                    | TextSpan d -> d.Text
                    | Newline _ -> "\n"
                    | Overwrite d -> d.Text
                    | Spinner d -> d.LatestText
                    | Marker m ->
                        if includeMarkers then renderMarkerLine m.Kind
                        else ""
                if contribution.Length > 0 then
                    frames.Add(contribution)
                    bytes <- bytes + System.Text.Encoding.UTF8.GetByteCount(contribution)
                i <- i - 1
            // frames is tail-first; reverse for chronological order.
            let sb = StringBuilder(bytes)
            for j = frames.Count - 1 downto 0 do
                sb.Append(frames.[j]) |> ignore
            let full = sb.ToString()
            // Final truncation: if we overshot maxBytes (the last
            // included entry may have pushed us over), trim from
            // the head (oldest) so the most recent content is
            // preserved.
            if System.Text.Encoding.UTF8.GetByteCount(full) <= maxBytes then
                full
            else
                // Walk char-by-char from the tail until we've fit.
                let bytesArr = System.Text.Encoding.UTF8.GetBytes(full)
                let startOffset = bytesArr.Length - maxBytes
                System.Text.Encoding.UTF8.GetString(
                    bytesArr, startOffset, maxBytes))

    /// Cycle 45c — tail of the reconstructed text. Used by the
    /// diagnostic bundle to render a `--- CONTENT HISTORY (last
    /// N KB) ---` section without forcing the caller to allocate
    /// the full reconstruction first. Markers are stripped; for
    /// the markers-rendered variant see `tailTextWithMarkers`.
    /// Includes the active (unsealed) span — paste-back triage
    /// expects to see whatever the user just typed / cmd just
    /// printed mid-stream.
    let tailText (state: T) (maxBytes: int) : string =
        tailTextInternal state maxBytes false true

    /// Cycle 47 follow-up (2026-05-13) — variant of `tailText`
    /// that renders `Marker` entries as labelled boundary lines
    /// (`--- prompt ---`, `--- input begins ---`, etc.) rather
    /// than skipping them. Used by the UIA Text-pattern
    /// materialiser so NVDA's review cursor surfaces semantic
    /// boundaries between commands. The diagnostic snapshot
    /// keeps using `tailText` (no marker noise) so paste-back
    /// triage stays readable.
    let tailTextWithMarkers (state: T) (maxBytes: int) : string =
        tailTextInternal state maxBytes true true

    /// Cycle 47 follow-up (2026-05-13) post-preview.114 —
    /// markers-rendered tail variant that EXCLUDES the active
    /// (unsealed) TextSpan. Used by the UIA Text-pattern
    /// materialiser during the user's typing window so NVDA's
    /// `ITextProvider` polling sees stable content across
    /// successive captures even as cmd echoes each typed
    /// character back into the active span. Without this gate,
    /// the maintainer's preview.114 dogfood surfaced NVDA
    /// reading aloud each accreting prefix
    /// (`"e"`, `"ec"`, `"ech"`, `"echo"`, ...) as if it were
    /// inserted text — distinct from NVDA's keyboard-hook
    /// `Speak typed characters` behaviour and therefore not
    /// silenceable via the user's NVDA settings.
    let tailTextWithMarkersSealedOnly
            (state: T) (maxBytes: int) : string =
        tailTextInternal state maxBytes true false

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
            // PR-Q (2026-05-14) — use the source captured at the
            // active span's FIRST byte, not the current
            // resolver. Seal-time resolution mis-tagged
            // `set /p` prompts as `UserInputEcho` because the
            // idle-seal fired AFTER `SubPromptIdle` flipped
            // `ShellInteraction` to `Composing`. Fall back to
            // the resolver only if no first-byte source was
            // recorded (defensive — shouldn't happen in
            // practice since `appendFromEvent`'s first-byte
            // branch sets it alongside `ActiveSpanStartedAt`).
            let resolvedSource =
                match state.ActiveSpanSource with
                | ValueSome s -> s
                | ValueNone -> resolveSource state
            let entry =
                TextSpan
                    { Seq = nextSeq state
                      Text = state.ActiveSpanText.ToString()
                      StartedAt = startedAt
                      SettledAt = at
                      Source = resolvedSource }
            state.ActiveSpanText.Clear() |> ignore
            state.ActiveSpanStartedAt <- ValueNone
            state.ActiveSpanSource <- ValueNone
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
                              At = now
                              Source = resolveSource state }
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
                              At = now
                              Source = resolveSource state }
                    state.Entries.Add(entry)
                    [ sealed'; entry ]

    /// Cycle 47 follow-up (2026-05-13) post-preview.116 — compute
    /// the cursor row AFTER `event` is applied, starting from
    /// `currentRow`. Used to detect visual-row changes that
    /// aren't carried by LF (e.g. cmd's conpty translator
    /// emitting CSI cursor-position sequences between the
    /// banner's `(c) Microsoft …` line and the first prompt
    /// instead of CRLF — the bug that mashed the entire banner
    /// + first prompt into one ContentHistory TextSpan).
    ///
    /// Only the row-changing cases are enumerated; column-only
    /// moves (`CUF` / `CUB`) and unrelated CSI dispatches
    /// (`SGR`, `DECSET`, etc.) return `currentRow` unchanged.
    /// Caller's `appendFromEvent` uses the delta as the seal
    /// trigger.
    let private cursorRowAfter (event: VtEvent) (currentRow: int) : int =
        match event with
        | Execute b when b = 0x0Auy ->
            currentRow + 1
        | CsiDispatch (parms, _, 'H', None)
        | CsiDispatch (parms, _, 'f', None) ->
            let row1 =
                if parms.Length >= 1 && parms.[0] > 0 then parms.[0]
                else 1
            row1 - 1
        | CsiDispatch (parms, _, 'A', None) ->
            let n =
                if parms.Length >= 1 && parms.[0] > 0 then parms.[0]
                else 1
            max 0 (currentRow - n)
        | CsiDispatch (parms, _, 'B', None) ->
            let n =
                if parms.Length >= 1 && parms.[0] > 0 then parms.[0]
                else 1
            currentRow + n
        | CsiDispatch (parms, _, 'E', None) ->
            let n =
                if parms.Length >= 1 && parms.[0] > 0 then parms.[0]
                else 1
            currentRow + n
        | CsiDispatch (parms, _, 'F', None) ->
            let n =
                if parms.Length >= 1 && parms.[0] > 0 then parms.[0]
                else 1
            max 0 (currentRow - n)
        | EscDispatch (intermediates, 'D') when intermediates.Length = 0 ->
            currentRow + 1
        | EscDispatch (intermediates, 'E') when intermediates.Length = 0 ->
            currentRow + 1
        | EscDispatch (intermediates, 'M') when intermediates.Length = 0 ->
            max 0 (currentRow - 1)
        | _ ->
            currentRow

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

            // Cycle 47 follow-up (2026-05-13) post-preview.116 —
            // cursor-row-change synthetic newline. Compute the
            // row the cursor will sit on after this event and
            // compare to the previous tick. If the row changed
            // AND it wasn't an LF (which has its own seal +
            // emit-Newline path below) AND the active span has
            // content, seal the span and emit a synthetic
            // Newline so the rendered tail reflects the visual
            // row break that cmd's conpty translator carried
            // via CSI cursor-positioning instead of CRLF.
            let isLf =
                match event with
                | Execute b when b = 0x0Auy -> true
                | _ -> false
            let oldRow = state.LogicalCursorRow
            let newRow = cursorRowAfter event oldRow
            state.LogicalCursorRow <- newRow
            let rowChangeEntries =
                if not isLf
                   && newRow <> oldRow
                   && state.ActiveSpanText.Length > 0 then
                    match sealActiveSpan state now with
                    | Some sealed' ->
                        let nl =
                            Newline { Seq = nextSeq state; At = now; Source = resolveSource state }
                        state.Entries.Add(nl)
                        [ sealed'; nl ]
                    | None -> []
                else []

            let preEntries = deferredCub @ deferredCr @ rowChangeEntries

            state.LastEventAt <- now

            let newEntries =
                match event with
                | Print rune ->
                    // Append to the active span. No new entry
                    // emitted until the span seals.
                    if state.ActiveSpanStartedAt.IsNone then
                        state.ActiveSpanStartedAt <- ValueSome now
                        // PR-Q (2026-05-14) — capture the source
                        // at the first byte. `sealActiveSpan`
                        // consumes this snapshot, avoiding the
                        // sub-prompt mis-tag race where idle-
                        // seal fired AFTER `SubPromptIdle`
                        // flipped state to `Composing`.
                        state.ActiveSpanSource <- ValueSome (resolveSource state)
                    state.ActiveSpanText.Append(rune.ToString())
                    |> ignore
                    []
                | Execute b when b = 0x0Auy ->
                    // LF → seal active span (if any), then emit a
                    // Newline entry.
                    let sealedEntry = sealActiveSpan state now
                    let nlEntry =
                        Newline { Seq = nextSeq state; At = now; Source = resolveSource state }
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
                              Payload = None
                              Source = EntrySource.BoundaryMarker }
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
                      Payload = payload
                      Source = EntrySource.BoundaryMarker }
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
            state.ActiveSpanSource <- ValueNone
            state.LastEventAt <- DateTime.MinValue
            state.PendingCUB <- ValueNone
            state.PendingCRDeferred <- false
            state.LogicalCursorRow <- 0)
