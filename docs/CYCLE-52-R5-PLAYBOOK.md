# Cycle 52 R5–R7 — PowerShell adapter playbook (START HERE)

> **Status**: PROPOSED / IN-FLIGHT. The R1–R4 + R4c foundation
> is shipped & CI-green on `main` (`b14667f`, PRs #347–#372,
> 2026-05-15 → 2026-05-16). R5 (PowerShell adapter) is the
> next push, **gated on the maintainer's R1–R4+R4c foundation
> dogfood sign-off**.
>
> **NEW / RECOVERED SESSION: this file is your start-here.**
> It is written to be self-contained: if the originating chat
> session is lost, reading this doc + the files it points at
> puts you exactly where the prior session was. Then read, in
> order: this file → [`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md)
> → [`adr/0005-osc133-shell-integration.md`](adr/0005-osc133-shell-integration.md)
> → [`SESSION-HANDOFF.md`](SESSION-HANDOFF.md) → the R5 file
> anchors in §4 below.

This playbook is the Cycle-52 continuation doc for stages
**R5 (PowerShell adapter) → R6 (feature unlock) → R7 (claude
adapter + closure)**. ADR 0006 owns the canonical R0–R7 stage
list; this doc is the *executable* brief + the meticulously-
scoped pruning sequence that precedes R5.

## 1. Where we are (the proven foundation)

`main` HEAD = `b14667f`. Cycle 52 R1–R4 + R4c shipped:

- **R1** — extracted the three-layer seam (`Terminal.Shell`
  transport / `Terminal.Core` pure session / accessibility
  channel) + `SessionHost` orchestration point. Behaviour-
  identical, maintainer-dogfood-validated.
- **R2** — cmd OSC-133 prompt injection (Option B, command-
  line `cmd.exe /K prompt …`), adapter-owned.
- **R3a/b** — OSC precedence latch + announce-compensation
  deletion (KI-R2-1 structurally fixed).
- **R3c/d/e** — spoken-watermark announce model; multi-
  interrupt watermark composition; single-key sub-prompt
  re-arm. All maintainer-validated.
- **R4a/b** — heuristic-detector namespace purify +
  `portability-lint` *structurally enforces* the
  Core↔Shell boundary.
- **R4c** — cmd emits a **boundary-only deferred `;D`**
  (`CommandFinished None`; no exit code — cmd has no native
  `%errorlevel%`-in-prompt mechanism, an OS-level limit, not
  ours). Completes the cmd OSC-133 event stream so R5/R6 are
  genuinely shell-agnostic in the core.

**Proven by the post-`5518f5c` foundation bundle (2026-05-16):**
the IOCell substrate is sound — `extractIOCell` produced a
correct `CmdLen`/`OutLen` split on *every* command (plain,
`dir`, test-02, test-09's seven segments); ContentHistory
markers + `RecentTuple` are clean. **Every remaining cmd
defect is in the racy announce-reconstruction layer, not the
substrate.**

## 2. Standing decisions that constrain R5 (do not relitigate)

1. **cmd announce-heuristic FREEZE** (maintainer, 2026-05-16,
   recorded in ADR 0006 §"Deferred to R6+" decision note +
   SESSION-HANDOFF). Do **not** push further cmd announce-
   heuristic patches before R5. The substrate is fine; the
   announce layer is rebuilt to PowerShell's clean reference
   afterward. Continuing to patch cmd announce heuristics now
   is the documented tail-chasing loop — stay out of it.
   cmd *substrate* work (R4c-class) is still fine.
2. **No exit code on cmd `;D`** — OS-level cmd limitation
   (would need clink / a doskey wrapper, both rejected).
   `ShellEvent.CommandFinished of int option` was designed
   for the asymmetry: `None` (cmd) / `Some <code>`
   (PowerShell R5, via `$LASTEXITCODE`).
3. **cmd emits no pre-prompt banner** — confirmed (bundle:
   zero `R3c banner announce` for cmd; PowerShell's fires
   fully). "Terminal edit Blank" on fresh cmd launch is the
   empty-document focus fallback, **architectural, not a
   bug**. The banner mechanism works (PowerShell proves it).
4. **MAUI out of scope** (maintainer 2026-05-15). F# + WPF
   kept.
5. **Deferrals** are tracked with rationale in ADR 0006
   §"Deferred to R6+" (the `stripNextPrompt`/region-cut
   retirement, SpeechCursor→IOCell-history navigation + per-
   cell ops, review-cursor-document decision, NVDA current-
   line propagation, multi-interrupt IOCell structure,
   fast-type/recall command-echo race). Do not duplicate
   that list elsewhere; cross-reference it.

## 3. The validation gate (maintainer's court — blocks R5 start)

One consolidated **R1–R4 + R4c foundation dogfood** on an
installed preview of post-`b14667f` `main`. NVDA matrix rows
`52-1`, `52-R3c`, `52-R3c-multi`, `52-R3d`, `52-R3e`,
`52-R4c` in [`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).
Run from an **installed preview**, not a dev `dotnet run`
(dev-host NVDA UIA delivery is unreliable on the VM; restart
NVDA if the notification wedge bites). R5 implementation does
**not** start until the maintainer signs this off.

