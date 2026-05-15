module PtySpeak.Tests.Unit.SessionModelTests

open System
open Xunit
open Terminal.Core
open Terminal.Parser

// ---------------------------------------------------------------------
// SessionModel — state machine + Cycle 51 IOCell pivot (ADR 0004)
// ---------------------------------------------------------------------
//
// Cycle 51 PR-W renamed the cell type to `IOCell`, added the
// `IOCellPhase` DU + `CellSequence` field, deleted the screen-row
// `extractContent` fallback, and made `ContentHistory` the SOLE
// extraction substrate with a strict drop-on-None contract: when
// the substrate has no authoritative (command, output) slice the
// cell does NOT finalize (loud silence beats stale-scrollback
// garbage).
//
// Consequence for tests: the legacy `apply` / `applyAndCapture` /
// `finalizeIncomplete` surface (which passes no ContentHistory)
// NEVER finalises a cell post-pivot — it drops. Finalize coverage
// (timestamps, metadata accumulation, ring-buffer eviction, exit
// code) now drives through `applyAndCaptureWithContentHistory`
// with a populated ContentHistory, which is the production path
// (`Program.fs handlePromptBoundary`). The pure state-machine
// transition coverage (Active advances on PromptStart /
// CommandStart / OutputStart) is unchanged.

// ---------------------------------------------------------------------
// Test fixture builders
// ---------------------------------------------------------------------

let private boundary
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = None
      MatchedRowIndex = None }

let private boundaryWith
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (commandId: string option)
        (extras: (string * string) list)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = commandId
      ExtraParams = Map.ofList extras
      MatchedRowText = None
      MatchedRowIndex = None }

/// Boundary with explicit `MatchedRowText` (the rendered prompt
/// row). PromptStart uses it to populate `IOCell.PromptText`;
/// CommandFinished uses it as the new-prompt tail the
/// heuristic-only extractor trims off the ContentHistory slice.
let private boundaryWithText
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (matchedText: string)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = Some matchedText
      MatchedRowIndex = None }

let private heuristicBoundary
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (stabilityMs: int)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.HeuristicPromptRegex stabilityMs
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = None
      MatchedRowIndex = None }

let private t0 = DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
let private after (ms: int) = t0.AddMilliseconds(float ms)

// Cell-grid fixture builders (still used by the PromptRowIndex
// capture + active-only clipboard tests; the screen-row
// *extraction* they once exercised was deleted in PR-W).

let private blankCell : Cell = Cell.blank

let private cellOf (ch: char) : Cell =
    { Ch = System.Text.Rune ch; Attrs = SgrAttrs.defaults }

let private blankRow (cols: int) : Cell[] =
    Array.create cols blankCell

let private rowOf (cols: int) (s: string) : Cell[] =
    let row = blankRow cols
    for i in 0 .. min (s.Length - 1) (cols - 1) do
        row.[i] <- cellOf s.[i]
    row

let private snapshotOf
        (rows: int)
        (cols: int)
        (lines: string list)
        : Cell[][]
        =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

let private boundaryWithRow
        (kind: BoundaryKind)
        (detectedAt: DateTime)
        (rowIndex: int)
        (matchedText: string)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.HeuristicPromptRegex 100
      DetectedAt = detectedAt
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = Some matchedText
      MatchedRowIndex = Some rowIndex }

// --- ContentHistory substrate helpers (the post-pivot finalize
// --- path; mirrors `Program.fs handlePromptBoundary`).

let private freshHistory () : ContentHistory.T =
    ContentHistory.create ContentHistory.defaultParameters

let private feedHistoryBytes
        (history: ContentHistory.T)
        (now: DateTime)
        (bytes: byte[])
        : ContentHistory.T =
    let parser = Terminal.Parser.Parser.create ()
    let events = Terminal.Parser.Parser.feedArray parser bytes
    for ev in events do
        ContentHistory.appendFromEvent history now ev |> ignore
    history

let private appendHistoryMarker
        (history: ContentHistory.T)
        (now: DateTime)
        (kind: ContentHistory.MarkerKind)
        : ContentHistory.T =
    ContentHistory.appendMarker history kind now None |> ignore
    history

/// Drive one full PromptStart → CommandStart → CommandFinished
/// cycle through the ContentHistory substrate so the cell
/// actually finalises (post-pivot the legacy `apply` path always
/// drops — ADR 0004 drop-on-None). Heuristic-only arm: a single
/// PromptStart marker, then a `cmd\nout\nnewPrompt` blob; the
/// extractor slices PromptStart→tail, trims `newPrompt`, splits
/// on the first newline. Returns the threaded state + the
/// finalised cell (if any).
let private runCellThroughCH
        (state: SessionModel.T)
        (startMs: int)
        (promptText: string)
        (cmd: string)
        (out: string)
        (newPrompt: string)
        (exitCode: int option)
        : SessionModel.T * SessionModel.IOCell option =
    let history = freshHistory ()
    let history =
        appendHistoryMarker
            history (after startMs) ContentHistory.MarkerKind.PromptStart
    let blob = sprintf "%s\n%s\n%s" cmd out newPrompt
    let history =
        feedHistoryBytes
            history (after (startMs + 5))
            (System.Text.Encoding.ASCII.GetBytes blob)
    let s1 =
        SessionModel.applyWithContentHistory
            state
            (boundaryWithText BoundaryKind.PromptStart (after startMs) promptText)
            [||] history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1
            (boundary BoundaryKind.CommandStart (after (startMs + 100)))
            [||] history true (-1L)
    SessionModel.applyAndCaptureWithContentHistory
        s2
        (boundaryWithText
            (BoundaryKind.CommandFinished exitCode)
            (after (startMs + 200))
            newPrompt)
        [||] history true (-1L)

