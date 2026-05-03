# Stage 7 issues inventory

This file is the **explicit design input for Parts 3 + 4 of**
[`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) — the
Output-handling and Input-interpretation framework cycles. Stage 7's
job (per
[`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) "Stage 7
implementation sketch (next)") is to ship a Claude Code roundtrip
end-to-end and **surface every gap that NVDA validation reveals** —
not solve them. Each gap below becomes a framework requirement that
the corresponding cycle's RFC must address.

> **Status: Stage 7 NVDA validation green (2026-05-03).** The full
> 11-PR Stage 7 sequence has shipped:
>
> - **PR #131 (PR-A)** — env-scrub PO-5 (closes
>   [`SECURITY.md`](../SECURITY.md) row PO-5).
> - **PR #132 (PR-B)** — extensible shell registry +
>   `PTYSPEAK_SHELL` startup override.
> - **PR #134 (PR-C)** — hot-switch hotkeys
>   `Ctrl+Shift+1` / `Ctrl+Shift+2`.
> - **PR #135 (PR-D)** — NVDA validation matrix expansion in
>   [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
>   "Stage 7 — Claude Code roundtrip" + this inventory's first
>   pre-seeded entries.
> - **PR-E** — `Ctrl+Shift+G` runtime debug-log toggle.
> - **PR-F** — `Ctrl+Shift+H` health check + `Ctrl+Shift+B`
>   incident marker + 5s background heartbeat.
> - **PR-G** — `Ctrl+Shift+;` dispatcher-deadlock fix
>   (`FlushPending(500).Result` sync-over-async).
> - **PR-H** — 500-char streaming-announce cap stopgap (tracked by
>   Issue #139 for Stream-profile-driven removal).
> - **PR #141 (PR-I)** — silent reader-loop shutdown on shell-switch
>   (`ChannelClosedException` was being mis-classified as a parser
>   error) + `currentShell` mutable so heartbeat / health-check
>   report the post-switch identity.
> - **PR #142 (PR-J)** — PowerShell as third built-in shell;
>   hotkeys reordered to `+1`=cmd / `+2`=PowerShell / `+3`=Claude
>   so the diagnostic control shell sits next to cmd; Ctrl+Shift+H
>   liveness probe (`Process.GetProcessById` alive/dead flag);
>   Ctrl+Shift+D inline shell-process snapshot announce.
> - **PR #143 (PR-K)** — env-scrub allow-list expanded with the
>   Windows runtime baseline (`SystemRoot`, `WINDIR`, `TEMP`,
>   `ProgramFiles`, `PSModulePath`, etc.) after the 2026-05-03 NVDA
>   pass surfaced PowerShell + claude.exe both dying on spawn
>   because the original 7-name allow-list stripped vars they
>   needed to initialise; misleading "stripped 0" log line replaced
>   with `"kept K of M parent vars; dropped D as sensitive"`.
> - **PR-L** (Stage 7 wrap-up) — doc-purpose audit; this status
>   block.
>
> **Maintainer's empirical confirmation:** post-PR-K NVDA pass
> (2026-05-03) confirmed all three shells spawn, stay alive, and
> announce correctly under hot-switch; `Ctrl+Shift+H` reports
> `alive` for the running shell.
>
> **Don't fix anything from the inventory in Stage 7; that's
> framework-cycle work.** The Output framework cycle (Part 3)
> consumes this inventory as design input for its research phase
> (`docs/research/OUTPUT-FRAMEWORK-PRIOR-ART.md`).

## How to use this file

1. **Run the Stage 7 NVDA validation flow** per
   `docs/SESSION-HANDOFF.md` "Stage 7 implementation sketch (next)"
   §4 (launch pty-speak with Claude Code as the spawned shell, type
   prompts, navigate responses, accept tool-use prompts, read
   results).
2. **For every gap surfaced** — every "broken" / "awkward" /
   "verbose" / "silent" / "this should be a list but reads as text"
   moment — add an entry to the appropriate section below with:
   - **One-line summary** in the section heading.
   - **What broke** — the literal user-observable behaviour
     (concrete enough that a reader can reproduce it).
   - **Why it matters** — what the user couldn't do, or what the
     speech queue did that was wrong.
   - **Reproduction steps** — exact prompt, exact key sequence,
     exact NVDA configuration if relevant.
   - **Hypothesised root cause** if known. Don't speculate
     architecturally yet; just describe what part of the pipeline
     looks suspect (parser? coalescer? UIA peer? input encoder?).
3. **Don't fix anything inline.** Each entry is a framework
   requirement; the framework cycles produce the fix.
4. **Update the framework cycle reading their input.** Once the
   inventory has enough entries to inform the Output framework
   cycle's research phase, link from there back to specific entries
   here so the design rationale traces back to the validation
   evidence.

## Category tags

Each entry is tagged with the framework taxonomy from the May-2026
plan so the relevant cycle can filter cleanly:

- `[output-stream]` — Stream profile concerns (echo dedup, suffix-diff,
  OSC 133 prompt awareness).
- `[output-form]` — Form profile concerns (Claude Code Ink fields,
  field detection, focus model).
- `[output-selection]` — Selection profile concerns (list detection,
  arrow-key navigation, item enumeration; subsumes original Stage 8).
- `[output-earcon]` — Earcon presentation-sink concerns (colour-to-earcon
  mapping; subsumes original Stage 9).
- `[output-tui]` — TUI profile concerns (alt-screen apps that aren't
  forms or selections — `less`, `vim`, `htop`).
- `[output-repl]` — REPL profile concerns (Python, Node, sqlite3 —
  prompt-aware; current line is input).
- `[input-suggest]` — Input-interpretation concerns (suggestion engine,
  command grammar lookup, fuzzy typo correction).
- `[input-buffer]` — Rolling-buffer concerns (echo correlation,
  on-demand surfacing via `Ctrl+Space` / `Tab` / `Ctrl+H`).
- `[input-form]` — Form-input concerns (field-aware input for
  Ink-rendered prompts).
- `[input-nl]` — Natural-language interpreter concerns (opt-in LLM
  backend for "list all hidden files" → command suggestion).
- `[review-mode]` — Stage 10 / Part 5 concerns (quick-nav letters,
  semantic-event taxonomy).
- `[other]` — Doesn't fit cleanly into the framework taxonomy; flag
  for triage.

A single entry can carry multiple tags if it crosses framework
boundaries (e.g. typed-input echo dedup is both `[output-stream]`
and `[input-buffer]` because the bridge is the echo-correlation
API).

## Entries

The four entries below are **design-derived** from
[`spec/tech-plan.md`](../spec/tech-plan.md) §7.4 "Known issues
you'll hit (drives next stages)" + the headline architectural
finding logged in
[`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) Stage 7 sketch
"Known risks". They predict what NVDA validation will surface
based on the architectural shape of the shipped substrate. The
maintainer's empirical NVDA pass either (a) confirms each
prediction with concrete reproduction steps + version pins, or
(b) refines the prediction (or marks it as not-reproducible if
the substrate behaves better than predicted), or (c) adds
additional empirically-surfaced entries below.

Each entry below is marked **Source: design prediction** at the
bottom; entries the maintainer adds during NVDA validation will
be marked **Source: NVDA pass YYYY-MM-DD** instead. The
distinction matters for the framework cycles' research phase:
predictions that don't reproduce empirically may indicate the
substrate is more capable than the spec assumed, and the
framework design adjusts accordingly.

### [other] Spurious "parser/reader loop" error announce on shell hot-switch

**What broke.** Pressing `Ctrl+Shift+1` (cmd) or `Ctrl+Shift+2`
(claude) reliably produced this NVDA announcement sequence:

1. "Switching to Claude Code." (or "...Command Prompt.")
2. ~700ms pause
3. **"Terminal parser error: Parser/reader loop: The channel
   has been closed."** ← spurious; sounds like a fatal error
4. "Switched to Claude Code." (or "...Command Prompt.")

The switch itself succeeded (the new ConPtyHost spawned and
took over input/output), but the spurious error message in
between made the switch sound broken to a screen-reader user.
Combined with claude's quiet welcome screen and the persisting
cmd screen contents (the documented `[output-tui]` no-screen-
reset limitation), the user perceived the switch as "didn't
work."

**Why it matters.** The shell-switch UX (`Ctrl+Shift+1` /
`Ctrl+Shift+2` per Stage 7 PR-C) is unusable when every press
generates a fake error announcement.

**Reproduction.** Launch pty-speak; let it settle in cmd. Press
`Ctrl+Shift+2`. NVDA announces all four lines including the
parser-error one. Empirically confirmed in the 2026-05-03
NVDA pass; full log slice available with the inventory entry.

**Hypothesised root cause.** Confirmed: when `switchToShell`
disposes the old `ConPtyHost`, the host's internal stdout
channel completes (`chan.Writer.TryComplete()` runs in
`Dispose`). The reader loop's `host.Stdout.ReadAsync(ct)` then
throws `ChannelClosedException` — which is NOT
`OperationCanceledException` — so the catch-all `| ex ->` arm
in `startReaderLoop` mis-classifies it as a real parser
fault and writes a `ParserError` to the SHARED notification
channel. The drain task announces it via `ActivityIds.error`
in the gap between "Switching..." and "Switched..." cues.
PR-I fixes by adding silent-shutdown arms for
`ChannelClosedException`, `IOException`, and
`ObjectDisposedException` to the reader-loop catch chain
(all three indicate intentional pipe/channel teardown).

**Inventory date.** 2026-05-03; pty-speak post-PR-#140
(Velopack preview cut between Stage 7 PR-D and PR-I);
NVDA version unrecorded. **Source: NVDA pass 2026-05-03;
fixed in PR-I.**

### [other] Heartbeat + health-check report stale shell name after hot-switch

**What broke.** After a successful `Ctrl+Shift+1` or
`Ctrl+Shift+2` hot-switch, the 5-second heartbeat log line +
the `Ctrl+Shift+H` health-check announcement continued to
report `Shell=Command Prompt` even when the running shell
had switched to claude.exe (and vice versa). Cosmetic — the
PID was correct (heartbeat showed `Pid=2596` after the
switch to claude.exe), but the shell-name field was wrong.

**Why it matters.** Mostly affects post-mortem log analysis:
the 2026-05-03 NVDA pass log alone, without the explicit
`Shell-switch: spawning Claude Code` event line, would not
have made it obvious that claude was the active shell during
heartbeats — making the wrong shell name a source of
confusion when triaging future issues. For the user, the
health-check announcement is also wrong-but-not-dangerously-so.

**Reproduction.** Same as above: launch, press `Ctrl+Shift+2`,
press `Ctrl+Shift+H`. Verdict text says "Command Prompt
shell" instead of "Claude Code shell".

**Hypothesised root cause.** Confirmed: `chosenShell` is
captured at startup and never updated. PR-I adds a
`mutable currentShell` field that `switchToShell` sets
after a successful spawn; heartbeat and health-check both
read from `currentShell` instead of `chosenShell`.

**Inventory date.** 2026-05-03; same release as the entry
above. **Source: NVDA pass 2026-05-03; fixed in PR-I.**

### [other] Ctrl+Shift+; dispatcher deadlock from FlushPending(500).Result

**What broke.** Pressing `Ctrl+Shift+;` (copy log to clipboard)
permanently wedges the WPF dispatcher. After the press: typing
into pty-speak does nothing, Alt+F4 doesn't close the window,
clicking the X button doesn't close it, no other app-reserved
hotkey produces an NVDA announcement (Ctrl+Shift+H, Ctrl+Shift+G,
etc. are all silent). The 5-second background heartbeat keeps
firing so the runtime is alive — only the WPF dispatcher is
seized up. Force-kill via Task Manager is the only way out.

**Why it matters.** The user can no longer interact with
pty-speak after pressing the very hotkey that exists to capture
diagnostic logs. The diagnostic-capture workflow is broken at
its terminal step.

**Reproduction.** Launch pty-speak. Run any cmd command that
produces enough output to keep NVDA reading for >30 seconds
(e.g. several `dir /s` invocations or a single `set`). While
NVDA is mid-readout, press `Ctrl+Shift+;` to copy the log.
Window wedges; never recovers.

**Hypothesised root cause.** Confirmed: `runCopyLatestLog`
called `sink.FlushPending(500).Result` synchronously on the
dispatcher. `FlushPending` is implemented as
`task { let! winner = Task.WhenAny(...) }` — the `let!`
captures the WPF dispatcher's `SynchronizationContext`. When
the dispatcher thread blocks on `.Result`, the task's
continuation can never resume because the dispatcher is
blocked. The 500ms timeout never fires because the timeout's
continuation also needs the dispatcher. Classic
sync-over-async deadlock. PR-G fixes by running the whole
operation in a Task off the dispatcher and using a dedicated
STA thread for `Clipboard.SetText` (which can also hang on
contention with NVDA's clipboard hooks; bounded by a 3s
timeout in the fix).

**Inventory date.** 2026-05-03; pty-speak post-PR-#137
(`v0.0.1-preview.NN` to be cut); cmd.exe Windows 10.0.26200;
NVDA version unrecorded. **Source: NVDA pass 2026-05-03;
fixed in PR-G.**

### [output-stream] Verbose readback: whole-screen announcement on every emit

**What broke.** Stage 5's generic `renderRows` coalescer
announces the **entire rendered screen** on every emit. Claude
Code's Ink reconciler drives whole-frame redraws ~10 Hz during
streaming response generation
([`spec/tech-plan.md`](../spec/tech-plan.md) §7.3). The
combination produces so much speech that NVDA's speech queue
falls behind and the user can't keep up — even with the
spinner-row dedup in place, the bulk of the response text is
re-announced on every Ink frame.

**Why it matters.** This is the first foundational architecture
decision the Output framework cycle (Part 3) addresses. Per
[`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) Stage 7 sketch
"Known risks" §2: the right fix isn't a verbosity-tuning knob
on the generic coalescer — it's a per-content-paradigm
presentation strategy (Stream / REPL / TUI / Form / Selection)
where each paradigm has its own announcement model.
[`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) Part
3.2's RFC owns the design.

**Reproduction.** Set `PTYSPEAK_SHELL=claude` and launch (or
`Ctrl+Shift+2` after launch). Ask Claude a multi-paragraph
question ("Explain how ConPTY works in 200 words"). Listen
for the speech-queue lag pattern: NVDA continues reading the
beginning of the response while later paragraphs scroll past
on screen.

**Hypothesised root cause.** `Coalescer.fs`'s
`processRowsChanged` flushes the full visible row range on each
debounce window. Claude's per-Ink-frame redraw bumps the
sequence number and triggers a flush; the flush re-announces
already-spoken rows because their hash differs from the
preceding flush by a single character (e.g. cursor advanced).
The framework cycle resolves this by introducing a Stream
profile with suffix-diff append-only emission for
streaming-output workloads.

**Source: design prediction** (per
[`spec/tech-plan.md`](../spec/tech-plan.md) §7.4 +
[`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) Stage 7 sketch
"Known risks" §2) **CONFIRMED empirically NVDA pass 2026-05-03**
with concrete severity worse than predicted: confirmed even on
plain `cmd.exe` interaction (not just Claude). Single-character
typed input produced character-by-character announce growth
`TextLen=140 → 141 → 142 → 143 → 145 → 146` — every keystroke
re-announces the full rendered screen. Running `dir` (or
similar) produced 1316 / 1347 / 847-character announces
back-to-back; NVDA needed 30-45 seconds to read each one,
during which any subsequent input queued behind. The terminal
becomes effectively unusable for any workload that produces
more than a few characters of output. Architectural fix
remains in scope for Output framework cycle Part 3.2 RFC; a
stopgap could cap announce length at the coalescer
(post-PR-G follow-up if needed).

### [output-selection] Tool-use prompt reads as flat text instead of listbox

**What broke.** Claude's tool-use confirmation prompt
("Edit / Yes / Always / No") is rendered as a vertical list of
text items, one of which carries a distinct background colour
indicating selection. NVDA reads it as a paragraph instead of
a listbox: "Edit Yes Always No" with no list semantics, no
"item N of M" announcement, no arrow-key list navigation.

**Why it matters.** The user can't tell which item is currently
selected without inferring from background colour, which the
substrate doesn't surface to the screen reader. Arrow keys
move the highlight on screen but NVDA doesn't announce the
movement. Pressing Enter accepts whatever is highlighted —
usable only if the user can guess.

**Reproduction.** Set `PTYSPEAK_SHELL=claude`. Ask Claude to
edit a file in the current directory. When the
"Edit / Yes / Always / No" prompt appears, press the down
arrow. Listen for an announcement of the selection change.
None will arrive (predicted).

**Hypothesised root cause.** No selection-list detection layer
in the parser → screen → UIA peer pipeline. The substrate
exposes the prompt as part of the Document text view; UIA's
List + ListItem control types aren't published. Original
spec Stage 8 ("Interactive list detection + UIA List
provider") owns the fix; the May-2026 plan folds Stage 8 into
the Output framework cycle's Selection profile.

**Source: design prediction** (per
[`spec/tech-plan.md`](../spec/tech-plan.md) §7.4 known issue
#2 → Stage 8). Empirical confirmation pending.

### [output-earcon] Red error text reads as plain text (no severity signal)

**What broke.** Claude (and other shells) emits red-coloured
text via SGR `\x1b[31m...\x1b[0m` to indicate errors / warnings.
NVDA reads the text but conveys no colour or severity
information. The user can't tell from the announcement that
they just read an error message vs. a normal output line.

**Why it matters.** Errors in compiled output, test failures,
git conflict markers, and Claude's own error messages all
become indistinguishable from normal text. Sighted users
process severity at a glance from colour; blind users currently
have no equivalent channel.

**Reproduction.** At a cmd or claude prompt, run a command
that produces red error output (e.g. ask Claude to deliberately
introduce a syntax error and run a build, or run
`git diff` on a file with conflict markers). Listen for any
audible cue distinguishing the error text from surrounding
output. None will arrive (predicted).

**Hypothesised root cause.** Stage 9 (earcons) hasn't shipped.
The May-2026 plan folds Stage 9 into the Output framework
cycle's Earcons presentation-sink layer. Frequency mapping
(red=220 Hz square alarm, yellow=440 Hz triangle warn, etc.)
is documented in spec §9.3 but not yet implemented.

**Source: design prediction** (per
[`spec/tech-plan.md`](../spec/tech-plan.md) §7.4 known issue
#3 → Stage 9). Empirical confirmation pending.

### [review-mode] No quick-nav after focus moves past response

**What broke.** Once the cursor moves past Claude's response
(user types a follow-up prompt, or the next tool-use prompt
arrives), there's no way to jump back to the previous
response and re-read a specific section. NVDA's review cursor
can move line-by-line via `Caps Lock + Numpad 8` etc., but
moving from "current screen line 30" back to "the start of
the response three exchanges ago" requires line-by-line
backwards navigation through every intervening line.

**Why it matters.** Long Claude sessions accumulate dozens of
exchanges; the user needs random-access navigation to
previous responses. Quick-nav letters (`e` for next error,
`o` for next output block, `c` for next command) are the
canonical screen-reader pattern (NVDA's browse-mode shortcut
keys); pty-speak doesn't yet expose them.

**Reproduction.** Have a multi-exchange Claude session
(3–4 exchanges). Try to jump back to the second response's
specific paragraph without using line-by-line review-cursor
navigation. No quick-nav is available (predicted).

**Hypothesised root cause.** Stage 10 (review mode +
quick-nav letters) hasn't shipped. The semantic-event taxonomy
(error / warning / command / output block / interactive
element / prompt) needs to land in the parser → semantic-mapper
layer first; review mode then consumes that taxonomy via
quick-nav letters. Spec §10 has the full design;
[`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md)
Part 5 sequences Stage 10 as the first non-built-in consumer
of the Output / Input framework taxonomies.

**Source: design prediction** (per
[`spec/tech-plan.md`](../spec/tech-plan.md) §7.4 known issue
#4 → Stage 10). Empirical confirmation pending.

### Template (use for new entries surfaced during NVDA validation)

```markdown
### [tag-1] [tag-2] One-line summary of the gap

**What broke.** Literal user-observable behaviour. Concrete enough
to reproduce.

**Why it matters.** What the user couldn't do; what the speech
queue did wrong; what the screen reader silenced or repeated
inappropriately.

**Reproduction.** Exact prompt to type into Claude Code; exact key
sequence; NVDA configuration if relevant (speak-typed-characters
on/off, review cursor mode, etc.).

**Hypothesised root cause.** Which part of the pipeline looks
suspect — parser? coalescer? UIA peer? input encoder? activity-ID
processing? Don't speculate architecturally; just point at the
component.

**Inventory date.** YYYY-MM-DD; pty-speak version
(`v0.0.1-preview.N`); Claude Code version (`claude --version`);
NVDA version.
```

## Cross-references

- [`docs/SESSION-HANDOFF.md`](SESSION-HANDOFF.md) "Stage 7
  implementation sketch (next)" §5 — the "Capture a Stage 7 issues
  inventory" step that produces entries here.
- [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md) Part 2
  (Stage 7 validation gate) and Parts 3 + 4 (Output / Input
  framework cycles) that consume this inventory as design input.
- [`spec/tech-plan.md`](../spec/tech-plan.md) §7 — the canonical
  Stage 7 specification; this file documents the gaps the
  spec-conformant implementation surfaces.
- [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) — the
  manual NVDA matrix Stage 7 ships a row in; the matrix is the
  procedure that produces the inventory entries.
