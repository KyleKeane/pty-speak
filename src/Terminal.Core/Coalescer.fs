namespace Terminal.Core

open System.Collections.Generic
open System.Text

/// Stage 5 — streaming-output coalescer (originally), now a
/// thin library of pure helpers + the `CoalescedNotification`
/// intermediate DU.
///
/// **Stage 8b restructuring (2026-05-04).** The Coalescer's
/// `State` record + four algorithm functions
/// (`processRowsChanged`, `onTimerTick`, `onModeChanged`,
/// `onParserError`) + `runLoop` orchestrator + per-instance
/// constants (`debounceWindow`, `spinnerWindow`,
/// `spinnerThreshold`) all moved into
/// `src/Terminal.Core/StreamProfile.fs` per the PR-N
/// substrate-cleanup contract. The constants became
/// `StreamProfile.Parameters` fields. The algorithms were
/// preserved verbatim — only their host module changed.
///
/// What this file retains:
/// - The `CoalescedNotification` DU. Used as the algorithms'
///   intermediate output type; the StreamProfile module
///   converts these to `(OutputEvent, ChannelDecision[])` pairs
///   for dispatch routing.
/// - Five pure hash + rendering helpers (`hashRowContent`,
///   `hashRow`, `hashAttrs`, `hashFrame`, `renderRows`). These
///   are stateless utilities the StreamProfile algorithms call;
///   they're named "Coalescer" by historical accident and kept
///   here for git-blame continuity. Future cleanup PR may rename
///   to a more neutral `StreamHelpers.fs` if the maintainer
///   prefers.
///
/// **PR-N substrate-cleanup contract (re-anchored 2026-05-04
/// in StreamProfile.fs):** the constants that USED to live here
/// at module scope are now on `StreamProfile.Parameters` —
/// `DebounceWindowMs`, `SpinnerWindowMs`, `SpinnerThreshold`,
/// `MaxAnnounceChars`. Caller-supplied at construction time;
/// `StreamProfile.defaultParameters` carries the Stage-7
/// values; the 9c TOML loader will override per
/// `[profile.stream]` when present.
module Coalescer =

    let private fnvOffsetBasis = 0xcbf29ce484222325UL
    let private fnvPrime = 0x00000100000001B3UL
    let private rowSwapMix = 0x9E3779B97F4A7C15UL

    /// Output type from the Stream profile's algorithm functions.
    /// `OutputBatch` carries post-debounce, post-spinner-suppress,
    /// post-cap announcement text. `ErrorPassthrough` carries a
    /// sanitised parser-error message. `ModeBarrier` signals a
    /// mode transition (alt-screen, etc.). The Stream profile's
    /// dispatch wrapper converts each into one or more
    /// `(OutputEvent, ChannelDecision[])` pairs the
    /// `OutputDispatcher` routes to channels.
    type CoalescedNotification =
        | OutputBatch of text: string
        | ErrorPassthrough of message: string
        | ModeBarrier of flag: TerminalModeFlag * value: bool

    /// Flatten SgrAttrs into a uint64 fingerprint. Stable across
    /// Cell instances with identical attrs.
    let internal hashAttrs (attrs: SgrAttrs) : uint64 =
        let colorHash (c: ColorSpec) : uint64 =
            match c with
            | Default -> 0UL
            | Indexed b -> 0x100UL ||| uint64 b
            | Rgb (r, g, b) ->
                0x1000000UL ||| (uint64 r <<< 16) ||| (uint64 g <<< 8) ||| uint64 b
        let mutable h = 0UL
        h <- h ^^^ colorHash attrs.Fg
        h <- h * fnvPrime
        h <- h ^^^ colorHash attrs.Bg
        h <- h * fnvPrime
        let flags =
            (if attrs.Bold then 1UL else 0UL)
            ||| (if attrs.Italic then 2UL else 0UL)
            ||| (if attrs.Underline then 4UL else 0UL)
            ||| (if attrs.Inverse then 8UL else 0UL)
        h <- h ^^^ flags
        h * fnvPrime

    /// PR-M (Issue #117) — content-only FNV-1a 64-bit over the
    /// cells, with NO row-index folding. Used by the cross-row
    /// spinner gate, which needs to recognise the same content
    /// landing at different rows across frames as the same hash.
    /// `hashRow` (with row-index fold) is used everywhere else
    /// for frame-hash computation + per-key spinner detection.
    let internal hashRowContent (cells: Cell[]) : uint64 =
        let mutable h = fnvOffsetBasis
        for cell in cells do
            h <- h ^^^ uint64 cell.Ch.Value
            h <- h * fnvPrime
            h <- h ^^^ hashAttrs cell.Attrs
            h <- h * fnvPrime
        h

    /// FNV-1a 64-bit folded with the row index. Used by frame-hash
    /// computation + per-key spinner detection.
    let internal hashRow (rowIdx: int) (cells: Cell[]) : uint64 =
        hashRowContent cells ^^^ (uint64 rowIdx * rowSwapMix)

    /// Compose row hashes into a frame hash. Order-independent
    /// XOR is correct here because each row hash already encodes
    /// its index via `^^^ (uint64 rowIdx * rowSwapMix)`.
    let internal hashFrame (rows: Cell[][]) : uint64 =
        let mutable h = 0UL
        for i in 0 .. rows.Length - 1 do
            h <- h ^^^ hashRow i rows.[i]
        h

    /// Render an array of `Cell[]` rows into the announcement
    /// string NVDA reads. Each row is sanitised individually
    /// through `AnnounceSanitiser.sanitise` (so PTY-originated
    /// BEL, ESC, BiDi, etc. are stripped from the row content)
    /// then joined with `\n`. The separator is added AFTER
    /// sanitisation so the row structure survives — sanitise
    /// strips `\n` as a C0 control, which would otherwise
    /// collapse multi-line output into a single line and defeat
    /// NVDA's per-line speech pause.
    let internal renderRows (rows: Cell[][]) : string =
        let sb = StringBuilder()
        // Drop trailing all-blank rows so a half-full screen
        // doesn't speak a wall of empty padding lines.
        let mutable lastRow = -1
        for r in 0 .. rows.Length - 1 do
            for c in 0 .. rows.[r].Length - 1 do
                if rows.[r].[c].Ch.Value <> int ' ' then lastRow <- r
        for r in 0 .. lastRow do
            if r > 0 then sb.Append('\n') |> ignore
            let row = rows.[r]
            // Find the rightmost non-blank cell to skip padding.
            let mutable lastCh = -1
            for c in 0 .. row.Length - 1 do
                if row.[c].Ch.Value <> int ' ' then lastCh <- c
            let rowSb = StringBuilder()
            for c in 0 .. lastCh do
                rowSb.Append(row.[c].Ch.ToString()) |> ignore
            sb.Append(AnnounceSanitiser.sanitise (rowSb.ToString())) |> ignore
        sb.ToString()
