## Architectural Tradeoffs for a Screen‑Reader‑First, Audio‑Native Interface to Windows Shells and TUIs

The question you are circling — compose‑then‑send notebook semantics versus character‑by‑character PTY pass‑through — is, at bottom, a question about *where the terminal abstraction lives*. In the notebook model, the terminal effectively does not exist; the user types into an accessible widget and a backend marshals a request/reply. In the pass‑through model, your program *is* a terminal emulator with an a11y skin attached. Almost every interesting tradeoff downstream — Read‑Host, vim, Ctrl‑C, tab completion, htop, the Claude Code interactive UI, progress bars — is a consequence of that single architectural choice colliding with the fact that on Windows the “terminal” abstraction is a relatively recent (and still leaky) standardization layer.

What follows is an attempt to lay out the mechanics dispassionately, with the Windows‑specific details called out where they matter most. The treatment assumes you already know what a PTY, a stream, an event loop, and a screen reader are; the goal is to map the moving parts to each other so the design space becomes legible.

## 1. Windows PTY / ConPTY internals

### The legacy console: an API server, not a stream

Until Windows 10 1809 (October 2018),  the Windows console was fundamentally unlike a Unix terminal. A console application linked against `kernel32`/`KernelBase` would call `WriteConsoleOutput`, `WriteConsoleOutputCharacter`, `SetConsoleCursorPosition`, `FillConsoleOutputAttribute`, `ReadConsoleInput`, and friends. These were not byte streams; they were *remote procedure calls* into the console host process (`conhost.exe`), which owned a structured screen buffer (a 2‑D array of `CHAR_INFO` cells containing UTF‑16 code units plus attribute words) and emitted `INPUT_RECORD` structures (keyboard events, mouse events, focus events, buffer‑resize events) on the input side. There was no “tty” between the application and the terminal; the application was driving the GUI almost directly, through an LPC channel. There were no escape sequences over the wire — color changes and cursor moves were function calls.

That history matters because it shaped both how programs are written for Windows and what accessibility tooling has had to do. NVDA’s “legacy” console support hooks `conhost`’s screen buffer through a combination of in‑process injection and direct buffer reads; “what is on screen” was, until recently, literally a `CHAR_INFO` grid that a screen reader could inspect.

### ConPTY: a Unix‑PTY‑shaped adapter glued onto that machine

`CreatePseudoConsole`, introduced in 1809, did not replace the legacy machine; it wrapped it. The public surface is small: `CreatePseudoConsole(size, hInput, hOutput, dwFlags, &hPC)`, `ResizePseudoConsole`, `ClosePseudoConsole`, plus the `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` attribute you attach to a `STARTUPINFOEX` so that a `CreateProcessW` call inherits the new pseudoconsole as its console.  `hInput` and `hOutput` are caller‑provided synchronous handles — anonymous pipes in the common case — and are explicitly documented as not supporting `OVERLAPPED` I/O.  The handles you pass in are the *host’s* side: bytes you `WriteFile` into `hInput` are delivered to the child as console input; bytes the child produces appear on `hOutput` for you to `ReadFile`.

Architecturally, calling `CreatePseudoConsole` spawns (or reuses) an instance of `conhost.exe` — built from the same source tree as `OpenConsole.exe`, which is the open‑source build shipped inside Windows Terminal and used as its private conhost. This conhost runs *headless*: no window, no GDI rendering. It still services every legacy console API call the child makes (`WriteConsoleOutput`, `SetConsoleCursorPosition`, etc.) into an in‑memory `CHAR_INFO` buffer. A “renderer” component in conhost — internally called the VT renderer — then diffs that buffer against its last transmitted state and emits virtual terminal sequences (CSI, OSC, SGR, cursor positioning, scroll regions, alternate screen buffer) onto `hOutput`.  On the input side, the host translates VT input it receives on `hInput` (and a Microsoft‑invented `WIN32-INPUT-MODE` envelope that smuggles full `KEY_EVENT_RECORD`s — modifier flags, scan codes, key‑up/key‑down — over the wire when negotiated) back into `INPUT_RECORD`s that the child sees through `ReadConsoleInput`. 

The practical consequences of this design are subtle and important for your problem:

1. **What you receive on `hOutput` is not what the child wrote; it is a re‑serialization.** A program that calls `WriteConsole("hello\n")` will, after ConPTY processing, appear on your pipe as roughly `\x1b[?25l\x1b[2J\x1b[m\x1b[Hhello\r\n…` plus an OSC 0 title and other housekeeping.  The pseudoconsole’s job is to make the child *look like* it is talking to a VT terminal regardless of what API surface it actually used. There is no flag to suppress this and emit only “what the child sent”; the simulated environment is the whole point.  (See `microsoft/terminal#2035` for the canonical discussion of why this surprises people.)
1. **A child that uses only the legacy Console API still produces VT output through ConPTY.** This is how Windows finally gives you a unified abstraction: cmd.exe, a 1990s‑era curses‑equivalent app, a modern UTF‑8/VT‑emitting app, and PowerShell all end up looking the same to the host. But it means *you* must parse VT to know what is on screen; you cannot fall back to reading the conhost buffer because that conhost is hidden inside the ConPTY machinery and there is no documented way to ask it for its `CHAR_INFO` grid.
1. **Line‑buffered vs raw / cooked is collapsed.** ConPTY’s child sees an ordinary console with the usual `ENABLE_LINE_INPUT`/`ENABLE_ECHO_INPUT`/`ENABLE_PROCESSED_INPUT` modes available through `SetConsoleMode` on `CONIN$`. From the host’s side this matters because line editing (backspace handling, “press Enter to submit a line”) happens *inside conhost*, not in your code. If the child sets `ENABLE_VIRTUAL_TERMINAL_INPUT` (which most modern shells and TUIs do), individual keystrokes are forwarded as VT sequences; otherwise, the conhost cooks the line and gives the child a single string on Enter. From the perspective of your accessibility frontend, you are always writing bytes into a pipe; the cooking happens behind the curtain.
1. **`WriteConsoleInput` vs writing to the input pipe.** Inside a child process or a host attached to a real conhost, `WriteConsoleInput` is the API to synthetically inject `INPUT_RECORD`s into the queue your own process will read. From outside the child — from the ConPTY host — you do *not* call `WriteConsoleInput`; you `WriteFile` bytes onto the input pipe, and conhost translates them. The distinction is exactly the *NIX distinction between writing into the master side of a `pty(7)` versus calling `ioctl(TIOCSTI)`; it is worth keeping straight in your head because much older Windows accessibility code (and projects like `winpty` from before 1809) used in‑process injection and screen‑buffer scraping rather than this stream model.
1. **Applications that “bypass standard streams” mostly do not, on ConPTY.** Programs that write directly to the screen buffer with `WriteConsoleOutput` (Far Manager, classical curses‑on‑Windows ports, older installers) are handled fine: conhost is still the server, it still owns the buffer, and the VT renderer turns those grid updates into escape sequences. What ConPTY does *not* support gracefully is programs that try to obtain a handle to the console window (`GetConsoleWindow`) and manipulate it with `SendMessage`/`SetWindowText` — there is no window. And it does not support programs that probe for a *real* screen reader by querying console window handles. In practice almost nothing does this; the meaningful “bypass” case is direct buffer writes, which ConPTY handles.

### OpenConsole vs conhost vs Windows Terminal vs pseudoconsole

These four words name *one* code base in different deployment shapes. The Microsoft `terminal` repository builds:

- **`OpenConsole.exe`** — the open‑source build of the console host. When deployed inside Windows Terminal it ships under that name; when inboxed in Windows it is renamed to `conhost.exe`. Windows Terminal launches its own `OpenConsole.exe` as the headless ConPTY host rather than relying on the system `conhost.exe`, so it always gets a recent build.
- **`conhost.exe`** — the inbox copy of the same code with `User32` GUI bits still active. In its classical role it both services the console API and draws the window. In its ConPTY role it services the API and emits VT through a pipe. The choice is made by which `CreateProcess` path was taken and whether `STARTUPINFOEX` carries an `HPCON`.
- **Pseudoconsole** — the API surface and the role conhost plays when there is no GUI: a “server only, no terminal”. The `ecosystem-roadmap` page on `learn.microsoft.com` is explicit that the strategic direction is to retire the classical conhost UI and have Windows Terminal sit on top of a pseudoconsole always.
- **Windows Terminal** — the WinUI XAML application: tabs, themes, settings, a custom DirectWrite renderer (Atlas engine), and a UIA provider for the text grid. It is a *consumer* of ConPTY, not part of it.

For your purposes — building an accessibility frontend — the implication is that you have effectively two viable host strategies. Either you call `CreatePseudoConsole` yourself and own the VT stream (the same posture Windows Terminal takes), in which case you have all the data and all the responsibility; or you let Windows Terminal own the PTY and consume its content through UIA, in which case you are dependent on whatever Windows Terminal’s UIA provider chooses to expose.

## 2. Why compose‑then‑send cannot cleanly host interactive content

A “compose then send” frontend implicitly assumes the shell’s interaction model is *request/response over text*: I submit a string, the shell runs it, I receive a string back. That assumption holds for an enormous portion of routine command‑line work — `ls`, `git status`, `dotnet build`, `python -c …`, `Get-ChildItem`, almost everything in a non‑interactive script — and for those cases the notebook model is dramatically better for a screen reader than a streaming terminal could ever be. The problems begin when one of the following four things happens, and they cannot be designed away without rebuilding part of the terminal.

### 2.1 Mid‑execution prompts: stdin during a running command

Many commands ask for input while running, *after* the user has already submitted the top‑level line. `sudo` (on WSL) wants a password; `apt` wants a Y/N; `npm init` walks through a wizard; `git commit` without `-m` will spawn an editor; the Python REPL inside `python` will sit there forever; `Read-Host` and `Read-Host -AsSecureString` block reading from `CONIN$`; `bash`’s `read` blocks on stdin; even something as mundane as `more` waits for a keypress per page. None of these are “TUI”; they are line‑oriented programs that simply have additional stdin reads beyond the first.

