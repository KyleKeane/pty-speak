# Cycle 52 R1 — current-architecture & dependency map

> **HISTORICAL (2026-05-16).** This was R1's pre-extraction
> evidence base (survey 2026-05-15). **Cycle 52 R1–R4 is now
> complete & merged (#347–#357)**; live state is in
> [`SESSION-HANDOFF.md`](SESSION-HANDOFF.md) and
> [`ADR 0006`](adr/0006-three-layer-refoundation.md). Kept
> for the planning↔shipped delta. **Things this doc says
> that are now untrue:**
> - "Namespace rename deferred to R3" (§"…relocated to
>   *Core assembly*") — **done in R4a (#356)**:
>   `HeuristicPromptDetector` is `namespace Terminal.Shell`,
>   logger category `Terminal.Shell.HeuristicPromptDetector`.
> - Any "deferred to R2/R3/R4" / "in progress" framing —
>   R1–R4 all shipped; R5 (PowerShell adapter) is next,
>   gated on the consolidated R1–R4 foundation dogfood.
> - `file:line` anchors predate the R2–R4 edits (announce
>   path rewritten in R3b, `Terminal.Shell` namespace in
>   R4a) and will have drifted — re-derive against current
>   `main` if precise line numbers are needed.

Evidence base for the ADR 0006 behaviour-preserving
extraction. Distilled from a full read-only codebase survey
(2026-05-15). Every claim carries a `file:line` anchor so R1
edits can be made without re-deriving the map. Companion to
[`adr/0006-three-layer-refoundation.md`](adr/0006-three-layer-refoundation.md).

## Headline finding

The codebase is architecturally sound at the high level
(substrate/channel per ADR 0001, IOCell per ADR 0004, OSC-133
decoder per ADR 0005). **The single load-bearing leak is
`HeuristicPromptDetector` (492 LOC in `Terminal.Core`)** — it
couples the pure core to cmd/PowerShell/Claude prompt regexes.
`Terminal.Core` already has **zero WPF, zero P/Invoke, zero
`Process`** (verified). So R1's job is narrow: move that one
module out, introduce the `ShellAdapter`/`SessionHost` seam,
change nothing observable.

## Layer alignment (ADR 0006 target vs today)

| ADR 0006 layer | Today | R1 action |
|---|---|---|
| **1 Transport** `Terminal.Shell` (new) | `HeuristicPromptDetector` in `Terminal.Core`; spawn/resolve scattered in `Program.fs`; `ShellRegistry`/`PseudoConsole`/`ConPtyHost` in `Terminal.Pty` (correctly placed) | New assembly: `ShellAdapter` iface + `SessionHost`; move `HeuristicPromptDetector` in as the cmd/PS/claude **fallback** translator; `Terminal.Pty` unchanged (P/Invoke + shell strings belong here) |
| **2 Core** `Terminal.Core` (purify) | Already WPF/P-Invoke/Process-free. Sole leak = `HeuristicPromptDetector` | Remove that one file; keep `Osc133`, `Screen`, `ContentHistory`, `SessionModel`, `SpeechCursor`, `ShellInteraction` |
| **3 Channel** WPF+UIA | `Terminal.Accessibility`, `Views`, `NvdaChannel`, `EarconChannel`, `FileLoggerChannel` | Untouched in R1 |
| **Orchestration** `SessionHost` (new) | Implicit, smeared across `Program.fs` (~5.3 kLOC file) | New ~one-file host owns spawn + adapter wiring + active-session/detector/SpeechCursor state + shell-switch reset |

## End-to-end data path (byte → IOCell → AT)

`startReaderLoop` `Program.fs:109` reads PTY bytes → per-byte
`onByteFromPty` `Program.fs:149` (feeds `ShellInteraction`) →
**`Parser.feedArray` `Program.fs:150`** → `Screen.Apply`
`Screen.fs:575‑717` (OSC-133 arm calls `Osc133.tryParse`
`Screen.fs:674`; fires `promptBoundaryEvent` `:716`) →
`ContentHistory.appendFromEvent` `Program.fs:161` →
`HeuristicPromptDetector.tryDetect` `Program.fs:2467`
(`RowsChanged`) → `promptBoundaryEvent` →
**`handlePromptBoundary` `Program.fs:1951‑2027`** →
`SessionModel.applyAndCaptureWithContentHistory`
`SessionModel.fs:867‑890` → **`extractIOCell`
`SessionModel.fs:750‑846` — the sole extractor** (OSC arm
`:756‑772`, heuristic/Seq-watermark arm `:773‑845`, drop-on-
None `:846`) → SpeechCursor append `Program.fs:2024` +
`OutputDispatcher.dispatch` `Program.fs:2215` →
`NvdaChannel.fs` → UIA caret (`Terminal.Accessibility`,
`ContentHistoryTextRange.fs`) per ADR 0002.

## Leak inventory & R4 nuance

- **Must move (R1):** `HeuristicPromptDetector.fs` — shell
  regexes `:98‑114`, per-shell stability windows `:120‑126`,
  `shellParams` lookup `:201‑209`, `tryDetect` `:227‑340`.
  Pure function with caller-threaded mutable state
  (`Program.fs:2467` threads `detectorState`). Signature is
  unchanged by the move.
- **Acceptable — do NOT flag in R4 lint:** `Config.fs:520`
  `knownShellKeys` (TOML validation set); `ShellPolicy.fs:133‑150`
  per-shell policy table (pure lookup, no detection).
  **R4 lint must scope to "no shell-pattern *detection* logic
  / no WPF / no P-Invoke / no `Process`", not a blunt "no
  shell substring"** — else it false-positives on config &
  policy.
- **Deferred tech-debt (NOT R1):** `SelectionDetector.fs:274,468`
  hard-codes `"claude"` (Ink-box selection). Claude-specific
  by design; relocate to a claude channel extension in R6+,
  noted not fixed now.

## R1 seams (the specific wrap points)

| Function | Anchor | R1 |
|---|---|---|
| `Parser.feedArray` | `Program.fs:150` | call site moves behind `ShellAdapter.Translate : byte[] → ShellEvent list` (parser/Screen stay *below* the adapter) |
| `resolveStartupShell` | `Program.fs:2813‑2889` | stays in `Program.fs`; delegates to new `SessionHost.Create(config, shellId)` |
| `HeuristicPromptDetector` | whole file | moves to `Terminal.Shell`; adapter owns the instance + threaded state |
| `handlePromptBoundary` | `Program.fs:1951‑2027` | stays; receives boundary via a `SessionHost` state-change event instead of the raw `promptBoundaryEvent` subscription |
| active session / `ShellInteraction` / `SpeechCursor` / detector state / shell-switch reset (`switchToShell` `Program.fs:4790‑4910`) | — | ownership moves into `SessionHost`; `Program.fs` holds a reference and calls methods |

`ShellEvent` boundary DU (ADR 0006):
`PromptStart | CommandStart | OutputChunk of bytes |
CommandFinished of int option | Raw of bytes`. The core
depends only on this — never bytes/screen/shell-id.

## Behaviour-identity regression net

Lean on, must stay green unchanged: `SessionModelTests.fs`
(largest — pins `extractIOCell` both arms + finalisation),
`HeuristicPromptDetectorTests.fs` (pins detector behaviour
across the move — signature identical, only assembly changes),
`ContentHistoryTests.fs`, `Osc133Tests.fs`, `ScreenTests.fs`,
`SpeechCursorTests.fs`.

**Coverage gap → R1 adds insurance:** no monolithic
"feed real bytes → assert IOCell" integration test, and the
`SessionHost`/shell-switch compose-root wiring is dogfood-only
today. R1 adds one end-to-end bytes→IOCell test so the
extraction has a unit-level equality check, not only the NVDA
dogfood.

## Risks

- **`Program.fs` is ~5.3 kLOC** with reader-loop / shell-
  switch / boundary / hotkey intertwined. Mitigation: move
  only spawn+resolve+adapter+session-state into `SessionHost`;
  leave dispatch + hotkey handling in place.
- **No local `dotnet`** — CI-only, multi-minute round trips.
  Mitigation: R1 is strictly behaviour-identical; read twice
  before push; every commit must pass the full suite; the
  decisive gate is the maintainer NVDA dogfood (`echo hi` /
  test-01 / history-recall narrate **identically** pre/post).
- State is single-threaded on the WPF dispatcher (no races
  found); detector state is caller-threaded — `SessionHost`
  takes ownership, removing the foot-gun.

## R1 commit order — as shipped

1. ✅ **R1.1 (#348)** New `Terminal.Shell` assembly:
   `IShellAdapter` interface + `ShellEvent` DU +
   `SessionHost` skeleton (inert, zero behaviour change).
2. ✅ **R1.2 (#349)** `HeuristicPromptDetector` →
   `Terminal.Shell`, wholesale. Kept `namespace
   Terminal.Core` (no caller churn); detector leaves the
   *Core assembly*. Namespace rename deferred to R3.
3. ✅ **R1.3 (#350)** `Program.fs` shell-resolution →
   `SessionHost.ResolveStartupShell(config, log)`, verbatim;
   `Program.fs` delegates with the same `log` instance →
   byte-identical logs.
4. ✅ **R1.4 (#351)** `startReaderLoop` routed through
   `CmdAdapter`. Per the maintainer's 2026-05-16 decision
   this is a **`VtEvent` seam** — `CmdAdapter.Translate :
   byte[] → VtEvent[]` is a verbatim `Parser.feedArray`
   wrapper. `ShellEvent`/`IShellAdapter` (R1.1) are
   **deliberately left as the R2 boundary**; they only
   become meaningful with R2's OSC-133. (The original plan's
   "bytes→IOCell integration test" was not added — the
   `VtEvent`-stream identity + the full unit suite + the
   consolidated dogfood are the behaviour-identity net;
   revisit a dedicated integration test in R2 when the
   event stream actually changes.)
5. ▶ **R1 gate (next):** one consolidated behaviour-
   identical NVDA dogfood from an **installed preview
   release** (preview.143 @ `66ab95d`), in the maintainer's
   normal environment — **not** a dev `dotnet run`/`publish`
   build (unreliable NVDA UIA-notification delivery on the
   RDP/VM). Acceptance: narrates identically to preview.141
   (residual heuristic bugs included — that *is* behaviour-
   identity). Then R2.

Each step shipped as an independently CI-gated PR. R1 landed
no OSC-133 emitter and no precedence rule — those are R2/R3.
