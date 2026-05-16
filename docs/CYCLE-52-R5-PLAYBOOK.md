# Cycle 52 R5–R7 — PowerShell adapter playbook (START HERE)

> **Status**: **R5 COMPLETE & VALIDATED (2026-05-16,
> #369–#375).** R1–R4+R4c foundation shipped & dogfood-
> signed-off; R5a (selection seam) + R5b (PowerShell
> adapter) shipped & CI-green; the R5b NVDA dogfood passed
> and **resolved the #1 R5 risk positively** (OSC-133 emits
> under a screen reader via the `prompt` function despite
> PSReadLine being auto-disabled; `;C` confirmed unreachable
> for the screen-reader use case = the final design, not a
> gap — see §4 R5c/R5d). **Next = R6 (feature unlock) and/or
> the P1–P5 pre-R5 pruning** (independent; the cmd
> announce-heuristic FREEZE still stands). This file stays
> the start-here for R6 and recovery.
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

- **R5a — adapter-selection seam, behaviour-identical
  (DONE — maintainer chose "literal R5a seam first"
  2026-05-16; recon-corrected scope).** Recon finding: the
  transport is already shell-agnostic *except* the OSC-133
  injection (already `ShellId`-gated at two sites);
  `CmdAdapter.Translate` is a verbatim `Parser.feedArray`
  wrapper; the full `IShellAdapter` (Spawn/WriteInput) is a
  big **non**-behaviour-identical refactor because spawn is
  `ConPtyHost.start`-direct and input `host.WriteBytes`-
  direct. So R5a is the **thin selection layer**, NOT full
  `IShellAdapter` adoption: a single
  `SessionHost.Osc133IntegratorFor : ShellId -> (string ->
  string)` selector (cmd → `CmdAdapter.IntegrateOsc133`;
  else → `id`) replacing the two inline `if = Cmd` gates
  (`SessionHost.ResolveStartupShell` + `Program.fs`
  `switchToShell`). **Byte-identical incl. logs** (the
  cmd-only "R2 cmd OSC-133 … applied" line still fires
  exactly when cmd; the single shared VT parser is NOT
  reset across shell switches — unchanged; spawn/input
  untouched). Pinned by `CmdAdapterTests`
  (`Osc133IntegratorFor` dispatch). **R5b adds the
  PowerShell arm in that ONE selector.** The full
  `IShellAdapter` (Spawn/WriteInput/`ShellEvent`-Translate)
  is the **R6** target, not R5 — the "Full IShellAdapter
  now" option was explicitly rejected (large, non-
  behaviour-identical, R6-class).
