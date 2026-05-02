# Logging

pty-speak writes structured logs to per-session files under
`%LOCALAPPDATA%\PtySpeak\logs\{yyyy-MM-dd}\`. Two hotkeys make
the logs easy to grab when reporting a bug or reviewing a
session:

- **`Ctrl+Shift+L`** — open the logs folder in File Explorer.
- **`Ctrl+Alt+L`** — copy the active session's log file content
  to the clipboard as a single string. NVDA announces the byte
  count on success ("Log copied to clipboard. N bytes; ready to
  paste."). Most efficient way to send a log to a maintainer:
  press the hotkey, switch to the chat / email / issue, paste.

## Where the logs live

```
%LOCALAPPDATA%\PtySpeak\logs\
├── 2026-05-02\                                ← today's day-folder
│   ├── pty-speak-13-45-23.log                 ← session that launched at 13:45:23 UTC
│   ├── pty-speak-15-12-08.log                 ← session that launched at 15:12:08 UTC
│   └── pty-speak-16-30-44.log                 ← active session (still being written)
├── 2026-05-01\                                ← yesterday's day-folder
│   ├── pty-speak-09-15-22.log
│   └── pty-speak-22-04-11.log
└── ... (up to 7 days of day-folders)
```

- **One file per launch / session** inside a per-day folder
  named `yyyy-MM-dd` (UTC). Each session gets its own file
  named with its launch timestamp (`pty-speak-HH-mm-ss.log`,
  no colons because Windows file paths reject them).
- **7-day retention** — entire day-folders older than 7 days
  are deleted on every fresh launch. Folders with names that
  don't parse as `yyyy-MM-dd` (manual subfolders, etc.) are
  ignored.
- **Sessions don't split across midnight.** A long-running
  session's file stays in its launch-day folder; no rolling
  mid-session. The next launch creates a file in the new
  day's folder.
- The active file is opened with `FileShare.ReadWrite` so you
  can open it in Notepad, NVDA, or any other reader while
  pty-speak is still running.

## Finding the right session for a bug report

Sessions are named with their launch timestamp, so within a
day-folder they sort alphabetically in chronological order.
The latest is always alphabetically last. To grab the session
where a bug just fired:

1. Press `Ctrl+Shift+L` — File Explorer opens at
   `%LOCALAPPDATA%\PtySpeak\logs\`.
2. Open today's day-folder.
3. Sort by Name (or Date Modified) — the most recent session
   is at the bottom.
4. Open that file. It contains ONLY this session's events,
   typically a few hundred lines for a normal use; thousands
   if the session was active for a long time.
5. Copy the relevant slice (or the whole file — single-session
   files are small) and paste it in a bug report.

Future Claude-Code-on-the-machine integration can grab the
latest log with one PowerShell line:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\PtySpeak\logs\$(Get-Date -Format yyyy-MM-dd)" |
  Sort-Object Name -Descending | Select-Object -First 1
```

## Format

Each entry is one line (multi-line stack traces are appended as
extra lines and read naturally by any text reader). The shape
is:

```
2026-05-02T13:45:23.123Z [INF] [Terminal.Core.Coalescer] Started runLoop with debounce=00:00:00.2000000
2026-05-02T13:45:23.456Z [INF] [Terminal.Pty.ConPtyHost] cmd.exe spawned, pid=1234, job assigned
2026-05-02T13:45:24.789Z [WRN] [Terminal.Core.Coalescer] Spinner suppression engaged
2026-05-02T13:45:25.012Z [ERR] [Terminal.Core.Coalescer] Coalescer crashed at lastSnapshotSeq=42
System.NullReferenceException: Object reference not set to an instance of an object.
   at Terminal.Core.Coalescer.processNotification@358...
```

Levels (in increasing severity):

- **TRC** — verbose tracing (off by default).
- **DBG** — debug detail (off by default).
- **INF** — lifecycle events, hotkey invocations, PTY spawn /
  resize.
- **WRN** — recoverable issues (e.g. spinner suppression
  triggered, ConPTY pipe drain timed out).
- **ERR** — caught exceptions that didn't crash the app
  (Coalescer crash, ConPTY spawn failure, drain task exception).
- **CRT** — critical bugs that probably did crash the app.

The default minimum level is `Information` (INF). A future
Phase 2 user-settings UI will expose the toggle.

**Runtime override:** set the `PTYSPEAK_LOG_LEVEL` environment
variable to any of `Trace`, `Debug`, `Information`, `Warning`,
`Error`, `Critical`, or `None` (case-insensitive) before
launching pty-speak. Useful for the maintainer / contributors
who want verbose diagnostics during a debugging session
without waiting for the Phase 2 settings UI. Anything
unrecognised silently falls back to `Information`.

## What gets logged

- App start (version, OS, screen size, log directory).
- App exit.
- ConPTY child spawn (PID, success / failure with Win32 code).
- Hotkey invocations (which hotkey, when).
- Resize events (debounced).
- Coalescer state transitions (start, mode barriers, exit,
  exception).
- Parser / reader-loop exceptions (full stack trace).
- UIA peer events relevant to debugging (peer creation, etc.).

## What pty-speak NEVER logs

This is a security-deliberate gap, not an oversight. Never logging:

- **Typed user input** (could contain passwords, API keys,
  personal information).
- **Paste content** (clipboard could contain anything,
  including credentials).
- **Full screen contents** (might contain sensitive command
  output, file paths, secrets the shell printed).
- **Environment variables** (`GITHUB_TOKEN`, `OPENAI_API_KEY`,
  `AWS_SECRET_ACCESS_KEY`, etc. — Stage 7's env-scrub work
  will handle filtering at the parent-to-child boundary; logs
  enforce the same discipline at the file boundary).
- **Clipboard read on paste** — only the byte count of the
  paste is logged, never the bytes themselves.

`SECURITY.md` documents this as a posture commitment — the
"Logging chokepoint" entry in the mitigations section. PRs that
add log calls MUST honour this list; reviewers should reject
any log site that risks leaking these categories.

## Sharing logs with a maintainer

**Fastest path** — `Ctrl+Alt+L`:

1. Reproduce the issue (or wait for it to happen — for the
   intermittent ones).
2. **Press `Ctrl+Alt+L`.** NVDA announces "Log copied to
   clipboard. N bytes; ready to paste." The active session's
   entire log file is now on the clipboard.
3. Switch to the chat / email / issue and paste.

**Manual path** — `Ctrl+Shift+L`:

1. Press `Ctrl+Shift+L` to open the logs folder.
2. Click into today's `yyyy-MM-dd` day-folder.
3. The most recent session is alphabetically last (filenames
   are launch timestamps).
4. Open the file and copy whatever range you need.

Logs do not contain personally-identifying information beyond
your machine's username (which appears in path prefixes). If
that matters for your bug report, redact paths before sharing.

## Implementation

`src/Terminal.Core/FileLogger.fs` implements `ILogger` and
`ILoggerProvider` directly — no third-party dependencies; only
`Microsoft.Extensions.Logging.Abstractions` (which ships with
the .NET 9 SDK) is referenced. The sink runs a single
background task that drains a bounded channel and serialises
all file I/O off the WPF dispatcher / parser threads, so
logging itself never blocks the UI.

For F# call sites, the `Logger` module in the same file
provides a static accessor:

```fsharp
let private logger = Logger.get "Terminal.Core.MyModule"
logger.LogInformation("event happened, count={Count}", count)
logger.LogError(ex, "operation failed at step={Step}", step)
```

The `LogXxx` extension methods come from
`Microsoft.Extensions.Logging` and accept structured templates
the same way ASP.NET Core loggers do.