/// Hand-construct a sealed IOCell for the clipboard formatter
/// tests (which need a populated History without driving the
/// state machine).
let private mkCell
        (seq: int64)
        (prompt: string)
        (cmd: string)
        (out: string)
        (exit: int option)
        : SessionModel.IOCell =
    { Id = Guid.NewGuid()
      CellSequence = seq
      CommandId = None
      Phase = SessionModel.IOCellPhase.Sealed
      ShellId = "cmd"
      PromptStartedAt = t0
      CommandStartedAt = Some (after 100)
      OutputStartedAt = Some (after 200)
      CommandFinishedAt = Some (after 300)
      PromptText = prompt
      CommandText = cmd
      OutputText = out
      ExitCode = exit
      Sources = Map.empty
      ExtraParams = Map.empty }

let private mkState (cells: SessionModel.IOCell list) : SessionModel.T =
    let q = System.Collections.Generic.Queue<SessionModel.IOCell>()
    for c in cells do q.Enqueue c
    { ShellId = "cmd"
      SessionId = Guid.NewGuid()
      SessionStartedAt = t0
      History = q
      MaxHistorySize = 100
      Active = None
      IsAltScreenActive = false
      NextCellSequence = int64 (List.length cells) }

// ---------------------------------------------------------------------
// Substrate types (preserved from Tier 1.A)
// ---------------------------------------------------------------------

[<Fact>]
let ``BoundaryKind.PromptStart constructs cleanly`` () =
    let k = BoundaryKind.PromptStart
    Assert.Equal(BoundaryKind.PromptStart, k)

[<Fact>]
let ``BoundaryKind.CommandStart constructs cleanly`` () =
    let k = BoundaryKind.CommandStart
    Assert.Equal(BoundaryKind.CommandStart, k)

[<Fact>]
let ``BoundaryKind.OutputStart constructs cleanly`` () =
    let k = BoundaryKind.OutputStart
    Assert.Equal(BoundaryKind.OutputStart, k)

[<Fact>]
let ``BoundaryKind.CommandFinished carries optional exit code`` () =
    match BoundaryKind.CommandFinished (Some 0) with
    | BoundaryKind.CommandFinished (Some c) -> Assert.Equal(0, c)
    | _ -> Assert.Fail("Expected CommandFinished (Some 0)")

[<Fact>]
let ``BoundarySource.Osc133 constructs cleanly`` () =
    Assert.Equal(BoundarySource.Osc133, BoundarySource.Osc133)

[<Fact>]
let ``BoundarySource.HeuristicPromptRegex carries stability ms`` () =
    match BoundarySource.HeuristicPromptRegex 250 with
    | BoundarySource.HeuristicPromptRegex ms -> Assert.Equal(250, ms)
    | _ -> Assert.Fail("Expected HeuristicPromptRegex")

[<Fact>]
let ``BoundarySource.HeuristicClaudeInkBox constructs cleanly`` () =
    Assert.Equal(
        BoundarySource.HeuristicClaudeInkBox,
        BoundarySource.HeuristicClaudeInkBox)

[<Fact>]
let ``PromptBoundaryData record literal constructs all fields`` () =
    let b = boundaryWith BoundaryKind.PromptStart t0 (Some "aid-1") [ "k", "v" ]
    Assert.Equal(BoundaryKind.PromptStart, b.Kind)
    Assert.Equal(Some "aid-1", b.CommandId)
    Assert.Equal(Some "v", Map.tryFind "k" b.ExtraParams)

[<Fact>]
let ``PromptBoundaryData supports None CommandId and empty ExtraParams`` () =
    let b = boundary BoundaryKind.PromptStart t0
    Assert.Equal(None, b.CommandId)
    Assert.True(Map.isEmpty b.ExtraParams)

[<Fact>]
let ``PromptBoundaryData carries optional MatchedRowText`` () =
    let b = boundaryWithText BoundaryKind.PromptStart t0 "C:\\>"
    Assert.Equal(Some "C:\\>", b.MatchedRowText)

[<Fact>]
let ``ScreenNotification.PromptBoundary round-trips through pattern match`` () =
    let b = boundary BoundaryKind.PromptStart t0
    let n = ScreenNotification.PromptBoundary b
    match n with
    | ScreenNotification.PromptBoundary data ->
        Assert.Equal(BoundaryKind.PromptStart, data.Kind)
    | _ -> Assert.Fail("Expected PromptBoundary case")

[<Fact>]
let ``ActiveTupleState cases enumerate as expected`` () =
    let states =
        [ SessionModel.ActiveTupleState.AwaitingPromptStart
          SessionModel.ActiveTupleState.AwaitingCommandStart
          SessionModel.ActiveTupleState.EditingCommand
          SessionModel.ActiveTupleState.OutputStreaming ]
    Assert.Equal(4, List.length states)

// ---------------------------------------------------------------------
// SessionModel.create
// ---------------------------------------------------------------------

[<Fact>]
let ``SessionModel.create initialises empty History + None Active`` () =
    let model = SessionModel.create "cmd" 50
    Assert.Equal(0, model.History.Count)
    Assert.Equal(None, model.Active)

[<Fact>]
let ``SessionModel.create initialises IsAltScreenActive=false`` () =
    Assert.False((SessionModel.create "cmd" 50).IsAltScreenActive)

[<Fact>]
let ``SessionModel.create initialises NextCellSequence=0`` () =
    Assert.Equal(0L, (SessionModel.create "cmd" 50).NextCellSequence)

[<Fact>]
let ``SessionModel.create issues a fresh SessionId per instance`` () =
    let a = SessionModel.create "cmd" 50
    let b = SessionModel.create "cmd" 50
    Assert.True(a.SessionId <> b.SessionId)

[<Fact>]
let ``SessionModel.create stamps SessionStartedAt close to now`` () =
    let model = SessionModel.create "cmd" 50
    let delta = (DateTime.UtcNow - model.SessionStartedAt).Duration()
    Assert.True(delta < TimeSpan.FromSeconds(5.0))

[<Fact>]
let ``SessionModel.createDefault uses DefaultMaxHistorySize (100)`` () =
    let model = SessionModel.createDefault "cmd"
    Assert.Equal(SessionModel.DefaultMaxHistorySize, model.MaxHistorySize)
    Assert.Equal(100, model.MaxHistorySize)

