namespace Terminal.Pty.Native

open System
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

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
