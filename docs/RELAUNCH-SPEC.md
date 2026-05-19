# Re-launch Specification — an audio-first, multi-agent computational workspace

> **Status:** Draft, 2026-05-19. Authored from an extended design
> dialogue with the maintainer (a blind professor of computer
> science). This document **supersedes the framing of**
> [`docs/adr/0010-interaction-strategy-structured-runner-vs-passthrough.md`](adr/0010-interaction-strategy-structured-runner-vs-passthrough.md)
> — ADR 0010 Option A was directionally right (stop depending on
> raw-terminal scraping for the primary surface) but under-scoped.
> This spec is the wider target ADR 0010 was a step toward. It is
> a **re-launch brief**: written so the effort can be restarted
> from the maintainer's own Windows machine, in a fast local
> loop, using the app to build the app.
>
> Nothing here is built yet. This is the specification, not a
> change. Implementation is a separate, phased, dogfood-gated
> sequence (§13).

---

## 0. How to use this document

This document is itself an instance of the thing it specifies: a
navigable, structured artifact you can enter at any heading,
leave, and return to without losing your place. It is long by
intent — completeness is the point, because it has to carry the
"where are we and why" framing that a diagram would otherwise
carry.

**Reading order for a re-launch:**

1. §1 Purpose and the person it is for — the goals and the
   lived-workflow constraints that are the real acceptance
   criteria.
2. §2 The core reframe — the one idea everything else follows
   from.
3. §4 Strategic decision (keep / freeze / pivot / localize) —
   what changes versus the current repo, and why nothing good is
   discarded. **Includes §4.5 — the foundational-stack
   (stewardship / security / reversibility) decision, the
   single hardest one to evaluate.**
4. §13 Phased plan — the concrete walking skeleton; **Phase 0 is
   the start.**
5. §16 Open decisions + §17 Working assumptions — what is *not*
   yet decided and what this document assumed so it could be
   written. Correct these first.
6. The remaining sections (§5–§12, §14–§15, §18) on demand, as
   the orientation surface for whichever part is being worked.

**Section index:**

- §1 — Purpose and the person it is for
- §2 — The core reframe: three projections of one context
- §3 — Reference experiences and what we take from each
- §4 — Strategic decision: keep / freeze / pivot / localize
  - §4.5 — Foundational stack: stewardship, security, reversibility
- §5 — The data model (locked): the chunk tree
- §6 — Navigation and the compose-by-speech loop
- §7 — Multimodal I/O: the universal routable event bus
- §8 — Side conversations and context management
- §9 — The agent organization model (the "company")
- §10 — The orientation surface
- §11 — Domain instantiation: the laboratory organization
- §12 — Tooling and backend modularity
- §13 — Phased plan / walking skeleton
- §14 — Principles and invariants (non-negotiable)
- §15 — What carries over from the current repo
- §16 — Open decisions (decide these)
- §17 — Working assumptions (correct these)
- §18 — Glossary

---

## 1. Purpose and the person it is for

The maintainer is a **blind professor of computer science** who
also runs a research laboratory. The goal is **a clean,
reliable, audio-first computational workspace** for two
inseparable purposes:

1. **Personal computing** — engaging Claude Code and peer
   AI / agentic systems to think, build software (starting with
   this workspace itself), and hold sustained computational
   work.
2. **Organizational operation** — running a complex research
   group: grant writing, manuscript preparation, laboratory
   inventory, research strategy, standard procedures, and the
   memory of *who to ask for what* across a large system.

These are not two products. §9 and §11 show they are the **same
architecture at two scales**.

### 1.1 The lived-workflow constraints (these are the real spec)

The maintainer stated these directly. They are acceptance
criteria, not background:

- **Composition is by long, slow, intentional spoken narrative.**
  Thoughts form while talking. There is no effective way to
  proofread or edit earlier words inline — doing so takes long
  enough that the train of thought is lost. The primary input
  loop therefore cannot be "type, re-read, correct." It must be
  narrate-and-confirm.
- **Leaving and returning is constant, and re-acquiring context
  is the hard part.** A screen used to externalize that context
  and manage attention. That externalization must move to
  navigable structure plus confirming feedback, in non-visual
  modalities.
- **Holding a large system in mind is the central difficulty.**
  Graphical summaries (system diagrams, org charts) are not
  available. The orientation they provide must be delivered as
  navigable, spoken, non-graphical structure (§10).
- **Ambient awareness without interruption.** The maintainer can
  build complicated mental models *if* confirming data flows
  back about actions and surrounding processes — but it must not
  interrupt the thread being composed.
- **Multiple threads of thought run in parallel** and must be
  managed without one derailing another.
- **Fast, reliable narration is the gating quality bar.** The
  enemy is finicky, stuttering, dropped speech. Because
  composition is by speaking, narration quality is the product's
  primary felt quality.
- **Modality must be fluid, not keyboard-committed.** Seamless
  flow between speech, keyboard, and gesture; output routable to
  speech, spatial audio, refreshable tactile / braille displays,
  and vibrational cues.

### 1.2 What success looks like, stated plainly

