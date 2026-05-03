namespace Terminal.Pty

open System
open System.ComponentModel
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open Terminal.Pty.Native

/// Errors that can fail PseudoConsole creation. Each variant carries the
/// relevant Win32 last-error code so callers can log or branch precisely.
type PtyCreateError =
    | CreatePipeFailed of stage: string * win32: int
    | CreatePseudoConsoleFailed of hresult: int
    | InitializeAttributeListFailed of win32: int
    | AllocAttributeListFailed
    | UpdateAttributeFailed of win32: int
    | CreateProcessFailed of win32: int
    /// Stage 6 PR-B — Job Object setup failure. The child process
    /// has already been started and terminated when this error
    /// returns; no orphan is left behind.
    | JobObjectSetupFailed of stage: string * win32: int

/// Configuration for a new pseudo-console session. cols/rows are the
/// initial grid size; the child can be resized later via
/// PseudoConsole.resize. commandLine is passed verbatim to CreateProcess
/// (lpCommandLine), so a value like "cmd.exe" works for the trivial
/// case.
type PtyConfig =
    { Cols: int16
      Rows: int16
      CommandLine: string }

/// A live pseudo-console session: the HPCON, the parent's ends of the
/// stdin/stdout pipes, and the child process. The attribute-list buffer
/// is also retained so it can be deleted/freed on disposal. Disposal
/// order follows Microsoft's guidance:
///   1. Drain the output pipe until it returns 0 / ERROR_BROKEN_PIPE
///   2. Close the pseudo-console (released here via the SafeHandle)
///   3. Close the parent's pipe handles
///   4. Free the attribute list
/// Owners of a PtySession are responsible for the drain step before
/// invoking Dispose; PtySession itself only handles steps 2-4.
type PtySession =
    { Console: SafePseudoConsoleHandle
      /// Parent's writable end of the input pipe; bytes written here
      /// reach the child's stdin.
      Stdin: SafeFileHandle
      /// Parent's readable end of the output pipe; bytes read here
      /// originate from the child's stdout/stderr.
      Stdout: SafeFileHandle
      ProcessHandle: nativeint
      ThreadHandle: nativeint
      ProcessId: uint32
      /// Heap-allocated attribute-list buffer. Must be passed to
      /// DeleteProcThreadAttributeList and then freed.
      AttributeList: nativeint
      /// Stage 6 PR-B — Job Object handle owning the child + any
      /// processes the child spawns. Closing this handle (which
      /// happens via SafeJobHandle.Dispose) triggers
      /// `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and the kernel
      /// terminates the entire process tree. Belt-and-braces on
      /// top of `ConPtyHost`'s targeted `TerminateProcess` of the
      /// immediate cmd.exe.
      JobHandle: SafeJobHandle
      /// Stage 7 PR-A — count of parent environment variables
      /// stripped by the env-scrub deny-list before this child was
      /// spawned. Information-only; surfaced for `Information`-level
      /// log emission at the composition root. Names and values are
      /// NEVER captured (per `SECURITY.md` logging discipline:
      /// env-var names like `BANK_API_KEY` are themselves
      /// sensitive). Closes `SECURITY.md` row PO-5.
      EnvScrubStrippedCount: int }

    interface IDisposable with
        member this.Dispose() =
            // SafePseudoConsoleHandle.ReleaseHandle calls ClosePseudoConsole.
            this.Console.Dispose()
            this.Stdin.Dispose()
            this.Stdout.Dispose()
            if this.AttributeList <> IntPtr.Zero then
                Win32.DeleteProcThreadAttributeList(this.AttributeList) |> ignore
                Marshal.FreeHGlobal(this.AttributeList)
            if this.ProcessHandle <> IntPtr.Zero then
                Win32.CloseHandle(this.ProcessHandle) |> ignore
            if this.ThreadHandle <> IntPtr.Zero then
                Win32.CloseHandle(this.ThreadHandle) |> ignore
            // Closing the job handle LAST gives the kernel a final
            // KILL_ON_JOB_CLOSE pass for any process that might
            // have escaped earlier cleanup steps. SafeHandle's
            // finaliser also runs this if Dispose is missed entirely
            // (e.g. on a hard parent crash).
            this.JobHandle.Dispose()