In a compose‑then‑send model, the only honest answer is *the kernel needs a side channel to ask*. That is precisely what Jupyter does, and it is worth dwelling on it, because Jupyter is the only mainstream notebook system that has actually solved this problem and the way it solved it tells you what is solvable and what is not. The Jupyter messaging spec has a dedicated `stdin` ROUTER/DEALER ZeroMQ socket on which the *kernel* originates requests: `input_request` carries a `prompt` and a `password` boolean; the frontend displays whatever UI it wants and sends back an `input_reply` whose `value` field becomes the result of `input()` in Python, `readline()` in R, and so on. Crucially, an `execute_request` carries an `allow_stdin` flag; if a frontend cannot support stdin requests it sets `allow_stdin: false`, and then a well‑behaved kernel *must not* issue `input_request` and is expected to raise on `input()` calls. The pattern is clean because the kernel is cooperating — it is calling a Jupyter‑aware `input()` that knows to route over the message bus.

There is no analog for arbitrary shell commands. `bash`’s `read` does not know about your notebook; it `read(2)`s from file descriptor 0. `Read-Host` does not consult a side channel; it reads `CONIN$`. The compose‑then‑send model can therefore solve mid‑execution prompts in essentially one of three ways, all imperfect:

- **Run the child against a real PTY anyway, and watch for “the child is reading but has not produced output for N ms”.** Then surface an inline edit field, take the user’s string, and `WriteFile` it onto the PTY input pipe with a `\r`. This works in practice but the heuristic is fragile — a child can read stdin while still producing output, can prompt without flushing, and can prompt without ever printing a recognizable prompt token.
- **Pattern‑match prompts.** Look for trailing `: `, `? `, `[y/N]`, lines without a newline, or particular substrings; the entire CLI‑automation ecosystem (`expect`, `pexpect`, `Posh-SSH`’s expect cmdlets) does this. It works for common cases and fails on novel ones; you cannot guarantee correctness across “all shells and all TUIs”.
- **Refuse to support it.** Run the command with `</dev/null` (or `< NUL` on Windows) so reads return EOF; if the program needs input it will fail. This is honest but unhelpful; it is also exactly what Jupyter does with `allow_stdin=False`.

### 2.2 `isatty()` and the program changes behavior

Programs routinely check whether their standard streams are connected to a terminal and switch behavior accordingly. On POSIX this is `isatty(fileno(stdin))`. On Windows the canonical idiom is `GetFileType(GetStdHandle(STD_INPUT_HANDLE)) == FILE_TYPE_CHAR`, which the CRT’s `_isatty(_fileno(stdin))` wraps; some programs additionally call `GetConsoleMode` and treat a non‑error return as “this is a console”. The MSYS2/Cygwin world complicates things — `mintty` uses a Windows *named pipe* rather than a console, so `GetFileType` returns `FILE_TYPE_PIPE` and the program thinks stdin is a pipe; Cygwin has internal heuristics inspecting the pipe name in the NT object namespace to recover an `isatty` answer. None of this is your problem directly, but it shapes what happens when you feed a command through a pipe instead of a pseudoconsole:

- `git log` paged into `less` when output is a tty; emits raw text and exits when stdout is a pipe.
- `ls --color=auto` colors only on a tty.
- `python` drops into the REPL on a tty; runs the file/expression and exits on a pipe.
- `node` likewise.
- `apt` shows a progress bar on a tty; emits log lines on a pipe.
- `npm install` produces a fancy spinner on a tty; produces JSON‑lines‑ish output on a pipe.
- PowerShell formats `Get-Process` as a table on a tty; as a stream of objects (effectively coerced via `Out-Default`) that may behave differently when host capability differs.

If you build a compose‑then‑send frontend that runs commands with their stdin/stdout as plain pipes — the natural implementation — many of these programs will degrade gracefully (which is what you want, mostly) but some will behave surprisingly. The pure‑pipe approach also breaks anything that uses ANSI input sequences for keys (arrows, Home/End), because there is no tty to deliver them on.

If, alternatively, your “compose then send” backend runs commands through a ConPTY so `isatty()` is true, then the very moment a program *behaves like a tty program*, you inherit the pass‑through problem you were trying to avoid: the program may emit color, cursor positioning, alt‑screen sequences, progress redraws, and so on. You now have to either parse VT in order to flatten it into a sane text rendering, or strip it and pretend you didn’t see it (which loses meaningful information — a progress bar isn’t an error, but it isn’t a static value either).

### 2.3 Full TUIs: alternate screen, raw mode, and keystroke semantics

Anything in the class of vim, nano, emacs ‑nw, htop, btop, tmux, fzf, less in its default mode, lazygit, gh’s interactive flows, k9s, ranger, mc, ncdu, Claude Code’s interactive UI, the Python debugger’s screen, `aws configure` in some versions, and most modern interactive installers (Ink/Rust‑ratatui/Bubble Tea apps) does at least three things at startup:

1. **Switch to the alternate screen buffer** (`\x1b[?1049h` — the DEC private mode “alternate buffer with cursor save”). This means the program is no longer appending to scrollback; it is drawing on a separate screen that will be torn down when the program exits, restoring the previous content. There is no semantically useful “transcript” of a TUI session; what was on screen at exit is gone by design.
1. **Put the terminal in raw mode** so individual keystrokes — not lines — are delivered, characters are not echoed by the terminal, and ^C is delivered as the byte `0x03` rather than generating a signal. On Windows, this is the combination of clearing `ENABLE_LINE_INPUT`, `ENABLE_ECHO_INPUT`, and `ENABLE_PROCESSED_INPUT` and setting `ENABLE_VIRTUAL_TERMINAL_INPUT`, or, in xterm‑style programs that came in through ConPTY, the program is sending DECSET sequences that affect the host’s interpretation.
1. **Take over the rendering loop.** Output is no longer “lines”; it is cursor positioning sequences (`CSI row;col H`), erasures (`CSI K`, `CSI J`), and selective rewrites. Reading vim’s stdout will give you something like “move to row 24, write the status line, move to row 5 column 7, write a character, move the cursor back”.

A compose‑then‑send model cannot host these. There is no string to submit and no string to receive; the very concept of “the output” is meaningless, because the program *is* the screen. You can technically pipe a command list to vim with `vim -e -s` (ex mode) or to fzf with stdin lines and read its stdout — but you have stopped using the program interactively; you are scripting it.

The honest options are:

- **Detect and decline.** Recognize that a TUI is launching and refuse, advising the user to run it some other way. The detection is itself not fully solvable (see §3), but it can be made to work for the common cases.
- **Detect and switch modes.** Recognize the alt‑screen transition and, for the lifetime of that program, become a terminal emulator with screen‑reader integration. This is the hybrid approach, and it forces you to build essentially everything a pass‑through model needs (§11).
- **Be a terminal emulator all the time** and accept that your “compose” mode is an overlay you draw on top.

### 2.4 `Read-Host -AsSecureString` and password prompts specifically

`Read-Host -AsSecureString` in PowerShell, `getpass.getpass()` in Python, `sudo`‘s `askpass` in WSL, OpenSSH client password prompts, `git credential helper` prompts — these are the worst case for a notebook model because they need three things simultaneously: (a) the input must be read from the user, (b) the input must not be echoed back to the screen (because a screen reader cannot un‑say what it has said and because shoulder‑surfing risk exists), and (c) on Windows, `Read-Host -AsSecureString` specifically requires that the bytes be written into a `SecureString` rather than transiting through a normal string in memory, which means PowerShell really wants to read them via its own host’s `ReadLineAsSecureString` rather than via an arbitrary pipe.

In a ConPTY host, the child reads the password as ordinary console input with `ENABLE_ECHO_INPUT` disabled and stores it in a SecureString itself; the host just sees that the program is reading and not producing output, exactly like any other prompt. Your accessibility frontend can do the right thing if it cooperates: present an edit field whose contents are not voiced (NVDA does this for password fields automatically when role/state is set correctly, and the Jupyter `input_request` `password: true` flag exists precisely for this), then write the bytes onto the PTY input pipe followed by `\r`. The notebook model handles this *better* than a streaming terminal in many ways, because you can use a real Win32 password edit control (whose UIA `IsPassword` property NVDA respects) instead of relying on the terminal not echoing.

What you cannot do in pure compose‑then‑send is *recognize* that the program is asking for a password without either pattern matching on the prompt string (“password:”, “passphrase:”, “PIN:”) or running the child against a PTY and observing that echo has been disabled. The latter is a clean signal — `ENABLE_ECHO_INPUT` cleared on a console input mode is a strong hint, and the OSC 8 / DEC mode flags around `Read-Password` requests are less universal — but again it requires you to be in the PTY‑middle role.

### 2.5 Pipes vs ttys on Windows: what actually changes

Concretely, on Windows, when stdin is a pipe rather than a console:

- `GetFileType(GetStdHandle(STD_INPUT_HANDLE))` returns `FILE_TYPE_PIPE` instead of `FILE_TYPE_CHAR`. CRT `_isatty` returns 0.
- `GetConsoleMode` fails with `ERROR_INVALID_HANDLE`; programs use this as a tty test.
- `ReadConsole`/`ReadConsoleInput` fail; only `ReadFile` works.
- There is no line editing — no cooked mode, no backspace handling — so a program that expects to call `ReadConsole` and get a fully edited line per Enter has to do its own editing.
- `Read-Host` in PowerShell still works because it tries `Console.ReadLine`, but `Read-Host -AsSecureString` reads byte‑by‑byte to disable echo, and on a pipe it cannot disable echo (echo is irrelevant on a pipe) — its behavior in scripts has historically been to fail or to read the line in plaintext, depending on PowerShell version.
- No keyboard “special keys”: arrows, F1‑F12, Home/End cannot be sent unless the program is parsing VT sequences and you send them as escape codes.
- Ctrl‑C: there is no console attached, so the only way to interrupt is to close the pipe or send a signal via `GenerateConsoleCtrlEvent` to a process *group*. The `^C sent to stdin` Q&A on `learn.microsoft.com` is illustrative: even with ConPTY, sending the byte `0x03` does not become a signal for the child if `CREATE_NEW_PROCESS_GROUP` was used, because conhost’s Ctrl‑C handling involves the process group identity. 

The asymmetry here is fundamental: when stdin is a pipe, you have less expressive power than when it is a tty, and many programs degrade. When stdin is a tty (ConPTY), you have full expressive power but you have *also* taken responsibility for the entire terminal protocol.

## 3. The hybrid middle ground

