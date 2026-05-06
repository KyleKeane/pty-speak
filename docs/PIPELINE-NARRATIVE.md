# Pipeline Narrative

> **Snapshot**: 2026-05-06
> **Status**: design / descriptive document — not a code change
> **Authoring item**: backlog item 19 (research stage)
> **Companion docs**:
> - [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) — canonical spec for the post-Stage-7 framework cycles
> - [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas (knobs on the stages this doc names)
> - [`docs/SESSION-MODEL.md`](SESSION-MODEL.md) — forward-looking design for the SessionModel substrate (item 28; future, not yet authored)
> - [`CONTRIBUTING.md`](../CONTRIBUTING.md) — F# / .NET 9 / WPF gotchas; testing conventions
> - [`spec/tech-plan.md`](../spec/tech-plan.md) — stage-by-stage implementation plan

This document is the **shared vocabulary** for how data flows
through pty-speak. Every UI event (keypress, screen mutation,
mode change, prompt boundary, shell switch, focus event,
hotkey, OS signal) maps to a named computational pathway with
explicit data available at each stage. When a future feature
is proposed, the question becomes "where does this go?" — the
pipeline-narrative is the doc that answers it.

The narrative is **descriptive** (current state) AND
**forward-looking** (named gaps that future substrate work
will fill). Where a stage exists today, this doc cites it
with file:line. Where a stage is planned (e.g. SessionModel
substrate per item 28), this doc names it as a reservation
in the vocabulary.

## Why this exists

Through PRs #161 through #169, pty-speak grew substantively:
canonical-state substrate, display pathways, colour-detection
earcons, the Ctrl+Shift+D diagnostic battery, sub-row suffix-
diff, parameterised behaviour. Each PR named computational
stages informally — "the diff layer", "the bulk-change
fallback", "the suffix path". That worked at the per-PR
scale. At project scale, the lack of a single-source-of-truth
glossary meant:

- Different sessions used different names for the same stage.
- New behaviour proposals had to re-derive "which existing
  thing does this attach to?" each time.
- Substrate gaps (no input pathway; no SessionModel) were
  silent assumptions rather than named research questions.

The maintainer's directive 2026-05-06: stop accreting
behaviour on top of an undocumented substrate. Name the
stages; map every event to a pathway; surface every gap.
Then build forward.

## Audience

Three intended readers, in order of likely first use:

1. **The maintainer**, when reasoning about where new
   behaviour should live, OR when deciding whether a
   surfaced UX issue is a heuristic patch vs. a substrate
   investment.
2. **Future Claude sessions**, when adding behaviour. The
   standard question becomes: "which pathway does this go
   in? what stage? what data is available there?" This
   doc is the answer.
3. **Future contributors**, when navigating the codebase.
   Goes alongside `ARCHITECTURE.md` (module-by-module map)
   as a flow-by-flow map.

The pipeline narrative does **not** describe HOW to use
pty-speak as an end user. That belongs in user-facing
documentation (`README.md`, `INSTALL.md`, future settings
guides).

## Reading order

For a complete pass:

1. **Pipeline glossary — current state** (the 12 named
   stages; the heart of the vocabulary).
2. **Event taxonomy** — every UI event mapped to its
   pathway. Has explicit gaps for future work.
3. **Substrate inventory** — what exists today, what's
   reserved for future research-stage items.
4. **Pathway taxonomy** — current pathways (Stream, Tui)
   and future pathways (Repl, Form, ClaudeCode, etc.).
5. **End-to-end traces** — narrative walk-throughs of two
   representative flows: a keystroke and an output byte.
6. **Seams catalogue** — where future capabilities would
   hook in, mapped to specific stages.
7. **Vocabulary glossary** — alphabetised reference.
8. **Versioning + maintenance** — how this doc stays
   honest as the code evolves.

For a targeted lookup ("what does stage 8 do?"), use the
glossary directly. For "where would I add feature X?", use
the seams catalogue.

## Status of this document

The pipeline narrative is a **snapshot** — it describes the
substrate as of the snapshot date. The codebase will evolve;
this doc gets re-snapshot-ed periodically (probably 1-2 times
per year, more often during heavy substrate work). The
relationship to truth:

- For **current state**: the doc has file:line citations.
  When the cited code changes in non-trivial ways, the doc
  needs updating.
- For **future / reserved**: the doc names backlog items.
  When the backlog item ships, the future-state section
  becomes current-state, and references update.
- For **drift**: minor drift is expected (a constant moves,
  a function is renamed). Major drift (the pipeline
  fundamentally restructures) triggers a re-snapshot.

**Snapshot triggers** — re-write this doc when:

- A new stage is added (e.g. SessionModel ships → stage 0/4.5
  insertion).
- A pathway protocol method is added or removed.
- An event type is added or retired.
- The output of a substrate audit identifies inconsistencies
  vs. this doc.

Drift between this doc and code is normal; it's caught at
audit time, not via continuous-validation automation.

## Pipeline glossary — current state

The output side, byte arrival to NVDA announcement. **12
numbered stages**, each with a stable name, module
location, inputs, outputs, parameters (cross-referenced to
USER-SETTINGS), and known fragility.

### Overview table

| # | Stage | Module | Notes |
|---|---|---|---|
| 1 | **Byte ingestion** | `Terminal.Pty.ConPtyHost` reader thread | Bytes arrive from child shell's stdout |
| 2 | **Parser application** | `Terminal.Parser.VtParser` + `Terminal.Core.Screen.Apply` | Bytes → CSI/SGR/text/OSC events → cell-grid mutations |
| 3 | **Notification emission** | `Terminal.Core.Screen` | RowsChanged, ModeChanged, Bell, ParserError emitted |
| 4 | **Canonical-state synthesis** | `Terminal.Core.CanonicalState.create` | Snapshot + sequence + row hashes packed into immutable Canonical |
| 5 | **Frame-dedup** | `Terminal.Core.StreamPathway` (`LastFrameHash`) | Identical frames are no-ops |
| 6 | **Spinner suppression** | `Terminal.Core.StreamPathway` (`isSpinnerSuppressed`) | Repeated rotating-character patterns suppressed |
| 7 | **Row-level diff** | `Terminal.Core.CanonicalState.computeDiff` | Which rows changed + their concatenated text |
| 8 | **Sub-row suffix detection** | `Terminal.Core.StreamPathway` (`computeRowSuffixDelta`) | Per row, what's new beyond previous emit's text |
| 8b | **Bulk-change fallback** | `Terminal.Core.StreamPathway` (heuristic) | When too many rows changed at once, bypass suffix detection |
| 9 | **Announcement payload assembly** | `Terminal.Core.StreamPathway` (`assembleSuffixPayload`, `capAnnounce`) | Combine per-row suffixes into single string; cap length |
| 10 | **Profile claim** | `Terminal.Core.OutputDispatcher` (`dispatch`) | Profiles inspect events, emit ChannelDecisions |
| 11 | **Channel rendering** | `Terminal.Core.OutputDispatcher` → channels (NvdaChannel, EarconChannel, FileLoggerChannel) | Each channel's `Send` receives event + render |
| 12 | **NVDA dispatch** | `PtySpeak.Views.TerminalView.Announce` | UIA Notification fires; NVDA reads it |

Auxiliary stages (not numbered linearly but relevant to the
flow):

- **Mode-barrier handling** — `StreamPathway.onModeBarrier`,
  fired between stages 4 and 5 on shell-switch / alt-screen
  transitions.
- **Earcon synthesis** — `Terminal.Audio.EarconPlayer`,
  consumed at stage 11 by EarconChannel.
- **Diagnostic battery** — `Terminal.App.Diagnostics`,
  parallel to the live pathway; runs on Ctrl+Shift+D.

### Stage 1: Byte ingestion

- **Module**: `src/Terminal.Pty/ConPtyHost.fs`
- **Input**: bytes from the child shell's stdout pipe (as
  exposed by ConPTY's pseudo-console).
- **Output**: bytes pushed onto an async reader channel.
- **Threading**: dedicated reader thread (per spawned
  shell). Survives the lifecycle of one ConPTY child.
- **Parameters**: none today (buffer size is hard-coded).
- **Known fragility**: the reader thread can wedge if the
  child shell emits high-volume output and downstream
  pathways back up. Heartbeat (Ctrl+Shift+H) reports
  reader staleness.

### Stage 2: Parser application

- **Module**: `src/Terminal.Parser/VtParser.fs` +
  `src/Terminal.Parser/StateMachine.fs` +
  `src/Terminal.Core/Screen.fs` (Apply)
- **Input**: bytes from stage 1.
- **Output**: CSI/SGR/text/OSC events → cell-grid
  mutations (in-place on `Screen`'s mutable cell array).
- **Threading**: a single reader-pump thread feeds bytes
  into the parser, which then calls `Screen.Apply` under
  the screen's gate-lock.
- **Parameters**: parser-internal limits (`MAX_OSC_RAW`,
  `MAX_CSI_PARMS`) — internal, not user-tunable.
- **Known fragility**: pathologically long escape sequences
  hit overflow guards (silent drop with parser-error
  notification). Real shells don't exercise these.
- **OSC handling today**: parser EMITS OSC events for all
  recognised OSC sequences. `Screen.Apply` SILENTLY
  DROPS them (security: OSC 52 clipboard is a known
  hostile vector; OSC 0/2 / 7 / 8 deferred to Phase 2).
- **OSC 133 reservation**: when the SessionModel substrate
  ships (item 28), `Screen.Apply` adds an arm for
  `parms.[0] = "133"` that emits a new
  `ScreenNotification.PromptBoundary` event. Today: not
  emitted.

### Stage 3: Notification emission

- **Module**: `src/Terminal.Core/Screen.fs` (event
  publishers)
- **Input**: completed Screen.Apply calls.
- **Output**: `ScreenNotification` values published to a
  `BoundedChannel`. Current taxonomy:
  - `RowsChanged of int list` — set of row indices that
    mutated since last batch.
  - `ParserError of string` — recoverable parser-side
    error.
  - `ModeChanged of TerminalModeFlag * bool` — alt-screen
    toggle, scroll-region change, etc.
  - `Bell` — BEL byte (0x07) consumed.
- **Reserved (future)**:
  - `PromptBoundary of PromptBoundaryData` — emitted on
    OSC 133 A/B/C/D parsing (item 28).
  - `CommandFinished of CommandFinishedData` — emitted on
    OSC 133 D specifically (subset of PromptBoundary;
    carries exit-code).
  - Any Phase 2 substrate-additions land here.
- **Threading**: notification publishers are called from
  the reader-pump thread (the same thread that runs
  `Screen.Apply`). Channel is bounded (256-capacity, drop-
  oldest-on-full).
- **Parameters**: none today (channel capacity hard-coded).
- **Backpressure**: if the channel fills, oldest events
  drop. This is a deliberate trade-off for screen-reader
  responsiveness — falling behind on RowsChanged is worse
  than dropping the oldest pending.

### Stage 4: Canonical-state synthesis

- **Module**: `src/Terminal.Core/CanonicalState.fs`
- **Input**: a `Screen` snapshot (rows × cells) + sequence
  number.
- **Output**: an immutable `CanonicalState.Canonical`
  record with:
  - `Snapshot: Cell[][]` — full screen grid
  - `SequenceNumber: int64` — monotonic
  - `RowHashes: uint64[]` — per-row position-aware hashes
  - `ContentHashes: uint64[]` — per-row position-
    independent hashes
  - `computeDiff: previousRowHashes -> CanonicalDiff`
- **Pure**: no state on the canonical record itself. Diff
  is computed on demand against caller-supplied baseline.
- **Reserved fields (future)**:
  - `SemanticSegments: Lazy<Segment[]>` — Phase 2 / 3.
  - `AiInterpretation: Lazy<string option>` — Phase 3.
  - Cursor position — TBD; currently lives on mutable
    `Screen.Cursor`, not on Canonical. Will need to
    migrate when cursor-aware diff ships.

### Stage 5: Frame-dedup

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`LastFrameHash` state field)
- **Input**: a Canonical (with its frame hash = XOR of row
  hashes).
- **Output**: short-circuit if the frame hash matches the
  pathway's last-seen frame hash. Otherwise pass through
  to stage 6.
- **Effect**: the no-op-frame case (PTY emits redundant
  bytes, terminal repaints unchanged content) is suppressed
  cheaply.
- **Parameters**: none directly (debounce-window affects
  whether dedup is even reached).

### Stage 6: Spinner suppression

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`isSpinnerSuppressed`)
- **Input**: per-row hashes.
- **Output**: short-circuit if a per-row spinner pattern
  is detected (same-row-content rapidly recurring within
  a configurable window). Otherwise pass through.
