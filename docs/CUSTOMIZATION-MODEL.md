# Customization Model

> **Snapshot**: 2026-05-07
> **Status**: design / forward-looking — research-stage sketch; specifies what the substrate must NOT preclude; no current implementation
> **Authoring item**: backlog item 31 (research stage)
> **Companion docs**:
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational vocabulary; the 12 stages this principle attaches to
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — substrate that gains per-tuple trace metadata under this principle
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing; SIM responsibility extends to introspection
> - [`PANE-MODEL.md`](PANE-MODEL.md) — UI composition; introduces "Pipeline Inspector" pane (or extends an existing reserved pane) under this principle
> - [`USER-SETTINGS.md`](USER-SETTINGS.md) — parameter atlas; override-rule TOML schema lands here when substrate ships
> - [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md), [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md), [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md), [`AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md), [`AUDIT-DOC-CURRENCY.md`](AUDIT-DOC-CURRENCY.md), [`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md) — audit baselines
> - [`CHANNEL-ARCHITECTURE.md`](CHANNEL-ARCHITECTURE.md) — channel-based-communication principle. Channels are the seams the user inspects + customises; the Customization principle's per-stage swapping maps to per-consumer swapping behind a channel.

## What this document is

A **forward-looking sketch** for the architectural
principle that **the entire pty-speak pipeline should be
user-introspectable and user-customizable**. Every
pipeline operation should be:

1. **Named and addressable**.
2. **Swappable** — alternative implementations exist or
   can be authored.
3. **Inspectable per-output** — the user can see which
   alternative ran for any given output.
4. **User-authorable as override rules** — picking an
   alternative persists as a rule.
5. **Context-keyed** — rules attach to context tuples
   (shell, command, semantic-category, etc.) so similar
   future outputs apply the rule automatically.

The doc names the principle, illustrates it with two
concrete use cases, specifies what existing + future
substrate **must NOT preclude** to enable this future
user experience, and surfaces open questions for
maintainer review.

It is deliberately a **sketch**, not a full spec or
implementation plan. Per the maintainer's framing
2026-05-07: "I'm describing an end-user experience and
workflow rather than core infrastructure code, but I'm
hoping that the implementation will allow for such
features to be available to the user down the road."

## Why this exists

The audit phase (PRs #172, #175-#180) closed
2026-05-07. In the same session, the maintainer added a
substantial new architectural input that wasn't yet
captured anywhere in the substrate-research-doc
collection.

The maintainer's directive verbatim:

> "for any given output that is displayed in the current
> output pane it should be very easy for me as a user to
> view the current step-by-step parts and display flow
> and to be able to change any one of the steps on the
> fly. Since there is an initial raw received message
> from the Shell, then we should store that and then
> apply a cascade of sequence, passing and display,
> formatting rules that end up creating the final output
> that the user interacts with. Each one of those
> sequenced operations should be editable so that if I
> get an output that is not being displayed correctly,
> that should have an interactive selectable list for
> instance but is currently being displayed this raw
> text, then I should be able to view the parcel and
> display steps and choose from a drop-down at each step
> to try a different display function."
>
> "The same process of customization would need to apply
> to the decision heuristics that led to the incorrect
> display output, processing flow so that other outputs
> that are similar, would change their default display to
> be the new customized display flow, which would include
> contextual data such as the current shell and the
> command that was Run in the input."
>
> "The similar end-user experience that would be enabled
> to good architectural choices now would be allowing for
> things like auto complete to have an easily configured
> sequence of documentation files that can be ordered and
> unchecked or checked to help with code completion and
> on the fly reference information."

This is a coherent architectural principle. Capturing it
NOW (while the framing is mental-model-clear) prevents
drift; subsequent implementation cycles consume this
doc as a constraint on design choices.

## Audience

Three intended readers:

1. **The maintainer**, when reasoning about whether a
   future feature is "customization-substrate work" or
   a separate concern.
2. **Future Claude sessions**, when implementing
   substrate components — this doc constrains what
   "good architecture" looks like.
3. **Future contributors**, when navigating user-facing
   customization code (when it lands).

Like other research-stage docs, this is **NOT a
user-facing guide**. End-user documentation for
customization workflows lives elsewhere when those
workflows ship.

## Reading order

1. **The customization principle** — five-point
   definition.
2. **Two illustrative use cases** — output-side
   (display correction) + input-side (autocomplete
   sources).
3. **What the substrate must not preclude** — per-doc-
   layer requirements.
4. **The pipeline-trace data model** (sketch).
5. **The override-rule data model** (sketch).
6. **The introspection UI sketch** — Pipeline Inspector
   pane or alternative.
7. **Composition with existing substrates** —
   cross-reference matrix.
8. **Substrate gaps** — what doesn't exist today.
9. **Versioning + maintenance** — snapshot model.
10. **Open questions** — 7 for maintainer review.

## The customization principle

Pty-speak's pipeline (12 stages per
[PIPELINE-NARRATIVE](PIPELINE-NARRATIVE.md), plus
auxiliary concerns) is a sequence of named operations.
Each operation transforms input data into output data:
parser bytes into cell mutations, screen state into
canonical-state diff, diff into payload, payload into
NVDA announcement, etc.