## 4. R5 scope — PowerShell adapter (file-anchored)

**Correction to first-glance assumptions:** the
`IShellAdapter` interface *exists* (`src/Terminal.Shell/ShellAdapter.fs`:
`SpawnConfig`, `RunningShell`, `SpawnError`, `IShellAdapter`
with `Spawn` / `Translate: byte[] -> ShellEvent list` /
`WriteInput`) **but is deliberately not wired**. The reader
loop takes a *concrete* `CmdAdapter`
([`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs):112
`(adapter: Terminal.Shell.CmdAdapter)`; :915
`let cmdAdapter = Terminal.Shell.CmdAdapter()`), and
`CmdAdapter.Translate` returns `VtEvent[]`, **not** the
interface's `ShellEvent list` (CmdAdapter.fs:19 — "`ShellEvent`
/ `IShellAdapter` is deliberately NOT used at R1"). So R5 is
**not** a clean drop-in against a live interface — it includes
a real seam-wiring step. Sequenced (mirror the R1 "extract
the seam, zero behaviour change" discipline):

- **R5a — wire the adapter-selection seam, behaviour-
  identical.** Make `CmdAdapter` implement `IShellAdapter`
  (or introduce a thin selection layer), change
  `startReaderLoop` + `SessionHost`/`switchToShell` to pick
  the adapter by `ShellRegistry.ShellId`. **No PowerShell
  yet.** cmd path byte-identical; CI + a quick dogfood gate
  it (R1 precedent). This de-risks R5b.
- **R5b — `PowerShellAdapter`.** New
  `src/Terminal.Shell/PowerShellAdapter.fs` mirroring
  `CmdAdapter` (owns one `Parser.create()`; the parser is
  NOT reset on shell-switch). Add a
  `PowerShellAdapter.IntegrateOsc133`-equivalent. Injection
  *mechanism* per ADR 0005 §3: a generated `prompt` function
  emitting OSC-133, injected via `powershell.exe -NoExit
  -Command "<prompt fn + init>"` (preferred — self-
  contained, no temp-file cleanup) or a generated profile.
  Gate the injection at the **two existing sites**:
  `SessionHost.ResolveStartupShell`
  ([`src/Terminal.Shell/SessionHost.fs`](../src/Terminal.Shell/SessionHost.fs):126-135,
  the `if resolvedShell.Id = ShellRegistry.Cmd` block — add a
  `PowerShell` arm) and the shell-switch path in `Program.fs`
  (`switchToShell`, near :4642
  `CmdAdapter.IntegrateOsc133`, the
  "R2 cmd OSC-133 prompt injection applied (shell-switch)"
  log). `ShellRegistry` already has the `PowerShell` entry
  (PR-J; `Resolve = fun () -> Ok "powershell.exe"`;
  `parseEnvVar` accepts `powershell`/`pwsh`) — **no registry
  work needed**.
- **R5c — exit code + `;C`.** Emit `;D;$LASTEXITCODE` (real
  exit code — the asymmetry cmd lacks) and, if reachable, a
  real `;C` OutputStart. A real `;C` routes extraction
  through the **clean `CleanOscAC` arm**
  ([`src/Terminal.Core/SessionModel.fs`](../src/Terminal.Core/SessionModel.fs):857-875,
  `Some promptStart, Some outputStart, _` →
  `[promptStart.Seq, outputStart.Seq)` cmd / `[outputStart.Seq,
  MaxValue)` out — **no watermark, no string-strip**). The
  consumer side is already done: `Osc133.tryParse`
  ([`src/Terminal.Core/Osc133.fs`](../src/Terminal.Core/Osc133.fs):80-114)
  parses `;C` and `;D;<int>`; `BoundaryKind` carries the
  code.
- **R5d — NVDA matrix `52-R5` rows + closure.** Pin the
  exact PowerShell command-line shape in a new
  `tests/Tests.Unit/PowerShellAdapterTests.fs` (mirror
  `CmdAdapterTests.fs` — the string is locally unverifiable;
  the pin fails CI loudly before a release+dogfood is spent).

**THE #1 R5 RISK — resolve empirically, do not assume:**
PowerShell **auto-disables PSReadLine when it detects a
screen reader** ("Warning: PowerShell detected that you might
be using a screen reader and has disabled PSReadLine for
compatibility purposes." — seen verbatim in the 2026-05-16
bundle). Consequences: any `PSConsoleHostReadLine`-based
OSC-133 hook will **not run**; only a `prompt` function
fires. A `prompt` function can emit `;A`/`;B`/`;D;$LASTEXITCODE`
(it runs post-exec, so `$LASTEXITCODE` is fresh — emit `;D`
*immediately*, not deferred like cmd) but **cannot emit
`;C`** (no Enter→exec hook, same limit as cmd). So the
realistic R5 target is **`;A`/`;B`/`;D;<code>` → the
`CmdOscAB`-equivalent arm**, *not* the clean `CleanOscAC`
arm. That is still a strict improvement over cmd (real exit
code; immediate not-deferred `;D`), but it means the clean-
`;C` reference may have to wait for a shell with a real
OutputStart hook. **R5b must be defensive + test-driven for
the no-`;C` fallback; the R5 dogfood (NVDA active) decides
whether `;C` is ever reachable.** This question is the R5
go/no-go on "clean reference" — surface it to the maintainer
early, with the bundle evidence, before committing to a
`;C`-dependent design.

Open R5 sub-questions (answer in R5b design, confirm in
dogfood): immediate vs deferred `;D` (PowerShell `prompt` is
post-exec → immediate; first prompt has no prior code →
synthesise `0` or omit); `-Command` vs generated `$PROFILE`
(start with `-Command`; switch to profile only if the init
string hits a length limit); cross-shell hot-switch history
continuity (cmd↔PS — `SessionHost` finalises the prior
shell's in-flight cell + fresh `SessionModel`; the R5 dogfood
must exercise `Ctrl+Shift+1`→cmd / `+2`→PS / `+1`→cmd).

## 5. Pre-R5 pruning sequence (old-code remnants — scoped)

These are **separate one-concern PRs**, each locally
unverifiable (no `dotnet` in the sandbox → CI is the only
build signal; read each file twice before push). They are
*substrate / dead-code* cleanups (the FREEZE is on *announce
heuristics*, not these). Recommended order — least-risk
first; none block R5a but all should land before R5b so the
PowerShell adapter is built against a pruned tree:

- **P1 — heuristic-detector detection-time gate (prune-with-
  care).** `HeuristicPromptDetector` runs full-tilt for cmd
  even when OSC-133 is authoritative; it is only muted at
  *emit* time
  ([`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs)
  `runDetector` ~:2482-2530; `tryDetect` in
  [`src/Terminal.Shell/HeuristicPromptDetector.fs`](../src/Terminal.Shell/HeuristicPromptDetector.fs)).
  The 2026-05-16 bundle showed hundreds of
  `HeuristicPromptDetector SUPPRESSED` / `R3a: muted
  heuristic boundary` lines — wasted per-chunk regex + map
  work + log spam. Fix: early-exit `runDetector` when
  `oscSeenThisSession` is set **for OSC-emitting shells**;
  keep it fully live for **claude** (no OSC-133 — load-
  bearing). Gate, don't delete.
