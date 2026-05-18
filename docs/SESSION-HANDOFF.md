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

## Current state (2026-05-18)

> **⇒ 2026-05-18 — SESSION-CLOSURE RECONCILIATION (read THIS
> first; it supersedes every earlier block in this section).**
> The boundary-diagnostic-capture track is **COMPLETE and on
> main** (#417–#429), *not* "next" (the earlier blocks below
> wrongly imply it is pending — that stale framing was the
> source of the session loops):
> - **#417** B1 `RawShellRecorder` + `Ctrl+Shift+T`; **#418**
>   protocol/catalogue; **#419–#421** C1/C2/C3 captured +
>   analysed; **#422/#423** boundary-fix P1 (per-shell
>   `BoundaryContract` seam) / P2′ (timing-independent cmd
>   split + tolerant prompt strip); **#424–#429** replay-oracle
>   (R-A/R-B, C1/C2/C3/C5/C6 — the regression guard).
> - **The cell-seal defect is CHARACTERISED FROM DATA** in
>   [`docs/boundary-capture/README.md`](boundary-capture/README.md)
>   (cross-scenario synthesis): it is **one** boundary defect
>   that *degrades with interaction complexity* — the OSC-133
>   markers needed to fence a cell **are present**; the
>   extractor just doesn't treat `;D` as a hard output-end nor
>   fence the trailing next-prompt out. The fix is **specified
>   + oracle-guarded** (seal on top-level `;D`; fence trailing
>   `;A <prompt> ;B`; hold cell open across unmarked
>   sub-prompts as ADR 0004 inline state; tolerate mixed OSC
>   terminators) **plus one independent channel-layer fix** for
>   the C2 fast-type repeated-speech timing artifact.
> - **#428 saga settled:** the Direction-1 **BS arm is
>   retained** (oracle-validated; C5 is an active guard); the
>   (ii) `tick` idle-seal gate was a **live regression and is
>   reverted** (#431→#432 on main).
> - Also merged this session: **#433** (CHANGELOG union-merge
>   CI), **#435** (menu accelerators / Diagnostics `Alt+D`,
>   Test Scripts `Alt+S`), **#436** (markdown-link-check CI
>   speedup). Open issues: **#434** (changelog-from-commits
>   tooling), **#437** (corpus battery: injected commands
>   bypass the keyboard `EnterPressed` transition → all FAIL
>   `missing=StreamChunk`), **#438** (yes/no `choice`
>   single-key — investigated, **PARKED as intermittent**;
>   focus-report/timing hypothesis; ruled out: #428, the
>   OSC-133 pivot, the input handler — see the issue).
> - **GATE — the next direction is a maintainer decision, not
>   more diagnosis.** [ADR 0010](adr/0010-interaction-strategy-structured-runner-vs-passthrough.md)
>   (**Proposed** 2026-05-18) puts the first-principles ROI
>   fork to the maintainer: **A** two-mode reframe (structured
>   command-runner primary + raw-PTY passthrough secondary —
>   recommended), **B** stay the course (implement the
>   specified boundary fix + C2 fix), **C** hybrid auto-detect.
>   **Status-quo implementation (= ADR 0010 Option B) is READY
>   but PAUSED** so the decision is not pre-empted. **Next
>   stage = the maintainer ratifies A/B/C in ADR 0010; the
>   chosen option's consequence list becomes the next cycle's
>   plan** (see § Next stage).
>
> *(Earlier blocks retained as the narrative trail; the block
> above is authoritative.)*