- **Effect**: noisy single-row repaints (npm install
  spinners, CI tail logs) don't blast NVDA with announces.
- **Parameters**: `[pathway.stream] spinner_window_ms`,
  `spinner_threshold` (USER-SETTINGS).
- **Known fragility**: spinner detection is heuristic;
  some legitimate output that happens to rapidly re-paint
  the same row (e.g. progress percentage updates) may be
  silenced.

### Stage 7: Row-level diff

- **Module**: `src/Terminal.Core/CanonicalState.fs`
  (`computeDiff`)
- **Input**: current row hashes + previous-emit's row
  hashes (carried by StreamPathway state).
- **Output**: `CanonicalDiff` record with:
  - `ChangedRows: int[]` — sorted indices of rows whose
    hashes differ.
  - `ChangedText: string` — newline-joined rendered text
    of changed rows (with trailing-blank trim +
    `AnnounceSanitiser` pass).
- **Pure**: no state. Always returns the same output for
  the same inputs.
- **Known fragility**: ChangedText is NOT bidirectional —
  trailing blanks lost, `AnnounceSanitiser` may strip
  characters; reconstructing per-row content from
  ChangedText alone isn't possible. Suffix-diff therefore
  re-renders per-row from snapshot.

### Stage 8: Sub-row suffix detection

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`computeRowSuffixDelta`, `EditDelta`)
- **Input**: per-row currentText (from snapshot via
  `CanonicalState.renderRow`) + per-row previousText (from
  StreamPathway state's `LastEmittedRowText` cache) +
  `BackspacePolicy`.
- **Output**: `EditDelta` per row:
  - `Suffix string` — text to announce (the new content
    beyond the longest-common-prefix; for shrinks under
    `AnnounceDeletedCharacter` policy, the deleted segment
    instead).
  - `Silent` — no announcement (identical text, attribute-
    only differences, or shrink under `SuppressShrink`
    policy).
- **Parameters**: `[pathway.stream.suffix_diff]
  backspace_policy` (USER-SETTINGS).
- **Known fragility**:
  - Mid-line insertion over-reports (Replace case has
    common-prefix < cursor position; emits more than the
    typed character).
  - Powerline / Starship / oh-my-posh prompt redraws
    over-report (async git-status segments rewrite the
    entire PS1).
  - Right-aligned RPROMPT (zsh) over-reports.
  - Autosuggestions (fish, zsh-autosuggestions) over-
    report.
  - **Scroll misalignment**: per-row cache is indexed by
    row position; when the screen scrolls (commands
    pushing content past the bottom), cache index N still
    holds what USED TO BE at row N, but the screen now has
    different content at row N. Diff sees "all rows
    changed" → bulk-change fallback engages → emits
    stale post-scroll content as if new. **Documented
    limitation; resolved by SessionModel substrate
    eventually**.

### Stage 8b: Bulk-change fallback

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`assembleSuffixPayload` bulk-change branch)
- **Input**: `diff.ChangedRows.Length` + `BulkChangeThreshold`
  parameter.
- **Output**: when the number of changed rows exceeds the
  threshold, the suffix-diff stage is skipped entirely;
  payload becomes `diff.ChangedText` (verbose).
- **Effect**: catches scrolls, screen clears, TUI repaints
  with a single heuristic. Avoids generating gibberish
  suffix-diffs against a stale baseline. **Itself a source
  of "stale content re-announce" when scroll is the cause
  of the bulk change** — see Stage 8 known fragility.
- **Parameters**: `[pathway.stream] bulk_change_threshold`
  (USER-SETTINGS).

### Stage 9: Announcement payload assembly

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`assembleSuffixPayload` → `capAnnounce`)
- **Input**: per-row EditDeltas (or ChangedText from
  bulk-change branch) + `MaxAnnounceChars`.
- **Output**: a single string (the StreamChunk OutputEvent
  payload). Truncated with `...announcement truncated` if
  beyond the cap.
- **Parameters**: `[pathway.stream] max_announce_chars`
  (USER-SETTINGS).
- **Known fragility**: truncation loses information;
  user has Ctrl+Shift+; to copy full log post-hoc.

### Stage 10: Profile claim

- **Module**: `src/Terminal.Core/OutputDispatcher.fs`
  (`dispatch`, `ProfileRegistry`)
- **Input**: an `OutputEvent` (built from stage 9's payload
  + stage 8 colour detection + stage 6 spinner state +
  semantic category).
- **Output**: `(effectiveEvent, ChannelDecision[])` pairs
  per active profile. Each decision references a channel
  by ID + a render instruction.
- **Active profiles today**:
  - `StreamProfile` — pass-through. Carries the event
    verbatim with NvdaChannel + FileLoggerChannel
    decisions.
  - `EarconProfile` — claims `ErrorLine` / `WarningLine` /
    `BellRang` semantics; emits earcon-render decisions
    for EarconChannel.
- **Parameters**: per-shell profile-set selection (Phase B
  TOML config).
- **Reserved (future)**: profiles are the integration
  point for Phase 2's input-side echo correlation
  (echo-suppression profile), Phase 3's AI summarisation
  profile, etc.

### Stage 11: Channel rendering

- **Module**: `src/Terminal.Core/OutputDispatcher.fs`
  (`routePair`) → channel implementations
- **Input**: per-channel `(effectiveEvent, RenderInstruction)`
  pairs from stage 10.
- **Output**: each channel's side-effect:
  - **NvdaChannel** — calls `marshalAnnounce(text,
    activityId)` (which forwards to WPF
    `TerminalView.Announce`).
  - **EarconChannel** — calls `EarconPlayer.play
    palette earconId`.
  - **FileLoggerChannel** — calls `logger.LogInformation`
    with the structured event template.
- **Parameters**:
  - `notification.activity_ids.*` (USER-SETTINGS, future
    parameters; today hard-coded in
    `NvdaChannel.semanticToActivityId`).
  - `notification.processing.*` (USER-SETTINGS, future).
  - `earcon.palette.*` (USER-SETTINGS, future TOML
    overrides).
- **Known fragility**: NvdaChannel and EarconChannel
  cross-thread to WPF dispatcher / audio output thread
  respectively. Failures in cross-thread marshal are
  logged but not surfaced to the user.