The maintainer can sit at their own Windows machine, speak a
request to Claude Code, hear it confirmed quickly and reliably,
receive a response they can navigate as fine-grained structure
(not one block), ask a clarifying question anchored to a
specific part of it without losing the original, take a second
agent aside with the shared context and come back without drift,
and — at a larger scale — dispatch tasks into a structured
organization of agents and track them like a CEO. The first
build delivers the smallest honest version of the first half of
that sentence, used locally to build the rest.

## 2. The core reframe: three projections of one context

The single idea everything else follows from:

> **A conversation is a computational process. Its crystallized
> artifact is a set of version-controlled files. Its
> participants are agents and tools. These are three projections
> of one underlying structured context, and the human's role
> across all three is editor and orchestrator.**

- The **process** projection is the thinking: the live dialogue,
  the tree of exchanges.
- The **artifact** projection is the result: documents,
  procedures, code, decisions — kept under local version
  control (git, run locally, is the diff / history engine for
  this projection; a remote like GitHub is optional, not
  required).
- The **participant** projection is the workforce: Claude Code,
  and peers (Aider, OpenCode, the Wolfram Engine, other models),
  each able to step in with a defined role and a defined slice
  of context.

Almost every difficulty the maintainer described is a
consequence of these three being unsupported or conflated:
a chat client supports only a flattened process projection; a
terminal supports only an ephemeral stream; the current repo's
hand-maintained `SESSION-HANDOFF` / ADR apparatus is the
maintainer manually supplying the artifact-and-orientation
projection by hand because no tool does it.

This spec makes all three first-class over **one shared
substrate** (§5, §8), so the same primitives serve a single
conversation, an authored document, a side discussion, a team,
and an entire organization (§9 — the structure is
self-similar / fractal across scale).

## 3. Reference experiences and what we take from each

- **The Wolfram notebook (the primary reference).** Before
  sight loss the maintainer worked very effectively in Wolfram
  notebooks: a rich semantic structure of cells — sections,
  subsections, prose, formatted mathematics, images, and code
  presented as structured scientific content rather than flat
  strings. Reorderable into a *computational narrative*. The
  document structure helped both the final output **and**
  holding contextual awareness of an experiment while exploring
  the next step. **Take:** the workspace's primary object is an
  editable, reorderable, addressable document of typed cells —
  not a transcript, not a terminal.
- **EMACSPEAK (the audio-computing reference).** The right
  reference for *audio-first computing as a complete idea*.
  **Two ways it is wrong for this maintainer, stated by them:**
  it is focused on old-school development, and it has a long,
  complex learning curve. **Take:** audio-first, everywhere;
  but general (not dev-only) and low-friction (not a steep
  curriculum).
- **Chat clients (the anti-reference).** The iOS chat / phone
  workflow is the concrete failure being escaped: a response is
  one opaque block; replying appends to a flat list and buries
  the original; the screen reader is frequently ejected from
  the thread into the surrounding interface. **Take:** every
  one of those is a *data-model* failure (flat append-only list
  of blobs), not a styling failure — see §5.
- **The current `pty-speak` repo (the inheritance).** 52 cycles
  produced a sound architectural skeleton and a load of
  terminal-scraping debt. §4 and §15 separate the two precisely.

## 4. Strategic decision: keep / freeze / pivot / localize

Four moves. None discards validated work; the architecture
discipline of the current repo is precisely what makes this
cheap.

### 4.1 KEEP — the skeleton (it is exactly what this needs)

These transfer untouched and are the reason the pivot is small,
not a rewrite:

| Asset (current repo) | Why it is exactly right here |
|---|---|
| Typed event bus + `CellEventBus`; ADR 0008 "recover maximal semantics, emit typed events, never relay ambiguity" | This *is* the universal routable event bus of §7. ADR 0008 applied at conversation granularity *is* the chunk tree of §5. |
| Substrate / channel split (ADR 0001) | Separating *what the content is* from *how it is rendered to a device* is what makes speech / spatial audio / braille / haptic routing a configuration, not a rewrite (§7). |
| Cell as the unit (ADR 0004) | The cell is the notebook cell and the chunk-tree node. Conceptually load-bearing. |
| Navigable cell history (ADR 0007) | The "leave, do other things, return recontextualized" function — the central cognitive requirement (§1.1). |
| Three-layer transport / core / channel seam + `SessionHost` (ADR 0006) | Lets a structured-agent transport slot in beside the shell adapter as *the same plug shape* (§12). Pure, shell-agnostic core. |
| WPF app shell, diagnostics infra, accessibility + dogfood discipline | The host, the triage tooling, and the "accessibility outcome is the acceptance criterion" rule all carry over. |

### 4.2 FREEZE — the terminal-scraping debt

