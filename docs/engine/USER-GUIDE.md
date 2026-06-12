# The engine user guide — your first sessions

> Who this is for: a brand-new user of the pty-speak
> **interaction engine** — the keyboard-only, audio-only
> computational notebook. No screen, no screen reader, no
> mouse: the app speaks for itself, every action is a
> keystroke, and every event has a sound with a place on the
> stereo stage. (The classic WPF terminal app is a separate
> surface with its own docs; see the repo README.)

## 1. What this is, in one paragraph

You talk to an AI participant (Claude Code, running locally on
your machine). Every reply comes back not as a wall of speech
but as a **navigable tree of typed chunks** — sections,
paragraphs, lists, code blocks, tool calls — that you walk
with single keys, hearing exactly as much as you ask for. The
things worth keeping you **pin** into a **notebook**: an
editable sequence of pinned results, your own narrative, and
section headers, which exports as a clean markdown document —
the publishable record of a computational exploration, and the
seed you can feed into the next one.

## 2. Setup (five minutes)

1. Install the **.NET 9 SDK** — from a command prompt,
   `dotnet --version` should answer with 9-point-something.
2. Install and authenticate the **Claude Code CLI** —
   `claude --version` should answer.
3. From the repo root:
   `dotnet run --project src/Engine.Host/Engine.Host.fsproj -c Release`
4. If the CLI is not on PATH as `claude.cmd`, tell the engine
   where it is, either with
   `setx ENGINE_CLAUDE_PATH "C:\path\to\claude.exe"` (then a
   fresh prompt) or in the config file (section 7).

On launch the engine says "Engine ready" followed by the key
list. If a previous session exists it offers to reopen it.
Everything spoken also prints to the console — that mirror is
for sighted collaborators and bug reports; audio is the
product.

## 3. The core loop (learn these six keys first)

| Key | What happens |
|---|---|
| `c` | Compose: type a request, Enter sends it |
| `g` | Jump to the start of the latest response |
| `j` / `k` | Next / previous (at the top level: section to section) |
| `l` / `h` | Descend into / ascend out of the focused chunk |
| `r` | Hear the focused chunk again, in full |
| `s` | Silence, immediately, everything |

Send a request with `c`. You hear it confirmed back ("Sent:
…") — that confirmation is your proof the engine heard you
correctly, without re-reading anything. While the participant
works you hear only soft ambient sounds on your right: one
tick per arriving chunk (its pitch tells you the kind), a
rising tone as the count grows. When the response completes
you hear a bright center tone and "Response complete, N
chunks." Press `g` and start walking.

## 4. Hearing structure

Replies are stored with their real shape. A response with
headings becomes **sections**: at the top level `j`/`k` step
section to section ("Heading level 2: Setup. 4 items
inside."), `l` enters the one you're on, `h` comes back out.
Lists work the same way — the list announces "Bulleted list, 3
items." and `l` walks into the items. Every move ends with
its position — "…, 2 of 5" — so you always know where you are
in the row, and `w` speaks the full breadcrumb: what you're
on, its position, every ancestor, and the depth. `t` and `e`
jump to the first and last item at the current level. You can
never fall out of the content: at an edge the engine says so,
plays a dull tone on the side you bumped, and stays put.

## 5. The stereo stage (what the sounds mean)

Best on headphones. Position = meaning:

- **Center** — the narrative thread: your request landing, a
  response completing (bright) or failing (low and long).
- **Near right** — content arriving: one soft tick per sealed
  chunk; high ring = heading, mid = paragraph, F-ish = code,
  low = a tool error.
- **Right** — progress: a rising series as the response grows.
- **Left side** — housekeeping: session start far left,
  diagnostic notes left.
- **Under your keys** — navigation: a crisp tick panned the
  way you moved (descend low, ascend high); a dull tone where
  you hit an edge.

With practice you can monitor a long-running turn entirely
peripherally — the stage never interrupts speech.

## 6. Branching and the notebook

- **Branch** (`b` on a focused chunk): ask "what do you mean
  here?" *anchored to that exact chunk*. The clarification
  nests under it; the main thread is untouched; `a` returns
  you to the precise place you left.
- **Pin** (`p`): the focused chunk joins your notebook.
- **Notebook mode** (`n` toggles): walk your pinned sequence
  with the same keys; `i` inserts your own narrative sentence,
  `u` inserts a section header, `[` and `]` reorder, `x`
  removes, `m` exports the whole thing as a markdown file and
  speaks the path.
- **Rerun** (`y` on a focused request): issue that request
  again as a fresh turn.

The notebook is the point of the instrument: explore in the
transcript, keep the good parts, narrate between them, reorder
into an argument, export. The export is plain markdown — ship
it, or paste it into a future request as the context for the
next exploration.

## 7. Tuning the instrument

Speech rate is `+` and `-`, live. Everything else lives in
`%LOCALAPPDATA%\PtySpeak\engine.toml` — voice, default rate,
how much of a long chunk a navigation read speaks, cue volume
or disabling cues entirely, and rebinding any key to any
single character. Full reference:
[`CONFIGURATION.md`](CONFIGURATION.md). The file is optional;
every setting has a sensible default, and a bad value never
breaks anything — the engine speaks a warning and uses the
default.

## 8. Sessions: nothing is lost

After every completed turn the session auto-saves. `v` saves
on demand; `o` (at startup or any time) reopens the last
session — the tree, your position targets, and the
conversation continuity (the next request resumes the same CLI
session). Sessions live under
`%LOCALAPPDATA%\PtySpeak\engine-sessions\`.

## 9. When something goes wrong

Press `d`: the engine speaks a one-breath summary (uptime,
event counts, the last error verbatim) and writes a full dump
file, speaking its path. That file plus the session event log
next to it is a complete bug report. More:
[`TROUBLESHOOTING.md`](TROUBLESHOOTING.md).

## 10. Where to go next

- [`WORKFLOWS.md`](WORKFLOWS.md) — recipes: code
  investigation, an experiment loop, building a narrative.
- [`KEYBOARD-REFERENCE.md`](KEYBOARD-REFERENCE.md) — every
  key, both modes, and how to rebind.
- [`CONFIGURATION.md`](CONFIGURATION.md) — every setting.
- [`../ENGINE-PHASE0.md`](../ENGINE-PHASE0.md) — the formal
  acceptance walk the maintainer runs.
