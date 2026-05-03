namespace Terminal.Pty.Native

open System
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

// Stage 7 PR-A — expose `internal` members of `EnvBlock` (the
// allow-list / deny-list assembly + UTF-16 marshalling) to the xUnit
// assembly so `EnvBlockTests` can pin the assembly rules and the
// byte-level marshalling layout independently. Mirrors the precedent
// in `src/Terminal.Core/Types.fs:10` and
// `src/Terminal.Accessibility/TerminalAutomationPeer.fs:22-23`.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Tests.Unit")>]
do ()

// ConPTY P/Invoke surface mirroring Microsoft's MiniTerm sample
// (microsoft/terminal/samples/ConPTY/MiniTerm/MiniTerm/Native/PseudoConsoleApi.cs).
//
// Stage 1 scope only: just enough surface to spawn cmd.exe under a
// pseudoconsole, drive stdin, drain stdout, and shut down cleanly.
// No Job Object yet (deferred to a later stage); no DCS passthrough,
// mouse, or clipboard bridging.
//
// All structs use LayoutKind.Sequential with mutable fields in the exact
// order the Win32 headers declare them. Pipe handles use SafeFileHandle so
// the GC closes them deterministically; HPCON is wrapped in
// SafePseudoConsoleHandle so ClosePseudoConsole runs on dispose.

[<Struct; StructLayout(LayoutKind.Sequential)>]
type COORD =
    { mutable X: int16
      mutable Y: int16 }

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type STARTUPINFO =
    { mutable cb: int32
      mutable lpReserved: string | null
      mutable lpDesktop: string | null
      mutable lpTitle: string | null
      mutable dwX: uint32
      mutable dwY: uint32
      mutable dwXSize: uint32
      mutable dwYSize: uint32
      mutable dwXCountChars: uint32
      mutable dwYCountChars: uint32
      mutable dwFillAttribute: uint32
      mutable dwFlags: uint32
      mutable wShowWindow: uint16
      mutable cbReserved2: uint16
      mutable lpReserved2: nativeint
      mutable hStdInput: nativeint
      mutable hStdOutput: nativeint
      mutable hStdError: nativeint }

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type STARTUPINFOEX =
    { mutable StartupInfo: STARTUPINFO
      mutable lpAttributeList: nativeint }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type PROCESS_INFORMATION =
    { mutable hProcess: nativeint
      mutable hThread: nativeint
      mutable dwProcessId: uint32
      mutable dwThreadId: uint32 }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type SECURITY_ATTRIBUTES =
    { mutable nLength: int32
      mutable lpSecurityDescriptor: nativeint
      mutable bInheritHandle: int32 }

// Stage 6 PR-B — Job Object support so closing pty-speak guarantees
// the entire child-process tree (cmd.exe + anything cmd.exe spawned)
// is killed by the kernel, even if pty-speak itself crashes hard
// without running its IDisposable. Layered on top of the existing
// best-effort `TerminateProcess` cleanup in `ConPtyHost.Dispose`:
// `TerminateProcess` is fast targeted cleanup for the immediate
// cmd.exe so its pipe drains promptly; the Job Object is the
// kernel-enforced safety net for any grandchildren (e.g. a `node`
// process Stage 7's Claude Code spawns inside pty-speak).
//
// The structs below mirror the Win32 headers exactly. All sizes are
// `Marshal.SizeOf` checked; deviating from the documented field
// order or alignment produces silent garbage when the kernel
// interprets the buffer.

[<Struct; StructLayout(LayoutKind.Sequential)>]
type JOBOBJECT_BASIC_LIMIT_INFORMATION =
    { mutable PerProcessUserTimeLimit: int64
      mutable PerJobUserTimeLimit: int64
      mutable LimitFlags: uint32
      mutable MinimumWorkingSetSize: unativeint
      mutable MaximumWorkingSetSize: unativeint
      mutable ActiveProcessLimit: uint32
      mutable Affinity: unativeint
      mutable PriorityClass: uint32
      mutable SchedulingClass: uint32 }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type IO_COUNTERS =
    { mutable ReadOperationCount: uint64
      mutable WriteOperationCount: uint64
      mutable OtherOperationCount: uint64
      mutable ReadTransferCount: uint64
      mutable WriteTransferCount: uint64
      mutable OtherTransferCount: uint64 }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type JOBOBJECT_EXTENDED_LIMIT_INFORMATION =
    { mutable BasicLimitInformation: JOBOBJECT_BASIC_LIMIT_INFORMATION
      mutable IoInfo: IO_COUNTERS
      mutable ProcessMemoryLimit: unativeint
      mutable JobMemoryLimit: unativeint
      mutable PeakProcessMemoryUsed: unativeint
      mutable PeakJobMemoryUsed: unativeint }

