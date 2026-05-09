namespace Terminal.Core

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Cycle 24c — bounded-channel async file writer for SessionTuple
/// JSONL persistence. Consumes `SessionModel.formatTupleAsJsonl`
/// (Cycle 24b) and appends each finalised tuple as one JSONL line
/// to a per-shell-session file.
///
/// Design (mirrors `FileLogger.fs:120-455`):
///
///   * **Bounded channel** with `BoundedChannelFullMode.Wait`.
///     If the channel ever fills (disk stall), the enqueue call
///     back-pressures into the SessionModel state machine.
///     Decades-stable bias: losing a tuple is worse than a
///     visible UI hitch. SessionModel.applyAndCapture fires at
///     most once per command (~1-10 Hz peak in real usage); a
///     256-capacity channel with a working disk drains at
///     >>1000 lines/sec — the back-pressure path should never
///     trigger in practice.
///   * **Single background drain task** serialises file I/O so
///     the WPF dispatcher / parser thread / coalescer never
///     block on disk during the common path.
///   * **One file per shell session.** Shell-switch
///     (`Ctrl+Shift+1/2/3`) creates a fresh `SessionModel.T` with
///     a new `SessionId`; the composition root constructs a fresh
///     sink for the new session, so file paths never collide.
///   * **Crash-safe-enough.** `FileShare.ReadWrite` allows
///     external readers (Notepad, future replay-CLI) to open the
///     active session-log while the sink is writing; each line
///     is flushed via `StreamWriter.Flush` so a hard crash loses
///     at most the last partial line. JSONL is line-delimited;
///     a partial last line is detectable and skippable by any
///     deserializer.
///   * **Silent error handling.** File I/O exceptions
///     (permission-denied, disk-full, path-too-long, etc.) are
///     swallowed — logging via `ILogger` instead. The maintainer
///     uses a screen reader; throwing into the NVDA path is
///     unacceptable.
///   * **No UTF-8 BOM.** `UTF8Encoding(false)` per the
///     `formatTupleAsJsonl` design doc — adding a BOM would
///     break the byte-for-byte stability the wire format
///     promises.
///
/// **Out of scope this cycle**:
///
///   * `MaxSessionSizeMb`-driven rotation (deferred).
///   * `Always` mode synchronous flush (Cycle 24d implements
///     true sync; this cycle's writer treats Always identically
///     to SessionLog and the composition root logs a Warning).
///   * Secrets sanitisation (Cycle 24d adds env-var deny-list
///     scrubbing of `commandText` before write).
///   * Deserializer + replay CLI (Cycle 25+).

/// Configuration for a `SessionLogWriterSink`. Constructed at
/// composition-root time from `Config.SessionPersistence` plus
/// the active SessionModel's `SessionId`.
type SessionLogWriterOptions =
    { /// Directory containing session-log files. Resolved at
      /// the composition root; the sink itself does not consult
      /// `Config`. Pass an absolute path; the sink calls
      /// `Directory.CreateDirectory` once at startup
      /// (idempotent).
      OutputDirectory: string
      /// SessionModel.T.SessionId. The output file is named
      /// `session-<SessionId>.jsonl`. Per the per-shell-session
      /// model (SESSION-MODEL Q5), shell-switch creates a fresh
      /// SessionModel + fresh sink, so SessionIds + filenames
      /// never collide.
      SessionId: Guid
      /// BoundedChannel capacity. 256 is ~2.5x a typical
      /// 100-tuple session History; far above realistic
      /// backlog at 1-10 commands/sec.
      ChannelCapacity: int }

module SessionLogWriterOptions =

    /// Default channel capacity. Sized for "should never block"
    /// at 1-10 commands/sec while remaining a tight cap on
    /// in-flight memory under any failure mode.
    [<Literal>]
    let DefaultChannelCapacity : int = 256

    /// Resolve the default output directory:
    /// `%LOCALAPPDATA%\PtySpeak\sessions`. Mirrors the
    /// `FileLogger` `%LOCALAPPDATA%` chain. `Environment
    /// .GetFolderPath(LocalApplicationData)` returns "" when
    /// the env var is unset (rare; sandbox / minimal accounts);
    /// in that case fall back to `Path.GetTempPath()` so the
    /// sink can still open a writable file rather than
    /// throwing at composition-root construction.
    let resolveDefaultOutputDirectory () : string =
        let baseDir =
            let raw =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData)
            if String.IsNullOrEmpty(raw) then Path.GetTempPath() else raw
        Path.Combine(baseDir, "PtySpeak", "sessions")

    /// Build a `SessionLogWriterOptions` for the given
    /// SessionId, using the supplied `outputDirOverride` when
    /// `Some` (from `Config.SessionPersistence.OutputDir`) and
    /// the default `%LOCALAPPDATA%\PtySpeak\sessions` chain
    /// otherwise.
    let createDefault
            (sessionId: Guid)
            (outputDirOverride: string option)
            : SessionLogWriterOptions
            =
        let dir =
            match outputDirOverride with
            | Some d when not (String.IsNullOrWhiteSpace(d)) -> d
            | _ -> resolveDefaultOutputDirectory ()
        { OutputDirectory = dir
          SessionId = sessionId
          ChannelCapacity = DefaultChannelCapacity }

