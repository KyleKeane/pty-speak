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

> **Status:** Stage 7 substrate **shipped** across four sequenced
> PRs (per
> [`docs/PROJECT-PLAN-2026-05.md`](PROJECT-PLAN-2026-05.md)
> Part 2):
>
> - **PR #131 (PR-A)** — env-scrub PO-5 (closes
>   [`SECURITY.md`](../SECURITY.md) row PO-5).
> - **PR #132 (PR-B)** — extensible shell registry +
>   `PTYSPEAK_SHELL` startup override.
> - **PR #134 (PR-C)** — hot-switch hotkeys
>   `Ctrl+Shift+1` / `Ctrl+Shift+2`.
> - **PR-D (this PR)** — manual NVDA-validation matrix expansion
>   in [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
>   "Stage 7 — Claude Code roundtrip" + this inventory's first
>   pre-seeded entries.
>
> **Empirical NVDA validation is maintainer-driven.** This PR
> ships the validation matrix + the design-derived inventory
> entries below; the maintainer runs the matrix, confirms or
> refines the predicted entries, and adds any additional gaps
> surfaced empirically. Per the May-2026 plan: **don't fix
> anything from the inventory in Stage 7; that's framework-cycle
> work.** The Output framework cycle (Part 3) consumes this
> inventory as design input for its research phase
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
"Known risks" §2). Empirical confirmation pending in
maintainer NVDA pass.

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