// ---------------------------------------------------------------------
// IOCell record shape (Q4 multi-line + Q8 exit-code option)
// ---------------------------------------------------------------------

[<Fact>]
let ``IOCell.CommandText supports multi-line via embedded newlines (Q4)`` () =
    let cell : SessionModel.IOCell =
        { Id = Guid.NewGuid()
          CellSequence = 0L
          CommandId = None
          Phase = SessionModel.IOCellPhase.Sealed
          ShellId = "cmd"
          PromptStartedAt = DateTime.UtcNow
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = None
          PromptText = ">"
          CommandText = "echo a\necho b\necho c"
          OutputText = ""
          ExitCode = None
          Sources = Map.empty
          ExtraParams = Map.empty }
    let lines = cell.CommandText.Split('\n')
    Assert.Equal(3, lines.Length)
    Assert.Equal("echo a", lines.[0])
    Assert.Equal("echo c", lines.[2])

[<Fact>]
let ``IOCell.ExitCode is int option (Q8 — substrate captures verbatim)`` () =
    let success : SessionModel.IOCell =
        { Id = Guid.NewGuid()
          CellSequence = 0L
          CommandId = None
          Phase = SessionModel.IOCellPhase.Sealed
          ShellId = "cmd"
          PromptStartedAt = DateTime.UtcNow
          CommandStartedAt = None
          OutputStartedAt = None
          CommandFinishedAt = None
          PromptText = ""
          CommandText = ""
          OutputText = ""
          ExitCode = Some 0
          Sources = Map.empty
          ExtraParams = Map.empty }
    let unreported = { success with ExitCode = None }
    let failure = { success with ExitCode = Some 127 }
    Assert.Equal(Some 0, success.ExitCode)
    Assert.Equal(None, unreported.ExitCode)
    Assert.Equal(Some 127, failure.ExitCode)

// =====================================================================
// State machine — Active advances on non-finalize boundaries
// (unchanged by the pivot: finalize is the only thing drop-on-None
// affects).
// =====================================================================

[<Fact>]
let ``apply PromptStart from AwaitingPromptStart populates Active + advances state`` () =
    let initial = SessionModel.create "cmd" 50
    let updated = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    match updated.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart,
            active.State)
        Assert.Equal(t0, active.Tuple.PromptStartedAt)
        Assert.Equal("cmd", active.Tuple.ShellId)
        Assert.Equal(0, updated.History.Count)
    | None -> Assert.Fail("Expected Active = Some after PromptStart")

[<Fact>]
let ``apply PromptStart assigns CellSequence 0 + Phase Composing`` () =
    let initial = SessionModel.create "cmd" 50
    let updated = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    match updated.Active with
    | Some active ->
        Assert.Equal(0L, active.Tuple.CellSequence)
        Assert.Equal(
            SessionModel.IOCellPhase.Composing, active.Tuple.Phase)
        Assert.Equal(1L, updated.NextCellSequence)
    | None -> Assert.Fail("Expected Active = Some after PromptStart")

[<Fact>]
let ``apply CommandStart after PromptStart advances to EditingCommand`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    match s2.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.EditingCommand, active.State)
        Assert.Equal(Some (after 100), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active after CommandStart")

[<Fact>]
let ``apply OutputStart after CommandStart advances to OutputStreaming`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    match s3.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming, active.State)
        Assert.Equal(Some (after 200), active.Tuple.OutputStartedAt)
    | None -> Assert.Fail("Expected Active after OutputStart")

[<Fact>]
let ``OutputStart in AwaitingCommandStart tolerates skipped CommandStart`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.OutputStart (after 100)) [||]
    match s2.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming, active.State)
        Assert.Equal(Some (after 100), active.Tuple.OutputStartedAt)
        Assert.Equal(None, active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active after tolerated OutputStart")

[<Fact>]
let ``Duplicate CommandStart in EditingCommand refreshes CommandStartedAt`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.CommandStart (after 200)) [||]
    match s3.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.EditingCommand, active.State)
        Assert.Equal(Some (after 200), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active to remain after duplicate CommandStart")

[<Fact>]
let ``Duplicate OutputStart in OutputStreaming refreshes OutputStartedAt`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.OutputStart (after 250)) [||]
    match s4.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming, active.State)
        Assert.Equal(Some (after 250), active.Tuple.OutputStartedAt)
    | None -> Assert.Fail("Expected Active to remain after duplicate OutputStart")

[<Fact>]
let ``CommandStart while OutputStreaming is logged + ignored (Active preserved)`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.OutputStart (after 200)) [||]
    let s4 = SessionModel.apply s3 (boundary BoundaryKind.CommandStart (after 250)) [||]
    match s4.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.OutputStreaming, active.State)
        Assert.Equal(Some (after 100), active.Tuple.CommandStartedAt)
    | None -> Assert.Fail("Expected Active to remain after anomalous CommandStart")

[<Fact>]
let ``CommandStart from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.CommandStart t0) [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``OutputStart from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.OutputStart t0) [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``CommandFinished from AwaitingPromptStart (no Active) is logged + ignored`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial (boundary (BoundaryKind.CommandFinished (Some 0)) t0) [||]
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``apply with HeuristicPromptRegex source populates Active`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial (heuristicBoundary BoundaryKind.PromptStart t0 100) [||]
    match updated.Active with
    | Some active ->
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart, active.State)
        Assert.Equal(t0, active.Tuple.PromptStartedAt)
    | None -> Assert.Fail("Expected Active after heuristic PromptStart")

[<Fact>]
let ``Sources map records HeuristicPromptRegex stability ms verbatim`` () =
    let initial = SessionModel.create "powershell" 50
    let s1 =
        SessionModel.apply
            initial (heuristicBoundary BoundaryKind.PromptStart t0 100) [||]
    match s1.Active with
    | Some active ->
        Assert.Equal(
            Some (BoundarySource.HeuristicPromptRegex 100),
            Map.tryFind BoundaryKind.PromptStart active.Tuple.Sources)
    | None -> Assert.Fail("Expected Active")

