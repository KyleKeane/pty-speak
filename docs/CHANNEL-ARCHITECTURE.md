# Channel Architecture

> **Snapshot**: 2026-05-08
> **Status**: design / descriptive document — formalizes the
>   channel-based-communication architectural principle the
>   maintainer articulated 2026-05-08, applied concretely in
>   Cycle 17 (PR #192).
> **Authoring item**: backlog item 32 (research stage)
> **Companion docs**:
> - [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — operational vocabulary; the 12 stages where channels appear at thread boundaries.
> - [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — architectural framing (Shell Interaction Manager + three-component model). The SIM owns shell-program conversation; channels are the inter-thread plumbing it relies on.
> - [`SESSION-MODEL.md`](SESSION-MODEL.md) — history substrate. SessionModel mutations happen on the channel-fed consumer thread; future Tier 2 persistence will use a dedicated flush-to-disk channel.
> - [`PANE-MODEL.md`](PANE-MODEL.md) — multi-pane workspace framework. The reserved Pipeline Inspector pane will visualise the channel boundaries this doc inventories.
> - [`CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) — user-introspectable pipeline. Channels are the seams the user inspects + customises.
> - [`CONTRIBUTING.md`](../CONTRIBUTING.md) — F# 9 / .NET 9 conventions, including threading gotchas. References the .NET `System.Threading.Channels` API + F# `Event<T>` idioms this doc builds on.

## What this document is

A **descriptive document** for the channel-based-communication
patterns that pty-speak's substrate already uses, plus a
**decision framework** for choosing channels vs F# events
vs direct calls in future implementation cycles.

It is NOT:
- A tutorial on the .NET `System.Threading.Channels` API.
  Defer to Microsoft's docs for the API surface; this doc
  assumes familiarity.
- An exhaustive refactoring backlog. The doc is descriptive
  about what exists today (Cycle 17 baseline) and
  forward-looking about three future channels named in
  Phase 2 / Tier 2 / Tier 3 plans. It does NOT prescribe
  code changes to existing channels.
- A spec. Spec-level commitments live in
  [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md);
  this doc informs spec re-authoring when implementation
  cycles consume the principle but doesn't replace it.

The doc is **descriptive** for the patterns shipping today
(three production channels + three F# events) and
**forward-looking** for the three named future channels
(input-keystroke for Phase 2; persistence-flush for Tier 2;
AI-summarisation for Tier 3). Each piece is tagged so readers
distinguish "this is real" from "this is design intent".

## Why this exists

The maintainer articulated a unifying architectural principle
2026-05-08 while reviewing Cycle 17 (PR #192 — channel-driven
actor model that closed the Tier 1.D idle-gap hole):

> "I think in general, a unifying principle of our
> architecture should be channel based communication
> pathways since this is the core interaction model that the
> user will have to manage. Anyway, it will be very nice if
> they can investigate the flow diagram, which messages are
> getting past along with channels inside of the network that
> represents the entire Code infrastructure."

Cycle 17 applied the principle concretely: `pumpChannel :
BoundedChannel<PumpInput>` collapsed all composition-root
state mutation onto a single consumer thread, eliminating the
race introduced by Cycle 16's diagnostic snapshot capture.
But no doc captured the principle. Future cycles —
specifically **Phase 2 input framework** (input-keystroke
channel for echo correlation; OSC 133 dispatch; per-pane
input routing), **Tier 2 persistence** (flush-to-disk
channel with backpressure semantics), and **Tier 3
AI-summarisation** (LLM request channel) — all need to make
channel/event/direct-call decisions. Without a captured
principle + taxonomy, those decisions risk drifting:
over-channelisation, missed cross-thread races, inconsistent
backpressure semantics.

This doc closes that gap. **Substrate-first per the
maintainer's "moving slowly and intentionally" guidance**:
formalize the principle BEFORE Phase 2 / Tier 2
implementation cycles consume it.

## Audience

Three reader personas:

1. **The maintainer**, when reasoning about whether a new
   boundary should be a channel, an event, or a direct call.
   The decision framework below is the working tool.
2. **Future Claude sessions** picking up implementation
   cycles. The catalog of current + future channels +
   anti-patterns prevents drift from the principle.
3. **Future contributors / code reviewers** checking
   consistency. The where-applies / where-doesn't taxonomy
   is the consistency rubric.

## Reading order

The doc is structured for both linear reading and lookup:

- **Linear path** (first read): scroll top-to-bottom. The
  principle → inventory → heuristic → anti-patterns → future
  channels → open questions arc reflects how the patterns
  emerged historically.
- **Lookup path** (recurring reference): jump to the
  inventory tables (current state) or the decision framework
  (when picking a pattern). The Pipeline Inspector preview +
  open questions are the forward-looking sections; safe to
  skip on a quick lookup.

If you're encountering pty-speak's channel patterns for the
first time, the **principle** section is mandatory; the rest
optionally on demand.

## The principle

**Channels are the canonical inter-thread communication
primitive in pty-speak.** They sit at thread boundaries; their
cardinality is producer-many-to-consumer-one (or one-to-one);
their backpressure semantics are explicit.

Three concrete properties define a "channel" in pty-speak's
sense (concretely: a `System.Threading.Channels.BoundedChannel<T>`):

1. **Single-threaded consumption** via
   `ChannelReader.WaitToReadAsync` + `TryRead`. The consumer
   task is the sole owner of any state it mutates in
   response to channel-delivered events.
2. **Explicit backpressure** via
   `BoundedChannelOptions.FullMode`:
   - `Wait` — producer blocks when full (preserves all
     events; risks producer slowdown).
   - `DropOldest` — overflow discards oldest unread event
     (preserves newest; loses history).
   - `DropNewest` — overflow rejects newest write
     (preserves history; rejects new producer effort).
   The choice is per-channel + intentional.
3. **Lifecycle via `ChannelWriter.TryComplete`**. Writers
   signal "no more events"; readers see end-of-channel and
   exit cleanly. Without `TryComplete`, the consumer's
   `WaitToReadAsync` hangs forever on app shutdown.

The principle's value is structural correctness: channels
**eliminate races by construction** when the consumer is the
sole owner of mutable state. They also expose the substrate
to user-facing inspection: a future Pipeline Inspector pane
(per [`PANE-MODEL.md`](PANE-MODEL.md)) can subscribe to
channel boundaries + visualise message flow without
modifying producer/consumer code.

**The principle has limits.** Section "F# Events — when
channels DON'T apply" below catalogs the patterns where
channels are inappropriate; section "Decision framework"
gives the choosing tool.

## Channel inventory — current state (Cycle 17 baseline)

Three production channels exist in the substrate today.

| Name | Payload | Producer | Consumer | Backpressure | Lifecycle | File:Line |
|---|---|---|---|---|---|---|
| `pumpChannel` | `PumpInput = Notification of ScreenNotification \| Tick of DateTimeOffset` | Parser reader thread (RowsChanged); Screen event subscribers (ModeChanged / Bell / PromptBoundary bridges); PathwayTickPump (Tick); ConPTY-spawn-failure path (ParserError) | Notification consumer task (sole owner of `currentSession`, `promptDetector`, `activePathway` mutables) | `DropOldest` (256) | App lifetime; `TryComplete` on shutdown | [`src/Terminal.App/Program.fs:813`](../src/Terminal.App/Program.fs) |
| `ConPtyHost.Stdout` | `byte array` (4 KB chunks from PTY read buffer) | `ConPtyHost.readerLoop` (synchronous `FileStream.Read` over the SafeFileHandle wrapping the PTY's stdout pipe) | `Program.startReaderLoop` (`host.Stdout.ReadAsync`) | `Wait` (256) | Per-shell-spawn; `TryComplete` when read returns ≤ 0 bytes or on cancellation | [`src/Terminal.Pty/ConPtyHost.fs:167`](../src/Terminal.Pty/ConPtyHost.fs) |
| `FileLogger.channel` | `LogEntry` (level + timestamp + category + message + optional exception) | Any thread calling `logger.LogInformation(...)` / `LogWarning(...)` / etc. — channel writes are thread-safe by design | Single drain task (`channel.Reader.WaitToReadAsync`); batched writes to file | `Wait` (~1024 configurable via `FileLoggerOptions.ChannelCapacity`) | Logger lifetime; final drain on cancellation | [`src/Terminal.Core/FileLogger.fs:141`](../src/Terminal.Core/FileLogger.fs) |

### Notes on each channel

#### `pumpChannel` (Cycle 17 — primary cross-thread channel)

The consumer is the **sole** owner of three mutable
references in `Program.fs`'s `compose ()` scope:
`currentSession : SessionModel.T`,
`promptDetector : HeuristicPromptDetector.T`, and
`activePathway : DisplayPathway.T`. All cross-thread mutation
attempts are **structurally impossible** because no other
thread holds references to those mutables. The producers
write `PumpInput` values via `pumpChannel.Writer.TryWrite`;
the consumer reads + dispatches inside its task body.

`DropOldest` is the right backpressure choice because:
- Tick events at 20Hz are replaceable (a dropped tick is
  re-fired 50ms later).
- RowsChanged events are coalesced downstream by
  StreamPathway's frame-dedup; lost intermediate frames are
  re-derivable from the next snapshot.
- Bell / ModeChanged / PromptBoundary events are sparse
  enough that 256-slot capacity is rarely full.

#### `ConPtyHost.Stdout` (PTY → reader thread)

The producer thread is a synchronous `readerLoop` blocking on
`FileStream.Read`; the channel decouples that blocking read
from the consumer's async `ReadAsync` await. Backpressure is
`Wait` because dropping bytes from a PTY stream would corrupt
parser state — every byte matters.

#### `FileLogger.channel` (any thread → drain writer)

`Wait` backpressure is correct because:
- Lossy logs would defeat the purpose (post-hoc diagnosis
  needs complete records).
- Producer-side latency under burst is acceptable (logging
  is rare in steady state; bursts are typically short).
- The 1024-slot capacity buffers any reasonable write storm.

The drain loop is single-threaded by design (writes are
serialised; file lock contention impossible). Multiple
producer threads write concurrently; the channel handles
synchronisation.

## F# Events — when channels DON'T apply

Three concrete patterns in `src/Terminal.Core/Screen.fs` use
F# `Event<T>` instead of channels. Each is appropriate;
channelising would add latency without correctness gain.

| Name | Payload | Trigger | Subscribers | Buffering pattern | File:Line |
|---|---|---|---|---|---|
| `screen.ModeChanged` | `TerminalModeFlag * bool` | `Screen.Apply` post-lock-release | `Program.fs` bridges to `pumpChannel`; future pathways may subscribe directly | Buffered in `pendingModeChanges` ResizeArray during gate; fired after lock release | [`src/Terminal.Core/Screen.fs:116`](../src/Terminal.Core/Screen.fs) |
| `screen.Bell` | `unit` | `Screen.Apply` on `0x07uy` (BEL byte) post-lock-release | `Program.fs` bridges to `pumpChannel` | Buffered in `pendingBell` boolean during gate | [`src/Terminal.Core/Screen.fs:126`](../src/Terminal.Core/Screen.fs) |
| `screen.PromptBoundary` | `PromptBoundaryData` | `Screen.Apply` on OSC 133 parse success post-lock-release | `Program.fs` bridges to `pumpChannel`; SessionModel consumes downstream | Buffered in `pendingPromptBoundaries` ResizeArray during gate | [`src/Terminal.Core/Screen.fs:138`](../src/Terminal.Core/Screen.fs) |

Why these are events, not channels:

1. **Same-thread synchronous fan-out within Terminal.Core.**
   The events fire on the same thread as `Screen.Apply` (the
   PathwayPump worker). Cross-thread dispatch is the bridge's
   job, not the event's.
2. **1-to-N subscriber model.** Multiple bridges may attach
   (today: just one each, but future pathways may subscribe
   directly). F# `Event<T>` natively supports multi-cast;
   single-channel-per-consumer would require fan-out logic.
3. **No backpressure semantics needed.** Subscribers are
   in-process function calls; they complete synchronously
   before the trigger returns.

### The "Event → Channel" bridge pattern

The recurring shape:

```fsharp
screen.ModeChanged.Add(fun (flag, value) ->
    pumpChannel.Writer.TryWrite(
        Notification (ModeChanged (flag, value))) |> ignore)
```

This bridge takes an in-process event and forwards it across
the thread boundary into a channel. The pattern is
**load-bearing** because:
- The event provides 1-to-N synchronous fan-out within
  Terminal.Core.
- The channel provides cross-thread serialisation into the
  consumer's mutation domain.
- Bridging keeps each abstraction at its appropriate layer.

The buffer-then-fire-after-lock pattern in
[`Screen.fs:Apply`](../src/Terminal.Core/Screen.fs) is also
load-bearing. Synchronous `Event<>.Trigger` while holding
the screen's gate lock would let subscriber side-effects
re-enter the lock and deadlock. Buffering events in
ResizeArray fields during the lock + firing after lock
release decouples the trigger from the lock holder.

## Decision framework

When introducing a new boundary, ask three questions in
order:

### Q1: Does the boundary cross a thread?

- **YES** → channel.
- **NO** → continue to Q2.

Cross-thread boundaries need either explicit
synchronisation (locks; mutex; `Interlocked`) or message
passing (channels; events with thread-safe subscribers).
Channels are the project's preferred answer.

### Q2: Does the consumer need backpressure / batching / drop semantics?

- **YES** → channel (even if same-thread; rare but valid).
- **NO** → continue to Q3.

Backpressure is the channel's superpower:
`BoundedChannelOptions.FullMode` makes the choice explicit.
Synchronous events have no backpressure — every trigger
runs every subscriber synchronously.

### Q3: Is the producer count fixed at compile time + the consumer single?

- **YES** → direct function call OR F# `Event<T>`.
- **NO** → channel.

Direct function calls suffice for tight in-process coupling
(`CanonicalState.create snapshot cursor seq`). F#
`Event<T>` is right for sparse 1-to-N fan-out where the
subscriber set is known and stable.

### Concrete categorisation (4 buckets)

Given the three questions, every boundary in pty-speak
falls into exactly one category:

| Category | Use when | Example |
|---|---|---|
| **`Channel<T>`** | Cross-thread + backpressure + decoupling | `pumpChannel`, `ConPtyHost.Stdout`, `FileLogger.channel` |
| **`Event<T>`** | Same-thread synchronous fan-out (sparse; 1-to-N) | `screen.Bell`, `screen.ModeChanged`, `screen.PromptBoundary` |
| **Direct function call** | In-process tight coupling; pure or quasi-pure | `CanonicalState.create`, `HeuristicPromptDetector.tryDetect` |
| **Mutable + lock** | Shared single-thread state with sparse cross-thread reads | Rare in pty-speak post-Cycle-17; was used for `currentSession` etc. before the channel-driven actor model collapsed them to single-thread mutation |

## Anti-patterns / gotchas

Five "don't"s with concrete pty-speak examples:

### 1. Don't channelise pure function calls

`CanonicalState.create snapshot cursor seq` is a pure
transformation: `(Cell[][], int*int, int64) → Canonical`.
Channelising it would add a queue + a worker task + a
result-channel-back, multiplying latency without correctness
gain.

**Test**: if the function would be a `let` binding inside
`compose ()` if rearranged, it's a function call. Don't
channelise.

### 2. Don't add channels to avoid passing parameters

Channels carry data across **thread** boundaries. Passing
data within a thread is what function parameters are for.
Adding a channel just because the call chain is deep
introduces unnecessary lifecycle complexity.

**Test**: if the producer + consumer run on the same thread,
re-examine whether you actually need a channel.

### 3. Don't choose `BoundedChannelFullMode.Wait` blindly

The default is rarely the right answer. Consider:
- **Tick events at 20Hz**: `DropOldest` is correct. Lost
  ticks are replaced 50ms later.
- **Log entries**: `Wait` is correct. Lossy logs defeat
  diagnosis.
- **PTY byte stream**: `Wait` is correct. Dropped bytes
  corrupt parser state.
- **Future input keystrokes**: `Wait` is correct. Keystrokes
  are user intent; can't drop.
- **Future AI-summarisation requests**: `DropOldest` is
  correct. Stale summaries are worthless; latest matters.

Pick the mode based on the data's nature, not the default.

### 4. Don't forget `TryComplete` on shutdown

Without it, the consumer's
`channel.Reader.WaitToReadAsync(ct)` hangs forever on app
close (the cancellation token interrupts the await but the
reader-task can leak waiting for work that never arrives).

**Pattern**: every channel-owning composition root + every
disposable wrapping a channel calls `channel.Writer.TryComplete()`
on dispose.

### 5. Don't bridge `Event<T>` → `Channel<T>` synchronously while holding a lock

The buffer-then-fire-after-lock pattern in
[`Screen.fs:Apply`](../src/Terminal.Core/Screen.fs) is
load-bearing. Synchronous `Event<>.Trigger` while holding
the screen's gate lock would let subscriber side-effects
re-enter the lock and deadlock.

**Pattern**: buffer events in ResizeArray fields during
locked sections; fire (and bridge to channels) after the
lock releases.

## Future channels (forward-looking)

Three named channels appear in future implementation plans.
Each anchors a near-future cycle's design.

### Input-keystroke channel (Phase 2 input framework)

| Aspect | Specification |
|---|---|
| Producer | WPF KeyDown handler in `src/Views/TerminalView.cs` (and future paste / focus events) |
| Consumer | Echo-correlation substrate (Phase 2 `InputPathway`) |
| Payload | `Keystroke = { Key + Modifiers + Timestamp + Source }` (sketch) |
| Backpressure | `Wait` — keystrokes are user intent; can't drop |
| Lifecycle | App lifetime; `TryComplete` on shutdown |
| Why a channel | UI thread (KeyDown) → pathway-pump worker thread (echo correlation); cross-thread + must not lose data |

The keystroke channel sits between the input substrate and
the consumer that correlates user-typed bytes with screen
mutations (echo correlation). Phase 2's input framework
cycle plans this as its primary substrate seam.

### SessionModel persistence-flush channel (Tier 2)

| Aspect | Specification |
|---|---|
| Producer | SessionModel state-machine (on `CommandFinished` arrival) |
| Consumer | Persistence writer (sole reader; serialises to disk) |
| Payload | `(SessionTuple, SerialisationHint)` |
| Backpressure | `Wait` (high capacity ~1024) — durable history matters; rare flushes |
| Lifecycle | App lifetime; final drain on shutdown |
| Why a channel | Producer (state-machine on consumer thread) → writer (separate thread; isolated I/O); cross-thread + must not lose data |

Tier 2's persistence design will use this channel to
decouple the SessionModel state-machine's tuple-finalisation
hot path from the durable-write I/O. Same `Wait`-backpressure
choice as `FileLogger.channel` for the same reason.

### AI-summarisation request channel (Tier 3)

| Aspect | Specification |
|---|---|
| Producer | Pathway / SessionModel-aware consumer (e.g. ClaudeCodePathway when a long output finishes) |
| Consumer | LLM client (sole reader; rate-limited to API budget) |
| Payload | `(SessionTuple, SummarisationPrompt, ResponseChannel)` (request-response shape) |
| Backpressure | `DropOldest` — stale AI summaries are worthless; latest matters |
| Lifecycle | App lifetime; abort pending requests on shutdown |
| Why a channel | Producer (pathway) → LLM-client thread (rate-limited; potentially blocking on network); decouple latency |

Tier 3's AI-summarisation design will use this channel to
gate LLM API calls behind the consumer's rate limit. The
`DropOldest` backpressure choice means a user typing
quickly through outputs gets the latest summary, not a
queue of stale ones.

## Pipeline Inspector pane preview

[`PANE-MODEL.md`](PANE-MODEL.md) reserves a **Pipeline
Inspector pane** in its catalog. The pane will visualise the
channel boundaries inventoried here — message flow rates,
queue depths, drop counts, lifecycle events — without
modifying producer/consumer code.

Cycle 18 establishes the inventory; the pane's design is
**out of scope here**. The pane will subscribe via a
dedicated "tap" mechanism (likely a per-channel
`Channel<T>` clone via fan-out, or a low-overhead
side-channel that records metadata only). The design call
defers to the cycle that ships the pane.

For cross-reference: Channel Architecture's contribution to
the Pipeline Inspector is the **schema** — what's a channel,
what fields characterise it, what events fire on it. The
pane consumes that schema to visualise.

## Open questions — design decisions still in flux

Q-and-A format mirrors [`SESSION-MODEL.md`](SESSION-MODEL.md).
None block Cycle 18; flagged here so future cycles see them.

### Q1: Should `screen.PromptBoundary` (currently F# Event + bridge) become a dedicated channel?

**Argument FOR**: consistency with `pumpChannel` (which
already carries OSC 133 events as `Notification of
PromptBoundary`).

**Argument AGAINST**: same-thread fan-out is what `Event<T>`
is for. Bridging via `Event.Add → Channel.TryWrite` is the
right abstraction at the Terminal.Core / Terminal.App
boundary. Channelising at the trigger site would
double-buffer the events.

**Tentative**: leave as Event with bridge. The bridge is
the right thread boundary.

### Q2: Should `FileLogger.channel`'s capacity become TOML-configurable?

Today: hardcoded ~1024 slots via `FileLoggerOptions.ChannelCapacity`.
Power users in high-throughput debugging scenarios might
benefit from larger capacity (more breathing room before
producers block).

**Tentative**: defer until demand surfaces. The current
default has been adequate through Stage 7 + Phase A +
Tier 1 cycles.

### Q3: Should the input-keystroke channel batch, or fire per-keystroke?

Phase 2 design call. Echo correlation needs precise per-
keystroke timing to match against screen mutations; batching
would lose that resolution.

**Tentative**: per-keystroke. Phase 2 input framework
cycle will firm up.

### Q4: How should the Pipeline Inspector pane SUBSCRIBE to channel events without coupling to producer threads?

Likely shape: a dedicated "tap" mechanism per channel —
maybe a fan-out that publishes to multiple readers
(`Channel<T>` clones), maybe a low-overhead metadata-only
side-channel.

**Tentative**: design alongside the pane in a future
cycle. Cycle 18 establishes the inventory; the pane's
design call defers.

### Q5: Should pty-speak ever use unbounded channels?

`System.Threading.Channels.Channel.CreateUnbounded<T>()`
exists. Pros: simpler (no backpressure decision); accepts
all writes. Cons: no protection against memory leaks if a
consumer falls permanently behind.

**Tentative**: NO. Discipline: `BoundedChannel` only,
with explicit `FullMode`. If a use case appears that
genuinely needs unbounded semantics, revisit.

## Out of scope

- **Channel-pool / rate-limiter patterns**. Future
  optimisation if profiling reveals contention; not
  applicable today.
- **Unbounded channels**. Per Q5 above, explicitly avoided.
  Discipline: `BoundedChannel` only.
- **Broadcast channels** (multi-consumer). Not used today;
  the Pipeline Inspector pane may need them eventually.
  Future doc / cycle decides.
- **Refactoring existing channels**. Cycle 18 is descriptive,
  not prescriptive. Existing channels are documented as-is;
  refactoring proposals (e.g. consolidating `pumpChannel`'s
  producer call sites) belong to follow-up cycles.
- **Spec changes**. Per [`CLAUDE.md`](../CLAUDE.md)
  spec-immutability rule;
  [`spec/event-and-output-framework.md`](../spec/event-and-output-framework.md)
  absorbs this when implementation cycles consume the
  principle.
- **Tier 1.D-cleanup** (consolidate detector invocation
  sites). Separate backlog item.
- **Channel-API tutorial content**. Defer to Microsoft's
  `System.Threading.Channels` docs + F# language docs for
  `Event<T>`. This doc assumes API familiarity.

## Versioning + maintenance

This doc is a **snapshot** dated 2026-05-08. The principle
is durable; the inventory drifts as cycles ship new
channels (Phase 2 / Tier 2 / Tier 3). Re-snapshot when:

- A new production channel ships (add an inventory row +
  bump the snapshot date).
- A future-channel candidate moves from "named" to
  "shipped" (move from "Future channels" section to
  "Channel inventory").
- The principle itself evolves (the maintainer articulates
  a refinement; capture the new framing + cite the source).

The **Change log** below tracks each re-snapshot. Per the
research-stage doc convention, drift between snapshots is
acceptable; re-snapshot when needed, don't patch
incrementally.

## Cross-references

- [`PIPELINE-NARRATIVE.md`](PIPELINE-NARRATIVE.md) — the
  12-stage operational vocabulary. Each stage's inter-thread
  boundary appears in this doc's channel inventory.
- [`INTERACTION-MODEL.md`](INTERACTION-MODEL.md) — the
  Shell Interaction Manager (SIM) is the conceptual owner
  of the shell-program conversation; channels are the SIM's
  inter-thread plumbing.
- [`SESSION-MODEL.md`](SESSION-MODEL.md) — SessionModel
  mutations happen on the channel-fed consumer thread (the
  Cycle 17 `pumpChannel` consumer). Tier 2 persistence will
  add a dedicated flush-to-disk channel.
- [`PANE-MODEL.md`](PANE-MODEL.md) — Pipeline Inspector
  pane reserved; this doc establishes the schema it queries.
- [`CUSTOMIZATION-MODEL.md`](CUSTOMIZATION-MODEL.md) —
  channels are the seams the user inspects + customises;
  the Customization principle's "swap an alternative
  implementation per stage" maps to "swap a consumer for a
  channel".
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — F# 9 / .NET 9
  conventions; threading gotchas.

## Change log

| Date | Change |
|---|---|
| 2026-05-08 | Initial design (Cycle 18). Channel inventory: 3 production channels (`pumpChannel`, `ConPtyHost.Stdout`, `FileLogger.channel`) + 3 F# Events (`screen.ModeChanged`, `screen.Bell`, `screen.PromptBoundary`). Decision framework (3-question heuristic + 4-bucket categorisation) + 5 anti-patterns + 3 future channel candidates (input-keystroke for Phase 2; persistence-flush for Tier 2; AI-summarisation for Tier 3). 5 open questions. Companion to PIPELINE-NARRATIVE / INTERACTION-MODEL / SESSION-MODEL / PANE-MODEL / CUSTOMIZATION-MODEL. |
