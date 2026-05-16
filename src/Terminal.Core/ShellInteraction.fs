namespace Terminal.Core

open System
open System.Text.RegularExpressions

/// Cycle 48 (ADR 0003) — semantic state machine over shell
/// interaction. Sits above ContentHistory / Screen /
/// SessionModel and below the announce layer. Classifies the
/// session as either `Composing` (waiting for the user to
/// type) or `Executing` (cmd is doing work for us); transitions
/// drive announce, earcon, and review-cursor decisions.
///
/// **Cycle 48 PR-B scope (this PR)**: types + pure
/// `tryTransition` + sub-prompt detector + a `State` container
/// + observer hooks. The composition root wires the existing
/// signal sources (Enter pressed, PromptStart fired, idle
/// ticks, byte-arrived) into this module and **logs**
/// transitions at Info. Announce routing is unchanged in PR-B.
///
/// PR-C will add `Source : EntrySource` to `ContentHistory.Entry`
/// using this state to classify each append. PR-D adds the
/// keyboard-handler-driven `UserInputBuffer`. PR-E switches
/// announce routing onto the state-machine transitions and
/// retires today's idle-flush. PR-F is the closure audit.
///
/// See [`docs/adr/0003-shell-interaction-state-machine.md`](../../docs/adr/0003-shell-interaction-state-machine.md)
/// for the full design.
module ShellInteraction =

    /// Cycle 48 PR-C (ADR 0003 §9.5) — `EntrySource` lives on
    /// `ContentHistory.EntrySource`. PR-B defined a copy here;
    /// PR-C moved it upstream so `ContentHistory.Entry` can
    /// carry the field directly. Re-exported here for callers
    /// that still reach via `ShellInteraction.EntrySource`.
    type EntrySource = ContentHistory.EntrySource

    /// Composing — pty-speak is waiting for user input. cmd's
    /// output during this state is treated as *echo* of the
    /// user's typing (or of pty-speak's own writes, in the
    /// diagnostic-battery / scripted case). Announces are
    /// suppressed; `EntrySource` is `UserInputEcho` for
    /// non-marker entries.
    ///
    /// `SinglekeySubmit` is true when the prompt that
    /// preceded this Composing burst matched a "single-key
    /// submit" pattern (`pause`, `choice /c …`). The keyboard
    /// handler in PR-D will treat the next non-modifier key
    /// as the submit instead of waiting for Enter.
    [<Struct>]
    type ComposingData =
        { EnteredAt : DateTime
          PromptText : string voption
          SinglekeySubmit : bool }

    /// Executing — cmd is doing work for us. Output is real;
    /// it accumulates and gets announced (per
    /// `ShellPolicy.Streaming`). PR-B doesn't accumulate the
    /// bytes (announce routing is unchanged); the
    /// `OutputLastByte*` fields support sub-prompt detection
    /// (transition [c]).
    [<Struct>]
    type ExecutingData =
        { EnteredAt : DateTime
          SubmittedCommand : string
          OutputLastByteIsLf : bool
          OutputLastByteAt : DateTime }

    type InteractionState =
        | Composing of ComposingData
        | Executing of ExecutingData

    /// Map an `InteractionState` to the `EntrySource` that
    /// non-marker entries appended during that state should
    /// carry. Used by the composition root's
    /// `setSourceResolver` delegate. Marker entries always get
    /// `EntrySource.BoundaryMarker` regardless of state.
    let entrySourceFor (state : InteractionState) : ContentHistory.EntrySource =
        match state with
        | Composing _ ->
            ContentHistory.EntrySource.UserInputEcho
        | Executing _ ->
            ContentHistory.EntrySource.CmdOutput

    /// Cycle 48 PR-D (ADR 0003 §5.1) — canonical record of the
    /// in-progress command line the user is composing. Updated
    /// at byte-write time (in the `writePtyBytes` wrapper) per
    /// the simplified MVP design: printable ASCII (0x20–0x7E)
    /// → AppendChar; BS (0x08) → Backspace; CR (0x0D) →
    /// Capture + clear (the captured text is the
    /// `EnterPressed` transition's `submittedCommand`).
    ///
    /// Self-locked. The keyboard pipeline runs on the WPF UI
    /// thread; the byte-write wrapper that drives the buffer
    /// runs on the UI thread too. Including the lock anyway so
    /// any future cross-thread reader (e.g., the UIA Text-
    /// pattern materialiser substituting buffer content for
    /// the active TextSpan in PR-E) is safe.
    ///
    /// The arrow / Home / End / Tab / paste cases are NOT
    /// handled in PR-D's byte-stream-driven implementation —
    /// arrow keys send escape sequences that the simple
    /// printable-ASCII filter ignores; paste sends bytes that
    /// land in the buffer one-by-one (acceptable for plain
    /// pastes; multi-line pastes with embedded `\r` will
    /// trigger multiple Capture calls, one per line). Refining
    /// to true key-level tracking is deferred to a future
    /// cycle (would require routing the `KeyCode` through to
    /// the buffer in addition to the encoded bytes).
    type UserInputBuffer() =
        let gate : obj = obj ()
        let chars = ResizeArray<char>()
        let mutable cursorIndex = 0

        member _.Count = lock gate (fun () -> chars.Count)

        member _.CursorIndex = lock gate (fun () -> cursorIndex)

        /// Snapshot of the current buffer contents. Doesn't
        /// mutate the buffer.
        member _.Snapshot () : string =
            lock gate (fun () -> System.String(chars.ToArray()))

        /// Insert `c` at the cursor, advance cursor by one.
        member _.AppendChar (c : char) : unit =
            lock gate (fun () ->
                if cursorIndex >= chars.Count then
                    chars.Add(c)
                else
                    chars.Insert(cursorIndex, c)
                cursorIndex <- cursorIndex + 1)

        /// Remove the char before the cursor; decrement cursor.
        /// No-op at start of buffer.
        member _.Backspace () : unit =
            lock gate (fun () ->
                if cursorIndex > 0 then
                    chars.RemoveAt(cursorIndex - 1)
                    cursorIndex <- cursorIndex - 1)

        /// Remove the char at the cursor (forward delete). No-op
        /// at end of buffer.
        member _.Delete () : unit =
            lock gate (fun () ->
                if cursorIndex < chars.Count then
                    chars.RemoveAt(cursorIndex))

        /// Move the cursor by `delta` (positive = right). Clamps
        /// to `[0, Count]`.
        member _.MoveCursor (delta : int) : unit =
            lock gate (fun () ->
                cursorIndex <- max 0 (min chars.Count (cursorIndex + delta)))

        /// Jump cursor to a specific index. Clamps to `[0, Count]`.
        member _.JumpTo (index : int) : unit =
            lock gate (fun () ->
                cursorIndex <- max 0 (min chars.Count index))

        /// Capture-and-clear: return the current buffer text
        /// and reset the buffer. Called from the `writePtyBytes`
        /// wrapper when it sees `0x0D` (Enter / submit).
        member _.Capture () : string =
            lock gate (fun () ->
                let text = System.String(chars.ToArray())
                chars.Clear()
                cursorIndex <- 0
                text)

        /// Reset to empty state. Called on shell hot-switch.
        member _.Reset () : unit =
            lock gate (fun () ->
                chars.Clear()
                cursorIndex <- 0)

    /// External signal types. Each transition is named after
    /// the event that drove it, not the state it goes to.
    type Transition =
        /// User pressed Enter (or pty-speak wrote `\r` to PTY
        /// in a scripted flow). Drives transition [a]:
        /// Composing → Executing.
        | EnterPressed of submittedCommand : string
        /// `HeuristicPromptDetector` emitted a `PromptStart`
        /// boundary. Drives transition [b]: Executing →
        /// Composing.
        | PromptDetected of promptText : string
        /// Sub-prompt detector fired (idle ≥ threshold AND
        /// last byte not `\n` AND output produced). Drives
        /// transition [c]: Executing → Composing with
        /// `SinglekeySubmit` flagged if the sub-prompt text
        /// matches a single-key pattern.
        | SubPromptIdle of subPromptText : string
        /// R3e (2026-05-16) — the user answered a SINGLE-KEY
        /// sub-prompt (e.g. cmd `choice /c YN`): a keystroke
        /// with no `\r`, so `EnterPressed` never fires. Drives
        /// `Composing(SinglekeySubmit=true) → Executing` so cmd's
        /// resumed output is tagged `CmdOutput` (not
        /// `UserInputEcho`) and a SUBSEQUENT sub-prompt in the
        /// same command can be re-detected. No-op for a
        /// non-single-key `Composing` (normal command typing) or
        /// `Executing`.
        | SingleKeySubmitted
        /// Alt-screen modulation (vim, less, full-TUI). Suspends
        /// the state machine; entry/exit toggle the suspension.
        /// PR-B logs but does not suspend (modulation lands
        /// later in the cycle).
        | AltScreenEntered
        | AltScreenExited

    /// Outcome of a transition attempt. `Some newState` if the
    /// transition is valid for the current state; `None` if it
    /// is not (e.g., `EnterPressed` while already Executing —
    /// pty-speak wrote `\r` mid-command, common in scripted
    /// flows). The caller logs `None` outcomes at Debug for
    /// audit.
    type TransitionOutcome =
        { NewState : InteractionState
          Trigger : Transition
          PriorState : InteractionState
          /// Cycle 48 PR-E — bytes accumulated during the
          /// just-finished Executing window. Non-empty only on
          /// Executing → Composing transitions; empty
          /// otherwise. Sanitised for announce by
          /// `AnnounceSanitiser.sanitise` at the call site, not
          /// here (the substrate accumulates raw bytes; the
          /// channel decides how to read them).
          AccumulatedOutput : string
          At : DateTime }

    /// State container. Holds the current state plus the
    /// detection thresholds. The composition root mutates it
    /// from two threads: `applyTransition` from the dispatcher
    /// (Enter / PromptStart / sub-prompt-detector tick) and
    /// `observeByte` from the reader thread. The `Gate` below
    /// serialises all mutation + read operations so the
    /// sub-prompt detector never observes partial state.
    type State() =
        let gate : obj = obj ()
        let mutable current : InteractionState =
            Composing
                { EnteredAt = DateTime.MinValue
                  PromptText = ValueNone
                  SinglekeySubmit = false }
        let mutable idleThresholdMs : int = 350
        let mutable lastTransitionAt : DateTime = DateTime.MinValue
        let mutable lastByteAt : DateTime = DateTime.MinValue
        let mutable lastByteWasLf : bool = false
        let mutable hadAnyBytesThisExecuting : bool = false
        // Cycle 48 PR-D — the UserInputBuffer that captures
        // what the user is composing. Self-locked. Lives at
        // State scope (not inside ComposingData) so the same
        // instance persists across Composing → Executing →
        // Composing transitions; transitions don't recreate
        // it. The buffer is cleared on shell hot-switch via
        // State.Reset.
        let userInputBuffer = UserInputBuffer()
        let mutable subPromptAccumulator : System.Text.StringBuilder =
            System.Text.StringBuilder()

        member internal _.Gate = gate

        member _.Current
            with get () = lock gate (fun () -> current)
            and set v = lock gate (fun () -> current <- v)

        member _.IdleThresholdMs
            with get () = lock gate (fun () -> idleThresholdMs)
            and set v = lock gate (fun () -> idleThresholdMs <- v)

        member _.LastTransitionAt
            with get () = lock gate (fun () -> lastTransitionAt)
            and set v = lock gate (fun () -> lastTransitionAt <- v)

        member _.LastByteAt
            with get () = lock gate (fun () -> lastByteAt)
            and set v = lock gate (fun () -> lastByteAt <- v)

        member _.LastByteWasLf
            with get () = lock gate (fun () -> lastByteWasLf)
            and set v = lock gate (fun () -> lastByteWasLf <- v)

        member _.HadAnyBytesThisExecuting
            with get () = lock gate (fun () -> hadAnyBytesThisExecuting)
            and set v = lock gate (fun () -> hadAnyBytesThisExecuting <- v)

        member _.SubPromptAccumulator = subPromptAccumulator

        /// Cycle 48 PR-D — the in-progress command line being
        /// composed. Self-locked. See `UserInputBuffer`'s
        /// docstring for the byte-stream-driven update model
        /// the `writePtyBytes` wrapper uses.
        member _.UserInputBuffer = userInputBuffer

        /// Reset on shell hot-switch. The new shell's state
        /// starts fresh — `Composing` with no prompt text and
        /// no carried-over byte timing.
        member this.Reset () =
            lock gate (fun () ->
                current <-
                    Composing
                        { EnteredAt = DateTime.UtcNow
                          PromptText = ValueNone
                          SinglekeySubmit = false }
                lastTransitionAt <- DateTime.UtcNow
                lastByteAt <- DateTime.MinValue
                lastByteWasLf <- false
                hadAnyBytesThisExecuting <- false
                subPromptAccumulator.Clear() |> ignore
                userInputBuffer.Reset())

    /// Per §9.4 (resolved 2026-05-13) — auto-detect single-key
    /// submit prompts. Two cases for the MVP:
    ///
    /// - `choice /c <opts>`: cmd renders a `[Y,N,A,E]?`-style
    ///   suffix. Regex matches `\[\w+(,\w+)*\]\?\s*$`.
    /// - `pause`: cmd renders `Press any key to continue . . .`
    ///   (with trailing space and ellipsis dots; no LF).
    ///   Substring match handles locale variants of "Press any
    ///   key" later if we add them.
    ///
    /// New patterns get added here as we encounter them. A
    /// future TOML config table
    /// `[shell_policy.sub_prompt_patterns]` lets users append
    /// without a code change (deferred).
    let private singleKeyChoiceRegex =
        Regex(@"\[\w+(,\w+)*\]\?\s*$", RegexOptions.Compiled)

    let isSingleKeySubmit (subPromptText : string) : bool =
        if String.IsNullOrEmpty subPromptText then false
        else
            singleKeyChoiceRegex.IsMatch subPromptText
            || subPromptText.Contains("Press any key to continue")

    /// Pure transition function. Given the current state and a
    /// trigger, return the resulting state if the transition is
    /// valid, or `None` if the trigger is a no-op for this
    /// state.
    ///
    /// **Validity rules**:
    ///
    /// - `EnterPressed` from `Composing` → `Executing`. From
    ///   `Executing` already, ignored (pty-speak wrote `\r`
    ///   while a command was running; subordinate scripted
    ///   `\r` writes shouldn't restart Executing).
    /// - `PromptDetected` from `Executing` → `Composing`. From
    ///   `Composing`, refresh the prompt text in place
    ///   (heuristic re-fired on the same shell prompt; common
    ///   on screen redraws).
    /// - `SubPromptIdle` from `Executing` → `Composing` with
    ///   `SinglekeySubmit` set per pattern match. From
    ///   `Composing`, ignored.
    /// - `AltScreenEntered` / `AltScreenExited`: PR-B logs but
    ///   does not transition.
    let tryTransition
            (priorState : InteractionState)
            (trigger : Transition)
            (at : DateTime)
            : InteractionState option =
        match priorState, trigger with
        // [a] Composing → Executing
        | Composing _, EnterPressed cmd ->
            Some
                (Executing
                    { EnteredAt = at
                      SubmittedCommand = cmd
                      OutputLastByteIsLf = false
                      OutputLastByteAt = at })

        // EnterPressed while Executing: pty-speak wrote `\r`
        // mid-command (e.g., scripted answer to set/p). Don't
        // reset the Executing state; cmd is still running.
        | Executing _, EnterPressed _ ->
            None

        // [b] Executing → Composing via heuristic
        | Executing _, PromptDetected promptText ->
            Some
                (Composing
                    { EnteredAt = at
                      PromptText = ValueSome promptText
                      SinglekeySubmit = false })

        // PromptDetected while Composing: heuristic re-fired
        // (screen redraw, banner re-render). Refresh the prompt
        // text in place but don't bump EnteredAt.
        | Composing data, PromptDetected promptText ->
            Some
                (Composing
                    { data with PromptText = ValueSome promptText })

        // [c] Executing → Composing via sub-prompt
        | Executing _, SubPromptIdle subPromptText ->
            Some
                (Composing
                    { EnteredAt = at
                      PromptText = ValueSome subPromptText
                      SinglekeySubmit = isSingleKeySubmit subPromptText })

        // SubPromptIdle while Composing: shouldn't fire (the
        // detector should be gated on Executing state by the
        // caller). Defensive no-op.
        | Composing _, SubPromptIdle _ ->
            None

        // [d] R3e — single-key sub-prompt answered. Only a
        // `Composing` whose prompt matched the single-key
        // pattern resumes Executing; this is what lets cmd's
        // post-answer output be tagged `CmdOutput` and a 2nd
        // sub-prompt in the same command re-detect (test-09).
        | Composing data, SingleKeySubmitted when data.SinglekeySubmit ->
            Some
                (Executing
                    { EnteredAt = at
                      SubmittedCommand = ""
                      OutputLastByteIsLf = false
                      OutputLastByteAt = at })

        // Normal command typing (`Composing`, not single-key)
        // or already `Executing`: a keystroke is not a
        // single-key sub-prompt answer. No-op.
        | Composing _, SingleKeySubmitted ->
            None
        | Executing _, SingleKeySubmitted ->
            None

        // Alt-screen modulation: PR-B no-op.
        | _, AltScreenEntered
        | _, AltScreenExited ->
            None

    /// Apply a transition to the state container. Returns the
    /// outcome so callers can log it. Returns `None` if the
    /// trigger was a no-op for the current state. Locks the
    /// state's gate so reader-thread `observeByte` calls don't
    /// interleave with the transition.
    let applyTransition
            (state : State)
            (trigger : Transition)
            (at : DateTime)
            : TransitionOutcome option =
        lock state.Gate (fun () ->
            let prior = state.Current
            match tryTransition prior trigger at with
            | None -> None
            | Some newState ->
                // Cycle 48 PR-E — capture the Executing window's
                // accumulated bytes BEFORE the state transition.
                // The accumulator is the "output the user should
                // hear" for transition [b] or [c]; we capture +
                // clear here so the caller gets the bytes and a
                // subsequent Executing entry starts fresh.
                let priorOutput =
                    match prior, newState with
                    | Executing _, Composing _ ->
                        let s = state.SubPromptAccumulator.ToString()
                        state.SubPromptAccumulator.Clear() |> ignore
                        s
                    | _ -> ""
                state.Current <- newState
                state.LastTransitionAt <- at
                match newState with
                | Executing _ ->
                    state.HadAnyBytesThisExecuting <- false
                    state.LastByteWasLf <- false
                    state.SubPromptAccumulator.Clear() |> ignore
                | _ -> ()
                Some
                    { NewState = newState
                      Trigger = trigger
                      PriorState = prior
                      AccumulatedOutput = priorOutput
                      At = at })

    /// Observe a single byte arriving from the PTY. Updates
    /// `LastByteAt`, `LastByteWasLf`, and (when in Executing)
    /// the sub-prompt accumulator. PR-B doesn't drive any
    /// transition from this; it only updates the state used by
    /// the sub-prompt detector below. Locks the state's gate.
    let observeByte
            (state : State)
            (at : DateTime)
            (b : byte)
            : unit =
        lock state.Gate (fun () ->
            state.LastByteAt <- at
            state.LastByteWasLf <- (b = 0x0Auy)
            match state.Current with
            | Executing _ ->
                state.HadAnyBytesThisExecuting <- true
                if state.SubPromptAccumulator.Length >= 4096 then
                    state.SubPromptAccumulator.Remove(0, 2048)
                    |> ignore
                state.SubPromptAccumulator.Append(char b) |> ignore
            | Composing _ ->
                ())

    /// Sub-prompt detector. Returns `Some SubPromptIdle ...`
    /// if the conditions for transition [c] are met:
    /// - state is `Executing`
    /// - at least one byte has been received this Executing
    ///   window
    /// - the last byte is NOT `\n`
    /// - elapsed since last byte ≥ `IdleThresholdMs`
    ///
    /// Returns `None` otherwise. Caller (typically the
    /// idle-flush dispatcher tick) feeds the outcome to
    /// `applyTransition`. Locks the state's gate.
    let trySubPromptDetect
            (state : State)
            (now : DateTime)
            : Transition option =
        lock state.Gate (fun () ->
            match state.Current with
            | Executing _ when
                state.HadAnyBytesThisExecuting
                && not state.LastByteWasLf
                && state.LastByteAt <> DateTime.MinValue
                && (now - state.LastByteAt).TotalMilliseconds >= float state.IdleThresholdMs ->
                let raw = state.SubPromptAccumulator.ToString()
                let text = raw.TrimEnd()
                Some (SubPromptIdle text)
            | _ ->
                None)

    /// Convenience for callers: format an InteractionState for
    /// log lines. Single-line, ASCII-only, bounded length.
    let describeState (s : InteractionState) : string =
        match s with
        | Composing d ->
            let promptStr =
                match d.PromptText with
                | ValueSome p when p.Length > 40 ->
                    p.Substring(0, 40) + "..."
                | ValueSome p -> p
                | ValueNone -> "<none>"
            sprintf
                "Composing(prompt=%s, single-key=%b)"
                promptStr
                d.SinglekeySubmit
        | Executing d ->
            let cmdStr =
                if d.SubmittedCommand.Length > 40 then
                    d.SubmittedCommand.Substring(0, 40) + "..."
                else
                    d.SubmittedCommand
            sprintf "Executing(cmd=%s)" cmdStr

    let describeTrigger (t : Transition) : string =
        match t with
        | EnterPressed cmd ->
            let s =
                if cmd.Length > 40 then cmd.Substring(0, 40) + "..."
                else cmd
            sprintf "EnterPressed(cmd=%s)" s
        | PromptDetected p ->
            let s =
                if p.Length > 40 then p.Substring(0, 40) + "..."
                else p
            sprintf "PromptDetected(%s)" s
        | SubPromptIdle p ->
            let s =
                if p.Length > 40 then p.Substring(0, 40) + "..."
                else p
            sprintf "SubPromptIdle(%s)" s
        | SingleKeySubmitted -> "SingleKeySubmitted"
        | AltScreenEntered -> "AltScreenEntered"
        | AltScreenExited -> "AltScreenExited"
