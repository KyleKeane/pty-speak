# Changelog

All notable changes to `pty-speak` will be documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project follows [Semantic Versioning](https://semver.org/).

Release tags follow the pattern `vMAJOR.MINOR.PATCH` (e.g. `v0.1.0`),
or `vMAJOR.MINOR.PATCH-preview.N` / `-rc.N` for prereleases.
Releases are produced by **publishing a release** in the GitHub
Releases UI (which creates the tag). The `release: published` event
triggers the Velopack release workflow described in
[`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md), which builds the
artifacts and updates the just-published release with the proper
title, body, and Velopack `Setup.exe` + nupkg + `RELEASES` files.

## [Unreleased]

### Added

- **`docs/HISTORICAL-CONTEXT-2026-05.md` — supplementary backup
  reference, NOT a primary handoff source.** Curated knowledge dump
  capturing the May-2026 cleanup cycle's guiding principles
  (cleanup before architecture; surface-don't-solve at validation
  gates; spec immutability + dated plans; letter suffixes for
  retroactive stages; historical CHANGELOG entries are frozen;
  small focused PRs over bundling; maintainer-side actions cluster;
  NVDA validation gates every stage; the maintainer's working
  constraints shape the workflow), the technology specificities
  that surfaced (F# 9 nullness at .NET-API boundaries; WPF
  dispatcher / `OnRender` / routed-event ordering; NVDA
  `MostRecent` vs `ImportantAll` activityId processing;
  `AutomationPeer.GetPatternCore` reachability; ConPTY init
  prologue + Job Object lifecycle; `PeriodicTimer` reuse bug;
  AllHashHistory spinner-gate threshold; bounded-channel +
  TCS-barrier patterns; Velopack constraints; xUnit test
  conventions), the coding paradigms specific to pty-speak
  (walking-skeleton stages; two-channel composition;
  `AnnounceSanitiser` chokepoint; `ActivityIds` as NVDA
  configuration vocabulary; `AppReservedHotkeys` filter ordering;
  OSC 52 silent-drop boundary; bracketed-paste injection defence),
  and the process patterns the cycle settled into (manual PR-create
  URL fallback; three-PR chunking for spec-numbering; CHECKPOINTS
  rows in shipping order; status-as-of header on dated docs;
  mechanical merges with content-hash verification; verify diff
  stat before commit; cross-link doc references; fixup-commit
  rhythm).

  Each entry has a "lives in" cross-reference back to the primary
  source (CONTRIBUTING.md, spec sections, CHANGELOG entries,
  module-level doc-comments, etc.). The doc is **explicitly not
  authoritative** — its job is to help a future contributor find
  a curated list rather than archaeology across git history.

  Linked from `README.md` "Quick links" + "Project layout" with
  the explicit "NOT a primary handoff source" framing per
  maintainer instruction. **Read `docs/SESSION-HANDOFF.md`
  first**; this file is the backup reference.

- **Final-handoff audit closing the May-2026 cleanup cycle.** Sweeps
  the last staleness in the entry-point docs so the next session
  picks up cleanly:

  - **`docs/SESSION-HANDOFF.md` "Where we left off" cell rewritten**
    to reflect post-cleanup-cycle state. "In-flight branch" flips
    from the long-merged `claude/audit-repo-handoff-FCsnT` to
    explicit "_None._ The May-2026 cleanup cycle is complete; next
    session starts from Part 2." "Last merged stages" gains the
    spec-formalized Stages 4a / 4b / 5a + the full PR #118 → #128
    cleanup-cycle narrative. "Next stage" rewritten to point at
    Stage 7 with explicit reference to the Stage 7 implementation
    sketch in this same file. "Last shipped release" caveat clarified
    (preview.43 is the latest code-bearing preview; subsequent PRs
    are docs-only).
  - **`docs/SESSION-HANDOFF.md` "Pending action items" #1
    expanded** from "five pending baseline tags" (Stages 0-3b) to
    twelve (5 original + 7 added per PR #127 for Stages 4 / 4a /
    4b / 5 / 5a / 6 / 11). Maintainer-side action: push the tags
    from a workstation, then delete each row from the
    `docs/CHECKPOINTS.md` "Pending checkpoint tags" table per the
    existing convention.
  - **`docs/SESSION-HANDOFF.md` closing notes** gain a paragraph
    documenting the May-2026 cleanup-cycle context (PRs #118 →
    #128 closed Part 1 + the bonus context-dump work; the
    fixup-commit rhythm was exercised on PR #121; small-focused-PR
    cadence works smoothly with squash-merge).
  - **`README.md` status block** realigned: "Stage 4.5" updates to
    "Stage 4a" (matches the spec-formalization in `spec/tech-plan.md`
    §4a); explicit mentions of newly-formalized Stages 4b and 5a
    added; cleanup is marked shipped; Stage 7 explicitly called out
    as next.
  - **`docs/PROJECT-PLAN-2026-05.md`** gains a "Status as of
    2026-05-03 cleanup cycle close" note at the top so a reader
    doesn't mistake Part 1's sub-items for live to-dos. The plan
    body below the note is preserved verbatim for decision-history
    continuity per the doc's own "Future revisions should land as
    new dated plans" rule.
  - **New `docs/STAGE-7-ISSUES.md` stub.** Pre-creates the file the
    Stage 7 implementation sketch references (per
    `docs/SESSION-HANDOFF.md` §5 of the sketch). Contains the
    framework-taxonomy category tags (`[output-stream]`,
    `[output-form]`, `[output-selection]`, `[output-earcon]`,
    `[output-tui]`, `[output-repl]`, `[input-suggest]`,
    `[input-buffer]`, `[input-form]`, `[input-nl]`,
    `[review-mode]`, `[other]`), an entry template, instructions
    for use, and explicit cross-references to the spec / plan /
    sketch. Empty entries section so the next session writes into
    a structured place without making meta-decisions.

  Closes the final context-dump candidate from the May-2026
  cleanup-cycle handoff queue. **Next session starts at Part 2 —
  Stage 7. Read `docs/SESSION-HANDOFF.md` first.**

- **`CONTRIBUTING.md` gains two session-tested practice notes** —
  one F# gotcha and one PR-workflow convention captured during the
  May-2026 cleanup cycle so future contributors don't re-learn them:

  - **F# 9 nullness annotations bite at .NET-API boundaries**
    (under "F# gotchas learned in practice"). Many .NET-API
    methods are typed `string?` under `<Nullable>enable</Nullable>`
    — `Path.GetFileName`, `Path.GetDirectoryName`,
    `Environment.GetEnvironmentVariable`, `StreamReader.ReadLine`,
    etc. Passing the result to a non-null `string` parameter
    compiles to an `FS3261` warning that becomes a build error
    under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
    Two acceptable patterns documented: helper signature accepts
    `string | null` and pattern-matches the null case (matches
    the `AnnounceSanitiser.sanitise` / `KeyEncoding.encodeOrNull`
    convention), or coerce at the call site via `nonNull` /
    inline `match`. PR #121 (Issue #107 filename refinement) hit
    this and is the worked example.

  - **CI failure on an open PR → push a fixup commit to the same
    branch** (under "Branching and pull requests"). GitHub PRs
    track the branch HEAD, not a snapshot at PR-creation time;
    pushing additional commits auto-extends the PR and re-runs
    CI without disturbing the PR number, title, body, or
    `Closes #N` references. **Don't open a new PR for a fixup**
    — the squash-merge convention combines original + fixup
    into a single canonical commit on `main`. PR #121 (same
    Issue #107 work) used this rhythm and is the worked
    example.

  Both bullets cross-reference each other so a reader landing on
  either entry finds the other.

- **`docs/CHECKPOINTS.md` checkpoint rows for shipped post-Stage-3b
  stages.** The 2026-05-03 audit (in
  [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md))
  flagged a "Post-Stage-3b checkpoint rows pending" hygiene gap:
  Stages 4, 4a, 4b, 5, 5a, 6, and 11 had all merged to `main` and
  shipped in maintainer-tested previews, but no `baseline/stage-N`
  rollback-tag rows existed. This PR fills the gap with seven new
  rows in shipping order:

  - `baseline/stage-4-uia-document-text-pattern` — anchor PR #68
    (real word-navigation closing the Stage 4 follow-up).
  - `baseline/stage-11-velopack-auto-update` — anchor PR #66
    (window-title version suffix + structured update-failure
    messages).
  - `baseline/stage-4b-process-cleanup-diagnostic` — anchor PR #81
    (`Ctrl+Shift+D` diagnostic launcher).
  - `baseline/stage-4a-claude-code-substrate` — anchor PR-B #86
    (alt-screen 1049 back-buffer; Stage 4a complete with PR-A #85
    + PR-B #86).
  - `baseline/stage-5-streaming-coalescer` — anchor PR #89
    (Coalescer module + alt-screen flush barrier + Acc/9 OnRender
    lock fix bundled).
  - `baseline/stage-6-keyboard-input` — anchor PR #100 (post-Stage-6
    stability fixup completing PR-A #92 + PR-B #99).
  - `baseline/stage-5a-diagnostic-logging` — anchor PR #122
    (FlushPending; the most recent constituent that completes the
    Stage 5a scope per spec §5a).

  Each row includes a paragraph-length scope description matching
  the existing Stage 0–3b row tone (PR links + release link where
  applicable + technical narrative). Each row also has a matching
  "Pending checkpoint tags" entry with the exact `git tag -a SHA -m`
  + `git push` commands the maintainer runs from a workstation
  (the dev sandbox proxy returns 403 on tag pushes per
  `docs/SESSION-HANDOFF.md` "Sandbox + tools caveats"). The
  obsolete "Post-Stage-3b checkpoint rows pending" notice is
  removed.

  No code touched. Pure docs hygiene closing out the audit
  branch's flagged TODO.

- **Stage 7 implementation sketch in `docs/SESSION-HANDOFF.md`.**
  The next session that picks up Part 2 of the May-2026 plan
  (Claude Code roundtrip + env-scrub PO-5 — the validation gate
  before the Output / Input framework cycles) gets a
  pre-digested execution plan parallel to the existing Stage 4
  / Stage 11 sketches in the same file. ~250 lines covering:

  - **Why Stage 7 is the validation gate** — the framework
    cycles need ground-truth signal from the primary target
    workload before they can be designed coherently.
  - **Implementation outline** — `claude.exe` resolution
    (`where.exe claude` with cmd.exe fallback), configurable
    shell via `PTYSPEAK_SHELL` env var, child-process
    environment block construction via `lpEnvironment` to
    `CreateProcess` (allow-list-with-deny-list-override scheme
    for PO-5 env-scrub), NVDA validation flow, Stage 7 issues
    inventory format (`docs/STAGE-7-ISSUES.md` with
    framework-taxonomy category tags as design input for
    Parts 3 + 4).
  - **Pre-digested decisions** — cmd.exe stays default;
    `ANTHROPIC_API_KEY` in allow-list; env-scrub log line
    counts but never names/values per `SECURITY.md` logging
    discipline; no spec-§7-deltas without ADR-style
    authorization.
  - **Critical files to touch** + **existing primitives to
    reuse** + **what this stage deliberately does NOT do** +
    **known risks** (F# string-block marshalling silently
    fails; Claude Code's NVDA experience may already exceed
    the coalescer's capacity; spawned-Claude lifecycle
    differs from cmd.exe; `where.exe claude` may resolve a
    stale wrapper).
  - **Scope discipline** — one PR with the env-scrub
    potentially as a fixup; STAGE-7-ISSUES.md grows over
    multiple NVDA verification cycles but doesn't block PR
    merge.

  Reading-order item 4 in the same file updates: §1-§6 are
  fully shipped, §4a/4b/5a are retroactively-formalized
  shipped stages, §7 is the next stage with the
  implementation plan in this file's "Stage 7 implementation
  sketch (next)" section.

- **`FileLoggerSink.FlushPending(timeoutMs)` API.** New public
  member that returns a `Task<bool>` completing when the
  background drain finishes its next per-batch flush, or after
  the timeout — whichever comes first. `true` means a flush
  completed within the window; `false` means the timeout fired
  (channel was idle, or the host pegged for longer than the
  budget).

  Implementation: a TCS-barrier owned by the sink. The drain
  loop atomically swaps the current `flushTcs` for a fresh one
  after every successful `StreamWriter.Flush` and completes
  the swapped one — so a caller that captures the current TCS
  and awaits it gets signalled the next time the drain
  completes a flush. Lock-protected swap; idempotent
  `TrySetResult`; signalled once more after the dispose-time
  final flush so callers awaiting at shutdown see completion
  rather than timeout.

  **Wired into `runCopyLatestLog` (`Ctrl+Shift+;`)** with a
  500ms budget. Without this barrier, the bounded channel
  could hold ~milliseconds of recent entries that hadn't been
  written yet — the clipboard would capture a stale snapshot
  of the file. The 500ms cap is the worst-case dispatcher
  block under user-pressed-the-hotkey conditions; in practice
  the drain finishes in low ms. On timeout, the handler logs
  an `Information`-level note and proceeds with the
  not-quite-current file content (better than no copy at all).

  Caveat: if the channel is fully idle (no pending entries),
  the drain loop is parked in `WaitToReadAsync` and won't fire
  a flush until something arrives. `FlushPending` returns
  `false` (timeout) in that case — but the file already
  contains everything the writer has produced, so the
  not-drained path is benign.

  Test:
  `tests/Tests.Unit/FileLoggerTests.fs` gains
  `FlushPending makes recently-enqueued entries readable while
  the writer is active` — enqueues 5 entries with a unique
  marker, calls `FlushPending(2000)`, then reads the file
  with `FileShare.ReadWrite` (matching `runCopyLatestLog`'s
  production path) WITHOUT disposing the sink first, and
  asserts every entry made it to disk. Failure here means
  the drain's `signalFlushComplete` wiring or the TCS-swap
  path regressed.

- **Strategic plan committed to repo:
  [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md).**
  Captures the post-PR-#116 architecture review and sequences
  the next ~8-12 weeks of work as **Part 1** Cleanup → **Part 2**
  Stage 7 Claude Code roundtrip + env-scrub PO-5 (validation
  gate) → **Part 3** Output-handling framework cycle (subsumes
  original Stages 8 + 9 as Selection profile + earcons sink) →
  **Part 4** Input-interpretation framework cycle (parallel to
  Part 3; bridges to it via echo-correlation API) → **Part 5**
  Stage 10 review mode + quick-nav (first non-built-in consumer
  of the framework's semantic-event taxonomy). The plan
  supersedes `spec/tech-plan.md`'s Stage 7-10 ordering
  specifically; the spec remains immutable as architectural
  rationale. `docs/SESSION-HANDOFF.md`, `docs/ROADMAP.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`, and `README.md`
  cross-link the plan as the canonical source for the next
  several months of work, so a fresh session (Claude or human)
  can pick up the work without re-deriving the rationale.

- **Repo-wide handoff doc freshness sweep.** Bundled with the
  plan-doc commit:
  - `docs/SESSION-HANDOFF.md` — Stage 11 implementation sketch
    relabeled "shipped — retained as reference" (parallel to
    the existing Stage 4 sketch); pending-action-items 5.1
    (PO-5 env-scrub) reframed via May-2026 plan Part 2;
    item 6 (diagnostic-launcher native replacement) marked
    actionable now that Stages 5 + 6 have shipped; item 7
    (stale-branch bulk-delete) count refreshed (~100, was 77).
  - `docs/CHECKPOINTS.md` — added "Post-Stage-3b checkpoint
    rows pending" notice listing missing checkpoint rows for
    Stages 4, 4.5, 5, 6, and 11.
  - `SECURITY.md` — PO-5 row reframed from "accepted risk /
    defer" to "planned / sequenced as plan Part 2"; TC-5 row
    marked **shipped** since Stage 5's `Coalescer` confirmed
    routes per-row announcements through `AnnounceSanitiser`
    (verified at `src/Terminal.Core/Coalescer.fs:178`); the
    PRs that add log calls clause cross-links the plan.
  - `docs/USER-SETTINGS.md` — verbosity section rewritten
    around the May-2026 plan's per-profile output-framework
    taxonomy (Off / Smart / Verbose preserved as a power-user
    override per profile, not the primary surface); Stage 9
    audio section flagged as subsumed into Part 3 Stage G.

- **`Ctrl+Shift+;` copies the active session's log file content
  to the clipboard.** Bundled with the logging-restructure
  work. Pressing the hotkey reads the active log file (the one
  `FileLoggerSink.ActiveLogPath` points to), sets the OS
  clipboard, and announces the byte count via NVDA ("Log
  copied to clipboard. N bytes; ready to paste."). Fastest
  path to send a session log to a maintainer for bug-report
  diagnosis — no File Explorer navigation required.

  Hotkey-choice rationale. The semicolon / colon key sits
  immediately to the right of `L` on a US-layout keyboard,
  so it pairs by physical proximity with `Ctrl+Shift+L`
  (open logs folder) — same hand position, two adjacent keys.
  Other candidates considered and declined: `Ctrl+Alt+L` (the
  original; collides with the Windows Magnifier zoom-in
  shortcut, AND the `Alt`-modifier path through WPF's input
  pipeline required a SystemKey-aware filter that broke
  `Alt+F4`); `Ctrl+Shift+C` (the cross-terminal "copy"
  convention but reserved here for a future
  copy-latest-command-output feature, plus today it folds to
  `0x03` / SIGINT in the keyboard encoder — claiming it
  would lose the `Ctrl+Shift+C`-as-interrupt habit some
  users have); `Ctrl+Shift+M` (stays reserved for the Stage 9
  earcon mute toggle). Layout caveat: on non-US keyboards
  the `OemSemicolon` virtual-key sits in a different
  physical position; remap support is on the Phase 2
  user-settings roadmap.

  Added to `AppReservedHotkeys`; wired in
  `setupCopyLatestLogKeybinding` in `Program.fs`. The handler
  catches and announces clipboard exceptions (the OS clipboard
  can transiently throw COMException under contention; one
  failed attempt becomes an audible error rather than a silent
  no-op).

  Documentation: README, USER-SETTINGS.md, and LOGGING.md
  updated with the new hotkey, the rationale, and a refreshed
  "Sharing logs with a maintainer" section that promotes
  `Ctrl+Shift+;` as the fastest path.

- **File-based structured logging.** New
  `Terminal.Core/FileLogger.fs` implements `ILogger` /
  `ILoggerProvider` directly against
  `Microsoft.Extensions.Logging.Abstractions` (the
  first-party SDK package — no Serilog or other third-party
  dependency added). A single background task drains a bounded
  channel, formats entries, and appends to
  `%LOCALAPPDATA%\PtySpeak\logs\pty-speak-{date}.log`. Daily
  rolling, 7-day retention, off-thread writes so the WPF
  dispatcher never blocks on disk.

  New `Ctrl+Shift+L` hotkey opens the logs folder in File
  Explorer for one-keypress retrieval when reporting bugs.
  Added to `AppReservedHotkeys`; wired in
  `setupOpenLogsKeybinding` in `Program.fs`. The
  `runOpenLogs` handler uses the same announce-before-launch
  pattern as the other window-spawning hotkeys so NVDA's
  speech queue gets ~700ms before File Explorer steals focus.

  Default log level: **Information**. Off-by-default trace
  levels (Trace, Debug) reserved for verbose troubleshooting;
  Phase 2 user-settings will surface a toggle.

  Initial log calls land at the diagnosis-critical points:

  - App startup (version, OS, log directory).
  - `compose ()` lifecycle.
  - ConPTY child spawn (success with PID, failure with full
    error variant).
  - Coalescer.runLoop entry, clean-cancel exit, and exception
    path (this is the path that will catch the post-Stage-6
    intermittent "Coalescer crashed" we still haven't pinned
    down).
  - Drain task crash path (alongside the existing
    `Announce(..., pty-speak.error)` user-facing notice).
  - App exit.

  **Security posture:** new "Logging chokepoint" entry in
  `SECURITY.md`. The call-site discipline NEVER logs typed
  user input, paste content, full screen contents, or
  environment variables. Same first-class status as the
  `AnnounceSanitiser` chokepoint from audit-cycle SR-2.

  **Documentation:** new `docs/LOGGING.md` covers location,
  format, retention, what's logged, what isn't, and how to
  share log slices with a maintainer.

  **Tests:** 8 new `FileLoggerTests` pinning the contract:
  Information entries land in today's file with the
  documented format; minimum-level filtering drops below-min
  entries; exception details land in the file; retention
  sweep deletes >7-day-old files on startup; log directory
  is created on demand; `LogDirectory` member exposes the
  path; `Logger.get` returns a `NullLogger` before
  `Logger.configure` runs; the configured factory's logger
  produces correctly-categorised output.

- **Stage 6 PR-B: keyboard input, paste, focus reporting, dynamic
  resize, and Job Object child-process lifecycle.** Second and
  final half of Stage 6 — pty-speak becomes interactive. Typed
  keys reach the cmd.exe child via the new pure-F# `KeyEncoding`
  module; paste via Ctrl+V / right-click / Edit menu wraps in
  bracketed-paste markers when DECSET ?2004 is set; window resize
  flows through to `ResizePseudoConsole` after a 200ms debounce;
  the spawned child plus any process it later spawns are
  contained in a Job Object so the entire tree dies via
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` even on a hard parent
  crash.

  Component pieces:

  - **New `Terminal.Core.KeyEncoding` module** — pure F# encoder
    `KeyCode * KeyModifiers * TerminalModes -> byte[] option`.
    Decoupled from `System.Windows.Input.Key` via its own
    `KeyCode` discriminated union and `KeyModifiers` flags type
    so a future Linux / macOS port (Avalonia, MAUI, web) reuses
    this module unchanged — only the WPF→KeyCode adapter changes.
    Encoding tables follow the xterm "PC-Style" + "VT220-Style"
    function-key conventions: arrows are DECCKM-aware (`\x1b[A`
    normal vs `\x1bOA` application), modified cursor keys use
    the SGR-modifier protocol (`\x1b[1;<mod>A`), F1-F4 use SS3
    form (`\x1bO<P/Q/R/S>`), F5-F12 use CSI form
    (`\x1b[<n>~`), Ctrl-letter folds Shift, Alt-letter
    ESC-prefixes, Backspace sends DEL (`0x7f`, modern xterm
    default that bash / zsh / PowerShell / Claude Code all
    expect). The `KeyCode.Unhandled` case is the
    future-proofing escape hatch — any unknown key produces
    `None` rather than a crash; new WPF Key values can ship
    without breaking us.

  - **Bracketed-paste handler** bound to
    `ApplicationCommands.Paste` so Ctrl+V, right-click → Paste,
    and Edit menu → Paste all flow through one site.
    `KeyEncoding.encodePaste` strips embedded `\x1b[201~` from
    clipboard content **before** wrapping — paste-injection
    defence diverging from xterm's permissive default. NVDA
    users can't easily inspect their clipboard before pasting,
    so an attacker-crafted paste containing `\x1b[201~`
    followed by a malicious command would otherwise close the
    bracket-paste frame early and execute the post-paste
    portion as if typed. SECURITY.md tracks this as a
    deliberate accessibility-first posture divergence.

  - **Focus reporting** via `OnGotKeyboardFocus` /
    `OnLostKeyboardFocus`. Emits `\x1b[I` / `\x1b[O` to the
    child only when DECSET ?1004 is set. Editors like nano /
    vim / Emacs and Claude Code use these to suspend cursor
    blink, save unsaved buffers on focus loss, etc.

  - **Dynamic resize** via `OnRenderSizeChanged` →
    `DispatcherTimer` (200ms trailing-edge debounce) →
    `ConPtyHost.Resize` → `Win32.ResizePseudoConsole`. WPF
    SizeChanged fires per pixel during a window drag (60Hz);
    debouncing prevents the child shell from re-laying-out on
    every tick and flooding Stage 5's output coalescer.
    Hardcoded `// TODO Phase 2: TOML-configurable` constant.
    Note: Stage 6 resizes the **PTY** (so the child shell sees
    the new column count); the in-process `Cell[,]` Screen
    grid stays at construction-time 30×120, so oversize
    windows have empty padding and undersize windows clip.
    Full grid runtime resize is logged as a Phase 2 stage in
    `docs/SESSION-HANDOFF.md`.

  - **Job Object child-process lifecycle.** `Native.fs` adds
    P/Invokes for `CreateJobObjectW`,
    `SetInformationJobObject`, `AssignProcessToJobObject` plus
    the `JOBOBJECT_BASIC_LIMIT_INFORMATION` /
    `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` / `IO_COUNTERS`
    structs and a `SafeJobHandle`. `PseudoConsole.create`
    creates a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
    set, assigns the immediate cmd.exe to it, and stores the
    handle on `PtySession.JobHandle`. **Layered on top of**
    the existing `TerminateProcess` cleanup rather than
    replacing it: `TerminateProcess` is fast targeted cleanup
    for the immediate cmd.exe (so its pipe drains promptly);
    the Job Object is the kernel-enforced safety net for
    grandchildren (e.g. a `node` process Stage 7's Claude
    Code launches inside pty-speak). On any setup-step
    failure the orphan child is terminated before returning
    Error so we never leak.

  - **`OnPreviewKeyDown` filter ordering** is load-bearing
    and pinned by inline doc-comment + the test suite:
    1. `AppReservedHotkeys` check first (Ctrl+Shift+U /
       Ctrl+Shift+D / Ctrl+Shift+R short-circuit and let
       the parent Window's `InputBindings` see them).
    2. NVDA / screen-reader modifier filter second (bare
       Insert / CapsLock and Numpad-with-NumLock-off
       return without `Handled`).
    3. WPF Key + ModifierKeys → `KeyCode` + `KeyModifiers`
       translate.
    4. Plain printable typing (letters / digits / space
       without Ctrl or Alt) defers to `OnPreviewTextInput`
       so WPF's text-composition pipeline (IME, AltGr,
       dead keys) handles it correctly.
    5. Encode + write via `KeyEncoding.encodeOrNull`.

  - **NVDA / screen-reader compatibility.** The bare
    Insert + CapsLock filter covers NVDA, JAWS, and Narrator
    modifier keys uniformly. The Numpad-with-NumLock-off
    filter covers NVDA's review-cursor numpad layout.
    Conservative on purpose — the cost of a few key presses
    not reaching the shell is tiny compared to the cost of
    breaking screen-reader navigation.

  - **`AppReservedHotkeys` refresh** in
    `src/Views/TerminalView.cs` — was stale (only listed
    Ctrl+Shift+U); now lists all three currently-shipped
    hotkeys (Ctrl+Shift+U, Ctrl+Shift+D, Ctrl+Shift+R) plus
    the future-reserved Ctrl+Shift+M (Stage 9) and
    Alt+Shift+R (Stage 10) as comments.

  - **`Program.fs compose ()`** — wires the new host into
    `TerminalView.SetPtyHost` after spawn. The view takes
    `Action<byte[]>` + `Action<int,int>` callbacks rather
    than a direct `ConPtyHost` reference so `Views/`
    intentionally doesn't take a project ref on
    `Terminal.Pty` (preserves the F#-first / WPF-only-at-
    the-edge boundary). All callbacks invoke on the WPF
    dispatcher thread, which is also the only thread that
    touches the ConPTY stdin pipe — single-writer
    discipline by construction.

  Tests:

  - **New `tests/Tests.Unit/KeyEncodingTests.fs`** (~35
    facts) pinning the entire encoding table: cursor keys
    (DECCKM normal vs application vs modified); editing
    keypad (Insert / Delete / Home / End / PageUp /
    PageDown); F1-F12 (SS3 vs CSI form, modified
    variants); Tab / Enter / Esc / Backspace; Ctrl-letter
    folding Shift; Alt-letter ESC-prefix; Ctrl+`@`/`[`/
    `?` mapping to NUL/ESC/DEL; non-ASCII char returns
    None; Unhandled returns None; bracketed paste
    wrapping + injection defence; Unicode UTF-8
    survival; focus-reporting bytes; SGR-modifier
    parameter encoding; `encodeOrNull` C#-friendly
    wrapper.

  - **`tests/Tests.Unit/ConPtyHostTests.fs`** extended
    with two new Stage 6 facts: `ConPtyHost.Resize`
    accepts new dimensions without erroring; the
    `JobHandle` is non-null and not-invalid after
    spawn (proves the Job Object setup path works).

- **Stage 6 PR-A: parser arms for DECCKM, bracketed paste, and
  focus reporting.** The first half of Stage 6 lands the
  parser-side mode-flag plumbing for the three remaining
  `TerminalModes` flags that were declared-but-inert in
  Stage 4.5: `DECCKM` (`?1`, application vs normal cursor
  mode), `BracketedPaste` (`?2004`), and `FocusReporting`
  (`?1004`). Each new arm in
  `src/Terminal.Core/Screen.fs csiPrivateDispatch` follows
  the Stage 4.5 alt-screen template exactly: idempotence
  guard (a no-op flip silently returns) + flag mutation +
  `pendingModeChanges.Add` for the post-lock-release
  `ModeChanged` event fire that Stage 5's coalescer
  subscribes to. The flags are now toggleable from
  child-shell escape sequences but the consumer-side
  behaviour (key encoder reading `Modes.DECCKM`, paste
  handler reading `Modes.BracketedPaste`, focus events
  reading `Modes.FocusReporting`) lands in Stage 6 PR-B
  alongside the WPF input wiring + `ResizePseudoConsole` +
  Job Object lifecycle. PR-A is pure F#, no WPF, no Win32
  — splitting the stage along that line lowers review cost
  and keeps the bisect surface small if NVDA validation
  catches something later.

  Tests: 15 new `ScreenTests` (3 modes × 4 cases:
  set-fires, reset-fires, idempotent-set-no-fire,
  idempotent-reset-no-fire — plus 3 cross-flag
  independence tests defending against future
  shared-backing-field refactors). Templates from the
  Stage 5 alt-screen `ModeChanged` test triplet.

- **Stage 5: streaming-output coalescer.** First stage where
  PTY output narrates itself — before Stage 5, the only NVDA
  flow was review-cursor exploration (the user navigated to
  new content); after Stage 5, NVDA reads streaming output
  line-by-line at conversational pace as the PTY produces it.
  Per `spec/tech-plan.md` §5: "When PTY output arrives, NVDA
  reads it aloud at conversational pace. Spinner doesn't
  flood. Multi-line output is announced line by line."

  Implementation:

  - New `Terminal.Core.Coalescer` module
    (`src/Terminal.Core/Coalescer.fs`) sits between the
    parser-side `notificationChannel` (256, DropOldest) and
    a new `coalescedChannel` (16, Wait). Reads every
    `ScreenNotification` the parser publishes, applies
    debounce + dedup + spinner suppression, and emits at
    most one `CoalescedNotification` per ~200ms window.

  - **Per-row + frame hash** via FNV-1a 64-bit. Per-row hash
    folds the row index in so a row swap can't alias to the
    same frame hash; frame hash XORs per-row hashes.
    Two consecutive `RowsChanged` events with identical
    screen content produce identical frame hashes and the
    second is suppressed entirely (composes with Claude
    Ink's full-frame redraws — without this, every row
    hash changes per redraw and per-row dedup never fires).

  - **Spinner heuristic**: sliding window keyed by
    `(rowIdx, hash)` with a 1s window and threshold of 5
    same-key hits, plus a generic "any-hash high-frequency
    anywhere" gate at 4× threshold. Suppresses repeated
    frames at high rate (`|/-\` spinners, etc.).

  - **Debounce**: leading-edge + trailing-edge. First event
    in an idle period (no flush in last 200ms) emits
    immediately for fast single-event UX (`echo hello`);
    subsequent events accumulate and drain on the next
    200ms timer tick.

  - **Alt-screen flush barrier**: new
    `ScreenNotification.ModeChanged(flag, value)` case
    + `TerminalModeFlag` discriminator added to
    `Terminal.Core.Types`. `Screen.enterAltScreen` /
    `exitAltScreen` now queue `(AltScreen, true/false)` into
    a `pendingModeChanges` buffer under the gate; `Apply`
    drains the buffer AFTER releasing the lock and fires
    a new `[<CLIEvent>] ModeChanged` event. The coalescer
    subscribes (via `Program.fs compose ()`) and on
    `ModeChanged` flushes any pending accumulator first,
    resets frame-hash + spinner state, then passes the
    barrier through. Stage 6 will reuse the same shape for
    DECCKM, bracketed paste, and focus reporting.

  - **Sanitisation**: every emit text passes through the
    audit-cycle SR-2 `AnnounceSanitiser.sanitise`
    chokepoint per row, then rows are joined with `\n` so
    NVDA's per-line speech pause survives (the bug-prone
    naive "sanitise the whole joined string" path would
    have stripped `\n` as a C0 control and collapsed
    multi-line output into a single line).

  - **Activity IDs**: new `Terminal.Core.ActivityIds`
    module providing the stable
    `pty-speak.{output,update,error,diagnostic,releases,mode}`
    vocabulary so NVDA users can configure per-tag
    handling (e.g. quieter speech for the install flow vs.
    streaming text). The new
    `TerminalView.Announce(message, activityId)` overload
    lets the drain pass the right tag per
    `CoalescedNotification` shape.

  - **Two-channel composition**: `Program.fs compose ()`
    now starts the coalescer as a `Task.Run` with the
    SHARED `cts.Token` (single CTS, unified
    cancellation across reader, coalescer, and drain).
    Production passes `TimeProvider.System`; tests
    inject `FakeTimeProvider` for deterministic
    debounce assertions.

  - **`TimeProvider` injection**: `Coalescer.runLoop`
    accepts `TimeProvider`; new
    `Microsoft.Extensions.TimeProvider.Testing` package
    pinned at 9.0.0 in `Directory.Packages.props` so
    `CoalescerTests` can advance time without
    `Thread.Sleep`.

  - **`Acc/9` OnRender lock fix bundled** (per the
    SESSION-HANDOFF item 5.3 commitment).
    `src/Views/TerminalView.cs`'s `OnRender` previously
    called `_screen.GetCell(row, c)` per cell, which
    re-entered the screen gate up to `Rows*Cols` times
    per render frame and could race with the parser
    thread between cells. Refactored to take ONE
    `_screen.SnapshotRows(0, _screen.Rows)` snapshot at
    the start of the frame and walk that immutable copy
    in `RenderRow` / `DrawRun`. Single gate acquisition
    per render; no measurable perf cost. SESSION-HANDOFF
    item 5.3 flips from "deferred — Stage 5 will revisit"
    to "✓ resolved by Stage 5".

  Tests:

  - New `tests/Tests.Unit/CoalescerTests.fs` (24 facts)
    pinning every algorithm independently
    (hash equality / row-swap defence; frame dedup;
    leading- / trailing-edge debounce; per-key spinner
    gate firing + GC release; mode barrier flush +
    state reset; `ParserError` pass-through with
    sanitisation; `renderRows` per-row sanitise +
    `\n` preservation + trailing-blank trimming;
    activity-ID vocabulary pinning; `runLoop`
    cancellation cleanup; `runLoop` end-to-end with
    real `Screen` + `FakeTimeProvider`).

  - `tests/Tests.Unit/ScreenTests.fs` extended with
    five new `ModeChanged` event tests (fires on
    enter; fires on exit; idempotent enter / exit
    do NOT fire; subscriber can call `SnapshotRows`
    without deadlock — pins the post-lock fire
    contract Stage 5's coalescer relies on).

  Out of scope (deferred per the approved Stage 5 plan):

  - **Verbosity profiles** (off / smart / verbose) —
    Phase 2 TOML config. Stage 5 ships hardcoded
    200ms debounce + 1s spinner-window with
    `// TODO Phase 2` comments at each constant.

  - **`ITextProvider2` / `TextChangedEvent`** —
    explicitly forbidden per spec §5.6 (NVDA disables
    TextChanged for terminals to prevent
    double-announce).

  - **`TermControl2` className** in
    `TerminalAutomationPeer.GetClassNameCore` — could
    signal NVDA's terminal-app heuristics; not
    validation-required; flag for any later stage that
    touches the peer.

  - **Per-event-class `activityId`s for the diagnostic
    / releases hotkeys** — today they pass through the
    default `pty-speak.update` tag. Stage 5 adds the
    vocabulary; whoever next touches those hotkeys can
    flip them.

  - "Command output complete" prompt-redraw signal —
    strategic review §G assigned this to Stage 8.

  - Stage 6 keyboard input + Stage 7 Claude Code
    roundtrip + Stage 8/9/10 features — separate
    stages.

- **Stage 4.5 PR-B: alt-screen 1049 back-buffer.** Closes the
  last latent gap in the Claude Code rendering substrate.
  Claude's Ink reconciler — and many other modern TUIs (`less`,
  `vim`, `fzf`, `git log` pager, npm install's progress bars)
  — sends `\x1b[?1049h` on startup to enter the alternate
  screen and `\x1b[?1049l` on exit. Without alt-screen support,
  the primary buffer's scrollback would get corrupted on every
  alt-screen TUI launch; with it, the primary content is
  preserved untouched and the screen reader can navigate
  whichever buffer is active at the moment.

  Implementation:

  - `Screen` now holds two `Cell[,]` buffers (`primaryBuffer`,
    `altBuffer`) and a `mutable activeBuffer` field that
    points at one of them. Every cell read / write site
    (`printRune`, `executeC0` for BS/HT/LF/CR, `eraseDisplay`,
    `eraseLine`, `csiDispatch` cursor moves, `SnapshotRows`,
    `GetCell`) was migrated from `cells.[r, c]` to
    `activeBuffer.[r, c]` in one mechanical rename.

  - `csiPrivateDispatch` (added by PR-A) now handles
    `?1049h` → `enterAltScreen ()` and `?1049l` →
    `exitAltScreen ()`. Both functions are idempotent: a
    repeated `?1049h` while already in alt-screen is a
    no-op; a repeated `?1049l` while already on primary is
    a no-op.

  - **Save/restore semantics match xterm `?1049`**: on enter,
    the cursor row / col / SGR attrs are captured into a
    `savedPrimary: (int * int * SgrAttrs) option` field;
    `activeBuffer` is repointed at `altBuffer`; the alt
    buffer is cleared (xterm convention — alt-screen always
    starts blank); cursor moves to (0, 0) with default
    attrs. On exit, the saved state is restored and
    `activeBuffer` is repointed at `primaryBuffer`. Primary
    cells are *never copied* — they sit unchanged in
    `primaryBuffer` because nothing wrote to them during
    the alt session.

  - **`Modes.AltScreen` flag** flips with the swap so future
    consumers (UIA peer announcing buffer changes, Stage 5
    coalescer needing flush barriers, etc.) can read it.

  - **`SequenceNumber` bumps on `?1049h/l`** as a side
    effect of every `Apply` call. Stage 5's coalescer
    should treat alt-screen toggles as a hard
    invalidation barrier — flush the debounce window,
    then resume — because the row content can change
    wholesale between buffers and a debounce window
    straddling a swap would mis-attribute rows. The PR-B
    test `SequenceNumber bumps on ?1049h and ?1049l` pins
    the contract for Stage 5's author to read.

  9 new tests in `tests/Tests.Unit/ScreenTests.fs` exercise:
  the AltScreen flag toggle; primary content preservation
  across alt-screen entry/exit; alt-buffer reset on every
  entry; cursor + attrs reset on enter and restore on
  exit; idempotency of double-enter and double-exit;
  `SnapshotRows` returning alt content during alt mode and
  primary content after exit; and the `SequenceNumber`
  bump on toggle.

  **Stage 4.5 cycle complete with this PR**: the substrate
  is in place for Stage 7 to actually run Claude Code. Next
  is Stage 5 (streaming output notifications) — the
  coalescer plugs into the `ScreenNotification` channel
  seam shipped in audit-cycle PR-B and uses the `Modes`
  bits + `SequenceNumber` bumps that Stage 4.5 PR-A and
  PR-B established.

- **Stage 4.5 PR-A: VT mode coverage + SGR table fills.**
  Closes the latent gap where the parser correctly emits
  events that `Screen.fs` silently dropped at its `_ -> ()`
  catch-all arms. Without these, Stage 7's Claude Code
  roundtrip fails because Claude's Ink reconciler sends
  `?25l` (hide cursor) + truecolor SGR + DECSC/DECRC on
  every state change. PR-B will follow with alt-screen
  1049 (the architectural piece — separate buffer +
  swap dispatch); PR-A is the catch-all-arm fills plus
  the substrate (`TerminalModes` record, private-CSI /
  ESC dispatch split, SGR walker refactor) that PR-B
  plugs into.

  - **`TerminalModes` record** in `Terminal.Core/Types.fs`
    centralises the mode bits Stage 5/6/7 need. Wired
    today: `CursorVisible` (DECTCEM `?25h/l`). Stubbed for
    Stage 6: `DECCKM`, `BracketedPaste`, `FocusReporting`.
    `AltScreen` is wired by PR-B.

  - **`Cursor` record refactored.** Dropped the dead
    `Visible: bool` field (single source of truth via
    `Modes.CursorVisible`). Replaced
    `SaveStack: (int * int) list` with
    `SaveStack: CursorSave list` where `CursorSave` is a
    record `{ Row; Col; Attrs }` so DECSC also saves SGR
    attrs (matches xterm convention; forward-compatible
    for Stage 6 origin mode / character-set selection).

  - **`InternalsVisibleTo("PtySpeak.Tests.Unit")`** added
    to `Terminal.Core` (top of `Types.fs`) so tests can
    introspect `TerminalModes` flags, `Cursor.SaveStack`
    depth, and the alt-screen back-buffer that PR-B will
    add. Mirrors the precedent in
    `Terminal.Accessibility/TerminalAutomationPeer.fs:22-23`.

  - **DECTCEM (`?25h` / `?25l`)** wired via a new
    `csiPrivateDispatch` helper in `Screen.fs`, dispatched
    from `Apply`'s `CsiDispatch` arm when the parser
    passes the `?` private marker. Public-marker CSI
    sequences continue to flow through the existing
    `csiDispatch`. Stage 6 will plug DECCKM (`?1`),
    bracketed paste (`?2004`), and focus reporting
    (`?1004`) into the same `csiPrivateDispatch`.

  - **DECSC (`ESC 7`) and DECRC (`ESC 8`)** wired via a
    new `escDispatch` helper in `Screen.fs`. DECSC pushes
    `{ Row; Col; Attrs }` onto `Cursor.SaveStack`. DECRC
    pops and restores; on empty stack, restores to
    (0, 0) with default attrs (xterm convention).

  - **256-colour SGR (`\x1b[38;5;n m` / `\x1b[48;5;n m`)
    and truecolor SGR (`\x1b[38;2;r;g;b m` /
    `\x1b[48;2;r;g;b m`)** wired via a refactored `applySgr`
    that walks the parameter array index-by-index,
    consuming sub-parameters when it sees a `38` or `48`
    trigger followed by `5` or `2`. Bounds-guards inline
    on each arm so a malformed `\x1b[38;5m` (missing index)
    degrades to "ignore" rather than throw — hostile-input
    parity with audit-cycle SR-1's `MAX_PARAM_VALUE`
    clamps. Colon-separated sub-params (`38:5:n`,
    `38:2:r:g:b`) require parser-side support and are
    Stage 6 territory; tracked as a `// TODO Stage 6:
    colon-separated sub-params` comment in the walker.

  - **OSC 52 defensive comment** in `Apply`'s `OscDispatch`
    arm. No behaviour change — every OSC dispatch is still
    silently dropped — but the explicit arm with a
    SECURITY-CRITICAL long-form comment (mirroring SR-1
    TC-1's style at `Screen.fs` lines 201-214) makes the
    reasoning grep-able for future audits. `SECURITY.md`
    row TC-2 cross-references the new chokepoint.

  18 new tests in `tests/Tests.Unit/ScreenTests.fs` exercise
  every arm: DECTCEM toggle, DECTCEM-vs-non-private
  isolation, DECSC/DECRC round-trip, DECSC-saves-attrs,
  DECRC-on-empty-stack, multi-level DECSC LIFO, 256-colour
  Fg/Bg, truecolor Fg/Bg, Print-carries-Indexed-into-cell,
  malformed `38;5` doesn't throw, malformed `38;2;...`
  doesn't throw, mixed SGR with truecolor in the middle,
  OSC 52 bumps SequenceNumber but doesn't mutate cells.
  The existing `fresh screen cursor is at 0,0 visible` test
  was migrated from `screen.Cursor.Visible` to
  `screen.Modes.CursorVisible`.

  Companion PR: PR-B (alt-screen 1049 back-buffer) lands
  on top of this. The full Stage 4.5 plan is in
  `/root/.claude/plans/replicated-riding-sketch.md`.

- **`Ctrl+Shift+R` release-notes browser hotkey.** Press
  `Ctrl+Shift+R` (mnemonic: **R**eleases) from inside pty-speak
  to open the GitHub Releases page for the configured
  `UpdateRepoUrl` (today
  `https://github.com/KyleKeane/pty-speak/releases`) in the
  user's default web browser. NVDA narrates "Opened release
  notes in default browser: <url>". Useful as a one-keypress
  answer to "what changed in this version?" without leaving
  pty-speak. The browser's own accessibility surface handles
  the release-notes navigation; pty-speak just hands the URL
  to the OS shell. URL is derived from `UpdateRepoUrl` so a
  fork / self-hosted variant only needs to update one
  constant (Phase 2's TOML config will make `UpdateRepoUrl`
  user-configurable per `SECURITY.md` row C-1; this hotkey
  inherits whatever the user configures).

  Note on the `Ctrl+Shift+R` vs `Alt+Shift+R` (Stage 10
  review-mode toggle, reserved) mnemonic overlap: WPF treats
  the two as distinct `KeyGesture`s (different modifier
  sets), so there is no actual keypress conflict. The R-vs-R
  mnemonic similarity is the only cost; the maintainer chose
  Ctrl+Shift+R explicitly for the "R for Releases" parallel.

  The reserved-hotkey list now reads: `Ctrl+Shift+U` (update),
  `Ctrl+Shift+D` (diagnostic), `Ctrl+Shift+R` (release notes),
  `Ctrl+Shift+M` (Stage 9 mute, reserved), `Alt+Shift+R`
  (Stage 10 review, reserved). `SECURITY.md` row A-3 +
  "What we defend against" bullet updated to reflect; the
  reserved-hotkey list in `docs/USER-SETTINGS.md` Keybindings
  section also updated.

- **`Ctrl+Shift+D` diagnostic-launcher hotkey.** Press
  `Ctrl+Shift+D` from inside pty-speak to launch the bundled
  process-cleanup diagnostic (`scripts/test-process-cleanup.ps1`)
  in a separate PowerShell window. NVDA narrates "Diagnostic
  launched in a separate PowerShell window. Switch to that
  window to follow the test." The diagnostic auto-detects when
  the user closes pty-speak (no Enter prompt), reports
  PASS/FAIL plain-text output one-fact-per-line for screen-
  reader audible follow-along, and runs both close paths
  (Alt+F4 and X button). Added because Task Manager's
  Processes-tab chevron-expand affordance is not screen-
  reader-accessible, so the long-deferred Stage 4 process-
  cleanup test could not be exercised by an NVDA-using
  maintainer via the original Task Manager walkthrough.

  The script is bundled into the Velopack install via
  `Terminal.App.fsproj`'s `Content` include; the hotkey
  resolves the script path via `AppContext.BaseDirectory`
  so no install path is hardcoded. PowerShell is launched
  with `-NoExit` so the window stays open after the test
  completes; the user reads the output and closes that
  window manually.

  Future diagnostics (UIA peer health, ConPTY child status,
  version dump) can be added as additional bundled scripts
  reached either through the same hotkey via a sub-menu or
  via additional reserved hotkeys following the
  app-reserved-hotkey contract in `spec/tech-plan.md` §6.
  The reserved-hotkey list is now: `Ctrl+Shift+U` (update),
  `Ctrl+Shift+D` (diagnostic), `Ctrl+Shift+M` (Stage 9 mute,
  reserved), `Alt+Shift+R` (Stage 10 review, reserved).

  `SECURITY.md` row A-3 (pre-Stage-6 keyboard contract)
  updated to reflect the new app-reserved hotkey.

- **Audit-cycle PR-D: deferred-test burn-down.** Closes the
  largest test-coverage gap identified by the audit
  (SESSION-HANDOFF.md item 6) and validates that PR-C's
  `InternalsVisibleTo("PtySpeak.Tests.Unit")` wiring works
  end-to-end. Two new test files in `tests/Tests.Unit/`:

  - **`UpdateMessagesTests.fs`** — six unit tests for the
    Stage 11 update-failure announcement mapping. PR-D
    extracted the exception-to-message logic from
    `runUpdateFlow`'s catch block into a pure function
    `Terminal.Core.UpdateMessages.announcementForException :
    exn -> string` so the regression class that matters
    most (the user-visible NVDA announcement per failure
    class) is testable without standing up an
    `IUpdateManager` adapter to mock Velopack's concrete
    type. Tests cover all four branches
    (`HttpRequestException` → network message,
    `TaskCanceledException` → timeout message,
    `IOException` → disk message, catch-all → generic),
    plus two defensive ordering tests that fail loudly if
    a refactor accidentally moves the catch-all above the
    specific branches.

  - **`WordBoundaryTests.fs`** — fourteen unit tests for
    `TerminalTextRange`'s word-boundary helpers
    (`IsWordSeparator`, `WordEndFrom`, `NextWordStart`,
    `PrevWordStart`). PR-D changed the four helpers from
    `static member private` to `static member internal`
    so Tests.Unit can reach them via PR-C's
    `InternalsVisibleTo` declaration. The tests pin the
    "whitespace-only word boundaries (paths read as one
    word)" policy that PR #68 shipped — anyone tightening
    `IsWordSeparator` to include punctuation will fail
    these tests and have to update them deliberately.

  Companion changes: `src/Terminal.App/Program.fs`
  `runUpdateFlow` now calls
  `UpdateMessages.announcementForException` instead of
  inlining the match (no behaviour change, just relocation
  for testability). `tests/Tests.Unit/Tests.Unit.fsproj`
  gains `UseWPF=true` and a ProjectReference to
  `Terminal.Accessibility` (needed to resolve the WPF
  reference set transitively when reaching internal
  helpers in that assembly).

- **Audit-cycle PR-B: pre-Stage-5 architectural seams +
  Stage 6 spec ADR.** Two seams Stage 5+ contributors can
  plug into without rebuilding the foundation:

  1. **Parser-thread → UIA-peer notification channel.** New
     `ScreenNotification` discriminated union in
     `src/Terminal.Core/Types.fs` (`RowsChanged of int list
     | ParserError of string`). `compose` in
     `src/Terminal.App/Program.fs` constructs a bounded
     `Channel<ScreenNotification>` (256 capacity, DropOldest)
     and starts a consumer task that drains 1:1 onto the
     existing `TerminalSurface.Announce` raise path
     (PR #63). `startReaderLoop` now takes the channel
     writer and publishes one `RowsChanged` per applied
     event batch. Stage 5 inserts the coalescer (debounce
     ~200ms, hash dedup, single notification per coalesced
     batch) between the parser publish and the consumer
     without changing the channel contract. Bonus: the
     loop's previous `with | _ -> ()` exception swallow
     becomes `ParserError` publish — closes the
     cross-cutting "parser exceptions are silently
     swallowed" gap from the audit. The "ConPTY child
     failed to start" path also publishes `ParserError`
     so users hear about it via NVDA rather than staring
     at a silent terminal.

  2. **`PreviewKeyDown` routing stub on `TerminalView`.**
     New override in `src/Views/TerminalView.cs` plus a
     public `AppReservedHotkeys` static list. The list
     seeds with `Ctrl+Shift+U` (Stage 11 self-update,
     shipped) and documents future entries as code comments
     (`Ctrl+Shift+M` Stage 9, `Alt+Shift+R` Stage 10).
     The override checks each reserved hotkey first and
     leaves `e.Handled = false` so WPF's `InputBindings`
     on the parent window can process the gesture before
     any future PTY forwarding. No PTY forwarding happens
     today — that's Stage 6 — but the seam is in place so
     the contract is enforceable at review time when Stage
     6 lands.

  3. **`spec/tech-plan.md` §6 ADR amendment** (maintainer-
     authorised; immutable-spec exception). Adds an "App-
     reserved hotkey preservation contract" clause at the
     top of Stage 6 making the contract normative: Stage 6's
     keyboard layer MUST preserve every entry in
     `TerminalView.AppReservedHotkeys` and MUST NOT mark
     them `e.Handled = true`. The list and the spec clause
     are co-equal sources of truth; new app-level hotkeys
     append to both. Failure mode if violated is captured
     in the spec text (silent loss of app-level hotkeys).

  Companion PRs in the audit cycle: PR-A (docs truth-up,
  shipped); PR-C (hygiene cleanup — MSAA delete +
  InternalsVisibleTo + Stage 11 tests; queued).

- **`docs/UPDATE-FAILURES.md` — Stage 11 NVDA failure
  announcements reference.** Standalone reference doc
  cataloguing the structured failure announcements PR #66
  introduced (HttpRequestException → "cannot reach GitHub
  Releases", TaskCanceledException → "Update check timed
  out", IOException → "Update could not be written to
  disk", catch-all → "Update failed: ...", in-flight dedup
  → "Update already in progress", IsInstalled false →
  "Auto-update only available in installed builds"). Each
  entry has cause, what to do, and what NOT to interpret
  it as. Cross-linked from README, ARCHITECTURE.md, and
  this CHANGELOG.

- **`docs/USER-SETTINGS.md` — forward-looking catalog of
  hardcoded decisions that could become user-configurable
  later.** Covers six categories with full rationale per
  section: word boundaries (the maintainer-flagged
  immediate trigger; whitespace-only today, vim / UAX #29
  / per-context-with-hotkey as plausible future modes),
  visual settings (font, size, colors, palette), audio /
  earcons (mute, volume, style, device routing,
  spec-defined defaults), keybindings (currently flat;
  remappable + collision-detection candidate), update
  behaviour (channel selection, auto-check, auto-apply),
  and verbosity / NVDA narration (off / smart / verbose
  presets). Each section follows the same four-part shape
  (current state / why hardcoded now / what configurability
  would look like / implementation notes) so a future
  contributor designing the Phase 2 TOML config substrate
  has the candidate list and the rationale, not just the
  decisions.

  Also adds a "Process for adding a new setting"
  six-bullet workflow (substrate first, per-setting PR,
  default = current behaviour, validate input, document
  in this file, smoke-test row) and a "Reminder for
  contributors" close-out summarising the meta-rule.

- **`CONTRIBUTING.md` — new "Consider configurability when
  iterating" section.** Codifies the meta-rule: every PR
  that introduces a hardcoded constant or fixed behaviour
  pauses to ask whether it's a config candidate; if yes,
  pick the right default, ship it as a constant, AND
  update the candidate catalog in
  `docs/USER-SETTINGS.md`. Explicit obligations (1-4) and
  reviewer guidance ("request changes on PRs that
  introduce a clearly-config-shaped value without
  updating the catalog or noting it"). The rule's purpose
  is captured: not to make every PR a config-design
  exercise, but to keep the rationale and candidate list
  current as the project grows so the eventual
  config-loader has a complete catalog to expose.

- **`.github/PULL_REQUEST_TEMPLATE.md` gains a
  "Configurability check" checklist item** — single bullet
  that asks contributors to either update USER-SETTINGS.md
  or note "no candidate settings introduced" below. Matches
  the "Adding new manual tests" pattern from PR #58 — the
  template enforces the rule at PR-write time, not just
  review time.

- **README docs index entry** added pointing at
  USER-SETTINGS.md so contributors browsing the docs find
  the catalog.

- **Real word-navigation in `TerminalTextRange`.** Closes the
  Stage 4 follow-up logged after `v0.0.1-preview.22`'s smoke
  pass — previously `TextUnit.Word` degraded to `TextUnit.Line`
  in `ExpandToEnclosingUnit`, `Move`, and `MoveEndpointByUnit`,
  so NVDA+Ctrl+RightArrow read a whole row instead of a single
  word. Now the three navigation methods all branch on
  `TextUnit.Word` separately:

  - **Word boundaries**: `' '` (space, U+0020) and `\t` (tab,
    U+0009) are word separators. Punctuation is NOT a separator
    so `"C:\\Users\\test>"` reads as one word — matching how
    most terminal users mentally parse paths and prompts. A
    later stage with an SGR-aware tokenizer can refine this.
  - `WordEndFrom(rows, cols, r, c)` walks forward from `(r, c)`
    until it hits a separator or the document end; returns
    one-past-end. Crosses row boundaries so a word that wraps
    is one word.
  - `NextWordStart(rows, cols, r, c)` walks forward, skipping
    the rest of the current word and any separator run, landing
    on the first non-separator cell of the next word. Returns
    `(rowCount, 0)` if no further word exists.
  - `PrevWordStart(rows, cols, r, c)` walks backward through
    any separator run, then back to the start of the word
    before the original position. Returns `(0, 0)` at origin.
  - `ExpandToEnclosingUnit(Word)` snaps the range to the word
    at `Start`; if `Start` lands on a separator it advances to
    the next word. `Document` and `Character` cases unchanged;
    `Paragraph` and `Page` still degrade to `Line` because
    terminal output doesn't have well-defined paragraph or
    page semantics.
  - `Move(Word, n)` walks `n` word boundaries (forward if
    positive, backward if negative), then expands to the word
    at the new position. Returns the number of words actually
    moved (clamped at document boundaries).
  - `MoveEndpointByUnit(endpoint, Word, n)` moves only one
    endpoint by `n` word boundaries, with the existing
    endpoint-collision rule (range collapses if endpoints
    cross).

  After this, NVDA's word-navigation commands (`NVDA+Ctrl+LeftArrow`
  / `NVDA+Ctrl+RightArrow` on laptop layout) read individual
  words from the buffer instead of jumping line-by-line.
  Verification is via the manual smoke matrix's Stage-4 word
  navigation row; the FlaUI integration test from PR #59 still
  pins Line navigation against regression but doesn't yet
  exercise Word semantics specifically — that's added when we
  have deterministic test fixtures (Stage 5+).

- **Window title and accessibility name now include the running
  version.** `MainWindow.xaml.cs` reads
  `AssemblyInformationalVersionAttribute` (which carries the
  prerelease tag like `0.0.1-preview.26` because
  `System.Version` doesn't) and sets `Title = "pty-speak {version}"`
  + `AutomationProperties.Name = "pty-speak terminal {version}"`.
  NVDA+T now reads the version, so users can audibly confirm
  which build they're running — particularly important after
  Stage 11's `Ctrl+Shift+U` self-update so the post-restart
  announcement reflects the new version. Strips any
  `+commit-sha` deterministic-build trailer from the
  announcement to keep it clean. Closes the
  "version-suffix-missing" follow-up logged in
  `docs/SESSION-HANDOFF.md`.

- **Stage 11 — Velopack auto-update via `Ctrl+Shift+U`.** The
  running app can now self-update from GitHub Releases:
  pressing the keybinding fetches the next preview's delta-
  nupkg, downloads ~KB-sized binary diff, and restarts in-
  place via Velopack's `ApplyUpdatesAndRestart`. No
  SmartScreen prompt, no UAC, no installer dialog —
  replaces the standalone
  `scripts/install-latest-preview.ps1` bridge for in-place
  updates (the script stays useful for fresh installs and
  development-environment workflows).

  Implementation:

  - `src/Views/TerminalView.cs` gains a public `Announce`
    method that raises a UIA Notification event via
    `UIElementAutomationPeer.FromElement`. Uses
    `MostRecent` processing so a fast download doesn't
    flood NVDA's speech queue with stale percentages.
  - `src/Terminal.App/Program.fs` adds `runUpdateFlow` (the
    background-task orchestrator that calls Velopack's
    `UpdateManager` against `GithubSource(repoUrl, null,
    prerelease=true)` and announces each phase: "Checking
    for updates", "Downloading X.Y.Z",
    "N percent downloaded" coalesced to 25% buckets,
    "Restarting to apply update") and
    `setupAutoUpdateKeybinding` (which wires the
    `KeyBinding` via the Window's `InputBindings`).
    `compose` calls `setupAutoUpdateKeybinding window`
    before the window is shown so the gesture is live for
    the user's first keypress.
  - The `KeyBinding` lives in `Window.InputBindings`
    rather than the future Stage 6 PTY input pipeline, so
    app-level shortcuts capture the gesture before it
    reaches any keyboard router.
  - `mgr.IsInstalled` check announces a "use the install
    script for development copies" message for `dotnet
    run` paths so the keybinding fails gracefully in dev.
  - `updateInProgress` mutable flag dedupes repeat
    keypresses while a download is in flight.
  - Failure handling is currently a single
    "Update failed: <reason>" announcement; structured
    pattern-matching on Velopack exception types
    (`NetworkUnavailable`, `SignatureMismatch`) for distinct
    announcements is a later refinement.

- **`scripts/install-latest-preview.ps1` — one-command preview
  installer for Windows.** Downloads the latest (or specified)
  preview's `Setup.exe` from the GitHub Release assets, strips
  the Mark-of-the-Web tag with `Unblock-File` so SmartScreen
  doesn't prompt the unsigned-preview line on every iteration,
  and runs the installer. Replaces the multi-step "open the
  release page → navigate the asset list → click `Setup.exe`
  → click 'More info' → click 'Run anyway'" flow that takes
  several screen-reader steps per iteration with a single
  command. Scoped to the iterative-smoke-testing workflow that
  Stage 4+ NVDA verification needs; once Stage 11 ships
  Velopack delta self-update via `Ctrl+Shift+U`, this script
  becomes unnecessary for in-place updates. New `scripts/README.md`
  documents the script and reserves the directory for future
  utilities.

- **Comprehensive manual smoke-test matrix
  (`docs/ACCESSIBILITY-TESTING.md` rewrite).** Reframes the
  accessibility-only doc as the universal manual-validation gate
  every release must pass. Three new always-run sections cover
  artifact integrity (Velopack assets present, Setup.exe
  non-zero, Authenticode status, hash matches) and launch /
  process hygiene (single window, version in title, one-deep
  process tree, clean shutdown, no orphan `cmd.exe`,
  re-launch). Every per-stage table now has a "Diagnostic
  decoder" subsection that maps each possible failure to the
  likely-responsible subsystem — file paths and PR numbers
  where applicable, so a FAIL goes from "something broke" to
  "look at file X, PR Y" without further triage. New top-level
  sections "Adding new manual tests" (criteria, required
  fields, where in the matrix), "Sunset rules" (when a row
  graduates to CI or gets deleted), and "Coverage that moved
  to CI" (audit trail of the matrix's shrinking surface area
  as automated assertions land). The Stage 4 section is
  rewritten to reflect the actual ship architecture
  (`AutomationPeer.GetPattern` override after the WM_GETOBJECT
  pivot, not the original raw-provider plan), and notes that
  PR #56's `tests/Tests.Ui/TextPatternTests.fs` now CI-pins
  the producer side of the Text pattern. `docs/RELEASE-PROCESS.md`
  step 5 was rewritten to defer to the comprehensive matrix
  rather than carry its own minimal smoke list, and
  `docs/SESSION-HANDOFF.md` item 3 (the visual install smoke
  for the maintainer) now points at the matrix and tracks
  Stage 4 NVDA verification alongside the existing single-window
  check. The PR template's accessibility checklist gains an
  "Adding new manual tests" reminder so PRs that ship
  CI-unverifiable behaviour grow the matrix in the same change.
  README's docs-index entry is updated to reflect the broader
  scope.

- **Stage 4 PR C — UIA Text pattern via `AutomationPeer.GetPattern`
  override + FlaUI verification test
  (`tests/Tests.Ui/TextPatternTests.fs`).** PR C started as a
  pure verification test for PR B's WM_GETOBJECT raw-provider
  path, but two CI iterations on `windows-latest` revealed an
  architectural finding that changed the path entirely:

  - The first iteration showed UIA3 (which NVDA / Inspect.exe
    / FlaUI.UIA3 use) dispatches `WM_GETOBJECT` with
    `UiaRootObjectId` (-25), never `OBJID_CLIENT` (-4), so
    PR B's `OBJID_CLIENT`-only match never surfaced any
    patterns to UIA3 clients. Diagnostic from a 29-line
    log dump.
  - The second iteration extended the match to also handle
    `UiaRootObjectId`. That broke the entire UIA tree: every
    UI test regressed with either an HRESULT failure on
    `UIA3Automation.FromHandle` or "TerminalView descendant
    not found in the UIA tree." The root cause is that
    `WM_GETOBJECT(UiaRootObjectId)` expects a provider
    implementing `IRawElementProviderFragmentRoot` — UIA
    needs the fragment-navigation surface to traverse from
    the returned root into descendants, and our simple
    provider can't supply that even with
    `HostRawElementProvider` wired.
  - The third iteration found the actual right path:
    `AutomationPeer.GetPattern` is `public virtual` (unlike
    the unreachable `protected virtual GetPatternCore` that
    bit PR #48), so external assemblies CAN override it. The
    override adds the Text pattern to the SAME peer that's
    already in WPF's tree, leaving WPF's fragment navigation
    untouched. No `WM_GETOBJECT` interception of UIA3
    messages needed.

  What ships in PR C:

  - `TerminalAutomationPeer` gains an `ITextProvider`
    constructor parameter and a `GetPattern` override that
    returns it for `PatternInterface.Text`, deferring to
    `base.GetPattern` for every other interface.
  - `TerminalView.OnCreateAutomationPeer` passes the
    `TextProvider` it constructed in PR B through to the
    peer.
  - `WindowSubclassNative` reverts to matching `OBJID_CLIENT`
    only — kept as a defensive MSAA fallback rather than the
    primary UIA path. The `UiaRootObjectId` constant is
    documented in the source as the discovery that drove the
    pivot.
  - `tests/Tests.Ui/TextPatternTests.fs` walks the UIA tree
    for the first element exposing the Text pattern, calls
    `DocumentRange.GetText(-1)`, and asserts the result has
    the expected minimum length (30 rows × 120 cols + 29
    row-joining newlines = 3629 chars for the
    `Program.compose` default screen size) plus at least one
    `\n`. Specific cell content from cmd.exe's banner is
    deliberately not asserted — banner wording isn't
    deterministic across Windows builds.
  - Test failure messages dump the WM_GETOBJECT log and the
    visible pattern flags so a future regression diagnoses
    itself without further iteration. That diagnostic
    machinery is what made the architectural finding
    tractable in the first place; it stays in place.

- **Stage 4 PR B — Text-pattern provider scaffolding +
  WM_GETOBJECT raw-provider path (legacy MSAA fallback).**
  `Terminal.Accessibility` gains real `TerminalTextProvider`
  and `TerminalTextRange` types whose `DocumentRange.GetText`
  returns a `\n`-joined render of the current
  `Screen.SnapshotRows` capture. `TerminalView` exposes the
  provider as a public `TextProvider` property; PR C wires it
  into the UIA peer's `GetPattern` override (the actual UIA3
  surface). The PR also ships a `Views/TerminalRawProvider.cs`
  implementing `IRawElementProviderSimple` and extends the
  `WindowSubclassNative` hook from PR A (#54) to return that
  provider for `WM_GETOBJECT(OBJID_CLIENT)` queries. PR C's
  CI iteration revealed UIA3 never queries `OBJID_CLIENT` —
  it uses `UiaRootObjectId` instead, which can't be
  intercepted with a simple provider — so the raw-provider
  path is kept as a defensive fallback for legacy MSAA
  clients only, not as the primary UIA path. Stage 4
  navigation (`Move`, `MoveEndpointByUnit`, attribute
  exposure) is still stubbed; the per-cell SGR exposure
  arrives in a later stage.

  The previously throwaway `Terminal.Accessibility/RawProviderSpike.fs`
  is removed — the foundation finding it captured (F# can
  implement `IRawElementProviderSimple`) is now demonstrated by
  the production code.

- **FlaUI integration test infrastructure
  (`tests/Tests.Ui/AutomationPeerTests.fs`).** First UIA test in
  the project: launches `Terminal.App.exe` from the build
  output, attaches via `UIA3Automation`, finds the `TerminalView`
  descendant by `ClassName`, and asserts `ControlType=Document`
  and `Name="Terminal"`. `Tests.Ui.fsproj` gains
  `FlaUI.Core` / `FlaUI.UIA3` package references and a
  `ReferenceOutputAssembly="false"` ProjectReference to
  `Terminal.App` so MSBuild builds the app before the test runs
  without linking its outputs into the test bin. The test is
  scoped to validate that PR #48's reduced-scope peer actually
  works at runtime (the `TerminalView` element is reachable via
  UIA with the expected role and identity) and to give any
  future Text-pattern attempt an automated verification harness
  before merging. This is the foundation piece that any further
  Stage 4 work — raw `IRawElementProviderSimple` provider,
  reflection-based binding, or anything else — needs in place.

- **Stage 4a (reduced scope) — UIA Document role + identity.**
  `TerminalView` now exposes a `TerminalAutomationPeer` via
  `OnCreateAutomationPeer`. UIA clients (NVDA, Inspect.exe) find
  the terminal element in the automation tree with
  `ControlType=Document`, `ClassName="TerminalView"`,
  `Name="Terminal"`, `IsControlElement=true`, and
  `IsContentElement=true`. The peer subclasses
  `FrameworkElementAutomationPeer` and overrides only the five
  parameterless `*Core` methods that the spike (PR #47) confirmed
  compile cleanly from F# under `Nullable=enable`.

  **What this PR deliberately does NOT ship:** the Text pattern
  (`ITextProvider` / `ITextRangeProvider`), navigation (`Move`,
  `MoveEndpointByUnit`), and SGR attribute exposure
  (`GetAttributeValue`). All three depended on overriding
  `AutomationPeer.GetPatternCore`, which CI iteration on this PR
  established is not reachable from any external assembly in the
  .NET 9 WPF reference assembly set. C# CS0117 fires on
  `base.GetPatternCore(...)` with "FrameworkElementAutomationPeer
  does not contain a definition for 'GetPatternCore'"; F# FS0855
  on `override _.GetPatternCore(...)` is the same finding via a
  different error code. Microsoft's documented examples that
  override `GetPatternCore` evidently compile only against
  internal Microsoft assemblies where the protected member is
  visible; the public reference assembly surfaces the type
  without the override target.

  Text-pattern exposure is therefore deferred to a follow-up
  Stage 4 PR. The likely path is implementing
  `IRawElementProviderSimple` directly on `TerminalView`,
  bypassing the `AutomationPeer` hierarchy that wraps the
  unreachable protected metadata. Investigation continues with
  focused effort rather than CI iteration; tracked in
  `docs/SESSION-HANDOFF.md` Stage 4 sketch.

- **README addition: "The complexities of trying to work with
  technology as a blind developer."** Records the maintainer's
  account of why this project exists, the iOS Claude Code
  workaround (disable VoiceOver, place finger by remembered
  pixel location, re-enable VoiceOver) used to interact with
  Claude on every message of this session, and an idiom
  (`blindly iterating`) the model produced in this session that
  is not literally accurate and casually demeans the people whose
  working conditions this project is being built to improve. The
  preferred phrasing is the literal one — `iterating without
  information`, `speculative iteration`, `guessing without
  evidence` — because it communicates more precisely and removes
  the sight-as-knowledge metaphor.

- **Stage 4 spike — F# AutomationPeer + ITextProvider /
  ITextRangeProvider interop probe.** `Terminal.Accessibility`
  gains `TerminalAutomationPeer.fs` replacing the empty
  `Placeholder.fs`. The spike subclasses
  `FrameworkElementAutomationPeer`, defines stub
  `TerminalTextProvider` and `TerminalTextRange` types
  implementing the C# UIA provider interfaces, and overrides the
  five Core methods (`GetAutomationControlTypeCore`,
  `GetClassNameCore`, `GetNameCore`, `IsControlElementCore`,
  `IsContentElementCore`). Every method is a no-op; PR 4a wires
  `GetText` to the real `Screen` snapshot and PR 4b implements
  navigation. The fsproj gains `<UseWPF>true</UseWPF>` so
  WindowsBase / PresentationCore / UIAutomationProvider /
  UIAutomationTypes are resolvable. **No source-level wiring**:
  `TerminalView.OnCreateAutomationPeer` is unchanged in this PR;
  the peer is reachable as a type but never instantiated yet, so
  there's no behavior change for the running app. The spike's
  purpose is to surface F#-meets-C# interop foot-guns (analogous
  to the `out SafeFileHandle&` byref bug from Stage 1) before
  building 250+ lines of dependent code on top.

- **Parser test coverage for SUB / OSC ST / DCS CAN / Unicode
  round-trip.** `tests/Tests.Unit/VtParserTests.fs` gains four new
  cases: SUB (0x1A) cancellation in CSI mirroring the existing CAN
  test; ST-terminated OSC asserting `bellTerminated=false` plus the
  trailing bare `EscDispatch` for the `\` byte; CAN inside DCS
  passthrough emitting `DcsHook` + `DcsPut`* + `DcsUnhook` (note the
  asymmetry with CSI — CAN there emits `Execute`, here it emits
  `DcsUnhook`); and an FsCheck property that any valid Unicode
  scalar encoded as UTF-8 round-trips through the parser as a
  single `Print` event with the same rune.
- **Velopack artifact-existence gate in `release.yml`.** A new
  PowerShell step after `vpk pack` asserts that `*Setup.exe` and
  `*-full.nupkg` exist under `releases/`. Defense-in-depth on top
  of `vpk pack`'s own exit code: a future Velopack version that
  renames an artifact would otherwise produce a green workflow
  whose release ships without the file the auto-update client
  expects (because softprops is configured with
  `fail_on_unmatched_files: false` so the delta nupkg pattern can
  legitimately match nothing on first releases).
- **Stage 4 substrate — `Screen.SequenceNumber` + `Screen.SnapshotRows`
  in `Terminal.Core`.** `Screen` now exposes a monotonic
  `SequenceNumber: int64` (incremented on every `Apply`) and a
  `SnapshotRows(startRow, count): int64 * Cell[][]` method that
  atomically captures an immutable copy of the requested rows
  paired with the sequence number at capture time. Both `Apply` and
  `SnapshotRows` serialize on a private gate object, which is the
  boundary between the WPF Dispatcher (where the parser feeds
  events) and the UIA RPC thread (where Stage 4's
  `ITextRangeProvider` will read snapshots from). This is the
  thread-safety primitive that spec §4.3's snapshot-on-construction
  rule depends on; landing it ahead of the UIA peer keeps the
  Stage 4 PR focused on the peer + provider implementation.
  `tests/Tests.Unit/ScreenTests.fs` covers fresh-screen baseline,
  per-event sequence increments, deep-copy independence, sequence-
  pairing, argument validation, the `count = 0` degenerate, and a
  concurrent producer / snapshot stress test.

### Changed

- **Spec formalization: Stage 5a — Diagnostic logging surface.**
  Per chat 2026-05-03 maintainer authorization, the post-Stage-6
  diagnostic-logging cycle is now formally documented in
  `spec/tech-plan.md` as **Stage 5a**. Same letter-suffix
  convention as Stages 4a and 4b. Sub-sections cover:

  - **5a.1** `FileLogger.fs` structured-logging substrate
    (off-thread `Channel<LogEntry>` drain, `Microsoft.Extensions.Logging.Abstractions`
    contract, retention, `PTYSPEAK_LOG_LEVEL` env var, "never log
    secrets" call-site discipline).
  - **5a.2** Per-session files in per-day folders
    (`pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log` per Issue #107;
    `FileLoggerSink.ActiveLogPath` accessor).
  - **5a.3** `Ctrl+Shift+L` open-logs hotkey + announce-before-launch
    pattern (parallel to Stage 4b).
  - **5a.4** `Ctrl+Shift+;` copy-active-log hotkey + `FileShare.ReadWrite`
    matching the writer's policy + hotkey-choice rationale
    (Magnifier collision avoidance, SystemKey-unwrap → Alt+F4
    breakage, US-layout physical proximity to `L`).
  - **5a.5** `FileLoggerSink.FlushPending(timeoutMs)` TCS-barrier
    so the copy hotkey captures up-to-the-moment state.
  - **5a.6** Validation matrix (xUnit + manual NVDA).
  - **5a.7** Post-Stage-5/6 streaming-pipeline diagnostics
    (PRs #109/#111/#114/#116) — the cycle the diagnostic
    logging surface itself enabled.

  `docs/ROADMAP.md` gains a Stage 5a row between Stage 6 and
  Stage 7 (matches shipping order — work began after Stage 6
  PR-B #99 / fixup #100 and continued through the post-Stage-6
  streaming-pipeline-fix PRs plus this session's Issue #107 +
  FlushPending refinements).

  No prose alignment needed in other docs because user-facing
  references use the hotkey names (`Ctrl+Shift+L`,
  `Ctrl+Shift+;`) and module name (`FileLogger.fs`) as pointers
  rather than a stage number.

  This completes the three-PR spec-stage-numbering chunk
  authorized in chat 2026-05-03 (Stage 4a + Stage 4b + Stage 5a
  all formally documented in the spec).

- **Spec formalization: Stage 4b — Process-cleanup diagnostic.**
  Per chat 2026-05-03 maintainer authorization, the `Ctrl+Shift+D`
  diagnostic-launcher work (PR #81) is now formally documented in
  `spec/tech-plan.md` as **Stage 4b**. Same letter-suffix
  convention as the prior Stage 4a spec edit; same Stage 3a/3b
  precedent. Sub-sections cover hotkey + script bundling,
  announce-before-launch pattern (700ms `Task.Delay` so NVDA's
  speech queue plays the cue before the spawned conhost steals
  focus), validation matrix, and the documented known limitation
  (conhost NVDA reading is unreliable; in-pty-speak rework is
  deferred per SESSION-HANDOFF item 6 and now actionable since
  Stage 6 shipped). `docs/ROADMAP.md` gains a Stage 4b row
  between Stage 11 and Stage 4a in shipping order;
  `docs/SESSION-HANDOFF.md` item 6 gains a §4b cross-link so
  future sessions land on the spec section directly.

  No other prose alignment needed because user-facing references
  to this work use the `Ctrl+Shift+D` hotkey name as the
  pointer rather than a stage number.

  Companion Stage 5a (diagnostic logging surface) ships next per
  the same chat 2026-05-03 authorization.

- **Spec formalization: Stage 4a — Claude Code rendering substrate.**
  Per chat 2026-05-03 maintainer authorization, the post-Stage-4
  rendering-substrate work (alt-screen 1049, DECTCEM cursor visibility,
  256/truecolor SGR, DECSC/DECRC, OSC 52 silent drop, `TerminalModes`
  record + private-CSI / ESC dispatch substrate) is now formally
  documented in `spec/tech-plan.md` as **Stage 4a**. Letter-suffix
  naming follows the existing Stage 3a/3b precedent and avoids
  collision with the `### 4.5` NVDA validation sub-section of
  Stage 4. `docs/ROADMAP.md` gains a Stage 4a row; forward-looking
  references in `docs/SESSION-HANDOFF.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`,
  `docs/ACCESSIBILITY-TESTING.md`,
  `docs/CHECKPOINTS.md`, and
  `docs/PROJECT-PLAN-2026-05.md` realign from "Stage 4.5" to
  "Stage 4a". Historical CHANGELOG entries from when the work
  shipped (PR-A #85, PR-B #86) keep their original "Stage 4.5"
  labels as release-notes-shaped artifacts of the moment they
  shipped.

  Companion Stage 4b (process-cleanup diagnostic) and Stage 5a
  (diagnostic logging surface) ship as separate PRs per the
  same chat 2026-05-03 authorization.

- **Per-session log filenames now use full date+time + millisecond
  tie-breaker** ([#107](https://github.com/KyleKeane/pty-speak/issues/107),
  Option A). Filename scheme moves from
  `pty-speak-HH-mm-ss.log` to
  `pty-speak-yyyy-MM-dd-HH-mm-ss-fff.log`; day folders stay
  `yyyy-MM-dd`. Two motivating concerns:

  1. **Self-describing when extracted.** The old filename
     dropped the date because the day-folder carried it; the
     moment a user copied a single log out of its folder
     (email attachment, paste into a bug report, drag into a
     chat) it lost its date context and became hard to
     correlate with the session it described. Embedding the
     full date in the filename keeps it self-describing
     anywhere it lands.

  2. **Uniqueness when the second tier collides.** Two
     launches in the same UTC second produced identical
     filenames under the old scheme; Issue #107's three
     candidate tie-breakers (millisecond suffix, short UUID,
     incremental counter) settled on milliseconds via Option
     A — alphabetical sort still equals chronological sort,
     no UUID-readability cost, no concurrent-counter retry
     code path. Two launches inside the same millisecond
     remain a theoretical collision but are vanishingly
     unlikely for human-launched terminal sessions.

  Affected code: `src/Terminal.Core/FileLogger.fs`'s
  `pathsForLaunch ()` (one-line format-string change) plus
  the doc-comment file-layout example. Tests:
  `tests/Tests.Unit/FileLoggerTests.fs` gains a
  `assertSessionFilenameFormat` helper that parses the
  filename through `DateTime.TryParseExact` against the new
  format string; the two existing tests
  (`active log lives inside a day-folder named yyyy-MM-dd`
  and `ActiveLogPath member exposes the per-session file
  inside today's day-folder`) tighten via the helper, plus
  a new test
  (`session filename uses yyyy-MM-dd-HH-mm-ss-fff format per
  Issue #107`) pins the parsed timestamp within 5 seconds of
  `DateTime.UtcNow` so the format reflects the launch
  instant rather than random digits. `docs/LOGGING.md`
  example tree updated. `tests/Tests.Unit/FileLoggerTests.fs`
  retention-sweep test fixtures use derived filenames
  (`pty-speak-{stale-or-fresh-yyyy-MM-dd}-12-00-00-000.log`)
  so the placeholder filenames match their day-folder for
  readability.

- **Restored two strategic INFO log entries that PR #111
  over-demoted.** Coalescer "Emit OutputBatch (leading-edge
  | trailing-edge)" and `TerminalView.Announce`
  "RaiseNotificationEvent firing" are back at `Information`.
  These are bounded by the coalescer's 200ms debounce
  (~5 events/sec at typing speed; far below any I/O lag
  threshold) and constitute the primary "is the streaming
  pipeline alive?" signal at default log level — without
  them, default logs show nothing of the streaming path,
  and a streaming-silence bug requires the user to launch
  with `PTYSPEAK_LOG_LEVEL=Debug` to capture any trace at
  all. The other PR #109 entries (reader publish, suppress,
  accumulate, drain dispatch) stay at `Debug` to keep the
  steady-state volume low; flip to Debug for full-chain
  diagnosis when needed.

- **Log-copy hotkey rebound from `Ctrl+Alt+L` to
  `Ctrl+Shift+;`** (the semicolon / colon key, immediately
  to the right of `L` on a US-layout keyboard).
  Maintainer-reported regressions on the post-#109 preview
  drove the move:

  1. `Ctrl+Alt+L` collides with the **Windows Magnifier**
     zoom-in shortcut on some default Magnifier configs;
     the OS swallowed the gesture before pty-speak saw it.
  2. The original fix for `Ctrl+Alt+L` not firing (PR #108)
     introduced a `SystemKey`-aware filter at the top of
     `OnPreviewKeyDown` so that `Alt`-modified gestures (which
     WPF reports as `e.Key == Key.System` + `e.SystemKey ==
     Key.L`) were unwrapped to the underlying key. Side
     effect: `Alt+F4` was unwrapped to `Key.F4 + Alt`, the
     encoder produced bytes, `e.Handled` became `true`, and
     the OS window-close gesture stopped working.

  `Ctrl+Shift+;` is a clean Ctrl+Shift gesture: no Alt path,
  no Magnifier collision, no SystemKey unwrap needed in the
  filter chain. Removing the SystemKey unwrap restored
  `Alt+F4` because `Key.System` falls through to
  `KeyCode.Unhandled`, the encoder returns null, `e.Handled`
  stays false, and WPF's default close handler fires.

  Mnemonic: physical proximity. The semicolon / colon key
  sits right next to `L`, so `Ctrl+Shift+L` (open the logs
  folder) and `Ctrl+Shift+;` (copy the active session log)
  live under one hand position. `Ctrl+Shift+C` was
  considered as the natural "copy" mnemonic but reserved
  for a future copy-latest-command-output feature (the
  cross-terminal convention for that gesture). `Ctrl+Shift+M`
  was considered but stays reserved for the Stage 9 earcon
  mute toggle. Layout caveat: on non-US keyboards the
  `OemSemicolon` virtual-key sits in a different physical
  position; remap support is on the Phase 2 user-settings
  roadmap.

  Updated everywhere it was documented: README,
  `docs/LOGGING.md`, `docs/USER-SETTINGS.md`,
  `docs/ACCESSIBILITY-INTERACTION-MODEL.md`, the
  `AppReservedHotkeys` table in `TerminalView.cs`, the
  `setupCopyLatestLogKeybinding` wiring in `Program.fs`, and
  the `HandleAppLevelShortcut` direct-dispatch path. The
  `Ctrl+Shift+L` open-folder primary is unchanged.

- **Streaming-path instrumentation demoted from `Information`
  to `Debug`** so the production default sees no per-frame
  log I/O. The PR #109 instrumentation at typing speed
  produced ~25 entries/second across all stages, which
  manifested as visible WPF dispatcher lag during streaming
  output. Demoting the per-event entries (reader publish,
  coalescer suppress / accumulate / emit, drain dispatch,
  peer-present raise) leaves the trail intact for diagnosis
  — set `PTYSPEAK_LOG_LEVEL=Debug` before launch to capture
  the full chain — but keeps the steady-state log silent.
  The peer-NULL `WARN` stays at `WARN` (rare, and the
  smoking-gun signal that a UIA client never connected and
  notifications are silently dropping). One-time entries
  (runLoop start, cancellation, hotkey invocations) stay at
  `Information`.

- **Logging restructured to per-session files in per-day
  folders.** The previous layout kept one daily-rolled file
  per UTC day; long-running development days produced massive
  aggregated files that were painful to navigate when grabbing
  a slice for a bug report. New layout (filename refined per
  Issue #107 — see the matching Changed entry):

  ```
  %LOCALAPPDATA%\PtySpeak\logs\
  ├── 2026-05-02\
  │   ├── pty-speak-2026-05-02-13-45-23-189.log    ← session that launched at 13:45:23.189 UTC
  │   ├── pty-speak-2026-05-02-15-12-08-401.log
  │   └── pty-speak-2026-05-02-16-30-44-027.log
  ├── 2026-05-01\
  │   └── pty-speak-2026-05-01-09-15-22-318.log
  └── ... (up to 7 days)
  ```

  Each launch creates a fresh session file named with its
  full launch timestamp inside today's day-folder. Sessions
  don't split across midnight (a long-running session stays
  in its launch-day folder). Retention deletes whole
  day-folders older than 7 days; folders with non-date names
  are ignored defensively. New `FileLoggerSink.ActiveLogPath`
  member exposes this session's file path for tools that
  want to grab the active session directly.

  `Ctrl+Shift+L` still opens the logs root; the user
  navigates one click into today's day-folder and picks the
  most recent session by alphabetical sort. Bug reports are
  now one-file pastes instead of "scroll a giant log to the
  right time range".

  `docs/LOGGING.md` updated with the new layout, retention
  rules, and a one-line PowerShell snippet for grabbing the
  latest session — useful for the future
  Claude-Code-on-the-machine workflow where a script could
  pull the most recent log without prompting the user.

- **`Ctrl+Shift+R` flipped from "open releases page" to "open
  draft-a-new-release form".** The original PR #83 hotkey opened
  `UpdateRepoUrl + "/releases"` (the listing). During post-Stage-5
  manual NVDA verification on the just-cut preview, the maintainer
  realised the daily-use path during the preview line is creating
  a release (publishing in the GitHub Releases UI triggers the
  Velopack build/upload workflow per `docs/RELEASE-PROCESS.md`),
  not browsing existing releases. Flipping the URL to
  `/releases/new` makes the hotkey a one-keypress shortcut to the
  cadence step that matters every preview cut. Mnemonic stays "R
  for **R**elease".

  Renames that follow the behaviour change:

  - `Program.fs runOpenReleases` → `runOpenNewRelease`
  - `Program.fs setupReleasesKeybinding` → `setupNewReleaseKeybinding`
  - `RoutedCommand("OpenReleases", ...)` → `"OpenNewRelease"`
  - `Terminal.Core.ActivityIds.releases` (`"pty-speak.releases"`)
    → `ActivityIds.newRelease` (`"pty-speak.new-release"`).
    The activity-ID rename is a soft breaking change for any NVDA
    user who already configured per-tag handling for the old
    string, but Stage 5's tag vocabulary just shipped on the
    preceding preview and is documented to accept renames until
    v0.1.0+.
  - Announce text: "Opened release notes in default browser:
    {url}" → "Opening new release form."
  - Doc updates: `README.md`, `SECURITY.md` (A-3 row + the
    pre-Stage-6 keyboard contract paragraph), `docs/USER-SETTINGS.md`.

  No hotkey contract change from the user's perspective; same
  `Ctrl+Shift+R`, different (more useful) URL.

- **SESSION-HANDOFF item 2 step 3 closed.** Post-Stage-5
  process-cleanup re-run via `Ctrl+Shift+D` on the post-Stage-5
  preview returned PASS for both close paths (Alt+F4 and
  X-button) per the maintainer's manual NVDA verification.
  Item 2 step 3 flips from "↻ pending" to "✓ PASS"; step 4
  ("After Stage 6 ships") is now the next pending pass.

- **`docs/SESSION-HANDOFF.md` item 2 truth-up.** The
  process-cleanup baseline test (Step 1 of the recurring
  cadence) was actually run via `Ctrl+Shift+D` on
  `v0.0.1-preview.27` during the post-Stage-4.5 hygiene
  session — both close paths PASSED, no orphans. The doc
  still framed the baseline as future tense ("next
  manual session — establishes whether the shipped code
  already has issues"); updated to reflect "✓ Baseline on
  `v0.0.1-preview.27` — PASS (2026-05-01)" plus a
  cross-reference to item 6 (the screen-reader-native
  replacement work, since NVDA's coverage of the spawned
  PowerShell window is the documented limitation; the
  underlying script's PASS/FAIL output is the source of
  truth). Step 2 ("After Stage 4.5 PR-B ships") is the
  next pending pass, since `v0.0.1-preview.28+` now carry
  the alt-screen back-buffer.

  No code paths touched.

- **Repo-hygiene cleanup (post-Stage-4.5 sweep).** Two
  small documentation fixes, a future-proofing convention
  added to `CONTRIBUTING.md`, and a one-time cleanup
  script for the maintainer to run on their workstation:

  - `docs/USER-SETTINGS.md` Keybindings section: corrected
    "Four app-level keybindings shipped today" to "Three"
    (the bullet list correctly enumerates three shipped
    `Ctrl+Shift+U/D/R` plus two reserved `Ctrl+Shift+M`,
    `Alt+Shift+R`; the prose count was off by one).

  - `CONTRIBUTING.md` Branching and pull requests: new
    bullet documenting the post-merge convention to
    delete the source branch (both remote and local).
    The repo had accumulated 75+ stale post-merge
    branches over the project's history; the codified
    convention prevents recurrence.

  - `scripts/cleanup-stale-branches.sh`: bundled
    maintainer-side script that deletes the 77 accumulated
    stale post-merge branches in one go. The agent
    sandbox cannot delete remote refs (proxy returns
    HTTP 403 on `git push --delete`), so this is a
    one-time maintainer action. Idempotent
    (`git ls-remote --exit-code` check skips branches
    that have already been deleted). The script can be
    deleted from the repo after the one-time cleanup
    finishes.

  - `docs/SESSION-HANDOFF.md` "Pending action items"
    item 7 tracks the cleanup-script run as a
    maintainer-side action.

  No code paths touched.

- **Audit-cycle PR-E: cache `~/.dotnet/tools` across CI
  runs.** Both `.github/workflows/ci.yml` (Build and test
  job) and `.github/workflows/release.yml` (release-pack
  job) now cache the global dotnet tools directory before
  `dotnet tool install -g vpk`. The install step gates on
  `cache-hit != 'true'` so a cached run skips the install
  entirely. Saves ~10s per CI run. Cache key is statically
  versioned (`v1`); bump to `v2` when a new vpk version is
  wanted (the cache key change forces a fresh install,
  which pulls latest from NuGet, then re-caches).

  Two other CI optimisations from SESSION-HANDOFF item 3
  investigated and **deferred**: merging the two
  `gaurav-nelson/github-action-markdown-link-check` steps
  into one invocation (the action doesn't support both
  `folder-path` and `file-path`; combining would either
  drop the `spec/` exclusion or require enumerating 14
  files explicitly that would drift); release.yml audit
  for vpk-pack input cache (per-build artefacts have no
  cache opportunity) and gh-download 5xx retry (no flakes
  observed yet, defer until a flake happens). Both
  trade-offs are documented in `docs/SESSION-HANDOFF.md`
  item 3 so a future contributor doesn't redo the
  investigation.

- **Audit-cycle PR-D: SESSION-HANDOFF.md cleanup.** Item 2
  (Re-enable the GitHub MCP server) removed as obsolete —
  the MCP has been working reliably for the last ~14 PRs
  in this session, the original "occasionally disconnects
  mid-session" concern has not recurred. Items 3-5
  renumbered to 2-4. Item 6 (Stage 11 `runUpdateFlow`
  test coverage) removed as shipped via this PR.

- **Audit-cycle PR-C: tightened `Terminal.Accessibility` API
  surface via `internal` + `InternalsVisibleTo`.**
  `TerminalAutomationPeer`, `TerminalTextProvider`,
  `TerminalTextRange`, and the `SnapshotText` module are now
  marked `internal` (were public by F# default). Two
  `[<assembly: InternalsVisibleTo>]` declarations grant access
  to `PtySpeak.Views` (the C# WPF library that constructs
  the peer in `TerminalView.OnCreateAutomationPeer`) and
  `PtySpeak.Tests.Unit` (so future Stage-5+ unit tests can
  reach into the accessibility types without re-exposing them
  publicly). `TerminalView.TextProvider` lowered from `public`
  to `internal` to match its now-internal type.

  Net effect: Stage 5+ contributors have the freedom to
  break these signatures without an external breaking-change
  concern. If the project ever publishes `Terminal.Accessibility`
  as a NuGet for third parties, the `internal` becomes the
  stable contract and we promote a curated subset to `public`
  intentionally.

- **Audit-cycle PR-C: Stage 11 `runUpdateFlow` test coverage
  scoped out of this PR; logged in
  `docs/SESSION-HANDOFF.md` item 6 as a focused follow-up.**
  The audit identified `runUpdateFlow` (~80 lines, three
  exception branches) as the largest untested surface in
  the codebase. The cheapest test approach needs an
  `IUpdateManager` adapter wrapping Velopack's concrete
  `UpdateManager` class — adapter scaffold big enough to
  warrant its own PR. SESSION-HANDOFF item 6 captures the
  recommended approach (full adapter OR a simpler
  pure-function extraction of the exception-to-message
  mapping) so the next contributor doesn't have to
  reverse-engineer the design decision.

- **Audit-cycle PR-A: documentation truth-up after Stage 4 +
  Stage 11 verification.** Three CRITICAL doc errors fixed
  in one focused PR: `README.md`'s status block referenced
  `v0.0.1-preview.15` and described "next preview will show
  live cmd.exe output" (was Stage 3 era language); now
  reflects Stages 0-4 + 11 shipped on `v0.0.1-preview.26`
  with NVDA verification. `docs/ROADMAP.md` Stage 11 row
  marked "shipped" instead of "next"; "Stage ordering"
  subsection rewritten to past tense. `docs/ARCHITECTURE.md`
  module table: `Terminal.Accessibility` row updated from
  "placeholder" to "implemented (4)" with the actual type
  surface; the `Terminal.Update *(future)*` row replaced
  with a row pointing at the actual `runUpdateFlow` location
  in `Terminal.App/Program.fs` (per walking-skeleton
  discipline, kept in the composition root).

  Bundled MEDIUM/LOW doc fixes: `docs/SESSION-HANDOFF.md`
  "from this point forward" phrasing replaced with
  "deprecated for in-place updates"; next-stage pointer
  updated to call out the PR-B notification-channel seam
  Stage 5 will plug into. `CONTRIBUTING.md` USER-SETTINGS
  cross-reference strengthened with explicit reviewer-block
  rule. `docs/USER-SETTINGS.md` gains an "Intentionally not
  user-configurable" subsection covering parser limits
  (alacritty/vte parity rationale) and earcon
  frequency/duration defaults (evidence-based from
  accessibility research; not arbitrary).

- **`SECURITY.md` rewritten with a comprehensive auto-update
  threat model and a consolidated vulnerability inventory.**
  Stage 11's auto-update flow added a new attack surface
  (network-fetch + execute) that wasn't analysed in the
  previous SECURITY.md. The maintainer asked for "every
  single vulnerability" and the known mitigations or
  forward-mitigation paths to be documented end-to-end. The
  rewrite:

  - **New section "Auto-update threat model"** enumerating
    nine threat classes (T-1 through T-9): passive observation,
    active MITM substitution, GitHub account compromise, CI
    runner / supply-chain attack, replay / downgrade, LPE via
    the update path, time-of-check vs time-of-use during apply,
    resource exhaustion, and Velopack log info-disclosure.
    Each class has Risk / Severity / Mitigation today / Future
    mitigation columns spelled out, with explicit references to
    the protections shipped in PRs #44, #63, #64, #65, #66.
    Includes a chain-of-trust diagram showing where each link
    can fail.
  - **New section "Vulnerability inventory"** consolidating
    every threat class in the document into a single table
    (terminal core, process / OS, update path, build and
    supply chain — 24 rows total). Each row has the threat
    ID, severity, mitigation today, what closes the gap at
    `v0.1.0+`, and shipping status. Severity and status
    glossaries make the table self-contained.
  - **"How to use this inventory"** subsection describing the
    contributor workflow: PRs that touch a protection class
    must update both the affected row and the narrative
    section. Reviewers are told to request changes on PRs
    that weaken a protection without updating SECURITY.md.
  - **Cross-link** from the existing "What we defend against"
    section to the new inventory so a contributor reading
    top-down lands on both the narrative and the audit-table
    view.

  No code changes; this is the documentation pass that
  captures the security state we've actually been shipping
  through the past several PRs.

- **`docs/RELEASE-PROCESS.md` step 3 rewritten with explicit
  CLI vs UI paths and target-branch failure recovery.**
  `v0.0.1-preview.23` was burned by a UI-path publish with the
  Target dropdown still pointing at a stale feature branch
  (`fix/stage-4-text-pattern-navigation`); the workflow's
  target-branch gate caught it correctly and failed fast, but
  the docs didn't make the failure mode prominent enough to
  prevent the recurrence (`v0.0.1-preview.14` was the first time
  this happened). The rewrite:

  - Splits step 3 into "3a CLI path (recommended)" and "3b UI
    path." The CLI path is recommended for screen-reader users
    because it's a single keyboard-driven command vs the UI's
    multi-step dropdown navigation.
  - Bolds and elaborates the "`--target main` is not optional"
    warning on the CLI command, with the explicit failure mode
    (gh uses your local checkout's current branch as the target
    if you don't pass `--target`).
  - Bolds and elaborates the "confirm Target reads `main`
    before clicking Publish" warning on the UI path, with NVDA-
    specific guidance (tab to the combobox, arrow until you
    hear "main", confirm). Adds an explicit fallback to the
    CLI path when the dropdown can't be confirmed.
  - New subsection "What to do if a release was published
    targeting the wrong branch" describing both recovery
    paths: skip the burned tag (the simple option;
    preview.{16, 17, 23} were all skipped this way) or
    delete-and-republish at the same tag with `--cleanup-tag`
    (only if the version number must be preserved).
  - "Common pitfalls" section's "Releases UI Target dropdown"
    entry expanded to cover both the UI and CLI failure modes,
    name the burned previews, and link forward to the new
    recovery procedure in step 3.

- **Stage 11 (Velopack auto-update) re-prioritised to land
  immediately after Stage 4, ahead of Stages 5-10.** The original
  ordering put Stage 11 last because auto-update is feature
  completeness rather than core functionality. Stage 4's manual
  NVDA verification cycle made the recurring cost of install
  friction visible — each iterative preview is download →
  SmartScreen prompts → install, several screen-reader steps per
  loop. Stage 11 has no architectural dependency on Stages 5-10
  (`UpdateManager` is independent of streaming notifications,
  keyboard input routing, list detection, earcons, and review
  mode), so moving it forward amortises the friction across all
  remaining stages. `docs/ROADMAP.md`'s Phase 1 table now lists
  Stage 11 as "next" with a "Stage ordering" subsection capturing
  the rationale; `docs/SESSION-HANDOFF.md` "Where we left off"
  and a new "Stage 11 implementation sketch" replace the old
  Stage 4 next-pointer (Stage 4 is fully merged on `main` as of
  PR #60); `spec/tech-plan.md` §11 gains an implementation-order
  note at the top (the spec content itself is unchanged — only
  the order of execution shifts). The standalone
  `scripts/install-latest-preview.ps1` (PR #61) is the bridge
  until Stage 11 lands and is documented as deprecated for
  in-place updates once it does.

- **Stage 4 implementation plan revised: spike + three small PRs
  instead of one big PR.** After completing the pre-Stage-4
  cleanup pass and re-reading the `ITextProvider` /
  `ITextRangeProvider` interfaces, the original "single PR,
  ~250-400 lines" estimate looked low by ~2x and bundled three
  independent review concerns (F#-meets-C# interop, navigation
  semantics, integration testing). New plan:
  1. **Spike** — 30-line throwaway proving F# can subclass WPF's
     `FrameworkElementAutomationPeer` and implement
     `ITextProvider` without an interop foot-gun on the order of
     the `out SafeFileHandle&` bug from Stage 1.
  2. **PR 4a — Minimal UIA surface.** `TerminalAutomationPeer`
     + `TerminalTextProvider` with `DocumentRange` / `GetText`
     working; every other `ITextRangeProvider` method stubbed to
     compile. Wires `TerminalView.OnCreateAutomationPeer`. Manual
     smoke via Inspect.exe + NVDA "current line".
  3. **PR 4b — Navigation semantics.** `Move` /
     `MoveEndpointByUnit` for Character/Word/Line/Paragraph/Document;
     `Compare` / `Clone` / `ExpandToEnclosingUnit` go from stubs
     to real implementations.
  4. **PR 4c — FlaUI integration test.** First test in
     `tests/Tests.Ui/`; adds FlaUI package references and asserts
     `ControlType=Document`, `Text` pattern present, non-empty
     `DocumentRange.GetText`. Also the de facto check that FlaUI
     works on the `windows-latest` GitHub Actions runner.
  Updated in `docs/SESSION-HANDOFF.md` (Stage 4 sketch),
  `docs/ROADMAP.md` (Stage 4 row), `docs/ARCHITECTURE.md` (Stage 4
  pointer), `docs/ACCESSIBILITY-TESTING.md` (Stage 4 matrix
  header note about which row lands in which PR). The spec
  (`spec/tech-plan.md` §4) is unchanged per the immutable-spec
  policy — this revision is purely about implementation order.

- **`docs/SESSION-HANDOFF.md` brought up to date.** Replaced the
  out-of-date "in-flight branch" / "last shipped release" rows: the
  `chore/session-handoff-and-final-audit` audit, the `preview.18`
  CHANGELOG, and the relaxed CHANGELOG-matching gate (PRs #35-#37)
  all merged on 2026-04-28; `v0.0.1-preview.18` is now the last
  shipped preview. Recorded the maintainer-reported Stage-3b
  finding that a separate `cmd.exe` console-host window appears
  behind the WPF window on launch, and tracked the conhost
  defect under "Pending action items" as orthogonal to Stage 4.
  Updated the Stage 4 sketch to reference the new
  `Screen.SnapshotRows` / `Screen.SequenceNumber` primitives so the
  snapshot rule is implementable without further substrate work.
- **Release-time `CHANGELOG.md` matching gate relaxed.** The pre-build
  step in `.github/workflows/release.yml` that failed the workflow
  when no `## [<version>]` section existed has been removed.
  `v0.0.1-preview.{16,17}` were burned by exactly that gate (publish a
  release without remembering to rename the section first → workflow
  fails → `release: published` won't refire for the same tag, so the
  next attempt has to bump). The `Generate release notes from
  CHANGELOG.md` step now resolves the body in this order: per-version
  `## [<version>]` section if present → `## [Unreleased]` content
  with the heading rewritten to `## [<version>] — <today>` for the
  release body → generic `"Release X. See CHANGELOG.md for details."`
  fallback (warned-on, not failed). Net effect: a maintainer can
  publish a release directly off `[Unreleased]` without burning a
  tag. `docs/RELEASE-PROCESS.md` "Cutting a release" updated to
  describe both flows.

### Removed

- **Audit-cycle PR-C: deleted dead-code MSAA fallback path
  (`WindowSubclassNative.cs`, `TerminalRawProvider.cs`,
  `WindowSubclassTests.fs`).** Stage 4's architectural pivot
  to `AutomationPeer.GetPattern` override (PR #56) made the
  WM_GETOBJECT subclass hook + `IRawElementProviderSimple`
  raw provider a "kept just in case" MSAA-only fallback. The
  audit found no real consumers and the maintainer
  authorised outright deletion (vs. `[Obsolete]`-deprecation
  with a tracking issue). Removed three files plus the
  `SourceInitialized` / `Closed` handlers in
  `MainWindow.xaml.cs` that installed and uninstalled the
  hook. Updated cross-references in `TerminalView.cs`,
  `TerminalAutomationPeer.fs` (docstring), `TextPatternTests.fs`
  (diagnostic message + verification-chain doc), and
  `docs/ACCESSIBILITY-TESTING.md` (diagnostic decoder no
  longer points at the deleted file).

  Stage 4's UIA Document role + Text pattern + review-cursor
  navigation chain is unaffected — that path lives entirely
  in `TerminalAutomationPeer` (Terminal.Accessibility) and
  `TerminalView.OnCreateAutomationPeer`. UIA3 clients
  (NVDA, Inspect.exe, FlaUI) reach the Text pattern through
  the WPF peer tree as designed.

- **`SmokeTests.fs` "string concat is associative" placeholder.**
  Was a vestigial FsCheck wire-up assertion from before
  `VtParserTests.fs` and `ScreenTests.fs` had real property tests.
  The file's other smoke ("Terminal.Core assembly loads") is
  preserved as a project-reference / type-loading sanity check.

- **Unused `FluentAssertions` package dependency.** The package was
  pinned in `Directory.Packages.props` and referenced in
  `tests/Tests.Unit/Tests.Unit.fsproj` but no test file used it
  (no `open FluentAssertions` / `using FluentAssertions` anywhere
  in the codebase). The project's testing convention is xUnit +
  FsCheck.Xunit (per `CONTRIBUTING.md` § Tests); FluentAssertions
  was never adopted. Removing the dead reference shrinks the
  restore graph and removes a meaningless dependency-update
  surface for Dependabot.

### Fixed

- **Streaming output was permanently silent.** Root cause:
  the coalescer's "any-hash-anywhere" spinner gate was
  fundamentally broken. It triggered when
  `AllHashHistory.Count >= 20`, but every call to
  `processRowsChanged` iterates all 30 screen rows and
  appends to that same history — so a single user event
  added 30 entries, instantly exceeding the 20-entry
  threshold. Once tripped, every subsequent event added
  another 30 entries faster than the 1-second sliding
  window could drain them, so the gate stayed permanently
  triggered for the entire session. Net effect: the
  cmd.exe banner, every typed character, and every command
  output were all silently suppressed at the coalescer
  before the dispatcher / NVDA path ever saw them.

  Diagnosed from a `PTYSPEAK_LOG_LEVEL=Debug` capture on
  the post-#114 preview where every `Reader published
  RowsChanged` entry was followed by `Suppressed (spinner)`
  — including the very first 16-byte cmd.exe banner chunk.
  No real spinner was running; the heuristic was firing on
  legitimate output.

  Fix: remove the broken any-hash-anywhere gate entirely.
  The per-`(rowIdx, hash)` gate (the OTHER spinner check,
  which fires when the same row state recurs ≥5 times in
  1s) handles the common spinner case (`|/-\` cycling on
  one cell) correctly and stays in place. Cross-row
  spinner detection — the original motivation for the
  any-hash gate — is filed as a follow-up issue with a
  proper redesign brief: count unique-hash recurrences,
  not total entries.

  Tests unchanged: existing `CoalescerTests.fs` covers
  the per-key gate; nothing covered the broken any-hash
  gate, so removing it doesn't regress any tested
  behaviour.

- **`Ctrl+Shift+;` log-copy failed with "file is in use by
  another process".** Maintainer-reported on the post-#111
  preview. The clipboard handler used `File.ReadAllText(path)`
  which opens the file with `FileShare.Read` (the overload's
  default) — meaning "I tolerate other readers but no
  writers." Since the `FileLogger` writer holds the file
  open with `FileAccess.Write`, the OS rejected the read
  open because it couldn't honor the reader's "no writers"
  requirement when the writer was already there.

  Fix: open the file via an explicit `FileStream` with
  `FileShare.ReadWrite`, matching the writer's policy. The
  writer is happy to coexist with concurrent readers
  (Notepad, NVDA, the Ctrl+Shift+; handler), so the OS
  grants the handle.

- **Log-copy hotkey didn't fire on the post-#103 preview.**
  Maintainer reported pressing the gesture and not hearing the
  "Log copied to clipboard. N bytes" announcement; the session
  log confirmed the handler never ran. The Window-level
  `KeyBinding` for the gesture was registered, but WPF's
  `CommandManager` class-handler routing on a custom
  `FrameworkElement` (`TerminalView`) didn't reliably fire
  `runCopyLatestLog` — same family of routing flakiness that
  bit `Ctrl+V` earlier in Stage 6.

  Fix: handle the gesture directly in
  `TerminalView.HandleAppLevelShortcut`, the same path that
  handles `Ctrl+V` and `Ctrl+L`. New
  `SetCopyLogToClipboardHandler` callback wired by
  `Program.fs compose ()` invokes the existing
  `runCopyLatestLog` handler. Both the direct path AND the
  Window-level `KeyBinding` are wired; whichever fires first
  wins. Direct path is reliable; Window-level is defence in
  depth.

- **`Alt+F4` window-close gesture stopped working** under the
  PR #108 SystemKey-aware filter. WPF reports Alt-modified
  gestures with `e.Key == Key.System` + the actual key in
  `e.SystemKey`. The PR #108 filter unwrapped that to make
  the original `Ctrl+Alt+L` reach the handler — but the same
  unwrap converted `Alt+F4` into `Key.F4 + Alt`, the encoder
  produced bytes, `e.Handled` became `true`, and the OS
  window-close gesture died.

  Fix: drop the SystemKey unwrap and rebind the log-copy
  hotkey to `Ctrl+Shift+;` (a clean Ctrl+Shift gesture that
  doesn't need the unwrap). `Key.System` now falls through
  to `KeyCode.Unhandled`, the encoder returns null, `e.Handled`
  stays false, and the OS default `Alt+F4` close handler
  fires. If a future Alt-modified reserved hotkey (Stage 10's
  `Alt+Shift+R`) is added, the unwrap can come back with an
  explicit `Alt+F4` fall-through.

- **Streaming-silence root cause: `PeriodicTimer` reuse bug in
  `Coalescer.runLoop`.** Diagnosed via the maintainer's manual
  NVDA verification on the post-Stage-6 preview, where typing
  `dir` produced no streaming announcement and the cmd.exe
  banner sometimes worked while subsequent output went silent.
  The audible signal was "Coalescer crashed: Operation is not
  valid due to the state of the object" — the exact message
  `PeriodicTimer.WaitForNextTickAsync` throws when called a
  second time before the previous call completes.

  Pre-fix, every iteration of the runLoop's main `while`
  unconditionally called `timer.WaitForNextTickAsync(ct)` to
  build a `Task.WhenAny` race against the input channel. When
  the reader won the race, the previous tick wait was orphaned
  but never cancelled; the next iteration called
  `WaitForNextTickAsync` AGAIN while the previous was still
  pending → `InvalidOperationException`. The catch handler
  surfaced "Coalescer crashed" then exited the loop, stopping
  all further streaming announcements for the rest of the
  session.

  Why intermittent: the bug requires the reader to win
  `Task.WhenAny` at least once before any timer tick fires
  (i.e. input arrives faster than the 200ms debounce). The
  cmd.exe launch banner was a single big chunk that arrived
  before any timer iteration cycled, so it announced
  correctly. Subsequent typed-input echoes triggered multiple
  fast iterations where the reader kept winning, and the
  second iteration's `WaitForNextTickAsync` crashed.

  Fix: track the pending timer task across loop iterations.
  Reuse the same wait until it actually fires; only after a
  timer tick wins does the next iteration start a fresh
  `WaitForNextTickAsync`. New regression test
  `runLoop survives multiple consecutive reader-wins without
  crashing the PeriodicTimer` in `CoalescerTests.fs` pumps 20
  fast events through the reader channel and asserts the
  runLoop keeps delivering notifications without faulting.

- **UI test flakiness on the windows-2025 runner.** The
  FlaUI-driven tests in `tests/Tests.Ui/` launch the actual
  `Terminal.App.exe` and wait for the WPF main window to
  appear. The previous 10-second timeout was tight; under
  parallel xUnit-test load on a freshly-provisioned
  Windows Server 2025 runner image, Velopack initialisation
  + WPF subsystem startup + ConPTY spawn could exceed it.
  Confirmed flake (not a code regression) by observing the
  same failure mode on PR #104, a markdown-only PR with
  zero code changes. Bumped to 30 seconds in three call
  sites: `AutomationPeerTests.fs`, two locations in
  `TextPatternTests.fs`. Same diagnostic messages preserved
  with the new timeout value. No application code touched;
  no behavioural change for users.

- **Ctrl+V paste re-fix + Ctrl+L clear-screen.** The previous
  attempt (in the post-Stage-6 fix-PR) added `KeyBinding`s mapping
  Ctrl+V and Shift+Insert to `ApplicationCommands.Paste`, but
  manual NVDA verification showed Ctrl+V still emitted `^V` to the
  shell. Two compounding causes:
  1. WPF's `CommandManager` class handler doesn't auto-process
     `InputBindings` on a raw `FrameworkElement` the way it does
     for built-in `Control`s, so the gesture wasn't reliably
     reaching `OnPasteExecuted`.
  2. Even when the routing did reach `OnPasteCanExecute`, an empty
     clipboard returned `CanExecute = false`, the gesture fell
     through unhandled to my `OnPreviewKeyDown` override, the
     encoder produced `0x16`, and cmd.exe echoed `^V`.

  Re-fix: handle Ctrl+V, Shift+Insert, and Ctrl+L explicitly at
  the top of `OnPreviewKeyDown` (new `HandleAppLevelShortcut`
  helper) before the encoder runs. Empty clipboard now becomes a
  silent no-op instead of a `^V` emission. The
  `ApplicationCommands.Paste` `CommandBinding` is kept for any
  future right-click-menu / Edit-menu paste paths.

  Ctrl+L is special-cased to send `cls\r` (the cmd.exe clear-
  screen command) instead of `0x0C` (form feed). Strictly the
  literally-correct terminal-emulator behaviour is to send `0x0C`
  and let the shell decide — but cmd.exe ignores `0x0C` and
  echoes `^L`, which is bad UX. Documented trade-off: when the
  foreground process is something that DOES interpret `0x0C`
  (Claude Code's Ink, `less`, `vim`), Ctrl+L will run `cls` as
  if typed instead of triggering that program's redraw. Acceptable
  for the current cmd.exe-only scope; revisit when Stage 7+ adds
  shell flexibility.

- **Three post-Stage-6 regressions surfaced during manual NVDA
  verification on the post-Stage-6 preview**, all targeted in a
  single follow-up PR:

  - **Ctrl+V didn't paste; sent `^V` to the shell instead.**
    `TerminalView`'s constructor added a `CommandBinding` for
    `ApplicationCommands.Paste`, which tells WPF "if Paste is
    invoked on me, here's the handler" — but did NOT add an
    `InputBinding` mapping `Ctrl+V` (or `Shift+Insert`) to the
    Paste command. Without the gesture-to-command map, Ctrl+V
    flowed through `OnPreviewKeyDown` → encoder → `0x16` → and
    cmd.exe echoed `^V` per its control-character display
    convention. Adding the two `InputBinding`s
    (`Ctrl+V` and `Shift+Insert`) wires the gestures to the
    existing `OnPasteExecuted` handler so the paste-injection
    chokepoint actually fires.

  - **Window resize didn't reflow; text cut off the right
    edge.** `TerminalView.MeasureOverride` returned the FIXED
    preferred size (`Cols × Rows × cellSize`), so the view
    never tracked window resize, so `OnRenderSizeChanged`
    never fired, so the Stage 6 `SizeChanged` →
    `DispatcherTimer` debounce → `ResizePseudoConsole` chain
    was dead. Changed `MeasureOverride` to honour
    `availableSize` (fall back to preferred size only when
    availableSize is unbounded, e.g. inside a `ScrollViewer`).
    The Screen buffer stays at construction-time 30×120 cells
    internally — full grid runtime resize is a documented
    Phase 2 stage — but cmd.exe now sees and adapts to the
    window's actual dimensions via `ResizePseudoConsole`,
    fixing the visible "text cuts off" symptom.

  - **Stage 5 streaming output announcements were silent.**
    `TerminalView.Announce` was hardcoded to use
    `AutomationNotificationProcessing.MostRecent`. That's the
    right choice for hotkey-style one-shot announcements
    (Ctrl+Shift+U / D / R, Velopack progress) where each new
    notification SHOULD supersede any in-flight one. But for
    Stage 5's streaming-PTY-output path it was wrong: rapid
    chunks arrive faster than NVDA can speak them, and under
    `MostRecent` each new chunk discards the in-flight speech
    of the previous one — typed-character echoes and command
    output were silently superseded before NVDA could read
    any of them. The two-arg `Announce(message, activityId)`
    overload now selects processing per activityId:
    `pty-speak.output` uses `ImportantAll` (queue all chunks);
    everything else keeps `MostRecent`. A new three-arg
    overload (`message, activityId, processing`) is exposed
    for any future caller that needs to override.

- **Diagnostic safety net for the coalescer drain task.**
  Previously the `Program.fs compose ()` drain task swallowed
  every unexpected exception silently with `| _ -> ()`. A
  crashed drain looked identical to a working-but-silent one,
  which made post-Stage-6 streaming-silence diagnosis hard
  ("is the drain dying or is NVDA filtering?"). The catch-all
  now sanitises the exception message through SR-2's
  chokepoint and emits one final `Announce(..., pty-speak.error)`
  before the task exits, so a future drain crash announces
  itself rather than disappearing into the void.

- **`Ctrl+Shift+D` and `Ctrl+Shift+R` announcements no longer get
  cut off by the spawned window's focus-grab.** Discovered during
  Stage 5 manual NVDA verification: pressing `Ctrl+Shift+D` started
  the diagnostic announcement but NVDA was interrupted as soon as
  the new PowerShell window activated and stole focus (NVDA's
  default interrupt-on-focus-change). Same shape for
  `Ctrl+Shift+R` once the browser activated. Pre-existing since
  the diagnostic hotkey shipped in PR #81 and the releases hotkey
  shipped in PR #84; latent because no NVDA verification cycle
  before today exercised the announce path end-to-end.

  Fix in `src/Terminal.App/Program.fs runDiagnostic` and
  `runOpenNewRelease`: announce a SHORT cue ("Launching
  diagnostic.", "Opening new release form.") FIRST, then
  schedule the actual `Process.Start` on a ~700ms `Task.Delay`
  so NVDA's speech queue has time to play the cue before the
  new window's title takes over. The longer guidance ("Switch
  to that window to follow the test.") is dropped from the cue
  — once the user hears the spawned window's title, they have
  all the context the long version provided. Both announces
  are also re-tagged with the proper `ActivityIds.diagnostic` /
  `ActivityIds.newRelease` per-class tags introduced in Stage 5
  (replacing the back-compat default `pty-speak.update`).

  No new hotkey contract; same `Ctrl+Shift+D/R` behaviour from
  the user's side, just audible. Phase 2 TOML config will make
  the 700ms delay configurable alongside the Stage 5 coalescer
  constants.

- **Update-failure announcements pattern-match on common
  exception types instead of a single generic catch.**
  `runUpdateFlow`'s `with` block now branches on:
  - `HttpRequestException` → "Update check failed: cannot
    reach GitHub Releases. Check your internet connection.
    (...)" — the offline case, the most common failure for
    end users on flaky connections.
  - `TaskCanceledException` → "Update check timed out. Check
    your internet connection and try Ctrl+Shift+U again." —
    timeouts and dropped-mid-download.
  - `IOException` → "Update could not be written to disk: ...
    Free up space or check folder permissions in
    %%LocalAppData%%\\pty-speak\\." — disk-side failures
    during download or patch application.
  - Catch-all for unexpected exceptions remains as
    "Update failed: ...".
  Replaces the single generic "Update failed: <ex.Message>"
  that PR #63 shipped with a "later stage can pattern-match"
  TODO comment. The user's offline-failure question on
  preview.25 install made this concrete enough to
  implement now.

- **Release workflow walks back through burned tags when
  fetching the prior `*-full.nupkg`.** `v0.0.1-preview.24`
  failed at the "Fetch prior release nupkg" step because the
  most recent prior release (`preview.23`) was a burned tag
  whose own workflow had failed at the target-branch gate, so
  no `*-full.nupkg` was ever uploaded to it. The original step
  picked the most recent prior release by publishedAt and
  blindly tried to download the asset — exit 1 from `gh
  release download` when no matching assets existed propagated
  to the workflow as a failure. Replaced with a walk-back loop
  that iterates releases in descending order and uses
  `gh release view --json assets` to find the most recent one
  that actually has a `*-full.nupkg`. Falls through to the
  existing "no prior nupkg, ship full-only" path if no release
  in the history has the asset (legitimate first release on a
  channel). Resolves the failure mode that
  `v0.0.1-preview.{14, 23, 24}` all hit at different points;
  combined with PR #64's documentation strengthening, makes
  burned tags a recoverable rather than cascading failure.

- **`MainWindow` moves keyboard focus to `TerminalSurface` on
  `Loaded`.** `v0.0.1-preview.21` install smoke established that
  even with PR #59's working Text-pattern navigation, NVDA still
  couldn't reach the buffer: focus stayed on the WPF `Window`
  after launch, so NVDA announced "pty-speak terminal, window"
  and anchored the review cursor on the Window (which has no
  Text pattern). The `TerminalView`'s Document-role peer with
  the working Text pattern was reachable in the UIA tree but
  invisible to NVDA's review cursor because focus was on the
  wrong element. One-line fix in `MainWindow.xaml.cs`: hook
  `Loaded` and call `TerminalSurface.Focus()`. NVDA now
  announces "Terminal, document" on launch and the review
  cursor anchors to the TerminalView, where PR #59's
  navigation is reachable.

- **Stage 4 Text-pattern navigation: NVDA's review cursor can
  now read the terminal buffer.** `v0.0.1-preview.20` install
  smoke established that PR #56's Text-pattern surface was
  reachable but unusable: NVDA's "read current line" returned
  "blank" and prev/next-line did nothing. Root cause was that
  `TerminalTextRange`'s `ExpandToEnclosingUnit`, `Move`,
  `MoveEndpointByUnit`, and `MoveEndpointByRange` were all
  no-op stubs from PR #56's "navigation deferred to PR D"
  scope. Without them NVDA's review cursor couldn't delimit a
  line: `ExpandToEnclosingUnit(Line)` was silently dropped,
  leaving the range collapsed at start with empty `GetText`
  output. Implementation in this commit:

  - `TerminalTextRange` now tracks mutable `(startRow,
    startCol, endRow, endCol)` endpoints (the UIA contract
    requires the void-returning navigation methods to mutate
    in place).
  - `ExpandToEnclosingUnit` handles `Character`, `Document`,
    and `Line` (other unit types degrade to `Line` until a
    terminal-output tokenizer arrives).
  - `Move`, `MoveEndpointByUnit`, `MoveEndpointByRange`
    implement UIA's contract including endpoint-collision
    handling (range collapses to the moved point if endpoints
    cross).
  - `CompareEndpoints` returns the lexicographic ordering
    over `(row, col)` positions.
  - `GetText` uses the range endpoints (was returning the
    entire snapshot regardless of range).
  - `DocumentRange` constructs a half-open `[(0,0), (rows, 0))`
    range matching UIA's standard endpoint convention.

  `tests/Tests.Ui/TextPatternTests.fs` gains a navigation
  regression test that asserts `ExpandToEnclosingUnit(Line)`
  bounds the range length below the full-document size, and
  `Move(Line, 1)` preserves the Line shape — the two
  invariants whose violation produced the preview.20
  failure mode.

- **Removed `MainWindow.xaml`'s
  `AutomationProperties.HelpText`.** Preview.20 NVDA smoke
  heard "Screen-reader-native Windows terminal. Stage 3b:
  bytes from a child shell are parsed and rendered; UIA
  exposure lands in Stage 4." read after the role
  announcement on every focus. That string was useful as
  developer documentation while the project was bootstrapping
  but is verbose chatter for the user. The window's name and
  Document role are sufficient.

- **Parser preserves the in-flight digit param across the
  Param → Intermediate transition (closes
  [Issue #42](https://github.com/KyleKeane/pty-speak/issues/42)).**
  `StateMachine.fs`'s `CsiParam → CsiIntermediate` and
  `DcsParam → DcsIntermediate` edges previously called
  `collectIntermediate` without first calling `pushParam`, so
  inputs like `\x1b[1$q` (CSI param + intermediate) or
  `\x1bP1$q...` (DECRQSS-shape DCS) emitted dispatch events
  with `parms = [||]` instead of `[|1|]`. Both edges now push
  the in-flight digit before transitioning, matching Williams'
  canonical `param;collect` action and alacritty/vte. The
  `CAN inside DCS passthrough emits DcsUnhook` test was
  re-augmented with the `$` byte (which used to be deliberately
  removed in #41 to dodge this bug); a new
  `CSI with param + intermediate preserves the in-flight digit`
  test pins the parallel CSI invariant so a future regression
  in either edge fails loudly.
- **CI release workflow now fetches the prior release `*-full.nupkg`
  before `vpk pack`, so deltas are produced for every non-first
  release.** `v0.0.1-preview.18` and `v0.0.1-preview.19` both
  shipped full-only — Velopack only generates a `*-delta.nupkg`
  when a prior `*-full.nupkg` exists in `--outputDir` at pack
  time, and CI starts from a fresh runner each release. New
  step uses `gh release list` + `gh release download
  --pattern '*-full.nupkg'` to drop the previous release's full
  package into `releases/` before `vpk pack`. A subsequent
  cleanup step removes the prior nupkg before the softprops
  upload so it doesn't get re-attached to the current release as
  a duplicate. First release on a channel (no prior to diff
  against) is handled silently — `gh release list` returns
  empty and the step logs and skips. Auto-update clients on
  the next release will fetch ~KB-sized delta packages instead
  of ~66 MB full nupkgs. `docs/RELEASE-PROCESS.md` updated to
  describe both new steps and the renumbered downstream steps.
- **`Terminal.App.exe` no longer allocates a console window at
  startup.** `Terminal.App.fsproj` previously set
  `OutputType=Exe` + `DisableWinExeOutputInference=true`, which
  forced the produced executable into the Windows console
  subsystem; Windows allocated a conhost for the parent process
  before any of our code ran, and that empty console window
  appeared behind the WPF window on every launch
  ([Issue #39](https://github.com/KyleKeane/pty-speak/issues/39)).
  Investigation ruled out the four ConPTY-side hypotheses
  originally listed (`STARTUPINFOEX.cb`, attribute attachment,
  `STARTF_USESTDHANDLES`, `CREATE_NEW_CONSOLE`) — the ConPTY
  setup matches Microsoft's canonical sample exactly. Switched
  the executable to `OutputType=WinExe` (Windows GUI subsystem,
  matching what the WPF SDK would auto-infer) and dropped the
  `DisableWinExeOutputInference` opt-out. No source changes
  needed; `grep` confirms zero `Console.WriteLine`/`printfn`
  calls in `src/`, so nothing was relying on an attached
  console. Visual smoke verification needs a Windows install of
  the next preview release.
- **`release.yml` now uploads Velopack's channel-suffixed manifest
  files.** `v0.0.1-preview.18` shipped with only three release
  assets (`*-full.nupkg`, `*-Setup.exe`, `RELEASES`) instead of the
  five Velopack produces. Root cause: the `softprops/action-gh-release`
  upload pattern was the literal `releases/releases.json`, but
  Velopack outputs `releases.<channel>.json` (we get `releases.win.json`
  for win-x64 packs since we don't pass `--channel`). With
  `fail_on_unmatched_files: false` the literal pattern matched
  nothing and the manifest was silently skipped — auto-update flows
  would have broken for any user installing from the release.
  Patterns updated to channel-agnostic globs:
  `releases/releases.*.json` and `releases/assets.*.json`. The
  artifact-existence gate added in PR #41 now also asserts both
  manifests are present, so the next release fails loudly if
  Velopack's naming changes again. `docs/RELEASE-PROCESS.md`
  refreshed with the actual `vpk pack` output set per
  Velopack's [packaging docs](https://docs.velopack.io/packaging/overview).

- **Yield in concurrent snapshot stress test.** The producer/snapshot
  test added in #38 now calls `Thread.Yield()` once per snapshot
  iteration. .NET's `Monitor` already yields on contended
  Apply/SnapshotRows, but the explicit hint keeps the test thread
  from starving the producer if the lock briefly goes uncontested
  on a slow CI scheduler.

### Security

- **Security audit cycle SR-3: SECURITY.md audit response.**
  Brings the vulnerability inventory and narrative into sync
  with the shipped code from SR-1 and SR-2, plus closes the
  documentation gaps the comprehensive audit identified.
  Companion to SR-1 (#76, parser hardening) and SR-2 (#77,
  accessibility hardening). The audit cycle is complete with
  this PR.

  - **6 new inventory rows.** `A-1`/`A-2`/`A-3` cover
    application-surface findings (jagged-snapshot bounds in
    word-boundary helpers, `Move(Character)` int32 underflow,
    pre-Stage-6 keyboard contract). `D-1`/`D-2` cover
    developer-tooling and operational mitigations
    (`install-latest-preview.ps1` Mark-of-the-Web strip,
    burned-tag visibility in public release history). `C-1`
    covers the deferred-to-Phase-2 hardcoded `UpdateRepoUrl`
    configuration item.

  - **3 inventory rows updated.** `TC-1` (response-generating
    sequences) annotated with the SR-1 catch-all-drop
    documentation. `TC-5` (control characters in NVDA
    `displayString`) flipped from `planned` to `partial`,
    citing SR-2's `AnnounceSanitiser` for the
    exception-message interpolation chokepoint. `TC-6`
    (output-rate ANSI-bomb DoS) updated to credit SR-1's
    parser-state caps (`MAX_PARAM_VALUE`, `MAX_DCS_RAW`,
    `OscIgnore`) and clarify that the Stage 5 ingestion-rate
    cap is still the remaining work.

  - **2 new narrative items.** `T-10` paragraph in the
    auto-update threat model elaborates the Mark-of-the-Web
    strip rationale (cross-references T-3); `D-2` bullet
    appears under "Out of scope for the update path" for
    burned-tag visibility.

  - **New `PO-5` row** documents the ConPTY environment
    inheritance accepted-risk: parent's full env block
    reaches the child via `lpEnvironment=IntPtr.Zero`,
    leaking sensitive vars (`GITHUB_TOKEN`,
    `OPENAI_API_KEY`, etc.) to the child shell. Significant
    change to close; tracked in `docs/SESSION-HANDOFF.md`
    item 5 alongside two other deferred follow-ups.

  - **New "Application surfaces" inventory section** between
    Process / OS and Update path, plus a new "Configuration"
    mini-section for the C-prefix.

  - **Lead-paragraph legend** explains the row-prefix
    naming (`TC-`, `PO-`, `A-`, `T-`, `B-`, `D-`, `C-`)
    so audit-grep queries stay consistent across surfaces.

  - **Doc-drift fix.** Tense agreement on the Ed25519
    public-key publication sentence (`is published as ...
    (it will be added)` -> `will be published as ... (it
    will be added)`).

  - **3 deferred-follow-up rows added to
    `docs/SESSION-HANDOFF.md`** (item 5) tracking the
    findings the audit identified but didn't close inline:
    PO-5 ConPTY env scrub, D-1 install-script TOCTOU
    between `Unblock-File` and `Start-Process`, Acc/9
    `TerminalView.OnRender` lock decision (deferred to
    Stage 5's parser-off-dispatcher rework).

  Vulnerability inventory now has 31 rows: TC-1..TC-6,
  PO-1..PO-5, A-1..A-3, T-1..T-10, B-1..B-4, D-1..D-2,
  C-1. All HIGH-severity findings from the November-December
  2025 audit are CLOSED in code (SR-1 + SR-2); all MEDIUM
  findings are either CLOSED or have an inventory row
  pointing at the deferred work.

- **Security audit cycle SR-2: accessibility hardening against
  malformed snapshots and untrusted exception messages.**
  Closes three HIGH/MEDIUM findings from the comprehensive
  code-level security audit. Companion to SR-1's parser
  hardening; together they close every HIGH-severity finding
  the audit identified.

  - **Jagged-snapshot bounds in word-boundary helpers.**
    `TerminalTextRange`'s `WordEndFrom`, `NextWordStart`, and
    `PrevWordStart` walked `rows.[r].[c]` assuming uniform
    row lengths (`c < cols`). `Screen.SnapshotRows` returns
    uniform rows today, but the `TerminalTextRange`
    constructor doesn't enforce uniformity, so a future
    refactor (e.g. ragged scrollback) or adversarial test
    construction could trigger `IndexOutOfRangeException`.
    Each `rows.[r].[c]` access in
    `src/Terminal.Accessibility/TerminalAutomationPeer.fs` is
    now guarded against `c >= rows.[r].Length`; the helpers
    advance to the next row when a short row is encountered.

  - **Control-character `AnnounceSanitiser`.** New
    `Terminal.Core.AnnounceSanitiser.sanitise : string ->
    string` strips C0 (0x00..0x1F), DEL (0x7F), and C1
    (0x80..0x9F) controls before any string reaches NVDA via
    UIA's `RaiseNotificationEvent`. Applied at the two call
    sites that interpolate exception messages: the
    `ParserError` construction in
    `src/Terminal.App/Program.fs` and all four interpolations
    in `Terminal.Core.UpdateMessages.announcementForException`.
    Closes the path where an exception message containing a
    BiDi override (U+202E), BEL (0x07), or ANSI escape
    sequence (0x1B) could confuse NVDA's notification handler
    or spoof announcement direction. Stage 5's streaming
    coalescer is the future second consumer; the sanitiser
    is the central chokepoint.

  - **`Move(Character, count)` int64 widening.** Both `Move`
    and `MoveEndpointByUnit` previously did `curIdx + count`
    in unchecked int32; `count = int.MinValue` underflowed to
    a positive value due to wrap, slipping past the `max 0`
    clamp and returning a wrong-direction result. Both sites
    now widen to int64 before the add, then narrow back to
    int after the bounds clamp. Same observed clamping
    behaviour for legitimate inputs; the underflow class
    disappears.

  Three new tests in `tests/Tests.Unit/WordBoundaryTests.fs`
  pin the jagged-snapshot contract (no
  `IndexOutOfRangeException` from any of the three helpers
  on a deliberately-jagged `Cell[][]`). Three new tests in
  `tests/Tests.Unit/UpdateMessagesTests.fs` pin the
  control-char strip contract end-to-end (BiDi override
  printable-Unicode preserved; BEL stripped from `IOException`
  message; clipboard-OSC `\x1b]52;c;...\x07` stripped from
  catch-all message). New `tests/Tests.Unit/AnnounceSanitiserTests.fs`
  exercises the sanitiser directly: empty / null tolerance,
  pure-ASCII identity, each control class stripped, BiDi /
  multi-byte UTF-8 / combining-mark printable Unicode
  preserved, long control-byte runs handled.

  Companion PRs: SR-1 (parser hardening, merged via #76);
  SR-3 (`SECURITY.md` audit response, queued). The full
  plan is in `/root/.claude/plans/replicated-riding-sketch.md`.

- **Security audit cycle SR-1: parser bounds against malicious
  input.** Closes three HIGH/MEDIUM findings from the
  comprehensive code-level security audit. All three are
  ANSI-bomb-class DoS protections — they don't change
  behaviour for legitimate input, just cap the parser-state
  accumulators so an adversarially-shaped byte stream
  can't allocate without bound or wrap into negative
  values.

  - **`currentParam` int32 clamp at 65535** (alacritty / vte
    parity). Input like `\x1b[999999999999999999m` previously
    overflowed int32 to a negative SGR param; now it clamps.
    Applied at both CSI and DCS digit-accumulation sites in
    `src/Terminal.Parser/StateMachine.fs`.

  - **`MAX_DCS_RAW = 4096` cap** on DCS payload emission.
    `DcsPassthrough` now tracks `dcsTotalLen` and stops
    emitting `DcsPut` events past the cap (DCS Hook + Unhook
    pair still fires, so the framing stays intact). Matches
    the ANSI-bomb resistance pattern Sixel / ReGIS terminal
    emulators use.

  - **OSC overflow transitions to `OscIgnore`.** Previously
    the parser silently truncated OSC payloads at
    `MAX_OSC_RAW = 1024` but stayed in `OscString`, where an
    embedded `\x1B` in dropped bytes could be misread as ST
    and desynchronise the state machine. New `OscIgnore`
    sub-state mirrors the existing `DcsIgnore` pattern:
    consumes bytes until ST/BEL terminator, then dispatches
    an empty `OscDispatch`.

  - **`Screen.csiDispatch` catch-all comment** documents that
    response-generating sequences (DSR, DA1/2/3, DECRQM,
    DECRQSS, CPR, title/font reports) are deliberately
    dropped per `SECURITY.md` row TC-1. Reviewers are
    instructed to block any PR that adds a handler in this
    match without a matching `SECURITY.md` update.

  Four new tests in `tests/Tests.Unit/VtParserTests.fs` pin
  each contract: SGR param clamp returns non-negative;
  8 KiB DCS payload produces ≤ 4096 `DcsPut` events with
  Hook+Unhook intact; 8 KiB OSC payload dispatches once
  with empty params; parser returns to `Ground` after OSC
  overflow + terminator.

  Companion PRs in this audit cycle (queued):
  SR-2 (accessibility hardening: jagged-array bounds,
  control-char `AnnounceSanitiser`, `Move` overflow guard);
  SR-3 (`SECURITY.md` audit response: 6 new inventory rows
  + cross-references). The full plan is in
  `/root/.claude/plans/replicated-riding-sketch.md`.

## [0.0.1-preview.18] — 2026-04-28

First preview cut from the Stage-3b state of `main`. The window now
shows live `cmd.exe` output (parser → screen → WPF rendering); the
documentation set, spec, and working conventions all reflect the
shipped-stage reality. **Unsigned preview build** — Authenticode +
Ed25519 manifest signing return before `v0.1.0`; SmartScreen will
warn on first run. See [`SECURITY.md`](SECURITY.md).

### Changed

- **Documentation audit (post-Stage-3b).** Brought README,
  `docs/ARCHITECTURE.md`, `docs/BUILD.md`, `docs/RELEASE-PROCESS.md`,
  `docs/ROADMAP.md`, `docs/ACCESSIBILITY-TESTING.md`,
  `CONTRIBUTING.md`, `SECURITY.md`, `docs/CONPTY-NOTES.md`, and
  `docs/SESSION-HANDOFF.md` in line with the actual state of `main`
  at Stage 3b. Highlights:
  - README status now distinguishes "last shipped preview" (Stage 0)
    from "on `main`" (Stages 1–3b); license dependency list reflects
    current vs future direct dependencies.
  - ARCHITECTURE adds a "current pipeline" diagram alongside the
    target one, an implementation-status column on the modules
    table, and a today/target split on the threading model.
  - CONTRIBUTING captures the F# / WPF gotchas hit during Stages 1–3b
    (`out SafeFileHandle` byref interop, `let rec` for self-referential
    class-body bindings, `internal` vs `private` constructors, F# DU
    C# interop via `IsXxx` / `.Item`, `FrameworkElement` lacking
    `Background`, `MC1002` / `NETSDK1022` / `NETSDK1047`); Tests
    section now reflects xUnit + FsCheck.Xunit (the actual frameworks)
    and the real test-project paths.
  - SECURITY annotates each "What we defend against" bullet with its
    current implementation status (most are still planned); Job Object
    deferral now consistent with `docs/CONPTY-NOTES.md`.
  - RELEASE-PROCESS workflow-step list now reflects the 12 actual
    steps in `release.yml`, including the two fail-fast gates added
    after `v0.0.1-preview.14`.
  - ROADMAP gains a Status column on the Phase 1 stage table and a
    cross-link to `docs/CHECKPOINTS.md`.
  - ACCESSIBILITY-TESTING gains rows for the only two stages with
    actual user-visible behaviour today (Stage 0 and Stage 3b); test
    fixtures section flagged as planned tooling.
- **Working conventions extracted from SESSION-HANDOFF into
  CONTRIBUTING.** SESSION-HANDOFF was carrying policy that binds every
  contributor (PR shape, CHANGELOG discipline, "`spec/` is immutable",
  "platform quirks go in `docs/CONPTY-NOTES.md`"), not just an
  inter-thread handoff. Moved those into CONTRIBUTING's "Branching and
  pull requests" section and a new "Documentation policy" section;
  SESSION-HANDOFF now points at them and keeps only the
  session-specific content (sandbox caveats, pending action items,
  Stage 4 sketch, reading order). Reading order updated so a new
  session reads SESSION-HANDOFF first, then CONTRIBUTING, then the
  rest.
- **Spec rewrite (post-Stage-3b).** Per the user's authorisation,
  applied an in-place ADR-style update to `spec/overview.md` and
  `spec/tech-plan.md` so the design contract reads true to what
  actually shipped, rather than retaining superseded choices that
  could mislead future contributors. Highlights:
  - **Elmish.WPF removed throughout.** Investigated and dropped (no
    stable .NET 9 build on nuget at the time we needed it). Replaced
    with the actual two-project F# / C# split: F# `Terminal.App`
    owns `[<EntryPoint>][<STAThread>] main`, C# `Views` library
    hosts `MainWindow.xaml` + `App.cs : Application` +
    `TerminalView.cs`.
  - **Module layout in overview.md** rewritten to match the real
    `src/` tree (`Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility`, `Views`,
    `Terminal.App`); future modules clearly marked as reserved
    names.
  - **Test framework** corrected: Expecto + FsCheck → xUnit +
    FsCheck.Xunit; per-module test projects → single `Tests.Unit/`.
  - **Stage 1 P/Invoke surface** rewritten with `nativeint&` for
    out-handle parameters; the silent `out SafeFileHandle&` byref
    bug now documented inline as a comment in the spec.
  - **Stage 1 validation criteria** rewritten around the ≥16-byte
    ConPTY init prologue (the "see directory listing" assertion the
    spec previously called for is unreliable due to the
    render-cadence finding in `docs/CONPTY-NOTES.md`).
  - **Stage 2 vs Stage 3a deferral split** clarified — the parser
    emits dispatches for everything, but `Screen.Apply` handles only
    basic-16 SGR + cursor + erase today; 256-color, truecolor, and
    DECSET are deferred with their owner-stages noted.
  - **Stage 3** annotated as having shipped split into 3a (screen
    model) + 3b (WPF rendering), with validation criteria trimmed
    to what Stage 3 alone demonstrates (cmd.exe startup banner
    renders); 256-color, resize, and typing pushed to their owner
    stages. Reference Code Map cleaned (Elmish.WPF link dropped, the
    bare FsCheck entry merged into a combined xUnit + FsCheck.Xunit
    entry).

### Added

- [`docs/SESSION-HANDOFF.md`](docs/SESSION-HANDOFF.md): handoff
  document for picking up between Claude Code sessions on this
  repo. Captures the things that aren't already in other docs:
  the working conventions observed in practice (small focused
  PRs, Conventional Commits, CHANGELOG-first releases), the
  sandbox / tools caveats (no `dotnet` locally, blocked Azure
  Blob URLs, tag-push 403, GitHub MCP disconnects), the
  pre-digested Stage 4 implementation sketch, and a recommended
  reading order for new sessions. Linked from `README.md`'s
  Quick links.
- New rows in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-2-vt-parser`, `baseline/stage-3a-screen-model`,
  and `baseline/stage-3b-wpf-rendering`, each with a corresponding
  entry in the "Pending checkpoint tags" section so the maintainer
  can sweep all four pending tags in one batch from a workstation.

- **CI hygiene gates** preventing two specific bug classes that bit
  us during Stage 0 ↔ Stage 3 iteration:
  - **`actionlint` job** in `ci.yml` lints `.github/workflows/*.yml`
    on every PR. Catches the YAML / shell / expression mistakes
    that produced the silent workflow startup_failures during the
    `v0.0.1-preview.{1..5}` diagnostic loop (PowerShell heredoc body
    lines at column 0 inside an indented `run: |` block, etc.).
  - **Release-time gates in `release.yml`** running before any
    build:
    - **Target-branch gate**: fails the workflow with a clear error
      if the release was published with `target_commitish` other
      than `main`. Prevents `v0.0.1-preview.14`'s failure mode where
      the release picked up an old branch's `release.yml`.
    - **CHANGELOG gate**: fails the workflow if `CHANGELOG.md` has
      no `## [<version>]` section matching the release tag. The
      release-notes step further down silently falls back to
      `"Release X. See CHANGELOG.md for details."` otherwise; we
      almost shipped that fallback on `.2..5`.
- **Stage 3b — WPF rendering + end-to-end wiring.** First visible
  terminal surface. New `TerminalView : FrameworkElement` in
  `src/Views/TerminalView.cs` overrides `OnRender(DrawingContext)`
  per spec §3.3: contiguous cells with identical SGR attrs coalesce
  into a single `FormattedText` run; backgrounds drawn first, text
  on top; manual underline at baseline; bold/italic via
  `FormattedText.SetFontWeight` / `SetFontStyle`. Default monospaced
  font is "Cascadia Mono" with Consolas / Courier New fallbacks at
  14pt; cell metrics computed once at construction. ANSI 16-colour
  palette mapped to WPF brushes; truecolor brushes constructed
  per-call.
- `MainWindow.xaml` now hosts a `<views:TerminalView />` with
  `x:FieldModifier="public"` so F# composition code in
  `Terminal.App` can reach it across the assembly boundary.
- `Program.fs` (Terminal.App) now wires the full Stage 3 pipeline:
  on `Window.Loaded` it spawns a 120×30 `ConPtyHost` running
  `cmd.exe`, then a background `Task` reads stdout chunks, feeds
  them through the Stage 2 `Parser`, and dispatches the resulting
  `VtEvent`s back to the UI thread via `Dispatcher.InvokeAsync`
  to apply them to a single `Screen` and invalidate the
  `TerminalView`. `Application.Exit` cancels the reader and
  disposes the `ConPtyHost`.
- `src/Views/Views.csproj` gains a `ProjectReference` to
  `Terminal.Core` so the C# control can use `Screen` / `Cell` /
  `SgrAttrs` / `ColorSpec` directly.

- **Stage 3a — screen model.** `Terminal.Core` gains the data types
  per spec §3.1 (`ColorSpec` DU, `SgrAttrs` struct, `Cell` struct,
  `Cursor` mutable record) and a `Screen` class consuming `VtEvent`s
  via `Apply`. Stage 3a coverage:
  - **Print**: writes a cell at the cursor with the current SGR
    attributes, advances Col, auto-wraps to the next row at
    end-of-line, scrolls when wrapping past the bottom row.
  - **C0 controls**: BS (cursor left, clamped), HT (next 8-column
    boundary), LF (cursor down + scroll), CR (cursor to col 0).
  - **CSI cursor movement**: A/B/C/D (relative, clamped at edges),
    H/f (CUP/HVP, 1-indexed → 0-indexed).
  - **CSI erase**: J (display, modes 0/1/2), K (line, modes 0/1/2).
  - **CSI SGR**: reset (0), bold (1/22), italic (3/23), underline
    (4/24), inverse (7/27); foreground colours 30-37 + bright
    90-97 + default 39; background colours 40-47 + bright 100-107
    + default 49. Empty-param CSI m equivalent to CSI 0m. 256-colour
    and truecolor sub-parameter forms are deferred (the parser would
    need to split on `:` vs `;` first).
  - **DECSET / DECSC / OSC / DCS**: silently ignored at this stage;
    Stage 4+ adds them as their owners need them.
- Stage 3a tests in `tests/Tests.Unit/ScreenTests.fs` covering each
  of the supported behaviours plus boundary clamping (cursor at
  edges, oversize CSI A movements) and the auto-wrap → scroll
  invariant.
- **Stage 2 — VT500 parser.** `Terminal.Parser` now contains a
  pure-F# implementation of Paul Williams' DEC ANSI parser
  ([vt100.net/emu/dec_ansi_parser.html](https://vt100.net/emu/dec_ansi_parser.html)),
  matching alacritty/vte's table-driven structure. `StateMachine`
  is a stateful single-byte feeder over fourteen `VtState` cases
  (Ground, Escape, EscapeIntermediate, CsiEntry/Param/Intermediate/Ignore,
  DcsEntry/Param/Intermediate/Passthrough/Ignore, OscString,
  SosPmApcString) with the canonical alacritty caps applied
  (`MAX_INTERMEDIATES = 2`, `MAX_OSC_PARAMS = 16`,
  `MAX_OSC_RAW = 1024`, `MAX_PARAMS = 16`). `Parser` exposes
  `create`/`feed`/`feedBytes`/`feedArray` for downstream consumers.
  A small UTF-8 decoder buffers continuation bytes and emits a
  single `Print of Rune` per scalar; malformed UTF-8 emits
  U+FFFD. See [`spec/tech-plan.md`](spec/tech-plan.md) Stage 2.
- The placeholder `VtEvent` discriminated union in
  `Terminal.Core/Types.fs` is replaced with the real DU per spec
  §2.2 (`Print | Execute | CsiDispatch | EscDispatch | OscDispatch
  | DcsHook | DcsPut | DcsUnhook`). Other DUs in `Types.fs` remain
  placeholders pending their owning stages.
- Stage 2 tests in `tests/Tests.Unit/VtParserTests.fs`:
  - **Fixture tests** for every byte-string example called out in
    spec §2.4 (`"Hello\r\n"`, `"\x1b[31mRed\x1b[0m"`, `"\x1b[2J"`,
    `"\x1b]0;Title\x07"`, `"\x1b[?1049h"`) plus multi-param
    SGR, default-parameter CSI, ESC dispatch (DECKPAM), CAN
    cancellation, and UTF-8 multi-byte assembly.
  - **FsCheck property tests** verifying the spec's robustness
    contract: parser never throws on arbitrary bytes; chunked feed
    equals whole-array feed; CAN (0x18) at any point returns the
    parser to `Ground`.
- [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md): rollback guide
  documenting stable development checkpoints. Defines the three
  durable references for each checkpoint (git tag in `baseline/`
  namespace, PR label `stable-baseline`, optional GitHub Release),
  the rollback procedures (read-only inspection, branch-from-baseline,
  destructive `main` reset), and the procedure for marking new
  checkpoints. Linked from `README.md`'s Quick links.
- **Stage 1 — ConPTY host.** `Terminal.Pty` library now contains the
  `Terminal.Pty.Native` P/Invoke surface (`COORD`, `STARTUPINFOEX`,
  `PROCESS_INFORMATION`, etc., and the kernel32 externs for
  `CreatePseudoConsole` / `CreatePipe` / `InitializeProcThreadAttributeList`
  / `UpdateProcThreadAttribute` / `CreateProcess`), a typed
  `PseudoConsole.create` lifecycle wrapper enforcing the strict 9-step
  Microsoft order (close ConPTY-owned handles in parent; correct
  `STARTUPINFOEX.cb`; no `CREATE_NEW_PROCESS_GROUP`), and a
  `ConPtyHost` high-level API exposing a stdin `FileStream` plus a
  `ChannelReader<byte array>` over stdout backed by a dedicated reader
  task. `SafePseudoConsoleHandle` (a `SafeHandleZeroOrMinusOneIsInvalid`
  subclass) ensures `ClosePseudoConsole` runs on disposal. See
  [`spec/tech-plan.md`](spec/tech-plan.md) Stage 1.
- Stage 1 acceptance test in `tests/Tests.Unit/ConPtyHostTests.fs`
  spawns `cmd.exe` under ConPTY and asserts the reader pipeline
  delivered at least the 16-byte ConPTY init prologue
  (`\x1b[?9001h\x1b[?1004h`). Validates the
  `CreatePipe → CreatePseudoConsole → CreateProcess → reader thread
  → channel → collectStdout` chain end-to-end. Stronger assertions
  on cmd's actual command output land in Stage 6 once a proper
  input pipeline lets us drive cmd deterministically. Windows-only;
  trivially passes on non-Windows so the suite runs unchanged on
  dev workstations.
- [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md): platform-quirks
  document for ConPTY behaviour observed in practice. First entry
  documents the **render-cadence** finding (fast-exit
  `cmd.exe /c <command>` loses its rendered output because conhost
  flushes on a timer-driven cadence and tears down before the next
  tick) plus forward-look items from `spec/overview.md` that haven't
  been hit in code yet. Linked from `README.md` and
  `docs/ARCHITECTURE.md`.
- New row in [`docs/CHECKPOINTS.md`](docs/CHECKPOINTS.md) for
  `baseline/stage-1-conpty-hello-world` covering the `Terminal.Pty`
  library shape and its acceptance test.

### Removed

- `.github/workflows/diagnose.yml`. Was added during the Stage 0
  release-pipeline diagnostic loop to isolate `release.yml` from
  workflow-level config issues. Its lessons live in the "Common
  pitfalls" section of [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md);
  the workflow itself is no longer needed.
- Unused `write` helper in `tests/Tests.Unit/ConPtyHostTests.fs`.
  Leftover from an earlier iteration that drove cmd via stdin; the
  working Stage 1 test asserts only on ConPTY-pipeline output
  capture, so the helper was dead code. The pattern can be re-added
  when Stage 6 lands the keyboard-to-PTY input pipeline.

## [0.0.1-preview.15] — 2026-04-27

First Stage 0 preview to ship installable artifacts. **Unsigned
preview build** — Authenticode + Ed25519 manifest signing are
deferred until before `v0.1.0`; SmartScreen will warn on first run.
See [`SECURITY.md`](SECURITY.md).

This version's binary footprint is intentionally trivial: an empty
WPF window titled "pty-speak" with `AutomationProperties.Name` set so
NVDA announces it. It exists so the deployment pipe is end-to-end
green before any terminal logic lands; future stages add the actual
ConPTY / parser / UIA work on top.

### Added

- Stage 0 shipping skeleton: F# / C# / WPF solution structure under
  [`src/`](src/) and [`tests/`](tests/) with a buildable empty-window
  app, central package management, and `TreatWarningsAsErrors=true`
  from day one.
  - F# class libraries `Terminal.Core`, `Terminal.Pty`, `Terminal.Parser`,
    `Terminal.Audio`, `Terminal.Accessibility` (placeholders for
    Stages 1–9).
  - C# WPF library `Views` hosting `MainWindow.xaml` with
    `AutomationProperties` set on the outer window. App is a plain C#
    `Application` subclass (no `App.xaml`); a Stage 0 window has no
    application-level resources.
  - F# EXE `Terminal.App` owning the `[<EntryPoint>][<STAThread>]`
    `main` that invokes `VelopackApp.Build().Run()` before any WPF
    type loads (Velopack issue #195).
  - `Tests.Unit` (xUnit + FsCheck.Xunit smoke tests) and `Tests.Ui`
    (placeholder; FlaUI work begins in Stage 4).
- CI now restores, builds, tests, publishes the app, and runs a
  Velopack `vpk pack` smoke on every PR; the resulting installer is
  uploaded as a `velopack-smoke-<run>` artifact (7-day retention).
- Release workflow keyed on `release: published` events. Maintainer
  publishes a release via the GitHub Releases UI (Target = `main`,
  prerelease checkbox set); workflow then builds, packs with
  Velopack, generates release notes from the matching CHANGELOG
  section, and updates the just-created release with the body and
  installer artifacts via `softprops/action-gh-release@v3`.

### Changed

- Release workflow simplified: SignPath Authenticode submission,
  Ed25519 release-manifest signing, and Authenticode verification
  steps are removed for the unsigned preview line. They will be
  reintroduced before `v0.1.0`; the "Re-enabling signing (deferred)"
  appendix in [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)
  keeps the procedure on file.
- CI no longer guards Restore/Build/Test on `hashFiles(...) != ''` —
  a typo in a project file now fails CI loudly instead of silently
  no-op'ing.

### Notes

- `v0.0.1-preview.{1..14}` were tagged in succession but never shipped
  installable artifacts; each was a diagnostic step in unwinding a
  silent workflow startup_failure on this repo. Root cause was a
  PowerShell `@"..."@` heredoc whose body lines were at column 0 in
  the YAML source while the surrounding `run: |` block was indented
  ten spaces — YAML literal blocks require all content lines to be
  indented at least as much as the block's first line, and the
  column-0 lines silently terminated the block, producing a malformed
  workflow file that GitHub Actions rejected at load time with no
  visible error. Fix: replace the heredoc with a properly-indented
  PowerShell array joined by newline. Documented in
  [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) so it isn't
  re-discovered the hard way.

### Project documentation (carried over from the initial scaffold)

- Specifications [`spec/overview.md`](spec/overview.md) and
  [`spec/tech-plan.md`](spec/tech-plan.md).
- Documentation scaffolding: README, [`CONTRIBUTING.md`](CONTRIBUTING.md),
  [`SECURITY.md`](SECURITY.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md),
  and supporting docs in [`docs/`](docs/).
- Issue templates for bug reports, feature requests, and accessibility
  regressions; pull request template and Dependabot configuration.
