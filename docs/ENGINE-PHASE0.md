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
| `r` | Re-narrate the focused chunk |
| `s` | Stop speech |
| `?` | Speak the key list |
| `q` | Quit |

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
