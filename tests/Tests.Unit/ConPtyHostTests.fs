module PtySpeak.Tests.Unit.ConPtyHostTests

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
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
let ``ConPtyHost spawns cmd.exe and round-trips dir`` () =
    if not (isWindows ()) then
        // Stage 1 is Windows-only; trivially pass on other platforms
        // so dev workstations don't see a red CI when running tests
        // locally.
        ()
    else
        let cfg =
            { Cols = 120s
              Rows = 30s
              CommandLine = "cmd.exe /K @echo off & prompt $G" }

        match ConPtyHost.start cfg with
        | Error e ->
            Assert.Fail(sprintf "ConPtyHost.start failed: %A" e)
        | Ok host ->
            use host = host
            // Wait briefly for cmd.exe to print its initial prompt
            // before we send anything. 250 ms is plenty.
            Thread.Sleep(250)

            // Send 'dir' followed by 'exit' so cmd.exe terminates and
            // the stdout pipe drains naturally rather than waiting on
            // the 2-second timeout.
            write host "dir\r\nexit\r\n"

            let output = collectStdout host (TimeSpan.FromSeconds(5.0))

            // 'dir' on Windows always prints either ' Directory of '
            // or '<DIR>' (folder marker) at minimum. Looking for the
            // simplest string that's stable across locales: '<DIR>'
            // appears next to every subdirectory, and a Windows
            // user's home / repo / build dir always has at least one.
            // If the test environment is unexpectedly empty, fall
            // back to ' bytes' which appears in dir's summary line.
            let stable = output.Contains("<DIR>") || output.Contains(" bytes")
            Assert.True(
                stable,
                sprintf
                    "Expected dir output marker in stdout. Captured %d bytes:\n%s"
                    output.Length
                    output)
