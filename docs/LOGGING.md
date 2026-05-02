# Logging

pty-speak writes structured logs to a daily-rolling file under
`%LOCALAPPDATA%\PtySpeak\logs\`. The `Ctrl+Shift+L` hotkey opens
that folder in File Explorer so you can grab the latest log when
reporting a bug or reviewing a session.

## Where the logs live

```
%LOCALAPPDATA%\PtySpeak\logs\
├── pty-speak-2026-05-02.log    ← today's log (active)
├── pty-speak-2026-05-01.log    ← yesterday
├── pty-speak-2026-04-30.log
└── ... (up to 7 days)
```

- One file per UTC day. Daily rolling at midnight UTC.
- 7-day retention. On startup, files older than 7 days are
  deleted.
- The active file is opened with `FileShare.ReadWrite` so you
  can open it in Notepad, NVDA, or any other reader while
  pty-speak is still running.

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

When reporting a bug:

1. Reproduce the issue (or wait for it to happen — for the
   intermittent ones).
2. Press `Ctrl+Shift+L` to open the logs folder.
3. Open `pty-speak-{today}.log` in Notepad (or any reader).
4. Either:
   - Copy the relevant time-range of entries (Ctrl+A to grab
     everything; or scroll to the relevant timestamp and
     select from there), then paste in the bug report.
   - OR attach the whole file (logs are small text — typically
     a few KB to a few hundred KB per day).

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
