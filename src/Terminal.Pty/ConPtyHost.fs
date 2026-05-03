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
type ConPtyHost internal (session: PtySession,
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

    /// Stage 6 PR-B — write bytes to the child's stdin. Convenience
    /// wrapper around `Stdin.Write` so callers don't have to reach
    /// through the FileStream property. Synchronous (the underlying
    /// FileStream is non-async per ConPTY's OVERLAPPED-I/O ban) and
    /// must be called from a single thread — Stage 6's wiring funnels
    /// every write through the WPF dispatcher thread, so serialisation
    /// is structural rather than enforced here.
    member _.WriteBytes(bytes: byte[]) : unit =
        if bytes.Length > 0 then
            stdin.Write(bytes, 0, bytes.Length)
            // Flush so the child sees the keystroke immediately —
            // the FileStream's 4-KB write buffer would otherwise
            // hold a single-byte arrow keypress until the next
            // write or close.
            stdin.Flush()

    /// Stage 6 PR-B — resize the underlying pseudo-console grid.
    /// Returns Ok on success, Error with the Win32 HRESULT
    /// otherwise. Idempotent and thread-safe per
    /// `ResizePseudoConsole`'s documented contract; production
    /// callers debounce upstream so the child shell isn't asked
    /// to re-layout for every WPF SizeChanged tick.
    member _.Resize(cols: int16, rows: int16) : Result<unit, int> =
        PseudoConsole.resize session cols rows

    /// Stage 6 PR-B — the Job Object handle owning the child +
    /// any process the child spawns. Exposed so tests can assert
    /// the handle is valid post-spawn; production code never
    /// interacts with this directly (lifecycle binds to the
    /// `PtySession` dispose path inside this host).
    member _.JobHandle = session.JobHandle

    /// Stage 7 PR-A — count of parent environment variables
    /// stripped by the env-scrub deny-list (`*_TOKEN`, `*_SECRET`,
    /// `*_KEY` minus the `ANTHROPIC_API_KEY` exemption,
    /// `*_PASSWORD`) before this child was spawned. Exposed so
    /// `Program.fs compose ()` can log the count at `Information`
    /// level. Names and values are NEVER captured (per
    /// `SECURITY.md` logging discipline).
    member _.EnvScrubStrippedCount: int = session.EnvScrubStrippedCount

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