- **R5b — `PowerShellAdapter` (DONE — dogfood-pending).**
  New `src/Terminal.Shell/PowerShellAdapter.fs` (statics
  only — no parser/instance; Translate stays the shared
  shell-agnostic path per R5a, spawn/input ConPtyHost-
  direct; R6 broadens). `Osc133InitScript` = a WinPS-5.1
  `prompt` function (F# **triple-quoted** → zero escaping
  risk) emitting `;D;$LASTEXITCODE` (real exit code — the
  asymmetry cmd lacks) → `;A` → `$($PWD.Path)>` → `;B`;
  same `;A`/`;B` framing as cmd ⇒ **same `CmdOscAB`
  consumer arm, zero consumer change**; `Osc133.tryParse`
  already decodes `;D;<int>`; the leading `;D;0` is handled
  by the existing `None,CommandFinished` ignore + R4c
  stray-gate exactly like cmd. `IntegrateOsc133` =
  `powershell.exe -NoExit -EncodedCommand <base64-UTF16LE
  of the script>` — chosen over `-Command "…"` because the
  base64 alphabet is space/quote/metacharacter-free, giving
  the **same quoting-safe property cmd's space-free value
  has** (the script can't be space-free, so encode it
  instead of fighting CreateProcess+PowerShell quoting).
  Wired via the **R5a selector**: one `PowerShell` arm in
  `SessionHost.Osc133IntegratorFor` (no second gate site —
  R5a centralised it) + a distinct `R5b PowerShell OSC-133
  prompt injection applied (startup|shell-switch)` log at
  the two log sites (cmd's lines unchanged + greppable).
  `ShellRegistry` PowerShell entry already exists (PR-J) —
  no registry work. Pinned by `PowerShellAdapterTests`
  (script + base64 round-trip) + the flipped
  `CmdAdapterTests` selector pin. **Locally unverifiable**
  (no PowerShell in sandbox; cmd/R2 precedent): the script
  is the dogfood-tunable knob; if the visible prompt or
  emission needs adjusting it is a contained
  `Osc133InitScript` one-liner.
- **R5c — exit code + `;C` (RESOLVED by the R5b dogfood,
  2026-05-16).** The exit code shipped in R5b
  (`;D;$LASTEXITCODE`). The `;C` sub-goal is **answered, not
  deferred**: `;C` (OutputStart) would need a
  `PSConsoleHostReadLine`/PSReadLine hook, and the dogfood
  confirmed PowerShell **auto-disables PSReadLine under a
  screen reader** (the maintainer heard the warning banner).
  Since the screen-reader user is the *only* use case here,
  `;C` is **not reachable for pty-speak by design** — the
  `prompt`-function `;A`/`;B`/`;D;<code>` path on the
  `CmdOscAB` arm is the **final R5 design**, not a stopgap.
  No further R5c work; the clean `CleanOscAC` reference
  waits for a future shell with a real OutputStart hook (R6+
  / claude TUI / a custom shell) — tracked in ADR 0006
  §"Deferred to R6+", not an R5 open item.
- **R5d — closure (DONE 2026-05-16).** `PowerShellAdapter`
  pinned by `PowerShellAdapterTests`; matrix `52-R5b`
  PASSED (below). R5 is **functionally complete &
  validated**: PowerShell emits OSC-133 (A/B/D + real exit
  code) under NVDA, clean command/output split, screen-
  reader-safe. **Future note (maintainer-requested
  2026-05-16):** add a `Diagnostics → PowerShell
  Interaction Tests` submenu mirroring the existing
  `CMD Interaction Tests` (PS equivalents of
  test-01/02/04/09), wired in `Program.fs` + `.ps1`
  scripts under `publish/scripts/`. **Deferred until R6**,
  when PS feature behaviour (per-line progress, prompt
  verbosity) needs PS-specific manual coverage and the
  concrete scenario list is known — adding empty scaffolding
  now has no signal. Tracked in ADR 0006 §"Deferred to R6+".

**THE #1 R5 RISK — RESOLVED POSITIVE (maintainer dogfood,
2026-05-16).** PowerShell auto-disables PSReadLine under a
screen reader (the maintainer confirmed hearing the
"PSReadLine … disabled … screen reader" warning banner).
**Outcome: the risk did not materialise.** The `prompt`
function is a core host hook independent of PSReadLine, so
OSC-133 emits regardless: switching to PowerShell works,
`echo hi` ⏎ → NVDA speaks `hi` (clean command/output split,
the same `CmdOscAB` arm as cmd — no command-echo, no
next-prompt bleed), with a real `$LASTEXITCODE`. The
`prompt`-only baseline **holds**; R5 needs no alternative
mechanism. The only consequence is the documented one:
`;C` is unreachable under a screen reader (R5c above) — an
accepted architectural conclusion, not a defect.

Settled R5 sub-questions (were open in R5b design; now
confirmed): immediate (not deferred) `;D` via the post-exec
`prompt` — works; first prompt's `;D;0` absorbed by the
`None,CommandFinished` ignore + R4c stray-gate — works
(no spurious launch announce reported); `-EncodedCommand`
(not `-Command`/profile) — works (no quoting/length issue);
cmd↔PowerShell hot-switch — works (maintainer switched in
and back). Deeper PS-specific corpus coverage is the R6
future-note above, not an R5 gap.

## 5. Pre-R5 pruning sequence (old-code remnants — scoped)

These are **separate one-concern PRs**, each locally
unverifiable (no `dotnet` in the sandbox → CI is the only
build signal; read each file twice before push). They are
*substrate / dead-code* cleanups (the FREEZE is on *announce
heuristics*, not these). Recommended order — least-risk
first; none block R5a but all should land before R5b so the
PowerShell adapter is built against a pruned tree:

