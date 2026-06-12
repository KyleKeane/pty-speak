# ADR 0014 — Engine configuration, declarative keymap, speech controls, and diagnostics

- **Status**: Proposed — authored 2026-06-12 in the
  launch-readiness cycle (same autonomy grant as ADR 0013).
  The extended `P0-ENGINE-1` dogfood ratifies.
- **Date**: 2026-06-12
- **Deciders**: Claude (autonomous, per maintainer grant);
  maintainer ratifies retroactively.
- **Companion docs**: [ADR 0011](0011-phase0-interaction-engine-bootstrap.md),
  [ADR 0013](0013-computational-notebook-authored-layer.md),
  [`docs/RELAUNCH-SPEC.md`](../RELAUNCH-SPEC.md) §1.1 (the
  narration quality bar), `Terminal.Core.Config` (the TOML
  precedent this follows).

## Context

Launch readiness for a keyboard-only, audio-only instrument
means three things the Phase 0 build lacks: (1) the user must
be able to **tune the instrument** — voice, rate, verbosity,
cue gains, key bindings — without recompiling; (2) the key
surface must be **one declarative table**, not a hand-matched
switch, so help text, documentation, conflict detection, and
user overrides all derive from a single source of truth; (3)
when something goes wrong in the field, the maintainer needs
**diagnostics the user can produce by ear** — a spoken summary
plus a dump file — because the user cannot read a console.

## Decisions

### C1 — `engine.toml`: the engine's own config file

`EngineConfig` (pure parse, `Engine.Core`; Tomlyn via the
established non-throwing `Toml.Parse → HasErrors → ToModel`
pattern) loads `%LOCALAPPDATA%\PtySpeak\engine.toml`:

```toml
schema_version = 1

[participant]
claude_executable = "C:\\path\\to\\claude.exe"  # else ENGINE_CLAUDE_PATH, else claude.cmd

[speech]
rate = 2          # SAPI -10..10, clamped
voice = "Microsoft Zira Desktop"  # substring match, optional

[narration]
move_read_cap_chars = 600   # navigation read bound

[cues]
enabled = true
gain = 1.0        # master multiplier 0.0..1.0 over per-cue gains

[keys]
# verb = "x"  — single-character overrides, validated for conflicts
pin = "p"
```

Every malformed value degrades to its default **with a typed
warning** the host speaks once at startup ("2 configuration
warnings; press d for details") and records in diagnostics —
never a crash, never silence (ADR 0008 honesty applied to
config). Missing file = pure defaults, no warning.

### C2 — The declarative keymap

`KeyMap` (pure, `Engine.Core`): every verb is a case of a
`Verb` union; the default bindings are one table of
`{ Verb; Key; Mode; Description }`. The host's key loop is a
table lookup, not a match ladder. Consequences, all tested:

- **No conflicts by construction**: a validator rejects
  duplicate keys per mode (defaults are asserted
  conflict-free in CI; user overrides that collide produce a
  warning and keep the default).
- **Help is generated** from the table — `?` can never drift
  from reality, and the keyboard-reference doc is checked
  against the same table's content.
- **User remapping** via `[keys]` in `engine.toml` (single
  printable character per verb; arrows stay hardwired to the
  four tree moves as the universal fallback).

### C3 — Speech controls on the sink seam

`ISpeechSink` gains `SetRate(int)` (clamped −10…+10) and
construction-time voice selection (case-insensitive substring
match against installed voices; no match = default voice + a
spoken note). Live keys: rate up / rate down step ±1 and
confirm ("Rate 3."). Rationale: for a power listener, rate is
the single highest-leverage tuning knob (§1.1); it must be a
keystroke, not a config edit.

### C4 — Diagnostics: the ear-first triage path

- `EngineDiagnostics` (`Engine.Core`): a thread-safe bounded
  ring (default 500 entries) recording every bus event,
  every turn outcome, config warnings, and host errors, with
  per-category counts.
- The diagnostics verb speaks a **summary** (uptime, turns,
  chunks sealed, notes/errors count, last error if any) and
  writes the **full dump** to a timestamped file under
  `%LOCALAPPDATA%\PtySpeak\engine-diagnostics\`, then speaks
  the file path — the exact bundle a bug report needs,
  produced without sight, in two keystrokes.
- The host also appends a **session event log** (one line per
  bus event) next to the session files, so post-hoc triage can
  replay what the user heard.

## Consequences

- Every behavioural constant a user might reasonably want to
  change now has a config key, a default, and a documented
  degradation path; `docs/engine/CONFIGURATION.md` is the
  reference.
- Adding a verb = one union case + one table row + one handler
  — the development guide's worked example.
- The diagnostics dump becomes the standard first ask in any
  field issue (mirroring the WPF app's `Ctrl+Shift+D`
  discipline, rebuilt ear-first).

## Status notes

- 2026-06-12: authored; implementation lands in this cycle
  (config → diagnostics → keymap → speech controls → host
  integration), each its own PR.
- 2026-06-12 (same session): **Implemented & CI-green** —
  #463 engine.toml + diagnostics ring · #465 keymap · #467
  `SetRate` + voice selection · #468 host integration · #471
  exploration verbs (find / structure summary / digit
  direct-address / hardwired Escape stop) on the same table.
  One CI failure in the whole cycle (#471 first run): a test
  fixture rebinding onto the newly-taken `z` — the conflict
  checker working as designed; fixture moved to a free key.
  The extended `P0-ENGINE-1` walk (step 12) is the
  ratification gate.