**The customization principle**: every such operation
should satisfy these five properties.

### 1. Named and addressable

Each operation has a stable identifier (e.g.
`stage7.row-level-diff`, `stage8.suffix-detection`,
`stage10.passthrough-profile`). The identifier is part
of the operation's contract; it does NOT change between
PRs without explicit migration. Identifiers are
hierarchical so future stages can be inserted without
renumbering.

Today: implicitly true at the *module* level (each
module is named) but not at the *operation* level
(stages are described in PIPELINE-NARRATIVE prose, not
addressed by ID).

### 2. Swappable — alternative implementations

For each operation, **alternatives** can be registered.
An alternative is a function that has the same input /
output contract as the default but implements the
transformation differently. Examples:

- `stage7.row-level-diff` default: hash-based row diff.
  Alternative: character-level diff. Alternative:
  semantic-line-diff (groups multi-line outputs into
  units).
- `stage10.profile-claim` default: PassThroughProfile.
  Alternative: a future SelectionProfile that detects
  list outputs and emits SelectionShown events.
  Alternative: AI-summarisation profile that condenses
  long output blocks.
- `stage12.NVDA-dispatch` default: ImportantAll
  notification processing. Alternative: MostRecent (drops
  intermediate). Alternative: Queued (defers).

Alternatives can be:
- **Built-in** — ship with pty-speak.
- **User-authored** — installed via TOML / extension.
- **AI-suggested** — a future LLM-assisted authoring
  workflow.

### 3. Inspectable per-output

Every output flowing through the pipeline carries (or
can be reconstructed to carry) its **trace**: which
alternative ran at each stage. A user looking at a
specific announcement on screen can ask "show me the
trace for this output" and see:

```
Output: "Build failed: error: undefined variable foo"
Trace:
  stage1.byte-ingestion       → default
  stage2.parser-application   → default
  stage3.notification-emission → default
  stage4.canonical-state      → default
  stage7.row-level-diff       → default
  stage8.suffix-detection     → default
  stage9.payload-assembly     → default (no truncation; 38 chars)
  stage10.profile-claim       → PassThroughProfile +
                                 ColorDetection (ErrorLine)
  stage11.channel-rendering   → NvdaChannel (ImportantAll) +
                                 EarconChannel (error-tone) +
                                 FileLoggerChannel
  stage12.NVDA-dispatch       → activity=pty-speak.output,
                                 processing=ImportantAll
Context: shell=cmd, command="cargo build", semantic=ErrorLine
```

The user reads the trace, decides "I'd rather have
ErrorLine route to a different earcon for cargo
specifically", and authors a rule (next property).

### 4. User-authorable as override rules

The user picks an alternative for a specific stage.
The pick becomes a **rule**:

```
rule: when shell=cmd AND command-prefix="cargo" AND
      semantic=ErrorLine
      → stage11.earcon-channel.alternative=cargo-error-earcon
```

Rules persist (TOML or similar). The user can author
rules:
- **Inline** — from the trace view, dropdown alternative
  → "save as rule for this context".
- **By template** — from a settings UI, pick a template
  ("when X, do Y").
- **By DSL** — a future text-based rule format for
  power users.

Rule authoring should be **forgiving** — invalid rules
warn at load time + are skipped, never crash the
substrate.

### 5. Context-keyed

Rules carry **context predicates**. A rule applies when
its predicate matches the current output's context.
Context fields include:

- **Shell** — `cmd`, `powershell`, `claude`, future
  shells.
- **Command** — exact match or pattern (`cargo build`,
  `cargo *`, regex).
- **Semantic category** — `StreamChunk`, `ErrorLine`,
  `WarningLine`, etc.
- **Output pattern** — regex or substring match on the
  raw output.
- **Exit code** — for SessionModel-aware rules.
- **Timing** — within N seconds of command start; only
  during streaming; etc.
