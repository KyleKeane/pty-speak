# Session handoff

A short brief for the next session (human contributor or Claude
Code). For anything historical that isn't in this doc, follow
the pointers in [§ Where to find detail](#where-to-find-detail).

This file aims to stay **under 200 lines**. If it starts to grow
past that, the older content should move to
`docs/archive/` rather than continue accumulating here. The
previous (multi-thousand-line) handoff is at
[`docs/archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md`](archive/pre-cycle-45/SESSION-HANDOFF-pre-cycle-45c-historical.md)
and serves the decision-trail role this file used to overload.

## Current state (2026-05-16)

> **NEW SESSION START HERE →
> [`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md)**
> (**Accepted 2026-05-15**; reads ADR 0005 as its mechanism
> spec). **Cycle 52 R1–R4 COMPLETE & merged (#347–#357)** —
> the three-layer re-foundation is structurally done and
> CI-enforced; KI-R2-1 is structurally fixed. **The gate
> before R5 is one consolidated R1–R4 "foundation" dogfood**
> (maintainer batching findings; 5 of those PRs are merged
> but not yet maintainer-dogfooded, by explicit choice).
> **R4c (pre-R5, maintainer-chosen 2026-05-16)** completes
> the cmd transport with a *boundary-only* deferred `;D`
> (`CommandFinished None`; no exit code — cmd has no native
> `%errorlevel%`-in-prompt mechanism). Folds into the same
> R1–R4 foundation dogfood (matrix `52-R4c`).

- **`main` HEAD = `cbf8d48`** (Cycle 52 R4b, #357).
- **Cycle 52 R1–R4 — the full structural re-foundation —
  COMPLETE.** Per-PR detail: [`CHANGELOG.md`](../CHANGELOG.md)
  + `git log`. Summary:
  - **R1** (#347–#352): `Terminal.Shell` assembly +
    `HeuristicPromptDetector` out of the Core *assembly* +
    shell-resolution → `SessionHost` + reader loop →
    `CmdAdapter` (the **VtEvent** seam). Behaviour-identical;
    **maintainer-dogfood-validated 2026-05-16**.
  - **R2** (#353): cmd OSC-133 prompt injection — **Option B**
    (adapter-owned command-line `cmd /K prompt
    $e]133;A$e\$p$g$e]133;B$e\`, cmd-only-gated). Consume
    side: `extractIOCell` gained a PromptStart+CommandStart
    arm — ADR 0005 §3's "implicit C" realised **consumer-
    side** (maintainer decision 2026-05-16). Core mechanism
    dogfood-validated 2026-05-16 (~9 clean evals, no escape
    leak); KI-R2-1 surfaced (see below). **R4c (this PR,
    2026-05-16)** prepends a boundary-only deferred `;D` →
    the live value is now `cmd /K prompt
    $e]133;D$e\$e]133;A$e\$p$g$e]133;B$e\` (cmd emits
    `CommandFinished None` at every prompt head; no exit
    code — cmd OS-level limitation).
  - **R3a** (#354): OSC-133 precedence — a per-shell-session
    `oscSeenThisSession` latch mutes the heuristic dispatch
    once an `Osc133` boundary is seen (reset on shell-switch,
    not alt-screen).
  - **R3b** (#355): retired the PR-X / PR-Y / PR-AB
    announce-path compensations; the tuple-final announce now
    speaks the R2-sealed IOCell `OutputText` (Seq-sliced,
    ≈ −200/+60 LOC). **KI-R2-1 structurally fixed by
    construction.** `commandEnterSeq`/`awaitingSubPromptEnter`
    kept (feed/gate `extractIOCell`); PR-AA/AC banner
    deliberately **preserved** (ADR R3 names only PR-AB/X/Y;
    the banner is not a finalised cell).
  - **R4a** (#356): `HeuristicPromptDetector` namespace
    `Terminal.Core` → `Terminal.Shell` (the deferred R1.2
    tail). **Logger category restrung → bundle greps now use
    `Terminal.Shell.HeuristicPromptDetector`.**
  - **R4b** (#357): `portability-lint` CI now enforces
    no-P/Invoke + no-`Terminal.Shell`-dependency in
    `Terminal.Core`. The boundary is **structural, not
    disciplinary** — the answer to "why 51 cycles stayed
    brittle".
- **Cycle 51 (IOCell pivot) — SHIPPED** (#337–#344). The
  PR-X/Y/AA/AB announce compensations it added were the
  heuristic-ceiling patches R3b has now retired. Detail:
  `CHANGELOG.md` / `git log`. `CYCLE-51-PLAYBOOK.md` is
  HISTORICAL.

### R1–R4 dogfood findings (post-maintainer-dogfood 2026-05-16)

State after the maintainer's batched dogfood of post-`cab2a0d`
`main`:

- **KI-R2-1 — RESOLVED & validated.** The volume-triggered
  echo+next-path leak is gone (R3b deleted the PR-X
  `lastEnterSeq`/`EndsWith` pile; R3c made the announce a
  settled-watermark Seq-gap). Confirmed by the maintainer.
- **#2 sub-prompt double-speech — FIXED & validated** (single
  sub-prompt, e.g. `test-04`). The question is announced once
  in real-time, then only the post-response output at finalise.
  Maintainer confirmed ("first half until input, then only the
  second half"). Stays fixed under R3d/R3e.
- **test-09 multi-sub-prompt — FIXED by R3e.** The
  SHA-confirmed bundle (build `62fdc2d`, validating R3d)
  exposed a structural defect on `test-09-multi-interrupt`
  (matrix `52-R3c-multi`): after the **first** single-key
  answer the 2nd sub-prompt was never announced and the
  resumed output (Seq 145–163) was mis-tagged `UserInputEcho`.
  Root cause (bundle-definitive): a `choice`-style answer is a
  single keystroke with **no `\r`**, so `EnterPressed` never
  fires; the state stayed stuck `Composing(SinglekeySubmit=
  true)`. **R3e** adds a `SingleKeySubmitted` transition (the
  byte-write path emits it on the first keystroke in that
  state) driving `Composing → Executing` → post-answer output
  tagged `CmdOutput` + the 2nd `Executing,SubPromptIdle →
  Composing` re-detects. This is the MULTI-incremental
  composition case R5/R6 depend on.
- **R3c plain-command echo regression — FIXED by R3d.** The
  SHA-confirmed bundle (build `09321e7`) proved R3c re-spoke
  the typed command on every plain command (`echo X` → "echo
  X⏎X" instead of "X"). Root cause (bundle-definitive): R3c
  raw-sliced from `lastAnnouncedSeq`, which the **idle-flush**
  (`runHeartbeat`) silently bumps to the next cell's
  CommandStart marker, *before* its command echo.
  `extractIOCell` was correct (`Arm=CmdOscAB CmdLen/OutLen`
  clean) — R3c ignored that split. **R3d** lower-bounds the
  announce slice at `max commandEnterSeq lastAnnouncedSeq`
  (`commandEnterSeq` = extractIOCell's CmdOscAB output-start,
  after the echo) → plain = output only; sub-prompt =
  post-question only (#2 preserved). Diagnostics log
  `CommandEnterSeq`/`LastAnnouncedSeq`/`FromSeq`.
- **#1 banner — ROOT-CAUSED; fresh-launch behaviour
  DEFERRED.** The bundle showed cmd emits **no startup banner**
  in the maintainer's environment (first content is the
  prompt path); `bannerAnnounce` correctly yields `None`, so
  fresh launch is genuinely silent — *not* a broken announce
  / not a code defect. The product decision (announce the
  prompt on cold launch vs. accept silence) is **deferred**
  per maintainer until after R3d lands.
- **Backspace-to-empty path-read — RESOLVED** (R3c watermark).
- **Alt+F4 not closing — RESOLVED** (was the maintainer's
  keyboard function-lock, not code).
- **Output-path either/or — tracked, DEFERRED** (maintainer
  lean 2026-05-16). Only with output-path announcements ON
  (non-default): fast `echo`+Enter → path only; slow → output
  only. Hypothesis: path-announce vs tuple-final contend under
  NVDA same-activity supersession + speech-render timing.
- **Cold-start keyboard — tracked, DEFERRED** (maintainer
  decision 2026-05-16). A freshly-built EXE doesn't receive
  keyboard until close+reopen. **NOT build-identity-caused**
  (that's build-time metadata only). Suspected cold-start
  window-activation / input-focus race; workaround exists
  (relaunch). Needs narrowing (launch method? every cold
  start vs post-build? window foregrounded?) before touching
  the load-bearing WPF startup/focus path.
- **SHA spoken as scientific notation — FIXED** (R4-followup-2:
  `Ctrl+Shift+H` now spells the `+<sha>` char-by-char; the
  startup log / bundle keep the raw `+sha` for grep).
- **R4a logger-category change** is maintainer-facing: bundle
  greps for the heuristic detector use
  `Terminal.Shell.HeuristicPromptDetector` (old
  `Terminal.Core.…` returns nothing).

## Next stage

**R4c — cmd CommandFinished completion (pre-R5; this PR):**
prepend a *boundary-only* deferred `;D` (`$e]133;D$e\`) to
the cmd `prompt` template so cmd emits
`BoundaryKind.CommandFinished None` at the head of every
prompt — completing the cmd OSC-133 event stream (`;B`→`;D`
output region) so R5/R6 are genuinely shell-agnostic in the
core. No exit code: cmd has no native `%errorlevel%`-in-
prompt mechanism (clink/doskey rejected — dependency / patch
class); the boundary is what R6 needs, the code is an
OS-level cmd limitation. Net-corrective — the real `;D`
replaces the misplaced Cycle-47 synthetic compensation for
cmd. Folded into the R1–R4 foundation dogfood (matrix
`52-R4c`); same installed preview, one NVDA pass.

> **cmd announce-heuristic FREEZE (maintainer decision
> 2026-05-16, post-`5518f5c` bundle).** The foundation
> bundle proved the IOCell substrate is sound; every
> remaining cmd defect (path/echo bleed under history-
> recall, "Terminal edit Blank" on fresh launch — cmd
> emits no banner, confirmed) is in the racy announce-
> reconstruction layer, not the substrate. **Do not push
> further cmd announce-heuristic patches before R5.**
> PowerShell's native `A/B/C/D` is the clean reference;
> cmd's announce path is rebuilt to it afterward. Full
> rationale + evidence in
> [ADR 0006](adr/0006-three-layer-refoundation.md)
> §"Deferred to R6+" decision note. cmd *substrate* work
> (R4c-class) is still fine — only the announce-heuristic
> layer is frozen.

**R5 — PowerShell adapter** (= ADR 0005 Stage D): full
OSC-133 `A/B/C/D` + exit code via a generated profile /
`-NoExit -Command`, as a second `Terminal.Shell` adapter.
**Gated on the R1–R4 foundation dogfood** (maintainer
decision 2026-05-16: checkpoint at R1–R4, validate the
foundation before a second adapter builds on it; R4c rides
that same dogfood). Then
**R6** feature unlock (per-line progress, prompt-verbosity
fix, clean SpeechCursor — trivial on the clean event
stream) → **R7** claude adapter decision + Cycle closure
audit. R1 + R4 were the structural gates; R5+ is net-
additive on the now-enforced boundary. NVDA matrix rows per
stage in [`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).