[<Fact>]
let ``PromptStart with MatchedRowText populates Active.Tuple.PromptText`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply
            initial
            (boundaryWithText BoundaryKind.PromptStart t0 "C:\\Users\\admin>")
            [||]
    match updated.Active with
    | Some active -> Assert.Equal("C:\\Users\\admin>", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after PromptStart")

[<Fact>]
let ``PromptStart with None MatchedRowText leaves PromptText empty`` () =
    let initial = SessionModel.create "cmd" 50
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    match updated.Active with
    | Some active -> Assert.Equal("", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after PromptStart")

[<Fact>]
let ``Active.Tuple.PromptRowIndex captured from boundary`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 5 "C:\\>")
            (snapshotOf 10 80 [ ""; ""; ""; ""; ""; "C:\\>"; ""; ""; ""; "" ])
    match s1.Active with
    | Some active -> Assert.Equal(Some 5, active.PromptRowIndex)
    | None -> Assert.Fail("Expected Active after PromptStart")

// =====================================================================
// Q3 alt-screen guard
// =====================================================================

[<Fact>]
let ``apply with IsAltScreenActive=true returns state unchanged`` () =
    let initial = SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    let updated =
        SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.True(obj.ReferenceEquals(initial, updated))
    Assert.Equal(None, updated.Active)
    Assert.Equal(0, updated.History.Count)

[<Fact>]
let ``enterAltScreen toggles flag true`` () =
    Assert.True((SessionModel.enterAltScreen (SessionModel.create "cmd" 50)).IsAltScreenActive)

[<Fact>]
let ``exitAltScreen toggles flag false`` () =
    let initial = SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    Assert.False((SessionModel.exitAltScreen initial).IsAltScreenActive)

[<Fact>]
let ``apply resumes after exitAltScreen`` () =
    let initial = SessionModel.enterAltScreen (SessionModel.create "cmd" 50)
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.Equal(None, s1.Active)
    let s2 = SessionModel.exitAltScreen s1
    let s3 = SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 100)) [||]
    Assert.True(s3.Active.IsSome)

// =====================================================================
// ADR 0004 — drop-on-None contract. The legacy `apply` /
// `applyAndCapture` / `finalizeIncomplete` surface (no
// ContentHistory) NEVER finalises a cell post-pivot: it drops.
// =====================================================================

[<Fact>]
let ``legacy apply CommandFinished drops the cell (no ContentHistory)`` () =
    let initial = SessionModel.create "cmd" 50
    let final =
        [ boundary BoundaryKind.PromptStart t0
          boundary BoundaryKind.CommandStart (after 100)
          boundary BoundaryKind.OutputStart (after 200)
          boundary (BoundaryKind.CommandFinished (Some 0)) (after 300) ]
        |> List.fold (fun s b -> SessionModel.apply s b [||]) initial
    Assert.Equal(None, final.Active)
    Assert.Equal(0, final.History.Count)

[<Fact>]
let ``legacy applyAndCapture CommandFinished returns None (drop-on-None)`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2, finalisedOpt =
        SessionModel.applyAndCapture
            s1 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
    Assert.True(finalisedOpt.IsNone)
    Assert.True(s2.Active.IsNone)
    Assert.Equal(0, s2.History.Count)

[<Fact>]
let ``legacy interrupting PromptStart drops the prior cell + starts new`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let s3 =
        SessionModel.apply s2 (boundary BoundaryKind.PromptStart (after 200)) [||]
    // Prior cell dropped (no ContentHistory slice), but the new
    // cell is created normally.
    Assert.Equal(0, s3.History.Count)
    match s3.Active with
    | Some active ->
        Assert.Equal(after 200, active.Tuple.PromptStartedAt)
        Assert.Equal(
            SessionModel.ActiveTupleState.AwaitingCommandStart, active.State)
        // The interrupting prompt opened a fresh cell — its
        // CellSequence is 1 (the dropped prior cell consumed 0).
        Assert.Equal(1L, active.Tuple.CellSequence)
    | None -> Assert.Fail("Expected new Active after interrupting PromptStart")