There are several places to draw the dividing line, and they each have a characteristic failure mode.

### 3.1 “Always PTY, but only forward keystrokes when needed”

This is the cleanest hybrid in principle. You always run children under a ConPTY; you always parse the VT stream into a screen state. But you do not put the user’s keystrokes onto the PTY input pipe unless the program is doing something that requires real‑time input. Most of the time you sit in “compose” mode: the user types into your accessible buffer, sees their command, edits it freely, and on Enter you write `command\r` to the PTY in one shot. While the command runs, output streams in and you announce it (with whatever audio/diff strategy you choose; see §4 and §6). When the program exits, the prompt reappears and you return to compose mode.

The question is what “the program is doing something that requires real‑time input” *means* operationally. A few signals you can use, none of them perfect on their own:

- **Alternate screen buffer entry** (`\x1b[?1049h`, `\x1b[?47h`, `\x1b[?1047h`). This is a strong signal — practically every full‑screen TUI sets it within milliseconds of startup. When you see it, switch to pass‑through; when you see the matching `?1049l`, switch back. This catches vim, htop, fzf, less (default mode), nano (in some terminfo configurations), tmux, and the great majority of ratatui/Bubble Tea/Ink apps.
- **DECSET cursor‑key and keypad modes** (`?1h`, `=`/`>`), and **mouse modes** (`?1000h`, `?1003h`, `?1006h`). A program enabling mouse tracking is going to consume real‑time input.
- **Bracketed paste enabled** (`?2004h`). On its own this is a weak signal — readline/PSReadLine enable it during line editing — but combined with other indicators it suggests interactivity.
- **Echo disabled.** If the child clears `ENABLE_ECHO_INPUT` on its `CONIN$` (visible to you because conhost will stop echoing input back on `hOutput`), there is probably a password prompt.
- **The program is blocked reading from stdin and has not produced output for some interval.** A heuristic for “the command finished and produced a prompt” vs “the command is waiting for input” — what `expect` and `pexpect` do.
- **Known program names** (`vim`, `nano`, `less`, `fzf`, `htop`, `top`, `claude`, `python` in REPL mode, `node`, `ipython`). Spawning by name is the easiest signal of all; “the user typed `vim foo.py`” tells you what is coming. The cost is that you maintain a list, and aliases / shell functions / wrappers will defeat you.

The honest summary is that *no signal is reliable in advance*, but several signals are reliable *very early*. A practical approach is “optimistically compose; on the first byte of evidence that the child is in TUI mode, switch to pass‑through within the same session, having lost no information because you have the entire VT stream buffered.” The user experience is then: line‑oriented commands feel like a notebook, full TUIs feel like a terminal, and the transition is automatic. This is roughly what TDSR does on Mac/Linux, what Emacspeak’s term‑mode does on Linux, and what an NVDA‑side implementation would have to do on Windows.

### 3.2 “Headless / non‑interactive flags”

Many programs offer non‑interactive variants. `git --no-pager log`, `apt-get -y`, `npm config set ... --silent`, `python -c …` instead of REPL, `pip --no-input`, `claude -p "prompt"` instead of the interactive UI, `ssh -T`, `aws configure --profile … --no-cli-pager`. There is also `TERM=dumb`, which forces many readline‑based and ncurses‑based programs into a degraded mode that does not redraw; `PAGER=cat`; `CI=true`, which a surprising number of tools (npm, Docker, GitHub CLI, hatch) treat as “no spinners, no progress bars, plain output”; and `NO_COLOR=1`, an emerging convention.

These are real and useful — for a notebook backend, setting `TERM=dumb`, `PAGER=cat`, `CI=true`, `NO_COLOR=1`, `PYTHONUNBUFFERED=1`, `FORCE_COLOR=0`, and providing a `stdin=NUL` by default will eliminate enormous amounts of visual noise and make many commands “compose then send” cleanly. The limits are:

- Not every tool honors these. `claude` interactive will still try to draw a full TUI; `htop` does not have a non‑interactive mode by design; vim’s `-e -s` ex/silent mode is a different program for practical purposes.
- These flags trade interactivity for batch behavior. The user has to know which flag for which tool, which is exactly the burden you were trying to remove.
- Some flags change semantics, not just rendering. `git log --no-pager` is fine; `apt-get -y` defaults to “yes” on prompts the user might actually want to read.

The reasonable role of these flags is as *defaults you can set in the notebook environment* so that the 80%‑case commands look nicer in a non‑terminal frontend, with the understanding that the long tail will still want a real PTY.

### 3.3 “Run TUIs in a separate terminal”

A pragmatic middle: when a TUI is requested, do not try to host it in your accessible frontend at all; spawn Windows Terminal (or your favorite terminal) with the command and let the user interact with it there, using whatever screen reader support that terminal has (Windows Terminal does expose a UIA tree; NVDA can speak it imperfectly). On exit, control returns to the notebook. This gives up the unified‑interface ambition but is by far the simplest implementation and may be the most pleasant for many workflows — full‑screen TUIs are arguably better experienced as discrete sessions anyway.

### 3.4 The hard truth about detection

In‑advance detection — “this command, before I run it, will be interactive” — is undecidable in the general case. A shell function `gs` could be `git status` or `git status | fzf`. A script could `read` from stdin or not depending on arguments. PowerShell’s `Read-Host` could be inside an `if` branch. The most you can do in advance is honor explicit signals (the user pressed a “run as TUI” key; the command name matches a known list; an environment variable says so) and otherwise *react quickly* to observed behavior. The fast‑reaction approach has the further advantage that it cannot regress — if a new tool starts emitting alt‑screen sequences, your detector still catches it.

## 4. Screen reader integration mechanics on Windows

### 4.1 NVDA Controller Client DLL

The Controller Client is NVDA’s documented out‑of‑process speech/braille API, distributed as `nvdaControllerClient32.dll` / `nvdaControllerClient64.dll` (and ARM64 variants in 2024+), with import libraries and a C header in `extras/controllerClient/` of the NVDA source tree. Any process can `LoadLibrary` it and call its exported functions; the DLL uses RPC under the hood to talk to the running NVDA process.

The 1.0 surface, supported by every NVDA version that has shipped the API, includes:

- `nvdaController_testIfRunning()` — returns 0 if NVDA is running.
- `nvdaController_speakText(LPCWSTR text)` — speaks the given UTF‑16 text. NVDA’s default behavior is to interrupt any current speech; you can model this as “speak, with interrupt.”
- `nvdaController_cancelSpeech()` — silences any current speech.
- `nvdaController_brailleMessage(LPCWSTR text)` — flashes a message on the braille display.

The 2.0 surface, introduced in NVDA 2024.1, adds SSML support and finer priority control. Returning error 1717 (`RPC_S_UNKNOWN_IF`) is how older NVDAs signal “I do not implement this”:

- `nvdaController_speakSsml(LPCWSTR ssml, SYMBOL_LEVEL symbolLevel, SPEECH_PRIORITY priority, BOOL asynchronous)` — speaks an SSML string. The `priority` argument gives you three levels (`SPEECH_PRIORITY_NORMAL`, `SPEECH_PRIORITY_NEXT`, `SPEECH_PRIORITY_NOW`) corresponding to NVDA’s queuing semantics: append, jump to head of queue but do not interrupt, or interrupt immediately. The `asynchronous` flag lets you block until the utterance is done (useful for synchronizing audio cues against speech), and `<mark/>` callbacks via `nvdaController_setOnSsmlMarkReachedCallback` let you fire events at known points in the spoken text.

For your purposes, three properties matter most. First, **the Controller Client lets your process originate speech without owning focus**. You do not need to be the focused window for NVDA to speak what you send; this matters when audio cues should fire from a background thread while focus is in your edit field. Second, **priority gives you the building block for “interrupt this stale announcement”** — a streaming output coming in fragments should generally use NORMAL, but the appearance of a prompt, an error, or a state change can use NEXT or NOW. Third, **SSML gives you prosody and voice changes** that you can use for audio formatting in the Raman/AsTeR sense — different roles spoken in different voices, pitch, or rate, which is what makes Emacspeak’s voice‑lock model effective.

There is a security note worth being aware of: NVDA runs on the lock screen and on secure desktops, and the Controller Client functions can be called there; the docs explicitly recommend that an application check whether Windows is locked before pushing sensitive data through `speakText`.

### 4.2 NVDA’s review cursor, browse mode, and consoles

NVDA has two cursor concepts that any terminal frontend must coexist with:

- The **system caret / focus** moves as the OS reports caret events. In a console with the legacy model, NVDA tracked the cursor in the conhost screen buffer; in a UIA‑aware console it tracks the UIA caret.
- The **review cursor** is NVDA’s own non‑visual cursor for inspecting the screen without moving focus. The numpad commands (`numpad 1/2/3` previous/current/next character, `4/5/6` word, `7/8/9` line, plus screen review with `NVDA+numpad7`/`numpad1` to jump to top/bottom of buffer) operate on the review cursor.

**Browse mode** is NVDA’s structural reading mode for documents (most prominently web pages and PDFs): single‑letter quick‑nav (`h` for heading, `k` for link, etc.), virtual buffer of the rendered text, arrow keys moving by rendered line independent of the focused element. Browse mode is *not* automatically used in a terminal — NVDA does not flip a terminal into browse mode the way it does for a web page — but the model is exactly what you want for inspecting prior output, and several NVDA add‑ons (and your hypothetical frontend) can present a virtual buffer for the rolling transcript that the user navigates with browse‑mode‑style keys.

### 4.3 UIA TextPattern as it applies to terminals

`IUIAutomationTextPattern` and `IUIAutomationTextRange` are Microsoft’s accessibility model for arbitrary text controls. A provider exposes `DocumentRange` (the whole text), gives a range for the visible portion (`GetVisibleRanges`), supports selection and movement (`Move`, `MoveEndpointByUnit` with units `Character`, `Word`, `Line`, `Paragraph`, `Page`, `Document`), and supports formatting attributes — font, color, foreground/background — through the `GetAttributeValue` API.

Windows Terminal implements `ITextProvider`/`ITextRangeProvider` on its TermControl, backed by the in‑memory text buffer that the renderer also reads. In practice, this means:

