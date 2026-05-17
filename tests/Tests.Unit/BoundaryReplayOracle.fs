module PtySpeak.Tests.Unit.BoundaryReplayOracle

open System
open System.IO
open Xunit
open Terminal.Core

// =====================================================================
// Cycle 52 — Boundary replay oracle (R-A).
//
// Replays a committed RawShellRecorder trace
// (`docs/boundary-capture/cmd/*.txt`) through the REAL
// Terminal.Core pipeline — parser → ContentHistory → Screen
// (OSC-133 PromptBoundary) → SessionModel — reproducing the
// production reader-loop ordering (whole-chunk text appended
// BEFORE that chunk's boundaries are handled; this boundary's
// ContentHistory marker appended AFTER extractIOCell runs) that
// the hand-built SessionModelTests structurally cannot model and
// that produced the C1/C2/C3 defects.
//
// Deterministic: a virtual clock derived from the trace's µs
// timestamps reproduces idle gaps with no real waiting; no WPF,
// no P/Invoke, no real cmd.exe / PTY.
//
// Seed convention: the C1–C3 traces were captured with
// Ctrl+Shift+T toggled AFTER the prompt was already shown, so
// the cell's opening `;A`/`;B` predate the trace. The harness
// seeds a synthetic "joined at a ready prompt P" (P = the stable
// cmd prompt the trace exhibits) so the trace's closing `;D`
// seals the cell — faithful to the mid-session production state.
//
// R-A asserts the C3 (`set /p`) scenario inline (the defect that
// drove the fix). R-B externalises per-trace expectation files,
// adds C1/C2 + backspace/long-idle corpus + the dedicated CI job.
// =====================================================================

// ---- trace parsing --------------------------------------------------

type private Dir =
    | In
    | Out

type private TraceEvent =
    { ElapsedUs: int64
      Dir: Dir
      Bytes: byte[] }

/// Unescape the RawShellRecorder rendering. The recorder writes
/// ESC/CR/LF/TAB/BEL as `\e \r \n \t \a`, non-ASCII as `\xHH`,
/// and every printable 0x20–0x7E (INCLUDING `\` 0x5C) verbatim.
/// So a `\` followed by a non-escape char is a LITERAL backslash
/// and the next char is reprocessed — this is what makes both
/// `C:\Users\Kyle` and ST-chains like `\e\\e]` decode correctly.
let private unescape (s: string) : byte[] =
    let out = ResizeArray<byte>(s.Length)
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if c = '\\' && i + 1 < s.Length then
            match s.[i + 1] with
            | 'e' -> out.Add 0x1Buy; i <- i + 2
            | 'r' -> out.Add 0x0Duy; i <- i + 2
            | 'n' -> out.Add 0x0Auy; i <- i + 2
            | 't' -> out.Add 0x09uy; i <- i + 2
            | 'a' -> out.Add 0x07uy; i <- i + 2
            | 'x' when i + 3 < s.Length ->
                let hex = s.Substring(i + 2, 2)
                out.Add(Convert.ToByte(hex, 16))
                i <- i + 4
            | _ ->
                // Literal backslash; reprocess the next char.
                out.Add 0x5Cuy
                i <- i + 1
        else
            out.Add(byte c)
            i <- i + 1
    out.ToArray()

let private parseTrace (path: string) : TraceEvent list =
    File.ReadAllLines path
    |> Array.choose (fun line ->
        if line.StartsWith "===" || line.Trim().Length = 0 then None
        else
            // +{us}us {DIR} {n}B | {escaped}
            let bar = line.IndexOf " | "
            let head =
                if bar >= 0 then line.Substring(0, bar) else line
            let payload =
                if bar >= 0 then line.Substring(bar + 3) else ""
            let parts =
                head.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length < 3 then None
            else
                let us =
                    parts.[0].TrimStart('+').TrimEnd([| 'u'; 's' |])
                let dir =
                    match parts.[1] with
                    | "IN" -> In
                    | _ -> Out
                match Int64.TryParse us with
                | true, v ->
                    Some { ElapsedUs = v
                           Dir = dir
                           Bytes = unescape payload }
                | _ -> None)
    |> Array.toList

// ---- synthetic boundary (mirrors SessionModelTests' helper) ---------

let private mkBoundary
        (kind: BoundaryKind)
        (at: DateTime)
        (matched: string option)
        : PromptBoundaryData
        =
    { Kind = kind
      Source = BoundarySource.Osc133
      DetectedAt = at
      CommandId = None
      ExtraParams = Map.empty
      MatchedRowText = matched
      MatchedRowIndex = None }

let private markerOf (k: BoundaryKind) : ContentHistory.MarkerKind =
    match k with
    | BoundaryKind.PromptStart -> ContentHistory.MarkerKind.PromptStart
    | BoundaryKind.CommandStart -> ContentHistory.MarkerKind.CommandStart
    | BoundaryKind.OutputStart -> ContentHistory.MarkerKind.OutputStart
    | BoundaryKind.CommandFinished _ ->
        ContentHistory.MarkerKind.CommandFinished

// ---- replay engine --------------------------------------------------

