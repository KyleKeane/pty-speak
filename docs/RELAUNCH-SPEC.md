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

### 0.1 Core canon (stabilized vocabulary)

Two phrases are **fixed as core canon** — stabilized linguistic
cornerstones the maintainer is presently using to engage with
this abstraction. They are deliberately *named first* so the
rest of the document is read against them. They may later be
decomposed into components or re-categorized; until then the
*terms themselves are fixed* and used consistently throughout.

- **Interaction engine** — *the system being built.* The
  platform-free core that ingests structured input, holds the
  canonical model (the §5 chunk tree, §8 context, §9
  organization), orchestrates participants, and emits typed
  events. It is **not** a GUI, a screen-reader integration, or a
  terminal. "We are focusing on the interaction engine" is the
  scope statement for the whole re-launch; §13 Phase 0 is its
  first build.
- **Universal event bus** — *the one typed semantic stream the
  interaction engine emits.* Every output mechanism — the
  self-voicing audio channel, spatial audio, haptics, braille,
  and (only if ever essential) any GUI / UIA / external screen
  reader — is merely **a consumer of this bus**, never a
  foundation and never privileged. Earlier phrasings in this
  document ("universal routable event bus", "the bus",
  `CellEventBus`) denote this canon term.

Consequence, ratified by the maintainer: **from day zero we
build for the interaction engine and the universal event bus and
ignore GUI / UIA / NVDA entirely.** They are not foundational,
not transitional, not a comparison oracle. *If* a GUI or
external screen reader ever becomes essential, it enters only as
one universal-event-bus consumer among many (§4.6, §14.11,
§14.12).

**Reading order for a re-launch:**

0. §0.1 Core canon — the fixed vocabulary ("interaction engine",
   "universal event bus") and the day-zero "ignore GUI / UIA /
   NVDA" consequence. Read this first; everything else is read
   against it.
1. §1 Purpose and the person it is for — the goals and the
   lived-workflow constraints that are the real acceptance
   criteria.
2. §2 The core reframe — the one idea everything else follows
   from.
3. §4 Strategic decision (keep / freeze / pivot / localize) —
   what changes versus the current repo, and why nothing good is
   discarded. **Includes §4.5 — the foundational-stack
   (stewardship / security / reversibility) decision — and
   §4.6 — self-voicing vs. external screen reader (output
   ownership): the two single hardest decisions to evaluate.**
4. §13 Phased plan — the concrete walking skeleton; **Phase 0 is
   the start.**
5. §16 Open decisions + §17 Working assumptions — what is *not*
   yet decided and what this document assumed so it could be
   written. Correct these first.
6. The remaining sections (§5–§12, §14–§15, §18) on demand, as
   the orientation surface for whichever part is being worked.

**Section index:**

- §0.1 — Core canon (stabilized vocabulary) — *read first*
- §1 — Purpose and the person it is for
- §2 — The core reframe: three projections of one context
- §3 — Reference experiences and what we take from each
- §4 — Strategic decision: keep / freeze / pivot / localize
  - §4.5 — Foundational stack: stewardship, security, reversibility
  - §4.6 — Self-voicing vs. external screen reader (output ownership)
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
| Diagnostics infra; accessibility + dogfood *discipline* (the "accessibility outcome is the acceptance criterion" rule) | **Keep** — but the validation *mechanism* is the system's own self-voicing channel, not an external screen reader (§4.6, §14.1). |
| WPF / UIA app shell, NVDA integration | **Defer (not day zero).** Per core canon (§0.1) we ignore GUI / UIA / NVDA entirely from day zero and build the interaction engine against the universal event bus. *If* a GUI ever becomes essential it re-enters only as one bus consumer among many (§4.6, §14.12) — never the host, never a foundation. |

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

1. **Accessibility substrate — historically the dominant
   bet; *superseded by §0.1 / §4.6*.** The original analysis:
   re-platforming the accessibility substrate is the genuinely
   dangerous move, so depending on Windows UIA + NVDA looked
   like the strongest, least-reversible bet. The day-zero
   self-voicing decision (§0.1, §4.6) **resolves this by
   elimination**: the system owns its audio path and takes *no*
   external-accessibility-substrate dependency at all, so the
   "dangerous to re-platform" risk does not apply to owned
   content — it is removed, not chosen. This *strengthens* the
   reversibility logic rather than weakening it. (UIA / NVDA
   retain relevance only as a possible deferred foreign-software
   edge adapter — §4.6 — never a foundation.)
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
best *current adapter* for the interaction engine on the
maintainer's machine?") by keeping the core model (§5, §8, §9)
free of platform, runtime, and UI-toolkit types. Then **the
model is the foundation; the runtime (.NET / Windows) is a
replaceable adapter** behind the ADR 0006 seam — and any future
output / GUI / screen-reader integration is likewise just a
universal-event-bus consumer (§0.1, §14.12) — so the
foundational bet is reversible. The architectural tax — no platform types in the
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