The "tiny finicky jittery complexity" has one source: 52 cycles
reconstructing clean semantics from a raw interactive-terminal
byte stream — the worst-structured possible source. That entire
layer (heuristic detection, OSC-133 precedence, sub-prompt
accumulators, the boundary-capture fix, #437 / #438) is
**demoted to an opt-in secondary "interactive terminal" mode**,
documented as accepted-imperfect, and **not invested in
further**. It is not deleted — it remains the escape hatch for
when a genuine live PTY (a REPL, a TUI, SSH) is required. The
moment it is not the primary surface, its imperfection stops
being a product-critical defect.

### 4.3 PIVOT — the primary surface is a structured agent stream

The primary surface is **not** a raw terminal and **not** a
generic command runner. It is a **structured AI-agent session
fed by the agent's native structured event stream** — Claude
Code CLI's structured / streaming-JSON interface (the Agent SDK
form) first. That stream is already typed (turns, tool calls,
tool results, content, completion). The boundary-detection
problem class that consumed 52 cycles **does not exist on this
path** — it was never going to. A command runner with a real
exit code is a *minor sibling* cell type, not the center.

### 4.4 LOCALIZE — move the loop to the maintainer's machine

For this specific artifact — a local Windows screen-reader
application — the phone + GitHub + cloud-sandbox loop is close
to the worst-matched loop possible: every compile, test, and
(critically) every narration-reliability check is a multi-minute
remote round trip, and narration reliability **cannot be
validated remotely at all**. The target loop is a **local
desktop app talking to a local Claude Code CLI on the same
Windows machine**, building and debugging itself there, with the
maintainer using the app to improve the app. GitHub / cloud
become optional, not required. This is the bootstrapping unlock;
everything in §13 Phase 0 serves it.

### 4.5 Foundational stack: stewardship, security, reversibility

The maintainer flagged this as essential repo information and as
the single hardest decision to evaluate. It is recorded here as
a strategic decision in the same category as §4.1–§4.4.

**Reframe it correctly first.** This is not "which language /
framework is best." It is a **governance and stewardship**
decision under irreducible uncertainty, with an asymmetric
stake specific to this maintainer: a foundation that degrades,
is abandoned, or develops a security or accessibility regression
is not an inconvenience to migrate around — it is a loss of
access to their own computing. It also **cannot be resolved by
research**: no one, inside or outside, can reliably predict any
vendor's ten-year strategy. The honest method is therefore (a)
criteria observable from outside and robust to being wrong, and
(b) an architecture that makes being wrong **cheap**. (b) is the
real answer; see the principle in §14.

**Evaluate it as four bets, not one — they have very different
risk:**

1. **Accessibility substrate — Windows UI Automation + NVDA.**
   This is the real foundation, more than the language, and the
   strongest part of the bet. UIA is a documented OS API under a
   multi-decade backward-compatibility obligation; NVDA is open
   source and stewarded by a mission-driven nonprofit, not a
   company that can pivot away from it. Re-platforming compute is
   annoying; re-platforming the accessibility substrate is the
   genuinely dangerous move. This is the dominant reason to stay
   on Windows.
2. **Runtime — .NET.** On the stated axis, close to the safest
   available, for a precise reason: not founder-obsession (the
   Wolfram model) but **structural incentive** — Microsoft's
   developer-tools and cloud businesses depend on .NET; it is
   open source under the .NET Foundation; it has a steady
   long-term-support cadence and one of the strongest
   "don't break what people built" cultures in software.
   Structural incentive **plus** open-source continuity is a
   sturdier shape of durability than founder-obsession, because
   it survives leadership change.
3. **The honest caveat — .NET broke once.** The .NET Framework →
   .NET Core transition (~2016–2020) was a real re-platforming
   event, handled with long overlap and clear guidance, but it
   happened. Lesson: even the best-stewarded runtime had one
   discontinuity in two decades — design so the *next* one costs
   weeks, not the project.
4. **UI toolkit — WPF.** The weakest sub-bet; hold it most
   loosely. Stable precisely because essentially frozen; new
   Microsoft UI investment goes elsewhere (and the successors
   have themselves been churny). It is also the **most
   replaceable** layer and the one the existing architecture
   already treats as a swappable channel. See §16.

**Decision criteria (outside-observable, uncertainty-robust):**
steward incentive *structurally* aligned (not promised);
decades-long backward-compatibility track record (not roadmap);
a second source if the steward fails (open source as escrow);
and low exit cost — the only criterion the maintainer fully
controls.