### Stage 12: NVDA dispatch

- **Module**: `src/Views/TerminalView.cs.Announce`
- **Input**: text + UIA ActivityId from stage 11's
  NvdaChannel.
- **Output**: a UIA `RaiseNotificationEvent` call. NVDA
  observes the event via UIA event handlers and queues
  the announcement.
- **Parameters**: WPF-side per-ActivityId
  `NotificationProcessing` enum (today hard-coded; future
  TOML).
- **Known fragility**: NVDA's per-ActivityId rules are
  configured on the NVDA side, not pty-speak's side. A
  user with custom NVDA rules might experience different
  cadence than the maintainer.

### Auxiliary: Mode-barrier handling

- **Module**: `src/Terminal.Core/StreamPathway.fs`
  (`onModeBarrier`)
- **Triggers**: shell-switch (Ctrl+Shift+1/2/3), alt-
  screen toggle (vim entry/exit), ConPTY mode changes.
- **Effect under each policy**:
  - `Verbose` — flush pending diff as a final StreamChunk.
  - `SummaryOnly` (default) — no flush; preserve baselines
    so post-barrier first emit doesn't re-announce stale
    content.
  - `Suppressed` — same as SummaryOnly at StreamPathway
    layer; differs in App-layer "switching to X" announce.
- **Parameters**: `[pathway.stream] mode_barrier_flush_policy`
  (USER-SETTINGS).
- **Reserved (future)**: when SessionModel ships, mode-
  barrier becomes a SessionModel boundary — closes the
  current shell's session-tuple list and starts a new
  shell's. The "flush" decision then becomes "do we
  announce the close" rather than "do we re-announce
  stale content".

## Event taxonomy

For each UI event type, the doc names: producing source,
data carried, pathway that handles it, output produced, and
whether handling is currently complete or has gaps.

### Keypress (KeyDown / TextInput)

- **Source**: WPF `OnPreviewKeyDown` in
  `src/Views/TerminalView.cs:537`.
- **Data carried**: WPF `Key` enum + `KeyModifiers` flags.
- **Translation**: `KeyEncoding.encode (keyCode,
  keyModifiers, screen.Modes)` → `byte[]` (the bytes the
  shell expects to see for this key).
- **Pathway**: **none today**. Bytes go DIRECTLY to
  `ConPtyHost.WriteBytes` (the closure wired in
  `Program.fs:1650-1651`).
- **Output produced**: bytes on PTY stdin; no OutputEvent
  emission.
- **Status**: ⚠ **GAP — no input-side pathway**. The
  Phase 2 input framework cycle introduces an
  `IInputPathway` protocol so keystrokes flow through a
  pathway-layer with hooks for echo correlation, autocomplete
  / suggestion announcement, hotkey absorption, and per-
  shell input transformation.
- **App-reserved hotkeys** (Ctrl+Shift+*) are intercepted
  BEFORE this stage in `OnPreviewKeyDown`'s pre-filter.
  Those don't go through KeyEncoding.

### Paste (Ctrl+V or Shift+Insert)

- **Source**: WPF clipboard event handlers in
  `src/Views/TerminalView.cs:684-694, 734-771`.
- **Data carried**: clipboard text content + bracketed-
  paste-state flag (whether the shell has DECSET ?2004
  enabled).
- **Translation**: `KeyEncoding.encodePaste (text,
  bracketedPaste)` → `byte[]`.
- **Pathway**: **none today**. Same direct path as
  keystrokes.
- **Status**: ⚠ Same gap as keystrokes. Future input
  pathway should treat paste as a distinct InputEvent type
  (different echo-correlation expectations, different
  pre-paste-confirmation possibilities).

### Screen mutation (RowsChanged)

- **Source**: `Screen.Apply` calls publish
  `RowsChanged of int list` after each batch of cell
  mutations.
- **Data carried**: list of row indices that changed.
- **Pathway**: `DisplayPathway.Consume` (StreamPathway,
  TuiPathway). Today's flow: `PathwayPump.handleRowsChanged`
  in `Program.fs:1123-1131`.
- **Output produced**: `OutputEvent[]` from the active
  pathway (StreamChunk + optional ErrorLine/WarningLine).
- **Status**: ✅ Complete for stream + TUI cases.
- **Known limitations**: scroll misalignment per stage 8
  fragility. Resolved by SessionModel substrate.

### Mode change (ModeChanged)

- **Source**: `Screen.enterAltScreen`,
  `Screen.exitAltScreen`, `Screen.csiPrivateDispatch` in
  `src/Terminal.Core/Screen.fs`.
- **Data carried**: `TerminalModeFlag * bool` — which mode,
  set or cleared.
- **Pathway**: `DisplayPathway.OnModeBarrier` (StreamPathway
  flushes pending diff per policy, TuiPathway handles
  alt-screen entry/exit).
- **Output produced**: `OutputEvent[]` from the pathway
  + a `ModeBarrier` semantic event from the pump.