- The visible grid is exposed as a single document; the line wrap state is exposed via the `IsWordWrap`‑ish text unit calculations; cells have attributes that surface as text attributes.
- The system caret position corresponds to the cursor cell.
- `TextChanged` events fire when the buffer is modified, and a `Notification` event pattern is used for some announcements.

What works: a screen reader can read the visible buffer, move by line, navigate the scrollback (modulo the terminal’s scrollback retention), and get notified when content changes. NVDA on Windows Terminal does, in fact, more or less function this way today.

What doesn’t, or doesn’t work well: the live‑updating, redraw‑heavy nature of a terminal is fundamentally hostile to the “document with insertions” model UIA expects. A vim screen looks to UIA like a document whose entire content frequently changes; NVDA must diff and choose what to announce, and the choices it makes (read the current line on caret move; announce text inserted at a known position; ignore wholesale rewrites) work for some patterns and not others. There are open and recently closed accessibility issues in `microsoft/terminal` that are illustrative: `#1350` was the original “set up the UIA tree at all”;  `#11929` covered popup announcements;  `#17892` describes Narrator only announcing single characters during mark‑mode navigation as of late 2024.  The mlt mltony `consoleToolkit` NVDA add‑on documents an entirely separate failure: UIA in the Windows Console trims trailing whitespace per line, so the add‑on has to inject a sentinel control character at the start of each line to preserve the leading‑space information for multi‑line command parsing. This is the texture of UIA‑on‑terminals work: it functions, but it has the wrong abstraction underneath, and you patch around it.

For your own frontend, the implication is that if you build a custom edit control with proper UIAutomation provider implementation (or use an off‑the‑shelf one like a WPF `RichTextBox` or a WinUI `TextBox` whose providers are already correct), you will get vastly better UIA behavior than any terminal can. If you treat the terminal as the model and try to expose its content via UIA, you will be re‑solving Windows Terminal’s accessibility problems.

### 4.4 IAccessible2 on Windows terminals

IAccessible2 is the GNOME/Mozilla‑driven extension to MSAA that supplies the `IAccessibleText`, `IAccessibleHypertext`, and similar interfaces. It is in active use by Firefox, LibreOffice, and a number of other applications, and NVDA supports it as a first‑class provider. Windows Terminal does *not* implement IAccessible2; it implements UIA. The native Windows console (conhost) historically exposed console content through an MSAA implementation but did not implement IAccessible2 either; in recent Windows it exposes content via UIA when “Use UI Automation to access the Windows Console when available” is enabled in NVDA settings. So in practice on Windows you can treat UIA as the modern path and IAccessible2 as not relevant to terminals.

### 4.5 NVDA’s two console paths and what ConPTY changes

NVDA historically had two code paths for the Windows console:

- **`consoleWinAPI` (legacy):** NVDA injects into the focused conhost process and reads the `CHAR_INFO` screen buffer directly via the console API. The “report dynamic content changes” feature compares the buffer state to the previous snapshot, and NVDA announces the diff. This works only when the console is focused (NVDA’s hooks need to be active in that process) and depends on a non‑headless conhost.
- **`UIA` (modern):** NVDA consumes the console’s UIA provider. This works for unfocused windows too (you can capture output while doing something else), survives the lifecycle of a ConPTY‑hosted conhost, and is the only path that works with Windows Terminal.

What changes when applications use ConPTY is subtle but important: when a child runs under ConPTY, the conhost it is attached to is *headless*. There is no window to attach the legacy path to. The terminal the user is looking at (Windows Terminal, or your frontend) is a separate process that consumes the VT stream and provides its own UIA tree. NVDA cannot use the legacy path on that conhost — there is nothing visible — and so it falls back to whatever UIA the visible terminal provides. This is why “use UIA for console” is, in 2025, a near‑mandatory setting for anyone using modern terminals; the legacy path is increasingly irrelevant.

### 4.6 Diffs, dynamic content, and what to announce

The core tension in a terminal is “what changed” vs “what is on screen now”. A diff‑based strategy is the natural fit: maintain a model of the previous screen state, compute the difference when the screen updates, and announce the differences in a stable order (typically top to bottom, left to right). NVDA’s existing dynamic‑content code in consoles does roughly this. The known failure modes:

- **High‑frequency redraws.** A progress bar that updates 20 times per second will, if announced naively, produce 20 announcements per second. The standard mitigation is debouncing: coalesce updates within a window (e.g. 200 ms) and announce only the latest. The cost is that very fast transitions are lost.
- **Wholesale screen rewrites.** A TUI redrawing its whole screen looks like “100 cells changed”; announcing every cell is useless. The right behavior depends on context: in an alt‑screen TUI, the user generally wants to know the focused element changed, not that 80×24 cells flickered.
- **Cursor‑directed announcement.** If the cursor moves and content at the cursor changed, announce the new content at the cursor. This is the “say what I am typing” / “say what is being inserted near the cursor” idiom and is the right behavior for shell input echoing.
- **Region‑of‑interest announcement.** A user can mark a region (a status line, a particular column) and the screen reader announces only changes there.

The right behavior is highly workload‑dependent, which is part of why a one‑size‑fits‑all terminal screen reader is so hard. Emacspeak’s approach in `term-mode` is illustrative: it treats the cursor as the focus and the line at the cursor as the announcement target, with explicit commands for “speak the whole screen” when the user wants it. TDSR’s approach is similar — review keys for navigation, automatic speech for new content near the cursor.

### 4.7 Speech interruption semantics

The semantics question is really three questions:

1. **When the user types, should the screen reader interrupt itself?** Standard behavior: yes. Anything that resembles user action interrupts ongoing speech. NVDA does this by default for keystrokes.
1. **When new output arrives while speech is in progress, interrupt or queue?** This is the hard one and depends on the kind of output. New prompt → probably interrupt. Continuation of a long log → probably queue. The Console Toolkit add‑on for NVDA explicitly exposes a “speak new lines immediately, cancelling old” option because the default queue‑append behavior gets very stale in fast‑moving consoles.
1. **When audio cues / earcons play, do they duck speech?** A separate audio mixing question; see §6.

The Controller Client v2 priority levels (NORMAL/NEXT/NOW) are precisely the knob for this. A reasonable rule of thumb: stdout content is NORMAL, prompts and state changes are NEXT, user keystroke echo and errors are NOW.

## 5. ANSI / VT parsing as an accessibility prerequisite

A terminal’s output stream is not a sequence of characters to be appended to a transcript; it is a *program* whose execution mutates a 2‑D character grid plus a cursor plus a small amount of state. Any accessibility frontend that wants to know “what is on screen” must execute that program against an in‑memory grid, exactly as a graphical terminal emulator does. There is no shortcut.

The relevant categories of sequence and what they cost you semantically:

- **C0 controls** (0x00–0x1F): BS, HT, LF, CR, BEL. Trivial individually; collectively they imply you must implement tab stops, the convention that LF *might* or might not include CR depending on terminal mode (`ENABLE_PROCESSED_OUTPUT` analog: `LNM` mode), and the fact that BEL is sometimes informational (an OSC string terminator) and sometimes “the program wants my attention.”
- **CSI (Control Sequence Introducer) sequences** (`ESC [ … final`): cursor positioning (`H`, `f`, `A`/`B`/`C`/`D` for relative moves), erasure (`J` clear screen with parameters 0/1/2/3, `K` clear line), scrolling regions (`r`), SGR (Select Graphic Rendition) for colors and attributes, save/restore cursor, and a long tail. The `pyte` library on PyPI and `libvterm` in C are the canonical implementations; reading their source is the fastest way to internalize the actual surface.
- **DEC private modes** (`ESC [ ? n h` / `l`): cursor visibility (`?25`), alternate screen buffer (`?47`, `?1047`, `?1049`), mouse modes (`?1000`–`?1006`), bracketed paste (`?2004`), cursor key application mode (`?1`). The alternate‑screen modes are *the* most important signal for your “this is a TUI” detector.
- **OSC (Operating System Command) sequences** (`ESC ] n ; … ST`): window title (`0`, `1`, `2`), clipboard set/get (`52`), hyperlinks (`8`), colors (`4`, `10`, `11`), notifications (`9` in iTerm dialect, `777` in others). OSC 8 hyperlinks are interesting for accessibility: they encode `text → URL` mappings that you could expose as links to a screen reader user, much as a web page does, rather than just speaking the visible label. Almost no terminal accessibility implementation does this today.
- **DCS / SOS / PM / APC** (Device Control String and friends): used for Sixel graphics, Kitty graphics protocol, terminal capabilities, and other extensions. Mostly safely ignorable for accessibility unless you want to be ambitious.
- **C1 controls in 8‑bit form** vs. their two‑byte ESC equivalents. Modern terminals generally use ESC‑prefixed.

The implementations you would look at:

- **`libvterm`** (Paul Evans’s C99 library, originally from the Vim project’s terminal feature; now used in Neovim’s `:terminal` and in `emacs-libvterm`). It is callback‑based, doesn’t malloc on the hot path, and exposes `VTermScreenCell` cells with full attributes plus a `damage` callback that tells you exactly which regions changed since the last call. This is the right abstraction for an accessibility frontend: you don’t need to draw, you just need to know what is on the grid and what just changed.
- **`pyte`** (Python). Pure Python, slow but ergonomic. TDSR uses it; the Console Toolkit‑style NVDA add‑ons could (and likely do). Pyte exposes `Screen` and `Stream` objects with a clean event model — you `stream.feed(bytes)` and it dispatches events to a screen.
- **`OpenConsole`’s own terminal/vt parser** in the `microsoft/terminal` repo. Useful as a reference for the Microsoft conventions, especially around the `WIN32-INPUT-MODE` envelope.
- **`xterm.js`’s parser.** TypeScript, also clean.

Three points are worth emphasizing:

1. **The grid is stateful, not derivable from any window of the stream.** If you join a session mid‑stream you cannot reconstruct the screen; you must replay from a known state. This has consequences for “diff‑based announcement”: your diff is against your own model, not against the raw stream.
1. **Cursor position interacts with semantics.** `\x1b[H` (home) followed by output is a redraw; the same output without `H` is an append. The distinction is invisible if you only look at “characters written” — you have to know where the cursor was.
1. **The alternate screen buffer is two grids, not one.** When a TUI exits, the previous grid is restored. Your model needs to maintain both, and your scrollback transcript should reflect the main buffer, not the alt buffer. (This is why “save vim’s output to a transcript” gives you nothing useful.)