[<Fact>]
let ``heuristic interrupting PromptStart also drops the prior cell`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 =
        SessionModel.apply
            initial (heuristicBoundary BoundaryKind.PromptStart t0 100) [||]
    let s2 =
        SessionModel.apply
            s1 (heuristicBoundary BoundaryKind.PromptStart (after 1000) 100) [||]
    Assert.Equal(0, s2.History.Count)
    Assert.True(s2.Active.IsSome)

[<Fact>]
let ``interrupt + restart writes new boundary's MatchedRowText to fresh cell`` () =
    let initial = SessionModel.create "powershell" 50
    let s1 =
        SessionModel.apply
            initial (boundaryWithText BoundaryKind.PromptStart t0 "PS C:\\>") [||]
    let s2 =
        SessionModel.apply
            s1
            (boundaryWithText BoundaryKind.PromptStart (after 100) "PS C:\\Projects>")
            [||]
    match s2.Active with
    | Some active -> Assert.Equal("PS C:\\Projects>", active.Tuple.PromptText)
    | None -> Assert.Fail("Expected Active after restart")
    // Prior cell dropped (drop-on-None; no ContentHistory).
    Assert.Equal(0, s2.History.Count)

[<Fact>]
let ``finalizeIncomplete with Active=Some drops the cell (drop-on-None)`` () =
    let initial = SessionModel.create "cmd" 50
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    let s2 = SessionModel.apply s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
    let finalised = SessionModel.finalizeIncomplete s2 (after 200)
    Assert.Equal(None, finalised.Active)
    Assert.Equal(0, finalised.History.Count)

[<Fact>]
let ``finalizeIncomplete with Active=None is a no-op`` () =
    let initial = SessionModel.create "cmd" 50
    let finalised = SessionModel.finalizeIncomplete initial (after 100)
    Assert.Equal(None, finalised.Active)
    Assert.Equal(0, finalised.History.Count)

[<Fact>]
let ``legacy apply / applyAndCapture API present but drops on finalize`` () =
    // Regression pin: the public surface still type-checks +
    // runs (80+ legacy callers), but post-pivot finalize with no
    // ContentHistory drops the cell rather than enqueueing it.
    let initial = SessionModel.create "cmd" 100
    let s1 = SessionModel.apply initial (boundary BoundaryKind.PromptStart t0) [||]
    Assert.True(s1.Active.IsSome)
    let s2, finalisedOpt =
        SessionModel.applyAndCapture
            s1 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
    Assert.True(finalisedOpt.IsNone)
    Assert.True(s2.Active.IsNone)
    Assert.Equal(0, s2.History.Count)

// =====================================================================
// ContentHistory substrate — the post-pivot finalize path
// (`applyAndCaptureWithContentHistory`; production analogue is
// `Program.fs handlePromptBoundary`).
// =====================================================================

[<Fact>]
let ``CH path — full cycle finalises one Sealed cell into History`` () =
    let initial = SessionModel.create "cmd" 100
    let s1, finalisedOpt =
        runCellThroughCH initial 0 "C:\\>" "echo hi" "hi" "C:\\>" (Some 0)
    Assert.True(finalisedOpt.IsSome)
    Assert.Equal(1, s1.History.Count)
    let cell = s1.History.ToArray().[0]
    Assert.Equal("echo hi", cell.CommandText)
    Assert.Equal("hi", cell.OutputText)
    Assert.Equal(Some 0, cell.ExitCode)
    Assert.Equal(Some (after 200), cell.CommandFinishedAt)
    Assert.Equal(SessionModel.IOCellPhase.Sealed, cell.Phase)
    Assert.Equal(0L, cell.CellSequence)
    Assert.Equal(None, s1.Active)

[<Fact>]
let ``CH path — OSC 133 PromptStart+OutputStart splits command vs output`` () =
    let initial = SessionModel.create "claude" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "echo hello\n")
    let history =
        appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let history =
        feedHistoryBytes
            history (after 15)
            (System.Text.Encoding.ASCII.GetBytes "hello world\n")
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
            history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||] history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true (-1L)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Contains("echo hello", cell.CommandText)
    Assert.Contains("hello world", cell.OutputText)
    Assert.Equal(SessionModel.IOCellPhase.Sealed, cell.Phase)

[<Fact>]
let ``CH path — heuristic-only PromptStart-only slices the blob`` () =
    let initial = SessionModel.create "cmd" 100
    let _s, finalisedOpt =
        runCellThroughCH initial 0 "$ " "echo hi" "hi" "C:\\>" (Some 0)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal("echo hi", cell.CommandText)
    Assert.Equal("hi", cell.OutputText)

[<Fact>]
let ``CH path — heuristic-only multi-line output preserved`` () =
    let initial = SessionModel.create "cmd" 100
    let _s, finalisedOpt =
        runCellThroughCH initial 0 "$ " "ls" "file1\nfile2\nfile3" "C:\\>" (Some 0)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal("ls", cell.CommandText)
    Assert.Contains("file1", cell.OutputText)
    Assert.Contains("file3", cell.OutputText)

[<Fact>]
let ``CH path — OSC 133 split wins when both markers present`` () =
    let initial = SessionModel.create "claude" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "explain this\n")
    let history =
        appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let history =
        feedHistoryBytes
            history (after 15)
            (System.Text.Encoding.ASCII.GetBytes "Here is the explanation.\n")
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
            history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||] history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true (-1L)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Contains("explain this", cell.CommandText)
    Assert.Contains("Here is the explanation", cell.OutputText)
    Assert.DoesNotContain("explain this", cell.OutputText)

[<Fact>]
let ``CH path — useContentHistory=true but no PromptStart marker drops`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history =
        feedHistoryBytes
            history t0 (System.Text.Encoding.ASCII.GetBytes "irrelevant\n")
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "C:\\>") [||]
            history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||] history true (-1L)
    let s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history true (-1L)
    Assert.True(finalisedOpt.IsNone)
    Assert.True(s3.Active.IsNone)
    Assert.Equal(0, s3.History.Count)

[<Fact>]
let ``CH path — useContentHistory=false drops on finalize`` () =
    let initial = SessionModel.create "claude" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "echo from-history\n")
    let history =
        appendHistoryMarker history (after 10) ContentHistory.MarkerKind.OutputStart
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
            history false (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||] history false (-1L)
    let s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2 (boundary (BoundaryKind.CommandFinished (Some 0)) (after 200)) [||]
            history false (-1L)
    Assert.True(finalisedOpt.IsNone)
    Assert.True(s3.Active.IsNone)
    Assert.Equal(0, s3.History.Count)

[<Fact>]
let ``CH path — useContentHistory=false leaves non-finalize state intact`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundary BoundaryKind.PromptStart t0) [||] history false (-1L)
    Assert.True(s1.Active.IsSome)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||] history false (-1L)
    Assert.Equal(
        SessionModel.ActiveTupleState.EditingCommand, s2.Active.Value.State)
    let s3 =
        SessionModel.applyWithContentHistory
            s2 (boundary BoundaryKind.OutputStart (after 200)) [||] history false (-1L)
    Assert.Equal(
        SessionModel.ActiveTupleState.OutputStreaming, s3.Active.Value.State)

// =====================================================================
// CH path — metadata accumulation, ordering, eviction, CellSequence
// =====================================================================

[<Fact>]
let ``CH path — CommandId hoists + ExtraParams merge (later wins)`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "cmd\nout\nC:\\>")
    let s1 =
        SessionModel.applyWithContentHistory
            initial
            (boundaryWith BoundaryKind.PromptStart t0 (Some "id-1") [ "k1", "v1" ])
            [||] history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1
            (boundaryWith BoundaryKind.CommandStart (after 100) None [ "k2", "v2" ])
            [||] history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2
            { Kind = BoundaryKind.CommandFinished (Some 0)
              Source = BoundarySource.Osc133
              DetectedAt = after 200
              CommandId = None
              ExtraParams = Map.ofList [ "k1", "overwrite" ]
              MatchedRowText = Some "C:\\>"
              MatchedRowIndex = None }
            [||] history true (-1L)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal(Some "id-1", cell.CommandId)
    Assert.Equal(Some "overwrite", Map.tryFind "k1" cell.ExtraParams)
    Assert.Equal(Some "v2", Map.tryFind "k2" cell.ExtraParams)