**Dogfood operations (Cycle 52 R1, learned 2026-05-15/16).**
Run R-stage dogfoods from an **installed preview release**
(`gh release create vX --target main --prerelease …` →
`scripts\install-latest-preview.ps1 -Tag vX`), **not** a dev
`dotnet run`/`publish` build — the dev-host process has
unreliable NVDA UIA-notification delivery on the RDP/VM and
burned ~an hour of false "regression" chasing. **NVDA
UIA-notification wedge:** after heavy app start/stop churn,
NVDA can stop voicing UIA Notification events (output /
diagnostics) while still speaking *menus* (native focus
tracking). This is an environment artifact, not a code
regression — **restart NVDA** (reboot the VM if that fails);
verified to fully restore it on both build types.

**Deferred (independent; revisit after Cycle 52):**

- **Backspace-to-empty announces the full input path**
  (observed 2026-05-16, preview.143 R1 dogfood prep). At a
  prompt, hold backspace until the line is empty: deleting
  the **last** character now narrates the entire input path
  (`C:\…\Terminal.App>`) instead of just `>`. Repro: new
  line, type `1`, backspace. Suspected **pre-existing**
  (announce-path / PR-I history-recall prefix-strip, Cycle
  49 era — untouched by R1; R1.1–R1.4 don't touch the
  announce path). **Classify during the R1 dogfood:** if
  preview.141 shows the same, it's pre-existing → R2
  backlog (the OSC-133/clean-SpeechCursor work targets
  exactly this); if preview.141 says only `>`, it's an R1
  regression and blocks R2. Maintainer: "not worth fixing
  now, note for later."