> **⇒ 2026-05-17 evening — CYCLE CLOSURE (post-#415; read
> THIS first, it supersedes every earlier block in this
> section).** ADR 0007 **Phase 6a-2b → the full Phase-6b
> correction round shipped CI-green and maintainer-dogfood-
> validated** ("everything seems good", 2026-05-17). The
> previously-PENDING `52-ADR7-P6a-fix` re-dogfood is
> **RESOLVED** — superseded and expanded by the consolidated
> `52-ADR7-P6b` round below (matrix row marked PASSED).
> Cycle-level PR block (all squash-merged to `main`,
> `1e63a0d` tip):
> - **#410** spatial pane keys (Ctrl+Shift+Left=terminal /
>   Right=history) + per-pane earcon (`CellEventBus.PaneSwitched`)
>   + **per-cell-source seam** (Ctrl+Shift+C copy / copy-cmd /
>   copy-output / rerun resolve the **list's selected cell by
>   `CellId`**, not SpeechCursor — SpeechCursor stays alive
>   for *navigation only*; full removal deferred) + persistent
>   visible selection via an own `ListBoxItem` ControlTemplate
>   (theme-independent; the prior `SystemColors` override
>   didn't render) + dispatcher-deferred focus onto the
>   selected `ListBoxItem` (fixes the menu focus-trap; gives
>   NVDA a real focused element).
> - **#411** empty-list placeholder (an empty `ListBox`
>   couldn't hold focus) + bold per-panel titles + light-
>   scheme colour inversion (temporary, low-vision test
>   machine — not the theme framework).
> - **#412** shell-switch **marker notice row** (history is
>   deliberately *not* reset on hot-switch) + **row-model
>   unification**: `cellHistoryRows : ResizeArray<CellView
>   option>` strictly 1:1 with the items (Some=cell,
>   None=notice) — retired the fragile parallel-array +
>   placeholder hack.
> - **#413 / #414** menu reorganization ×2 → top-level order
>   **Interface · Terminal · Cell History · Diagnostics ·
>   About**; Shell nested under Terminal; Prompt Path →
>   Terminal; Open Data Folder → Diagnostics; Window → under
>   Interface; "Output and Session" → "Session"; Help →
>   About; all `(not yet implemented)` placeholders ENABLED
>   (disabled items weren't announced by the screen reader)
>   as the living D2/Phase-6 op-set sketch.
> - **#415** About-menu browser links (Open GitHub Repo /
>   Submit a Feature Request / Submit an Issue — one shared
>   `runOpenUrlInBrowser`) + a **Version** item showing the
>   build version + git short SHA at runtime.
>
> **Findings recorded (no code, maintainer-confirmed):**
> Output Verbosity is **NOT** deprecated — it still gates the
> live output-announce path (`ShellPolicy.Streaming`),
> distinct from the post-hoc cell history. The focused-item
> **outline-on-blur** visual is deferred to the stable Tree
> (see Deferred list). Both are logged so they aren't
> re-litigated.
>
> **NEXT (maintainer-chosen 2026-05-17): the cell-seal /
> boundary computational-accuracy track.** The navigable
> history UI is now structurally solid, BUT it does not
> populate for interactive scenarios — the canonical
> input-test produces **no sealed command cell** because the
> cell is dropped upstream (ADR 0004 drop-on-None /
> Cycle-52 boundary detection), not lost between the bus and
> the list. This is the "boundary-diagnostic-capture track"
> the [#403 re-sequencing amendment](adr/0007-canonical-iocell-history-navigation.md#re-sequencing-amendment-2026-05-17-maintainer-ratified)
> already named; it is now the active focus (see § Next
> stage). Phase 6c/6d + the real cell-nav/per-cell-op
> implementations behind the enabled placeholders follow
> once cells reliably seal.
>
> *(Earlier 2026-05-17 blocks below are retained as the
> narrative trail; the block above is the authoritative
> current state.)*

> **⇒ 2026-05-17 autonomous-sprint update (read this first
> for the latest):** ADR 0007 **Phases 0, 1, 2a, 2b, 2c, 3
> shipped CI-green** (`main` past `5ccbd6c`). 2a/2b dogfood
> **feature-PASSED**; **2c (`52-ADR7-P2c`) and 3
> (`52-ADR7-P3`) dogfood rows are pending the maintainer's
> NVDA pass**. Two pre-existing cmd-substrate issues were
> surfaced and recorded (not patched, FREEZE): ADR 0006
> §"Deferred to R6+" item 1 **"Tracked variant 1"**
> (command-line-edit input/output desync) and **"Tracked
> variant 2"** (clean-command argument truncation).
> **RE-SEQUENCED 2026-05-17 (maintainer-ratified) — this
> supersedes the earlier "STOPPED at Phase 4 / one-word
> unblock (C1, proceed)" framing.** The maintainer
> re-sequenced rather than just unblocking 4a:
> **Phase 6a (the cell-history interface scaffolding) is
> pulled forward and STARTS NOW** on cmd/PowerShell —
> decoupled from the segment model; it depends only on D1+D2
> (both shipped) and renders fine with imperfect cell
> boundaries (an orthogonal, already-tracked concern).
> Open decisions: **C = C1 ratified** (keep both live
> `ProgressSegment`s + the sealed `Output`) **+** a
> first-class **differentiable-`CellKind` / cell-history
> filtering** requirement; **A deferred** (granularity is a
> Phase-4-implementation detail, not now); **B + Claude
> complexity deferred**, folded into a new
> **boundary-diagnostic-capture track** (timestamped record
> of every shell/Claude-CLI event → analyse boundary signals
> from data, cmd/PowerShell first). Order:
> **Phase 6a now → boundary-diagnostic track → Phase 4/4b/5
> → Phase 6b+ filtering / Phase 7 oracle.** Canonical detail:
> [ADR 0007 § Re-sequencing amendment — 2026-05-17](adr/0007-canonical-iocell-history-navigation.md#re-sequencing-amendment-2026-05-17-maintainer-ratified).
> Phase 6a's NVDA dogfood remains the hard D8 ratification
> gate.
>
> **⇒ Phase 6a COMPLETE & VALIDATED (2026-05-17).** All
> three sub-PRs shipped CI-green and the **hard D8 gate is
> CLEARED**: **6a-1 (#404)** `CellEventBus` (the canonical
> *dedicated typed cell bus*, parallel to the
> byte-`OutputDispatcher`; settled the `pty-speak.cell.*`
> taxonomy) + `cell.focused`; **6a-2a (#405)**
> `cell.appended` off the `appendCell` seal site (+ pure
> `cellCount`/`cellViewsFrom`); **6a-2b (#408)** the
> focusable **flat `ListBox` → UIA `List`** + the
> `Ctrl+Shift+Left/Right` pane switch + Interface→Pane menu.
> **`52-ADR7-P6a` dogfood PASSED → D8 control type RATIFIED
> = flat `List`** (reads cleanly under NVDA; pane switch
> both directions; SpeechCursor manual nav independent;
> startup focus = terminal; no double-speak — the parallel
> focus-marker behaved as designed). `Tree` = deferred
> Phase-4/5 growth path (typed model unchanged).
> **Surfaced (NOT a D8-blocker — pane-switch focus plumbing):**
> after the WPF `Menu` (its own focus scope) was entered,
> focus could get trapped in the menu (arrows drove menu
> items, unrecoverable); plus a low-vision request to make
> the selected row visibly obvious. **6a-2b follow-up
> shipped** (`Keyboard.Focus` cross-scope in both pane
> handlers + select-latest-on-enter + high-contrast
> `SystemColors` selection recolor + larger font) →
> **re-dogfood `52-ADR7-P6a-fix` PENDING; Phase-6+ proceed
> is gated on it** (D8 itself stays ratified).
> **Bus architecture RESOLVED (maintainer 2026-05-17):**
> keep the **two specialised typed sources** (`CellEventBus`
> + `OutputDispatcher` — collapsing them = the ADR 0008
> anti-pattern); add a thin universal-tap façade **only
> when a real second universal sink exists** (the
> spatial-audio work), not speculatively. Canonical detail:
> [ADR 0007 6a-2b "Architectural conformance" + the resolved
> open-decision](adr/0007-canonical-iocell-history-navigation.md)
> + ship log.
> **NEXT per the ratified re-sequencing ([#403 amendment](adr/0007-canonical-iocell-history-navigation.md#re-sequencing-amendment-2026-05-17-maintainer-ratified)):**
> the **boundary-diagnostic-capture track** (a
> high-resolution-timestamped record of every shell /
> Claude-CLI event → determine begin/end-of-evaluation
> boundary signals from recorded data, cmd/PowerShell
> first, Claude deferred) → then Phase 4/4b/5 (segments,
> informed by the capture) → Phase 6b+ filtering / Phase 7
> oracle. The boundary track's detailed design is itself a
> scoped work item — specify when taken up (ADR 0007),
> deliberately not built speculatively.
> **BINDING (maintainer deep-review 2026-05-17 — see
> [ADR 0007 6a-2b "Architectural conformance"](adr/0007-canonical-iocell-history-navigation.md)):**
> list-item focus → screen reader announces **natively**;
> we do **NOT** manually `Announce` items (double-speak) —
> instead the list's focus/selection handler publishes
> `CellEventBus.Focused` *in parallel* (universal bus still
> fed for earcon / spatial-audio / braille / separate-
> process sinks). SpeechCursor manual tooling stays
> **separate** code (the #404 `Focused`-off-`runSpeechCursor*`
> publish is the *legacy keyboard model's* source; the list
> model's source is its own focus handler — both coexist,
> decoupled). Core never depends on WPF (frontend
> publishes interaction events / subscribes lifecycle
> events via the bus contract → interface is
> separate-process-capable). Flat list is a temporary
> scaffold; Tree swap stays trivial (typed event → future
> "N sub-components" earcon, no list computation). **Plus
> an OPEN DECISION for the maintainer** (recorded in the
> ADR, NOT a sidetrack): single universal tap-point vs.
> `CellEventBus`-alongside-`OutputDispatcher` — gates only
> 6a-2b's focus-handler publish target.
> The session checkpointed here deliberately: 6a-2b is the
> single locally-unverifiable (no NVDA / no compiler)
> dogfood-gating PR and warrants a fresh full-rigor pass,
> not a rushed tail-of-session one.
> Original recovery brief continues below.

> **NEW / RECOVERED SESSION START HERE →
> [`CYCLE-52-R5-PLAYBOOK.md`](CYCLE-52-R5-PLAYBOOK.md)** — the
> self-contained R5 brief + pruning sequence + ops playbook
> (written so a lost chat session lands exactly where the
> prior one was). It points onward to
> [`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md)
> (**Accepted 2026-05-15**; reads ADR 0005 as its mechanism
> spec). **Cycle 52 R1–R4+R4c + R5 COMPLETE & VALIDATED
> (#347–#375, 2026-05-16).** Three-layer re-foundation
> structural + CI-enforced; KI-R2-1 fixed; cmd OSC-133
> (A/B/D); **R5a selection seam + R5b PowerShell adapter
> shipped, and the R5b NVDA dogfood PASSED — the #1 R5 risk
> resolved positive** (PowerShell emits OSC-133 under a
> screen reader via the `prompt` function even though
> PSReadLine is auto-disabled; `echo hi`→"hi" clean; real
> exit code; `;C` unreachable-under-screen-reader = the
> final design, not a gap). **P1–P5 pruning COMPLETE; R6a
> (hybrid progress streaming) + R6b (prompt-path verbosity)
> + R6b-followup (3 more on-change modes) SHIPPED &
> dogfood-PASSED (matrix `52-R6a`/`52-R6b` ✅, #383/#385 +
> followup). **R6c was redirected by the maintainer
> (2026-05-16) from a dead-code "quick patch" to a
> comprehensive design review — now
> [ADR 0007](adr/0007-canonical-iocell-history-navigation.md)
> (**Accepted 2026-05-16**): SpeechCursor as the canonical
> navigable IOCell history (D1 typed cells / D2 per-cell ops
> / D3 live-trickle / D5a focusable standard control + pane
> switch / D6 on-send test-oracle / D7 fenced off from legacy
> / D8 standard UIA control, `Tree`-rec, 6a-dogfood-gated /
> D9 cell events on the canonical pipeline). D9's principle
> elevated to **[ADR 0008](adr/0008-maximal-semantic-surfacing.md)**
> (Accepted). **Implementation: Phase 0→3 shipped CI-green;
> re-sequenced 2026-05-17 (see the top banner) — Phase 6a now
> → boundary-diagnostic track → Phase 4/4b/5 → Phase 6b+ /
> Phase 7**, then R6d (PS-diagnostics submenu) → R7 (claude +
> closure). *(Superseded next-step text — the top
> 2026-05-17 banner is authoritative; this line kept for
> recovery context.)*
> The maintainer-flagged "prompt announce interrupts the
> output read" is parked as ADR 0006 §"Deferred to R6+"
> item 10 (NOT addressed now, maintainer's call). The cmd
> announce-heuristic FREEZE still stands. Historical detail
> kept below for recovery.
> **The R1–R4 "foundation" dogfood** was signed off
> 2026-05-16, gating (and now cleared for) R5.
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

**CURRENT next stage (2026-05-18): ratify [ADR 0010](adr/0010-interaction-strategy-structured-runner-vs-passthrough.md)
— the interaction-strategy fork.** The boundary-diagnostic-
capture pass that the *previous* "next stage" called for is
**done** (#417–#429); the cell-seal defect is characterised
from data and the fix is specified + oracle-guarded
([`docs/boundary-capture/README.md`](boundary-capture/README.md)).
Before implementing it, the maintainer asked the
first-principles question: does raw-shell-passthrough +
semantic segmentation earn its keep for the actual goal (a
clean Windows coding interface for blind devs), versus a
structured command-runner. ADR 0010 (**Proposed**) records
the analysis + three options and **must be ratified before
implementation resumes**:

- **A — two-mode reframe (recommended):** structured
  command-runner as the *primary* surface (exact boundaries +
  real exit codes *for free*, no scraping for the ~80%
  non-interactive path) with the existing raw-PTY passthrough
  demoted to an explicit secondary "interactive terminal"
  mode. Reuses the three-layer/IOCell/CellEventBus/history/
  oracle investment via the `ShellAdapter` seam.
- **B — stay the course:** implement the already-specified
  one boundary/extraction fix + the independent C2
  channel-layer fix, oracle-guarded; raw terminal stays the
  single primary surface.
- **C — hybrid auto-detect.**

On ratification, the chosen option's **Consequences** list
becomes the cycle plan and this section is rewritten to it.
ADR 0007 Phase 4/4b/5 (segments) + 6c/6d + the real
cell-nav/per-cell-op implementations behind the enabled
`(not yet implemented)` placeholders follow once cells
reliably seal — under whichever mode A/B/C makes primary.

*Historical (shipped) — R4c / R5 / R6 below are DONE; kept
as the build-order trail, not "next":*

**R4c — cmd CommandFinished completion (SHIPPED, pre-R5):**
prepended a *boundary-only* deferred `;D` (`$e]133;D$e\`) to
the cmd `prompt` template so cmd emits
`BoundaryKind.CommandFinished None` at the head of every
prompt — completing the cmd OSC-133 event stream (`;B`→`;D`
output region) so R5/R6 are genuinely shell-agnostic in the
core. No exit code: cmd has no native `%errorlevel%`-in-
prompt mechanism (clink/doskey rejected — dependency / patch
class); the boundary is what R6 needs, the code is an
OS-level cmd limitation. Net-corrective — the real `;D`
replaced the misplaced Cycle-47 synthetic compensation for
cmd. Folded into the R1–R4 foundation dogfood (matrix
`52-R4c`).

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

**R5 — PowerShell adapter — COMPLETE & VALIDATED
(2026-05-16, #374 R5a + #375 R5b).** Shipped as
`-NoExit -EncodedCommand` injecting a `prompt`-function
emitting `;A`/`;B`/`;D;$LASTEXITCODE` (A/B/D, real exit
code) on the same `CmdOscAB` consumer arm as cmd. **`;C`
is NOT shipped and that is final, not a gap:** PowerShell
auto-disables PSReadLine under a screen reader (the only
use case here), so the `;C`/`PSConsoleHostReadLine` hook
can't run — the A/B/D baseline is the design conclusion,
not a stopgap. The R5b NVDA dogfood passed: PowerShell
spawns under NVDA, `echo hi`→"hi" clean split, real exit
code; the #1 R5 risk resolved positive.

**Next = R6 — feature unlock** (per-line progress, prompt-
verbosity fix, clean SpeechCursor — "trivial on the clean
event stream" per ADR 0006) → **R7** claude adapter
decision + Cycle closure audit. The **P1–P5 pre-R5 pruning
sequence** (heuristic-detector detection-time gate;
Cycle-47 synthetic-compensation shell-guard; dead pathway
config keys; canonical-corpus ESC-byte bug; comment-rot
sweep — all in
[`CYCLE-52-R5-PLAYBOOK.md`](CYCLE-52-R5-PLAYBOOK.md) §5)
is independent and can land in parallel; the cmd
announce-heuristic FREEZE still stands. NVDA matrix rows
per stage in
[`ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).

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
- **Phase 6b dogfood round 2 known limitations** (2026-05-17;
  detail in [ADR 0007](adr/0007-canonical-iocell-history-navigation.md)
  Phase 6b note). NOT cell-history-list bugs — the list
  faithfully projects `CellEventBus.Appended` off the seal
  site: (a) no live output trickle (= Phase 4, by design);
  (b) the input-test seals no command cell (ADR 0004
  drop-on-None / Cycle 52 boundary detection, upstream).
  (c) history not reset on shell hot-switch — accepted;
  **shipped** a shell-switch **marker notice row** with the
  row-model unification (`cellHistoryRows` = one
  `CellView option` collection 1:1 with the displayed items;
  retired the parallel-array + placeholder hack). (a)/(b)
  remain the open computational-accuracy items.
- **Cell-history focused-item outline-on-blur** (future
  feature, deferred to the stable Tree representation per
  maintainer 2026-05-17): when focus leaves the cell-history
  pane the selected row should switch from a filled
  background to an outline-only indication. Not built now —
  revisit with the Tree (ADR 0007 Phase 4/5 / D8 growth
  path); the menu's enabled `(not yet implemented)`
  placeholders are the parallel op-set sketch.
- **Output Verbosity** is **NOT** deprecated (checked
  2026-05-17): it still gates the live output-announce path
  (`ShellPolicy.Streaming`); distinct from the post-hoc
  cell-history review, so it stays. Recorded so the question
  isn't re-litigated.

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
