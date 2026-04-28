# ConPTY platform notes

Quirks of the Windows Pseudo-Console API (ConPTY) that pty-speak has
hit in practice during Stage 1+ and the workarounds we settled on.
This file complements [`spec/overview.md`](../spec/overview.md)
(external research summary) with what we actually observed running
against `windows-2025` GitHub-hosted runners.

## Render cadence: fast-exit children lose output

**Observation.** Spawning `cmd.exe /c echo MARKER` under ConPTY and
reading the parent's output pipe consistently captures only the
16-byte ConPTY init prologue (`\x1b[?9001h\x1b[?1004h`) — not the
echoed `MARKER`. Even chaining a 1.5s `ping -n 2 127.0.0.1 > nul`
after the echo doesn't help; the captured payload stays at 16 bytes.
By contrast, spawning interactive `cmd.exe` (no `/c`, no `/K`
arguments) and waiting a few seconds reliably produces ~130 bytes of
ANSI output (cmd's startup mode setup, alt-screen entry, title via
OSC 0).

**Mechanism.** ConPTY hosts an in-memory `conhost` that maintains a
character grid and renders changes to the parent's read pipe on a
**timer-driven cadence** (~16–32 ms between renders), not a
write-driven one. When a child like `cmd.exe /c echo MARKER` writes
"MARKER\r\n" to its conhost-managed stdout, conhost queues a render.
If the child exits before the next render tick fires *with cmd's
output as the dominant payload*, the rendered output is lost — the
HPCON tears down without flushing. cmd's startup ANSI (cursor mode,
clear, title) does survive even fast exits, because those are
emitted by conhost as escape passthrough, not screen rendering.

This matches the warning in
[`spec/overview.md`](../spec/overview.md):

> There is **lag between input and output** from conpty's
> double-buffered render cadence.

**Implications.**

- **Don't use fast-exit `cmd.exe /c <command>` for round-trip
  tests.** Output is non-deterministic.
- **Use interactive `cmd.exe`** (long-lived) and drive it via stdin
  for round-trip integration tests. Stage 6's keyboard-to-PTY
  pipeline is the right place for that.
- **The 16-byte init prologue is reliable**: every `ConPtyHost.start`
  call produces it before any child output. Tests that just need to
  prove the read pipeline is alive can assert on capturing ≥16 bytes
  (this is what `ConPtyHostTests` does today).

**Not yet investigated.** Whether the render cadence is configurable,
whether explicit `ResizePseudoConsole` triggers a flush, or whether
DCS passthrough behaves differently. Stage 2's parser landed without
surfacing additional render-vs-passthrough distinctions; this section
will be updated if Stage 5+'s streaming work reveals new corner cases.

## Other open ConPTY items (forward-look)

These are documented as forward-looking concerns from
[`spec/overview.md`](../spec/overview.md); they have not yet been
encountered in code. Expected resolution stage in parentheses.

- **Job Object lifecycle deferred from Stage 1.** `PseudoConsole.create`
  spawns the child without wrapping it in a Windows Job Object;
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` was deferred so that Stage 1
  could land the minimum-viable ConPTY chain. Until added, a host
  crash leaves the child running. The threat-model document
  ([`SECURITY.md`](../SECURITY.md)) calls out the same gap. Owner
  stage TBD — likely Stage 6 alongside the input pipeline, since
  that's the first stage where stale child processes become
  user-visible.
- **No DCS passthrough by default** (issue
  [microsoft/terminal#17313](https://github.com/microsoft/terminal/issues/17313)).
  Sixel and kitty graphics will be approximated. (Stage 3+ if we
  ever care.)
- **Weak mouse handling.** WezTerm and Yazi document ConPTY as a
  bottleneck. (Stage 6 when keyboard/mouse input lands.)
- **Wide-character width tables evolve with conhost.** Grapheme
  clusters and combining marks may render approximately. (Stage 3,
  cell measurement.)
- **`CREATE_NEW_PROCESS_GROUP` blocks Ctrl+C delivery to the child**
  (Microsoft Q&A,
  [link](https://learn.microsoft.com/en-ca/answers/questions/5832200/c-sent-to-the-stdin-of-a-program-running-under-a-p)).
  Already handled: `PseudoConsole.create` deliberately omits this
  flag. Documented inline.
- **Microsoft's deadlock warning** for servicing input + output +
  process-exit on a single thread.
  Already handled: `ConPtyHost` uses a dedicated reader Task plus a
  separate stdin `FileStream`; process-exit is observed via
  `WaitForSingleObject`.