- **Fast-type command-echo leak** (observed 2026-05-16,
  post-R4c local dogfood). Fast-typing `echo hi` + hitting
  Enter quickly speaks the command (`echo hi`) *then* the
  output (`hi`) instead of just `hi` — the `52-R3d` fail
  signal under the fast-type window. R3d fixed normal-paced
  typing; the fast-type race in the `commandEnterSeq`
  byte-watermark (± an NVDA caret-read contribution) is
  residual. **Not an R4c regression** (R4c didn't touch the
  command/output watermark or the caret path); same root
  cause as the deferred BOTH-edges region-cut work — see
  [ADR 0006](adr/0006-three-layer-refoundation.md)
  §"Deferred to R6+" item 1 (now covers the leading
  command/output edge, not just the trailing edge).
  Classification rule + bundle-disambiguation recorded
  there. Eliminated by the clean event stream (R5 `;C` /
  the R6 cmd region-cut); **deferred, do not report as new**.
- **`EntrySource.DraftInputRecall`** (deferred from Cycle 49
  E3) — substrate refinement; no audible bug behind it
  after PR-I's screen-read approach.
- **Sub-prompts as nested IOCells** (ADR 0006; only if
  Cycle 51 dogfood surfaces the need). Future *shape* now
  recorded — see below.
