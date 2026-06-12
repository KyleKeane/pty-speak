# Engine Phase 0 — the local bootstrap: running and validating

> Status: shipped CI-green 2026-06-11 (ADR 0011; RELAUNCH-SPEC
> §13 Phase 0). **Maintainer local dogfood = the acceptance
> gate** — CI validates everything except narration, and
> narration is the product (§1.1).

## What this is

`Engine.Host` is the first build of the **interaction engine**:
a console executable that talks to a **local Claude Code CLI**
over its structured stream-json interface, decomposes each
response into a sealed, navigable **chunk tree**, and voices
everything through its own **self-voicing SAPI channel** — no
WPF, no UIA, no NVDA (day-zero canon, spec §0.1). The WPF
terminal app is untouched and still builds/ships as before.

Assemblies (all additive — ADR 0011 E1):

| Assembly | What it holds |
|---|---|
| `Engine.Core` (plain `net9.0`) | Chunk model + tree, stream-json parser, markdown chunker, ingest fold, engine event bus, navigation verbs, narration rendering, attention queue/policy, `ISpeechSink` contract |
| `Engine.Participants` | The Claude Code CLI process runner (spawn + line pump) |
| `Engine.Voice` | `SapiSink` — Windows SAPI behind `ISpeechSink` |
| `Engine.Host` | The console host wiring all of it |

## Prerequisites (on the local machine)

1. **.NET 9 SDK** — `dotnet --version` should print a 9.x
   version.
2. **Claude Code CLI installed and authenticated** — `claude
   --version` from a cmd prompt should answer. If the
   executable is not on `PATH` as `claude.cmd` (the npm shim
   name the host tries by default), set the env var from cmd:

   ```
   setx ENGINE_CLAUDE_PATH "C:\full\path\to\claude.exe"
   ```

   (then open a fresh cmd window so the var is picked up).

## Build and run

From the repo root, in cmd:

```
dotnet run --project src/Engine.Host/Engine.Host.fsproj -c Release
```

On start the host speaks: "Engine ready." followed by the key
list. Everything it says also prints to the console (a
debugging mirror only — audio is the product surface).

## Keys

| Key | Verb |
|---|---|
| `c` | Compose a request (type a line, Enter sends) |
| `b` | Branch: compose anchored at the focused chunk (§5.1) |
| `a` | Return to anchor — back to the exact branch origin |
| `g` | Jump to the start of the latest response |
| `j` / Down | Next chunk |
| `k` / Up | Previous chunk |
| `l` / Right | Descend into the chunk's children |
| `h` / Left | Ascend to the parent |
| `r` | Re-narrate the focused chunk (full body — navigation reads are capped at 600 chars with an honest "Truncated" marker) |
| `w` | Where am I — kind + position + ancestor trail + depth (ADR 0012 S2) |
| `s` | Stop speech (cancels AND clears everything queued) |
| `?` | Speak the key list |
| `q` | Quit |

## The semantic outline (ADR 0012 S1)

Responses are no longer flat: a heading absorbs its section's
content as children, so the tree carries the document's real
outline. Practically: after `g`, `j`/`k` step **section to
section** (headings announce their size — "Heading level 2:
Setup. 4 items inside."), `l` enters the section you're on,
and every move ends with its position ("…, 2 of 5").

## The spatial stage (ADR 0012 S3/S4)

Every universal-event-bus event also renders as a short
stereo-positioned tone (best on headphones; speakers work).
The stage layout encodes the attention contract by position:

- **Center** — the narrative thread: your request landing
  (660 Hz blip), response complete (bright 880 Hz), response
  failed (low 220 Hz, longer).
- **Near right** — the content trickle: one soft tick per
  sealed chunk, **pitch identifies the kind** (headings ring
  high B5, paragraphs C5, code F5, tool errors low D#4…).
- **Right** — turn progress: a rising pitch series as the
  count grows.
- **Far left / left** — lifecycle (session start) /
  diagnostic notes.
- **Navigation** — a crisp tick panned toward the movement
  (next right, previous left; descend low, ascend high); a
  refused move (an edge) rings dull on the side you bumped.

## The attention contract, audible

- Your request is **confirmed foreground** ("Sent: …") —
  the §6.1 narrate-and-confirm loop.
- While the response streams you hear only **ambient progress**
  ("4 chunks so far.") — sealed chunks are never auto-read
  (§5.2; you navigate them on demand).
- Completion is **foreground** ("Response complete, 9
  chunks."), then `g` jumps you to the response start.

## Phase 0 acceptance walk (spec §13)

Run on the local machine, judged on the self-voicing channel
(spec §14.1 — no external screen reader in the loop):

1. Launch; hear "Engine ready" promptly and cleanly.
2. `c`, type `Summarize this repository's purpose in three
   short sections with a code example.`, Enter.
   - PASS: "Sent: …" is spoken back fast (the narrate-and-
     confirm bar); progress arrives ambient; completion is
     announced.
3. `g` then `j`/`k` through the response.
   - PASS: each chunk reads as typed structure ("Heading level
     1 …", "Numbered list, 3 items.", "Code block, fsharp, 2
     lines …"), narration is interruptible (`s`), `r` repeats.
4. `l` into a list, `j` across items, `h` back out.
   - PASS: focus never leaves the content model (§6.4) — edges
     say "no next chunk" etc. and stay put.
5. Focus a chunk, `b`, ask "what do you mean here?", Enter.
   - PASS: the clarification's response chunks nest under that
     chunk; `a` returns to the exact anchor afterwards.
6. `c` again with a follow-up — the CLI session resumes (the
   engine passes `--resume`), so context carries.
7. Quality bar throughout (§1.1): no stutter, no dropped
   utterances, interrupt is immediate. Note SAPI voice quality
   itself is the E7 bootstrap tradeoff — judge *reliability*
   here; voice upgrade is a sink swap later.
8. **Outline + orientation (ADR 0012 S1/S2):** on a response
   with headings, confirm `j`/`k` at the top level move
   section-to-section, headings announce "N items inside",
   `l` enters the section, every move ends "…, N of M", and
   `w` speaks a correct breadcrumb (kind, position, ancestor
   trail, depth) from anywhere in the tree.
9. **The spatial stage (ADR 0012 S3/S4), on headphones:**
   during a turn, confirm the seal ticks sit near right and
   differ in pitch by content kind; progress sits right and
   rises; completion is a bright center tone; nav ticks pan
   toward the movement and an edge rings dull on the bumped
   side. The check: with speech stopped (`s`), navigate and
   tell next from previous from edge **by ear alone**.

Record the outcome in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
(matrix row `P0-ENGINE-1`).

## Known Phase 0 boundaries (by design)

- Keyboard-first composition; speech input arrives via OS
  dictation into the compose line (ADR 0011 E8).
- One participant (Claude CLI), per-turn invocation with
  `--resume` (E4); no persistent stdin process yet.
- Editor verbs (§6.3), side-conversation policy (§8.3), and the
  organization layer (§9) are Phases 1–4 — modelled (authored
  order, anchors) but not exposed.
- The wire-format fixture corpus is the parser contract; if the
  installed CLI's stream-json differs, unknown shapes surface
  as spoken ambient notes ("Unrecognized stream event type:
  …") rather than breaking — paste what you hear and the
  parser gets extended.
