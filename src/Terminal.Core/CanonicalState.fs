namespace Terminal.Core

/// Phase A — Layer 2 canonical-state substrate.
///
/// The substrate captures a single Screen snapshot and exposes
/// canonical representations of it that display pathways
/// (Layer 3) consume. v1 ships just Snapshot + RowHashes +
/// computeDiff; future Phase 2 / Phase 3 sub-stages add lazy
/// SemanticSegments + AiInterpretation fields without breaking
/// existing pathway implementations.
///
/// **Design note: stateless substrate, stateful pathways.**
/// The plan-file architectural draft sketched
/// `DiffSince: int64 -> CanonicalDiff` — substrate keeps
/// snapshot history indexed by sequence. Implementation chose
/// the functionally-equivalent inverse: substrate is mostly
/// stateless (current snapshot + current row hashes), pathway
/// stores its own `lastSeenRowHashes`, and the substrate's
/// `computeDiff` is a pure function `previousRowHashes ->
/// CanonicalDiff`. Cheaper memory footprint (no per-sequence
/// snapshot history); the pathway-side state is one uint64[]
/// of length screenRows.
///
/// **Algorithm reuse.** `Coalescer.hashRow` and
/// `Coalescer.hashRowContent` (originally Stage 5 / PR-M
/// infrastructure) provide the per-row hashes that drive
/// change detection. `Coalescer.renderRows`-style per-row
/// rendering produces the text. The substrate calls into
/// these helpers — Phase A doesn't add new algorithms.
module CanonicalState =

    open System.Text

    /// The diff between two snapshots, expressed as the rows
    /// that changed plus a text rendering of the changed rows
    /// only. v1 returns the changed rows joined by '\n' (matches
    /// `Coalescer.renderRows` per-row sanitisation contract).
    /// Future versions could surface row-by-row deltas for more
    /// sophisticated pathways (e.g., REPL prompt-aware diff).
    type CanonicalDiff =
        { /// Sorted, unique row indices whose hash differs from
          /// the supplied previous row hashes.
          ChangedRows: int[]
          /// Text rendering of the changed rows only. Empty
          /// string if `ChangedRows` is empty.
          ChangedText: string }

    /// The empty diff — no rows changed since the previous
    /// snapshot. Pathways receiving this should emit no
    /// OutputEvent.
    let emptyDiff : CanonicalDiff =
        { ChangedRows = [||]
          ChangedText = "" }

    /// Render a single row to its announcement-text form:
    /// trim trailing blank cells, walk cells to char string,
    /// sanitise via `AnnounceSanitiser`. Identical to the
    /// per-row block inside `renderChangedRows`; extracted so
    /// callers (including StreamPathway's suffix-diff path)
    /// can ask for one row at a time.
    ///
    /// Returns `""` for out-of-range row indices — the caller
    /// is responsible for guarding against negative or beyond-
    /// snapshot indices, but a defensive fallback avoids index
    /// exceptions.
    let internal renderRow (snapshot: Cell[][]) (rowIdx: int) : string =
        if rowIdx < 0 || rowIdx >= snapshot.Length then
            ""
        else
            let row = snapshot.[rowIdx]
            // Trim trailing blank cells in the row so end-of-
            // line padding doesn't leak into the announcement.
            let mutable lastCh = -1
            for c in 0 .. row.Length - 1 do
                if row.[c].Ch.Value <> int ' ' then lastCh <- c
            let rowSb = StringBuilder()
            for c in 0 .. lastCh do
                rowSb.Append(row.[c].Ch.ToString()) |> ignore
            AnnounceSanitiser.sanitise (rowSb.ToString())

    /// Render only the rows whose indices appear in
    /// `changedRows`. Mirrors `Coalescer.renderRows`'s per-row
    /// sanitisation + trailing-blank-cell trim, but operates
    /// on the changed-rows subset rather than the whole
    /// snapshot. Rows are emitted in `changedRows` order
    /// (caller is responsible for sorting if order matters;
    /// `computeDiff` returns sorted indices).
    let internal renderChangedRows
            (snapshot: Cell[][])
            (changedRows: int[])
            : string
            =
        if changedRows.Length = 0 then
            ""
        else
            let sb = StringBuilder()
            let mutable first = true
            for rowIdx in changedRows do
                if rowIdx >= 0 && rowIdx < snapshot.Length then
                    if not first then sb.Append('\n') |> ignore
                    sb.Append(renderRow snapshot rowIdx) |> ignore
                    first <- false
            sb.ToString()

    /// Pure function: given a current snapshot's row hashes
    /// (using `Coalescer.hashRow`'s position-aware hashing) and
    /// the previous-known row hashes from a pathway, return
    /// the indices of rows that changed and the rendered text
    /// of those rows.
    ///
    /// **First-call semantics.** When `previousRowHashes` is
    /// empty (a pathway's initial state), every row in the
    /// current snapshot is considered "changed" — the diff
    /// returns all row indices and the full snapshot text.
    /// This matches the plan's open-question position #1
    /// (emit the first snapshot in full).
    ///
    /// **Length-mismatch semantics.** If the previous hash
    /// array is shorter than the current snapshot (e.g., a
    /// future stage resizes the screen mid-session), missing
    /// indices are treated as "different" — those rows show
    /// up in the diff.
    let internal computeDiffFromHashes
            (snapshot: Cell[][])
            (currentRowHashes: uint64[])
            (previousRowHashes: uint64[])
            : CanonicalDiff
            =
        let changed = ResizeArray<int>()
        for i in 0 .. currentRowHashes.Length - 1 do
            let prevHash =
                if i < previousRowHashes.Length then
                    Some previousRowHashes.[i]
                else
                    None
            match prevHash with
            | Some h when h = currentRowHashes.[i] -> ()
            | _ -> changed.Add(i)
        if changed.Count = 0 then
            emptyDiff
        else
            let rows = changed.ToArray()
            { ChangedRows = rows
              ChangedText = renderChangedRows snapshot rows }

    /// The canonical state record. v1 shape:
    /// - `Snapshot`: full Cell[][] of the current screen
    /// - `SequenceNumber`: from Screen.SnapshotRows; monotonic
    /// - `RowHashes`: per-row hashes (Coalescer.hashRow,
    ///   position-aware) for fast change detection
    /// - `ContentHashes`: per-row content-only hashes
    ///   (Coalescer.hashRowContent) for cross-row dedup;
    ///   exposed for pathways that want position-independent
    ///   change detection (e.g., scrolling-content pathways)
    /// - `computeDiff`: pure function the pathway calls with
    ///   its previously-seen row hashes
    ///
    /// Phase 2 will add `SemanticSegments: Lazy<Segment[]>`.
    /// Phase 3 will add `AiInterpretation: Lazy<string option>`.
    /// Both are purely additive — existing pathways read only
    /// the v1 fields; new pathways opt into the lazy fields
    /// when their producers ship.
    type Canonical =
        { Snapshot: Cell[][]
          SequenceNumber: int64
          RowHashes: uint64[]
          ContentHashes: uint64[]
          computeDiff: uint64[] -> CanonicalDiff }

    /// Build a canonical state from a Screen snapshot. The
    /// row hashes are computed eagerly (cheap — FNV-1a per
    /// row) and cached in the record so `computeDiff` reuses
    /// them rather than re-walking the snapshot each call.
    let create (snapshot: Cell[][]) (sequenceNumber: int64) : Canonical =
        let rowHashes =
            Array.init snapshot.Length (fun i -> Coalescer.hashRow i snapshot.[i])
        let contentHashes =
            Array.init snapshot.Length (fun i -> Coalescer.hashRowContent snapshot.[i])
        { Snapshot = snapshot
          SequenceNumber = sequenceNumber
          RowHashes = rowHashes
          ContentHashes = contentHashes
          computeDiff =
            fun previousRowHashes ->
                computeDiffFromHashes snapshot rowHashes previousRowHashes }