- **User-defined tags** — future enhancement.

Rule precedence (when multiple rules match) is an open
question (Q6 below).

## Two illustrative use cases

### Use case 1 — Output-side display correction

**Scenario**: User runs `gh pr list` in PowerShell.
Output is a list of PRs with metadata, but it streams as
raw text and NVDA reads it as one long announcement.
The user wants it rendered as a navigable selection
list (one PR per item, NVDA can navigate between them).

**Today's behavior**: StreamPathway emits a
StreamChunk with the full payload. NVDA reads it
linearly.

**Customization workflow**:

1. User presses a hotkey (proposed: `Ctrl+Shift+I` for
   "inspect last output") OR navigates to the
   misbehaving output in a future Pipeline Inspector
   pane.
2. The trace appears, showing each stage's alternative.
3. At `stage10.profile-claim`, the user sees the
   default PassThroughProfile is selected. A dropdown
   offers alternatives: `SelectionProfile (heuristic)`,
   `SelectionProfile (gh-cli aware)`,
   `AISummariseProfile`.
4. User picks `SelectionProfile (gh-cli aware)`. The
   output re-renders as a selection list. NVDA reads
   "1 of 12 pull requests; current: #142 ...".
5. User confirms. A dialog asks "save as rule? for what
   context?". Default-suggested context:
   `shell=powershell AND command-prefix="gh pr list"`.
   User confirms or refines.
6. Rule persists. Future `gh pr list` outputs in
   PowerShell apply `SelectionProfile (gh-cli aware)`
   automatically.

**Substrate requirements** for this scenario:
- Stage 10 has an alternatives registry exposing
  `PassThroughProfile`, `SelectionProfile (heuristic)`,
  `SelectionProfile (gh-cli aware)`, etc.
- Per-output trace capture (which profile ran).
- Re-render capability — apply an alternative
  retroactively to the captured raw output.
  (SessionModel's per-tuple raw-output storage enables
  this.)
- Override-rule storage with context predicates.
- Pipeline Inspector pane (or modal) UI.

### Use case 2 — Input-side autocomplete with ordered sources

**Scenario**: User types in a future shell pane.
Autocomplete suggestions come from multiple sources:
shell history, language-server, user-curated docs, LLM
suggestions. User wants to reorder these sources
(prioritize language-server over shell history) or
toggle individual sources off entirely.

**Today's behavior**: No autocomplete pipeline exists.
The shell's own line-editor handles autocomplete; pty-
speak doesn't intervene.

**Customization workflow** (when input pathway ships):

1. User opens a settings UI (proposed: a future
   Pipeline Inspector pane variant for input pipelines).
2. The pane lists the autocomplete-source cascade
   (default ordering):
   - 1. shell-history
   - 2. language-server (if configured)
   - 3. user-curated-docs (file path)
   - 4. llm-suggestions (if LLM-pane enabled)
3. User reorders: drags `language-server` to position
   1.
4. User toggles `llm-suggestions` off.
5. User adds a custom source: a path to a markdown
   reference file. The source registers as
   `user-curated-2`.
6. Configuration persists. Future autocomplete prompts
   use the new ordering + sources.

**Substrate requirements** for this scenario:
- Input pathway protocol (Phase 2 substrate).
- Autocomplete-source registry (alternatives).
- User-orderable cascade.
- Per-source enable/disable.
- User-authored source registration (file paths;
  custom sources).

The same customization principle applies whether the
pipeline is an output cascade or an input cascade —
both are sequences of named, swappable operations.

## What the substrate must not preclude

For each existing + future substrate layer, this
section enumerates what the customization principle
requires. Implementation does NOT need to ship today;
substrate decisions today must NOT make implementation
infeasible later.

### PIPELINE-NARRATIVE (operational mechanics)

- **Each of the 12 stages** has an explicit
  identifier (`stage1.byte-ingestion` through
  `stage12.NVDA-dispatch` plus auxiliary IDs for
  mode-barrier, spinner, etc.).
- **Alternatives registry** per stage: future module
  exposing `Map<StageId, Map<AlternativeId,
  AlternativeImpl>>`. Today's defaults register as
  `default` alternative.