[<Fact>]
let ``CH path — CommandId conflict preserves earlier value`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "cmd\nout\nC:\\>")
    let s1 =
        SessionModel.applyWithContentHistory
            initial
            (boundaryWith BoundaryKind.PromptStart t0 (Some "first") [])
            [||] history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1
            (boundaryWith BoundaryKind.CommandStart (after 100) (Some "second") [])
            [||] history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2
            (boundaryWithText
                (BoundaryKind.CommandFinished (Some 0)) (after 200) "C:\\>")
            [||] history true (-1L)
    Assert.True(finalisedOpt.IsSome)
    Assert.Equal(Some "first", finalisedOpt.Value.CommandId)

[<Fact>]
let ``CH path — Sources map records (Kind, Source) per boundary`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history = appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 5)
            (System.Text.Encoding.ASCII.GetBytes "cmd\nout\nC:\\>")
    let s1 =
        SessionModel.applyWithContentHistory
            initial
            { Kind = BoundaryKind.PromptStart
              Source = BoundarySource.Osc133
              DetectedAt = t0
              CommandId = None
              ExtraParams = Map.empty
              MatchedRowText = Some "$ "
              MatchedRowIndex = None }
            [||] history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1
            { Kind = BoundaryKind.CommandStart
              Source = BoundarySource.HeuristicPromptRegex 100
              DetectedAt = after 100
              CommandId = None
              ExtraParams = Map.empty
              MatchedRowText = None
              MatchedRowIndex = None }
            [||] history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2
            { Kind = BoundaryKind.CommandFinished (Some 0)
              Source = BoundarySource.Osc133
              DetectedAt = after 200
              CommandId = None
              ExtraParams = Map.empty
              MatchedRowText = Some "C:\\>"
              MatchedRowIndex = None }
            [||] history true (-1L)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal(
        Some BoundarySource.Osc133,
        Map.tryFind BoundaryKind.PromptStart cell.Sources)
    Assert.Equal(
        Some (BoundarySource.HeuristicPromptRegex 100),
        Map.tryFind BoundaryKind.CommandStart cell.Sources)
    Assert.Equal(
        Some BoundarySource.Osc133,
        Map.tryFind (BoundaryKind.CommandFinished (Some 0)) cell.Sources)

[<Fact>]
let ``CH path — CommandFinished with no exit code yields None ExitCode`` () =
    let initial = SessionModel.create "cmd" 100
    let _s, finalisedOpt =
        runCellThroughCH initial 0 "$ " "cmd" "out" "C:\\>" None
    Assert.True(finalisedOpt.IsSome)
    Assert.Equal(None, finalisedOpt.Value.ExitCode)

[<Fact>]
let ``CH path — two sequences yield two cells in order`` () =
    let initial = SessionModel.create "cmd" 100
    let s1, _ = runCellThroughCH initial 0 "$ " "first" "o1" "C:\\>" (Some 0)
    let s2, _ = runCellThroughCH s1 1000 "$ " "second" "o2" "C:\\>" (Some 1)
    Assert.Equal(2, s2.History.Count)
    let arr = s2.History.ToArray()
    Assert.Equal("first", arr.[0].CommandText)
    Assert.Equal("second", arr.[1].CommandText)
    Assert.Equal(Some 0, arr.[0].ExitCode)
    Assert.Equal(Some 1, arr.[1].ExitCode)

[<Fact>]
let ``CH path — CellSequence is monotonic across cells`` () =
    let initial = SessionModel.create "cmd" 100
    let s1, c0 = runCellThroughCH initial 0 "$ " "a" "oa" "C:\\>" (Some 0)
    let s2, c1 = runCellThroughCH s1 1000 "$ " "b" "ob" "C:\\>" (Some 0)
    let _s3, c2 = runCellThroughCH s2 2000 "$ " "c" "oc" "C:\\>" (Some 0)
    Assert.Equal(0L, c0.Value.CellSequence)
    Assert.Equal(1L, c1.Value.CellSequence)
    Assert.Equal(2L, c2.Value.CellSequence)

[<Fact>]
let ``CH path — CellSequence resets to 0 on a fresh model (shell-switch)`` () =
    let m1 = SessionModel.create "cmd" 100
    let _s1, c1 = runCellThroughCH m1 0 "$ " "a" "oa" "C:\\>" (Some 0)
    // Shell-switch constructs a fresh SessionModel.T.
    let m2 = SessionModel.create "powershell" 100
    let _s2, c2 = runCellThroughCH m2 0 "$ " "b" "ob" "C:\\>" (Some 0)
    Assert.Equal(0L, c1.Value.CellSequence)
    Assert.Equal(0L, c2.Value.CellSequence)

[<Fact>]
let ``CH path — History bounded at MaxHistorySize (FIFO eviction)`` () =
    let mutable state = SessionModel.create "cmd" 2
    for i in 0 .. 2 do
        let s, _ =
            runCellThroughCH state (i * 1000) "$ " (sprintf "c%d" i) "o" "C:\\>" (Some 0)
        state <- s
    Assert.Equal(2, state.History.Count)
    let arr = state.History.ToArray()
    // c0 evicted; c1 + c2 remain in order.
    Assert.Equal("c1", arr.[0].CommandText)
    Assert.Equal("c2", arr.[1].CommandText)
    Assert.Equal(1L, arr.[0].CellSequence)
    Assert.Equal(2L, arr.[1].CellSequence)

