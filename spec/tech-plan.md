# Phase 1 (MVP) Implementation Specification: Accessible F# Windows Terminal for Claude Code with NVDA

This document is an implementation-focused specification for Phase 1 of an accessible Windows terminal built in F#/.NET 9, WPF (with a thin C# XAML library and an F# composition root — Elmish.WPF was investigated and dropped, see `spec/overview.md`), ConPTY (P/Invoke), a custom WPF UI Automation provider, TPL Dataflow + Channels, NAudio earcons, and Velopack distribution. The MVP target is: **a blind user can run Claude Code (Ink-based React TUI) inside the terminal, hear all output via NVDA, navigate output as a structured document, interact with selection lists via NVDA-friendly listbox semantics, and get earcons for ANSI color/style changes.** Phase 1 succeeds when "Claude Code can drive its own further development inside our terminal."

The structure below follows a **walking-skeleton / tracer-bullet** sequencing (each stage produces production-quality code in a thin, observable, end-to-end slice that the user can validate independently before adding more) — see Cockburn / Hunt & Thomas on this pattern ([Code Climate](https://codeclimate.com/blog/kickstart-your-next-project-with-a-walking-skeleton), [Henrico Dolfing](https://www.henricodolfing.com/2018/04/start-your-project-with-walking-skeleton.html), [Built In on Tracer Bullets](https://builtin.com/software-engineering-perspectives/what-are-tracer-bullets)). Microsoft’s own ConPTY samples (`EchoCon` then `MiniTerm`) show the same incremental progression: pipes → ConPTY → STARTUPINFOEX → CreateProcess → I/O loop ([microsoft/terminal samples](https://learn.microsoft.com/tr-tr/windows/terminal/samples)).

-----

## Stage 0 — Solution skeleton, CI, and “Hello WPF” baseline

**Goal.** A buildable F#/WPF solution that launches an empty window, with CI green, before any terminal logic exists. This is the "shipping skeleton" that lets every subsequent stage ship to GitHub Releases.

**Layout (F# projects + one C# WPF library + one F# executable):**

- `Terminal.Core` (F# class lib, `net9.0-windows`) — pure types: `ColorSpec`, `SgrAttrs`, `Cell`, `Cursor`, `Screen`, `VtEvent`. (Reserved placeholder DUs for `BusMessage` and `Earcon` land in their owner stages.)
- `Terminal.Pty` (F# class lib) — ConPTY P/Invoke and process lifecycle.
- `Terminal.Parser` (F# class lib) — Paul Williams VT500 state machine.
- `Terminal.Audio` (F# class lib, placeholder until Stage 9) — NAudio earcon player.
- `Terminal.Accessibility` (F# class lib, placeholder until Stage 4) — WPF AutomationPeer + UIA providers.
- `Views` (C# WPF Custom Control Library `net9.0-windows`, `<UseWPF>true</UseWPF>`, `OutputType=Library`) — `MainWindow.xaml` + a plain `App.cs : Application` (do NOT include `App.xaml` — the WPF SDK auto-classifies it as `ApplicationDefinition` which is invalid in a Library project, error `MC1002`). The custom `TerminalView : FrameworkElement` lives here too because XAML-adjacent C# is the cleanest place for `OnRender` over UIA-friendly text glyphs.
- `Terminal.App` (F# executable, `Microsoft.NET.Sdk`, `OutputType=Exe`, `<UseWPF>true</UseWPF>`, `DisableWinExeOutputInference=true`) — owns `[<EntryPoint>][<STAThread>] main`. References `Views`, `Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`. The composition root.
- `Tests.Unit` (F# xUnit + FsCheck.Xunit) and `Tests.Ui` (placeholder; FlaUI UIA3 work begins in Stage 4).

**WPF entry-point pattern (so Velopack can run before WPF starts):** in `Terminal.App/Program.fs`, declare `[<EntryPoint; STAThread>] let main argv = VelopackApp.Build().Run(); ...` then construct the `Application` and run. The Velopack call must happen before any WPF type loads (Velopack issue #195). The earlier draft of this plan put the entry point in `App.xaml.cs` of the C# library, but `App.xaml` in `OutputType=Library` is invalid — keep entry in the F# executable instead.

**Validation (Stage 0).** Solution builds with `TreatWarningsAsErrors=true`. `vpk pack` succeeds against the publish output (`dotnet publish src/Terminal.App/Terminal.App.fsproj -c Release -r win-x64 --self-contained -o publish` — note: do NOT pass `--no-restore` after a platform-default restore; produces `NETSDK1047`). The packed `pty-speak-Setup.exe` installs to `%LocalAppData%\pty-speak\current\` without UAC. App launches an empty window. NVDA announces "pty-speak terminal, window" via `AutomationProperties.Name`. CI green. The Stage 0 baseline shipped as `v0.0.1-preview.15` (after a 14-attempt diagnostic loop — see "Common pitfalls" in `docs/RELEASE-PROCESS.md`).

**Failure modes.** WPF reference missing (`UseWPF` not set on the F# executable); Velopack's `VelopackApp.Build().Run()` not called first (causes infinite re-launch loop on update — Velopack issue #195); `App.xaml` shipped in the Library project (`MC1002`); explicit `<Page>` / `<ApplicationDefinition>` items duplicating the WPF SDK auto-glob (`NETSDK1022`); PowerShell heredocs at column 0 inside indented YAML `run: |` blocks silently terminating the literal block and producing zero-second `startup_failure` runs (this was the root cause of the 14-attempt diagnostic loop; the `actionlint` workflow-lint job in CI now catches it).

-----

## Stage 1 — ConPTY “Hello World” (cmd.exe → bytes → console)

**Goal.** Spawn `cmd.exe` under a pseudoconsole, write `dir\r\n` to its input, read back the directory listing as raw bytes, and print them to `Debug`. No GUI, no parser. This is the smallest end-to-end ConPTY proof; it’s exactly what Microsoft’s `MiniTerm` sample does ([microsoft/terminal MiniTerm](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/PseudoConsole.cs); [Program.cs](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/Program.cs)) and what `EchoCon` shows in C++ ([EchoCon.cpp](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/EchoCon/EchoCon/EchoCon.cpp)).

### 1.1 P/Invoke surface (F#)

```fsharp
namespace Terminal.Pty.Native
open System
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

[<Struct; StructLayout(LayoutKind.Sequential)>]
type COORD = { mutable X: int16; mutable Y: int16 }

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type STARTUPINFO =
    { mutable cb: int32
      mutable lpReserved: string
      mutable lpDesktop: string
      mutable lpTitle: string
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
      mutable lpReserved2: IntPtr
      mutable hStdInput: IntPtr
      mutable hStdOutput: IntPtr
      mutable hStdError: IntPtr }

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type STARTUPINFOEX =
    { mutable StartupInfo: STARTUPINFO
      mutable lpAttributeList: IntPtr }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type PROCESS_INFORMATION =
    { mutable hProcess: IntPtr
      mutable hThread: IntPtr
      mutable dwProcessId: uint32
      mutable dwThreadId: uint32 }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type SECURITY_ATTRIBUTES =
    { mutable nLength: int32
      mutable lpSecurityDescriptor: IntPtr
      mutable bInheritHandle: int32 }

module Constants =
    let PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016un
    let EXTENDED_STARTUPINFO_PRESENT       = 0x00080000u
    let STARTF_USESTDHANDLES               = 0x00000100u

module PseudoConsole =
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint32 dwFlags, nativeint& phPC)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int ResizePseudoConsole(nativeint hPC, COORD size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int ClosePseudoConsole(nativeint hPC)

    // Note: hReadPipe and hWritePipe are out-parameters. Declaring them
    // as `SafeFileHandle&` is the natural choice but is *silently
    // broken* on the F# / Win32 boundary — the call succeeds and
    // returns sensible-looking handles but every subsequent operation
    // throws NullReferenceException. Declare as `nativeint&` and wrap
    // manually: `new SafeFileHandle(p, ownsHandle = true)`. The same
    // workaround applies to any kernel32 export with multiple
    // SafeFileHandle out-parameters; `Native.fs` is the reference.
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CreatePipe(
        nativeint& hReadPipe, nativeint& hWritePipe,
        nativeint lpPipeAttributes, uint32 nSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool InitializeProcThreadAttributeList(
        nativeint lpAttributeList, int dwAttributeCount, int dwFlags, nativeint& lpSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool UpdateProcThreadAttribute(
        nativeint lpAttributeList, uint32 dwFlags, nativeint Attribute,
        nativeint lpValue, nativeint cbSize, nativeint lpPreviousValue, nativeint lpReturnSize)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool DeleteProcThreadAttributeList(nativeint lpAttributeList)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateProcess(
        string lpApplicationName, string lpCommandLine,
        nativeint lpProcessAttributes, nativeint lpThreadAttributes,
        bool bInheritHandles, uint32 dwCreationFlags,
        nativeint lpEnvironment, string lpCurrentDirectory,
        STARTUPINFOEX& lpStartupInfo, PROCESS_INFORMATION& lpProcessInformation)
```

These signatures track Microsoft's `PseudoConsoleApi.cs` ([MiniTerm Native/PseudoConsoleApi.cs](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/Native/PseudoConsoleApi.cs)) and the official `0x00020016` constant  ([UpdateProcThreadAttribute docs](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute), [creating-a-pseudoconsole-session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)) with two F#-specific deviations: `nativeint` instead of `IntPtr` everywhere (just an alias) and the `nativeint&` workaround for SafeFileHandle out-parameters described above.

### 1.2 The exact lifecycle (must follow this order)

1. `CreatePipe(&inputReadSide, &inputWriteSide, NULL, 0)` — input pipe.
1. `CreatePipe(&outputReadSide, &outputWriteSide, NULL, 0)` — output pipe.
1. `CreatePseudoConsole({cols, rows}, inputReadSide, outputWriteSide, 0, &hPC)`.
1. **Close `inputReadSide` and `outputWriteSide` in the parent.** Microsoft says “Upon completion of the CreateProcess call … the handles given during creation should be freed from this process. This will decrease the reference count on the underlying device object and allow I/O operations to properly detect a broken channel”  ([creating-a-pseudoconsole-session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)). Failing this is the #1 ConPTY mistake and produces hangs.
1. **InitializeProcThreadAttributeList — call twice**: first with `lpAttributeList = NULL` to get `bytesRequired`; then `Marshal.AllocHGlobal(bytesRequired)` and call again with the allocated buffer  ([InitializeProcThreadAttributeList docs](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-initializeprocthreadattributelist)).
1. `UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, sizeof(IntPtr), NULL, NULL)`. 
1. Set `STARTUPINFOEX.StartupInfo.cb = sizeof<STARTUPINFOEX>`.
1. `CreateProcess(NULL, "cmd.exe", NULL, NULL, false, EXTENDED_STARTUPINFO_PRESENT, NULL, NULL, &siEx, &pi)` — note `bInheritHandles = false` is correct; ConPTY duplicates the handles internally via the attribute. Pass `EXTENDED_STARTUPINFO_PRESENT` ([UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)).
1. After `CreateProcess`, **close `inputReadSide` and `outputWriteSide`** in the parent if not already closed.
1. Spawn **two dedicated threads**: one that `ReadFile`s from `outputReadSide` into a `Channel<byte[]>`, one that drains a write channel into `WriteFile` on `inputWriteSide`. Microsoft strongly warns: “We highly recommend that each of the communication channels is serviced on a separate thread… Servicing all of the pseudoconsole activities on the same thread may result in a deadlock”  ([creating-a-pseudoconsole-session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)).
1. Shutdown: signal write thread to exit, **drain the output read until it returns 0 bytes or `ERROR_BROKEN_PIPE`**, then `ClosePseudoConsole(hPC)`, then `CloseHandle` on the remaining handles. Failing to drain output before `ClosePseudoConsole` is the wezterm bug Microsoft documented  ([discussion #17716](https://github.com/microsoft/terminal/discussions/17716)).

### 1.3 Common F#/ConPTY pitfalls

- **STARTUPINFOEX size**: must use `sizeof<STARTUPINFOEX>` not `sizeof<STARTUPINFO>`. F# struct layout must be `LayoutKind.Sequential` with all fields `mutable` and exactly the Win32 layout — F# field ordering preserves declaration order which matches the C struct.
- **`out SafeFileHandle&` is silently broken**: declared P/Invoke parameters of type `SafeFileHandle&` for out-handles compile, the call succeeds, but the wrapped handle throws `NullReferenceException` on first use. Declare as `nativeint&` and wrap manually with `new SafeFileHandle(p, ownsHandle = true)`. See `Terminal.Pty.Native` for the canonical pattern.
- **Don’t pass `CREATE_NEW_PROCESS_GROUP`**: it isolates the child from ConPTY’s Ctrl+C delivery  (documented gotcha at [MS Q&A](https://learn.microsoft.com/en-ca/answers/questions/5832200/c-sent-to-the-stdin-of-a-program-running-under-a-p)). For Phase 1 we want Ctrl+C to interrupt Claude Code.
- **Pipe deadlocks**: if you only read or only write you can deadlock; interleave on separate threads  (Raymond Chen, [Old New Thing](https://devblogs.microsoft.com/oldnewthing/20110707-00/?p=10223)).
- **Environment**: pass `NULL` (or in F# `0n`) for `lpEnvironment` to inherit; for Stage 7 we will explicitly compose `TERM=xterm-256color` and `COLORTERM=truecolor` env block.
- **Job Object lifecycle deferred from Stage 1.** The original spec called for wrapping the child in a Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; we deferred this so Stage 1 could land the minimum-viable ConPTY chain. Until added, a host crash leaves the child running. See `docs/CONPTY-NOTES.md` for the deferral note. Likely lands in Stage 6 alongside the input pipeline.

### 1.4 Validation criteria (Stage 1)

- Run a console F# program (or the Stage 1 acceptance test) that spawns `cmd.exe` under ConPTY and reads bytes from the output pipe via the dedicated reader Task into a bounded `Channel<byte array>`. **Good:** you capture at least the 16-byte ConPTY init prologue (`\x1b[?9001h\x1b[?1004h`). Spawning interactive `cmd.exe` for ~3 seconds also produces ~130 bytes of cmd's startup ANSI (cursor mode, alt-screen entry, OSC 0 title). **Broken:** hang on read = handle inheritance / drain bug; access violation = `cb` field wrong; `0x57` (ERROR_INVALID_PARAMETER) from CreateProcess = STARTUPINFOEX layout wrong; `NullReferenceException` on the first pipe read = the `out SafeFileHandle&` byref bug above.
- **Do NOT use fast-exit `cmd.exe /c <command>` to verify round-trip output.** ConPTY's conhost renders to the parent pipe on a timer-driven cadence (~16–32 ms); a child that produces output and exits before the next render tick has its output torn down with the HPCON. The 16-byte init prologue is reliable; round-trip output proofs require an interactive shell driven via stdin (Stage 6). See `docs/CONPTY-NOTES.md` for the full finding.
- Reference C# implementations to cross-check: [waf/MiniTerm](https://github.com/microsoft/Terminal/issues/251), [akobr/ConPty.Sample](https://github.com/akobr/ConPty.Sample), [nishang Invoke-ConPtyShell](https://github.com/samratashok/nishang/blob/master/Shells/Invoke-ConPtyShell.ps1).
- No NVDA involvement at this stage — pure byte-level proof.

-----

## Stage 2 — VT parser (parse-but-don’t-render)

**Goal.** A pure-F# Paul Williams VT500 parser that consumes bytes and emits a stream of typed `VtEvent` values. No state outside the parser, no rendering. This is testable in isolation (golden files + FsCheck) before any UI exists.

### 2.1 Reference and shape

Paul Williams’ diagram and state list at [vt100.net](https://vt100.net/emu/dec_ansi_parser.html) is the canonical spec; alacritty/vte is the canonical Rust implementation  (~3K SLOC, table-driven, MIT/Apache) and exposes a `Perform` trait callback set: `print(c)`, `execute(byte)`, `csi_dispatch(params, intermediates, ignore, action)`, `esc_dispatch`, `osc_dispatch`, `hook/put/unhook`  ([alacritty/vte](https://github.com/alacritty/vte), [docs.rs/vte](https://docs.rs/vte/latest/vte/), [jwilm.io announcement post on the table-driven design](https://jwilm.io/blog/announcing-alacritty/)). We will translate the same shape into F#.

### 2.2 Idiomatic F# state machine

F# DUs map directly to the Williams states ([fsharpforfunandprofit on state machines](https://fsharpforfunandprofit.com/posts/designing-with-types-representing-states/), [tutorialspoint on F# DUs](https://www.tutorialspoint.com/fsharp/fsharp_discriminated_unions.htm)):

```fsharp
type VtState =
    | Ground | Escape | EscapeIntermediate
    | CsiEntry | CsiParam | CsiIntermediate | CsiIgnore
    | DcsEntry | DcsParam | DcsIntermediate | DcsPassthrough | DcsIgnore
    | OscString | SosPmApcString

type VtEvent =
    | Print of Rune
    | Execute of byte                                   // C0/C1
    | CsiDispatch of parms: int[] * intermediates: byte[] * finalByte: char * priv: char option
    | EscDispatch of intermediates: byte[] * finalByte: char
    | OscDispatch of parms: byte[][] * bellTerminated: bool
    | DcsHook    of parms: int[] * intermediates: byte[] * finalByte: char
    | DcsPut     of byte
    | DcsUnhook
```

Implement `advance: byte[] -> seq<VtEvent>` with an internal `Parser` record holding `state`, `params: ResizeArray<int>`, `intermediates: byte[2]`, `oscBuffer`, etc. (alacritty caps `MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`, `MAX_OSC_RAW = 1024`  per [vte src/lib.rs](https://github.com/alacritty/vte/blob/master/src/lib.rs); copy these caps).

### 2.3 Minimum sequence support for Phase 1

Keep this *small* — Claude Code uses a narrow surface. The Stage 2 parser is generic: it emits `CsiDispatch` / `OscDispatch` / `EscDispatch` for **any** legal sequence regardless of whether the screen model interprets it yet. The split below tracks what Stage 3a's `Screen.Apply` actually consumes today vs what's deferred to a later stage.

- **C0** (parser → Execute; Screen.Apply handles all of these in Stage 3a): BS (0x08), HT (0x09), LF (0x0A), CR (0x0D), BEL (0x07), ESC (0x1B).
- **CSI cursor + erase** (Stage 3a): `CUU/CUD/CUF/CUB` (A/B/C/D, relative + clamped at edges), `CUP`/`HVP` (`H`/`f`, 1-indexed → 0-indexed), `ED` (`J` modes 0/1/2 — display erase), `EL` (`K` modes 0/1/2 — line erase).
- **CSI SGR** (`m`): the parser emits the full parameter list; Stage 3a's `Apply` handles **basic-16**: reset (0), bold (1/22), italic (3/23), underline (4/24), inverse (7/27), foreground 30-37 + bright 90-97 + default 39, background 40-47 + bright 100-107 + default 49. **256-colour (`38;5;n`/`48;5;n`) and truecolor (`38;2;r;g;b`/`48;2;r;g;b`) sub-parameter handling are deferred** — the parser would need to split on `:` vs `;` first, and the WPF renderer has no truecolor brush palette yet. Ship in a later stage. See [chadaustin's Truecolor narrative](https://chadaustin.me/2024/01/truecolor-terminal-emacs/) and [termstandard/colors](https://github.com/termstandard/colors) for what we will eventually implement.
- **CSI DEC private modes** (`?h`/`?l` — DECSET / DECRST): the parser emits `CsiDispatch` with `priv = Some '?'`; **`Screen.Apply` silently ignores all of them in Stage 3a**. Stage 4+ adds them as their owners need them: 1 (DECCKM, owned by the input layer in Stage 6), 25 (DECTCEM cursor visibility, Stage 4), 1049 (alt screen + save cursor, when an Ink TUI lands in Stage 7), 2004 (bracketed paste, Stage 6), 1004 (focus events, Stage 6).
- **OSC** (parser-side only in Stage 2): 0/2 (window title), 7 (working dir notification, optional). UIA exposure of OSC 0/2 happens in Stage 4; OSC 8 hyperlink lookup is Stage 4+.
- **ESC**: `ESC =`/`ESC >` (DECKPAM/DECKPNM), `ESC 7`/`ESC 8` (DECSC/DECRC). Parser-side only.

xterm's full reference is at [invisible-island ctlseqs](https://www.invisible-island.net/xterm/ctlseqs/ctlseqs.html). Don't implement DCS Sixel, ReGIS, or device-attributes responses in Phase 1.

### 2.4 Validation (Stage 2)

- **Unit tests (xUnit + FsCheck)** with explicit byte-string fixtures:
  - `"Hello\r\n"` → `[Print 'H'; Print 'e'; Print 'l'; Print 'l'; Print 'o'; Execute 0x0D; Execute 0x0A]`.
  - `"\x1b[31mRed\x1b[0m"` → SGR(31), three Prints, SGR(0).
  - `"\x1b[2J"` → CsiDispatch with `parms=[2]`, final=`'J'`.
  - `"\x1b]0;Title\x07"` → OscDispatch with bell-terminated.
  - `"\x1b[?1049h"` → CsiDispatch with `priv='?'`, `parms=[1049]`, final=‘h’.
- **Property-based tests with FsCheck** ([fscheck.github.io](https://fscheck.github.io/FsCheck/)): “for any sequence of random bytes the parser never throws and always returns to Ground after at most N bytes following an ST/BEL/CAN/SUB”. This is the kind of fuzz-resilience Williams’ diagram guarantees (“Correctness – if you were to feed this parser a stream of characters that is random or deliberately pathological, it is claimed that this parser will exhibit the same visible behaviour as any one of DEC’s 8-bit ANSI-compatible terminals” — [vt100.net](https://vt100.net/emu/dec_ansi_parser.html)).
- **Golden files**: capture 5–10 KB of real Claude Code output (run claude in conhost, redirect with `script` or a tee-pipe), commit as test fixtures, assert event-stream equivalence run-to-run. Common parser bugs to catch: forgetting CAN(0x18)/SUB(0x1A) cancel any state back to Ground; treating OSC ST as `\x1b\\` only and missing BEL; off-by-one on default parameter (empty = 0 = default).
- **No NVDA validation needed** at Stage 2.

-----

## Stage 3 — Screen model + WPF rendering

**Goal.** A terminal screen buffer wired to a WPF custom control such that running `cmd.exe` inside the app shows correct text. Still no accessibility, still no earcons.

> **As shipped, Stage 3 split into two checkpoints** for review and rollback ergonomics — `baseline/stage-3a-screen-model` (`Terminal.Core` gains the data types and `Screen.Apply` for the basic-16 SGR + cursor + erase subset) and `baseline/stage-3b-wpf-rendering` (`Views/TerminalView.cs` overrides `OnRender(DrawingContext)` and `Terminal.App/Program.fs` wires the full pipeline). See `docs/CHECKPOINTS.md`. The substages share validation criteria; only Stage 3b's end-to-end pipe is end-user observable.

### 3.1 Screen buffer data structure

For Phase 1, **list-of-lines with a circular scrollback buffer of `Cell[]`** is the simplest design that performs well enough and is easy to expose to UIA later. Each cell stores:

```fsharp
[<Struct>]
type SgrAttrs =
    { Fg: ColorSpec     // Default | Indexed of byte | Rgb of byte*byte*byte
      Bg: ColorSpec
      Bold: bool; Italic: bool; Underline: bool; Inverse: bool }

[<Struct>]
type Cell = { Ch: Rune; Attrs: SgrAttrs }
```

Maintain two buffers — `primary` and `alternate` (alt-screen ?1049 swaps them; never copy scrollback into the alt buffer — see [daintreehq #1490](https://github.com/daintreehq/daintree/issues/1490) on the gnome-terminal/iTerm2 conventions). Use a **ring buffer over `Cell[]` rows** with a fixed scrollback cap (say 5 000 lines) — wezterm’s default is 3 500 ([wezterm scrollback docs](https://wezterm.org/scrollback.html)).

### 3.2 Cursor / resize / streaming

- A separate `Cursor = { Row; Col; Visible; SaveStack }` value — DECSC/DECRC just push/pop.
- Resize: when `ResizePseudoConsole` is called, the buffer’s column count changes; lines either wrap or get truncated. Phase 1 strategy: snap PTY to the new grid, retain rows as-is, let Claude Code redraw on its next render. (Reflow is hard — out of scope.)
- Streaming: feed bytes in 16 KB chunks from the read thread → `Parser.advance` → mutate buffer → emit a “buffer-changed since seq N” notification. **All buffer mutation lives on a single dedicated thread** (the parser thread).

### 3.3 WPF rendering choices

The WPF text-rendering hierarchy (slowest→fastest): `TextBlock` < `FormattedText` < `TextLine`/`GlyphRun` ([Microsoft Learn: Optimizing Performance: Text](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-text); benchmark in [dgrunwald/WPF-Text-Rendering-Benchmark](https://github.com/dgrunwald/WPF-Text-Rendering-Benchmark) showing GlyphRun is the fastest). For Phase 1 **use `DrawingVisual` + `FormattedText`** in an `OnRender` override of a `FrameworkElement` subclass; this is the simplest WPF path that supports per-cell foreground/background brushes and is “fast enough” for 80×24 streams. We will not use `TextBlock` (too slow when redrawn 60×/sec on streaming output).

The control layout: a host `Canvas` containing a single `TerminalView : FrameworkElement` that overrides `OnRender(DrawingContext)`. Per row, build one `FormattedText` per contiguous run of cells with identical attrs (typical Claude Code output averages ~3 runs per line, so this is cheap). Background fills are drawn first (`DrawingContext.DrawRectangle(brush, null, Rect)`), text on top.

### 3.4 Validation (Stage 3)

Stage 3 has no keyboard-input pipeline (Stage 6) and no `ResizePseudoConsole` wiring yet (planned with the input pipeline so resize and key events share the same dispatcher). The validation here focuses on what `cmd.exe` produces *unprompted* on startup.

- Launch `Terminal.App.exe` (or `dotnet run --project src/Terminal.App`); a 30×120 terminal surface opens with `cmd.exe` running underneath. Wait ~1 s; cmd's startup banner and first prompt render in the WPF window.
- Stage 3a unit tests (`tests/Tests.Unit/ScreenTests.fs`) cover Print + auto-wrap + scroll, BS/HT/LF/CR, CSI A/B/C/D/H/f, J/K erase, and basic-16 SGR.
- 256-color tests, resize behaviour, and "type characters into the WPF window, see them appear" all belong to the stages that ship the corresponding plumbing — 256-color in a parser/screen colour-handling pass, resize + typing in Stage 6.
- Still no NVDA.

-----

## Stage 4 — First UIA provider (text exposure)

**Goal.** NVDA’s review cursor (`NVDA+Numpad`) can read the terminal content character by character, word by word, line by line. Narrator can read it. No streaming announcements yet — just static text exposure.

### 4.1 AutomationPeer skeleton

Override `OnCreateAutomationPeer` on `TerminalView` to return a `TerminalAutomationPeer` derived from `FrameworkElementAutomationPeer` ([WPF docs: UI Automation of a Custom Control](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control)). Required Core overrides:

- `GetAutomationControlTypeCore() = AutomationControlType.Document` (Document is the right type for “navigable text” — same choice TermControl makes; see Windows Terminal’s [TermControlAutomationPeer.cpp](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControlAutomationPeer.cpp)).
- `GetClassNameCore() = "TerminalView"` — pick a *unique stable name*; NVDA keys overlay classes by class name (NVDA’s `winConsoleUIA.py` keys on `UIAAutomationId == "Text Area"` for Windows Terminal — see [winConsoleUIA.py](https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/winConsoleUIA.py)).
- `GetNameCore() = "Terminal"`.
- `IsControlElementCore = true`, `IsContentElementCore = true`.
- `GetPatternCore(PatternInterface.Text) -> ITextProvider` returning the peer itself (or a delegate object).

### 4.2 ITextProvider / ITextRangeProvider

These live in `System.Windows.Automation.Provider` and follow the contract documented at [Microsoft Learn: Text and TextRange Control Patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange). Minimal Phase 1 implementation:

`ITextProvider`:

- `DocumentRange { get }` → a `TerminalTextRange` covering rows 0..N-1.
- `SupportedTextSelection { get }` → `SupportedTextSelection.None` (Phase 1; Phase 2 can add Single).
- `GetSelection()` → empty array.
- `GetVisibleRanges()` → a single range covering on-screen rows.
- `RangeFromChild(child)` and `RangeFromPoint(point)` → simple stubs returning the document range.

`ITextRangeProvider` (the heavy lifting):

- Identity: hold `(startRowCol, endRowCol)` pointing into a snapshot of the buffer.
- `GetText(maxLength)` → join the cells’ `Ch` for the range, inserting `'\n'` at row boundaries; truncate to `maxLength` if `>= 0`. Microsoft’s guidance: return **plain text, including control characters like CR**, but no markup ([Text and TextRange](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange)).
- `Move(unit, count)` and `MoveEndpointByUnit(endpoint, unit, count)` — for `Character`, `Word`, `Line`, `Paragraph`, `Document`. Word is whitespace-bounded; Line is a row; Paragraph = Line for terminals; Document = full buffer.
- `Compare`, `CompareEndpoints`, `Clone`, `ExpandToEnclosingUnit`, `FindAttribute` (return null), `FindText` (substring), `GetAttributeValue` (return `AutomationElementIdentifiers.NotSupported` for everything in Phase 1), `GetBoundingRectangles` (compute pixel rects from cell coords for each line), `GetEnclosingElement` (the peer itself), `Select` (no-op, since we report no selection support), `ScrollIntoView`, `AddToSelection`/`RemoveFromSelection` (no-ops).

The Windows Terminal `TermControlAutomationPeer` and its `XamlUiaTextRange` ([microsoft/terminal #2083](https://github.com/microsoft/terminal/pull/2083/files/967421649e23e511bb8781a998b077702a6ab982)) is the gold-standard reference for this code; copy its structure.

### 4.3 Snapshot rule (UIA thread-safety)

UIA calls into your provider on the **UIA RPC thread, not the WPF Dispatcher**. Mutating the buffer while UIA reads = crashes (TermControl crashes when accessing the buffer off-thread are documented in [terminal #14592](https://github.com/microsoft/terminal/issues/14592)). Strategy: every range holds an immutable snapshot of the affected rows (cheap because rows are arrays). When the buffer mutates, we increment a sequence number; ranges already handed to NVDA keep their snapshot.

### 4.4 Programmatic verification (no NVDA needed)

- **Inspect.exe** (`C:\Program Files (x86)\Windows Kits\10\bin\<sdk>\<arch>\Inspect.exe`): hover over the terminal, verify ControlType=Document, ClassName=TerminalView, Patterns include Text, Document Range returns the buffer text ([Microsoft Learn: Inspect](https://learn.microsoft.com/en-us/windows/win32/winauto/inspect-objects)).
- **AccEvent.exe** for event monitoring later ([AccEvent docs](https://learn.microsoft.com/en-us/windows/win32/winauto/accessible-event-watcher)).
- **Accessibility Insights for Windows** is the modern replacement and is the recommended tool ([Microsoft Learn: Accessibility testing](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-testing)).
- **FlaUI/UIA3 unit tests**: `var app = Application.Attach(pid); using var auto = new UIA3Automation(); var elem = auto.GetDesktop().FindFirstDescendant(cf => cf.ByClassName("TerminalView")); var text = elem.Patterns.Text.Pattern.DocumentRange.GetText(int.MaxValue);` then assert ([FlaUI README](https://github.com/FlaUI/FlaUI), [Gu.Wpf.UiAutomation](https://github.com/GuOrg/Gu.Wpf.UiAutomation)).

### 4.5 NVDA validation (Stage 4)

- Open the terminal with cmd running. **Press Caps Lock+Numpad8** (Say current line) — NVDA reads the prompt line. **Numpad 7/9** moves prev/next line. **Numpad 4/5/6** previous word / current word / next word. **Numpad 1/2/3** for character ([Perkins School: NVDA Review Cursor](https://www.perkins.org/resource/computer-numpad-part-2-nvda-review-cursor-video-tutorial/), [NVDA User Guide review-cursor section](https://download.nvaccess.org/documentation/userGuide.html)).
- “Good” sounds like: NVDA reads the visible text and never says “blank” on lines that are not actually empty. “Broken” sounds like: NVDA says “TerminalView” and stops (no pattern), or “blank” (empty range), or repeats the same line forever (range Move not advancing).

-----

## Stage 5 — Streaming output notifications

**Goal.** When PTY output arrives, NVDA reads it aloud at conversational pace. Spinner doesn’t flood. Multi-line output is announced line by line. Tested with `echo`, `dir`, busy loops.

### 5.1 Use UIA Notification events, not TextChanged

This is the modern best practice and what Windows Terminal switched to in 2022 ([nvaccess/nvda PR #14047](https://github.com/nvaccess/nvda/pull/14047), [issue #13781](https://github.com/nvaccess/nvda/issues/13781)). NVDA explicitly considers `UIA_Text_TextChangedEventId` a perf nightmare (“Processing the number of text change events sent by wt and conhost is a huge bottleneck”) and disabled it for non-terminal UIA classes ([PR #14067](https://github.com/nvaccess/nvda/pull/14067)). For our terminal, register an `automationId` and `className` that signals “modern terminal that uses notifications” — copy Windows Terminal’s `"TermControl2"` precedent. On every coalesced flush, raise a Notification event.

### 5.2 API

In WPF/.NET, `AutomationPeer.RaiseNotificationEvent` was added to System.Windows.Automation.Peers in .NET Framework 4.8 / .NET Core. The native call underlying it is `UiaRaiseNotificationEvent(IRawElementProviderSimple* provider, NotificationKind kind, NotificationProcessing processing, BSTR displayString, BSTR activityId)` ([Microsoft Learn: UiaRaiseNotificationEvent](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiaraisenotificationevent)). Use:

- `NotificationKind.Other` for output text.
- `NotificationProcessing.All` for normal output (queue everything, in order).
- `NotificationProcessing.MostRecent` or `ImportantMostRecent` for status updates that should preempt — e.g., “Claude finished” — because NVDA cancels prior speech when these flags arrive ([nvaccess PR #9466](https://github.com/nvaccess/nvda/pull/9466)).
- `displayString` = the announcement text (one or more lines, joined with spaces or `\n`).
- `activityId` = a stable non-localized tag like `"output"`, `"prompt"`, `"error"` so NVDA can be configured per-tag.

### 5.3 Coalescing / debouncing

The “spinner problem” is real (Claude Code’s Ink renderer redraws the same spinner row at ~10 Hz; without filtering NVDA will read “thinking” or the spinner glyph forever). Use a **debounced flush window of 150–250 ms** and a per-line dedup:

- The parser thread mutates the buffer and pushes “row N changed” tickets into a `Channel<int>` (drop-oldest with capacity 1 per row).
- A **Dataflow consumer** runs `BufferBlock<int>` → `BatchBlock` (size 64) on a 200 ms timer using `Task.Delay`/`PeriodicTimer`.
- Each batch is reduced: **only consider rows that don’t end in the same hash as the previous flush**, **suppress same-row updates ≥ 5/sec for ≥ 1 s** (spinner heuristic), and **collect contiguous changed rows into a single string**.
- Marshal to the WPF `Dispatcher` (mandatory: `RaiseNotificationEvent` must be called on the thread that owns the AutomationPeer, i.e., the UI thread — Windows Terminal does this with `dispatcher.RunAsync` for the same reason in [TermControlAutomationPeer.cpp](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControlAutomationPeer.cpp)).
- Call `peer.RaiseNotificationEvent(NotificationKind.Other, NotificationProcessing.All, text, "output")`.

### 5.4 TPL Dataflow + Channels backpressure

`BroadcastBlock` is the wrong tool for the *output→announce* path because it drops messages under backpressure ([SO discussion of BroadcastBlock with bounded queues](https://qa.social.msdn.microsoft.com/Forums/en-US/69f65dd4-f802-4cb6-afbb-e0309f939fe9/broadcastblock-with-guaranteed-delivery-and-back-pressure?forum=tpldataflow)). Use it only for “fan-out raw bytes to renderer + parser + earcon” where loss is fine because the renderer is backed by the same buffer. For announcements, use a `BoundedChannel<Announcement>` with `FullMode = DropOldest` so spinner storms don’t block the parser thread.

### 5.5 Validation (Stage 5)

- `echo hello` → NVDA says “hello” exactly once.
- `dir` → NVDA reads each line of the listing in order, with a pause between flushes.
- Busy-loop printing dots for 5 s → NVDA does not get “stuck”; speech queue drains in <1 s after loop ends.
- Cursor blinking does not produce announcements (no row text changed, only attrs).
- Use the **NVDA Event Tracker add-on** ([addons.nvda-project.org evtTracker](https://addons.nvda-project.org/addons/evtTracker.en.html)) to confirm NVDA received the Notification events with the expected `displayString` and `activityId`.

### 5.6 Failure modes

- **Double-announcement**: typically means both LiveText (TextChanged) and Notification fire. Make sure `ITextProvider2` is *not* implemented in Phase 1 with `TextChangedEvent` raised on every mutation — only Notification. NVDA explicitly blocks notifications for the *legacy* console class to avoid double-reporting ([nvaccess PR #13261](https://github.com/nvaccess/nvda/pull/13261)).
- **No announcement at all**: peer not on Dispatcher thread → `RaiseNotificationEvent` silently no-ops. Wrap in `Dispatcher.BeginInvoke`.
- **Spinner loop**: dedup by hash of the row contents; suppress if same hash within 1 s.

-----

## Stage 6 — Keyboard input to PTY

**Goal.** Typing into the WPF window sends correct VT byte sequences to the child. Arrow keys, Tab, Enter, Ctrl+C, Ctrl+L, Esc, function keys all work. NVDA shortcuts (anything with Insert/CapsLock) are NOT swallowed.

### 6.1 Capture model

Use **two events** on the `TerminalView` ([Microsoft Learn: Input Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/input-overview)):

- `TextInput` (or `PreviewTextInput`) for actual character input — this is the only correct WPF event for IME, dead keys, and AltGr composition. WPF folks explicitly warn against KeyDown for typed characters: “input can come from voice, ink, IMEs and other stuff… WPF does all the processing in a global input processing pipeline” ([dotnet/wpf discussion #8217](https://github.com/dotnet/wpf/discussions/8217)).
- `PreviewKeyDown` for non-textual keys (arrows, F-keys, Tab, Enter, Esc, Ctrl-combos, modifier-only sequences).

### 6.2 NVDA-aware filtering

In `PreviewKeyDown`, **return immediately without `e.Handled = true`** if any of:

- `Keyboard.Modifiers` includes `ModifierKeys.None` and `e.Key == Key.CapsLock` (NVDA modifier key).
- `e.Key == Key.Insert` or `e.SystemKey == Key.Insert` (also NVDA modifier).
- The key is part of NVDA’s Numpad review (Numpad0..9 with NumLock off + NVDA prefix). The simplest rule: **if NumLock is off and the key is a numpad key, do not handle it** so NVDA can take it.

### 6.3 Key→bytes translation

Reference: [invisible-island ctlseqs](https://www.invisible-island.net/xterm/ctlseqs/ctlseqs.html) and Windows Terminal’s [terminalInput.cpp](https://github.com/microsoft/terminal/blob/main/src/terminal/input/terminalInput.cpp). The parser-side state for DECCKM (mode 1) decides Normal vs Application cursor mode:

|Key  |Normal (DECCKM off)    |Application (DECCKM on)|
|-----|-----------------------|-----------------------|
|Up   |`\x1b[A`               |`\x1bOA`               |
|Down |`\x1b[B`               |`\x1bOB`               |
|Right|`\x1b[C`               |`\x1bOC`               |
|Left |`\x1b[D`               |`\x1bOD`               |
|Home |`\x1b[1~` (or `\x1bOH`)|`\x1bOH`               |
|End  |`\x1b[4~` (or `\x1bOF`)|`\x1bOF`               |

(See the table at [xfree86 ctlseqs](https://www.xfree86.org/current/ctlseqs.html); ESPTerm’s transcription is also correct ([espterm-xterm.html](https://espterm.github.io/docs/espterm-xterm.html)).)

Editing keypad (always the same regardless of DECCKM): `Insert=\x1b[2~`, `Delete=\x1b[3~`, `PageUp=\x1b[5~`, `PageDown=\x1b[6~`, `F1=\x1bOP`, `F2=\x1bOQ`, `F3=\x1bOR`, `F4=\x1bOS`, `F5=\x1b[15~`, `F6=\x1b[17~` … `F12=\x1b[24~`.

Modifier encoding for cursor keys (Ctrl/Shift/Alt): `\x1b[1;<mod>A` where mod=2(Shift), 3(Alt), 5(Ctrl), 6(Ctrl+Shift), etc.

Special handling:

- **Ctrl+C** → `\x03` (and ConPTY translates this into a real Ctrl-Break for the child; that’s why we must not pass `CREATE_NEW_PROCESS_GROUP`).
- **Ctrl+D** → `\x04`. **Ctrl+L** → `\x0C`. **Ctrl+\** → `\x1C`.
- **Tab** → `\t`. **Shift+Tab** → `\x1b[Z`. **Esc** → `\x1b`. **Backspace** → `\x7f` (DEL) by default in xterm, configurable.
- **Bracketed paste**: when the parser sees `?2004h`, wrap pasted text in `\x1b[200~ ... \x1b[201~`.
- **Focus events**: when `?1004h` is set, send `\x1b[I` on Got Focus and `\x1b[O` on Lost Focus.

### 6.4 TextInput

For `e.Text` (string), encode UTF-8 and write to PTY. WPF’s `TextInput` already handles dead-keys and IME composition for you (it fires once with the composed string).

### 6.5 Validation (Stage 6)

- Launch PowerShell inside the terminal. Type `ls`+Enter. NVDA announces directory listing.
- Up/Down arrows scroll command history (PowerShell echoes new prompt, NVDA reads it).
- Tab completion works (PowerShell echoes the completion).
- Ctrl+C in a `ping localhost -t` interrupts.
- Esc cancels a Tab completion.
- **NVDA shortcuts still work**: pressing Insert+T announces window title; Caps Lock+Numpad7 reads previous line.

### 6.6 Failure modes

- “NVDA shortcut isn’t working” → you set `e.Handled = true` too aggressively; the WPF input pipeline is consumed before NVDA’s hooks run.
- “Arrow keys do nothing in vim/htop” → DECCKM tracking missing in parser; arrows always send `\x1b[A` even after Application mode set.
- “IME / Korean / Chinese doesn’t work” → you used KeyDown char fallback instead of TextInput.

-----

## Stage 7 — Run Claude Code end-to-end

**Goal.** Launch Claude Code as the child process, complete a roundtrip prompt → response, hear it via NVDA. Document what works and what doesn’t (the latter informs Stages 8–10).

### 7.1 Launching Claude Code on Windows

Claude Code on Windows distributes as a native binary (`claude.exe`) installed at `%USERPROFILE%\.local\bin\claude.exe` by the official native installer ([Claude Code: Advanced Setup docs](https://code.claude.com/docs/en/setup)). Older paths (npm-installed or WSL) still exist; the simplest Phase 1 approach: **shell out to `cmd.exe /c claude` or directly launch `claude.exe` if found on PATH**, letting users run claude from their normal install. Phase 1 does not bundle claude.

Resolution order:

1. `where.exe claude` to find it; if missing, error message saying “install Claude Code first”.
1. Launch it via the same ConPTY pipeline as cmd.exe — the executable name and command line are the only differences.

### 7.2 Environment

Set in the `lpEnvironment` block (UTF-16, double-null terminated):

- `TERM=xterm-256color` (Claude Code/Ink expects this).
- `COLORTERM=truecolor` (enables 24-bit RGB output — see [termstandard/colors](https://github.com/termstandard/colors)).
- Inherit the parent’s `PATH`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOME` (set HOME to `%USERPROFILE%` if absent for npm/git compatibility).
- Pass through `ANTHROPIC_API_KEY` if set (or rely on `claude login` having stored creds).

Claude Code also has one Windows-specific knob worth surfacing: `CLAUDE_CODE_GIT_BASH_PATH` to point at Git Bash, since Claude internally uses Git Bash for shell tools ([Claude Code terminal config](https://code.claude.com/docs/en/terminal-config)).

### 7.3 Expected Claude Code startup behavior

On first invocation Claude Code will:

- Send `\x1b[?1049h` (enter alt screen) — your buffer must support this.
- Send `\x1b[?25l` (hide cursor) frequently during render.
- Send `\x1b[?2004h` (bracketed paste enable) — your input layer must respect it.
- Use truecolor SGR everywhere: `\x1b[38;2;r;g;bm`.
- **Re-render the entire screen on every state change** — this is Ink’s reconciler driving the whole frame each tick ([Ink Terminal Rendering Engine](https://deepwiki.com/youmengde/claude-code-snapshot-backup/8.3-ink-terminal-rendering-engine)). Anthropic explicitly rewrote it for diff-based output to reduce flicker ([Boris Cherny on rewrite](https://www.threads.com/@boris_cherny/post/DSZbZatiIvJ/)) but you should still expect frequent same-frame redraws.

### 7.4 Validation (Stage 7)

- Run `claude` inside the terminal. NVDA announces the welcome screen.
- Type a prompt (“Say hi”). Press Enter. Claude responds. NVDA reads the response.
- **Known issues you’ll hit (drives next stages):**
  - Spinner during “thinking” announces repeatedly → fix in Stage 5’s dedup if not already; tune in Stage 10.
  - Selection prompt (“Edit / Yes / Always / No”) is announced as flat text, not as a listbox → Stage 8.
  - Red error text is announced as plain text → Stage 9.
  - No way to jump back through the response after focus moves → Stage 10.

-----

## Stage 8 — Interactive list detection + UIA List provider

**Goal.** When Claude Code shows a selection list, NVDA announces it as a real listbox, arrow keys move through items, Enter confirms. The UI Automation tree exposes a List with ListItem children in parallel with the Document text view.

### 8.1 Detection heuristic

VT events alone tell you when “Edit / Yes / Always / No (esc)” is on screen. Heuristic that works for Ink-based TUIs:

1. **Stable rectangular region** — N consecutive rows, last touched within the past 100 ms with no new output for the next 100 ms.
1. **Single row differs in fg/bg SGR** — the highlighted item has inverted attrs (`SGR 7`) or a distinct background.
1. **Arrow key causes exactly one row’s highlight to move** — track the last keystroke and the next paint; if Up/Down was pressed and only the highlight column moved, you’ve confirmed a list.
1. **Items separated by `/`, `|`, or distinct lines** — parse the highlighted region to extract item labels.

This is intentionally a Phase 1 heuristic. False positives are tolerable; the user can press a hotkey (e.g., `Alt+Shift+L`) to disable list mode for a session.

### 8.2 UIA list provider (parallel to Document)

In `GetChildrenCore` of `TerminalAutomationPeer`, when a list is detected return:

- A `TerminalListAutomationPeer` with `ControlType.List`, implementing `ISelectionProvider` ([Microsoft Learn: ISelectionProvider](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.provider.iselectionprovider) — `GetSelection()`, `CanSelectMultiple = false`, `IsSelectionRequired = true`).
- For each item, a `TerminalListItemAutomationPeer` with `ControlType.ListItem`, implementing `ISelectionItemProvider` (`Select`, `AddToSelection`, `RemoveFromSelection`, `IsSelected`, `SelectionContainer`) and `IInvokeProvider` (`Invoke()`).
- Raise `AutomationEvents.SelectionItemPatternOnElementSelected` when the highlighted item changes; raise `AutomationEvents.AutomationFocusChanged` on the new item. The pattern is the same as `ListBoxAutomationPeer` and is documented at [Microsoft Learn: ISelectionItemProvider](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.provider.iselectionitemprovider) (and Microsoft’s [UIAutomationFragmentProvider sample](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/UIAutomationFragmentProvider/README.md)).

### 8.3 NVDA list-mode key translation

When NVDA is in focus mode on the list and the user presses Down arrow, NVDA sends Down to our control. Our peer should:

1. Translate `ISelectionItemProvider.Select` (or arrow key in focus mode) to a PTY arrow byte sequence — exactly the same translation as Stage 6.
1. Wait for the parser to detect the highlight move (timeout 200 ms).
1. Update the selected ListItem peer accordingly and raise the SelectionChanged event.

Enter / Invoke → `\r` (or `\x1b\r` if alt-mode) sent to PTY, then collapse the list peer (it disappears from the UIA tree on next tick because the highlight region is gone).

### 8.4 Validation (Stage 8)

- Trigger a Claude prompt that asks to edit a file (“edit foo.py”). Claude shows “Edit, Yes, Always, No”. NVDA announces “list, Edit, 1 of 4” or similar.
- Down arrow → “Yes, 2 of 4”. NVDA announces.
- Enter → confirms; list disappears; NVDA announces Claude’s next output.

### 8.5 Failure modes

- **List peer persists after dismissal** → make sure detection requires the region to be stable; on first re-render that destroys the rectangular region, drop the peer.
- **NVDA reads it twice** (once as document text and once as listbox) → in `IsContentElementCore` for the rows backing the list, return `false` so the document view skips them while the list peer covers them. (This is the same `IsContentElement=false` trick TermControl uses for HwndHost layering — [terminal PR #14097](https://github.com/microsoft/terminal/pull/14097).)

-----

## Stage 9 — Earcons (NAudio) and color announcement

**Goal.** Red text plays an “alarm” earcon, green plays “confirm”, yellow plays “warn”. User can mute. No interference with NVDA speech.

### 9.1 NAudio setup

```fsharp
open NAudio.CoreAudioApi
open NAudio.Wave

let device = MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
let output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100)  // 100ms latency
let format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)  // 48 kHz mono
```

`AudioClientShareMode.Shared` is correct: it shares the device with NVDA’s TTS without taking exclusive control (Mark Heath’s NAudio docs: “In shared mode… will automatically resample the incoming audio. If you choose Exclusive then you are requesting exclusive access to the sound card” — [naudio/NAudio WasapiOut.md](https://github.com/naudio/NAudio/blob/master/Docs/WasapiOut.md)). 100 ms latency is enough for short earcons; lower latency drops can cause clicks.

### 9.2 Synthesis

Use `NAudio.Wave.SampleProviders.SignalGenerator` (built-in sine/square/triangle/sawtooth/white-noise/pink-noise) wrapped in a custom `EnvelopedSampleProvider` that applies an ADSR envelope. The envelope shape is the standard Attack→Decay→Sustain→Release ([thewolfsound on envelopes](https://thewolfsound.com/envelopes/), [Earlevel Engineering ADSR code](https://www.earlevel.com/main/2013/06/03/envelope-generators-adsr-code/)). For 100–200 ms earcons, attack 5 ms, decay 30 ms, sustain 0.7, release 80 ms produces a clean, click-free tone.

### 9.3 Frequency mapping

NVDA’s TTS speech band is roughly 100–4 000 Hz. To **avoid masking speech**, place earcons either *below 180 Hz* (felt rather than heard) or *above 1.5 kHz with short duration*. Recommended Phase 1 vocabulary:

|Color/Style                     |Earcon          |Frequency             |Waveform|Duration|
|--------------------------------|----------------|----------------------|--------|--------|
|Red (FG 1, 9, or RGB-r-dominant)|“alarm”         |220 Hz                |Square  |120 ms  |
|Yellow (FG 3, 11)               |“warn”          |440 Hz                |Triangle|90 ms   |
|Green (FG 2, 10)                |“confirm”       |880 Hz + 1320 Hz pluck|Sine    |60 ms   |
|Cyan (FG 6, 14)                 |“info”          |1760 Hz blip          |Sine    |40 ms   |
|Bold                            |high-pitch chirp|2200 Hz               |Sine    |30 ms   |
|Italic                          |(none in MVP)   |—                     |—       |—       |

Trigger rule: emit the earcon on **transition into the attribute** (last cell had different attrs), not per cell. Coalesce: if multiple transitions happen within 50 ms (e.g., rainbow text), pick the dominant color of the run.

### 9.4 No-conflict with NVDA

- Use shared mode (already covered).
- **Volume by default 30%** so NVDA speech remains dominant.
- **Latency 100 ms is fine**; don’t go to exclusive mode (would silence NVDA TTS).
- Provide a global mute toggle (default key `Ctrl+Shift+M`), persisted in TOML config.

### 9.5 Configuration via Tomlyn

```fsharp
open Tomlyn
type EarconCfg = { Enabled: bool; Volume: float; Mappings: Map<string, EarconDef> }
let cfg = Toml.ToModel<Config>(File.ReadAllText configPath)
```

Tomlyn is the canonical .NET TOML library ([xoofx/Tomlyn](https://github.com/xoofx/Tomlyn)).

### 9.6 Validation (Stage 9)

- `echo -e "\x1b[31mError\x1b[0m"` → alarm earcon plays before/with the announcement.
- `echo -e "\x1b[32mDone\x1b[0m"` → confirm earcon.
- `Ctrl+Shift+M` → earcons mute. Run the same command → no earcon, but NVDA still reads.
- Stress test: rapid color changes do not produce audio dropouts or clicks.

-----

## Stage 10 — Review mode + structured navigation

**Goal.** A “browse mode” similar to NVDA’s web browse mode, but at the terminal level: keys navigate the document instead of going to the PTY. Quick-nav letters jump to errors / warnings / commands / interactive elements.

### 10.1 Mode toggle

Hotkey `Alt+Shift+R` toggles between Interactive mode (keys → PTY, current default) and Review mode (keys → review cursor). Announce the mode change via Notification event with `NotificationProcessing.MostRecent` so NVDA preempts.

### 10.2 Quick-nav letters (only in Review mode)

|Key            |Jumps to                                                                               |
|---------------|---------------------------------------------------------------------------------------|
|`e` / `Shift+e`|Next/prev error (red text run, length ≥ 3 chars)                                       |
|`w` / `Shift+w`|Next/prev warning (yellow text run)                                                    |
|`c` / `Shift+c`|Next/prev command (line starting with `$`, `>`, or after detected prompt)              |
|`o` / `Shift+o`|Next/prev output block (separator: 2+ blank lines, or alt-screen entry/exit)           |
|`i` / `Shift+i`|Next/prev interactive element (currently-shown listbox)                                |
|`p` / `Shift+p`|Next/prev unfilled prompt (input field detected by cursor stability + visible question)|

When focus reaches an item, build a snapshot range, raise `RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged)`, and `RaiseNotificationEvent(...MostRecent...)` with the line text plus a prefix (“error:”, “warning:”, “prompt:”).

### 10.3 Color-based detection (no semantic interpretation)

- Error: any cell run where `Fg` resolves to red or bright red, or where Bg is red. We do **not** attempt to parse “ERROR:” or stack traces; pure visual heuristic.
- Warning: yellow.
- Output block boundaries: detect via VT events — `ED` (clear screen), `?1049` (alt screen toggle), or 2+ consecutive blank rows.

### 10.4 Validation (Stage 10)

- Run `python -c "raise ValueError('boom')"`. Press `Alt+Shift+R` then `e`. NVDA reads the red traceback line(s).
- After a long Claude response, press `Alt+Shift+R`, then `c`, then `c` to jump back through commands.
- Press `Alt+Shift+R` again → exits review mode, focus returns to PTY input.

### 10.5 Failure modes

- Heuristic false positives (e.g., red-on-red ASCII art) → make the threshold configurable; require a minimum 3-character run.
- Review mode “sticks” because mode toggle key was eaten by PTY → bind the toggle in `PreviewKeyDown` (handle BEFORE PTY translation).

-----

## Stage 11 — Velopack auto-update

**Goal.** Ship to GitHub Releases. User presses `Ctrl+Shift+U`; app checks for updates; reports progress audibly via UIA LiveRegion; restarts on apply.

### 11.1 Velopack from F#

Velopack works fine from F# even though docs are C# ([Velopack docs](https://docs.velopack.io/getting-started/csharp); NuGet listing notes “#r directive can be used in F# Interactive” — [NuGet Velopack](https://www.nuget.org/packages/Velopack/)). In your `Main`:

```fsharp
[<STAThread; EntryPoint>]
let main argv =
    VelopackApp.Build().Run()      // must run first; may exit/restart for hooks
    let app = App()
    app.InitializeComponent()
    app.Run()
```

### 11.2 Update flow

```fsharp
let mgr = UpdateManager(GithubSource("https://github.com/USER/REPO", null, prerelease = false))
let info = mgr.CheckForUpdatesAsync().Result
match info with
| null -> announce "Already up to date"
| upd ->
    announce (sprintf "Update %s available, downloading..." upd.TargetFullRelease.Version)
    mgr.DownloadUpdatesAsync(upd, progress = Action<int>(fun pct -> announce (sprintf "%d percent" pct))).Wait()
    announce "Restarting"
    mgr.ApplyUpdatesAndRestart(upd)
```

`ApplyUpdatesAndRestart` exits the process and the Velopack updater takes over. Bind to `Ctrl+Shift+U`.

### 11.3 Progress announcement via LiveRegion

For *progress*, a `LiveRegion` is more appropriate than Notification. WPF supports it via `AutomationProperties.LiveSetting` ([Microsoft Learn: UIA Notification event](https://learn.microsoft.com/en-us/archive/blogs/winuiautomation/can-your-desktop-app-leverage-the-new-uia-notification-event-in-order-to-have-narrator-say-exactly-what-your-customers-need)). For the MVP, simpler: just call `RaiseNotificationEvent(NotificationKind.ActionInProgress, NotificationProcessing.MostRecent, "...percent", "update")`.

### 11.4 GitHub Releases CI

Velopack’s GitHub Actions guidance ([docs.velopack.io GitHub Actions](https://docs.velopack.io/distributing/github-actions)) is the canonical pipeline. SignPath integration is via signing your published exe before `vpk pack`. Velopack’s reputation: “Updates apply and relaunch in ~2 seconds with no UAC prompts” ([Velopack README](https://github.com/velopack/velopack)).

### 11.5 Velopack pitfalls

- **Application requiring elevation is unsupported** ([Velopack discussions #8](https://github.com/velopack/velopack.docs/discussions/8)) — keep the manifest at `asInvoker`.
- **Endless restart loop** if `VelopackApp.Build().Run()` is forgotten ([issue #195](https://github.com/velopack/velopack/issues/195)).
- **Signed-vs-unsigned**: Velopack’s installer is signed by you (via SignPath in your build); the Velopack updater binary itself is signed by the project. If your code-signing certificate is missing, Windows SmartScreen will prompt on first install. Always test the installer flow on a clean VM.

### 11.6 Validation (Stage 11)

- Cut version 0.1.0 → install. Verify NVDA announces app version on startup.
- Cut version 0.1.1 → publish. In running 0.1.0, press `Ctrl+Shift+U`. NVDA: “Update 0.1.1 available, downloading… 25 percent… 100 percent… restarting.” App restarts as 0.1.1.

-----

## Validation Methodology Quick Reference

For each stage:

|Stage|Observable outcome           |NVDA test                  |Diagnostic if fails                                  |Programmatic verification  |
|-----|-----------------------------|---------------------------|-----------------------------------------------------|---------------------------|
|1    |`dir` output appears in Debug|n/a                        |`GetLastError`, check struct size, drain handles     |None                       |
|2    |All golden tests pass        |n/a                        |Print state transitions; compare to vt100.net diagram|xUnit + FsCheck            |
|3    |Type at cmd, see chars       |n/a                        |Re-render frame in WPF Snoop                         |Visual                     |
|4    |Review cursor reads buffer   |NVDA+Numpad7/8/9           |Inspect.exe shows Document + Text pattern            |Inspect.exe, FlaUI         |
|5    |NVDA announces output        |echo, dir, busy loop       |Event Tracker add-on shows Notification              |AccEvent.exe, Event Tracker|
|6    |Type into PowerShell         |NVDA reads dir listing     |KeyDown logging, NumLock state                       |Manual + FlaUI             |
|7    |Claude responds              |NVDA reads response        |Trace VT bytes, check TERM env                       |Manual                     |
|8    |NVDA reads listbox           |Down arrow advances        |Inspect.exe shows List + ListItem                    |Inspect.exe                |
|9    |Earcons play on color        |Hear alarm/confirm         |NAudio device check                                  |Manual                     |
|10   |Quick-nav jumps              |Press `e` for next error   |Mode indicator announce                              |Manual                     |
|11   |Update completes             |NVDA announces “restarting”|Velopack log files                                   |Manual                     |

**Tools** ([Microsoft Learn: Accessibility testing](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-testing)):

- **Accessibility Insights for Windows** (recommended modern tool).
- **Inspect.exe** — `C:\Program Files (x86)\Windows Kits\10\bin\<ver>\<arch>\Inspect.exe` ([docs](https://learn.microsoft.com/en-us/windows/win32/winauto/inspect-objects)).
- **AccEvent.exe** — same folder ([docs](https://learn.microsoft.com/en-us/windows/win32/winauto/accessible-event-watcher)).
- **NVDA Event Tracker add-on** for confirming Notification events ([addons.nvda-project.org](https://addons.nvda-project.org/addons/evtTracker.en.html)).
- **FlaUI / Gu.Wpf.UiAutomation** for automated UIA tests ([FlaUI](https://github.com/FlaUI/FlaUI), [Gu.Wpf.UiAutomation](https://github.com/GuOrg/Gu.Wpf.UiAutomation)).

-----

## Consolidated Pitfalls Checklist

1. **ConPTY**: don’t forget to close `inputReadSide` and `outputWriteSide` in the parent after `CreateProcess`; double-call `InitializeProcThreadAttributeList`; use separate threads for read/write; drain output before `ClosePseudoConsole` ([microsoft Q&A on CREATE_NEW_PROCESS_GROUP](https://learn.microsoft.com/en-ca/answers/questions/5832200/), [discussion #17716](https://github.com/microsoft/terminal/discussions/17716)).
1. **WPF UIA thread affinity**: `RaiseNotificationEvent` and `RaiseAutomationEvent` must run on the Dispatcher thread (the thread that created the peer); always wrap in `Dispatcher.BeginInvoke` ([TermControlAutomationPeer.cpp pattern](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControlAutomationPeer.cpp)).
1. **NVDA double-announcement**: never raise both TextChanged and Notification for the same content — pick Notification ([nvaccess PR #13261](https://github.com/nvaccess/nvda/pull/13261), [issue #13781](https://github.com/nvaccess/nvda/issues/13781)).
1. **Spinner loops**: dedup row content by hash; suppress same-row flushes within 1 s ([nvaccess discussion of high-volume terminal events #11002](https://github.com/nvaccess/nvda/issues/11002) referenced in PR #14047).
1. **TPL Dataflow backpressure**: BroadcastBlock drops messages under load — use bounded Channels with DropOldest for announce path ([SO/MSDN forums on guaranteed delivery](https://qa.social.msdn.microsoft.com/Forums/en-US/69f65dd4-f802-4cb6-afbb-e0309f939fe9/)).
1. **F# P/Invoke**: structs must be `LayoutKind.Sequential`, fields `mutable`, exact field order; pass `STARTUPINFOEX` by `&` (byref); use `SafeFileHandle` for pipe ends.
1. **Velopack signed-vs-unsigned**: cert must be available at pack time; otherwise SmartScreen blocks on install. Test installer in clean VM. Don’t request elevation.
1. **WPF input**: use `TextInput` for chars (not `KeyDown`) to support IME and dead keys ([dotnet/wpf #8217](https://github.com/dotnet/wpf/discussions/8217)).
1. **Don’t swallow NVDA keys**: never set `e.Handled = true` in PreviewKeyDown for Insert / CapsLock / Numpad-with-NumLock-off; let WPF bubble them so NVDA’s hooks fire.
1. **Alt-screen scrollback**: do not retain scrollback while in alternate screen; switch buffers atomically with `?1049` ([daintree #1490 conventions](https://github.com/daintreehq/daintree/issues/1490)).

-----

## Reference Code Map

When Claude Code implements this, point it at these specific files:

- ConPTY P/Invoke layer: [microsoft/terminal/samples/ConPTY/MiniTerm/MiniTerm/Native/PseudoConsoleApi.cs](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/Native/PseudoConsoleApi.cs); full sample [PseudoConsole.cs](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/PseudoConsole.cs); driver [Program.cs](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/MiniTerm/MiniTerm/Program.cs).
- C++ canonical: [EchoCon.cpp](https://github.com/microsoft/terminal/blob/main/samples/ConPTY/EchoCon/EchoCon/EchoCon.cpp).
- VT parser: Williams diagram at [vt100.net](https://vt100.net/emu/dec_ansi_parser.html); reference impl [alacritty/vte src/lib.rs](https://github.com/alacritty/vte/blob/master/src/lib.rs).
- UIA peer for terminal: [microsoft/terminal TermControlAutomationPeer.cpp](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControlAutomationPeer.cpp); WPF variant in PR [#14097](https://github.com/microsoft/terminal/pull/14097).
- NVDA terminal handling: [nvaccess/nvda winConsoleUIA.py](https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/winConsoleUIA.py); Notification migration [PR #14047](https://github.com/nvaccess/nvda/pull/14047).
- Input translation: [microsoft/terminal terminalInput.cpp](https://github.com/microsoft/terminal/blob/main/src/terminal/input/terminalInput.cpp); xterm canonical [invisible-island ctlseqs](https://www.invisible-island.net/xterm/ctlseqs/ctlseqs.html).
- NAudio: [WasapiOut docs](https://github.com/naudio/NAudio/blob/master/Docs/WasapiOut.md); Mark Heath’s [sine wave generator](https://markheath.net/post/playback-of-sine-wave-in-naudio).
- Velopack: [velopack/velopack](https://github.com/velopack/velopack); [docs csharp.mdx](https://github.com/velopack/velopack.docs/blob/master/docs/getting-started/csharp.mdx).
- FlaUI: [FlaUI/FlaUI](https://github.com/FlaUI/FlaUI); [Gu.Wpf.UiAutomation](https://github.com/GuOrg/Gu.Wpf.UiAutomation).
- xUnit + FsCheck.Xunit: [xunit/xunit](https://github.com/xunit/xunit); [fscheck/FsCheck](https://github.com/fscheck/FsCheck); [`[<Property>]` attribute docs](https://fscheck.github.io/FsCheck/RunningTests.html#Using-FsCheck-Xunit); [intro on F# for fun and profit](https://swlaschin.gitbooks.io/fsharpforfunandprofit/content/posts/property-based-testing.html).
- Tomlyn (future, Phase 2 verbosity profiles): [xoofx/Tomlyn](https://github.com/xoofx/Tomlyn).

-----

## Closing Note on Sequencing

This sequencing is deliberately **infrastructure-first / features-second** — Stages 0–4 build the entire vertical slice (build pipeline, ConPTY, parser, screen model, UIA exposure) before any feature beyond “type into cmd, hear it via NVDA.” That is exactly the walking-skeleton discipline ([Code Climate on walking skeletons](https://codeclimate.com/blog/kickstart-your-next-project-with-a-walking-skeleton)): every stage from 5 onward is a *feature* that you can independently ship and (most importantly for a blind developer) independently validate via NVDA. If Stage 7 fails (Claude Code doesn’t run) you only need to debug environment/launch — not also ConPTY, the parser, the buffer, or the UIA layer, because each of those was already validated.

The single biggest risk in the schedule is the Stage 4 + 5 boundary (UIA exposure + streaming announcements). Microsoft and NVDA’s collective lessons learned over five years of Windows Terminal accessibility work ([nvaccess/nvda Improving the console experience with UI Automation](https://github.com/nvaccess/nvda/wiki/Improving-the-console-experience-with-UI-Automation), [issue #11002 on UIA freezes](https://github.com/nvaccess/nvda/issues/11002), [PR #14047 prototype](https://github.com/nvaccess/nvda/pull/14047)) all converge on one conclusion: **emit Notification events, not TextChanged events; debounce on line boundaries; and never swallow NVDA’s modifier keys.** If your Phase 1 honors those three rules, the user can drive Claude Code with NVDA.