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

/// SafeHandle wrapper for HPCON. ClosePseudoConsole runs on Dispose so
/// callers don't have to remember to close it. Treats 0 and -1 as
/// invalid (matches conpty's convention even though 0 is the more
/// common failure value).
type SafePseudoConsoleHandle() =
    inherit SafeHandleZeroOrMinusOneIsInvalid(true)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    static extern void ClosePseudoConsole(nativeint hPC)

    override this.ReleaseHandle() =
        ClosePseudoConsole(this.handle)
        true

/// All the P/Invoke functions we need. Kept in a single module so the
/// callable surface is auditable in one place. Every signature mirrors
/// what MiniTerm uses; deviations from the canonical signature are a
/// bug.
module Win32 =
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint32 dwFlags,
        SafePseudoConsoleHandle& phPC)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int ResizePseudoConsole(SafePseudoConsoleHandle hPC, COORD size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CreatePipe(
        SafeFileHandle& hReadPipe,
        SafeFileHandle& hWritePipe,
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