- **Status**: ✅ Complete with policy-driven flush
  behaviour (PR #168, PR #169).
- **Known limitations**: shell-switch barrier is an
  imperative path in Program.fs that's only LOOSELY tied
  to mode-change. Cleaning this up is a smaller backlog
  item; not blocking.

### Bell (BEL byte 0x07)

- **Source**: `Screen.executeC0` consumes BEL → publishes
  `Bell` notification.
- **Data carried**: none (event is itself the signal).
- **Pathway**: bypass — `OutputEventBuilder.fromScreenNotification`
  produces a `BellRang` semantic event. EarconProfile
  claims it; EarconChannel plays bell-ping.
- **Status**: ✅ Complete.
- **Known limitations**: BEL is rare; usually emitted by
  shells on tab-completion-conflict. Pty-speak's earcon
  + NVDA's "completed" handling overlap; user can mute
  via Ctrl+Shift+M (Stage 9).

### Parser error

- **Source**: VtParser overflow guards or unrecognised
  sequence handling.
- **Data carried**: a string describing the failure mode.
- **Pathway**: bypass — direct semantic event.
- **Status**: ✅ Functional but `Priority.Background` is
  not honoured (StreamProfile pass-through ignores
  Priority); errors announce despite intended-low priority.
- **Future fix**: when profiles become Priority-aware
  (Phase 2), ParserError defaults to silent.

### Hotkey (Ctrl+Shift+*)

- **Source**: WPF `KeyBinding` + `RoutedCommand` machinery,
  bound by `bindHotkey` in `Program.fs`.
- **Pathway**: bypass — handlers in `Program.fs` (top-level
  for window-only handlers, in-`compose` for handlers that
  capture local state).
- **Reserved hotkeys**:
  - `Ctrl+Shift+U` — Velopack auto-update (Stage 11)
  - `Ctrl+Shift+D` — diagnostic battery
  - `Ctrl+Shift+R` — draft-a-new-release form launcher
  - `Ctrl+Shift+L` — open logs folder
  - `Ctrl+Shift+;` — copy active log to clipboard
  - `Ctrl+Shift+1` / `+2` / `+3` — hot-switch the spawned
    shell
  - `Ctrl+Shift+G` — toggle FileLogger min-level
  - `Ctrl+Shift+H` — health-check announce
  - `Ctrl+Shift+B` — incident marker boundary line
- **Reserved (not yet bound)**:
  - `Ctrl+Shift+M` — Stage 9 earcon mute
  - `Alt+Shift+R` — Stage 10 review-mode toggle
  - `Ctrl+Shift+4` / `+5` / `+6` — additional shells
- **Output produced**: per-handler announces via
  `window.TerminalSurface.Announce`.
- **Status**: ✅ Complete for shipped hotkeys.

### Shell switch (Ctrl+Shift+1/2/3)

- **Source**: hotkey handler `switchToShell` in
  `Program.fs`.
- **Pathway**: imperative — disposes old `ConPtyHost`,
  creates new one, calls `activePathway.Reset()` +
  `activePathway.SetBaseline(canonical)` per pathway
  protocol. Mode-barrier event also fires.
- **Status**: ✅ Functional.
- **Future tightening**: shell-switch should emit a
  semantic event (`ShellSwitched`) instead of relying on
  the App-layer announce + mode-barrier double-mechanism.
  Backlog (small).

### Focus change

- **Source**: WPF window focus events (gain / lose).
- **Pathway**: bypass — emits OSC bracketed-focus-state
  bytes if DECSET ?1004 is enabled (`TerminalView.cs:654-673`).
- **Status**: ✅ Functional. No pathway involvement —
  bytes go straight to PTY stdin.
- **Future**: input pathway might intercept this for
  user-side announce ("terminal focused" / "terminal
  unfocused").

### Window resize

- **Source**: WPF `SizeChanged` event.
- **Effect today**: ConPTY pseudo-console is resized via
  `ResizePseudoConsole` Win32 call. Screen's row/col
  counts update.
- **Pathway**: bypass — direct ConPTY API call.
- **Status**: ✅ Functional but minimal.
- **Future**: pathway integration for "screen geometry
  changed; baseline reset needed" — currently handled
  implicitly by next RowsChanged.

### Update / version events

- **Source**: Velopack auto-update flow.
- **Pathway**: bypass — `runUpdateFlow` handler directly
  announces.
- **Status**: ✅ Complete (Stage 11).

### Diagnostic battery (Ctrl+Shift+D)

- **Source**: hotkey handler `runDiagnostic`.
- **Pathway**: bypass — runs in
  `Terminal.App.Diagnostics.runFullBattery`. Uses
  `OutputDispatcher.installEventTap` to observe events
  during the battery window.
- **Output produced**: announce + log-file artefact.
- **Status**: ✅ Complete (PR #165).

### Prompt boundary (OSC 133) — RESERVED

- **Source (future)**: VtParser → Screen.Apply OSC arm
  → new `ScreenNotification.PromptBoundary` event.
- **Data carried (future)**:
  - `BoundaryKind`: `PromptStart` (OSC 133 A),
    `CommandStart` (OSC 133 B), `OutputStart` (OSC 133 C),
    `CommandFinished` (OSC 133 D + exit code).
  - `Timestamp`: when the boundary was seen.
  - `ExitCode option`: only for D.
- **Pathway (future)**: `DisplayPathway.OnPromptBoundary`
  — new method on the protocol; concrete pathways
  (StreamPathway, ReplPathway) implement it.
- **Output produced (future)**: SessionModel mutations
  + `PromptDetected` / `CommandSubmitted` /
  `CommandFinished` semantic events (the placeholder
  cases in `OutputEventTypes.fs:77-81`).
- **Status**: ⚠ **NOT EMITTED — substrate gap**. Item 28
  ships this.
- **Heuristic fallback**: shells that don't emit OSC 133
  (cmd.exe by default, claude.exe per
  `spec/overview.md:71`) need heuristic prompt detection
  (regex on prompt patterns, Ink box-drawing detection
  for Claude Code). Specified in SessionModel design doc
  (item 28).

### Command finished (OSC 133 D) — RESERVED

- Same as prompt boundary; called out separately because
  it carries the exit code, which is what makes "command
  succeeded vs. failed" semantically tagged. Future
  features (review-only-failures, auto-rerun-on-failure)
  consume this.

### Claude Code response boundary — RESERVED (Phase 2)

- **Source (future)**: AI-output-pattern detection
  (heuristic, since Claude Code doesn't emit OSC 133).
  Probably looks for the box-drawing characters and
  prompt regex per `spec/overview.md:71`.
- **Pathway (future)**: ClaudeCodePathway — a SessionModel-
  aware pathway that maps Claude Code's input/output
  pattern to (user message, AI response) tuples.
- **Status**: 🔮 Future (Phase 2).

## Substrate inventory — what exists, what's missing

### Substrate components today (named, in-code)

| Component | Module | Role |
|---|---|---|
| **VtParser** | `src/Terminal.Parser/VtParser.fs` + `StateMachine.fs` | Bytes → CSI / SGR / text / OSC events |
| **Screen** | `src/Terminal.Core/Screen.fs` | Cell grid + mode flags + cursor + notifications |
| **CanonicalState** | `src/Terminal.Core/CanonicalState.fs` | Snapshot + sequence + per-row hashes + diff |
| **DisplayPathway protocol** | `src/Terminal.Core/DisplayPathway.fs` | Output-side pathway interface (Consume / Tick / OnModeBarrier / Reset / SetBaseline) |
| **StreamPathway** | `src/Terminal.Core/StreamPathway.fs` | Default pathway: row-diff + suffix-diff + frame-dedup + spinner-suppress |
| **TuiPathway** | `src/Terminal.Core/TuiPathway.fs` | Alt-screen pathway: emits no events while alt-screen is active |
| **PathwaySelector** | `src/Terminal.Core/PathwaySelector.fs` | Picks pathway per shell + alt-screen state |
| **OutputDispatcher** | `src/Terminal.Core/OutputDispatcher.fs` | Profile/Channel routing + event taps |
| **Profile registry** | (in OutputDispatcher) | StreamProfile, EarconProfile |
| **Channel registry** | (in OutputDispatcher) | NvdaChannel, EarconChannel, FileLoggerChannel |
| **NvdaChannel** | `src/Terminal.Core/NvdaChannel.fs` | UIA Notification bridge |
| **EarconChannel** | `src/Terminal.Core/EarconChannel.fs` | Earcon player wrapper |
| **EarconPlayer** | `src/Terminal.Audio/EarconPlayer.fs` | Tone synthesis + WASAPI playback |
| **FileLoggerChannel** | `src/Terminal.Core/FileLoggerChannel.fs` | Structured event logging |
| **FileLogger** | `src/Terminal.Core/FileLogger.fs` | Line-based file sink |
| **Config** | `src/Terminal.Core/Config.fs` | TOML parameter loader |
| **Terminal.App.Program** | `src/Terminal.App/Program.fs` | Composition root + PathwayPump + hotkey handlers |
| **Terminal.App.Diagnostics** | `src/Terminal.App/Diagnostics.fs` | Ctrl+Shift+D self-test battery |
| **HotkeyRegistry** | `src/Terminal.Core/HotkeyRegistry.fs` | Reserved hotkey IDs |
| **ShellRegistry** | `src/Terminal.Pty/ShellRegistry.fs` | Shell-id taxonomy + resolver |
| **TerminalView** | `src/Views/TerminalView.cs` | WPF rendering + input + UIA peer |
| **TerminalAutomationPeer** | `src/Terminal.Accessibility/TerminalAutomationPeer.fs` | UIA Document/Text-pattern provider |
| **KeyEncoding** | `src/Terminal.Pty/KeyEncoding.fs` | WPF Key → byte[] for PTY stdin |
| **ConPtyHost** | `src/Terminal.Pty/ConPtyHost.fs` | ConPTY wrapper with stdin/stdout |
| **AnnounceSanitiser** | `src/Terminal.Core/AnnounceSanitiser.fs` | Strip control chars from announcement payloads |

### Substrate gaps (named research-stage entries)

| Gap | Status | Backlog item | Substrate role when shipped |
|---|---|---|---|
| **InputPathway protocol** | Not started | Phase 2 input framework cycle (in PROJECT-PLAN-2026-05) | Mirror of DisplayPathway for the input side; intercepts keystrokes / paste / focus before they hit ConPtyHost. Required for echo correlation, autocomplete announcement, per-shell input transformation. |
| **SessionModel substrate** | Not started | Item 28 | Holds (prompt, command, output, exit-code) tuples. Sourced from OSC 133 + heuristics. Persisted across sessions. Required for history navigation, command-output review, scroll-misalignment correctness. |
| **Cursor-aware diff** | Not started | Phase 2 ReplPathway prerequisite | Cursor position threading into Canonical / DisplayPathway; required for "announce char at cursor" on arrow-key navigation. |
| **Echo correlation** | Not started | Phase 2 input framework cycle | Track outgoing keystrokes; suppress matching screen mutations; resolves UX issue #1 (double-announce). |
| **Per-input-vs-output ActivityId routing** | Not started | Phase 2 | Separate `pty-speak.input-echo` ActivityId with different NVDA processing; resolves UX issue #4 (stuttering). |
| **Scrollback / history navigation** | Not started | Item 27 (downstream of item 28) | Query SessionModel for content beyond visible window. |
| **Profile.Priority awareness** | Reserved | Phase 2 | Profiles consume `Priority` field; ParserError-as-Background actually suppresses. |
| **Per-shell parameter overrides** | Reserved | Phase B / TOML | Different `bulk_change_threshold` (etc.) for cmd / PowerShell / claude. |

## Pathway taxonomy

A pathway is a concrete implementation of the
`DisplayPathway.T` protocol — five methods (Consume, Tick,
OnModeBarrier, Reset, SetBaseline) that turn canonical-state
mutations into output events. Today's roster is small;
Phase 2 expands it considerably.

### Pathways shipped today

#### StreamPathway

- **Module**: `src/Terminal.Core/StreamPathway.fs`
- **Default for**: cmd, PowerShell, Claude Code, fallback
  for unknown shells.
- **Behaviour**: row-level diff, sub-row suffix-diff,
  frame-dedup, spinner-suppression, debounce-window-based
  leading-edge + trailing-edge emit, colour detection,
  bulk-change fallback, parameterised backspace policy,
  parameterised mode-barrier flush policy.
- **State**: `LastFrameHash`, `LastEmittedRowHashes`,
  `LastEmittedRowText`, `PendingDiff`, `PendingFrameHash`,
  `PendingColor`, `PendingSnapshot`, `LastRowHashes`,
  spinner / hash history.
- **Tests**: `tests/Tests.Unit/StreamPathwayTests.fs` (~70
  tests).
- **Limitations**: per stage 8 known fragility — scroll
  misalignment, mid-line-insertion over-reporting,
  Powerline / autosuggestion over-reporting.

#### TuiPathway

- **Module**: `src/Terminal.Core/TuiPathway.fs`
- **Default for**: vim, less, htop, any alt-screen
  consumer.
- **Behaviour**: emits NO events while alt-screen is
  active. The screen is treated as a TUI canvas rather
  than streaming output; users use NVDA's review-cursor
  to navigate.
- **State**: minimal; just an "is alt-screen active"
  flag.
- **Tests**: `tests/Tests.Unit/TuiPathwayTests.fs`.
- **Hot-switch**: `PathwaySelector` picks TuiPathway
  on alt-screen entry; switches back to StreamPathway
  on alt-screen exit.

### Pathways future (Phase 2 / 3)

Each future pathway is named here as a vocabulary
reservation. Implementation lives in the framework cycles.

#### ReplPathway

- **Status**: 🔮 Future (Phase 2)
- **Default for**: Python REPL, Node REPL, ipython, any
  shell with explicit prompt boundaries.
- **Substrate prerequisites**:
  - OSC 133 parsing (item 28)
  - SessionModel substrate (item 28)
  - Cursor-aware diff (Phase 2)
- **Behaviour**: consumes SessionModel; treats input-line
  + output-block as a SEMANTIC UNIT rather than per-row.
  Announces "you typed X; result was Y; exit code Z" as
  coherent units. Supports navigation across recent
  command-tuples.
- **Tests**: TBD.

#### FormPathway

- **Status**: 🔮 Future (Phase 2)
- **Default for**: gum, fzf, any TUI selection / form
  interface.
- **Substrate prerequisites**:
  - Selection event detection (e.g. via screen-pattern
    matching or shell hints)
  - Cursor position awareness
- **Behaviour**: announces "you can choose from N
  options" + per-cursor-movement "currently on option X".
  Uses the SelectionShown / SelectionItem /
  SelectionDismissed semantic categories (placeholders
  in `OutputEventTypes.fs:50-55`).
- **Tests**: TBD.

#### ClaudeCodePathway

- **Status**: 🔮 Future (Phase 2)
- **Default for**: claude.exe.
- **Substrate prerequisites**:
  - Heuristic prompt detection (Claude Code doesn't emit
    OSC 133 per `spec/overview.md:71`)
  - SessionModel substrate
- **Behaviour**:
  - Suppresses red text in startup banner (resolves
    backlog item 22).
  - Treats user message + AI response as a tuple, similar
    to ReplPathway.
  - Provides session-history navigation across the
    conversation.
- **Tests**: TBD.

#### AiInterpretedPathway

- **Status**: 🔮 Future (Phase 3)
- **Default for**: any pathway where the user opts into
  AI summarisation.
- **Substrate prerequisites**:
  - SessionModel substrate
  - AI inference pipeline (separate concern)
  - User opt-in flow (see `SECURITY.md` AI privacy
    rows)
- **Behaviour**: instead of announcing literal output,
  announces an AI-generated summary of each output block.
  E.g. "command produced 100 rows of file listing;
  largest file is X" rather than reading 100 rows.
- **Tests**: TBD.

#### SessionConsumer (base / mixin)

- **Status**: 🔮 Future (item 28 introduces)
- **Role**: a pathway "concern" that ReplPathway,
  FormPathway, ClaudeCodePathway, AiInterpretedPathway
  share. Provides:
  - SessionModel mutation handling
  - Tuple-aware event emission (per-tuple boundaries)
  - History navigation hooks
- **Implementation**: probably via composition rather
  than inheritance (F# doesn't favour deep class
  hierarchies). A `SessionAwarePath` record that wraps a
  `DisplayPathway.T` with additional `OnPromptBoundary`
  + `OnSessionUpdate` methods.

### Pathway selection — current state

`PathwaySelector` (`src/Terminal.Core/PathwaySelector.fs`)
decides which pathway is active based on:

1. **Mode flag**: alt-screen active → TuiPathway;
   alt-screen inactive → StreamPathway.
2. **Shell hint**: per-shell defaults can override (TOML
   `[shell.<id>] pathway = "stream" | "tui"`). Today's
   default for all shells is StreamPathway with TuiPathway
   on alt-screen entry.

Future selector will consider:

- Per-shell pathway preferences (REPL for Python,
  ClaudeCodePathway for claude.exe, etc.)
- Per-app overrides (claude inside cmd → ClaudeCodePathway)
- User-side configuration (`config.toml`)

## End-to-end traces

Two representative flows, each annotated with where data
lives, which threads operate, which timing concerns apply,
where parameters affect behaviour.

### Trace A: a single keystroke

The user presses `e` at the cmd.exe prompt. Walk through
every stage:

#### A.1 — User presses `e`

**Hardware → OS**: keyboard interrupt → Windows raw input
→ WPF input pipeline.

**Threading**: WPF UI dispatcher thread.

**Data**: a Win32 `WM_KEYDOWN` then `WM_CHAR` event for
the `e` key. WPF translates into `KeyEventArgs`.

#### A.2 — WPF KeyDown handler (TerminalView.cs:537)

**Module**: `src/Views/TerminalView.cs`

**Code path**: `OnPreviewKeyDown(KeyEventArgs e)` is the
entry point. Pre-filter checks for app-reserved hotkeys
(Ctrl+Shift+*) — if matched, marks `e.Handled = false` so
the WPF KeyBinding machinery on the parent window can
fire its `RoutedCommand`. For non-hotkeys (the `e` keystroke
case), continues to character translation.

**Threading**: WPF UI dispatcher thread.

**Translation**:

```
e.Key  →  TranslateKey(e.Key)  →  KeyCode.E
e.KeyboardDevice.Modifiers  →  TranslateModifiers(...)  →  KeyModifiers.None
```

`KeyCode` and `KeyModifiers` are pty-speak's own platform-
neutral types (`src/Terminal.Pty/KeyEncoding.fs:50`).

**Data state**: the WPF input pipeline carries no terminal
context — `_screen.Modes` is consulted separately because
some keys translate differently depending on alt-screen
mode (e.g. function keys).

#### A.3 — KeyEncoding.encode

**Module**: `src/Terminal.Pty/KeyEncoding.fs`

**Function**: `KeyEncoding.encodeOrNull(keyCode, keyMods,
screenModes)` returns `byte[] | null`.

**Logic for `e`**: lowercase 'e' has no modifier; encode
as ASCII byte `0x65` (`byte 'e'`).

**Output**: `[| 0x65uy |]`.

**Threading**: still WPF dispatcher.

**Notes on input pathway absence**: today, this byte goes
DIRECTLY to ConPtyHost. There is no pathway intercept
between KeyEncoding and the PTY. Future Phase 2 input
framework introduces an `IInputPathway` here:

- The pathway sees `IntendedKeystroke` events with
  KeyCode + bytes + modifier-state + timestamp.
- The pathway can:
  - Pass through (emit bytes to PTY) — the default for
    most keys.
  - Intercept (handle in pty-speak; do NOT emit) — for
    autocomplete UI navigation, hotkey gestures.
  - Augment (emit bytes AND emit a pathway-side
    announcement) — for "user typed X; pty-speak
    pre-announces 'X' to NVDA".
- The pathway's `OnKeyPress` method would feed
  `KeystrokeTracker.record` for echo correlation.

**Status**: ⚠ this stage is the largest substrate gap. No
input-side pathway exists.

#### A.4 — ConPtyHost.WriteBytes

**Module**: `src/Terminal.Pty/ConPtyHost.fs:49-56`

**Code path**: `ConPtyHost.WriteBytes(bytes)` writes the
byte array into the pseudo-console's input pipe via a
`FileStream` constructed from the `SafeFileHandle`
returned by `CreatePseudoConsole`.

```
host.WriteBytes([| 0x65uy |])
  → stdin.Write([| 0x65uy |], 0, 1)
  → stdin.Flush()
```

**Threading**: WPF dispatcher (same thread as Key handler).
ConPtyHost's contract requires single-threaded callers —
`Stage 6's wiring funnels every write through the WPF
dispatcher thread, so serialisation is structural rather
than enforced` (per ConPtyHost.fs:49 comment).

**Output**: the byte `0x65` is now sitting in cmd.exe's
stdin pipe.

#### A.5 — cmd.exe processes the byte

**External (out of pty-speak's control)**: cmd.exe's line-
editor consumes the `e`. It echoes `e` back through
stdout (the shell echoes typed characters; that's how
the user sees what they typed). Cursor advances by 1
column.

**Threading**: cmd.exe's own threads. We don't see them.

**Output**: byte `0x65` → cmd.exe's stdout pipe.

#### A.6 — Stage 1: Byte ingestion

**Module**: `src/Terminal.Pty/ConPtyHost.fs` reader thread

**Code path**: a dedicated reader thread polls the pseudo-
console's stdout `FileStream`. When bytes arrive, they're
buffered and pushed to a downstream channel.

**Threading**: per-shell reader thread (runs forever for
the lifetime of one ConPtyHost child).

**Data**: 1 byte (`0x65`) arrives. The reader thread
calls into `VtParser.feedArray` with the byte.

**Notes**: real shells emit bytes in chunks, not byte-by-
byte. For an interactive type-then-echo, the chunk size
is typically 1-3 bytes. For DIR / similar bulk output, it
can be hundreds.

#### A.7 — Stage 2: Parser application

**Module**: `src/Terminal.Parser/VtParser.fs` +
`src/Terminal.Parser/StateMachine.fs`

**Code path**: VtParser's state machine consumes `0x65`.
It's a printable ASCII character; the state machine emits
a `Print 0x65` event (see StateMachine.fs).

**Output**: `VtEvent.Print 0x65uy`.

**Pass-through**: `Screen.Apply` receives the Print event,
writes the cell at the current cursor position with
content `e` (and current SGR attributes), advances the
cursor by 1.

**Threading**: still on the reader-pump thread (single
thread feeds parser → screen).

**Locking**: `Screen.Apply` takes the gate-lock for the
duration. `SnapshotRows` (called later by canonical-state
synthesis) takes the same lock; this serialises mutations
against snapshots.

**Side effects**: the cell at row N, col M is now `e`.
Cursor is at row N, col M+1. Internal `sequenceNumber`
bumps from S to S+1.

#### A.8 — Stage 3: Notification emission

**Module**: `src/Terminal.Core/Screen.fs` event publishers

**Code path**: after `Screen.Apply` completes a batch
(the reader-pump batches multiple events per call when
multiple bytes arrive), it publishes a `RowsChanged`
notification with the list of changed row indices.

**Output**: `RowsChanged [| N |]` (just one row changed).

**Channel**: pushed onto `notificationChannel` (a
`BoundedChannel<ScreenNotification>`, 256-capacity, drop-
oldest-on-full).

**Threading**: publish from reader-pump thread; consume
from PathwayPump thread.

#### A.9 — Stage 4: Canonical-state synthesis

**Module**: `src/Terminal.Core/CanonicalState.fs`

**Code path** (from `Program.fs:handleRowsChanged`):

```
let seq, snapshot = screen.SnapshotRows(0, screen.Rows)
let canonical = CanonicalState.create snapshot seq
```

`SnapshotRows` takes the screen lock, copies all 30 rows
of cells, returns. `CanonicalState.create` computes
`RowHashes` and `ContentHashes` on the snapshot, packs
into the immutable record.

**Output**: a `Canonical` with snapshot, seq number, row
hashes.

**Threading**: PathwayPump thread.

**Cost**: O(rows × cols) for snapshot copy + hash
computation. ~30 × 120 = 3600 cells. Fast (microseconds).

#### A.10 — Stage 5: Frame-dedup

**Module**: `src/Terminal.Core/StreamPathway.fs`
(`processCanonicalState`)

**Code path**: pathway computes `frameHash = XOR of all
RowHashes`. Compares against `state.LastFrameHash`. They
differ (cell at row N changed); frame-dedup branch does
NOT short-circuit.

**State**: `LastFrameHash` updated to the new value at
end of processing.

#### A.11 — Stage 6: Spinner suppression

**Code path**: checks per-row spinner history. Single
keystroke doesn't match a spinner pattern; suppression
does NOT fire.

#### A.12 — Stage 7: Row-level diff

**Code path**: pathway calls `canonical.computeDiff
state.LastEmittedRowHashes`. Compares each row's hash
against the previous-emit hash. Only row N's hash differs
(cell at col M changed from blank to `e`).

**Output**: `CanonicalDiff { ChangedRows = [| N |];
ChangedText = "<prompt>e" }` (the rendered text of row N).

#### A.13 — Stage 8: Sub-row suffix detection

**Code path**: `assembleSuffixPayload` is called.
`ChangedRows.Length = 1 ≤ BulkChangeThreshold = 3`, so
the suffix-diff path applies. For row N:

- `currentText = renderRow snapshot N` → `"<prompt>e"`
- `previousText = state.LastEmittedRowText[N]` → `"<prompt>"`
- LCP("<prompt>e", "<prompt>") = length of "<prompt>"
- `currentText.Length > previousText.Length` (Append case)
- `Suffix "e"`

**Output**: per-row delta `Suffix "e"` for row N.

#### A.14 — Stage 9: Announcement payload assembly

**Code path**: `assembleSuffixPayload` returns `"e"`.
`capAnnounce parameters payload` checks length against
`MaxAnnounceChars = 500`. `"e".Length = 1`; no
truncation.

**Output**: payload string `"e"`.

#### A.15 — Stage 10: Profile claim

**Code path**: `streamOutputEvent "e"` builds an
`OutputEvent { Semantic = StreamChunk; Priority = Polite;
... Payload = "e" }`. Calls `OutputDispatcher.dispatch
event`.

`StreamProfile.Apply event` claims it (pass-through),
emits a `(event, [|{Channel = NvdaChannel.id; Render =
RenderText "e"}; {Channel = FileLoggerChannel.id; Render
= RenderText "e"}|])` pair.

`EarconProfile.Apply event` ignores (StreamChunk has no
earcon claim today).

**Threading**: still PathwayPump thread.

#### A.16 — Stage 11: Channel rendering

**NvdaChannel.Send**: receives `(event, RenderText "e")`.
Calls `marshalAnnounce ("e", "pty-speak.output")`.

**FileLoggerChannel.Send**: receives `(event, RenderText
"e")`. Calls `logger.LogInformation` with the structured
template; line written to log file via FileLogger sink.

**Threading**: NvdaChannel's marshalAnnounce hops to WPF
dispatcher (via `window.Dispatcher.InvokeAsync`).
FileLoggerChannel writes from PathwayPump thread (the
FileLogger sink is itself thread-safe).

#### A.17 — Stage 12: NVDA dispatch

**Module**: `src/Views/TerminalView.cs.Announce`

**Code path** (on WPF dispatcher thread): calls
`AutomationProperties.SetName(_terminalSurface,
"e")` then `peer.RaiseNotificationEvent(...)` with
`NotificationProcessing.ImportantAll` and ActivityId
`"pty-speak.output"`.

**Output**: a UIA NotificationEvent fires. NVDA observes
it, queues "e" for speech, eventually speaks it.

#### A.18 — User hears `e`

End to end: ~tens of milliseconds from keypress to NVDA
audible output, depending on NVDA's voice rate and the
debounce window. The trace took ~17 stages to walk
through; the maintainer hears the result as a single
spoken character.

### Trace B: a single byte of output (DIR scenario)

A more complex flow: cmd.exe responds to `dir` by
streaming dozens of rows of output. Walk through how
pty-speak handles the first chunk of bytes.

#### B.1 — cmd.exe writes output bytes

**External**: cmd.exe processes `dir` and starts emitting
the listing. First batch is the header:
" Volume in drive C is OS\r\nVolume Serial Number is
24C0-BD5D\r\n Directory of C:\Users\...\r\n\r\n" — about
100 bytes.

**Output**: bytes flow into the pseudo-console's stdout
pipe.

#### B.2 — Stage 1: Byte ingestion (chunked)

**Code path**: ConPtyHost reader thread sees ~100 bytes
arrive in one read. Pushes them through to VtParser.

**Note**: bytes can arrive in arbitrary chunks. The
parser is byte-stream-tolerant — it doesn't care if
multi-byte sequences are split across reads.

#### B.3 — Stage 2: Parser application (multi-event)

VtParser's state machine produces a sequence of events
for the chunk:

```
Print ' ', Print 'V', Print 'o', ..., Print '\n',
Print 'V', Print 'o', Print 'l', ..., Print '\n',
...
```

`\n` (LF) goes through the state machine's executeC0
arm; it advances the cursor to column 0 of the next row
(possibly scrolling).

`\r` (CR) goes through the same arm; resets cursor to
column 0 of the current row.

**Side effects**: many cells written; cursor advances
across multiple rows; possibly scrolls.

#### B.4 — Stage 3: Notification (batched)

After the entire chunk processes, ONE `RowsChanged`
notification is published with the FULL set of changed
row indices (e.g. `[| 0; 1; 2; 3; 4; 5 |]` if 6 rows
changed).

**Note**: notifications are BATCHED per `Screen.Apply`
call, not per VT event. The reader-pump's batching
strategy reduces noise.

#### B.5 — Stage 4: Canonical-state synthesis

Same as A.9. The new Canonical reflects the post-chunk
state.

#### B.6 — Stages 5-7: dedup / spinner / diff

**Frame-dedup**: not deduped; frame hash differs.

**Spinner-suppression**: not suppressed; pattern doesn't
match.

**Row-level diff**: against the LastEmittedRowHashes (which
reflect the state JUST AFTER the user typed `dir` and
pressed Enter). The diff shows ~6+ rows changed.

#### B.7 — Stage 8b: Bulk-change fallback engages

`ChangedRows.Length = 6 > BulkChangeThreshold = 3`. The
suffix-diff stage is BYPASSED. Payload becomes
`diff.ChangedText` — the verbose, newline-joined
rendered text of all changed rows.

**Implication**: the user hears all 6 rows in one
announcement. NVDA queues ~600 characters.

#### B.8 — Stage 9: Truncation check

If `ChangedText.Length > MaxAnnounceChars (500)`, the
`capAnnounce` function truncates to 500 chars + appends
"...announcement truncated; X more characters available
— press Ctrl+Shift+; to copy full log.".

For 6-row DIR header at ~100 bytes: under 500. Full
content survives.

For larger DIR output (subsequent chunks may have many
rows): truncation kicks in.

#### B.9 — Stages 10-12: dispatch + render + NVDA

Same as trace A. The 500-char (or smaller) payload is
queued to NVDA via UIA NotificationEvent.

**NVDA's behaviour**: with `NotificationProcessing.ImportantAll`,
new announcements interrupt current speech. As more DIR
chunks arrive (B.10 below), they queue and may interrupt.

#### B.10 — More chunks arrive

cmd.exe continues streaming DIR output. Each chunk goes
through stages 1-12. Each produces a separate `OutputEvent`
+ NVDA announcement.

**Trailing-edge timer**: between chunks, the
`onTimerTick` periodic timer (50ms cadence) fires. If
`debounceWindow` (200ms) has elapsed since the last emit
and there's no pending diff, no-op.

**Result**: user hears 1-3 large announcements per second
during the DIR streaming period.

#### B.11 — DIR completes; new prompt printed

cmd.exe finishes printing files, then prints the new
prompt `C:\...>`. This is one more chunk → one more
RowsChanged → one more (small) StreamChunk.

**Scroll fragility (current limitation)**: if DIR's
output filled the screen and the new prompt scrolls past
the bottom, the cache `LastEmittedRowText` is now
misaligned with the screen. The diff sees "all rows
changed" → bulk-change fallback → emits the full POST-
SCROLL content (which includes most of the DIR listing
that just shifted up).

**Documented**: per stage 8 known fragility. Resolved
when SessionModel substrate (item 28) ships and the diff
becomes content-aware rather than position-aware.

## Seams catalogue

For each future capability, the pipeline-stage seam where
it lands. Used to scope new feature design.

### Echo correlation (Phase 2)

- **Seam**: between InputPathway (new substrate) and
  Stage 8 (sub-row suffix detection).
- **Mechanism**: InputPathway records each outgoing
  keystroke into a `KeystrokeTracker` with timestamp.
  Stage 8's `computeRowSuffixDelta` consults the
  tracker; if the suffix matches a recent keystroke,
  return `Silent` (echo suppressed).
- **Parameter**: `[stream.echo_correlation] policy =
  "track_keystrokes" | ...` (atlas item).

### OSC 133 / SessionModel (item 28)

- **Seam**: between Stage 2 (parser application) and
  Stage 3 (notification emission). The parser already
  emits OSC events; adding OSC 133 means a new arm in
  `Screen.Apply`'s OSC dispatcher that emits a
  `ScreenNotification.PromptBoundary` event, which the
  pump then routes to a new `OnPromptBoundary` method on
  `DisplayPathway.T`.
- **Mechanism**:
  1. VtParser → `OscDispatch(["133", "A"], ...)` event
     for OSC 133 A (prompt-start).
  2. `Screen.Apply`'s OSC arm matches `parms.[0] = "133"`,
     calls `handleOsc133 parms` which builds a
     `PromptBoundaryData { Kind = PromptStart; ... }`
     and publishes `ScreenNotification.PromptBoundary`.
  3. PathwayPump's reader-loop has a new match arm for
     `PromptBoundary`; calls
     `activePathway.OnPromptBoundary boundaryData`.
  4. SessionConsumer (the pathway base for SessionModel-
     aware pathways) translates the boundary into
     SessionModel mutations: open new tuple on PromptStart,
     close on CommandFinished, append output on screen
     mutation between OutputStart and CommandFinished.
- **Spec**: see future `docs/SESSION-MODEL.md`.

### Cursor-aware diff (Phase 2 ReplPathway prerequisite)

- **Seam**: stage 4 (Canonical-state synthesis) gains
  cursor position; stage 7 (row-level diff) and stage 8
  (sub-row suffix detection) consume it.
- **Mechanism**: `Canonical` record gains a `CursorPosition:
  (int * int)` field (row, col). DisplayPathway state
  carries previous cursor position. Suffix-diff has
  cursor-position-aware logic: when cursor is mid-line and
  user types, the diff is "current text from cursor-back-1
  to cursor" rather than "current text beyond LCP".
- **Parameter**: `[stream.cursor_announcement] enabled`,
  `mode` (atlas items).

### Per-input-vs-output ActivityId routing

- **Seam**: stage 11 (channel rendering) gains per-event
  routing-hint; NvdaChannel's `semanticToActivityId` reads
  the hint.
- **Mechanism**: StreamPathway's emit path classifies each
  `OutputEvent` with a routing hint (`RoutingHint =
  Output | InputEcho`). The hint is set based on
  echo-correlation result. NvdaChannel maps:
  - `RoutingHint.Output` → `pty-speak.output` (current)
  - `RoutingHint.InputEcho` → `pty-speak.input-echo`
    (new)
- **Parameter**: `[notification.activity_ids.input_echo]`
  + `[notification.processing.input_echo]` (atlas items).

### Scrollback / history navigation (item 27, post-item-28)

- **Seam**: SessionModel substrate is the source of
  truth; new hotkey-handler-pathway emits "review-cursor
  past commands" announcements.
- **Mechanism**: SessionModel's `tuples: List<SessionTuple>`
  is queryable. Hotkey (e.g. `Alt+Up` for previous
  command, `Alt+Down` for next) navigates an in-memory
  cursor over the tuples. Pathway emits the relevant
  tuple's content as a StreamChunk (via a stream-like
  pseudo-pathway) when the cursor moves.
- **Parameter**: TBD; navigation hotkeys + perhaps
  preferred-tuple-detail-level.

### AI-summarisation (Phase 3)

- **Seam**: a new pathway type (AiInterpretedPathway)
  consumes SessionModel; emits AI-generated summaries
  rather than literal output content.
- **Mechanism**: per command-finished event, the pathway
  POSTs the output content to an AI endpoint, receives
  a summary, emits the summary as the StreamChunk.
- **Privacy**: per `SECURITY.md`, opt-in only; never
  default.
- **Parameter**: `[ai.summarisation] enabled = false` +
  per-shell overrides + endpoint config.

### Profile.Priority awareness (Phase 2)

- **Seam**: stage 10 (profile claim) gains priority
  consultation; today's StreamProfile pass-through ignores
  Priority.
- **Mechanism**: profiles read `OutputEvent.Priority`;
  `Background` priority events are silently dropped at
  stage 10 (no ChannelDecision emitted). Resolves the
  ParserError "always announces" issue.

### Per-shell parameter overrides (Phase B / TOML)

- **Seam**: Config.fs schema gains nested per-shell
  sections; resolveStreamParameters reads shell-specific
  overrides on top of global defaults.
- **Mechanism**:
  ```toml
  [pathway.stream]
  bulk_change_threshold = 3  # global default
  [pathway.stream.shell.powershell]
  bulk_change_threshold = 5  # PowerShell-specific override
  ```

## Vocabulary glossary

Alphabetised reference for every named term in this doc.
Definitions are intentionally brief; code references go
back to the relevant module.

- **AnnounceSanitiser** — strips control characters from
  announcement payloads. Module:
  `src/Terminal.Core/AnnounceSanitiser.fs`.
- **ActivityId** — UIA Notification tag (e.g.
  `pty-speak.output`, `pty-speak.diagnostic`). Each
  semantic category routes to one ActivityId. NVDA
  applies per-tag rules (NotificationProcessing).
- **AiInterpretedPathway** — future pathway (Phase 3) that
  emits AI summaries rather than literal output. Atlas
  parameter: `[ai.summarisation]`. Privacy: opt-in.
- **AnnouncementPayload** — string assembled at stage 9
  for the StreamChunk OutputEvent. Cap-able via
  `MaxAnnounceChars`.
- **BackspacePolicy** — parameter controlling stage 8's
  Shrink branch behaviour. Values:
  `SuppressShrink | AnnounceDeletedCharacter |
  AnnounceDeletedWord`.
- **Bell** — ScreenNotification emitted on BEL byte
  (0x07) consumed.
- **BellRang** — semantic category emitted by
  OutputEventBuilder on Bell notification. Routes to
  EarconChannel.
- **BulkChangeFallback** — stage 8b heuristic; when
  changed-rows-count exceeds threshold, emit
  `diff.ChangedText` (verbose) rather than per-row
  suffix.
- **BulkChangeThreshold** — parameter (default 3)
  controlling stage 8b activation. Atlas item.
- **Canonical** — immutable record produced by stage 4.
  Carries Snapshot, SequenceNumber, RowHashes,
  ContentHashes, computeDiff.
- **CanonicalState** — module containing Canonical +
  computeDiff + renderRow helpers. Module:
  `src/Terminal.Core/CanonicalState.fs`.
- **Cell** — single character + SGR attributes at one
  grid position. Module: `src/Terminal.Core/Types.fs`.
- **ChangedRows** — sorted array of row indices in a
  CanonicalDiff.
- **ChangedText** — newline-joined rendered text of
  ChangedRows; the verbose-emit payload.
- **Channel** — output sink. Today: NvdaChannel,
  EarconChannel, FileLoggerChannel.
- **ChannelDecision** — `(channelId, render)` pair
  emitted by a Profile per dispatched event.
- **ClaudeCodePathway** — future pathway (Phase 2) for
  claude.exe. SessionModel-aware. Suppresses banner
  red text, treats user/AI message pairs as tuples.
- **ColorDetection** — parameter (default true) gating
  ErrorLine / WarningLine emission per dominant row
  colour at stage 8.
- **CommandFinished** — semantic category (and reserved
  ScreenNotification variant) for OSC 133 D events.
  Carries exit code.
- **CommandSubmitted** — semantic category placeholder
  in OutputEventTypes; producer ships when input-side
  echo-correlation lands.
- **ConPtyHost** — wrapper around Win32 ConPTY APIs.
  Module: `src/Terminal.Pty/ConPtyHost.fs`. Owns
  pseudo-console handle + stdin / stdout streams.
- **ContentHashes** — per-row position-independent hashes
  on the Canonical record. Reserved for cross-row dedup.
- **CursorAware diff** — future stage-7 / stage-8
  enhancement that includes cursor position in diff
  reasoning. Phase 2.
- **DebounceWindow** — parameter (default 200ms)
  governing stage-5 leading-edge / trailing-edge timing.
- **DisplayPathway** — output-side pathway protocol.
  Methods: Consume, Tick, OnModeBarrier, Reset,
  SetBaseline. Module:
  `src/Terminal.Core/DisplayPathway.fs`.
- **DisplayPathway.T** — the F# record type implementing
  the protocol.
- **EarconChannel** — output channel for earcon playback.
- **EarconPlayer** — tone synthesis + WASAPI playback.
- **EarconProfile** — profile that claims ErrorLine /
  WarningLine / BellRang semantics; emits earcon-render
  decisions.
- **EchoCorrelation** — future Phase 2 substrate;
  matches outgoing keystrokes to subsequent screen
  mutations to suppress double-announce.
- **EditDelta** — result of stage 8 per-row computation.
  `Suffix string | Silent`.
- **ErrorLine** — semantic category emitted by
  StreamPathway when a changed row's dominant colour is
  red. Routes to EarconChannel for error-tone.
- **EventTap** — short-lived observer of
  OutputDispatcher events. Used by Diagnostics.
- **FileLogger** — line-based file sink. Module:
  `src/Terminal.Core/FileLogger.fs`.
- **FileLoggerChannel** — output channel that logs every
  OutputEvent at Information level.
- **FormPathway** — future pathway (Phase 2) for
  selection / form UIs (gum, fzf).
- **FrameDedup** — stage 5; suppresses identical-frame
  re-emits via XOR-of-row-hashes.
- **FrameHash** — XOR of row hashes; stage 5's primary
  key.
- **HotkeyRegistry** — reserved hotkey IDs. Module:
  `src/Terminal.Core/HotkeyRegistry.fs`.
- **InputPathway** — future Phase 2 substrate; mirror
  of DisplayPathway for the input side. Not yet
  implemented.
- **KeyEncoding** — translation of WPF Key + modifiers
  to PTY-stdin bytes. Module:
  `src/Terminal.Pty/KeyEncoding.fs`.
- **KeystrokeTracker** — future Phase 2 component;
  records outgoing keystrokes for echo correlation.
- **LastEmittedRowHashes** — StreamPathway state field;
  baseline for stage 7 diff.
- **LastEmittedRowText** — StreamPathway state field;
  baseline for stage 8 suffix-diff.
- **LastFrameHash** — StreamPathway state field;
  baseline for stage 5 frame-dedup.
- **MaxAnnounceChars** — parameter (default 500)
  governing stage 9 truncation.
- **ModeBarrier** — auxiliary stage; fires between
  stages 4 and 5 on shell-switch / alt-screen / mode-
  change. Pathway has `OnModeBarrier` method.
- **ModeBarrierFlushPolicy** — parameter (default
  SummaryOnly) controlling mode-barrier behaviour.
  Values: `Verbose | SummaryOnly | Suppressed`.
- **ModeChanged** — ScreenNotification variant.
- **NotificationChannel** — bounded
  System.Threading.Channels channel between Screen
  publishers and PathwayPump.
- **NotificationProcessing** — UIA enum dictating NVDA's
  per-event behaviour: `ImportantAll | All |
  MostRecent | CurrentThenMostRecent`.
- **NvdaChannel** — output channel that bridges to UIA
  NotificationEvent.
- **OnModeBarrier** — DisplayPathway protocol method
  for mode-barrier handling.
- **OnPromptBoundary** — future DisplayPathway protocol
  method (item 28). Reserved.
- **OutputDispatcher** — module containing dispatch +
  Profile/Channel registries + event tap surface.
- **OutputEvent** — record carrying semantic + payload +
  priority + verbosity + source + extensions. Flows from
  pathway through profiles to channels.
- **OSC** — Operating System Command (escape sequences
  starting with `ESC ]`). Used by shells for various
  signalling.
- **OSC 133** — semantic-prompt protocol; emits
  prompt-start / command-start / output-start /
  command-finished sequences.
- **PathwayPump** — Program.fs reader loop that consumes
  ScreenNotifications and dispatches to active pathway.
- **PathwaySelector** — picks pathway per shell + alt-
  screen state. Module:
  `src/Terminal.Core/PathwaySelector.fs`.
- **PendingDiff** — StreamPathway state field; carries
  accumulated diff during debounce window.
- **PendingSnapshot** — StreamPathway state field;
  carries snapshot reference for trailing-edge suffix-
  diff.
- **PromptBoundary** — future ScreenNotification variant
  (item 28). Reserved.
- **PromptDetected** — semantic category placeholder.
  Producer ships with OSC 133.
- **Priority** — OutputEvent field; values
  `Background | Polite | Assertive`. Today honoured
  only loosely.
- **Profile** — module that claims OutputEvents and
  emits ChannelDecisions. Today: StreamProfile,
  EarconProfile.
- **ReplPathway** — future pathway (Phase 2) for shells
  with explicit prompt boundaries.
- **RowHashes** — per-row position-aware hashes on
  Canonical.
- **RowsChanged** — ScreenNotification variant; lists
  changed row indices.
- **Screen** — cell grid + mode flags + cursor.
  Module: `src/Terminal.Core/Screen.fs`.
- **ScreenNotification** — discriminated union of events
  emitted by Screen. Today: `RowsChanged | ParserError |
  ModeChanged | Bell`. Reserved future:
  `PromptBoundary`.
- **SemanticCategory** — discriminated union of event
  types. StreamChunk, ErrorLine, WarningLine, BellRang,
  PromptDetected, CommandSubmitted, etc.
- **SequenceNumber** — monotonic int64 on Canonical;
  bumped per Screen.Apply call.
- **SessionConsumer** — future pathway base for
  SessionModel-aware behaviours. Reserved.
- **SessionModel** — future substrate (item 28); holds
  (prompt, command, output, exit-code) tuples sourced
  from OSC 133.
- **SetBaseline** — DisplayPathway protocol method;
  seeds baseline on hot-switch.
- **ShellRegistry** — shell-id taxonomy + resolver.
  Module: `src/Terminal.Pty/ShellRegistry.fs`.
- **Snapshot** — Cell[][] copy produced by Screen.SnapshotRows.
- **SpinnerSuppression** — stage 6; pattern-matches
  rapidly-recurring same-row content.
- **SpinnerThreshold / SpinnerWindowMs** — parameters
  governing stage 6.
- **StreamChunk** — semantic category for streaming
  output events.
- **StreamPathway** — default DisplayPathway. Module:
  `src/Terminal.Core/StreamPathway.fs`.
- **StreamProfile** — pass-through profile that routes
  StreamChunk to NvdaChannel + FileLoggerChannel.
- **Suffix** — `EditDelta` case carrying the new content
  beyond the longest-common-prefix.
- **SuffixDiff** — stage 8; per-row LCP-based diff.
- **TerminalAutomationPeer** — UIA peer providing
  Document + Text patterns. Module:
  `src/Terminal.Accessibility/TerminalAutomationPeer.fs`.
- **TerminalView** — WPF custom-control rendering +
  input + UIA peer creation. Module:
  `src/Views/TerminalView.cs`.
- **Tick** — DisplayPathway protocol method; called
  periodically (every 50ms) for trailing-edge flush.
- **TuiPathway** — alt-screen pathway. Module:
  `src/Terminal.Core/TuiPathway.fs`.
- **VtParser** — VT/ANSI byte-stream parser. Module:
  `src/Terminal.Parser/VtParser.fs` +
  `src/Terminal.Parser/StateMachine.fs`.
- **WarningLine** — semantic category for yellow-
  dominant rows. Routes to EarconChannel for warning-
  tone.

## Versioning + maintenance

### Snapshot model

This document is dated. Drift between doc and code is
expected; minor drift is normal, major drift triggers a
re-snapshot. The doc is NOT validated by automation
(there's no CI check that "every file:line cited still
exists"). Instead, drift is caught at audit time.

### When to re-snapshot

Triggers for a fresh snapshot:

- A new pipeline stage is inserted (e.g. SessionModel
  ships → stages renumber).
- A pathway protocol method is added or removed.
- A new ScreenNotification variant lands.
- The substrate inventory's "missing" column gains or
  loses an entry.
- An audit pass identifies inconsistencies vs. this doc.

### How to re-snapshot

1. Compare current code to the doc's current-state
   sections.
2. Update file:line citations.
3. Move "future / reserved" entries to "current state"
   when they ship.
4. Add new "future / reserved" entries when new
   substrate gaps are identified.
5. Update the snapshot date at the top.
6. Add an entry to the change-log section below.

### Cross-doc consistency rules

- **Pipeline-narrative is canonical for vocabulary**.
  When a stage / pathway / event-type is named, this doc
  is the source of truth.
- **`spec/event-and-output-framework.md` is canonical
  for spec-level commitments**. The pipeline-narrative
  describes what's IMPLEMENTED; the spec describes what's
  COMMITTED. Disagreements are bugs.
- **`USER-SETTINGS.md` is canonical for parameters**.
  Every parameter named here cross-references it.
- **`SECURITY.md` is canonical for threat model**. Anywhere
  the pipeline-narrative discusses security trade-offs
  (OSC 52 drop, AI summarisation privacy), it
  cross-references.

### Change log

| Date | Change |
|---|---|
| 2026-05-06 | Initial snapshot. 12-stage glossary, event taxonomy, substrate inventory, two end-to-end traces, seams catalogue, vocabulary glossary. References item 28 (SessionModel) as forward substrate gap. |

## Out of scope for this document

Things explicitly NOT covered here:

- **End-user configuration guides** — see future user-
  facing docs (`README.md`, `INSTALL.md`, settings
  guide).
- **Implementation details of future substrate** — the
  pipeline-narrative names what's coming; full design
  docs live elsewhere (`docs/SESSION-MODEL.md` for item
  28; future Phase 2 design docs).
- **Threat model / security analysis** — see
  `SECURITY.md`.
- **Cross-platform considerations** — see backlog item
  21.
- **Per-stage performance / benchmarks** — not addressed
  here; if a stage becomes a hotspot, profile and
  document separately.
- **Detailed test coverage map** — partially in this doc
  (each pathway names its test file); full test
  inventory is item 25 (testing inventory pass).