/// Win32 constants we need for ConPTY. Suffixed for the right type:
/// `un` = unativeint (matches `IntPtr Attribute` field on
/// UpdateProcThreadAttribute), `u` = uint32, no suffix = signed int32.
module Constants =
    /// Marks an HPCON in a process/thread attribute list. Win32 header value
    /// is `ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)` which expands
    /// to `0x00020016`. See processthreadsapi.h.
    let PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: unativeint = 0x00020016un

    /// dwCreationFlags bit that tells CreateProcess the lpStartupInfo
    /// pointer is actually a STARTUPINFOEX. Required when using
    /// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
    let EXTENDED_STARTUPINFO_PRESENT: uint32 = 0x00080000u

    /// dwFlags bit on STARTUPINFO indicating hStdInput/Output/Error are
    /// honoured. Not needed for ConPTY (the attribute list supplies
    /// equivalent state) but kept here for completeness.
    let STARTF_USESTDHANDLES: uint32 = 0x00000100u

    /// CreateProcess return-code on misconfigured STARTUPINFOEX.
    let ERROR_INVALID_PARAMETER: int = 0x57

    /// Pipe drain sentinel — ReadFile returns this when the write end has
    /// been closed and nothing remains to read.
    let ERROR_BROKEN_PIPE: int = 0x6D

    /// `JobObjectInfoClass` value that selects
    /// `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` for
    /// `SetInformationJobObject`. From `winnt.h`.
    let JobObjectExtendedLimitInformation: int = 9

    /// `LimitFlags` bit on `JOBOBJECT_BASIC_LIMIT_INFORMATION` that
    /// causes the kernel to terminate every process associated with
    /// the job when the last handle to the job is closed. This is
    /// the single bit that makes child-tree cleanup work even when
    /// the parent crashes without running its IDisposable.
    let JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: uint32 = 0x00002000u

    /// dwCreationFlags bit telling CreateProcess that the
    /// `lpEnvironment` block we pass is UTF-16 (not the legacy ANSI
    /// CP_ACP encoding the kernel defaults to). Mandatory for the
    /// Stage 7 env-scrub block (PO-5): we marshal env entries through
    /// `Encoding.Unicode` so the Win32 ANSI fallback would mojibake
    /// every value with a non-ASCII byte. From `processthreadsapi.h`.
    let CREATE_UNICODE_ENVIRONMENT: uint32 = 0x00000400u

/// SafeHandle wrapper for HPCON. ClosePseudoConsole runs on Dispose so
/// callers don't have to remember to close it. Treats 0 and -1 as
/// invalid (matches conpty's convention even though 0 is the more
/// common failure value).
///
/// We wrap manually (via SetHandle in the secondary constructor) instead
/// of relying on `SafePseudoConsoleHandle&` byref marshalling, which is
/// a known sharp edge for F# `out`-parameter interop and was producing
/// runtime failures in earlier iterations of this file.
type SafePseudoConsoleHandle() =
    inherit SafeHandleZeroOrMinusOneIsInvalid(true)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    static extern void ClosePseudoConsole(nativeint hPC)

    /// Construct from a kernel-returned HPCON. The handle takes
    /// ownership and will run ClosePseudoConsole on disposal.
    new(preexistingHandle: nativeint) as this =
        new SafePseudoConsoleHandle()
        then this.SetHandle(preexistingHandle)

    override this.ReleaseHandle() =
        ClosePseudoConsole(this.handle)
        true

