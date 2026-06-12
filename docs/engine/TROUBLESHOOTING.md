# Engine troubleshooting

> Everything here is diagnosable by ear. The universal first
> step is `d`: the spoken summary names the last error
> verbatim, and the dump file it writes (path spoken) plus the
> session event log is a complete bug report.

## Launch problems

**Nothing speaks at launch.**
- The console mirror still prints — if text appears but no
  audio: check the Windows default output device, and that
  another exclusive-mode app isn't holding it.
- SAPI failures are swallowed by design (the engine never
  crashes for audio); the console mirror is the fallback
  evidence.

**"Could not run the participant…" after composing.**
- The Claude CLI isn't found. Resolution order: `engine.toml`
  `[participant] claude_executable` → `ENGINE_CLAUDE_PATH` env
  var → `claude.cmd` on PATH. From cmd, `claude --version`
  must answer in the same environment you launch from.
- Note `setx` only affects *new* command prompts.

**"Participant exited with code N."**
- The CLI ran but failed. Common causes: not authenticated
  (run `claude` interactively once), no network, or a flag
  mismatch from a very old CLI. The dump file carries the
  stderr tail.

## Mid-session

**"N configuration warnings; press d for details."**
- `engine.toml` has a malformed value. Nothing is broken —
  defaults are in effect. `d` reads the exact keys; fix at
  leisure. [`CONFIGURATION.md`](CONFIGURATION.md) has the
  per-key rules.

**Ambient notes: "Unrecognized stream event type: X."**
- The installed CLI emits a stream-json shape this build
  doesn't know. Content still flows (unknown shapes are
  surfaced, never fatal). Report the spoken X — extending the
  parser is a one-arm change (see the development guide).

**A turn completes with 0 chunks.**
- The reply had no markdown content the chunker recognizes, or
  arrived entirely as unknown blocks (the notes will say).
  `r` on the request + `y` to rerun is the quick retry.

**Speech went quiet but ticks continue.**
- A long utterance may have been cancelled by your own
  navigation (by design: your keys preempt). `r` re-reads the
  focused chunk. If speech never returns, quit and relaunch —
  and send the dump.

## Sessions and files

| What | Where |
|---|---|
| Sessions (auto + `v`) | `%LOCALAPPDATA%\PtySpeak\engine-sessions\` |
| Notebook exports (`m`) | same folder, `.md` |
| Diagnostics dumps (`d`) | `%LOCALAPPDATA%\PtySpeak\engine-diagnostics\` |
| Config | `%LOCALAPPDATA%\PtySpeak\engine.toml` |

**"Could not open the last session…"**
- The file failed validation (the spoken note carries the
  typed reason: schema, malformed line, structural). Sessions
  are append-only snapshots — an older file in the folder will
  still open; nothing deletes them.

**Restored session won't continue the conversation.**
- Continuity rides the CLI's own session id (`--resume`). If
  the CLI's session store was cleared, the engine still has
  the full tree — compose continues with fresh CLI context.

## Reporting a bug

1. `d` — note the spoken summary, grab the dump file.
2. Include: the dump, the session `.jsonl`, the event `.log`
   next to it, and what you pressed last.
3. The console mirror text (if a sighted collaborator is
   present) is gravy, not required — the dump contains the
   same trail.
