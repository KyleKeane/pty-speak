# Project plan — May 2026

This document is the strategic plan that emerged from a 2026-05-03
review of post-PR-#116 state. It supersedes the "Next stage" pointer
in [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) and reshapes the
`spec/tech-plan.md` Stage 7-10 sequence around two foundational
architecture cycles that did not exist in the original walking-skeleton
plan.

**Authoring context.** The plan was developed in a Claude Code session
through iterative refinement: cleanup-only sketch → add output-handling
framework → add input-interpretation framework → audit unshipped
original-plan stages → final five-part sequencing. The document is
checked into the repo so that a fresh session (Claude or human) can
pick up the work without re-deriving the rationale. Future revisions
should land as new dated plans (`PROJECT-PLAN-2026-MM.md`) rather than
edits in place — the sequence shapes the architecture cycles below
and rewriting it would lose decision history.

> **Status as of 2026-05-03 cleanup cycle close (PR #128 merged).**
> **Part 1 is shipped.** Every Part 1 sub-item below
> (1.1 FluentAssertions removal, 1.2 CHANGELOG `[Unreleased]`
> consolidation, 1.3 Issue #107 log-filename refinement, 1.4
> `FlushPending` API, 1.5 docs freshness sweep, 1.6 plan committed
> to repo) has merged to `main`. The spec stage-numbering hygiene
> the plan flagged ("Stages 4a / 4b / 5a should be formal in the
> spec") is also done — see `spec/tech-plan.md` §4a / §4b / §5a.
> Part 1.7 (pre-architecture preview cut as a tagged save point)
> remains a maintainer-side action.
>
> Bonus context-dump work shipped during the same cycle but not in
> the original Part 1 scope: the **Stage 7 implementation sketch**
> in `docs/SESSION-HANDOFF.md` (so the next session has a
> pre-digested plan for Part 2), the **`baseline/stage-N`
> rollback rows** for all shipped post-Stage-3b stages in
> `docs/CHECKPOINTS.md`, and two new gotchas in `CONTRIBUTING.md`
> (F# 9 `string | null` at .NET-API boundaries; fixup-commit
> rhythm on open PRs that fail CI).
>
> **Next session starts at Part 2** (Stage 7). The plan body
> below is preserved verbatim for decision-history continuity —
> read it as the snapshot it was at authorship time, not as a
> live to-do list.

## Context

Streaming output reaches NVDA end-to-end as of PR #116 (the broken
`AllHashHistory` spinner gate that was suppressing every event has
been removed). The pipeline is now provably alive: the cmd.exe banner
reads on launch, typed characters trigger speech, command output
triggers speech.

**But two foundational architecture decisions remain unsolved AND
four original-plan Stages remain unshipped.** This plan sequences them
deliberately so each cycle starts from a validated foundation.

### Output decision (unsolved): announcement text is the whole screen on every emit

Stage 5's `renderRows` design. Manageable for a single one-shot
command; untenable for typing (NVDA reads the whole screen for each
keystroke); **fundamentally wrong** for any structured workload — REPL
prompts, TUI apps, Ink-rendered forms (Claude Code), selection lists
(fzf, peco). Different content paradigms need different presentation
strategies, not different verbosity tunings of one strategy.

### Input decision (unsolved): keystrokes pass straight through to the encoder

Today's input path is `OnPreviewKeyDown` → `KeyEncoding.encodeOrNull`
→ `_writeBytes` → PTY. No interpretation, no rolling buffer, no
completion, no lint, no natural-language suggestion surface. For a
sighted developer typing into bash, that's fine — bash does
completion, the screen shows it. For a blind developer who can't see
incremental visual feedback, the input pipeline is doing nothing to
help them stay oriented in their own typing. Different input paradigms
(raw shell, REPL prompt, search/filter, form fields, natural-language)
want different interpretation strategies — and a rolling buffer
per-keystroke gives us a hook to compute help / lint / suggest WITHOUT
interrupting the natural typing rhythm.

### Original-plan stages still unshipped

Per [`spec/tech-plan.md`](../spec/tech-plan.md) audit:

| Stage | Name | Status | Recommended sequencing |
|---|---|---|---|
| 7 | Run Claude Code end-to-end + env-scrub PO-5 | **Unshipped** | **BLOCK** — validation gate before frameworks |
| 8 | Interactive list detection + UIA list | **Unshipped** | **BUNDLE** into output framework (list-detection is a Selection-profile sub-stage; UIA list is a presentation-sink concern) |
| 9 | Earcons (NAudio) + colour announcement | **Unshipped** | **BUNDLE** into output framework (earcons are a presentation sink the framework drives; colour-to-earcon mapping is a profile-tunable strategy) |
| 10 | Review mode + structured quick-nav | **Unshipped** | **AFTER** — first consumer of the framework's semantic-event taxonomy; validates the API design |

Stages 0, 1, 2, 3, 3b, 4, 4.5 (informal), 5, 6, 11 are merged to
`main`; the maintainer's preview cadence has continued through
`v0.0.1-preview.43` as of plan authorship.

This plan does NOT attempt the architecture decisions. It closes out
cleanup, ships Stage 7 as the validation gate, runs the framework
cycles with Stages 8+9 folded in, and finishes with Stage 10 as the
first framework consumer. **Detailed designs of frameworks happen in
Phase 3.2 / 4.2 RFC documents, not here.**

## Part 1 — Cleanup cycle (concrete; ~1–2 days)

### 1.1 Drop unused `FluentAssertions` package (trivial)

Survey confirmed: package is referenced but **completely unused** (no
`using FluentAssertions` / `open FluentAssertions` anywhere).

- `Directory.Packages.props` — remove `<PackageVersion Include="FluentAssertions" ...>`
- `tests/Tests.Unit/Tests.Unit.fsproj` — remove `<PackageReference Include="FluentAssertions" />`
- CHANGELOG `[Unreleased]` Removed entry
- ~5-minute PR

### 1.2 CHANGELOG `[Unreleased]` consolidation

Survey flagged duplicate narrative threads from the rapid PR cycle
(#106 → #108 → #109 → #111 → #114 → #116):

- Two "Fixed" entries describe different root causes of the **same
  streaming-silence symptom** (PeriodicTimer reuse + AllHashHistory
  spinner gate). Should consolidate as **one entry**: "Streaming
  output now works end-to-end" with sub-bullets crediting both fixes.
- The `Ctrl+Shift+;` hotkey rationale appears in both Changed and
  Added sections — consolidate.
- Three "Fixed" subsections + two "Added" subsections interleave.
  **Reorder** per Keep-A-Changelog convention: Fixed → Changed →
  Added.
- Read end-to-end as v0.1.0 release notes; rewrite for narrative
  coherence.

Pure documentation work; no code touched. ~half a day. CI gate:
Markdown link check.

### 1.3 Issue #107 — Log filename refinement

Replace `pty-speak-HH-mm-ss.log` with full datetime + uniqueness
tie-breaker per the issue brief.

- `FileLoggerSink.pathsForLaunch()` — new naming scheme
- `tests/Tests.Unit/FileLoggerTests.fs` — test for the new format
- `docs/LOGGING.md` — example updated
- ~1 hour.

### 1.4 `FileLoggerSink.FlushPending` API

Already-deferred follow-up. The maintainer's diagnostic workflow
(`Ctrl+Shift+;` → paste log) currently copies a log that's missing
the most recent ~seconds of entries because the writer's bounded
channel hasn't drained yet. Add `Task FlushPending(int timeoutMs)`;
call from `runCopyLatestLog` before reading the file.

- Implementation: **TCS-based barrier** — drain task completes a
  per-flush TCS after each batch flush; FlushPending awaits a fresh
  TCS with timeout.
- Test: enqueue entries, call FlushPending, assert content is on disk.
- ~1–2 hours.

### 1.5 Documentation freshness sweep

Survey confirmed minimal drift, but a final read-through before the
architecture cycles:

- `docs/SESSION-HANDOFF.md` — "Where we left off" → post-#116 state;
  Stage 5 streaming-output functional but the verbose-readback issue
  is the next architecture cycle's domain.
- `docs/ROADMAP.md` — mark Stages 5 and 6 as functional with the
  verbose-readback caveat that's the architecture cycles' domain. Add
  a "Post-Stage-7 architecture cycles" section noting Parts 3 + 4.
- `docs/ACCESSIBILITY-INTERACTION-MODEL.md` — append a section
  noting the input + output frameworks are the next major architecture
  decisions; this doc will be revised as the new designs land.

### 1.6 Commit this plan to the repo

The plan-mode workspace is ephemeral; the maintainer's strategic
thinking deserves a permanent home. This document IS the artifact of
that step. Future revisions should land as new dated plans
(`PROJECT-PLAN-YYYY-MM.md`) so decision history accumulates rather
than being overwritten.

Also: link from `README.md`, `docs/SESSION-HANDOFF.md`, and
`docs/ROADMAP.md` so the plan is discoverable from the natural entry
points.

### 1.7 Pre-architecture preview cut (save point)

After 1.1–1.6 land in `main`, cut a tagged preview (e.g.
`v0.1.0-preview.N`) labelled in release notes as "Pre-output-architecture
save point — streaming output works end-to-end via Stage 5 generic
coalescer; the output-framework architecture cycle starts from this
commit." Rollback point if the architecture cycle hits dead-ends.

### Issues NOT in cleanup (deferred)

| Issue | Why deferred |
|---|---|
| **#115** — coalescer suffix-diff | **IS** the output-architecture decision; not cleanup |
| #117 — cross-row spinner redesign | Likely subsumed by per-profile presentation logic in the new framework |
| #112 — menu + palette | Settings UI for the framework would naturally live here; depends on architecture |
| #113 — `Ctrl+Shift+C` copy-output | Needs prompt-detection — exactly what the new framework provides |
| #110 — .NET 9 → 10 platform bump | Independent infrastructure; orthogonal; defer |
| `ITextProvider.GetSelection` (Pattern B) | Depends on framework's notion of "current line" |

### Spec hygiene (also Part 1 — small)

The audit flagged three shipped features without stage numbers in
`spec/tech-plan.md`:

- `Ctrl+Shift+D` diagnostic launcher (PR #81) — **resolved**
  per chat 2026-05-03; formalized as **Stage 4b** in
  `spec/tech-plan.md` §4b.
- `Ctrl+Shift+;` log-copy (PRs #111 / #114) — **resolved** per
  chat 2026-05-03; formalized as part of **Stage 5a** (diagnostic
  logging surface) in `spec/tech-plan.md` §5a alongside the
  `FileLogger.fs` ship.
- Alt-screen 1049 + DECTCEM coverage — **resolved** per chat
  2026-05-03; formalized as **Stage 4a** (Claude Code rendering
  substrate) in `spec/tech-plan.md` §4a. Letter-suffix naming
  (matches Stage 3a/3b precedent) avoids collision with the
  existing `### 4.5` NVDA validation sub-section of Stage 4.
  Forward-looking references in this repo previously read
  "Stage 4.5"; historical CHANGELOG entries from when the work
  shipped keep their original "Stage 4.5" labels as
  release-notes-shaped artifacts.

Per `CONTRIBUTING.md`, `spec/tech-plan.md` is immutable for planners
but maintainer-editable. Either: assign stage numbers (e.g. "Stage
4a", "Stage 6.1", etc.) and document in spec, OR add an "Out-of-stage
shipped features" section to `docs/ROADMAP.md`. Maintainer's call
which surface owns this.

## Part 2 — Stage 7: Claude Code roundtrip + env-scrub PO-5 (validation gate; ~3–5 days)

### Why Stage 7 belongs here, before frameworks

Stage 7 ships the maintainer's primary target workload — Claude Code
running inside pty-speak, end-to-end. Without that validation, the
framework designs would optimise for theoretical paradigms (Stream,
REPL, TUI, Form, Selection) without ground-truth signal from the
workload they're supposed to serve best.

Concretely: Claude Code is **Ink-rendered**, uses **alt-screen +
cursor visibility tricks**, **renders Markdown** with code blocks +
lists + headings, presents **interactive prompts** (multi-choice,
text input, file selection), produces **structured streaming output**
(assistant turns + tool calls + tool results). Each of those
characteristics maps to a framework-design decision. Watching Claude
Code break (or not break) inside the current Stage 5 generic coalescer
tells us what the framework MUST handle correctly — far better than
enumerating paradigms in the abstract.

### Scope per `spec/tech-plan.md` §7

1. **ConPTY-spawn Claude Code** as the child shell instead of cmd.exe
   (configurable, with cmd.exe staying default for now).
2. **Validate the substrate** — does the parser handle Claude Code's
   full ANSI surface? Does the screen model render Ink output
   correctly? Does alt-screen-on-launch work?
3. **Env-scrub PO-5** — strip sensitive env-vars (`*_TOKEN`,
   `*_SECRET`, `*_KEY`, `*_PASSWORD`, etc.) before child spawn. Log
   what's stripped (count, not values). Documented in `SECURITY.md`
   as a posture commitment.
4. **NVDA-validate** the end-to-end flow — start Claude Code, ask a
   question, hear the streaming response, navigate the response with
   the review cursor, accept a tool-use confirmation, hear the result.
5. **Document gaps** — anything the user reports as broken / awkward
   / verbose / silent goes into a "Stage 7 issues" inventory that
   becomes input to Part 3 + Part 4 framework design.

### Out of scope for Stage 7

- **Fixing the gaps** identified during Stage 7 NVDA validation — those
  become framework requirements, not Stage 7 hotfixes. Stage 7's job
  is to surface them, not solve them.
- **Configurable shell** beyond the Claude-Code-as-default toggle —
  Phase 2 user-settings work.
- **Distributing pty-speak with Claude Code bundled** — packaging
  concern; Stage 7 expects Claude Code already installed on the user's
  `PATH`.

### Verification

- `Ctrl+Shift+D` (diagnostic launcher) shows the expected PID
  lifecycle.
- `Get-ChildItem env:` inside the spawned Claude Code session does
  NOT show `GITHUB_TOKEN`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`,
  `AWS_*`, etc.
- A maintainer-led NVDA validation cycle on the post-Stage-7 preview
  produces a signed-off "Stage 7 substrate validated" + a Stage-7-issues
  inventory.

## Part 3 — Output-handling framework architecture cycle (kickoff brief)

**Detailed planning happens after Part 1 + Part 2 land.** This section
is the kickoff brief — what the cycle aims for and what its research
phase produces. Stage 7's gaps inventory feeds directly into Part 3's
profile design.

### Why profile-based

Different content paradigms need different presentation strategies:

| Paradigm | Examples | Presentation strategy |
|---|---|---|
| **Stream** | bash echo, command output, dmesg, build logs | Suffix-diff append-only; new content reads as it appears |
| **REPL** | Python, Node, sqlite3, ipython | Prompt-aware; current line is input; output blocks are command results |
| **TUI** | less, vim, htop, nano, tmux | Full-screen redraw; review-cursor primary; minimal proactive announcements |
| **Form** | Claude Code (Ink), gum form, fzf multi-select | Structured fields with focus model; arrow-key field navigation; per-field announce |
| **Selection** | fzf, peco, gum choose | List enumeration; selected item announces; minimal noise on others |

The current Stage 5 coalescer is **one strategy** ("emit the whole
rendered screen") trying to serve all paradigms — and failing. The
framework decision is: what's the right abstraction for plugging in
multiple strategies, detecting which one applies, and letting the
user override?

### Cycle phases

**3.1 Research phase** — read, don't code. ~1 day.

Survey prior art and write a maintainer-readable doc enumerating
relevant patterns. Sources to study:

- **NVDA itself** — its review-cursor / browse-mode /
  speak-typed-characters model; current terminal-app heuristics
- **BRLTTY** — Linux braille daemon; terminal screen-scraping;
  speech-buffer-style modes
- **emacspeak** (TV Raman) — speech-first audio desktop; `eshell`
  speech-first shell; auditory icons; how Raman thinks about
  speech-first design
- **JAWS** terminal patterns (limited public docs; what's available)
- **edbrowse** — line-mode editor / browser; primitive but
  speech-friendly text navigation
- **Modern terminal protocols** — OSC 133 (FinalTerm prompt markers;
  VS Code / Wezterm / iTerm), OSC 1337 (iTerm extensions), kitty's
  command-output framing, kitty graphics, Sixel
- **Claude Code's Ink rendering** — how alt-screen redraws map to
  logical structure; what signals (if any) it emits we can hook
- **IPython / Jupyter** — REPL accessibility; structured output
  protocols (`text/plain`, `text/html`, `application/vnd.jupyter.*`)

**Output**: `docs/research/OUTPUT-FRAMEWORK-PRIOR-ART.md`. Annotated
summaries; quotes; link references.

**3.2 Design phase — RFC**. ~2 days.

After research, produce a maintainer-reviewable RFC covering:

- **Profile taxonomy** — initial proposal: Stream / REPL / TUI / Form
  / Selection. Maintainer validates / extends.
- **Profile detection** — OSC 133 prompt markers (gold standard;
  opt-in by shell config); alt-screen mode bit (`?1049h`; strong TUI
  signal); application-name lookup against ConPTY child PID; per-app
  config; manual user toggle (`Ctrl+Alt+1..5` style).
- **Profile interface** — `IOutputProfile` with `OnPtyBytes` /
  `OnRowsChanged` / `OnModeChanged` / `OnUserInput` /
  `Render(snapshot) → AnnouncementBatch` methods. Each profile owns
  its own debounce / dedup / verbosity tuning.
- **State machine** — when does the system switch profiles? Detection
  events, manual override, app-launch transitions.
- **User-customisation surface** — Phase 2 user-settings TOML; per-app
  overrides; per-profile parameters; manual switch hotkeys.
- **Plugin extensibility** — can a third party ship a new profile (for
  `htop`, `psql`, etc.) without modifying core? Probably yes via
  assembly-load convention.
- **Migration plan** — existing `Coalescer.fs` becomes the initial
  Stream profile; nothing else in the pipeline (parser, screen, drain,
  peer) changes. The framework slots between coalescer-style emit
  decisions and the drain.

**Output**: `docs/RFC-OUTPUT-FRAMEWORK.md`. Maintainer review →
iterate.

**3.3 Implementation phase**. Multi-week, staged.

Each stage = its own multi-PR mini-cycle with NVDA validation gate.

- **Stage A** — framework scaffolding + default Stream profile
  (replicates today's behaviour)
- **Stage B** — Stream refinements: suffix-diff, OSC 133 prompt
  awareness, typed-input echo dedup. **Closes #115.**
- **Stage C** — REPL profile (Python, Node, sqlite3)
- **Stage D** — TUI profile (less, vim, htop) — alt-screen-triggered;
  review-cursor primary
- **Stage E** — Form profile (Claude Code Ink) — **addresses Stage 7
  issues inventory** for the Form-paradigm category. Field detection,
  focus model.
- **Stage F** — Selection profile (fzf, peco, gum-choose) — list
  enumeration. **Subsumes original Stage 8** (interactive list
  detection + UIA list — list-detection becomes a profile-detection
  signal; UIA list exposure becomes a presentation-sink contract).
- **Stage G** — Earcons + colour-to-earcon mapping (NAudio
  integration). **Subsumes original Stage 9.** Earcons live as a
  presentation sink the framework drives; colour-to-earcon mapping is
  profile-tunable. Gates the `Ctrl+Shift+M` mute hotkey reservation.
- **Stage H** — User-settings UI (depends on #112 menu/palette
  landing) — exposes per-profile parameters, per-app overrides,
  manual switches.

## Part 4 — Input-interpretation framework architecture cycle (kickoff brief)

**Detailed planning happens after Part 1 + Part 2 land AND Part 3's
research+RFC complete.** The two frameworks share infrastructure
(profile detection, app-name lookup, per-app config, settings UI), so
Part 4 benefits from inheriting Part 3's design vocabulary. This
section is the kickoff brief — what the cycle aims for and how it
diverges from output.

### Why an input framework

Today every keystroke goes straight from `OnPreviewKeyDown` to the
encoder to the PTY. Bash does its own completion, the visual cursor
moves, sighted users see suggestions appear. A blind user gets none
of that — they have to type the command exactly right, hear cmd.exe
echo each char, and discover errors only after pressing Enter.

The maintainer's framing: "since we have on every keystroke a dynamic
update of the user's typing, we can do computation to help flag typos
or make suggestions" — without forcing the user to learn extra
commands, without overwhelming the speech queue, and without
interrupting their natural typing rhythm.

### Input paradigms (parallel to output paradigms)

| Paradigm | Examples | Interpretation strategy |
|---|---|---|
| **Raw passthrough** | bare bash typing without help requested | Encode and forward; no buffer-side computation |
| **Shell-aware** | bash / cmd / PowerShell with command discovery | Tokenise the rolling buffer; lex against known command grammar; on-demand suggestion of completions, argument flags, typo fixes |
| **Search-input** | fzf, peco, ripgrep filter, Vim `/` search | Live-filter feedback; suggest matches on Tab; current-match-count announces silently or on-request |
| **Form-input** | Claude Code (Ink) text fields, gum forms | Track which field has focus; field-specific validation (URL, number, etc.); suggest based on field semantics |
| **Natural-language** | optional opt-in mode | User types "list all hidden files in this folder"; LLM (configurable backend) returns candidate command(s); NVDA reads the suggestion; Tab to accept / Enter to send literal request to PTY |

### Rolling-buffer model

State held by an `InputBuffer` component:

- **Buffer text** — the line the user is composing, independent of
  PTY echo
- **Cursor position** — within the buffer, for token-boundary
  detection and per-token speak-on-demand
- **Buffer reset triggers** — Enter (send to PTY), Esc (clear),
  Ctrl+C (interrupt), prompt redraw
- **Update events** — every keystroke emits
  `BufferChanged(buffer, cursor, lastInsertedChar)`

The buffer is fed through an interpreter pipeline:

1. **Tokeniser** — splits buffer into `(tokenType, text, range)`
   tuples (command / arg / flag / quoted-string)
2. **Lexer per known command** — git, docker, npm, kubectl, etc.
   Schema-driven (see `clap`-style or `complete` shell completion
   data)
3. **Suggestion engine** — fuzzy-match typos; complete partial
   commands; complete partial arguments
4. **NL engine** (optional, opt-in) — sends buffer to a local or
   remote LLM; returns candidate commands with explanations
5. **Annotation collector** — gathers `Hint`, `Warning`, `Suggestion`
   annotations; presented to user on demand

### Surfacing without overwhelming

Critical UX constraint for a blind-user audience: **don't spam the
speech queue**. Interpretation results sit waiting for an explicit
user gesture:

- **Ctrl+Space** — read next suggestion (cycles through ranked
  candidates)
- **Tab** — accept current top suggestion (replaces buffer text)
- **Ctrl+H** — read help / argument doc for current token
- **Ctrl+L** *(reserved)* — read lint warnings for current buffer
- **Enter without acceptance** — buffer sent to PTY as-is; suggestions
  discarded

Earcons (Part 3 Stage G territory) signal "suggestion available" or
"lint warning present" so the user knows there's something to query,
but speech only fires on explicit request.

### Echo correlation (closes the loop with output framework)

When the user types `echo hello`, cmd.exe echoes those chars back
through the PTY → output framework receives them → wants to announce
them. But NVDA's speak-typed-characters already spoke each char as it
was typed. Result: double-speech.

The input framework's rolling buffer provides the missing piece: when
the output framework's Stream profile sees incoming bytes, it can
correlate against the input buffer's recently-emitted bytes and
**suppress the echo** (it's already been spoken by NVDA on the input
side). Cleanly resolves Issue #115's "Echo deduplication"
sub-question.

### Cycle phases (parallel to Part 3's structure)

**4.1 Research phase** — read, don't code. ~1 day, partly overlapping
with Part 3.1.

Survey prior art for input-interpretation patterns:

- **Fish shell** — autosuggestions, syntax highlighting, history-driven
  completion. Speech-friendly?
- **PSReadLine** — PowerShell's interactive editing layer; predictive
  intellisense
- **`carapace` / `clap`-completions / `complgen`** — declarative shell
  completion grammars; reusable schemas
- **GitHub Copilot CLI** (`gh copilot suggest`) — natural-language →
  shell-command translation; Anthropic Claude could do the same with
  arbitrary shells
- **Warp Terminal** — block-based input; structured editing;
  arguments-as-fields
- **emacspeak's `eshell`** — speech-first command-line interaction;
  how Raman handles input UX
- **PTY-side completion protocols** — readline's tab-completion;
  bash-completion; zsh's completion system; fish's autosuggest

**Output**: `docs/research/INPUT-INTERPRETATION-PRIOR-ART.md`.
Annotated summaries; quotes; link references. Likely shares some
sources with Part 3.1.

**4.2 Design phase — RFC**. ~2 days. Run after Part 3's RFC iteration
completes (so the input RFC inherits the profile-detection /
app-name-lookup / settings-UI vocabulary).

Cover:

- **Input profile taxonomy** — Raw passthrough / Shell-aware /
  Search-input / Form-input / NL
- **Profile detection** — reuse Part 3's signals (alt-screen, app
  name, OSC); plus new signals (current PTY state, cursor position)
- **`IInputInterpreter` interface** — `OnBufferChanged` /
  `OnCursorMoved` / `OnSubmit` / `Suggestions(state) →
  AnnotationBatch` methods
- **Echo correlation API** — how Stream-output profile queries the
  input framework's "recently-emitted bytes" to suppress double-speech
- **Speech-queue management** — how to ensure on-demand reads
  (Ctrl+Space, Tab, Ctrl+H) don't compete with streaming-output
  announcements
- **Plugin extensibility** — third party adds a `kubectl` interpreter
  or a `psql` interpreter without modifying core
- **NL backend abstraction** — opt-in; pluggable provider (local
  llama, Anthropic, OpenAI, etc.); user must explicitly enable; never
  sends data without explicit acknowledgement (security/privacy
  concern)

**Output**: `docs/RFC-INPUT-FRAMEWORK.md`. Maintainer review →
iterate.

**4.3 Implementation phase**. Multi-week, staged.

- **Stage I-A** — `InputBuffer` rolling-state primitive +
  `BufferChanged` event (replaces direct `OnPreviewKeyDown` →
  encoder; encoder still consumes from buffer-on-Enter for now)
- **Stage I-B** — Echo correlation API; output framework's Stream
  profile suppresses correlated echoes (closes the typed-char-echo
  half of #115)
- **Stage I-C** — Tokeniser + lexer scaffolding; first lexer for one
  shell (cmd.exe or PowerShell)
- **Stage I-D** — Suggestion engine; on-demand surfacing via
  `Ctrl+Space` / `Tab`; first command grammars (git, docker, basic
  Unix tools)
- **Stage I-E** — Argument-doc surfacing via `Ctrl+H`
- **Stage I-F** — Lint annotations + earcon signals (depends on Part
  3 Stage G earcons)
- **Stage I-G** — NL interpreter mode (opt-in); pluggable backend
- **Stage I-H** — Form-input profile (Ink integration; depends on
  Part 3 Stage E)

Each stage = its own multi-PR mini-cycle with NVDA validation.

### Out of scope for input framework cycle

- **Customisable / user-defined keybindings** — Phase 2 user-settings
  work; the framework offers gestures (`Ctrl+Space`, `Tab`, `Ctrl+H`)
  but lets users remap.
- **Voice input** (speech-to-text → buffer) — orthogonal; could be a
  future Stage I-Z.
- **Replacing the bash / PSReadLine completion** — pty-speak's
  interpreter ENHANCES on top; the shell still does its own.

## Part 5 — Stage 10: Review mode + structured quick-nav (~1 week)

Per `spec/tech-plan.md` §10. **Ships AFTER both frameworks land**
because Stage 10 is the first non-built-in consumer of the new
semantic-event taxonomy — its job is to validate that the framework's
API design is general enough.

### Why this sequencing

Stage 10 needs the output framework's notion of "what kind of content
is this segment of the screen?" (error vs warning vs prompt vs
command vs output-block vs link vs heading) to power the quick-nav
letters (`e`/`w`/`p`/`c`/`o`/`l`/`h` etc. per `spec/overview.md`).
And it needs the input framework's mode-switching primitives to
handle the `Alt+Shift+R` review-mode-toggle gesture cleanly (the
gesture must NOT pass through to the PTY when review mode is active).

If Stage 10 ships before frameworks, we'd hand-roll semantic
detection inside Stage 10 code, then have to refactor it out when
the frameworks land. Worse: hand-rolled semantic detection sets a
precedent the frameworks have to either retrofit or break.

### Scope

- **Review-mode toggle** — `Alt+Shift+R` (the reservation already in
  `AppReservedHotkeys`); enters a mode where keystrokes navigate the
  screen rather than reach the PTY.
- **Quick-nav letters** — `e/E` next/prev error, `w/W` warning, `p/P`
  prompt, `c/C` command, `o/O` output block, `l/L` link, `d/D` diff
  marker, `h/H` heading, `i/I` interactive element, `g/G` top/bottom,
  `j/k` line. Each letter queries the output framework's
  semantic-event store for matching segments.
- **Mode announcement** — entering review mode announces "Review
  mode" via NVDA; exiting announces "Interactive mode."
- **Esc returns to interactive** — standard NVDA browse/focus toggle
  parallel.

### Verification

- Each quick-nav letter jumps the review cursor to the next semantic
  match.
- Mode-toggle hotkey is announced.
- Mode-state interacts cleanly with framework-driven streaming
  announcements (e.g. new errors arriving in interactive mode are
  still announced; review mode pauses proactive announcements until
  user exits).
- NVDA validation cycle on the post-Stage-10 preview confirms
  parallel-to-NVDA-browse-mode UX.

## Critical files (Part 1 only)

| File | What changes |
|---|---|
| `Directory.Packages.props` | Remove FluentAssertions PackageVersion |
| `tests/Tests.Unit/Tests.Unit.fsproj` | Remove FluentAssertions PackageReference |
| `src/Terminal.Core/FileLogger.fs` | New `FlushPending` member; Issue #107 filename scheme |
| `src/Terminal.App/Program.fs` | Call `FlushPending` before file read in `runCopyLatestLog` |
| `tests/Tests.Unit/FileLoggerTests.fs` | Tests for new filename + Flush |
| `docs/LOGGING.md` | New filename format example |
| `docs/SESSION-HANDOFF.md` | "Where we left off" → post-#116 state + plan-doc reference |
| `docs/ROADMAP.md` | Stages 5/6 marked functional + caveats; post-Stage-7 architecture cycles section |
| `docs/ACCESSIBILITY-INTERACTION-MODEL.md` | Note about pending input/output framework decisions |
| `README.md` | Plan-doc link; updated shipped-stages list |
| `CHANGELOG.md` | `[Unreleased]` block consolidated for v0.1.0 release-notes coherence |

## Existing primitives to reuse

- `FileLoggerSink` (`src/Terminal.Core/FileLogger.fs:120`) —
  async-channel-based logger; the Flush API extends this without
  restructuring.
- `Logger.get` (`src/Terminal.Core/FileLogger.fs:446`) — categorised
  logger accessor; cleanup work uses existing accessors.
- `BoundedChannel<T>` pattern across the codebase — Flush
  implementation uses the same primitives the existing drain task
  does.
- xUnit `[<Fact>]` with backtick-named tests (per `FileLoggerTests.fs`
  style) — new tests follow the established pattern.

## Verification (Part 1)

- All cleanup PRs CI green on Build & test (windows-latest),
  actionlint, Markdown link check.
- Existing tests still pass. New tests for #107 + Flush API.
- **Manual NVDA verification on the post-cleanup preview:**
  - cmd.exe banner reads on launch (sanity check that the cleanup
    PRs didn't regress #116's fix)
  - `Ctrl+Shift+L` opens logs folder; new filename format visible
  - `Ctrl+Shift+;` copies the active log; copied content reflects
    up-to-the-moment state (Flush works)
  - Streaming output still functional (verbose readback expected;
    that's the architecture cycle's job to fix)
- A tagged preview (`v0.1.0-preview.N`) cut as the pre-architecture
  save point.

## Out of scope for THIS plan

- **Detailed Stage 7 design** (Part 2). Separate plan, written when
  Part 1 completes.
- **Detailed RFCs** for output framework (Part 3.2) and input
  framework (Part 4.2). Each is its own document, written after the
  corresponding research phase.
- **Per-stage implementation plans** for Parts 3.3, 4.3, and 5. Each
  stage is its own multi-PR mini-cycle planned in detail when the
  prior stage validates.
- **New features / bugs surfaced during cleanup or Stage 7 validation**
  — file as issues; defer to the framework cycles or Phase 2
  user-settings.
- **The Part 3.1 + 4.1 research phases** are not blocked — they can
  run in parallel with Part 1's mechanical cleanup or Part 2's Stage
  7 work if maintainer wants — but they're listed as Part 3 / 4
  deliverables for clarity.

## Strategic continuity

The full sequence:

1. **Part 1 — Cleanup** (~1–2 days). Trivial-to-small PRs; CHANGELOG
   narrative consolidation; spec hygiene; pre-architecture preview
   save-point cut.
2. **Part 2 — Stage 7: Claude Code roundtrip + env-scrub** (~3–5
   days). The validation gate: a real Ink-rendered workload running
   end-to-end inside pty-speak. Outputs a "Stage 7 issues" inventory
   that becomes design input for Parts 3 + 4.
3. **Part 3 — Output framework cycle** (multi-week). Research → RFC
   → staged implementation. **Subsumes original Stage 8 (Selection
   profile = list detection + UIA list) and Stage 9 (earcons + colour
   announcement = presentation-sink stage).** Closes Issues #115 +
   #117.
4. **Part 4 — Input framework cycle** (multi-week, partly overlaps
   with Part 3). Research → RFC → staged implementation. Echo-correlation
   API explicitly bridges to Part 3.
5. **Part 5 — Stage 10: Review mode + quick-nav** (~1 week). First
   non-built-in consumer of the new semantic-event taxonomy;
   validates the framework API design.

Within Parts 3 and 4:

- **3.1 + 4.1 research phases** can run partly in parallel (some
  sources overlap).
- **3.2 RFC** comes first — establishes profile-detection,
  app-name-lookup, and settings-UI vocabulary that Part 4 inherits.
- **4.2 RFC** runs after 3.2 settles — reuses the design vocabulary;
  defines echo correlation API as the explicit input ↔ output bridge.
- **Implementation** phases interleave by stage. Part 3 Stage A
  (scaffolding + Stream profile) lands first because it's the
  foundation everything else depends on. Part 4 Stage I-A
  (`InputBuffer` rolling state) follows. Then per-profile work
  alternates as each profile's full input + output story coheres
  (e.g. the Form profile's output side from Part 3 Stage E and input
  side from Part 4 Stage I-H land together).

Each cycle is multi-week. NVDA validation gates every stage.

The maintainer's flags shape the whole sequence:

- **"Don't shortcut the architecture decision"** → Cleanup first;
  Stage 7 validation gate first; research-before-design-before-code;
  staged implementation with NVDA validation per profile.
- **"Both input and output need extensible frameworks"** → Two
  parallel cycles, deliberately scoped, sharing infrastructure.
- **"We want a clean foundation aligned with the new larger scope"**
  → Stage 8 + 9 fold into the output framework as profile / sink
  contributions; Stage 10 ships AFTER frameworks as their first
  consumer; nothing original-plan gets orphaned or duplicated.

The plan deliberately scopes Parts 3, 4, 5 as **outline-only**.
Detailed design lives in the RFC documents that emerge from each
cycle's research phase. This plan file commits to the *sequence* and
the *frameworks' shapes*; per-cycle plans get written after each
phase produces its inputs.

Total elapsed time, rough estimate: **Part 1 ~2 days + Part 2 ~5 days
+ Part 3 ~3-5 weeks + Part 4 ~3-5 weeks (partly overlapping Part 3) +
Part 5 ~1 week ≈ 8–12 weeks of focused work** to reach v0.1.0 with
both frameworks shipped, Stage 10 review-mode landed, Claude Code
roundtrip validated, all original-plan stages either shipped or
formally subsumed.
