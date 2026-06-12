# Engine textbook, chapter 4 — navigation and narration

> Files: `src/Engine.Core/Navigator.fs`, `ChunkNarration.fs`.
> Tests: `NavigatorTests`, `ChunkNarrationTests`. Decisions:
> ADR 0011, ADR 0012 S2/S5; spec §6.2 (the verbs), §6.4 (the
> non-ejection invariant), §10 (orientation).

## Navigator — verbs as pure transitions

Navigation state is two values: the focused chunk id and the
anchor stack. Every verb is a pure function
`State → Tree → State * Move`, where `Move` is the typed
outcome: `Moved chunk`, `Edge description`, or
`NothingFocused`. The host narrates the outcome and plays the
direction-coded cue; the navigator itself knows nothing about
sound.

**The non-ejection invariant holds by construction**, not by
care: an `Edge` returns the state unchanged, and the only way
focus ever changes is `focus`, which verifies the target
exists in the tree. There is no code path that can place the
cursor outside the content model — the single clearest failure
of the screen-reader-on-chat experience, eliminated by type
shape (spec §6.4).

The verb set: `focus`, `jumpToLatestResponse` (target supplied
by the ingest session), `next`/`previous` (authored-order
siblings), `descend` (first child) / `ascend` (parent),
`firstSibling`/`lastSibling`, `current` (re-narration), and
the anchor pair — `pushAnchor` records the focus, and
`returnToAnchor` pops back to the *exact chunk* a branch was
spun from; anchors nest as a stack so branches-within-branches
unwind correctly (spec §5.1 "anchor + return").

## ChunkNarration — the canonical voice

Self-voicing (spec §4.6/§14.11) means the engine renders its
own meaning to speakable text; nothing downstream re-derives
it. The rendering discipline, used by every kind:
**structure before content** — "Code block, fsharp, 14
lines." precedes the body; "Numbered list, 3 items." precedes
nothing (containers defer content to their children); a
heading announces its level, title, and section size. A
listener can therefore always *skip early*: the first second
of any utterance identifies what it is and how big.

Three renderings, three uses:

- `describe` — the full canonical read (`r`).
- `describeCapped` — the same, bounded for navigation: a long
  body cuts at the configured cap with the honest marker
  "Truncated; N more characters — press r to hear all."
  Because structure leads, the cut can never eat the label.
- `describeAt` — `describeCapped` plus the positional suffix
  "…, 3 of 5": the audio scrollbar, present on every move.

## locate — orientation that cannot drift

`locate` is the where-verb: the focused chunk's short label
and position, the ancestor trail innermost-first ("inside
section Setup, inside your request: …"), and the depth. It is
computed from the tree at the moment of asking — the spec §10
observation is that hand-maintained orientation (the
SESSION-HANDOFF of the old world) drifts; a derived one
cannot. Trail labels clip at 40 characters because the trail
is orientation, not content.

## Why narration text lives in the core

English strings in a "pure core" deserve a justification: the
narration IS the canonical rendering of the model — the
engine's equivalent of a UI. Putting it beside the model (a)
keeps every consumer (voice, console mirror, future braille)
verbatim-identical, and (b) makes the strings testable as
values. Localization, when it comes, swaps this one module
behind the same signatures.