- **Per-output trace** capture: when an output flows
  through, each stage records "I am `stage8.suffix-
  detection`, alternative `default`, transformed
  input X to output Y in N μs". Today's substrate
  doesn't capture this; it would be a new event-tap
  mechanism similar to `installEventTap` from PR #165
  but spanning all stages.

### SESSION-MODEL (history substrate)

- **Per-tuple trace storage**: when a SessionTuple
  closes, it carries the trace data for outputs in
  its window. Trace data may be lazy / on-demand to
  avoid memory pressure.
- **Raw-output retention**: SessionModel already
  stores raw output bytes per tuple (per
  SESSION-MODEL.md §4 design). Customization adds:
  re-render-from-raw capability when user picks an
  alternative.
- **Query by trace**: the user can ask "show me all
  outputs that ran `stage10.profile-claim` =
  `default`" to find candidates for customization.

### INTERACTION-MODEL (architectural framing)

The Shell Interaction Manager (SIM) gains a sixth
responsibility:

6. **Trace + introspection surface** — capture per-
   output trace; expose introspection API for the UI
   (Pipeline Inspector pane). Apply override rules to
   shape stage selection.

This responsibility may emerge as a literal sub-module
("Trace Recorder", "Override Engine") when implementation
begins. The conceptual placement is within the SIM's
"semantic event production" + "document model
maintenance" responsibilities (§4 of INTERACTION-MODEL).

### PANE-MODEL (UI composition)

- **New pane type: Pipeline Inspector** — joins the 5-
  entry catalog (shell + file tree + cherry-picked I/O +
  language docs + AI assistance). The Pipeline Inspector
  shows per-output trace + offers dropdowns for
  alternatives.
- **Alternatively** — extend the **Cherry-picked I/O
  pairs pane** to support trace inspection per pinned
  output. The cherry-picked pane already knows about
  (command, output) pairs; adding trace + dropdowns
  fits naturally.
- Pane Coordinator gains: pane-to-pane "show trace for
  this output" coordination protocol.

The introspection UI is a future pane; substrate
decisions today must not preclude registering it.

### USER-SETTINGS (parameter atlas)

- **Override-rule TOML schema** lands here:
  ```
  [[rule]]
  when = { shell = "powershell", command_prefix = "gh pr list" }
  then = { stage = "stage10.profile-claim",
           alternative = "SelectionProfile (gh-cli aware)" }
  ```
- **Atlas grows** to document each stage's available
  alternatives + their parameters.
- **Per-context schema validation** — atlas describes
  which context fields are queryable + their types.

Implementation defers; atlas reservation marks this as
📋 reserved.

### Spec (`event-and-output-framework.md` etc.)

When spec re-authoring resumes (deferred per Track C
Tier 3), the customization principle becomes a
spec-level commitment with named contracts:

- Alternatives-registry contract.
- Trace-capture contract.
- Override-rule application contract.

ADR-required per CLAUDE.md when added.

### Track A audit (code consistency)

Future customization-substrate code passes Track A's
naming-consistency bar:
- Each alternative has a stable name matching its
  registration ID.
- Stage IDs match PIPELINE-NARRATIVE.

### Track B audit (test inventory)

Customization substrate gains its own test cluster:
- Alternatives-registry registration / lookup.
- Trace-capture round-trip.
- Override-rule application precedence.
- Re-render-from-raw correctness.

Per Track B's pattern: property-based tests for
substrate invariants.

### Track D audit (atlas alignment)

Override rules' TOML schema is an atlas extension.
Atlas grows alongside customization-substrate
implementation; orphan-detection includes
"alternatives without atlas entries".

## The pipeline-trace data model (sketch)

Sketched, not specified. Concrete F# / data shapes
deferred to implementation cycle.

```fsharp
type StageId = string  // "stage8.suffix-detection"
type AlternativeId = string  // "default", "user-authored-3"

type StageInvocation = {
    StageId: StageId
    Alternative: AlternativeId
    InputDigest: uint64    // hash of input
    OutputDigest: uint64   // hash of output
    DurationMicros: int
    ErrorMessage: string option  // if alternative failed
}

type OutputTrace = {
    OutputId: Guid
    Timestamp: DateTime
    Context: ContextTuple
    Invocations: StageInvocation array
}