For an accessibility frontend, the right shape is: maintain a `libvterm`‑style model, expose the main‑buffer grid as a scrolling document to the screen reader via a UIA TextProvider you control, and *additionally* expose the active alt‑buffer screen (when present) as a separate view. When the alt buffer is in use, the document the screen reader navigates is the active TUI screen; when it isn’t, the document is the appended transcript. This is the structural shape `Emacspeak`’s `eterm` and `vterm` modes have arrived at on the Emacs side.

## 6. Audio and sonification as first‑class output

### 6.1 Earcons, auditory icons, and the conceptual frame

The terminology comes from Gaver (auditory icons — sounds that resemble their referent, like a paper‑crumple for “delete”), Blattner et al. (earcons — abstract motifs, like a four‑note chord for “error”), and the body of work T.V. Raman pulled together first in AsTeR (his 1994 Cornell PhD thesis, ACM Doctoral Dissertation Award) and then operationalized in Emacspeak (1994‑present, in the Smithsonian’s permanent IT collection since 1999). Raman’s key contribution is that audio rendering is not just speech with sound effects sprinkled on top; it is a *medium* with its own grammar — voice‑lock (using different voices/prosody to indicate syntactic role, the way visual syntax highlighting uses color), audio formatting (Aural CSS, structural intonation), auditory icons for state changes, and crucially the *active listening* model where the user can navigate the document structurally rather than passively absorbing a linear narration.

For a shell frontend, the natural mapping is:

|State / event                  |Audio role                                                 |
|-------------------------------|-----------------------------------------------------------|
|Command submitted              |Short rising earcon (start)                                |
|Command running, output flowing|Ambient texture, low volume, ducked under speech           |
|Command finished, exit 0       |Soft chime                                                 |
|Command finished, nonzero exit |Descending earcon, possibly with stderr cue                |
|New prompt available           |Brief tick                                                 |
|Program reading stdin          |Sustained tone, indicating “waiting on you”                |
|Password prompt                |Distinctive earcon (and screen reader announces “password”)|
|Stderr output                  |A different timbre than stdout’s texture                   |
|Network/IO latency             |Pitch/rate of the ambient texture                          |
|Progress bar (0–100%)          |Pitch sweep — high pitch = more done                       |
|Long output (>screen)          |Audio cue when output exceeds N lines                      |

### 6.2 Spatialization

Stereo placement is the cheapest spatialization on Windows: pan stdout to slightly left, stderr to slightly right, prompts in the center, screen reader speech also center. This is mixed in software with `XAudio2` or `WASAPI` and consumes essentially zero CPU. The user gains the ability to *parse two streams in parallel* — listening to a continuous build log while the screen reader speaks an error message, the brain can attend to whichever the spatial location of the sound primes.

HRTF (head‑related transfer function) spatialization places sounds in apparent 3‑D space. Windows Spatial Audio (`ISpatialAudioClient`, `ISpatialAudioObject`) and the HRTF mode of the system audio engine can produce convincing externalized positions if the user is on headphones. The cost of going from stereo to HRTF is mostly engineering effort — the perceptual gain depends on the user. T.V. Raman has noted in AsTeR’s demo materials that he uses stereo to spatialize matrix rows and columns; he reads a matrix by hearing elements move from left to right as the row is read, with row position encoded by something else (pitch, in his case). That sort of structural sonification is the most powerful and the most underexplored application.

### 6.3 Latency budgets

For audio feedback to feel responsive (keystroke clicks; “you pressed Enter and now the command is running” cue), the literature converges on roughly **20 ms ideal, 100 ms acceptable, 200 ms noticeably laggy**. On Windows this is achievable with:

- WASAPI in shared mode: ~10–30 ms practical latency.
- WASAPI in exclusive mode: ~3–10 ms, but you take the device exclusively (incompatible with the screen reader, which is also using audio).
- DirectSound / `XAudio2` mid‑pipeline: low tens of ms.

Practically: use WASAPI shared mode, keep your audio buffer small (~480 frames at 48 kHz = 10 ms), and pre‑load all cue assets into memory. The latency budget for *speech* is much more forgiving — humans tolerate 100–300 ms before TTS feels sluggish — but the latency budget for *immediate keystroke feedback* is tight.

A subtle additional constraint: if you are using NVDA’s Controller Client for speech *and* mixing your own audio cues independently, both will go to the default audio endpoint and you do not directly control NVDA’s buffer. NVDA’s audio path adds its own latency, on the order of 50–150 ms depending on synthesizer (eSpeak is fastest; OneCore is heavier). Mixing strategies:

- **Independent streams, no ducking.** Simplest; works because earcons are short and don’t compete much.
- **Ducking via the Windows audio session API.** Your audio cues raise a “communications” flag that Windows’ audio engine uses to attenuate other sessions. Crude but it works.
- **Ducking via NVDA’s own setting.** NVDA has a “lower background audio while speaking” option (added 2016). If you check that, ambient textures get attenuated automatically while NVDA speaks.

### 6.4 Sonification examples and prior art

Beyond AsTeR and Emacspeak:

- **SoundCommander** (a 2000s blind‑developer audio mixer / command‑line tool; mostly historical interest).
- **The Vortex CHI literature on auditory progress bars** (Crispien, Brewster) demonstrates that mapping percentage to pitch is intuitive and accurate to ~5% — better than people guess from visual progress bars.
- **`emacspeak`’s `auditory-icons`** module: a curated library of short WAV cues for “select”, “open”, “close”, “mark”, “yank”, “save”, “error” etc., played in response to specific Emacs events. The user learns the icon → meaning mapping the way sighted users learn color/icon conventions.
- **NVDA’s add‑on ecosystem**, including the `consoleToolkit` add‑on whose “beep on console text update” option is exactly a sonification cue, and several add‑ons that play earcons on focus role changes.
- **Raman’s CONGRATS** (his 1980s undergraduate project at IIT Bombay): listened to mathematical curves by sonification. This is the same intellectual lineage as the audio‑first terminal.

### 6.5 Does real‑time interaction enable audio loops impossible in batch?

Yes, and meaningfully so. Concretely:

- **Keystroke‑rate sonification.** Each character you type can produce a tiny click whose pitch encodes the character class (letter, digit, punctuation), giving a continuous “you’re typing correctly” feedback that is faster than speech could ever be. This requires <30 ms loop latency, only achievable in a pass‑through model.
- **Live progress.** A `make -j8` run can map “lines per second” to ambient pitch in real time; the user hears the build accelerate and decelerate. In a batch model you would only hear “compile” → silence → “done”.
- **Search‑as‑you‑type feedback.** Like fzf: each keystroke filters; a sonification of “result count” or “match quality” tells you in real time whether you are closing in. Without a streaming loop you cannot do this.
- **Continuous tail of a log.** Map the rate of log entries to a continuous tone; spikes are audible without listening to content.
- **Ctrl‑C feedback.** A distinct audio cue *immediately* when Ctrl‑C is interpreted as a signal, before the program has even had a chance to print its cleanup messages.

These are genuinely beyond what a compose‑then‑send model can do, even with side channels. They constitute the strongest argument for at least a hybrid that uses pass‑through whenever it can deliver feedback faster than batched speech.

## 7. Timing and event‑loop architecture

The set of concurrent activities a serious frontend must coordinate is large: reading the PTY output handle without blocking; reading the user’s keystrokes from the focused control; reading any auxiliary handles (stderr, log files, side channels); driving the VT parser into the screen model; computing diffs and announcement candidates; pushing speech via the Controller Client; mixing audio cues; handling resize events; polling the child for liveness; signaling Ctrl‑C; writing input back to the PTY; and updating the UIA tree your provider exposes. None of these can block the others, and several have tight latency requirements.

### 7.1 The shape of the event loop

The natural decomposition on Windows is one of:

- **Win32 IOCP (I/O Completion Ports) with `OVERLAPPED` I/O on every handle.** This is the highest‑performance, most Windows‑idiomatic approach. *But* ConPTY explicitly does not support overlapped I/O on its input/output handles (“currently restricted to synchronous I/O” per the Microsoft docs).  So you must either use anonymous pipes with synchronous reads on dedicated threads, or use named pipes you create with `FILE_FLAG_OVERLAPPED` and then bind to the IOCP — the latter works as long as you create the pipes yourself with overlapped support before handing them to `CreatePseudoConsole`. The Microsoft `terminal` sample code uses synchronous reads on dedicated threads, which is correct and simpler.
- **asyncio / Tokio / libuv with Windows pipe support.** `asyncio` on Windows has the `ProactorEventLoop` which is IOCP‑backed; it handles named pipes natively. This is what most modern implementations would reach for.
- **Dedicated threads per handle, with a thread‑safe queue feeding the main loop.** Simplest, perfectly fine for one PTY. Two threads: one reads the output pipe in a loop and pushes byte chunks to a queue; one writes the input pipe when the queue says so. Main thread does parsing, announcement, and UI.

The Microsoft pseudoconsole documentation specifically recommends “each of the communication channels is serviced on a separate thread that maintains its own client buffer state and messaging queue inside your application. Servicing all of the pseudoconsole activities on the same thread may result in a deadlock where one of the communications buffers is filled and waiting for your action while you attempt to act on the other.” This is a real hazard and worth heeding.

### 7.2 The fragmentation problem

A child process writing “hello world\n” might appear at your `ReadFile` as: one chunk of “hello world\n”; or “hel” then “lo wor” then “ld\n”; or 13 chunks of one byte each. Pipe boundaries do not correspond to logical units. If you naively announce every chunk you receive, you will announce “hel” then nothing then “lo wor” then “ld” — useless.

Standard responses:

- **Feed the VT parser regardless of chunk boundaries.** Parsers like libvterm and pyte are stateful and chunk‑boundary‑agnostic by design; they buffer partial sequences internally.
- **Decide announcement boundaries at the line / cell level, not the chunk level.** Announce when a newline is added to the grid, when the cursor moves past a column, or when a debounce timer fires.
- **Coalesce with a debounce.** Wait ~50–100 ms after the last received byte; if no more arrives, announce what you have. If more arrives, restart the timer. This trades some latency for sane chunking. The right window depends on the workload — too short and you announce fragments; too long and feedback feels laggy.
- **Coalesce by VT semantics.** Announce after a `\n`, after the cursor returns to column 0, after a known SGR reset — events that frequently mark “logical end of a line.”