[<Fact>]
let ``CH path — MaxHistorySize=0 keeps no history but doesn't crash`` () =
    let initial = SessionModel.create "cmd" 0
    let s1, finalisedOpt =
        runCellThroughCH initial 0 "$ " "echo hi" "hi" "C:\\>" (Some 0)
    Assert.Equal(0, s1.History.Count)
    Assert.True(finalisedOpt.IsNone)
    Assert.Equal(None, s1.Active)

// =====================================================================
// Cycle 51 PR-X — Seq-watermark command/output split (the
// history-scroll fix). When a valid command-Enter watermark is
// supplied, the redraws cmd reprints on every Up/Down history
// recall (which accumulate linearly in ContentHistory between
// PromptStart and the executed command) must NOT leak into
// OutputText — the watermark is the exact command/output
// boundary.
// =====================================================================

[<Fact>]
let ``CH path — Seq watermark excludes history-scroll redraws from output`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history =
        appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    // Up/Down history scroll: the prompt line redrawn several
    // times with different recalled commands, then the final one.
    let history =
        feedHistoryBytes
            history (after 1)
            (System.Text.Encoding.ASCII.GetBytes "recalled-one\n")
    let history =
        feedHistoryBytes
            history (after 2)
            (System.Text.Encoding.ASCII.GetBytes "recalled-two\n")
    let history =
        feedHistoryBytes
            history (after 3)
            (System.Text.Encoding.ASCII.GetBytes "echo final\n")
    // The user presses Enter HERE — Program.fs captures
    // ContentHistory.latestSeq as the command-Enter watermark.
    let enterSeq = ContentHistory.latestSeq history
    // The command then runs, producing output, then the next
    // prompt is painted.
    let history =
        feedHistoryBytes
            history (after 4)
            (System.Text.Encoding.ASCII.GetBytes "real-output-line\nC:\\>")
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
            history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
            history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2
            (boundaryWithText
                (BoundaryKind.CommandFinished (Some 0)) (after 200) "C:\\>")
            [||] history true enterSeq
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal("echo final", cell.CommandText)
    Assert.Equal("real-output-line", cell.OutputText)
    // The loud history-scroll regression: recalled redraws must
    // NOT appear anywhere in the sealed cell.
    Assert.DoesNotContain("recalled-one", cell.OutputText)
    Assert.DoesNotContain("recalled-two", cell.OutputText)
    Assert.DoesNotContain("recalled-one", cell.CommandText)

[<Fact>]
let ``CH path — watermark <= PromptStart Seq falls back to first-newline`` () =
    // The legacy heuristic must still hold for callers that pass
    // -1L (no usable watermark) — every pre-PR-X test relies on
    // this fallback.
    let initial = SessionModel.create "cmd" 100
    let _s, finalisedOpt =
        runCellThroughCH initial 0 "$ " "echo hi" "hi" "C:\\>" (Some 0)
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal("echo hi", cell.CommandText)
    Assert.Equal("hi", cell.OutputText)

[<Fact>]
let ``CH path — watermark split keeps multi-line output intact`` () =
    let initial = SessionModel.create "cmd" 100
    let history = freshHistory ()
    let history =
        appendHistoryMarker history t0 ContentHistory.MarkerKind.PromptStart
    let history =
        feedHistoryBytes
            history (after 1)
            (System.Text.Encoding.ASCII.GetBytes "dir\n")
    let enterSeq = ContentHistory.latestSeq history
    let history =
        feedHistoryBytes
            history (after 2)
            (System.Text.Encoding.ASCII.GetBytes "file1\nfile2\nfile3\nC:\\>")
    let s1 =
        SessionModel.applyWithContentHistory
            initial (boundaryWithText BoundaryKind.PromptStart t0 "$ ") [||]
            history true (-1L)
    let s2 =
        SessionModel.applyWithContentHistory
            s1 (boundary BoundaryKind.CommandStart (after 100)) [||]
            history true (-1L)
    let _s3, finalisedOpt =
        SessionModel.applyAndCaptureWithContentHistory
            s2
            (boundaryWithText
                (BoundaryKind.CommandFinished (Some 0)) (after 200) "C:\\>")
            [||] history true enterSeq
    Assert.True(finalisedOpt.IsSome)
    let cell = finalisedOpt.Value
    Assert.Equal("dir", cell.CommandText)
    Assert.Contains("file1", cell.OutputText)
    Assert.Contains("file3", cell.OutputText)

// =====================================================================
// IOCellPhase + schemaVersion-2 JSONL serialization (PR-W)
// =====================================================================

[<Fact>]
let ``formatIOCellAsJsonl emits schemaVersion 2 + cellSequence + phase`` () =
    let cell = { mkCell 7L "C:\\>" "echo hi" "hi" (Some 0) with
                    Phase = SessionModel.IOCellPhase.Sealed }
    let line = SessionModel.formatIOCellAsJsonl cell
    Assert.StartsWith("{\"schemaVersion\":2,", line)
    Assert.Contains("\"cellSequence\":7", line)
    Assert.Contains("\"phase\":{\"kind\":\"sealed\"}", line)
    Assert.EndsWith("\n", line)

[<Fact>]
let ``formatIOCellAsJsonl serialises each IOCellPhase as a tagged object`` () =
    let baseCell = mkCell 0L "C:\\>" "c" "o" None
    let composing =
        SessionModel.formatIOCellAsJsonl
            { baseCell with Phase = SessionModel.IOCellPhase.Composing }
    let executing =
        SessionModel.formatIOCellAsJsonl
            { baseCell with Phase = SessionModel.IOCellPhase.Executing }
    let awaiting =
        SessionModel.formatIOCellAsJsonl
            { baseCell with
                Phase =
                    SessionModel.IOCellPhase.AwaitingSubPromptResponse "Proceed" }
    let sealed_ =
        SessionModel.formatIOCellAsJsonl
            { baseCell with Phase = SessionModel.IOCellPhase.Sealed }
    Assert.Contains("\"phase\":{\"kind\":\"composing\"}", composing)
    Assert.Contains("\"phase\":{\"kind\":\"executing\"}", executing)
    Assert.Contains(
        "\"phase\":{\"kind\":\"awaitingSubPromptResponse\",\"subPromptText\":\"Proceed\"}",
        awaiting)
    Assert.Contains("\"phase\":{\"kind\":\"sealed\"}", sealed_)

[<Fact>]
let ``formatIOCellAsJsonl escapes subPromptText in the phase object`` () =
    let cell =
        { mkCell 0L "C:\\>" "c" "o" None with
            Phase =
                SessionModel.IOCellPhase.AwaitingSubPromptResponse
                    "say \"hi\"" }
    let line = SessionModel.formatIOCellAsJsonl cell
    Assert.Contains(
        "\"subPromptText\":\"say \\\"hi\\\"\"", line)

// ---------------------------------------------------------------------
// formatHistoryForClipboard (Cycle 22b) — hand-built History
// (the apply-driven builders dropped post-pivot; the formatter
// itself is unchanged).
// ---------------------------------------------------------------------

[<Fact>]
let ``formatHistoryForClipboard empty session shows '(no entries)' marker`` () =
    let state = SessionModel.create "cmd" 100
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("=== pty-speak session history ===", text)
    Assert.Contains("Shell:             cmd", text)
    Assert.Contains("History:           0 of 100", text)
    Assert.Contains(
        "(no entries; session has not yet captured any prompt boundaries)", text)
    Assert.DoesNotContain("--- Entry", text)
    Assert.DoesNotContain("--- Active", text)

[<Fact>]
let ``formatHistoryForClipboard renders snapshot timestamp in header`` () =
    let state = SessionModel.create "cmd" 100
    let now = DateTime(2026, 5, 8, 18, 30, 45, 123, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Snapshot:          2026-05-08T18:30:45.123Z", text)

[<Fact>]
let ``formatHistoryForClipboard with one cell shows Entry 1`` () =
    let state = mkState [ mkCell 0L "C:\\>" "echo hi" "hi" None ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("History:           1 of 100", text)
    Assert.Contains("--- Entry 1 ---", text)
    Assert.Contains("Prompt:            C:\\>", text)
    Assert.Contains("Command:           echo hi", text)
    Assert.Contains("Output:            hi", text)
    Assert.Contains("ExitCode:          (none)", text)

[<Fact>]
let ``formatHistoryForClipboard preserves multi-line CommandText verbatim`` () =
    let cell =
        mkCell 0L "C:\\>" "echo line1\necho line2\necho line3"
            "line1\nline2\nline3" (Some 0)
    let state = mkState [ cell ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("echo line1\necho line2\necho line3", text)
    Assert.Contains("line1\nline2\nline3", text)
    Assert.Contains("ExitCode:          0", text)

[<Fact>]
let ``formatHistoryForClipboard renders empty fields as '(empty)' marker`` () =
    let cell =
        { mkCell 0L "C:\\>" "" "" None with
            CommandStartedAt = None
            OutputStartedAt = None }
    let state = mkState [ cell ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Command:           (empty)", text)
    Assert.Contains("Output:            (empty)", text)
    Assert.Contains("CommandStarted:    (none)", text)
    Assert.Contains("OutputStarted:     (none)", text)

[<Fact>]
let ``formatHistoryForClipboard renders Sources map with provenance`` () =
    let cell =
        { mkCell 0L "C:\\>" "" "" None with
            Sources =
                Map.ofList
                    [ BoundaryKind.PromptStart,
                      BoundarySource.HeuristicPromptRegex 100 ] }
    let state = mkState [ cell ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains(
        "Source(s):         PromptStart=HeuristicPromptRegex(100ms)", text)

[<Fact>]
let ``formatHistoryForClipboard active-only (no history) renders Active block`` () =
    let initial = SessionModel.create "cmd" 100
    let s1 =
        SessionModel.apply
            initial
            (boundaryWithRow BoundaryKind.PromptStart t0 0 "C:\\>")
            (snapshotOf 3 80 [ "C:\\>"; ""; "" ])
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now s1
    Assert.Contains("History:           0 of 100", text)
    Assert.DoesNotContain("--- Entry", text)
    Assert.Contains("--- Active (in flight) ---", text)
    Assert.Contains("State:             AwaitingCommandStart", text)
    Assert.Contains("PromptRowIndex:    0", text)

[<Fact>]
let ``formatHistoryForClipboard renders shell + session id + AltScreenActive flag`` () =
    let state =
        { SessionModel.create "powershell" 50 with IsAltScreenActive = true }
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains("Shell:             powershell", text)
    Assert.Contains("History:           0 of 50", text)
    Assert.Contains("AltScreenActive:   True", text)
    Assert.Contains(string state.SessionId, text)

[<Fact>]
let ``formatHistoryForClipboard preserves full content (no truncation)`` () =
    let longCmd = String.replicate 200 "x"
    let longOut = String.replicate 500 "y"
    let state = mkState [ mkCell 0L "C:\\>" longCmd longOut (Some 0) ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    Assert.Contains(longCmd, text)
    Assert.Contains(longOut, text)

[<Fact>]
let ``formatHistoryForClipboard history entries appear oldest first`` () =
    let state =
        mkState
            [ mkCell 0L "C:\\one>" "a" "oa" (Some 0)
              mkCell 1L "C:\\two>" "b" "ob" (Some 0)
              mkCell 2L "C:\\three>" "c" "oc" (Some 0) ]
    let now = DateTime(2026, 5, 8, 18, 0, 0, DateTimeKind.Utc)
    let text = SessionModel.formatHistoryForClipboard now state
    let oneIdx = text.IndexOf("C:\\one>")
    let twoIdx = text.IndexOf("C:\\two>")
    let threeIdx = text.IndexOf("C:\\three>")
    Assert.True(oneIdx >= 0)
    Assert.True(twoIdx > oneIdx)
    Assert.True(threeIdx > twoIdx)
    Assert.Contains("--- Entry 1 ---", text)
    Assert.Contains("--- Entry 2 ---", text)
    Assert.Contains("--- Entry 3 ---", text)