/// Bounded-channel async writer that owns one session-log file.
/// One sink per shell session. Disposed at shell-switch (the
/// composition root constructs a fresh sink for the new shell)
/// and at app shutdown.
///
/// Threading: `Enqueue` is safe to call from any thread —
/// enqueues serialise on the channel writer. The drain task
/// runs on a single background thread and is the only writer
/// to the file.
type SessionLogWriterSink
        (options: SessionLogWriterOptions, logger: ILogger) =

    let channel =
        let opts =
            BoundedChannelOptions(
                options.ChannelCapacity,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false)
        Channel.CreateBounded<SessionModel.SessionTuple>(opts)

    let cts = new CancellationTokenSource()

    let activeLogPath : string =
        let fileName =
            sprintf "session-%s.jsonl"
                (options.SessionId.ToString("D"))
        Path.Combine(options.OutputDirectory, fileName)

    let drainTask =
        Task.Run(fun () ->
            task {
                try
                    Directory.CreateDirectory(options.OutputDirectory)
                    |> ignore
                with ex ->
                    logger.LogError(
                        ex,
                        "SessionLogWriter: failed to create output directory {Dir}; sink will be inert.",
                        options.OutputDirectory)

                let mutable writer : StreamWriter | null = null

                let openWriter () =
                    let stream =
                        new FileStream(
                            activeLogPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite)
                    // No-BOM UTF-8 per the Cycle 24b wire format;
                    // a BOM would break byte-for-byte stability.
                    new StreamWriter(stream, UTF8Encoding(false))

                let ensureWriter () =
                    if writer = null then
                        writer <- openWriter ()

                let writeOne (tuple: SessionModel.SessionTuple) =
                    try
                        // Serialize THEN open the writer; if the
                        // serializer throws (lone-surrogate
                        // contract per Cycle 24b), we don't waste
                        // a file handle.
                        let line = SessionModel.formatTupleAsJsonl tuple
                        ensureWriter ()
                        match writer with
                        | null -> ()
                        | w ->
                            // formatTupleAsJsonl already includes
                            // the trailing '\n'; use Write (NOT
                            // WriteLine) to avoid an extra
                            // platform-dependent line terminator.
                            w.Write(line)
                    with ex ->
                        // Persistence must NEVER crash the app or
                        // surface to NVDA. Log + skip; the next
                        // tuple gets a fresh shot.
                        logger.LogError(
                            ex,
                            "SessionLogWriter: write failed for tuple {TupleId}; tuple skipped.",
                            tuple.Id)

                let mutable keepGoing = true
                let reader = channel.Reader
                try
                    while keepGoing && not cts.Token.IsCancellationRequested do
                        let! got = reader.WaitToReadAsync(cts.Token).AsTask()
                        if not got then
                            keepGoing <- false
                        else
                            let mutable peek =
                                Unchecked.defaultof<SessionModel.SessionTuple>
                            while reader.TryRead(&peek) do
                                writeOne peek
                            try
                                match writer with
                                | null -> ()
                                | w -> w.Flush()
                            with _ -> ()
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogError(
                        ex,
                        "SessionLogWriter: drain loop exited unexpectedly.")

                // Final drain after cancellation: pick up any
                // tuples enqueued before shutdown.
                let mutable final =
                    Unchecked.defaultof<SessionModel.SessionTuple>
                while channel.Reader.TryRead(&final) do
                    writeOne final

                try
                    match writer with
                    | null -> ()
                    | w ->
                        w.Flush()
                        (w :> IDisposable).Dispose()
                with _ -> ()
            } :> Task)

    /// Enqueue a finalised SessionTuple. Returns when the tuple
    /// is queued (NOT when it's written to disk; that happens on
    /// the drain task). Under normal load returns synchronously
    /// in <100µs. Under a sustained disk stall the call
    /// back-pressures (`BoundedChannelFullMode.Wait`); the
    /// caller's thread blocks until the drain catches up.
    ///
    /// Safe to call from any thread. Idempotent on
    /// post-`Dispose` calls (silently dropped).
    member _.Enqueue (tuple: SessionModel.SessionTuple) : unit =
        try
            // `WriteAsync` blocks (back-pressure) when the
            // channel is full; `WriteAsync(...).AsTask().GetAwaiter()
            // .GetResult()` propagates that semantic to the
            // synchronous caller.
            channel.Writer.WriteAsync(tuple, cts.Token).AsTask()
                .GetAwaiter().GetResult()
        with
        | :? OperationCanceledException -> ()
        | :? ChannelClosedException -> ()
        | ex ->
            logger.LogWarning(
                ex,
                "SessionLogWriter: enqueue failed for tuple {TupleId}; tuple dropped.",
                tuple.Id)

    /// Path to the active session-log file. Useful for tests and
    /// for a future diagnostic helper (Cycle 24e) that announces
    /// the path on demand.
    member _.ActiveLogPath : string = activeLogPath

    interface IDisposable with
        member _.Dispose() =
            // TryComplete signals the drain loop to finish
            // pending entries then exit. Cancel is a backstop in
            // case the loop is parked in WaitToReadAsync. Wait
            // gives the drain task a bounded window to complete
            // its final flush + dispose; on timeout we proceed
            // anyway — the OS reclaims the file handle on
            // process exit.
            try channel.Writer.TryComplete() |> ignore with _ -> ()
            try cts.Cancel() with _ -> ()
            try drainTask.Wait(2000) |> ignore with _ -> ()
            try cts.Dispose() with _ -> ()
