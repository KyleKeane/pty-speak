# Engine textbook, chapter 8 — configuration and the keymap

> Files: `src/Engine.Core/EngineConfig.fs`, `KeyMap.fs`.
> Tests: `EngineConfigTests`, `KeyMapTests`. Decision: ADR
> 0014 C1/C2. User-facing references:
> [`CONFIGURATION.md`](CONFIGURATION.md),
> [`KEYBOARD-REFERENCE.md`](KEYBOARD-REFERENCE.md).

## EngineConfig — the warn-and-default discipline

The parse contract is a single sentence: **no input text can
crash the parse or silently change behaviour.** Every
degradation — malformed TOML, a wrong-typed value, an
out-of-range number, a multi-character key override, a newer
schema — produces the default *plus a typed warning string*
that the host speaks (as a count, with `d` for detail) and
records in diagnostics. Unknown sections and keys are
*silently* ignored — that asymmetry is deliberate forward
compatibility: an older build must not nag about keys a newer
build added; true incompatibility is what the
`schema_version` gate is for (a newer version rejects the
file whole, because half-reading a future format is worse
than defaults).

Implementation inherits `Terminal.Core.Config`'s hard-won
Tomlyn patterns: the non-throwing `Toml.Parse → HasErrors →
ToModel` entry, and tuple-deconstructed `TryGetValue` matches
(`| true, (:? int64 as i)`) that sidestep the F# 9
`byref<obj | null>` friction. TOML quirks handled: integers
box as `int64`; a gain of `1` is accepted as `1.0`.

## KeyMap — one table, four derivations

Every verb is a case of `Verb`; the bindings are one list of
`{ Verb; Key; Modes; Description }`. From that single table
derive, with no second source of truth anywhere:

1. **Dispatch** — `tryFind mode keyChar` is the host's entire
   key decoding.
2. **Conflict checking** — `validate` groups per mode; the
   defaults are CI-asserted conflict-free, so a developer
   adding a verb on a taken key cannot merge.
3. **Generated help** — `helpFor mode` concatenates the
   table; `?` is incapable of drifting from behaviour, and
   the test asserts help covers every binding.
4. **User overrides** — `withOverrides` applies `[keys]`
   entries one at a time, re-validating after each; a
   collision (in any *shared* mode) warns and keeps the
   default, so the map stays conflict-free under arbitrary
   user input. Cross-mode-disjoint reuse is legitimately
   allowed (tested).

`verbName` gives each verb its stable `snake_case` config
name; the compiler's exhaustiveness check on that function is
the mechanism that forces a new verb to become documentable.

## The mode model

Two modes (`Transcript` / `NotebookMode`) — which sequence the
cursor walks — and a binding declares the modes it lives in.
Shared verbs (`Modes = both`) occupy one key in both tables;
mode-local verbs may reuse each other's characters. Arrow
keys are deliberately **outside** the table: hardwired in the
host to the four tree moves as the fallback that survives any
remapping experiment — an instrument whose user can lock
themselves out of moving has failed.

## Why a table and not a config-DSL

A bindings *language* (chords, sequences, modifiers) is
power the v1 surface doesn't need and a failure mode the
audio-only user can't debug by ear. One printable character
per verb, validated, spoken on `?` — every property of the
system stays literally speakable, which is the design test
this whole layer answers to.