### 7.3 What the input thread must guarantee

The input thread must be responsive even when the output thread is hammered. A common bug: the user presses Ctrl‑C, but the main thread is blocked parsing 5 MB of build output and the signal arrives 800 ms late. Mitigations:

- Make the keystroke‑to‑pipe path *separate* from the parse/announce path. The input thread reads keystrokes and writes to the PTY input pipe; it does not need to wait for the parse loop.
- Make Ctrl‑C special‑cased: when you detect Ctrl‑C in the focused window, call `GenerateConsoleCtrlEvent(CTRL_C_EVENT, processGroupId)` on the child’s process group rather than (or in addition to) sending the byte `0x03` to the pipe. The Microsoft Q&A on Ctrl‑C delivery to ConPTY children documents that this is the reliable path; sending `0x03` works if and only if the child was not created with `CREATE_NEW_PROCESS_GROUP`, and reliable interrupt requires the kernel‑side signal. 

### 7.4 Latency budgets, again, in the system context

For the system to feel responsive:

- Keystroke‑to‑echo: <50 ms ideal.
- Keystroke‑to‑audio‑cue: <50 ms ideal.
- Command‑finished cue: <100 ms after the actual exit.
- New‑output‑begins‑speaking: 50–200 ms after first bytes arrive, depending on coalescing window.
- Ctrl‑C‑to‑interrupt‑acknowledged cue: <100 ms.

Most of these are well within reach on modern hardware *if* you avoid synchronous calls into anything slow on the latency‑critical path. The most common offenders are RPC into NVDA’s speech engine (which can take 10–40 ms to dispatch and which can block while espeak does its job), and synchronous text rendering / UIA‑tree updates. Putting both behind an async queue solves it.

## 8. The notebook‑as‑frontend model: Jupyter and Wolfram

### 8.1 Jupyter as the architecturally relevant case

Jupyter is the only widely deployed notebook system that has rigorously addressed the “kernel needs to ask for input” problem, and how it solved it is informative.

A Jupyter session has four ZeroMQ sockets between frontend and kernel:

- **Shell** (ROUTER on the kernel) — code execution requests, completion, history, kernel‑info.
- **IOPub** (PUB on the kernel) — broadcasts of stdout/stderr, display data, status (idle/busy), execute_input echoes, errors.
- **Stdin** (ROUTER on the kernel) — the *reverse‑direction* channel: kernel sends `input_request`, frontend sends `input_reply`.
- **Control** — priority requests (interrupt, shutdown).

The execution flow is: frontend sends `execute_request` on shell with a code string and `allow_stdin: true|false`. Kernel runs the code, broadcasting stdout/stderr fragments on IOPub as `stream` messages. If the user code calls `input(prompt)`, the kernel (specifically: a Jupyter‑aware `input` shim installed in `sys.stdin`) sends an `input_request{prompt, password}` on stdin; the frontend collects user input however it wants (an inline edit field is the conventional choice, but a modal dialog or audio prompt are equally valid) and sends `input_reply{value}`. The kernel’s `input()` call returns the value. When the code finishes, kernel sends `execute_reply` on shell with `status: ok|error|abort`.

This works *because the kernel is cooperating*. The Python kernel ships with an `input()` replacement that uses this mechanism. The R kernel patches `readline`. The Julia kernel patches `readline()`. The kernel maintainers have written Jupyter‑aware shims for the language’s stdin primitives.

For arbitrary shell commands the equivalent does not exist. There is no Jupyter‑aware `read` in bash. There is no Jupyter‑aware `Read-Host` in PowerShell. When you run `!command` in a Jupyter notebook with the IPython kernel, IPython spawns the subprocess with stdin redirected (typically to `/dev/null` or to the empty string); if the subprocess tries to read stdin, it gets EOF. This is *exactly* the limitation we ran into in §2: the elegant message‑driven design works only when the executing code is willing to call the right primitive.

### 8.2 What that means for “Jupyter for shells”

There are projects that have tried — `bash_kernel`, `Calysto Bash`, `Xonsh`’s own kernel, `IPowerShell` and the official `PowerShell` Jupyter kernel, plus various “shell” magics in IPython. They share a characteristic: they work well for compose‑then‑send line execution and they all degrade in the same ways for interactive content.

`bash_kernel` is illustrative. Internally it uses `pexpect` (a Python expect/pty library) to maintain a long‑lived bash subprocess against a real PTY, sends commands as `command\n`, and reads output until it sees the bash prompt again — effectively a synchronous expect loop. If you `read` inside a command, pexpect waits forever; the kernel has a heuristic where after some seconds without seeing a prompt it sends an `input_request` to the frontend; if the frontend supplies input it pushes it onto the PTY and resumes waiting. This works for simple cases. It fails for anything that doesn’t print the standard PS1 prompt, anything that needs raw‑mode keys, anything full‑screen, anything reading binary, and anything where the user’s response should depend on what’s *currently* on the screen (as opposed to a single prompt string).

`xonsh`’s Jupyter mode and the official PowerShell kernel have similar shape and similar limits.

### 8.3 Wolfram notebook as a frontend

Wolfram notebooks have a different architecture: notebooks talk to a Wolfram kernel through MathLink/WSTP, which is a structured RPC (not text‑based), and the kernel can call back into the frontend through symbolic expressions like `DynamicModule`, `Manipulate`, etc. The frontend already has rich UI primitives — controls, text fields, dynamic interfaces — that the kernel can summon. For shell integration specifically, Wolfram offers `RunProcess` (synchronous), `StartProcess` (asynchronous with stdin/stdout streams), and shell escape via `!command`. None of these talk to a PTY; they use OS pipes.

What Wolfram has that Jupyter does not: an extremely capable, server‑driven UI in the notebook frontend, with first‑class accessibility paths through its FrontEnd. What it doesn’t have: a PTY abstraction. If you wanted a Wolfram notebook as a shell frontend, you would either (a) implement the same “long‑lived process via pipes plus a heuristic for prompts” as bash_kernel, with the same limits, or (b) integrate a real ConPTY in a Wolfram plugin / external program and stream content into the notebook as text — at which point the notebook is just a viewer for content your PTY driver generates and is not doing any “notebook” work.

### 8.4 The serious effort question

Has anyone done a serious notebook‑as‑shell? `bash_kernel` and the PowerShell kernel exist and work for non‑interactive use. `jupyterlab-terminal` is a real terminal *inside* JupyterLab — it is fundamentally an xterm.js running over WebSocket to a PTY on the server, so it is the pass‑through model with a notebook chrome. The Mathematica `SystemDialogInput`, `Input`, `InputString` primitives talk to the frontend over WSTP for dialog input; they are not stdin substitutes.

What no one has built well is a notebook that is *first‑class for blind users* and that handles both line‑oriented commands and interactive content gracefully. The Jupyter ecosystem has accessibility issues even for non‑shell use (the JupyterLab UI is notoriously hard with screen readers); Wolfram is somewhat better on the FrontEnd side but its shell story is shallow. There is a real architectural gap here.

## 9. Existing prior art

### 9.1 Emacspeak (T. V. Raman, 1994–present)

Emacs‑based, Linux/macOS primary, Windows possible only under WSL. The architecture is: a TTS server (DECtalk, Eloquence, eSpeak, espeak‑ng, OneCore via a bridge, IBM ViaVoice, Apple Say) is a separate process speaking a small command protocol on stdin; Emacs Lisp `dtk‑speak` formats utterances into that protocol; major modes are *speech‑enabled* by advising Emacs’s own functions to also call into the speech layer. Audio formatting uses voice changes (different “personalities” — pitch, rate, head‑size — assigned to roles like comments, strings, keywords) and a library of auditory icons that play on specific events.

For terminal use, Emacspeak has two relevant modes:

- **`term-mode` / `ansi-term`:** a built‑in Emacs terminal emulator using ANSI parsing; Emacspeak speech‑enables it so cursor moves and inserts are voiced. Works well for line‑oriented programs and for many text editors; the alt‑screen / full‑redraw case is where it gets thin.
- **`vterm` (via `emacs-libvterm`):** a much better terminal emulator that wraps libvterm; Emacspeak’s vterm support is more recent and benefits from libvterm’s correctness. This is the closest existing system to “a screen‑reader‑first terminal with full TUI support” — vim, htop, fzf all work, with caveats around what the user can perceive.

What Emacspeak teaches: audio formatting is real and works; voice‑lock for source code is genuinely faster than spoken‑word‑with‑role‑prefixes; a single Emacs session as the entire computing environment is a productive lifestyle for some users. What it doesn’t translate to Windows easily is its dependency on the Emacs ecosystem and on a TTS server protocol that NVDA does not natively speak (the `nvda2speechd` bridge is one half of the solution — Linux applications driving a Windows‑style speech API; for Emacspeak‑style on NVDA you’d want the reverse, an Emacspeak TTS server that drives NVDA’s Controller Client).

### 9.2 TDSR — Tyler Spivey’s Terminal Detail Screen Reader

Python, GPLv3, macOS/Linux. The technical approach is illuminating: TDSR runs *inside the terminal*. You launch `tdsr` as your shell (or as a wrapper that then `exec`s your shell); TDSR creates a PTY for the child shell, runs the shell against that PTY, and sits in the middle as a `pyte` screen + speech driver. Keystrokes you type at the outer terminal are forwarded to the child PTY; output from the child is fed to pyte, which updates its screen model, and TDSR speaks the diff (or, on review keys, navigates the screen model and speaks under cursor).

TDSR’s review cursor model is roughly: numpad 7/8/9 for previous/current/next line, 4/5/6 for word, 1/2/3 for character, on a frozen view of the pyte screen. Speech goes to speech‑dispatcher on Linux or to `say` on macOS. A community Rust server (`tdsr-server`) bridges to NVDA for users who want NVDA to do the speaking. An NVDA add‑on (`tdsr-NVDA`, by Derek Riemer) sets up that bridge specifically and crucially disables NVDA’s own cursor tracking and live‑region behavior in the console, because TDSR is now the source of truth.

