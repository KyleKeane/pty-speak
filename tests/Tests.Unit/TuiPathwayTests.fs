module PtySpeak.Tests.Unit.TuiPathwayTests

open System
open Xunit
open Terminal.Core

// ---------------------------------------------------------------------
// Phase A — TuiPathway behavioural pinning
// ---------------------------------------------------------------------
//
// TuiPathway suppresses streaming output for alt-screen TUIs
// (vim, less, top, full-screen fzf). Behaviour:
//   * `Consume` always returns `[||]` regardless of the
//     canonical state — the user navigates via NVDA review
//     cursor / browse mode rather than streaming announcements.
//   * `Tick` always returns `[||]` — no accumulator.
//   * `OnModeBarrier` returns a single ModeBarrier OutputEvent
//     so the channel sees the transition in the audit trail.
//   * `Reset` is a no-op (no per-session state).

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

let private snapshotOf (rows: int) (cols: int) (lines: string list) : Cell[][] =
    let arr = Array.init rows (fun _ -> blankRow cols)
    lines
    |> List.iteri (fun i line ->
        if i < rows then arr.[i] <- rowOf cols line)
    arr

let private now : DateTimeOffset =
    DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero)

[<Fact>]
let ``Consume on first canonical state emits no events`` () =
    let pathway = TuiPathway.create ()
    let canonical = CanonicalState.create (snapshotOf 5 10 [ "vim"; "buffer"; "rows" ]) 0L
    let result = pathway.Consume canonical
    Assert.Equal(0, result.Length)

[<Fact>]
let ``Consume on changed canonical state still emits no events`` () =
    let pathway = TuiPathway.create ()
    let snap1 = snapshotOf 3 10 [ "buffer line 1" ]
    let snap2 = snapshotOf 3 10 [ "buffer line 2" ]
    let _ = pathway.Consume (CanonicalState.create snap1 0L)
    let result = pathway.Consume (CanonicalState.create snap2 1L)
    Assert.Equal(0, result.Length)

[<Fact>]
let ``Tick emits no events`` () =
    let pathway = TuiPathway.create ()
    let result = pathway.Tick now
    Assert.Equal(0, result.Length)

[<Fact>]
let ``OnModeBarrier emits one ModeBarrier OutputEvent`` () =
    let pathway = TuiPathway.create ()
    let result = pathway.OnModeBarrier now
    Assert.Equal(1, result.Length)
    Assert.Equal(SemanticCategory.ModeBarrier, result.[0].Semantic)
    Assert.Equal("", result.[0].Payload)
    Assert.Equal("tui", result.[0].Source.Producer)

[<Fact>]
let ``Reset is a no-op (no exception, no state change)`` () =
    let pathway = TuiPathway.create ()
    pathway.Reset ()
    let result = pathway.Consume (CanonicalState.create (snapshotOf 1 5 [ "x" ]) 0L)
    Assert.Equal(0, result.Length)

[<Fact>]
let ``pathway Id is "tui"`` () =
    let pathway = TuiPathway.create ()
    Assert.Equal("tui", pathway.Id)

[<Fact>]
let ``OnModeBarrier returns Polite priority`` () =
    // The TuiPathway can't reliably distinguish AltScreen
    // toggles from other mode flips through the v1 pathway
    // interface (which passes only `DateTimeOffset`). The
    // pathway emits Polite as a defensive default; the
    // channel still routes via ActivityIds.mode for any
    // ModeBarrier semantic.
    let pathway = TuiPathway.create ()
    let result = pathway.OnModeBarrier now
    Assert.Equal(Priority.Polite, result.[0].Priority)