- **P2 — Cycle-47 synthetic-CommandFinished shell-guard
  (prune-with-care).** The synthetic-`CommandFinished`-
  before-`;A` compensation
  ([`Program.fs`](../src/Terminal.App/Program.fs) ~:2280-2287,
  `if finalisedOpt.IsSome && k = …PromptStart`) is now dead
  for cmd post-R4c (cmd finalises via the real `;D`
  `CommandFinished` arm) but **still load-bearing for
  claude**. Guard the block on the shell being heuristic-
  only (not cmd); do not delete. (May fold into P1 — same
  "heuristic-only shells" predicate.)
- **P3 — dead pathway config keys (safe-to-delete).** The
  `[pathway.<id>]` TOML schema + `pathway` key parsing in
  [`src/Terminal.Core/Config.fs`](../src/Terminal.Core/Config.fs)
  are parsed but unconsumed since Cycle 45c deleted the
  pathway pipeline. Remove the parsing + deprecate the
  schema with a one-line comment. Also drop the dead
  `#[pathway.stream]` / `#substrate_mode` lines in the
  generated `config.toml` template.
- **P4 — canonical-corpus ESC-byte fix (bug).**
  [`tests/fixtures/canonical-interactions.toml`](../tests/fixtures/canonical-interactions.toml):146
  (`command = "echo <ESC>[31mPtySpeakDiagRedText<ESC>[0m"`)
  contains **raw ESC (0x1B) bytes** → TOML 1.0.0 rejects
  (`Invalid control character \u1B`), so the entire
  canonical-corpus diagnostic path **silently fails to
  load** (`Ctrl+Shift+D` skips corpus validation with only a
  `[WRN]`). Re-encode the ESC as the escape the loader
  expects (confirm against the corpus loader — likely
  ``); verify no other raw control bytes in the file
  (`grep -anP '\x1b' tests/fixtures/canonical-interactions.toml`).
  This is a real loss of test coverage; fix before R5b so
  PowerShell corpus scenarios load.