/// Functions for creating, resizing, and tearing down a pseudoconsole.
/// All public functions return Result so callers don't need a try/catch
/// around the P/Invoke layer.
module PseudoConsole =

    /// Create a pseudo-console + spawn the configured child. On success
    /// the returned PtySession owns all handles and must be disposed
    /// (after draining stdout) to release them.
    ///
    /// The lifecycle here is the strict 9-step order from
    /// docs.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session
    /// — deviations are bugs. The two most-broken steps in
    /// non-Microsoft samples are:
    ///   * Step 4: closing the inputReadSide / outputWriteSide handles
    ///     in the parent immediately after CreatePseudoConsole. ConPTY
    ///     keeps its own copies; if the parent doesn't drop these
    ///     references, the read pipe never sees EOF and the parent
    ///     hangs on shutdown.
    ///   * Step 8: setting STARTUPINFOEX.cb to sizeof<STARTUPINFOEX>,
    ///     not sizeof<STARTUPINFO>. Misset cb produces
    ///     ERROR_INVALID_PARAMETER (0x57) from CreateProcess.
    let create (cfg: PtyConfig) : Result<PtySession, PtyCreateError> =
        // 1+2. Two anonymous pipes — one for input, one for output.
        // Bind raw IntPtr handles first, then wrap into SafeFileHandle
        // manually. F#'s `out SafeFileHandle` byref marshalling has been
        // observed to silently produce NullReferenceException at runtime;
        // the IntPtr-then-wrap pattern avoids that.
        let mutable inputReadHandle = IntPtr.Zero
        let mutable inputWriteHandle = IntPtr.Zero
        let mutable outputReadHandle = IntPtr.Zero
        let mutable outputWriteHandle = IntPtr.Zero

        if not (Win32.CreatePipe(&inputReadHandle, &inputWriteHandle, IntPtr.Zero, 0u)) then
            Error(CreatePipeFailed("input", Marshal.GetLastWin32Error()))
        elif not (Win32.CreatePipe(&outputReadHandle, &outputWriteHandle, IntPtr.Zero, 0u)) then
            Win32.CloseHandle(inputReadHandle) |> ignore
            Win32.CloseHandle(inputWriteHandle) |> ignore
            Error(CreatePipeFailed("output", Marshal.GetLastWin32Error()))
        else
            // Wrap pipe ends into SafeFileHandles now that we have all
            // four. Each takes ownership; CloseHandle runs on disposal.
            let inputRead = new SafeFileHandle(inputReadHandle, true)
            let inputWrite = new SafeFileHandle(inputWriteHandle, true)
            let outputRead = new SafeFileHandle(outputReadHandle, true)
            let outputWrite = new SafeFileHandle(outputWriteHandle, true)

            // 3. CreatePseudoConsole — takes the read end of the input
            // pipe and the write end of the output pipe.
            let size = { X = cfg.Cols; Y = cfg.Rows }
            let mutable hPCRaw = IntPtr.Zero
            let createHr = Win32.CreatePseudoConsole(size, inputRead, outputWrite, 0u, &hPCRaw)

            if createHr <> 0 then
                inputRead.Dispose()
                inputWrite.Dispose()
                outputRead.Dispose()
                outputWrite.Dispose()
                Error(CreatePseudoConsoleFailed createHr)
            else
                let hPC = new SafePseudoConsoleHandle(hPCRaw)
                // 4. Drop the handles ConPTY now owns. The parent must
                // not retain references; otherwise the pipe never
                // signals EOF on shutdown.
                inputRead.Dispose()
                outputWrite.Dispose()

                // 5. Initialize the proc/thread attribute list. Two
                // calls: first to size the buffer, then to populate it.
                let mutable attrSize = IntPtr.Zero
                Win32.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, &attrSize) |> ignore
                // The first call always fails with
                // ERROR_INSUFFICIENT_BUFFER (122); we ignore that and
                // use the size it filled in.
                let attrList = Marshal.AllocHGlobal(attrSize)
                if attrList = IntPtr.Zero then
                    hPC.Dispose()
                    inputWrite.Dispose()
                    outputRead.Dispose()
                    Error AllocAttributeListFailed
                elif not (Win32.InitializeProcThreadAttributeList(attrList, 1, 0, &attrSize)) then
                    let err = Marshal.GetLastWin32Error()
                    Marshal.FreeHGlobal(attrList)
                    hPC.Dispose()
                    inputWrite.Dispose()
                    outputRead.Dispose()
                    Error(InitializeAttributeListFailed err)
                else
                    // 6. Attach the HPCON to the attribute list.
                    let updated =
                        Win32.UpdateProcThreadAttribute(
                            attrList,
                            0u,
                            Constants.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                            hPC.DangerousGetHandle(),
                            nativeint IntPtr.Size,
                            IntPtr.Zero,
                            IntPtr.Zero)
                    if not updated then
                        let err = Marshal.GetLastWin32Error()
                        Win32.DeleteProcThreadAttributeList(attrList) |> ignore
                        Marshal.FreeHGlobal(attrList)
                        hPC.Dispose()
                        inputWrite.Dispose()
                        outputRead.Dispose()
                        Error(UpdateAttributeFailed err)
                    else
                        // 7. Build STARTUPINFOEX with the correct cb.
                        let mutable si =
                            { StartupInfo =
                                { cb = Marshal.SizeOf<STARTUPINFOEX>()
                                  lpReserved = null
                                  lpDesktop = null
                                  lpTitle = null
                                  dwX = 0u
                                  dwY = 0u
                                  dwXSize = 0u
                                  dwYSize = 0u
                                  dwXCountChars = 0u
                                  dwYCountChars = 0u
                                  dwFillAttribute = 0u
                                  dwFlags = 0u
                                  wShowWindow = 0us
                                  cbReserved2 = 0us
                                  lpReserved2 = IntPtr.Zero
                                  hStdInput = IntPtr.Zero
                                  hStdOutput = IntPtr.Zero
                                  hStdError = IntPtr.Zero }
                              lpAttributeList = attrList }

                        let mutable pi = Unchecked.defaultof<PROCESS_INFORMATION>

                        // Stage 7 PR-A — build the env-scrub block
                        // immediately before `CreateProcess`. The
                        // kernel copies the bytes during the call so
                        // we free the block in either branch (success
                        // or failure) on the same scope. `lpEnvironment`
                        // is paired with `CREATE_UNICODE_ENVIRONMENT`
                        // because we marshal UTF-16LE; without the
                        // flag the kernel would reinterpret the bytes
                        // as ANSI (CP_ACP) and mojibake every
                        // non-ASCII value. Closes `SECURITY.md` row
                        // PO-5.
                        let envBuilt = EnvBlock.build ()

                        // 8. CreateProcess. bInheritHandles = false is
                        // correct: ConPTY duplicates the std handles
                        // through the attribute list, not by inheritance.
                        let started =
                            Win32.CreateProcess(
                                null,
                                cfg.CommandLine,
                                IntPtr.Zero,
                                IntPtr.Zero,
                                false,
                                Constants.EXTENDED_STARTUPINFO_PRESENT
                                    ||| Constants.CREATE_UNICODE_ENVIRONMENT,
                                envBuilt.Block,
                                null,
                                &si,
                                &pi)

                        // Free the env block once `CreateProcess`
                        // returns. The kernel has already copied the
                        // bytes into the new process by this point;
                        // holding the HGlobal alive any longer would
                        // be a leak. Free unconditionally so the
                        // failure-return paths below stay simple.
                        Marshal.FreeHGlobal(envBuilt.Block)

                        if not started then
                            let err = Marshal.GetLastWin32Error()
                            Win32.DeleteProcThreadAttributeList(attrList) |> ignore
                            Marshal.FreeHGlobal(attrList)
                            hPC.Dispose()
                            inputWrite.Dispose()
                            outputRead.Dispose()
                            Error(CreateProcessFailed err)
                        else
                            // 9. Stage 6 PR-B — create a Job Object,
                            // mark it KILL_ON_JOB_CLOSE, and assign
                            // the freshly-spawned child to it. The
                            // child is already running by the time
                            // CreateProcess returned (we don't pass
                            // CREATE_SUSPENDED), so there's a
                            // microsecond-window race in which the
                            // child could fork before assignment;
                            // cmd.exe in practice doesn't fork
                            // anything that fast, and any escapee
                            // would still be killed by the standard
                            // ConPTY pipe-close path on shutdown.
                            // The Job Object is the kernel-enforced
                            // safety net for the post-CreateProcess
                            // descendant tree (e.g. processes
                            // started by a shell command Stage 7's
                            // Claude Code launches).
                            //
                            // On any setup failure, we MUST
                            // terminate the orphan child before
                            // returning Error — leaving a running
                            // child after a failed start would leak
                            // a process and confuse later cleanup.
                            let cleanupChildOnFailure () =
                                try Win32.TerminateProcess(pi.hProcess, 1u) |> ignore with _ -> ()
                                try Win32.CloseHandle(pi.hProcess) |> ignore with _ -> ()
                                try Win32.CloseHandle(pi.hThread) |> ignore with _ -> ()
                                Win32.DeleteProcThreadAttributeList(attrList) |> ignore
                                Marshal.FreeHGlobal(attrList)
                                hPC.Dispose()
                                inputWrite.Dispose()
                                outputRead.Dispose()
                            let jobHandleRaw = Win32.CreateJobObjectW(IntPtr.Zero, null)
                            if jobHandleRaw = IntPtr.Zero then
                                let err = Marshal.GetLastWin32Error()
                                cleanupChildOnFailure ()
                                Error(JobObjectSetupFailed("CreateJobObjectW", err))
                            else
                                let jobHandle = new SafeJobHandle(jobHandleRaw)
                                // Build JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                                // with KILL_ON_JOB_CLOSE on; everything
                                // else zeroed.
                                let mutable jobInfo =
                                    { BasicLimitInformation =
                                        { PerProcessUserTimeLimit = 0L
                                          PerJobUserTimeLimit = 0L
                                          LimitFlags = Constants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                          MinimumWorkingSetSize = 0un
                                          MaximumWorkingSetSize = 0un
                                          ActiveProcessLimit = 0u
                                          Affinity = 0un
                                          PriorityClass = 0u
                                          SchedulingClass = 0u }
                                      IoInfo =
                                        { ReadOperationCount = 0UL
                                          WriteOperationCount = 0UL
                                          OtherOperationCount = 0UL
                                          ReadTransferCount = 0UL
                                          WriteTransferCount = 0UL
                                          OtherTransferCount = 0UL }
                                      ProcessMemoryLimit = 0un
                                      JobMemoryLimit = 0un
                                      PeakProcessMemoryUsed = 0un
                                      PeakJobMemoryUsed = 0un }
                                let jobInfoSize =
                                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
                                let jobInfoPtr = Marshal.AllocHGlobal(jobInfoSize)
                                try
                                    Marshal.StructureToPtr<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(
                                        jobInfo, jobInfoPtr, false)
                                    let setOk =
                                        Win32.SetInformationJobObject(
                                            jobHandle.DangerousGetHandle(),
                                            Constants.JobObjectExtendedLimitInformation,
                                            jobInfoPtr,
                                            uint32 jobInfoSize)
                                    if not setOk then
                                        let err = Marshal.GetLastWin32Error()
                                        jobHandle.Dispose()
                                        cleanupChildOnFailure ()
                                        Error(JobObjectSetupFailed("SetInformationJobObject", err))
                                    else
                                        let assignOk =
                                            Win32.AssignProcessToJobObject(
                                                jobHandle.DangerousGetHandle(),
                                                pi.hProcess)
                                        if not assignOk then
                                            let err = Marshal.GetLastWin32Error()
                                            jobHandle.Dispose()
                                            cleanupChildOnFailure ()
                                            Error(JobObjectSetupFailed("AssignProcessToJobObject", err))
                                        else
                                            Ok
                                                { Console = hPC
                                                  Stdin = inputWrite
                                                  Stdout = outputRead
                                                  ProcessHandle = pi.hProcess
                                                  ThreadHandle = pi.hThread
                                                  ProcessId = pi.dwProcessId
                                                  AttributeList = attrList
                                                  JobHandle = jobHandle
                                                  EnvScrubStrippedCount =
                                                      envBuilt.StrippedCount }
                                finally
                                    Marshal.FreeHGlobal(jobInfoPtr)

    /// Resize the pseudo-console grid. Idempotent and safe to call from
    /// any thread; the underlying ResizePseudoConsole is documented
    /// thread-safe.
    let resize (session: PtySession) (cols: int16) (rows: int16) : Result<unit, int> =
        let size = { X = cols; Y = rows }
        let hr = Win32.ResizePseudoConsole(session.Console.DangerousGetHandle(), size)
        if hr = 0 then Ok() else Error hr

    /// Throw a Win32Exception describing the last error. Used by tests
    /// that prefer exceptions over Result-walking; production callers
    /// should pattern-match on PtyCreateError instead.
    let throwLastWin32 (context: string) =
        raise (Win32Exception(Marshal.GetLastWin32Error(), context))