type ContextTuple = {
    Shell: ShellId
    Command: string option   // None if input-side trace
    SemanticCategory: SemanticCategory option
    OutputPattern: string option  // first 80 chars of raw
    ExitCode: int option
}
```

**Memory model**: traces are ephemeral by default
(retained for the lifetime of the SessionTuple they
attach to). Long-term persistence is opt-in
(power-user feature; persists across sessions for
forensic debugging).

**Query API** (sketch):
- `Tracer.getTrace(outputId) → OutputTrace option`
- `Tracer.queryByContext(predicate) → OutputTrace seq`
- `Tracer.queryByAlternative(stageId, altId) → OutputTrace seq`

## The override-rule data model (sketch)

```fsharp
type StagePredicate =
    | ShellEquals of ShellId
    | ShellIn of ShellId list
    | CommandEquals of string
    | CommandPrefix of string
    | CommandRegex of string
    | SemanticEquals of SemanticCategory
    | OutputContains of string
    | OutputRegex of string
    | ExitCodeEquals of int
    | ExitCodeNonZero
    | And of StagePredicate list
    | Or of StagePredicate list
    | Not of StagePredicate

type OverrideRule = {
    RuleId: Guid               // stable across edits
    DisplayName: string        // user-readable
    When: StagePredicate
    Then: Map<StageId, AlternativeId>
    Priority: int              // higher wins on conflict
    Disabled: bool             // user can toggle without delete
    AuthoredAt: DateTime
    ContextSnapshot: ContextTuple option  // captured at authoring
}
```

**Persistence**: rules persist to TOML (per Q3 below).
Schema example:

```toml
[[rule]]
id = "550e8400-e29b-41d4-a716-446655440000"
display_name = "Render gh pr list as selection in PowerShell"
priority = 10
disabled = false
authored_at = "2026-05-08T14:32:00Z"

[rule.when]
type = "And"

[[rule.when.predicates]]
type = "ShellEquals"
value = "powershell"

[[rule.when.predicates]]
type = "CommandPrefix"
value = "gh pr list"

