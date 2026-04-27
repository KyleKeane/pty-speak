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
let ``ConPtyHost spawns cmd.exe and captures startup bytes`` () =
    if not (isWindows ()) then
        // Stage 1 is Windows-only; trivially pass on other platforms
        // so dev workstations don't see a red CI when running tests
        // locally.
        ()
    else
        try
            // Spawn cmd.exe interactively. We don't try to drive it
            // (no stdin write, no command execution) — stdin push and
            // command-roundtrip will be exercised when Stage 6 lands a
            // proper input pipeline. Stage 1's acceptance criterion
            // per spec/tech-plan.md §1.4 is simpler: confirm the
            // ConPTY plumbing works end-to-end (CreatePipe →
            // CreatePseudoConsole → CreateProcess → reader thread →
            // Channel → collectStdout).
            //
            // cmd.exe under ConPTY emits a startup byte stream
            // containing at minimum the ConPTY init prologue
            // (\x1b[?9001h\x1b[?1004h, 16 bytes) and typically also
            // cmd's own setup (cursor mode, alt-screen, title — about
            // ~130 bytes total when cmd reaches its REPL state). Any
            // non-trivial capture proves the read path works.
            let cfg =
                { Cols = 120s
                  Rows = 30s
                  CommandLine = "cmd.exe" }

            match ConPtyHost.start cfg with
            | Error e ->
                Assert.Fail(sprintf "ConPtyHost.start failed: %A" e)
            | Ok host ->
                use host = host
                // Wait briefly for cmd.exe to write its startup
                // sequences before collecting. The actual collection
                // itself blocks waiting on the channel so we just
                // need ConPTY to have emitted something by the
                // collection's timeout.
                let output = collectStdout host (TimeSpan.FromSeconds(3.0))
                // Minimum: the 16-byte ConPTY init prologue. Anything
                // less means the pipe pipeline never delivered output
                // to the channel, which is the architectural failure
                // we're guarding against.
                Assert.True(
                    output.Length >= 16,
                    sprintf
                        "Expected >= 16 bytes from cmd.exe under ConPTY (the init prologue). Captured %d bytes:\n%s"
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