/// SafeHandle for a Job Object handle. Closing the handle when
/// `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` is set on the job triggers
/// kernel-level termination of every process in the job — that's
/// the entire mechanism for grandchild cleanup. Wraps the same
/// `IntPtr-then-SetHandle` idiom as `SafePseudoConsoleHandle` to
/// avoid F#'s sharp-edged `out SafeHandle` byref marshalling.
type SafeJobHandle() =
    inherit SafeHandleZeroOrMinusOneIsInvalid(true)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    static extern bool CloseHandle(nativeint hObject)

    /// Construct from a kernel-returned job handle. The handle takes
    /// ownership and CloseHandle runs on disposal — which, for a
    /// KILL_ON_JOB_CLOSE job, kills every process still in the job.
    new(preexistingHandle: nativeint) as this =
        new SafeJobHandle()
        then this.SetHandle(preexistingHandle)

    override this.ReleaseHandle() =
        CloseHandle(this.handle) |> ignore
        true

/// All the P/Invoke functions we need. Kept in a single module so the
/// callable surface is auditable in one place. Every signature mirrors
/// what MiniTerm uses; deviations from the canonical signature are a
/// bug.
///
/// Parameters that C# binds as `out SafeFileHandle` / `out HPCON` are
/// declared here as `nativeint&` and wrapped manually by callers.
/// F#'s built-in marshalling of byref-SafeHandle arguments has been
/// reported to produce silent NullReferenceException at runtime;
/// the IntPtr-then-wrap pattern is more boring and more reliable.
module Win32 =
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint32 dwFlags,
        nativeint& phPC)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int ResizePseudoConsole(nativeint hPC, COORD size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CreatePipe(
        nativeint& hReadPipe,
        nativeint& hWritePipe,
        nativeint lpPipeAttributes,
        uint32 nSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool InitializeProcThreadAttributeList(
        nativeint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        nativeint& lpSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool UpdateProcThreadAttribute(
        nativeint lpAttributeList,
        uint32 dwFlags,
        unativeint Attribute,
        nativeint lpValue,
        nativeint cbSize,
        nativeint lpPreviousValue,
        nativeint lpReturnSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool DeleteProcThreadAttributeList(nativeint lpAttributeList)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateProcess(
        string | null lpApplicationName,
        string | null lpCommandLine,
        nativeint lpProcessAttributes,
        nativeint lpThreadAttributes,
        bool bInheritHandles,
        uint32 dwCreationFlags,
        nativeint lpEnvironment,
        string | null lpCurrentDirectory,
        STARTUPINFOEX& lpStartupInfo,
        PROCESS_INFORMATION& lpProcessInformation)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint hObject)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 ResumeThread(nativeint hThread)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool TerminateProcess(nativeint hProcess, uint32 uExitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 WaitForSingleObject(nativeint hHandle, uint32 dwMilliseconds)

    // Stage 6 PR-B — Job Object P/Invokes for child-process tree cleanup.
    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern nativeint CreateJobObjectW(
        nativeint lpJobAttributes,
        string | null lpName)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool SetInformationJobObject(
        nativeint hJob,
        int JobObjectInfoClass,
        nativeint lpJobObjectInfo,
        uint32 cbJobObjectInfoLength)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool AssignProcessToJobObject(
        nativeint hJob,
        nativeint hProcess)

/// Stage 7 PR-A — Win32 child-process environment block construction
/// for the env-scrub PO-5 mitigation (`SECURITY.md` row PO-5). Without
/// this, `CreateProcess` is invoked with `lpEnvironment=IntPtr.Zero`
/// and the child inherits the parent's full environment block —
/// including any `*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`
/// surfaced into the parent (CI runners, devcontainer images, dotfiles,
/// the maintainer's shell rc, etc.). The pty-speak threat model
/// explicitly does not want those reaching whichever shell the user
/// launches inside the terminal.
///
/// Strategy: **allow-list with deny-list override.** Allow-list keeps
/// the env-vars Claude Code (and other modern shells) actually need to
/// function (per `spec/tech-plan.md` §7.2). Deny-list overrides the
/// allow-list for the specific-pattern variables we know are
/// security-sensitive. Always-set pairs (`TERM`, `COLORTERM`) are
/// layered on top so the child sees the terminal capability matrix
/// pty-speak actually emulates.
///
/// Marshalling: UTF-16LE, `NAME=VALUE\0` per entry, **sorted by
/// uppercase name** (Win32 convention; some kernel paths reject
/// out-of-order blocks), terminating extra `\0` after the last entry.
/// Caller is responsible for `Marshal.FreeHGlobal` on the returned
/// `Block` after `CreateProcess` returns — the kernel copies the
/// bytes during the call.
///
/// **Logging discipline:** the public `build` family returns the
/// stripped count as an `int` for `Information`-level logging.
/// Names and values are NEVER returned or logged (env-var names like
/// `BANK_API_KEY` are themselves sensitive); see `SECURITY.md`'s
/// logging contract.
module EnvBlock =

    open System
    open System.Collections
    open System.Text

    /// Allow-list of parent env-var names to preserve in the child
    /// block. Per `spec/tech-plan.md` §7.2. Lookup is
    /// case-insensitive (Windows env-var names are case-insensitive
    /// by convention).
    ///
    /// `HOME` is in the allow-list and additionally falls back to
    /// `%USERPROFILE%` when absent (npm/git compatibility on
    /// Windows; spec §7.2).
    ///
    /// `ANTHROPIC_API_KEY` is in the allow-list AND has an explicit
    /// deny-list exemption — Claude Code is the primary target
    /// workload and stripping its credential would defeat Stage 7's
    /// purpose. A future "guest mode" Phase-2 setting could deny it
    /// for sandboxed sessions.
    ///
    /// `CLAUDE_CODE_GIT_BASH_PATH` is the Windows-specific knob
    /// surfaced by Claude Code's terminal-config docs.
    let internal allowedNames : Set<string> =
        Set.ofList [
            "PATH"
            "USERPROFILE"
            "APPDATA"
            "LOCALAPPDATA"
            "HOME"
            "ANTHROPIC_API_KEY"
            "CLAUDE_CODE_GIT_BASH_PATH"
        ]

    /// Always-set name/value pairs that override anything the parent
    /// might supply for these names. Per `spec/tech-plan.md` §7.2.
    let internal alwaysSet : (string * string) list =
        [ "TERM", "xterm-256color"
          "COLORTERM", "truecolor" ]

    /// Returns true when an env-var name matches a deny-list pattern.
    /// **Suffix match on uppercase** so `KEYBOARD_LAYOUT` does NOT
    /// match `*_KEY` (no leading underscore before `KEY`). Single
    /// explicit exemption: `ANTHROPIC_API_KEY` matches `*_KEY` but
    /// is allowed because Claude Code's auth token is the primary
    /// workload's credential.
    ///
    /// Patterns (each requires the leading underscore):
    ///   `*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`.
    let internal isDenied (name: string) : bool =
        let n = name.ToUpperInvariant()
        if n = "ANTHROPIC_API_KEY" then false
        else
            n.EndsWith("_TOKEN", StringComparison.Ordinal)
            || n.EndsWith("_SECRET", StringComparison.Ordinal)
            || n.EndsWith("_KEY", StringComparison.Ordinal)
            || n.EndsWith("_PASSWORD", StringComparison.Ordinal)

    /// Outcome of the assembly stage. Pure, marshalling-free; the
    /// next stage takes this and produces the actual HGlobal bytes.
    /// Exposed as `internal` so unit tests can pin the assembly
    /// rules without going through the marshalling round-trip.
    type internal Assembled =
        { /// Sorted-by-uppercase-name `NAME=VALUE` entries that will
          /// land in the child block. Includes the always-set pairs.
          Entries: (string * string) list
          /// Count of parent variables dropped by the deny-list.
          /// Information-only; for log emission.
          StrippedCount: int }

    /// Pure assembly: takes a parent environment as a name→value map
    /// and produces the (sorted, deduped) entry list plus the
    /// stripped count. Lookup of `allowedNames` membership is
    /// case-insensitive via uppercased keys.
    let internal assemble (parent: Map<string, string>) : Assembled =
        // Track stripped count from the parent set explicitly so the
        // count reflects "what was stripped from the parent" rather
        // than "what is missing from the final block".
        let mutable stripped = 0
        let allowedUpper =
            allowedNames |> Set.map (fun s -> s.ToUpperInvariant())
        // 1. Filter parent by allow-list AND deny-list. Deny-list
        //    wins over allow-list (spec §7.2 + handoff sketch).
        let kept =
            parent
            |> Map.toList
            |> List.choose (fun (k, v) ->
                let upper = k.ToUpperInvariant()
                if isDenied k then
                    stripped <- stripped + 1
                    None
                elif Set.contains upper allowedUpper then
                    Some (upper, v)
                else
                    // Outside the allow-list: drop, but don't count
                    // toward "stripped" since these aren't sensitive.
                    None)
            |> Map.ofList
        // 2. HOME=%USERPROFILE% fallback per spec §7.2 — applies only
        //    when HOME is not set and USERPROFILE is.
        let kept =
            if Map.containsKey "HOME" kept then kept
            else
                match Map.tryFind "USERPROFILE" kept with
                | Some up -> Map.add "HOME" up kept
                | None -> kept
        // 3. Layer always-set on top — overrides anything the parent
        //    supplied for those names.
        let final =
            alwaysSet
            |> List.fold (fun acc (k, v) -> Map.add (k.ToUpperInvariant()) v acc) kept
        // 4. Sort by uppercase name (Win32 convention).
        let entries =
            final
            |> Map.toList
            |> List.sortBy fst
        { Entries = entries; StrippedCount = stripped }

    /// Outcome of building a complete env block. The `Block` field is
    /// HGlobal-allocated UTF-16 bytes ready for `lpEnvironment`; the
    /// caller owns it and MUST `Marshal.FreeHGlobal` after
    /// `CreateProcess` returns (success or failure).
    type Built =
        { /// HGlobal pointer to UTF-16LE bytes. Caller frees.
          Block: nativeint
          /// Total byte length of the block including the trailing
          /// double-NUL terminator. Suitable for `Marshal.Copy`-style
          /// round-trip verification in tests.
          ByteLength: int
          /// Number of parent env-vars stripped by the deny-list.
          /// Information-only; for `Information`-level logging.
          /// Names and values are NEVER captured.
          StrippedCount: int }

    /// Marshal the assembled entries into an HGlobal UTF-16LE block.
    /// Format: each entry is `NAME=VALUE\u0000` in UTF-16LE; final
    /// extra `\u0000` after the last entry. Empty input still
    /// produces a 2-byte block holding just the terminating
    /// `\u0000`.
    ///
    /// Uses the explicit `\u0000` escape instead of a literal NUL
    /// byte in source so the file stays plain ASCII (matches the
    /// codebase convention for control-byte literals — see
    /// `tests/Tests.Unit/AnnounceSanitiserTests.fs` header note on
    /// the F# 9 BiDi / Trojan-Source warning under
    /// `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).
    let internal marshalBlock (entries: (string * string) list) : nativeint * int =
        // Build the full string in managed memory first, then
        // marshal to HGlobal. Cleaner than writing char-at-a-time
        // and easier to round-trip in tests.
        let sb = StringBuilder()
        for (name, value) in entries do
            sb.Append(name) |> ignore
            sb.Append('=') |> ignore
            sb.Append(value) |> ignore
            sb.Append('\u0000') |> ignore
        // Final NUL terminator after the last entry.
        sb.Append('\u0000') |> ignore
        let bytes = Encoding.Unicode.GetBytes(sb.ToString())
        let ptr = Marshal.AllocHGlobal(bytes.Length)
        Marshal.Copy(bytes, 0, ptr, bytes.Length)
        ptr, bytes.Length

    /// Test-friendly entry point: build a block from an explicit
    /// parent map. Lets unit fixtures pin behaviour without touching
    /// process-wide state.
    let buildFromMap (parent: Map<string, string>) : Built =
        let assembled = assemble parent
        let block, len = marshalBlock assembled.Entries
        { Block = block
          ByteLength = len
          StrippedCount = assembled.StrippedCount }

    /// Production entry point: build a block from the current process
    /// environment. The parent map is collected from
    /// `Environment.GetEnvironmentVariables()`.
    let build () : Built =
        let parent =
            Environment.GetEnvironmentVariables()
            |> Seq.cast<DictionaryEntry>
            |> Seq.choose (fun de ->
                match de.Key, de.Value with
                | (:? string as k), (:? string as v) -> Some (k, v)
                | _ -> None)
            |> Map.ofSeq
        buildFromMap parent
