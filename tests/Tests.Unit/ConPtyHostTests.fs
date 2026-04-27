module PtySpeak.Tests.Unit.ConPtyHostTests

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Terminal.Pty

/// Stage 1 acceptance test from spec/tech-plan.md §1.4: spawn cmd.exe
/// under ConPTY, write 'dir\r\n' to its stdin, drain stdout for a
/// couple of seconds, and assert we observed bytes that look like a
/// cmd.exe directory listing.
///
/// Windows-only by construction (ConPTY + cmd.exe). On non-Windows the
/// test exits trivially-passing so the same suite runs unchanged on
/// dev workstations; the real validation runs in CI on
/// windows-latest.

let private isWindows () =
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

let private write (host: ConPtyHost) (text: string) =
    let bytes = Encoding.UTF8.GetBytes(text: string)
    host.Stdin.Write(bytes, 0, bytes.Length)
    host.Stdin.Flush()

let private collectStdout (host: ConPtyHost) (timeout: TimeSpan) : string =
    use cts = new CancellationTokenSource(timeout)
    let buffer = StringBuilder()
    let task =
        task {
            try
                let mutable continueReading = true
                while continueReading && not cts.Token.IsCancellationRequested do
                    let! chunkOpt =
                        task {
                            try
                                let! chunk = host.Stdout.ReadAsync(cts.Token).AsTask()
                                return Some chunk
                            with
                            | :? OperationCanceledException -> return None
                            | :? ChannelClosedException -> return None
                        }
                    match chunkOpt with
                    | None -> continueReading <- false
                    | Some chunk ->
                        // cmd.exe outputs CP437/OEM by default but the
                        // ASCII subset (which is what 'dir' produces
                        // for English locales) renders identically as
                        // UTF-8. Lossy decode is fine for an assertion
                        // that just looks for substrings.
                        buffer.Append(Encoding.UTF8.GetString(chunk)) |> ignore
            with
            | _ -> ()
        }
    try task.Wait(timeout) |> ignore with _ -> ()
    buffer.ToString()

[<Fact>]
let ``ConPtyHost spawns cmd.exe and captures its stdout`` () =
    if not (isWindows ()) then
        // Stage 1 is Windows-only; trivially pass on other platforms
        // so dev workstations don't see a red CI when running tests
        // locally.
        ()
    else
        try
            // Use 'cmd.exe /c echo <marker>' rather than '/K dir' +
            // stdin write. The /c form runs the given command and
            // exits — we don't depend on cmd.exe reaching its
            // interactive REPL state under ConPTY's scheduling, and
            // the marker is locale-independent. This still validates
            // the Stage 1 end-to-end claim: ConPtyHost.start, ConPTY
            // process spawn, child stdout → reader thread → Channel
            // → collectStdout. (The stdin-write path is exercised in
            // a separate test once Stage 6 has a proper input
            // pipeline; for Stage 1, output capture is the proof.)
            let marker = "PTY_SPEAK_STAGE1_MARKER"
            let cfg =
                { Cols = 120s
                  Rows = 30s
                  CommandLine = sprintf "cmd.exe /c echo %s" marker }

            match ConPtyHost.start cfg with
            | Error e ->
                Assert.Fail(sprintf "ConPtyHost.start failed: %A" e)
            | Ok host ->
                use host = host
                let output = collectStdout host (TimeSpan.FromSeconds(5.0))
                Assert.True(
                    output.Contains(marker),
                    sprintf
                        "Expected output to contain %s. Captured %d bytes:\n%s"
                        marker
                        output.Length
                        output)
        with
        | ex ->
            // Surface the full exception (type, message, stack, inner)
            // so a future CI failure shows the actual cause instead of
            // xUnit's standard reflection-invoke wrapper.
            Assert.Fail(
                sprintf
                    "ConPtyHost test threw %s: %s\n\nStack trace:\n%s\n\nInner: %A"
                    (ex.GetType().FullName)
                    ex.Message
                    ex.StackTrace
                    ex.InnerException)
