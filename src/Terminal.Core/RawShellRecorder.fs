namespace Terminal.Core

open System
open System.Text
open System.Collections.Generic

/// Cycle 52 boundary-diagnostic-capture — **Instrument B**:
/// the explicitly-toggled raw PTY byte recorder.
///
/// NOT always-on (that is Instrument A, the bounded
/// boundary-decision ring). `start`/`stop` gate an in-RAM
/// event buffer that exists only for the capture window, so a
/// long session never accumulates an unbounded object. Records
/// every byte **in** (app → shell: the user's keystrokes) and
/// **out** (shell → app: everything the shell produced) with a
/// high-resolution elapsed timestamp + direction, so the
/// input / output / sub-prompt boundary can be reconstructed
/// from ground truth (the whole point of the cell-seal track).
///
/// WPF-free `Terminal.Core` module. Fed from two threads — the
/// reader loop (`Output`) and the dispatcher thread (`Input`)
/// — so all state is behind one lock. The mutable-module +
/// lock shape mirrors `EarconChannel`; `clearForTests` mirrors
/// its test-isolation hook.
module RawShellRecorder =

    type Direction =
        /// app → shell — bytes the user typed (keystrokes).
        | Input
        /// shell → app — bytes the shell produced.
        | Output

    [<Struct>]
    type private Event =
        { ElapsedUs: int64
          Dir: Direction
          Bytes: byte[] }

    let private gate: obj = obj ()
    let mutable private recording: bool = false
    let mutable private startedUtc: DateTime = DateTime.MinValue
    let private sw = System.Diagnostics.Stopwatch()
    let private events = List<Event>()
    let mutable private lastTrace: string option = None
    let mutable private lastTracePath: string option = None

    /// True while a capture is in progress.
    let isRecording () : bool =
        lock gate (fun () -> recording)

    /// Begin a capture (clearing any prior in-progress buffer).
    /// Returns false if already recording.
    let start () : bool =
        lock gate (fun () ->
            if recording then false
            else
                events.Clear()
                startedUtc <- DateTime.UtcNow
                sw.Restart()
                recording <- true
                true)

    /// Record a chunk. Cheap early-out when not recording — the
    /// PTY taps call this unconditionally and additively. A
    /// defensive copy is taken so a later mutation of the
    /// caller's buffer cannot corrupt the trace.
    let record (dir: Direction) (bytes: byte[]) : unit =
        if bytes.Length > 0 then
            lock gate (fun () ->
                if recording then
                    events.Add(
                        { ElapsedUs = sw.Elapsed.Ticks / 10L
                          Dir = dir
                          Bytes = Array.copy bytes }))

    // byte → readable, unambiguous token. Screen-reader-safe
    // and one-event-per-line (no embedded real newline) so the
    // trace stays grep-/paste-analysable; OSC-133 reads as
    // `\e]133;A\e\\`.
    let private esc (b: byte) : string =
        match b with
        | 0x1Buy -> "\\e"
        | 0x0Duy -> "\\r"
        | 0x0Auy -> "\\n"
        | 0x09uy -> "\\t"
        | 0x07uy -> "\\a"
        | _ when b >= 0x20uy && b < 0x7Fuy -> string (char b)
        | _ -> sprintf "\\x%02X" (int b)

    let private renderBytes (bytes: byte[]) : string =
        let sb = StringBuilder(bytes.Length * 2)
        for b in bytes do
            sb.Append(esc b) |> ignore
        sb.ToString()

    /// Stop the capture and format the trace. Returns
    /// `(formattedText, inBytes, outBytes)` or `None` when not
    /// recording. The formatted text is retained in RAM
    /// (`getLastTrace`) for the "copy most recent" command and
    /// the Ctrl+Shift+D fold-in; the on-disk path is recorded
    /// by the caller via `setLastTracePath`.
    let stop () : (string * int * int) option =
        lock gate (fun () ->
            if not recording then None
            else
                recording <- false
                let inB =
                    events
                    |> Seq.filter (fun e -> e.Dir = Input)
                    |> Seq.sumBy (fun e -> e.Bytes.Length)
                let outB =
                    events
                    |> Seq.filter (fun e -> e.Dir = Output)
                    |> Seq.sumBy (fun e -> e.Bytes.Length)
                let sb = StringBuilder()
                sb.AppendLine(
                    sprintf
                        "=== pty-speak raw shell trace — started %s UTC ==="
                        (startedUtc.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff")))
                |> ignore
                sb.AppendLine(
                    sprintf
                        "=== %d events; IN %d bytes; OUT %d bytes ==="
                        events.Count
                        inB
                        outB)
                |> ignore
                sb.AppendLine(
                    "=== format: +<elapsed_us>us <DIR> <nbytes>B | <escaped>  (\\e=ESC \\r \\n \\t \\a \\xHH) ===")
                |> ignore
                for e in events do
                    let dir =
                        match e.Dir with
                        | Input -> "IN "
                        | Output -> "OUT"
                    sb.AppendLine(
                        sprintf
                            "+%dus %s %dB | %s"
                            e.ElapsedUs
                            dir
                            e.Bytes.Length
                            (renderBytes e.Bytes))
                    |> ignore
                let text = sb.ToString()
                lastTrace <- Some text
                events.Clear()
                Some(text, inB, outB))

    /// The most recent finished trace's formatted text (RAM
    /// only; survives until the next `start` or `clearForTests`).
    let getLastTrace () : string option =
        lock gate (fun () -> lastTrace)

    let setLastTracePath (path: string) : unit =
        lock gate (fun () -> lastTracePath <- Some path)

    /// The on-disk path the most recent trace was written to.
    let getLastTracePath () : string option =
        lock gate (fun () -> lastTracePath)

    /// Test isolation — drop all state.
    let clearForTests () : unit =
        lock gate (fun () ->
            recording <- false
            startedUtc <- DateTime.MinValue
            events.Clear()
            lastTrace <- None
            lastTracePath <- None)
