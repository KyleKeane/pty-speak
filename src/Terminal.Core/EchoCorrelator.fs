namespace Terminal.Core

open System
open System.Text
open System.Collections.Generic

/// Cycle 38c — input-echo correlator for cmd / PowerShell.
///
/// **Problem.** Cmd (and PowerShell with default `Set-PSReadLineOption`
/// echo) writes the user's typed bytes back through PTY-stdout as
/// the user types — `echo hello\r` produces 11 echoed bytes followed
/// by the command output `hello\r\n` followed by the next prompt.
/// Without correlation, NVDA reads ALL of this as one announce.
///
/// **Solution.** This module tracks bytes written to PTY-stdin via
/// `ConPtyHost.WriteBytes` (wired at `Program.fs:2417`). When a
/// `StreamChunk` `OutputEvent` arrives at `EchoSuppressorProfile`,
/// the profile asks this correlator how many leading bytes of the
/// payload match the recently-typed input. Matched bytes are
/// stripped from the announce text but preserved in the FileLogger
/// audit trail so post-hoc debugging via the `Ctrl+Shift+D` bundle
/// can verify suppression behaviour.
///
/// **CR-LF normalisation.** Cmd translates a `\r` (0x0D, the Enter
/// keystroke per `KeyEncoding.fs`) input byte to `\r\n` (0x0D 0x0A)
/// in the echo. The matcher detects this and consumes both
/// payload bytes against the single correlator byte. Without
/// normalisation, `echo hello\r` would match against `echo hello\r`
/// but fail at the trailing `\n` cmd appends.
///
/// **Time-bounded.** Entries older than `Parameters.MaxAgeMs`
/// (default 5 seconds) are skipped during matching and dropped from
/// the buffer. Without this, a stale typed byte from minutes ago
/// could spuriously match a fresh output. 5 seconds is generous —
/// even a slow shell start-up usually echoes within 200ms.
///
/// **Bounded buffer.** `Parameters.MaxBufferSize` (default 4096
/// bytes) caps memory. When a `recordWrite` would exceed the cap,
/// the oldest entries are dropped to make room.
///
/// **Thread safety.** All mutating operations take a per-instance
/// lock. WriteBytes is called from the WPF UI thread; matching is
/// called from the OutputDispatcher pump thread; the lock keeps the
/// buffer consistent across both.
module EchoCorrelator =

    /// Configuration parameters. Mutable for unit-test
    /// scenarios; production composition uses `defaultParameters`.
    type Parameters =
        { MaxBufferSize: int
          MaxAgeMs: int }

    let defaultParameters: Parameters =
        { MaxBufferSize = 4096
          MaxAgeMs = 5000 }

    /// Internal per-byte record. Each byte the user types is
    /// stored alongside the timestamp at which it was written
    /// to PTY-stdin.
    type private TimedByte =
        { Byte: byte
          RecordedAt: DateTime }

    /// Producer state. Mutable class — the composition root
    /// holds one instance, wires `recordWrite` into the WriteBytes
    /// path, and passes the instance to `EchoSuppressorProfile`
    /// for matching at dispatch time.
    type T(parameters: Parameters) =
        let gate: obj = obj ()
        let pending: LinkedList<TimedByte> = LinkedList<TimedByte>()
        member val internal Parameters: Parameters = parameters with get
        member internal _.Gate = gate
        member internal _.Pending = pending

    /// Create a fresh correlator with the supplied parameters.
    let create (parameters: Parameters) : T = T(parameters)

    /// Drop entries older than `MaxAgeMs` from the FRONT of the
    /// pending buffer (the head is the oldest). Called internally
    /// before any matching or recording operation so callers don't
    /// have to worry about stale entries.
    let private expireOld (state: T) (now: DateTime) : unit =
        let maxAge = TimeSpan.FromMilliseconds(float state.Parameters.MaxAgeMs)
        let mutable continueLoop = true
        while continueLoop do
            match state.Pending.First with
            | null -> continueLoop <- false
            | head ->
                if (now - head.Value.RecordedAt) > maxAge then
                    state.Pending.RemoveFirst()
                else
                    continueLoop <- false

    /// Record a chunk of bytes written to PTY-stdin. Each byte
    /// is tagged with `now`. Buffer is capped at
    /// `MaxBufferSize`; over-cap pushes evict the oldest entries.
    let recordWrite (state: T) (now: DateTime) (bytes: byte[]) : unit =
        if bytes.Length > 0 then
            lock state.Gate (fun () ->
                expireOld state now
                for b in bytes do
                    state.Pending.AddLast(
                        { Byte = b; RecordedAt = now })
                    |> ignore
                while state.Pending.Count > state.Parameters.MaxBufferSize do
                    state.Pending.RemoveFirst())

    /// Try to match a leading prefix of `payload` against the
    /// pending buffer. CR-LF normalisation: when the correlator
    /// has `\r` (0x0D) and the payload has `\r\n` (0x0D 0x0A) at
    /// the same position, both payload bytes are consumed against
    /// the one correlator byte.
    ///
    /// **Side effect**: if any bytes matched, the corresponding
    /// correlator entries are CONSUMED (removed from pending).
    /// This is the atomic match-then-consume operation
    /// `EchoSuppressorProfile` calls per StreamChunk so a given
    /// input byte echoes exactly once.
    ///
    /// Returns the count of PAYLOAD bytes matched (which, with
    /// CR-LF normalisation, may exceed the count of correlator
    /// bytes consumed).
    let matchAndConsumeEchoPrefix
            (state: T)
            (now: DateTime)
            (payload: string)
            : int =
        if String.IsNullOrEmpty payload then 0
        else
            lock state.Gate (fun () ->
                expireOld state now
                let payloadBytes = Encoding.UTF8.GetBytes(payload)
                let mutable payloadIdx = 0
                // F# 9 strict-nullness: `LinkedList<T>.First` and
                // `LinkedListNode<T>.Next` both return
                // `LinkedListNode<T> | null`. Annotate the cell
                // explicitly and `nonNull`-coerce inside the loop
                // body where the guard ensures non-null. Mirrors
                // the F# 9 nullness pattern called out in
                // `CLAUDE.md` ("FS3261 is the canonical CI failure").
                let mutable currentNode : LinkedListNode<TimedByte> | null =
                    state.Pending.First
                let mutable stop = false
                while not stop
                      && payloadIdx < payloadBytes.Length
                      && not (isNull currentNode) do
                    let nodeNN = nonNull currentNode
                    let cByte = nodeNN.Value.Byte
                    let pByte = payloadBytes.[payloadIdx]
                    if cByte = pByte then
                        payloadIdx <- payloadIdx + 1
                        currentNode <- nodeNN.Next
                        // CR-LF expansion: cmd translates a typed
                        // `\r` (the Enter keystroke per
                        // `KeyEncoding`) into `\r\n` on the
                        // echo path. After matching the `\r` in
                        // both, look ahead one byte in the
                        // payload — if it's `\n`, consume it
                        // too without advancing the correlator.
                        // Handles both "correlator ends at \r"
                        // (e.g. user typed `echo` + Enter) and
                        // "correlator has more after \r" (queued
                        // additional input).
                        if cByte = 0x0Duy
                           && payloadIdx < payloadBytes.Length
                           && payloadBytes.[payloadIdx] = 0x0Auy then
                            payloadIdx <- payloadIdx + 1
                    else
                        stop <- true
                // Consume the matched correlator entries.
                let mutable head : LinkedListNode<TimedByte> | null =
                    state.Pending.First
                while not (isNull head)
                      && not (obj.ReferenceEquals(head, currentNode)) do
                    state.Pending.RemoveFirst()
                    head <- state.Pending.First
                payloadIdx)

    /// Clear all pending bytes. Called on shell-switch (alongside
    /// the existing `HeuristicPromptDetector` / `SelectionDetector`
    /// resets in `Program.fs`) so a stale byte from the prior shell
    /// can't match a fresh output on the new shell.
    let reset (state: T) : unit =
        lock state.Gate (fun () -> state.Pending.Clear())

    /// Test-only — read pending byte count without consuming.
    let internal pendingCount (state: T) : int =
        lock state.Gate (fun () -> state.Pending.Count)