- **Canonical-IOCell navigation / operations layer**
  (maintainer direction 2026-05-16; full detail in
  [ADR 0006](adr/0006-three-layer-refoundation.md)
  §"Deferred to R6+", cross-linked from
  [ADR 0004](adr/0004-iocell-model-for-shell-interaction.md)
  Decision 2). Five tracked deferrals, all gated on the
  canonical IOCell being solid (post-R5 foundation), all
  deliberately *not* pre-R5: (1) retire `stripNextPrompt` →
  one shell-agnostic output-region cut (R5's real `;C`/`;D`
  reveals the general shape); (2) replace SpeechCursor
  manual history-nav with IOCell-history nav + per-cell ops
  (copy-output, run-again-input); (3) **open decision** —
  write IOCell history into the review-cursor document or
  keep parallel; (4) move live current-line/system focus so
  NVDA "read current line" propagates; (5) multi-interrupt
  IOCell as navigable chunks in a container / sequence of
  output segments tied to the input cell. Explicitly-
  tracked, rationale-bearing deferrals — not punts.
- **Cell-navigation hotkeys** (`h`, `o`, `Alt+Up/Down`) per
  CANONICAL-DISPLAY-CATALOG §1.6.
- **User-facing replay UI** — leverages the
  `parseFromJsonl` reader shipped in PR-W2.
- **PowerShell support** — depends on PS-side heuristic
  emission emitting reliable PromptStart markers.
- **Cycle 45g** — `ShellPolicy` consolidation (~200 LOC
  pure refactor).