[rule.then]
"stage10.profile-claim" = "SelectionProfile (gh-cli aware)"
```

**Application order**:
- All matching rules collected.
- Sorted by priority (descending).
- First match for each stage wins.
- Conflict reporting: when multiple rules try to set
  the same stage, the loser is logged.

**Validation**:
- Unknown stage IDs / alternative IDs → warning at
  load time + skip rule.
- Malformed predicates → same.
- Circular references → not possible (rules don't
  reference other rules).

## The introspection UI sketch

The Pipeline Inspector is a future pane (per
[PANE-MODEL.md](PANE-MODEL.md) catalog extension). Layout
sketch:

```
┌──────────────────────────────────────────────────┐
│ Pipeline Inspector — Output #4521                │
├──────────────────────────────────────────────────┤
│ Context: shell=powershell, cmd="gh pr list",     │
│          semantic=StreamChunk, exit=0            │
│                                                  │
│ Stage trace:                                     │
│   1 byte-ingestion           [default]      ▼   │
│   2 parser-application       [default]      ▼   │
│   3 notification-emission    [default]      ▼   │
│   4 canonical-state          [default]      ▼   │
│   ...                                            │
│  10 profile-claim            [PassThrough]  ▼   │
│       Alternatives:                              │
│         • PassThrough (current)                  │
│         • SelectionProfile (heuristic)           │
│         • SelectionProfile (gh-cli aware)        │
│         • [Author new alternative...]            │
│   ...                                            │
│                                                  │
│ Output:                                          │
│   "1. PR #142 — feat: add foo                   │
│    2. PR #141 — fix: bar                        │
│    ..."                                          │
│                                                  │
│ [Try alternative] [Save as rule] [Close]         │
└──────────────────────────────────────────────────┘
```

Accessibility considerations (cross-reference to
[PANE-MODEL §10](PANE-MODEL.md) "the hard problems"):

- Pane focus, NVDA review cursor, ActivityIds — all
  per the Pane contract.
- Stage-trace list uses UIA tree pattern (each stage
  is a tree node; alternatives are child nodes).
- Dropdown alternatives use UIA combobox pattern.
- Rule-authoring confirmation uses standard accessible
  dialog idioms.

The UI is sketched, not specified; implementation
cycle decides concrete shape.

## Composition with existing substrates

| Concept (this doc) | PIPELINE-NARRATIVE | SESSION-MODEL | INTERACTION-MODEL | PANE-MODEL | USER-SETTINGS |
|---|---|---|---|---|---|
| Stage IDs | stages 1-12 | (not relevant) | SIM responsibility 6 | (not relevant) | (not relevant) |
| Alternatives registry | per-stage | (not relevant) | SIM owns registration | (not relevant) | atlas grows with alternatives |
| Per-output trace | new substrate | per-tuple metadata | SIM responsibility 6 | Pipeline Inspector pane consumes | (not relevant) |
| Override rules | shapes stage selection | (not relevant) | SIM applies rules | Inspector authors rules | TOML schema lives here |
| Context keying | (not relevant) | active tuple's CommandText / ExitCode | SIM provides context | (not relevant) | predicate validation |
| Re-render from raw | (not relevant) | SessionModel raw-output storage enables | SIM orchestrates re-render | Inspector triggers | (not relevant) |

## Substrate gaps

Six pieces don't exist today and would need to be built
for the customization principle to be implementable:

### 1. Alternatives registry per pipeline stage

No `Map<StageId, Map<AlternativeId, AlternativeImpl>>`
exists. Today: each stage has one implementation,
hardcoded.

Implementation needs:
- A registration API per stage.
- Default alternatives register at app startup.
- User-authored alternatives load from config / extension.

### 2. Per-output trace capture

No mechanism captures "which alternative ran at each
stage for this specific output". Today's pipeline runs
synchronously through fixed code paths.

Implementation needs:
- Trace-capture mechanism (likely an extension of
  `OutputDispatcher.installEventTap` from PR #165 to
  span all stages, not just the dispatch step).
- OutputId generation + propagation through stages.
- Memory-bounded trace retention.

### 3. Override-rule persistence

No TOML schema for `[[rule]]` entries; no rule loader;
no rule application engine.

Implementation needs:
- TOML schema design (sketched in this doc).
- Loader with validation.
- Application engine (predicate matching + stage
  override).

### 4. Context-keying engine

No engine that asks "given this output's context, which
rules apply?".

Implementation needs:
- Context tuple construction (data sourced from
  SessionModel + SIM).
- Predicate matching.
- Conflict resolution.

### 5. Pipeline Inspector pane (or extension to existing pane)

No UI exists for inspection.

Implementation needs:
- Pane abstraction (per PANE-MODEL — itself not yet
  implemented).
- Specific Pipeline Inspector pane logic.
- OR extension to a reserved pane (Cherry-picked I/O
  is a candidate).

### 6. Authoring UX

No UI exists for users to author / edit / save rules.

Implementation needs:
- In-pane dropdown UI per stage.
- Rule-saving dialog with context-tuple suggestions.
- Settings UI for browsing / editing existing rules.

All six gaps are forward-looking. The customization
principle is satisfied when all six exist.

## Versioning + maintenance

Follows the snapshot model established by
PIPELINE-NARRATIVE / SESSION-MODEL / INTERACTION-MODEL /
PANE-MODEL.

### When to re-snapshot

Trigger conditions:
- A pipeline stage gains its first alternative.
- The first OverrideRule schema lands.
- Pipeline Inspector pane (or equivalent) ships.
- Maintainer's framing of the principle shifts.

### What stability means

The principle (5-point definition) is intended to be
**stable across implementation cycles**. The substrate
gaps catalog will shrink as implementation lands; the
data-model sketches will firm up; the UI sketch will
crystallize. The **principle itself** doesn't change
just because a piece was implemented.

## Open questions / Resolutions

Seven questions surfaced for maintainer review. As of
2026-05-08, all 7 questions resolved per the Cycle 23
Phase 2 walk-through. Resolutions captured below;
original framing preserved for historical context.

### Q1 — Naming — ✅ Resolved 2026-05-08

**Resolution**: KEEP `CUSTOMIZATION-MODEL.md`.

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): names the user's relationship
to the pipeline as a first-class concern; parallels
other research-stage doc names (`SESSION-MODEL.md`,
`INTERACTION-MODEL.md`, `PANE-MODEL.md`); distinct
from `PIPELINE-NARRATIVE.md`. Renaming now would
create vocabulary churn across docs that already
cross-reference this name (PR #181 introduced the
doc; PR #200 cross-referenced it from the audit
backlog).

**Original question**: The doc adopts
`CUSTOMIZATION-MODEL.md`. Alternatives:
- `INTROSPECTION-MODEL.md` — emphasises the see-what's-
  happening capability.
- `USER-AUTHORSHIP-MODEL.md` — emphasises rule
  authorship.
- `PIPELINE-CUSTOMIZATION.md` — more specific.
- `EDITABLE-PIPELINE.md` — terse.

### Q2 — Alternatives registry shape — ✅ Resolved 2026-05-08

**Resolution**: **BOTH** compile-time built-ins +
runtime extensions. Built-in alternatives ship in code
(well-tested + accessible); user-authored alternatives
load from a future `%LOCALAPPDATA%\PtySpeak\extensions\`
directory as `.fsx` scripts.

**Cross-reference**: same plumbing as the input-side
extension loading planned in
`spec/event-and-output-framework.md` §A.5 phase 2;
this resolution applies that pattern to outputs too.
Spec is not edited here per CLAUDE.md spec-immutability
rule — implementation cycle that ships the registry
will land an ADR if spec extension is needed.

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): built-in alternatives give users
a tested baseline that is keyboard- and screen-reader-
accessible by construction; runtime `.fsx` loading lets
power users add new alternatives without recompiling
pty-speak. Reusing the planned A.5-phase-2 plumbing
avoids inventing a parallel extension mechanism for
the output side.

**Original question**: How do alternative
implementations register?
- **Compile-time** — alternatives ship with pty-speak;
  user enables/disables but can't add new.
- **Runtime via TOML extension** — alternatives load
  from a configured directory on startup; no recompile
  needed for new ones.
- **Both** — built-in alternatives compiled in;
  user-authored alternatives load from extension
  directory.

### Q3 — Trace persistence — ✅ Resolved 2026-05-08

**Resolution**: **in-memory per SessionTuple by
default**; **separate persistent ring buffer as opt-in
power-user feature** for forensic debugging.

**Cross-reference**: aligns with SessionModel's
three-tier persistence model
(`memory_only` / `session_log` / `always`) per
[`SESSION-MODEL.md`](SESSION-MODEL.md) §5. The trace
ring buffer is independent of tuple persistence — a
user can persist tuples without persisting traces and
vice versa.

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): default minimises memory + disk
footprint; opt-in ring buffer satisfies the rare
forensic-debugging need without forcing always-on disk
writes. Mirrors the established pattern for
SessionModel persistence — substrate exposes the
capability, user opts in via TOML.

**Original question**: Per-output traces stored where?
- **In-memory per SessionTuple** — survives during the
  tuple's lifetime; lost on session end.
- **Persisted with SessionTuple to disk** — if
  SessionModel persists tuples, traces persist too.
- **Separate trace ring buffer** — opt-in, bounded
  size, persists for forensic debugging.

### Q4 — Rule-context-keying scope — ✅ Resolved 2026-05-08

**Resolution**: **full enumeration at substrate level**
(all `ContextTuple` fields available — shell,
command-prefix, semantic-category, output-pattern,
exit-code, timing); **rule-authoring UX defaults to
recommended subset** (shell + command-prefix +
semantic-category — covers most cases without
overwhelming the user).

**Cross-reference**: rule-authoring UX shape lives with
[`USER-SETTINGS.md`](USER-SETTINGS.md) (override-rule
schema, lands when override-rule implementation cycle
begins).

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): substrate-level full enumeration
future-proofs power-user rules without changing the
data model later; UX-level subset default keeps the
dropdown-driven authoring experience focused on the
cases users actually reach for. Same substrate-versus-
UX layering as Q3 (substrate exposes; UX surfaces a
sensible default).

**Original question**: Which context fields qualify as
"similar" for rule-context-keying?
- **Minimal** — shell only.
- **Recommended** — shell + command-prefix + semantic-
  category.
- **Full** — all fields enumerated in `ContextTuple`
  including output-pattern + exit-code + timing.
- **User-extensible** — user can add custom context
  fields via tags / annotations.

### Q5 — UI surface for introspection — ✅ Resolved 2026-05-08

**Resolution**: **Pipeline Inspector pane** as a new
entry in the [`PANE-MODEL.md`](PANE-MODEL.md) pane
catalog. **Modal panel** retained as a fallback for
users who want ephemeral inspection without committing
pane real estate.

**Cross-reference**: PANE-MODEL.md catalog will grow a
new row for "Pipeline Inspector pane" + a dedicated
subsection covering producer, content source, hotkey,
navigation, and cross-pane coordination — that edit
lands in its own follow-up cycle when Pipeline
Inspector implementation begins (per audit→fixup-loop
precedent: companion-doc edits defer until the
implementation that backs them lands).

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): the multi-pane workspace
already supports adding new pane types (PANE-MODEL.md
Q1 + Q4 resolved 2026-05-07 confirm the catalog model
+ moderate per-pane TOML-schema scope). Pipeline
Inspector slots in cleanly. Modal panel remains as a
fallback — useful for one-off "what just happened to
this output?" queries where dedicating pane real
estate isn't warranted.

**Original question**:
- **Pipeline Inspector pane** (new pane in PANE-MODEL
  catalog).
- **Modal panel** triggered by hotkey (no new pane).
- **Companion app** (separate window).
- **Extension to Cherry-picked I/O pairs pane** (per
  the catalog).

### Q6 — Rule precedence when multiple match — ✅ Resolved 2026-05-08

**Resolution**: **explicit user-priority field**
(`priority = N` per rule); **default priority computed
from context-specificity at rule-creation time** so
the user doesn't have to think about it for simple
cases.

**Cross-reference**: TOML rule-schema example
(`priority = N` field placement) lands with
[`USER-SETTINGS.md`](USER-SETTINGS.md) override-rule
section in a follow-up cycle.

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): simplest implementation; user
always has agency when intent diverges from what
specificity would compute; sensible defaults keep
simple cases simple. Avoids the "why didn't my rule
fire?" surprise mode of pure most-specific-wins or
pure most-recent-wins.

**Original question**: When multiple rules match the
same context + target the same stage:
- **Most-specific context wins** — implementation needs
  to compare predicate specificity; complex but
  intuitive.
- **Most-recent authored wins** — simple but
  surprising.
- **Explicit user-priority field** — `priority = N`
  per rule; user controls; explicit but verbose.
- **Hybrid** — explicit priority is the tie-breaker;
  default priority computed from context-specificity.

### Q7 — Rule-authoring UX — ✅ Resolved 2026-05-08

**Resolution**: **start with dropdowns + automatic
context-tuple suggestion**; add Recipe DSL + visual
conditional-logic editor later as power-user tooling.

**Cross-reference**: dropdown UI lives inside the
Pipeline Inspector pane (per Q5 resolution above). DSL
+ visual-editor surfaces deferred until first-use
authoring is shipped + pain points surface.

**Rationale** (per maintainer agreement, Cycle 23
Phase 2 walk-through): the maintainer's original
example explicitly described dropdowns as the primary
UX; ship that first. DSL and visual-editor surfaces
are additive — they don't conflict with dropdown-
authored rules and can be added when power-user demand
emerges. Avoids upfront over-engineering.

**Original question**: How does the user author rules?
- **Dropdown at each step** (per maintainer's example) —
  inline in the inspector; pick alternative + confirm
  context.
- **Recipe DSL** — a future text-based rule format
  ("when X then Y") for power users.
- **Conditional-logic editor** — visual builder for
  predicates.
- **All three** — dropdowns for inline edits; DSL for
  power-users; visual editor for complex predicates.

## Companion-doc cross-reference index

Quick reference:
- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — the 12 stages this principle attaches to.
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — per-tuple metadata extension; raw-output storage enables re-render.
- [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — SIM responsibility extension.
- [`PANE-MODEL.md`](PANE-MODEL.md) — Pipeline Inspector pane catalog entry.
- [`USER-SETTINGS.md`](USER-SETTINGS.md) — TOML override-rule schema; alternatives parameter docs.
- [`AUDIT-CODE-CONSISTENCY.md`](AUDIT-CODE-CONSISTENCY.md) — naming-consistency baseline.
- [`AUDIT-TEST-INVENTORY.md`](AUDIT-TEST-INVENTORY.md) — future test cluster recommendations.
- [`AUDIT-SPEC-ALIGNMENT.md`](AUDIT-SPEC-ALIGNMENT.md) — spec-extension recommendation.
- [`AUDIT-ATLAS-ALIGNMENT.md`](AUDIT-ATLAS-ALIGNMENT.md) — atlas extension on rule schema.
- [`AUDIT-DOC-CURRENCY.md`](AUDIT-DOC-CURRENCY.md) — doc-currency baseline.
- [`AUDIT-BACKLOG-VALIDATION.md`](AUDIT-BACKLOG-VALIDATION.md) — item 31 = this doc.
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — branching + PR conventions.

## Change log

| Date | Author | Change |
|---|---|---|
| 2026-05-07 | Cycle 7 (item 31) | Initial snapshot. Captures maintainer's 2026-05-07 directive on user-introspectable + user-customizable pipeline architecture. Names the 5-point principle. Two illustrative use cases (output-side display correction; input-side autocomplete sources). Specifies what existing + future substrate must NOT preclude. Sketches pipeline-trace data model + override-rule data model + Pipeline Inspector UI. 6 substrate gaps. 7 open questions surfaced for maintainer. |
| 2026-05-08 | Cycle 23 Phase 3 | Q1-Q7 resolutions captured per maintainer's Cycle 23 Phase 2 walk-through. All 7 doc-recommended answers selected. Section header updated to "Open questions / Resolutions"; per-question Resolution + Rationale + (where applicable) Cross-reference + Original question blocks added; original question prose preserved verbatim. |

