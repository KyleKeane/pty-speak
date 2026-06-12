# Engine configuration reference — engine.toml

> Location: `%LOCALAPPDATA%\PtySpeak\engine.toml`. The file is
> optional — every key has a default, and **no value in this
> file can break the engine**: a malformed file or value
> degrades to the default with a warning the engine speaks
> once at startup ("N configuration warnings; press d for
> details") and records in diagnostics. Unknown sections and
> keys are ignored silently (forward compatibility). Parser:
> `src/Engine.Core/EngineConfig.fs`.

## Schema version

```toml
schema_version = 1
```

Optional. A file declaring a *newer* schema than the build
supports is rejected whole (defaults + warning) — an old build
never half-reads a future format.

## [participant]

| Key | Type | Default | Meaning |
|---|---|---|---|
| `claude_executable` | string | *(unset)* | Full path to the Claude Code CLI. Resolution order: this key → the `ENGINE_CLAUDE_PATH` environment variable → `claude.cmd` on PATH. |

```toml
[participant]
claude_executable = "C:\\Users\\me\\AppData\\Local\\Programs\\claude\\claude.exe"
```

## [speech]

| Key | Type | Default | Meaning |
|---|---|---|---|
| `rate` | integer | `0` | SAPI speaking rate, −10…+10. Out-of-range clamps with a warning. Live keys `+`/`-` adjust from this starting point (session-scoped). |
| `voice` | string | *(system default)* | Case-insensitive substring matched against installed voice names ("Zira" matches "Microsoft Zira Desktop"). No match = default voice + a spoken note. |

## [narration]

| Key | Type | Default | Meaning |
|---|---|---|---|
| `move_read_cap_chars` | integer | `600` | How much of a chunk a *navigation* read speaks before the honest truncation marker ("Truncated; N more characters — press r to hear all."). Floor 50. `r` always reads everything. |

## [cues]

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `true` | Master switch for the spatial stage (all event + navigation tones). Speech is unaffected. |
| `gain` | float | `1.0` | Master multiplier over every cue's own gain, 0.0–1.0 (integers accepted; out-of-range clamps). |

## [keys]

Single-character verb rebindings; see
[`KEYBOARD-REFERENCE.md`](KEYBOARD-REFERENCE.md) § Rebinding
for the verb names and the conflict rules.

```toml
[keys]
pin = "z"
```

## A complete example

```toml
schema_version = 1

[participant]
claude_executable = "C:\\tools\\claude.exe"

[speech]
rate = 4
voice = "Zira"

[narration]
move_read_cap_chars = 400

[cues]
enabled = true
gain = 0.7

[keys]
pin = "z"
rerun = "Y"
```

## Degradation behaviour (the contract)

| You write | The engine does |
|---|---|
| Nothing (no file) | Pure defaults, silent. |
| Valid TOML, valid values | Exactly what you said. |
| Valid TOML, wrong type (`rate = "fast"`) | Default + one warning naming the key. |
| Valid TOML, out of range (`rate = 40`) | Clamped + one warning with both numbers. |
| Malformed TOML | All defaults + one warning with the parser detail. |
| `schema_version = 99` | All defaults + one warning. |
| Unknown key / section | Ignored, no warning. |

Warnings are spoken once at startup as a count; `d`
(diagnostics) reads them in full and they are in the dump
file.