The Riemer add‑on’s documentation explicitly articulates why TDSR over SSH is better than NVDA‑over‑Windows‑Terminal‑over‑SSH: NVDA polls accessibility APIs to detect cursor movement, with a fixed timeout that can lose fast cursor changes; NVDA diffs the entire screen to detect changes, which is expensive in a console; NVDA’s plugin architecture is awkward for terminal‑specific logic. TDSR has none of these problems because it has the byte stream and the pyte model directly.

For your Windows situation, the relevant takeaway is twofold:

1. The TDSR architecture *is* the “pass‑through with semantic screen model” approach you would build for Windows. It is well‑validated as the right shape.
1. TDSR does not run natively on Windows. A Windows TDSR would replace the PTY layer with ConPTY, the speech layer with the NVDA Controller Client, the keyboard input layer with Win32 message hooks (or a Windows console subsystem app that owns the keyboard), and would keep `pyte` (or move to libvterm).

### 9.3 Terminal Access for NVDA (NVDA add‑on)

A recent NVDA add‑on, “Terminal Access for NVDA,” explicitly inspired by TDSR and Speakup, providing screen‑reader‑oriented review and navigation for Windows Terminal and PowerShell. It is an NVDA‑side approach (NVDA stays the screen reader; the add‑on extends NVDA’s terminal handling) rather than a separate process. It is worth investigating directly as the closest existing thing on Windows; it lives in the official NVDA add‑on store.

### 9.4 The mltony `consoleToolkit` and `tonyEnhancements` add‑ons

Console Toolkit is the most engineered NVDA add‑on for Windows console accessibility I am aware of. Features:

- Override `shift+numpad7` so it reads the first *visible* line rather than the first line of the entire scrollback buffer.
- Option to speak new console lines immediately, cancelling stale speech (the priority‑NEXT behavior described in §4).
- Edit‑prompt window: `NVDA+E` extracts the current command line by sending sentinel control characters around it via Home/End, captures the segment of buffer between them, and presents the command in an accessible Win32 edit field; after editing, the add‑on rewrites the console’s current line by erasing and re‑typing.
- Capture command output: `Ctrl+Enter` reruns the previous command piped to `less` and captures the result for browsing.
- Beep on console text update — a primitive sonification.

This add‑on is, in effect, an existence proof that the *compose‑then‑send overlay on a streaming terminal* idea is viable enough to be useful. The “edit prompt” feature is exactly the “notebook cell to edit the command, then send the result to the running terminal” pattern in miniature.

### 9.5 BRLTTY on Windows

BRLTTY is the Linux‑world refreshable‑braille screen reader; it has Windows support. Its Windows screen driver historically reads the console screen content via the legacy console API (`ReadConsoleOutput` on the conhost buffer). With the move to ConPTY, BRLTTY’s Windows path becomes degraded for the same reasons NVDA’s legacy console path does: the headless conhost has no buffer to read in the usual way. BRLTTY on Windows is a niche tool today (most braille users on Windows use NVDA or JAWS as the screen reader and let their braille display be driven by that screen reader); for purposes of this discussion the main lesson is that screen‑buffer scraping was a viable Windows approach until ~2018 and is no longer.

### 9.6 PowerShell ISE: deprecation and why

PowerShell ISE was a WPF‑based scripting environment for Windows PowerShell 5.1 and earlier. It had a Console pane and a Script pane in one accessible window — exactly the “notebook for a shell” shape, with full Windows accessibility (UIA, IAccessible2 via WPF’s a11y bridge). For blind PowerShell users in the 2010s, ISE was significantly more accessible than the raw console, because the panes were ordinary text controls instead of a terminal grid.

It is “no longer in active feature development” per Microsoft (the official docs) and is not in PowerShell 6+. The reasons given are *not* accessibility — Microsoft pivoted to VS Code with the PowerShell extension because the codebase was Windows‑only WPF, the team wanted cross‑platform, and VS Code’s editing experience is in their view superior. There is no replacement that preserves ISE’s particular niceties for blind users; VS Code is more accessible than it was, but its terminal pane is xterm.js, which is the most rendering‑heavy and least screen‑reader‑friendly terminal currently in wide deployment.

ISE is interesting for your design because its very existence and popularity with blind PowerShell users demonstrates that “command pane that is a real text control + script pane that is a real text control + run‑script‑and‑see‑result loop” is *valuable* — but it doesn’t generalize to arbitrary TUIs (ISE could not host vim), and PSReadLine famously does not work in ISE (so the interactive‑editing features your power users get in the console are absent in ISE). It is the existence proof that compose‑then‑send works for line‑oriented PowerShell.

### 9.7 PSReadLine and the screen‑reader interaction

PSReadLine is PowerShell’s command‑line editor (readline analog) — history, tab completion, syntax highlighting, predictive IntelliSense, multi‑line editing. By default, when PowerShell detects a screen reader (it queries the system accessibility state via `SystemParametersInfo SPI_GETSCREENREADER`), it *disables PSReadLine* and emits the well‑known warning (“PowerShell detected that you might be using a screen reader …”). The reason: PSReadLine constantly re‑renders the entire current line via VT sequences, which makes screen readers re‑read the whole line on every keystroke instead of just announcing the new character.

PowerShell’s GitHub has a long‑running issue (`PSReadLine#859`) requesting a “no redraw” accessibility mode that would emit only the inserted characters. As of mid‑2026 it is not the default behavior, though there has been work toward it. The practical implication: a screen‑reader user on PowerShell loses history navigation, tab completion as you type, and prediction by default. A frontend you build can re‑expose all of these through its own UI (your edit field has its own history, you can call PowerShell’s tab‑completion API directly via the `CommandCompletion.CompleteInput` static method, you can show predictions in your own list control) — and arguably this is the strongest “compose‑then‑send is better than streaming” argument for the PowerShell case specifically.

### 9.8 EdSharp and the blind‑developer Windows toolset

Jamal Mazrui’s EdSharp is a C# text editor designed from the ground up for screen reader users (NVDA, JAWS, Window‑Eyes, System Access), implementing the “Homer editor interface” first developed in JAWS scripts. It is not a terminal but is in the same intellectual line as the notebook‑as‑frontend approach: ordinary Windows text controls augmented with role‑aware speech messages, plus a corpus of file‑oriented features (multi‑file search, snippets, HTML scaffolding, brf↔text conversion) optimized for keyboard and speech. It demonstrates that “speak relevant context aggressively, using the screen reader as an output channel” produces a strong UX when applied to an editor; the same template applies to a shell frontend.

### 9.9 Multimodal CLI / voice interfaces

Research and practice in this area is thin but exists. Talon Voice (`talonvoice.com`) is the most widely used voice control system for blind and RSI users on Windows/macOS/Linux; it supports speaking commands like “term comma git status” to invoke shell commands. Mozilla’s `DeepSpeech` and Microsoft’s Speech SDK are the speech‑recognition substrates. The “Voicebot” projects (multiple by that name) and academic work like *VoiceCommand* in HCI literature explore voice‑driven CLIs — most concluded that voice is good for high‑level commands and bad for command construction (typing is faster), which suggests a hybrid where voice macros invoke commands but editing is keyboard‑driven.

## 10. Is real‑time interaction worth it?

### 10.1 What real‑time pass‑through actually buys you

**Tab completion that responds as you type.** In a streaming model, you type `git che<TAB>` and the shell’s completion engine — bash with `bash-completion`, PowerShell’s `TabExpansion2`, fish’s predictive autosuggestions, zsh’s compsys — produces completion candidates immediately. The screen reader announces the completion. This is fundamentally bidirectional and immediate; you cannot reproduce it in compose‑then‑send unless you (a) re‑implement completion in your frontend by introspecting the shell’s completion API, or (b) round‑trip every keystroke to the shell and back, which is just pass‑through with extra steps. The completion APIs *are* introspectable — PowerShell’s `[System.Management.Automation.CommandCompletion]::CompleteInput(input, cursorIndex, options)` returns full completion data without any shell roundtrip; bash exposes via `complete -p` and `compgen`; fish through its CLI — so a compose frontend can replicate completion if you put in the work, but you replicate it shell by shell.

**Ctrl‑C interrupting running commands.** Discussed in §7.3: feasible in both models, but pass‑through has the natural semantics. In compose‑then‑send you need an explicit “interrupt” control that maps to `GenerateConsoleCtrlEvent`.

**History recall (Up arrow).** Pass‑through gives this for free via PSReadLine, readline, etc. — except, as discussed, PSReadLine disables itself when a screen reader is detected, so on PowerShell+NVDA you get *less* of this than a sighted user does. Compose‑then‑send naturally gives the frontend its own history (every submitted cell is in the cell history); you press Up and re‑enter a previous cell, editable. This is *better* than terminal history in some ways (the history is visible/navigable as a document) and worse in others (it’s not unified with the shell’s own history file).

**Inline editing of the command line.** readline / PSReadLine support arrow‑key cursor movement, word‑wise movement, search‑backward, kill, yank, multi‑line editing. With a screen reader, *every keystroke triggers a re‑announcement* because the rendered line changes on screen, which is why PSReadLine is disabled. Compose‑then‑send naturally gives you a Win32 edit control with proper accessibility (announce inserted character, announce caret movement, navigation by word/line) — this is one of the *strongest* arguments for compose‑then‑send, and it is essentially the same argument that led to PowerShell ISE.

**Real‑time progress feedback (apt/curl/pip progress bars).** Pass‑through lets you see progress as it happens; compose‑then‑send sees only final output. In a sonification‑rich frontend, real‑time progress is enormously valuable for the blind user (you can hear pip downloading a 200 MB package as a pitch sweep). This is a strong pass‑through argument.

**Interactive REPLs.** Python REPL, IPython, Node, Julia, the SQLite shell. The same considerations as TUIs apply — they expect keystroke‑level input and produce streaming output. IPython is particularly relevant because *it already has a Jupyter kernel*, so the right way to use IPython from a blind‑friendly notebook is the Jupyter kernel, not the REPL. Plain Python REPL can be wrapped with `python -i -c …` for compose‑then‑send but loses readline, multi‑line editing, and the `?` introspection that makes the REPL pleasant.

### 10.2 What compose‑then‑send buys you

**Full screen‑reader‑friendly editing of the command.** The single biggest win. Move around with arrow keys, hear each character, edit, hear the result, then submit. No re‑rendering races, no rendering of color codes, no fight with PSReadLine.