- **P5 — comment-rot sweep (docs-class, low-risk).** Stale
  references to deleted concepts in *comments only*:
  `StreamPathway`/`TuiPathway`/`DisplayPathway`/
  `LinearTextStream`/`SessionTuple`(old IOCell name)/
  `selectPathwayForShell` across `Program.fs`,
  `Config.fs`, `ContentHistory.fs`, `CanonicalState.fs`,
  `Diagnostics.fs`. No runtime impact; fold into whichever
  PR touches each file, or one dedicated sweep. The
  `pathwayPump` *naming* stays for now (the pump is load-
  bearing notification routing; only the comments describing
  the deleted pathway layer are wrong).

**Vestigial screen-row plumbing** in `SessionModel.fs`
(`oldPromptRowIndex`/`newPromptRowIndex`/`snapshot: Cell[][]`,
marked "vestigial … PR-X removes") is **deferred, not P-
sequenced** — it is inert (post-pivot extraction is
ContentHistory-only) and removing it is a wide-surface change
better done with R6's extraction rework. Note it; don't prune
pre-R5.

## 6. Operational playbook (battle-tested this cycle — reuse)

- **No `dotnet`/cmd/PowerShell in the dev sandbox.** CI
  (windows-latest) is the only build signal. Read each F#
  file twice before push; a missed `let rec`, an OR-pattern
  across a line break (FS0010/FS0583 — bit us in #369), or a
  pinned-string drift is a multi-minute round-trip.
- **The assigned dev branch is reused across PRs** and its
  remote tip is the *prior PR's pre-squash commit* (squash-
  merge rewrites history). Every push after the first hits
  "rejected — non-fast-forward". The safe, proven dance:
  `git checkout main && git pull` → `git checkout -B
  <assigned-branch>` → make changes → commit → `git fetch
  origin <branch>` → **verify content-identical**:
  `git diff --stat main origin/<branch>` (empty == prior PR
  fully squash-merged, force loses nothing) → `git push
  --force-with-lease`. Never blind-force; always verify the
  empty tree-diff first.
