# Engine workflows — recipes for computational exploration

> Companion to [`USER-GUIDE.md`](USER-GUIDE.md). Each recipe is
> a key-by-key pattern that experienced users settle into. The
> common shape: **ask → monitor peripherally → walk the
> structure → keep the good parts → narrate → export.**

## 1. The investigation loop (understanding anything)

The bread-and-butter pattern for exploring a codebase, a
dataset description, an unfamiliar concept:

1. `c` — "Explain the architecture of this repository in
   sections: entry points, core model, persistence."
2. While it streams, keep thinking; the right-side ticks tell
   you content is landing and what kind.
3. `g`, then `j` across the sections, hearing each heading and
   its size. Descend (`l`) only into the section you care
   about.
4. On the chunk that matters: `b` — "what calls this?" The
   answer nests right there; `a` brings you back.
5. `p` the chunks worth keeping as you go.
6. Repeat from 1 with sharper questions. Depth-first when a
   section is hot, breadth-first (`j` at the top) when
   orienting.

The discipline that makes this fast: **never listen to a whole
response**. Headings + counts + positions are usually enough
to decide where to descend; `r` exists for the one chunk that
deserves a full read.

## 2. The experiment loop (run, observe, adjust)

For iterative computational work — a calculation, a script, a
simulation parameter sweep:

1. `c` — "Write and run a Python script that simulates 1000
   coin flips and reports the longest run."
2. The turn streams: a G-pitched tick = a tool call went out;
   the next ticks = results landing. You can tell *the agent
   is executing* without a word spoken.
3. `g`, walk to the tool result (`j`; tool results have their
   own pitch and announce as "Tool result"), then `l`/`r` for
   the actual numbers.
4. Pin the result (`p`).
5. `c` — "Same again but 10,000 flips, and compare." (The CLI
   session persists — *it remembers the script*.)
6. After a few rounds: `n`, reorder the pinned results into
   sequence with `[`/`]`, `i` to add "Run 2 doubled the run
   length, as theory predicts," `m` to export.

You end the session holding a markdown lab notebook of the
whole sweep — produced entirely by ear, never re-reading.

## 3. Building a computational narrative (the notebook as the goal)

When the deliverable IS the document — a report, a tutorial, a
decision memo:

1. Open with structure: in notebook mode (`n`), `u` "Question",
   `u` "Method", `u` "Results", `u` "Conclusion". Four section
   headers, an empty skeleton.
2. Back to transcript (`n`), explore (workflows 1–2), pinning
   into the notebook as findings arrive. New pins append at
   the end; `[` walks each up under the right section.
3. Between pins, `i` narrative cells: the *why* in your words
   — the connective tissue no agent wrote.
4. `m` exports. The result reads top-to-bottom as an argument,
   with live results embedded — section headers, your prose,
   re-fenced code, labelled tool output.

## 4. Flowing into a new domain (narrative as seed)

Yesterday's export is today's context:

1. Open the exported `.md`, copy its text (or have a future
   request reference its path).
2. `c` — "Here is yesterday's investigation: <paste>. Extend
   it: does the conclusion hold for the asymmetric case?"
3. The new response decomposes into the same navigable
   structure — the narrative literally flows forward, because
   the export format IS the engine's ingest format.

## 5. The triage pattern (when a turn goes wrong)

- A low, long center tone = the turn failed. `g` still works —
  whatever sealed before the failure is navigable.
- Ambient notes on your left ("Unrecognized stream event…",
  "Participant exited with code…") are the engine being honest
  about what it saw. `d` speaks the summary and writes the
  dump; the last error is read verbatim.
- `o` after a restart restores the session; the conversation
  resumes where it was.

## 6. Listening posture (the meta-recipe)

- Crank the rate (`+`) until comprehension just holds; trained
  listeners run SAPI far above default.
- Treat the stage as your peripheral vision: compose your next
  thought WHILE a turn streams; the completion tone is your
  cue to look up.
- `s` is always safe: stop means stop, and the structure is
  still there — nothing in this instrument is ever lost by not
  listening to it.
- `w` whenever you return from a thought: one keystroke
  re-orients you completely.
