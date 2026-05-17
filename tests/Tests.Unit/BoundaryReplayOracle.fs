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
                    let bytes = unescape payload
                    // Loud guard, scoped to the whitespace-strip
                    // signature ONLY: a lone printable 0x20 renders
                    // as trailing whitespace and is silently
                    // stripped to an EMPTY payload in a paste→commit
                    // round-trip (C1/C2 line 12/13 hit exactly this).
                    // It is NOT a general unescape-fidelity check —
                    // the recorder counts raw PTY bytes and `unescape`
                    // is a best-effort reconstruction that legitimately
                    // differs by ~1 on large OSC/ST chunks (extraction
                    // tolerates that — it reads ContentHistory text +
                    // Screen OSC parsing, not byte-exact payloads). So
                    // fire only when the declared count is ≥1 yet the
                    // decoded payload is empty (the byte vanished).
                    let declared =
                        match Int32.TryParse(parts.[2].TrimEnd 'B') with
                        | true, n -> n
                        | _ -> -1
                    if declared >= 1 && bytes.Length = 0 then
                        failwithf
                            "%s: declared %dB but decoded 0B on '%s' — payload whitespace-stripped in a paste/commit round-trip; repair the fixture (escape a lone 0x20 as \\x20)"
                            path declared (head.Trim())
                    Some { ElapsedUs = v
                           Dir = dir
                           Bytes = bytes }
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

// ---- repo file locator ----------------------------------------------

let private repoFile (rel: string) : string =
    // `DirectoryInfo.Parent` is `DirectoryInfo | null` (F# 9
    // nullness). Walk via an explicit nullable-pattern recursion
    // (CLAUDE.md FS3261 guidance) — no `isNull` on a
    // non-nullable, no nullable→non-nullable assignment.
    let rec walk (dir: DirectoryInfo | null) : string =
        match dir with
        | null -> ""
        | d ->
            let candidate = Path.Combine(d.FullName, rel)
            if File.Exists candidate then candidate
            else walk d.Parent
    walk (DirectoryInfo(AppContext.BaseDirectory))

let private cmdFile (name: string) : string =
    repoFile (Path.Combine("docs", "boundary-capture", "cmd", name))

// ---- expectation file (R-B1 schema v1) ------------------------------
//
// One trace ⇒ one `.expected` (hand-rolled, schemaVersioned per
// ADR-0004 wire discipline). `#`/blank lines ignored; `key=value`
// (value may contain `=`). Keys:
//   schemaVersion=1
//   seedPrompt=<the mid-prompt-join seed P>
//   cellCount=<int>
//   cellN.phase=Sealed
//   cellN.commandContains=<substr>     (optional)
//   cellN.outputContains=<substr>      (optional)
//   cellN.outputNotContains=<substr>   (optional)
// Assertions are substring-based on purpose — exact Command/Output
// boundary text is slice-semantics-sensitive (P3's concern); the
// oracle pins the load-bearing invariants (seal, no bleed).

let private parseExpected (path: string) : Map<string, string> =
    File.ReadAllLines path
    |> Array.choose (fun raw ->
        let line = raw.Trim()
        if line.Length = 0 || line.StartsWith "#" then None
        else
            match line.IndexOf '=' with
            | i when i > 0 ->
                Some(line.Substring(0, i), line.Substring(i + 1))
            | _ -> None)
    |> Map.ofArray

let private assertScenario (traceName: string) (expectedName: string) =
    let tracePath = cmdFile traceName
    let expectedPath = cmdFile expectedName
    Assert.True(
        File.Exists tracePath,
        sprintf "trace %s not found from %s"
            traceName AppContext.BaseDirectory)
    Assert.True(
        File.Exists expectedPath,
        sprintf "expectation %s not found from %s"
            expectedName AppContext.BaseDirectory)
    let exp = parseExpected expectedPath
    Assert.Equal("1", exp.["schemaVersion"])
    let seed = exp.["seedPrompt"]
    let trace = parseTrace tracePath
    Assert.NotEmpty trace
    let cells = replay trace seed
    Assert.Equal(int exp.["cellCount"], List.length cells)
    cells
    |> List.iteri (fun i c ->
        let g k = Map.tryFind (sprintf "cell%d.%s" i k) exp
        match g "phase" with
        | Some "Sealed" ->
            Assert.Equal(SessionModel.IOCellPhase.Sealed, c.Phase)
        | _ -> ()
        match g "commandContains" with
        | Some s -> Assert.Contains(s, c.CommandText)
        | None -> ()
        match g "commandNotContains" with
        | Some s -> Assert.DoesNotContain(s, c.CommandText)
        | None -> ()
        match g "outputContains" with
        | Some s -> Assert.Contains(s, c.OutputText)
        | None -> ()
        match g "outputNotContains" with
        | Some s -> Assert.DoesNotContain(s, c.OutputText)
        | None -> ())

// ---- scenarios ------------------------------------------------------

[<Fact>]
let ``replay oracle: C1 echo slow — seals, command intact, no bleed`` () =
    assertScenario
        "C1-echo-hi-slow.txt" "C1-echo-hi-slow.expected"

[<Fact>]
let ``replay oracle: C2 echo fast — byte-identical, same invariants`` () =
    assertScenario
        "C2-echo-hi-fast.txt" "C2-echo-hi-fast.expected"

[<Fact>]
let ``replay oracle: C3 set/p — seals (was drop-on-None), no bleed`` () =
    assertScenario
        "C3-set-p-text-input.txt" "C3-set-p-text-input.expected"

// SKIPPED: caught a real defect (#428). cmd echoes the in-line
// erase as `\x08 \x08`, a Screen-level destructive edit; extraction
// reads linear ContentHistory (ADR 0004) which doesn't apply `\x08`,
// so backspaced chars survive in the sealed CommandText
// ("ECHO HELLOXX" not "ECHO HELLO"). The substrate fix is an
// ADR-0004-level follow-up (maintainer decision 2026-05-17); the
// trace + .expected stay in the corpus so this flips to an active
// regression guard once #428 lands. Remove the Skip then.
[<Fact(Skip = "known defect #428: backspace not applied to ContentHistory CommandText (ADR-0004 substrate follow-up)")>]
let ``replay oracle: C5 backspace/retype — deleted chars gone from CommandText`` () =
    assertScenario
        "C5-backspace-retype.txt" "C5-backspace-retype.expected"

[<Fact>]
let ``replay oracle: C6 long-idle mid-compose — one cell, command spans the gap`` () =
    assertScenario
        "C6-long-idle-midcompose.txt" "C6-long-idle-midcompose.expected"