- **P1 — heuristic-detector detection-time gate (DONE
  2026-05-16, #377).** `HeuristicPromptDetector` had been
  running full-tilt for cmd/PowerShell even when OSC-133 is
  authoritative — only muted at *emit* time (the 2026-05-16
  bundle showed hundreds of `HeuristicPromptDetector
  SUPPRESSED` / `R3a: muted heuristic boundary` lines =
  wasted per-chunk regex + log spam). Fix shipped: the
  prompt-detector logic was extracted verbatim into
  `runHeuristicPromptDetector` and `runDetector` now calls
  it only `if not oscSeenThisSession`
  ([`src/Terminal.App/Program.fs`](../src/Terminal.App/Program.fs)).
  **Behaviour-identical** (single notification-consumer
  thread ⇒ `oscSeenThisSession` can't flip mid-call, so
  "skip" ≡ the prior "run then mute"; `promptDetector` is
  read nowhere else except the `Ctrl+Shift+D` snapshot,
  which now shows its as-of-OSC state; `oscSeenThisSession`
  resets on shell-switch so it's correct across switches).
  **Claude unchanged** — it never sets `oscSeenThisSession`
  (no OSC-133) so its detector stays fully live. The
  `SelectionDetector` (claude-only, own gate) is **not**
  gated by P1. Net: same one-time `R3a precedence …`
  Information marker; the per-chunk spam + scan disappear
  post-OSC. CI-green; validated by the next dogfood's
  regression sweep (cmd/claude narrate identically; bundle
  shows no per-chunk `R3a: muted` post-OSC).
- **P2 — Cycle-47 synthetic-CommandFinished shell-guard
  (DONE 2026-05-16, #379).** The synthetic-`CommandFinished`-
  before-`;A` compensation in
  [`Program.fs`](../src/Terminal.App/Program.fs) was already
  correct *by construction* post-R4c/R5b (cmd & PowerShell
  finalise on the real `;D` call, so the `;A` PromptStart
  call has `finalisedOpt = None` → it never tripped for
  them). P2 adds `not oscSeenThisSession` to the guard —
  using the **same predicate as P1**, the single source of
  truth for "OSC authoritative this session". Golden path:
  identical. claude (`oscSeenThisSession` always false, no
  OSC 133): unchanged, still fires (load-bearing). The only
  behaviour delta is the intended hardening: in the
  defensive missed/garbled-`;D` edge an OSC shell no longer
  falls back to the cmd-heuristic-era synthetic marker —
  exactly the R3a precedence principle. Comment + one
  `&&`-condition change; CI-green.
- **P3 — dead pathway config keys (INVESTIGATED 2026-05-16,
  #380; re-scoped — audit's "safe-to-delete" was
  optimistic).** Findings: (a) the generated `config.toml`
  template
  ([`src/Terminal.Core/Config.fs`](../src/Terminal.Core/Config.fs)
  :218+) is **already clean** — it has **no**
  `#[pathway.stream]` / `#substrate_mode` lines; the
  maintainer's on-disk file showing them is a stale-build
  artifact (`Ctrl+Shift+E` only creates-if-missing, never
  rewrites). So that half of P3 is a **no-op**. (b)
  `ShellPathwayConfig.PathwayId` + `resolvePathwayForShell`
  + the `[pathway.<id>]` parameter-table parsing are
  confirmed **dead** (no live consumer in `Terminal.App` —
  grep-verified) **but entangled in the live
  `ShellPathwayConfig` record**, which also carries the
  live `Verbosity` → `currentShellPolicy.Streaming/PromptPath`
  config consumed throughout `Program.fs`. Removing the
  dead field/resolver is therefore a surgical, wide-ish,
  **locally-unverifiable** record/parser refactor — NOT a
  clean delete. Per the "no risky locally-unverifiable rip
  in a proceed-pass" discipline it is **re-scoped as
  deferred P3b** (ADR 0006 §"Deferred to R6+"), not
  bundled. Net P3 deliverable: this finding recorded; no
  code change (nothing safe + substantial to ship).
- **P4 — canonical-corpus ESC-byte fix (DONE 2026-05-16, #381).**
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
  This is a real loss of test coverage. **Shipped:** the two
  raw ESC bytes re-encoded as the TOML-1.0 standard
  Unicode escape (Tomlyn decodes it → U+001B →
  Diagnostics `Encoding.UTF8.GetBytes` → 0x1B byte to the
  PTY — exactly the prior intent); applied via `sed` since
  the raw 0x1B can't pass through the Edit tool (the
  CLAUDE.md "use the explicit Unicode escape, not a raw
  byte" foot-gun). `grep -c $'\x1b'` now 0; the loader is
  unchanged; the whole corpus load is restored (it was
  failing wholesale on this one line, not just this
  scenario). Validation: the maintainer's next
  `Ctrl+Shift+D` shows a populated
  `--- CANONICAL CORPUS RESULTS ---` section instead of the
  `corpus skipped: TOML parse error` `[WRN]` — diagnostic,
  behaviour-neutral, not a blocking manual test.
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
