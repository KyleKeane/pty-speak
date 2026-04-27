namespace Terminal.Pty

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Terminal.Pty.Native

/// High-level host for a ConPTY session. Wraps PtySession with:
///   * a FileStream on stdin so callers can `WriteAsync` bytes
///   * a background reader task that drains stdout into a Channel of
///     byte chunks (consumers iterate via ReadAllAsync)
///   * a CancellationTokenSource that signals shutdown to the reader
///
/// Stage 1 scope: enough to spawn cmd.exe, push 'dir\r\n' through
/// stdin, and observe the directory listing arrive via stdout.
/// Subsequent stages will replace the byte-array Channel with a
/// System.IO.Pipelines reader and feed it into the VT parser, but the
/// outer shape (one ConPtyHost per child process, IDisposable for
/// cleanup) carries through.
type ConPtyHost private (session: PtySession,
                         stdin: FileStream,
                         stdout: ChannelReader<byte array>,
                         readerTask: Task,
                         cts: CancellationTokenSource) =

    /// Bytes written here go to the child's stdin. The stream is
    /// synchronous because ConPTY forbids OVERLAPPED I/O on its pipe
    /// ends; calls block until the kernel accepts the bytes.
    member _.Stdin: FileStream = stdin

    /// Stream of stdout chunks. Each chunk is a heap-allocated byte
    /// array of whatever ReadFile returned in a single call (typically
    /// up to a few KB). The channel completes when the child closes
    /// its output or shutdown is requested.
    member _.Stdout: ChannelReader<byte array> = stdout

    member _.ProcessId: uint32 = session.ProcessId

    /// Convenience: wait for the child process to exit. Returns when
    /// WaitForSingleObject signals on the process handle. Does not
    /// itself stop the reader — that happens when the pipe drains.
    member _.WaitForExitAsync(?timeoutMs: int) : Task<bool> =
        let timeout = defaultArg timeoutMs Threading.Timeout.Infinite
        Task.Run(fun () ->
            let result = Win32.WaitForSingleObject(session.ProcessHandle, uint32 timeout)
            // WAIT_OBJECT_0 = 0x00000000 means signalled (process exited).
            result = 0u)

    interface IDisposable with
        member _.Dispose() =
            // Signal the reader to stop. The reader may already be
            // exiting due to ERROR_BROKEN_PIPE.
            try cts.Cancel() with _ -> ()
            // Best-effort: terminate the child if still running so the
            // pipe can drain.
            try
                let waitResult = Win32.WaitForSingleObject(session.ProcessHandle, 0u)
                if waitResult <> 0u then
                    Win32.TerminateProcess(session.ProcessHandle, 1u) |> ignore
            with _ -> ()
            // Wait briefly for the reader to wind down so we don't
            // dispose handles out from under it. 500 ms is plenty.
            try readerTask.Wait(500) |> ignore with _ -> ()
            try stdin.Dispose() with _ -> ()
            try cts.Dispose() with _ -> ()
            (session :> IDisposable).Dispose()

/// Module factory functions for ConPtyHost. Kept separate from the
/// type so the constructor stays private and the only entry point is
/// `start`.
module ConPtyHost =

    /// Background task body: pulls bytes off the stdout pipe and
    /// publishes them to the channel. Exits cleanly on cancellation,
    /// EOF, or ERROR_BROKEN_PIPE.
    let private readerLoop (handle: SafeFileHandle)
                           (writer: ChannelWriter<byte array>)
                           (ct: CancellationToken) : Task =
        Task.Run(fun () ->
            // FileStream over the SafeFileHandle gives us a managed
            // ReadFile wrapper. ConPTY forbids OVERLAPPED I/O so we
            // pass useAsync=false; the stream is synchronous.
            let reader = new FileStream(handle, FileAccess.Read, 4096, false)
            let buffer = Array.zeroCreate<byte> 4096
            let mutable running = true
            try
                while running && not ct.IsCancellationRequested do
                    let n =
                        try reader.Read(buffer, 0, buffer.Length)
                        with
                        | :? IOException -> 0
                        | :? ObjectDisposedException -> 0
                    if n <= 0 then
                        running <- false
                    else
                        let chunk = Array.zeroCreate<byte> n
                        Buffer.BlockCopy(buffer, 0, chunk, 0, n)
                        writer.TryWrite(chunk) |> ignore
            finally
                writer.TryComplete() |> ignore
                reader.Dispose())

    /// Spawn a child under a fresh pseudo-console and return a
    /// ConPtyHost wrapping it. Returns Error if any step of the
    /// PseudoConsole.create lifecycle fails.
    let start (cfg: PtyConfig) : Result<ConPtyHost, PtyCreateError> =
        match PseudoConsole.create cfg with
        | Error e -> Error e
        | Ok session ->
            // Bounded channel: 256 chunks (~1 MB at 4 KB each) is
            // plenty for Stage 1's byte-trickle test. Production
            // pipelines will use System.IO.Pipelines on top of this.
            let chan = Channel.CreateBounded<byte array>(
                BoundedChannelOptions(256,
                    SingleReader = false,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait))
            let cts = new CancellationTokenSource()
            let stdin = new FileStream(session.Stdin, FileAccess.Write, 4096, false)
            let task = readerLoop session.Stdout chan.Writer cts.Token
            Ok(new ConPtyHost(session, stdin, chan.Reader, task, cts))
