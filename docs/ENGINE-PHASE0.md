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

The full key surface (two modes, rebinding, design notes) is
[`engine/KEYBOARD-REFERENCE.md`](engine/KEYBOARD-REFERENCE.md);
`?` speaks the live table. The core six: `c` compose · `g`
latest response · `j`/`k` next/previous · `l`/`h`
descend/ascend · `r` repeat · `s` stop. Beyond Phase 0's
original surface the engine now also has: the notebook (`p`
pin, `n` toggle, `i`/`u`/`[`/`]`/`x`/`m` edit + export),
sessions (`v` save, `o` reopen; auto-save after every turn),
find/summary/direct-address (`f`/`z`/digits), rerun (`y`),
diagnostics (`d`), and live speech rate (`+`/`-`). Config:
[`engine/CONFIGURATION.md`](engine/CONFIGURATION.md).

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

10. **The notebook loop (ADR 0013):** pin two chunks (`p`),
   `n` into the notebook, `i` a narrative sentence between
   them (`[` to move it), `m` export — confirm the spoken
   path and that the markdown file reads as a document; `x`
   a cell and hear the honest count.
11. **Persistence:** quit (`q`), relaunch, press `o` — the
   session AND notebook restore (counts spoken) and the next
   `c` request continues the same conversation.
12. **Config + diagnostics (ADR 0014):** put a deliberate
   mistake in `engine.toml` (e.g. `rate = "fast"`), relaunch —
   one spoken warning count, nothing broken; `d` reads the
   detail and names the dump file. Set `rate = 4` and a
   `[keys]` rebinding and confirm both take effect. `+`/`-`
   adjust rate live.

Record the outcome in
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
(matrix row `P0-ENGINE-1`).

## Known Phase 0 boundaries (by design)

- Keyboard-first composition; speech input arrives via OS
  dictation into the compose line (ADR 0011 E8).
- Notebook editing is the v1 verb set (pin / narrative /
  section / reorder / remove / export); inline text editing
  of existing cells is the v2 editor (ADR 0013).
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
