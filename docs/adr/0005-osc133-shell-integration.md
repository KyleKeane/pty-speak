# ADR 0005 — OSC 133 shell integration (the heuristic-screen-scrape exit)

- **Status:** Accepted (2026-05-15; maintainer approved
  ADR 0005 + ADR 0006 together) / **cmd mechanism implemented
  via ADR 0006 R2–R4** (#353–#357, 2026-05-16). Supersedes
  the heuristic prompt-detection model as the *primary*
  boundary source; does not delete it (it becomes the
  fallback, now muted-once-OSC-seen per R3a). The **cmd**
  emit strategy shipped as **Option B** (command-line
  `prompt`; A/B only — `;C` realised consumer-side, `;D`
  deferred; see §3 R2 status note). **PowerShell** (full
  A/B/C/D) is R5, pending the R1–R4 foundation dogfood.
  Implementation proceeds via ADR 0006's R0–R7 stages.
- **Status note (2026-05-15):** the "Walking-skeleton stages"
  A–F list below is **superseded by the R0–R7 stage list in
  [ADR 0006](0006-three-layer-refoundation.md)**, which folds
  this ADR's OSC-133 mechanism into the three-layer
  re-foundation. Read 0006 for the live stage plan; this ADR
  remains the canonical *mechanism* spec (per-shell emit
  strategy, injection seam, the existing decoder path).
- **Deciders:** maintainer (chose "pivot to shell integration"
  2026-05-15 after the Cycle 51 PR-X…PR-AE dogfood cycle).
- **Predecessors:** ADR 0003 (interaction state machine),
  ADR 0004 (IOCell model). This ADR keeps both; it changes
  *where boundary truth comes from*, not the IOCell substrate.

## Context

Cycle 51 shipped PR-W → PR-AE: a Seq-watermark narration model
plus seven compensating patches (PR-X history-scroll watermark,
PR-Y sub-prompt-question strip, PR-Z recall-wrap, PR-AA
preamble/banner, PR-AB fast-type echo strip, PR-AC switch
banner, PR-AD SpeechCursor-from-IOCells, PR-AE cursor-anchored
prompt detection). Each fixed a real symptom; collectively they
were scaffolding around one unsound foundation.

**Root cause (established at PR-AE, confirmed by the
2026-05-15 preview.141 dogfood):** `HeuristicPromptDetector`
reconstructs shell semantics — *where a prompt, command, and
output begin and end* — by regex-scraping an unstructured
`cmd.exe` screen. cmd emits no boundary protocol, redraws the
input line in place on Up/Down history recall, and overwrites
in place for progress loops. PR-AE removed the acute corruption
(phantom `PromptStart` storms), but the residue is the model's
ceiling, not regressions:

1. **Progress loops narrate only at completion.** No
   per-step boundary exists in cmd; tuple-finalise is the only
   announce point. Per-step narration needs a real
   OutputStart…CommandFinished region.
2. **SpeechCursor command items are garbage / missing.**
   `IOCell.CommandText` for a history-recalled command is
   `"echo 1echo 2echo hiecho hi"` (doskey redraw soup). Even
   the byte-level `UserInputBuffer.Capture()` is `[A1` /
   `[A[A[B` for recalled commands — the bytes are arrow keys,
   not the command. A clean recalled command is
   *unrecoverable* from a cmd PTY without shell cooperation.
3. **Prompt-path verbosity suppresses echo output;
   review-cursor lacks per-prompt path** — extraction-layer
   bugs of the same family.

The decode + clean-extraction half already exists and is
tested:

- `src/Terminal.Core/Osc133.fs` — `tryParse` decodes
  `ESC ] 133 ; A|B|C|D[;<code>] (BEL|ST)` into a
  `PromptBoundaryData` with `BoundarySource.Osc133`.
- `src/Terminal.Core/Screen.fs` (`Apply`'s `OscDispatch`
  arm, ~line 674) already calls `Osc133.tryParse` and
  publishes the boundary through the same path the heuristic
  detector uses.
- `SessionModel.extractIOCell` already has the clean arm:
  `Some promptStart, Some outputStart -> sliceText` gives an
  exact `(CommandText, OutputText)` split with **no
  watermark, no strip, no cursor heuristic**.
- `BoundarySource` already distinguishes `Osc133` vs
  `HeuristicPromptRegex <ms>` for downstream confidence.

**The missing half is the emitter.** cmd / PowerShell do not
produce OSC 133 by default. If they did, the existing clean
path activates and every issue above is addressed at the
source — and the seven compensation patches become
deletable.

## Decision

1. **Inject shell integration so cmd and PowerShell emit
   OSC 133**, making `BoundarySource.Osc133` the primary
   boundary source and the existing `extractIOCell` OSC arm
   the primary extraction path.
2. **Keep `HeuristicPromptDetector` as an explicit
   fallback** for sessions/shells that do not emit OSC 133
   (claude's Ink TUI, a remote shell over ssh, a user shell
   that strips our init). Add a **precedence rule**: once any
   `Osc133` boundary is observed for the current shell
   session, the heuristic detector is muted for that session
   (no double boundaries). `BoundarySource` already carries
   the discriminator; the gate is a single session-scoped
   bool next to the detector.
3. **Per-shell emit strategy:**
   - **cmd** — set `PROMPT` so each prompt expansion emits
     `ESC]133;A` … `ESC]133;B` around the prompt text, and
     emits the *prior* command's `ESC]133;D;%errorlevel%`
     at the head of the next prompt (cmd has no post-exec
     hook; deferred-D is the standard Windows-Terminal cmd
     integration technique). cmd's `PROMPT` supports `$E`
     (ESC) on Windows 10+. OutputStart (`C`) is the implicit
     transition after `B` + Enter. This yields a clean
     PromptStart / CommandStart / (deferred) CommandFinished
     — sufficient for an exact command/output split, and the
     command captured at `B` is the *final* line the shell
     will run, so **history recall is correct by
     construction**.
     - **R2 status note (2026-05-16):** implemented as
       **Option B** (command-line `cmd.exe /K prompt …`,
       adapter-owned — `CmdAdapter.IntegrateOsc133`), *not*
       `setx`/env `PROMPT`. Two refinements forced by the
       shipped consumer + cmd's `prompt` semantics:
       (1) "OutputStart (`C`) is the implicit transition
       after `B` + Enter" is **not** synthesised — the
       shipped `SessionModel.extractIOCell` needs a literal
       marker. cmd's `prompt` has no post-Enter/pre-output
       hook, so the "implicit C" is realised **consumer-
       side**: a new PromptStart + CommandStart
       `extractIOCell` arm anchors the command/output split
       at the shell-emitted `;B` marker (maintainer decision
       2026-05-16). (2) deferred-`D;%errorlevel%` is itself
       deferred — its live `%errorlevel%` can't defer
       through cmd's *command-line* `%`-expansion without
       fragile escaping; **A/B only** in R2, which is
       sufficient for the split (the R2 gate). `;D` returns
       when reachable without the quoting hazard.
   - **PowerShell / pwsh** — a generated `prompt` function +
     `PSConsoleHostReadLine` / PSReadLine handler emits full
     `A` / `B` / `C` / `D;$LASTEXITCODE`. Injected via
     `-NoExit -Command` or a generated profile passed on the
     spawn command line.
   - **claude** — full-screen Ink TUI; OSC 133 N/A. Stays on
     the heuristic/`claudeRegex` fallback. Out of scope for
     v1 (cmd + PowerShell first).
4. **Injection seam:** `Terminal.Pty.PseudoConsole`'s
   `CommandLine` is passed verbatim to `CreateProcess`. cmd
   gets `cmd.exe /K "<init>"` (or a `PROMPT` value in the
   spawned environment, which the env-scrub allows because
   it is ours, not parent-derived); PowerShell gets
   `-NoExit -Command "<init>"`. Exact mechanism chosen in
   Stage B; both are non-invasive and reversible.

## What this fixes (and what it lets us delete)

Fixes at the source: per-step progress streaming (real
`C`…`D` region), clean command capture incl. history recall
(captured at `B`), prompt-path verbosity (prompt text is now
delimited by `A`…`B`), review/speech-cursor command items,
and ends the patch-on-patch churn the maintainer flagged.

Becomes deletable once OSC 133 is the active path (Stage C):
the PR-AB fast-type strip, the PR-X/PR-Y watermark + strip
logic in the announce path, and the cursor-anchored selection
in `HeuristicPromptDetector` collapses back to fallback-only.
**Net-subtractive** — the pivot removes scaffolding rather
than adding more.

## Consequences / costs

- **Shell-init injection complexity.** A user with a custom
  `PROMPT` / PowerShell profile can conflict. Mitigation:
  compose (prepend our `A`, append our `B`) rather than
  replace where feasible; document; the heuristic fallback
  still covers the conflict case.
- **Windows-10+ dependency** for cmd `$E`. Acceptable —
  pty-speak already targets Win10/11.
- **OSC must not render.** The VT parser already absorbs OSC
  as non-printing (this is *why* heuristic-era ContentHistory
  prompt text is clean); no screen leakage expected. Pinned
  by a Stage-B test.
- **Adversarial shell can emit false boundaries.** Already
  bounded (`Osc133.fs` security note; `MAX_OSC_RAW`;
  `BoundarySource` exposes confidence). Unchanged.

## Walking-skeleton stages

- **A — this ADR.** Design + maintainer decision. Docs-only.
- **B — cmd emitter.** Inject `PROMPT` (A/B + deferred D).
  Verify the existing `extractIOCell` OSC arm activates;
  heuristic still runs in parallel (no precedence yet).
  NVDA matrix row. Gate: maintainer cmd dogfood — clean
  command/output split incl. history recall.
- **C — precedence + scaffolding retirement.** Mute the
  heuristic detector once `Osc133` is seen; delete PR-AB and
  the PR-X/PR-Y announce-path compensations on the OSC path.
- **D — PowerShell emitter.** Full A/B/C/D + exit code.
- **E — feature unlock.** Per-line progress streaming;
  prompt-path verbosity fix; SpeechCursor clean command
  items — now trivial on delimited regions.
- **F — claude/other-shell decision + Cycle closure audit.**

Each stage is independently CI-gated and dogfood-validated;
no stage merges ahead of its validation (walking-skeleton
discipline, per CLAUDE.md).

## Validation gate

Per stage: maintainer NVDA dogfood on a release build, plus
a `docs/ACCESSIBILITY-TESTING.md` matrix row. Stage B's gate
is the decisive one: with cmd emitting OSC 133, the
`echo hi` / history-recall / progress-loop scenarios that
defeated the heuristic model must produce exact boundaries
(`BoundarySource=Osc133`, clean `CmdText`/`OutText`, no
phantom finalises) **without** any announce-path watermark
or strip.

## Alternatives considered

- **Keep patching the heuristic layer** — rejected by the
  maintainer 2026-05-15; the residual issues are the model's
  ceiling, not bugs.
- **Two contained bug fixes then stop** — leaves progress
  streaming and clean recalled-command capture permanently
  impossible on cmd; does not end the churn.
- **Cursor/scroll diffing to recover structure** — strictly
  more heuristic scaffolding on the same unsound foundation.