- **Docs-only PRs use the fast-merge lane**: strictly `.md`
  (+ `LICENSE`) → merge once **Markdown link check** is
  green; skip the slow Windows build/test. Verify scope with
  the PR file list first. A `.fs`/`.fsproj`/workflow touch
  forfeits the lane.
- **CI poll cadence: every 60s** via `Bash sleep 60`
  (`run_in_background: true`) then
  `mcp__github__pull_request_read method=get_check_runs`.
  Webhooks are unreliable in this maintainer's env — poll;
  treat webhook events as a free early-exit, never depend on
  them. Don't poll faster; don't sleep longer.
- **CI failure → ask for the log slice, never speculative-
  fix.** The sandbox can't fetch CI logs (blob storage +
  api.github.com 403). Tell the maintainer the exact strings
  to search (`error FS`, `error MSB`, `Build FAILED`,
  `[FAIL]`) + request ~8-10 surrounding lines. One wrong
  guess = a wasted multi-minute round-trip.
- **`AskUserQuestion` only when genuinely blocked** (a real
  design decision; a CI-log request when the maintainer may
  be away; MCP-disconnect manual-PR fallback). It is the
  phone-notification surface — do not buzz it for status.
  Plain text for status/progress.
- **Standing merge authorization**: green CI (or green link
  check for docs-only) → squash-merge without re-asking →
  `git checkout main && git pull` → recreate the dev branch
  for the next PR. Keep CI-completion replies terse ("all
  green, merging").
- **Maintainer is blind / screen-reader (NVDA).** Never
  propose GUI-dialog-tree walks; give keyboard/CLI
  equivalents. Never use "blind/see/look/view" as a
  metaphor — state the literal condition ("no local
  compiler — CI is the only signal"; "the bundle shows").
- **Diagnostic bundles**: ask via the in-app menu
  (`Diagnostics → Grep diagnostics` / `Copy Latest Bundle`)
  for paste-safe slices; the maintainer reads `Ctrl+Shift+H`
  Version line first to rule out a stale local build before
  any "regression" chase (a local build always reports
  `0.0.1-preview.1`; the `+<git-short-sha>` is the only
  build-identity signal — match it to `git rev-parse --short
  HEAD`).
- **Tag pushes 403** from the sandbox. Checkpoint tags are
  staged in [`CHECKPOINTS.md`](CHECKPOINTS.md) for the
  maintainer to sweep from their workstation.

## 7. Reading order for a fresh / recovered session

1. **This file** (you are here) — state, decisions, R5
   scope, pruning sequence, ops.
2. [`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md)
   — R0–R7 canonical stage list + §"Deferred to R6+"
   (the deferral backlog + the cmd-announce-FREEZE decision
   note + the both-edges reconstruction analysis).
3. [`adr/0005-osc133-shell-integration.md`](adr/0005-osc133-shell-integration.md)
   — OSC-133 *mechanism* spec; §3 PowerShell bullet + §3 R4c
   status note.
4. [`SESSION-HANDOFF.md`](SESSION-HANDOFF.md) — current
   state + the FREEZE banner (kept terse; this playbook is
   the deep brief).
5. R5 code anchors (§4 above):
   `src/Terminal.Shell/ShellAdapter.fs` (the unwired
   interface), `CmdAdapter.fs` (the template),
   `SessionHost.fs`:126-135 (injection gate),
   `Program.fs`:112/915/~4642 (concrete-adapter wiring +
   shell-switch gate), `SessionModel.fs`:857-917
   (`extractIOCell` arms), `Osc133.fs`:80-114 (the
   consumer, already done).
6. [`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md)
   — the `52-*` matrix rows = the foundation-dogfood gate
   that blocks R5 start.

When R5–R7 ship, mark this doc HISTORICAL and move it to
[`archive/cycle-closed/`](archive/cycle-closed/) per the
cycle-closure discipline (the closure-audit PR does this).
