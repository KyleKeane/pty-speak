# #428 — ContentHistory backspace-erase: design note

Status: **Partially shipped** (2026-05-17 → 2026-05-18). Scope:
the cmd `\x08`-erase defect caught by the replay oracle's C5
fact (`docs/boundary-capture/REPLAY-ORACLE.md`). Implements the
spirit of [ADR 0004](adr/0004-iocell-model-for-shell-interaction.md)
more faithfully; **does not amend** any ADR 0004 decision.

> **Update 2026-05-18 — (ii) reverted.** The Direction-1 core
> (the BS arm on `appendFromEvent`) shipped and is oracle-
> validated (C5 active). The companion sub-decision **(ii)**
> — gating `tick`'s idle-seal for `UserInputEcho` spans — was
> implemented in #431 and **reverted the same day**: it broke
> the live announce watermark (R3d/`commandEnterSeq`, the R6a
> path at `Program.fs` ~5079–5125), reintroducing the
> "echo hi⏎hi" + prompt-path-bleed class and garbling the
> sub-prompt / Ctrl+C announces. The replay oracle could not
> catch it because the harness never calls
> `ContentHistory.tick` — exactly the live-only fidelity gap
> the "Open sub-decision" section below flagged. **(ii)
> stays deferred** until there is a live-faithful test (a
> harness `tick` model or an integration test) that exercises
> the idle-seal + watermark interaction. The narrow
> slow-typed-then-paused-then-backspaced residual (i) is
> reaccepted in the meantime.

## Problem

When the user edits a command in-line with Backspace before
pressing Enter, the deleted characters survive into the sealed
IOCell `CommandText` (`ECHO HELLOXX`, not `ECHO HELLO`). C5
asserts the invariant and fails; it currently ships
`Skip`-with-reason pending this work.

## Root cause (confirmed against code)

cmd echoes an in-line erase as the 3-byte idiom `BS SP BS`
(`0x08 0x20 0x08`) — a Screen-level destructive edit.

- The VT parser emits BS as `Execute 0x08uy`
  (`src/Terminal.Parser/StateMachine.fs`, C0-execute path).
- `Screen` **does** apply it: cursor-left, clamped at col 0
  (`src/Terminal.Core/Screen.fs:15`, `:185`). The rendered
  prompt row is therefore correct.
- `ContentHistory.appendFromEvent`
  (`src/Terminal.Core/ContentHistory.fs:810-866`) has arms for
  LF (`0x0A`), CR (`0x0D`), BEL (`0x07`), and CUB (`CSI D`),
  but **no `0x08` arm** — BS falls into the catch-all
  `_ -> []` and is dropped. The `SP` between the two BS bytes
  is a `Print` and *is* appended. Net: the active span keeps
  the to-be-deleted chars (and gains a stray space).
- Extraction reads `ContentHistory.sliceText` via `cmdAbSplit`
  (`src/Terminal.Core/SessionModel.fs:814-838`), which includes
  the unsealed `ActiveSpanText`. So whatever the active span
  holds becomes `CommandText`.

This is the ContentHistory-vs-Screen tension: Screen is
display-only post-pivot (ADR 0001/0004); ContentHistory is the
sole extraction substrate. The substrate simply never learned
the erase.

## Directions considered

1. **Teach ContentHistory the `\x08` erase (within ADR 0004).**
   Add a BS arm to `appendFromEvent` that deletes the last
   char of the active span. ContentHistory stays the sole
   extraction substrate. **Chosen.**
2. **Let extraction consult the Screen-rendered row.**
   Directly contradicts ADR 0004 ("screen-row coordinates are
   display-only post-pivot; ContentHistory is the sole
   extraction substrate") and reopens a ratified Cycle-51
   pivot for a localized defect. **Rejected.**
3. **Post-hoc regex of the `BS SP BS` idiom on the flattened
   command text.** Shell-specific, brittle across PowerShell /
   PSReadLine and across chunk/span splits. **Rejected.**

## Chosen mechanism (Direction 1)

Add to `appendFromEvent`'s `match event with`:

```
| Execute b when b = 0x08uy ->
    // BS — destructive cursor-back on the active span.
    // Composes with cmd's `BS SP BS` erase idiom.
    if state.ActiveSpanText.Length > 0 then
        state.ActiveSpanText.Remove(
            state.ActiveSpanText.Length - 1, 1) |> ignore
        if state.ActiveSpanText.Length = 0 then
            state.ActiveSpanStartedAt <- ValueNone
            state.ActiveSpanSource <- ValueNone
    []
```

- **Clamp at empty** — a BS with an empty active span is a
  no-op. It does **not** reach back into sealed entries
  (append-only invariant; ADR 0004). This matches cmd reality:
  the line editor never echoes a BS that would erase past the
  prompt.
- **Reset start/source when emptied** so a subsequent `Print`
  re-snapshots the first-byte source (keeps the PR-Q
  first-byte-source invariant honest).
- Arithmetic check on `ECHO HELLOXX` + two `BS SP BS`:
  `…XX` →BS `…X` →SP `…X ` →BS `…X` →BS `…`+? second idiom →
  `ECHO HELLO`. Composes correctly.

`cursorRowAfter` returns `currentRow` for BS (not enumerated)
→ no spurious synthetic newline; deferred CR/CUB resolution
treats BS as a non-`Print` next-event → no spurious overwrite.
No change to the ADR 0004 JSONL schema (sealed entries, `Seq`
identity, wire format all unchanged). One-line Status-note in
ADR 0004 is the only doc touch.

## Open sub-decision — the idle-seal fragmentation gap

Active-span-only BS is correct **iff the typed command is not
fragmented across sealed TextSpans before the BS arrives**. The
200 ms idle-tick (`ContentHistory.tick`) seals the active span
when typing pauses; a later BS then cannot reach the sealed
chars. The replay oracle never calls `tick`, so C5/C6 go green
either way — but **live** cmd runs `tick` on a PeriodicTimer,
so a slow-typed-then-paused-then-backspaced command would still
leak in production.

- **(i) Accept the gap.** Ship the BS handler only. Oracle
  green; document the live-only residual. Smallest, knowingly
  partial.
- **(ii) Also gate idle-seal while composing.** Skip `tick`'s
  seal when `resolveSource state = EntrySource.UserInputEcho`.
  The typed command is suppressed from announces anyway
  (ADR 0003/0008), so idle-sealing it serves no purpose and
  only creates the hazard. Keeps the whole command in one
  active span until `CommandStart`, so BS always reaches it.
  Uses only the existing `SourceResolver` machinery; stays
  within ADR 0004. Slightly larger (touches `tick`).
  **Recommended** — makes oracle and live behaviour agree.

## Out of scope

PowerShell / PSReadLine in-line editing is CSI-cursor-driven
(kill-line, full-line repaint, cursor save/restore), not the
`\x08`-erase idiom — a distinct, larger ContentHistory↔CSI
impedance, separate from #428 and tracked independently
(R5 PowerShell-adapter context). #428 fixes the cmd `\x08`
erase only.

## Acceptance

- Flip C5 `Skip` → active (`commandNotContains=X` green);
  C1/C2/C3/C6 stay green.
- New `ContentHistoryTests` unit cases in isolation: bare BS
  deletes last char; BS on empty span no-ops (no reach into
  sealed entries); `BS SP BS` idiom nets to deletion; BS does
  not cross a seal boundary. If (ii): a `tick` mid-compose does
  not seal a `UserInputEcho` span.
- Diagnostic args shipped in the same PR (CLAUDE.md "new
  features ship with diagnostic triggers"): Debug-log the BS
  application with pre/post `ActiveSpanText.Length`.