**Recommendation (maintainer ratifies).** Stay Windows + .NET
for the **runtime** — not because any vendor is guaranteed (no
honest answer claims that) but because it is the strongest
available runtime on the criteria that matter here *and* the
architecture lets the bet be wrong cheaply. **UIA + NVDA are
*not* part of the recommendation:** per §0.1 / §4.6 the system
self-voices and takes no external-accessibility-substrate
dependency; that sub-bet is eliminated, not chosen. WPF / GUI
is deferred entirely (§0.1, §4.1, §15). Enforce core
portability as a named principle (§14.10); the open decision in
§16 is now the GUI-essential trigger, not a WPF-replacement
trigger.

### 4.6 Self-voicing vs. external screen reader (output ownership)

Another foundational decision the maintainer raised and asked to
be distilled into a principle. It **refines §4.5**: §4.5 governs
the platform / runtime; this governs *who owns the rendering of
meaning to the user*.

**The instinct (recorded).** Build a self-voicing ecosystem —
the system renders its own canonical content to audio directly —
rather than depending on an external screen reader (NVDA) to
re-derive meaning from a UI built for sighted use. Grounds:
canonical control (the format the user controls, not another
developer's interface), per-user optimization, privacy and
security from owning the audio path end-to-end, and values
encoded into the canonical format.

**Why it is internally consistent, not a detour.** Depending on
a generic screen reader to voice the system's *own* content is
the same shape of mistake as terminal-scraping (§4.2):
reconstructing semantics through a generic adaptation layer when
the system already owns them. ADR 0008 (maximal semantic
surfacing) applied to the output channel **is** self-voicing.
It is also the mechanical cure for the §1.1 fast-and-reliable
narration bar: the repo's long history of fighting
UIA-notification churn *is* the cost of not owning the audio
path.

**The honest cost (the scary part, correctly identified).**
Self-voicing makes the system responsible for an accessibility
surface NVDA represents decades of battle-tested edge-case
handling for. For the system's **own** canonical content this is
bounded and tractable (it is the §5 chunk tree the system
already produces). For **foreign software and the open web** it
is the unbounded-reconstruction problem (§4.2 / ADR 0010) at
OS / web scale — automation frameworks + vision models are
powerful but brittle, slow, costly, non-deterministic: the
opposite of the §1.1 bar, and a real risk of recreating
terminal-scraping at planetary scale. A *cloud* vision model for
that edge also *introduces* a privacy exposure (screen content
to a third party), inverting the privacy rationale unless the
edge uses local models.

**The reconciliation — scope is the whole answer.** These are
two different decisions; conflating them is the trap (the
maintainer instinctively flagged only the web part as scary):

- **Self-voicing is the primary, canonical experience for
  everything the system owns** — content, decisions, operations,
  orientation — rendered directly from the user-controlled
  canonical format, never reconstructed through a generic
  external screen reader. **Ratified.**
- **For software the system does not own** (foreign apps, the
  open web), an external accessibility substrate (UIA) and / or
  automation + vision would be **bounded, opt-in adapters at the
  edge** — the same status as the §4.2 secondary terminal mode
  and the §14.10 replaceable-adapter stance: never the primary
  path, never on the critical loop. **Deferred — not day
  zero.** When built, such an adapter is just another
  universal-event-bus participant; local-first vision is
  preferred to preserve the privacy rationale.
- **Day-zero strengthening (maintainer-ratified, supersedes the
  earlier "NVDA transitional / comparison oracle" hedge).** Per
  core canon (§0.1): from day zero we **ignore GUI / UIA / NVDA
  entirely**. They are not foundational, not transitional, and
  **not** a development comparison oracle — any such dependency
  would re-impose the very external-developer constraints this
  build exists to escape, and would contaminate the
  from-scratch interaction engine. The self-voicing channel is
  validated on its own terms (§14.1). The single residual cost
  — no external oracle for the access-critical channel early —
  is accepted *because* the universal event bus makes adding a
  screen-reader consumer later trivial and non-foundational
  (§14.10 reversible-by-construction); the door is not closed,
  it is simply not built now.

**Recommendation (maintainer ratifies).** Adopt self-voicing as
the canonical output-ownership model, scoped as above. Distilled
as principle §14.11.

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

- Reuse only the **interaction-engine** parts of the current
  repo (§4.1): the typed event bus, the three-layer
  transport / core / channel seam + `SessionHost`, and
  diagnostics. **No WPF shell, no UIA, no NVDA** — per core
  canon (§0.1) those are not built day zero.
- A **self-voicing audio channel** as a universal-event-bus
  consumer — the system owns the audio path end-to-end (§4.6,
  §14.11). This is the only output channel required for Phase
  0.
- One new transport adapter: **Claude Code CLI over its
  structured / streaming interface**, behind the existing
  `ShellAdapter` / `SessionHost` seam, running **locally on the
  maintainer's machine**.
- Ingest decomposes the response into a sealed **chunk tree**
  (§5); the streaming rule (§5.2) is honored.
- The v1 navigation verbs (§6.2) and the non-ejection invariant
  (§6.4).
- **Fast, reliable narration is the explicit gating quality
  bar** (§1.1, §7.1) — owning the audio path is precisely how
  it is met (it is the §4.6 mechanical cure for the
  UIA-churn cost); validated on the self-voicing channel's own
  terms, not against a screen reader.
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
   phase is not done until the maintainer has validated it
   locally **on the system's own self-voicing channel** — not
   against an external screen reader (§4.6, §0.1). (The
   *discipline* is carried from the current repo; the
   validation mechanism changes to the owned channel.)
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
11. **Own the voice for what you own.** The system self-voices
    its own canonical content, decisions, and operations
    directly from the user-controlled canonical format; it
    never depends on a generic external screen reader to
    re-derive meaning the system already holds (ADR 0008
    applied to the output channel; the cure for the §1.1
    narration-reliability cost). External screen readers /
    automation + vision are bounded, opt-in *edge* adapters for
    foreign software the system does not own (§4.6) — never the
    primary path, never on the critical loop, and **not built
    day zero**.
12. **Build the interaction engine, not a GUI.** (§0.1 canon.)
    From day zero the work targets the *interaction engine* and
    the *universal event bus*; GUI / UIA / NVDA are ignored
    entirely — not foundational, not transitional, not a
    comparison oracle. *If* a GUI or external screen reader
    ever becomes essential it enters **only as one
    universal-event-bus consumer among many** (alongside
    self-voicing audio, spatial audio, haptics, braille), never
    a host and never a foundation. Avoiding GUI / UIA
    considerations is itself a design constraint that protects
    the from-scratch system.

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
| `CellEventBus` + typed event taxonomy | **Keep** — *is* the universal event bus (§0.1, §7). |
| Diagnostics, hotkey contract, accessibility + dogfood *discipline* | **Keep** — triage + the "accessibility outcome is the acceptance criterion" rule; validation mechanism becomes the self-voicing channel (§14.1). |
| WPF / UIA shell, NVDA integration | **Defer (not day zero)** — per core canon (§0.1) GUI / UIA / NVDA are ignored from day zero; a GUI, if ever essential, re-enters only as one universal-event-bus consumer among many (§4.6, §14.12), never the host. |
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
9. **GUI-essential trigger (§0.1, §4.6).** GUI / UIA is
   deferred entirely day zero. Open: what, if anything, would
   ever make a visual surface *essential*, and confirmation
   that it would then enter **only** as a universal-event-bus
   consumer (§14.12), never a host. Related: with the
   accessibility-substrate dependency *removed* by self-voicing,
   the §4.5 existential-risk question narrows to the **runtime**
   (.NET on the maintainer's machine) and the **self-voicing
   channel itself** — what the contingency for each is, and
   whether a thin runtime-portability spike is worth scheduling
   so §14.10 reversibility is proven, not assumed.
10. **Foreign-software edge boundary (§4.6).** Which foreign
    surfaces (if any) ever warrant a bounded edge adapter vs.
    are simply out of scope; local vs. cloud vision models for
    any such web edge (privacy-determining). The earlier
    "NVDA sunset / comparison-oracle" sub-question is **closed**
    — NVDA is not built at all (§0.1).

## 17. Working assumptions (correct these)

Stated so they are correctable rather than silent:

- Primary runtime platform is the maintainer's own machine
  (Windows / .NET assumed; §4.5). **No NVDA / UIA dependency** —
  per core canon (§0.1) the interaction engine self-voices and
  ignores GUI / UIA / NVDA from day zero.
- The current repo's **interaction-engine** parts (event bus,
  three-layer seam + `SessionHost`, data model, diagnostics) in
  F# / .NET 9 are reused, not rebuilt (ADR 0006; MAUI out of
  scope). The **WPF / UIA / NVDA presentation layer is deferred,
  not reused as a host** (§0.1, §4.1, §15). The stack is chosen
  on the §4.5 stewardship / reversibility analysis; the core is
  kept platform-type-free (§14.10) so the bet stays reversible.
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

*Core-canon terms are marked **[canon]** and defined
authoritatively in §0.1; they are fixed vocabulary.*

- **Interaction engine** **[canon]** — the platform-free core
  being built: ingests structured input, holds the canonical
  model, orchestrates participants, emits typed events; not a
  GUI / screen reader / terminal (§0.1).
- **Universal event bus** **[canon]** — the one typed semantic
  stream the interaction engine emits; every output mechanism
  is merely a consumer of it (§0.1, §7). Subsumes the earlier
  "universal routable event bus" / `CellEventBus` phrasings.
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
- **Self-voicing** — the system renders its own canonical
  content to audio directly, owning the audio path end-to-end,
  rather than depending on a generic external screen reader to
  re-derive it (§4.6, §14.11).
- **Edge adapter** — a bounded, opt-in connector to software the
  system does not own (foreign apps / the open web), via
  external accessibility APIs and / or automation + vision;
  never on the critical loop (§4.6).

---

*End of draft. This document is the navigable artifact form of
the design dialogue that produced it — the orientation surface
(§10) for the re-launch, until the system can generate its
own.*