/// Replay a trace and return (sealed cells, final state). Seeds a
/// synthetic ready-prompt `seedPrompt` so a mid-prompt-join trace
/// can still seal its cell at the captured `;D`.
let private replay
        (trace: TraceEvent list)
        (seedPrompt: string)
        : SessionModel.IOCell list
        =
    let virtualBase =
        DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let clockAt (us: int64) = virtualBase.AddTicks(us * 10L)

    let history = ContentHistory.create ContentHistory.defaultParameters
    let screen = Screen(40, 120)
    let parser = Terminal.Parser.Parser.create ()
    let mutable state = SessionModel.create "cmd" 100
    let mutable commandEnterSeq = -1L
    let sealedCells = ResizeArray<SessionModel.IOCell>()

    // Screen buffers OSC-133 boundaries and fires them after each
    // Apply; collect per chunk, drain after the chunk's appends.
    let pending = ResizeArray<PromptBoundaryData>()
    screen.PromptBoundary.Add(fun b -> pending.Add b)

    // Faithful per-boundary handling (the SessionModel subset of
    // Program.fs handlePromptBoundary; cmd OSC sessions don't hit
    // the heuristic synthetic-CF branch).
    let handleBoundary (b: PromptBoundaryData) (now: DateTime) =
        let aug =
            match b.MatchedRowText, b.Kind with
            | None, (BoundaryKind.PromptStart | BoundaryKind.CommandFinished _) ->
                let _, (cr, _), snap =
                    screen.SnapshotRows(0, screen.Rows)
                { b with
                    MatchedRowText = Some(CanonicalState.renderRow snap cr)
                    MatchedRowIndex = Some cr }
            | _ -> b
        let _, _, snap = screen.SnapshotRows(0, screen.Rows)
        let nextState, finalised =
            SessionModel.applyAndCaptureWithContentHistory
                state aug snap history true commandEnterSeq
        state <- nextState
        match finalised with
        | Some c -> sealedCells.Add c
        | None -> ()
        ContentHistory.appendMarker history (markerOf aug.Kind) now None
        |> ignore

    // --- seed: joined at a ready prompt ---
    let t0 = clockAt 0L
    ContentHistory.appendMarker
        history ContentHistory.MarkerKind.PromptStart t0 None
    |> ignore
    for ev in Terminal.Parser.Parser.feedArray
                  parser
                  (Text.Encoding.ASCII.GetBytes seedPrompt) do
        ContentHistory.appendFromEvent history t0 ev |> ignore
    ContentHistory.appendMarker
        history ContentHistory.MarkerKind.CommandStart t0 None
    |> ignore
    state <-
        (SessionModel.applyWithContentHistory
            state
            (mkBoundary BoundaryKind.PromptStart t0 (Some seedPrompt))
            [||] history true -1L)
    state <-
        (SessionModel.applyWithContentHistory
            state
            (mkBoundary BoundaryKind.CommandStart t0 None)
            [||] history true -1L)

    // --- replay the recorded chunks ---
    for ev in trace do
        let now = clockAt ev.ElapsedUs
        match ev.Dir with
        | In ->
            // The only IN effect SessionModel observes is the
            // command-Enter watermark (Program.fs:5514). P2′'s cmd
            // split is timing-independent, so this is fidelity, not
            // load-bearing for cmd; still captured for the record.
            for b in ev.Bytes do
                if b = 0x0Duy then
                    commandEnterSeq <- ContentHistory.latestSeq history
        | Out ->
            let events = Terminal.Parser.Parser.feedArray parser ev.Bytes
            // Whole chunk's text/markers FIRST (reader thread).
            for e in events do
                ContentHistory.appendFromEvent history now e |> ignore
            // Then Screen.Apply (fires the chunk's boundaries).
            pending.Clear()
            for e in events do
                screen.Apply e
            // Then drain boundaries (consumer thread).
            for b in List.ofSeq pending do
                handleBoundary b now

    List.ofSeq sealedCells

// ---- C3 oracle ------------------------------------------------------

let private repoFile (rel: string) : string =
    // Walk up from the test bin dir to the repo root.
    let mutable d = DirectoryInfo(AppContext.BaseDirectory)
    let mutable found = ""
    while not (isNull d) && found = "" do
        let candidate = Path.Combine(d.FullName, rel)
        if File.Exists candidate then found <- candidate
        else d <- d.Parent
    found

[<Fact>]
let ``R-A oracle: C3 set/p trace seals a cell with no prompt-path bleed`` () =
    let path =
        repoFile (Path.Combine("docs", "boundary-capture", "cmd",
                               "C3-set-p-text-input.txt"))
    Assert.True(
        File.Exists path,
        sprintf "C3 trace not found from %s" AppContext.BaseDirectory)
    let trace = parseTrace path
    Assert.NotEmpty trace
    // The stable cmd prompt the C3 trace exhibits (its trailing
    // next-prompt path) — the seed for the mid-prompt join.
    let seed =
        "C:\\Users\\Kyle\\git\\pty-speak\\src\\Terminal.App>"
    let cells = replay trace seed
    // The C3 DEFECT was: this sealed NO cell (drop-on-None).
    // P2′ must seal exactly one, with the set/p result as output
    // and the next-prompt path fenced out (no bleed).
    Assert.Equal(1, List.length cells)
    let c = List.head cells
    Assert.Equal(SessionModel.IOCellPhase.Sealed, c.Phase)
    Assert.Contains("Hello, NAME!", c.OutputText)
    Assert.DoesNotContain(seed, c.OutputText)