- **Spatial audio channel** + **custom TTS channel** —
  Cycle 51 proves the extensibility surface; the actual
  sink impls are their own projects.
- **Velopack staleness investigation** (PR-M).

See [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md)
for sequencing rationale + risks.

## Operational gotchas

Things a new session needs that aren't in
[`CLAUDE.md`](../CLAUDE.md) or
[`CONTRIBUTING.md`](../CONTRIBUTING.md):

- **No `dotnet` in the sandbox.** Read your edits twice; CI on
  Windows-latest is the only compile-and-test gate available.
- **Tag pushes return 403.** Branch pushes are fine; tag pushes
  are blocked by the local git proxy. Stage tag commands in
  [`docs/CHECKPOINTS.md`](CHECKPOINTS.md) "Pending checkpoint
  tags" for the maintainer to sweep from their workstation.
- **Maintainer uses NVDA.** Don't suggest GUI dialog walks
  (System Properties → … chains, Task Manager chevrons, etc.).
  Surface keyboard- or shell-only equivalents instead.
- **CI failure log access is restricted.** The sandbox blocks
  `productionresultssa*.blob.core.windows.net` and
  `api.github.com`. When CI fails, ask the maintainer for the
  log slice rather than guessing from the diff.
- **GitHub MCP can disconnect mid-session.** Observed
  2026-05-13: MCP token failed to retrieve during the PR-B
  fixup cycle, blocking auto-merge + check-status queries.
  Fallback: ask the maintainer to merge via the GitHub UI
  "Squash and merge" button. Webhook events kept flowing
  independently. See CLAUDE.md "Sandbox / runtime constraints".
- **Diagnostic-bundle chunking.** Don't ask for full
  `Ctrl+Shift+D` bundles unprompted — they can be multi-MB and
  crash iOS chat clients on paste. Use the menu items under
  `Diagnostics → Extract` to ask for targeted slices.

## Where to find detail

| If you need… | Open |
|---|---|
| **Cycle 52 re-foundation (START HERE)** | [`docs/adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md) |
| Cycle 52 R1 architecture/dependency map | [`docs/CYCLE-52-R1-ARCHITECTURE-MAP.md`](CYCLE-52-R1-ARCHITECTURE-MAP.md) |
| Cycle 52 OSC-133 mechanism spec | [`docs/adr/0005-osc133-shell-integration.md`](adr/0005-osc133-shell-integration.md) |
| Cycle 51 (shipped) locked decisions | [`docs/adr/0004-iocell-model-for-shell-interaction.md`](adr/0004-iocell-model-for-shell-interaction.md) |
| Cycle 51 execution map (HISTORICAL) | [`docs/CYCLE-51-PLAYBOOK.md`](CYCLE-51-PLAYBOOK.md) |
| Cycle-by-cycle trail | [`CHANGELOG.md`](../CHANGELOG.md) `[Unreleased]` |
| Doc-archiving discipline | [`docs/DOC-MAP.md`](DOC-MAP.md#archiving-stale-onboarding-narrative) |
| Active strategic plan + roadmap | [`docs/PROJECT-PLAN-2026-05-12.md`](PROJECT-PLAN-2026-05-12.md) |
| Pre-Cycle-45 decision history | [`docs/archive/pre-cycle-45/`](archive/pre-cycle-45/) (README inside) |
| Substrate / architecture | [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) + [`docs/adr/0001-substrate-channel-dichotomy.md`](adr/0001-substrate-channel-dichotomy.md) |
| Spec | [`spec/tech-plan.md`](../spec/tech-plan.md), [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md) |
| Module map | [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) |
| Manual-test matrix | [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md) |
| Doc routing index | [`docs/DOC-MAP.md`](DOC-MAP.md) |
| Per-audience entry points | [`README.md`](../README.md) "Quick links" |
| Sandbox / runtime rules (for Claude) | [`CLAUDE.md`](../CLAUDE.md) |
| PR shape, F# gotchas, accessibility rules | [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
