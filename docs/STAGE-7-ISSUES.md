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

> **Status:** stub created in PR #129 (final-handoff audit). **No
> entries yet** — Stage 7 implementation hasn't started. The next
> session that picks up Part 2 / Stage 7 fills this file as it
> validates Claude Code under NVDA. Don't try to fix anything from
> the inventory in Stage 7; that's framework-cycle work.

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

_(empty — Stage 7 NVDA validation hasn't started yet. First entry
arrives when the next session ships Stage 7 and runs the
`docs/ACCESSIBILITY-TESTING.md` Stage 7 manual matrix row.)_

### Template (delete when first real entry lands)

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
