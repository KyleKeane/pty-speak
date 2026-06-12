# Engine keyboard reference

> The complete key surface, both modes, and how to rebind.
> The authoritative source is the one table in
> `src/Engine.Core/KeyMap.fs` (`KeyMap.defaults`) — the in-app
> help (`?`) is *generated* from it, CI asserts it is
> conflict-free, and this document mirrors it. If this page
> and the app ever disagree, trust `?` and file the doc bug.

## Modes

The engine has exactly two modes — which sequence the cursor
walks:

- **Transcript** (startup): the capture tree — everything that
  happened, in its real structure.
- **Notebook** (`n` toggles): your authored sequence — pinned
  chunks, narrative, sections.

Movement, repeat, where, stop, help, save, diagnostics, rate
and quit behave identically in both. Arrow keys are hardwired
duplicates of `j`/`k`/`l`/`h` in transcript mode and survive
any remapping.

## Transcript mode

| Key | Verb | Notes |
|---|---|---|
| `c` | compose a request | type a line, Enter sends; Enter on an empty line aborts ("Nothing sent.") |
| `b` | branch at the focused chunk | the clarification nests under it; the anchor is remembered only if you actually send |
| `a` | return to the branch anchor | anchors nest as a stack |
| `g` | jump to the latest response | the first chunk of the most recent turn's reply |
| `j` / Down | next | sibling at this level; "…, N of M" |
| `k` / Up | previous | |
| `l` / Right | descend | into a section's content, a list's items |
| `h` / Left | ascend | to the parent |
| `t` | first item at this level | |
| `e` | last item at this level | |
| `r` | repeat in full | the uncapped read of the focused chunk |
| `w` | where am I | kind, position, ancestor trail, depth |
| `p` | pin to the notebook | appends a live reference |
| `y` | rerun the focused request | re-issues that request text as a new turn |
| `n` | switch to the notebook | |
| `v` | save the session | auto-save also runs after every turn |
| `o` | open the last saved session | restores tree + conversation continuity |
| `d` | diagnostics | speaks the summary; writes + names the dump file |
| `+` / `-` | speech rate up / down | SAPI −10…+10, confirmed aloud |
| `s` | stop speech | cancels AND clears everything queued |
| `?` | speak the key list | generated from the table |
| `q` | quit | |

## Notebook mode

| Key | Verb | Notes |
|---|---|---|
| `j` / `k` | next / previous cell | |
| `t` / `e` | first / last cell | |
| `r` | repeat the cell in full | |
| `w` | where am I | "cell N of M" |
| `[` | move this cell up | edge plays the dull tone |
| `]` | move this cell down | |
| `x` | remove this cell | |
| `i` | insert a narrative cell | type a line, Enter |
| `u` | insert a section header | type a title, Enter |
| `m` | export as markdown | writes the file, speaks the path |
| `n` | back to the transcript | |
| `v` `d` `+` `-` `s` `?` `q` | as in transcript mode | |

## Rebinding

Any verb can move to any single printable character via
`[keys]` in `engine.toml` — the verb names are the
`snake_case` forms in the tables' Verb column source
(`KeyMap.verbName`), e.g.:

```toml
[keys]
pin = "z"
notebook_remove = "X"
```

Rules (all enforced, all spoken at startup if violated):

- One printable character per verb; anything else warns and
  keeps the default.
- A rebinding that would collide with another verb *in any
  shared mode* warns and keeps the default — the keymap is
  conflict-free by construction.
- A verb bound only in one mode may reuse a character that the
  other mode uses for something else (the modes never overlap).
- Arrow keys cannot be unbound.

## Design notes (why these keys)

- The home-row cluster `j k l h` mirrors the universal text
  convention; movement is the thing you do most, so it costs
  the least.
- Verbs that *ask* (`r`, `w`, `d`, `?`) are all left-hand
  reaches — query while the right hand rests on movement.
- Destructive or mode-changing keys (`x`, `q`) have no
  modifier shyness because every one of them is either
  confirmable by ear (removal announces) or reversible (`n`
  toggles back, sessions auto-save).
- `s` is deliberately adjacent to nothing destructive: silence
  must be a reflex.