**Atomic, well‑defined units for announcement.** “I just ran `ls -la`. The output is 23 lines. Press Enter to navigate.” Each cell has a beginning, end, and identity; you can name it, save it, replay it, search across the history of cells. This is a profoundly different relationship to past work than a flat scrolling buffer.

**No timing complexity for the common case.** A user enters a command, presses Enter, output arrives, user reads it. No debouncing decisions, no diff strategy choices, no interrupt vs queue debates. The complexity is in the rare cases (prompts, TUIs) rather than the common cases.

**Trivial integration with notebooks, history files, snippets, sharing.** A history of cells is exactly a notebook. You can save it as `.ipynb` or `.nb`. You can paste cells from a colleague. You can publish a “how I debugged this” document that is real, replayable shell. Streaming terminals make this awkward (you have `script(1)`/`PSReadLine`’s history, but neither is structured).

**Compatibility with mainstream a11y APIs.** Your edit control is just a Win32 / WPF / WinUI edit control with full UIA support; your output view is a read‑only text control or document. NVDA, JAWS, and Narrator all handle these well without special‑case logic. Compare to terminal accessibility where every screen reader does battle with the terminal’s custom UIA provider.

### 10.3 Workflow fit

The compose‑then‑send model fits very well:

- Composing and running individual shell commands one at a time (the bulk of routine work).
- Working with command outputs you actually need to read carefully (errors, file listings, query results).
- Saving and replaying sequences of commands.
- Working with one‑shot subcommands of programs that have CLI subcommands (`git status`, `aws s3 ls`, `kubectl get pods`).
- Driving non‑interactive build systems, test runners, package managers.
- Calling LLMs via their non‑interactive APIs (`claude -p`, `gh copilot suggest`).
- Anything that fits “I have a question, I run a command, I get an answer.”

The pass‑through model is essentially required for:

- Full‑screen editors (vim, nano, emacs ‑nw).
- Process monitors (htop, top, btop).
- Interactive selectors (fzf, sk).
- Pagers (less, more) in interactive mode.
- Long‑running TUIs (Claude Code’s interactive UI, k9s, lazygit, ranger, gh’s interactive prompts, debugger frontends like pudb).
- REPLs that don’t have alternative APIs (some database shells).
- Anything where the *value* is in the moment‑to‑moment interaction rather than the output.

The interesting cases live in the middle and have soft preferences:

- Tab completion: depends on how good a frontend‑side completion you can build for the shells you care about.
- PowerShell with PSReadLine features: arguably better in compose mode for screen reader users, *because* PSReadLine disables itself.
- Python REPL: better as a Jupyter kernel than as either alternative.
- `npm`, `pip`, `apt` with progress bars: depends on whether you value the progress feedback.

The conclusion most people who think hard about this seem to reach is that *both* are needed, that the hybrid in §3 is what you actually want, and that the design effort is in making the boundary between the two modes as graceful as possible.

## 11. What “all shells + all TUIs” actually requires, honestly

A truly unified interface — “anything that runs in a terminal works here, with first‑class screen reader and audio support” — is, when you trace through what it requires, essentially equivalent to building a screen‑reader‑first terminal emulator. The components are not optional:

1. **A ConPTY host.** Required for hosting Windows console apps faithfully. Already a Microsoft API; using it is straightforward.
1. **A VT/ANSI parser maintaining a grid model.** Required so you can answer “what is on screen now”. Libvterm or pyte will do; neither is trivial to integrate but neither is research‑grade either.
1. **Keystroke translation to VT input.** Required so vim’s arrow keys, Ctrl‑R, Esc, F‑keys, Alt‑modifiers, mouse events, and special compositions all reach the child. This is the WIN32‑INPUT‑MODE / xterm‑mode dispatch table; non‑trivial but bounded.
1. **A signal path for Ctrl‑C / Ctrl‑Break that goes through `GenerateConsoleCtrlEvent`, not just byte 0x03.**
1. **Resize handling** via `ResizePseudoConsole` when the user changes the visible window.
1. **Diff‑based announcement strategy** with debouncing, region of interest, cursor‑directed read, alt‑buffer awareness, and user‑configurable verbosity.
1. **A UIA provider** (or, alternatively, a strategy of speaking directly through the Controller Client and letting screen readers fall back to whatever they get).
1. **An audio engine** for cues and sonification at low latency.
1. **A mode‑switching detector** that recognizes the alt‑screen / TUI signal and transitions cleanly.
1. **A way to handle programs that bypass everything** — e.g. open `CONOUT$` directly and write to it. Mostly handled by ConPTY transparently, but worth verifying for your specific tools.

This is a several‑person‑year project if done well. The good news is that the building blocks exist: libvterm is mature; ConPTY is documented; the NVDA Controller Client is stable; pyte is mature; Microsoft’s `terminal` repository is open source as a reference for ambiguous corners.

### 11.1 The 80% solution

If you scope the ambition to “line‑oriented commands, simple prompts, and REPLs that have alternative non‑interactive modes,” the project is much smaller. Concretely:

- A custom edit control as the compose cell, full UIA accessibility.
- A read‑only document view for output, also fully UIA‑accessible, with both “newest at bottom” streaming and a structural mode (cells as collapsible blocks).
- A ConPTY host that runs cmd / PowerShell / WSL bash / Claude Code’s non‑interactive mode as the backend, with environment defaults set for non‑interactive behavior (`TERM=dumb`, `CI=true`, `NO_COLOR=1`, `PAGER=cat`, `PYTHONUNBUFFERED=1`).
- A side‑channel for prompts: detect “child is reading and not producing output” within a small budget, present an inline edit field for the user’s response, write it to the PTY.
- For interactive programs, *spawn Windows Terminal* with the command and the appropriate profile. Returning focus to your frontend when it exits.
- Sonification on top: command‑start, command‑end, exit‑status earcons; ambient texture during running; spatialized stdout/stderr.
- NVDA Controller Client integration for all speech, using priority levels appropriately.

This covers the great majority of CS‑professor / Claude‑Code workflow: writing and editing code, running tests, querying systems, calling AI tools via their non‑interactive APIs, reading errors, navigating output. It explicitly punts full‑screen TUIs to a separate terminal where Windows Terminal’s existing UIA support is “good enough” for those moments.

### 11.2 The 95% solution

Add a mode‑switching pass‑through path: when an alt‑screen sequence is observed (or when the user explicitly requests “interactive mode” for a known program), become a terminal emulator with libvterm/pyte underneath, forward keystrokes, and use a diff‑and‑debounce announcement strategy. On exit, go back to compose mode. The cost is the components in items 2–9 above. The benefit is that vim, fzf, htop, Claude Code’s interactive UI all work in‑place without context switching to another window.

### 11.3 The 100% solution

100% means matching what a sighted user can do in any terminal, with as much fidelity. This requires not just the components above but also:

- Proper handling of programs that detect a terminal and use it in ways your audio model has no representation for (Sixel images; the kitty graphics protocol; emoji‑in‑terminal flourishes).
- Mouse support; some TUIs use mouse essentially. NVDA users have mouse review commands; integrating these with terminal mouse modes is non‑obvious.
- Performance at the level of a dedicated terminal emulator. A `cat large_file` should not hang you.

Whether 100% is worth pursuing is a value judgment. The honest assessment from looking at TDSR, Emacspeak, and the work in the NVDA add‑on ecosystem is that 95% is achievable by one motivated person over a year or two, and that 100% requires resourcing closer to “a terminal emulator project with accessibility as a primary requirement”. The WezTerm author’s open issue on the TDSR repo (`tspivey/tdsr#19`) asking “what would a first‑class terminal screen reader experience look like” — and the resulting discussion — is the best public artifact of what such a project would entail.

### 11.4 Graceful degradation

Whichever scope you choose, design for graceful degradation:

- A program that emits an alt‑screen sequence and you do not handle it → fall back to launching it in Windows Terminal and notifying the user.
- A command whose prompt you do not detect → eventually the user notices “no output, no completion” and presses a key that sends to stdin.
- A VT sequence your parser doesn’t understand → log and skip, don’t crash.
- Audio device unavailable → all cues silent, speech still works.
- NVDA not running → fall back to SAPI directly (EdSharp’s pattern).

The point of designing the degradation explicitly is that you can guarantee the *floor* of behavior — “the user can always edit a command, press Enter, and hear the output, no matter what” — even when the upper layers fail. This is the property a CS professor whose daily work depends on this tool will want.

-----

The space you are exploring — the precise mapping from “shell I want to use” to “how it should feel as an audio experience” — is genuinely open. The mechanics above constrain what is possible, but the design choices about *what to announce when*, *what to sonify*, *what the cell/transcript model looks like*, and *how mode transitions are signaled* are aesthetic and personal as much as they are technical. The strongest existing systems (Emacspeak, TDSR, ISE for its narrow domain) all show the imprint of a single designer’s preferences. A new system built around NVDA on Windows, with ConPTY underneath and the Controller Client above, has the technical ingredients for something better than any of them. The work is in choosing well at the seams.

The report has been written in full as a series of sections. The complete report is assembled from the previously recorded sections in the order they were submitted:

1. Introduction — Architectural Tradeoffs for a Screen‑Reader‑First, Audio‑Native Interface to Windows Shells and TUIs
1. Windows PTY / ConPTY internals
1. Why compose‑then‑send cannot cleanly host interactive content
1. The hybrid middle ground
1. Screen reader integration mechanics on Windows
1. ANSI / VT parsing as an accessibility prerequisite
1. Audio and sonification as first‑class output
1. Timing and event‑loop architecture
1. The notebook‑as‑frontend model: Jupyter and Wolfram
1. Existing prior art
1. Is real‑time interaction worth it?
1. What “all shells + all TUIs” actually requires, honestly

The final closing paragraph (already included at the end of section 11) summarizes that the design space is genuinely open: the technical mechanics constrain what’s possible, but choices about announcement timing, sonification design, the cell/transcript model, and mode‑transition signaling are aesthetic decisions. The strongest existing systems (Emacspeak, TDSR, PowerShell ISE in its narrow domain) all bear the imprint of a single designer’s preferences. A new system built around NVDA on Windows, with ConPTY underneath and the NVDA Controller Client above, has the technical ingredients for something better than any of them — the work is in choosing well at the seams.