**The decisive move.** Convert the unanswerable question ("what
is the perfect ten-year stack?") into a small one ("what is the
best *current adapter* for a Windows screen-reader workspace?")
by keeping the core model (§5, §8, §9) free of platform,
runtime, and UI-toolkit types. Then **the model is the
foundation; .NET / WPF / Windows / NVDA are current adapters**
behind the ADR 0006 seam, and the foundational bet is
reversible. The architectural tax — no platform types in the
core — is the cheapest insurance available and the correct
response to uncertainty that cannot be studied away. The repo
already pays a version of this tax (the enforced core/shell
boundary); this generalizes it from "isolate the terminal" to
"isolate the platform". Recorded as a non-negotiable principle
in §14.

**On the Wolfram analogy.** The earlier Wolfram bet worked on
credible multi-decade founder stability obsession plus personal
insight into it. .NET cannot offer the personal-insight part; it
offers structural incentive plus open-source continuity instead
— a different and, for raw durability, sturdier shape of the
same property. And it is not either/or: the Wolfram Engine is a
planned **participant** (§12), so the trusted single-vendor
capability is kept and orchestrated, not abandoned, while the
*foundation* sits on the structurally-incentivized, open,
accessibility-mature substrate.

**Recommendation (maintainer ratifies).** Stay Windows + .NET +
UIA + NVDA — not because any vendor is guaranteed (no honest
answer claims that) but because it is the strongest available
combination on the criteria that matter here *and* the
architecture lets the bet be wrong cheaply. Hold WPF loosely.
Enforce core portability as a named principle (§14) and name the
WPF-replacement trigger as an open decision (§16).

## 5. The data model (locked): the chunk tree

This is the load-bearing decision and it is **locked**. Every
iOS pain the maintainer described decomposes to one defect: a
conversation modelled as a flat, append-only list of opaque
blobs. The model is the fix.

### 5.1 The primitives

- **Chunk.** The atomic node. An agent response is **decomposed
  on ingest** into a tree of typed chunks (heading, paragraph,
  list, list-item, code block, tool-call, tool-result, output,
  error). The structure is *already present* in the agent's
  output (Markdown / structured stream); it is **kept**, never
  flattened. This is ADR 0008 at conversation granularity:
  never relay the flattened-ambiguous blob when the structured
  form is in the source.
- **Stable identity.** Every chunk has a durable id. The
  navigation layer addresses chunks by id, not by scroll
  position.
- **Tree, not list.** A conversation is a tree. A follow-up or
  clarification is a **child branch anchored to the chunk it is
  about**, not an append to the end. Asking "what do you mean
  here" creates a branch under *that* node; the answer nests
  there; the spine being navigated is untouched.
- **Anchor + return.** "Where I branched from" is remembered.
  After a side branch, one operation returns to the exact chunk.
  This is the direct, mechanical fix for "responding makes me
  lose the original."
- **Capture order vs authored order.** Each cell carries its
  immutable capture (temporal) position *and* a separate
  authored position. v1 only appends; the authored ordering
  exists in the model from the first commit so the editor verbs
  (§6.3) are not a later rewrite.

### 5.2 The streaming rule (a screen-reader-specific constraint)

A response arrives as a stream. A streaming blob is hostile to a
screen reader — a moving target, and screen readers handle text
changing under the cursor badly (the current repo's UIA-churn
history is exactly this). Therefore:

> A chunk becomes **navigable and stable only once it is
> sealed.** While the response streams, a separate lightweight
> **ambient** signal reports progress ("response in progress,
> four chunks so far") on a peripheral channel (§7.3). The
> maintainer never navigates a moving target and is never cut
> off from progress.

### 5.3 Capture layer vs authored layer

- The **capture layer** is the transcript: immutable, time-
  ordered, the honest record of what happened. The existing
  IOCell / event-bus machinery is good at this. It is the
  *source*.
- The **authored layer** is the notebook: a document where the
  maintainer pulls capture chunks into named sections, writes
  narrative cells beside them, reorders, and revisits a cell to
  re-issue it with a change (which spawns a *new* agent
  interaction producing *new* capture chunks). This is the
  *workspace*.
- The chat-log is what flows **in**; the notebook is what the
  maintainer **thinks in**. They are layered, not competing.

## 6. Navigation and the compose-by-speech loop

### 6.1 The primary loop

Compose a request by speaking (or typing) → the request is
**confirmed back fast and reliably without forcing inline
re-reading** → it is sent to a local agent → the structured
response arrives as a sealed, navigable chunk tree → navigate /
branch / annotate. Narrate-and-confirm, never type-re-read-
correct.

### 6.2 First-class navigation verbs (v1)

These are the operations the maintainer named explicitly. They
are verbs over the chunk tree (§5), not scroll:

- **Jump to the start of the latest agent response.** (Not
  swipe / arrow through each prior message.)
- **Next / previous sibling chunk.**
- **Descend into a chunk's children** (its clarifications /
  branches) / **ascend** to the parent.
- **Return to anchor** — back to the exact chunk a side branch
  was spun from.
- **Re-narrate the current chunk** (fast, reliable, on demand).
- **Notification review** — move to the notification queue and
  back without losing thread position (§7.3).
- **Semantic info about run code** — for a tool-call / code
  chunk, report what was run, where, and its result, as
  structure.

### 6.3 Editor verbs (v2 — modelled now, exposed later)

Annotate a chunk; bookmark a chunk; split / merge / re-segment a
chunk; reorder; group into sections / subsections; revisit a
cell and re-issue with a change. These make the maintainer the
**editor inside a document** rather than a reader of a
transcript. The data model (§5.1 authored order) supports them
from day one; the v1 UI does not expose them yet.

### 6.4 The non-ejection invariant

> Focus **stays inside the content model.** Navigation never
> ejects the screen reader into surrounding chrome. This is the
> single clearest failure of the iOS workflow, and it is
> beatable here precisely because the content model and its
> focus are ours to control.

## 7. Multimodal I/O: the universal routable event bus

### 7.1 One stream, many devices

There is **one typed semantic event stream** (the §4.1 bus).
Rendering to a device is a **routed, orchestrated channel**, not
a code path. Output device targets, in priority order for the
maintainer:

1. **Fast, reliable speech (TTS).** The number-one quality bar
   (§1.1). No stutter, no drop, low latency, interruptible.
2. **Spatial audio.** Carries the foreground / ambient
   distinction (§7.3) and participant / thread identity (§8,
   §9) by position.
3. **Earcons / ambient cues.** Non-verbal state and progress.
4. **Refreshable tactile / braille display.** Structured chunk
   text on demand.
5. **Vibrational / haptic cues.** Confirmations and boundaries.

### 7.2 Input is fluid, not keyboard-committed

Speech, keyboard, and gesture are interchangeable input
surfaces feeding the same command layer. The maintainer is
comfortable on a keyboard but must not be bound to it; speech is
the primary composition surface.

### 7.3 Foreground vs ambient (the attention contract)

Two routing classes, always distinguished:

- **Foreground** — the narrative thread the maintainer is
  composing or navigating. Sequential, never interrupted by
  state noise.
- **Ambient / peripheral** — awareness of surrounding processes
  (a response streaming, a background task finishing, an agent
  needing input). Positioned away from the foreground (spatial
  audio / earcon / haptic) so a mental model of "what is
  happening around me" is maintained **without derailing
  composition**. This is the maintainer's "confirming data
  flows back without interrupting the thread" requirement made
  concrete.

## 8. Side conversations and context management

### 8.1 The isolation invariant (stated precisely)

The "pull one person aside in a meeting" model. Easy to get
subtly wrong; stated as an invariant:

- A side conversation is a **fork from an anchor**.
- It receives a **read-only snapshot** of the main thread's
  context up to that anchor — so it has *all the same context*.
- The main thread's context is **append-only and is never
  mutated by the side conversation** — so returning to the main
  thread, nothing has drifted.
- Anything the side conversation produces that belongs in the
  main is **promoted by an explicit act of the maintainer** (a
  new cell placed into the main), **never implicitly merged**.
  Promotion is a verb the editor performs, not a side effect.

### 8.2 The mechanism that makes it real (and fixes two phones)

The maintainer today uses two phones — one for one model, one
for another — and they have *no shared knowledge base*, so the
"pull one aside, the other undisturbed" move is impossible.
The reason it fails, and the design truth that fixes it:

> Different agents / models / tools have **separate,
> incompatible native memories** and cannot share a context
> window. Therefore the **shared context is the document, not
> any model's session.** Every agent invocation is fed a
> **rendered slice of the document** as its input context. The
> document is the memory; agents are stateless relative to one
> another.

Two phones fails because each model's context lives only in its
own session. The instant the document is the substrate and every
participant is fed slices of it, "pull one aside with the full
shared context" is trivial, and the no-drift invariant (§8.1) is
**automatic**: a side conversation is just a different slice fed
to a different participant; it *structurally cannot* mutate the
main, because the main is the document, not the agent.

### 8.3 The context-management policy layer

Beyond the invariant, the maintainer wants explicit control over
information flow: some shared files, some shared chat history,
and **deliberate decisions about which conversations flow to
whom and which participant may access which parts of the
information**. So a policy layer over the document substrate:

- **Scopes.** A context slice is a named, bounded view of the
  document (files + chunk subtrees + history ranges).
- **Grants.** A participant / role (§9) is granted specific
  scopes — not the whole document by default.
- **Flow rules.** Explicit rules for which conversations'
  outputs feed which other contexts, and which require explicit
  promotion to cross a boundary.
- **Sandboxing.** A scope can be sealed so work inside it cannot
  affect anything outside until explicitly promoted (the "spin
  out a side company" primitive of §9).

## 9. The agent organization model (the "company")

The maintainer's words: *an army of adjustable agents who can,
from the information given, work out how to reorganize,
restructure, fill different roles, and spin out side companies
as needed.* This is the side-conversation model (§8) scaled up,
using the **same primitives** — fork, snapshot, promote, scope,
sandbox — applied recursively. The structure is **self-similar /
fractal**: a side conversation is a tiny team; a team is a
sub-organization; the whole is an organization; one mechanism at
every scale. This is what "I want to build worlds" means
concretely: a substrate for constructing and operating
arbitrarily nested structured organizations over one primitive
set.

### 9.1 Organizational primitives

- **Role** — a named purpose + a context scope (§8.3) + a
  participant binding (which agent / model fills it) + a remit
  (what it may decide vs must escalate).
- **Team** — a set of roles sharing a scope, with an internal
  flow.
- **Department / specialization** — a long-lived team for a
  recurring function.
- **Assignment** — a task dispatched to a role / team, with
  state (open / in progress / blocked / handed off / done).
- **Responsibility handoff** — explicit transfer of an
  assignment between roles, preserving its context slice.
- **Sandbox / side company** — a sealed sub-organization (§8.3
  sandboxing) that runs independently and reports / promotes
  back deliberately.
- **The maintainer is the CEO** — issues and dispatches orders
  and tasks "from within my head," and triages, restructures,
  and onboards roles on the fly.

### 9.2 Onboarding and adjustability

Roles are **onboarded incrementally**, "like building a company
from scratch," each stepping in at a chosen moment with a chosen
amount of context to offer a distinct perspective and
recommendations. Agents can be asked to **propose** a
restructuring (new roles, regrouped teams, a spun-out sandbox)
given the current state; the maintainer ratifies — the same
"agent recommends, human decides, promotion is explicit"
discipline as everywhere else (§8.1).

## 10. The orientation surface

The sharpest observation from the design dialogue:

> The current repo's `SESSION-HANDOFF.md` / ADR / `CLAUDE.md`
> apparatus **is an orientation surface the maintainer has been
> building by hand for 52 cycles**, because it is the
> externalized working memory no tool provides. The hand-built
> version is the specification for an automatable feature.

The system must, on demand, **narrate where the maintainer is**:
where in the project / process, what the consequences of the
decision in front of them are, and what is still open — as a
**navigable spoken tree**, not a diagram. It is itself a
chunk-tree (§5) view (project → cycle → decision →
consequences / open questions), generated by an agent reading
the artifact projection (§2) plus the live process.

**Open generation tradeoff (decide in §16):** regenerate fresh
on demand (always accurate, costs a call, never drifts) vs
maintain incrementally (cheap, instant, can drift the way a
stale handoff block does). The maintainer has lived both
failure modes by hand; their instinct on which is preferable is
the deciding input.

## 11. Domain instantiation: the laboratory organization

The professorial / laboratory operation is **not a separate
system** — it is one instantiation of §9 with domain roles and
scopes. Building the model of the lab "on the fly" is exactly
the agent-recommends / human-ratifies onboarding of §9.2.

Indicative roles / departments (the maintainer triages and
restructures these live; this is illustrative, not fixed):

- **Grant writing** — proposal drafting, budget, compliance,
  deadlines; scope = funding docs + prior awards.
- **Manuscript preparation** — drafting, figures, references,
  submission tracking; scope = the manuscript + data + venue
  rules.
- **Laboratory inventory** — stock, consumables, equipment
  state, reorder thresholds.
- **Research strategy** — direction, prioritization,
  cross-project synthesis.
- **Procedures** — standard operating procedures, protocol
  memory.
- **Who-to-ask memory** — the map of which person / system
  holds which knowledge in a large complex group; itself a
  navigable structure (§5, §10).

Each is a team with a bounded scope, assignments with state, and
responsibility handoffs (§9.1). Sandboxing (§8.3) lets a risky
or exploratory sub-effort run without disturbing the rest. The
maintainer dispatches into this from the same compose-by-speech
loop (§6.1) used for code — *issue an order from within my head,
have it land on the right part of the organization.*

## 12. Tooling and backend modularity

- **One adapter seam, N tools.** The ADR 0006 transport seam is
  designed for many participants; **Claude Code CLI is the
  first concrete implementation**. Aider, OpenCode, the Wolfram
  Engine, and additional models (for multiple thematic
  conversations with different purposes / models) are *further
  instances of the same seam*, not separate pathways. Build the
  seam honestly for N; implement one now.
- **Local git is the artifact version-control engine.** The
  maintainer's own realization, and it is correct: git runs
  locally, on the maintainer's machine, as the diff / history /
  branching engine for the artifact projection (§2). It does
  not require GitHub; a remote is an optional backup / sharing
  channel. Sandboxes / side companies (§8.3, §9.1) map
  naturally onto branches / worktrees.
- **Core stays pure (ADR 0006).** No transport-specific or
  device-specific code in the core; participants are transport
  adapters, devices are channels. This is what keeps the seam
  for N tools and the routing for N devices a configuration
  rather than a rewrite.
- **Existing stack retained.** F# / .NET 9 core + WPF shell
  (the §4.1 keep-list). MAUI remains out of scope (existing
  maintainer decision, ADR 0006). Re-launching does **not** mean
  green-field; it means a new transport adapter + the chunk-tree
  model + the local loop, on the kept skeleton.

## 13. Phased plan / walking skeleton

Each phase is its own PR (or short PR sequence), independently
**locally dogfood-gated with a screen reader** before the next
begins (the existing walking-skeleton + accessibility-acceptance
discipline carries over). Net-subtractive where possible.
Nothing on the §4.1 keep-list is discarded; the §4.2 freeze-list
is quarantined, not deleted.

### Phase 0 — the local bootstrap (the start)

The smallest honest version of the primary loop, run locally so
the app can build the app.

- Reuse the existing WPF shell + event bus + channel + NVDA
  integration + diagnostics (§4.1).
- One new transport adapter: **Claude Code CLI over its
  structured / streaming interface**, behind the existing
  `ShellAdapter` / `SessionHost` seam, running **locally on the
  maintainer's Windows machine**.
- Ingest decomposes the response into a sealed **chunk tree**
  (§5); the streaming rule (§5.2) is honored.
- The v1 navigation verbs (§6.2) and the non-ejection invariant
  (§6.4).
- **Fast, reliable narration is the explicit gating quality
  bar** (§1.1, §7.1) — and it is *only* measurable here, in the
  local screen-reader loop.
- Data model includes authored order + stable ids + tree
  branching from the first commit (§5.1), even though only the
  v1 verbs are exposed.

**Acceptance:** the maintainer, at their own machine, speaks a
request to Claude Code, hears it confirmed quickly and without
stutter, navigates the structured response by chunk, branches a
clarification anchored to a chunk, and returns to anchor — and
can use this loop to make the next change to the app itself.
This is the phone-and-cloud-loop exit.

### Phase 1 — the editor

Expose the §6.3 editor verbs: annotate, bookmark, split / merge /
re-segment, reorder, section / subsection grouping. The
transcript becomes an authored notebook. Acceptance: the
maintainer restructures a real working session into a
navigable computational narrative and returns to it later
recontextualized.

### Phase 2 — side conversations + context policy

Implement the §8 fork / snapshot / promote invariant and the
§8.3 scope / grant / flow / sandbox policy layer over the
document substrate. Acceptance: spin out a side conversation
with full shared context, return with zero drift, promote one
result explicitly.

### Phase 3 — multiple participants

Additional transport adapters (e.g. a second model, Aider,
OpenCode, Wolfram Engine) as §12 seam instances; "pull one
aside" across participants; agents make recommendations the
maintainer ratifies. Acceptance: the two-phones workflow is
replaced — two participants, one shared document context, one
undisturbed by consulting the other.

### Phase 4 — the organization layer

The §9 primitives: roles, teams, assignments, handoffs,
sandboxes, CEO dispatch — over the same substrate. Acceptance:
the maintainer dispatches a multi-step task into a small
agent team and tracks it to completion by structure, not
memory.

### Phase 5 — the laboratory instantiation

Populate §11 domain roles / scopes; build the lab model on the
fly via §9.2 onboarding. Acceptance: a real lab task (e.g. a
grant-deadline triage) is run through the organization.

### Phase 6 — multimodal device maturation

Mature the §7 routing to spatial audio, refreshable
braille / tactile, and haptic targets beyond speech. Acceptance:
foreground / ambient (§7.3) is carried by spatial position;
braille and haptic channels are usable for their named roles.

> Phases 1–6 are the **direction**, not a commitment to build
> all of them before re-evaluating. Phase 0 is the commitment.
> The maintainer re-steers from the working Phase 0 loop —
> which is the entire point of building Phase 0 first.

## 14. Principles and invariants (non-negotiable)

1. **Accessibility outcome is the acceptance criterion.** A
   phase is not done until the maintainer has validated it with
   a screen reader, locally. (Carried from the current repo.)
2. **Maximal semantic surfacing (ADR 0008).** Recover the most
   structure the source unambiguously provides; emit typed
   events; never relay the flattened-ambiguous form. The chunk
   tree is this principle.
3. **The document is the memory.** Shared context is the
   document, never a model session. Agents are fed slices.
4. **No-drift isolation.** Side work forks read-only and
   promotes only by explicit human act; the main is append-only
   and never implicitly mutated.
5. **Capture is the source; authored is the workspace.** Never
   collapse them; never make the transcript the thing the
   maintainer must think in.
6. **Foreground is never interrupted by ambient.** Awareness is
   peripheral by construction.
7. **Local-first.** The primary loop runs on the maintainer's
   machine; cloud / GitHub are optional.
8. **One concern per change; each phase independently
   dogfood-gated.** (Carried from the current repo.)
9. **Self-similar primitives.** One primitive set (chunk, fork,
   snapshot, promote, scope, sandbox, role) at every scale —
   chunk, conversation, document, team, organization.
10. **The model is the foundation; the stack is a replaceable
    adapter.** The core domain model (§5, §8, §9) carries no
    platform, runtime, or UI-toolkit types. .NET / WPF /
    Windows / NVDA are current adapters behind the ADR 0006
    seam, chosen on stewardship grounds (§4.5) and deliberately
    kept reversible. The foundational-stack bet is survivable
    because being wrong about it is cheap by construction —
    never by prediction.

## 15. What carries over from the current repo

Concrete inheritance map, so re-launch is provably not a
green-field restart:

| Current artifact | Disposition |
|---|---|
| ADR 0001 substrate / channel dichotomy | **Keep** — becomes content vs device-routing (§7). |
| ADR 0004 IOCell | **Keep / generalize** — the cell becomes the chunk-tree node (§5). |
| ADR 0006 three-layer seam + `SessionHost` | **Keep** — the N-tool transport seam (§12). |
| ADR 0007 navigable cell history | **Keep / extend** — capture-layer navigation; authored layer (§5.3) is the new growth. |
| ADR 0008 maximal semantic surfacing | **Keep / elevate** — the spine principle (§14.2). |
| `CellEventBus` + typed event taxonomy | **Keep** — the universal routable bus (§7). |
| WPF shell, diagnostics, hotkey contract, accessibility + dogfood discipline | **Keep** — host + triage + acceptance rules. |
| `HeuristicPromptDetector`, OSC-133 precedence, sub-prompt accumulators, boundary-capture fix, #437 / #438 | **Freeze** — opt-in secondary PTY mode (§4.2); not invested in. |
| ADR 0010 Option A framing | **Superseded by this document** — directionally right, under-scoped. (Cross-link / status update is a follow-up, not done here.) |
| `SESSION-HANDOFF` / ADR / `CLAUDE.md` orientation apparatus | **Keep, then automate** — it is the §10 orientation-surface spec. |

## 16. Open decisions (decide these)

Each is a genuine fork the maintainer should decide; this
document does not pre-empt them.

1. **Chunk grain (§5.1).** Every paragraph, every
   heading-delimited section, or model-marked boundaries? Sets
   how fine navigation is — a lived-experience judgment about
   stepping through vs jumping over.
2. **Orientation generation (§10).** Fresh-on-demand vs
   maintained-incrementally. Which drift / cost failure mode is
   preferable.
3. **First-build scope (§13 Phase 0).** Confirmed by the
   dialogue as: smallest usable Claude-Code-CLI chat loop with
   the tree / authored model underneath but editor verbs
   deferred. Re-confirm at re-launch.
4. **Promotion semantics (§8.1).** Always an explicit human
   act — confirmed. Open sub-question: may some "advisor" roles
   be configured to never promote by default?
5. **Policy-layer granularity (§8.3).** How fine scopes /
   grants / flow rules need to be for the lab instantiation
   (§11) — likely discovered, not specified up front.
6. **Org-state persistence (§9).** Assignments / handoffs /
   sandbox state: in git alongside the artifact, or a separate
   structured store? (Affects how "version the organization"
   works.)
7. **Cell / chunk addressing across documents and sessions
   (§5.1).** The id scheme that survives reordering, branching,
   re-issue, and cross-document reference.
8. **Spatial-audio model (§7.1, §7.3).** How positions map to
   foreground / ambient and to participants / threads.
9. **WPF-replacement trigger (§4.5).** WPF is the loosely-held
   layer. What observable signal (a support-policy change, an
   accessibility regression, a successor reaching parity) should
   trigger planning its replacement — and is a thin
   UI-channel-portability spike worth scheduling *before* any
   trigger fires, so the reversibility in §14.10 is proven, not
   assumed? Related: which layer's failure is the true
   existential risk (the §4.5 analysis says the accessibility
   substrate, not the runtime) and what the contingency for
   *that* is.

## 17. Working assumptions (correct these)

Stated so they are correctable rather than silent:

- Primary platform is Windows + NVDA (existing).
- The existing WPF / F# / .NET 9 shell is reused, not rebuilt
  (ADR 0006; MAUI out of scope). The stack is chosen on the
  stewardship / security / reversibility analysis in §4.5, not
  taken for granted; WPF is held loosely (§4.5, §16.9) and the
  core is kept platform-type-free (§14.10) so the bet stays
  reversible.
- Claude Code CLI exposes a usable structured / streaming
  programmatic interface (Agent SDK / structured CLI output)
  sufficient for a typed chunk stream — to be verified
  concretely as the first Phase 0 task.
- Git is used locally as the artifact VCS; a GitHub remote is
  optional.
- "Re-launch" means a new transport adapter + the chunk-tree
  model + the local loop on the kept skeleton — **not** a
  rewrite.
- This document supersedes ADR 0010's *framing* but does not
  itself edit ADR 0010 / `SESSION-HANDOFF` / `CLAUDE.md`; those
  cross-links are a deliberate follow-up so this draft can be
  reviewed in isolation first.

## 18. Glossary

- **Chunk** — the atomic typed node of a decomposed response;
  the unit of navigation (§5.1).
- **Chunk tree** — a conversation as a tree of chunks with
  anchored branches, not a flat list (§5).
- **Cell** — a chunk in its authored-document role (§5.3); the
  notebook unit.
- **Capture layer** — the immutable time-ordered transcript;
  the source (§5.3).
- **Authored layer** — the editable, reorderable notebook; the
  workspace (§5.3).
- **Anchor** — the chunk a side branch was spun from; the
  return target (§5.1).
- **Fork / snapshot** — a side conversation's read-only copy of
  context up to an anchor (§8.1).
- **Promotion** — the explicit human act of moving a side
  result into the main (§8.1).
- **Participant** — an agent / model / tool bound to the seam
  (§12); a workforce member.
- **Scope / grant / flow / sandbox** — the context-management
  policy primitives (§8.3).
- **Role / team / assignment / handoff** — the organization
  primitives (§9.1).
- **Orientation surface** — the on-demand navigable spoken map
  of "where am I and what follows" (§10).
- **Substrate / channel** — content model vs device routing
  (ADR 0001; §7).
- **Foreground / ambient** — the uninterrupted thread vs
  peripheral awareness (§7.3).
- **Secondary mode** — the frozen, opt-in raw-PTY interactive
  terminal (§4.2).
- **Portable core / platform adapter** — the principle that the
  core domain model carries no platform / runtime / UI-toolkit
  types, so .NET / WPF / Windows / NVDA are replaceable adapters
  and the foundational-stack bet is reversible (§4.5, §14.10).

---

*End of draft. This document is the navigable artifact form of
the design dialogue that produced it — the orientation surface
(§10) for the re-launch, until the system can generate its
own.*
